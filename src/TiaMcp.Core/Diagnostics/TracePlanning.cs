using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

public sealed class TracePlanning(string artifactsRoot)
{
    public static JsonObject Capabilities() => new() {
        ["schemaVersion"] = "trace-capabilities.v1", ["targetProbed"] = false, ["nativeQualification"] = false,
        ["routes"] = new JsonArray(
            Route("manual", "ordinaryPlc", "documentedUi", "specificationOnly", "Human configuration/readback/transfer/arming/recording/export required; .ttcfgx is not a verified generation schema."),
            Route("nativeApi", "ordinaryPlc", "notEstablished", "unavailable", "Installed V21 public references do not establish general PLC trace lifecycle APIs."),
            Route("simaticAxCli", "axPlcTrace", "documentedExternalCli", "unavailable", "SIMATIC AX trace 2.8.1 documents YAML-configured PLC jobs and TIA tags on S7-1500 firmware >=2.6, but excludes save triggers on TIA-engineered PLCs. Requires AX project/toolchain/entitlement and target qualification. Full settings readback and TIA project trace-object interoperability are not established; this bridge does not execute the CLI."),
            Route("nativeApi", "longTerm", "notEstablished", "unavailable", "Separate target and continuous acquisition qualification required."),
            Route("nativeApi", "projectTrace", "notEstablished", "unavailable", "Multi-device prerequisites, synchronization and combined formats are not qualified."),
            Route("nativeApi", "drive", "notEstablished", "unavailable", "Startdrive UI availability is not ordinary PLC Openness support."),
            Route("safetyValidation", "safetyTest", "documentedSeparateFeature", "unavailable", "SafetyFunction.TraceConfiguration belongs to Safety Validation; this module does not expose it."),
            Route("uiAssistance", "ordinaryPlc", "separateAuthorizationAndQualification", "unavailable", "Existing native UI pause remains in force."),
            Route("sim_test", "polling", "existingSimulator", "separateTool", "Wall-clock sequential polling; non-atomic across symbols, no scan accuracy, cannot guarantee one-scan glitches. sim_test can write stimuli.")),
        ["measurementFormats"] = new JsonArray("trace-measurement.v1 (explicit normalized JSON)", "trace-measurement.v2 (byte-exact supplied JSON with per-reading quality, clock domains/epochs and incomplete captures; all behavioral verdicts unresolved)", "UTF-8 CSV with explicit column/decimal/unit/clock mapping; no native dialect qualification", "No binary .ttrecx/.lttcd/.dat decoder or .ttcfgx generator"),
        ["targetLimits"] = null, ["limitsReason"] = "CPU, firmware, product, license, types, OB timing, slots and resource limits require target-specific validation.",
        ["diagnosticTemplates"] = DiagnosticTemplates.All(),
        ["sources"] = new JsonArray(
            "https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function",
            "https://docs.tia.siemens.cloud/r/en-us/v21/trace-and-logic-analyzer-function/using-trace-functions/configuring/export-configuration",
            "https://docs.industrial-operations-x.siemens.cloud/r/en-us/ax/tracing-docs/2.8.1/tracing?contentId=Fy96HyUxq~OmdgolDfoW2w",
            "docs/plans/expansion-plans/bug-driven-trace-simatic-ax-feasibility.md",
            "docs/plans/expansion-plans/bug-driven-trace-diagnostics.md")
    };
    private static JsonObject Route(string backend, string family, string api, string implementation, string limit) => new() {
        ["backend"] = backend, ["family"] = family, ["documentedRoute"] = api, ["implementation"] = implementation,
        ["qualification"] = "unqualified", ["targetAvailability"] = "unknown", ["limitations"] = limit };

    public JsonObject Prepare(string planId, JsonObject request)
    {
        var specification = Build(planId, request);
        var receipt = new DiagnosticStore(artifactsRoot).Save(specification); receipt["specification"] = specification; return receipt;
    }

