using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using TiaMcp.Core;
using TiaMcp.Core.Inventory;
using TiaMcp.Host;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Fails(Action action, string code) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
async Task FailsAsync(Func<Task> action, string code) { try { await action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
InventoryRow Row(int i) => new("/devices/d/items/" + i, "Module " + i, "Station", "module", "/devices/d/items/" + i, "/devices/d", "project.ap21",
    InventoryRow.ConfiguredKeys.ToDictionary(k => k, k => InventoryField.Missing("notExposed", "Not supported by this fixture.", k)));
var coverage = new InventoryCoverage(true, false, 30000, 50000, []);
var store = new InventoryStore();
var snapshot = store.Capture(Enumerable.Range(0, 30000).Select(Row), "project.ap21", coverage, DateTimeOffset.UtcNow);
var seen = new HashSet<string>(); string? cursor = null; int pages = 0;
do
{
    var page = store.Page(snapshot.Metadata.SnapshotId, 173, cursor);
    Check(page.Offset == seen.Count && page.Rows.Length <= 173, "Contiguous bounded pages");
    foreach (var row in page.Rows) Check(seen.Add(row.RowId), "No duplicated rows");
    cursor = page.NextCursor; pages++;
} while (cursor != null);
Check(seen.Count == 30000 && pages > 170, "Large inventory was not silently truncated");
var first = store.Page(snapshot.Metadata.SnapshotId, 1);
var replay = store.Page(snapshot.Metadata.SnapshotId, 1, first.NextCursor);
Check(replay.Rows.Single().Name == "Module 1", "Cursor replay");
Fails(() => store.Page(snapshot.Metadata.SnapshotId, 1, first.NextCursor + "x"), "InvalidCursor");
Fails(() => store.Page(snapshot.Metadata.SnapshotId, 0), "InvalidRequest");
var other = store.Capture([Row(7)], "project.ap21", coverage, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)));
Check(other.Metadata.CapturedAtUtc.Offset == TimeSpan.Zero, "Capture UTC label matches the timestamp offset");
Fails(() => store.Page(other.Metadata.SnapshotId, 1, first.NextCursor), "InvalidCursor");
first.Rows[0].Configured["mlfb"] = InventoryField.Read("tampered", "test");
Check(store.Page(snapshot.Metadata.SnapshotId, 1).Rows[0].Configured["mlfb"].State == "notExposed", "Page cannot mutate snapshot");
Fails(() => store.Capture([Row(1), Row(1)], "project.ap21", coverage, DateTimeOffset.UtcNow), "InventoryDuplicateIdentity");
var row1 = Row(1); row1.Configured["mlfb"] = InventoryField.Read("6ES7 151-1AA00", "project"); row1.Configured["firmware"] = InventoryField.Read("V4.1", "project");
row1 = row1 with { Actual = new() { ["mlfb"] = InventoryField.Read("6ES7151-1AA00", "online"), ["firmware"] = InventoryField.Read("V4.1.0", "online"),
    ["serialNumber"] = InventoryField.Read("001234567890123456", "online"), ["hardwareRevision"] = InventoryField.Read("1", "online") } };
Check(row1.Compare().Comparisons["firmware"] == "match" && row1.Compare().Comparisons["mlfb"] == "match", "Conservative normalization");
row1.Actual["firmware"] = InventoryField.Read("V4.2.0", "online");
Check(row1.Compare().Comparisons["firmware"] == "mismatch", "Firmware mismatch reported");
Check(row1.Compare().Comparisons["hardwareRevision"] == "notComparable", "Missing values are not matches");
row1.Configured["mlfb"] = InventoryField.Read("6ES7 151-1xA00", "project");
Check(row1.Compare().Comparisons["mlfb"] == "notComparable", "Catalog wildcard isn't exact identity");
var festo = Row(3); festo.Configured["mlfb"] = InventoryField.Read("CPX-AP-I", "project"); festo.Actual["mlfb"] = InventoryField.Read("CPX-AP-I", "online");
Check(festo.Compare().Comparisons["mlfb"] == "match", "Literal X in a vendor product name is not a catalog placeholder");
var missing = SiemensWebIdentity.Failure(row1, "https://192.0.2.10", 1, new BridgeException("OnlineTimeout", "timed out"));
Check(missing.Actual.Values.All(v => v.Value == null) && missing.Configured["firmware"].Value == "V4.1", "Failed refresh never retains stale actual values or alters configured ones");

