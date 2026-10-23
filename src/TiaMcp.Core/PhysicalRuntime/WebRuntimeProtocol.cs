using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Inventory;

namespace TiaMcp.Core.PhysicalRuntime;

public sealed record WebRuntimeContext(string Endpoint, string ConnectionId, string Epoch, string PeerCertificateSha256);
public sealed record WebRuntimeSymbol(WebRuntimeContext Context, string Symbol, string Representation, string MetadataSha256);
public sealed record WebRuntimeReadRequest(WebRuntimeSymbol Selection, string RequestId, JsonElement Body);
public sealed record WebRuntimeReadResult(WebRuntimeSymbol Selection, string RequestId, string Status, JsonElement? Value,
    int? ProviderErrorCode, DateTimeOffset RequestStartedUtc, DateTimeOffset ResponseReceivedUtc, double ElapsedMilliseconds)
{
    public string SchemaVersion => "physical-runtime-web-read.v1";
    public string ValueEncoding => "siemens-web-api-json";
    public string Clock => "hostUtcAndMonotonicDuration";
    public bool HostClockWentBackwards => ResponseReceivedUtc < RequestStartedUtc;
    public DateTimeOffset? SourceTimestamp => null;
    public DateTimeOffset? ServerTimestamp => null;
    public bool AtomicAcrossSignals => false;
    public bool ScanSynchronized => false;
    public bool DeviceIdentityVerified => false;
    public bool ProgramMatchVerified => false;
    public bool NativeQualified => false;
}

