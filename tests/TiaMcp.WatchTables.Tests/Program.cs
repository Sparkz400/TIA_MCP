using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.WatchTables;

if (args.Length == 3 && args[0] == "--document-evidence") return DocumentChecks.Evidence(args[1], args[2]);

var checks = new (string, Action)[] {
    ("Authored MotorA entry oracle and TEMP retained", Oracle),
    ("Determinism, explanation and immutable input/output", Determinism),
    ("MotorB, nested instance and explicit external storage", Instances),
    ("Stale source, mixed project/PLC/capture and stale declarations", Freshness),
    ("All normalized selection sources and declaration evidence", CandidateSources),
    ("Ambiguous, unavailable and unsupported types stay gaps", Gaps),
    ("Duplicate policy preserves intent and conflicting types reject", Duplicates),
    ("Budget exhaustion retains every omitted candidate", Bounds),
    ("Reusable template mapping, order, type and role guards", Templates),
    ("Separate address observations, optimized DB and alignment guards", Addresses),
    ("One thousand captured rows retain deterministic bounded paging intent", Scale),
    ("Hash-bound case and native-gate separation", Claims)
}.Concat(DocumentChecks.Checks).Append(("Genuine owned XML captures: hashes, no-op, reviewed delta and recovery baseline", (Action)NativeDocumentChecks.Run)).ToArray();
int failed = 0;
foreach (var (name, action) in checks) try { action(); Console.WriteLine("PASS " + name); } catch(Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + "\n" + e); }
if (args.Length > 1)
{
    try { Integration(args[1]); Console.WriteLine("PASS existing DiagnosticPlanner output integration"); }
    catch(Exception e) { failed++; Console.Error.WriteLine("FAIL diagnostic integration\n" + e); }
}
if (failed == 0 && args.Length > 0)
{
    File.WriteAllText(args[0], WatchTablePlanner.Plan(F.Request()).ToJsonString(BridgeProtocol.Json));
    var r=F.Request();
    var variants=new JsonArray(WatchTablePlanner.Plan(r with {MaxRows=2}),WatchTablePlanner.Plan(r with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=[F.Candidate("normalized")]}),
        WatchTablePlanner.Plan(r with {Template=new("simple","1",[new("command","input","Bool")]),TemplateBindings=[new("command","enable","Explicit template mapping")]}),
        WatchTablePlanner.Plan(r with {AddressObservations=[new("starts",r.Instance.Storage,["Starts"],"DInt","absolute","2026-10-15T10:00:00Z",new JsonObject{["origin"]="authored fixture"},Area:"DB",StartBit:32,WidthBits:32,DbNumber:1,OptimizedDb:false)]}));
    File.WriteAllText(args[0]+".variants.json",variants.ToJsonString(BridgeProtocol.Json));
}
int total=checks.Length+(args.Length>1?1:0);
Console.WriteLine($"{total - failed}/{total} watch planner groups passed; no native table qualification.");
return failed == 0 ? 0 : 1;

