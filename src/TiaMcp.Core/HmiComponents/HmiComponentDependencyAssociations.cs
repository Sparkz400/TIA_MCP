using System.Text.Json.Nodes;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

internal static class HmiComponentDependencyAssociations
{
    private sealed record Observation(string Scope, string Path, string ReviewId, string? Type, string? Version,
        string[]? Dependencies, string? Reason);

    internal static void Add(JsonObject report, JsonObject request)
    {
        var objects = new Dictionary<(string Scope, string Path), Observation>();
        var exports = report["libraryAssociations"]!.AsArray().ToDictionary(
            row => (Scope: Text(Object(row!["selection"]), "scope"), Path: Text(Object(row["selection"]), "objectPath")), row => row!.AsObject());
        int references = 0;
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            if (report[scope] is not JsonObject component) continue;
            string review = Text(Object(component["selection"]), "reviewId");
            foreach (var value in component["objects"]!.AsArray())
            {
                var node = Object(value!["capturedObject"]);
                var metadata = node["metadata"] as JsonObject;
                var observed = metadata?["libraryInstance"] as JsonObject;
                var (type, version) = Identity(observed);
                string? reason = "ownerIdentityUnavailable";
                string[]? dependencies = null;
                if (version is not null)
                {
                    var captured = observed?["dependencies"] as JsonObject;
                    reason = "dependencyObservationUnavailable";
                    if (String(captured?["schemaVersion"]) == "plc-library-dependencies.v1" &&
                        String(captured?["source"]) == "LibraryTypeVersion.Dependencies" && String(captured?["status"]) == "observed")
                    {
                        reason = "invalidDependencyObservation";
                        if (captured?["requiredVersionGuids"] is JsonArray list && list.Count <= 1000)
                        {
                            var ids = list.Select(GuidText).ToArray();
                            if (ids.All(id => id is not null)) { dependencies = ids.Select(id => id!).ToArray(); reason = null; }
                        }
                    }
                }
                references += dependencies?.Length ?? 0;
                if (references > 20000) throw Error("Selected dependency observations exceed 20000 references.");
                string path = Text(node, "objectPath");
                objects.Add((scope, path), new(scope, path, review, type, version, dependencies, reason));
            }
        }

