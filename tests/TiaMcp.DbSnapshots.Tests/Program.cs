using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using TiaMcp.Core;
using TiaMcp.Core.DbSnapshots;
using TiaMcp.Openness.V21;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidDbSnapshot")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
void XmlReject(string xml, string label)
{ try { DbSnapshotReview.Parse(Encoding.UTF8.GetBytes(xml)); } catch (Exception e) when (e is XmlException or BridgeException) { Check(true, label); return; } throw new Exception(label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
const string plc = "/projects/Fixture/devices/CPU/plc", path = plc + "/blocks/Data";
var node = new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["name"] = "Data", ["kind"] = "block", ["plcPath"] = plc,
    ["parentId"] = "/projects/Fixture", ["metadata"] = new JsonObject { ["kind"] = "GlobalDB" } };
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(node, new JsonObject { ["nodeId"] = "/projects/Fixture", ["objectPath"] = "/projects/Fixture", ["kind"] = "project", ["name"] = "Fixture", ["parentId"] = null, ["metadata"] = new JsonObject() }), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var review = reviews.Read(id);
const string xml = """
<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V21"/><SW.Blocks.InterfaceSnapshot ID="0"><AttributeList><Name>Data</Name><Snapshot ReadOnly="true"><SnapshotValues><Value Path="Flag" Type="Bool">TRUE</Value><Value Path="Nested.Items[0]" Type="VendorType"> 日本語 😀 </Value><Value Path="Large" Type="LInt">9007199254740993</Value></SnapshotValues></Snapshot><SnapshotDate ReadOnly="true">2017-12-06T08:04:11.4590585Z</SnapshotDate><StructureModified ReadOnly="true">2017-12-06T08:22:13.3292585Z</StructureModified><Opaque>untouched</Opaque></AttributeList></SW.Blocks.InterfaceSnapshot></Document>
""";
byte[] before = Encoding.UTF8.GetBytes(xml), after = Encoding.UTF8.GetBytes(xml.Replace("TRUE", "true").Replace("9007199254740993", "9007199254740992"));
JsonObject Build(byte[]? a = null, byte[]? b = null, JsonObject? source = null, string selected = path) => DbSnapshotReview.Build(id, source ?? review, selected, a ?? before, b ?? after);
var report = Build();
Check((int)report["summary"]!["changedPathCount"]! == 2, "Text spelling and beyond-double integers differ without coercion");
Check((string)report["before"]!["values"]![1]!["text"]! == " 日本語 😀 ", "Whitespace and Unicode values preserved");
Check((string)report["before"]!["values"]![1]!["type"]! == "VendorType", "Unknown vendor type retained without validation");
Check((string)report["summary"]!["structureTimestampComparison"]! == "sameReportedText" && !(bool)report["structureCompatibilityVerified"]!, "Equal dates cannot certify structure");
foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "programIdentityVerified", "structureCompatibilityVerified" })
    Check(!(bool)report[flag]!, "Supplied evidence cannot promote " + flag);
