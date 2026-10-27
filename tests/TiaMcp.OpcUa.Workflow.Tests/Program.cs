using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.OpcUa;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Reject(string code, Action run)
{
    try { run(); } catch (BridgeException error) { Equal(code, error.Code); return; }
    throw new Exception("Expected rejection: " + code);
}
string Text(JsonNode? value) => value!.GetValue<string>();
byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
string Ref(JsonObject record) => Text(record["recordRef"]);
string Artifact(JsonObject record, string name) => Ref(record) + ":" + name;
string RecordPath(string root, JsonObject record, string name) => Path.Combine(root, "opcua-workflow", Ref(record).Split(':')[1], name);
void InStore(Action<string, OpcUaWorkflowStore, ArtifactUploadStore> action)
{
    string root = Path.Combine(Path.GetTempPath(), "tia-opcua-workflow-" + Guid.NewGuid().ToString("N"));
    try { action(root, new(root), new(root, new PackageIntakeLimits { MaxChunkBytes = 1048576 })); }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
string Upload(ArtifactUploadStore store, byte[] bytes)
{
    var upload = store.Begin("input.bin", bytes.Length, Hash(bytes));
    for (int offset = 0; offset < bytes.Length; offset += upload.MaxChunkBytes)
        store.Write(upload.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(upload.MaxChunkBytes, bytes.Length - offset)));
    return store.Commit(upload.UploadId).ArtifactRef!;
}
JsonObject Capture(OpcUaWorkflowStore store, ArtifactUploadStore uploads, string file = "machine-v1.xml") => store.Inspect(Upload(uploads, Fixture(file)));
JsonObject Read(OpcUaWorkflowStore store, string reference, long offset = 0, int count = 65536) => JsonSerializer.SerializeToNode(store.ReadArtifact(reference, offset, count), BridgeProtocol.Json)!.AsObject();
byte[] Download(OpcUaWorkflowStore store, string reference)
{
    using var output = new MemoryStream();
    while (true)
    {
        var page = Read(store, reference, output.Length);
        output.Write(Convert.FromBase64String(Text(page["data"])));
        if (page["complete"]!.GetValue<bool>()) { Equal(Hash(output.ToArray()), Text(page["sha256"])); return output.ToArray(); }
    }
}

