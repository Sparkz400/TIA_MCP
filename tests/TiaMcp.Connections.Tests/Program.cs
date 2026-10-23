using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Connections;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, string code = "InvalidConnectionReview")
{ try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
const string project = "/projects/Fixture", a = project + "/devices/A", b = project + "/devices/B", h = project + "/devices/HMI";
const string block = a + "/plc/blocks/Send", tag = h + "/hmi/tags/Value";
var nodes = new JsonArray();
void Node(string path, string kind, string? parent, string name = "Shared") => nodes.Add(new JsonObject {
    ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = name, ["parentId"] = parent, ["metadata"] = new JsonObject() });
Node(project, "project", null);
foreach (string owner in new[] { a, b, h }) { Node(owner, "hardware", project); Node(owner + "/interface", "hardware", owner); }
Node(block, "block", a); Node(tag, "hmiTag", h); Node(project + "/virtual", "hardware", a); nodes[^1]!["origin"] = "virtual";
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" }, ["nodes"] = nodes, ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root);
string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var review = reviews.Read(id);
JsonObject Endpoint(string owner) => new() { ["ownerPath"] = owner, ["interfacePath"] = owner + "/interface", ["reason"] = null };
JsonObject Connection(string key, string protocol, string origin = "engineering", string local = a, string partner = b) => new() {
    ["id"] = key, ["protocol"] = protocol, ["origin"] = origin, ["local"] = Endpoint(local), ["partner"] = Endpoint(partner),
    ["properties"] = new JsonObject { ["Name"] = "Same name", ["Port"] = 102, ["Opaque"] = "日本語 😀\nretained" } };
JsonObject Consumer(string path, string key) => new() { ["objectPath"] = path, ["connectionId"] = key, ["reason"] = "Supplied explicit reference" };
var before = new JsonObject { ["connectionsComplete"] = false, ["consumersComplete"] = false,
    ["connections"] = new JsonArray(Connection("s7", "S7"), Connection("tcp", "TCP"), Connection("hmi", "HMI", "hmiSoftware", h), Connection("udp", "UDP", "program")),
    ["consumers"] = new JsonArray(Consumer(block, "s7"), Consumer(tag, "hmi")) };
var after = before.DeepClone().AsObject(); after["connections"]![0]!["id"] = "new-s7"; after["connections"]![0]!["properties"]!["Port"] = 103;
after["connections"]![2]!["partner"] = Endpoint(a); after["consumers"]![0]!["connectionId"] = "new-s7";
var packet = new JsonObject { ["schemaVersion"] = "connection-review-request.v1", ["reviewId"] = id, ["afterKind"] = "proposal", ["before"] = before, ["after"] = after,
    ["matches"] = new JsonArray(new JsonObject { ["beforeId"] = "s7", ["afterId"] = "new-s7" }, new JsonObject { ["beforeId"] = "tcp", ["afterId"] = "tcp" }, new JsonObject { ["beforeId"] = "hmi", ["afterId"] = "hmi" }) };
JsonObject Mutate(Action<JsonObject> edit) { var p = packet.DeepClone().AsObject(); edit(p); return p; }
JsonObject Build(JsonObject? p = null, JsonObject? r = null) => ConnectionReview.Build(id, r ?? review, p ?? packet);
var result = Build();
Check((int)result["summary"]!["changedCount"]! == 2, "Explicit S7 property and HMI partner changes reported");
Check((string)result["comparisons"]![0]!["changedFields"]![0]! == "properties/Port", "Property diff identifies exact field");
Check((string)result["comparisons"]![1]!["status"]! == "unchanged", "Explicit identical TCP mapping stays unchanged");
Check((int)result["summary"]!["unmatchedBeforeCount"]! == 1 && (int)result["summary"]!["unmatchedAfterCount"]! == 1, "Equal UDP IDs never implicitly match across phases");
Check(result["observations"]!.AsArray().All(o => (string)o!["resourceAssessment"]! == "notAssessed"), "Duplicate ports never become invented resource conflicts or passes");
Check(result["consumers"]!.AsArray().All(c => (string)c!["comparisonStatus"]! == "changed"), "Declared PLC and HMI consumers retain changed connection association");
Check((int)result["summary"]!["unresolvedEndpointCount"]! == 0 && !(bool)result["observations"]![0]!["local"]!["nativeOwnershipVerified"]!, "Captured hardware ancestry never proves native ownership");
Check(!(bool)result["nativeQualified"]! && !(bool)result["wholeProjectAnalysisComplete"]!, "Supplied observations cannot certify native or completeness claims");
Check((string)result["observations"]![0]!["connection"]!["properties"]!["Opaque"]! == "日本語 😀\nretained", "Opaque multiline Unicode survives unchanged");
foreach (string family in new[] { "S7", "TCP", "UDP", "ISO", "ISO-on-TCP", "FDL", "PtP", "HMI", "vendor-extension" })
    Check((bool)Build(Mutate(p => p["before"]!["connections"]![0]!["protocol"] = family))["observations"]![0]!["advertisedFamily"]! == (family != "vendor-extension"), "Preserves protocol family " + family + " without property validation");
string Local(JsonObject r) => (string)r["observations"]![0]!["local"]!["status"]!;
Check(Local(Build(Mutate(p => p["before"]!["connections"]![0]!["local"]!["interfacePath"] = b + "/interface"))) == "ownershipUnproven", "Same-name interface under wrong CPU does not prove ownership");
Check(Local(Build(Mutate(p => p["before"]!["connections"]![0]!["local"]!["ownerPath"] = a.ToLowerInvariant()))) == "pathNotCaptured", "Case-distinct path never rebinds");
Check(Local(Build(Mutate(p => p["before"]!["connections"]![0]!["local"]!["ownerPath"] = project + "/virtual"))) == "pathNotCaptured", "Virtual object cannot become endpoint");
Check(Local(Build(Mutate(p => p["before"]!["connections"]![0]!["local"]!["ownerPath"] = block))) == "notHardware", "Captured block cannot become hardware endpoint");
Check(Local(Build(Mutate(p => { p["before"]!["connections"]![0]!["local"]!["ownerPath"] = null; p["before"]!["connections"]![0]!["local"]!["reason"] = "External unknown partner"; }))) == "selectorUnknown", "Explicit unknown endpoint remains unknown");
var cyclic = review.DeepClone().AsObject(); cyclic["model"]!["nodes"]![2]!["parentId"] = a + "/interface";
Check(Local(Build(r: cyclic)) == "ownershipUnproven", "Cyclic ancestry terminates without proving ownership");
var absentParent = review.DeepClone().AsObject(); absentParent["model"]!["nodes"]![2]!["parentId"] = "not-captured";
Check(Local(Build(r: absentParent)) == "ownershipUnproven", "Missing ancestry never resolves by path prefix");
Check((int)Build(Mutate(p => p["matches"] = new JsonArray()))["summary"]!["matchedCount"]! == 0, "Empty mapping never falls back to names or addresses");
var nullField = Build(Mutate(p => p["after"]!["connections"]![1]!["properties"]!["a/b~c"] = null));
Check((string)nullField["comparisons"]![1]!["changedFields"]![0]! == "properties/a~1b~0c", "Missing versus null differs with escaped field pointer");
var reordered = Build(Mutate(p => { var rows = p["after"]!["connections"]!.AsArray(); var row = rows[0]; rows.RemoveAt(0); rows.Add(row); }));
Check(JsonNode.DeepEquals(result["comparisons"], reordered["comparisons"]), "Array reordering does not change explicit mapping");
var numeric = Build(Mutate(p => { p["before"]!["connections"]![1]!["properties"]!["number"] = JsonNode.Parse("9007199254740993"); p["after"]!["connections"]![1]!["properties"]!["number"] = JsonNode.Parse("9007199254740992"); }));
Check((string)numeric["comparisons"]![1]!["status"]! == "changed", "Adjacent integers beyond double precision remain distinct");
var exponential = Build(Mutate(p => { p["before"]!["connections"]![1]!["properties"]!["number"] = JsonNode.Parse("1e400"); p["after"]!["connections"]![1]!["properties"]!["number"] = JsonNode.Parse("2e400"); }));
Check((string)exponential["comparisons"]![1]!["status"]! == "changed", "Opaque large exponents do not collapse to infinity");
var empty = Build(Mutate(p => { foreach (string phase in new[] { "before", "after" }) { p[phase]!["connections"] = new JsonArray(); p[phase]!["consumers"] = new JsonArray(); } p["matches"] = new JsonArray(); }));
Check((int)empty["summary"]!["beforeCount"]! == 0 && !(bool)empty["wholeProjectAnalysisComplete"]!, "Empty supplied snapshots do not prove empty native inventory");
var nested = Build(Mutate(p => p["after"]!["connections"]![1]!["properties"]!["extension"] = JsonNode.Parse("{\"unknown\":[null,{\"x\":true}]}")));
Check((string)nested["comparisons"]![1]!["changedFields"]![0]! == "properties/extension", "Opaque nested vendor properties are compared and retained");
var unknown = Build(Mutate(p => { p["before"]!["consumers"]![0]!["objectPath"] = null; p["before"]!["consumers"]![0]!["connectionId"] = "absent"; }));
Check((int)unknown["summary"]!["unresolvedConsumerCount"]! == 1, "Absent consumer and connection stay unresolved");
foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "liveFreshnessVerified", "wholeProjectAnalysisComplete" })
    Check(!(bool)Build(Mutate(p => { p["before"]!["connectionsComplete"] = true; p["after"]!["connectionsComplete"] = true; p["before"]!["consumersComplete"] = true; p["after"]!["consumersComplete"] = true; }))[flag]!, "Supplied complete flags cannot promote " + flag);
