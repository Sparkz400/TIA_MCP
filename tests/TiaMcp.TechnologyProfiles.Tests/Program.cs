using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.TechnologyObjects;

if(args.Length!=1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks=new List<string>();
void Check(bool ok,string label) { if(!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action action,string label,string code="InvalidTechnologyProfile")
{ try {action();} catch(BridgeException e) when(e.Code==code) {Check(true,label);return;} throw new Exception("Expected refusal: "+label); }
string Hash(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
const string project="/projects/Fixture", plc=project+"/PLC", technology=plc+"/technology/Axis日本😀", peer=plc+"/technology/Peer", hardware=plc+"/hardware/Drive";
JsonObject Node(string path,string kind,string? parent)=>new(){["nodeId"]=path,["objectPath"]=path,["kind"]=kind,["name"]=path.Split('/').Last(),["parentId"]=parent,["origin"]="native",["plcPath"]=plc,["metadata"]=new JsonObject()};
var nodes=new JsonArray(Node(project,"project",null),Node(plc,"plc",project),Node(technology,"technologyObject",plc),Node(peer,"technologyObject",plc),Node(hardware,"hardware",plc));
nodes[2]!["metadata"]=new JsonObject{["systemLibraryType"]="TO_PositioningAxis",["systemLibraryVersion"]="10.0"};
var model=new JsonObject{["schemaVersion"]="project-model.v1",["project"]=new JsonObject{["projectPath"]="synthetic.ap21",["host"]="synthetic"},["nodes"]=nodes,
 ["edges"]=new JsonArray(new JsonObject{["source"]=new JsonObject{["nodeId"]=technology,["objectPath"]=technology},["target"]=new JsonObject{["nodeId"]=hardware,["objectPath"]=hardware},["kind"]="suppliedFixtureEdge",["evidence"]=new JsonObject{["origin"]="parsedSupported",["resolution"]="resolved"}})};
var reviews=new ProjectReviewStore(root);string id=(string)reviews.Save(model,new(),new(){["syntheticFixture"]=true})["reviewId"]!;var review=reviews.Read(id);
JsonObject Value(string text)=>new(){["kind"]="scalar",["text"]=text};
JsonObject Parameter(string name,string access="unknown")=>new(){["name"]=name,["nativeType"]="System.Double",["access"]=access,["value"]=Value("0"),["metadata"]=new JsonObject{["unit"]="supplied",["text"]="日本語 😀\nopaque"}};
JsonObject Relation(string name,string source,string? target,string kind="coupling")=>new(){["id"]=name,["kind"]=kind,["sourcePath"]=source,["targetPath"]=target,["targetMember"]=null,["state"]=target is null?"disconnected":"declared",["reason"]=null,["metadata"]=new JsonObject()};
var before=new JsonObject{["identity"]=new JsonObject{["plcPath"]=plc,["orderNumber"]="supplied-CPU",["firmware"]="supplied-FW",["technologyType"]="TO_PositioningAxis",["technologyVersion"]="10.0",["options"]=new JsonArray("supplied-option"),["unknownReason"]=null},
 ["valueDomain"]="offline",["parametersComplete"]=false,["relationshipsComplete"]=false,["parameters"]=new JsonArray(Parameter("Scale","readOnly"),Parameter("Mode"),Parameter("Peer")),
 ["relationships"]=new JsonArray(Relation("actor",technology,hardware,"hardware"),Relation("leading",technology,peer),Relation("back",peer,technology),Relation("missing",technology,"/unknown","mapping"),Relation("off",technology,null,"hardware"))};
before["relationships"]![3]!["targetMember"]="DB.Member";
var after=before.DeepClone().AsObject();after["parameters"]![0]!["value"]=Value("1");after["parameters"]![0]!["access"]="readWrite";after["parameters"]![1]!["value"]=Value("9007199254740993");after["relationships"]![0]!["metadata"]!["addressBits"]=16;
var packet=new JsonObject{["schemaVersion"]="technology-profile-review-request.v1",["reviewId"]=id,["technologyPath"]=technology,["afterKind"]="proposal",["before"]=before,["after"]=after};
JsonObject Mutate(Action<JsonObject> edit){var p=packet.DeepClone().AsObject();edit(p);return p;}
JsonObject Build(JsonObject? p=null,JsonObject? r=null)=>TechnologyProfileReview.Build(id,r??review,p??packet);
var result=Build();
Check((int)result["summary"]!["changedCount"]! ==3,"Parameters and declared relationship differences are retained");
Check((int)result["summary"]!["readOnlyConflictCount"]! ==1,"After access promotion cannot erase before read-only conflict");
Check((int)result["summary"]!["unknownAccessChangeCount"]! ==1,"Unknown access never authorizes a proposal");
Check((int)result["summary"]!["declaredCycleRecordCount"]! ==4,"Both cycle edges in both supplied phases are flagged");
Check((int)result["summary"]!["unresolvedEndpointCount"]! ==2,"Missing exact targets remain unresolved in both phases");
Check((string)result["relationships"]![3]!["memberResolution"]! =="notVerified","Captured or supplied path cannot certify a DB member");
Check((string)result["relationships"]![4]!["targetResolution"]! =="declaredDisconnected","Explicit disconnected state remains distinct from unknown");
Check(result["context"]!["selectionAndAncestors"]!.AsArray().Count==3 && result["context"]!["directCapturedEdges"]!.AsArray().Count==1,"Saved graph handoff preserves exact ancestry and direct edge");
foreach(string flag in new[]{"nativeExecutable","nativeQualified","nativeExecuted","targetCompatibilityVerified","sourceAuthenticityVerified","liveFreshnessVerified","wholeObjectAnalysisComplete"})Check(!(bool)result[flag]!,"Supplied evidence cannot promote "+flag);
Check((string)result["observations"]![4]!["parameter"]!["value"]!["text"]! =="9007199254740993","Beyond-double input stays exact text");
Check(JsonNode.DeepEquals(packet["before"],before),"Building does not modify input");
var observed=Build(Mutate(p=>{p["afterKind"]="observation";p["after"]!["valueDomain"]="online";}));
Check((int)observed["summary"]!["readOnlyConflictCount"]! ==0 && (string)observed["afterValueDomain"]! =="online","Observation domains do not imply native writes");
var missing=Build(Mutate(p=>p["after"]!["parameters"]!.AsArray().RemoveAt(2)));
Check(missing["changes"]!.AsArray().Any(c=>(string?)c!["status"]=="onlyBefore"),"Absent peer does not become deletion approval");
var unicode=Build(Mutate(p=>{foreach(string phase in new[]{"before","after"})p[phase]!["parameters"]=new JsonArray(Parameter("é"),Parameter("e\u0301"));}));
Check((int)unicode["summary"]!["beforeParameterCount"]! ==2,"Composed and decomposed names remain distinct exact identities");
Check((string)result["technologyPath"]! ==technology,"Unicode selected technology path remains exact");
var reorder=Build(Mutate(p=>{var rows=p["after"]!["parameters"]!.AsArray();var first=rows[0];rows.RemoveAt(0);rows.Add(first);}));
Check(JsonNode.DeepEquals(result["changes"],reorder["changes"]),"Exact keys are independent of collection order");
Check((int)Build(Mutate(p=>p["before"]!["relationships"]![0]!["targetPath"]=hardware.ToLowerInvariant()))["summary"]!["unresolvedEndpointCount"]! ==3,"Case-distinct endpoints never resolve by name");
var unknown=Build(Mutate(p=>{p["before"]!["relationships"]![0]!["state"]="unknown";p["before"]!["relationships"]![0]!["targetPath"]=null;p["before"]!["relationships"]![0]!["reason"]="Not captured";}));
Check((string)unknown["relationships"]![0]!["targetResolution"]! =="unresolved","Unknown is not disconnected");
var huge=Build(Mutate(p=>{p["before"]!["parameters"]![2]!["metadata"]!["v"]=JsonNode.Parse("1e400");p["after"]!["parameters"]![2]!["metadata"]!["v"]=JsonNode.Parse("2e400");}));
Check((int)huge["summary"]!["changedCount"]! ==4,"Opaque numeric metadata never collapses to infinity");
var badEdge=review.DeepClone().AsObject();badEdge["model"]!["edges"]![0]!["source"]!["objectPath"]=peer;
Check(Build(r:badEdge)["context"]!["directCapturedEdges"]!.AsArray().Count==0,"Mismatched edge path cannot bind by node ID alone");
var ancestry=review.DeepClone().AsObject();ancestry["model"]!["nodes"]![2]!["parentId"]="missing";
Check((bool)Build(r:ancestry)["context"]!["ancestorTraversalStopped"]!,"Missing captured parent is explicit");
ancestry["model"]!["nodes"]![2]!["parentId"]=technology;Check((bool)Build(r:ancestry)["context"]!["ancestorTraversalStopped"]!,"Cyclic ancestry stops without inventing a root");
foreach(string field in new[]{"plcPath","orderNumber","firmware","technologyType","technologyVersion"})Reject(()=>Build(Mutate(p=>p["after"]!["identity"]![field]="other")),"Cross-profile substitution refused: "+field);
foreach(string field in new[]{"plcPath","technologyType","technologyVersion"})Reject(()=>Build(Mutate(p=>{foreach(string phase in new[]{"before","after"})p[phase]!["identity"]![field]="other";})),"Captured selection mismatch refused: "+field);
Reject(()=>Build(Mutate(p=>p["technologyPath"]=hardware)),"Wrong captured kind refused");
Reject(()=>Build(Mutate(p=>p["technologyPath"]=technology.ToLowerInvariant())),"Exact TO path required");
Reject(()=>Build(Mutate(p=>p["reviewId"]=new string('0',64))),"Wrong saved review refused");
Reject(()=>Build(Mutate(p=>p["before"]!["valueDomain"]="online")),"Online proposal refused");
Reject(()=>Build(Mutate(p=>p["nativeQualified"]=true)),"Unexpected field refused");
foreach(string array in new[]{"parameters","relationships"})Reject(()=>Build(Mutate(p=>p["before"]![array]!.AsArray().Add(p["before"]![array]![0]!.DeepClone()))),"Duplicate "+array+" refused");
foreach(string state in new[]{"disconnected","unknown","approved"})Reject(()=>Build(Mutate(p=>p["before"]!["relationships"]![0]!["state"]=state)),"Contradictory relationship state refused: "+state);
Reject(()=>Build(Mutate(p=>p["before"]!["relationships"]![0]!["targetPath"]=null)),"Declared target cannot be absent");
Reject(()=>Build(Mutate(p=>p["before"]!["parameters"]![0]!["name"]="bad\nname")),"Control-bearing exact identity refused");
Reject(()=>Build(Mutate(p=>p["before"]!["parametersComplete"]="true")),"Completeness must be explicit Boolean");
Reject(()=>Build(Mutate(p=>p["before"]!["parameters"]![0]!["value"]=null)),"Unreadable values need explicit reason");
Reject(()=>Build(Mutate(p=>{foreach(string phase in new[]{"before","after"})p[phase]!["identity"]!["firmware"]=null;})),"Unknown profile needs reason");
var unavailable=Build(Mutate(p=>p["before"]!["parameters"]![2]!["value"]=new JsonObject{["kind"]="unavailable",["reason"]="Not captured"}));
Check((string)unavailable["observations"]![2]!["parameter"]!["value"]!["kind"]! =="unavailable","Unknown value never becomes zero");
Reject(()=>TechnologyProfileReview.Parse(Encoding.UTF8.GetBytes("{\"x\":{\"a\":1,\"a\":2}}")),"Duplicate nested fields refused");
Reject(()=>TechnologyProfileReview.Parse(new byte[TechnologyProfileReview.InputLimit+1]),"Input byte bound enforced");
foreach(var bad in new[]{new byte[]{255},Encoding.UTF8.GetBytes("{\"x\":\"\\ud800\"}"),Encoding.UTF8.GetBytes(new string('[',33)+new string(']',33))})
{bool refused=false;try{TechnologyProfileReview.Parse(bad);}catch(Exception e)when(e is ArgumentException or InvalidOperationException or JsonException){refused=true;}Check(refused,"Invalid UTF8/UTF16 or depth refused");}
var virtualReview=review.DeepClone().AsObject();virtualReview["model"]!["nodes"]![2]!["origin"]="virtual";Reject(()=>Build(r:virtualReview),"Virtual selection refused");
var proposed=Mutate(p=>{foreach(string phase in new[]{"before","after"})p[phase]!["parameters"]=new JsonArray(Enumerable.Range(0,501).Select(i=>{var row=Parameter("p"+i);row["value"]=Value(phase);return (JsonNode)row;}).ToArray());});
Reject(()=>Build(proposed),"Proposal record bound enforced");
proposed["afterKind"]="observation";Check((int)Build(proposed)["summary"]!["changedCount"]! ==502,"Large observed differences remain reviewable");
var uploads=new ArtifactUploadStore(root);
(string Id,string Ref,byte[] Bytes) Upload(JsonObject p){byte[] data=Encoding.UTF8.GetBytes(p.ToJsonString()+"\n");var u=uploads.Begin("technology-profile.json",data.Length,Hash(data));for(int i=0;i<data.Length;i+=u.MaxChunkBytes)uploads.Write(u.UploadId,i,Convert.ToBase64String(data,i,Math.Min(u.MaxChunkBytes,data.Length-i)));return(u.UploadId,uploads.Commit(u.UploadId).ArtifactRef!,data);}
var input=Upload(packet);var store=new TechnologyProfileWorkflowStore(root);var receipt=store.Review(id,input.Ref);string record=(string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(id,input.Ref)),"Idempotent retained receipt");
Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(input.Bytes),"Original input bytes retained");
foreach(string section in new[]{"observations","relationships","changes"})Check(store.Page(record,section,0,1)["nextOffset"] is not null,"Bounded "+section+" paging");
Check(store.Page(record,"context")["rows"]!.AsArray().Count==1,"Context available through bounded paging");
Reject(()=>store.Page(record,"changes",999),"Offset refused","InvalidRequest");Reject(()=>store.Page(record,"changes",0,201),"Count bound enforced","InvalidRequest");
uploads.Release(input.Id);Check(JsonNode.DeepEquals(receipt,new TechnologyProfileWorkflowStore(root).Page(record)),"Release and restart preserve record");
foreach(string key in new[]{"input","reportArtifact"}){string file=Path.Combine(root,(string)receipt[key]!["path"]!);byte[] original=File.ReadAllBytes(file);File.WriteAllText(file,"tampered");Reject(()=>store.Page(record),"Corrupted "+key+" refused","ArtifactChanged");File.WriteAllBytes(file,original);}
foreach(string defect in new[]{"report","input-path","extra"})
{var envelope=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!;
 if(defect=="report")envelope["report"]!["nativeExecuted"]=true;else if(defect=="extra")envelope["extra"]=true;else envelope["input"]!["path"]="other/file";
 byte[] data=JsonSerializer.SerializeToUtf8Bytes(envelope,BridgeProtocol.Json);string hash=Hash(data);File.WriteAllBytes(Path.Combine(root,"technology-profile-reviews",hash+".json"),data);
 Reject(()=>store.Page(hash),"Rehashed forged "+defect+" refused",defect=="report"?"ArtifactChanged":"InvalidTechnologyProfile");}
var wide=Upload(Mutate(p=>p["before"]!["parameters"]![0]!["metadata"]!["wide"]=new string('x',130*1024)));var wideReceipt=store.Review(id,wide.Ref);var page=store.Page((string)wideReceipt["recordId"]!,"observations");
Check((bool)page["rows"]![0]!["omitted"]!&&JsonSerializer.SerializeToUtf8Bytes(page,BridgeProtocol.Json).Length<128*1024,"Oversized rows omitted explicitly without truncating original");
var many=Mutate(p=>{p["afterKind"]="observation";foreach(string phase in new[]{"before","after"}){p[phase]!["parameters"]=new JsonArray(Enumerable.Range(0,4096).Select(i=>(JsonNode)Parameter("p"+i)).ToArray());p[phase]!["relationships"]=new JsonArray(Enumerable.Range(0,1024).Select(i=>(JsonNode)Relation("r"+i,technology,hardware)).ToArray());}});
var max=Upload(many);var maxReceipt=store.Review(id,max.Ref);Check((int)store.Page((string)maxReceipt["recordId"]!,"observations",8000,200)["total"]! ==8192,"Maximum parameter set paged");Check((int)store.Page((string)maxReceipt["recordId"]!,"relationships",2000,200)["total"]! ==2048,"Maximum relationship set paged");
foreach(var (field,count) in new[]{("parameters",4097),("relationships",1025)})Reject(()=>Build(Mutate(p=>p["before"]![field]=new JsonArray(Enumerable.Range(0,count).Select(i=>(JsonNode)(field=="parameters"?Parameter("p"+i):Relation("r"+i,technology,peer))).ToArray()))),"Bound enforced: "+field);
string saved=Path.Combine(root,"project-reviews",id+".json");byte[] savedBytes=File.ReadAllBytes(saved);File.WriteAllText(saved,"tampered");Reject(()=>store.Page(record),"Saved review corruption refused","ArtifactChanged");File.WriteAllBytes(saved,savedBytes);
string filler=Path.Combine(root,"technology-profile-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);var fresh=Upload(Mutate(p=>p["afterKind"]="observation"));Reject(()=>store.Review(id,fresh.Ref),"Incomplete files count toward capacity","ArtifactCapacity");File.Delete(filler);
// Independent binary/text source bundles retain roles after the original uploads are released.
(string Id,string Ref) RawUpload(string name,byte[] data){var u=uploads.Begin(name,data.Length,Hash(data));for(int i=0;i<data.Length;i+=u.MaxChunkBytes)uploads.Write(u.UploadId,i,Convert.ToBase64String(data,i,Math.Min(u.MaxChunkBytes,data.Length-i)));return(u.UploadId,uploads.Commit(u.UploadId).ArtifactRef!);}
JsonObject Declaration(string label,string name,string reference,byte[] data)=>new(){["label"]=label,["purpose"]="supplied baseline; no authenticity claim",["phase"]="before",["fileName"]=name,["uploadRef"]=reference,["sha256"]=Hash(data),["bytes"]=data.Length};
byte[] xmlBytes=Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<supplied>日本語\r\nopaque</supplied>")).ToArray();byte[] binary=[0,255,128,13,10,0];
var xmlUpload=RawUpload("source日本.xml",xmlBytes);var binaryUpload=RawUpload("source.bin",binary);
var bundle=packet.DeepClone().AsObject();bundle["schemaVersion"]="technology-profile-review-request.v2";bundle["sources"]=new JsonArray(Declaration("é", "source日本.xml",xmlUpload.Ref,xmlBytes),Declaration("e\u0301","source.bin",binaryUpload.Ref,binary));
var duplicateRole=bundle["sources"]![0]!.DeepClone().AsObject();duplicateRole["label"]="same bytes, different role";duplicateRole["purpose"]="separately declared comparison source";duplicateRole["phase"]="after";bundle["sources"]!.AsArray().Add(duplicateRole);
var bundledInput=Upload(bundle);var bundled=store.Review(id,bundledInput.Ref);string bundledId=(string)bundled["recordId"]!;
var sourcePage=store.Page(bundledId,"sources",0,200);var sourceRows=sourcePage["rows"]!.AsArray().Select(r=>r!["value"]!.AsObject()).ToArray();
Check(sourceRows.Length==3,"Independent source labels and phases remain distinct");
Check(JsonNode.DeepEquals(sourceRows[0]["artifact"],sourceRows[2]["artifact"])&&!JsonNode.DeepEquals(sourceRows[0],sourceRows[2]),"Identical bytes retain separate declared purposes without duplicate storage");
Check(ProjectReviewStore.ReadArtifact(root,sourceRows[0]["artifact"]!.AsObject()).SequenceEqual(xmlBytes),"UTF16 BOM CRLF and non-ASCII source bytes retained exactly");
Check(ProjectReviewStore.ReadArtifact(root,sourceRows[1]["artifact"]!.AsObject()).SequenceEqual(binary),"Binary source bytes retained exactly");
Check(JsonNode.DeepEquals(bundled,store.Review(id,bundledInput.Ref)),"Source bundle intake is idempotent while uploads remain committed");
Check((int)store.Page(record,"sources")["total"]! ==0 && JsonNode.DeepEquals(receipt,store.Page(record)),"Legacy receipt is unchanged and has no inferred sources");
string[] OwnedFiles()=>Directory.EnumerateFiles(Path.Combine(root,"technology-profile-reviews")).Select(f=>Path.GetFileName(f)+":"+Hash(File.ReadAllBytes(f))).Order().ToArray();
void RefuseBundle(Action<JsonObject> change,string label,string code="InvalidTechnologyProfile")
{var bad=bundle.DeepClone().AsObject();change(bad);var incoming=Upload(bad);var existing=OwnedFiles();Reject(()=>store.Review(id,incoming.Ref),label,code);Check(existing.SequenceEqual(OwnedFiles()),label+" preserves all workflow files");}
RefuseBundle(p=>p["sources"]!.AsArray().Add(p["sources"]![0]!.DeepClone()),"Duplicate source label refused");
RefuseBundle(p=>p["sources"]![0]!["fileName"]="wrong.xml","Source filename substitution refused");
RefuseBundle(p=>p["sources"]![0]!["sha256"]=new string('0',64),"Source digest substitution refused");
RefuseBundle(p=>p["sources"]![0]!["bytes"]=1,"Source size substitution refused");
RefuseBundle(p=>p["sources"]![0]!["bytes"]=1.5,"Fractional source size refused");
RefuseBundle(p=>p["sources"]![0]!["bytes"]=16*1024*1024+1,"Individual source bound enforced");
RefuseBundle(p=>p["sources"]=new JsonArray(),"Empty v2 bundle refused");
RefuseBundle(p=>p["sources"]![0]!["phase"]="verified","Unknown source phase refused");
RefuseBundle(p=>p["sources"]![0]!["nativeQualified"]=true,"Extra source authority field refused");
RefuseBundle(p=>p["sources"]![0]!["uploadRef"]="file:source.bin","Non-upload source reference refused");
RefuseBundle(p=>p["sources"]=new JsonArray(Enumerable.Range(0,17).Select(i=>{var row=duplicateRole.DeepClone();row["label"]="label"+i;return row;}).ToArray()),"Source count bound enforced");
RefuseBundle(p=>p["sources"]=new JsonArray(Enumerable.Range(0,5).Select(i=>{var row=duplicateRole.DeepClone();row["label"]="label"+i;row["bytes"]=16*1024*1024;return row;}).ToArray()),"Aggregate source bytes bounded including repeated roles");
RefuseBundle(p=>p["schemaVersion"]="technology-profile-review-request.v1","Legacy schema cannot carry unbound source fields");
// A bad later source must not publish an otherwise valid first source.
byte[] firstFresh=[1,8,9];var firstUpload=RawUpload("first.bin",firstFresh);
RefuseBundle(p=>{p["sources"]![0]=Declaration("fresh","first.bin",firstUpload.Ref,firstFresh);p["sources"]![1]!["sha256"]=new string('0',64);},"Validate all source captures before first publication");
Check(!File.Exists(Path.Combine(root,"technology-profile-reviews",Hash(firstFresh)+".source")),"Rejected later source did not publish earlier bytes");
foreach(var sourceRow in sourceRows.Take(2)){string file=Path.Combine(root,(string)sourceRow["artifact"]!["path"]!);byte[] original=File.ReadAllBytes(file);File.WriteAllBytes(file,[99]);Reject(()=>store.Page(bundledId),"Changed owned source refuses summary reopen","ArtifactChanged");Reject(()=>store.Review(id,bundledInput.Ref),"Intake never silently repairs a corrupted owned source","ArtifactChanged");Check(File.ReadAllBytes(file).SequenceEqual(new byte[]{99}),"Refusal preserves corrupted bytes for explicit recovery");File.WriteAllBytes(file,original);}
var originalEnvelope=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,bundled["reportArtifact"]!.AsObject()))!;
foreach(string defect in new[]{"purpose","reference","version"})
{var forged=originalEnvelope.DeepClone();if(defect=="version")forged["schemaVersion"]="technology-profile-review-record.v1";else if(defect=="purpose")forged["report"]!["sources"]![0]!["purpose"]="certified";else forged["report"]!["sources"]![0]!["artifact"]=sourceRows[1]["artifact"]!.DeepClone();byte[] data=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);string hash=Hash(data);File.WriteAllBytes(Path.Combine(root,"technology-profile-reviews",hash+".json"),data);Reject(()=>store.Page(hash),"Rehashed source "+defect+" forgery refused",defect=="version"?"InvalidTechnologyProfile":"ArtifactChanged");}
uploads.Release(xmlUpload.Id);uploads.Release(binaryUpload.Id);uploads.Release(bundledInput.Id);
Check(JsonNode.DeepEquals(bundled,new TechnologyProfileWorkflowStore(root).Page(bundledId)),"Bundle reopens after every original upload is released");
Check(ProjectReviewStore.ReadArtifact(root,store.Page(bundledId,"sources")["rows"]![0]!["value"]!["artifact"]!.AsObject()).SequenceEqual(xmlBytes),"Owned source remains downloadable after upload release");
// Capacity can interrupt publication between files; retained partial files are never a completed record.
byte[] secondFresh=[2,8,9,10];var secondUpload=RawUpload("second.bin",secondFresh);var partial=bundle.DeepClone().AsObject();partial["sources"]=new JsonArray(Declaration("first","first.bin",firstUpload.Ref,firstFresh),Declaration("second","second.bin",secondUpload.Ref,secondFresh));var partialInput=Upload(partial);
string storage=Path.Combine(root,"technology-profile-reviews");string[] recordsBefore=Directory.GetFiles(storage,"*.json").Order().ToArray();long existingLength=Directory.EnumerateFiles(storage).Sum(f=>new FileInfo(f).Length);
string obstruction=Path.Combine(storage,"interrupted-capacity.tmp");using(var f=File.Create(obstruction))f.SetLength(512L*1024*1024-existingLength-firstFresh.Length);
Reject(()=>store.Review(id,partialInput.Ref),"Mid-bundle capacity refusal","ArtifactCapacity");Check(File.Exists(Path.Combine(storage,Hash(firstFresh)+".source"))&&!File.Exists(Path.Combine(storage,Hash(secondFresh)+".source")),"Partial publication remains owned and bounded");Check(recordsBefore.SequenceEqual(Directory.GetFiles(storage,"*.json").Order()),"Interrupted source publication never produces a successful record");
File.Delete(obstruction);var resumed=new TechnologyProfileWorkflowStore(root).Review(id,partialInput.Ref);Check((int)store.Page((string)resumed["recordId"]!,"sources")["total"]! ==2,"Explicit retry verifies retained partial source and completes independently");
var sixteen=partial.DeepClone().AsObject();sixteen["sources"]=new JsonArray(Enumerable.Range(0,16).Select(i=>(JsonNode)Declaration("role"+i,"first.bin",firstUpload.Ref,firstFresh)).ToArray());var sixteenInput=Upload(sixteen);var sixteenReceipt=store.Review(id,sixteenInput.Ref);Check((int)store.Page((string)sixteenReceipt["recordId"]!,"sources")["total"]! ==16,"Maximum source count retains every distinct role");
byte[] maximumSource=new byte[16*1024*1024];maximumSource[0]=255;maximumSource[^1]=128;var maximumUpload=RawUpload("maximum.bin",maximumSource);
var maximumBundle=partial.DeepClone().AsObject();maximumBundle["sources"]=new JsonArray(Enumerable.Range(0,4).Select(i=>(JsonNode)Declaration("max"+i,"maximum.bin",maximumUpload.Ref,maximumSource)).ToArray());var maximumInput=Upload(maximumBundle);var maximumReceipt=store.Review(id,maximumInput.Ref);uploads.Release(maximumUpload.Id);
Check((int)store.Page((string)maximumReceipt["recordId"]!,"sources")["total"]! ==4,"Exact 16 MiB individual and 64 MiB declared aggregate bounds pass after release");
Check(ProjectReviewStore.ReadArtifact(root,store.Page((string)maximumReceipt["recordId"]!,"sources")["rows"]![0]!["value"]!["artifact"]!.AsObject()).SequenceEqual(maximumSource),"Maximum binary source remains byte-exact");