Test("exact source bytes and hash retained after upload release and store reopen", () => InStore((root, store, uploads) => {
    var bytes = Fixture("machine-v1.xml");
    string upload = Upload(uploads, bytes);
    var record = store.Inspect(upload);
    uploads.Release(upload[7..]);
    var newer = new OpcUaWorkflowStore(root);
    Equal(Ref(record), Ref(newer.Reopen(Ref(record))));
    Equal(true, bytes.AsSpan().SequenceEqual(Download(newer, Artifact(record, "original.xml"))));
    Equal(Hash(bytes), Text(record["summary"]!["sourceSha256"]));
    Equal(false, record["nativeQualified"]!.GetValue<bool>());
}));
Test("native export bytes register as ordinary comparable model records", () => InStore((root, store, uploads) => {
    var bytes = Fixture("machine-v1.xml");
    var exported = store.InspectExported(bytes);
    Equal(Hash(bytes), Text(exported["summary"]!["sourceSha256"]));
    Equal("model", Text(new OpcUaWorkflowStore(root).Reopen(Ref(exported))["kind"]));
    Equal(3, store.Compare(Ref(exported), Ref(Capture(store, uploads, "machine-v2.xml")))["summary"]!["breakingChangeCount"]!.GetValue<int>());
    Reject("OpcUaCapacity", () => store.InspectExported([]));
    Reject("OpcUaInvalidXml", () => store.InspectExported(Encoding.UTF8.GetBytes("<not-closed")));
}));
Test("inspection pages and bound cursors preserve exact ordering", () => InStore((_, store, uploads) => {
    var record = Capture(store, uploads);
    var page = store.ReportPage(Ref(record), "nodes", 1);
    var cursor = page["nextCursor"]!.Deserialize<OpcUaReportCursor>(BridgeProtocol.Json)!;
    var next = store.ReportPage(Ref(record), "nodes", 1, cursor);
    Equal(1, next["offset"]!.GetValue<int>());
    var full = JsonNode.Parse(Download(store, Artifact(record, "report.json")))!;
    Equal(full["nodes"]![1]!.ToJsonString(), next["rows"]![0]!.ToJsonString());
}));
Test("single-model comparison preserves consumer manifest and independent impacts", () => InStore((_, store, uploads) => {
    var before = Capture(store, uploads); var after = Capture(store, uploads, "machine-v2.xml");
    byte[] consumers = Fixture("consumers-v1.json");
    var comparison = store.Compare(Ref(before), Ref(after), Upload(uploads, consumers));
    Equal(3, comparison["summary"]!["breakingChangeCount"]!.GetValue<int>());
    Equal(3, store.ReportPage(Ref(comparison), "clientImpacts")["rows"]!.AsArray().Count);
    Equal(true, consumers.AsSpan().SequenceEqual(Download(store, Artifact(comparison, "consumers.json"))));
    Equal(true, comparison["summary"]!["unknownConsumers"]!.GetValue<bool>());
    Equal(false, comparison["summary"]!["compatibilityCertified"]!.GetValue<bool>());
}));
Test("explicit cross-file dependency set and v2 transitive impact", () => InStore((_, store, uploads) => {
    var device = Capture(store, uploads, "dependencies/device.xml");
    var types1 = Capture(store, uploads, "dependencies/types-v1.xml");
    var types2 = Capture(store, uploads, "dependencies/types-v2.xml");
    var before = store.Dependencies([Ref(device), Ref(types1)]);
    var after = store.Dependencies([Ref(device), Ref(types2)]);
    Equal(true, before["summary"]!["selectedDependenciesResolved"]!.GetValue<bool>());
    var comparison = store.CompareDependencies(Ref(before), Ref(after), Upload(uploads, Fixture("dependencies/consumers-v2.json")));
    var impacts = store.ReportPage(Ref(comparison), "clientImpacts")["rows"]!.AsArray();
    Equal(true, impacts.Any(i => i!["indirectChanges"]!.AsArray().Count > 0));
    Equal(false, comparison["summary"]!["dependencyClosureComplete"]!.GetValue<bool>());
    Equal(2, store.ReportPage(Ref(comparison), "before.dependencies")["total"]!.GetValue<int>());
}));
Test("missing dependencies remain explicit and completeness false", () => InStore((_, store, uploads) => {
    var device = Capture(store, uploads, "dependencies/device.xml");
    var set = store.Dependencies([Ref(device)]);
    Equal(false, set["summary"]!["selectedDependenciesResolved"]!.GetValue<bool>());
    Equal(false, set["summary"]!["dependencyClosureComplete"]!.GetValue<bool>());
    Equal(true, store.ReportPage(Ref(set), "diagnostics")["rows"]!.AsArray().Any(r => Text(r!["code"]) == "DependencyNodeNotProvided"));
    Equal(true, store.ReportPage(Ref(set), "limitations")["rows"]!.AsArray().Any(r => Text(r).Contains("remain opaque")));
}));
Test("distinct captures of duplicate bytes remain ambiguous", () => InStore((_, store, uploads) => {
    var first = Capture(store, uploads); var second = Capture(store, uploads);
    var set = store.Dependencies([Ref(first), Ref(second)]);
    Equal(true, store.ReportPage(Ref(set), "diagnostics")["rows"]!.AsArray().Any(r => Text(r!["code"]) == "AmbiguousNodeDefinition"));
}));
Test("same handles and excessive set size rejected", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads);
    Reject("OpcUaInvalidSources", () => store.Dependencies([Ref(model), Ref(model)]));
    Reject("OpcUaInvalidSources", () => store.Dependencies([]));
    Reject("OpcUaInvalidSources", () => store.Dependencies(Enumerable.Repeat(Ref(model), 33).ToArray()));
}));
Test("malformed XML and external DTD refused without publication", () => InStore((_, store, uploads) => {
    Reject("OpcUaInvalidXml", () => store.Inspect(Upload(uploads, Encoding.UTF8.GetBytes("<not-closed"))));
    Reject("OpcUaInvalidXml", () => store.Inspect(Upload(uploads, Encoding.UTF8.GetBytes("<!DOCTYPE x SYSTEM 'http://127.0.0.1/forbidden'><x/>"))));
    Equal(0, store.List()["total"]!.GetValue<int>());
}));
Test("oversize committed NodeSet rejected before parsing", () => InStore((_, store, uploads) => {
    Reject("OpcUaCapacity", () => store.Inspect(Upload(uploads, new byte[OpcUaModelReview.MaxBytes + 1])));
}));
Test("uncommitted upload and upload overwrite refused", () => InStore((_, store, uploads) => {
    var bytes = Fixture("machine-v1.xml"); var pending = uploads.Begin("x.xml", bytes.Length, Hash(bytes));
    Reject("UploadIncomplete", () => store.Inspect("upload:" + pending.UploadId));
    var committed = Upload(uploads, bytes);
    Reject("UploadNotWritable", () => uploads.Write(committed[7..], 0, Convert.ToBase64String(bytes)));
}));
Test("stale v1 consumer binding refused without publication", () => InStore((_, store, uploads) => {
    var before = Capture(store, uploads); var after = Capture(store, uploads, "machine-v2.xml");
    var consumers = JsonNode.Parse(Fixture("consumers-v1.json"))!; consumers["modelSha256"] = new string('0', 64);
    Reject("OpcUaStaleConsumers", () => store.Compare(Ref(before), Ref(after), Upload(uploads, Encoding.UTF8.GetBytes(consumers.ToJsonString()))));
    Equal(2, store.List()["total"]!.GetValue<int>());
}));
Test("stale v2 consumer binding refused", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads); var set = store.Dependencies([Ref(model)]);
    Reject("OpcUaStaleConsumers", () => store.CompareDependencies(Ref(set), Ref(set), Upload(uploads, Fixture("dependencies/consumers-v2.json"))));
}));
Test("oversize consumer JSON rejected", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads);
    Reject("OpcUaCapacity", () => store.Compare(Ref(model), Ref(model), Upload(uploads, new byte[1024 * 1024 + 1])));
}));
Test("artifact and record path traversal refused", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads);
    foreach (string input in new[] { "../machine.xml", @"C:\Windows\win.ini", "file:///tmp/input.xml", Ref(model) + ":../../receipt.json", Ref(model) + "\n" })
    { Reject("OpcUaInvalidReference", () => store.Reopen(input)); Reject("OpcUaInvalidReference", () => store.ReadArtifact(input)); }
    Reject("InvalidRequest", () => store.Inspect(@"C:\Windows\win.ini"));
}));
Test("wrong record kind refused", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads); var set = store.Dependencies([Ref(model)]);
    Reject("OpcUaInvalidReference", () => store.Compare(Ref(model), Ref(set)));
    Reject("OpcUaInvalidReference", () => store.CompareDependencies(Ref(model), Ref(set)));
}));
Test("stale cursor record report and section bindings refused", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads); var other = Capture(store, uploads);
    var cursor = store.ReportPage(Ref(model), "nodes", 1)["nextCursor"]!.Deserialize<OpcUaReportCursor>(BridgeProtocol.Json)!;
    Reject("OpcUaStaleCursor", () => store.ReportPage(Ref(other), "nodes", 1, cursor));
    Reject("OpcUaStaleCursor", () => store.ReportPage(Ref(model), "nodes", 1, cursor with { ReportSha256 = new string('0', 64) }));
    Reject("OpcUaStaleCursor", () => store.ReportPage(Ref(model), "diagnostics", 1, cursor));
    Reject("OpcUaInvalidCursor", () => store.ReportPage(Ref(model), "nodes", 1, cursor with { Offset = -1 }));
}));
Test("page read and list bounds fail closed", () => InStore((_, store, uploads) => {
    var model = Capture(store, uploads);
    Reject("OpcUaPageLimit", () => store.ReportPage(Ref(model), "nodes", 501));
    Reject("OpcUaInvalidSection", () => store.ReportPage(Ref(model), "../../original.xml"));
    Reject("OpcUaPageLimit", () => store.ReadArtifact(Artifact(model, "report.json"), 0, 65537));
    Reject("OpcUaInvalidCursor", () => store.ReadArtifact(Artifact(model, "report.json"), long.MaxValue));
    Reject("OpcUaPageLimit", () => store.List(0, 101));
}));
Test("repeated inspection creates new immutable records and preserves previous bytes", () => InStore((root, store, uploads) => {
    var first = Capture(store, uploads); byte[] receipt = File.ReadAllBytes(RecordPath(root, first, "receipt.json"));
    byte[] report = Download(store, Artifact(first, "report.json"));
    var second = Capture(store, uploads, "machine-v2.xml");
    Equal(false, Ref(first) == Ref(second));
    Equal(true, receipt.AsSpan().SequenceEqual(File.ReadAllBytes(RecordPath(root, first, "receipt.json"))));
    Equal(true, report.AsSpan().SequenceEqual(Download(store, Artifact(first, "report.json"))));
}));
Test("retained original tampering invalidates comparison and download", () => InStore((root, store, uploads) => {
    var model = Capture(store, uploads); var comparison = store.Compare(Ref(model), Ref(model));
    File.WriteAllText(RecordPath(root, model, "original.xml"), "changed");
    Reject("OpcUaIntegrityFailure", () => store.Reopen(Ref(comparison)));
    Reject("OpcUaIntegrityFailure", () => store.ReadArtifact(Artifact(comparison, "report.json")));
    Equal("integrity-failed", Text(store.List()["rows"]![0]!["state"]));
}));
Test("receipt overwrite rejected by digest in handle", () => InStore((root, store, uploads) => {
    var model = Capture(store, uploads);
    File.AppendAllText(RecordPath(root, model, "receipt.json"), " ");
    Reject("OpcUaIntegrityFailure", () => store.Reopen(Ref(model)));
}));
Test("stale intent manifest refused even with unchanged receipt", () => InStore((root, store, uploads) => {
    var model = Capture(store, uploads);
    File.AppendAllText(RecordPath(root, model, "intent.json"), " ");
    Reject("OpcUaIntegrityFailure", () => store.Reopen(Ref(model)));
}));
Test("missing receipt remains incomplete and discoverable without retry overwrite", () => InStore((root, store, uploads) => {
    var model = Capture(store, uploads); string receipt = RecordPath(root, model, "receipt.json");
    File.Move(receipt, receipt + ".pending");
    Reject("OpcUaPublicationIncomplete", () => store.Reopen(Ref(model)));
    Equal("incomplete", Text(store.List()["rows"]![0]!["state"]));
    _ = Capture(store, uploads);
    Equal(true, File.Exists(receipt + ".pending")); Equal(false, File.Exists(receipt));
}));
Test("oversize report rows are explicit omissions with downloadable exact content", () => InStore((_, store, uploads) => {
    string refs = string.Concat(Enumerable.Repeat("<Reference ReferenceType=\"i=35\">ns=1;s=B</Reference>", 4000));
    string xml = "<UANodeSet xmlns=\"http://opcfoundation.org/UA/2011/03/UANodeSet.xsd\"><NamespaceUris><Uri>urn:test</Uri></NamespaceUris><UAObject NodeId=\"ns=1;s=A\" BrowseName=\"1:A\"><References>" + refs + "</References></UAObject></UANodeSet>";
    var record = store.Inspect(Upload(uploads, Encoding.UTF8.GetBytes(xml)));
    var page = store.ReportPage(Ref(record), "nodes");
    Equal(true, page["rows"]![0]!["omitted"]!.GetValue<bool>());
    Equal(true, Encoding.UTF8.GetByteCount(page.ToJsonString()) < OpcUaWorkflowStore.PageBytes);
    var report = JsonNode.Parse(Download(store, Artifact(record, "report.json")))!;
    Equal(4000, report["nodes"]![0]!["references"]!.AsArray().Count);
}));
Test("discovery offsets and empty last byte read are stable across restart", () => InStore((root, store, uploads) => {
    var record = Capture(store, uploads); _ = Capture(store, uploads, "machine-v2.xml");
    var page = new OpcUaWorkflowStore(root).List(0, 1);
    Equal(1, page["nextOffset"]!.GetValue<int>());
    Equal(1, store.List(1, 1)["rows"]!.AsArray().Count);
    byte[] bytes = Fixture("machine-v1.xml");
    var eof = Read(store, Artifact(record, "original.xml"), bytes.Length);
    Equal(true, eof["complete"]!.GetValue<bool>()); Equal("", Text(eof["data"]));
}));

int failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { ++failures; Console.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} OPC UA workflow checks passed; no native workers.");
return failures == 0 ? 0 : 1;
