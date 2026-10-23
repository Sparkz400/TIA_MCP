using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.SoftwareUnits;

/// <summary>Reviews supplied unit facts and proposed edits, never native visibility or compilation.</summary>
public static class SoftwareUnitReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private sealed record Unit(JsonObject Facts, JsonArray Relations);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Input must contain 1 byte..4 MiB.");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Error("Duplicate JSON field."); Unique(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
        }
        Unique(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an object.");
    }

    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request)
    {
        Fields(request, "schemaVersion reviewId plcPath coverage units objects dependencies changes");
        if (Text(request, "schemaVersion") != "software-unit-review-request.v1" || Text(request, "reviewId") != reviewId)
            throw Error("Request must name this exact saved review and schema.");
        string plc = Text(request, "plcPath");
        var all = review["model"]!["nodes"]!.AsArray().OfType<JsonObject>()
            .Where(n => (string?)n["origin"] != "virtual" && !string.IsNullOrEmpty((string?)n["objectPath"]))
            .ToDictionary(n => (string)n["objectPath"]!, StringComparer.Ordinal);
        JsonObject Node(string path)
        {
            if (!all.TryGetValue(path, out var node) || ((string?)node["plcPath"] != plc && path != plc))
                throw Error("Exact object is absent from the selected captured PLC: " + path);
            return node;
        }
        if ((string?)Node(plc)["kind"] != "plc") throw Error("Select an exact captured PLC.");
        var coverage = Object(request["coverage"]); Fields(coverage, "unitsComplete objectsComplete dependenciesComplete");
        foreach (string key in coverage.Select(p => p.Key)) Boolean(coverage, key);
        var units = new Dictionary<string, Unit>(StringComparer.Ordinal);
        int relationCount = 0;
        foreach (var value in Array(request, "units", 128, 1))
        {
            var row = Object(value); Fields(row, "objectPath namespacePreset relationsComplete relations");
            string path = Text(row, "objectPath"); var node = Node(path);
            if ((string?)node["kind"] != "softwareUnit" || (string?)node["scopePath"] != path) throw Error("Only exact standard software units are supported.");
            string? preset = NullableText(row, "namespacePreset", 256); Boolean(row, "relationsComplete");
            if (preset is not null && node["metadata"]?["namespacePreset"] is JsonNode capturedPreset && capturedPreset.GetValue<string>() != preset) throw Error("Supplied namespace preset contradicts saved metadata.");
            var relations = Array(row, "relations", 2048); relationCount += relations.Count;
            if (relationCount > 2048 || !units.TryAdd(path, new(row, relations))) throw Error("Duplicate unit or relation bound exceeded.");
            var names = new HashSet<(string, string)>();
            foreach (var relation in relations)
            {
                var r = Object(relation); Fields(r, "relationType relatedObject targetPath");
                if (!names.Add((Text(r, "relationType", 64), Text(r, "relatedObject", 256)))) throw Error("Duplicate relation type/name.");
                NullableText(r, "targetPath");
                if (r["targetPath"] is not null && all.ContainsKey(Text(r, "targetPath"))) _ = Node(Text(r, "targetPath"));
            }
        }
        var objects = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var value in Array(request, "objects", 2048))
        {
            var row = Object(value); Fields(row, "objectPath namespace publication"); string path = Text(row, "objectPath"); var node = Node(path);
            if ((string?)node["kind"] is not ("block" or "type") || !units.ContainsKey((string?)node["scopePath"] ?? "")) throw Error("Object must belong to a supplied standard unit.");
            string? ns = NullableText(row, "namespace", 256);
            if (ns is not null && node["metadata"]?["namespace"] is JsonNode known && known.GetValue<string>() != ns) throw Error("Supplied namespace contradicts saved metadata.");
            if (Text(row, "publication", 32) is not ("Published" or "Unpublished" or "unknown" or "notApplicable") || !objects.TryAdd(path, row)) throw Error("Invalid publication or duplicate object.");
        }
        var dependencies = Array(request, "dependencies", 4096);
        foreach (var value in dependencies)
        {
            var row = Object(value); Fields(row, "sourcePath targetPath targetName reason");
            var source = Node(Text(row, "sourcePath"));
            if ((string?)source["kind"] is not ("block" or "type") || !units.ContainsKey((string?)source["scopePath"] ?? "")) throw Error("Dependency source must be an exact object in a supplied unit.");
            NullableText(row, "targetPath"); Text(row, "targetName", 256); Text(row, "reason", 512);
            if (row["targetPath"] is not null && all.ContainsKey(Text(row, "targetPath"))) _ = Node(Text(row, "targetPath"));
        }
        var before = Assess(units, objects);
        var editedUnits = units.ToDictionary(p => p.Key, p => new Unit(p.Value.Facts.DeepClone().AsObject(), p.Value.Relations.DeepClone().AsArray()), StringComparer.Ordinal);
        var editedObjects = objects.ToDictionary(p => p.Key, p => p.Value.DeepClone().AsObject(), StringComparer.Ordinal);
        foreach (var value in Array(request, "changes", 128))
        {
            var change = Object(value); string kind = Text(change, "kind", 32);
            if (kind == "setPublication")
            {
                Fields(change, "kind objectPath value"); string path = Text(change, "objectPath"), access = Text(change, "value", 32);
                if (!editedObjects.TryGetValue(path, out var obj) || Restricted(Node(path)) || !Publishable(Node(path)) || access is not ("Published" or "Unpublished")) throw Error("Publication edit needs a supplied supported block/type and Published/Unpublished.");
                obj["publication"] = access;
                continue;
            }
            string unitPath = Text(change, "unitPath");
            if (Restricted(Node(unitPath)) || !editedUnits.TryGetValue(unitPath, out var unit)) throw Error("Change must select a supplied unit.");
            if (kind == "setNamespacePreset")
            {
                Fields(change, "kind unitPath value"); unit.Facts["namespacePreset"] = Text(change, "value", 256, true);
            }
            else if (kind == "addRelation")
            {
                Fields(change, "kind unitPath relationType targetPath"); string path = Text(change, "targetPath"), type = Text(change, "relationType", 64); var target = Node(path);
                if (path == unitPath || !RelationTarget(type, target, plc)) throw Error("Relation target/type unsupported, or relation points to itself.");
                string name = Text(target, "name", 256);
                if (char.IsWhiteSpace(name[0])) throw Error("New relation name cannot start with whitespace.");
                if (unit.Relations.Any(r => Text(r!, "relationType") == type && Text(r!, "relatedObject") == name)) throw Error("Relation already exists; remove it explicitly before replacement.");
                unit.Relations.Add(new JsonObject { ["relationType"] = type, ["relatedObject"] = name, ["targetPath"] = path });
            }
            else if (kind == "removeRelation")
            {
                Fields(change, "kind unitPath relationType relatedObject"); string type = Text(change, "relationType", 64), name = Text(change, "relatedObject", 256);
                var match = unit.Relations.SingleOrDefault(r => Text(r!, "relationType") == type && Text(r!, "relatedObject") == name);
                if (match is null) throw Error("Selected exact relation is absent; no implicit name substitution.");
                unit.Relations.Remove(match);
            }
            else throw Error("Unsupported change kind; native create/move/template operations are not implemented by this review.");
        }
        var after = Assess(editedUnits, editedObjects);
        var report = new JsonObject { ["schemaVersion"] = "software-unit-review.v1", ["reviewId"] = reviewId, ["plcPath"] = plc,
            ["suppliedCoverage"] = coverage.DeepClone(), ["changes"] = request["changes"]!.DeepClone(),
            ["units"] = new JsonArray(units.Select(p => (JsonNode?)new JsonObject { ["objectPath"] = p.Key, ["beforeNamespacePreset"] = p.Value.Facts["namespacePreset"]?.DeepClone(),
                ["afterNamespacePreset"] = editedUnits[p.Key].Facts["namespacePreset"]?.DeepClone(), ["relationsComplete"] = p.Value.Facts["relationsComplete"]!.DeepClone() }).ToArray()),
            ["objects"] = new JsonArray(objects.Select(p => (JsonNode?)new JsonObject { ["objectPath"] = p.Key, ["namespace"] = p.Value["namespace"]?.DeepClone(),
                ["beforePublication"] = p.Value["publication"]!.DeepClone(), ["afterPublication"] = editedObjects[p.Key]["publication"]!.DeepClone() }).ToArray()),
            ["relations"] = new JsonArray(), ["dependencies"] = new JsonArray(), ["cycles"] = new JsonArray(), ["collisions"] = new JsonArray(),
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["wholeProjectAnalysisComplete"] = false,
            ["limits"] = "Supplied facts and exact captured selectors only. Direct declared access prerequisites are not native visibility or compile proof. Missing paths never resolve by name. Namespace-preset edits do not rewrite member namespaces in this model; native propagation, syntax, preservation, library links and runtime remain unverified." };
        foreach (var (phase, state) in new[] { ("before", before), ("after", after) })
        {
            foreach (var row in state.Relations) { var copy = row!.DeepClone(); copy["phase"] = phase; report["relations"]!.AsArray().Add(copy); }
            foreach (string path in state.Cycles) report["cycles"]!.AsArray().Add(new JsonObject { ["phase"] = phase, ["unitPath"] = path });
        }
        for (int i = 0; i < dependencies.Count; i++)
            report["dependencies"]!.AsArray().Add(new JsonObject { ["index"] = i, ["evidence"] = dependencies[i]!.DeepClone(), ["before"] = before.Dependencies[i]!.DeepClone(), ["after"] = after.Dependencies[i]!.DeepClone() });
        foreach (var group in objects.Where(p => p.Value["namespace"] is not null).GroupBy(p => (Text(Node(p.Key), "kind"), Text(p.Value, "namespace", 256, true), Text(Node(p.Key), "name", 256))))
            if (group.Count() > 1) report["collisions"]!.AsArray().Add(new JsonObject { ["kind"] = group.Key.Item1, ["namespace"] = group.Key.Item2, ["name"] = group.Key.Item3,
                ["objectPaths"] = new JsonArray(group.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()) });
        string[] omittedUnits = all.Where(p => (string?)p.Value["plcPath"] == plc && (string?)p.Value["kind"] == "softwareUnit" && !units.ContainsKey(p.Key)).Select(p => p.Key).ToArray();
        int omittedObjects = all.Count(p => (string?)p.Value["plcPath"] == plc && units.ContainsKey((string?)p.Value["scopePath"] ?? "") && (string?)p.Value["kind"] is "block" or "type" && !objects.ContainsKey(p.Key));
        if (Boolean(coverage, "unitsComplete") && omittedUnits.Length > 0 || Boolean(coverage, "objectsComplete") && omittedObjects > 0) throw Error("Claimed supplied inventory completeness contradicts captured objects.");
        report["summary"] = new JsonObject { ["unitCount"] = units.Count, ["objectCount"] = objects.Count, ["dependencyCount"] = dependencies.Count,
            ["changeCount"] = request["changes"]!.AsArray().Count, ["omittedCapturedUnits"] = omittedUnits.Length, ["omittedCapturedObjects"] = omittedObjects,
            ["beforeUnresolvedDependencies"] = before.Dependencies.Count(d => (string?)d!["status"] != "declaredPrerequisitesPresent"),
            ["afterUnresolvedDependencies"] = after.Dependencies.Count(d => (string?)d!["status"] != "declaredPrerequisitesPresent"),
            ["cycleRowCount"] = report["cycles"]!.AsArray().Count, ["collisionCount"] = report["collisions"]!.AsArray().Count };
        return report;

        (JsonArray Relations, JsonArray Dependencies, string[] Cycles) Assess(Dictionary<string, Unit> state, Dictionary<string, JsonObject> declarations)
        {
            var relations = new JsonArray(); var exact = new HashSet<(string Unit, string Type, string Target)>();
            foreach (var pair in state)
                foreach (var value in pair.Value.Relations)
                {
                    var r = value!; string type = Text(r, "relationType"), path = (string?)r["targetPath"] ?? "";
                    bool resolved = all.TryGetValue(path, out var target) && RelationTarget(type, target, plc) && (string?)target["name"] == Text(r, "relatedObject");
                    if (resolved) exact.Add((pair.Key, type, path));
                    relations.Add(new JsonObject { ["unitPath"] = pair.Key, ["relation"] = r.DeepClone(), ["resolution"] = resolved ? "exactSuppliedPath" : "unresolved" });
                }
            var evaluated = new JsonArray();
            foreach (var value in dependencies)
            {
                var d = value!; string source = (string)Node(Text(d, "sourcePath"))["scopePath"]!, path = (string?)d["targetPath"] ?? "";
                string status = "targetAbsentOrUnresolved";
                if (all.TryGetValue(path, out var target) && (string?)target["name"] == Text(d, "targetName"))
                {
                    string owner = (string?)target["scopePath"] ?? "";
                    if (owner == source) status = (string?)target["kind"] is "block" or "type" ? "declaredPrerequisitesPresent" : "unsupportedTarget";
                    else if (state.ContainsKey(owner))
                    {
                        if (!Publishable(target)) status = "unsupportedTarget";
                        else if (!declarations.TryGetValue(path, out var obj) || Text(obj, "publication") is "unknown" or "notApplicable") status = "publicationUnknown";
                        else if (Text(obj, "publication") != "Published") status = "notPublished";
                        else status = exact.Contains((source, "SoftwareUnit", owner)) ? "declaredPrerequisitesPresent" : Boolean(state[source].Facts, "relationsComplete") ? "missingDirectRelation" : "relationCoverageUnknown";
                    }
                    else if (owner == plc)
                    {
                        string type = (string?)target["kind"] == "technology" ? "TODB" : (string?)target["metadata"]?["kind"] == "GlobalDB" ? "NonUnitDB" : "unsupported";
                        status = type == "unsupported" ? "unsupportedTarget" : exact.Contains((source, type, path)) ? "declaredPrerequisitesPresent" : Boolean(state[source].Facts, "relationsComplete") ? "missingDirectRelation" : "relationCoverageUnknown";
                    }
                    else status = "targetUnitNotSupplied";
                }
                evaluated.Add(new JsonObject { ["status"] = status });
            }
            var adjacency = state.Keys.ToDictionary(k => k, k => exact.Where(e => e.Unit == k && e.Type == "SoftwareUnit").Select(e => e.Target).ToArray(), StringComparer.Ordinal);
            var cycles = state.Keys.Where(start => {
                var pending = new Stack<string>(adjacency[start]); var visited = new HashSet<string>(StringComparer.Ordinal);
                while (pending.TryPop(out string? current)) { if (current == start) return true; if (visited.Add(current) && adjacency.TryGetValue(current, out var next)) foreach (string n in next) pending.Push(n); }
                return false;
            }).ToArray();
            return (relations, evaluated, cycles);
        }
    }
    private static bool Restricted(JsonObject node) => (string?)node["access"]?["state"] == "protected" || (bool?)node["metadata"]?["generated"] == true || (bool?)node["metadata"]?["isKnowHowProtected"] == true || (bool?)node["metadata"]?["isSafety"] == true;
    private static bool Publishable(JsonObject node) => (string?)node["kind"] == "type" || (string?)node["kind"] == "block" && (string?)node["metadata"]?["kind"] is "FC" or "FB" or "GlobalDB" or "InstanceDB";
    private static bool RelationTarget(string type, JsonObject node, string plc) => (string?)node["plcPath"] == plc && type switch {
        "SoftwareUnit" => (string?)node["kind"] == "softwareUnit", "NonUnitDB" => (string?)node["kind"] == "block" && (string?)node["scopePath"] == plc && (string?)node["metadata"]?["kind"] == "GlobalDB",
        "TODB" => (string?)node["kind"] == "technology" && (string?)node["scopePath"] == plc, _ => false };
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object row.");
    private static void Fields(JsonObject value, string fields)
    { var expected = fields.Split(' '); if (value.Count != expected.Length || expected.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown fields; expected: " + fields); }
    private static JsonArray Array(JsonObject value, string key, int max, int min = 0) => value[key] is JsonArray rows && rows.Count >= min && rows.Count <= max ? rows : throw Error(key + " collection exceeds bounds or is absent.");
    private static bool Boolean(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean " + key);
    private static string? NullableText(JsonObject value, string key, int max = 8192) => value[key] is null ? null : Text(value, key, max, true);
    private static string Text(JsonNode node, string key, int max = 8192, bool empty = false)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null || text.Length > max || !empty && string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Error("Invalid exact text: " + key);
        _ = new UTF8Encoding(false, true).GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidUnitReview", message);
}
