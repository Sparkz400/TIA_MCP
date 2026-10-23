using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Indexing;

namespace TiaMcp.Tests;

// Authored graph oracle: expected edge IDs below are independent of the traversal implementation.
internal static class ProjectModelRelationshipTests
{
    internal static JObject Fixture()
    {
        var nodes = new JArray("root,A,B,C,D,X,Y,Z".Split(',').Select(id => new JObject {
            ["nodeId"] = id, ["parentId"] = id == "root" ? null : "root", ["name"] = id, ["kind"] = id == "root" ? "project" : "block",
            ["origin"] = "native", ["objectPath"] = "/fixture/" + id, ["access"] = new JObject { ["state"] = "readable" }, ["metadata"] = new JObject(), ["detailAvailability"] = "metadata" }));
        JObject? Endpoint(string? id) => id == null ? null : new JObject { ["nodeId"] = id, ["objectPath"] = "/fixture/" + id };
        JObject Edge(string id, string? from, string? to, string kind = "calls") => new JObject {
            ["edgeId"] = id, ["source"] = Endpoint(from), ["target"] = Endpoint(to), ["kind"] = kind,
            ["evidence"] = new JObject { ["provider"] = "independentFixture", ["origin"] = "unresolved", ["resolution"] = from == null || to == null ? "unresolved" : "resolved", ["sourcePath"] = "/fixture", ["location"] = id,
                ["capturedAtUtc"] = "2026-10-21T00:00:00Z", ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "fixtureOnly" } },
            ["unresolved"] = from == null || to == null ? new JObject { ["reason"] = "Independently declared missing endpoint" } : null };
        return new JObject {
            ["sessionGeneration"] = "relationship-oracle", ["project"] = new JObject { ["name"] = "Independent relationship oracle", ["projectPath"] = "D:/fixture/oracle.ap21", ["host"] = "fixture" },
            ["nodes"] = nodes, ["edges"] = new JArray(Edge("ab", "A", "B"), Edge("ab-second", "A", "B"), Edge("bc", "B", "C"), Edge("ca", "C", "A"), Edge("self", "A", "A"), Edge("missing-target", "A", null), Edge("missing-source", null, "A"), Edge("disconnected", "X", "Y"), Edge("read", "A", "Z", "reads"), Edge("incoming", "D", "A")),
            ["coverage"] = new JArray(new JObject { ["provider"] = "independentFixture", ["scope"] = "/fixture", ["status"] = "complete", ["visited"] = 8, ["returned"] = 10, ["warnings"] = new JArray(), ["continuationAvailable"] = false }) };
    }
    internal static void Run()
    {
        var store = new ProjectModelStore(); var saved = store.Capture(Fixture()); string id = saved.Value<string>("snapshotId")!;
        void Edges(JObject graph, string expected) => Check.Equal(expected, string.Join(",", graph["edges"]!.Select(e => (string)e["edgeId"]!).OrderBy(x => x, StringComparer.Ordinal)));
        var all = store.Relationships(id, "A", null!, "both", 5, 500, 1000);
        Edges(all, "ab,ab-second,bc,ca,incoming,missing-source,missing-target,read,self");
        Check.Equal(2, all["unresolved"]!.Count()); Check.Equal(false, all.Value<bool>("truncated"));
        Check.Equal("notEstablished", all["relationshipScope"]!.Value<string>("projectCompleteness"));
        var first = store.Relationships(id, "A", new[] { "calls" }, "out", 1, 500, 1000);
        Edges(first, "ab,ab-second,missing-target,self"); Check.Equal(true, first.Value<bool>("truncated"));
        Check.Equal("depth", string.Join(",", first["relationshipScope"]!["stopReasons"]!.Values<string>()));
        Edges(store.Relationships(id, "A", new[] { "calls" }, "in", 1, 500, 1000), "ca,incoming,missing-source,self");
        var cycle = store.Relationships(id, "A", new[] { "calls" }, "out", 3, 500, 1000);
        Edges(cycle, "ab,ab-second,bc,ca,missing-target,self"); Check.Equal(false, cycle.Value<bool>("truncated"));
        var empty = store.Relationships(id, "A", new[] { "unadvertised" }, "both", 5, 500, 1000);
        Edges(empty, ""); Check.Equal("notEstablished", empty["relationshipScope"]!.Value<string>("projectCompleteness"));
        foreach (var bounded in new[] { store.Relationships(id,"A",null!,"both",5,1,1000), store.Relationships(id,"A",null!,"both",5,500,1) }) {
            Check.Equal(true,bounded.Value<bool>("truncated")); Check.Equal(true,bounded["relationshipScope"]!["stopReasons"]!.Any());
        }
        // The real store/export/reopen path must retain source bytes while qualifying incomplete provider coverage.
        var partial = new JObject { ["provider"] = "nativeRelationships", ["scope"] = "/fixture/A", ["status"] = "complete", ["visited"] = 8, ["returned"] = 4, ["warnings"] = new JArray(), ["continuationAvailable"] = false };
        store.RememberRelationships(id, (JArray)first["edges"]!, new JArray(partial), first);
        var exported = store.Get(id); var provider = exported["coverage"]!.Single(c => (string?)c["provider"] == "nativeRelationships");
        Check.Equal("partial", (string?)provider["status"]); Check.Equal("complete", partial.Value<string>("status"));
        Check.Equal(true, JToken.DeepEquals(saved["edges"], new JArray(exported["edges"]!.OrderBy(e => Array.IndexOf(saved["edges"]!.Select(v => (string)v["edgeId"]!).ToArray(), (string)e["edgeId"]!)))));
        Edges(ProjectModelStore.QueryRelationships(JObject.Parse(exported.ToString(Formatting.None)), "A", null!, "both", 5, 500, 1000), "ab,ab-second,bc,ca,incoming,missing-source,missing-target,read,self");
        var before = store.Get(id);
        try { store.RememberRelationships(id,new JArray(first["edges"]![0]!.DeepClone(),first["edges"]![0]!.DeepClone()),new JArray()); throw new Exception("Duplicate observation accepted"); }
        catch (ProjectModelStore.ModelException error) { Check.Equal("InvalidProjectModel",error.Code); }
        Check.Equal(true,JToken.DeepEquals(before,store.Get(id)));
        foreach (var malformed in new[] { "duplicate", "endpoint", "unresolved" }) {
            var doc=Fixture(); if(malformed=="duplicate")((JArray)doc["edges"]!).Add(doc["edges"]![0]!.DeepClone());
            if(malformed=="endpoint")doc["edges"]![0]!["target"]!["objectPath"]="/same-name-other";
            if(malformed=="unresolved")doc["edges"]![5]!["unresolved"]=null;
            try { new ProjectModelStore().Capture(doc); throw new Exception("Malformed graph accepted"); }
            catch(ProjectModelStore.ModelException error){Check.Equal("InvalidProjectModel",error.Code);}
        }
        var virtualDoc=Fixture();
        ((JArray)virtualDoc["nodes"]!).Add(new JObject { ["nodeId"]="display",["parentId"]="root",["name"]="Display folder",["kind"]="virtualFolder",["origin"]="virtual",
            ["metadata"]=new JObject(),["access"]=new JObject { ["state"]="virtual" },["detailAvailability"]="metadata" });
        var virtualEdge=(JObject)virtualDoc["edges"]![0]!.DeepClone();virtualEdge["edgeId"]="virtual-edge";virtualEdge["kind"]="contains";
        virtualEdge["source"]=new JObject { ["nodeId"]="display",["objectPath"]=null };virtualEdge["target"]=new JObject { ["nodeId"]="A",["objectPath"]="/fixture/A" };
        ((JArray)virtualDoc["edges"]!).Add(virtualEdge);
        var virtualExport=new ProjectModelStore().Capture(virtualDoc);
        Edges(ProjectModelStore.QueryRelationships(virtualExport,"A",new[]{"contains"}),"virtual-edge");
        Check.Equal(false,((JObject)virtualExport["nodes"]!.Single(n=>(string?)n["nodeId"]=="display")).ContainsKey("objectPath"));
        string? evidence=Environment.GetEnvironmentVariable("TIA_RELATIONSHIP_EVIDENCE");
        if(!string.IsNullOrWhiteSpace(evidence)){Directory.CreateDirectory(evidence);File.WriteAllText(Path.Combine(evidence,"store-export.json"),exported.ToString(Formatting.None));File.WriteAllText(Path.Combine(evidence,"virtual-endpoint-export.json"),virtualExport.ToString(Formatting.None));}
    }
}
