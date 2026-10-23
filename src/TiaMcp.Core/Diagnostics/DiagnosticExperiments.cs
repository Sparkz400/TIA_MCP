using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Composes existing offline evidence; never acquires, stimulates or authenticates a target.</summary>
public sealed class DiagnosticExperiments(string artifactsRoot)
{
    private const string Limits = "Supplied saved source and observations only. Sample predictions do not prove cause or a fix. Program identity, native signal visibility, acquisition settings and clock synchronization remain unverified. Watch and trace artifacts require separate target qualification.";

    public JsonObject Plan(string planId, JsonObject request)
    {
        var (experiment, watch, trace) = Build(planId, request);
        // Derive and validate all requested work before publication. Capacity/I/O failures can
        // still leave valid content-addressed children; no partial experiment is returned.
        var store = new DiagnosticStore(artifactsRoot);
        store.Save(watch); store.Save(trace);
        var receipt = store.Save(experiment); receipt["experiment"] = experiment; return receipt;
    }

    public JsonObject Review(string experimentId, JsonObject request)
    {
        var parts = BuildReview(experimentId, request);
        var store = new DiagnosticStore(artifactsRoot);
        store.Save(parts.BeforeReport); store.Save(parts.AfterReport); store.Save(parts.Comparison);
        var receipt = store.Save(parts.Report); receipt["review"] = parts.Report; return receipt;
    }

    internal DiagnosticExperimentReviewParts BuildReview(string experimentId, JsonObject request)
    {
        D.Bound(request);
        Fields(request, "afterExperimentId", "beforeMeasurementId", "afterMeasurementId", "comparison");
        var versions = new DiagnosticStore(artifactsRoot);
        bool aV2 = TraceQualityMeasurements.IsV2(versions.Read(D.Required(request, "beforeMeasurementId", 64))),
            bV2 = TraceQualityMeasurements.IsV2(versions.Read(D.Required(request, "afterMeasurementId", 64)));
        if (aV2 != bV2) throw new BridgeException("MeasurementVersionMismatch", "Mixed v1/v2 experiment inputs are refused.");
        if (aV2) return new DiagnosticQualityExperiments(artifactsRoot).BuildReview(experimentId, request);
        string afterId = D.Required(request, "afterExperimentId", 64);
        var before = ReadExperiment(experimentId); var after = ReadExperiment(afterId);
        var lineage = ReplanLineage(before, after);
        string beforeMeasurement = D.Required(request, "beforeMeasurementId", 64), afterMeasurement = D.Required(request, "afterMeasurementId", 64);
        var beforeImport = RevalidateMeasurement(beforeMeasurement, D.Required(before, "tracePlanId", 64));
        var afterImport = RevalidateMeasurement(afterMeasurement, D.Required(after, "tracePlanId", 64));
        var measurements = new TraceMeasurements(artifactsRoot);
        var beforeReport = measurements.BuildAnalysis(beforeMeasurement); var afterReport = measurements.BuildAnalysis(afterMeasurement);
        var comparison = new TraceComparison(artifactsRoot).Build(beforeMeasurement, afterMeasurement, Object(request, "comparison"));
        var report = new JsonObject {
            ["schemaVersion"] = "diagnostic-experiment-review.v1", ["beforeExperimentId"] = experimentId, ["afterExperimentId"] = afterId,
            ["beforeMeasurementId"] = beforeMeasurement, ["afterMeasurementId"] = afterMeasurement,
            ["request"] = request.DeepClone(), ["lineage"] = lineage,
            ["beforeReportId"] = Id(beforeReport), ["afterReportId"] = Id(afterReport), ["comparisonId"] = Id(comparison),
            ["beforeHypotheses"] = beforeReport["hypotheses"]!.DeepClone(), ["afterHypotheses"] = afterReport["hypotheses"]!.DeepClone(),
            ["beforeQualityAssessment"] = beforeReport["qualityAssessment"]!.DeepClone(), ["afterQualityAssessment"] = afterReport["qualityAssessment"]!.DeepClone(),
            ["beforeAcquisition"] = comparison["beforeAcquisition"]!.DeepClone(), ["afterAcquisition"] = comparison["afterAcquisition"]!.DeepClone(),
            ["sourceChanged"] = comparison["sourceChanged"]!.DeepClone(), ["programIdentityChanged"] = comparison["programIdentityChanged"]!.DeepClone(),
            ["sourceAuthenticityVerified"] = false, ["clockSynchronizationVerified"] = false, ["causationProven"] = false,
            ["bugFixProven"] = false, ["nativeQualification"] = false, ["nativeAccessAttempted"] = false, ["limits"] = Limits };
        foreach (var document in new[] { beforeReport, afterReport, comparison, report }) D.Bound(document, DiagnosticStore.MaxBytes);
        return new(report, beforeReport, afterReport, comparison, before, after, beforeImport, afterImport);
    }

