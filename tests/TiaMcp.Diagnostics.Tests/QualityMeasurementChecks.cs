using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class QualityMeasurementChecks
{
    internal static JsonObject? Samples;
    internal static JsonObject Capture(Fixture f, string trace, int variant = 0)
    {
        var legacy = f.Capture(trace); var samples = new JsonArray();
        decimal?[] times = [-10, 0, 10, 30, 20, 20, 0, 10, 20, 30, null, 50];
        for (int index = 0; index < times.Length; index++)
        {
            var readings = new JsonObject(); int offset = 0;
            foreach (var pair in legacy["samples"]![0]!["values"]!.AsObject())
            {
                if (pair.Key == "final" && index == 8) continue;
                JsonNode? value = pair.Value!.DeepClone();
                if (pair.Key == "final") value = index is 0 or 6 or 9;
                if (pair.Key == "override") value = true;
                string status = pair.Key == "final" ? index switch { 2 => "bad", 3 => "uncertain", 5 => "missing", _ => "good" } : "good";
                if (status == "missing") value = null;
                readings[pair.Key] = new JsonObject { ["value"] = value, ["status"] = status,
                    ["statusCode"] = status == "good" ? "0x00000000" : "raw </td><script>alert('quality')</script> " + status,
                    ["error"] = pair.Key == "final" && index == 11 ? "transport unavailable" : null,
                    ["timestamps"] = new JsonArray(
                        new JsonObject { ["clockId"] = "source", ["epoch"] = index < 6 ? "boot-0" : "boot-1", ["value"] = times[index] },
                        new JsonObject { ["clockId"] = "host", ["epoch"] = "session", ["value"] = index * 100m + offset++ }) };
            }
            samples.Add(new JsonObject { ["sequence"] = index, ["readings"] = readings });
        }
        return new JsonObject { ["schemaVersion"] = "trace-measurement.v2", ["tracePlanId"] = trace, ["reviewId"] = f.ReviewId,
            ["mode"] = "synthetic", ["programIdentity"] = "unverified original fixture", ["units"] = legacy["units"]!.DeepClone(),
            ["clocks"] = new JsonArray(new JsonObject { ["id"] = "source", ["label"] = "device </style><img src='https://invalid.test/a'> clock",
                ["kind"] = "source", ["timeUnit"] = "ms" }, new JsonObject { ["id"] = "host", ["label"] = "sequential receive clock", ["kind"] = "host", ["timeUnit"] = "ms" }),
            ["quality"] = new JsonObject { ["atomicAcrossSignals"] = false, ["scanSynchronized"] = false, ["programMatchVerified"] = false },
            ["capture"] = new JsonObject { ["state"] = "truncated", ["reason"] = "Original truncation marker " + variant, ["reportedDroppedSamples"] = null },
            ["expectedIntervals"] = new JsonArray(
                new JsonObject { ["clockId"] = "source", ["epoch"] = "boot-0", ["interval"] = 10 },
                new JsonObject { ["clockId"] = "source", ["epoch"] = "boot-1", ["interval"] = 10 },
                new JsonObject { ["clockId"] = "host", ["epoch"] = "session", ["interval"] = 100 }), ["samples"] = samples };
    }
    internal static JsonObject Comparison() => new() { ["reason"] = "Explicit original source-clock epoch, no clock synchronization.",
        ["beforeClockId"] = "source", ["beforeEpoch"] = "boot-0", ["afterClockId"] = "source", ["afterEpoch"] = "boot-0",
        ["beforeAnchorSeconds"] = 0, ["afterAnchorSeconds"] = 0, ["windowStartSeconds"] = -0.01m, ["windowEndSeconds"] = 0.03m };
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("Quality check " + (checks + 1) + ": " + label); checks++; }
        void Reject(string code, Action action)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode r) => r["id"]!.GetValue<string>();
        using var f = new Fixture(); var store = new DiagnosticStore(f.Root); var experiments = new DiagnosticExperiments(f.Root);
        string experiment = Id(experiments.Plan(Id(f.Plan()), ExperimentChecks.Request(f)));
        string trace = store.Read(experiment)["tracePlanId"]!.GetValue<string>();
        var m = Capture(f, trace);
        byte[] raw = Encoding.UTF8.GetBytes(" \r\n" + m.ToJsonString() + "\r\n");
        var source = f.Artifact(raw, ".json"); string imported = Id(f.Measurements.Import(trace, source));
        var owned = store.Read(imported)["inputArtifact"]!;
        Check(File.ReadAllBytes(owned["path"]!.GetValue<string>()).SequenceEqual(raw), "Exact whitespace and original JSON retained");
        File.WriteAllText(source["path"]!.GetValue<string>(), "external source moved after independent retention");
        var analysis = f.Measurements.Analyze(imported); var report = analysis["report"]!;
        var final = report["qualityAssessment"]!["series"]!.AsArray().Single(s => (string?)s!["signalId"] == "final" && (string?)s["clockId"] == "source")!;
        Check(final["usableValueReadings"]!.GetValue<int>() == 7 && final["unknownValueReadings"]!.GetValue<int>() == 5, "Independent final usable/unknown counts");
        Check(final["trueReadings"]!.GetValue<int>() == 3 && final["falseReadings"]!.GetValue<int>() == 4, "No zero or bad-value substitution");
        Check(final["clockResets"]!.GetValue<int>() == 1 && final["duplicateTimes"]!.GetValue<int>() == 1, "Reset and duplicate retained distinctly");
        Check(final["epochChanges"]!.GetValue<int>() == 1 && final["intervalGaps"]!.GetValue<int>() == 1, "Explicit epoch and irregular gap");
        Check(final["adjacentValueChangeCount"]!.GetValue<int>() == 2, "Only two good exact-interval adjacent changes");
        Check(report["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 4 && report["hypotheses"]![0]!["unknownSamples"]!.GetValue<int>() == 5, "Useful observation counts");
        Check((string?)report["hypotheses"]![0]!["verdict"] == "unresolved", "Supplied quality cannot grant behavioral verdict");
        Check((string?)report["capture"]!["state"] == "truncated" && report["capture"]!["reportedDroppedSamples"] is null, "Unknown drops and truncation survive");
        string after = Id(f.Import(trace, Capture(f, trace, 1)));
        var comparison = new TraceComparison(f.Root).Compare(imported, after, Comparison());
        Check((string?)comparison["comparison"]!["schemaVersion"] == "trace-comparison.v2", "Versioned comparison");
        Check(comparison["comparison"]!["beforeWindow"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 2, "Window observes only selected clock/epoch");
        Check(comparison["comparison"]!["beforeWindow"]!["hypotheses"]![0]!["unknownSamples"]!.GetValue<int>() == 3, "Excluded epochs are not unknown samples");
        var reviewRequest = ExperimentChecks.ReviewRequest(experiment, imported, after); reviewRequest["comparison"] = Comparison();
        var reviewed = experiments.Review(experiment, reviewRequest);
        Check((string?)reviewed["review"]!["schemaVersion"] == "diagnostic-experiment-review.v2", "v2 experiment composition");
        var pairs = new JsonArray();
        for (int run = 0; run < 2; run++)
        {
            var pair = reviewRequest.DeepClone().AsObject(); pair["beforeExperimentId"] = experiment; pair["id"] = "run-" + run;
            pair["beforeMeasurementId"] = run == 0 ? imported : Id(f.Import(trace, Capture(f, trace, 2)));
            pair["afterMeasurementId"] = run == 0 ? after : Id(f.Import(trace, Capture(f, trace, 3))); pairs.Add(pair);
        }
        var repeatedRequest = new JsonObject { ["reason"] = "Two explicitly selected original observation pairs.", ["pairs"] = pairs };
        var repeated = new DiagnosticRepeatedExperiments(f.Root).Compare(repeatedRequest);
        Check((string?)repeated["report"]!["schemaVersion"] == "diagnostic-experiment-repeat.v2", "v2 repeated composition");
        Check(repeated["report"]!["hypotheses"]![0]!["before"]!["unresolvedRuns"]!.GetValue<int>() == 2, "Repeated poor evidence remains unresolved");
        foreach (var (id, experimentReport) in new[] { (Id(analysis), false), (Id(comparison), false), (Id(reviewed), true), (Id(repeated), true) })
            foreach (string format in new[] { "json", "markdown", "html" })
            {
                var exported = experimentReport ? new DiagnosticExperimentReportExport(f.Root).Export(id, format) : new DiagnosticReportExport(f.Root).Export(id, format);
                Check((string?)exported["schemaVersion"] == "diagnostic-quality-report-export.v2", "Versioned complete export " + format);
                string file = exported["artifact"]!["path"]!.GetValue<string>(); string text = File.ReadAllText(file);
                if (format == "json")
                {
                    var combined = JsonNode.Parse(text)!;
                    Check(Convert.FromBase64String(combined["sourceReportBase64"]!.GetValue<string>()).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root, "trace-diagnostics", id + ".json"))), "Exact report original bytes");
                    foreach (var measurement in combined["measurements"]!.AsArray())
                    {
                        byte[] original = Convert.FromBase64String(measurement!["original"]!["base64"]!.GetValue<string>());
                        Check(Convert.ToHexStringLower(SHA256.HashData(original)) == measurement["original"]!["sha256"]!.GetValue<string>(), "Independent original hash");
                        Check(JsonNode.DeepEquals(JsonNode.Parse(original), measurement["document"]!["measurement"]), "Complete original readings, statuses and times");
                    }
                }
                if (format == "html") Check(!text.Contains("<script>") && !text.Contains("<img ") && text.Contains("&lt;script&gt;"), "Hostile labels inert");
            }
        Check(Id(new TraceMeasurements(f.Root).Analyze(imported)) == Id(analysis), "Analysis stable after store recreation");
        Check(Id(new DiagnosticExperiments(f.Root).Review(experiment, reviewRequest)) == Id(reviewed), "Review restart stable");
        Check(Id(new DiagnosticRepeatedExperiments(f.Root).Compare(repeatedRequest)) == Id(repeated), "Repeated restart stable");
        foreach (var mutate in new Action<JsonObject>[] {
            x => x["samples"]![0]!["readings"]!["final"]!["status"] = "invented",
            x => x["samples"]![0]!["readings"]!["final"]!["timestamps"]![0]!["clockId"] = "absent",
            x => x["samples"]![1]!["sequence"] = 0,
            x => x["samples"]![0]!["readings"]!["step"]!["value"] = 1.5m,
            x => x["clocks"]!.AsArray().Add(x["clocks"]![0]!.DeepClone()) })
        {
            var invalid = m.DeepClone().AsObject(); mutate(invalid);
            int before = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length;
            Reject("MeasurementFormatUnsupported", () => f.Import(trace, invalid));
            Check(before == Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length, "Invalid request publishes no retained child");
        }
        var empty = m.DeepClone().AsObject(); empty["samples"] = new JsonArray(); empty["capture"]!["state"] = "incomplete";
        Check(f.Measurements.Analyze(Id(f.Import(trace, empty)))["report"]!["qualityAssessment"]!["sampleGroups"]!.GetValue<int>() == 0, "Representable empty incomplete capture");
        var rawBad = m.DeepClone().AsObject(); rawBad["samples"]![0]!["readings"]!["step"]!["status"] = "bad"; rawBad["samples"]![0]!["readings"]!["step"]!["value"] = "raw undecodable value";
        Check((string?)store.Read(Id(f.Import(trace, rawBad)))["measurement"]!["samples"]![0]!["readings"]!["step"]!["value"] == "raw undecodable value", "Bad raw scalar preserved without numeric coercion");
        string legacy = Id(f.Import(trace, f.Capture(trace)));
        Reject("MeasurementVersionMismatch", () => new TraceComparison(f.Root).Compare(imported, legacy, Comparison()));
        var badClock = Comparison(); badClock["beforeClockId"] = "unknown";
        Reject("MeasurementClockMismatch", () => new TraceComparison(f.Root).Compare(imported, after, badClock));
        var badEpoch = Comparison(); badEpoch["beforeEpoch"] = "unknown";
        Reject("MeasurementClockMismatch", () => new TraceComparison(f.Root).Compare(imported, after, badEpoch));
        var secondEpoch = Comparison(); secondEpoch["beforeEpoch"] = "boot-1"; secondEpoch["windowEndSeconds"] = 0.05m;
        var selected = new TraceComparison(f.Root).Compare(imported, after, secondEpoch)["comparison"]!["beforeWindow"]!;
        var selectedFinal = selected["series"]!.AsArray().Single(s => (string?)s!["signalId"] == "final")!;
        Check(selectedFinal["selectionExclusions"]!["absentReading"]!.GetValue<int>() == 1 &&
              selectedFinal["selectionExclusions"]!["unknownTime"]!.GetValue<int>() == 1, "Window preserves absent and untimed exclusion counts");
        var epochFinal = selected["completeEpochAssessment"]!.AsArray().Single(s => (string?)s!["signalId"] == "final")!;
        Check(epochFinal["untimedSelectedReadings"]!.GetValue<int>() == 1 &&
              epochFinal["selectionExclusions"]!["absentReading"]!.GetValue<int>() == 1, "Complete epoch retains untimed observation and unassigned absence");
        var precise = m.DeepClone().AsObject();
        precise["samples"]![0]!["readings"]!["final"]!["timestamps"]![0]!["value"] = 0.0000000000000000000000000001m;
        string preciseId = Id(f.Import(trace, precise));
        Reject("MeasurementFormatUnsupported", () => new TraceComparison(f.Root).Compare(preciseId, after, Comparison()));
        foreach (string duplicate in new[] {
            m.ToJsonString().Replace("\"schemaVersion\":\"trace-measurement.v2\"", "\"schemaVersion\":\"trace-measurement.v2\",\"schemaVersion\":\"trace-measurement.v2\""),
            m.ToJsonString().Replace("\"status\":\"good\"", "\"status\":\"good\",\"status\":\"bad\""),
            m.ToJsonString().Replace("\"clockId\":\"source\"", "\"clockId\":\"source\",\"clockId\":\"host\"") })
            Reject("MeasurementFormatUnsupported", () => f.Measurements.Import(trace, f.Artifact(Encoding.UTF8.GetBytes(duplicate), ".json")));
        var attested = m.DeepClone().AsObject();
        foreach (string key in new[] { "atomicAcrossSignals", "scanSynchronized", "programMatchVerified" }) attested["quality"]![key] = true;
        attested["capture"]!["state"] = "complete"; attested["capture"]!["reportedDroppedSamples"] = 0;
        var attestedReport = f.Measurements.Analyze(Id(f.Import(trace, attested)))["report"]!;
        Check((string?)attestedReport["hypotheses"]![0]!["verdict"] == "unresolved" &&
              !attestedReport["atomicityVerified"]!.GetValue<bool>(), "Supplied complete and atomic attestations never grant authority");
        var changed = store.Read(imported); changed["measurement"]!["samples"]![0]!["readings"]!["final"]!["value"] = false;
        Reject("CaptureMismatch", () => f.Measurements.Analyze(Id(store.Save(changed))));
        var changedAnalysis = report.DeepClone().AsObject(); changedAnalysis["hypotheses"]![0]!["matchedSamples"] = 999;
        Reject("CaptureMismatch", () => new DiagnosticReportExport(f.Root).Export(Id(store.Save(changedAnalysis)), "html"));
        var changedComparison = comparison["comparison"]!.DeepClone().AsObject(); changedComparison["beforeWindow"]!["verdict"] = "supportedPrediction";
        Reject("CaptureMismatch", () => new DiagnosticReportExport(f.Root).Export(Id(store.Save(changedComparison)), "json"));
        byte[] originalBytes = File.ReadAllBytes(owned["path"]!.GetValue<string>());
        File.WriteAllText(owned["path"]!.GetValue<string>(), "changed retained original");
        Reject("ArtifactChanged", () => new TraceMeasurements(f.Root).Analyze(imported));
        File.WriteAllBytes(owned["path"]!.GetValue<string>(), originalBytes);
        var duplicatePairs = repeatedRequest.DeepClone().AsObject(); duplicatePairs["pairs"]![1]!["beforeMeasurementId"] = imported;
        Reject("ExperimentRepeatedInput", () => new DiagnosticRepeatedExperiments(f.Root).Compare(duplicatePairs));
        Samples = new JsonObject { ["measurement"] = m.DeepClone(), ["comparison"] = Comparison(), ["experimentRequest"] = ExperimentChecks.Request(f) };
        Console.WriteLine($"PASS quality measurement v2: {checks} original-byte, quality, clock, comparison, experiment, export and refusal checks; no native execution.");
    }
}
