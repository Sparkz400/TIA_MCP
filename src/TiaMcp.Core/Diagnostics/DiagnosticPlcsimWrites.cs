using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>One write of a diagnostic-case signal: symbol/type/datatype follow the PLCSIM capture source rules.</summary>
public sealed record DiagnosticPlcsimWrite(string SignalId, string Symbol, string Type, string Datatype, JsonElement Expected, JsonElement Value, string Reason);

/// <summary>Admits writes of exact diagnostic-case signals on an owned PLCSIM instance and maps the worker's guarded result. Never a physical PLC.</summary>
public static class DiagnosticPlcsimWrites
{
    /// <summary>Reuses PLCSIM capture admission: owned-instance identity format, type/datatype fit and re-resolved case declarations.</summary>
    public static (JsonObject Arguments, RuntimeCaptureAssociation Association) Admit(string artifactsRoot, string caseId,
        int instanceId, string instanceName, string creationId, DiagnosticPlcsimWrite[] writes)
    {
        if (writes is not { Length: >= 1 and <= 16 } || writes.Any(w => w is null))
            throw new BridgeException("RuntimeWriteLimit", "Supply 1..16 diagnostic signal writes; nothing was sent.");
        string reviewId = D.Required(new DiagnosticStore(artifactsRoot).Read(caseId, "diagnostic-case.v1"), "reviewId", 64);
        string[] labels = writes.Select((_, i) => i.ToString("x32")).ToArray();
        var request = new RuntimeCaptureRequest(creationId, labels, 100, 1, ReviewId: reviewId,
            Association: new(caseId, writes.Select((w, i) => new RuntimeCaptureMapping(labels[i], w.SignalId, w.Reason)).ToArray()),
            Source: "plcsim", Simulator: new(instanceId, instanceName, writes.Select(w => new SimulatorCaptureSignal(w.Symbol, w.Type, w.Datatype)).ToArray()));
        RuntimeCaptureStore.Validate(request);
        var association = RuntimeCaptureAssociations.Resolve(artifactsRoot, request, SimulatorCaptureContracts.Signals(request))!;
        var signals = request.Simulator!.Signals;
        if (writes.Where((w, i) => !SimulatorCaptureContracts.Value(signals[i], w.Expected) || !SimulatorCaptureContracts.Value(signals[i], w.Value)).Any())
            throw new BridgeException("InvalidRequest", "expected and value must be exact JSON values of each signal's declared datatype; nothing was sent.");
        var arguments = new JsonObject { ["instanceId"] = instanceId, ["instanceName"] = instanceName, ["creationId"] = creationId,
            ["writes"] = new JsonArray(writes.Select(w => (JsonNode?)new JsonObject { ["symbol"] = w.Symbol, ["type"] = w.Type,
                ["expected"] = JsonNode.Parse(w.Expected.GetRawText()), ["value"] = JsonNode.Parse(w.Value.GetRawText()) }).ToArray()) };
        return (arguments, association);
    }

    public static JsonObject Receipt(JsonObject arguments, RuntimeCaptureAssociation association, JsonElement response)
    {
        var result = JsonNode.Parse(response.GetRawText()) as JsonObject;
        if (result?["instanceId"] is not JsonValue id || !id.TryGetValue<int>(out int instanceId) || instanceId != arguments["instanceId"]!.GetValue<int>() ||
            D.Text(result, "instanceName") != D.Text(arguments, "instanceName") || D.Text(result, "state") is not ("written" or "incomplete" or "preconditionMismatch"))
            throw new BridgeException("PlcsimResponseInvalid", "The simulator worker returned no usable write result; the outcome is unknown and is not retried.");
        result["schemaVersion"] = "diagnostic-plcsim-write.v1"; result["caseId"] = association.CaseId;
        result["association"] = JsonSerializer.SerializeToNode(association, BridgeProtocol.Json);
        foreach (string claim in new[] { "physicalPlc", "forced", "programMatchVerified", "nativeQualified" }) result[claim] = false;
        return result;
    }
}