    internal JsonObject Build(string planId, JsonObject request)
    {
        D.Bound(request);
        RejectUnknown(request, ["backend", "family", "reviewId", "settings"]);
        string backend = D.Required(request, "backend", 40);
        if (backend != "manual") throw new BridgeException("UnsupportedCapability", "Only a manual trace specification is implemented. No native configuration, polling or device mutation was attempted.");
        if (D.Text(request, "family") != "ordinaryPlc") throw new BridgeException("UnsupportedCapability", "Only an ordinary PLC manual specification is implemented; other trace families require separate qualification.");
        var store = new DiagnosticStore(artifactsRoot); var plan = store.Read(planId, "diagnostic-case.v1");
        string reviewId = D.Text(plan, "reviewId");
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        if (D.Text(request, "reviewId") != reviewId) throw new BridgeException("TracePlanStale", "Supply the plan's exact reviewId. A new source review requires a new diagnostic plan and instance/signal resolution.");
        DiagnosticPlanner.Revalidate(plan, review);
        DiagnosticAlarmBindings.RequireMatched(plan);
        if (plan["selectedInstance"] is null || D.Rows(plan["signals"]).Count == 0)
            throw new BridgeException("InstanceResolutionIncomplete", "Select captured instance storage and a nonempty signal manifest first.");
        if (D.Rows(plan["selectedInstance"]?["rootCallSites"]).Count == 0 || D.Rows(plan["selectedInstance"]?["callSites"]).Count == 0)
            throw new BridgeException("InstanceResolutionIncomplete", "Capture the missing root/callee call site before preparing a manual trace specification.");
        var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToArray();
        if (signals.Any(s => D.Text(s, "resolution") == "unavailable"))
            throw new BridgeException("SignalUnobservable", "Resolve or explicitly remove unavailable signals in a new diagnostic plan; substitutes need their own evidence and reason.");
        var settings = request["settings"] as JsonObject ?? throw new BridgeException("InvalidRequest", "Explicit requested settings are required; there are no universal device defaults.");
        RejectUnknown(settings, ["task", "rationale", "sampleEvery", "preSamples", "postSamples", "trigger"]);
        D.Required(settings, "task", 256); D.Required(settings, "rationale", 2000);
        foreach (string key in new[] { "sampleEvery", "preSamples", "postSamples" })
        {
            int value = settings[key]?.GetValue<int>() ?? -1;
            if (value < (key == "preSamples" ? 0 : 1) || value > 10000000)
                throw new BridgeException("InvalidRequest", key + " is outside request bounds; these bounds are not device limits.");
        }
        var trigger = settings["trigger"] as JsonObject ?? throw new BridgeException("InvalidRequest", "A single explicit trigger is required.");
        RejectUnknown(trigger, ["signalId", "kind", "threshold"]);
        string triggerId = D.Required(trigger, "signalId", 64), kind = D.Required(trigger, "kind", 40);
        var triggerSignal = signals.SingleOrDefault(s => D.Text(s, "id") == triggerId)
            ?? throw new BridgeException("InvalidRequest", "Trigger must reference a selected signal.");
        if (kind is not ("risingEdge" or "fallingEdge" or "above" or "below")) throw new BridgeException("UnsupportedCapability", "This specification supports one edge or threshold trigger; compound triggers are not inferred.");
        if ((kind is "risingEdge" or "fallingEdge") && !D.Text(triggerSignal, "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidRequest", "An edge trigger requires a Bool declaration.");
        if (kind is "above" or "below")
        {
            if (D.Text(triggerSignal, "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase)
                || trigger["threshold"] is not JsonValue value || !value.TryGetValue<double>(out double threshold) || !double.IsFinite(threshold))
                throw new BridgeException("InvalidRequest", "Threshold trigger requires a finite numeric threshold and non-Bool declaration.");
        }
        var pending = new JsonArray("Verify current project, CPU order number, firmware, software unit, namespace and online program against the captured source.",
            "Verify every symbolic member in the native trace signal selector; no online address is inferred from block-local names.",
            "Verify signal count/types, optimized DB/access compatibility, free trace slots, memory, duration, sampling task and CPU load.",
            "Verify trigger support, end-of-OB sampling semantics and requested pre/post sample counts on this target; a one-scan transient inside a task may remain invisible.",
            "Independently read back every signal and setting; retain that evidence before transfer/arming.");
        var steps = new JsonArray("Create a new uniquely named user-reviewed trace in TIA; preserve all existing traces and measurements. Stop if resources are exhausted.",
            "Enter the exact signal components, scopes and requested settings below; resolve every pending validation before capture.",
            "Keep offline configuration, device transfer, arming, recording, stopping and export as separate deliberate actions.",
            "On interruption or connection loss inspect current state; do not blindly repeat transfer, arm or deletion.",
            "Export the measurement and retain the native original, hashes, timestamps/units, settings readback, target/program identity and acquisition uncertainty.",
            "Import understood UTF-8 CSV with explicit mapping or normalized trace-measurement.v1, preserving quality and lineage; the tool does not decode native Siemens binary formats.",
            "Do not delete user traces or measurements. No reproduced fault leaves the hypotheses unresolved.");
        var specification = new JsonObject { ["schemaVersion"] = "trace-plan.v1", ["diagnosticPlanId"] = planId,
            ["reviewId"] = reviewId, ["backend"] = backend, ["family"] = "ordinaryPlc", ["fulfillment"] = "partialManualHandoff",
            ["nativeExecutable"] = false, ["mutationAttempted"] = false, ["target"] = plan["selectedInstance"]!.DeepClone(),
            ["project"] = plan["project"]?.DeepClone(), ["sourceRevision"] = plan["sourceRevision"]!.DeepClone(),
            ["report"] = plan["report"]!.DeepClone(), ["hypotheses"] = plan["hypotheses"]!.DeepClone(), ["signals"] = plan["signals"]!.DeepClone(),
            ["requestedSettings"] = settings.DeepClone(), ["effectiveSettings"] = null, ["targetLimits"] = null,
            ["pendingValidation"] = pending, ["manualSteps"] = steps, ["coverageGaps"] = plan["coverageGaps"]!.DeepClone(),
            ["signalSelectionCoverage"] = plan["automaticSelection"] is JsonObject selection ? new JsonObject {
                ["gaps"] = selection["gaps"]?.DeepClone(), ["truncated"] = selection["truncated"]?.DeepClone(),
                ["complete"] = false, ["maxSignals"] = selection["maxSignals"]?.DeepClone(), ["semantics"] = selection["semantics"]?.DeepClone() } : null,
            ["ownership"] = "Specification artifact only; no TIA trace or measurement ownership acquired.",
            ["freshness"] = "Immutable saved review only; source, binding, program, target or firmware changes invalidate execution assumptions." };
        if (plan.ContainsKey("alarmBindings")) specification["alarmBindings"] = plan["alarmBindings"]!.DeepClone();
        return specification;
    }
    private static void RejectUnknown(JsonObject value, string[] fields)
    {
        if (value.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal)))
            throw new BridgeException("AcquisitionRequirementUnsatisfied", "Unknown acquisition/settings fields cannot be silently ignored. Only the documented manual specification fields are supported.");
    }
}