var handler = new FakeHandler();
using (var provider = new SiemensWebIdentity("https://192.0.2.10", "test-token", handler))
{
    await provider.ProbeAsync(default);
    var identified = await provider.IdentifyAsync(Row(1), 12, default);
    Check(identified.Actual["serialNumber"].Value == "SERIAL-0001" && identified.Actual["hardwareRevision"].Value == "0" && identified.Actual["firmware"].Value == "V4.1.0", "Actual I&M0 mapping preserves zero revision");
    Check(handler.Methods.SequenceEqual(["Api.Browse", "Modules.Browse", "Modules.ReadIdentificationMaintenance"]), "Only allowlisted read methods called");
    Check(handler.LastType == "actual" && handler.LastNumber == 0, "No configured fallback");
    handler.Name = "Wrong module";
    await FailsAsync(() => provider.IdentifyAsync(Row(1), 12, default), "IdentityMismatch");
    handler.Name = "Module 1";
    handler.ApiCode = 1103; await FailsAsync(() => provider.IdentifyAsync(Row(1), 12, default), "ImNotSupported");
    handler.ApiCode = 2; await FailsAsync(() => provider.ProbeAsync(default), "PermissionDenied");
    handler.ApiCode = null; handler.Unsupported = true; await FailsAsync(() => provider.ProbeAsync(default), "UnsupportedInterface");
    handler.Unsupported = false; handler.BadId = true; await FailsAsync(() => provider.ProbeAsync(default), "InvalidOnlineResponse");
    handler.BadId = false; handler.Malformed = true; await FailsAsync(() => provider.ProbeAsync(default), "InvalidOnlineResponse");
    handler.Malformed = false; handler.Huge = true; await FailsAsync(() => provider.ProbeAsync(default), "OnlineResponseLimit");
}
// Well-formed JSON can still violate the expected envelope or method payload.
using (var malformed = new SiemensWebIdentity("https://192.0.2.10", "test-token", handler = new FakeHandler()))
{
    foreach (var payload in new[] { "null", "[]", "1", "true", "\"text\"", "{}", "{\"nodes\":null}", "{\"nodes\":{}}" })
    {
        handler.ResultOverride = JsonSerializer.Deserialize<JsonElement>(payload);
        await FailsAsync(() => malformed.NodeAsync(12, default), "InvalidOnlineResponse");
        await FailsAsync(() => malformed.BrowseAsync(12, default), "InvalidOnlineResponse");
    }
    handler.OverrideMethod = "Modules.ReadIdentificationMaintenance";
    foreach (var payload in new[] { "null", "[]", "1", "true", "\"text\"", "{}", "{\"data\":null}", "{\"data\":[]}" })
    {
        handler.ResultOverride = JsonSerializer.Deserialize<JsonElement>(payload);
        await FailsAsync(() => malformed.IdentifyAsync(Row(1), 12, default), "InvalidOnlineResponse");
    }
    handler.ResultOverride = null;
    foreach (var envelope in new[] { "missingVersion", "wrongVersion", "both", "neither", "duplicateId", "invalidError" })
    {
        handler.Envelope = envelope;
        await FailsAsync(() => malformed.ProbeAsync(default), "InvalidOnlineResponse");
    }
    handler.Envelope = null;
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
    {
        handler.Status = status;
        await FailsAsync(() => malformed.ProbeAsync(default), "PermissionDenied");
    }
    handler.Status = HttpStatusCode.Redirect;
    await FailsAsync(() => malformed.ProbeAsync(default), "OnlineHttpError");
    handler.Status = HttpStatusCode.OK;
    handler.TransportFailure = true;
    await FailsAsync(() => malformed.IdentifyAsync(Row(1), 12, default), "OnlineUnreachable");
    handler.TransportFailure = false;
    await malformed.ProbeAsync(default);
    Check((await malformed.IdentifyAsync(Row(1), 12, default)).Online.Status == "read", "Same provider recovers after malformed/auth/transport responses without stale actual fallback");
}

// Neither a returned metadata record nor an earlier page may rewrite retained coverage.
var metadataStore = new InventoryStore();
var mutableWarnings = new[] { "Original traversal warning." };
var metadataSnapshot = metadataStore.Capture([Row(1)], "project.ap21", coverage with { Warnings = mutableWarnings }, DateTimeOffset.UtcNow);
mutableWarnings[0] = "Changed caller warning";
metadataSnapshot.Metadata.Coverage.Warnings[0] = "Changed snapshot warning";
metadataStore.Page(metadataSnapshot.Metadata.SnapshotId).Snapshot.Coverage.Warnings[0] = "Changed page warning";
Check(metadataStore.Get(metadataSnapshot.Metadata.SnapshotId).Metadata.Coverage.Warnings.Single() == "Original traversal warning.", "Coverage remains immutable across caller, snapshot and page references");

Fails(() => SiemensWebIdentity.ValidateEndpoint("http://192.0.2.10"), "InvalidRequest");
Fails(() => SiemensWebIdentity.ValidateEndpoint("https://user:secret@192.0.2.10"), "InvalidRequest");
Fails(() => SiemensWebIdentity.ValidateEndpoint("https://192.0.2.10/other?token=secret"), "InvalidRequest");
Check(InventoryExport.CsvCell("=1+1") == "\"'=1+1\"" && InventoryExport.CsvCell("  @SUM(1)").StartsWith("\"'"), "CSV formula neutralization");
Check(InventoryExport.CsvCell("quoted,\"text\"\nnew") == "\"quoted,\"\"text\"\"\nnew\"", "CSV quoting/newlines");

