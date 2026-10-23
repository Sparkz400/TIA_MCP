using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Normalized measurement import and bounded declarative hypothesis checks. No code evaluation.</summary>
public sealed class TraceMeasurements(string artifactsRoot)
{
    public JsonObject ImportUpload(string tracePlanId, string uploadRef, JsonObject? csvOptions = null)
    {
        var uploaded = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        var artifact = new JsonObject { ["path"] = uploaded.Path, ["sha256"] = uploaded.Sha256 };
        var raw = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes);
        if (csvOptions is null && TraceQualityMeasurements.IsQualityDocument(raw))
            return Import(tracePlanId, artifact); // v2 validates before retaining an independent immutable input.
        return Import(tracePlanId, new DiagnosticStore(artifactsRoot).RetainUpload(uploadRef), csvOptions);
    }

    public JsonObject Import(string tracePlanId, JsonObject artifact, JsonObject? csvOptions = null)
    {
        var imported = Normalize(tracePlanId, artifact, csvOptions);
        if (TraceQualityMeasurements.IsV2(imported))
        {
            // Validate the entire request before publishing an immutable owned copy. A later quota/I/O
            // refusal may retain this valid child, but never returns a completed import.
            var raw = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes);
            imported["inputArtifact"] = new DiagnosticStore(artifactsRoot).Publish(raw, ".input")["artifact"]!.DeepClone();
        }
        var receipt = new DiagnosticStore(artifactsRoot).Save(imported);
        receipt["sampleCount"] = imported["measurement"]!["samples"]!.AsArray().Count;
        receipt["qualityAssessment"] = imported["qualityAssessment"]!.DeepClone();
        return receipt;
    }

    internal JsonObject Normalize(string tracePlanId, JsonObject artifact, JsonObject? csvOptions = null)
    {
        D.Required(artifact, "sha256", 64);
        var bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes);
        if (csvOptions is null && TraceQualityMeasurements.IsQualityDocument(bytes))
            return new TraceQualityMeasurements(artifactsRoot).Normalize(tracePlanId, artifact);
        var store = new DiagnosticStore(artifactsRoot); var plan = store.Read(tracePlanId, "trace-plan.v1");
        var measurement = csvOptions is null ? JsonNode.Parse(bytes)?.AsObject() ?? throw new BridgeException("MeasurementFormatUnsupported", "Expected normalized JSON.")
            : TraceCsvReader.Convert(bytes, csvOptions, plan, tracePlanId, artifact);
        if (D.Text(measurement, "schemaVersion") == "trace-measurement.v2")
            return new TraceQualityMeasurements(artifactsRoot).Normalize(tracePlanId, artifact);
        if (D.Text(measurement, "schemaVersion") != "trace-measurement.v1")
            throw new BridgeException("MeasurementFormatUnsupported", "Only trace-measurement.v1 JSON is supported. Native Siemens files require a separately qualified decoder.");
        if (D.Text(measurement, "tracePlanId") != tracePlanId || D.Text(measurement, "reviewId") != D.Text(plan, "reviewId"))
            throw new BridgeException("MeasurementLineageMismatch", "Measurement must name the exact trace plan and source review.");
        string mode = D.Required(measurement, "mode", 40);
        if (mode is not ("manualNativeExport" or "polling" or "synthetic")) throw new BridgeException("MeasurementFormatUnsupported", "Unknown acquisition mode.");
        D.Required(measurement, "timeUnit", 16);
        if (D.Text(measurement, "timeUnit") is not ("s" or "ms" or "us" or "ns")) throw new BridgeException("InvalidRequest", "Time unit must be s, ms, us or ns; timestamps are retained without resampling.");
        D.Required(measurement, "clock", 512);
        D.Required(measurement, "programIdentity", 2000);
        var quality = measurement["quality"] as JsonObject ?? throw new BridgeException("MeasurementIncomplete", "Explicit acquisition quality is required.");
        foreach (string field in new[] { "atomicAcrossSignals", "scanSynchronized", "dropoutsKnown", "programMatchVerified" })
            if (quality[field] is not JsonValue q || !q.TryGetValue<bool>(out _)) throw new BridgeException("MeasurementIncomplete", "Missing Boolean quality field: " + field);
        if (quality["reportedDroppedSamples"] is not JsonValue dropped || !dropped.TryGetValue<int>(out int lost) || lost < 0)
            throw new BridgeException("MeasurementIncomplete", "reportedDroppedSamples must be a nonnegative integer; dropoutsKnown states whether that count is known.");
        if (mode == "polling" && (quality["atomicAcrossSignals"]!.GetValue<bool>() || quality["scanSynchronized"]!.GetValue<bool>()))
            throw new BridgeException("AcquisitionRequirementUnsatisfied", "Polling cannot claim atomic samples or scan-synchronized acquisition.");
        if (mode == "manualNativeExport")
        {
            var original = measurement["originalArtifact"] as JsonObject ?? throw new BridgeException("MeasurementIncomplete", "Retain the original native export as a hashed host artifact.");
            D.Required(original, "sha256", 64);
            _ = ProjectReviewStore.ReadArtifact(artifactsRoot, original, 32 * 1024 * 1024);
            D.Required(measurement, "conversion", 2000);
            // These attestations are kept as supplied evidence, never upgraded to automatic qualification.
            D.Required(measurement, "settingsReadbackEvidence", 4096);
        }
        var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var units = measurement["units"] as JsonObject ?? throw new BridgeException("MeasurementIncomplete", "Units per signal are required (use '1' for dimensionless).");
        if (!units.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(signals.Keys)) throw new BridgeException("MeasurementLineageMismatch", "Unit columns must exactly match the signal manifest.");
        foreach (string id in signals.Keys) D.Required(units, id, 64);
        var samples = measurement["samples"] as JsonArray ?? throw new BridgeException("MeasurementIncomplete", "Missing samples.");
        if (samples.Count is < 1 or > 10000) throw new BridgeException("ArtifactCapacity", "Normalized import supports 1..10000 samples.");
        decimal? previous = null;
        decimal? expected = quality["expectedInterval"] is null ? null : Number(quality["expectedInterval"]);
        if (expected is <= 0 or > 1000000000000000000m) throw new BridgeException("InvalidRequest", "Expected interval must be positive and within the supported time range.");
        foreach (var sample in samples)
        {
            decimal time = Number(sample?["time"]);
            if (time is < -1000000000000000000m or > 1000000000000000000m || previous is not null && time <= previous)
                throw new BridgeException("MeasurementIncomplete", "Sample times must be strictly increasing within +/-1e18; negative pre-trigger times are preserved. Split clock resets into separate captures.");
            previous = time;
            var values = sample?["values"] as JsonObject ?? throw new BridgeException("MeasurementIncomplete", "Missing sample values.");
            if (!values.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(signals.Keys))
                throw new BridgeException("MeasurementLineageMismatch", "Every sample must contain all manifest IDs; use null for an unavailable value.");
            foreach (var pair in values)
            {
                if (pair.Value is null) continue;
                if (D.Text(signals[pair.Key], "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value is not JsonValue value || !value.TryGetValue<bool>(out _)) throw new BridgeException("MeasurementFormatUnsupported", "Boolean declaration requires Boolean sample values.");
                }
                else ValidateNumericSample(Number(pair.Value), D.Text(signals[pair.Key], "datatype"));
            }
        }
        var imported = new JsonObject { ["schemaVersion"] = "trace-import.v1", ["tracePlanId"] = tracePlanId,
            ["inputArtifact"] = artifact.DeepClone(), ["measurement"] = measurement.DeepClone(),
            ["qualityAssessment"] = AssessQuality(measurement) };
        return imported;
    }

    public JsonObject Analyze(string measurementId)
    {
        var report = BuildAnalysis(measurementId);
        var receipt = new DiagnosticStore(artifactsRoot).Save(report); receipt["report"] = report; return receipt;
    }

    internal JsonObject BuildAnalysis(string measurementId)
    {
        if (TraceQualityMeasurements.IsV2(new DiagnosticStore(artifactsRoot).Read(measurementId)))
            return new TraceQualityMeasurements(artifactsRoot).BuildAnalysis(measurementId);
        var store = new DiagnosticStore(artifactsRoot); var imported = store.Read(measurementId, "trace-import.v1");
        var plan = store.Read(D.Text(imported, "tracePlanId"), "trace-plan.v1");
        _ = new ProjectReviewStore(artifactsRoot).Read(D.Text(plan, "reviewId"));
        var m = imported["measurement"]!.AsObject(); var q = m["quality"]!;
        var assessment = AssessQuality(m);
        var samples = D.Rows(m["samples"]); var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        bool clean = q["atomicAcrossSignals"]!.GetValue<bool>() && q["dropoutsKnown"]!.GetValue<bool>() && q["programMatchVerified"]!.GetValue<bool>()
            && q["reportedDroppedSamples"]!.GetValue<int>() == 0
            && assessment["missingValues"]!.GetValue<int>() == 0 && assessment["detectedIntervalGaps"]!.GetValue<int>() == 0;
        var findings = new JsonArray();
        foreach (var hypothesis in D.Rows(plan["hypotheses"]))
        {
            var rule = hypothesis?["prediction"] as JsonObject;
            var finding = new JsonObject { ["id"] = hypothesis!["id"]!.DeepClone(), ["statement"] = hypothesis["statement"]!.DeepClone(),
                ["verdict"] = "unresolved", ["causationProven"] = false };
            findings.Add(finding);
            if (rule is null) { finding["reason"] = "No explicit bounded prediction supplied."; continue; }
            var when = Conditions(rule["when"], signals); var expect = Conditions(rule["expect"], signals);
            foreach (var condition in when.Concat(expect))
                if (!D.Text(signals[D.Text(condition, "signalId")], "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase)
                    && D.Text(m["units"], D.Text(condition, "signalId")) != D.Text(condition, "unit"))
                    throw new BridgeException("MeasurementLineageMismatch", "Numeric prediction units must exactly match measurement units; no implicit conversion is performed.");
            int matched = 0, contradicted = 0, unknown = 0; var examples = new JsonArray();
            foreach (var sample in samples)
            {
                var values = sample!["values"]!.AsObject(); bool? applies = All(when, values);
                if (applies == false) continue;
                if (applies is null) { unknown++; continue; }
                bool? observed = All(expect, values);
                if (observed is null) { unknown++; continue; }
                if (observed == true) matched++; else contradicted++;
                if (examples.Count < 20) examples.Add(new JsonObject { ["time"] = sample["time"]!.DeepClone(), ["predictionMatched"] = observed });
            }
            finding["matchedSamples"] = matched; finding["contradictedSamples"] = contradicted; finding["unknownSamples"] = unknown; finding["examples"] = examples;
            finding["verdict"] = !clean || unknown > 0 || matched + contradicted == 0 ? "unresolved" : contradicted > 0 ? "contradictedPrediction" : "supportedPrediction";
            finding["reason"] = !clean ? "Quality/program uncertainty prevents a definitive sample-level prediction verdict. Counts remain observations."
                : matched + contradicted == 0 ? "Capture did not contain the specified condition; absence does not prove the fault is fixed."
                : "Verdict applies only to supplied predictions at recorded samples; correlation/order does not prove cause or exclude shorter missed events.";
        }
        var transitions = new JsonArray(); int transitionCount = 0;
        foreach (string signal in signals.Keys)
            for (int index = 1; index < samples.Count; index++)
            {
                var before = samples[index - 1]!["values"]![signal]; var after = samples[index]!["values"]![signal];
                if (before is null || after is null || Equal(before, after)) continue;
                transitionCount++;
                if (transitions.Count < 200) transitions.Add(new JsonObject { ["signalId"] = signal, ["afterTime"] = samples[index - 1]!["time"]!.DeepClone(),
                    ["byTime"] = samples[index]!["time"]!.DeepClone(), ["before"] = before.DeepClone(), ["after"] = after.DeepClone() });
            }
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-report.v1", ["measurementId"] = measurementId,
            ["tracePlanId"] = imported["tracePlanId"]!.DeepClone(), ["reviewId"] = plan["reviewId"]!.DeepClone(),
            ["symptom"] = plan["report"]!.DeepClone(), ["mode"] = m["mode"]!.DeepClone(), ["hypotheses"] = findings,
            ["transitions"] = transitions, ["transitionCount"] = transitionCount, ["transitionsTruncated"] = transitionCount > 200,
            ["timeUnit"] = m["timeUnit"]!.DeepClone(), ["units"] = m["units"]!.DeepClone(),
            ["acquisitionQuality"] = q.DeepClone(), ["qualityAssessment"] = assessment,
            ["acquisitionContext"] = AcquisitionContext(m), ["clockSynchronizationVerified"] = false,
            ["coverageGaps"] = plan["coverageGaps"]!.DeepClone(), ["causationProven"] = false, ["nativeQualification"] = false,
            ["nextCapture"] = "Resolve quality and source-program uncertainty, reproduce the specified condition, and choose a target-qualified sample task/window capable of observing the shortest relevant event. Replan changed signals or instances." };
        return report;
    }

    internal static JsonObject AcquisitionContext(JsonNode measurement) => new() {
        ["mode"] = measurement["mode"]!.DeepClone(), ["clock"] = measurement["clock"]!.DeepClone(),
        ["timeUnit"] = measurement["timeUnit"]!.DeepClone(), ["programIdentity"] = measurement["programIdentity"]!.DeepClone(),
        ["identityVerified"] = false };

    // Reassess retained samples too; immutable older imports may contain rounded gap counts.
    internal static JsonObject AssessQuality(JsonNode measurement)
    {
        decimal? previous = null, expected = measurement["quality"]!["expectedInterval"] is null ? null : Number(measurement["quality"]!["expectedInterval"]);
        int missing = 0, gaps = 0;
        foreach (var sample in D.Rows(measurement["samples"]))
        {
            decimal time = Number(sample!["time"]);
            if (previous is not null && expected is not null && TraceNumber.HasIntervalGap(previous.Value, time, expected.Value)) gaps++;
            previous = time; missing += sample["values"]!.AsObject().Count(p => p.Value is null);
        }
        return new JsonObject { ["missingValues"] = missing, ["detectedIntervalGaps"] = gaps,
            ["qualityOrigin"] = "callerAttestedPlusStructuralChecks", ["nativeQualification"] = false,
            ["settingsIndependentlyVerifiedByTool"] = false, ["sourceProgramIndependentlyVerifiedByTool"] = false };
    }

    internal static JsonObject[] Conditions(JsonNode? node, Dictionary<string, JsonObject> signals)
    {
        if (node is not JsonArray array || array.Count is < 1 or > 16) throw new BridgeException("InvalidRequest", "Prediction when/expect must each contain 1..16 conditions.");
        var result = new List<JsonObject>();
        foreach (var item in array)
        {
            if (item is not JsonObject condition || !signals.TryGetValue(D.Text(item, "signalId"), out var signal)) throw new BridgeException("InvalidRequest", "Prediction references an unknown signal.");
            if (condition.Any(p => p.Key is not ("signalId" or "op" or "value" or "unit"))) throw new BridgeException("InvalidRequest", "Unknown prediction condition fields cannot be silently ignored.");
            string op = D.Text(condition, "op");
            if (op is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge")) throw new BridgeException("InvalidRequest", "Unsupported comparison operator.");
            string datatype = D.Text(signal, "datatype");
            if (!DiagnosticPlanner.ScalarTypes.Contains(datatype, StringComparer.OrdinalIgnoreCase))
                throw new BridgeException("SignalUnobservable", "Prediction signal '" + D.Text(condition, "signalId") + "' has no supported captured scalar type. Resolve its declaration or revise the prediction in a new diagnostic case.");
            bool boolean = datatype.Equals("Bool", StringComparison.OrdinalIgnoreCase);
            if (boolean && (op is not ("eq" or "ne") || condition["value"] is not JsonValue value || !value.TryGetValue<bool>(out _)))
                throw new BridgeException("InvalidRequest", "Bool predictions support eq/ne against Boolean values only.");
            if (!boolean) { _ = Number(condition["value"]); D.Required(condition, "unit", 64); }
            result.Add(condition);
        }
        return result.ToArray();
    }
    internal static void ValidatePredictions(JsonArray hypotheses, JsonArray signals)
    {
        var manifest = signals.OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        foreach (var h in hypotheses)
        {
            if (h?["prediction"] is null) continue;
            if (h["prediction"] is not JsonObject rule) throw new BridgeException("InvalidRequest", "Expected a declarative prediction object.");
            if (rule.Any(p => p.Key is not ("when" or "expect"))) throw new BridgeException("InvalidRequest", "Unknown prediction fields cannot be silently ignored.");
            _ = Conditions(rule["when"], manifest); _ = Conditions(rule["expect"], manifest);
        }
    }
    internal static void ValidateNumericSample(decimal number, string datatype)
    {
        (decimal min, decimal max)? range = datatype.ToUpperInvariant() switch {
            "SINT" => (sbyte.MinValue, sbyte.MaxValue), "USINT" or "BYTE" => (0, byte.MaxValue),
            "INT" => (short.MinValue, short.MaxValue), "UINT" or "WORD" => (0, ushort.MaxValue),
            "DINT" => (int.MinValue, int.MaxValue), "UDINT" or "DWORD" => (0, uint.MaxValue),
            "LINT" => (long.MinValue, long.MaxValue), "ULINT" or "LWORD" => (0, ulong.MaxValue), _ => null };
        if (range is not null && (number < range.Value.min || number > range.Value.max || decimal.Truncate(number) != number))
            throw new BridgeException("MeasurementFormatUnsupported", "Numeric sample is outside the declared integer type/range.");
    }
    internal static bool? All(JsonObject[] conditions, JsonObject values)
    {
        bool unknown = false;
        foreach (var c in conditions)
        {
            var observed = values[D.Text(c, "signalId")]; if (observed is null) { unknown = true; continue; }
            var expected = c["value"]!; string op = D.Text(c, "op");
            bool result = op switch { "eq" => Equal(observed, expected), "ne" => !Equal(observed, expected),
                "lt" => Number(observed) < Number(expected), "le" => Number(observed) <= Number(expected),
                "gt" => Number(observed) > Number(expected), "ge" => Number(observed) >= Number(expected), _ => false };
            if (!result) return false;
        }
        return unknown ? null : true;
    }
    internal static bool Equal(JsonNode a, JsonNode b) => a is JsonValue v && v.TryGetValue<bool>(out bool value)
        ? b is JsonValue w && w.TryGetValue<bool>(out bool other) && value == other : Number(a) == Number(b);
    internal static decimal Number(JsonNode? node)
    {
        if (node is not JsonValue)
            throw new BridgeException("MeasurementFormatUnsupported", "Expected a finite JSON decimal number within decimal range; text values are not coerced.");
        return TraceNumber.Parse(node.ToJsonString());
    }
}