Check(!(bool)DbSnapshotReview.Build(id, review, path, before, null)["summary"]!["comparisonRequested"]!, "Single document inspection has no invented comparison");
byte[] Utf16(string text) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text.Replace("utf-8", "utf-16"))).ToArray();
Check(JsonNode.DeepEquals(DbSnapshotReview.Parse(Utf16(xml)), DbSnapshotReview.Parse(before)), "UTF-16 BOM and declared encoding supported");
Check((bool)Build(b: before)["summary"]!["originalBytesEqual"]!, "Exact byte equality reported");
string Export(string created, string flag)
{
    string file = Path.Combine(root, "export-" + Guid.NewGuid().ToString("N") + ".xml");
    File.WriteAllText(file, xml.Replace("<Engineering version=\"V21\"/>", "<Engineering version=\"V21\"/><DocumentInfo><Created>" + created + "</Created></DocumentInfo>").Replace("TRUE", flag));
    return file;
}
string firstExport = Export("2026-10-31T10:00:00.0000000Z", "TRUE");
Check(DbSnapshotXml.SameSnapshot(firstExport, Export("2026-10-31T10:00:05.0000000Z", "TRUE")), "Exports differing only in DocumentInfo/Created hold the same stored snapshot");
Check(!DbSnapshotXml.SameSnapshot(firstExport, Export("2026-10-31T10:00:05.0000000Z", "FALSE")), "A changed stored snapshot value is detected across exports");
Check(!(bool)Build(b: Utf16(xml))["summary"]!["originalBytesEqual"]! && (int)Build(b: Utf16(xml))["summary"]!["changedPathCount"]! == 0, "Encoding differences remain separate from values");
var opaque = Build(b: Encoding.UTF8.GetBytes(xml.Replace("untouched", "different")));
Check(!(bool)opaque["summary"]!["originalBytesEqual"]! && (int)opaque["summary"]!["changedPathCount"]! == 0, "Opaque changes never become whole-document equality");
Check((string)Build(b: Encoding.UTF8.GetBytes(xml.Replace("08:22:13", "09:22:13")))["summary"]!["structureTimestampComparison"]! == "differentReportedText", "Changed structure timestamp exposed");
Check((string)Build(b: Encoding.UTF8.GetBytes(xml.Replace("<StructureModified ReadOnly=\"true\">2017-12-06T08:22:13.3292585Z</StructureModified>", "")))["summary"]!["structureTimestampComparison"]! == "unknown", "Missing structure timestamp stays unknown");
Check((string)DbSnapshotReview.Parse(Encoding.UTF8.GetBytes(xml.Replace("2017-12-06T08:04:11.4590585Z", "unknown-clock")))["snapshotDate"]! == "unknown-clock", "Unvalidated date text cannot produce freshness");
Check((int)DbSnapshotReview.Parse(Encoding.UTF8.GetBytes(xml.Replace("<SnapshotValues>", "<SnapshotValues xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/Snapshot/v1\">")))["valueCount"]! == 3, "Documented namespace shape read without XSD claim");
foreach (string kind in new[] { "GlobalDB", "InstanceDB", "ArrayDB" }) { var source = review.DeepClone().AsObject(); source["model"]!["nodes"]![0]!["metadata"]!["kind"] = kind; Check((string)Build(source: source)["objectPath"]! == path, "Exact captured " + kind + " selectable"); }
foreach (string defect in new[] { "virtual", "FC", "no-plc", "wrong-name" })
{
    var source = review.DeepClone().AsObject(); var n = source["model"]!["nodes"]![0]!;
    if (defect == "virtual") n["origin"] = "virtual"; else if (defect == "FC") n["metadata"]!["kind"] = "FC"; else if (defect == "no-plc") n["plcPath"] = null; else n["name"] = "Other";
    Reject(() => Build(source: source), "Refuses selected source " + defect);
}
Reject(() => Build(selected: path.ToLowerInvariant()), "No case-folded selection");
Reject(() => Build(selected: path + "\ud800"), "Invalid Unicode selector refused before serialization");
Reject(() => Build(selected: path + "\n"), "Control-bearing selector refused");
Reject(() => Build(selected: path.Replace("CPU", "OtherCPU")), "Same name on another CPU cannot rebind");
Reject(() => Build(b: Encoding.UTF8.GetBytes(xml.Replace("<Name>Data", "<Name>Other"))), "Different exported DB name refused");
var moved = Build(b: Encoding.UTF8.GetBytes(xml.Replace("Path=\"Flag\"", "Path=\"Renamed\"")));
Check((string)moved["changes"]![0]!["status"]! == "onlyBefore" && (string)moved["changes"]![1]!["status"]! == "onlyAfter", "Paths never match by type or value");
Check((string)Build(b: Encoding.UTF8.GetBytes(xml.Replace("Type=\"Bool\"", "Type=\"OpaqueBool\"")))["changes"]![0]!["status"]! == "differentTextOrType", "Type-only changes remain visible");
Check((int)DbSnapshotReview.Parse(Encoding.UTF8.GetBytes(xml.Replace("TRUE", "")))["valueCount"]! == 3, "Empty scalar text preserved without invented default");
foreach (var (bad, label) in new[] {
    (xml.Replace("<Document>", "<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///missing-secret'>]><Document>"), "DTD and external entities refused"),
    (xml.Replace("Path=\"Large\"", "Path=\"Flag\""), "Duplicate paths refused"),
    (xml.Replace(" Type=\"Bool\"", ""), "Missing type refused"),
    (xml.Replace("<Name>Data</Name>", "<Name>Data</Name><Name>Other</Name>"), "Ambiguous name refused"),
    (xml.Replace("<SnapshotValues>", "<SnapshotValues xmlns=\"urn:unknown\">"), "Unknown value namespace refused"),
    (xml.Replace("TRUE</Value>", "<Unknown/>TRUE</Value>"), "Nested value markup refused"),
    (xml.Replace("<SnapshotValues>", "<SnapshotValues><Unknown/>"), "Unknown value row refused"),
    (xml.Replace("<Document>", "<Document xmlns=\"urn:unknown\">"), "Unknown export namespace refused"),
    (xml.Replace("<Name>Data</Name>", "<Name>Data</Name><q:Name xmlns:q=\"urn:shadow\">Other</q:Name>"), "Namespace-shadowed duplicate refused"),
    (xml.Replace("<Opaque>untouched</Opaque>", string.Concat(Enumerable.Repeat("<x>", 34)) + string.Concat(Enumerable.Repeat("</x>", 34))), "Opaque depth bounded"),
    (xml[..^5], "Malformed XML refused") }) XmlReject(bad, label);