var output = Path.Combine(Path.GetTempPath(), "tia-inventory-tests-" + Guid.NewGuid().ToString("N"));
var exportSnapshot = store.Capture([row1 with { Name = "=HYPERLINK(\"bad\")" }, Row(2)], "project.ap21",
    coverage with { ScopeComplete = false, Truncated = true, Warnings = ["Stopped at requested bound."] }, DateTimeOffset.UtcNow);
var csv = JsonSerializer.SerializeToElement(InventoryExport.Write(exportSnapshot, output, "csv"), InventoryStore.Json);
var csvText = File.ReadAllText(csv.GetProperty("artifact").GetProperty("path").GetString()!);
Check(csvText.Contains("[notRead]") && csvText.Contains("mismatch") && csvText.Contains("'="), "CSV includes gaps, mismatches and literal formulas");
var xlsx = JsonSerializer.SerializeToElement(InventoryExport.Write(exportSnapshot, output, "xlsx"), InventoryStore.Json);
using (var zip = ZipFile.OpenRead(xlsx.GetProperty("artifact").GetProperty("path").GetString()!))
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var sheet = XDocument.Load(stream);
    Check(!sheet.Descendants(ns + "f").Any() && sheet.Descendants(ns + "row").Count() == 3, "XLSX has all rows and no formulas");
    Check(sheet.Descendants(ns + "t").Any(t => t.Value == "001234567890123456"), "Long serial stays text");
    Check(sheet.Descendants(ns + "pane").Single().Attribute("state")!.Value == "frozen" && sheet.Descendants(ns + "autoFilter").Any(), "Usable workbook headers/filter");
    using var metaStream = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open(); var meta = XDocument.Load(metaStream);
    Check(meta.Descendants(ns + "t").Any(t => t.Value == "Stopped at requested bound."), "Partial scope exported explicitly");
}

// Host behavior with absent native workers: errors before network or worker activity, and snapshot export.
var bridge = new RejectBridge(); var options = new HostOptions("missing", output, null, 10, false, "missing", null);
var tools = new DeviceInventoryTools(bridge, options, store, new SiemensWebIdentityFactory());
var badPage = await tools.Inventory(default, pageSize: 0); Check(badPage.IsError == true && bridge.Calls == 0, "Validate before worker dispatch");
var badBinding = await tools.Identify(exportSnapshot.Metadata.SnapshotId, "https://192.0.2.10", row1.RowId, 1, [new(row1.RowId, 1)], default);
Check(badBinding.IsError == true, "Non-CPU controller is rejected before network access");
Check(tools.Export(exportSnapshot.Metadata.SnapshotId, "xlsx").IsError != true, "Host export route");
var cpu = Row(0) with { Kind = "cpu" }; cpu.Configured["ip"] = InventoryField.Read("192.0.2.10", "native interface");
var staleModule = Row(2); staleModule.Actual["serialNumber"] = InventoryField.Read("STALE", "old observation");
var source = store.Capture([cpu, Row(1), staleModule], "project.ap21", coverage, DateTimeOffset.UtcNow);
var factory = new FixtureFactory();
var onlineTools = new DeviceInventoryTools(bridge, options, store, factory);
var onlineResult = await onlineTools.Identify(source.Metadata.SnapshotId, "https://192.0.2.10", cpu.RowId, 101,
    [new(Row(1).RowId, 12), new(Row(2).RowId, 13)], default);
Check(onlineResult.IsError != true && bridge.Calls == 0, "Host identification does not invoke Openness or change the project");
var onlineJson = JsonSerializer.SerializeToElement(onlineResult.StructuredContent, InventoryStore.Json);
var enrichedId = onlineJson.GetProperty("snapshot").GetProperty("snapshotId").GetString()!;
var enrichedRows = store.Get(enrichedId).ReadRows().ToArray();
Check(enrichedId != source.Metadata.SnapshotId && !onlineJson.GetProperty("onlineScopeComplete").GetBoolean(), "Partial identification creates a separate snapshot");
Check(enrichedRows[1].Actual["serialNumber"].Value == "SERIAL-0001" && enrichedRows[2].Actual["serialNumber"].State == "unsupported" && enrichedRows[2].Actual["serialNumber"].Value == null, "Per-module failure preserves success and clears stale actual data");
Check(store.Get(source.Metadata.SnapshotId).ReadRows().ElementAt(2).Actual["serialNumber"].Value == "STALE", "Original snapshot remains immutable");
int requestsBefore = factory.Handler.Methods.Count;
var wrongEndpoint = await onlineTools.Identify(source.Metadata.SnapshotId, "https://192.0.2.99", cpu.RowId, 101, [new(Row(1).RowId, 12)], default);
Check(wrongEndpoint.IsError == true && factory.Handler.Methods.Count == requestsBefore, "Reject wrong endpoint without network I/O");
Check(!onlineJson.GetRawText().Contains("SECRET MUST NEVER BE ECHOED") && !onlineJson.GetRawText().Contains("test-token"), "No remote error text or token enters inventory");
Check(store.Release(other.Metadata.SnapshotId), "Release snapshot");
Fails(() => store.Get(other.Metadata.SnapshotId), "InventorySnapshotUnavailable");
// Malformed module payloads must not abort successful peers or retain stale observations.
var recoveryStore = new InventoryStore();
var recoverySource = recoveryStore.Capture([cpu, Row(1), staleModule], "project.ap21",
    coverage with { Warnings = ["Original traversal warning."] }, DateTimeOffset.UtcNow);
