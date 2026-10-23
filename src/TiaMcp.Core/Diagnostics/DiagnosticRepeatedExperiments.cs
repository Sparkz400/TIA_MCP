using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Explicit repeated pairs, descriptive run counts and conservative requested-window verdicts.</summary>
public sealed class DiagnosticRepeatedExperiments(string artifactsRoot)
{
    internal const string Limits = "Descriptive recorded-run counts only; no statistical significance, causal effect or successful fix is established. Distinct original input hashes do not prove independent experiments. Quality is caller-attested and structurally checked, not authenticated. Requested windows are aligned by caller-selected anchors, never synchronized clocks. Missing events, incomplete windows and poor-quality samples remain unresolved.";

    public JsonObject Compare(JsonObject request)
    {
        var parts = Build(request);
        var store = new DiagnosticStore(artifactsRoot);
        foreach (var pair in parts.Pairs)
        {
            store.Save(pair.BeforeReport); store.Save(pair.AfterReport); store.Save(pair.Comparison); store.Save(pair.Report);
        }
        var receipt = store.Save(parts.Report); receipt["report"] = parts.Report; return receipt;
    }

    internal DiagnosticRepeatedExperimentParts Build(JsonObject request)
    {
        D.Bound(request);
        Fields(request, "reason", "pairs"); D.Required(request, "reason", 2000);
        if (request["pairs"] is not JsonArray pairs || pairs.Count is < 2 or > 8)
            throw new BridgeException("InvalidRequest", "Repeated comparison requires 2..8 explicitly paired runs.");
        var store = new DiagnosticStore(artifactsRoot);
        var versions = pairs.SelectMany(p => new[] { D.Required(p!, "beforeMeasurementId", 64), D.Required(p!, "afterMeasurementId", 64) })
            .Select(id => TraceQualityMeasurements.IsV2(store.Read(id))).Distinct().ToArray();
        if (versions.Length != 1) throw new BridgeException("MeasurementVersionMismatch", "Mixed v1/v2 repeated inputs are refused.");
        if (versions[0]) return new DiagnosticQualityExperiments(artifactsRoot).BuildRepeated(request);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var originalInputs = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<DiagnosticExperimentReviewParts>(); var runs = new JsonArray();
        foreach (var item in pairs)
        {
            if (item is not JsonObject pair) throw new BridgeException("InvalidRequest", "Each repeated pair must be an object.");
            Fields(pair, "id", "beforeExperimentId", "afterExperimentId", "beforeMeasurementId", "afterMeasurementId", "comparison");
            string id = D.Required(pair, "id", 64);
            if (!ids.Add(id)) throw new BridgeException("InvalidRequest", "Repeated pair IDs must be unique.");
            var reviewRequest = pair.DeepClone().AsObject(); reviewRequest.Remove("id"); reviewRequest.Remove("beforeExperimentId");
            var derived = new DiagnosticExperiments(artifactsRoot).BuildReview(D.Required(pair, "beforeExperimentId", 64), reviewRequest);
            foreach (var import in new[] { derived.BeforeImport, derived.AfterImport })
            {
                var measurement = import["measurement"]!;
                string hash = D.Text(measurement, "mode") == "manualNativeExport"
                    ? D.Required(measurement["originalArtifact"]!, "sha256", 64) : D.Required(import["inputArtifact"]!, "sha256", 64);
                if (!originalInputs.Add(hash))
                    throw new BridgeException("ExperimentRepeatedInput", "An original measurement input cannot count as multiple runs or both phases. Reusing its wrapper ID or changing normalization does not create an independent run.");
            }
            if (parts.Count > 0) CheckComparable(parts[0], derived);
            parts.Add(derived);
            runs.Add(new JsonObject { ["id"] = id, ["reviewId"] = Id(derived.Report),
                ["beforeWindow"] = DiagnosticExperimentWindows.Assess(derived.BeforeExperiment, derived.BeforeImport, derived.Comparison, "before"),
                ["afterWindow"] = DiagnosticExperimentWindows.Assess(derived.AfterExperiment, derived.AfterImport, derived.Comparison, "after") });
        }
        var aggregates = new JsonArray();
        foreach (var hypothesis in D.Rows(parts[0].BeforeExperiment["diagnosticCase"]?["hypotheses"]))
        {
            string id = D.Required(hypothesis!, "id", 64);
            JsonObject Counts(string side)
            {
                var results = runs.Select(run => run![side + "Window"]!["hypotheses"]!.AsArray()
                    .Single(h => D.Text(h, "id") == id)!).ToArray();
                return new JsonObject { ["runs"] = results.Length,
                    ["supportedPredictionRuns"] = results.Count(r => D.Text(r, "verdict") == "supportedPrediction"),
                    ["contradictedPredictionRuns"] = results.Count(r => D.Text(r, "verdict") == "contradictedPrediction"),
                    ["unresolvedRuns"] = results.Count(r => D.Text(r, "verdict") == "unresolved"),
                    ["conditionObservedRuns"] = results.Count(r => r["matchedSamples"]!.GetValue<int>() + r["contradictedSamples"]!.GetValue<int>() > 0) };
            }
            aggregates.Add(new JsonObject { ["id"] = id, ["statement"] = hypothesis!["statement"]!.DeepClone(),
                ["before"] = Counts("before"), ["after"] = Counts("after"), ["causationProven"] = false, ["bugFixProven"] = false });
        }
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-experiment-repeat.v1", ["request"] = request.DeepClone(),
            ["runPairCount"] = pairs.Count, ["runs"] = runs, ["hypotheses"] = aggregates,
            ["beforeDiagnosticPlanId"] = parts[0].BeforeExperiment["diagnosticPlanId"]!.DeepClone(),
            ["afterDiagnosticPlanId"] = parts[0].AfterExperiment["diagnosticPlanId"]!.DeepClone(),
            ["requestedWindowsEqual"] = true, ["signalMappingsEqual"] = true,
            ["settingsEqualAcrossRuns"] = parts.All(p => JsonNode.DeepEquals(p.BeforeExperiment["request"]?["trace"]?["settings"], parts[0].BeforeExperiment["request"]?["trace"]?["settings"])
                && JsonNode.DeepEquals(p.AfterExperiment["request"]?["trace"]?["settings"], parts[0].AfterExperiment["request"]?["trace"]?["settings"])),
            ["aggregation"] = "descriptiveRunCountsOnly", ["independentRunsVerified"] = false,
            ["sourceAuthenticityVerified"] = false, ["clockSynchronizationVerified"] = false, ["causationProven"] = false,
            ["bugFixProven"] = false, ["nativeQualification"] = false, ["nativeAccessAttempted"] = false, ["limits"] = Limits };
        D.Bound(report, DiagnosticStore.MaxBytes);
        return new(report, parts.ToArray());
    }

    internal DiagnosticRepeatedExperimentParts Read(string reportId)
    {
        var store = new DiagnosticStore(artifactsRoot);
        if (TraceQualityMeasurements.Schema(store.Read(reportId)) == "diagnostic-experiment-repeat.v2")
            return new DiagnosticQualityExperiments(artifactsRoot).ReadRepeated(reportId);
        var retained = store.Read(reportId, "diagnostic-experiment-repeat.v1");
        if (retained["request"] is not JsonObject request) throw new BridgeException("CaptureMismatch", "Repeated report has no retained request.");
        var parts = Build(request);
        if (!JsonNode.DeepEquals(retained, parts.Report)) throw new BridgeException("CaptureMismatch", "Repeated report differs from its original experiments and measurements.");
        foreach (var pair in parts.Pairs) _ = new DiagnosticExperiments(artifactsRoot).ReadReview(Id(pair.Report));
        return parts;
    }

    internal static void CheckComparable(DiagnosticExperimentReviewParts first, DiagnosticExperimentReviewParts next)
    {
        foreach (var side in new[] { (first.BeforeExperiment, next.BeforeExperiment), (first.AfterExperiment, next.AfterExperiment) })
            if (D.Text(side.Item1, "diagnosticPlanId") != D.Text(side.Item2, "diagnosticPlanId"))
                throw new BridgeException("ExperimentLineageMismatch", "Repeated pairs must use the same exact before case and same exact after case. Compare different case cohorts separately.");
        foreach (string field in new[] { "windowStartSeconds", "windowEndSeconds" })
            if (TraceMeasurements.Number(first.Comparison["alignment"]![field]) != TraceMeasurements.Number(next.Comparison["alignment"]![field]))
                throw new BridgeException("ExperimentWindowMismatch", "Repeated pairs require the same requested relative window; individual anchors remain explicit.");
        string[] Mappings(JsonObject comparison) => D.Rows(comparison["signals"])
            .Select(s => D.Text(s, "beforeSignalId") + "\0" + D.Text(s, "afterSignalId") + "\0" + D.Text(s, "unit"))
            .Order(StringComparer.Ordinal).ToArray();
        if (!Mappings(first.Comparison).SequenceEqual(Mappings(next.Comparison), StringComparer.Ordinal))
            throw new BridgeException("MeasurementLineageMismatch", "Repeated pairs must compare the same explicit signal mappings and units.");
    }

    internal static string Id(JsonObject document) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document, BridgeProtocol.Json)));
    private static void Fields(JsonObject value, params string[] fields)
    {
        if (value.Any(p => !fields.Contains(p.Key, StringComparer.Ordinal)))
            throw new BridgeException("InvalidRequest", "Unknown repeated comparison request field.");
    }
}