Reject(() => DbSnapshotReview.Parse(new byte[DbSnapshotReview.InputLimit + 1]), "Byte bound enforced");
string many = xml.Replace("<Value Path=\"Flag\" Type=\"Bool\">TRUE</Value><Value Path=\"Nested.Items[0]\" Type=\"VendorType\"> 日本語 😀 </Value><Value Path=\"Large\" Type=\"LInt\">9007199254740993</Value>", string.Concat(Enumerable.Range(0, 20000).Select(i => $"<Value Path=\"V{i}\" Type=\"Int\">{i}</Value>")));
Check((int)DbSnapshotReview.Parse(Encoding.UTF8.GetBytes(many))["valueCount"]! == 20000, "Maximum value count supported");
XmlReject(many.Replace("</SnapshotValues>", "<Value Path=\"extra\" Type=\"Int\">1</Value></SnapshotValues>"), "Value count bound enforced");
var uploads = new ArtifactUploadStore(root);
(string Id, string Ref) Upload(byte[] data) { var u = uploads.Begin("snapshot.xml", data.Length, Hash(data)); for (int i = 0; i < data.Length; i += u.MaxChunkBytes) uploads.Write(u.UploadId, i, Convert.ToBase64String(data, i, Math.Min(u.MaxChunkBytes, data.Length - i))); return (u.UploadId, uploads.Commit(u.UploadId).ArtifactRef!); }
var left = Upload(before); var right = Upload(Utf16(Encoding.UTF8.GetString(after))); var store = new DbSnapshotWorkflowStore(root);
var receipt = store.Review(id, path, left.Ref, right.Ref); string record = (string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt, store.Review(id, path, left.Ref, right.Ref)), "Idempotent receipt");
Check(ProjectReviewStore.ReadArtifact(root, receipt["comparisonInput"]!.AsObject()).SequenceEqual(Utf16(Encoding.UTF8.GetString(after))), "Exact original UTF-16 bytes retained");
foreach (string section in new[] { "beforeValues", "afterValues", "changes" }) Check(store.Page(record, section, 0, 1)["nextOffset"] is not null, "Bounded paging " + section);
Reject(() => store.Page(record, "changes", 99), "Bad offset refused", "InvalidRequest");
Reject(() => store.Page(record, "changes", 0, 201), "Page count bounded", "InvalidRequest");
var single = store.Review(id, path, left.Ref); Check((int)store.Page((string)single["recordId"]!, "afterValues")["total"]! == 0, "Single inspection has no comparison rows");
var native = new JsonObject { ["kind"] = "nativeInterfaceSnapshotExport", ["projectPath"] = "SYNTHETIC.ap21", ["plcPath"] = plc, ["objectPath"] = path };
var nativeReport = DbSnapshotReview.Build(id, review, path, before, null, native);
Check((string)nativeReport["association"]! == "nativeExportOfSelectedDB" && !(bool)nativeReport["sourceAuthenticityVerified"]!, "Captured source recorded without an authenticity claim");
foreach (var (key, value) in new[] { ("objectPath", path + "2"), ("plcPath", plc + "2"), ("projectPath", "other.ap21"), ("kind", "suppliedUpload") })
{ var wrong = native.DeepClone().AsObject(); wrong[key] = value; Reject(() => DbSnapshotReview.Build(id, review, path, before, null, wrong), "Captured " + key + " mismatch refused"); }
string exported = Path.Combine(root, "exports", "db-snapshot.xml"); Directory.CreateDirectory(Path.GetDirectoryName(exported)!); File.WriteAllBytes(exported, before);
var capture = store.Capture(id, path, new JsonObject { ["identity"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["plcPath"] = plc, ["objectPath"] = path, ["kind"] = "block", ["name"] = "Data" },
    ["artifact"] = new JsonObject { ["path"] = exported, ["sha256"] = Hash(before) } });
