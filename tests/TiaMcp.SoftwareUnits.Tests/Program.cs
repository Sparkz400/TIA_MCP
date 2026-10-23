using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.SoftwareUnits;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidUnitReview")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
JsonObject J(string text) => JsonNode.Parse(text)!.AsObject();
const string plc = "/projects/Fixture/plc/A", a = plc + "/units/A", b = plc + "/units/B", c = plc + "/units/C";
const string source = a + "/blocks/Run", target = b + "/blocks/Value", far = c + "/blocks/Value", db = plc + "/blocks/Shared";
var nodes = new JsonArray();
void Node(string path, string kind, string name, string scope, string? subtype = null, string owner = plc)
{ nodes.Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = name, ["plcPath"] = owner, ["scopePath"] = scope, ["metadata"] = new JsonObject { ["kind"] = subtype } }); }
Node("/projects/Fixture", "project", "Fixture", ""); Node(plc, "plc", "A", plc);
foreach (var u in new[] { a, b, c }) Node(u, "softwareUnit", u.Split('/')[^1], u);
Node(source,"block","Run",a,"FC"); Node(target,"block","Value",b,"GlobalDB"); Node(far,"block","Value",c,"GlobalDB");
Node(db,"block","Shared",plc,"GlobalDB"); Node(a+"/blocks/Cycle","block","Cycle",a,"OB");
Node(a+"/groups/G","group","G",a); Node(plc+"/TO/Axis","technology","Axis",plc);
Node(plc+"/virtual","block","Virtual",a,"FC"); nodes[^1]!["origin"] = "virtual";
Node("/projects/Fixture/plc/Other/blocks/Value","block","Value",b,"GlobalDB","/projects/Fixture/plc/Other");
var reviews = new ProjectReviewStore(root);
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" }, ["nodes"] = nodes, ["edges"] = new JsonArray() };
string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var review = reviews.Read(id);
JsonObject Relation(string name, string? path, string type = "SoftwareUnit") => new() { ["relationType"] = type, ["relatedObject"] = name, ["targetPath"] = path };
JsonObject Dep(string path, string name) => new() { ["sourcePath"] = source, ["targetPath"] = path, ["targetName"] = name, ["reason"] = "Explicit supplied read 日本語 😀" };
var packet = new JsonObject { ["schemaVersion"] = "software-unit-review-request.v1", ["reviewId"] = id, ["plcPath"] = plc,
 ["coverage"] = J("""{"unitsComplete":true,"objectsComplete":false,"dependenciesComplete":false}"""),
 ["units"] = new JsonArray(new[]{a,b,c}.Select(u=>(JsonNode?)new JsonObject { ["objectPath"] = u, ["namespacePreset"] = "Shared", ["relationsComplete"] = true, ["relations"] = new JsonArray() }).ToArray()),
 ["objects"] = new JsonArray(new[]{source,target,far}.Select(p=>(JsonNode?)new JsonObject { ["objectPath"] = p, ["namespace"] = "Shared", ["publication"] = p==target?"Unpublished":"Published" }).ToArray()),
 ["dependencies"] = new JsonArray(Dep(target,"Value"),Dep(far,"Value"),Dep(db,"Shared")),
 ["changes"] = new JsonArray(new JsonObject { ["kind"]="setPublication",["objectPath"]=target,["value"]="Published" },
 new JsonObject { ["kind"]="setNamespacePreset",["unitPath"]=b,["value"]="Future" }) };