Reject(() => Build(Mutate(p => p["reviewId"] = new string('0', 64))), "Wrong exact review refused");
Reject(() => Build(Mutate(p => p["afterKind"] = "native")), "Unsupported after role refused");
Reject(() => Build(Mutate(p => p["before"]!["connections"]!.AsArray().Add(p["before"]!["connections"]![0]!.DeepClone()))), "Duplicate connection identity refused");
Reject(() => Build(Mutate(p => p["matches"]!.AsArray().Add(p["matches"]![0]!.DeepClone()))), "Repeated mapping refused");
Reject(() => Build(Mutate(p => p["matches"]![0]!["afterId"] = "absent")), "Unobserved mapping refused");
Reject(() => Build(Mutate(p => p["after"]!["connections"]![0]!["origin"] = "program")), "Cross-origin substitution refused");
Reject(() => Build(Mutate(p => p["before"]!["connections"]![0]!["local"]!["ownerPath"] = null)), "Unknown endpoint requires reason");
Reject(() => Build(Mutate(p => { p["before"]!["connectionsComplete"] = true; p["before"]!["consumers"]![0]!["connectionId"] = "absent"; })), "Contradictory complete connection inventory refused");
Reject(() => Build(Mutate(p => p["before"]!["consumersComplete"] = "true")), "Nonboolean coverage refused");
Reject(() => Build(Mutate(p => p["nativeQualified"] = true)), "Unknown request field refused");
Reject(() => Build(Mutate(p => p["before"]!["connections"]![0]!["id"] = "bad\nID")), "Control-bearing selector refused");
Reject(() => Build(Mutate(p => p["before"]!["connections"]![0]!["properties"] = new JsonArray())), "Nonobject property payload refused");
Reject(() => Build(Mutate(p => p["before"]!["connections"] = new JsonArray(Enumerable.Range(0,513).Select(i => (JsonNode?)Connection(i.ToString(),"TCP")).ToArray()))), "Connection count bounded");
Reject(() => Build(Mutate(p => p["before"]!["consumers"] = new JsonArray(Enumerable.Range(0,2049).Select(i => (JsonNode?)Consumer(block,"s7")).ToArray()))), "Consumer count bounded");
Reject(() => ConnectionReview.Parse(Encoding.UTF8.GetBytes("{\"properties\":{\"x\":1,\"x\":2}}")), "Nested duplicate opaque fields refused");
Reject(() => ConnectionReview.Parse(new byte[ConnectionReview.InputLimit+1]), "Input byte bound enforced");
foreach (byte[] invalid in new[] { new byte[] { 0xff }, Encoding.UTF8.GetBytes("{\"opaque\":\"\\ud800\"}"), Encoding.UTF8.GetBytes(new string('[',33)+new string(']',33)) })
{
    bool refused=false; try { ConnectionReview.Parse(invalid); } catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException) { refused=true; }
    Check(refused,"Malformed Unicode or excessive depth refuses before retention");
}
Check(JsonNode.DeepEquals(result,Build()), "Review leaves original request and model unchanged");
var uploads = new ArtifactUploadStore(root);
(string UploadId,string Ref,byte[] Bytes) Upload(JsonObject p)
{ byte[] bytes=Encoding.UTF8.GetBytes(p.ToJsonString()+"\n"); var upload=uploads.Begin("connections.json",bytes.Length,Hash(bytes)); for(int i=0;i<bytes.Length;i+=upload.MaxChunkBytes) uploads.Write(upload.UploadId,i,Convert.ToBase64String(bytes,i,Math.Min(upload.MaxChunkBytes,bytes.Length-i))); return (upload.UploadId,uploads.Commit(upload.UploadId).ArtifactRef!,bytes); }
var input=Upload(packet);var store=new ConnectionWorkflowStore(root);var receipt=store.Review(id,input.Ref);string record=(string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt,store.Review(id,input.Ref)),"Idempotent retained receipt");
Check(ProjectReviewStore.ReadArtifact(root,receipt["input"]!.AsObject()).SequenceEqual(input.Bytes),"Original bytes including final newline retained");
foreach(string section in new[]{"observations","comparisons","consumers","matches"})
    Check(JsonNode.DeepEquals(store.Page(record,section,0,1)["rows"]![0]!["value"],result[section]![0]),"Paged "+section+" equals derived report");
