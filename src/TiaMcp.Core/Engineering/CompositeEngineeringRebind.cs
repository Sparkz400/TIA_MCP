using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Output-only to_connect retarget plan for TO hardware connections broken across a declared hardware replacement.</summary>
internal static class CompositeEngineeringRebind
{
    private const int MaxEntries = 200;

    internal static JsonObject Plan(string artifactsRoot, JsonObject report, string recordRef, bool toConnectAvailable)
    {
        // A replacement is a declared model correspondence, or an AML correspondence whose objects carry declared hardware mappings in both phases.
        var hardware = report["mappings"]!.AsArray().Where(r => Text(r!["state"]) == "declaredAssociation" && Text(r!["mapping"]!["purpose"]) == "hardware")
            .Select(r => r!["mapping"]!).ToLookup(m => (Text(m["phase"]), Text(m["source"]!["path"])), m => Text(m["target"]!["objectPath"])!);
        IEnumerable<string> Model(JsonNode selector, string phase) => Text(selector["kind"]) switch
        { "model" => [Text(selector["objectPath"])!], "amlObject" => hardware[(phase, Text(selector["path"]))], _ => [] };
        var replacements = new Dictionary<string, List<(string Path, string Id)>>(StringComparer.Ordinal);
        foreach (var claim in report["correspondences"]!.AsArray().Where(r => Text(r!["state"]) == "declaredCorrespondence").Select(r => r!["correspondence"]!))
            foreach (string from in Model(claim["before"]!, "before")) foreach (string to in Model(claim["after"]!, "after"))
            {
                if (!replacements.TryGetValue(from, out var list)) replacements.Add(from, list = []);
                list.Add((to, Text(claim["id"])!));
            }
        var technology = new TechnologyProfileWorkflowStore(artifactsRoot);
        IEnumerable<(string Record, JsonNode Row)> Relationships(string phase) => report["contexts"]![phase]!["technology"]!.AsArray().Select(r => Text(r)!)
            .SelectMany(record => technology.ReadVerified(record)["report"]!["relationships"]!.AsArray().Select(row => (record, row!)))
            .Where(x => Text(x.Item2["phase"]) == phase && Text(x.Item2["relationship"]!["kind"]) == "hardware");
        var after = Relationships("after").Select(x => x.Row).ToLookup(r => (Text(r["relationship"]!["sourcePath"]), Text(r["relationship"]!["id"])));
        var covered = after.Select(g => g.Key.Item1).ToHashSet(StringComparer.Ordinal);
        var entries = new JsonArray(); int broken = 0, uncovered = 0;
        foreach (var (record, row) in Relationships("before").Where(x => Text(x.Row["relationship"]!["state"]) == "declared"))
        {
            var relation = row["relationship"]!; var metadata = relation["metadata"]!;
            string path = Text(relation["sourcePath"])!, id = Text(relation["id"])!, target = Text(relation["targetPath"])!;
            // No after-phase record for this TO is missing evidence, not a broken connection.
            if (!covered.Contains(path)) { uncovered++; continue; }
            var current = after[(path, id)].ToArray();
            string afterState = current.Length == 0 ? "missing" : current.Length > 1 ? "ambiguous"
                : Text(current[0]["relationship"]!["state"]) == "declared" ? Text(current[0]["targetResolution"])! : Text(current[0]["relationship"]!["state"])!;
            if (afterState == "capturedObject" || ++broken > MaxEntries) continue;
            var candidates = replacements.GetValueOrDefault(target) ?? [];
            string[] paths = candidates.Select(c => c.Path).Distinct(StringComparer.Ordinal).ToArray();
            string? role = Text(metadata["role"]); int? sensor = Number(metadata["sensorIndex"]), channelIndex = Number(metadata["channelIndex"]);
            var channel = metadata["target"] is JsonObject reference && Text(reference["kind"]) == "channel" ? reference : null;
            string state = paths.Length == 0 ? "noDeclaredReplacement" : paths.Length > 1 ? "conflictingReplacement"
                : role is null || role == "sensor" && sensor is null ? "connectionRoleUnknown" : toConnectAvailable ? "planned" : "changeKindUnavailable";
            JsonObject? change = null;
            if (state == "planned")
            {
                var payload = new JsonObject { ["technologyPath"] = path, ["role"] = role, ["hardwarePath"] = paths[0] };
                if (role == "sensor") payload["sensorIndex"] = sensor;
                if (channel is not null) { payload["channelType"] = channel["type"]?.DeepClone(); payload["channelIoType"] = channel["ioType"]?.DeepClone(); payload["channelNumber"] = channel["number"]?.DeepClone(); }
                else if (channelIndex is not null) payload["channelIndex"] = channelIndex;
                change = new JsonObject { ["kind"] = "to_connect", ["payload"] = payload };
            }
            entries.Add(new JsonObject { ["technologyRecordRef"] = record, ["technologyPath"] = path, ["relationshipId"] = id, ["role"] = role, ["beforeTargetPath"] = target,
                ["afterState"] = afterState, ["replacementHardwarePaths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()),
                ["correspondenceIds"] = new JsonArray(candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Select(c => (JsonNode)c).ToArray()),
                ["selectorCarriedOver"] = channel is not null || channelIndex is not null, ["state"] = state, ["change"] = change });
        }
        return new JsonObject { ["schemaVersion"] = "composite-engineering-rebind-plan.v1", ["recordRef"] = recordRef, ["toConnectAvailable"] = toConnectAvailable,
            ["brokenConnectionCount"] = broken, ["notCapturedAfterCount"] = uncovered, ["truncated"] = broken > MaxEntries, ["entries"] = entries,
            ["applied"] = false, ["nativeVerified"] = false,
            ["limits"] = "Output only; nothing is prepared or applied. Breakage is the after-phase TO relationship being missing, disconnected, unknown or unresolved; replacements come only from declared correspondences. A carried-over channel or channel index is the before selector on the replacement, not a verified equivalent. Each change must still pass change_prepare/apply with its own preview, checkpoint and compile." };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
    private static int? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;
}
