using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// The Host's runtime_unified_* checks build clients over a scripted transport with the production peer check.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TiaMcp.Inventory.Tests")]

namespace TiaMcp.Core.UnifiedRuntime;

/// <summary>
/// One login session with a WinCC Unified PC Runtime GraphQL server (https://host/graphql/ through its IIS web server). The
/// explicit leaf-certificate pin is the trust anchor, as in the Web API client; host name, validity and server usage must match
/// too. The password reaches only the login request buffer, which is cleared; the token never leaves this object.
/// </summary>
public sealed class UnifiedGraphQlClient : IDisposable
{
    public const int MaxRequestBytes = 1024 * 1024;   // manual 2.1: larger requests are refused with 413
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private string? token;
    private bool loginAttempted;
    public bool LoginDispatched { get; private set; }
    public string? UserLanguage { get; private set; }
    public string? UserName { get; private set; }
    public int? AutoLogoffSeconds { get; private set; }
    public string[] ActiveFields { get; private set; } = [];
    public string[] LoggedFields { get; private set; } = [];

    public UnifiedGraphQlClient(Uri endpoint, string peerCertificateSha256)
        : this(endpoint, Handler(peerCertificateSha256, out var verified), verified) { }

    // Tests pass a handler without TLS; production always goes through the pinned handler above.
    internal UnifiedGraphQlClient(Uri endpoint, HttpMessageHandler handler, Func<bool>? verified = null)
    {
        this.endpoint = endpoint;
        peerVerified = verified;
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private readonly Func<bool>? peerVerified;

    private static SocketsHttpHandler Handler(string pin, out Func<bool> verified)
    {
        if (pin is not { Length: 64 } || pin.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new BridgeException("UnifiedCertificatePinInvalid", "peerCertificateSha256 is the lowercase SHA-256 of the Runtime web server's leaf certificate.");
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.Zero };
        handler.SslOptions.AllowTlsResume = false;
        handler.SslOptions.RemoteCertificateValidationCallback = PeerValidation(pin, out verified);
        return handler;
    }

    /// <summary>The TLS peer check of the pinned handler: the leaf's SHA-256 equals the pin, the host name matches and the
    /// certificate is within its validity; only chain trust is waived (the pin replaces it). Tests drive the same callback.</summary>
    internal static RemoteCertificateValidationCallback PeerValidation(string pin, out Func<bool> verified)
    {
        bool seen = false;
        verified = () => seen;
        return (_, certificate, _, errors) =>
        {
            using var peer = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            bool valid = peer is not null &&
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                DateTime.UtcNow >= peer.NotBefore.ToUniversalTime() && DateTime.UtcNow <= peer.NotAfter.ToUniversalTime() &&
                Convert.ToHexStringLower(SHA256.HashData(peer.RawData)) == pin;
            seen = valid;
            return valid;
        };
    }

    /// <summary>login(username, password) with the password as a JSON string from the protected credential store, then the
    /// user's UMC language and the server's alarm fields. Refused credentials end the session (code 101/102).</summary>
    public async Task LoginAsync(string user, ReadOnlyMemory<byte> passwordJson, CancellationToken cancellationToken)
    {
        if (loginAttempted) throw new BridgeException("UnifiedLoginAlreadyAttempted", "This session already attempted its one login.");
        loginAttempted = true;
        byte[] request;
        using (var password = JsonDocument.Parse(passwordJson))
        {
            if (password.RootElement.ValueKind != JsonValueKind.String) throw new BridgeException("RuntimeCredentialFormatInvalid", "The protected secret must be a JSON string.");
            request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                query = "mutation($username:String,$password:String){login(username:$username,password:$password){token user{name fullName language autoLoggOffSec} error{code description}}}",
                variables = new { username = user, password = password.RootElement },
            });
        }
        JsonElement login;
        try { LoginDispatched = true; login = await Post(request, authorized: false, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(request); }
        if (!login.TryGetProperty("login", out var session) || session.ValueKind != JsonValueKind.Object)
            throw new BridgeException("UnifiedLoginResponseInvalid", "The login response carries no session.");
        string? code = session.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var c) ? c.ToString() : null;
        if (code is not (null or "0"))
            throw new BridgeException("UnifiedAuthenticationDenied", code == "101" ? "Incorrect credentials (101)." : "UMC refused the login (" + code + ").");
        if (!session.TryGetProperty("token", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 8192 } supplied)
            throw new BridgeException("UnifiedLoginResponseInvalid", "The login response carries no token.");
        token = supplied;
        if (session.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            UserName = u.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            UserLanguage = u.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } language ? language : null;
            AutoLogoffSeconds = u.TryGetProperty("autoLoggOffSec", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int seconds) ? seconds : null;
        }
        var schema = await QueryAsync(new JsonObject { ["query"] = UnifiedAlarmRules.IntrospectionQuery }, cancellationToken);
        ActiveFields = UnifiedAlarmRules.TypeFields(schema, "active");
        LoggedFields = UnifiedAlarmRules.TypeFields(schema, "logged");
    }

    public Task<JsonElement> QueryAsync(JsonObject request, CancellationToken cancellationToken)
    {
        if (token is null) throw new BridgeException("UnifiedSessionRequired", "Connect first; this session has no login token.");
        return Post(Encoding.UTF8.GetBytes(request.ToJsonString()), authorized: true, cancellationToken);
    }

    private async Task<JsonElement> Post(byte[] body, bool authorized, CancellationToken cancellationToken)
    {
        if (body.Length > MaxRequestBytes) throw new BridgeException("UnifiedRequestTooLarge", "GraphQL requests are limited to 1 MB.");
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (authorized) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException error) when (error.InnerException is System.Security.Authentication.AuthenticationException)
        { throw new BridgeException("UnifiedTlsUnverified", "The server's certificate did not match the pinned SHA-256, host name or validity."); }
        catch (HttpRequestException) { throw new BridgeException("UnifiedTransportFailure", "The GraphQL server could not be reached; check that a runtime project runs and the endpoint's host name."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new BridgeException("UnifiedTimeout", "The GraphQL server did not answer within 30 s."); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) { token = null; throw new BridgeException("UnifiedSessionExpired", "The server ended the session (token expired); connect again."); }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new MemoryStream();
            var chunk = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw new BridgeException("UnifiedResponseLimit", "The response exceeds 16 MB; narrow the request with filterString or maxItems.");
                buffer.Write(chunk, 0, read);
            }
            JsonElement document;
            try { document = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(); }
            catch (JsonException) { throw new BridgeException("UnifiedResponseInvalid", "HTTP " + (int)response.StatusCode + " without a GraphQL JSON body."); }
            var errors = UnifiedAlarmRules.TopLevelErrors(document);
            // The login request carries the password: its errors are reported by code only, never with the server's text.
            if (errors.Count > 0 && !authorized) throw new BridgeException("UnifiedLoginFailed", "The server refused the login request; no server text is returned for it.");
            if (errors.Count > 0)
                throw new BridgeOperationException("UnifiedQueryFailed", "The GraphQL server refused the request: " + (errors[0]?["message"]?.GetValue<string>() ?? "no message") + ".",
                    JsonSerializer.SerializeToElement(new JsonObject { ["errors"] = errors, ["httpStatus"] = (int)response.StatusCode }));
            if (!response.IsSuccessStatusCode) throw new BridgeException("UnifiedHttpFailure", "HTTP " + (int)response.StatusCode + ".");
            if (!document.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new BridgeException("UnifiedResponseInvalid", "The response carries no data object.");
            return data;
        }
    }

    public bool Verified => peerVerified?.Invoke() ?? false;

    public void Dispose() { token = null; client.Dispose(); }
}
