using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record WebRuntimeProfile(JsonElement? ApiVersion, string QuantityProfile, long? MaximumRequestBytes,
    long? MaximumParallelRequests, long? MaximumUserSessions, string[]? AuthenticationModes,
    bool SessionInfoObserved, string? SessionUser, string? SessionAuthenticationMode, string? RuntimeTimeout,
    DateTimeOffset? PasswordExpiresUtc, bool? PasswordExpirationWarning, bool? HasNoPermissions);

/// <summary>One owned, pinned HTTPS session. Callers serialize access and persist intent before Login/Logout.</summary>
public sealed class WebRuntimeClient : IDisposable
{
    private readonly HttpClient client;
    private string? token;
    private bool loginAttempted;
    private bool disposed;
    private long lastUse = Stopwatch.GetTimestamp();
    public WebRuntimeContext Context { get; }
    public bool PeerVerified { get; private set; }
    public bool LoginRejected { get; private set; }
    public bool LoginDispatched { get; private set; }
    public bool SessionUnusable { get; private set; }
    public WebRuntimeProfile Profile { get; private set; } = new(null,"unavailable",null,null,null,null,false,null,null,null,null,null,null);
    public TimeSpan IdleLimit { get; private set; } = TimeSpan.FromSeconds(90);
    public bool IdleExpired => Stopwatch.GetElapsedTime(lastUse) >= IdleLimit;
    public string[] Methods { get; private set; } = [];

