using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Drives;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidDriveProfile")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
const string project = "/projects/Fixture", hardware = project + "/devices/Drive/items/CU";
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = project, ["objectPath"] = project, ["kind"] = "project", ["name"] = "Fixture", ["parentId"] = null, ["metadata"] = new JsonObject() },
    new JsonObject { ["nodeId"] = hardware, ["objectPath"] = hardware, ["kind"] = "hardware", ["name"] = "CU", ["parentId"] = project, ["metadata"] = new JsonObject() }), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var review = reviews.Read(id);
JsonObject Scalar(string text, string type = "opaque") => new() { ["kind"] = "scalar", ["type"] = type, ["text"] = text };
JsonObject Parameter(string name, int number, int index = -1, string? parent = null, string access = "unknown") => new() {
    ["name"] = name, ["number"] = number, ["arrayIndex"] = index, ["parentName"] = parent, ["unit"] = "", ["access"] = access, ["value"] = Scalar("0"),
    ["metadata"] = new JsonObject { ["description"] = "日本語 😀\nopaque", ["MinValue"] = "0", ["MaxValue"] = "100" } };
var parameters = new JsonArray(Parameter("p100[0]", 100, 0), Parameter("p100[0].0", 100, 0, "p100[0]"), Parameter("p100[0].3", 100, 0, "p100[0]", "readOnly"), Parameter("p200", 200), Parameter("p300", 300), Parameter("r19", 19, access: "readOnly"));
parameters[4]!["value"] = new JsonObject { ["kind"] = "reference", ["driveId"] = "CU-A", ["parameterName"] = "r19", ["reason"] = null };
var before = new JsonObject { ["identity"] = new JsonObject { ["driveId"] = "CU-A", ["family"] = "G120", ["orderNumber"] = "supplied-order", ["firmware"] = "supplied-version", ["parameterSet"] = null, ["unknownReason"] = null },
    ["valueDomain"] = "offline", ["parametersComplete"] = false, ["parameters"] = parameters };
