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

/// <summary>One gated owned-PLCSIM write: Expected must equal the current simple-mode read exactly before Value is sent.</summary>
public sealed record WebRuntimeWriteItem(string SelectionId, JsonElement Expected, JsonElement Value);

/// <summary>Pure Web API request/response contracts. Supplied context is not TLS, CPU or program authentication.</summary>
public static class WebRuntimeProtocol
{
    public const int MaximumResponseBytes = 256 * 1024;
    public const int MaximumDiagnosticEntries = 500;
    public const int MaximumSyslogEntries = 20;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Dictionary<string, string> DiagnosticMethods = new(StringComparer.Ordinal)
    {
        ["languages"] = "Project.ReadLanguages",
        ["alarms"] = "Alarms.Browse",
        ["diagnosticBuffer"] = "DiagnosticBuffer.Browse",
        ["syslog"] = "Syslog.Browse",
        ["operatingMode"] = "Plc.ReadOperatingMode",
        ["modeSelector"] = "Plc.ReadModeSelectorState",
        ["systemTime"] = "Plc.ReadSystemTime",
        ["timeSettings"] = "Plc.ReadTimeSettings",
        ["projectInformation"] = "Project.ReadInformation"
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

    /// <summary>Localized lists require language/count; syslog takes only count. No acknowledgment or arbitrary filters.</summary>
    public static JsonElement Diagnostic(string requestId, string kind, string? language = null, int? count = null)
    {
        Id(requestId); string method = DiagnosticMethod(kind);
        if (kind == "syslog")
        {
            if (language is not null || count is null or < 1 or > MaximumSyslogEntries) throw Error("RuntimeDiagnosticSelectionInvalid");
            return Request(method, requestId, new { count = count.Value });
        }
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
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("count", out var limit) ||
                limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out int n))
                throw Error("RuntimeRequestChanged");
            if (kind != "syslog")
            {
                if (!parameters.TryGetProperty("language", out var tag) || tag.ValueKind != JsonValueKind.String) throw Error("RuntimeRequestChanged");
                language = tag.GetString();
            }
            count = n;
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
            static bool Counter(JsonElement node, string name) => node.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) && n >= 0;
            // Only the documented top-level shape is required; unknown vendor fields stay in the raw value.
            bool valid = kind switch
            {
                "operatingMode" => result.ValueKind == JsonValueKind.String && result.GetString()!.Length is >= 1 and <= 64,
                "modeSelector" => Field(result, "mode_selector"),
                "systemTime" => Field(result, "timestamp"),
                "timeSettings" => Field(result, "current_offset") && Field(result, "utc_offset"),
                "languages" => Rows(result, "languages", 64) && result.GetProperty("languages").EnumerateArray().All(x => Field(x, "language")),
                "alarms" or "diagnosticBuffer" => Rows(result, "entries", count!.Value),
                "syslog" => Rows(result, "entries", count!.Value) && Counter(result, "count_total") && Counter(result, "count_lost") &&
                    result.GetProperty("entries").EnumerateArray().All(row => row.TryGetProperty("raw", out var raw) &&
                        raw.ValueKind == JsonValueKind.String && raw.GetString()!.Length is > 0 and <= 65536),
                _ => result.ValueKind == JsonValueKind.Object
            };
            if (!valid) throw Error("RuntimeResponseInvalid");
            if (count is not null)
            {
                entries = result.GetProperty("entries").GetArrayLength();
                // count_current is the CPU's current record total; absent means completeness is unknown, not complete.
                // Syslog's count_total is lifetime history, not the current buffer size.
                truncated = kind != "syslog" && result.TryGetProperty("count_current", out var total) && total.ValueKind == JsonValueKind.Number &&
                    total.TryGetInt64(out long n) ? n > entries : null;
            }
            value = result.Clone();
        }
        return new(current, kind, method, requestId, status, value, code, entries, truncated, requestStarted.ToUniversalTime(),
            responseReceived.ToUniversalTime(), elapsed.TotalMilliseconds);
    }

    // Gated owned-PLCSIM mutations, kept out of the read-only builder: Siemens Web API PlcProgram.Write (write_value) and
    // Plc.RequestChangeOperatingMode (change_operating_mode), docs/plans/expansion-plans/physical-plc-runtime.md [W13]/[W14].
    private static readonly Dictionary<string, string> MutationPermissions = new(StringComparer.Ordinal)
    { ["PlcProgram.Write"] = "write_value", ["Plc.RequestChangeOperatingMode"] = "change_operating_mode", ["Alarms.Acknowledge"] = "acknowledge_alarms" };

    public static string MutationPermission(string method) =>
        method is not null && MutationPermissions.TryGetValue(method, out var permission) ? permission : throw Error("RuntimeMethodUnsupported");

    public static JsonElement Write(string requestId, WebRuntimeSymbol selection, JsonElement value)
    {
        Id(requestId);
        if (selection is null || selection.Representation != "simple" || value.ValueKind is JsonValueKind.Undefined) throw Error("RuntimeWriteInvalid");
        Text(selection.Symbol, 4096); ValidateJson(value, 16 * 1024);
        return Mutation("PlcProgram.Write", requestId, new Dictionary<string, object> { ["var"] = selection.Symbol, ["value"] = value, ["mode"] = "simple" });
    }

    public static JsonElement ChangeOperatingMode(string requestId, string mode)
    {
        Id(requestId);
        if (mode is not ("run" or "stop")) throw Error("RuntimeOperatingModeInvalid");
        return Mutation("Plc.RequestChangeOperatingMode", requestId, new Dictionary<string, object> { ["mode"] = mode });
    }

    /// <summary>Alarms.Acknowledge takes the alarm's `id` (Siemens S7-1500 Web server function manual 11/2023, A5E03484625-AK, 5.9.3, p. 221).
    /// The manual calls it the "acknowledgement ID" but only lists the Alarms.Browse entry `id` (Table 5-53); that is what is sent.</summary>
    public static JsonElement AcknowledgeAlarm(string requestId, string alarmId)
    {
        Id(requestId); AlarmId(alarmId);
        return Mutation("Alarms.Acknowledge", requestId, new Dictionary<string, object> { ["id"] = alarmId });
    }

    /// <summary>Alarms.Browse for one alarm: language and alarm_id (5.9.2, Table 5-50, p. 216). Used to check and read back an acknowledgement.</summary>
    public static JsonElement AlarmById(string requestId, string language, string alarmId)
    {
        Id(requestId); AlarmId(alarmId);
        if (language is null || language.Length is < 2 or > 35 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Error("RuntimeDiagnosticSelectionInvalid");
        return Request("Alarms.Browse", requestId, new Dictionary<string, object> { ["language"] = language, ["alarm_id"] = alarmId });
    }

    /// <summary>The acknowledgement state of the one alarm an Alarms.Browse(alarm_id) answered: "acknowledged", "not_acknowledged", null when the
    /// alarm has no acknowledgement object (nothing to acknowledge), or a provider status (for example invalidAlarmId for error 800).</summary>
    public static (string Status, int? ProviderErrorCode, string? Acknowledgement) AlarmState(ReadOnlyMemory<byte> response, string requestId, string alarmId)
    {
        using var document = Response(response, requestId);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            int code = error.GetProperty("code").GetInt32();
            return (code == 800 ? "invalidAlarmId" : ProviderStatus(code), code, null);
        }
        var result = document.RootElement.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() > 1) throw Error("RuntimeResponseInvalid");
        if (entries.GetArrayLength() == 0) return ("alarmNotListed", null, null);
        var entry = entries[0];
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != alarmId)
            throw Error("RuntimeResponseInvalid");
        if (!entry.TryGetProperty("acknowledgement", out var ack)) return ("read", null, null);
        if (ack.ValueKind != JsonValueKind.Object || !ack.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            state.GetString() is not ("acknowledged" or "not_acknowledged")) throw Error("RuntimeResponseInvalid");
        return ("read", null, state.GetString());
    }

    // Ticket-creating methods (Siemens manual 11/2023, 5.3 Ticket mechanism, p. 164; 5.6.5 p. 194; 5.9.6 p. 228; 5.10.1 p. 229). The result is a ticket
    // string redeemed once at GET /api/ticket?id=...; Api.CloseTicket(id) (5.3.2, p. 170) frees it. Plc.RestoreBackup is not implemented.
    private static readonly Dictionary<string, (string Method, string Permission)> TicketKinds = new(StringComparer.Ordinal)
    {
        ["serviceData"] = ("Modules.DownloadServiceData", "download_service_data"),
        ["profiling"] = ("PlcProgram.DownloadProfilingData", "read_value"),
        ["backup"] = ("Plc.CreateBackup", "backup_plc")
    };
    public static (string Method, string Permission) TicketKind(string kind) =>
        kind is not null && TicketKinds.TryGetValue(kind, out var known) ? known : throw Error("RuntimeTicketKindUnsupported");

    /// <summary>serviceData needs hwid (the module's hardware ID); profiling and backup take no parameters.</summary>
    public static JsonElement TicketRequest(string requestId, string kind, long? hwid = null)
    {
        Id(requestId); var (method, _) = TicketKind(kind);
        if ((kind == "serviceData") != hwid is not null || hwid is < 0 or > 0xFFFF) throw Error("RuntimeTicketSelectionInvalid");
        var body = new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = requestId, ["method"] = method };
        if (hwid is not null) body["params"] = new Dictionary<string, object> { ["hwid"] = hwid.Value };
        return JsonSerializer.SerializeToElement(body);
    }

    public static JsonElement CloseTicket(string requestId, string ticket)
    {
        Id(requestId); Ticket(ticket);
        return JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = "Api.CloseTicket",
            ["params"] = new Dictionary<string, object> { ["id"] = ticket }
        });
    }

    public static (string Status, int? ProviderErrorCode, string? Ticket) TicketResult(ReadOnlyMemory<byte> response, string requestId)
    {
        using var document = Response(response, requestId);
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            int code = error.GetProperty("code").GetInt32();
            return (code switch
            {
                2 => "permissionDenied",
                4 or 600 => "resourceUnavailable",
                7 => "partnerNotAccessible",
                1000 or 1001 => "backupBusy",
                1004 => "plcNotInStop",
                1100 => "invalidHardwareId",
                _ => ProviderStatus(code)
            }, code, null);
        }
        var result = document.RootElement.GetProperty("result");
        if (result.ValueKind != JsonValueKind.String) throw Error("RuntimeResponseInvalid");
        string ticket = result.GetString()!; Ticket(ticket);
        return ("created", null, ticket);
    }

    /// <summary>A ticket becomes part of a URL query: only base64 and URL-safe characters, so nothing from the CPU can add a parameter or a path.</summary>
    public static void Ticket(string ticket)
    {
        if (ticket is not { Length: >= 4 and <= 512 } || ticket.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=' or '-' or '_')))
            throw Error("RuntimeResponseInvalid");
    }
    private static void AlarmId(string id)
    {
        if (id is not { Length: >= 1 and <= 64 } || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw Error("RuntimeAlarmIdInvalid");
    }

    /// <summary>Boolean true only acknowledges the request; callers read back. Provider errors stay explicit and are never retried.</summary>
    public static (string Status, int? ProviderErrorCode) MutationResult(ReadOnlyMemory<byte> response, string requestId)
    {
        using var document = Response(response, requestId);
        if (document.RootElement.TryGetProperty("error", out var error)) { int code = error.GetProperty("code").GetInt32(); return (ProviderStatus(code), code); }
        if (document.RootElement.GetProperty("result").ValueKind != JsonValueKind.True) throw Error("RuntimeResponseInvalid");
        return ("accepted", null);
    }

    private static JsonElement Mutation(string method, string id, Dictionary<string, object> parameters) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });

    private static string ProviderStatus(int code) => code switch
    {
        2 => "permissionDenied",
        -32601 => "unsupportedMethod",
        4 => "resourceUnavailable",
        200 => "addressUnavailable",
        201 => "invalidAddress",
        203 => "invalidArrayIndex",
        204 => "unsupportedAddress",
        _ => "providerError"
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
