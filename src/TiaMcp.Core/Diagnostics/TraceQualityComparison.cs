using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

internal sealed class TraceQualityComparison(string artifactsRoot)
{
    internal JsonObject Build(string beforeId, string afterId, JsonObject options)
    {
        D.Bound(options);
        TraceQualityMeasurements.Fields(options, "reason", "beforeClockId", "beforeEpoch", "afterClockId", "afterEpoch",
            "beforeAnchorSeconds", "afterAnchorSeconds", "windowStartSeconds", "windowEndSeconds", "allowChangedSource", "signalMap");
        D.Required(options, "reason", 2000);
        if (options.ContainsKey("allowChangedSource") && (options["allowChangedSource"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new BridgeException("InvalidRequest", "allowChangedSource must be Boolean.");
        var reader = new TraceQualityMeasurements(artifactsRoot);
        var a = reader.Read(beforeId); var b = reader.Read(afterId);
        var am = TraceQualityMeasurements.Object(a, "measurement"); var bm = TraceQualityMeasurements.Object(b, "measurement");
        var ap = reader.Plan(D.Required(a, "tracePlanId", 64)); var bp = reader.Plan(D.Required(b, "tracePlanId", 64));
        ValidateWindow(am, options, "before"); ValidateWindow(bm, options, "after");
        bool changed = D.Text(ap, "reviewId") != D.Text(bp, "reviewId");
        bool changedProgram = D.Text(am, "programIdentity") != D.Text(bm, "programIdentity");
        if ((changed || changedProgram) && options["allowChangedSource"]?.GetValue<bool>() != true)
            throw new BridgeException("ProgramRevisionMismatch", "Changed reviews/program labels require explicit allowChangedSource and reason.");
        var aSignals = D.Rows(ap["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var bSignals = D.Rows(bp["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        bool explicitMap = options.ContainsKey("signalMap");
        var maps = explicitMap ? TraceQualityMeasurements.Array(options, "signalMap", 64)
            : new JsonArray(aSignals.Keys.Intersect(bSignals.Keys, StringComparer.Ordinal)
                .Select(id => (JsonNode)new JsonObject { ["before"] = id, ["after"] = id }).ToArray());
        if (maps.Count == 0) throw new BridgeException("MeasurementLineageMismatch", "At least one signal mapping is required.");
        var aid = new HashSet<string>(StringComparer.Ordinal); var bid = new HashSet<string>(StringComparer.Ordinal);
        var results = new JsonArray();
        foreach (var item in maps)
        {
            var map = item as JsonObject ?? throw new BridgeException("InvalidRequest", "Signal mapping must be an object.");
            TraceQualityMeasurements.Fields(map, "before", "after");
            string x = D.Required(map, "before", 64), y = D.Required(map, "after", 64);
            if (!aid.Add(x) || !bid.Add(y) || !aSignals.TryGetValue(x, out var sa) || !bSignals.TryGetValue(y, out var sb))
                throw new BridgeException("MeasurementLineageMismatch", "Mappings must be distinct exact signal IDs.");
            bool storage = D.Text(sa, "storagePath") == D.Text(sb, "storagePath") && JsonNode.DeepEquals(sa["memberPath"], sb["memberPath"]);
            if (!storage && !explicitMap) throw new BridgeException("MeasurementLineageMismatch", "Changed instance/storage requires explicit signal mappings.");
            if (D.Text(sa, "datatype") != D.Text(sb, "datatype") || D.Text(am["units"], x) != D.Text(bm["units"], y))
                throw new BridgeException("MeasurementLineageMismatch", "Signal types and units must match exactly.");
            results.Add(new JsonObject { ["beforeSignalId"] = x, ["afterSignalId"] = y,
                ["beforeIdentity"] = sa.DeepClone(), ["afterIdentity"] = sb.DeepClone(), ["sameStorage"] = storage,
                ["unit"] = am["units"]![x]!.DeepClone(), ["before"] = WindowSeries(am, x, options, "before"),
                ["after"] = WindowSeries(bm, y, options, "after") });
        }
        var report = new JsonObject { ["schemaVersion"] = "trace-comparison.v2", ["beforeMeasurementId"] = beforeId, ["afterMeasurementId"] = afterId,
            ["beforeTracePlanId"] = a["tracePlanId"]!.DeepClone(), ["afterTracePlanId"] = b["tracePlanId"]!.DeepClone(),
            ["beforeReviewId"] = ap["reviewId"]!.DeepClone(), ["afterReviewId"] = bp["reviewId"]!.DeepClone(),
            ["reason"] = options["reason"]!.DeepClone(), ["alignment"] = options.DeepClone(), ["signals"] = results,
            ["sourceChanged"] = changed, ["programIdentityChanged"] = changedProgram,
            ["beforeAcquisition"] = TraceQualityMeasurements.Context(am), ["afterAcquisition"] = TraceQualityMeasurements.Context(bm),
            ["beforeAssessment"] = a["qualityAssessment"]!.DeepClone(), ["afterAssessment"] = b["qualityAssessment"]!.DeepClone(),
            ["beforeWindow"] = Window(am, ap, options, "before"), ["afterWindow"] = Window(bm, bp, options, "after"),
            ["settingsEqual"] = JsonNode.DeepEquals(ap["requestedSettings"], bp["requestedSettings"]), ["resampled"] = false };
        TraceQualityMeasurements.Authority(report); D.Bound(report, DiagnosticStore.MaxBytes); return report;
    }
    internal static void ValidateWindow(JsonObject m, JsonObject options, string side)
    {
        string clock = D.Required(options, side + "ClockId", 64), epoch = D.Required(options, side + "Epoch", 64);
        if (!D.Rows(m["clocks"]).Any(c => D.Text(c, "id") == clock))
            throw new BridgeException("MeasurementClockMismatch", "Selected clock domain does not exist in the exact measurement.");
        if (!D.Rows(m["samples"]).Any(s => TraceQualityMeasurements.Object(s, "readings").Any(r =>
                TraceQualityMeasurements.Stamp(r.Value, clock) is JsonObject stamp && D.Text(stamp, "epoch") == epoch)))
            throw new BridgeException("MeasurementClockMismatch", "Selected epoch is not recorded in this clock domain; no implicit epoch selection.");
        _ = TraceQualityMeasurements.Time(options[side + "AnchorSeconds"]);
        if (TraceQualityMeasurements.Time(options["windowStartSeconds"]) >= TraceQualityMeasurements.Time(options["windowEndSeconds"]))
            throw new BridgeException("InvalidRequest", "Window start must precede end.");
    }
    internal static JsonObject WindowSeries(JsonObject m, string signal, JsonObject options, string side) =>
        TraceQualityMeasurements.Series(m, signal, D.Required(options, side + "ClockId", 64), D.Required(options, side + "Epoch", 64),
            TraceQualityMeasurements.Time(options[side + "AnchorSeconds"]),
            TraceQualityMeasurements.Time(options["windowStartSeconds"]), TraceQualityMeasurements.Time(options["windowEndSeconds"]));
    internal static JsonObject Window(JsonObject m, JsonObject plan, JsonObject options, string side)
    {
        string clock = D.Required(options, side + "ClockId", 64), epoch = D.Required(options, side + "Epoch", 64);
        decimal anchor = TraceQualityMeasurements.Time(options[side + "AnchorSeconds"]),
            start = TraceQualityMeasurements.Time(options["windowStartSeconds"]), end = TraceQualityMeasurements.Time(options["windowEndSeconds"]);
        bool Selected(JsonNode sample, string signal)
        {
            var stamp = TraceQualityMeasurements.Stamp(sample["readings"]?[signal], clock);
            if (stamp?["value"] is null || D.Text(stamp, "epoch") != epoch) return false;
            decimal time = TraceNumber.SubtractTime(TraceQualityMeasurements.Seconds(m, clock, stamp["value"]!), anchor);
            return time >= start && time <= end;
        }
        var series = new JsonArray(D.Rows(plan["signals"]).Select(s => (JsonNode)WindowSeries(m, D.Text(s, "id"), options, side)).ToArray());
        var reasons = new JsonArray("Supplied clocks, epochs, sequence grouping and program identity are not independently verified.");
        if (D.Text(m["capture"], "state") != "complete") reasons.Add("Capture is not declared complete.");
        if (m["capture"]!["reportedDroppedSamples"] is null || m["capture"]!["reportedDroppedSamples"]!.GetValue<int>() != 0)
            reasons.Add("Dropped sample count is unknown or nonzero.");
        // Inspect the complete selected epoch: clipping a window cannot hide its reset/gaps/bad evidence.
        var full = new JsonArray(D.Rows(plan["signals"]).Select(s => (JsonNode)TraceQualityMeasurements.Series(
            m, D.Text(s, "id"), clock, epoch, anchor, null, null)).ToArray());
        foreach (var s in full)
        {
            if (s!["clockResets"]!.GetValue<int>() > 0 || s["duplicateTimes"]!.GetValue<int>() > 0)
                reasons.Add("Selected epoch contains reset or duplicate timestamps for " + D.Text(s, "signalId") + ".");
            if (s["firstRecordedTime"] is null || s["lastRecordedTime"] is null ||
                TraceQualityMeasurements.Number(s["firstRecordedTime"]) > start || TraceQualityMeasurements.Number(s["lastRecordedTime"]) < end)
                reasons.Add("Recorded epoch does not span both requested boundaries for " + D.Text(s, "signalId") + ".");
        }
        return new JsonObject { ["clockId"] = clock, ["epoch"] = epoch, ["anchorSeconds"] = anchor,
            ["requestedStartSeconds"] = start, ["requestedEndSeconds"] = end, ["series"] = series,
            ["completeEpochAssessment"] = full, ["hypotheses"] = TraceQualityMeasurements.Findings(m, plan, Selected),
            ["coverage"] = "recordedObservationsOnly", ["verdict"] = "unresolved", ["reasons"] = reasons,
            ["completenessVerified"] = false, ["clockSynchronizationVerified"] = false };
    }
}