var after = before.DeepClone().AsObject(); after["parameters"]![2]!["value"] = Scalar("1"); after["parameters"]![2]!["access"] = "readWrite";
after["parameters"]![3]!["value"] = Scalar("9007199254740993"); after["parameters"]![3]!["unit"] = null;
var packet = new JsonObject { ["schemaVersion"] = "drive-profile-review-request.v1", ["reviewId"] = id, ["hardwarePath"] = hardware, ["afterKind"] = "proposal", ["before"] = before, ["after"] = after };
JsonObject Mutate(Action<JsonObject> edit) { var p = packet.DeepClone().AsObject(); edit(p); return p; }
JsonObject Build(JsonObject? p = null, JsonObject? r = null) => DriveProfileReview.Build(id, r ?? review, p ?? packet);
var result = Build();
Check((int)result["summary"]!["changedCount"]! == 2, "Exact changed parameter count");
Check((int)result["summary"]!["readOnlyConflictCount"]! == 1, "After access promotion cannot erase supplied read-only conflict");
Check((int)result["summary"]!["unknownAccessChangeCount"]! == 1, "Unknown access cannot become write permission");
Check((string)result["observations"]![2]!["parameter"]!["name"]! == "p100[0].3", "Sparse bit collection offset never rewrites full bit identity");
Check((string)result["observations"]![2]!["parentResolution"]! == "suppliedParentChain", "Exact declared bit parent remains supplied evidence");
Check((string)result["observations"]![4]!["referenceResolution"]! == "suppliedParameter", "Exact local BICO reference resolves only to supplied row");
foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "driveIdentityVerified", "wholeDriveAnalysisComplete", "nativeConstraintsVerified" }) Check(!(bool)result[flag]!, "Supplied evidence cannot promote " + flag);
Check((string)result["observations"]![0]!["parameter"]!["metadata"]!["description"]! == "日本語 😀\nopaque", "Opaque multiline metadata preserved");
var observed = Build(Mutate(p => { p["afterKind"] = "observation"; p["after"]!["valueDomain"] = "online"; }));
Check((string)observed["beforeValueDomain"]! == "offline" && (string)observed["afterValueDomain"]! == "online" && (int)observed["summary"]!["readOnlyConflictCount"]! == 0, "Offline/online observations remain distinct domains without mutation assessment");
var large = Build(Mutate(p => { p["before"]!["parameters"]![3]!["value"] = Scalar("9007199254740992"); p["after"]!["parameters"]![3]!["unit"] = ""; }));
Check((int)large["summary"]!["changedCount"]! == 2, "Beyond-double scalar values never coerce");
Check((string)Build(Mutate(p => p["after"]!["parameters"]![2]!["number"] = 101))["changes"]![2]!["status"]! == "selectorConflict", "Same name with changed Number is a selector conflict");
var moved = Build(Mutate(p => p["after"]!["parameters"]![2]!["name"] = "p100[0].2"));
Check(moved["changes"]!.AsArray().Count(c => (string?)c!["status"] is "onlyBefore" or "onlyAfter") == 2, "Bit name change never matches by ordinal or numeric tuple");
Check((int)Build(Mutate(p => p["before"]!["parameters"]![4]!["value"]!["driveId"] = "OtherCU"))["summary"]!["unresolvedReferenceCount"]! == 1, "Same-name source in another drive never resolves locally");
Check((int)Build(Mutate(p => p["before"]!["parameters"]![4]!["value"]!["parameterName"] = "R19"))["summary"]!["unresolvedReferenceCount"]! == 1, "BICO reference is case exact");
var unknownOwner = Build(Mutate(p => { p["before"]!["parameters"]![4]!["value"]!["driveId"] = null; p["before"]!["parameters"]![4]!["value"]!["reason"] = "Source owner unavailable"; }));
Check((string)unknownOwner["observations"]![4]!["referenceResolution"]! == "unresolved", "Unknown BICO owner is not asserted to be another drive");
var writable = Build(Mutate(p => p["before"]!["parameters"]![3]!["access"] = "readWrite"));
Check((string)writable["changes"]![3]!["proposalAssessment"]! == "notAssessed" && !(bool)writable["nativeConstraintsVerified"]!, "Supplied writable state never produces approval");
var metadataNumber = Build(Mutate(p => { p["before"]!["parameters"]![0]!["metadata"]!["opaque"] = JsonNode.Parse("1e400"); p["after"]!["parameters"]![0]!["metadata"]!["opaque"] = JsonNode.Parse("2e400"); }));
Check((string)metadataNumber["changes"]![0]!["changedFields"]![0]! == "metadata", "Opaque large exponent metadata never collapses to infinity");
var chain = Build(Mutate(p => { foreach (string phase in new[] { "before", "after" }) p[phase]!["parameters"] = new JsonArray(Enumerable.Range(0,131).Select(i => (JsonNode?)Parameter("bit"+i, 1, 0, i == 130 ? null : "bit"+(i+1))).ToArray()); }));
Check((string)chain["observations"]![0]!["parentResolution"]! == "unresolved" && (string)chain["observations"]![130]!["parentResolution"]! == "notDeclared", "Long parent chain stops within traversal budget");
Check(!(bool)Build(Mutate(p => { p["before"]!["parametersComplete"] = true; p["after"]!["parametersComplete"] = true; }))["wholeDriveAnalysisComplete"]!, "Supplied completeness never certifies whole-drive coverage");
var unknownFirmware = Build(Mutate(p => { foreach (string phase in new[] { "before", "after" }) { p[phase]!["identity"]!["firmware"] = null; p[phase]!["identity"]!["unknownReason"] = "Firmware not observed"; } }));
Check(unknownFirmware["identity"]!["firmware"] is null && !(bool)unknownFirmware["driveIdentityVerified"]!, "Unknown firmware remains explicit without a fabricated version");
Reject(() => Build(Mutate(p => { foreach (string phase in new[] { "before", "after" }) p[phase]!["identity"]!["firmware"] = null; })), "Unknown target metadata needs a reason");
var unknown = Build(Mutate(p => p["before"]!["parameters"]![4]!["value"] = new JsonObject { ["kind"] = "unavailable", ["reason"] = "Native source not captured" }));
Check((string)unknown["observations"]![4]!["parameter"]!["value"]!["kind"]! == "unavailable", "Unavailable value never becomes zero or disconnected");
foreach (string defect in new[] { "absent", "cyclic", "number", "index" })
{
    var p = Mutate(p => { if (defect == "absent") p["before"]!["parameters"]![2]!["parentName"] = "missing"; else if (defect == "cyclic") p["before"]!["parameters"]![0]!["parentName"] = "p100[0].3"; else p["before"]!["parameters"]![0]![defect == "number" ? "number" : "arrayIndex"] = 5; });
    Check((int)Build(p)["summary"]!["unresolvedParentCount"]! > 0, "Unresolved parent " + defect + " remains explicit");
}
Check(JsonNode.DeepEquals(result["changes"], Build(Mutate(p => { var rows = p["after"]!["parameters"]!.AsArray(); var first = rows[0]; rows.RemoveAt(0); rows.Add(first); }))["changes"]), "Collection order never changes parameter matching");
Reject(() => Build(Mutate(p => p["reviewId"] = new string('0',64))), "Wrong review refused");
Reject(() => Build(Mutate(p => p["hardwarePath"] = hardware.ToLowerInvariant())), "Case-distinct hardware refused");
foreach (string change in new[] { "driveId", "family", "orderNumber", "firmware", "parameterSet" }) Reject(() => Build(Mutate(p => p["after"]!["identity"]![change] = "other")), "Cross-profile " + change + " substitution refused");
Reject(() => Build(Mutate(p => p["before"]!["valueDomain"] = "online")), "Online-value proposal refused");
Reject(() => Build(Mutate(p => p["afterKind"] = "write")), "Native operation kind refused");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]!.AsArray().Add(p["after"]!["parameters"]![0]!.DeepClone()))), "Duplicate parameter names refused");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]![0]!["arrayIndex"] = 32768)), "Documented array-index range enforced");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]![0]!["number"] = 1.5)), "Fractional selector refused");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]![0]!["name"] = "p\n1")), "Control-bearing name refused");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]![0]!["value"] = null)), "Null value requires explicit unavailable state");
Reject(() => Build(Mutate(p => p["after"]!["parameters"]![4]!["value"]!["parameterName"] = null)), "Missing BICO selector requires reason");
Reject(() => Build(Mutate(p => p["after"]!["parametersComplete"] = "true")), "Nonboolean completeness refused");
Reject(() => Build(Mutate(p => p["nativeQualified"] = true)), "Unexpected native claim refused");
Reject(() => DriveProfileReview.Parse(Encoding.UTF8.GetBytes("{\"x\":{\"a\":1,\"a\":2}}")), "Duplicate nested JSON refused");
Reject(() => DriveProfileReview.Parse(new byte[DriveProfileReview.InputLimit+1]), "Input byte limit enforced");
foreach (var bad in new[] { new byte[] { 255 }, Encoding.UTF8.GetBytes("{\"x\":\"\\ud800\"}"), Encoding.UTF8.GetBytes(new string('[',33)+new string(']',33)) })
{ bool refused = false; try { DriveProfileReview.Parse(bad); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException) { refused = true; } Check(refused, "Invalid Unicode or excessive JSON depth refused"); }
foreach (string defect in new[] { "virtual", "block" }) { var r = review.DeepClone().AsObject(); r["model"]!["nodes"]![1]![defect == "virtual" ? "origin" : "kind"] = defect; Reject(() => Build(r: r), "Non-native-hardware selection refused: " + defect); }
var uploads = new ArtifactUploadStore(root);
(string Id,string Ref,byte[] Bytes) Upload(JsonObject p) { byte[] data = Encoding.UTF8.GetBytes(p.ToJsonString()+"\n"); var u = uploads.Begin("drive-profile.json",data.Length,Hash(data)); for (int i=0;i<data.Length;i+=u.MaxChunkBytes) uploads.Write(u.UploadId,i,Convert.ToBase64String(data,i,Math.Min(u.MaxChunkBytes,data.Length-i))); return (u.UploadId,uploads.Commit(u.UploadId).ArtifactRef!,data); }
var input = Upload(packet); var store = new DriveProfileWorkflowStore(root); var receipt = store.Review(id,input.Ref); string record = (string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(id,input.Ref)), "Idempotent receipt");
Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(input.Bytes), "Exact original bytes retained");
foreach (string section in new[] { "observations", "changes" }) Check(store.Page(record,section,0,1)["nextOffset"] is not null, "Stable bounded " + section + " pages");
Reject(() => store.Page(record,"telegrams"), "V1 refuses v2-only telegram sections", "InvalidRequest");
Reject(() => store.Page(record,"changes",99), "Out-of-bounds offset refused", "InvalidRequest");
Reject(() => store.Page(record,"changes",0,201), "Page count bounded", "InvalidRequest");
uploads.Release(input.Id); Check(JsonNode.DeepEquals(receipt,new DriveProfileWorkflowStore(root).Page(record)), "Release and restart preserve receipt");
foreach (string key in new[] { "input", "reportArtifact" }) { string file = Path.Combine(root,(string)receipt[key]!["path"]!); byte[] original = File.ReadAllBytes(file); File.WriteAllText(file,"tampered"); Reject(() => store.Page(record),"Corrupted "+key+" refused","ArtifactChanged"); File.WriteAllBytes(file,original); }
foreach (string defect in new[] { "report", "input-path", "extra" })
{
    var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!;
    if (defect=="report") envelope["report"]!["driveIdentityVerified"]=true; else if (defect=="extra") envelope["extra"]=true; else envelope["input"]!["path"]="other/file";
    byte[] data=JsonSerializer.SerializeToUtf8Bytes(envelope,BridgeProtocol.Json);string hash=Hash(data);File.WriteAllBytes(Path.Combine(root,"drive-profile-reviews",hash+".json"),data);
    Reject(()=>store.Page(hash),"Rehashed forged "+defect+" refused",defect=="report"?"ArtifactChanged":"InvalidDriveProfile");
}
var wide=Upload(Mutate(p=>p["before"]!["parameters"]![0]!["metadata"]!["wide"]=new string('x',130*1024)));var wideReceipt=store.Review(id,wide.Ref);var page=store.Page((string)wideReceipt["recordId"]!,"observations");
Check((bool)page["rows"]![0]!["omitted"]!&&JsonSerializer.SerializeToUtf8Bytes(page,BridgeProtocol.Json).Length<128*1024,"Oversized row explicitly omitted within page budget");
var many=Mutate(p=>{foreach(string phase in new[]{"before","after"})p[phase]!["parameters"]=new JsonArray(Enumerable.Range(0,4096).Select(i=>(JsonNode?)Parameter("p"+i,i)).ToArray());});
var max=Upload(many);var maxReceipt=store.Review(id,max.Ref);Check((int)store.Page((string)maxReceipt["recordId"]!,"observations",8000,200)["total"]! ==8192,"Maximum supplied parameters remain navigable");
Reject(()=>Build(Mutate(p=>p["before"]!["parameters"]=new JsonArray(Enumerable.Range(0,4097).Select(i=>(JsonNode?)Parameter("p"+i,i)).ToArray()))),"Parameter row bound enforced");
string saved=Path.Combine(root,"project-reviews",id+".json");byte[] savedBytes=File.ReadAllBytes(saved);File.WriteAllText(saved,"tampered");Reject(()=>store.Page(record),"Saved source corruption refused","ArtifactChanged");File.WriteAllBytes(saved,savedBytes);
string filler=Path.Combine(root,"drive-profile-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);var fresh=Upload(Mutate(p=>p["afterKind"]="observation"));Reject(()=>store.Review(id,fresh.Ref),"Incomplete retention counts toward quota","ArtifactCapacity");File.Delete(filler);
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new {artifactRoot=root,reviewId=id,packet},BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new {completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));
Console.WriteLine($"PASS drive profiles: {checks.Count} checks; supplied synthetic evidence only.");
TelegramTests.Run(root, review, packet);
