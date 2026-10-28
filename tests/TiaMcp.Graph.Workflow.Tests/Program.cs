using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Graph;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Supply a fresh evidence directory.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
var store = new GraphWorkflowStore(root); var uploads = new ArtifactUploadStore(root); int checks = 0;
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS GRAPH workflow " + name); }
void Reject(string code, Action action)
{
    try { action(); } catch (BridgeException error) { Check(error.Code == code, "refuses " + code + ": " + error.Code); return; }
    catch (GraphException error) { Check(error.Code == code, "refuses " + code + ": " + error.Code); return; }
    throw new Exception("Expected " + code);
}
string Upload(byte[] bytes)
{
    var status = uploads.Begin("sequence.xml", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    for (int offset = 0; offset < bytes.Length; offset += status.MaxChunkBytes)
        uploads.Write(status.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(status.MaxChunkBytes, bytes.Length - offset)));
    return uploads.Commit(status.UploadId).ArtifactRef!;
}
byte[] Download(string reference, string name)
{
    using var output = new MemoryStream();
    while (true)
    {
        var part = Json(store.ReadArtifact(reference, name, output.Length)); output.Write(Convert.FromBase64String(Text(part, "data")));
        if (part.GetProperty("complete").GetBoolean())
        { Check(Text(part, "sha256") == Convert.ToHexStringLower(SHA256.HashData(output.ToArray())), "download matches independent SHA-256"); return output.ToArray(); }
    }
}
var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/sequence-v1.xml"));
var upload = Upload(raw); var before = Json(store.Inspect(upload)); string beforeRef = Text(before, "recordRef");
Check(!before.GetProperty("nativeQualified").GetBoolean() && !before.GetProperty("fullSchemaValidated").GetBoolean(), "inspection does not promote native or schema qualification");
uploads.Release(upload["upload:".Length..]); store = new(root);
Check(Download(beforeRef, "original.xml").SequenceEqual(raw), "exact original bytes survive upload release and fresh store");
Check(Convert.ToHexStringLower(SHA256.HashData(Download(beforeRef, "receipt.json"))) == beforeRef.Split(':')[2], "receipt bytes independently bind record handle");
var report = JsonDocument.Parse(Download(beforeRef, "report.json")).RootElement;
Check(Convert.FromBase64String(Text(report, "originalSourceBase64")).SequenceEqual(raw), "full report retains original encoded source");
var page = Json(store.ReportPage(beforeRef, "/graphs/0/sequences/0/steps", count:1));
Check(page.GetProperty("rows").GetArrayLength() == 1 && page.GetProperty("nextOffset").GetInt32() == 1, "nested array page retains exact index and continuation");
Check(Json(store.ReportPage(beforeRef, "/graphs/0/sequences/0/steps", offset:page.GetProperty("total").GetInt32())).GetProperty("nextOffset").ValueKind == JsonValueKind.Null,
    "empty terminal page is explicit");