    internal DiagnosticExperimentReviewParts ReadReview(string reportId)
    {
        var store = new DiagnosticStore(artifactsRoot);
        if (TraceQualityMeasurements.Schema(store.Read(reportId)) == "diagnostic-experiment-review.v2")
            return new DiagnosticQualityExperiments(artifactsRoot).ReadReview(reportId);
        var retained = store.Read(reportId, "diagnostic-experiment-review.v1");
        var parts = BuildReview(D.Required(retained, "beforeExperimentId", 64), Object(retained, "request"));
        Same(retained, parts.Report, "experiment review");
        Same(store.Read(D.Required(retained, "beforeReportId", 64), "diagnostic-report.v1"), parts.BeforeReport, "before analysis");
        Same(store.Read(D.Required(retained, "afterReportId", 64), "diagnostic-report.v1"), parts.AfterReport, "after analysis");
        Same(store.Read(D.Required(retained, "comparisonId", 64), "trace-comparison.v1"), parts.Comparison, "comparison");
        return parts;
    }

    private (JsonObject Experiment, JsonObject Watch, JsonObject Trace) Build(string planId, JsonObject request)
    {
        D.Bound(request); Fields(request, "reason", "watch", "trace"); D.Required(request, "reason", 2000);
        var store = new DiagnosticStore(artifactsRoot); var plan = store.Read(planId, "diagnostic-case.v1");
        string reviewId = D.Required(plan, "reviewId", 64);
        var reviews = new ProjectReviewStore(artifactsRoot);
        DiagnosticPlanner.Revalidate(plan, reviews.Read(reviewId));
        if (plan.ContainsKey("testSuiteEvidence"))
        {
            var supplied = Object(plan, "testSuiteEvidence"); var reference = Object(supplied, "receipt");
            if (supplied["findingIndex"] is not JsonValue index || !index.TryGetValue<int>(out int findingIndex))
                throw new BridgeException("CaptureMismatch", "Retained Test Suite finding lacks its exact index.");
            var derived = reviews.ReadTestSuiteFinding(D.Required(reference, "relativePath"), D.Required(reference, "sha256", 64), findingIndex);
            Same(supplied, derived, "Test Suite finding");
            if (D.Text(derived, "reviewId") != reviewId || D.Text(derived["finding"], "binding") != "exactCapturedSource"
                || D.Text(derived["finding"]?["finding"]?["resolvedObject"]?["object"], "objectPath") != D.Text(plan, "blockPath"))
                throw new BridgeException("CaptureMismatch", "Test Suite finding does not bind to the experiment's exact review and block.");
        }
        var watch = new DiagnosticWatchPlanning(artifactsRoot).Build(planId, Object(request, "watch"));
        var trace = new TracePlanning(artifactsRoot).Build(planId, Object(request, "trace"));
        var tests = new JsonArray();
        foreach (var hypothesis in D.Rows(plan["hypotheses"]).OfType<JsonObject>())
        {
            string id = D.Required(hypothesis, "id", 64);
            tests.Add(new JsonObject { ["hypothesisId"] = id, ["statement"] = hypothesis["statement"]!.DeepClone(),
                ["prediction"] = hypothesis["prediction"]?.DeepClone(),
                ["assessment"] = hypothesis["prediction"] is null ? "unresolvedWithoutPrediction" : "samplePredictionOnly",
                ["signals"] = new JsonArray(D.Rows(plan["signals"]).Where(s => D.Rows(s?["hypothesisIds"]).Any(h => h?.GetValue<string>() == id))
                    .Select(s => (JsonNode?)new JsonObject { ["id"] = s!["id"]!.DeepClone(), ["role"] = s["role"]!.DeepClone(), ["reason"] = s["reason"]!.DeepClone() }).ToArray()) });
        }
        var gaps = new JsonArray();
        if (!plan.ContainsKey("alarmEvidence")) gaps.Add("No alarm context was selected; no alarm association is inferred.");
        if (plan.ContainsKey("alarmEvidence") && !plan.ContainsKey("alarmBindings")) gaps.Add("Selected alarm context has no explicit executable operand bindings.");
        if (!plan.ContainsKey("testSuiteEvidence")) gaps.Add("No retained Test Suite finding was selected; no test execution is inferred.");
        foreach (var test in tests.Where(t => D.Text(t, "assessment") == "unresolvedWithoutPrediction"))
            gaps.Add("Hypothesis " + D.Text(test, "hypothesisId") + " has no declarative sample prediction.");
        var experiment = new JsonObject { ["schemaVersion"] = "diagnostic-experiment.v1", ["diagnosticPlanId"] = planId,
            ["reviewId"] = reviewId, ["request"] = request.DeepClone(), ["diagnosticCase"] = plan.DeepClone(),
            ["watchPlanId"] = Id(watch), ["tracePlanId"] = Id(trace), ["tests"] = tests, ["compositionGaps"] = gaps,
            ["sourceAuthenticityVerified"] = false, ["nativeExecutable"] = false, ["nativeAccessAttempted"] = false,
            ["qualification"] = "offlineExperimentOnly", ["limits"] = Limits };
        // Exercise serialization limits before publishing any child.
        foreach (var document in new[] { watch, trace, experiment }) D.Bound(document, DiagnosticStore.MaxBytes);
        return (experiment, watch, trace);
    }

