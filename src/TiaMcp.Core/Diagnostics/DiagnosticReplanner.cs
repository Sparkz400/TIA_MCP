using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

public sealed class DiagnosticReplanner(string artifactsRoot)
{
    public JsonObject Replan(string planId, JsonObject request)
    {
        D.Bound(request); var old = new DiagnosticStore(artifactsRoot).Read(planId, "diagnostic-case.v1");
        DiagnosticPlanner.Revalidate(old, new ProjectReviewStore(artifactsRoot).Read(D.Required(old, "reviewId", 64)));
        D.Required(request, "reason", 2000);
        var mappings = request["signalMappings"] as JsonArray ?? throw new BridgeException("InvalidRequest", "Explicit mappings for all old signals are required; instance names are never string-replaced.");
        var oldSignals = D.Rows(old["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        if (mappings.Count != oldSignals.Count || !mappings.Select(m => D.Text(m, "id")).ToHashSet(StringComparer.Ordinal).SetEquals(oldSignals.Keys))
            throw new BridgeException("InvalidRequest", "Map every old signal exactly once.");
        var signals = new JsonArray();
        foreach (var mapping in mappings)
        {
            var oldSignal = oldSignals[D.Text(mapping, "id")];
            signals.Add(new JsonObject { ["id"] = oldSignal["id"]!.DeepClone(), ["role"] = oldSignal["role"]!.DeepClone(), ["hypothesisIds"] = oldSignal["hypothesisIds"]!.DeepClone(),
                ["reason"] = D.Required(mapping!, "reason", 2000), ["storagePath"] = D.Required(mapping!, "storagePath"), ["memberPath"] = D.Strings(D.Path(mapping?["memberPath"])), ["expectedDatatype"] = oldSignal["datatype"]?.DeepClone() });
        }
        var next = new JsonObject { ["reviewId"] = D.Required(request, "reviewId", 64), ["report"] = request["report"]?.DeepClone() ?? old["report"]!.DeepClone(),
            ["templateId"] = old["template"]?["id"]?.DeepClone(),
            ["testSuiteFinding"] = request["testSuiteFinding"]?.DeepClone(),
            ["blockPath"] = D.Required(request, "blockPath"), ["instance"] = request["instance"]?.DeepClone(), ["searchTerms"] = request["searchTerms"]?.DeepClone(),
            ["signals"] = signals, ["hypotheses"] = old["hypotheses"]!.DeepClone(), ["observations"] = request["observations"]?.DeepClone(),
            ["context"] = request["context"]?.DeepClone(), ["lineage"] = new JsonObject { ["previousPlanId"] = planId, ["reason"] = request["reason"]!.DeepClone(), ["mapping"] = mappings.DeepClone() } };
        if (request.ContainsKey("alarmContext")) next["alarmContext"] = request["alarmContext"]?.DeepClone();
        if (request.ContainsKey("alarmBindings")) next["alarmBindings"] = request["alarmBindings"]?.DeepClone();
        return new DiagnosticPlanner(artifactsRoot).Plan(next);
    }
}
