using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Safety;

if (args.Length != 1 || Directory.Exists(args[0]) || File.Exists(args[0])) throw new ArgumentException("Supply one fresh fixture output directory.");
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
int checks = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
void Refuse(Action action, string label)
{
    try { action(); }
    catch (Exception e) when (e is BridgeException or JsonException or DecoderFallbackException) { checks++; return; }
    throw new Exception("Expected refusal: " + label);
}
const string plc = "/projects/Fixture/devices/Station/plc/CPU日本", file = "C:/synthetic/Fixture.ap21";
var review = new JsonObject { ["model"] = new JsonObject { ["project"] = new JsonObject { ["projectPath"] = file },
    ["nodes"] = new JsonArray(new JsonObject { ["objectPath"] = plc, ["kind"] = "plc", ["origin"] = "native" }) } };
JsonObject Signature(string type, ulong value) => new() { ["type"] = type, ["value"] = value };
var snapshot = new JsonObject { ["project"] = new JsonObject { ["path"] = file }, ["plcPath"] = plc,
    ["isLoggedOn"] = false, ["isPasswordSet"] = false, ["warnings"] = new JsonArray(),
    ["completeness"] = new JsonObject { ["complete"] = false, ["scopeComplete"] = true, ["truncated"] = false, ["gaps"] = new JsonArray("No safety approval") },
    ["programSignatures"] = new JsonArray(Signature("CollectiveOfflineSignature", ulong.MaxValue), Signature("CommunicationAddressOfflineSignature", 0)),
    ["block"] = new JsonObject { ["path"] = plc + "/blocks/Main", ["plcPath"] = plc },
    ["blockSignatureServiceAvailable"] = true, ["blockSignatures"] = new JsonArray(Signature("BlockOfflineSignature", 9007199254740993)),
    ["runtimeGroups"] = new JsonArray(new JsonObject { ["name"] = "RTG日本", ["mainSafetyBlockName"] = "Main", ["opaque"] = new JsonArray(1, 2) }),
    ["settings"] = new JsonArray(new JsonObject { ["name"] = "setting", ["value"] = "preserved\r\n日本", ["valueComplete"] = true }),
    ["futureEnvelope"] = new JsonObject { ["opaque"] = true } };
byte[] Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);
JsonObject Build(JsonObject after, JsonObject? model = null) => SafetyEvidenceReview.Build(new string('a',64), model ?? review, plc, Bytes(snapshot), Bytes(after));
JsonObject Changed(Action<JsonObject> action) { var copy = snapshot.DeepClone().AsObject(); action(copy); return copy; }
var original = Bytes(snapshot); var same = Build(snapshot);
Check((int)same["summary"]!["changeCount"]! == 0, "Equal observations have no differences");
Check((int)same["summary"]!["issueCount"]! == 2, "Zero observations remain explicit in both phases");
Check(original.AsSpan().SequenceEqual(Bytes(snapshot)), "Input is not modified");
foreach (string flag in new[] { "nativeExecutable", "nativeExecuted", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "safetyApproval", "requiredSignatureProfileVerified", "wholeProgramAnalysisComplete" })
    Check(!(bool)same[flag]!, "Never promote " + flag);
var changed = Changed(s => { s["programSignatures"]![0]!["value"] = ulong.MaxValue - 1; s["blockSignatures"]![0]!["value"] = 9007199254740994UL; });
var result = Build(changed);
Check((int)result["summary"]!["programSignatureChangeCount"]! == 1 && (int)result["summary"]!["blockSignatureChangeCount"]! == 1, "Exact large integer changes detected");
Check((ulong)result["changes"]![0]!["before"]!["value"]! == ulong.MaxValue, "UInt64 maximum retained");
Check((string)result["changes"]![0]!["beforeSignatureDecimal"]! == "18446744073709551615", "Change decimal text survives clients with limited numeric precision");
Check((string)result["signatures"]![2]!["valueDecimal"]! == "9007199254740993", "Unchanged block signature also has exact decimal text");
var reordered = Changed(s => { var rows = s["programSignatures"]!.AsArray(); var first = rows[0]!.DeepClone(); rows.RemoveAt(0); rows.Add(first); });
Check((int)Build(reordered)["summary"]!["changeCount"]! == 0, "Signature row order is not identity");
Check((int)Build(Changed(s => s["runtimeGroups"]![0]!["opaque"] = new JsonArray(2, 1)))["summary"]!["changeCount"]! == 1, "Opaque nested array order stays significant");
Check((bool)Build(Changed(s => s["futureEnvelope"]!["opaque"] = false))["summary"]!["otherReportedContentChanged"]!, "Unknown envelope content participates in comparison");
Check((int)Build(Changed(s => s["programSignatures"]!.AsArray().RemoveAt(0)))["summary"]!["programSignatureChangeCount"]! == 1, "Missing signature stays an observed difference");
var partial = Build(Changed(s => { s["completeness"]!["scopeComplete"] = false; s["completeness"]!["truncated"] = true; s["warnings"]!.AsArray().Add(new JsonObject { ["path"] = plc, ["message"] = "missing attribute" }); }));
Check((int)partial["summary"]!["issueCount"]! == 3, "Actual native structured warnings remain visible");
Check((int)Build(Changed(s => s["blockSignatureServiceAvailable"] = false))["summary"]!["issueCount"]! == 3, "Unavailable block service cannot disappear");
foreach (string section in new[] { "programSignatures", "blockSignatures", "settings", "runtimeGroups" })
    Refuse(() => Build(Changed(s => s[section]!.AsArray().Add(s[section]![0]!.DeepClone()))), "duplicate " + section);