var recoveryFactory = new FixtureFactory(); recoveryFactory.Handler.UnavailableHardwareId = null;
recoveryFactory.Handler.MalformedHardwareId = 13;
var recoveryTools = new DeviceInventoryTools(bridge, options, recoveryStore, recoveryFactory);
var recovery = await recoveryTools.Identify(recoverySource.Metadata.SnapshotId, "https://192.0.2.10", cpu.RowId, 101,
    [new(Row(1).RowId, 12), new(Row(2).RowId, 13)], default);
var recoveryJson = JsonSerializer.SerializeToElement(recovery.StructuredContent, InventoryStore.Json);
var recoveryId = recoveryJson.GetProperty("snapshot").GetProperty("snapshotId").GetString()!;
var recoveryRows = recoveryStore.Get(recoveryId).ReadRows().ToArray();
Check(recovery.IsError != true && !recoveryJson.GetProperty("onlineScopeComplete").GetBoolean() &&
    recoveryRows[1].Online.Status == "read" && recoveryRows[2].Actual.Values.All(f => f.State == "readError" && f.Value == null),
    "Malformed module becomes an explicit failed observation while successful peer remains available");
Check(recoveryRows[2].Online.Endpoint == "https://192.0.2.10" && recoveryRows[2].Online.HardwareId == 13 &&
    recoveryRows[2].Online.ObservedAtUtc != null && recoveryStore.Get(recoveryId).Metadata.ParentSnapshotId == recoverySource.Metadata.SnapshotId,
    "Failure preserves controller, hardware ID, observation time and snapshot lineage");
foreach (var format in new[] { "csv", "xlsx" })
{
    var exported = JsonSerializer.SerializeToElement(InventoryExport.Write(recoveryStore.Get(recoveryId), output, format), InventoryStore.Json);
    var exportPath = exported.GetProperty("artifact").GetProperty("path").GetString()!;
    string content;
    if (format == "csv") content = File.ReadAllText(exportPath);
    else
    {
        using var zip = ZipFile.OpenRead(exportPath);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        content = XDocument.Load(stream).ToString();
    }
    Check(content.Contains("[readError]") && content.Contains("SERIAL-0001") && !content.Contains("STALE"), "Failed and successful observations survive " + format + " export without stale values");
}
recoveryFactory.Handler.MalformedHardwareId = null;
recoveryFactory.Handler.Status = HttpStatusCode.Unauthorized;
var denied = await recoveryTools.Identify(recoveryId, "https://192.0.2.10", cpu.RowId, 101, [new(Row(1).RowId, 12)], default);
var deniedJson = JsonSerializer.SerializeToElement(denied.StructuredContent, InventoryStore.Json);
var deniedId = deniedJson.GetProperty("snapshot").GetProperty("snapshotId").GetString()!;
Check(recoveryStore.Get(deniedId).ReadRows().ElementAt(1).Actual.Values.All(f => f.State == "permissionDenied" && f.Value == null),
    "Expired authorization clears a previously successful observation in the child snapshot");
Check(recoveryStore.Get(recoveryId).ReadRows().ElementAt(1).Actual["serialNumber"].Value == "SERIAL-0001" &&
    recoveryStore.Get(recoverySource.Metadata.SnapshotId).ReadRows().ElementAt(2).Actual["serialNumber"].Value == "STALE",
    "Failed refresh cannot mutate either ancestor");
recoveryFactory.Handler.Status = HttpStatusCode.OK;
var recovered = await recoveryTools.Identify(deniedId, "https://192.0.2.10", cpu.RowId, 101, [new(Row(1).RowId, 12)], default);
var recoveredJson = JsonSerializer.SerializeToElement(recovered.StructuredContent, InventoryStore.Json);
Check(recoveredJson.GetProperty("onlineScopeComplete").GetBoolean() && bridge.Calls == 0,
    "Explicit retry recovers after authorization returns with no native worker activity");
int beforeRefusal = recoveryFactory.Handler.Methods.Count;
var invalidBinding = await recoveryTools.Identify(deniedId, "https://192.0.2.10", cpu.RowId, 101, [new(Row(1).RowId, 101)], default);
Check(invalidBinding.IsError == true && recoveryFactory.Handler.Methods.Count == beforeRefusal,
    "Controller hardware ID cannot be rebound to another row, even before transport");
