using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;

if (args is ["--tls-fixtures", var directory]) { RuntimeTlsFixtures.Create(directory); return; }
if (args is ["--profile-check", var profileEndpoint, var profilePin, var profileRequest, var profileOutput]) { await RuntimeAdvertisedProfileChecks.Run(profileEndpoint, profilePin, profileRequest, profileOutput); return; }
if (args is ["--pressure", var pressureRoot]) { await RuntimeCapturePressureChecks.Run(Path.GetFullPath(pressureRoot)); return; }
if (args is ["--soak", var soakRoot, var soakSeconds]) { await RuntimeCaptureSoakChecks.Run(Path.GetFullPath(soakRoot), int.Parse(soakSeconds, System.Globalization.CultureInfo.InvariantCulture)); return; }
if (args is ["--soak-long", var longRoot, var epochCount, var epochSeconds]) { await RuntimeCaptureSoakChecks.RunLong(Path.GetFullPath(longRoot), int.Parse(epochCount, System.Globalization.CultureInfo.InvariantCulture), int.Parse(epochSeconds, System.Globalization.CultureInfo.InvariantCulture)); return; }
if (args is ["--soak-crash", var crashRoot]) { await RuntimeCaptureSoakChecks.Crash(Path.GetFullPath(crashRoot)); return; }
if (args is ["--soak-recover", var recoverRoot]) { await RuntimeCaptureSoakChecks.Recover(Path.GetFullPath(recoverRoot)); return; }
if (args is ["--disk-full", var volumeRoot, var ownerToken, var evidenceRoot]) { await RuntimeCaptureDiskFullChecks.Run(volumeRoot,ownerToken,Path.GetFullPath(evidenceRoot)); return; }

int checks = 0;
void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
void Fails(Action action, string code)
{
    try { action(); } catch (BridgeException e) when (e.Code == code)
    { Check(!e.Message.Contains("remote-secret"), "No remote error text"); return; }
    throw new Exception("Expected " + code);
}
JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
string connection = new('1', 32), epoch = new('2', 32), certificate = new('3', 64), id = new('4', 32);
var context = WebRuntimeProtocol.Context("https://PLC.example:443/api/jsonrpc", connection, epoch, certificate);
Check(context.Endpoint == "https://plc.example", "Canonical origin");
var metadata = Json("{\"name\":\"Counter\",\"datatype\":\"ulint\",\"unknownVendorField\":{\"x\":1}}");
var selected = WebRuntimeProtocol.Symbol(context, "\"MyDB\".Counter", "simple", metadata);
var request = WebRuntimeProtocol.Read(context, selected, id);
var start = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(2));
byte[] Response(string result) => Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + result + "}");
WebRuntimeReadResult Read(string result) => WebRuntimeProtocol.ReadResult(context, request, Response(result), start, start.AddMilliseconds(20), TimeSpan.FromMilliseconds(20));

Check(request.Body.GetProperty("method").GetString() == "PlcProgram.Read", "Only read method");
Check(request.Body.GetProperty("params").GetProperty("var").GetString() == "\"MyDB\".Counter", "Exact quoted symbol");
Check(Read("18446744073709551615").Value!.Value.GetRawText() == "18446744073709551615", "UInt64 precision preserved");
Check(Read("-9223372036854775808").Value!.Value.GetRawText() == "-9223372036854775808", "Int64 precision preserved");
Check(Read("1.234567890123456789e-100").Value!.Value.GetRawText() == "1.234567890123456789e-100", "Numeric representation retained without floating conversion");
var value = Read("{\"x\":[true,null,\"Text\",42]}");
Check(value.Value!.Value.GetProperty("x")[2].GetString() == "Text", "Structured JSON retained");
Check(value.RequestStartedUtc.Offset == TimeSpan.Zero && value.ResponseReceivedUtc.Offset == TimeSpan.Zero, "UTC timestamps truthful");
Check(value.SourceTimestamp is null && value.ServerTimestamp is null && !value.AtomicAcrossSignals && !value.ScanSynchronized &&
    !value.DeviceIdentityVerified && !value.ProgramMatchVerified && !value.NativeQualified, "Success never manufactures native evidence");
