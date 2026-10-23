using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Security;

if (args is ["--handoff-publication-child", var childRoot, var childUpload, var childPhase, var signalPath])
{
    var childStore = new RuntimeHandoffWorkflowStore(childRoot, null, (phase, path) => {
        if (phase == childPhase) { File.WriteAllText(signalPath, path); Thread.Sleep(Timeout.Infinite); }
    });
    if (childPhase.StartsWith("observation", StringComparison.Ordinal)) childStore.RetainObservation("target", childUpload);
    else childStore.Review(childUpload);
    return;
}
if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidAccessReview")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
const string project = "/projects/Fixture", hardware = project + "/devices/CPU/items/CPU";
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = "project", ["objectPath"] = project, ["kind"] = "project", ["name"] = "Fixture", ["parentId"] = null, ["metadata"] = new JsonObject() },
    new JsonObject { ["nodeId"] = "cpu", ["objectPath"] = hardware, ["kind"] = "hardware", ["name"] = "CPU", ["parentId"] = "project", ["metadata"] = new JsonObject() }), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!; var review = reviews.Read(id);
JsonObject Right(string name, string scope = "cpu:A") => new() { ["id"] = name, ["scope"] = scope };
JsonObject Entity(string name, string kind, JsonArray? rights = null, params string[] inherits) => new() {
    ["id"] = name, ["kind"] = kind, ["enabled"] = true, ["rightsComplete"] = true, ["assignmentsComplete"] = true,
    ["rights"] = rights ?? new(), ["inherits"] = new JsonArray(inherits.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
var identity = new JsonObject { ["domain"] = "cpuRuntime", ["sourceId"] = "cpu:A", ["version"] = "supplied-version", ["securityMode"] = "supplied-mode", ["unknownReason"] = null };
var before = new JsonObject { ["identity"] = identity.DeepClone(), ["entitiesComplete"] = true, ["entities"] = new JsonArray(
    Entity("user:Überwachung", "user", null, "group:operators"), Entity("group:operators", "group", null, "role:monitor"),
    Entity("role:monitor", "role", new JsonArray(Right("ReadTag"), Right("ModifyTag"))), Entity("role:unrelated", "role", new JsonArray(Right("WriteFirmware")))) };
var after = before.DeepClone().AsObject(); after["entities"]![2]!["rights"]!.AsArray().RemoveAt(1);
var packet = new JsonObject { ["schemaVersion"] = "access-review-request.v1", ["reviewId"] = id,
    ["owner"] = new JsonObject { ["nodeId"] = "cpu", ["objectPath"] = hardware }, ["identity"] = identity,
    ["subjectId"] = "user:Überwachung", ["afterKind"] = "proposal", ["requiredRights"] = new JsonArray(Right("ReadTag")), ["allowedRights"] = new JsonArray(Right("ReadTag")), ["before"] = before, ["after"] = after };
JsonObject Mutate(Action<JsonObject> edit) { var p = packet.DeepClone().AsObject(); edit(p); return p; }
JsonObject Build(JsonObject? p = null, JsonObject? r = null) => AccessReview.Build(id, r ?? review, p ?? packet);
var expected = Build();
Check((int)expected["summary"]!["beforeUnexpectedCount"]! == 1 && (int)expected["summary"]!["afterUnexpectedCount"]! == 0, "Read-only proposal removes exact supplied extra grant");
Check((int)expected["summary"]!["changedCount"]! == 1, "Only the edited role changes");
Check(expected["assessments"]![0]!["potentialRights"]!.AsArray().Count == 2, "Unrelated role never contributes grants");
Check((bool)expected["assessments"]![0]!["suppliedClosureComplete"]! && expected["assessments"]![0]!["visitedIds"]!.AsArray().Count == 3, "Transitive supplied group and role closure");
foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "effectiveRightsVerified", "minimumRightsVerified", "lockoutSafetyVerified" }) Check(!(bool)expected[flag]!, "No promotion of " + flag);
Check((int)Build(Mutate(p => { p["requiredRights"]![0]!["scope"] = "cpu:B"; p["allowedRights"]![0]!["scope"] = "cpu:B"; }))["summary"]!["afterRequiredNotObservedCount"]! == 1, "Equal right names on another scope never satisfy requirements");
Check((int)Build(Mutate(p => { p["allowedRights"]![0]!["id"] = "readtag"; p["requiredRights"]![0]!["id"] = "readtag"; }))["summary"]!["afterUnexpectedCount"]! == 1, "Case-sensitive exact grants");
foreach (string defect in new[] { "missing", "cycle", "unknown", "disabled", "rights", "assignments", "inventory" })
{
    var p = Mutate(p => {
        var entities = p["after"]!["entities"]!;
        if (defect == "missing") entities[0]!["inherits"] = new JsonArray("absent");
        if (defect == "cycle") entities[2]!["inherits"] = new JsonArray("group:operators");
        if (defect == "unknown") entities[2]!["enabled"] = null;
        if (defect == "disabled") entities[2]!["enabled"] = false;
        if (defect == "rights") entities[2]!["rightsComplete"] = false;
        if (defect == "assignments") entities[2]!["assignmentsComplete"] = false;
        if (defect == "inventory") p["after"]!["entitiesComplete"] = false;
    });
    var a = Build(p)["assessments"]![1]!;
    Check(!(bool)a["suppliedClosureComplete"]! && a["gaps"]!.AsArray().Count > 0, "Explicit gap: " + defect);
    if (defect is "unknown" or "disabled") Check(a["potentialRights"]!.AsArray().Count == 1, "Inactive declarations cannot hide potential grants: " + defect);
}
var absent = Build(Mutate(p => p["subjectId"] = "missing"))["assessments"]![0]!;
Check(absent["requiredNotObserved"]!.AsArray().Count == 1 && !(bool)absent["suppliedClosureComplete"]!, "Absent subject is unresolved, not a safe empty account");
var diamond = Build(Mutate(p => p["before"]!["entities"]![0]!["inherits"]!.AsArray().Add("role:monitor")));
Check(diamond["assessments"]![0]!["potentialRights"]!.AsArray().Count == 2 && diamond["assessments"]![0]!["gaps"]!.AsArray().Count == 0, "Diamond graph deduplicates without false cycle");
var deep = Mutate(p => { var e = p["before"]!["entities"]!.AsArray(); e[0]!["inherits"] = new JsonArray("g0"); for (int i=0;i<66;i++) e.Add(Entity("g"+i,"group",null,i==65?"role:monitor":"g"+(i+1))); });
Check(Build(deep)["assessments"]![0]!["gaps"]!.AsArray().Any(g => (string?)g!["reason"] == "depthLimit"), "Deep graph explicitly bounded");
foreach (string domain in new[] { "projectUmac", "legacyWeb", "siwarexWeb", "opcUa" })
{
    var p = Mutate(p => { foreach (var i in new[] { p["identity"]!, p["before"]!["identity"]!, p["after"]!["identity"]! }) i["domain"] = domain;
        if (domain == "projectUmac") { p["owner"]!["nodeId"] = "project"; p["owner"]!["objectPath"] = project; } });
    Check((string)Build(p)["identity"]!["domain"]! == domain, "Separate explicit domain " + domain);
}
foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
    ("foreign review", p=>p["reviewId"]=new string('a',64)), ("wrong node",p=>p["owner"]!["nodeId"]="other"), ("wrong path",p=>p["owner"]!["objectPath"]="/other"),
    ("domain drift",p=>p["after"]!["identity"]!["domain"]="legacyWeb"), ("source drift",p=>p["after"]!["identity"]!["sourceId"]="cpu:B"),
    ("required outside allowed",p=>p["requiredRights"]!.AsArray().Add(Right("WriteFirmware"))), ("duplicate entity",p=>p["before"]!["entities"]!.AsArray().Add(p["before"]!["entities"]![0]!.DeepClone())),
    ("duplicate scoped right",p=>p["allowedRights"]!.AsArray().Add(Right("ReadTag"))), ("duplicate link",p=>p["before"]!["entities"]![0]!["inherits"]!.AsArray().Add("group:operators")),
    ("credential field",p=>p["before"]!["entities"]![0]!["password"]="REDACTION-TEST-SENTINEL"), ("private key field",p=>p["privateKey"]="REDACTION-TEST-SENTINEL"),
    ("malformed state",p=>p["after"]!["entities"]![0]!["enabled"]="true"), ("unknown entity kind",p=>p["after"]!["entities"]![0]!["kind"]="effectiveAdministrator"),
    ("unknown coverage",p=>p["after"]!["entities"]![0]!["assignmentsComplete"]=null), ("unknown mode reason",p=>p["identity"]!["securityMode"]=null),
    ("control character",p=>p["subjectId"]="bad\nvalue"), ("unknown after kind",p=>p["afterKind"]="apply") }) Reject(()=>Build(Mutate(edit)),label);
