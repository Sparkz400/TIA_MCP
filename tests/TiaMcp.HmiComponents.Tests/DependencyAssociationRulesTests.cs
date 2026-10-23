using System.Text.Json.Nodes;
using TiaMcp.Core.HmiComponents;

internal static class DependencyAssociationRulesTests
{
    internal static void Run()
    {
        const string a="10000000-0000-0000-0000-000000000001", b="10000000-0000-0000-0000-000000000002", type="20000000-0000-0000-0000-000000000001";
        int checks=0;
        JsonObject CapturedObject(string path,string version,params string[] deps) => new() { ["capturedObject"]=new JsonObject {
            ["objectPath"]=path,["metadata"]=new JsonObject { ["libraryInstance"]=new JsonObject {
                ["schemaVersion"]="plc-library-instance-observation.v1",["source"]="LibraryTypeInstanceInfo.LibraryTypeVersion",["status"]="linked",
                ["typeGuid"]=type,["versionGuid"]=version,["dependencies"]=new JsonObject { ["schemaVersion"]="plc-library-dependencies.v1",
                    ["source"]="LibraryTypeVersion.Dependencies",["status"]="observed",["requiredVersionGuids"]=new JsonArray(deps.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()) } } } } };
        JsonObject Export(string path,string version) => new() { ["selection"]=new JsonObject { ["scope"]="afterComponent",["objectPath"]=path },
            ["exportIdentity"]=new JsonObject { ["typeGuid"]=type,["versionGuid"]=version },["file"]=new JsonObject { ["id"]=path },["identifierAgreement"]="match" };
        JsonObject Report()=>new() { ["schemaVersion"]="hmi-component-review.v5",["beforeComponent"]=null,
            ["afterComponent"]=new JsonObject { ["selection"]=new JsonObject { ["reviewId"]=new string('a',64) },["objects"]=new JsonArray(CapturedObject("/a",a,b),CapturedObject("/b",b)) },
            ["libraryAssociations"]=new JsonArray(Export("/a",a),Export("/b",b)),["summary"]=new JsonObject() };
        JsonObject Selection(string path="/a",int index=0,string target="/b")=>new() { ["scope"]="afterComponent",["objectPath"]=path,["dependencyIndex"]=index,["targetObjectPath"]=target,["reason"]="Exact original observation." };
        JsonObject Request()=>new() { ["dependencyAssociations"]=new JsonArray(Selection()) };
        JsonObject Observation(JsonObject r,int i=0)=>r["afterComponent"]!["objects"]![i]!["capturedObject"]!["metadata"]!["libraryInstance"]!.AsObject();
        void Check(bool yes,string label){if(!yes)throw new Exception(label);checks++;Console.WriteLine("PASS "+label);}
        void Reject(JsonObject report,JsonObject request,string label){try{HmiComponentDependencyAssociations.Add(report,request);}catch(TiaMcp.Core.BridgeException){Check(true,label);return;}throw new Exception("Accepted: "+label);}
        string Status(JsonObject r)=>(string)r["dependencyAssociations"]![0]!["status"]!;
        var r=Report();var q=Request();HmiComponentDependencyAssociations.Add(r,q);
        Check(Status(r)=="match" && (bool)r["dependencyCoverage"]![0]!["allObservedIndicesMatched"]!,"Exact selected dependency, target and export agree");
        Check((bool)r["dependencyCoverage"]![1]!["allObservedIndicesMatched"]!,"Observed empty list is complete within selected indices");
        Check(!(bool)r["dependencyAssociations"]![0]!["nativeIdentityVerified"]! && !(bool)r["dependencyCoverage"]![0]!["dependencyClosureVerified"]!,"Agreement grants no native or closure qualification");
        q["dependencyAssociations"]![0]!["targetObjectPath"]="/changed";
        Check((string?)r["dependencyAssociations"]![0]!["selection"]!["targetObjectPath"]=="/b","Returned selection is detached");
        foreach(string defect in new[]{"missing","null","schema","source","status","invalidGuid","tooMany","shape","owner"})
        {
            r=Report();var o=Observation(r);var d=o["dependencies"]!.AsObject();
            switch(defect){case "missing":o.Remove("dependencies");break;case "null":o["dependencies"]=null;break;case "schema":d["schemaVersion"]="future";break;
                case "source":d["source"]="name";break;case "status":d["status"]="unavailable";break;case "invalidGuid":d["requiredVersionGuids"]![0]=42;break;
                case "tooMany":d["requiredVersionGuids"]=new JsonArray(Enumerable.Repeat(b,1001).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());break;
                case "shape":o["dependencies"]=new JsonArray();break;case "owner":o["versionGuid"]=Guid.Empty.ToString("D");break;}
            HmiComponentDependencyAssociations.Add(r,new(){["dependencyAssociations"]=new JsonArray()});
            Check((string?)r["dependencyCoverage"]![0]!["dependencyStatus"]=="unavailable" && r["dependencyCoverage"]![0]!["requiredCount"] is null && !(bool)r["dependencyCoverage"]![0]!["allObservedIndicesMatched"]!,"Unavailable cannot become empty: "+defect);
        }
        r=Report();r["libraryAssociations"]!.AsArray().RemoveAt(1);HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="unavailable","Absent target export cannot establish agreement");
        r=Report();Observation(r,1)["versionGuid"]=" "+b+" ";HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="unavailable","Padded target GUID is not an exact identity observation");
        r=Report();Observation(r)["dependencies"]!["requiredVersionGuids"]![0]=" "+b+" ";HmiComponentDependencyAssociations.Add(r,new(){["dependencyAssociations"]=new JsonArray()});
        Check((string?)r["dependencyCoverage"]![0]!["dependencyStatus"]=="unavailable","Padded required GUID does not silently become a canonical dependency");
        r=Report();Observation(r,1)["versionGuid"]=a;HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="different","Wrong selected object version is a contradiction");
        r=Report();r["libraryAssociations"]![1]!["exportIdentity"]!["versionGuid"]=a;r["libraryAssociations"]![1]!["identifierAgreement"]="different";
        HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="different","Wrong original export version is a contradiction");
        r=Report();var extra=CapturedObject("/other",b);extra["capturedObject"]!["metadata"]!["libraryInstance"]!["typeGuid"]="20000000-0000-0000-0000-000000000002";
        r["afterComponent"]!["objects"]!.AsArray().Add(extra);HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="conflicting" && (bool)r["dependencyAssociations"]![0]!["identityConflict"]!,"Same version GUID with competing type identities remains conflicting");
        r=Report();Observation(r)["dependencies"]!["requiredVersionGuids"]!.AsArray().Add(b);HmiComponentDependencyAssociations.Add(r,Request());
        Check(r["dependencyCoverage"]![0]!["repeatedVersionGuids"]![0]!["indices"]!.AsArray().Count==2 && (int)r["dependencyCoverage"]![0]!["unassociatedIndices"]![0]! ==1,"Repeated GUID indices remain distinct and unmapped indices remain visible");
        r=Report();q=Request();q["dependencyAssociations"]!.AsArray().Add(q["dependencyAssociations"]![0]!.DeepClone());Reject(r,q,"Duplicate mapping index refuses");
        foreach(string defect in new[]{"scope","source","target","index","negative","fraction","extra"})
        {
            q=Request();var row=q["dependencyAssociations"]![0]!;
            switch(defect){case "scope":row["scope"]="beforeComponent";break;case "source":row["objectPath"]="a";break;case "target":row["targetObjectPath"]="b";break;
                case "index":row["dependencyIndex"]=1;break;case "negative":row["dependencyIndex"]=-1;break;case "fraction":row["dependencyIndex"]=0.5;break;case "extra":row["qualified"]=true;break;}
            Reject(Report(),q,"Exact selection refuses: "+defect);
        }
        r=Report();Observation(r,1)["dependencies"]!["requiredVersionGuids"]!.AsArray().Add(a);q=Request();q["dependencyAssociations"]!.AsArray().Add(Selection("/b",0,"/a"));
        HmiComponentDependencyAssociations.Add(r,q);Check((int)r["summary"]!["dependencyCyclicMappingCount"]! ==2,"Declared two-node cycle is explicit");
        r=Report();Observation(r)["dependencies"]!["requiredVersionGuids"]![0]=a;q=new(){["dependencyAssociations"]=new JsonArray(Selection("/a",0,"/a"))};
        HmiComponentDependencyAssociations.Add(r,q);Check((bool)r["dependencyAssociations"]![0]!["declaredCycle"]! && r["dependencyCoverage"]![0]!["selfReferenceIndices"]!.AsArray().Count==1,"Self dependency is retained and marked cyclic");
        r=Report();Observation(r)["dependencies"]!["requiredVersionGuids"]=new JsonArray();Reject(r,Request(),"Selected index cannot exist in an observed empty list");
        r=Report();r["beforeComponent"]=r["afterComponent"]!.DeepClone();r["beforeComponent"]!["selection"]!["reviewId"]=new string('b',64);
        r["beforeComponent"]!["objects"]![1]!["capturedObject"]!["metadata"]!["libraryInstance"]!["typeGuid"]="20000000-0000-0000-0000-000000000002";
        HmiComponentDependencyAssociations.Add(r,Request());Check(Status(r)=="match","Competing identity in another review scope does not join this scope");
        r=Report();r["afterComponent"]!["objects"]!.AsArray().Clear();r["libraryAssociations"]=new JsonArray();
        for(int i=0;i<20;i++)r["afterComponent"]!["objects"]!.AsArray().Add(CapturedObject("/"+i,a,Enumerable.Repeat(b,1000).ToArray()));
        var within=r.DeepClone().AsObject();HmiComponentDependencyAssociations.Add(within,new(){["dependencyAssociations"]=new JsonArray()});
        Check((int)within["summary"]!["dependencyUnassociatedIndexCount"]! ==20000,"Full 20000-reference limit retains all unmapped indices");
        r["afterComponent"]!["objects"]!.AsArray().Add(CapturedObject("/overflow",a,b));Reject(r,new(){["dependencyAssociations"]=new JsonArray()},"One reference beyond the shared bound refuses");
        q=new(){["dependencyAssociations"]=new JsonArray(Enumerable.Range(0,4097).Select(_=>(JsonNode?)Selection()).ToArray())};Reject(Report(),q,"Explicit mapping count is bounded before evaluation");
        Console.WriteLine($"{checks} focused dependency association checks passed; direct evaluator only, no native qualification.");
    }
}