var clockJump = WebRuntimeProtocol.ReadResult(context, request, Response("1"), start, start.AddMinutes(-1), TimeSpan.FromMilliseconds(20));
Check(clockJump.HostClockWentBackwards && clockJump.ElapsedMilliseconds == 20, "UTC regression does not rewrite measured elapsed time");
Fails(() => WebRuntimeProtocol.ReadResult(context, request, Response("1"), start, start, TimeSpan.FromSeconds(-1)), "RuntimeTimingInvalid");
Fails(() => WebRuntimeProtocol.ReadResult(context, request, Response("1"), start, start, TimeSpan.FromMinutes(2)), "RuntimeTimingInvalid");

foreach (string badEndpoint in new[] { " https://plc.example", "https://plc.example ", "https://plc.example\\path", "https://plc.example\n" })
    Fails(() => WebRuntimeProtocol.Context(badEndpoint, connection, epoch, certificate), "RuntimeIdentityInvalid");
foreach (string badEndpoint in new[] { "http://plc.example", "https://user:pass@plc.example", "https://plc.example/?token=remote-secret", "https://plc.example/#x", "https://plc.example/elsewhere" })
    Fails(() => WebRuntimeProtocol.Context(badEndpoint, connection, epoch, certificate), "InvalidRequest");
foreach (var changed in new[] { context with { Endpoint = "https://other.example" }, context with { ConnectionId = new('5', 32) },
    context with { Epoch = new('6', 32) }, context with { PeerCertificateSha256 = new('7', 64) } })
    Fails(() => WebRuntimeProtocol.Read(changed, selected, id), "RuntimeContextChanged");
Fails(() => WebRuntimeProtocol.Validate(context with { Endpoint = "https://PLC.example" }), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Read(context, selected, "12"), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, " ", "simple", metadata), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, "\ud800", "simple", metadata), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, "x\n", "simple", metadata), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, new string('x', 4097), "simple", metadata), "RuntimeIdentityInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, "x", "guessed", metadata), "RuntimeRepresentationUnsupported");
Fails(() => WebRuntimeProtocol.Symbol(context, "x", "simple", Json("{\"name\":\"x\"}")), "RuntimeMetadataInvalid");
Fails(() => WebRuntimeProtocol.Symbol(context, "x", "simple", Json("{\"name\":\"x\",\"datatype\":\"int\",\"a\":1,\"a\":2}")), "RuntimeResponseInvalid");
var reordered = WebRuntimeProtocol.Symbol(context, selected.Symbol, "simple", Json("{\"unknownVendorField\":{\"x\":1},\"datatype\":\"ulint\",\"name\":\"Counter\"}"));
Check(reordered.MetadataSha256 == selected.MetadataSha256, "Metadata object order independent");
var altered = WebRuntimeProtocol.Symbol(context, selected.Symbol, "simple", Json("{\"name\":\"Counter\",\"datatype\":\"lint\"}"));
Check(altered.MetadataSha256 != selected.MetadataSha256, "Metadata drift changes digest");

foreach (var pair in new[] { (2,"permissionDenied"),(-32601,"unsupportedMethod"),(4,"resourceUnavailable"),(200,"addressUnavailable"),
    (201,"invalidAddress"),(203,"invalidArrayIndex"),(204,"unsupportedAddress"),(999,"providerError") })
{
    byte[] bytes = Encoding.UTF8.GetBytes($$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","error":{"code":{{{{pair.Item1}}}},"message":"remote-secret","data":{"password":"remote-secret"}}}""");
    var failed = WebRuntimeProtocol.ReadResult(context, request, bytes, start, start, TimeSpan.Zero);
    Check(failed.Value is null && failed.ProviderErrorCode == pair.Item1 && failed.Status == pair.Item2, "Explicit provider failure " + pair.Item1);
    Check(!JsonSerializer.Serialize(failed, BridgeProtocol.Json).Contains("remote-secret"), "Provider text omitted");
}
foreach (string broken in new[] { "[]", "{\"result\":1}",
    $$$$"""{"jsonrpc":"2.0","id":"other","result":1}""",
    $$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","result":1,"error":{"code":2}}""",
    $$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","result":1,"result":2}""",
    $$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","error":{"code":"2"}}""",
    $$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","error":{"code":2147483648}}""" })
    Fails(() => WebRuntimeProtocol.ReadResult(context, request, Encoding.UTF8.GetBytes(broken), start, start, TimeSpan.Zero), "RuntimeResponseInvalid");