Reject(()=>store.Page(record,"observations",100),"Out-of-bounds page refused","InvalidRequest");
Reject(()=>store.Page(record,"matches",0,201),"Oversized page refused","InvalidRequest");
uploads.Release(input.UploadId);Check(JsonNode.DeepEquals(receipt,new ConnectionWorkflowStore(root).Page(record)),"Upload release and store recreation preserve receipt");
foreach(string key in new[]{"input","reportArtifact"})
{
    string path=Path.Combine(root,(string)receipt[key]!["path"]!);byte[] original=File.ReadAllBytes(path);File.WriteAllText(path,"tampered");Reject(()=>store.Page(record),"Corrupted "+key+" refused","ArtifactChanged");File.WriteAllBytes(path,original);
}
var forged=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!.AsObject();forged["report"]!["summary"]!["changedCount"]=0;
byte[] forgedBytes=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);string forgedId=Hash(forgedBytes);File.WriteAllBytes(Path.Combine(root,"connection-reviews",forgedId+".json"),forgedBytes);
Reject(()=>store.Page(forgedId),"Rehashed invented report refused","ArtifactChanged");
forged.Remove("input");forgedBytes=JsonSerializer.SerializeToUtf8Bytes(forged,BridgeProtocol.Json);forgedId=Hash(forgedBytes);File.WriteAllBytes(Path.Combine(root,"connection-reviews",forgedId+".json"),forgedBytes);
Reject(()=>store.Page(forgedId),"Malformed rehashed envelope refuses structurally");
foreach (string defect in new[] { "extra", "reference" })
{
    var envelope=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,receipt["reportArtifact"]!.AsObject()))!.AsObject();
    if(defect=="extra") envelope["extra"]=true; else envelope["input"]!["path"]="other/"+(string)envelope["input"]!["sha256"]!+".input";
    byte[] data=JsonSerializer.SerializeToUtf8Bytes(envelope,BridgeProtocol.Json);string key=Hash(data);File.WriteAllBytes(Path.Combine(root,"connection-reviews",key+".json"),data);
    Reject(()=>store.Page(key),"Rehashed envelope "+defect+" refuses structurally");
}
var large=Mutate(p=>p["before"]!["connections"]![0]!["properties"]!["wide"] = new string('x',130*1024));var big=Upload(large);var bigReceipt=store.Review(id,big.Ref);
var page=store.Page((string)bigReceipt["recordId"]!,"observations",0,200);
Check((bool)page["rows"]![0]!["omitted"]! && JsonSerializer.SerializeToUtf8Bytes(page,BridgeProtocol.Json).Length<=128*1024,"Oversized observation explicitly omitted within page bound");
var full=JsonNode.Parse(ProjectReviewStore.ReadArtifact(root,bigReceipt["reportArtifact"]!.AsObject()))!;
Check(((string)full["report"]!["observations"]![0]!["connection"]!["properties"]!["wide"]!).Length==130*1024,"Download retains complete oversized property");
var maximum=Mutate(p=> {p["before"]!["consumers"]=new JsonArray(Enumerable.Range(0,2048).Select(_=>(JsonNode?)Consumer(block,"s7")).ToArray());p["after"]!["consumers"]=p["before"]!["consumers"]!.DeepClone();});
var maxUpload=Upload(maximum);var maxReceipt=store.Review(id,maxUpload.Ref);var maxPage=store.Page((string)maxReceipt["recordId"]!,"consumers",0,200);
Check((int)maxPage["total"]! == 4096 && maxPage["nextOffset"] is not null && JsonSerializer.SerializeToUtf8Bytes(maxPage,BridgeProtocol.Json).Length<=128*1024,"Maximum consumers page within byte budget");
var many = Mutate(p => { foreach (string phase in new[] { "before", "after" }) { p[phase]!["connections"] = new JsonArray(Enumerable.Range(0,512).Select(i => (JsonNode?)Connection(i.ToString(),"TCP")).ToArray()); p[phase]!["consumers"] = new JsonArray(); } p["matches"] = new JsonArray(Enumerable.Range(0,512).Select(i => (JsonNode?)new JsonObject { ["beforeId"] = i.ToString(), ["afterId"] = i.ToString() }).ToArray()); });
var manyUpload=Upload(many);var manyReceipt=store.Review(id,manyUpload.Ref);var manyPage=store.Page((string)manyReceipt["recordId"]!,"observations",0,200);
Check((int)manyReceipt["summary"]!["matchedCount"]! == 512 && (int)manyPage["total"]! == 1024 && manyPage["nextOffset"] is not null, "Maximum connections and mappings remain navigable");
string filler=Path.Combine(root,"connection-reviews","capacity.tmp");using(var f=File.Create(filler))f.SetLength(512L*1024*1024);
var different=Upload(Mutate(p=>p["matches"]=new JsonArray()));Reject(()=>store.Review(id,different.Ref),"Incomplete publication counts toward quota","ArtifactCapacity");File.Delete(filler);
string savedPath=Path.Combine(root,"project-reviews",id+".json");byte[] savedBytes=File.ReadAllBytes(savedPath);File.WriteAllText(savedPath,"tampered");Reject(()=>store.Page(record),"Saved source corruption refuses reopen","ArtifactChanged");File.WriteAllBytes(savedPath,savedBytes);
File.WriteAllText(Path.Combine(root,"mcp-fixture.json"),JsonSerializer.Serialize(new {artifactRoot=root,reviewId=id,packet},BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root,"verification.json"),JsonSerializer.Serialize(new {completed=true,nativeExecuted=false,nativeQualified=false,count=checks.Count,checks},BridgeProtocol.Json));
Console.WriteLine($"PASS connections: {checks.Count} checks; synthetic evidence only.");