foreach (JsonNode? bad in new JsonNode?[] { JsonValue.Create(true), JsonValue.Create(-1), JsonValue.Create("1"), JsonNode.Parse("18446744073709551616"), JsonNode.Parse("1.5"), null })
    Refuse(() => Build(Changed(s => s["programSignatures"]![0]!["value"] = bad?.DeepClone())), "invalid UInt64");
Refuse(() => Build(Changed(s => s["plcPath"] = plc + "Other")), "wrong PLC");
Refuse(() => Build(Changed(s => s["project"]!["path"] = file + ".other")), "wrong project");
Refuse(() => Build(Changed(s => s["block"]!["path"] = plc + "/blocks/Other")), "different block");
Refuse(() => Build(Changed(s => s["block"]!["plcPath"] = plc + "Other")), "block owner");
Refuse(() => Build(Changed(s => s.Remove("block"))), "orphan signature service");
Refuse(() => Build(Changed(s => s["error"] = null)), "failed native response");
Refuse(() => Build(Changed(s => s["isLoggedOn"] = "false")), "Boolean coercion");
Refuse(() => Build(Changed(s => s["runtimeGroups"]![0]!["name"] = "RTG\n")), "control identity");
var virtualReview = review.DeepClone().AsObject(); virtualReview["model"]!["nodes"]![0]!["origin"] = "virtual";
Refuse(() => Build(snapshot, virtualReview), "virtual PLC");
var duplicateReview = review.DeepClone().AsObject(); duplicateReview["model"]!["nodes"]!.AsArray().Add(duplicateReview["model"]!["nodes"]![0]!.DeepClone());
Refuse(() => Build(snapshot, duplicateReview), "ambiguous PLC");
Refuse(() => SafetyEvidenceReview.Parse(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")), "duplicate JSON keys");
Refuse(() => SafetyEvidenceReview.Parse(new byte[] { 0xff }), "invalid UTF8");
Refuse(() => SafetyEvidenceReview.Parse(new byte[SafetyEvidenceReview.InputLimit + 1]), "size bound");
File.WriteAllBytes(Path.Combine(output, "before.json"), original);
File.WriteAllBytes(Path.Combine(output, "after.json"), Bytes(changed));
File.WriteAllBytes(Path.Combine(output, "report.json"), Bytes(result));
string root = Path.Combine(output, "store");
JsonObject Node(string path, string kind, string? parent) => new() { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind,
    ["name"] = path.Split('/').Last(), ["parentId"] = parent, ["plcPath"] = plc, ["origin"] = "native", ["metadata"] = new JsonObject() };
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = file, ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(Node("/projects/Fixture", "project", null), Node(plc, "plc", "/projects/Fixture")), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var uploads = new ArtifactUploadStore(root);
(string Id, string Ref) Upload(byte[] bytes, string fileName = "safety.json")
{
    string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    var u = uploads.Begin(fileName, bytes.Length, digest);
    for (int i = 0; i < bytes.Length; i += u.MaxChunkBytes) uploads.Write(u.UploadId, i, Convert.ToBase64String(bytes, i, Math.Min(u.MaxChunkBytes, bytes.Length-i)));
    return (u.UploadId, uploads.Commit(u.UploadId).ArtifactRef!);
}
var left = Upload(original); var right = Upload(Bytes(changed)); var store = new SafetyEvidenceWorkflowStore(root);
var retained = store.Review(id, plc, left.Ref, right.Ref); string record = (string)retained["recordId"]!;
Check((int)retained["summary"]!["changeCount"]! == 2, "Retained workflow compares original uploads");
foreach (string section in new[] { "observations", "signatures", "issues", "changes" })
    Check(store.Page(record, section, 0, 1)["nextOffset"] is not null, "Bounded paging " + section);
Refuse(() => store.Page(record, "changes", 0, 201), "Page count limit");
Refuse(() => store.Page(record, "changes", 100), "Offset outside report");
Refuse(() => store.Page(record, "unknown"), "Unknown section");
uploads.Release(left.Id); uploads.Release(right.Id);
Check(JsonNode.DeepEquals(retained, new SafetyEvidenceWorkflowStore(root).Page(record)), "Upload release and store restart preserve evidence");
foreach (string key in new[] { "input", "comparisonInput", "reportArtifact" })
{
    string path = Path.Combine(root, (string)retained[key]!["path"]!); byte[] saved = File.ReadAllBytes(path);
    File.WriteAllText(path, "tampered"); Refuse(() => store.Page(record), "Corrupted " + key); File.WriteAllBytes(path, saved);
}
string savedReview = Path.Combine(root, "project-reviews", id + ".json"); byte[] reviewBytes = File.ReadAllBytes(savedReview);
File.WriteAllText(savedReview, "tampered"); Refuse(() => store.Page(record), "Corrupted saved selection"); File.WriteAllBytes(savedReview, reviewBytes);
var large = Upload(Bytes(Changed(s => s["futureEnvelope"]!["opaque"] = new string('x', 130 * 1024))));
var small = Upload(original); var largeRecord = store.Review(id, plc, small.Ref, large.Ref);
var page = store.Page((string)largeRecord["recordId"]!, "changes");
Check((bool)page["rows"]![0]!["omitted"]!, "Oversized changed content is explicitly omitted from page, retained in original");
Check(Bytes(page).Length < 128 * 1024, "Response bound holds for oversized source");
var supportBytes = new byte[] { 0, 255, 1, 254, 2 };
var support = Upload(supportBytes, "opaque日本.bin");
JsonObject Source(string phase = "context", string purpose = "Supplied supporting evidence") => new() { ["uploadRef"] = support.Ref, ["phase"] = phase, ["purpose"] = purpose };
var sources = new JsonArray(Source());
var withSources = store.Review(id, plc, small.Ref, large.Ref, sources);
string sourceRecord = (string)withSources["recordId"]!;
Check((string)withSources["sources"]![0]!["fileName"]! == "opaque日本.bin", "Original supporting filename retained");
Check((int)withSources["sources"]![0]!["bytes"]! == supportBytes.Length && !(bool)withSources["nativeExecuted"]!, "Source byte count and nonexecution receipt");
Check(JsonNode.DeepEquals(withSources, store.Review(id, plc, small.Ref, large.Ref, sources)), "Same input/source roles give identical immutable record");
var reversedRole = store.Review(id, plc, small.Ref, large.Ref, new JsonArray(Source("before")));
Check((string)reversedRole["recordId"]! != sourceRecord, "Source role participates in record identity");
Check(store.Review(id, plc, small.Ref, large.Ref, new JsonArray(Source("before"), Source("after")))["sources"]!.AsArray().Count == 2, "Identical bytes in distinct phases remain valid supplied roles");
foreach (var invalidSources in new[] { new JsonArray(Source("invalid")), new JsonArray(Source(), Source()), new JsonArray(new JsonObject { ["uploadRef"] = support.Ref, ["phase"] = null, ["purpose"] = "x" }), new JsonArray(Source("context", "bad\n")), new JsonArray(Enumerable.Range(0,17).Select(i => (JsonNode)Source("context", i.ToString())).ToArray()) })
    Refuse(() => store.Review(id, plc, small.Ref, large.Ref, invalidSources), "Malformed source declarations");
var manySources = new JsonArray(Enumerable.Range(0,16).Select(i => (JsonNode)Source("context", new string('日', 250) + i)).ToArray());
var crowded = store.Review(id, plc, small.Ref, large.Ref, manySources);
var crowdedPage = store.Page((string)crowded["recordId"]!, "changes");
Check(Bytes(crowdedPage).Length <= 128 * 1024 && (bool)crowdedPage["rows"]![0]!["omitted"]!, "Large source metadata still permits bounded page progress");
string sourcePath = Path.Combine(root, (string)withSources["sources"]![0]!["artifact"]!["path"]!);
Check(File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(supportBytes), "Opaque supporting bytes preserved exactly");
uploads.Release(support.Id);
Check(JsonNode.DeepEquals(withSources, new SafetyEvidenceWorkflowStore(root).Page(sourceRecord)), "Supporting sources survive upload release and restart");
File.WriteAllText(sourcePath, "changed"); Refuse(() => store.Page(sourceRecord), "Tampered supporting source"); File.WriteAllBytes(sourcePath, supportBytes);
JsonObject ReadRecord(JsonObject receipt) => JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, (string)receipt["reportArtifact"]!["path"]!)))!.AsObject();
void Forged(Action<JsonObject> change, string label)
{
    var forged = ReadRecord(withSources); change(forged); byte[] bytes = Bytes(forged);
    string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    File.WriteAllBytes(Path.Combine(root, "safety-evidence-reviews", hash + ".json"), bytes);
    Refuse(() => store.Page(hash), label);
}
Forged(r => r["report"]!["nativeQualified"] = true, "Forged report claim despite correct envelope digest");
Forged(r => r["report"]!["summary"]!["changeCount"] = 0, "Forged derived comparison");
Forged(r => r["sources"]![0]!["bytes"] = supportBytes.Length + 1, "Forged source length");
Forged(r => r["sources"]![0]!["artifact"]!["path"] = "../outside.bin", "Noncanonical source path");
Forged(r => r["sources"]![0]!["phase"] = "approved", "Forged source phase");
Forged(r => r["sources"]!.AsArray().Add(r["sources"]![0]!.DeepClone()), "Duplicate retained source role");
Forged(r => r["input"]!["path"] = r["comparisonInput"]!["path"]!.DeepClone(), "Noncanonical observation reference");
Forged(r => r["extra"] = true, "Unknown record fields");
Refuse(() => Build(Changed(s => s["warnings"]!.AsArray().Add(null))), "Null warning");
Refuse(() => Build(Changed(s => s.Remove("completeness"))), "Missing capture completeness");
Refuse(() => Build(Changed(s => s["completeness"]!["scopeComplete"] = "true")), "Coverage Boolean coercion");
Refuse(() => Build(Changed(s => s["block"]!["path"] = plc + "Other/blocks/Main")), "Block path prefix confusion");
Refuse(() => SafetyEvidenceReview.Parse(Encoding.UTF8.GetBytes("{\"nested\":{\"a\":1,\"a\":2}}")), "Nested duplicate field");
Refuse(() => SafetyEvidenceReview.Parse(Encoding.UTF8.GetBytes(new string('[',33) + new string(']',33))), "Depth limit");
foreach (string section in new[] { "programSignatures", "blockSignatures", "settings", "runtimeGroups" })
    Refuse(() => Build(Changed(s => s[section] = new JsonArray(Enumerable.Range(0, section.EndsWith("Signatures") ? 129 : 4097).Select(i => (JsonNode)new JsonObject { ["type"] = i.ToString(), ["name"] = i.ToString(), ["value"] = 1UL }).ToArray()))), "Row bound " + section);