        var identities = new JsonArray();
        var conflicts = new HashSet<(string Scope, string Version)>();
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            var observations = objects.Values.Where(o => o.Scope == scope && o.Version is not null)
                .Select(o => (Version: o.Version!, Type: o.Type!, Path: o.Path, Source: "savedObject"))
                .Concat(exports.Where(e => e.Key.Scope == scope).Select(e => (
                    Version: (string)e.Value["exportIdentity"]!["versionGuid"]!, Type: (string)e.Value["exportIdentity"]!["typeGuid"]!,
                    Path: e.Key.Path, Source: "selectedExport")));
            foreach (var group in observations.GroupBy(o => o.Version).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var types = group.Select(o => o.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                bool conflict = types.Length > 1;
                if (conflict) conflicts.Add((scope, group.Key));
                identities.Add(new JsonObject { ["scope"] = scope, ["versionGuid"] = group.Key,
                    ["typeGuids"] = new JsonArray(types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
                    ["conflictingTypeObservations"] = conflict,
                    ["observations"] = new JsonArray(group.Select(o => (JsonNode?)new JsonObject {
                        ["objectPath"] = o.Path, ["source"] = o.Source, ["typeGuid"] = o.Type }).ToArray()) });
            }
        }

        var rows = new JsonArray();
        var selected = new Dictionary<(string Scope, string Path, int Index), JsonObject>();
        foreach (var value in Array(request, "dependencyAssociations", 4096))
        {
            var selection = Object(value);
            Fields(selection, "scope objectPath dependencyIndex targetObjectPath reason");
            string scope = Text(selection, "scope", 32), path = Text(selection, "objectPath"), targetPath = Text(selection, "targetObjectPath");
            Text(selection, "reason", 2000);
            if (selection["dependencyIndex"] is not JsonValue indexValue || !indexValue.TryGetValue<int>(out int index) || index < 0 || index >= 1000 ||
                !objects.TryGetValue((scope, path), out var owner) || !objects.TryGetValue((scope, targetPath), out var target))
                throw Error("Dependency mapping requires exact selected source/target paths in one component scope and an index in 0..999.");
            if (owner.Dependencies is null || index >= owner.Dependencies.Length)
                throw Error("Dependency index is absent from a valid observed list; unavailable observations remain in coverage.");
            string required = owner.Dependencies[index];
            exports.TryGetValue((scope, targetPath), out var export);
            string? exportedVersion = String(export?["exportIdentity"]?["versionGuid"]);
            string targetAgreement = Agreement(required, target.Version), exportAgreement = Agreement(required, exportedVersion);
            string pairAgreement = String(export?["identifierAgreement"]) ?? "unavailable";
            bool conflict = conflicts.Contains((scope, required));
            string status = targetAgreement == "different" || exportAgreement == "different" || pairAgreement == "different" ? "different" :
                conflict ? "conflicting" : targetAgreement == "match" && exportAgreement == "match" && pairAgreement == "match" ? "match" : "unavailable";
            var row = new JsonObject { ["selection"] = selection.DeepClone(), ["reviewId"] = owner.ReviewId, ["requiredVersionGuid"] = required,
                ["targetTypeGuid"] = target.Type, ["targetVersionGuid"] = target.Version,
                ["exportFile"] = export?["file"]?.DeepClone(), ["exportIdentity"] = export?["exportIdentity"]?.DeepClone(),
                ["targetVersionAgreement"] = targetAgreement, ["exportVersionAgreement"] = exportAgreement, ["targetExportAgreement"] = pairAgreement,
                ["identityConflict"] = conflict, ["status"] = status, ["declaredCycle"] = false,
                ["nativeIdentityVerified"] = false, ["dependencyClosureVerified"] = false };
            if (!selected.TryAdd((scope, path, index), row)) throw Error("The same dependency observation index is mapped more than once.");
            rows.Add(row);
        }

        // Evaluate only explicit matching mappings, independently in each saved scope.
        foreach (string scope in new[] { "beforeComponent", "afterComponent" })
        {
            var matched = rows.OfType<JsonObject>().Where(r => (string?)r["selection"]?["scope"] == scope && (string?)r["status"] == "match").ToArray();
            var adjacency = matched.GroupBy(r => (string)r["selection"]!["objectPath"]!)
                .ToDictionary(g => g.Key, g => g.Select(r => (string)r["selection"]!["targetObjectPath"]!).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var reachable = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (string start in matched.Select(r => (string)r["selection"]!["targetObjectPath"]!).Distinct(StringComparer.Ordinal))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<string>(); pending.Push(start);
                while (pending.Count != 0)
                {
                    string current = pending.Pop();
                    if (!seen.Add(current)) continue;
                    if (adjacency.TryGetValue(current, out var next)) foreach (string child in next) pending.Push(child);
                }
                reachable.Add(start, seen);
            }
            foreach (var row in matched)
                row["declaredCycle"] = reachable[(string)row["selection"]!["targetObjectPath"]!].Contains((string)row["selection"]!["objectPath"]!);
        }

        var coverage = new JsonArray();
        foreach (var owner in objects.Values)
        {
            int[] indices = Enumerable.Range(0, owner.Dependencies?.Length ?? 0).ToArray();
            var mapped = selected.Where(p => p.Key.Scope == owner.Scope && p.Key.Path == owner.Path).ToDictionary(p => p.Key.Index, p => p.Value);
            var repeats = owner.Dependencies?.Select((id, index) => (Id: id, Index: index)).GroupBy(p => p.Id).Where(g => g.Count() > 1);
            JsonArray Numbers(IEnumerable<int> numbers) => new(numbers.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            coverage.Add(new JsonObject { ["scope"] = owner.Scope, ["reviewId"] = owner.ReviewId, ["objectPath"] = owner.Path,
                ["dependencyStatus"] = owner.Dependencies is null ? "unavailable" : "observed", ["reason"] = owner.Reason,
                ["requiredCount"] = owner.Dependencies is null ? null : JsonValue.Create(owner.Dependencies.Length),
                ["selectedIndices"] = Numbers(indices.Where(mapped.ContainsKey)), ["unassociatedIndices"] = Numbers(indices.Where(i => !mapped.ContainsKey(i))),
                ["repeatedVersionGuids"] = new JsonArray((repeats ?? []).Select(g => (JsonNode?)new JsonObject { ["versionGuid"] = g.Key, ["indices"] = Numbers(g.Select(p => p.Index)) }).ToArray()),
                ["selfReferenceIndices"] = Numbers(indices.Where(i => owner.Dependencies![i] == owner.Version)),
                ["allObservedIndicesMatched"] = owner.Dependencies is not null && indices.All(i => mapped.TryGetValue(i, out var row) && (string?)row["status"] == "match"),
                ["dependencyClosureVerified"] = false });
        }
        report["schemaVersion"] = "hmi-component-review.v6";
        report["dependencyAssociations"] = rows; report["dependencyCoverage"] = coverage; report["dependencyIdentities"] = identities;
        var summary = report["summary"]!.AsObject();
        summary["dependencyAssociationCount"] = rows.Count;
        summary["dependencyMismatchCount"] = rows.Count(r => (string?)r!["status"] == "different");
        summary["dependencyUnavailableCount"] = rows.Count(r => (string?)r!["status"] == "unavailable");
        summary["dependencyConflictingCount"] = rows.Count(r => (string?)r!["status"] == "conflicting");
        summary["dependencyUnobservedObjectCount"] = coverage.Count(r => (string?)r!["dependencyStatus"] == "unavailable");
        summary["dependencyUnassociatedIndexCount"] = coverage.Sum(r => r!["unassociatedIndices"]!.AsArray().Count);
        summary["dependencyCyclicMappingCount"] = rows.Count(r => (bool)r!["declaredCycle"]!);
        report["dependencyAssociationPolicy"] = "Explicit dependency indices and target object paths within each selected saved component scope only. Compare original direct version GUID observations with selected object identities and existing original-export associations. Repeated indices, conflicting type observations and cycles remain explicit. At most 1000 entries per observed list, 20000 total entries and 4096 explicit mappings. Missing or malformed observations remain unavailable, never empty. Matching all observed indices does not establish complete native dependencies, consumers, instance identity, source authenticity, safe installation or generation. No name joins, inferred cross-review edges or native operation admission.";
    }

    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? GuidText(JsonNode? node)
    {
        string? text = String(node);
        return text?.Length == 36 && Guid.TryParseExact(text, "D", out var guid) && guid != Guid.Empty ? guid.ToString("D") : null;
    }
    private static (string? Type, string? Version) Identity(JsonObject? value)
    {
        if (String(value?["schemaVersion"]) != "plc-library-instance-observation.v1" ||
            String(value?["source"]) != "LibraryTypeInstanceInfo.LibraryTypeVersion" || String(value?["status"]) != "linked") return (null, null);
        string? type = GuidText(value?["typeGuid"]), version = GuidText(value?["versionGuid"]);
        return type is null || version is null ? (null, null) : (type, version);
    }
    private static string Agreement(string expected, string? actual) => actual is null ? "unavailable" : expected == actual ? "match" : "different";
}