// v3 supplied consumers never become captured graph edges.
string block=plc+"/blocks/Consumer", leaf=plc+"/blocks/Caller";
var consumerModel=model.DeepClone().AsObject();consumerModel["nodes"]!.AsArray().Add(Node(block,"block",plc));consumerModel["nodes"]!.AsArray().Add(Node(leaf,"block",plc));
JsonObject Edge(string from,string to,string kind="dependsOn")=>new(){["source"]=new JsonObject{["nodeId"]=from,["objectPath"]=from},["target"]=new JsonObject{["nodeId"]=to,["objectPath"]=to},["kind"]=kind,["evidence"]=new JsonObject{["origin"]="parsedSupported",["resolution"]="resolved"}};
consumerModel["edges"]=new JsonArray(Edge(peer,technology),Edge(block,peer),Edge(leaf,block),Edge(hardware,technology),Edge(leaf,hardware),Edge(technology,leaf),Edge(leaf,technology,"unsupported"));
string consumerReviewId=(string)reviews.Save(consumerModel,new(),new(){["syntheticFixture"]=true})["reviewId"]!;var consumerReview=reviews.Read(consumerReviewId);
JsonObject Consumer(string name,string target=peer,string kind="parameter",string key="Scale")=>new(){["id"]=name,["subjectKind"]=kind,["subjectKey"]=key,["objectPath"]=target,["nodeId"]=target,["member"]="DB.Value",["state"]="declared",["reason"]=null};
var consumerPacket=packet.DeepClone().AsObject();consumerPacket["schemaVersion"]="technology-profile-review-request.v3";consumerPacket["reviewId"]=consumerReviewId;consumerPacket["sources"]=new JsonArray();
consumerPacket["consumers"]=new JsonObject{["before"]=new JsonArray(Consumer("é"),Consumer("e\u0301",hardware,"relationship","actor")),["after"]=new JsonArray(Consumer("é",block),Consumer("new",peer,"relationship","leading"))};
consumerPacket["impactLimits"]=new JsonObject{["impactDepth"]=3,["maxImpactPaths"]=100,["maxImpactEdgeVisits"]=5000};
JsonObject ConsumerReport(Action<JsonObject>? edit=null,JsonObject? saved=null)
{
 var request=consumerPacket.DeepClone().AsObject();edit?.Invoke(request);var baseline=request.DeepClone().AsObject();baseline.Remove("consumers");baseline.Remove("impactLimits");baseline.Remove("sources");baseline["schemaVersion"]="technology-profile-review-request.v1";
 var report=TechnologyProfileReview.Build(consumerReviewId,saved??consumerReview,baseline);TechnologyProfileConsumers.Add(report,saved??consumerReview,request);return report;
}
var consumers=ConsumerReport();
Check(consumers["consumers"]!.AsArray().Count==4,"Both phases retain separate Unicode consumer IDs");
Check(consumers["consumerChanges"]!.AsArray().Any(c=>(string?)c!["id"]=="é" && (string?)c["status"]=="changed"),"Same consumer ID preserves changed object association");
Check(consumers["consumerChanges"]!.AsArray().Any(c=>(string?)c!["status"]=="onlyBefore")&&consumers["consumerChanges"]!.AsArray().Any(c=>(string?)c!["status"]=="onlyAfter"),"Missing supplied rows remain only-before/after");
Check(consumers["consumers"]!.AsArray().All(c=>(string?)c!["objectResolution"]=="capturedObject" && (string?)c["memberResolution"]=="notVerified" && !(bool)c["nativeLinkVerified"]!),"Exact captured objects never certify declared native member/link");
Check(consumers["impactPaths"]!.AsArray().Any(p=>(string?)p!["changedObjectPath"]==technology && (int)p["distance"]! ==3),"Existing traversal retains transitive incoming saved paths");
Check(consumers["impactPaths"]!.AsArray().All(p=>p!["objectPaths"]!.AsArray().Select(x=>(string)x!).Distinct(StringComparer.Ordinal).Count()==p["objectPaths"]!.AsArray().Count),"Cycles never repeat an object in a saved path");
Check(consumers["impactPaths"]!.AsArray().Count(p=>(string?)p!["changedObjectPath"]==technology && (string?)p["affectedObjectPath"]==leaf && (string?)p["reviewSide"]=="before")==2,"Diamond retains distinct dependency paths");
Check(!(bool)consumers["impactTraversal"]!["complete"]! && (string)consumers["impactPaths"]![0]!["change"]! =="suppliedContextRoot","Context roots are explicitly not actual native changes or complete impact");
Check((int)consumers["impactTraversal"]!["excludedEdges"]![0]!["counts"]!["unsupportedRelationshipKind"]! ==1,"Unsupported captured edges remain counted as exclusions");
foreach(var (edit,expected) in new (Action<JsonObject>,string)[]{
 (r=>{r["objectPath"]="/missing";r["nodeId"]="/missing";},"missing"),
 (r=>r["nodeId"]=hardware,"identityMismatch"),
 (r=>{r["objectPath"]="/missing";},"identityMismatch"),
 (r=>{r["objectPath"]=peer.ToLowerInvariant();r["nodeId"]=peer.ToLowerInvariant();},"missing"),
 (r=>{r["state"]="unknown";r["reason"]="No capture";r["objectPath"]=null;r["nodeId"]=null;},"notDeclared")})
 Check((string)ConsumerReport(p=>edit(p["consumers"]!["before"]![0]!.AsObject()))["consumers"]![0]!["objectResolution"]! ==expected,"Consumer resolution stays explicit: "+expected);