var noBlock = Changed(s => { s.Remove("block"); s.Remove("blockSignatures"); s.Remove("blockSignatureServiceAvailable"); s["programSignatures"] = new JsonArray(); });
var withoutBlock = SafetyEvidenceReview.Build(id, review, plc, Bytes(noBlock), Bytes(noBlock));
Check((int)withoutBlock["summary"]!["changeCount"]! == 0 && (int)withoutBlock["summary"]!["issueCount"]! == 2, "Whole PLC capture with empty signatures remains incomplete evidence");
var maximal = Changed(s => { foreach (string section in new[] { "settings", "runtimeGroups" }) s[section] = new JsonArray(Enumerable.Range(0,4096).Select(i => (JsonNode)new JsonObject { ["name"] = i.ToString(), ["value"] = i }).ToArray()); });
Check((int)Build(maximal)["summary"]!["changeCount"]! == 8194, "Bounded maximum settings/groups compare without loss");
var bigSupport = Upload(new byte[16 * 1024 * 1024], "bounded.bin");
var totalSources = new JsonArray(Enumerable.Range(0,5).Select(i => (JsonNode)new JsonObject { ["uploadRef"] = bigSupport.Ref, ["phase"] = "context", ["purpose"] = i.ToString() }).ToArray());
int retainedBefore = Directory.GetFiles(Path.Combine(root, "safety-evidence-reviews")).Length;
Refuse(() => store.Review(id, plc, small.Ref, large.Ref, totalSources), "Repeated source roles count toward total byte limit");
Check(Directory.GetFiles(Path.Combine(root, "safety-evidence-reviews")).Length == retainedBefore, "Failed source admission publishes no retained artifacts");
var tooBigSupport = Upload(new byte[16 * 1024 * 1024 + 1], "too-big.bin");
Refuse(() => store.Review(id, plc, small.Ref, large.Ref, new JsonArray(new JsonObject { ["uploadRef"] = tooBigSupport.Ref, ["phase"] = "context", ["purpose"] = "Oversized" })), "Per-source byte limit");
uploads.Release(bigSupport.Id); uploads.Release(tooBigSupport.Id);
File.WriteAllText(Path.Combine(output, "mcp-fixture.json"), new JsonObject { ["artifactRoot"] = root, ["reviewId"] = id, ["plcPath"] = plc,
    ["before"] = Convert.ToBase64String(original), ["after"] = Convert.ToBase64String(Bytes(changed)) }.ToJsonString());
File.WriteAllText(Path.Combine(output, "record.json"), retained.ToJsonString());
Console.WriteLine($"Safety evidence: {checks} checks passed; supplied synthetic observations only. Store: {root}");