packet["units"]![0]!["relations"]!.AsArray().Add(Relation("B",b));
packet["units"]![1]!["relations"]!.AsArray().Add(Relation("C",c));
JsonObject Build(JsonObject? value = null, JsonObject? saved = null) => SoftwareUnitReview.Build(id,saved??review,value??packet);
string Status(JsonObject value, int index, string phase="after") => (string)value["dependencies"]![index]![phase]!["status"]!;
var result = Build();
Check(Status(result,0,"before")=="notPublished" && Status(result,0)=="declaredPrerequisitesPresent","Producer publication edit changes direct declared prerequisites");
Check(Status(result,1)=="missingDirectRelation","Transitive A-B-C never grants direct A-C access");
Check(Status(result,2)=="missingDirectRelation","Root DB requires exact NonUnitDB relation");
Check((int)result["summary"]!["collisionCount"]! == 1,"Identical qualified object names across units report ambiguity");
Check((string)result["units"]![1]!["afterNamespacePreset"]! == "Future" && (string)result["objects"]![1]!["namespace"]! == "Shared","Preset change never rewrites existing object namespace");
Check((int)result["summary"]!["omittedCapturedObjects"]! == 1,"Coverage counts omitted OB without counting foreign PLC nodes");
Check(!result["nativeExecutable"]!.GetValue<bool>() && !result["wholeProjectAnalysisComplete"]!.GetValue<bool>(),"No native/completeness claim");
JsonObject Mutate(Action<JsonObject> edit) { var copy=packet.DeepClone().AsObject();edit(copy);return copy; }
Check(Status(Build(Mutate(p=>p["units"]![0]!["relations"]![0]!["targetPath"]=null)),0)=="missingDirectRelation","Missing exact relation path never resolves by name");
Check(Status(Build(Mutate(p=>p["dependencies"]![0]!["targetPath"]=null)),0)=="targetAbsentOrUnresolved","Missing dependency path never resolves by name");
Check(Status(Build(Mutate(p=>p["dependencies"]![0]!["targetName"]="value")),0)=="targetAbsentOrUnresolved","Names stay case exact");
Check(Status(Build(Mutate(p=>p["units"]![0]!["relationsComplete"]=false)),1)=="relationCoverageUnknown","Missing relation under incomplete evidence remains unknown");
Check(Status(Build(Mutate(p=>{p["changes"]=new JsonArray();p["objects"]![1]!["publication"]="unknown";})),0)=="publicationUnknown","Unknown publication stays unknown");
Check(Status(Build(Mutate(p=>{p["dependencies"]![0]!["targetPath"]=a+"/groups/G";p["dependencies"]![0]!["targetName"]="G";})),0)=="unsupportedTarget","Same-unit group cannot become callable dependency");
Check(Status(Build(Mutate(p=>{p["dependencies"]![0]!["targetPath"]=source;p["dependencies"]![0]!["targetName"]="Run";})),0)=="declaredPrerequisitesPresent","Same-unit member needs no inter-unit publication");
var edits=Mutate(p=> {p["changes"]!.AsArray().Add(new JsonObject{["kind"]="addRelation",["unitPath"]=a,["relationType"]="NonUnitDB",["targetPath"]=db});p["changes"]!.AsArray().Add(new JsonObject{["kind"]="removeRelation",["unitPath"]=a,["relationType"]="SoftwareUnit",["relatedObject"]="B"});});
Check(Status(Build(edits),2)=="declaredPrerequisitesPresent" && Status(Build(edits),0)=="missingDirectRelation","Sequential add and removal recalculate affected prerequisites");
var cycle=Build(Mutate(p=>p["units"]![2]!["relations"]!.AsArray().Add(Relation("A",a))));
Check(cycle["cycles"]!.AsArray().Count==6,"Three cycle members reported in both phases");
foreach(var field in new[]{"reviewId","plcPath"}) Reject(()=>Build(Mutate(p=>p[field]="wrong")),"Reject wrong "+field);
Reject(()=>Build(Mutate(p=>p["extra"]=true)),"Reject unknown request field");
Reject(()=>Build(Mutate(p=>p["coverage"]!["unitsComplete"]="true")),"Reject nonboolean coverage");
Reject(()=>Build(Mutate(p=>p["coverage"]!["objectsComplete"]=true)),"Reject contradicted complete inventory");
Reject(()=>Build(Mutate(p=>p["units"]!.AsArray().RemoveAt(2))),"Reject complete units with omitted captured unit");
Reject(()=>Build(Mutate(p=>p["objects"]![0]!["objectPath"]=plc+"/virtual")),"Reject virtual source even with object path");
Reject(()=>Build(Mutate(p=>p["dependencies"]![0]!["targetPath"]="/projects/Fixture/plc/Other/blocks/Value")),"Reject foreign PLC target");
Reject(()=>Build(Mutate(p=>p["units"]!.AsArray().Add(p["units"]![0]!.DeepClone()))),"Reject duplicate unit");
Reject(()=>Build(Mutate(p=>p["units"]![0]!["relations"]!.AsArray().Add(Relation("B",b)))),"Reject duplicate relation");
Reject(()=>Build(Mutate(p=>p["changes"]!.AsArray().Add(new JsonObject{["kind"]="addRelation",["unitPath"]=a,["relationType"]="SoftwareUnit",["targetPath"]=a}))),"Reject self relation edit");
Reject(()=>Build(Mutate(p=>p["changes"]!.AsArray().Add(new JsonObject{["kind"]="move",["unitPath"]=a}))),"Reject unsupported move");
foreach(string flag in new[]{"generated","isSafety","isKnowHowProtected"}) {var copy=review.DeepClone().AsObject();copy["model"]!["nodes"]!.AsArray().Single(n=>(string?)n!["objectPath"]==target)!["metadata"]![flag]=true;Reject(()=>Build(saved:copy),"Reject known restricted publication "+flag);}
foreach(string field in new[]{"namespace","namespacePreset"}) { var copy=review.DeepClone().AsObject();copy["model"]!["nodes"]!.AsArray().Single(n=>(string?)n!["objectPath"]==(field=="namespace"?target:b))!["metadata"]![field]="Different";Reject(()=>Build(saved:copy),"Reject contradictory "+field); }
Reject(()=>SoftwareUnitReview.Parse(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")),"Reject duplicate JSON fields");
Reject(()=>SoftwareUnitReview.Parse(new byte[SoftwareUnitReview.InputLimit+1]),"Reject overlarge input");
try {SoftwareUnitReview.Parse([0xff]);throw new Exception("Invalid UTF8 accepted");} catch(DecoderFallbackException) { Check(true,"Invalid UTF8 refuses"); }
Reject(()=>Build(Mutate(p=>p["dependencies"]![0]!["reason"]="bad\ntext")),"Reject control text");
Reject(()=>Build(Mutate(p=>p["dependencies"]=new JsonArray(Enumerable.Range(0,4097).Select(_=>(JsonNode?)Dep(target,"Value")).ToArray()))),"Reject dependency overflow");
Check(Status(Build(Mutate(p=>{p["coverage"]!["unitsComplete"]=false;p["units"]!.AsArray().RemoveAt(2);p["objects"]!.AsArray().RemoveAt(2);})),1)=="targetUnitNotSupplied","Missing supplied target unit remains unresolved");
var ob=Mutate(p=>{p["objects"]!.AsArray().Add(new JsonObject{["objectPath"]=a+"/blocks/Cycle",["namespace"]="",["publication"]="notApplicable"});p["changes"]![0]!["objectPath"]=a+"/blocks/Cycle";});
Reject(()=>Build(ob),"OB publication edit refused");
var safety=review.DeepClone().AsObject();safety["model"]!["nodes"]!.AsArray().Single(n=>(string?)n!["objectPath"]==a)!["kind"]="safetyUnit";Reject(()=>Build(saved:safety),"Safety units not substituted for standard units");
var to=Mutate(p=>{p["dependencies"]!.AsArray().Add(Dep(plc+"/TO/Axis","Axis"));p["changes"]!.AsArray().Add(new JsonObject{["kind"]="addRelation",["unitPath"]=a,["relationType"]="TODB",["targetPath"]=plc+"/TO/Axis"});});
Check(Status(Build(to),3)=="declaredPrerequisitesPresent","Exact TO relation prerequisite supported without runtime claim");
Check(JsonNode.DeepEquals(result,Build()),"Reviewing never mutates supplied input or saved model");
var uploads=new ArtifactUploadStore(root);
(string UploadId,string Ref,byte[] Bytes) Upload(JsonObject p) {byte[] bytes=Encoding.UTF8.GetBytes(p.ToJsonString()+"\n");var upload=uploads.Begin("units.json",bytes.Length,Hash(bytes));for(int i=0;i<bytes.Length;i+=upload.MaxChunkBytes) uploads.Write(upload.UploadId,i,Convert.ToBase64String(bytes,i,Math.Min(upload.MaxChunkBytes,bytes.Length-i)));return(upload.UploadId,uploads.Commit(upload.UploadId).ArtifactRef!,bytes);}
var input=Upload(packet);var store=new SoftwareUnitWorkflowStore(root);var receipt=store.Review(id,input.Ref);string record=(string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(id,input.Ref)),"Idempotent receipt");
Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(input.Bytes),"Original bytes including newline preserved");
Check(JsonNode.DeepEquals(store.Page(record,"dependencies",0,1)["rows"]![0]!["value"],result["dependencies"]![0]),"Paged row equals complete derived report");
Reject(()=>store.Page(record,"dependencies",4),"Reject out-of-bounds page","InvalidRequest");
Reject(()=>store.Page(record,"changes",0,201),"Reject oversized page","InvalidRequest");
uploads.Release(input.UploadId);Check(JsonNode.DeepEquals(receipt,new SoftwareUnitWorkflowStore(root).Page(record)),"Reopen after upload release");
string reportPath=Path.Combine(root,(string)receipt["reportArtifact"]!["path"]!);byte[] original=File.ReadAllBytes(reportPath);File.WriteAllText(reportPath,"tampered");Reject(()=>store.Page(record),"Reject retained report corruption","ArtifactChanged");File.WriteAllBytes(reportPath,original);
var forged=JsonNode.Parse(original)!.AsObject();forged["report"]!["summary"]!["dependencyCount"]=0;byte[] forgedBytes=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);string forgedId=Hash(forgedBytes);File.WriteAllBytes(Path.Combine(root,"software-unit-reviews",forgedId+".json"),forgedBytes);Reject(()=>store.Page(forgedId),"Reject rehashed invented derived report","ArtifactChanged");
var large=Mutate(p=>p["dependencies"]=new JsonArray(Enumerable.Range(0,4096).Select(_=>(JsonNode?)Dep(target,"Value")).ToArray()));var largeUpload=Upload(large);var largeReceipt=store.Review(id,largeUpload.Ref);var page=store.Page((string)largeReceipt["recordId"]!,"dependencies",0,200);Check(JsonSerializer.SerializeToUtf8Bytes(page,BridgeProtocol.Json).Length<=128*1024 && page["nextOffset"] is not null,"Maximum dependency fixture pages within byte budget");
// Incomplete publication bytes count against retention and never become a receipt.
string filler=Path.Combine(root,"software-unit-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);
var different=Upload(Mutate(p=>p["changes"]=new JsonArray()));Reject(()=>store.Review(id,different.Ref),"Reject store capacity including leftovers","ArtifactCapacity");File.Delete(filler);
var wideModel=model.DeepClone().AsObject();var wide=Mutate(p=>{p["coverage"]!["unitsComplete"]=false;p["objects"]=new JsonArray();p["dependencies"]=new JsonArray();p["changes"]=new JsonArray();});
for(int i=0;i<150;i++) {string path=b+"/blocks/"+new string('x',1000)+i;wideModel["nodes"]!.AsArray().Add(new JsonObject{["nodeId"]=path,["objectPath"]=path,["kind"]="block",["name"]="Duplicate",["plcPath"]=plc,["scopePath"]=b,["metadata"]=new JsonObject{["kind"]="FC"}});wide["objects"]!.AsArray().Add(new JsonObject{["objectPath"]=path,["namespace"]="Shared",["publication"]="Published"});}
string wideId=(string)reviews.Save(wideModel,new(),new(){["syntheticFixture"]=true})["reviewId"]!;wide["reviewId"]=wideId;
var wideUpload=Upload(wide);var wideReceipt=store.Review(wideId,wideUpload.Ref);var widePage=store.Page((string)wideReceipt["recordId"]!,"collisions");
Check(widePage["rows"]![0]!["omitted"]!.GetValue<bool>() && widePage["nextOffset"] is null,"Oversized collision row explicitly omitted with complete artifact available");
var wideRecord=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,wideReceipt["reportArtifact"]!.AsObject()))!;
Check(wideRecord["report"]!["collisions"]![0]!["objectPaths"]!.AsArray().Count==150,"Complete report preserves every oversized-row identity");
string savedPath=Path.Combine(root,"project-reviews",id+".json");byte[] savedBytes=File.ReadAllBytes(savedPath);File.WriteAllText(savedPath,"tampered");Reject(()=>store.Page(record),"Saved source corruption refuses reopen","ArtifactChanged");File.WriteAllBytes(savedPath,savedBytes);
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new {artifactRoot=root,reviewId=id,packet},BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new {completed=true,nativeExecuted=false,nativeQualified=false,count=checks.Count,checks},BridgeProtocol.Json));
Console.WriteLine($"PASS software units: {checks.Count} checks; synthetic evidence only.");
