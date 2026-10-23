using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Explicit, immutable observation intent; never a proof of an online PLC address.</summary>
public sealed class DiagnosticUaBindings(string artifactsRoot)
{
    public const string Limits = "Caller-selected UA nodes are associated with saved diagnostic declarations only. No server datatype equivalence, online PLC address, running-program identity, physical device, clock synchronization or behavioral assertion is verified. The binding must be selected before acquisition; existing unbound captures cannot acquire it retroactively.";

    public JsonObject Plan(string experimentId, JsonObject request)
    {
        var binding = Build(experimentId, request);
        var receipt = new DiagnosticStore(artifactsRoot).Save(binding);
        receipt["binding"] = binding;
        return receipt;
    }

    private JsonObject Build(string experimentId, JsonObject request)
    {
        D.Bound(request);
        TraceQualityMeasurements.Fields(request, "attemptId", "reason", "mappings");
        string attemptId = D.Required(request, "attemptId", 32);
        if (attemptId.Length != 32 || attemptId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new BridgeException("InvalidRequest", "attemptId must be an exact lowercase 32-hex session attempt ID.");
        D.Required(request, "reason", 2000);
        var mappings = TraceQualityMeasurements.Array(request, "mappings", 64);
        if (mappings.Count == 0) throw new BridgeException("InvalidRequest", "Select 1..64 explicit diagnostic signal to UA node mappings.");
        var experiment = new DiagnosticExperiments(artifactsRoot).ReadExperiment(experimentId);
        var signals = D.Rows(experiment["diagnosticCase"]!["signals"]);
        var selected = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new HashSet<OpcUaRuntimeNode>();
        foreach (var item in mappings)
        {
            var mapping = item as JsonObject ?? throw new BridgeException("InvalidRequest", "Each mapping must be an object.");
            TraceQualityMeasurements.Fields(mapping, "signalId", "node", "unit", "reason");
            string id = D.Required(mapping, "signalId", 64);
            D.Required(mapping, "unit", 128); D.Required(mapping, "reason", 2000);
            var nodeJson = TraceQualityMeasurements.Object(mapping, "node");
            TraceQualityMeasurements.Fields(nodeJson, "namespaceUri", "identifierType", "identifier");
            var node = new OpcUaRuntimeNode(D.Required(nodeJson, "namespaceUri"), D.Required(nodeJson, "identifierType", 16), D.Required(nodeJson, "identifier"));
            OpcUaCaptureContracts.ValidateNode(node);
            var matches = signals.Where(s => D.Text(s, "id") == id).ToArray();
            if (!ids.Add(id) || !nodes.Add(node) || matches.Length != 1 || D.Text(matches[0], "resolution") != "capturedDeclaration")
                throw new BridgeException("UaDiagnosticBindingMismatch", "Mappings require distinct exact nodes and distinct resolved signals from this experiment.");
            selected.Add(new JsonObject { ["signalId"] = id, ["node"] = JsonSerializer.SerializeToNode(node, BridgeProtocol.Json),
                ["unit"] = mapping["unit"]!.DeepClone(), ["reason"] = mapping["reason"]!.DeepClone(),
                ["declaration"] = matches[0]!.DeepClone() });
        }
        var result = new JsonObject { ["schemaVersion"] = "diagnostic-ua-binding.v1", ["experimentId"] = experimentId,
            ["attemptId"] = attemptId, ["request"] = request.DeepClone(), ["experiment"] = experiment,
            ["mappings"] = selected, ["associationKind"] = "callerSelectedObservationIntent",
            ["programMatchVerified"] = false, ["addressMatchVerified"] = false, ["datatypeMatchVerified"] = false,
            ["sourceAuthenticityVerified"] = false, ["physicalQualified"] = false, ["clockSynchronizationVerified"] = false,
            ["assertionsAdmitted"] = false, ["nativeAccessAttempted"] = false, ["limits"] = Limits };
        D.Bound(result, DiagnosticStore.MaxBytes);
        return result;
    }

    public JsonObject Read(string bindingId)
    {
        var saved = new DiagnosticStore(artifactsRoot).Read(bindingId, "diagnostic-ua-binding.v1");
        var derived = Build(D.Required(saved, "experimentId", 64), TraceQualityMeasurements.Object(saved, "request"));
        if (!JsonNode.DeepEquals(saved, derived))
            throw new BridgeException("UaDiagnosticBindingMismatch", "Retained binding differs from its original experiment, declarations or explicit mapping request.");
        return saved;
    }

    public JsonObject ValidateForCapture(string bindingId, string attemptId, OpcUaRuntimeNode[] nodes)
    {
        var binding = Read(bindingId);
        var selected = binding["mappings"]!.AsArray().Select(m => m!["node"]!.Deserialize<OpcUaRuntimeNode>(BridgeProtocol.Json)!);
        if (D.Text(binding, "attemptId") != attemptId || !selected.SequenceEqual(nodes))
            throw new BridgeException("UaDiagnosticBindingMismatch", "The capture must use the binding's exact session attempt and ordered node selection.");
        return binding;
    }

    public static void ValidateId(string? id)
    {
        if (id is not null && (id.Length != 64 || id.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new BridgeException("InvalidRequest", "diagnosticBindingId must be an exact lowercase SHA-256 ID.");
    }
}
