using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TiaMcp.Indexing;

namespace TiaMcp.Tests;

internal static class ProjectModelTests
{
    internal static Task TechnologyChildSelections()
    {
        var fixture=Fixture(); var nodes=(JArray)fixture["nodes"]!;
        var plc=nodes.Single(n => (string?)n["nodeId"] == "plc-a");
        string ownerPath=(string)plc["objectPath"]!+"/technology/Axis";
        var template=(JObject)nodes.Single(n => (string?)n["nodeId"] == "start-a").DeepClone();
        template["nodeId"]="axis";template["parentId"]="plc-a";template["kind"]="technologyObject";
        template["name"]="Axis";template["objectPath"]=ownerPath;
        template["metadata"]=new JObject { ["ownerPath"]=(string)plc["objectPath"]!+"/technology",["composition"]="TechnologicalObjects" };
        nodes.Add(template);
        foreach (string composition in new[] { "OutputCams","MeasuringInputs" })
        {
            var child=(JObject)template.DeepClone();child["nodeId"]=composition;child["parentId"]="axis";child["name"]="Shared";
            child["objectPath"]=ownerPath+"/children/"+composition+"/Shared";
            child["metadata"]=new JObject { ["ownerPath"]=ownerPath,["composition"]=composition };nodes.Add(child);
        }
        var store=new ProjectModelStore();string id=(string)store.Capture(fixture)["snapshotId"]!;
        Check.Equal(2,store.Children(id,"axis")["nodes"]!.Count());
        var hits=store.Search(id,"Shared","technologyObject",null!,null!)["nodes"]!;
        Check.Equal(2,hits.Count());
        foreach(var hit in hits)
        {
            string childId=(string)hit["nodeId"]!;var reference=store.Reference(id,childId);
            var resolved=store.ResolveReference(reference,"recorded-session");
            Check.Equal(ownerPath,(string?)resolved["metadata"]!["ownerPath"]);
            Check.Equal(childId,(string?)resolved["metadata"]!["composition"]);
            Check.Equal("axis",(string?)hit["ancestors"]!.Last()!["nodeId"]);
        }
        Check.Equal(false,(string?)store.Reference(id,"OutputCams")["objectPath"] == (string?)store.Reference(id,"MeasuringInputs")["objectPath"]);
        return Task.CompletedTask;
    }

    internal static Task HierarchyAndExactSelection()
    {
        var store = new ProjectModelStore(); var captured = store.Capture(Fixture()); string id = (string)captured["snapshotId"]!;
        Check.Equal(28, captured["nodes"]!.Count());
        Check.Equal((long)System.Text.Encoding.UTF8.GetByteCount(captured.ToString(Newtonsoft.Json.Formatting.None)), (long)captured["usage"]!["snapshotBytes"]!);
        Check.Equal("project", (string)store.Children(id, null!)["nodes"]![0]!["nodeId"]!);
        var names = new List<string>(); string? cursor = null;
        do
        {
            var page = store.Children(id, "blocks-a", 1, cursor!);
            names.AddRange(page["nodes"]!.Select(n => (string)n["nodeId"]!)); cursor = (string?)page["nextCursor"];
        } while (cursor != null);
        Check.Equal("empty-a,malicious,nested-a,protected", string.Join(",", names.OrderBy(x => x)));
        Check.Equal(0, store.Children(id, "empty-a")["nodes"]!.Count());
        var search = store.Search(id, "Start", "block", null!, null!);
        Check.Equal(4, search["nodes"]!.Count());
        Check.Equal(4, search["nodes"]!.Select(n => (string)n["objectPath"]!).Distinct(StringComparer.Ordinal).Count());
        var a = store.Reference(id, "start-a"); var b = store.Reference(id, "start-b");
        Check.Equal(false, (string)a["objectPath"]! == (string)b["objectPath"]!);
        Check.Equal("start-a", (string)store.ResolveReference(a, "recorded-session")["nodeId"]!);
        var mismatch = (JObject)a.DeepClone(); mismatch["objectPath"] = b["objectPath"];
        Expect("SelectionNotFound", () => store.ResolveReference(mismatch, "recorded-session"));
        mismatch = (JObject)a.DeepClone(); mismatch["projectPath"] = "D:\\other-copy\\Explorer.ap21";
        Expect("SelectionNotFound", () => store.ResolveReference(mismatch, "recorded-session"));
        Expect("VirtualSelection", () => store.Reference(id, "devices"));
        // Query copies must not grant a caller write access to the retained model.
        captured["nodes"]![0]!["name"] = "Changed";
        Check.Equal("Explorer", (string)store.Get(id)["nodes"]![0]!["name"]!);
        var hit = search["nodes"]!.Single(n => (string)n["nodeId"]! == "unit-start");
        Check.Equal("project,devices,line-a,cpu-a,plc-a,units-a,unit-control,unit-blocks", string.Join(",", hit["ancestors"]!.Select(n => (string)n["nodeId"]!)));
        return Task.CompletedTask;
    }

