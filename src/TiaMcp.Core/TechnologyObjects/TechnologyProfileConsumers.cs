using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TechnologyObjects;

/// <summary>Explicit supplied consumers and bounded saved dependency context, never inferred native links.</summary>
public static class TechnologyProfileConsumers
{
    public static void Add(JsonObject report, JsonObject review, JsonObject request)
    {
        var declarations = Object(request["consumers"]); Fields(declarations, "before after");
        var limits = Object(request["impactLimits"]); Fields(limits, "impactDepth maxImpactPaths maxImpactEdgeVisits");
        int depth = Limit("impactDepth", 5), maxPaths = Limit("maxImpactPaths", 200), visits = Limit("maxImpactEdgeVisits", 20000);
        var model = review["model"]!.AsObject(); var nodes = model["nodes"]!.AsArray().Select(Object).ToArray();
        var byPath = nodes.Where(n => n["objectPath"] is not null).ToLookup(n => (string)n["objectPath"]!, StringComparer.Ordinal);
        var byId = nodes.ToLookup(n => (string)n["nodeId"]!, StringComparer.Ordinal);
        string plc = (string)request["before"]!["identity"]!["plcPath"]!, selected = (string)request["technologyPath"]!;
        string Resolve(string? path, string? id)
        {
            if (path is null || id is null) return "notDeclared";
            var paths = byPath[path].Take(2).ToArray(); var ids = byId[id].Take(2).ToArray();
            if (paths.Length > 1 || ids.Length > 1) return "ambiguous";
            if (paths.Length == 0 && ids.Length == 0) return "missing";
            if (paths.Length != 1 || ids.Length != 1 || !ReferenceEquals(paths[0], ids[0])) return "identityMismatch";
            var node = paths[0];
            if ((string?)node["origin"] == "virtual") return "outOfScope";
            if (node["plcPath"] is null) return "scopeUnknown";
            return (string?)node["plcPath"] == plc ? "capturedObject" : "outOfScope";
        }
        var consumers = new JsonArray(); var changes = new JsonArray(); var roots = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var states = new List<Dictionary<string, JsonObject>>();
        foreach (string phase in new[] { "before", "after" })
        {
            if (declarations[phase] is not JsonArray rows || rows.Count > 256) throw Invalid("Supply 0..256 consumers per phase.");
            var state = new Dictionary<string, JsonObject>(StringComparer.Ordinal); states.Add(state); Root(selected, phase);
            foreach (var item in rows)
            {
                var row = Object(item); Fields(row, "id subjectKind subjectKey objectPath nodeId member state reason");
                if (!state.TryAdd(Text(row,"id",256),row)) throw Invalid("Duplicate exact consumer ID in phase.");
                string kind = Text(row,"subjectKind",16), key = Text(row,"subjectKey",256), status = Text(row,"state",16);
                if (kind is not ("parameter" or "relationship") || status is not ("declared" or "unknown")) throw Invalid("Unknown consumer subject kind or state.");
                string? path = Optional(row,"objectPath",8192), id = Optional(row,"nodeId",8192), member = Optional(row,"member",1024), reason = Optional(row,"reason",1024);
                if ((path is null) != (id is null) || status == "declared" && path is null || status == "unknown" && reason is null)
                    throw Invalid("Declare both exact consumer identity fields or an explained unknown.");
                bool subjectFound = request[phase]![kind == "parameter" ? "parameters" : "relationships"]!.AsArray()
                    .Any(p => (string?)p![kind == "parameter" ? "name" : "id"] == key);
                string resolution = Resolve(path,id);
                consumers.Add(new JsonObject { ["phase"] = phase, ["consumer"] = row.DeepClone(),
                    ["subjectResolution"] = subjectFound ? "suppliedDeclaration" : "missingDeclaration", ["objectResolution"] = resolution,
                    ["memberResolution"] = member is null ? "notDeclared" : "notVerified", ["nativeLinkVerified"] = false });
                if (subjectFound && status == "declared" && resolution == "capturedObject") Root(path!,phase);
            }
        }
        foreach (string key in states[0].Keys.Union(states[1].Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            states[0].TryGetValue(key,out var before); states[1].TryGetValue(key,out var after); var fields = new JsonArray();
            if (before is not null && after is not null)
                foreach (string field in new[] { "subjectKind", "subjectKey", "objectPath", "nodeId", "member", "state", "reason" })
                    if (!JsonNode.DeepEquals(before[field],after[field])) fields.Add(field);
            changes.Add(new JsonObject { ["id"] = key, ["status"] = before is null ? "onlyAfter" : after is null ? "onlyBefore" : fields.Count == 0 ? "unchanged" : "changed", ["changedFields"] = fields });
        }
        // Supplied links never become graph edges. Exclude ambiguous/out-of-scope nodes before using the existing traversal.
        var scoped = model.DeepClone().AsObject();
        scoped["nodes"] = new JsonArray(nodes.Where(n => Resolve((string?)n["objectPath"],(string?)n["nodeId"]) == "capturedObject").Select(n => n.DeepClone()).ToArray());
        string reviewId = (string)request["reviewId"]!;
        var impact = ProjectReviewStore.ObservedImpactPaths(reviewId,reviewId,scoped,scoped,
            new JsonArray(roots.OrderBy(p => p.Key,StringComparer.Ordinal).Select(p => (JsonNode)p.Value).ToArray()),depth,maxPaths,visits);
        report["consumers"] = consumers; report["consumerChanges"] = changes; report["impactPaths"] = impact["paths"]!.DeepClone();
        var coverage = impact["coverage"]!.DeepClone().AsObject();
        coverage["excludedNodeCount"] = nodes.Length - scoped["nodes"]!.AsArray().Count;
        coverage["rootCount"] = roots.Count;
        coverage["consumerPolicy"] = "Both phases use this same saved review. Roots are the selected TO and exact same-PLC captured objects of declared consumers whose subjects exist in that phase. Incoming saved paths describe context candidates, not effects of proposed parameter/relationship changes. Supplied consumer links are never inserted as captured edges. Other PLCs, virtual nodes, unknown scopes, mismatched and ambiguous identities are excluded. Member existence and native links remain unverified; absent declarations do not prove removal or no impact.";
        report["impactTraversal"] = coverage;
        report["schemaVersion"] = "technology-profile-review.v3";
        return;

        int Limit(string key, int maximum) => limits[key] is JsonValue value && value.TryGetValue<int>(out int count) && count >= 1 && count <= maximum
            ? count : throw Invalid("Impact limit out of range: " + key);
        void Root(string path,string phase)
        {
            if (!roots.TryGetValue(path,out var root)) roots[path] = root = new JsonObject { ["path"] = path, ["change"] = "suppliedContextRoot", ["changedFields"] = new JsonArray() };
            root[phase + "ObjectPath"] = path;
        }
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Invalid("Expected consumer object.");
    private static void Fields(JsonObject row,string fields)
    { var names = fields.Split(' '); if (row.Count != names.Length || names.Any(n => !row.ContainsKey(n))) throw Invalid("Unexpected consumer fields."); }
    private static string? Optional(JsonObject row,string key,int maximum) => row[key] is null ? null : Text(row,key,maximum);
    private static string Text(JsonObject row,string key,int maximum)
    {
        if (row[key] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null) throw Invalid("Expected consumer text: " + key);
        EngineeringIdentityGuard.Text(text,key,maximum); return text;
    }
    private static BridgeException Invalid(string message) => new("InvalidTechnologyProfile",message);
}