/// <summary>Pure Web API request/response contracts. Supplied context is not TLS, CPU or program authentication.</summary>
public static class WebRuntimeProtocol
{
    public const int MaximumResponseBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> ReadMethods = new(StringComparer.Ordinal)
        { "Api.Browse", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read" };

    public static WebRuntimeContext Context(string endpoint, string connectionId, string epoch, string peerCertificateSha256)
    {
        Text(endpoint, 4096);
        // URI parsing must not silently trim whitespace or reinterpret a backslash before binding credentials.
        if (endpoint != endpoint.Trim() || endpoint.Contains('\\')) throw Error("RuntimeIdentityInvalid");
        string canonical = SiemensWebIdentity.ValidateEndpoint(endpoint).GetLeftPart(UriPartial.Authority);
        Id(connectionId); Id(epoch); EngineeringIdentityGuard.Sha256(peerCertificateSha256, "peerCertificateSha256");
        return new(canonical, connectionId, epoch, peerCertificateSha256);
    }

    public static void Validate(WebRuntimeContext context)
    {
        if (context is null || Context(context.Endpoint, context.ConnectionId, context.Epoch, context.PeerCertificateSha256) != context)
            throw Error("RuntimeIdentityInvalid");
    }

    public static void SameContext(WebRuntimeContext expected, WebRuntimeContext current)
    {
        Validate(expected); Validate(current);
        if (expected != current) throw Error("RuntimeContextChanged");
    }

    public static WebRuntimeSymbol Symbol(WebRuntimeContext context, string symbol, string representation, JsonElement metadata)
    {
        Validate(context); Text(symbol, 4096);
        if (representation is not ("simple" or "raw")) throw Error("RuntimeRepresentationUnsupported");
        ValidateJson(metadata, 64 * 1024);
        if (metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !metadata.TryGetProperty("datatype", out var type) || type.ValueKind != JsonValueKind.String)
            throw Error("RuntimeMetadataInvalid");
        Text(name.GetString()!, 4096); Text(type.GetString()!, 128);
        // This digest binds supplied metadata, not a downloaded-program revision or a verified symbol association.
        return new(context, symbol, representation, CanonicalJson.Sha256(JsonNode.Parse(metadata.GetRawText())!));
    }

    public static WebRuntimeReadRequest Read(WebRuntimeContext current, WebRuntimeSymbol selection, string requestId)
    {
        if (selection is null) throw Error("RuntimeSelectionInvalid");
        SameContext(selection.Context, current); Text(selection.Symbol, 4096);
        EngineeringIdentityGuard.Sha256(selection.MetadataSha256, "metadataSha256");
        if (selection.Representation is not ("simple" or "raw")) throw Error("RuntimeRepresentationUnsupported");
        Id(requestId);
        return new(selection, requestId, Request("PlcProgram.Read", requestId,
            new { var = selection.Symbol, mode = selection.Representation }));
    }

    public static JsonElement Browse(string requestId, string? symbol = null, bool children = true)
    {
        Id(requestId);
        if (symbol is not null) Text(symbol, 4096);
        if (!children && symbol is null) throw Error("RuntimeSelectionInvalid");
        var parameters = new Dictionary<string, string> { ["mode"] = children ? "children" : "var" };
        if (symbol is not null) parameters["var"] = symbol;
        return Request("PlcProgram.Browse", requestId, parameters);
    }

    public static JsonElement Discover(string requestId, bool permissions = false)
    {
        Id(requestId);
        return Request(permissions ? "Api.GetPermissions" : "Api.Browse", requestId, null);
    }

    public static string[] Names(ReadOnlyMemory<byte> response, string requestId)
    {
        using var document = Response(response, requestId);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out _)) throw Error("RuntimeDiscoveryFailed");
        var result = root.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 2048) throw Error("RuntimeResponseInvalid");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in result.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw Error("RuntimeResponseInvalid");
            string value = name.GetString()!; Text(value, 128);
            if (!names.Add(value)) throw Error("RuntimeResponseInvalid");
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    public static string ReadAvailability(IReadOnlyCollection<string> advertisedMethods, IReadOnlyCollection<string>? permissions)
    {
        ArgumentNullException.ThrowIfNull(advertisedMethods);
        if (!advertisedMethods.Contains("PlcProgram.Read", StringComparer.Ordinal)) return "unsupported";
        if (permissions is null) return "permissionUnknown";
        return permissions.Contains("read_value", StringComparer.Ordinal) ? "permittedAtObservation" : "permissionDenied";
    }

    public static JsonElement BrowseResult(ReadOnlyMemory<byte> response, string requestId, bool children)
    {
        using var document = Response(response, requestId);
        if (document.RootElement.TryGetProperty("error", out _)) throw Error("RuntimeBrowseFailed");
        var result = document.RootElement.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 10000 || !children && result.GetArrayLength() != 1)
            throw Error("RuntimeResponseInvalid");
        foreach (var row in result.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw Error("RuntimeResponseInvalid");
            Text(name.GetString()!, 4096);
        }
        return result.Clone();
    }

    public static WebRuntimeReadResult ReadResult(WebRuntimeContext current, WebRuntimeReadRequest request,
        ReadOnlyMemory<byte> response, DateTimeOffset requestStarted, DateTimeOffset responseReceived, TimeSpan elapsed)
    {
        if (request is null) throw Error("RuntimeSelectionInvalid");
        var expected = Read(current, request.Selection, request.RequestId);
        ValidateJson(request.Body, 64 * 1024);
        if (!JsonElement.DeepEquals(expected.Body, request.Body)) throw Error("RuntimeRequestChanged");
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(1)) throw Error("RuntimeTimingInvalid");
        using var document = Response(response, request.RequestId);
        JsonElement? value = null; int? code = null; string status;
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            code = error.GetProperty("code").GetInt32();
            status = code switch
            {
                2 => "permissionDenied", -32601 => "unsupportedMethod", 4 => "resourceUnavailable",
                200 => "addressUnavailable", 201 => "invalidAddress", 203 => "invalidArrayIndex",
                204 => "unsupportedAddress", _ => "providerError"
            };
        }
        else
        {
            var result = document.RootElement.GetProperty("result");
            if (request.Selection.Representation == "raw" && (result.ValueKind != JsonValueKind.Array ||
                result.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetByte(out _))))
                throw Error("RuntimeResponseInvalid");
            value = result.Clone(); status = "read";
        }
        return new(request.Selection, request.RequestId, status, value, code, requestStarted.ToUniversalTime(),
            responseReceived.ToUniversalTime(), elapsed.TotalMilliseconds);
    }

    private static JsonElement Request(string method, string id, object? parameters)
    {
        if (!ReadMethods.Contains(method)) throw Error("RuntimeMethodNotReadOnly");
        var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) body["params"] = parameters;
        return JsonSerializer.SerializeToElement(body);
    }

    internal static JsonDocument Response(ReadOnlyMemory<byte> response, string requestId)
    {
        Id(requestId);
        if (response.Length is < 1 or > MaximumResponseBytes) throw Error("RuntimeResponseLimit");
        JsonDocument? doc = null;
        try
        {
            _ = Utf8.GetCharCount(response.Span);
            doc = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement; ValidateJson(root, MaximumResponseBytes);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != requestId ||
                root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _)) throw Error("RuntimeResponseInvalid");
            if (root.TryGetProperty("error", out var error) && (error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _)))
                throw Error("RuntimeResponseInvalid");
            return doc;
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException)
        { doc?.Dispose(); throw Error("RuntimeResponseInvalid"); }
        catch { doc?.Dispose(); throw; }
    }

    private static void ValidateJson(JsonElement element, int maximum)
    {
        if (element.ValueKind == JsonValueKind.Undefined) throw Error("RuntimeResponseInvalid");
        try
        {
            if (Utf8.GetByteCount(element.GetRawText()) > maximum) throw Error("RuntimeResponseLimit");
            void Visit(JsonElement value, int depth)
            {
                if (depth > 32) throw Error("RuntimeResponseInvalid");
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!keys.Add(property.Name)) throw Error("RuntimeResponseInvalid");
                        _ = Utf8.GetByteCount(property.Name); Visit(property.Value, depth + 1);
                    }
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var child in value.EnumerateArray()) Visit(child, depth + 1);
                else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
            }
            Visit(element, 0);
        }
        catch (Exception e) when (e is EncoderFallbackException or InvalidOperationException)
        { throw Error("RuntimeResponseInvalid"); }
    }

    private static void Id(string value)
    {
        if (value is not { Length: 32 } || value.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw Error("RuntimeIdentityInvalid");
    }
    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw Error("RuntimeIdentityInvalid");
        try { _ = Utf8.GetByteCount(value); } catch (EncoderFallbackException) { throw Error("RuntimeIdentityInvalid"); }
    }
    private static BridgeException Error(string code) => new(code, "Runtime protocol evidence refused; check exact scope, response shape and bounds. No remote text or credentials are returned.");
}