var virtualReview=review.DeepClone().AsObject();virtualReview["model"]!["nodes"]![1]!["origin"]="virtual";Reject(()=>Build(r:virtualReview),"Virtual owner refused");
Reject(()=>AccessReview.Parse(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")),"Duplicate JSON fields refused");
Reject(()=>AccessReview.Parse(new byte[AccessReview.InputLimit+1]),"Input bound enforced");
var many=Mutate(p=>{var e=p["before"]!["entities"]!.AsArray();while(e.Count<512)e.Add(Entity("e"+e.Count,"role"));});Check((int)Build(many)["summary"]!["beforeCount"]! ==512,"Maximum entities accepted");
many["before"]!["entities"]!.AsArray().Add(Entity("overflow","role"));Reject(()=>Build(many),"Entity bound enforced");
foreach(string field in new[]{"rights","inherits"}) Reject(()=>Build(Mutate(p=>p["before"]!["entities"]![0]![field]=new JsonArray(Enumerable.Range(0,65).Select(i=>field=="rights"?(JsonNode?)Right("r"+i):JsonValue.Create("r"+i)).ToArray()))),"Per-row bound "+field);
try { AccessReview.Parse([0xff]); throw new Exception("Invalid UTF-8 accepted"); } catch(DecoderFallbackException) { Check(true,"Invalid UTF-8 refused"); }
try { Build(Mutate(p=>p["password"]="REDACTION-TEST-SENTINEL")); throw new Exception("Secret field accepted"); }
catch(BridgeException e) { Check(!e.ToString().Contains("REDACTION-TEST-SENTINEL"),"Forbidden-field failure does not echo value"); }
var store = new AccessWorkflowStore(root);
(string Ref, byte[] Bytes, string UploadId) Upload(JsonObject p)
{
    byte[] bytes=Encoding.UTF8.GetBytes(p.ToJsonString()+"\n");var u=new ArtifactUploadStore(root);var opened=u.Begin("public.json",bytes.Length,Hash(bytes));string uid=opened.UploadId;
    for(int offset=0;offset<bytes.Length;offset+=65536)u.Write(uid,offset,Convert.ToBase64String(bytes.AsSpan(offset,Math.Min(65536,bytes.Length-offset))));
    return (u.Commit(uid).ArtifactRef!,bytes,uid);
}
var upload=Upload(packet);var receipt=store.Review(id,upload.Ref);string record=(string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(id,upload.Ref)),"Idempotent immutable review");
Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(upload.Bytes),"Exact public input bytes retained");
new ArtifactUploadStore(root).Release(upload.UploadId);Check(JsonNode.DeepEquals(receipt,new AccessWorkflowStore(root).Page(record)),"Released upload and fresh store preserve review");
foreach(string section in new[]{"observations","assessments","changes"})
{
    var rows=new JsonArray();int offset=0;do{var page=store.Page(record,section,offset,1);foreach(var row in page["rows"]!.AsArray())rows.Add(row!["value"]!.DeepClone());if(page["nextOffset"] is null)break;offset=(int)page["nextOffset"]!;}while(true);
    Check(JsonNode.DeepEquals(rows,expected[section]),"Exact complete paging "+section);
}
foreach(var (section,offset,count) in new[]{("bad",0,1),("changes",999,1),("changes",-1,1),("changes",0,201)})Reject(()=>store.Page(record,section,offset,count),"Bad page refused","InvalidRequest");
foreach(string defect in new[]{"report","input-path","extra"})
{
    var envelope=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!;
    if(defect=="report")envelope["report"]!["effectiveRightsVerified"]=true;else if(defect=="extra")envelope["extra"]=true;else envelope["input"]!["path"]="other/file";
    byte[] data=JsonSerializer.SerializeToUtf8Bytes(envelope,BridgeProtocol.Json);File.WriteAllBytes(Path.Combine(root,"access-reviews",Hash(data)+".json"),data);
    Reject(()=>store.Page(Hash(data)),"Rehashed forgery "+defect,defect=="report"?"ArtifactChanged":"InvalidAccessReview");
}
string saved=Path.Combine(root,"project-reviews",id+".json");byte[] original=File.ReadAllBytes(saved);File.WriteAllText(saved,"changed");Reject(()=>store.Page(record),"Saved source corruption refused","ArtifactChanged");File.WriteAllBytes(saved,original);
foreach(string key in new[]{"input","reportArtifact"})
{ string path=Path.Combine(root,(string)receipt[key]!["path"]!);byte[] bytes=File.ReadAllBytes(path);File.WriteAllText(path,"tampered");Reject(()=>store.Page(record),"Corrupt retained "+key,"ArtifactChanged");File.WriteAllBytes(path,bytes); }
var wide=Mutate(p=>{var e=p["before"]!["entities"]!.AsArray();e[0]!["inherits"]=new JsonArray(Enumerable.Range(0,10).Select(i=>(JsonNode?)JsonValue.Create("wide"+i)).ToArray());for(int i=0;i<10;i++)e.Add(Entity("wide"+i,"role",new JsonArray(Enumerable.Range(0,64).Select(j=>(JsonNode?)Right(i+":"+j+new string('x',250))).ToArray())));});
var wideUpload=Upload(wide);var wideReceipt=store.Review(id,wideUpload.Ref);var widePage=store.Page((string)wideReceipt["recordId"]!,"assessments");
Check((bool)widePage["rows"]![0]!["omitted"]!&&JsonSerializer.SerializeToUtf8Bytes(widePage,BridgeProtocol.Json).Length<128*1024,"Oversized assessment explicitly omitted from bounded page");
string filler=Path.Combine(root,"access-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);var fresh=Upload(Mutate(p=>p["afterKind"]="observation"));Reject(()=>store.Review(id,fresh.Ref),"Incomplete files count toward retention limit","ArtifactCapacity");File.Delete(filler);
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new {artifactRoot=root,reviewId=id,packet},BridgeProtocol.Json));
RuntimeHandoffChecks.Run(root, id, record, packet, Check);
RuntimeHandoffWorkflowChecks.Run(root);
RuntimeCaptureHandoffChecks.Run(root);
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new {completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));
Console.WriteLine($"PASS access review: {checks.Count} checks; supplied public metadata only.");
