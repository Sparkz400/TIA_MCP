using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.TechnologyObjects;

/// <summary>Exact supplied TO evidence, without native compatibility or mutation authority.</summary>
public static class TechnologyProfileReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Input must contain 1 byte..4 MiB.");
        string text = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                { _ = Utf8.GetByteCount(p.Name); if (!names.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
        }
        Unique(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an object.");
    }

    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request)
    {
        Fields(request, "schemaVersion reviewId technologyPath afterKind before after");
        if (Text(request, "schemaVersion") != "technology-profile-review-request.v1" || Text(request, "reviewId") != reviewId)
            throw Error("Select this exact saved review and request schema.");
        string path = Text(request, "technologyPath"), afterKind = Text(request, "afterKind");
        if (afterKind is not ("observation" or "proposal")) throw Error("Unknown afterKind.");
        var nodes = review["model"]!["nodes"]!.AsArray().Select(Object).ToArray();
        var selected = nodes.Where(n => (string?)n["objectPath"] == path && (string?)n["origin"] != "virtual").ToArray();
        if (selected.Length != 1 || (string?)selected[0]["kind"] != "technologyObject") throw Error("Select one exact captured technology object.");
        var paths = nodes.Where(n => (string?)n["origin"] != "virtual" && n["objectPath"] is not null)
            .GroupBy(n => Text(n, "objectPath"), StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var b = Object(request["before"]); var a = Object(request["after"]);
        var before = Snapshot(b); var after = Snapshot(a);
        if (!JsonNode.DeepEquals(b["identity"], a["identity"])) throw Error("CPU/TO profile declarations must match exactly; cross-profile mapping is not inferred.");
        var identity = Object(b["identity"]);
        if (selected[0]["plcPath"] is not null && !JsonNode.DeepEquals(selected[0]["plcPath"], identity["plcPath"])) throw Error("PLC declaration contradicts captured TO owner.");
        if (selected[0]["metadata"] is JsonObject metadata)
            foreach (var (captured, declared) in new[] { ("systemLibraryType", "technologyType"), ("systemLibraryVersion", "technologyVersion") })
                if (metadata[captured] is not null && !JsonNode.DeepEquals(metadata[captured], identity[declared])) throw Error("Profile contradicts captured " + captured + ".");
        if (afterKind == "proposal" && (Text(b, "valueDomain") != "offline" || Text(a, "valueDomain") != "offline")) throw Error("Proposals require offline observations.");
        var observations = new JsonArray(); var changes = new JsonArray(); var relationships = new JsonArray();
        int readOnly = 0, unknownAccess = 0, unresolved = 0, cycles = 0, changed = 0;
        foreach (var (phase, state) in new[] { ("before", before), ("after", after) })
        {
            foreach (var row in state.Parameters.Values) observations.Add(new JsonObject { ["phase"] = phase, ["parameter"] = row.DeepClone() });
            var adjacency = state.Relationships.Values.Where(r => (string?)r["state"] == "declared")
                .GroupBy(r => Text(r, "sourcePath"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => Text(r, "targetPath")).ToArray(), StringComparer.Ordinal);
            foreach (var row in state.Relationships.Values)
            {
                string source = Text(row, "sourcePath"); string? target = NullableText(row, "targetPath");
                string sr = paths.ContainsKey(source) ? "capturedObject" : "unresolved";
                string tr = target is null ? Text(row, "state") == "disconnected" ? "declaredDisconnected" : "unresolved" : paths.ContainsKey(target) ? "capturedObject" : "unresolved";
                bool cycle = false;
                if (Text(row, "state") == "declared")
                {
                    var pending = new Stack<string>(); pending.Push(target!); var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (pending.TryPop(out string? next))
                    {
                        if (next == source) { cycle = true; break; }
                        if (visited.Add(next) && adjacency.TryGetValue(next, out var peers)) foreach (string peer in peers) pending.Push(peer);
                    }
                }
                unresolved += (sr == "unresolved" ? 1 : 0) + (tr == "unresolved" ? 1 : 0); if (cycle) cycles++;
                relationships.Add(new JsonObject { ["phase"] = phase, ["relationship"] = row.DeepClone(), ["sourceResolution"] = sr,
                    ["targetResolution"] = tr, ["memberResolution"] = row["targetMember"] is null ? "notDeclared" : "notVerified",
                    ["inDeclaredCycle"] = cycle });
            }
        }
        Compare("parameter", before.Parameters, after.Parameters, "nativeType access value metadata");
        Compare("relationship", before.Relationships, after.Relationships, "kind sourcePath targetPath targetMember state reason metadata");
        if (afterKind == "proposal" && changed > 500) throw Error("At most 500 changed proposal records are supported.");
        string ancestryEnd = "root";
        var selection = new JsonArray(); var visitedIds = new HashSet<string>(StringComparer.Ordinal); JsonObject? current = selected[0];
        while (current is not null && selection.Count < 128 && visitedIds.Add(Text(current, "nodeId")))
        {
            selection.Add(current.DeepClone()); string? parent = (string?)current["parentId"];
            var parents = nodes.Where(n => (string?)n["nodeId"] == parent).ToArray();
            if (parent is not null && parents.Length != 1) ancestryEnd = "unresolvedParent";
            current = parent is not null && parents.Length == 1 ? parents[0] : null;
        }
        bool SelectedEndpoint(JsonNode? endpoint) => endpoint is JsonObject row && (string?)row["nodeId"] == (string?)selected[0]["nodeId"] && (string?)row["objectPath"] == path;
        var direct = review["model"]!["edges"]!.AsArray().Where(e => SelectedEndpoint(e?["source"]) || SelectedEndpoint(e?["target"])).ToArray();
        return new JsonObject { ["schemaVersion"] = "technology-profile-review.v1", ["reviewId"] = reviewId, ["technologyPath"] = path,
            ["identity"] = identity.DeepClone(), ["afterKind"] = afterKind, ["beforeValueDomain"] = b["valueDomain"]!.DeepClone(), ["afterValueDomain"] = a["valueDomain"]!.DeepClone(),
            ["coverage"] = new JsonObject { ["beforeParametersComplete"] = Boolean(b,"parametersComplete"), ["afterParametersComplete"] = Boolean(a,"parametersComplete"),
                ["beforeRelationshipsComplete"] = Boolean(b,"relationshipsComplete"), ["afterRelationshipsComplete"] = Boolean(a,"relationshipsComplete") },
            ["observations"] = observations, ["relationships"] = relationships, ["changes"] = changes,
            ["context"] = new JsonObject { ["selectionAndAncestors"] = selection, ["ancestorTraversalStopped"] = current is not null || ancestryEnd != "root", ["ancestorTraversalEnd"] = current is not null ? "cycleOrLimit" : ancestryEnd,
                ["directCapturedEdges"] = new JsonArray(direct.Take(1024).Select(e => e!.DeepClone()).ToArray()), ["directEdgesTruncated"] = direct.Length > 1024 },
            ["summary"] = new JsonObject { ["beforeParameterCount"] = before.Parameters.Count, ["afterParameterCount"] = after.Parameters.Count,
                ["beforeRelationshipCount"] = before.Relationships.Count, ["afterRelationshipCount"] = after.Relationships.Count,
                ["changedCount"] = changed, ["readOnlyConflictCount"] = readOnly, ["unknownAccessChangeCount"] = unknownAccess,
                ["unresolvedEndpointCount"] = unresolved, ["declaredCycleRecordCount"] = cycles },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["nativeExecuted"] = false, ["targetCompatibilityVerified"] = false,
            ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["wholeObjectAnalysisComplete"] = false,
            ["limits"] = "Supplied values and declarations only. Text values and opaque metadata are not coerced or interpreted. Missing rows do not prove creation/deletion. Captured endpoints are saved identities, not fresh native references; member/type/route/CPU/version/license eligibility and connection direction/units are unverified. Cycles describe supplied edges, not native invalidity. No write, tuning, motion, compile, XML-preservation or save/reopen qualification." };

        void Compare(string kind, Dictionary<string, JsonObject> left, Dictionary<string, JsonObject> right, string fields)
        {
            foreach (string key in left.Keys.Union(right.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                left.TryGetValue(key, out var old); right.TryGetValue(key, out var next); var differences = new JsonArray();
                if (old is not null && next is not null) foreach (string field in fields.Split(' ')) if (!JsonNode.DeepEquals(old[field], next[field])) differences.Add(field);
                string status = old is null ? "onlyAfter" : next is null ? "onlyBefore" : differences.Count == 0 ? "unchanged" : "changed";
                if (status != "unchanged") changed++;
                string assessment = afterKind == "proposal" ? "notAssessed" : "notProposal";
                if (afterKind == "proposal" && kind == "parameter" && old is not null && next is not null && !JsonNode.DeepEquals(old["value"], next["value"]))
                {
                    if (Text(old,"access") == "readOnly") { assessment = "contradictsSuppliedReadOnly"; readOnly++; }
                    else if (Text(old,"access") == "unknown") { assessment = "accessUnknown"; unknownAccess++; }
                }
                changes.Add(new JsonObject { ["kind"] = kind, ["key"] = key, ["status"] = status, ["changedFields"] = differences, ["proposalAssessment"] = assessment });
            }
        }
    }
    private sealed record State(Dictionary<string, JsonObject> Parameters, Dictionary<string, JsonObject> Relationships);
    private static State Snapshot(JsonObject snapshot)
    {
        Fields(snapshot, "identity valueDomain parametersComplete relationshipsComplete parameters relationships");
        if (Text(snapshot,"valueDomain") is not ("offline" or "online")) throw Error("Unknown value domain.");
        Boolean(snapshot,"parametersComplete"); Boolean(snapshot,"relationshipsComplete");
        var identity = Object(snapshot["identity"]); Fields(identity,"plcPath orderNumber firmware technologyType technologyVersion options unknownReason");
        Text(identity,"plcPath"); string? unknown = NullableText(identity,"unknownReason",1024);
        foreach (string key in new[] { "orderNumber","firmware","technologyType","technologyVersion" })
            if (NullableText(identity,key,256) is null && unknown is null) throw Error("Unknown profile fields require a reason.");
        var options = Array(identity,"options",64); var optionSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in options) { var holder = new JsonObject { ["option"] = option?.DeepClone() }; if (!optionSet.Add(Text(holder,"option",256))) throw Error("Duplicate exact option."); }
        var parameters = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in Array(snapshot,"parameters",4096))
        {
            var row=Object(item); Fields(row,"name nativeType access value metadata");
            if (!parameters.TryAdd(Text(row,"name",256),row)) throw Error("Duplicate exact parameter name.");
            NullableText(row,"nativeType",256); if (Text(row,"access") is not ("readOnly" or "readWrite" or "unknown")) throw Error("Unknown access declaration.");
            Object(row["metadata"]); var value=Object(row["value"]);
            if (Text(value,"kind") is "scalar" or "opaque") { Fields(value,"kind text"); Scalar(value,"text",InputLimit); }
            else if (Text(value,"kind") == "unavailable") { Fields(value,"kind reason"); Text(value,"reason",1024); }
            else throw Error("Expected scalar/opaque text or an unavailable reason.");
        }
        var relations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in Array(snapshot,"relationships",1024))
        {
            var row=Object(item); Fields(row,"id kind sourcePath targetPath targetMember state reason metadata");
            if (!relations.TryAdd(Text(row,"id",256),row)) throw Error("Duplicate exact relationship ID.");
            if (Text(row,"kind") is not ("hardware" or "coupling" or "mapping" or "dependency" or "child" or "cam" or "interpreter")) throw Error("Unknown declared relationship kind.");
            Text(row,"sourcePath"); string? target=NullableText(row,"targetPath"); NullableText(row,"targetMember",1024); string? reason=NullableText(row,"reason",1024);
            string state=Text(row,"state");
            if (state is not ("declared" or "disconnected" or "unknown") || state == "declared" && target is null || state == "disconnected" && (target is not null || row["targetMember"] is not null) || state == "unknown" && reason is null)
                throw Error("Contradictory or unexplained relationship state.");
            Object(row["metadata"]);
        }
        return new(parameters,relations);
    }
    private static void Scalar(JsonObject value, string field, int max)
    { if (value[field] is not JsonValue v || !v.TryGetValue<string>(out string? text) || text is null || text.Length > max) throw Error("Expected bounded scalar text: " + field); _ = Utf8.GetByteCount(text); }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object row.");
    private static void Fields(JsonObject value, string fields)
    { var expected = fields.Split(' '); if (value.Count != expected.Length || expected.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown fields; expected: " + fields); }
    private static JsonArray Array(JsonObject value, string key, int max) => value[key] is JsonArray rows && rows.Count <= max ? rows : throw Error(key + " collection exceeds bounds or is absent.");
    private static bool Boolean(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean " + key);
    private static string? NullableText(JsonObject value, string key, int max = 8192) => value[key] is null ? null : Text(value, key, max);
    private static string Text(JsonNode node, string key, int max = 8192)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null || text.Length > max || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Error("Invalid exact text: " + key);
        _ = Utf8.GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidTechnologyProfile", message);
}
