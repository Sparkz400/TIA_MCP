using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Core;

public sealed partial class ProjectReviewStore
{
    internal static JsonObject AlarmContext(string reviewId, JsonObject review, JsonNode? value)
    {
        if (value is not JsonObject selector || selector.Count != 3 || selector.Any(p => p.Key is not ("reviewId" or "nodeId" or "objectPath")) ||
            new[] { "reviewId", "nodeId", "objectPath" }.Any(key => selector[key] is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > 4096))
            throw new BridgeException("InvalidRequest", "alarmContext requires only exact reviewId, nodeId and objectPath strings.");
        var model = review["model"]!.AsObject(); var nodes = NativeNodes(model);
        string path = Text(selector, "objectPath");
        if (Text(selector, "reviewId") != reviewId || !ExactNativeEndpoint(selector, nodes) ||
            Text(nodes[path], "kind") is not ("alarm" or "supervision" or "hmiAlarm"))
            throw new BridgeException("AlarmContextUnresolved", "Select an exact nonvirtual alarm, supervision or hmiAlarm node in this saved review; names and cross-review identities cannot substitute.");
        const int maxBytes = 256 * 1024;
        var ancestors = new JsonArray(); var relationships = new JsonArray();
        var result = new JsonObject {
            ["reviewId"] = reviewId, ["selector"] = selector.DeepClone(), ["object"] = nodes[path].DeepClone(),
            ["ancestry"] = ancestors, ["ancestryStatus"] = "unresolved", ["relationships"] = relationships,
            ["sourceAuthenticityVerified"] = false, ["nativeQualified"] = false, ["storageSelected"] = false,
            ["coverageComplete"] = false, ["freshness"] = "savedSnapshotOnly",
            ["limits"] = new JsonObject { ["maxAncestors"] = 64, ["maxRelationships"] = 100, ["maxBytes"] = maxBytes },
            ["policy"] = "Explicit supplied alarm context only. Captured parents may include virtual navigation; this does not establish native ownership. Direct relationships are not a full dependency graph or executable storage selection. Use saved-review comparison for transitive impacts. Missing evidence, protected source and unknown storage remain gaps." };
        bool Fits() => Encoding.UTF8.GetByteCount(result.ToJsonString(BridgeProtocol.Json)) <= maxBytes - 2048;
        if (!Fits()) throw new BridgeException("ArtifactCapacity", "Selected alarm metadata exceeds the context byte budget; the original review remains retained.");
        var ids = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in Rows(model["nodes"]).OfType<JsonObject>())
        {
            string id = Text(row, "nodeId");
            if (id.Length == 0 || !ids.TryAdd(id, row)) throw Invalid("Alarm context requires unique captured node IDs.");
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { Text(selector, "nodeId") };
        var current = nodes[path]; string ancestryStatus;
        while (true)
        {
            string parent = Text(current, "parentId");
            if (Text(current, "kind") == "project") { ancestryStatus = "capturedToProject"; break; }
            if (parent.Length == 0 || !ids.TryGetValue(parent, out var ancestor)) { ancestryStatus = "missingParent"; break; }
            if (!visited.Add(parent)) { ancestryStatus = "cycle"; break; }
            if (ancestors.Count == 64) { ancestryStatus = "ancestorLimit"; break; }
            ancestors.Add(ancestor.DeepClone());
            if (!Fits()) { ancestors.RemoveAt(ancestors.Count - 1); ancestryStatus = "byteLimit"; break; }
            current = ancestor;
        }
        result["ancestryStatus"] = ancestryStatus;
        int adjacent = 0, omitted = 0, unusable = 0, index = 0;
        foreach (var edge in Rows(model["edges"]))
        {
            int edgeIndex = index++;
            if (!new[] { "source", "target" }.Any(side => Text(edge?[side], "objectPath") == path || Text(edge?[side], "nodeId") == Text(selector, "nodeId"))) continue;
            adjacent++;
            string status = edge?["unresolved"] is not null || Text(edge?["evidence"], "resolution") != "resolved" ||
                Text(edge?["evidence"], "origin") is not ("nativeConfirmed" or "parsedSupported") ? "unresolvedEvidence" :
                !ExactNativeEndpoint(edge?["source"], nodes) || !ExactNativeEndpoint(edge?["target"], nodes) ? "endpointMismatch" :
                !ImpactKinds.Contains(Text(edge, "kind"), StringComparer.Ordinal) ? "unsupportedRelationshipKind" : "exactCapturedEndpoints";
            if (status != "exactCapturedEndpoints") unusable++;
            if (relationships.Count == 100) { omitted++; continue; }
            relationships.Add(new JsonObject { ["index"] = edgeIndex, ["status"] = status, ["edge"] = edge!.DeepClone() });
            if (!Fits()) { relationships.RemoveAt(relationships.Count - 1); omitted++; }
        }
        result["totalAdjacentObserved"] = adjacent; result["omittedRelationships"] = omitted;
        result["unusableRelationships"] = unusable;
        result["truncated"] = omitted > 0 || ancestryStatus is "ancestorLimit" or "byteLimit";
        return result;
    }
}