internal sealed record DiagnosticRepeatedExperimentParts(JsonObject Report, DiagnosticExperimentReviewParts[] Pairs);

internal static class DiagnosticExperimentWindows
{
    internal static JsonObject Assess(JsonObject experiment, JsonObject imported, JsonObject comparison, string side)
    {
        var measurement = imported["measurement"]!;
        var quality = measurement["quality"]!; var assessment = TraceMeasurements.AssessQuality(measurement);
        decimal divisor = D.Text(measurement, "timeUnit") switch { "s" => 1m, "ms" => 1000m, "us" => 1000000m, "ns" => 1000000000m, _ => throw new BridgeException("InvalidRequest", "Unknown time unit.") };
        var alignment = comparison["alignment"]!;
        decimal anchor = TraceMeasurements.Number(alignment[side + "AnchorSeconds"]),
            start = TraceMeasurements.Number(alignment["windowStartSeconds"]), end = TraceMeasurements.Number(alignment["windowEndSeconds"]);
        var rows = D.Rows(measurement["samples"]).Select(sample => {
            decimal raw = TraceMeasurements.Number(sample!["time"]), seconds = raw / divisor;
            if (seconds * divisor != raw) throw new BridgeException("MeasurementFormatUnsupported", "Window conversion loses time precision.");
            return (Sample: sample!, Time: TraceNumber.SubtractTime(seconds, anchor));
        }).ToArray();
        var selected = rows.Where(r => r.Time >= start && r.Time <= end).ToArray();
        bool spans = rows[0].Time <= start && rows[^1].Time >= end;
        var reasons = new JsonArray();
        foreach (string field in new[] { "atomicAcrossSignals", "dropoutsKnown", "programMatchVerified" })
            if (quality[field]?.GetValue<bool>() != true) reasons.Add("Caller-attested " + field + " is false.");
        if (quality["reportedDroppedSamples"]!.GetValue<int>() > 0) reasons.Add("The capture reports dropped samples.");
        if (assessment["missingValues"]!.GetValue<int>() > 0) reasons.Add("The original capture contains missing signal values.");
        if (assessment["detectedIntervalGaps"]!.GetValue<int>() > 0) reasons.Add("The original capture contains detected sampling gaps.");
        if (quality["expectedInterval"] is null) reasons.Add("No expected sample interval was supplied.");
        if (!spans) reasons.Add("Recorded samples do not span both boundaries of the requested window.");
        if (selected.Length < 2) reasons.Add("Fewer than two recorded samples lie inside the requested window.");
        var manifest = D.Rows(experiment["diagnosticCase"]?["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var hypotheses = new JsonArray();
        foreach (var hypothesis in D.Rows(experiment["diagnosticCase"]?["hypotheses"]))
        {
            int matched = 0, contradicted = 0, unknown = 0; bool prediction = hypothesis?["prediction"] is JsonObject;
            if (hypothesis?["prediction"] is JsonObject rule)
            {
                var when = TraceMeasurements.Conditions(rule["when"], manifest); var expect = TraceMeasurements.Conditions(rule["expect"], manifest);
                foreach (var row in selected)
                {
                    var values = row.Sample["values"]!.AsObject(); bool? condition = TraceMeasurements.All(when, values);
                    if (condition == false) continue;
                    if (condition is null) { unknown++; continue; }
                    bool? expected = TraceMeasurements.All(expect, values);
                    if (expected is null) unknown++; else if (expected == true) matched++; else contradicted++;
                }
            }
            string verdict = !prediction || reasons.Count > 0 || unknown > 0 || matched + contradicted == 0 ? "unresolved"
                : contradicted > 0 ? "contradictedPrediction" : "supportedPrediction";
            hypotheses.Add(new JsonObject { ["id"] = hypothesis!["id"]!.DeepClone(), ["statement"] = hypothesis["statement"]!.DeepClone(),
                ["matchedSamples"] = matched, ["contradictedSamples"] = contradicted, ["unknownSamples"] = unknown, ["verdict"] = verdict,
                ["reason"] = !prediction ? "No declarative sample prediction."
                    : reasons.Count > 0 ? "Acquisition or window quality prevents a sample prediction verdict; counts remain observations."
                    : unknown > 0 ? "Required predicate values are unknown."
                    : matched + contradicted == 0 ? "The specified condition was not observed inside this window; absence does not prove a fix."
                    : "Verdict applies only to recorded samples inside the requested window, with caller-attested quality.",
                ["causationProven"] = false, ["bugFixProven"] = false });
        }
        return new JsonObject { ["requestedStartSeconds"] = start, ["requestedEndSeconds"] = end, ["anchorSeconds"] = anchor,
            ["firstRecordedSeconds"] = rows[0].Time, ["lastRecordedSeconds"] = rows[^1].Time, ["samplesInWindow"] = selected.Length,
            ["firstWindowSampleSeconds"] = selected.Length == 0 ? null : JsonValue.Create(selected[0].Time),
            ["lastWindowSampleSeconds"] = selected.Length == 0 ? null : JsonValue.Create(selected[^1].Time),
            ["recordedSpanCoversRequestedWindow"] = spans,
            ["coverage"] = selected.Length == 0 ? "noRecordedSamples" : spans ? "completeRecordedSpan" : "partialRecordedSpan",
            ["qualityGatePassed"] = reasons.Count == 0, ["qualityGateReasons"] = reasons,
            ["qualityGateScope"] = "conservativeWholeCaptureQualityAndRequestedWindow", ["qualityAssessment"] = assessment,
            ["acquisitionQuality"] = quality.DeepClone(), ["acquisitionContext"] = TraceMeasurements.AcquisitionContext(measurement),
            ["hypotheses"] = hypotheses, ["sourceAuthenticityVerified"] = false, ["clockSynchronizationVerified"] = false };
    }
}