var nullBinding = await recoveryTools.Identify(deniedId, "https://192.0.2.10", cpu.RowId, 101, [null!], default);
Check(nullBinding.IsError == true && recoveryFactory.Handler.Methods.Count == beforeRefusal, "Null binding refuses before transport");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await recoveryTools.Identify(deniedId, "https://192.0.2.10", cpu.RowId, 101, [new(Row(1).RowId, 12)], cancellation.Token);
        throw new Exception("Expected caller cancellation");
    }
    catch (OperationCanceledException) { checks++; }
    Check(recoveryStore.Get(deniedId).ReadRows().ElementAt(1).Online.Status == "permissionDenied", "Caller cancellation leaves the selected snapshot unchanged");
}
var ambiguousStore = new InventoryStore();
var ambiguousSource = ambiguousStore.Capture([cpu, Row(1) with { Name = cpu.Name }], "project.ap21", coverage, DateTimeOffset.UtcNow);
var ambiguousFactory = new FixtureFactory();
var ambiguousTools = new DeviceInventoryTools(bridge, options, ambiguousStore, ambiguousFactory);
var ambiguous = await ambiguousTools.Identify(ambiguousSource.Metadata.SnapshotId, "https://192.0.2.10", cpu.RowId, 101,
    [new(Row(1).RowId, 101)], default);
Check(ambiguous.IsError == true && ambiguousFactory.Handler.Methods.Count == 0,
    "Even an exact duplicate name cannot associate another row with the controller's identity");
ambiguousFactory.Handler.OverrideMethod = "Modules.Browse";
ambiguousFactory.Handler.ResultOverride = JsonSerializer.Deserialize<JsonElement>("null");
var commonMalformed = await ambiguousTools.Identify(ambiguousSource.Metadata.SnapshotId, "https://192.0.2.10", cpu.RowId, 101,
    [new(cpu.RowId, 101), new(Row(1).RowId, 12)], default);
var commonJson = JsonSerializer.SerializeToElement(commonMalformed.StructuredContent, InventoryStore.Json);
Check(commonMalformed.IsError != true && !commonJson.GetProperty("onlineScopeComplete").GetBoolean() &&
    commonJson.GetProperty("selectedRows").EnumerateArray().All(r => r.GetProperty("online").GetProperty("status").GetString() == "readError") &&
    ambiguousFactory.Handler.Methods.SequenceEqual(["Api.Browse", "Modules.Browse"]),
    "Malformed controller response records failure for every selection without any I&M read");
// Decode the saved OOXML independently of the writer, then compare with the source strings.
string DecodeCell(string text) => Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", match =>
    ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
string[] ExportNames(InventoryStore.Snapshot captured, string format, string directory)
{
    var receipt = JsonSerializer.SerializeToElement(InventoryExport.Write(captured, directory, format), InventoryStore.Json);
    var path = receipt.GetProperty("artifact").GetProperty("path").GetString()!;
    using (var input = File.OpenRead(path))
        Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input)) == receipt.GetProperty("artifact").GetProperty("sha256").GetString(), "Export receipt binds exact bytes");
    if (format == "csv")
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); parser.ReadFields();
        var names = new List<string>();
        while (!parser.EndOfData) names.Add(parser.ReadFields()![0]);
        return names.ToArray();
    }
    using var zip = ZipFile.OpenRead(path);
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var sheet = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    Check(!sheet.Descendants(ns + "f").Any() && sheet.Descendants(ns + "c").All(c => (string?)c.Attribute("t") == "inlineStr"), "Every XLSX cell remains literal text");
    using var detailsStream = zip.GetEntry("xl/worksheets/sheet2.xml")!.Open();
    var details = XDocument.Load(detailsStream, LoadOptions.PreserveWhitespace);
    Check(details.Descendants(ns + "row").Any(r => r.Elements(ns + "c").Select(c => DecodeCell(c.Element(ns + "is")!.Element(ns + "t")!.Value))
        .SequenceEqual(["Warnings", string.Join("; ", captured.Metadata.Coverage.Warnings)])), "Capture-detail warnings preserve their exact text too");
    return sheet.Root!.Element(ns + "sheetData")!.Elements(ns + "row").Skip(1)
        .Select(r => DecodeCell(r.Elements(ns + "c").First().Element(ns + "is")!.Element(ns + "t")!.Value)).ToArray();
}
var textStore = new InventoryStore();
string[] literalNames = ["_x000D_", "_x005F_x000D_", "_x000a__xFFFF_", "_xABCD_x0001_", "_X000D_ is literal", "_x00ZZ_ is literal",
    "one\rtwo\nthree\r\nfour", "=HYPERLINK(\"bad\")", "  @SUM(1)", "quoted,\"literal\"", "  leading and trailing  ", "Grüße 日本語 🐝", new string('a', 32767), string.Concat(Enumerable.Repeat("_x000D_", 4681))];
var literals = textStore.Capture(literalNames.Select((name, i) => Row(i) with { Name = name }), "project.ap21", coverage, DateTimeOffset.UtcNow);
Check(ExportNames(literals, "xlsx", output).SequenceEqual(literalNames), "XLSX preserves escape-looking strings, overlapping underscores, CR/LF, whitespace, Unicode and maximum cell text");
var csvNames = ExportNames(literals, "csv", output);
Check(csvNames.Select((name, i) => name == (i is 7 or 8 ? "'" + literalNames[i] : literalNames[i])).All(x => x), "CSV preserves source text except documented formula neutralization");
string[] controlNames = ["NUL\0end", "bell\aend", "vertical\vend", "form\fend", "unit\u001Fend", "noncharacters\uFFFE\uFFFF", "tab\tend", "all" + new string(Enumerable.Range(0, 32).Select(i => (char)i).ToArray()) + "end"];
var controls = textStore.Capture(controlNames.Select((name, i) => Row(i) with { Name = name }), "project.ap21",
    coverage with { Warnings = ["warning\r\n_x000D_\0"] }, DateTimeOffset.UtcNow);