foreach(string defect in new[]{"scope","unknownScope","virtual","duplicatePath","duplicateId"})
{
 var changedReview=consumerReview.DeepClone().AsObject();var ns=changedReview["model"]!["nodes"]!.AsArray();
 if(defect=="scope")ns[3]!["plcPath"]="/another/PLC";else if(defect=="unknownScope")ns[3]!["plcPath"]=null;else if(defect=="virtual")ns[3]!["origin"]="virtual";
 else {var copy=ns[3]!.DeepClone();copy[defect=="duplicatePath"?"nodeId":"objectPath"]="/other";ns.Add(copy);}
 var report=ConsumerReport(saved:changedReview);string expected=defect.StartsWith("duplicate")?"ambiguous":defect=="unknownScope"?"scopeUnknown":"outOfScope";
 Check((string)report["consumers"]![0]!["objectResolution"]! ==expected,"Saved consumer "+defect+" is not silently selected");
 Check(report["impactPaths"]!.AsArray().All(p=>!p!["objectPaths"]!.AsArray().Any(x=>(string?)x==peer)),"Excluded consumer cannot be traversed: "+defect);
}
Check((string)ConsumerReport(p=>p["consumers"]!["before"]![0]!["subjectKey"]="not supplied")["consumers"]![0]!["subjectResolution"]! =="missingDeclaration","Broken parameter association stays unresolved");
foreach(string bound in new[]{"impactDepth","maxImpactPaths","maxImpactEdgeVisits"})
{
 var report=ConsumerReport(p=>p["impactLimits"]![bound]=1);
 Check((bool)report["impactTraversal"]!["truncated"]!&&report["impactTraversal"]!["truncationReasons"]!.AsArray().Any(x=>(string?)x==bound),"Saved traversal exposes "+bound+" bound");
 foreach(int invalid in new[]{0,bound=="impactDepth"?6:bound=="maxImpactPaths"?201:20001}) Reject(()=>ConsumerReport(p=>p["impactLimits"]![bound]=invalid),"Reject invalid "+bound+"="+invalid);
}
foreach(string defect in new[]{"duplicate","extra","unknown","halfIdentity","kind","missingField","tooMany","fractionalLimit"})
 Reject(()=>ConsumerReport(p=>{
 var row=p["consumers"]!["before"]![0]!;
 if(defect=="duplicate")p["consumers"]!["before"]!.AsArray().Add(row.DeepClone());else if(defect=="extra")row["nativeQualified"]=true;else if(defect=="unknown")row["state"]="unknown";else if(defect=="halfIdentity")row["nodeId"]=null;else if(defect=="kind")row["subjectKind"]="nativeMember";else if(defect=="missingField")row.AsObject().Remove("member");else if(defect=="fractionalLimit")p["impactLimits"]!["impactDepth"]=1.5;else p["consumers"]!["before"]=new JsonArray(Enumerable.Range(0,257).Select(i=>(JsonNode)Consumer("c"+i)).ToArray());
 }),"Consumer boundary refuses "+defect);