    internal static Task CursorLifetimeAndSession()
    {
        var now = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
        var store = new ProjectModelStore(() => now); string id = (string)store.Capture(Fixture(), 30)["snapshotId"]!;
        string cursor = (string)store.Children(id, "blocks-a", 1)["nextCursor"]!;
        Expect("InvalidCursor", () => store.Children(id, "blocks-b", 1, cursor));
        Expect("InvalidCursor", () => store.Children(id, "blocks-a", 2, cursor));
        Expect("InvalidCursor", () => store.Search(id, null!, null!, null!, null!, 1, cursor));
        var chars = cursor.ToCharArray(); chars[8] = chars[8] == 'A' ? 'B' : 'A';
        Expect("InvalidCursor", () => store.Children(id, "blocks-a", 1, new string(chars)));
        var reference = store.Reference(id, "start-a");
        Expect("SessionMismatch", () => store.ResolveReference(reference, "reconnected-session"));
        now = now.AddSeconds(31);
        Expect("ModelExpired", () => store.Get(id));
        Expect("ModelNotFound", () => store.Get(id));
        id = (string)store.Capture(Fixture())["snapshotId"]!;
        store.Invalidate("project_close"); Expect("ModelInvalidated", () => store.Children(id, null!));
        Check.Equal(true, store.Release(id)); Check.Equal(false, store.Release(id));
        return Task.CompletedTask;
    }

    internal static Task InvalidAncestryAndBudgets()
    {
        var fixture = Fixture(); fixture["nodes"]![1]!["parentId"] = "missing";
        Expect("InvalidModelAncestry", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); fixture["nodes"]![0]!["parentId"] = "empty-a";
        Expect("InvalidModelAncestry", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); fixture["nodes"]![1]!["objectPath"] = "/virtual/is/not/native";
        Expect("InvalidProjectModel", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); ((JArray)fixture["nodes"]!).Add(fixture["nodes"]![0]!.DeepClone());
        Expect("AmbiguousModelIdentity", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); fixture["nodes"]![0]!["metadata"]!["huge"] = new string('x', 65536);
        Expect("ModelCapacity", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); fixture["edges"]![0]!["target"]!["objectPath"] = "/wrong-object";
        Expect("InvalidProjectModel", () => new ProjectModelStore().Capture(fixture));
        fixture = Fixture(); fixture["limits"]!["maxBytes"] = 1500;
        Expect("ModelCapacity", () => new ProjectModelStore().Capture(fixture));
        // Parent traversal must terminate at the documented depth ceiling, even
        // when every parent exists and the chain is acyclic.
        fixture = Fixture(); fixture["edges"] = new JArray(); var chain = new JArray();
        for (int depth = 0; depth <= 64; depth++) chain.Add(new JObject { ["nodeId"] = "depth-" + depth, ["parentId"] = depth == 0 ? null : "depth-" + (depth - 1),
            ["kind"] = "blockGroup", ["name"] = "Depth " + depth, ["origin"] = "native", ["objectPath"] = "/depth/" + depth, ["access"] = new JObject { ["state"] = "readable" },
            ["detailAvailability"] = "metadata", ["metadata"] = new JObject() });
        fixture["nodes"] = chain;
        Check.Equal(65, new ProjectModelStore().Capture(fixture)["nodes"]!.Count());
        var tooDeep = (JObject)chain[64]!.DeepClone(); tooDeep["nodeId"] = "depth-65"; tooDeep["parentId"] = "depth-64"; tooDeep["objectPath"] = "/depth/65"; chain.Add(tooDeep);
        Expect("ModelCapacity", () => new ProjectModelStore().Capture(fixture));
        return Task.CompletedTask;
    }