Check(capture["inputSource"] is JsonObject && !single.ContainsKey("inputSource") && JsonNode.DeepEquals(capture, store.Page((string)capture["recordId"]!)), "Captured record reopens with its native source; supplied receipts unchanged");
JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
var bound = store.LoadPayload(Payload(new { snapshotRecordId = (string)capture["recordId"]!, snapshotSha256 = new string('0', 64), projectPath = "forged.ap21", snapshotPath = "forged.xml" }));
Check((string)bound["snapshotSha256"]! == Hash(before) && (string)bound["blockPath"]! == path && (string)bound["projectPath"]! == "synthetic.ap21", "Load binds the reviewed DB and snapshot hash over caller values");
Check(Path.IsPathFullyQualified((string)bound["snapshotPath"]!) && File.ReadAllBytes((string)bound["snapshotPath"]!).SequenceEqual(before), "Load binds the reviewed snapshot bytes over a caller path");
Reject(() => store.LoadPayload(Payload(new { snapshotRecordId = (string)capture["recordId"]!, blockPath = path + "2" })), "Load on another DB refused", "DbSnapshotIdentityMismatch");
Reject(() => store.LoadPayload(Payload(new { snapshotRecordId = record })), "Supplied review cannot authorize a load", "DbSnapshotSourceRequired");
uploads.Release(left.Id); uploads.Release(right.Id); Check(JsonNode.DeepEquals(receipt, new DbSnapshotWorkflowStore(root).Page(record)), "Release and restart preserve references");
foreach (string key in new[] { "input", "comparisonInput", "reportArtifact" })
{ string file = Path.Combine(root, (string)receipt[key]!["path"]!); byte[] original = File.ReadAllBytes(file); File.WriteAllText(file, "tampered"); Reject(() => store.Page(record), "Corrupted " + key + " refused", "ArtifactChanged"); File.WriteAllBytes(file, original); }
foreach (string defect in new[] { "report", "input-path", "extra" })
{
    var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!;
    if (defect == "report") envelope["report"]!["programIdentityVerified"] = true;
    else if (defect == "extra") envelope["extra"] = true;
    else envelope["input"]!["path"] = "other/" + (string)envelope["input"]!["sha256"]! + ".xml";
    byte[] data = JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json); string hash = Hash(data);
    File.WriteAllBytes(Path.Combine(root, "db-snapshot-reviews", hash + ".json"), data);
    Reject(() => store.Page(hash), "Rehashed forged " + defect + " refused", defect == "report" ? "ArtifactChanged" : "InvalidDbSnapshot");
}
var large = Upload(Encoding.UTF8.GetBytes(xml.Replace("TRUE", new string('x', 130 * 1024)))); var largeReceipt = store.Review(id, path, large.Ref);
var page = store.Page((string)largeReceipt["recordId"]!, "beforeValues");
Check((bool)page["rows"]![0]!["omitted"]! && JsonSerializer.SerializeToUtf8Bytes(page, BridgeProtocol.Json).Length < 128 * 1024, "Oversized value explicitly omitted within page bound");
var maxUpload = Upload(Encoding.UTF8.GetBytes(many)); var maxReceipt = store.Review(id, path, maxUpload.Ref);
Check((int)store.Page((string)maxReceipt["recordId"]!, "beforeValues", 19800, 200)["rows"]!.AsArray().Count == 200, "Maximum snapshot navigable to last page");
string saved = Path.Combine(root, "project-reviews", id + ".json"); byte[] savedBytes = File.ReadAllBytes(saved); File.WriteAllText(saved, "tampered"); Reject(() => store.Page(record), "Saved source corruption refuses reopen", "ArtifactChanged"); File.WriteAllBytes(saved, savedBytes);
string filler = Path.Combine(root, "db-snapshot-reviews", "capacity.tmp"); using (var f = File.Create(filler)) f.SetLength(512L * 1024 * 1024);
var fresh = Upload(Encoding.UTF8.GetBytes(xml.Replace("TRUE", "FALSE"))); Reject(() => store.Review(id, path, fresh.Ref), "Incomplete files count toward quota", "ArtifactCapacity"); File.Delete(filler);
File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, reviewId = id, objectPath = path, before = Convert.ToBase64String(before), after = Convert.ToBase64String(Utf16(Encoding.UTF8.GetString(after))) }, BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, count = checks.Count, checks }, BridgeProtocol.Json));
Console.WriteLine($"PASS DB snapshots: {checks.Count} checks; synthetic documents only.");
