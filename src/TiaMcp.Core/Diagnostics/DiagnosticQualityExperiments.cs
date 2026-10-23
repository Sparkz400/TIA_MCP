using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

internal sealed class DiagnosticQualityExperiments(string artifactsRoot)
{
    internal DiagnosticExperimentReviewParts BuildReview(string experimentId, JsonObject request)
    {
        D.Bound(request); TraceQualityMeasurements.Fields(request, "afterExperimentId", "beforeMeasurementId", "afterMeasurementId", "comparison");
        var experiments = new DiagnosticExperiments(artifactsRoot); var reader = new TraceQualityMeasurements(artifactsRoot);
        var before = experiments.ReadExperiment(experimentId); string afterId = D.Required(request, "afterExperimentId", 64);
        var after = experiments.ReadExperiment(afterId); var lineage = DiagnosticExperiments.ReplanLineage(before, after);
        string beforeId = D.Required(request, "beforeMeasurementId", 64), afterMeasurementId = D.Required(request, "afterMeasurementId", 64);
        var a = reader.Read(beforeId); var b = reader.Read(afterMeasurementId);
        if (D.Text(a, "tracePlanId") != D.Text(before, "tracePlanId") || D.Text(b, "tracePlanId") != D.Text(after, "tracePlanId"))
            throw new BridgeException("MeasurementLineageMismatch", "Each v2 measurement must belong to its exact experiment trace plan.");
        var ar = reader.BuildAnalysis(beforeId); var br = reader.BuildAnalysis(afterMeasurementId);
        var comparison = new TraceQualityComparison(artifactsRoot).Build(beforeId, afterMeasurementId, TraceQualityMeasurements.Object(request, "comparison"));
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-experiment-review.v2", ["beforeExperimentId"] = experimentId,
            ["afterExperimentId"] = afterId, ["beforeMeasurementId"] = beforeId, ["afterMeasurementId"] = afterMeasurementId,
            ["request"] = request.DeepClone(), ["lineage"] = lineage, ["beforeReportId"] = TraceQualityMeasurements.Id(ar),
            ["afterReportId"] = TraceQualityMeasurements.Id(br), ["comparisonId"] = TraceQualityMeasurements.Id(comparison),
            ["beforeHypotheses"] = ar["hypotheses"]!.DeepClone(), ["afterHypotheses"] = br["hypotheses"]!.DeepClone(),
            ["beforeWindow"] = comparison["beforeWindow"]!.DeepClone(), ["afterWindow"] = comparison["afterWindow"]!.DeepClone(),
            ["beforeQualityAssessment"] = ar["qualityAssessment"]!.DeepClone(), ["afterQualityAssessment"] = br["qualityAssessment"]!.DeepClone(),
            ["beforeAcquisition"] = comparison["beforeAcquisition"]!.DeepClone(), ["afterAcquisition"] = comparison["afterAcquisition"]!.DeepClone(),
            ["sourceChanged"] = comparison["sourceChanged"]!.DeepClone(), ["programIdentityChanged"] = comparison["programIdentityChanged"]!.DeepClone() };
        TraceQualityMeasurements.Authority(report); D.Bound(report, DiagnosticStore.MaxBytes);
        return new(report, ar, br, comparison, before, after, a, b);
    }
    internal DiagnosticExperimentReviewParts ReadReview(string id)
    {
        var store = new DiagnosticStore(artifactsRoot); var saved = store.Read(id, "diagnostic-experiment-review.v2");
        var parts = BuildReview(D.Required(saved, "beforeExperimentId", 64), TraceQualityMeasurements.Object(saved, "request"));
        TraceQualityMeasurements.Same(saved, parts.Report, "v2 experiment review");
        foreach (var (field, expected) in new[] { ("beforeReportId", parts.BeforeReport), ("afterReportId", parts.AfterReport), ("comparisonId", parts.Comparison) })
            TraceQualityMeasurements.Same(store.Read(D.Required(saved, field, 64)), expected, field);
        return parts;
    }
    internal DiagnosticRepeatedExperimentParts BuildRepeated(JsonObject request)
    {
        D.Bound(request); TraceQualityMeasurements.Fields(request, "reason", "pairs"); D.Required(request, "reason", 2000);
        var pairs = TraceQualityMeasurements.Array(request, "pairs", 8);
        if (pairs.Count < 2) throw new BridgeException("InvalidRequest", "Repeated comparison needs 2..8 pairs.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var originals = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<DiagnosticExperimentReviewParts>(); var runs = new JsonArray();
        foreach (var item in pairs)
        {
            var pair = item as JsonObject ?? throw new BridgeException("InvalidRequest", "Pair must be an object.");
            TraceQualityMeasurements.Fields(pair, "id", "beforeExperimentId", "afterExperimentId", "beforeMeasurementId", "afterMeasurementId", "comparison");
            string id = D.Required(pair, "id", 64); if (!ids.Add(id)) throw new BridgeException("InvalidRequest", "Pair IDs must be distinct.");
            var reviewRequest = pair.DeepClone().AsObject(); reviewRequest.Remove("id"); reviewRequest.Remove("beforeExperimentId");
            var part = BuildReview(D.Required(pair, "beforeExperimentId", 64), reviewRequest);
            foreach (var imported in new[] { part.BeforeImport, part.AfterImport })
            {
                var m = TraceQualityMeasurements.Object(imported, "measurement");
                string hash = D.Text(m, "mode") == "manualNativeExport" ? D.Required(m["originalArtifact"]!, "sha256", 64)
                    : D.Required(imported["inputArtifact"]!, "sha256", 64);
                if (!originals.Add(hash)) throw new BridgeException("ExperimentRepeatedInput", "Original bytes cannot count twice as independent runs.");
            }
            if (parts.Count > 0) DiagnosticRepeatedExperiments.CheckComparable(parts[0], part);
            parts.Add(part); runs.Add(new JsonObject { ["id"] = id, ["reviewId"] = TraceQualityMeasurements.Id(part.Report),
                ["beforeWindow"] = part.Comparison["beforeWindow"]!.DeepClone(), ["afterWindow"] = part.Comparison["afterWindow"]!.DeepClone() });
        }
        var hypotheses = new JsonArray();
        foreach (var h in D.Rows(parts[0].BeforeExperiment["diagnosticCase"]?["hypotheses"]))
        {
            JsonObject Counts(string side) => new() { ["runs"] = parts.Count, ["supportedPredictionRuns"] = 0, ["contradictedPredictionRuns"] = 0,
                ["unresolvedRuns"] = parts.Count, ["conditionObservedRuns"] = runs.Count(r => D.Rows(r![side + "Window"]!["hypotheses"])
                    .Any(f => D.Text(f, "id") == D.Text(h, "id") && f!["matchedSamples"]!.GetValue<int>() + f["contradictedSamples"]!.GetValue<int>() > 0)) };
            hypotheses.Add(new JsonObject { ["id"] = h!["id"]!.DeepClone(), ["statement"] = h["statement"]!.DeepClone(),
                ["before"] = Counts("before"), ["after"] = Counts("after"), ["causationProven"] = false, ["bugFixProven"] = false });
        }
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-experiment-repeat.v2", ["request"] = request.DeepClone(),
            ["runPairCount"] = pairs.Count, ["runs"] = runs, ["hypotheses"] = hypotheses,
            ["beforeDiagnosticPlanId"] = parts[0].BeforeExperiment["diagnosticPlanId"]!.DeepClone(),
            ["afterDiagnosticPlanId"] = parts[0].AfterExperiment["diagnosticPlanId"]!.DeepClone(),
            ["requestedWindowsEqual"] = true, ["signalMappingsEqual"] = true, ["independentRunsVerified"] = false,
            ["aggregation"] = "descriptiveRunCountsOnly",
            ["clockEpochMappings"] = new JsonArray(parts.Select(p => (JsonNode)p.Comparison["alignment"]!.DeepClone()).ToArray()) };
        TraceQualityMeasurements.Authority(report); D.Bound(report, DiagnosticStore.MaxBytes); return new(report, parts.ToArray());
    }
    internal DiagnosticRepeatedExperimentParts ReadRepeated(string id)
    {
        var saved = new DiagnosticStore(artifactsRoot).Read(id, "diagnostic-experiment-repeat.v2");
        var parts = BuildRepeated(TraceQualityMeasurements.Object(saved, "request"));
        TraceQualityMeasurements.Same(saved, parts.Report, "v2 repeated report");
        foreach (var p in parts.Pairs) _ = ReadReview(TraceQualityMeasurements.Id(p.Report));
        return parts;
    }
}
