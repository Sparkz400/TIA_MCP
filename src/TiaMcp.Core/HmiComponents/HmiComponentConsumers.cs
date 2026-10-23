using System.Text.Json.Nodes;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Bounded incoming context from each exact saved snapshot, never generated or joined relationships.</summary>
internal static class HmiComponentConsumers
{
    internal static void Add(JsonObject report, JsonObject request, Func<string, JsonObject> readReview)
    {
        var limits = HmiComponentReview.Object(request["impactLimits"]);
        HmiComponentReview.Fields(limits, "impactDepth maxImpactPaths maxImpactEdgeVisits");
        int depth = Limit("impactDepth", 5), maximum = Limit("maxImpactPaths", 200), visits = Limit("maxImpactEdgeVisits", 20000);
        var paths = new JsonArray(); var coverage = new JsonArray();
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            if (request[scope] is null) continue;
            var selection = request[scope]!;
            string id = (string)selection["reviewId"]!;
            Capture(scope, id, readReview(id)["model"]!.AsObject(), selection["objectPaths"]!.AsArray().Select(p => (string)p!).ToArray(), new());
        }
        var destination = request["destination"]!; string destinationId = (string)destination["reviewId"]!, hmi = (string)destination["hmiPath"]!;
        var model = readReview(destinationId)["model"]!.AsObject();
        var nodes = model["nodes"]!.AsArray().OfType<JsonObject>().ToDictionary(n => (string)n["nodeId"]!, StringComparer.Ordinal);
        var roots = new List<string>(); var excluded = new JsonArray();
        foreach (var output in report["outputs"]!.AsArray())
        {
            var node = output!["capturedObject"] as JsonObject;
            if (node is null || (string?)node["metadata"]?["hmiIdentity"]?["family"] != (string?)destination["family"] ||
                (string?)node["metadata"]?["hmiIdentity"]?["hmiPath"] != hmi || !Within(node))
                excluded.Add(output["id"]!.DeepClone());
            else roots.Add((string)node["objectPath"]!);
        }
        Capture("destination", destinationId, model, roots.ToArray(), excluded);
        report["schemaVersion"] = "hmi-component-review.v2";
        report["consumerPaths"] = paths; report["consumerCoverage"] = coverage;
        report["summary"]!["consumerPathCount"] = paths.Count;
        report["summary"]!["consumerScopeCount"] = coverage.Count;
        report["summary"]!["truncatedConsumerScopes"] = coverage.Count(c => (bool)c!["traversal"]!["truncated"]!);
        return;

        int Limit(string field, int maximumValue) => limits[field] is JsonValue v && v.TryGetValue<int>(out int n) && n >= 1 && n <= maximumValue
            ? n : throw HmiComponentReview.Error("Consumer traversal limit out of range: " + field);
        bool Within(JsonObject node)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add((string)node["nodeId"]!))
            {
                if ((string?)node["origin"] == "virtual") return false;
                if ((string?)node["objectPath"] == hmi) return true;
                if ((string?)node["parentId"] is not string parent || !nodes.TryGetValue(parent, out node!)) return false;
            }
            return false;
        }
        void Capture(string scope, string reviewId, JsonObject captured, string[] selected, JsonArray omitted)
        {
            var ordered = selected.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var changes = new JsonArray(ordered.Select(p => (JsonNode?)new JsonObject { ["path"] = p, ["afterObjectPath"] = p,
                ["change"] = "selectedContextRoot", ["changedFields"] = new JsonArray() }).ToArray());
            // Reuse the shared traversal for one actual snapshot; the empty side is never exposed as captured evidence.
            var empty = new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };
            var result = ProjectReviewStore.ObservedImpactPaths(reviewId, reviewId, empty, captured, changes, depth, maximum, visits);
            foreach (var path in result["paths"]!.AsArray())
                paths.Add(new JsonObject { ["scope"] = scope, ["reviewId"] = reviewId, ["rootObjectPath"] = path!["changedObjectPath"]!.DeepClone(),
                    ["consumerObjectPath"] = path["affectedObjectPath"]!.DeepClone(), ["distance"] = path["distance"]!.DeepClone(),
                    ["objectPaths"] = path["objectPaths"]!.DeepClone(), ["edges"] = path["edges"]!.DeepClone() });
            var traversal = result["coverage"]!.DeepClone().AsObject();
            traversal["excludedEdges"] = traversal["excludedEdges"]![1]!["counts"]!.DeepClone();
            traversal["policy"] = "Incoming saved dependency context for selected roots in this exact review only. No component-version or destination edges are joined. Supplied bindings and proposed outputs never create graph edges. Paths do not establish regeneration effects, member compatibility, live identity or complete consumers.";
            coverage.Add(new JsonObject { ["scope"] = scope, ["reviewId"] = reviewId,
                ["roots"] = new JsonArray(ordered.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["excludedOutputIds"] = omitted, ["traversal"] = traversal });
        }
    }
}
