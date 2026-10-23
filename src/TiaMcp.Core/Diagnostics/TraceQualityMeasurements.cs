using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Versioned supplied observations; clocks, quality and incomplete data are never upgraded to authority.</summary>
public sealed class TraceQualityMeasurements(string artifactsRoot)
{
    internal const string Limits = "Supplied sequence groups and individual observations only. Clock labels, epochs, source identity, atomicity and synchronization are unverified. No interpolation, zero substitution, rate, correlation, causal effect or successful fix is established. Missing, bad, uncertain, untimed and truncated evidence remains explicit.";
    internal static string Schema(JsonNode? value) => D.Text(value, "schemaVersion");
    internal static bool IsV2(JsonNode? value) => Schema(value) == "trace-import.v2";
    internal static bool IsQualityDocument(byte[] bytes)
    {
        using var parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
            !parsed.RootElement.EnumerateObject().Any(p => p.Name == "schemaVersion" &&
                p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "trace-measurement.v2")) return false;
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new BridgeException("MeasurementFormatUnsupported", "Duplicate JSON property names are ambiguous.");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Check(item);
        }
        Check(parsed.RootElement);
        return true;
    }
    internal static JsonObject Object(JsonNode? value, string field) => value?[field] as JsonObject
        ?? throw new BridgeException("MeasurementFormatUnsupported", field + " must be an object.");
    internal static JsonArray Array(JsonNode? value, string field, int maximum) =>
        value?[field] is JsonArray array && array.Count <= maximum ? array
        : throw new BridgeException("MeasurementFormatUnsupported", field + " must be an array within its published bound.");
    internal static void Fields(JsonObject value, params string[] names)
    {
        if (value.Any(p => !names.Contains(p.Key, StringComparer.Ordinal)))
            throw new BridgeException("MeasurementFormatUnsupported", "Unknown quality/timing field cannot be ignored.");
    }
    internal static decimal Number(JsonNode? value) => TraceMeasurements.Number(value);
    internal static decimal Time(JsonNode? value)
    {
        decimal number = Number(value);
        return number is >= -1000000000000000000m and <= 1000000000000000000m ? number
            : throw new BridgeException("MeasurementFormatUnsupported", "Time is outside +/-1e18.");
    }
    internal static void TextOrNull(JsonObject value, string field, int maximum)
    {
        if (!value.ContainsKey(field) || value[field] is not null &&
            (value[field] is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length > maximum))
            throw new BridgeException("MeasurementFormatUnsupported", field + " must be an explicit bounded string or null.");
    }
    internal static void Same(JsonNode? actual, JsonNode? expected, string label)
    {
        if (!JsonNode.DeepEquals(actual, expected))
            throw new BridgeException("CaptureMismatch", "Retained " + label + " differs from its original evidence.");
    }
    internal static string Id(JsonObject document) => DiagnosticRepeatedExperiments.Id(document);
    internal static void Authority(JsonObject document)
    {
        foreach (string key in new[] { "sourceAuthenticityVerified", "clockSynchronizationVerified", "atomicityVerified",
            "behavioralAuthority", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted" })
            document[key] = false;
        document["limits"] = Limits;
    }

    internal JsonObject Plan(string traceId)
    {
        var store = new DiagnosticStore(artifactsRoot); var retained = store.Read(traceId, "trace-plan.v1");
        var request = new JsonObject { ["backend"] = retained["backend"]?.DeepClone(), ["family"] = retained["family"]?.DeepClone(),
            ["reviewId"] = retained["reviewId"]?.DeepClone(), ["settings"] = retained["requestedSettings"]?.DeepClone() };
        var derived = new TracePlanning(artifactsRoot).Build(D.Required(retained, "diagnosticPlanId", 64), request);
        Same(retained, derived, "trace specification");
        return retained;
    }

    internal JsonObject Normalize(string traceId, JsonObject artifact)
    {
        var raw = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes);
        var measurement = JsonNode.Parse(raw) as JsonObject
            ?? throw new BridgeException("MeasurementFormatUnsupported", "Expected trace-measurement.v2 JSON.");
        _ = IsQualityDocument(raw); // Includes duplicate-name checks before any publication.
        Fields(measurement, "schemaVersion", "tracePlanId", "reviewId", "mode", "programIdentity", "clocks", "quality",
            "capture", "expectedIntervals", "units", "samples", "originalArtifact", "conversion", "settingsReadbackEvidence");
        var plan = Plan(traceId);
        if (Schema(measurement) != "trace-measurement.v2" || D.Text(measurement, "tracePlanId") != traceId ||
            D.Text(measurement, "reviewId") != D.Text(plan, "reviewId"))
            throw new BridgeException("MeasurementLineageMismatch", "Quality measurement must name its exact trace plan and review.");
        string mode = D.Required(measurement, "mode", 40);
        if (mode is not ("synthetic" or "polling" or "manualNativeExport"))
            throw new BridgeException("MeasurementFormatUnsupported", "Unsupported acquisition mode.");
        D.Required(measurement, "programIdentity", 2000);
        var quality = Object(measurement, "quality");
        Fields(quality, "atomicAcrossSignals", "scanSynchronized", "programMatchVerified");
        foreach (string field in new[] { "atomicAcrossSignals", "scanSynchronized", "programMatchVerified" })
            if (quality[field] is not JsonValue v || !v.TryGetValue<bool>(out _))
                throw new BridgeException("MeasurementFormatUnsupported", "Explicit Boolean supplied quality is required.");
        if (mode == "polling" && (quality["atomicAcrossSignals"]!.GetValue<bool>() || quality["scanSynchronized"]!.GetValue<bool>()))
            throw new BridgeException("AcquisitionRequirementUnsatisfied", "Polling cannot claim atomic or scan-synchronized sampling.");
        var capture = Object(measurement, "capture"); Fields(capture, "state", "reason", "reportedDroppedSamples");
        if (D.Text(capture, "state") is not ("complete" or "incomplete" or "truncated" or "unknown"))
            throw new BridgeException("MeasurementFormatUnsupported", "Explicit capture completeness state is required.");
        D.Required(capture, "reason", 2000);
        if (!capture.ContainsKey("reportedDroppedSamples") || capture["reportedDroppedSamples"] is not null &&
            (capture["reportedDroppedSamples"] is not JsonValue lost || !lost.TryGetValue<int>(out int count) || count < 0))
            throw new BridgeException("MeasurementFormatUnsupported", "Dropped sample count must be a nonnegative integer or explicit null.");
        var clocks = Array(measurement, "clocks", 8);
        if (clocks.Count == 0) throw new BridgeException("MeasurementFormatUnsupported", "At least one explicit clock domain is required.");
        var clockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in clocks)
        {
            var clock = item as JsonObject ?? throw new BridgeException("MeasurementFormatUnsupported", "Clock must be an object.");
            Fields(clock, "id", "label", "kind", "timeUnit");
            if (!clockIds.Add(D.Required(clock, "id", 64))) throw new BridgeException("MeasurementFormatUnsupported", "Duplicate clock domain.");
            D.Required(clock, "label", 512);
            if (D.Text(clock, "kind") is not ("source" or "host" or "supplied") || D.Text(clock, "timeUnit") is not ("s" or "ms" or "us" or "ns"))
                throw new BridgeException("MeasurementFormatUnsupported", "Unsupported clock kind or time unit.");
        }
        var intervals = Array(measurement, "expectedIntervals", 64); var intervalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in intervals)
        {
            var interval = item as JsonObject ?? throw new BridgeException("MeasurementFormatUnsupported", "Interval must be an object.");
            Fields(interval, "clockId", "epoch", "interval");
            string clock = D.Required(interval, "clockId", 64), epoch = D.Required(interval, "epoch", 64);
            if (!clockIds.Contains(clock) || !intervalKeys.Add(clock + "\0" + epoch) || Time(interval["interval"]) <= 0)
                throw new BridgeException("MeasurementFormatUnsupported", "Expected intervals require distinct declared clock/epoch and positive exact interval.");
        }
        var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var units = Object(measurement, "units");
        if (!units.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(signals.Keys))
            throw new BridgeException("MeasurementLineageMismatch", "Units must exactly match the retained signal manifest.");
        foreach (string signal in signals.Keys) D.Required(units, signal, 64);
        long previousSequence = -1;
        foreach (var item in Array(measurement, "samples", 10000))
        {
            var sample = item as JsonObject ?? throw new BridgeException("MeasurementFormatUnsupported", "Sample must be an object.");
            Fields(sample, "sequence", "readings");
            if (sample["sequence"] is not JsonValue seq || !seq.TryGetValue<long>(out long sequence) ||
                sequence <= previousSequence || sequence > 1000000000000L)
                throw new BridgeException("MeasurementFormatUnsupported", "Sequence must increase in original order within 0..1e12; timestamps may reset.");
            previousSequence = sequence;
            var readings = Object(sample, "readings");
            foreach (var pair in readings)
            {
                if (!signals.TryGetValue(pair.Key, out var signal)) throw new BridgeException("MeasurementLineageMismatch", "Unknown signal reading.");
                var read = pair.Value as JsonObject ?? throw new BridgeException("MeasurementFormatUnsupported", "Reading must be an object.");
                Fields(read, "value", "status", "statusCode", "error", "timestamps");
                string status = D.Required(read, "status", 32);
                if (status is not ("good" or "bad" or "uncertain" or "missing") || !read.ContainsKey("value") ||
                    read["value"] is JsonArray or JsonObject)
                    throw new BridgeException("MeasurementFormatUnsupported", "Reading requires scalar/null value and explicit supported status.");
                TextOrNull(read, "statusCode", 512); TextOrNull(read, "error", 2000);
                if (status == "good" && read["value"] is not null)
                {
                    if (D.Text(signal, "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase))
                    {
                        if (read["value"] is not JsonValue boolean || !boolean.TryGetValue<bool>(out _))
                            throw new BridgeException("MeasurementFormatUnsupported", "Good Boolean observations require Boolean values.");
                    }
                    else TraceMeasurements.ValidateNumericSample(Number(read["value"]), D.Text(signal, "datatype"));
                }
                var observedClocks = new HashSet<string>(StringComparer.Ordinal);
                foreach (var timestamp in Array(read, "timestamps", 8))
                {
                    var stamp = timestamp as JsonObject ?? throw new BridgeException("MeasurementFormatUnsupported", "Timestamp must be an object.");
                    Fields(stamp, "clockId", "epoch", "value");
                    string clock = D.Required(stamp, "clockId", 64); D.Required(stamp, "epoch", 64);
                    if (!clockIds.Contains(clock) || !observedClocks.Add(clock) || !stamp.ContainsKey("value"))
                        throw new BridgeException("MeasurementFormatUnsupported", "Each reading has at most one timestamp per declared clock; unknown time is explicit null.");
                    if (stamp["value"] is not null) _ = Time(stamp["value"]);
                }
            }
        }
        if (mode == "manualNativeExport")
        {
            var original = Object(measurement, "originalArtifact"); D.Required(original, "sha256", 64);
            _ = ProjectReviewStore.ReadArtifact(artifactsRoot, original, 32 * 1024 * 1024);
            D.Required(measurement, "conversion", 2000); D.Required(measurement, "settingsReadbackEvidence", 4096);
        }
        var imported = new JsonObject { ["schemaVersion"] = "trace-import.v2", ["tracePlanId"] = traceId,
            ["inputArtifact"] = artifact.DeepClone(), ["measurement"] = measurement.DeepClone(),
            ["qualityAssessment"] = Assess(measurement, plan) };
        D.Bound(imported, DiagnosticStore.MaxBytes);
        return imported;
    }

    internal JsonObject Read(string measurementId)
    {
        var retained = new DiagnosticStore(artifactsRoot).Read(measurementId, "trace-import.v2");
        var derived = Normalize(D.Required(retained, "tracePlanId", 64), Object(retained, "inputArtifact"));
        Same(retained, derived, "v2 normalization");
        return retained;
    }

    internal static bool Usable(JsonNode? read) => read is JsonObject r && D.Text(r, "status") == "good" &&
        r["value"] is not null && r["error"] is null;
    internal static JsonObject? Stamp(JsonNode? reading, string clock) => D.Rows(reading?["timestamps"])
        .OfType<JsonObject>().SingleOrDefault(s => D.Text(s, "clockId") == clock);
    internal static decimal? Expected(JsonNode m, string clock, string epoch) => D.Rows(m["expectedIntervals"])
        .OfType<JsonObject>().Where(i => D.Text(i, "clockId") == clock && D.Text(i, "epoch") == epoch)
        .Select(i => (decimal?)Number(i["interval"])).SingleOrDefault();
    internal static decimal Seconds(JsonNode m, string clock, JsonNode value)
    {
        var domain = D.Rows(m["clocks"]).Single(c => D.Text(c, "id") == clock)!;
        decimal divisor = D.Text(domain, "timeUnit") switch { "s" => 1, "ms" => 1000, "us" => 1000000, _ => 1000000000 };
        decimal raw = Time(value), seconds = raw / divisor;
        if (seconds * divisor != raw) throw new BridgeException("MeasurementFormatUnsupported", "Clock conversion would lose exact precision.");
        return seconds;
    }
    internal static JsonObject Assess(JsonObject m, JsonObject plan)
    {
        int absent = 0, good = 0, bad = 0, uncertain = 0, missing = 0, nullValues = 0, errors = 0, untimed = 0;
        long sequenceGaps = 0, previous = -1;
        foreach (var sample in D.Rows(m["samples"]))
        {
            long sequence = sample!["sequence"]!.GetValue<long>();
            if (previous >= 0) sequenceGaps += sequence - previous - 1;
            previous = sequence;
            var readings = Object(sample, "readings");
            absent += D.Rows(plan["signals"]).Count - readings.Count;
            foreach (var r in readings.Select(p => p.Value!))
            {
                switch (D.Text(r, "status")) { case "good": good++; break; case "bad": bad++; break; case "uncertain": uncertain++; break; default: missing++; break; }
                if (r["value"] is null) nullValues++;
                if (r["error"] is not null) errors++;
                if (!D.Rows(r["timestamps"]).Any(t => t?["value"] is not null)) untimed++;
            }
        }
        var series = new JsonArray();
        foreach (var signal in D.Rows(plan["signals"]))
            foreach (var clock in D.Rows(m["clocks"]))
                series.Add(Series(m, D.Text(signal, "id"), D.Text(clock, "id"), null, null, null, null));
        return new JsonObject { ["sampleGroups"] = D.Rows(m["samples"]).Count, ["absentReadings"] = absent,
            ["goodStatusReadings"] = good, ["badStatusReadings"] = bad, ["uncertainStatusReadings"] = uncertain,
            ["missingStatusReadings"] = missing, ["nullValues"] = nullValues, ["errorReadings"] = errors,
            ["untimedReadings"] = untimed, ["sequenceGaps"] = sequenceGaps, ["capture"] = m["capture"]!.DeepClone(),
            ["suppliedQuality"] = m["quality"]!.DeepClone(), ["series"] = series,
            ["qualityOrigin"] = "unverifiedSuppliedObservationsAndStructuralChecks", ["completenessVerified"] = false };
    }

    // Windows select a clock and epoch explicitly. Every boundary clears the previous observation.
    internal static JsonObject Series(JsonObject m, string signal, string clock, string? epoch,
        decimal? anchor, decimal? start, decimal? end)
    {
        int count = 0, usable = 0, unknown = 0, trueCount = 0, falseCount = 0, resets = 0, duplicates = 0, epochChanges = 0,
            irregular = 0, gaps = 0, changes = 0, excluded = 0, untimed = 0;
        decimal? min = null, max = null, firstTime = null, lastTime = null, priorTime = null;
        string? priorEpoch = null; JsonNode? priorValue = null; long? priorSequence = null;
        var transitions = new JsonArray(); var statuses = new JsonObject { ["good"] = 0, ["bad"] = 0, ["uncertain"] = 0, ["missing"] = 0, ["absent"] = 0 };
        var exclusions = new JsonObject { ["absentReading"] = 0, ["clockNotRecorded"] = 0, ["unknownTime"] = 0, ["otherEpoch"] = 0, ["outsideWindow"] = 0 };
        foreach (var sample in D.Rows(m["samples"]))
        {
            long sequence = sample!["sequence"]!.GetValue<long>();
            var read = sample["readings"]?[signal]; var stamp = Stamp(read, clock);
            string currentEpoch = stamp is null ? "" : D.Text(stamp, "epoch");
            decimal? raw = stamp?["value"] is null ? null : Time(stamp["value"]);
            decimal? selectedTime = raw is null ? null : anchor is null ? raw : TraceNumber.SubtractTime(Seconds(m, clock, stamp!["value"]!), anchor.Value);
            bool selected = epoch is null || currentEpoch == epoch;
            if (start is not null) selected &= selectedTime is not null && selectedTime >= start && selectedTime <= end;
            if (!selected)
            {
                excluded++;
                string reason = read is null ? "absentReading" : stamp is null ? "clockNotRecorded" :
                    epoch is not null && currentEpoch != epoch ? "otherEpoch" : raw is null ? "unknownTime" : "outsideWindow";
                exclusions[reason] = exclusions[reason]!.GetValue<int>() + 1;
                priorValue = null; priorTime = null; priorSequence = null; priorEpoch = null; continue;
            }
            count++;
            if (raw is null) untimed++;
            string status = read is null ? "absent" : D.Text(read, "status"); statuses[status] = statuses[status]!.GetValue<int>() + 1;
            bool good = Usable(read);
            if (good)
            {
                usable++; var value = read!["value"]!;
                if (value is JsonValue v && v.TryGetValue<bool>(out bool flag)) { if (flag) trueCount++; else falseCount++; }
                else { decimal n = Number(value); min = min is null ? n : Math.Min(min.Value, n); max = max is null ? n : Math.Max(max.Value, n); }
            }
            else unknown++;
            if (selectedTime is not null) { firstTime ??= selectedTime; lastTime = selectedTime; }
            bool adjacent = good && raw is not null && priorTime is not null && priorValue is not null && priorSequence + 1 == sequence;
            if (priorEpoch is not null && stamp is not null && priorEpoch != currentEpoch) { epochChanges++; adjacent = false; }
            decimal? expected = Expected(m, clock, currentEpoch);
            if (raw is not null && priorTime is not null && currentEpoch == priorEpoch)
            {
                decimal delta = TraceNumber.SubtractTime(raw.Value, priorTime.Value);
                if (delta < 0) { resets++; adjacent = false; }
                else if (delta == 0) { duplicates++; adjacent = false; }
                if (delta > 0 && expected is not null && delta != expected)
                { irregular++; if (delta > expected) gaps++; adjacent = false; }
                if (expected is null) adjacent = false;
            }
            if (adjacent && !TraceMeasurements.Equal(priorValue!, read!["value"]!))
            {
                changes++;
                if (transitions.Count < 200) transitions.Add(new JsonObject { ["clockId"] = clock, ["epoch"] = currentEpoch,
                    ["afterTime"] = priorTime, ["byTime"] = raw, ["before"] = priorValue!.DeepClone(), ["after"] = read!["value"]!.DeepClone() });
            }
            priorValue = good && raw is not null ? read!["value"] : null;
            priorTime = raw; priorEpoch = stamp is null ? null : currentEpoch; priorSequence = sequence;
        }
        return new JsonObject { ["signalId"] = signal, ["clockId"] = clock, ["epoch"] = epoch,
            ["selectedReadings"] = count, ["excludedReadings"] = excluded, ["usableValueReadings"] = usable,
            ["selectionExclusions"] = exclusions, ["untimedSelectedReadings"] = untimed,
            ["unknownValueReadings"] = unknown, ["statusCounts"] = statuses, ["trueReadings"] = trueCount, ["falseReadings"] = falseCount,
            ["minimum"] = min, ["maximum"] = max, ["firstRecordedTime"] = firstTime, ["lastRecordedTime"] = lastTime,
            ["clockResets"] = resets, ["duplicateTimes"] = duplicates, ["epochChanges"] = epochChanges,
            ["irregularIntervals"] = irregular, ["intervalGaps"] = gaps, ["adjacentValueChanges"] = transitions,
            ["adjacentValueChangeCount"] = changes, ["changesTruncated"] = changes > 200,
            ["transitionSemantics"] = "Recorded endpoints only; no interpolation, rate or event-time inference. Exact expected intervals, consecutive sequence and good observations required.",
            ["coverage"] = count == 0 ? "noSelectedReadings" : "recordedObservationsOnly" };
    }

    internal static JsonArray Findings(JsonObject m, JsonObject plan, Func<JsonNode, string, bool>? selected = null)
    {
        var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var findings = new JsonArray();
        foreach (var hypothesis in D.Rows(plan["hypotheses"]))
        {
            int matched = 0, contradicted = 0, unknown = 0;
            if (hypothesis?["prediction"] is JsonObject rule)
            {
                var when = TraceMeasurements.Conditions(rule["when"], signals); var expect = TraceMeasurements.Conditions(rule["expect"], signals);
                foreach (var condition in when.Concat(expect))
                    if (!D.Text(signals[D.Text(condition, "signalId")], "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase) &&
                        D.Text(m["units"], D.Text(condition, "signalId")) != D.Text(condition, "unit"))
                        throw new BridgeException("MeasurementLineageMismatch", "Prediction units must match exactly.");
                foreach (var sample in D.Rows(m["samples"]))
                {
                    if (selected is not null && !when.Concat(expect).Select(c => D.Text(c, "signalId")).Distinct(StringComparer.Ordinal)
                        .Any(signal => selected(sample!, signal))) continue;
                    var values = new JsonObject();
                    foreach (string signal in signals.Keys)
                    {
                        var read = sample!["readings"]?[signal];
                        values[signal] = Usable(read) && (selected is null || selected(sample, signal)) ? read!["value"]!.DeepClone() : null;
                    }
                    bool? applies = TraceMeasurements.All(when, values);
                    if (applies == false) continue;
                    bool? observed = applies is null ? null : TraceMeasurements.All(expect, values);
                    if (observed is null) unknown++; else if (observed.Value) matched++; else contradicted++;
                }
            }
            findings.Add(new JsonObject { ["id"] = hypothesis!["id"]!.DeepClone(), ["statement"] = hypothesis["statement"]!.DeepClone(),
                ["matchedSamples"] = matched, ["contradictedSamples"] = contradicted, ["unknownSamples"] = unknown,
                ["verdict"] = "unresolved", ["causationProven"] = false,
                ["reason"] = "Counts describe supplied sequence groups, not verified simultaneous states. Source identity, acquisition quality and timing remain unverified." });
        }
        return findings;
    }

    internal JsonObject BuildAnalysis(string measurementId)
    {
        var imported = Read(measurementId); var m = Object(imported, "measurement"); var plan = Plan(D.Required(imported, "tracePlanId", 64));
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-report.v2", ["measurementId"] = measurementId,
            ["tracePlanId"] = imported["tracePlanId"]!.DeepClone(), ["reviewId"] = plan["reviewId"]!.DeepClone(),
            ["inputArtifact"] = imported["inputArtifact"]!.DeepClone(), ["hypotheses"] = Findings(m, plan),
            ["qualityAssessment"] = Assess(m, plan), ["clocks"] = m["clocks"]!.DeepClone(), ["capture"] = m["capture"]!.DeepClone(),
            ["acquisitionContext"] = Context(m), ["coverageGaps"] = plan["coverageGaps"]!.DeepClone() };
        Authority(report); D.Bound(report, DiagnosticStore.MaxBytes); return report;
    }
    internal static JsonObject Context(JsonObject m) => new() { ["mode"] = m["mode"]!.DeepClone(),
        ["programIdentity"] = m["programIdentity"]!.DeepClone(), ["clocks"] = m["clocks"]!.DeepClone(),
        ["suppliedQuality"] = m["quality"]!.DeepClone(), ["identityVerified"] = false, ["rowGroupingVerified"] = false };
}
