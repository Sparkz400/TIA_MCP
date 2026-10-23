using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Explicit supplied operand observations checked against independent executable declarations.</summary>
internal static class DiagnosticAlarmBindings
{
    private const int MaxBytes = 512 * 1024;

    internal static JsonObject Resolve(DiagnosticEvidence source, JsonObject alarm, JsonObject? instance, JsonArray signals, JsonNode? value)
    {
        if (instance is null) throw new BridgeException("InstanceResolutionIncomplete", "Alarm bindings require an explicitly selected executable instance.");
        if (value is not JsonArray selectors || selectors.Count is < 1 or > 16)
            throw new BridgeException("InvalidRequest", "alarmBindings requires 1..16 explicit operand selections.");
        var rows = new JsonArray(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonObject { ["schemaVersion"] = "diagnostic-alarm-bindings.v1", ["reviewId"] = alarm["reviewId"]!.DeepClone(),
            ["alarmSelector"] = alarm["selector"]!.DeepClone(), ["selectors"] = selectors.DeepClone(), ["bindings"] = rows,
            ["allSelectedMatched"] = false, ["sourceAuthenticityVerified"] = false, ["nativeQualified"] = false,
            ["runtimeAlarmAssociationVerified"] = false, ["coverageComplete"] = false, ["maxBytes"] = MaxBytes,
            ["policy"] = "Normalized supplied alarm operands and declared relationship paths, independently checked against saved executable source. Matching declarations do not prove alarm generation, runtime invocation, causality or complete consumers." };
        foreach (var item in selectors)
        {
            var selector = ExactObject(item, "id", "signalId", "operand", "edgeIndices");
            string id = Text(selector, "id", 64), signalId = Text(selector, "signalId", 64);
            if (!ids.Add(id)) throw new BridgeException("InvalidRequest", "Alarm binding IDs must be unique.");
            var endpoint = ExactObject(selector["operand"], "nodeId", "objectPath");
            Text(endpoint, "nodeId", 4096); string path = Text(endpoint, "objectPath", 4096);
            if (selector["edgeIndices"] is not JsonArray indices || indices.Count is < 1 or > 8)
                throw new BridgeException("InvalidRequest", "Select 1..8 original directed relationship indices.");
            var edgeIndices = new List<int>();
            foreach (var index in indices)
            {
                if (index is not JsonValue number || !number.TryGetValue<int>(out int n) || n < 0 || n >= D.Rows(source.Review["model"]?["edges"]).Count || edgeIndices.Contains(n))
                    throw new BridgeException("InvalidRequest", "Relationship indices must be distinct original array positions.");
                edgeIndices.Add(n);
            }
            var selectedSignals = signals.OfType<JsonObject>().Where(s => D.Text(s, "id") == signalId).ToArray();
            if (selectedSignals.Length != 1) throw new BridgeException("InvalidRequest", "Select exactly one existing diagnostic signal by ID.");
            if (!ExactEndpoint(source, endpoint) || !source.Nodes.TryGetValue(path, out var node))
                throw new BridgeException("InvalidRequest", "Operand selection must match an exact nonvirtual saved node and path.");
            var edges = new JsonArray(edgeIndices.Select(i => (JsonNode?)new JsonObject { ["index"] = i,
                ["edge"] = source.Review["model"]!["edges"]![i]!.DeepClone() }).ToArray());
            var row = new JsonObject { ["id"] = id, ["signalId"] = signalId, ["operand"] = node.DeepClone(), ["relationships"] = edges,
                ["signal"] = selectedSignals[0].DeepClone(), ["status"] = "observationUnavailable", ["declaration"] = null, ["storage"] = null };
            rows.Add(row);
            row["status"] = Evaluate(source, alarm, instance, selectedSignals[0], node, edges, row);
            if (Encoding.UTF8.GetByteCount(result.ToJsonString(BridgeProtocol.Json)) > MaxBytes)
                throw new BridgeException("ArtifactCapacity", "Alarm binding evidence exceeds 512 KiB; narrow the explicit selection. Original reviews remain retained.");
        }
        result["allSelectedMatched"] = rows.All(r => D.Text(r, "status") == "capturedDeclarationMatch");
        return result;
    }

    private static string Evaluate(DiagnosticEvidence source, JsonObject alarm, JsonObject instance, JsonObject signal, JsonObject node, JsonArray edges, JsonObject row)
    {
        string current = D.Text(alarm["selector"], "objectPath");
        var visited = new HashSet<string>(StringComparer.Ordinal) { current };
        foreach (var entry in edges)
        {
            var edge = entry!["edge"]!;
            if (D.Text(edge, "kind") is not ("dependsOn" or "reads" or "hmiBinding") || edge["unresolved"] is not null ||
                D.Text(edge["evidence"], "resolution") != "resolved" || D.Text(edge["evidence"], "origin") is not ("nativeConfirmed" or "parsedSupported") ||
                !ExactEndpoint(source, edge["source"]) || !ExactEndpoint(source, edge["target"]) || D.Text(edge["source"], "objectPath") != current)
                return "relationshipUnresolved";
            current = D.Text(edge["target"], "objectPath");
            if (!visited.Add(current)) return "relationshipUnresolved";
        }
        if (current != D.Text(node, "objectPath")) return "relationshipUnresolved";
        if (node["metadata"]?["isKnowHowProtected"] is JsonNode protection &&
            (protection is not JsonValue protectionValue || !protectionValue.TryGetValue<bool>(out _))) return "invalidObservation";
        if (D.Text(node, "kind") != "member" || D.Text(node["access"], "state") == "protected" || node["metadata"]?["isKnowHowProtected"]?.GetValue<bool>() == true ||
            node["metadata"]?["alarmOperand"] is not JsonObject observation)
            return "observationUnavailable";
        if (observation["schemaVersion"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version)) return "invalidObservation";
        if (version != "alarm-operand.v1") return "observationUnavailable";
        string[] members; string datatype; JsonObject declaredInstance;
        try
        {
            ExactObject(observation, "schemaVersion", "role", "blockPath", "instance", "memberPath", "datatype");
            if (Text(observation, "role", 32) is not ("condition" or "delay" or "parameter" or "status")) return "invalidObservation";
            Text(observation, "blockPath", 4096); datatype = Text(observation, "datatype", 128);
            members = D.Path(observation["memberPath"]);
            declaredInstance = ExactObject(observation["instance"], "dbPath", "memberPath");
            Text(declaredInstance, "dbPath", 4096);
            if (declaredInstance["memberPath"] is not JsonArray instanceMembers || instanceMembers.Count > 8) return "invalidObservation";
            if (instanceMembers.Count > 0) D.Path(instanceMembers);
        }
        catch (Exception e) when (e is BridgeException or InvalidOperationException or ArgumentException or FormatException)
        { return "invalidObservation"; }
        if (D.Text(observation, "blockPath") != D.Text(instance, "blockPath") || D.Text(declaredInstance, "dbPath") != D.Text(instance, "dbPath") ||
            !JsonNode.DeepEquals(declaredInstance["memberPath"], instance["memberPath"]) || D.Text(node, "plcPath") != D.Text(instance, "plcPath") ||
            D.Text(node, "scopePath").Length == 0 || D.Text(node, "scopePath") != D.Text(instance, "scopePath")) return "ownerMismatch";
        var declaration = source.Declaration(D.Text(instance, "blockPath"), members);
        if (declaration is null) return "memberUnavailable";
        row["declaration"] = source.Evidence(D.Text(instance, "blockPath"), declaration);
        row["declaration"]!["datatype"] = (string?)declaration.Attribute("Datatype");
        if ((string?)declaration.Attribute("Datatype") != datatype) return "datatypeMismatch";
        if (D.Rows(instance["callSites"]).Count == 0 || D.Rows(instance["rootCallSites"]).Count == 0) return "callEvidenceUnavailable";
        if (D.Text(signal, "resolution") != "capturedDeclaration") return "signalUnavailable";
        if (D.Text(signal, "datatype") != datatype) return "datatypeMismatch";
        var storage = new DiagnosticSignalSelector(source, instance, new JsonArray()).OperandStorage(members);
        row["storage"] = storage;
        if (storage["truncated"]!.GetValue<bool>() || D.Rows(storage["gaps"]).Count > 0) return "resolutionIncomplete";
        return D.Rows(storage["candidates"]).Any(c => D.Text(c, "storagePath") == D.Text(signal, "storagePath") &&
            JsonNode.DeepEquals(c?["memberPath"], signal["memberPath"])) ? "capturedDeclarationMatch" : "storageMismatch";
    }

    internal static void RequireMatched(JsonObject plan)
    {
        if (plan["alarmBindings"] is JsonObject bindings && bindings["allSelectedMatched"]?.GetValue<bool>() != true)
            throw new BridgeException("AlarmBindingUnresolved", "Resolve every explicitly selected alarm operand association before watch or trace handoff. Review the retained binding statuses.");
    }

    private static bool ExactEndpoint(DiagnosticEvidence source, JsonNode? endpoint) => endpoint is JsonObject &&
        source.Nodes.TryGetValue(D.Text(endpoint, "objectPath"), out var node) && D.Text(node, "origin") != "virtual" &&
        D.Text(node, "nodeId").Length > 0 && D.Text(node, "nodeId") == D.Text(endpoint, "nodeId") && D.Text(endpoint, "resolution") != "unresolved";
    private static JsonObject ExactObject(JsonNode? value, params string[] keys)
    {
        if (value is not JsonObject obj || obj.Count != keys.Length || keys.Any(k => !obj.ContainsKey(k)))
            throw new BridgeException("InvalidRequest", "Expected only explicit fields: " + string.Join(", ", keys));
        return obj;
    }
    private static string Text(JsonNode value, string key, int max)
    {
        if (value[key] is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl))
            throw new BridgeException("InvalidRequest", "Expected a bounded nonempty string without control characters: " + key);
        return text;
    }
}
