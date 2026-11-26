using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "diagnostic_ua_binding_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Prepare an immutable UA observation binding before polling/subscription capture. Supply experimentId and request:{attemptId,reason,mappings:[{signalId,node:{namespaceUri,identifierType,identifier},unit,reason}]}. No server contact. Pass the returned id as diagnosticBindingId in a new runtime_ua_capture_start or runtime_ua_subscription_start; existing unbound captures cannot be rebound. Node datatype, online address/program, identity and timing unverified.")]
    public CallToolResult DiagnosticUaBindingPlan(string experimentId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticUaBindings(options.ArtifactsDirectory).Plan(experimentId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_plan", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Plan an offline diagnosis from a saved reviewId and report, and persist it as an immutable case; no native access. Optional: searchTerms/hypotheses, blockPath, instance {dbPath, memberPath}, signals, context/observations, autoSignals, testSuiteFinding, alarmContext, alarmBindings. Instance selection stays explicit; other candidates are ranked, never chosen. Online addresses are not verified; an alarm binding is not Siemens or runtime alarm proof.")]
    public CallToolResult DiagnosticPlan(JsonElement request) => DiagnosticResult(() => new DiagnosticPlanner(options.ArtifactsDirectory).Plan(DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_watch_plan", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Plan offline watch rows from an immutable diagnostic_plan.id and its saved review. request:{tableName,tiaVersion,maxRows?:1..64,externalStorage?:[{objectPath,reason}]}; tiaVersion is caller-supplied, unverified. Needs an exact selected instance and justified signals; each external storage its own allowance. Returns a summary and hashed artifact for artifact_read. Unresolved alarmBindings refuse. No native XML, live addresses, worker access or native apply eligibility.")]
    public CallToolResult DiagnosticWatchPlan(string planId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticWatchPlanning(options.ArtifactsDirectory).Plan(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_plan", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compose a retained offline diagnostic experiment from exact diagnostic_plan planId. request:{reason,watch:{diagnostic_watch_plan request},trace:{trace_prepare request}}. Revalidate saved source, exact instance, selected alarm operands and Test Suite finding; derive justified watch rows and a manual trace specification together. Retains original inputs and explicit unresolved evidence. No native acquisition, online authority, automatic equipment selection or assertion admission.")]
    public CallToolResult DiagnosticExperimentPlan(string planId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticExperiments(options.ArtifactsDirectory).Plan(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_review", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Review imported before/after measurements for an exact retained diagnostic experiment. request:{afterExperimentId,beforeMeasurementId,afterMeasurementId,comparison:{trace_compare options}}. The after case must be the same case or a direct diagnostic_replan successor; unrelated instances refuse. Changed sources and storage need explicit comparison options. No native acquisition, clock synchronization, causality or fix proof. Mixed v1/v2 inputs refuse.")]
    public CallToolResult DiagnosticExperimentReview(string experimentId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticExperiments(options.ArtifactsDirectory).Review(experimentId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_compare_runs", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare 2..8 explicit repeated offline experiment pairs. request:{reason,pairs:[{id,beforeExperimentId,afterExperimentId,beforeMeasurementId,afterMeasurementId,comparison:{trace_compare options}}]}. Requires consistent before/after cases, windows and signal mappings and distinct inputs across phases; revalidates source and lineage. No resampling, statistical inference, causality or fix proof. Mixed v1/v2 inputs refuse.")]
    public CallToolResult DiagnosticExperimentCompareRuns(JsonElement request)
        => DiagnosticResult(() => new DiagnosticRepeatedExperiments(options.ArtifactsDirectory).Compare(DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_report_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained diagnostic-experiment-review.v1/v2 or diagnostic-experiment-repeat.v1/v2 reportId as json, markdown or html. Rederives original evidence and window analyses before composing the exact source document, cases and full child analysis/comparison results. Readable HTML is escaped and self-contained. Returns a hashed artifact; no native acquisition, source authentication, clock synchronization, statistical inference, causality or fix proof.")]
    public CallToolResult DiagnosticExperimentReportExport(string reportId, string format)
        => DiagnosticResult(() => new DiagnosticExperimentReportExport(options.ArtifactsDirectory).Export(reportId, format));

    [McpServerTool(Name = "trace_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return offline trace route decisions, implementation and qualification separately. No target probe. TIA Openness ordinary trace creation is not established. Siemens SIMATIC AX CLI is a documented separate PLC-job route, with missing bridge/target/readback qualification. Manual specification is partial fulfillment. Long-term/project/drive/safety remain separate families. Polling cannot guarantee one-scan events.")]
    public CallToolResult TraceCapabilities() => DiagnosticResult(TracePlanning.Capabilities);

    [McpServerTool(Name = "trace_prepare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Persist a reviewable manual trace specification. planId is diagnostic_plan.id. request: backend:'manual', family:'ordinaryPlc', exact reviewId, settings:{task,sampleEvery,preSamples,postSamples,rationale,trigger:{signalId,kind:risingEdge|fallingEdge|above|below,threshold for numeric}}. Retained declarations and explicit alarmBindings are revalidated; unresolved bindings refuse with AlarmBindingUnresolved. Counts/settings are requested, not verified device limits. No native apply, transfer, arm or capture. Returns all pending native validations and preservation/recovery instructions.")]
    public CallToolResult TracePrepare(string planId, JsonElement request) => DiagnosticResult(() => new TracePlanning(options.ArtifactsDirectory).Prepare(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "trace_read", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Import one host-owned hashed artifact {path,sha256} or committed uploadRef against exact tracePlanId: trace-measurement.v1/v2 JSON, or v1 UTF-8 CSV with explicit csvOptions (delimiter, decimalSeparator, headerRecord, timeColumn, timeUnit, columns:[{signalId,column,unit}], mode, clock, programIdentity, quality). Max 10000 samples; rejects precision loss. No native Siemens binary decoder, CSV dialect detection or v2 CSV. Offline only.")]
    public CallToolResult TraceRead(string tracePlanId, JsonElement? artifact = null, JsonElement? csvOptions = null, string? uploadRef = null) => DiagnosticResult(() =>
    {
        if ((artifact is null) == (uploadRef is null)) throw new BridgeException("InvalidRequest", "Supply exactly one artifact or uploadRef.");
        var measurements = new TraceMeasurements(options.ArtifactsDirectory);
        var csv = csvOptions is null ? null : DiagnosticRequest(csvOptions.Value);
        return uploadRef is null ? measurements.Import(tracePlanId, DiagnosticRequest(artifact!.Value), csv)
            : measurements.ImportUpload(tracePlanId, uploadRef, csv);
    });

    [McpServerTool(Name = "trace_analyze", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Analyze a trace_read measurementId offline against declarative hypothesis predictions. Conditions are {signalId,op:eq|ne|lt|le|gt|ge,value}, ANDed within when/expect. Reports supported/contradicted or unresolved predictions, transitions, gaps and uncertainty; persists an exportable report. No script evaluation, causal proof or fix claim; identities and clock sync unverified. v2 leaves behavioral verdicts unresolved; bad/missing values never enter statistics.")]
    public CallToolResult TraceAnalyze(string measurementId) => DiagnosticResult(() => new TraceMeasurements(options.ArtifactsDirectory).Analyze(measurementId));

    [McpServerTool(Name = "diagnostic_report_export", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export a retained trace_analyze, trace_compare or runtime_capture_analyze reportId as json, markdown or html. JSON keeps the exact original bytes; readable exports add findings, limits, report hash and source JSON; HTML is self-contained, no scripts or network resources. Returns hashed artifacts for artifact_read, 8 MiB each. v1 shows retained results without rerun; v2 rederives the original source chains into a complete diagnostic-quality-report.v2. No native access, source authentication, clock synchronization, causality or fix proof.")]
    public CallToolResult DiagnosticReportExport(string reportId, string format)
        => DiagnosticResult(() => new DiagnosticReportExport(options.ArtifactsDirectory).Export(reportId, format));

    [McpServerTool(Name = "trace_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two imported measurements without resampling. options requires reason, beforeAnchorSeconds, afterAnchorSeconds, windowStartSeconds, windowEndSeconds. Changed source reviews or program identities need allowChangedSource:true; changed storage/instances need signalMap:[{before,after}]; types/units must match. v2 also requires beforeClockId, beforeEpoch, afterClockId, afterEpoch; mixed v1/v2 refuse. Anchors do not synchronize clocks; never proves a fix or causality.")]
    public CallToolResult TraceCompare(string beforeMeasurementId, string afterMeasurementId, JsonElement options)
        => DiagnosticResult(() => new TraceComparison(DiagnosticArtifactsRoot).Compare(beforeMeasurementId, afterMeasurementId, DiagnosticRequest(options)));

    [McpServerTool(Name = "diagnostic_replan", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Reuse a diagnostic case on a new source review or machine instance with explicit revalidation. request requires reviewId, blockPath, instance:{dbPath,memberPath}, reason and signalMappings:[{id,storagePath,memberPath,reason}] for all prior signal IDs. Identities resolve anew; changed types fail. testSuiteFinding, alarmContext and alarmBindings must be supplied anew, never inherited. No blind instance-name replacement or native access.")]
    public CallToolResult DiagnosticReplan(string planId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticReplanner(options.ArtifactsDirectory).Replan(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "trace_check_readback", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare a hashed host artifact containing trace-readback.v1 against exact tracePlanId: target/project/program identity, signal components/types, settings, observability and supplied limits. Requires retained readback evidence artifacts. This is an offline comparison of caller-supplied observations, not a native probe or authorization to transfer/arm. Reports all differences and never upgrades native qualification.")]
    public CallToolResult TraceCheckReadback(string tracePlanId, JsonElement artifact)
        => DiagnosticResult(() => new TraceReadback(options.ArtifactsDirectory).Check(tracePlanId, DiagnosticRequest(artifact)));

    private string DiagnosticArtifactsRoot => options.ArtifactsDirectory;
    private static JsonObject DiagnosticRequest(JsonElement input) => JsonNode.Parse(input.GetRawText()) as JsonObject
        ?? throw new BridgeException("InvalidRequest", "Expected a JSON object.");
    private static CallToolResult DiagnosticResult(Func<JsonObject> action)
    {
        try { return Result(action().ToJsonString(), false); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            return Result(JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = (e as BridgeException)?.Code ?? "DiagnosticFailed",
                    message = e.Message,
                    details = new
                    {
                        mutationAttempted = false,
                        nativeAccessAttempted = false,
                        retrySafe = true,
                        resolution = (e as DiagnosticResolutionException)?.Details
                    }
                }
            }, BridgeProtocol.Json), true);
        }
    }
}
