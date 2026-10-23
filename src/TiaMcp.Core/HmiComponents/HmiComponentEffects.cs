using System.Text.Json.Nodes;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Independent supplied post-operation files compared with the reviewed output intent.</summary>
internal static class HmiComponentEffects
{
    internal static void Add(JsonObject report, JsonObject request)
    {
        var files = HmiComponentReview.Files(request).ToDictionary(f => HmiComponentReview.Text(f, "id"), StringComparer.Ordinal);
        var outputs = report["outputs"]!.AsArray().OfType<JsonObject>().ToDictionary(o => (string)o["id"]!, StringComparer.Ordinal);
        var observations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var value in HmiComponentReview.Array(request, "observations", 256))
        {
            var observation = HmiComponentReview.Object(value);
            HmiComponentReview.Fields(observation, "outputId files complete");
            string id = HmiComponentReview.Text(observation, "outputId", 128);
            if (!outputs.ContainsKey(id) || !observations.TryAdd(id, observation)) throw HmiComponentReview.Error("Observation must select a unique reviewed output ID.");
            _ = HmiComponentReview.Boolean(observation, "complete");
        }
        var checks = new JsonArray();
        foreach (var output in outputs.Values)
        {
            string id = (string)output["id"]!, action = (string)output["selection"]!["action"]!;
            var expected = action == "remove" ? new JsonArray() : output[action == "retain" ? "currentFiles" : "proposedFiles"]!.DeepClone().AsArray();
            bool expectedComplete = action == "remove" || (bool)output["selection"]![action == "retain" ? "currentComplete" : "proposedComplete"]! && expected.Count > 0;
            var observed = new JsonArray(); var gaps = new JsonArray();
            bool observedComplete = false;
            if (observations.TryGetValue(id, out var observation))
            {
                observedComplete = HmiComponentReview.Boolean(observation, "complete");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fileId in HmiComponentReview.Array(observation, "files", 32))
                {
                    if (fileId is not JsonValue v || !v.TryGetValue<string>(out string? key) || key is null || !files.TryGetValue(key, out var file))
                        throw HmiComponentReview.Error("Observation refers to an unknown file ID.");
                    if (!names.Add((string)file["fileName"]!)) throw HmiComponentReview.Error("Observed filenames must be unique, including case variants.");
                    observed.Add(new JsonObject { ["fileName"] = file["fileName"]!.DeepClone(), ["sha256"] = file["sha256"]!.DeepClone(), ["bytes"] = file["bytes"]!.DeepClone() });
                }
            }
            else gaps.Add("No independent post-operation observation was supplied for this output.");
            if (!expectedComplete) gaps.Add("The reviewed expected file set is incomplete.");
            if (!observedComplete) gaps.Add("The observed file set is not declared complete; absence is not proof of removal.");
            var expectedByName = expected.OfType<JsonObject>().ToDictionary(f => (string)f["fileName"]!, StringComparer.Ordinal);
            var observedByName = observed.OfType<JsonObject>().ToDictionary(f => (string)f["fileName"]!, StringComparer.Ordinal);
            var rows = new JsonArray();
            foreach (string name in expectedByName.Keys.Union(observedByName.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                expectedByName.TryGetValue(name, out var wanted); observedByName.TryGetValue(name, out var actual);
                string status = wanted is null ? (expectedComplete ? "unexpected" : "unresolvedExpected") : actual is null ? (observedComplete ? "missing" : "unobserved") :
                    JsonNode.DeepEquals(wanted, actual) ? "match" : "different";
                rows.Add(new JsonObject { ["fileName"] = name, ["status"] = status, ["expected"] = wanted?.DeepClone(), ["observed"] = actual?.DeepClone() });
            }
            bool mismatch = rows.Any(r => (string?)r!["status"] is "unexpected" or "missing" or "different");
            checks.Add(new JsonObject { ["outputId"] = id, ["action"] = action, ["comparison"] = "exactBytes",
                ["expectedCompleteDeclared"] = expectedComplete, ["observedCompleteDeclared"] = observedComplete,
                ["status"] = mismatch ? "mismatch" : !expectedComplete || !observedComplete ? "incomplete" : "match",
                ["files"] = rows, ["gaps"] = gaps, ["nativeEffectVerified"] = false, ["contentAssociationVerified"] = false,
                ["contentCompletenessVerified"] = false, ["ownershipVerified"] = false });
        }
        report["schemaVersion"] = "hmi-component-review.v3";
        report["effectChecks"] = checks;
        foreach (string status in new[] { "match", "mismatch", "incomplete" })
            report["summary"]!["effect" + char.ToUpperInvariant(status[0]) + status[1..] + "Count"] = checks.Count(c => (string)c!["status"]! == status);
        report["effectPolicy"] = "Compare independently supplied observed bytes with proposed bytes for create/update, current bytes for retain, and an empty expected set for remove. Exact case-sensitive filenames and SHA-256/length are compared; no XML/YAML normalization, scripts or native execution. Completeness and output associations are caller declarations, never native proof. Missing observations and incomplete sets remain gaps; unselected outputs and whole-project preservation remain unverified.";
    }
}