    internal JsonObject ReadExperiment(string id)
    {
        var store = new DiagnosticStore(artifactsRoot); var retained = store.Read(id, "diagnostic-experiment.v1");
        var (derived, watch, trace) = Build(D.Required(retained, "diagnosticPlanId", 64), Object(retained, "request"));
        Same(retained, derived, "experiment");
        Same(store.Read(D.Required(retained, "watchPlanId", 64), "diagnostic-watch-plan.v1"), watch, "watch plan");
        Same(store.Read(D.Required(retained, "tracePlanId", 64), "trace-plan.v1"), trace, "trace plan");
        return retained;
    }

    private JsonObject RevalidateMeasurement(string id, string traceId)
    {
        var retained = new DiagnosticStore(artifactsRoot).Read(id, "trace-import.v1");
        if (D.Text(retained, "tracePlanId") != traceId)
            throw new BridgeException("MeasurementLineageMismatch", "Each measurement must belong to its exact experiment trace plan; same signal names are not instance identity.");
        var measurement = Object(retained, "measurement");
        var derived = new TraceMeasurements(artifactsRoot).Normalize(traceId, Object(retained, "inputArtifact"), measurement["csvMapping"] as JsonObject);
        // Prior imports may contain historical quality counts. Reassess without modifying them.
        var comparable = retained.DeepClone().AsObject(); comparable["qualityAssessment"] = derived["qualityAssessment"]!.DeepClone();
        Same(comparable, derived, "measurement normalization from original bytes");
        return derived;
    }

    internal static JsonObject ReplanLineage(JsonObject before, JsonObject after)
    {
        string previous = D.Required(before, "diagnosticPlanId", 64), current = D.Required(after, "diagnosticPlanId", 64);
        if (previous == current) return new JsonObject { ["kind"] = "sameDiagnosticCase", ["previousPlanId"] = previous, ["currentPlanId"] = current };
        var old = Object(before, "diagnosticCase"); var next = Object(after, "diagnosticCase");
        if (next["lineage"] is not JsonObject lineage || D.Text(lineage, "previousPlanId") != previous)
            throw new BridgeException("ExperimentLineageMismatch", "Different cases require a direct explicit diagnostic_replan lineage from the before case.");
        D.Required(lineage, "reason", 2000);
        if (!JsonNode.DeepEquals(old["hypotheses"], next["hypotheses"]) || !JsonNode.DeepEquals(old["template"], next["template"]))
            throw new BridgeException("ExperimentLineageMismatch", "Replan must preserve the hypotheses and selected role template.");
        var oldSignals = D.Rows(old["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var nextSignals = D.Rows(next["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var mappings = lineage["mapping"] as JsonArray;
        if (mappings is null || mappings.Count != oldSignals.Count || nextSignals.Count != oldSignals.Count
            || mappings.Select(m => D.Text(m, "id")).Distinct(StringComparer.Ordinal).Count() != oldSignals.Count)
            throw new BridgeException("ExperimentLineageMismatch", "Replan must explicitly map every previous signal exactly once.");
        foreach (var mapping in mappings)
        {
            string id = D.Text(mapping, "id");
            if (mapping is not JsonObject row || !oldSignals.TryGetValue(id, out var a) || !nextSignals.TryGetValue(id, out var b)
                || D.Text(row, "reason") != D.Text(b, "reason") || string.IsNullOrWhiteSpace(D.Text(row, "reason"))
                || D.Text(row, "storagePath") != D.Text(b, "storagePath") || !JsonNode.DeepEquals(row["memberPath"], b["memberPath"])
                || D.Text(a, "datatype") != D.Text(b, "datatype") || D.Text(a, "role") != D.Text(b, "role")
                || !JsonNode.DeepEquals(a["hypothesisIds"], b["hypothesisIds"]))
                throw new BridgeException("ExperimentLineageMismatch", "Retained replan mapping differs from exact resolved signal identities, types or justifications.");
        }
        return new JsonObject { ["kind"] = "explicitDirectReplan", ["previousPlanId"] = previous, ["currentPlanId"] = current, ["replan"] = lineage.DeepClone() };
    }

    private static string Id(JsonObject document) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, BridgeProtocol.Json)));
    private static JsonObject Object(JsonObject parent, string field) => parent[field] as JsonObject
        ?? throw new BridgeException("InvalidRequest", field + " must be an explicit object.");
    private static void Fields(JsonObject value, params string[] fields)
    {
        if (value.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal))) throw new BridgeException("InvalidRequest", "Unknown experiment request field.");
    }
    private static void Same(JsonNode? retained, JsonNode? derived, string label)
    {
        if (!JsonNode.DeepEquals(retained, derived)) throw new BridgeException("CaptureMismatch", "Retained " + label + " differs from its original source evidence.");
    }
}

internal sealed record DiagnosticExperimentReviewParts(JsonObject Report, JsonObject BeforeReport, JsonObject AfterReport,
    JsonObject Comparison, JsonObject BeforeExperiment, JsonObject AfterExperiment, JsonObject BeforeImport, JsonObject AfterImport);
