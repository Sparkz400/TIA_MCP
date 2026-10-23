using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Explicit event-aligned comparison; preserves source revisions and acquisition differences.</summary>
public sealed class TraceComparison(string artifactsRoot)
{
    public JsonObject Compare(string beforeId, string afterId, JsonObject options)
    {
        var report = Build(beforeId, afterId, options);
        var receipt = new DiagnosticStore(artifactsRoot).Save(report); receipt["comparison"] = report; return receipt;
    }

    internal JsonObject Build(string beforeId, string afterId, JsonObject options)
    {
        var versions = new DiagnosticStore(artifactsRoot);
        bool aV2 = TraceQualityMeasurements.IsV2(versions.Read(beforeId)), bV2 = TraceQualityMeasurements.IsV2(versions.Read(afterId));
        if (aV2 != bV2) throw new BridgeException("MeasurementVersionMismatch", "Mixed v1/v2 comparisons are refused; preserve each original contract.");
        if (aV2) return new TraceQualityComparison(artifactsRoot).Build(beforeId, afterId, options);
        D.Bound(options);
        if (options.Any(p => p.Key is not ("reason" or "beforeAnchorSeconds" or "afterAnchorSeconds" or "windowStartSeconds" or "windowEndSeconds" or "allowChangedSource" or "signalMap")))
            throw new BridgeException("InvalidRequest", "Unknown comparison option; explicit signal selection cannot be ignored.");
        if (options.ContainsKey("allowChangedSource") && (options["allowChangedSource"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new BridgeException("InvalidRequest", "allowChangedSource must be Boolean when supplied.");
        if (options.ContainsKey("signalMap") && options["signalMap"] is not JsonArray)
            throw new BridgeException("InvalidRequest", "signalMap must be an array when supplied; omit it to select matching IDs automatically.");
        var store = new DiagnosticStore(artifactsRoot);
        var beforeImport = store.Read(beforeId, "trace-import.v1"); var afterImport = store.Read(afterId, "trace-import.v1");
        var before = beforeImport["measurement"]!; var after = afterImport["measurement"]!;
        var beforePlan = store.Read(D.Text(beforeImport, "tracePlanId"), "trace-plan.v1"); var afterPlan = store.Read(D.Text(afterImport, "tracePlanId"), "trace-plan.v1");
        bool changedSource = D.Text(beforePlan, "reviewId") != D.Text(afterPlan, "reviewId");
        bool changedProgram = D.Text(before, "programIdentity") != D.Text(after, "programIdentity");
        string reason = D.Required(options, "reason", 2000);
        if ((changedSource || changedProgram) && options["allowChangedSource"]?.GetValue<bool>() != true)
            throw new BridgeException("ProgramRevisionMismatch", "Different source reviews or declared program identities require allowChangedSource:true and an explicit comparison reason.");
        decimal beforeAnchor = Time(options["beforeAnchorSeconds"]), afterAnchor = Time(options["afterAnchorSeconds"]);
        decimal start = Time(options["windowStartSeconds"]), end = Time(options["windowEndSeconds"]);
        if (start >= end) throw new BridgeException("InvalidRequest", "Comparison window start must precede end, relative to the explicit anchors.");
        var aSignals = D.Rows(beforePlan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var bSignals = D.Rows(afterPlan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var mappings = options["signalMap"] as JsonArray;
        bool explicitMapping = mappings is not null;
        mappings ??= new JsonArray(aSignals.Keys.Intersect(bSignals.Keys, StringComparer.Ordinal).Select(id => (JsonNode?)new JsonObject { ["before"] = id, ["after"] = id }).ToArray());
        if (mappings.Any(m => m is not JsonObject row || row.Any(p => p.Key is not ("before" or "after"))
            || row["before"] is not JsonValue aId || !aId.TryGetValue<string>(out _)
            || row["after"] is not JsonValue bId || !bId.TryGetValue<string>(out _)))
            throw new BridgeException("InvalidRequest", "Every signal mapping must contain only before/after string IDs.");
        if (mappings.Count is < 1 or > 64 || mappings.Select(m => D.Text(m, "before")).Distinct().Count() != mappings.Count || mappings.Select(m => D.Text(m, "after")).Distinct().Count() != mappings.Count)
            throw new BridgeException("InvalidRequest", "Comparison needs 1..64 one-to-one signal mappings.");
        var results = new JsonArray();
        foreach (var mapping in mappings)
        {
            string aid = D.Required(mapping!, "before", 64), bid = D.Required(mapping!, "after", 64);
            if (!aSignals.TryGetValue(aid, out var a) || !bSignals.TryGetValue(bid, out var b)) throw new BridgeException("MeasurementLineageMismatch", "Comparison mapping contains an unknown signal.");
            bool sameStorage = D.Text(a, "storagePath") == D.Text(b, "storagePath") && JsonNode.DeepEquals(a["memberPath"], b["memberPath"]);
            if (!sameStorage && !explicitMapping) throw new BridgeException("MeasurementLineageMismatch", "Different storage/instances require an explicit signalMap; IDs alone are not identity.");
            if (D.Text(a, "datatype") != D.Text(b, "datatype") || D.Text(before["units"], aid) != D.Text(after["units"], bid))
                throw new BridgeException("MeasurementLineageMismatch", "Compared signal types and units must match exactly; no silent unit/type conversion.");
            results.Add(new JsonObject { ["beforeSignalId"] = aid, ["afterSignalId"] = bid, ["sameStorage"] = sameStorage,
                ["beforeIdentity"] = a.DeepClone(), ["afterIdentity"] = b.DeepClone(), ["unit"] = before["units"]![aid]!.DeepClone(),
                ["before"] = Summarize(before, aid, beforeAnchor, start, end), ["after"] = Summarize(after, bid, afterAnchor, start, end) });
        }
        var report = new JsonObject { ["schemaVersion"] = "trace-comparison.v1", ["beforeMeasurementId"] = beforeId, ["afterMeasurementId"] = afterId,
            ["beforeTracePlanId"] = beforeImport["tracePlanId"]!.DeepClone(), ["afterTracePlanId"] = afterImport["tracePlanId"]!.DeepClone(),
            ["beforeReviewId"] = beforePlan["reviewId"]!.DeepClone(), ["afterReviewId"] = afterPlan["reviewId"]!.DeepClone(),
            ["sourceChanged"] = changedSource, ["reason"] = reason, ["alignment"] = options.DeepClone(), ["signals"] = results,
            ["programIdentityChanged"] = changedProgram, ["beforeAcquisition"] = TraceMeasurements.AcquisitionContext(before), ["afterAcquisition"] = TraceMeasurements.AcquisitionContext(after),
            ["clockSynchronizationVerified"] = false,
            ["beforeQuality"] = before["quality"]!.DeepClone(), ["afterQuality"] = after["quality"]!.DeepClone(),
            ["beforeAssessment"] = TraceMeasurements.AssessQuality(before), ["afterAssessment"] = TraceMeasurements.AssessQuality(after),
            ["settingsEqual"] = JsonNode.DeepEquals(beforePlan["requestedSettings"], afterPlan["requestedSettings"]),
            ["acquisitionModesEqual"] = D.Text(before, "mode") == D.Text(after, "mode"), ["resampled"] = false,
            ["bugFixProven"] = false, ["nativeQualification"] = false,
            ["limits"] = "Explicit caller-selected anchors do not establish clock synchronization or simultaneous events. Program identities and clock labels are supplied observations, not authenticated native identities. Counts/ranges may reflect different sample rates or coverage. Missing events never prove a fix; preserve acquisition uncertainty and review changed programs independently." };
        return report;
    }
    private static JsonObject Summarize(JsonNode measurement, string id, decimal anchor, decimal start, decimal end)
    {
        decimal divisor = D.Text(measurement, "timeUnit") switch { "s" => 1m, "ms" => 1000m, "us" => 1000000m, "ns" => 1000000000m, _ => throw new BridgeException("InvalidRequest", "Unknown measurement time unit.") };
        int count = 0, missing = 0, trueCount = 0, falseCount = 0, transitionCount = 0; decimal? minimum = null, maximum = null; var changes = new JsonArray(); JsonNode? previous = null; decimal? previousTime = null;
        foreach (var sample in D.Rows(measurement["samples"]))
        {
            decimal rawTime = Numeric(sample?["time"]), seconds = rawTime / divisor;
            if (seconds * divisor != rawTime) throw new BridgeException("MeasurementFormatUnsupported", "Time conversion would lose precision; choose a supported exact time representation.");
            decimal time = TraceNumber.SubtractTime(seconds, anchor);
            if (time < start || time > end) continue;
            count++; var value = sample!["values"]![id];
            if (value is null) { missing++; previous = null; previousTime = null; continue; }
            if (value is JsonValue v && v.TryGetValue<bool>(out bool boolean)) { if (boolean) trueCount++; else falseCount++; }
            else { decimal n = Numeric(value); minimum = minimum is null ? n : Math.Min(minimum.Value, n); maximum = maximum is null ? n : Math.Max(maximum.Value, n); }
            bool equal = previous is not null && (value is JsonValue j && j.TryGetValue<bool>(out _) ? JsonNode.DeepEquals(previous, value) : Numeric(previous) == Numeric(value));
            if (previous is not null && !equal)
            {
                transitionCount++;
                if (changes.Count < 200) changes.Add(new JsonObject { ["afterSeconds"] = previousTime, ["bySeconds"] = time, ["before"] = previous.DeepClone(), ["after"] = value.DeepClone() });
            }
            previous = value; previousTime = time;
        }
        return new JsonObject { ["samplesInWindow"] = count, ["missingValues"] = missing, ["trueSamples"] = trueCount, ["falseSamples"] = falseCount,
            ["minimum"] = minimum, ["maximum"] = maximum, ["transitions"] = changes, ["transitionCount"] = transitionCount, ["transitionsTruncated"] = transitionCount > 200,
            ["coverage"] = count == 0 ? "noSamplesInWindow" : "recordedSamplesOnly" };
    }
    private static decimal Numeric(JsonNode? node)
    {
        if (node is null) throw new BridgeException("InvalidRequest", "Comparison numeric value is required.");
        return TraceNumber.Parse(node.ToJsonString());
    }
    private static decimal Time(JsonNode? node) { decimal value = Numeric(node); return value is >= -1000000000000000000m and <= 1000000000000000000m ? value : throw new BridgeException("InvalidRequest", "Comparison time must be within +/-1e18 seconds."); }
}