var maximumConsumers=ConsumerReport(p=>{foreach(string phase in new[]{"before","after"})p["consumers"]![phase]=new JsonArray(Enumerable.Range(0,256).Select(i=>(JsonNode)Consumer("c"+i)).ToArray());});
Check(maximumConsumers["consumers"]!.AsArray().Count==512 && (int)maximumConsumers["impactTraversal"]!["rootCount"]! ==2,"Maximum declarations retain all rows without duplicating graph roots");

foreach(string mode in new[]{"unknown","missingSubject","empty"})
{
 var report=ConsumerReport(p=>{foreach(string phase in new[]{"before","after"}){var rows=p["consumers"]![phase]!.AsArray();if(mode=="empty")rows.Clear();else foreach(var row in rows){if(mode=="unknown"){row!["state"]="unknown";row["reason"]="not established";}else row!["subjectKey"]="absent";}}});
 Check((int)report["impactTraversal"]!["rootCount"]! ==1,"Only selected TO is an impact root for "+mode+" consumers");
}
var noEdges=consumerReview.DeepClone().AsObject();noEdges["model"]!["edges"]=new JsonArray();
Check(ConsumerReport(saved:noEdges)["impactPaths"]!.AsArray().Count==0,"Supplied consumer declarations never invent graph edges");
var badEvidence=consumerReview.DeepClone().AsObject();foreach(var edge in badEvidence["model"]!["edges"]!.AsArray())edge!["evidence"]!["resolution"]="unknown";
var unsupportedReport=ConsumerReport(saved:badEvidence);
Check(unsupportedReport["impactPaths"]!.AsArray().Count==0 && (int)unsupportedReport["impactTraversal"]!["excludedEdges"]![0]!["counts"]!["unresolvedOrUnsupportedEvidence"]! ==6,"Unresolved captured evidence is excluded and counted");
var largeEdges=consumerReview.DeepClone().AsObject();foreach(var edge in largeEdges["model"]!["edges"]!.AsArray())edge!["metadata"]=new JsonObject{["opaque"]=new string('X',1024*1024)};
var byteBound=ConsumerReport(saved:largeEdges);
Check(byteBound["impactPaths"]!.AsArray().Count==0 && byteBound["impactTraversal"]!["truncationReasons"]!.AsArray().Any(x=>(string?)x=="maxImpactPathBytes"),"Saved path byte bound reports an omitted oversized first path");
var inputBefore=consumerPacket.ToJsonString();var savedBefore=consumerReview.ToJsonString();ConsumerReport();
Check(consumerPacket.ToJsonString()==inputBefore && consumerReview.ToJsonString()==savedBefore,"Consumer review never changes supplied request or saved model");