    internal static Task GraphsAndSeparateFreshness()
    {
        ProjectModelRelationshipTests.Run();
        var store = new ProjectModelStore(); string id = (string)store.Capture(Fixture())["snapshotId"]!;
        var graph = store.Relationships(id, "start-a", new[] { "calls" }, "out", 5, 500, 1000);
        Check.Equal(2, graph["nodes"]!.Count()); Check.Equal(3, graph["edges"]!.Count());
        Check.Equal(1, graph["unresolved"]!.Count()); Check.Equal(false, (bool)graph["truncated"]!);
        var bounded = store.Relationships(id, "start-a", null!, "both", 5, 2, 2);
        Check.Equal(true, (bool)bounded["truncated"]!);
        Check.Equal(true, bounded["nodes"]!.Count() <= 2 && bounded["edges"]!.Count() <= 2);
        Expect("InvalidRequest", () => store.Relationships(id, "start-a", null!, "both", 6));
        var before = store.Get(id)["metadataRevision"]!.DeepClone();
        store.RememberDetails(id, "start-a", new JObject { ["capturedAtUtc"] = DateTime.UtcNow.ToString("O"), ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = "blockInterface" } });
        var after = store.Get(id); Check.Equal(true, JToken.DeepEquals(before, after["metadataRevision"]));
        Check.Equal(false, (bool)after["metadataRevision"]!["includesContent"]!);
        Check.Equal("observationsCapturedSeparately", (string)after["contentFreshness"]!);
        var invalid = (JObject)after["edges"]![0]!.DeepClone(); invalid["target"]!["objectPath"] = "/renamed";
        Expect("InvalidProjectModel", () => store.RememberRelationships(id, new JArray(invalid), new JArray()));
        Check.Equal(true, JToken.DeepEquals(after, store.Get(id)));
        var narrowDocument = Fixture(); narrowDocument["limits"]!["maxBytes"] = (long)after["usage"]!["snapshotBytes"]! + 1000;
        var narrowStore = new ProjectModelStore(); var narrow = narrowStore.Capture(narrowDocument); string narrowId = (string)narrow["snapshotId"]!;
        Expect("ModelCapacity", () => narrowStore.RememberDetails(narrowId, "start-a", new JObject { ["hugeObservation"] = new string('x', 3000) }));
        Check.Equal(true, JToken.DeepEquals(narrow, narrowStore.Get(narrowId)));
        return Task.CompletedTask;
    }

    internal static Task ProviderContractIntegration()
    {
        var store = new ProjectModelStore(); string id = (string)store.Capture(Fixture())["snapshotId"]!;
        var reference = store.Reference(id, "start-a"); var node = store.ResolveReference(reference, "recorded-session");
        var request = new ProjectModelProviderRequest(reference, node, "typedPreview");
        var absent = new ProjectModelProviders().Inspect(request);
        Check.Equal("unsupported", (string)absent["status"]!); Check.Equal(0, absent["attachments"]!.Count());
        var registry = new ProjectModelProviders(new[] { new FixtureProvider() });
        var result = registry.Inspect(request);
        Check.Equal("partial", (string)result["status"]!);
        Check.Equal(true, JToken.DeepEquals(reference, result["reference"]));
        Check.Equal("ladFbdNetwork", (string)result["attachments"]![0]!["kind"]!);
        store.RememberDetails(id, "start-a", new JObject { ["previews"] = result });
        Check.Equal(true, JToken.DeepEquals(result, store.Get(id)["nodes"]!.Single(n => (string)n["nodeId"]! == "start-a")["details"]!["previews"]));
        result["contentRevision"]!["scope"] = "exposedMetadata";
        Expect("InvalidProviderResponse", () => ProjectModelProviders.Validate(result, request, "fixture-reader"));
        result = registry.Inspect(request); result["reference"]!["objectPath"] = "/same-name-other-copy";
        Expect("InvalidProviderResponse", () => ProjectModelProviders.Validate(result, request, "fixture-reader"));
        var hmiRef = store.Reference(id, "hmi");
        var hmi = new ProjectModelProviders().Inspect(new ProjectModelProviderRequest(hmiRef, store.ResolveReference(hmiRef, "recorded-session"), "hmiPreview"));
        Check.Equal("hmi", (string)hmi["provider"]!); Check.Equal("unsupported", (string)hmi["status"]!);
        Check.Equal<JToken?>(null, store.ResolveReference(hmiRef, "recorded-session")["plcPath"]);
        var longReference = (JObject)reference.DeepClone(); var longNode = (JObject)node.DeepClone();
        longReference["objectPath"] = longNode["objectPath"] = "/projects/" + new string('a', 2048);
        var smallRequest = new ProjectModelProviderRequest(longReference, longNode, "typedPreview", maxBytes: 1024);
        Expect("ModelCapacity", () => new ProjectModelProviders().Inspect(smallRequest));
        Expect("ModelCapacity", () => new ProjectModelProviders(new[] { new FailingProvider() }).Inspect(smallRequest));
        var malformed = (JObject)reference.DeepClone(); malformed.Remove("sessionGeneration");
        Expect("InvalidSelection", () => new ProjectModelProviderRequest(malformed, node, "typedPreview"));
        return Task.CompletedTask;
    }

    internal static Task GraphSerializedByteLimit()
    {
        var document = Fixture(); var original = (JObject)document["edges"]!.Single(e => (string)e["edgeId"]! == "unknown-1").DeepClone();
        var edges = new JArray();
        for (int i = 0; i < 20; i++)
        {
            var edge = (JObject)original.DeepClone(); edge["edgeId"] = "large-unresolved-" + i;
            edge["evidence"]!["native"] = new JObject { ["details"] = new string('x', 60000) };
            edges.Add(edge);
        }
        document["edges"] = edges;
        ((JArray)document["coverage"]!).Add(new JObject { ["provider"] = "largeCoverage", ["scope"] = "fixture", ["status"] = "partial", ["visited"] = 20, ["returned"] = 20,
            ["warnings"] = new JArray(new string('w', 20000)), ["continuationAvailable"] = false });
        var store = new ProjectModelStore(); string id = (string)store.Capture(document)["snapshotId"]!;
        var graph = store.Relationships(id, "start-a", null!, "both", 5, 500, 1000);
        int serialized = System.Text.Encoding.UTF8.GetByteCount(graph.ToString(Newtonsoft.Json.Formatting.None));
        Check.Equal(true, serialized <= 448 * 1024);
        Check.Equal(serialized, (int)graph["payloadBytes"]!);
        Check.Equal(true, (bool)graph["truncated"]!);
        Check.Equal(true, graph["relationshipScope"]!["stopReasons"]!.Values<string>().Contains("bytes"));
        Check.Equal(true, graph["nodes"]!.Any(n => (string?)n["nodeId"] == "start-a"));
        Check.Equal(true, graph["edges"]!.Count() > 0 && graph["edges"]!.Count() < 20);
        Check.Equal(graph["edges"]!.Count(), graph["unresolved"]!.Count());
        graph["nativeCoverage"] = new JObject { ["evidence"] = new string('a', 60000) };
        Check.Equal(true, System.Text.Encoding.UTF8.GetByteCount(graph.ToString(Newtonsoft.Json.Formatting.None)) <= ProjectModelStore.MaximumPageBytes);
        return Task.CompletedTask;
    }

    internal static Task CachedPerformance()
    {
        foreach (int count in new[] { 1000, 10000, 50000 })
        {
            var document = Fixture(); var nodes = new JArray();
            for (int i = 0; i < count; i++) nodes.Add(new JObject { ["nodeId"] = "n" + i, ["parentId"] = i == 0 ? null : "n0", ["kind"] = i == 0 ? "project" : "block",
                ["name"] = "Node " + i.ToString("D5"), ["origin"] = "native", ["objectPath"] = "/objects/" + i, ["access"] = new JObject { ["state"] = "readable" }, ["detailAvailability"] = "metadata", ["metadata"] = new JObject() });
            document["nodes"] = nodes; document["edges"] = new JArray();
            var store = new ProjectModelStore(); var capture = Stopwatch.StartNew(); var snapshot = store.Capture(document); capture.Stop();
            string id = (string)snapshot["snapshotId"]!;
            var timings = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                var watch = Stopwatch.StartNew(); store.Search(id, "Node", "block", null!, null!); store.Children(id, "n0"); watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
            }
            double p95 = timings.Skip(1).OrderBy(x => x).ElementAt(18);
            Console.WriteLine($"MODEL PERF syntheticNodes={count} captureMs={capture.Elapsed.TotalMilliseconds:F1} coldCombinedQueryMs={timings[0]:F1} warmCombinedCachedSearchChildrenP95Ms={p95:F1} warmRuns=19 bytes={snapshot["usage"]!["snapshotBytes"]} processPeakBytes={Process.GetCurrentProcess().PeakWorkingSet64}. This is pure cache capture, not native TIA capture.");
            Check.Equal(true, store.Search(id, "Node", "block", null!, null!)["nodes"]!.Count() == 100);
        }
        return Task.CompletedTask;
    }

    private static JObject Fixture()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            string path = Path.Combine(directory, "fixtures", "project-explorer", "representative.project-model.json");
            if (File.Exists(path)) return JObject.Parse(File.ReadAllText(path));
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Project model fixture must be packaged with tests.");
    }

    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (ProjectModelStore.ModelException error) { Check.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected model error: " + code);
    }

    private sealed class FixtureProvider : IProjectModelProvider
    {
        public string ProviderId => "fixture-reader";
        public bool Supports(JObject node, string facet) => (string)node["kind"]! == "block" && facet == "typedPreview";
        public JObject Inspect(ProjectModelProviderRequest request)
        {
            var revision = new JObject { ["status"] = "available", ["scope"] = "graphicalNetwork", ["algorithm"] = "sha256", ["value"] = new string('b', 64) };
            var response = ProjectModelProviders.Describe(request.Reference, request.Facet);
            response["provider"] = ProviderId; response["status"] = "partial"; response["contentRevision"] = revision;
            response["attachments"] = new JArray(new JObject { ["kind"] = "ladFbdNetwork", ["source"] = new JObject { ["objectPath"] = request.Reference["objectPath"], ["contentRevision"] = revision.DeepClone(), ["capturedAtUtc"] = DateTime.UtcNow.ToString("O") },
                ["content"] = new JObject { ["language"] = "LAD", ["nodes"] = new JArray(new JObject { ["key"] = "opaque-1", ["kind"] = "opaqueNative" }) }, ["coverage"] = new JObject { ["status"] = "partial", ["unknownNodes"] = 1 } });
            return response;
        }
    }

    private sealed class FailingProvider : IProjectModelProvider
    {
        public string ProviderId => "failing-reader";
        public bool Supports(JObject node, string facet) => true;
        public JObject Inspect(ProjectModelProviderRequest request) => throw new InvalidOperationException("Unavailable optional provider.");
    }
}