Check(ExportNames(controls, "xlsx", output).SequenceEqual(controlNames), "XML-prohibited characters round-trip through escaped XLSX text");
var controlsCsv = JsonSerializer.SerializeToElement(InventoryExport.Write(controls, output, "csv"), InventoryStore.Json);
var controlsCsvText = File.ReadAllText(controlsCsv.GetProperty("artifact").GetProperty("path").GetString()!);
// TextFieldParser normalizes some embedded control/newline combinations; check retained bytes here.
Check(controlNames.All(name => controlsCsvText.Contains("\"" + name + "\",\"Station\",", StringComparison.Ordinal)), "CSV preserves every control character in the exact source text");
var cleanupDirectory = Path.Combine(output, "failed-exports"); Directory.CreateDirectory(cleanupDirectory);
string existingFile = Path.Combine(cleanupDirectory, "existing.txt"); File.WriteAllText(existingFile, "keep exact bytes");
var tooLong = textStore.Capture([Row(1) with { Name = new string('b', 32768) }], "project.ap21", coverage, DateTimeOffset.UtcNow);
Fails(() => InventoryExport.Write(tooLong, cleanupDirectory, "xlsx"), "ExportCellLimit");
Check(Directory.GetFiles(cleanupDirectory).SequenceEqual([existingFile]) && File.ReadAllText(existingFile) == "keep exact bytes", "Rejected XLSX leaves no partial artifact and preserves existing files");
Check(ExportNames(tooLong, "csv", cleanupDirectory).Single() == new string('b', 32768), "CSV retains text exceeding the Excel cell limit");
var lateFailure = textStore.Capture([Row(1)], "project.ap21", coverage with { Warnings = [new string('c', 32768)] }, DateTimeOffset.UtcNow);
var beforeLateFailure = Directory.GetFiles(cleanupDirectory).Order().ToArray();
Fails(() => InventoryExport.Write(lateFailure, cleanupDirectory, "xlsx"), "ExportCellLimit");
Check(Directory.GetFiles(cleanupDirectory).Order().SequenceEqual(beforeLateFailure), "Failure in capture-details sheet removes the incomplete workbook without touching earlier exports");
var exportTools = new DeviceInventoryTools(bridge, options, textStore, new SiemensWebIdentityFactory());
var hostArtifactsBefore = Directory.GetFiles(Path.Combine(output, "inventory")).Order().ToArray();
var rejectedExport = exportTools.Export(tooLong.Metadata.SnapshotId, "xlsx");
var rejectedJson = JsonSerializer.SerializeToElement(rejectedExport.StructuredContent, InventoryStore.Json);
Check(rejectedExport.IsError == true && rejectedJson.GetProperty("error").GetProperty("code").GetString() == "ExportCellLimit" && bridge.Calls == 0,
    "Host export returns the cell-limit refusal without native activity");