var ambiguousMany=consumerReview.DeepClone().AsObject();var ambiguousNodes=ambiguousMany["model"]!["nodes"]!.AsArray();
for(int i=0;i<10000;i++){var node=Node(plc+"/blocks/duplicate"+i,"block",plc);node["nodeId"]=peer;ambiguousNodes.Add(node);}
var manyReport=ConsumerReport(saved:ambiguousMany);
Check((string)manyReport["consumers"]![0]!["objectResolution"]! =="ambiguous","Ten thousand duplicate saved IDs stay ambiguous without choosing one");
Check((int)manyReport["impactTraversal"]!["excludedNodeCount"]! ==10001,"Every ambiguous ID peer is excluded from saved traversal");
var packetUpload=Upload(consumerPacket);var consumerReceipt=store.Review(consumerReviewId,packetUpload.Ref);string consumerRecord=(string)consumerReceipt["recordId"]!;uploads.Release(packetUpload.Id);
Check(JsonNode.DeepEquals(consumerReceipt,new TechnologyProfileWorkflowStore(root).Page(consumerRecord)),"v3 survives input upload release and store restart");
foreach(string section in new[]{"consumers","consumerChanges","impactPaths","impactTraversal"})Check(store.Page(consumerRecord,section)["rows"]!.AsArray().Count>0,"v3 exposes "+section+" through existing paged tool");
Check((int)store.Page(consumerRecord,"sources")["total"]! ==0,"v3 consumer-only request needs no unrelated source file");
foreach(var old in new[]{record,bundledId})foreach(string section in new[]{"consumers","consumerChanges","impactPaths","impactTraversal"})Check((int)store.Page(old,section)["total"]! ==0,"Old records get no invented "+section);
Check(JsonNode.DeepEquals(receipt,store.Page(record))&&JsonNode.DeepEquals(bundled,store.Page(bundledId)),"Existing v1/v2 receipts remain identical");
var retainedV3=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,consumerReceipt["reportArtifact"]!.AsObject()))!;
foreach(string defect in new[]{"consumer","impact","authority","version"})
{
 var forged=retainedV3.DeepClone();if(defect=="consumer")forged["report"]!["consumers"]![0]!["objectResolution"]="missing";else if(defect=="impact")forged["report"]!["impactPaths"]=new JsonArray();else if(defect=="authority")forged["report"]!["impactTraversal"]!["complete"]=true;else forged["schemaVersion"]="technology-profile-review-record.v2";
 byte[] data=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);string hash=Hash(data);File.WriteAllBytes(Path.Combine(root,"technology-profile-reviews",hash+".json"),data);Reject(()=>store.Page(hash),"Rehashed v3 "+defect+" forgery refuses",defect=="version"?"InvalidTechnologyProfile":"ArtifactChanged");
}
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new{artifactRoot=root,reviewId=id,packet,consumerReviewId,consumerPacket},BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new{completed=true,nativeExecuted=false,count=checks.Count,checks},BridgeProtocol.Json));
Console.WriteLine($"PASS technology profiles: {checks.Count} checks; synthetic supplied observations only.");
EncoderTests.Run(root, review, packet);