foreach (string brokenValue in new[] { "{\"a\":1,\"a\":2}", "{\"a\":{\"b\":1,\"b\":2}}", "NaN", "[", new string('[',33) + "0" + new string(']',33) })
    Fails(() => Read(brokenValue), "RuntimeResponseInvalid");
Fails(() => WebRuntimeProtocol.ReadResult(context, request, new byte[] { 0xff, 0xfe }, start, start, TimeSpan.Zero), "RuntimeResponseInvalid");
Fails(() => WebRuntimeProtocol.ReadResult(context, request, Array.Empty<byte>(), start, start, TimeSpan.Zero), "RuntimeResponseLimit");
Fails(() => Read("\"" + new string('x', WebRuntimeProtocol.MaximumResponseBytes) + "\""), "RuntimeResponseLimit");
var forged = request with { Body = Json($$$$"""{"jsonrpc":"2.0","id":"{{{{id}}}}","method":"PlcProgram.Write","params":{"var":"x","value":1}}""") };
Fails(() => WebRuntimeProtocol.ReadResult(context, forged, Response("true"), start, start, TimeSpan.Zero), "RuntimeRequestChanged");

var raw = WebRuntimeProtocol.Read(context, selected with { Representation = "raw" }, id);
var rawResult = WebRuntimeProtocol.ReadResult(context, raw, Response("[0,255,17]"), start, start, TimeSpan.Zero);
Check(rawResult.Value!.Value.GetArrayLength() == 3, "Raw bytes retained");
foreach (string invalid in new[] { "[256]", "[-1]", "[1.5]", "[true]", "null", "42", "\"bytes\"" })
    Fails(() => WebRuntimeProtocol.ReadResult(context, raw, Response(invalid), start, start, TimeSpan.Zero), "RuntimeResponseInvalid");
var names = WebRuntimeProtocol.Names(Response("[{\"name\":\"PlcProgram.Read\"},{\"name\":\"PlcProgram.Write\"}]"), id);
Check(WebRuntimeProtocol.ReadAvailability(names, null) == "permissionUnknown", "Advertised read does not grant permission");
Check(WebRuntimeProtocol.ReadAvailability(names, []) == "permissionDenied", "Explicit no grants");
Check(WebRuntimeProtocol.ReadAvailability([], ["read_value"]) == "unsupported", "Permission cannot create method support");
Check(WebRuntimeProtocol.ReadAvailability(names, ["read_value"]) == "permittedAtObservation", "Permission evidence remains observation scoped");
Check(WebRuntimeProtocol.Discover(id).GetProperty("method").GetString() == "Api.Browse", "Discovery method");
Check(WebRuntimeProtocol.Discover(id,true).GetProperty("method").GetString() == "Api.GetPermissions", "Permissions method");
foreach (string invalid in new[] { "{}", "[1]", "[{\"name\":\"x\"},{\"name\":\"x\"}]", "[{\"name\":null}]" })
    Fails(() => WebRuntimeProtocol.Names(Response(invalid), id), "RuntimeResponseInvalid");