Check(Directory.GetFiles(Path.Combine(output, "inventory")).Order().SequenceEqual(hostArtifactsBefore), "Host refusal leaves existing artifact inventory intact");
// Stream the large export checks so validation does not load the workbook into a DOM.
var scaleOutput = Path.Combine(output, "scale");
foreach (var format in new[] { "csv", "xlsx" })
{
    var receipt = JsonSerializer.SerializeToElement(InventoryExport.Write(snapshot, scaleOutput, format), InventoryStore.Json);
    var path = receipt.GetProperty("artifact").GetProperty("path").GetString()!;
    int count = 0; bool exactNames = true;
    if (format == "csv")
    {
        using var parser = new TextFieldParser(path, Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(","); parser.ReadFields();
        while (!parser.EndOfData) { exactNames &= parser.ReadFields()![0] == "Module " + count; count++; }
    }
    else
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        bool header = true;
        while (reader.ReadToFollowing("row", ns.NamespaceName))
        {
            using var subtree = reader.ReadSubtree(); var row = XElement.Load(subtree);
            if (header) { header = false; continue; }
            exactNames &= DecodeCell(row.Elements(ns + "c").First().Element(ns + "is")!.Element(ns + "t")!.Value) == "Module " + count;
            count++;
        }
    }
    Check(count == 30000 && exactNames && receipt.GetProperty("rowsExported").GetInt32() == count, "All 30000 rows retain exact order and identity in " + format);
}
File.WriteAllText(Path.Combine(output, "excel-fixture.json"), JsonSerializer.Serialize(new { nativeQualification = false, cases = new[] {
    new { snapshotId = literals.Metadata.SnapshotId, names = literalNames, warnings = literals.Metadata.Coverage.Warnings },
    new { snapshotId = controls.Metadata.SnapshotId, names = controlNames, warnings = controls.Metadata.Coverage.Warnings },
    new { snapshotId = snapshot.Metadata.SnapshotId, names = Enumerable.Range(0, 30000).Select(i => "Module " + i).ToArray(), warnings = snapshot.Metadata.Coverage.Warnings }
} }, InventoryStore.Json));
// Progress heartbeats count up, stop before the call's result and never fail the call.
var beats = new List<int>();
using (var stopBeats = new CancellationTokenSource())
{
    var heartbeat = ToolSurface.Heartbeat(TimeSpan.FromMilliseconds(20), "probe", stopBeats.Token, (beat, message) =>
    { lock (beats) beats.Add(beat); Check(message.StartsWith("probe still running", StringComparison.Ordinal), "Heartbeat names the running tool"); return Task.CompletedTask; });
    await Task.Delay(200);
    stopBeats.Cancel(); await heartbeat;
}
int beatsAtStop; lock (beats) beatsAtStop = beats.Count;
await Task.Delay(100);
Check(beatsAtStop >= 3 && beats.SequenceEqual(Enumerable.Range(1, beatsAtStop)) && beats.Count == beatsAtStop, "Heartbeats increase strictly and stop with the call");
using (var stopFailing = new CancellationTokenSource())
{
    var failing = ToolSurface.Heartbeat(TimeSpan.FromMilliseconds(10), "probe", stopFailing.Token, (_, _) => throw new IOException("transport closed"));
    await Task.Delay(50);
    stopFailing.Cancel(); await failing;
    Check(failing.IsCompletedSuccessfully, "A failed heartbeat never faults the call it accompanies");
}
// Output schemas: every advertised schema accepts the Host refusal envelope, stays self-contained and draft-07 safe.
var outputSchemas = ToolSurface.OutputSchemas();
Check(ToolSurface.ReviewContracts.All(r => outputSchemas.ContainsKey(r.Review) && outputSchemas.ContainsKey(r.Page)), "Every review family advertises receipt and page schemas");
foreach (var (tool, schema) in outputSchemas)
{
    var text = schema.GetRawText();
    Check(schema.GetProperty("type").GetString() == "object" && schema.GetProperty("anyOf").EnumerateArray().Any(a => a.TryGetProperty("$ref", out var r) && r.GetString() == "#/$defs/error")
        && schema.GetProperty("$defs").TryGetProperty("error", out _), tool + " schema admits the refusal envelope");
    Check(!text.Contains("\"prefixItems\"") && !Regex.IsMatch(text, "\"\\$ref\":\"(?!#/\\$defs/)"), tool + " schema has only local references and no prefixItems");
}
var wrapped = ToolSurface.WithError(JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"self\":{\"$ref\":\"#\"}}}").RootElement);
Check(wrapped.GetProperty("$defs").GetProperty("result").GetProperty("properties").GetProperty("self").GetProperty("$ref").GetString() == "#/$defs/result"
    && wrapped.GetProperty("anyOf").GetArrayLength() == 2, "Declared result schemas keep their references under the error alternative");

// Resources read through the artifact-root and link checks of artifact_read; completion lists names only, never through links.
var resourceRoot = Path.Combine(output, "resources");
var outside = Path.Combine(output, "outside");
Directory.CreateDirectory(Path.Combine(resourceRoot, "exports")); Directory.CreateDirectory(Path.Combine(resourceRoot, "project-reviews")); Directory.CreateDirectory(outside);
File.WriteAllText(Path.Combine(resourceRoot, "exports", "plan.json"), "{\"ok\":true}");
File.WriteAllBytes(Path.Combine(resourceRoot, "exports", "raw.bin"), [0, 159, 255]);
File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
File.WriteAllBytes(Path.Combine(resourceRoot, "exports", "large.json"), new byte[TiaResources.MaxBytes + 1]);
var reviewBytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"project-review.v1\"}");
var reviewId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(reviewBytes));
File.WriteAllBytes(Path.Combine(resourceRoot, "project-reviews", reviewId + ".json"), reviewBytes);
File.WriteAllBytes(Path.Combine(resourceRoot, "project-reviews", new string('a', 64) + ".json"), reviewBytes);
var link = Path.Combine(resourceRoot, "linked");
var mklink = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) mklink.ArgumentList.Add(argument);
using (var child = System.Diagnostics.Process.Start(mklink)!) { child.WaitForExit(); Check(child.ExitCode == 0, "Test junction created"); }
var resources = new TiaResources(options with { ArtifactsDirectory = resourceRoot });
void Refused(Action action, string label) { try { action(); } catch (ModelContextProtocol.McpException) { checks++; return; } throw new Exception(label); }
Check(resources.Artifact("exports/plan.json") is ModelContextProtocol.Protocol.TextResourceContents { Text: "{\"ok\":true}", MimeType: "application/json", Uri: "tia://artifacts/exports/plan.json" },
    "JSON artifact reads as text with its canonical URI");
Check(resources.Artifact("exports\\raw.bin") is ModelContextProtocol.Protocol.BlobResourceContents blob && blob.DecodedData.ToArray().SequenceEqual(new byte[] { 0, 159, 255 }),
    "Other artifacts read as exact bytes");