static void Oracle()
{
    var p = WatchTablePlanner.Plan(F.Request());
    var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures/expected-motor.json")))!;
    var rows = p["rows"]!.AsArray(); Equal(4, rows.Count);
    for(int i=0;i<rows.Count;i++)
    {
        Equal(expected["rowIds"]![i]!.GetValue<string>(), rows[i]!["candidateId"]!.GetValue<string>());
        Equal(expected["types"]![i]!.GetValue<string>(), rows[i]!["candidate"]!["datatype"]!.GetValue<string>());
        True(JsonNode.DeepEquals(expected["members"]![i],rows[i]!["candidate"]!["memberPath"]));
        Equal(expected["selectedStorage"]!.GetValue<string>(),rows[i]!["candidate"]!["storage"]!["object"]!["objectPath"]!.GetValue<string>());
        True(rows[i]!["onlineAddress"] is null && rows[i]!["modifyIntention"]!.GetValue<bool>() == false);
    }
    Equal("temp",p["unresolvedSuggestions"]![0]!["candidateId"]!.GetValue<string>());
    True(!p["rows"]!.ToJsonString().Contains(expected["forbiddenStorage"]!.GetValue<string>(),StringComparison.Ordinal));
}
static void Determinism()
{
    var request=F.Request(); string before=request.DiagnosticCase!.ToJsonString(); var a=WatchTablePlanner.Plan(request);var b=WatchTablePlanner.Plan(request);
    Equal(CanonicalJson.Serialize(a),CanonicalJson.Serialize(b)); Equal(before,request.DiagnosticCase.ToJsonString());
    a["rows"]![0]!["candidate"]!["reason"]="changed output";Equal(before,request.DiagnosticCase.ToJsonString());
    True(b["rows"]![0]!["selectionReasons"]![0]!["evidence"] is JsonObject);
    var hash=b["planSha256"]!.GetValue<string>();b.Remove("planSha256");Equal(hash,CanonicalJson.Sha256(b));
}
static void Instances()
{
    var r=F.Request(); var d=(JsonObject)r.DiagnosticCase!.DeepClone(); d["signals"]![0]!["storagePath"]=F.Plc+"/blocks/MotorB";
    Error("ExternalStorageNotSelected",()=>WatchTablePlanner.Plan(F.WithCase(r,d)));
    var nested=r with {DiagnosticCase=null,DiagnosticCaseSha256=null,Instance=new(r.Instance.Storage,["MotorA"]),Candidates=[F.Candidate("other",member:["MotorB","Running"])]};
    Error("InstanceMismatch",()=>WatchTablePlanner.Plan(nested));
    var ext=F.Ref("/blocks/MotorB",new string('d',64)); var c=F.Candidate("explicit",member:["Running"]) with {Storage=ext};
    c=c with {Evidence=F.Evidence(c.SourceObject,ext,c.MemberPath,c.Datatype)};
    var allowed=r with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=[c],ExternalStorage=[new(ext,"Explicit second motor comparison") ]};
    Equal(1,WatchTablePlanner.Plan(allowed)["rows"]!.AsArray().Count);
}
static void Freshness()
{
    var r=F.Request(); Error("StaleObservation",()=>WatchTablePlanner.Plan(r with {ExpectedSource=r.ExpectedSource with {Snapshot=r.ExpectedSource.Snapshot with {ObservationId="older"}}}));
    Error("StaleContent",()=>WatchTablePlanner.Plan(r with {ExpectedSource=r.ExpectedSource with {ContentSha256=new string('e',64)}}));
    var c=F.Candidate("x");
    Error("StaleObservation",()=>WatchTablePlanner.Plan(r with {Candidates=[c with {Storage=c.Storage with {Snapshot=c.Storage.Snapshot with {SourceSha256=new string('e',64)}}}]}));
    Error("StaleObservation",()=>WatchTablePlanner.Plan(r with {Candidates=[c with {Storage=c.Storage with {Object=c.Storage.Object with {ProjectPath="D:/copy.ap21"}}}]}));
    Error("PlcMismatch",()=>WatchTablePlanner.Plan(r with {Candidates=[c with {Storage=c.Storage with {Object=c.Storage.Object with {PlcPath="/plc/other"}}}]}));
    var d=(JsonObject)r.DiagnosticCase!.DeepClone();d["signals"]![0]!["evidence"]!["utf8Sha256"]=new string('f',64);
    Error("CaptureMismatch",()=>WatchTablePlanner.Plan(F.WithCase(r,d)));
}
static void CandidateSources()
{
    foreach(string source in new[]{"interface","callGraph","instanceDb","selectedNetwork","ioMap"})
    {
        var c=F.Candidate(source,source);var r=F.Request() with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=[c]};
        Equal(source,WatchTablePlanner.Plan(r)["rows"]![0]!["candidate"]!["sourceKind"]!.GetValue<string>());
        var bad=(JsonObject)c.Evidence.DeepClone();bad["declaration"]!["datatype"]="Real";
        Error("CaptureMismatch",()=>WatchTablePlanner.Plan(r with {Candidates=[c with {Evidence=bad}]}));
    }
    var network=F.Candidate("network","selectedNetwork") with {NetworkId=null};
    Error("InvalidRequest",()=>WatchTablePlanner.Plan(F.Request() with {Candidates=[network]}));
}
static void Gaps()
{
    var candidates=new[]{F.Candidate("amb") with {Resolution="ambiguous",UnavailableReason="Two namespace-qualified symbols"},F.Candidate("missing") with {Resolution="unavailable",UnavailableReason="Protected declaration"},F.Candidate("structure",type:"UDT_Motor")};
    var p=WatchTablePlanner.Plan(F.Request() with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=candidates});
    Equal(0,p["rows"]!.AsArray().Count); Equal(3,p["unresolvedSuggestions"]!.AsArray().Count);
    Equal("AmbiguousSignal",p["unresolvedSuggestions"]![0]!["code"]!.GetValue<string>());
    foreach(string storageClass in new[]{"temp","inOutAlias","indirect","unknown"})
        Equal(0,WatchTablePlanner.Plan(F.Request() with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=[F.Candidate("transient") with {StorageClass=storageClass} ]})["rows"]!.AsArray().Count);
}
static void Duplicates()
{
    var r=F.Request() with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=[F.Candidate("a"),F.Candidate("b")]};
    Equal(2,WatchTablePlanner.Plan(r)["rows"]!.AsArray().Count);
    var collapsed=WatchTablePlanner.Plan(r with {DuplicatePolicy="collapseExact"});Equal(1,collapsed["rows"]!.AsArray().Count);Equal(2,collapsed["rows"]![0]!["selectionReasons"]!.AsArray().Count);
    Error("ConflictingSignalType",()=>WatchTablePlanner.Plan(r with {Candidates=[F.Candidate("a"),F.Candidate("b",type:"Int")]}));
    Error("DuplicateCandidateId",()=>WatchTablePlanner.Plan(r with {Candidates=[F.Candidate("a"),F.Candidate("a")]}));
    var second=F.Candidate("b") with {SymbolComponents=["Alias","Running"]};
    Equal(2,WatchTablePlanner.Plan(r with {Candidates=[F.Candidate("a"),second],DuplicatePolicy="collapseExact"})["rows"]!.AsArray().Count);
}
static void Bounds()
{
    var p=WatchTablePlanner.Plan(F.Request() with {MaxRows=2});Equal(2,p["rows"]!.AsArray().Count);Equal(2,p["omittedByBudget"]!.GetValue<int>());True(p["truncated"]!.GetValue<bool>());Equal(3,p["unresolvedSuggestions"]!.AsArray().Count);
    Error("InvalidRequest",()=>WatchTablePlanner.Plan(F.Request() with {MaxRows=0}));
    Error("WatchTableLimitExceeded",()=>WatchTablePlanner.Plan(F.Request() with {Candidates=Enumerable.Range(0,10001).Select(i=>F.Candidate("c"+i)).ToArray()}));
    var evidence=F.Candidate("x").Evidence;evidence["large"]=new string('x',65537);
    Error("WatchTableLimitExceeded",()=>WatchTablePlanner.Plan(F.Request() with {Candidates=[F.Candidate("x") with {Evidence=evidence}]}));
    var r=F.Request();var d=(JsonObject)r.DiagnosticCase!.DeepClone();d["automaticSelection"]=new JsonObject{["truncated"]=true};
    var partial=WatchTablePlanner.Plan(F.WithCase(r,d));True(partial["sourceTruncated"]!.GetValue<bool>()&&partial["truncated"]!.GetValue<bool>());
}
static void Templates()
{
    var t=new WatchTableTemplate("motor","1",[new("state","state","DInt"),new("command","input","Bool")]);
    var r=F.Request() with {Template=t,TemplateBindings=[new("command","enable","User command"),new("state","starts","Count transitions")]};
    var p=WatchTablePlanner.Plan(r);Equal("starts",p["rows"]![0]!["candidateId"]!.GetValue<string>());Equal("enable",p["rows"]![1]!["candidateId"]!.GetValue<string>());
    Error("TemplateMappingMissing",()=>WatchTablePlanner.Plan(r with {TemplateBindings=[new("state","starts","Only one")]}));
    Error("TemplateTypeMismatch",()=>WatchTablePlanner.Plan(r with {TemplateBindings=[new("state","enable","Wrong scalar"),new("command","running","Wrong role")]}));
    Equal(3,p["sourceGaps"]!.AsArray().Count(x=>x?["code"]?.GetValue<string>()=="NotMappedByTemplate"));
}
static void Addresses()
{
    var r=F.Request();var obs=new WatchTableAddressObservation("starts",r.Instance.Storage,["Starts"],"DInt","absolute","2026-10-15T10:00:00Z",new JsonObject{["origin"]="authored offline address fixture"},Area:"DB",StartBit:32,WidthBits:32,DbNumber:1,OptimizedDb:false);
    var p=WatchTablePlanner.Plan(r with {AddressObservations=[obs]});True(p["rows"]![3]!["onlineAddress"] is null);Equal(32,p["rows"]![3]!["suppliedAddressObservation"]!["widthBits"]!.GetValue<int>());
    Error("InvalidAddressObservation",()=>WatchTablePlanner.Plan(r with {AddressObservations=[obs with {OptimizedDb=true}]}));
    Error("InvalidAddressObservation",()=>WatchTablePlanner.Plan(r with {AddressObservations=[obs with {StartBit=3}]}));
    Error("AddressObservationMismatch",()=>WatchTablePlanner.Plan(r with {AddressObservations=[obs with {MemberPath=["Running"]}]}));
    Error("InvalidAddressObservation",()=>WatchTablePlanner.Plan(r with {AddressObservations=[obs with {WidthBits=16}]}));
    Error("InvalidRequest",()=>WatchTablePlanner.Plan(r with {AddressObservations=[obs,obs]}));
    var overlap=obs with {CandidateId="enable",MemberPath=["Enable"],Datatype="Bool",WidthBits=1};
    Equal(1,WatchTablePlanner.Plan(r with {AddressObservations=[obs,overlap]})["addressWarnings"]!["items"]!.AsArray().Count);
}
static void Scale()
{
    var list=Enumerable.Range(0,1000).Select(i=>F.Candidate("c"+i,member:["Signal"+i])).ToArray();
    var r=F.Request() with {DiagnosticCase=null,DiagnosticCaseSha256=null,Candidates=list,MaxRows=1000};var p=WatchTablePlanner.Plan(r);
    Equal(1000,p["rows"]!.AsArray().Count);Equal("c999",p["rows"]![999]!["candidateId"]!.GetValue<string>());
    Equal(CanonicalJson.Sha256(p),CanonicalJson.Sha256(WatchTablePlanner.Plan(r)));
}
static void Claims()
{
    var r=F.Request();Error("DiagnosticCaseMismatch",()=>WatchTablePlanner.Plan(r with {DiagnosticCaseSha256=new string('0',64)}));
    var d=(JsonObject)r.DiagnosticCase!.DeepClone();d["signals"]![0]!["onlineAddress"]="%DB1.DBX0.0";
    Error("InvalidRequest",()=>WatchTablePlanner.Plan(F.WithCase(r,d)));
    var p=WatchTablePlanner.Plan(r);True(!p["nativeExecutable"]!.GetValue<bool>()&&!p["nativeDocumentGenerated"]!.GetValue<bool>());Equal(6,p["pendingNativeGates"]!.AsArray().Count);
}
static void Integration(string samplesPath)
{
    var samples=JsonNode.Parse(File.ReadAllText(samplesPath))!;var d=samples["diagnostic-case.v1"]!.AsObject();
    string project=(d["project"]!["path"]??d["project"]!["projectPath"])!.GetValue<string>(), plc=d["selectedInstance"]!["plcPath"]!.GetValue<string>(), scope=d["selectedInstance"]!["scopePath"]!.GetValue<string>();
    var snapshot=new EngineeringSnapshotIdentity(d["reviewId"]!.GetValue<string>(),CanonicalJson.Sha256(d["sourceRevision"]!));
    EngineeringObjectReference Ref(string path) => new(new(project,plc,scope,path,"block"),snapshot,d["sourceRevision"]!["documents"]!.AsArray().FirstOrDefault(x=>x!["objectPath"]!.GetValue<string>()==path)?["utf8Sha256"]?.GetValue<string>());
    var block=Ref(d["blockPath"]!.GetValue<string>()); var db=Ref(d["selectedInstance"]!["dbPath"]!.GetValue<string>());
    var r=new WatchTablePlanningRequest{ExpectedSource=block,CapturedSource=block,Instance=new(db,d["selectedInstance"]!["memberPath"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray()),TableName="Integration",DiagnosticCase=d,DiagnosticCaseSha256=CanonicalJson.Sha256(d),ExternalStorage=d["signals"]!.AsArray().Select(x=>x!["storagePath"]!.GetValue<string>()).Distinct().Where(x=>x!=db.Object.ObjectPath).Select(x=>new WatchTableExternalStorage(Ref(x),"Explicit supplied diagnostic storage")).ToArray()};
    var p=WatchTablePlanner.Plan(r);Equal(11,p["rows"]!.AsArray().Count);
}
static void True(bool value){if(!value)throw new Exception("Assertion failed");}
static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}; got {actual}");}
static void Error(string code,Action action){try{action();}catch(BridgeException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}

static class F
{
    public const string Plc="/plc/PLC_A", Project="D:/fixtures/motor.ap21";
    public static EngineeringSnapshotIdentity Snapshot=new("review-motor",new string('a',64));
    public static EngineeringObjectReference Ref(string suffix,string? content=null)=>new(new(Project,Plc,Plc,Plc+suffix,"block"),Snapshot,content);
    public static EngineeringObjectReference Block=>Ref("/blocks/Motor",new string('b',64));
    public static EngineeringObjectReference Db=>Ref("/blocks/MotorA",new string('c',64));
    public static WatchTablePlanningRequest Request()
    {
        var signals=new JsonArray();
        foreach(var (id,name,type,role) in new[]{("enable","Enable","Bool","input"),("running","Running","Bool","output"),("fault","Fault","Bool","state"),("starts","Starts","DInt","state"),("temp","Scratch","Int","temporary")})
            signals.Add(new JsonObject{["id"]=id,["storagePath"]=Db.Object.ObjectPath,["memberPath"]=new JsonArray(name),["datatype"]=type,["role"]=role,["reason"]="Authored motor diagnostic "+name,["hypothesisIds"]=new JsonArray("h1"),["resolution"]=id=="temp"?"unavailable":"capturedDeclaration",["traceability"]=id=="temp"?"unavailable":"requiresTargetValidation",["onlineAddress"]=null,["symbolComponents"]=new JsonArray("MotorA",name),["unavailableReason"]=id=="temp"?"TEMP has no persistent instance storage":null,["evidence"]=id=="temp"?null:new JsonObject{["objectPath"]=Db.Object.ObjectPath,["utf8Sha256"]=Db.ContentSha256,["origin"]="savedNativeXml",["xmlLine"]=10,["liveVerified"]=false}});
        var d=new JsonObject{["schemaVersion"]="diagnostic-case.v1",["reviewId"]=Snapshot.ObservationId,["project"]=new JsonObject{["path"]=Project},["blockPath"]=Block.Object.ObjectPath,["selectedInstance"]=new JsonObject{["dbPath"]=Db.Object.ObjectPath,["blockPath"]=Block.Object.ObjectPath,["memberPath"]=new JsonArray(),["plcPath"]=Plc,["resolution"]="capturedStorageOnly"},["sourceRevision"]=new JsonObject{["reviewId"]=Snapshot.ObservationId,["documents"]=new JsonArray(new JsonObject{["objectPath"]=Db.Object.ObjectPath,["status"]="exported",["utf8Sha256"]=Db.ContentSha256})},["signals"]=signals,["nativeExecutable"]=false,["qualification"]="offlinePlanningOnly",["coverageGaps"]=new JsonArray("Captured source remains offline")};
        return new(){ExpectedSource=Block,CapturedSource=Block,Instance=new(Db,[]),TableName="MotorA diagnostic",DiagnosticCase=d,DiagnosticCaseSha256=CanonicalJson.Sha256(d)};
    }
    public static WatchTablePlanningRequest WithCase(WatchTablePlanningRequest r,JsonObject d)=>r with {DiagnosticCase=d,DiagnosticCaseSha256=CanonicalJson.Sha256(d)};
    public static JsonObject Evidence(EngineeringObjectReference source,EngineeringObjectReference storage,IReadOnlyList<string> path,string type)=>new(){["observationId"]=Snapshot.ObservationId,["sourceSha256"]=Snapshot.SourceSha256,["objectPath"]=source.Object.ObjectPath,["declaration"]=new JsonObject{["storagePath"]=storage.Object.ObjectPath,["memberPath"]=new JsonArray(path.Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray()),["datatype"]=type,["contentSha256"]=storage.ContentSha256}};
    public static WatchTableCandidate Candidate(string id,string source="interface",string type="Bool",string[]? member=null)
    {var path=member??["Running"];var origin=source=="instanceDb"?Db:Block;return new(id,source,origin,Db,path,type,"output","Independent captured candidate "+id,"capturedDeclaration",Evidence(origin,Db,path,type),["MotorA",..path],NetworkId:source=="selectedNetwork"?"network:1":null,StorageClass:"instanceMember");}
}