Check(!WebRuntimeProtocol.Browse(id).GetProperty("params").TryGetProperty("var",out _), "Root omits var instead of empty string");
Check(WebRuntimeProtocol.Browse(id,"\"DB\".x",false).GetProperty("params").GetProperty("mode").GetString() == "var", "Exact variable mode");
Fails(() => WebRuntimeProtocol.Browse(id,null,false), "RuntimeSelectionInvalid");
Check(WebRuntimeProtocol.BrowseResult(Response("[]"),id,true).GetArrayLength()==0,"Empty children valid");
Fails(() => WebRuntimeProtocol.BrowseResult(Response("[]"),id,false), "RuntimeResponseInvalid");
Fails(() => WebRuntimeProtocol.BrowseResult(Response("[{},{}]"),id,false), "RuntimeResponseInvalid");
var browse = WebRuntimeProtocol.BrowseResult(Response("[{\"name\":\"x\",\"vendorField\":1}]"),id,false);
Check(browse[0].GetProperty("vendorField").GetInt32()==1,"Unknown metadata preserved");

// Each read-only diagnostic kind: exact Siemens method, parameters, documented result shape and bounds.
WebRuntimeDiagnosticResult Diagnostic(string kind, string result, string? language = null, int? count = null)
{
    var body = WebRuntimeProtocol.Diagnostic(id, kind, language, count);
    return WebRuntimeProtocol.DiagnosticResult(context, kind, body, Response(result), start, start.AddMilliseconds(5), TimeSpan.FromMilliseconds(5));
}
foreach (var (kind, method, result) in new[] {
    ("languages", "Project.ReadLanguages", "{\"languages\":[{\"language\":\"en-US\"},{\"language\":\"de-DE\",\"active\":true}]}"),
    ("operatingMode", "Plc.ReadOperatingMode", "\"run\""),
    ("modeSelector", "Plc.ReadModeSelectorState", "{\"mode_selector\":\"no_switch\"}"),
    ("systemTime", "Plc.ReadSystemTime", "{\"timestamp\":\"2026-10-27T11:46:31.172848234Z\"}"),
    ("timeSettings", "Plc.ReadTimeSettings", "{\"current_offset\":\"PT1H\",\"utc_offset\":\"PT1H\",\"rule\":{\"std\":{\"offset\":\"PT0S\"}}}"),
    ("projectInformation", "Project.ReadInformation", "{\"project_name\":\"Line\",\"versions\":[]}") })
{
    var body = WebRuntimeProtocol.Diagnostic(id, kind);
    Check(body.GetProperty("method").GetString() == method && !body.TryGetProperty("params", out _), "Exact parameterless method " + method);
    var observed = Diagnostic(kind, result);
    Check(observed.Status == "read" && observed.Method == method && observed.Value!.Value.GetRawText() == JsonDocument.Parse(result).RootElement.GetRawText() &&
        observed.Entries is null && observed.Truncated is null && !observed.ProgramMatchVerified && !observed.LosslessHistoryVerified, "Raw bounded diagnostic " + kind);
    Fails(() => WebRuntimeProtocol.Diagnostic(id, kind, "en-US", 10), "RuntimeDiagnosticSelectionInvalid");
}
Check(Diagnostic("systemTime", "{\"timestamp\":\"2026-10-27T11:46:31.172848234Z\"}").Value!.Value.GetProperty("timestamp").GetString() == "2026-10-27T11:46:31.172848234Z", "CPU time kept as raw text, not reparsed");
foreach (var (kind, method) in new[] { ("alarms", "Alarms.Browse"), ("diagnosticBuffer", "DiagnosticBuffer.Browse") })
{
    var body = WebRuntimeProtocol.Diagnostic(id, kind, "en-US", 2);
    Check(body.GetProperty("method").GetString() == method && body.GetProperty("params").GetProperty("language").GetString() == "en-US" &&
        body.GetProperty("params").GetProperty("count").GetInt32() == 2 && body.GetProperty("params").EnumerateObject().Count() == 2, "Bounded list request " + method);
    var partial = Diagnostic(kind, "{\"entries\":[{\"id\":1},{\"id\":2}],\"count_current\":7,\"count_max\":1000,\"last_modified\":\"2026-10-27T00:00:00Z\"}", "en-US", 2);
    Check(partial.Entries == 2 && partial.Truncated == true, "Truncation explicit against count_current " + kind);
    Check(Diagnostic(kind, "{\"entries\":[{\"id\":1}],\"count_current\":1}", "en-US", 2).Truncated == false, "Complete list " + kind);
    Check(Diagnostic(kind, "{\"entries\":[]}", "en-US", 2).Truncated is null, "Unknown total is not complete " + kind);
    Fails(() => Diagnostic(kind, "{\"entries\":[{},{},{}]}", "en-US", 2), "RuntimeResponseInvalid");
    Fails(() => Diagnostic(kind, "{\"entries\":[1]}", "en-US", 2), "RuntimeResponseInvalid");
    foreach (var (language, count) in new (string?, int?)[] { (null, 2), ("en-US", null), ("en US", 2), ("e", 2), ("en-US", 0), ("en-US", 501) })
        Fails(() => WebRuntimeProtocol.Diagnostic(id, kind, language, count), "RuntimeDiagnosticSelectionInvalid");
}
foreach (var (kind, invalid) in new[] { ("operatingMode", "{\"mode\":\"run\"}"), ("modeSelector", "\"run\""), ("systemTime", "{\"timestamp\":1}"),
    ("timeSettings", "{\"current_offset\":\"PT1H\"}"), ("languages", "{\"languages\":[{\"language\":null}]}"), ("projectInformation", "[]") })
    Fails(() => Diagnostic(kind, invalid), "RuntimeResponseInvalid");