    public WebRuntimeClient(WebRuntimeContext context)
    {
        WebRuntimeProtocol.Validate(context); Context = context;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = TimeSpan.Zero };
        // A resumed TLS session can retain an old certificate despite a new TCP connection.
        handler.SslOptions.AllowTlsResume = false;
        // Explicit leaf pin is the trust anchor. Name, validity and server usage still have to match.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            using var peer = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            bool valid = peer is not null &&
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                DateTime.UtcNow >= peer.NotBefore.ToUniversalTime() && DateTime.UtcNow <= peer.NotAfter.ToUniversalTime() &&
                Convert.ToHexStringLower(SHA256.HashData(peer.RawData)) == Context.PeerCertificateSha256 &&
                peer.Extensions.OfType<X509EnhancedKeyUsageExtension>().All(e => e.EnhancedKeyUsages.Cast<Oid>()
                    .Any(o => o.Value == "1.3.6.1.5.5.7.3.1"));
            PeerVerified = valid;
            return valid;
        };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        string id = Id(); var body = WebRuntimeProtocol.Discover(id);
        var methods = await Exchange(body, bytes => WebRuntimeProtocol.Names(bytes, id), cancellationToken);
        if (JsonSerializer.SerializeToUtf8Bytes(methods).Length > 64*1024) throw Error("RuntimeDiscoveryLimit");
        if (!new[] { "Api.Login", "Api.Logout", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read" }.All(methods.Contains))
            throw Error("RuntimeMethodsUnavailable");
        Methods = methods;
        if (Methods.Contains("Api.Version"))
        {
            var version = await Query("Api.Version", cancellationToken);
            if (version.ValueKind != JsonValueKind.Number || version.GetRawText().Length > 64 || !version.TryGetDecimal(out var number) || number <= 0)
                throw Error("RuntimeProfileInvalid");
            Profile = Profile with { ApiVersion = version };
        }
        if (Methods.Contains("Api.GetQuantityStructures"))
        {
            var limits = await Query("Api.GetQuantityStructures", cancellationToken);
            long Limit(string name)
            {
                if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty(name,out var value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var n) || n <= 0)
                    throw Error("RuntimeQuantityProfileUnsupported");
                return n;
            }
            // The V21 table and Siemens client use max names; conflicting example spellings are not aliases.
            if (limits.ValueKind == JsonValueKind.Object && limits.EnumerateObject().Any(p => p.Name is
                "webapi_http_request_body_size" or "webapi_parallel_requests" or "webapi_parallel_user_sessions"))
                throw Error("RuntimeQuantityProfileUnsupported");
            Profile = Profile with { QuantityProfile = "siemens-v21-table-max", MaximumRequestBytes = Limit("webapi_max_http_request_body_size"),
                MaximumParallelRequests = Limit("webapi_max_parallel_requests"), MaximumUserSessions = Limit("webapi_max_parallel_user_sessions") };
        }
        if (Methods.Contains("Api.GetAuthenticationMode"))
        {
            var authentication = await Query("Api.GetAuthenticationMode", cancellationToken);
            if (authentication.ValueKind != JsonValueKind.Object || !authentication.TryGetProperty("authentication_modes",out var modes) ||
                modes.ValueKind != JsonValueKind.Array || modes.GetArrayLength() is < 1 or > 16)
                throw Error("RuntimeProfileInvalid");
            var names = new List<string>();
            foreach (var mode in modes.EnumerateArray())
            {
                if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("local" or "static" or "disabled" or "umc" or "umc_sso"))
                    throw Error("RuntimeProfileInvalid");
                if (names.Contains(mode.GetString()!)) throw Error("RuntimeProfileInvalid");
                names.Add(mode.GetString()!);
            }
            Profile = Profile with { AuthenticationModes = names.ToArray() };
        }
    }

    public void ValidateLoginMode(string user, string mode)
    {
        if (mode is not ("local" or "static" or "disabled" or "umc")) throw Error("RuntimeAuthenticationModeUnsupported");
        // A CPU advertising only UMC can still admit its explicitly enabled local Anonymous user.
        if (Profile.AuthenticationModes is { } modes && !modes.Contains(mode) && !(user == "Anonymous" && mode == "local" && modes.Contains("umc")))
            throw Error("RuntimeAuthenticationModeUnsupported");
    }

    public async Task LoginAsync(string id, string user, string mode, ReadOnlyMemory<byte> passwordJson, CancellationToken cancellationToken)
    {
        if (loginAttempted || token is not null) throw Error("RuntimeLoginAlreadyAttempted");
        ValidateLoginMode(user, mode);
        using var password = JsonDocument.Parse(passwordJson, new JsonDocumentOptions { MaxDepth = 1 });
        if (password.RootElement.ValueKind != JsonValueKind.String) throw Error("RuntimeCredentialFormatInvalid");
        _ = new UTF8Encoding(false, true).GetByteCount(password.RootElement.GetString()!);
        var body = Request("Api.Login", id, new { user, mode, password = password.RootElement });
        loginAttempted = true;
        await Exchange(body, bytes =>
        {
            using var response = WebRuntimeProtocol.Response(bytes, id);
            LoginRejected = response.RootElement.TryGetProperty("error", out _);
            var result = Result(response);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("token", out var value) || value.ValueKind != JsonValueKind.String)
                throw Error("RuntimeLoginResponseInvalid");
            string supplied = value.GetString()!;
            if (supplied.Length is < 1 or > 8192 || supplied.Any(c => c < 33 || c > 126)) throw Error("RuntimeLoginResponseInvalid");
            token = supplied;
            ApplyPolicy(result);
            if (result.TryGetProperty("has_no_permissions",out var denied))
            {
                if (denied.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Error("RuntimeProfileInvalid");
                Profile = Profile with { HasNoPermissions = denied.GetBoolean() };
            }
            return true;
        }, cancellationToken);
    }

    public async Task VerifySessionAsync(string user, string mode, CancellationToken cancellationToken)
    {
        Active();
        if (!Methods.Contains("Api.GetSessionInfo")) return;
        try
        {
            var info = await Query("Api.GetSessionInfo", cancellationToken);
            if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("username",out var username) ||
                username.ValueKind != JsonValueKind.String || username.GetString() != user)
                throw Error("RuntimeSessionIdentityMismatch");
            string? observedMode = null;
            if (info.TryGetProperty("authentication_mode",out var authentication))
            {
                if (authentication.ValueKind != JsonValueKind.String || authentication.GetString() != mode)
                    throw Error("RuntimeSessionIdentityMismatch");
                observedMode = authentication.GetString();
            }
            else if (user is not ("Anonymous" or "Everybody")) throw Error("RuntimeSessionIdentityMismatch");
            ApplyPolicy(info);
            Profile = Profile with { SessionInfoObserved = true, SessionUser = user, SessionAuthenticationMode = observedMode };
        }
        catch { SessionUnusable = true; throw; }
    }

    private void ApplyPolicy(JsonElement result)
    {
        if (result.TryGetProperty("runtime_timeout",out var timeout))
        {
            if (timeout.ValueKind != JsonValueKind.String || timeout.GetString()!.Length > 128) throw Error("RuntimeProfileInvalid");
            TimeSpan interval;
            try { interval = XmlConvert.ToTimeSpan(timeout.GetString()!); } catch { throw Error("RuntimeProfileInvalid"); }
            if (interval <= TimeSpan.Zero) throw Error("RuntimeProfileInvalid");
            if (interval < IdleLimit) IdleLimit = interval;
            Profile = Profile with { RuntimeTimeout = timeout.GetString() };
        }
        if (result.TryGetProperty("password_expiration",out var expiration))
        {
            if (expiration.ValueKind != JsonValueKind.Object || !expiration.TryGetProperty("timestamp",out var timestamp) ||
                timestamp.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParseExact(timestamp.GetString(),"yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var parsed) ||
                !expiration.TryGetProperty("warning",out var warning) || warning.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Error("RuntimeProfileInvalid");
            Profile = Profile with { PasswordExpiresUtc = parsed, PasswordExpirationWarning = warning.GetBoolean() };
        }
    }

    private async Task<JsonElement> Query(string method, CancellationToken cancellationToken)
    {
        string id = Id();
        return await Exchange(Request(method,id),bytes =>
        {
            using var response = WebRuntimeProtocol.Response(bytes,id);
            return Result(response).Clone();
        },cancellationToken);
    }

    public async Task<string[]> PermissionsAsync(CancellationToken cancellationToken)
    {
        Active(); string id = Id();
        return await Exchange(WebRuntimeProtocol.Discover(id, permissions: true), bytes => WebRuntimeProtocol.Names(bytes, id), cancellationToken);
    }

    public async Task<JsonElement> BrowseAsync(string? symbol, bool children, CancellationToken cancellationToken)
    {
        Active(); string id = Id();
        return await Exchange(WebRuntimeProtocol.Browse(id, symbol, children), bytes => WebRuntimeProtocol.BrowseResult(bytes, id, children), cancellationToken);
    }

    public async Task<WebRuntimeReadResult> ReadAsync(WebRuntimeSymbol selection, CancellationToken cancellationToken)
    {
        Active(); var request = WebRuntimeProtocol.Read(Context, selection, Id());
        DateTimeOffset start = DateTimeOffset.UtcNow; long tick = Stopwatch.GetTimestamp();
        return await Exchange(request.Body, bytes => WebRuntimeProtocol.ReadResult(Context, request, bytes, start,
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(tick)), cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (token is null) throw Error("RuntimeRemoteSessionUnknown");
        string id = Id();
        try
        {
            await Exchange(Request("Api.Logout", id), bytes =>
            {
                using var response = WebRuntimeProtocol.Response(bytes, id);
                if (Result(response).ValueKind != JsonValueKind.True) throw Error("RuntimeLogoutUnconfirmed");
                return true;
            }, cancellationToken);
        }
        finally { token = null; }
    }

    private async Task<T> Exchange<T>(JsonElement body, Func<ReadOnlyMemory<byte>, T> parse, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(body);
        byte[] buffer = new byte[WebRuntimeProtocol.MaximumResponseBytes + 1];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (requestBytes.Length > Math.Min(64 * 1024, Profile.MaximumRequestBytes ?? 64 * 1024)) throw Error("RuntimeRequestLimit");
            using var request = new HttpRequestMessage(HttpMethod.Post, Context.Endpoint + "/api/jsonrpc")
                { Content = new ByteArrayContent(requestBytes), Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Content.Headers.ContentType = new("application/json");
            // The request header asks the peer to close; zero pool lifetime separately prevents local reuse.
            request.Headers.ConnectionClose = true;
            if (token is not null) request.Headers.Add("X-Auth-Token", token);
            if (body.GetProperty("method").GetString() == "Api.Login") LoginDispatched = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!PeerVerified) throw Error("RuntimeTlsUnverified");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw Error("RuntimeAuthenticationDenied");
            if (!response.IsSuccessStatusCode) throw Error("RuntimeHttpFailure");
            if (response.Content.Headers.ContentLength > WebRuntimeProtocol.MaximumResponseBytes) throw Error("RuntimeResponseLimit");
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > WebRuntimeProtocol.MaximumResponseBytes) throw Error("RuntimeResponseLimit");
            if (token is not null)
            {
                using var inspected = WebRuntimeProtocol.Response(buffer.AsMemory(0,length),body.GetProperty("id").GetString()!);
                void RefuseToken(JsonElement node)
                {
                    if (node.ValueKind == JsonValueKind.String && node.GetString()!.Contains(token,StringComparison.Ordinal)) throw Error("RuntimeSecretEchoRejected");
                    if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) RefuseToken(child);
                    if (node.ValueKind == JsonValueKind.Object) foreach (var property in node.EnumerateObject())
                    {
                        if (property.Name.Contains(token,StringComparison.Ordinal)) throw Error("RuntimeSecretEchoRejected");
                        RefuseToken(property.Value);
                    }
                }
                // Error bodies are already discarded by the parsers; inspect only publicly returned results.
                if (inspected.RootElement.TryGetProperty("result",out var resultNode)) RefuseToken(resultNode);
            }
            T result = parse(buffer.AsMemory(0, length)); lastUse = Stopwatch.GetTimestamp(); return result;
        }
        catch (OperationCanceledException) { throw Error(cancellationToken.IsCancellationRequested ? "RuntimeCancelled" : "RuntimeTimeout"); }
        catch (HttpRequestException) { throw Error("RuntimeTransportFailure"); }
        catch (IOException) { throw Error("RuntimeTransportFailure"); }
        finally { CryptographicOperations.ZeroMemory(requestBytes); CryptographicOperations.ZeroMemory(buffer); }
    }

    private void Active() { if (token is null || IdleExpired || SessionUnusable) throw Error("RuntimeSessionExpired"); }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static JsonElement Request(string method, string id, object? parameters = null) =>
        JsonSerializer.SerializeToElement(parameters is null ? (object)new { jsonrpc = "2.0", id, method } : new { jsonrpc = "2.0", id, method, @params = parameters });
    private static JsonElement Result(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("error", out var error))
            throw Error(error.GetProperty("code").GetInt32() is 100 or 102 or 105 ? "RuntimeAuthenticationDenied" : "RuntimeProviderRejected");
        return response.RootElement.GetProperty("result");
    }
    private static BridgeException Error(string code) => new(code, "Runtime request refused or incomplete. No remote text, password or token is returned; do not automatically retry login/logout.");
    public void Dispose() { disposed = true; token = null; client.Dispose(); }
}