Refused(() => resources.Artifact("../outside/secret.txt"), "Path traversal leaves the artifact root");
Refused(() => resources.Artifact(Path.Combine(outside, "secret.txt")), "Absolute path outside the root");
Refused(() => resources.Artifact("linked/secret.txt"), "Junction inside the root is followed");
Refused(() => resources.Artifact("exports/large.json"), "Artifact above the resource limit is read whole");
Check(resources.Review(reviewId).Contains("project-review.v1"), "Saved review reads by its content hash");
Refused(() => resources.Review(new string('a', 64)), "Review whose bytes do not match its ID");
Refused(() => resources.Review("../exports/plan"), "Review ID that is not a content hash");
var names = TiaResources.ArtifactPaths(resourceRoot).ToList();
Check(names.Contains("exports/plan.json") && !names.Any(name => name.StartsWith("linked", StringComparison.Ordinal)), "Completion lists root files and never walks a junction");
Directory.Delete(link);
Console.WriteLine("Export artifacts: " + output);
Console.WriteLine($"PASS Inventory: {checks} checks, 30000 rows / {pages} pages; protocol fixtures, exports and host guards. No physical hardware qualification.");

sealed class RejectBridge : IBridgeClient
{
    public int Calls;
    public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    { Calls++; throw new Exception("Native worker must not be called"); }
}
sealed class FakeHandler : HttpMessageHandler
{
    public List<string> Methods = []; public string Name = "Module 1"; public string? LastType; public int LastNumber;
    public int? ApiCode; public bool Unsupported, BadId, Malformed, Huge;
    public Dictionary<uint, string>? Names; public uint? UnavailableHardwareId;
    public JsonElement? ResultOverride; public string? OverrideMethod, Envelope;
    public HttpStatusCode Status = HttpStatusCode.OK; public bool TransportFailure;
    public uint? MalformedHardwareId;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Scheme != "https" || request.Method != HttpMethod.Post || !request.Headers.Contains("X-Auth-Token")) throw new Exception("Request contract");
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); var body = document.RootElement;
        string method = body.GetProperty("method").GetString()!; Methods.Add(method);
        if (TransportFailure) throw new IOException("SECRET transport diagnostics");
        object result;
        if (method == "Api.Browse") result = Unsupported ? new[] { new { name = "Modules.Browse" } } : new[] { new { name = "Modules.Browse" }, new { name = "Modules.ReadIdentificationMaintenance" } };
        else if (method == "Modules.Browse")
        {
            uint hwid = body.GetProperty("params").GetProperty("hwid").GetUInt32();
            result = new { nodes = new[] { new { hwid, name = Names?.GetValueOrDefault(hwid) ?? Name, subtype = "cpu" } } };
        }
        else if (method == "Modules.ReadIdentificationMaintenance")
        {
            LastType = body.GetProperty("params").GetProperty("type").GetString(); LastNumber = body.GetProperty("params").GetProperty("number").GetInt32();
            result = new { data = new { serial_number = "SERIAL-0001", order_number = "6ES7 151-1AA00", hardware_revision = 0, software_revision = new { type = "V", major = 4, minor = 1, patch = 0 } } };
        }
        else throw new Exception("Unexpected method " + method);
        if (ResultOverride.HasValue && (OverrideMethod == null || OverrideMethod == method)) result = ResultOverride.Value;
        if (method == "Modules.ReadIdentificationMaintenance" && body.GetProperty("params").GetProperty("hwid").GetUInt32() == MalformedHardwareId)
            result = JsonSerializer.Deserialize<JsonElement>("[]");
        string id = BadId ? "wrong" : body.GetProperty("id").GetString()!;
        int? failureCode = method == "Modules.ReadIdentificationMaintenance" && body.GetProperty("params").GetProperty("hwid").GetUInt32() == UnavailableHardwareId ? 1103 : ApiCode;
        string response = failureCode.HasValue ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = failureCode.Value, message = "SECRET MUST NEVER BE ECHOED" } }) : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
        response = Envelope switch
        {
            "missingVersion" => JsonSerializer.Serialize(new { id, result }),
            "wrongVersion" => JsonSerializer.Serialize(new { jsonrpc = "1.0", id, result }),
            "both" => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result, error = new { code = 2 } }),
            "neither" => JsonSerializer.Serialize(new { jsonrpc = "2.0", id }),
            "duplicateId" => response.Insert(1, "\"id\":\"ambiguous\","),
            "invalidError" => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = "2" } }),
            _ => response
        };
        if (Malformed) response = "not JSON";
        if (Huge) response = new string('x', 300000);
        return new HttpResponseMessage(Status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
sealed class FixtureFactory : IInventoryIdentityFactory
{
    public FakeHandler Handler = new() { Names = new() { [101] = "Module 0", [12] = "Module 1", [13] = "Module 2" }, UnavailableHardwareId = 13 };
    public SiemensWebIdentity Create(string endpoint, string? token) => new(endpoint, "test-token", Handler);
}