var after = Json(store.Inspect(Upload(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/sequence-v2.xml")))));
var comparison = Json(store.Compare(beforeRef, Text(after, "recordRef"))); string comparisonRef = Text(comparison, "recordRef");
Check(comparison.GetProperty("sourceBytesChanged").GetBoolean() && comparison.GetProperty("counts").GetProperty("changes").GetInt32() > 0 &&
    !comparison.GetProperty("behavioralEquivalenceCertified").GetBoolean(), "comparison exposes changes without claiming behavior");
Reject("GraphInvalidReference", () => store.Compare(comparisonRef, beforeRef));
Reject("GraphInvalidSection", () => store.ReportPage(beforeRef, "/originalSourceBase64"));
Reject("GraphPageLimit", () => store.ReportPage(beforeRef, "/graphs", count:201));
Reject("GraphInvalidSection", () => store.ReportPage(beforeRef, "/graphs/00/sequences"));
Reject("GraphArtifactMissing", () => store.ReadArtifact(beforeRef, "../source.xml"));
Reject("GraphInvalidReference", () => store.Reopen(new string(':', 1000000)));
Reject("InvalidXml", () => store.Inspect(Upload(Encoding.UTF8.GetBytes("<!DOCTYPE Graph [<!ENTITY e SYSTEM 'file:///not-permitted'>]><Graph>&e;</Graph>"))));
Reject("GraphCapacity", () => store.Inspect(Upload(new byte[GraphDocument.MaxInputBytes + 1])));
var hostile = Json(store.Inspect(Upload(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(raw).Replace("Idle", "&lt;script&gt;alert(1)&lt;/script&gt;")))));
string html = Encoding.UTF8.GetString(Download(Text(hostile, "recordRef"), "viewer.html"));
Check(!html.Contains("<script", StringComparison.OrdinalIgnoreCase) && html.Contains("Content-Security-Policy"), "HTML escapes hostile names and runs no script");
var large = Json(store.Inspect(Upload(Encoding.UTF8.GetBytes("<Graph><Unknown>" + new string('x', 150000) + "</Unknown></Graph>"))));
var omitted = Json(store.ReportPage(Text(large, "recordRef"), "/unsupported"));
Check(omitted.GetProperty("rows").EnumerateArray().Any(r => r.TryGetProperty("omitted", out var value) && value.GetBoolean()), "oversized rows are explicit omissions with complete artifact retained");
string original = Path.Combine(root, "graph-workflow", beforeRef.Split(':')[1], "original.xml"); File.AppendAllText(original, " ");
Reject("GraphIntegrityFailure", () => store.Reopen(comparisonRef)); File.WriteAllBytes(original, raw);
Check(Text(Json(store.Reopen(comparisonRef)), "state") == "complete", "restored exact source revalidates full comparison input chain");
string incomplete = Path.Combine(root, "graph-workflow", new string('a',32)); Directory.CreateDirectory(incomplete); File.WriteAllText(Path.Combine(incomplete,"intent.json"),"interrupted fixture");
Check(Json(store.List()).GetProperty("rows").EnumerateArray().Any(r=>Text(r,"state")=="incomplete"), "unfinished publication remains discoverable");
File.AppendAllText(Path.Combine(root,"graph-workflow",Text(after,"recordRef").Split(':')[1],"report.json"), " ");
Check(Json(store.List()).GetProperty("rows").EnumerateArray().Any(r=>Text(r,"state")=="integrity-failed"), "changed report is discoverable as damaged");
string exports = Path.Combine(root, "exports"); Directory.CreateDirectory(exports);
string Fixture(string name) { string path = Path.Combine(exports, name); File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", name), path); return path; }
string populated = Fixture("native-populated.xml"), emptyCondition = Fixture("native-empty-condition.xml");
var exported = Json(store.Retain(populated, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(populated)))));
Check(Download(Text(exported, "recordRef"), "original.xml").SequenceEqual(File.ReadAllBytes(populated)), "native export is retained as an exact document record");
Reject("GraphInputChanged", () => store.Retain(populated, new string('0', 64)));
Reject("ArtifactPathDenied", () => store.Retain(Path.Combine(AppContext.BaseDirectory, "fixtures", "native-populated.xml"), new string('0', 64)));
JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);
var byRef = store.ImportPayload(Payload(new { graphRef = Text(exported, "recordRef"), expectedBlockNames = new[] { "GraphQualification" } }));
Check(byRef["xml"]!.GetValue<string>().StartsWith("<?xml") && byRef["expectedBlockNames"]![0]!.GetValue<string>() == "GraphQualification" &&
    byRef["sourceSha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(populated))), "retained native GRAPH document becomes an import payload without BOM");
Check(store.ImportPayload(Payload(new { path = "exports/native-populated.xml" }))["xml"]!.GetValue<string>() == byRef["xml"]!.GetValue<string>(), "artifact-relative path yields the same import payload");
Reject("GraphImportRefused", () => store.ImportPayload(Payload(new { path = emptyCondition })));
Reject("GraphImportRefused", () => store.ImportPayload(Payload(new { graphRef = Text(large, "recordRef") })));
Reject("InvalidRequest", () => store.ImportPayload(Payload(new { graphRef = Text(exported, "recordRef"), path = populated })));
var editResult = Json(store.Edit(Text(exported, "recordRef"), Payload(new object[] {
    new { op = "renameStep", number = 1, name = "Wait" }, new { op = "setStepComment", number = 1, lang = "en-US", text = "Waits for start" } })));
var edited = editResult.GetProperty("document"); string editedRef = Text(edited, "recordRef");
Check(edited.GetProperty("inputs")[0].GetString() == Text(exported, "recordRef") && Text(editResult.GetProperty("comparison"), "kind") == "comparison" &&
    editResult.GetProperty("comparison").GetProperty("inputs")[1].GetString() == editedRef && editResult.GetProperty("comparison").GetProperty("counts").GetProperty("changes").GetInt32() > 0,
    "typed edit retains a source-linked document and its comparison");
Check(store.ImportPayload(Payload(new { graphRef = editedRef, expectedBlockNames = new[] { "GraphQualification" } }))["xml"]!.GetValue<string>().Contains("Name=\"Wait\""),
    "edited document becomes a graph_import payload");
var chained = Json(store.Edit(editedRef, Payload(new[] { new { op = "renameTransition", number = 1, name = "Go" } })));
Check(Text(Json(store.Reopen(Text(chained.GetProperty("comparison"), "recordRef"))), "state") == "complete" &&
    Text(Json(store.Reopen(Text(chained.GetProperty("document"), "recordRef"))), "state") == "complete", "chained edit lineage reopens");
Reject("GraphEditIdentity", () => store.Edit(editedRef, Payload(new[] { new { op = "renameStep", number = 9, name = "Ghost" } })));
Reject("GraphEditInput", () => store.Edit(editedRef, Payload(new { op = "renameStep" })));
Reject("GraphInvalidReference", () => store.Edit(Text(editResult.GetProperty("comparison"), "recordRef"), Payload(new[] { new { op = "renameStep", number = 1, name = "Ghost" } })));
Console.WriteLine($"{checks} GRAPH workflow checks passed; no native execution.");
