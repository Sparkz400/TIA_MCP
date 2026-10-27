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
/// <summary>One bounded read-only CPU diagnostic observation; RequestId is null when the method was not advertised and nothing was sent.</summary>
public sealed record WebRuntimeDiagnosticResult(WebRuntimeContext Context, string Kind, string Method, string? RequestId, string Status,
    JsonElement? Value, int? ProviderErrorCode, int? Entries, bool? Truncated,
    DateTimeOffset RequestStartedUtc, DateTimeOffset ResponseReceivedUtc, double ElapsedMilliseconds)
{
    public string SchemaVersion => "physical-runtime-web-diagnostic.v1";
    public string ValueEncoding => "siemens-web-api-json";
    public string Clock => "hostUtcAndMonotonicDuration";
    public bool HostClockWentBackwards => ResponseReceivedUtc < RequestStartedUtc;
    public bool LosslessHistoryVerified => false;
    public bool DeviceIdentityVerified => false;
    public bool ProgramMatchVerified => false;
    public bool NativeQualified => false;
}

/// <summary>Pure Web API request/response contracts. Supplied context is not TLS, CPU or program authentication.</summary>
public static class WebRuntimeProtocol
{
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumDiagnosticEntries = 500;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<string, string> DiagnosticMethods = new(StringComparer.Ordinal)
    {
        ["languages"] = "Project.ReadLanguages", ["alarms"] = "Alarms.Browse", ["diagnosticBuffer"] = "DiagnosticBuffer.Browse",
        ["operatingMode"] = "Plc.ReadOperatingMode", ["modeSelector"] = "Plc.ReadModeSelectorState",
        ["systemTime"] = "Plc.ReadSystemTime", ["timeSettings"] = "Plc.ReadTimeSettings", ["projectInformation"] = "Project.ReadInformation"
    };
    private static readonly HashSet<string> ReadMethods = new(DiagnosticMethods.Values.Concat(
        ["Api.Browse", "Api.GetPermissions", "PlcProgram.Browse", "PlcProgram.Read"]), StringComparer.Ordinal);

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
            status = ProviderStatus(code.Value);
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

    public static string DiagnosticMethod(string kind) =>
        kind is not null && DiagnosticMethods.TryGetValue(kind, out var method) ? method : throw Error("RuntimeDiagnosticKindUnsupported");

    /// <summary>Only alarms/diagnosticBuffer take a language and a bounded count; acknowledgment, filters and alarm IDs are not exposed.</summary>
    public static JsonElement Diagnostic(string requestId, string kind, string? language = null, int? count = null)
    {
        Id(requestId); string method = DiagnosticMethod(kind);
        bool listed = kind is "alarms" or "diagnosticBuffer";
        if (listed != language is not null || listed != count is not null) throw Error("RuntimeDiagnosticSelectionInvalid");
        if (!listed) return Request(method, requestId, null);
        // A BCP 47 tag as returned by Project.ReadLanguages, for example en-US.
        if (language!.Length is < 2 or > 35 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || count is < 1 or > MaximumDiagnosticEntries)
            throw Error("RuntimeDiagnosticSelectionInvalid");
        return Request(method, requestId, new Dictionary<string, object> { ["language"] = language, ["count"] = count!.Value });
    }

    public static WebRuntimeDiagnosticResult DiagnosticResult(WebRuntimeContext current, string kind, JsonElement request,
        ReadOnlyMemory<byte> response, DateTimeOffset requestStarted, DateTimeOffset responseReceived, TimeSpan elapsed)
    {
        Validate(current); string method = DiagnosticMethod(kind);
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            !request.TryGetProperty("method", out var sent) || sent.ValueKind != JsonValueKind.String || sent.GetString() != method)
            throw Error("RuntimeRequestChanged");
        string requestId = id.GetString()!; string? language = null; int? count = null;
        if (request.TryGetProperty("params", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("language", out var tag) || tag.ValueKind != JsonValueKind.String ||
                !parameters.TryGetProperty("count", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out int n))
                throw Error("RuntimeRequestChanged");
            language = tag.GetString(); count = n;
        }
        if (!JsonElement.DeepEquals(request, Diagnostic(requestId, kind, language, count))) throw Error("RuntimeRequestChanged");
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromMinutes(1)) throw Error("RuntimeTimingInvalid");
        using var document = Response(response, requestId);
        JsonElement? value = null; int? code = null, entries = null; bool? truncated = null; string status = "read";
        if (document.RootElement.TryGetProperty("error", out var error)) { code = error.GetProperty("code").GetInt32(); status = ProviderStatus(code.Value); }
        else
        {
            var result = document.RootElement.GetProperty("result");
            static bool Field(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.String && v.GetString()!.Length is >= 1 and <= 256;
            static bool Rows(JsonElement node, string name, int maximum) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= maximum && v.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Object);
            // Only the documented top-level shape is required; unknown vendor fields stay in the raw value.
            bool valid = kind switch
            {
                "operatingMode" => result.ValueKind == JsonValueKind.String && result.GetString()!.Length is >= 1 and <= 64,
                "modeSelector" => Field(result, "mode_selector"),
                "systemTime" => Field(result, "timestamp"),
                "timeSettings" => Field(result, "current_offset") && Field(result, "utc_offset"),
                "languages" => Rows(result, "languages", 64) && result.GetProperty("languages").EnumerateArray().All(x => Field(x, "language")),
                "alarms" or "diagnosticBuffer" => Rows(result, "entries", count!.Value),
                _ => result.ValueKind == JsonValueKind.Object
            };
            if (!valid) throw Error("RuntimeResponseInvalid");
            if (count is not null)
            {
                entries = result.GetProperty("entries").GetArrayLength();
                // count_current is the CPU's current record total; absent means completeness is unknown, not complete.
                truncated = result.TryGetProperty("count_current", out var total) && total.ValueKind == JsonValueKind.Number &&
                    total.TryGetInt64(out long n) ? n > entries : null;
            }
            value = result.Clone();
        }
        return new(current, kind, method, requestId, status, value, code, entries, truncated, requestStarted.ToUniversalTime(),
            responseReceived.ToUniversalTime(), elapsed.TotalMilliseconds);
    }

    private static string ProviderStatus(int code) => code switch
    {
        2 => "permissionDenied", -32601 => "unsupportedMethod", 4 => "resourceUnavailable",
        200 => "addressUnavailable", 201 => "invalidAddress", 203 => "invalidArrayIndex",
        204 => "unsupportedAddress", _ => "providerError"
    };

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
