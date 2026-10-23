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
    [Description("Prepare an immutable UA observation binding before polling/subscription capture. Supply experimentId and request:{attemptId,reason,mappings:[{signalId,node:{namespaceUri,identifierType,identifier},unit,reason}]}. Resolves exact saved experiment declarations and distinct explicit nodes without contacting a server. Pass the returned id as diagnosticBindingId in a new runtime_ua_capture_start or runtime_ua_subscription_start request using the exact session attempt and ordered nodes. Existing unbound captures cannot be rebound. Node datatype, online PLC address/program, physical identity, timing and behavioral assertions remain unverified.")]
    public CallToolResult DiagnosticUaBindingPlan(string experimentId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticUaBindings(options.ArtifactsDirectory).Plan(experimentId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan offline diagnosis from saved reviewId and report. Optional searchTerms/hypotheses, blockPath, instance:{dbPath,memberPath}, signals:[{id,role,reason,hypothesisIds,storagePath,memberPath}], context/observations, autoSignals:{seeds:[{scope:LocalVariable|GlobalVariable,components,reason}],maxSignals,maxDepth}. Optional testSuiteFinding:{relativePath,sha256,index} verifies a retained Test Suite receipt and explicitly selects its exact captured block; unresolved or cross-review findings refuse. Original finding/counters and unverified source provenance are retained. Optional alarmContext:{reviewId,nodeId,objectPath} retains an exact supplied alarm/supervision/hmiAlarm, captured ancestry and bounded direct evidence; it never selects executable storage. Optional alarmBindings:[{id,signalId,operand:{nodeId,objectPath},edgeIndices}] checks selected member nodes with metadata.alarmOperand profile alarm-operand.v1 against executable declarations and captured call arguments. Retains original directed paths and unresolved/type/owner gaps; this supplied profile is not a Siemens format or runtime alarm proof. Instance selection remains explicit. Ranks other candidates without choosing equipment. Exact qualified types, native root edges and bounded explicit SCL/FlgNet calls resolve captured instances. Automatic signal selection follows conservative dependencies and simple actual bindings; unsupported source/aliases remain gaps. Online addresses/observability remain unverified. Persists immutable case without native access. See docs/guides/trace-diagnostics.md.")]
    public CallToolResult DiagnosticPlan(JsonElement request) => DiagnosticResult(() => new DiagnosticPlanner(options.ArtifactsDirectory).Plan(DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_watch_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan offline watch rows from an immutable diagnostic_plan.id and its retained saved review. request:{tableName,tiaVersion,maxRows?:1..64,externalStorage?:[{objectPath,reason}]}. tiaVersion is explicit caller-supplied, unverified metadata. Exact selected instance and justified signals required; each external storage needs a separate allowance. Returns bounded summary and hashed artifact for artifact_read; preserves unresolved signals, provenance and row omissions. Selected alarm evidence and operand associations are revalidated and retained in the downloadable envelope; unresolved explicit alarmBindings refuse with AlarmBindingUnresolved. No native XML, live addresses, worker access or native apply eligibility.")]
    public CallToolResult DiagnosticWatchPlan(string planId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticWatchPlanning(options.ArtifactsDirectory).Plan(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compose a retained offline diagnostic experiment from exact diagnostic_plan planId. request:{reason,watch:{diagnostic_watch_plan request},trace:{trace_prepare request}}. Revalidate saved source, exact instance, selected alarm operands and Test Suite finding; derive justified watch rows and a manual trace specification together. Retains original inputs and explicit unresolved evidence. No native acquisition, online authority, automatic equipment selection or assertion admission.")]
    public CallToolResult DiagnosticExperimentPlan(string planId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticExperiments(options.ArtifactsDirectory).Plan(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_review", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Review imported before/after measurements for an exact retained diagnostic experiment. request:{afterExperimentId,beforeMeasurementId,afterMeasurementId,comparison:{trace_compare options}}. Revalidate original experiment/source evidence and exact trace-plan membership. The after case must be the same case or an explicit direct diagnostic_replan successor; unrelated instances refuse. Retain both analyses, raw-sample comparison, quality gaps and source/instance lineage. Changed sources and storage need explicit comparison options. No native acquisition, clock synchronization, causality or fix proof. Paired v2 inputs produce diagnostic-experiment-review.v2 with explicit clock/epoch windows; mixed versions refuse.")]
    public CallToolResult DiagnosticExperimentReview(string experimentId, JsonElement request)
        => DiagnosticResult(() => new DiagnosticExperiments(options.ArtifactsDirectory).Review(experimentId, DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_compare_runs", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare 2..8 explicit repeated offline experiment pairs. request:{reason,pairs:[{id,beforeExperimentId,afterExperimentId,beforeMeasurementId,afterMeasurementId,comparison:{trace_compare options}}]}. Requires consistent exact before/after cases, windows and signal mappings and distinct original measurement inputs across phases. Revalidates original source, direct explicit replan lineage and instance mappings. Retains poor-quality and incomplete-window evidence; no resampling, statistical inference, causality or fix proof. Paired v2 inputs produce diagnostic-experiment-repeat.v2 with every explicit clock/epoch mapping and unresolved behavioral verdicts; mixed versions refuse.")]
    public CallToolResult DiagnosticExperimentCompareRuns(JsonElement request)
        => DiagnosticResult(() => new DiagnosticRepeatedExperiments(options.ArtifactsDirectory).Compare(DiagnosticRequest(request)));

    [McpServerTool(Name = "diagnostic_experiment_report_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained diagnostic-experiment-review.v1/v2 or diagnostic-experiment-repeat.v1/v2 reportId as json, markdown or html. Rederives original evidence and window analyses before composing the exact source document, cases and full child analysis/comparison results. Readable HTML is escaped and self-contained. Returns a hashed artifact; no native acquisition, source authentication, clock synchronization, statistical inference, causality or fix proof.")]
    public CallToolResult DiagnosticExperimentReportExport(string reportId, string format)
        => DiagnosticResult(() => new DiagnosticExperimentReportExport(options.ArtifactsDirectory).Export(reportId, format));

    [McpServerTool(Name = "trace_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return offline trace route decisions, implementation and qualification separately. No target probe. TIA Openness ordinary trace creation is not established. Siemens SIMATIC AX CLI is a documented separate PLC-job route, with missing bridge/target/readback qualification. Manual specification is partial fulfillment. Long-term/project/drive/safety remain separate families. Polling cannot guarantee one-scan events.")]
    public CallToolResult TraceCapabilities() => DiagnosticResult(TracePlanning.Capabilities);

    [McpServerTool(Name = "trace_prepare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Persist a reviewable manual trace specification. planId is diagnostic_plan.id. request: backend:'manual', family:'ordinaryPlc', exact reviewId, settings:{task,sampleEvery,preSamples,postSamples,rationale,trigger:{signalId,kind:risingEdge|fallingEdge|above|below,threshold for numeric}}. Retained declarations and explicit alarmBindings are revalidated; unresolved bindings refuse with AlarmBindingUnresolved. Counts/settings are requested, not verified device limits. No native apply, transfer, arm or capture. Returns all pending native validations and preservation/recovery instructions.")]
    public CallToolResult TracePrepare(string planId, JsonElement request) => DiagnosticResult(() => new TracePlanning(options.ArtifactsDirectory).Prepare(planId, DiagnosticRequest(request)));

    [McpServerTool(Name = "trace_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Import exactly one host-owned hashed artifact {path,sha256} or committed uploadRef against exact tracePlanId (uploaded bytes are retained independently): trace-measurement.v1 or trace-measurement.v2 JSON, or v1 UTF-8 CSV with explicit csvOptions (delimiter,decimalSeparator,headerRecord,timeColumn,timeUnit,columns:[{signalId,column,unit}],mode,clock,programIdentity,quality; optional missingValues/settingsReadbackEvidence). Supports <=10000 samples; preserves negative pre-trigger times and units. Rejects decimal precision loss. No native Siemens binary decoder or automatic CSV dialect qualification. Native originals/readback evidence retained; polling cannot claim atomic/scan-synchronized quality. v2 retains explicit clock domains/epochs, per-reading status/error/timestamps, absent readings, irregular/reset times and capture completeness. v2 JSON is retained byte-exactly; no v2 CSV coercion. See docs/reference/diagnostic-measurement-quality.md. Offline only.")]
    public CallToolResult TraceRead(string tracePlanId, JsonElement? artifact = null, JsonElement? csvOptions = null, string? uploadRef = null) => DiagnosticResult(() =>
    {
        if ((artifact is null) == (uploadRef is null)) throw new BridgeException("InvalidRequest", "Supply exactly one artifact or uploadRef.");
        var measurements = new TraceMeasurements(options.ArtifactsDirectory);
        var csv = csvOptions is null ? null : DiagnosticRequest(csvOptions.Value);
        return uploadRef is null ? measurements.Import(tracePlanId, DiagnosticRequest(artifact!.Value), csv)
            : measurements.ImportUpload(tracePlanId, uploadRef, csv);
    });

    [McpServerTool(Name = "trace_analyze", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Analyze a trace_read measurementId offline against original declarative hypothesis predictions. Conditions are {signalId,op:eq|ne|lt|le|gt|ge,value}, ANDed within when/expect. Reports supported/contradicted sample predictions or unresolved, bounded transition intervals, gaps and uncertainty. No script evaluation, causal proof or claim that an unreproduced fault is fixed. Persists an exportable report with original clock, program label, mode and time unit; supplied identities and clock synchronization remain unverified. v2 counts usable observations but all behavioral verdicts remain unresolved; bad/uncertain/missing/error values never enter statistics, and changes cannot bridge gaps, resets, epochs or missing timing.")]
    public CallToolResult TraceAnalyze(string measurementId) => DiagnosticResult(() => new TraceMeasurements(options.ArtifactsDirectory).Analyze(measurementId));

    [McpServerTool(Name = "diagnostic_report_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a retained trace_analyze, trace_compare or runtime_capture_analyze reportId as json, markdown or html. JSON preserves the exact original bytes; readable exports include findings, acquisition context, limits, report hash and complete source JSON. HTML is self-contained with escaped text and no scripts/network resources. Returns hashed artifacts for artifact_read; 8 MiB per output and existing diagnostic retention bounds apply. Historical missing context stays explicit. v1 presents retained results without rerun. v2 rederives original source chains and exports a complete diagnostic-quality-report.v2 with original JSON bytes, statuses and times. No native access, source authentication, clock synchronization, causality or fix proof.")]
    public CallToolResult DiagnosticReportExport(string reportId, string format)
        => DiagnosticResult(() => new DiagnosticReportExport(options.ArtifactsDirectory).Export(reportId, format));

    [McpServerTool(Name = "trace_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two imported measurements without resampling. options requires reason, beforeAnchorSeconds, afterAnchorSeconds, windowStartSeconds, windowEndSeconds; anchors are explicit caller-selected events. Different source reviews or declared program identities require allowChangedSource:true; changed storage/instances require explicit signalMap:[{before,after}]. Types/units must match. Unknown options and malformed mappings refuse; time conversion and anchor subtraction must be exact. Retains both declared program identities, clock labels, modes and original time units beside raw-sample statistics and transition intervals. Caller-selected anchors do not synchronize clocks; never proves a fix or causality. v2 requires beforeClockId, beforeEpoch, afterClockId and afterEpoch; it selects each original clock/epoch separately. Mixed v1/v2 inputs refuse. v2 includes complete-epoch quality and original per-reading times without resampling.")]
    public CallToolResult TraceCompare(string beforeMeasurementId, string afterMeasurementId, JsonElement options)
        => DiagnosticResult(() => new TraceComparison(DiagnosticArtifactsRoot).Compare(beforeMeasurementId, afterMeasurementId, DiagnosticRequest(options)));

    [McpServerTool(Name = "diagnostic_replan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reuse a diagnostic case on a new source review or machine instance with explicit revalidation. request requires reviewId, blockPath, instance:{dbPath,memberPath}, reason, signalMappings:[{id,storagePath,memberPath,reason}] covering all prior signal IDs. Every identity/declaration is resolved anew; changed types fail. Hypotheses and the selected role template are retained with lineage; the template is resolved from the current catalog and target observability remains unverified. Optional testSuiteFinding must be supplied anew and verified for the new review; old Test Suite evidence stays only in the previous case lineage. Optional alarmContext also requires fresh explicit selection for this review; omitted alarm evidence stays only in predecessor lineage. Optional alarmBindings also requires fresh explicit selections and alarmContext; old operand associations are never inherited automatically. No blind instance-name replacement or native access.")]
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
            return Result(JsonSerializer.Serialize(new { error = new { code = (e as BridgeException)?.Code ?? "DiagnosticFailed",
                message = e.Message, details = new { mutationAttempted = false, nativeAccessAttempted = false, retrySafe = true,
                    resolution = (e as DiagnosticResolutionException)?.Details } } }, BridgeProtocol.Json), true);
        }
    }
}