byte[] denied = Encoding.UTF8.GetBytes($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","error":{"code":2,"message":"remote-secret"}}""");
var deniedResult = WebRuntimeProtocol.DiagnosticResult(context, "diagnosticBuffer", WebRuntimeProtocol.Diagnostic(id, "diagnosticBuffer", "en-US", 5), denied, start, start, TimeSpan.Zero);
Check(deniedResult.Status == "permissionDenied" && deniedResult.ProviderErrorCode == 2 && deniedResult.Value is null &&
    !JsonSerializer.Serialize(deniedResult, BridgeProtocol.Json).Contains("remote-secret"), "Diagnostic denial explicit without remote text");
Fails(() => WebRuntimeProtocol.Diagnostic(id, "alarmsAcknowledge"), "RuntimeDiagnosticKindUnsupported");
Fails(() => WebRuntimeProtocol.DiagnosticResult(context, "operatingMode", WebRuntimeProtocol.Diagnostic(id, "systemTime"), Response("\"run\""), start, start, TimeSpan.Zero), "RuntimeRequestChanged");
Fails(() => WebRuntimeProtocol.DiagnosticResult(context, "alarms",
    Json($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","method":"Alarms.Browse","params":{"language":"en-US","count":5,"alarm_id":"1"}}"""), Response("{\"entries\":[]}"), start, start, TimeSpan.Zero), "RuntimeRequestChanged");

checks += RuntimeCaptureChecks.Run(args.Length == 1 ? Path.GetFullPath(args[0])+".captures" : Path.Combine(Path.GetTempPath(),"tia-capture-"+Guid.NewGuid().ToString("N")),Read("18446744073709551615"),metadata);
checks += await RuntimePollingChecks.Run(args.Length == 1 ? Path.GetFullPath(args[0])+".polling" : Path.Combine(Path.GetTempPath(),"tia-polling-"+Guid.NewGuid().ToString("N")));
checks += await RuntimeCaptureSoakChecks.CheckAcquisitionWaitBoundaries();

if (args.Length == 1)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
    File.WriteAllText(args[0], JsonSerializer.Serialize(new { schemaVersion="runtime-contract-test-output.v1", nativeExecuted=false,
        checks, reads=new[] { Read("18446744073709551615"), Read("-9223372036854775808"), clockJump, rawResult } }, BridgeProtocol.Json));
}
Console.WriteLine($"PASS physical-runtime protocol contracts: {checks} checks; no network, native or authenticated session execution.");
