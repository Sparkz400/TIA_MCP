using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class ProjectReviewImpactTests
{
    private const string Root = "/projects/Impact";
    private const string Xml = "<Document><Block><Interface><Member Name='Enable' Datatype='Bool'/></Interface><NetworkSource><Token Text='Enable'/></NetworkSource></Block></Document>";
    private static string PathOf(string name) => Root + "/plc/blocks/" + name;

    internal static async Task Run()
    {
        DiamondsCyclesAndEvidence();
        SnapshotSidesAndScopeAbsence();
        ExcludedEdgesAndExactIdentity();
        TraversalBounds();
        SerializedByteBound();
        using var fixture = new Fixture();
        string id = fixture.Save(Model(["A"], []));
        foreach (var limits in new[] { (0, 100, 5000), (6, 100, 5000), (3, 0, 5000), (3, 201, 5000), (3, 100, 0), (3, 100, 20001) })
        {
            var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(fixture.Store.Compare(id, id,
                impactDepth: limits.Item1, maxImpactPaths: limits.Item2, maxImpactEdgeVisits: limits.Item3)));
            Check.Equal("InvalidRequest", error.Code);
        }
    }

    private static void DiamondsCyclesAndEvidence()
    {
        using var fixture = new Fixture();
        // Deliberately unsorted, with two independent paths from D to the changed A.
        var model = Model(["D", "C", "B", "A"], [Edge("D", "C"), Edge("A", "D"), Edge("C", "A"), Edge("D", "B"), Edge("B", "A")]);
        string before = fixture.Save(model), after = fixture.Save(model, changed: true);
        var result = fixture.Store.Compare(before, after);
        var paths = result["observedImpactPaths"]!.AsArray();
        Check.Equal(8, paths.Count);
        Check.Equal(false, result["impactTraversal"]!["truncated"]!.GetValue<bool>());
        Check.Equal(false, result["impactTraversal"]!["complete"]!.GetValue<bool>());
        Check.Equal(Encoding.UTF8.GetByteCount(paths.ToJsonString(BridgeProtocol.Json)), result["impactTraversal"]!["pathBytes"]!.GetValue<int>());
        foreach (string side in new[] { "before", "after" })
        {
            var rows = paths.Where(p => p!["reviewSide"]!.GetValue<string>() == side).ToArray();
            Check.Equal("B,C,D,D", string.Join(",", rows.Select(p => p!["affectedObjectPath"]!.GetValue<string>().Split('/').Last())));
            Check.Equal("1,1,2,2", string.Join(",", rows.Select(p => p!["distance"]!.GetValue<int>())));
            foreach (var row in rows)
            {
                Check.Equal(side == "before" ? before : after, row!["reviewId"]!.GetValue<string>());
                Check.Equal(true, row["changedFields"]!.AsArray().Any(f => f!.GetValue<string>() == "interfaceChanged"));
                var sequence = row["objectPaths"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
                Check.Equal(sequence.Length, sequence.Distinct(StringComparer.Ordinal).Count());
                Check.Equal(PathOf("A"), sequence.Last());
                var edges = row["edges"]!.AsArray();
                for (int i = 0; i < edges.Count; i++)
                {
                    Check.Equal(sequence[i], edges[i]!["source"]!["objectPath"]!.GetValue<string>());
                    Check.Equal(sequence[i + 1], edges[i]!["target"]!["objectPath"]!.GetValue<string>());
                    var original = model["edges"]!.AsArray().Single(e => e!["edgeId"]!.GetValue<string>() == edges[i]!["edgeId"]!.GetValue<string>());
                    Check.Equal(true, JsonNode.DeepEquals(original, edges[i]));
                }
            }
            Check.Equal(PathOf("B"), rows[2]!["objectPaths"]![1]!.GetValue<string>());
            Check.Equal(PathOf("C"), rows[3]!["objectPaths"]![1]!.GetValue<string>());
        }
        // Impact analysis covers all changes, independent of the changes page.
        Check.Equal(true, JsonNode.DeepEquals(paths, fixture.Store.Compare(before, after, offset: 1, maxItems: 1)["observedImpactPaths"]));
        Check.Equal(0, fixture.Store.Compare(before, before)["observedImpactPaths"]!.AsArray().Count);
        model["edges"] = new JsonArray(model["edges"]!.AsArray().Reverse().Select(e => e!.DeepClone()).ToArray());
        string reordered = fixture.Save(model, changed: true);
        var repeated = fixture.Store.Compare(before, reordered)["observedImpactPaths"]!.AsArray();
        foreach (var row in repeated.Where(p => p!["reviewSide"]!.GetValue<string>() == "after")) row!["reviewId"] = after;
        Check.Equal(true, JsonNode.DeepEquals(paths, repeated));
    }

    private static void SnapshotSidesAndScopeAbsence()
    {
        using var fixture = new Fixture();
        string before = fixture.Save(Model(["A", "B", "C"], [Edge("B", "A")]));
        string after = fixture.Save(Model(["A", "B", "C"], [Edge("C", "A")]), changed: true);
        var paths = fixture.Store.Compare(before, after)["observedImpactPaths"]!.AsArray();
        Check.Equal(2, paths.Count);
        Check.Equal("before", paths[0]!["reviewSide"]!.GetValue<string>());
        Check.Equal(PathOf("B"), paths[0]!["affectedObjectPath"]!.GetValue<string>());
        Check.Equal("after", paths[1]!["reviewSide"]!.GetValue<string>());
        Check.Equal(PathOf("C"), paths[1]!["affectedObjectPath"]!.GetValue<string>());

        string partial = fixture.Save(Model(["B", "C"], []), includeDocument: false);
        var removed = fixture.Store.Compare(before, partial);
        Check.Equal("removedFromCapturedScope", removed["changes"]![0]!["change"]!.GetValue<string>());
        Check.Equal(1, removed["observedImpactPaths"]!.AsArray().Count);
        Check.Equal("before", removed["observedImpactPaths"]![0]!["reviewSide"]!.GetValue<string>());
        Check.Equal("removedFromCapturedScope", removed["observedImpactPaths"]![0]!["change"]!.GetValue<string>());
        var added = fixture.Store.Compare(partial, before);
        Check.Equal("after", added["observedImpactPaths"]![0]!["reviewSide"]!.GetValue<string>());
        Check.Equal("addedToCapturedScope", added["observedImpactPaths"]![0]!["change"]!.GetValue<string>());

        var differentCopy = JsonNode.Parse(Model(["A", "B"], [Edge("B", "A")]).ToJsonString().Replace(Root, "/projects/Other"))!.AsObject();
        differentCopy["project"]!["projectPath"] = "other.ap21";
        string other = fixture.Save(differentCopy, changed: true, documentPath: "/projects/Other/plc/blocks/A");
        var crossCopy = fixture.Store.Compare(before, other, allowDifferentProjectCopy: true)["observedImpactPaths"]!.AsArray();
        Check.Equal(PathOf("A"), crossCopy[0]!["changedObjectPath"]!.GetValue<string>());
        Check.Equal("/projects/Other/plc/blocks/A", crossCopy[1]!["changedObjectPath"]!.GetValue<string>());
        Check.Equal("/plc/blocks/A", crossCopy[1]!["changedPath"]!.GetValue<string>());
    }

    private static void ExcludedEdgesAndExactIdentity()
    {
        using var fixture = new Fixture();
        var unresolved = Edge("C", "A"); unresolved["unresolved"] = new JsonObject { ["reason"] = "native target not resolved" };
        var inferred = Edge("D", "A"); inferred["evidence"]!["origin"] = "inferred";
        var partial = Edge("E", "A"); partial["evidence"]!["resolution"] = "partial";
        var wrongId = Edge("F", "A"); wrongId["source"]!["nodeId"] = "some-other-node";
        var wrongResolution = Edge("G", "A"); wrongResolution["target"]!["resolution"] = "unresolved";
        var missingId = Edge("H", "A"); missingId["source"]!.AsObject().Remove("nodeId");
        var containment = Edge("H", "A"); containment["kind"] = "contains"; containment["edgeId"] = "contains-H-A";
        var crossProject = Edge("B", "A"); crossProject["edgeId"] = "cross-project";
        crossProject["source"]!["objectPath"] = "/projects/Other/plc/blocks/B";
        var unknownKind = Edge("C", "A"); unknownKind["edgeId"] = "unknown-kind"; unknownKind["kind"] = "possibleCall";
        var unknownEvidence = Edge("D", "A"); unknownEvidence["edgeId"] = "unknown-evidence"; unknownEvidence["evidence"]!["origin"] = "futureEvidence";
        var valid = Edge("B", "A"); valid["evidence"]!["origin"] = "parsedSupported";
        // Endpoint resolution is optional in project-model.v1; edge evidence carries resolution.
        valid["source"]!.AsObject().Remove("resolution"); valid["target"]!.AsObject().Remove("resolution");
        var model = Model(["A", "B", "C", "D", "E", "F", "G", "H"],
            [valid, unresolved, inferred, partial, wrongId, wrongResolution, missingId, containment, crossProject, unknownKind, unknownEvidence, Edge("Outside", "A")]);
        var result = fixture.Store.Compare(fixture.Save(model), fixture.Save(model, changed: true));
        Check.Equal(2, result["observedImpactPaths"]!.AsArray().Count);
        foreach (var exclusion in result["impactTraversal"]!["excludedEdges"]!.AsArray())
        {
            Check.Equal(2, exclusion!["counts"]!["unsupportedRelationshipKind"]!.GetValue<int>());
            Check.Equal(4, exclusion["counts"]!["unresolvedOrUnsupportedEvidence"]!.GetValue<int>());
            Check.Equal(5, exclusion["counts"]!["endpointOutsideCapturedNodesOrIdentityMismatch"]!.GetValue<int>());
        }
        Check.Equal(false, result["impactTraversal"]!["truncated"]!.GetValue<bool>());
        Check.Equal(false, result["impactTraversal"]!["complete"]!.GetValue<bool>());
        foreach (string name in new[] { "A", "B" })
        {
            var virtualModel = Model(["A", "B"], [Edge("B", "A")]);
            virtualModel["nodes"]!.AsArray().Single(n => n!["objectPath"]!.GetValue<string>() == PathOf(name))!["origin"] = "virtual";
            string virtualBefore = fixture.Save(virtualModel, includeDocument: false);
            virtualModel["nodes"]!.AsArray().Single(n => n!["objectPath"]!.GetValue<string>() == PathOf("A"))!["name"] = "Changed";
            var virtualResult = fixture.Store.Compare(virtualBefore, fixture.Save(virtualModel, includeDocument: false));
            Check.Equal(0, virtualResult["observedImpactPaths"]!.AsArray().Count);
            foreach (var exclusion in virtualResult["impactTraversal"]!["excludedEdges"]!.AsArray())
                Check.Equal(1, exclusion!["counts"]!["endpointOutsideCapturedNodesOrIdentityMismatch"]!.GetValue<int>());
        }
    }

    private static void TraversalBounds()
    {
        using var fixture = new Fixture();
        var model = Model(["A", "B", "C"], [Edge("C", "B"), Edge("B", "A")]);
        string before = fixture.Save(model), after = fixture.Save(model, changed: true);
        var depth = fixture.Store.Compare(before, after, impactDepth: 1);
        Check.Equal(2, depth["observedImpactPaths"]!.AsArray().Count);
        Check.Equal("impactDepth", depth["impactTraversal"]!["truncationReasons"]![0]!.GetValue<string>());
        var count = fixture.Store.Compare(before, after, maxImpactPaths: 1);
        Check.Equal(1, count["observedImpactPaths"]!.AsArray().Count);
        Check.Equal("maxImpactPaths", count["impactTraversal"]!["truncationReasons"]![0]!.GetValue<string>());
        var work = fixture.Store.Compare(before, after, maxImpactEdgeVisits: 1);
        Check.Equal(1, work["observedImpactPaths"]!.AsArray().Count);
        Check.Equal(1, work["impactTraversal"]!["visitedEdges"]!.GetValue<int>());
        Check.Equal("maxImpactEdgeVisits", work["impactTraversal"]!["truncationReasons"]![0]!.GetValue<string>());
        var exact = fixture.Store.Compare(before, after, impactDepth: 2, maxImpactPaths: 4, maxImpactEdgeVisits: 4);
        Check.Equal(4, exact["observedImpactPaths"]!.AsArray().Count);
        Check.Equal(false, exact["impactTraversal"]!["truncated"]!.GetValue<bool>());
        Check.Equal(false, exact["impactTraversal"]!["complete"]!.GetValue<bool>());
    }

    private static void SerializedByteBound()
    {
        using var fixture = new Fixture();
        var edge = Edge("B", "A"); edge["evidence"]!["location"] = new string('x', 600000);
        var model = Model(["A", "B"], [edge]);
        var result = fixture.Store.Compare(fixture.Save(model), fixture.Save(model, changed: true));
        Check.Equal(1, result["observedImpactPaths"]!.AsArray().Count);
        Check.Equal("maxImpactPathBytes", result["impactTraversal"]!["truncationReasons"]![0]!.GetValue<string>());
        int bytes = Encoding.UTF8.GetByteCount(result["observedImpactPaths"]!.ToJsonString(BridgeProtocol.Json));
        Check.Equal(bytes, result["impactTraversal"]!["pathBytes"]!.GetValue<int>());
        Check.Equal(true, bytes <= 1024 * 1024);
        Check.Equal(true, JsonNode.DeepEquals(edge, result["observedImpactPaths"]![0]!["edges"]![0]));
    }

    private static JsonObject Edge(string source, string target) => new() {
        ["edgeId"] = source + "->" + target, ["kind"] = "calls",
        ["source"] = new JsonObject { ["nodeId"] = source, ["objectPath"] = PathOf(source), ["resolution"] = "resolved" },
        ["target"] = new JsonObject { ["nodeId"] = target, ["objectPath"] = PathOf(target), ["resolution"] = "resolved" },
        ["evidence"] = new JsonObject { ["origin"] = "nativeConfirmed", ["resolution"] = "resolved", ["sourcePath"] = PathOf(source),
            ["location"] = new JsonObject { ["network"] = 7, ["opaque"] = "retained exactly" }, ["capturedAtUtc"] = "2026-10-12T00:00:00Z",
            ["contentRevision"] = new JsonObject { ["status"] = "unavailable", ["scope"] = "nativeCrossReferenceObservation" } }, ["unresolved"] = null };

    private static JsonObject Model(string[] blocks, JsonObject[] edges) => new() {
        ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["host"] = "machine", ["projectPath"] = "impact.ap21" },
        ["nodes"] = new JsonArray(new[] { new JsonObject { ["nodeId"] = "project", ["kind"] = "project", ["objectPath"] = Root, ["name"] = "Impact" } }
            .Concat(blocks.Select(b => new JsonObject { ["nodeId"] = b, ["kind"] = "block", ["objectPath"] = PathOf(b), ["name"] = b })).Cast<JsonNode?>().ToArray()),
        ["edges"] = new JsonArray(edges.Cast<JsonNode?>().ToArray()), ["coverage"] = new JsonArray() };

    private sealed class Fixture : IDisposable
    {
        private string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tia-review-impact-" + Guid.NewGuid().ToString("N"));
        internal ProjectReviewStore Store => new(DirectoryPath);
        internal string Save(JsonObject model, bool changed = false, bool includeDocument = true, string? documentPath = null)
        {
            string xml = changed ? Xml.Replace("Datatype='Bool'", "Datatype='Int'") : Xml;
            var docs = includeDocument ? new JsonArray(new JsonObject { ["objectPath"] = documentPath ?? PathOf("A"), ["status"] = "exported", ["xml"] = xml,
                ["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))) }) : new JsonArray();
            return Store.Save(model, docs, new JsonObject())["reviewId"]!.GetValue<string>();
        }
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
}
