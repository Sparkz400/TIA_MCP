using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class RepeatedExperimentChecks
{
    internal static JsonObject Pair(string id, string experiment, string before, string after) => new() {
        ["id"] = id, ["beforeExperimentId"] = experiment, ["afterExperimentId"] = experiment,
        ["beforeMeasurementId"] = before, ["afterMeasurementId"] = after, ["comparison"] = ExperimentChecks.Comparison() };
    private static string Id(JsonNode value) => value["id"]!.GetValue<string>();

    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new Exception("Repeated experiment check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        using var f = new Fixture(); AlarmBindingChecks.AddEvidence(f);
        var request = AlarmBindingChecks.Request(f); request["testSuiteFinding"] = ExperimentChecks.Finding(f);
        request["report"] = "Conveyor </td><script>fetch('https://invalid.example/')</script> [link](https://invalid.example) ` ```\n# hostile";
        var plan = f.Planner.Plan(request);
        var experiment = new DiagnosticExperiments(f.Root).Plan(Id(plan), ExperimentChecks.Request(f));
        string experimentId = Id(experiment), traceId = experiment["experiment"]!["tracePlanId"]!.GetValue<string>();
        string Capture(string label, Action<JsonObject>? edit = null)
        {
            var capture = f.Capture(traceId); capture["clock"] = "independently authored " + label;
            edit?.Invoke(capture); return Id(f.Import(traceId, capture));
        }
        string before1 = Capture("before one"), before2 = Capture("before two", m => {
            foreach (int index in new[] { 2, 3, 4 }) m["samples"]![index]!["values"]!["run"] = false;
        });
        string after1 = Capture("after one", m => { foreach (var sample in m["samples"]!.AsArray()) sample!["values"]!["final"] = true; });
        string after2 = Capture("after two", m => m["samples"]![0]!["values"]!["feedback"] = null);
        var repeats = new DiagnosticRepeatedExperiments(f.Root);
        var repeatedRequest = new JsonObject { ["reason"] = "Two separately authored paired observations; no independence or fix claim.",
            ["pairs"] = new JsonArray(Pair("first", experimentId, before1, after1), Pair("second", experimentId, before2, after2)) };
        string originalRequest = repeatedRequest.ToJsonString();
        var receipt = repeats.Compare(repeatedRequest); var report = receipt["report"]!;
        Check(originalRequest == repeatedRequest.ToJsonString());
        Check(report["runPairCount"]!.GetValue<int>() == 2);
        var aggregate = report["hypotheses"]![0]!;
        Check(aggregate["before"]!["supportedPredictionRuns"]!.GetValue<int>() == 1);
        Check(aggregate["before"]!["contradictedPredictionRuns"]!.GetValue<int>() == 1);
        Check(aggregate["before"]!["unresolvedRuns"]!.GetValue<int>() == 0);
        Check(aggregate["after"]!["unresolvedRuns"]!.GetValue<int>() == 2);
        Check(aggregate["after"]!["conditionObservedRuns"]!.GetValue<int>() == 1);
        Check(report["runs"]![0]!["beforeWindow"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 3);
        Check(report["runs"]![1]!["beforeWindow"]!["hypotheses"]![0]!["contradictedSamples"]!.GetValue<int>() == 3);
        Check(report["runs"]![1]!["afterWindow"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 3);
        Check(report["runs"]![1]!["afterWindow"]!["qualityGatePassed"]!.GetValue<bool>() == false);
        foreach (string field in new[] { "independentRunsVerified", "sourceAuthenticityVerified", "clockSynchronizationVerified", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted" })
            Check(report[field]!.GetValue<bool>() == false);
        var store = new DiagnosticStore(f.Root);
        var originals = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).ToDictionary(p => p, File.ReadAllBytes);
        Check(Id(new DiagnosticRepeatedExperiments(f.Root).Compare(repeatedRequest)) == Id(receipt));
        var exporter = new DiagnosticExperimentReportExport(f.Root);
        JsonObject? combined = null; string combinedId = "";
        foreach (string format in new[] { "json", "markdown", "html" })
        {
            var exported = exporter.Export(Id(receipt), format);
            byte[] bytes = File.ReadAllBytes(exported["artifact"]!["path"]!.GetValue<string>());
            string text = Encoding.UTF8.GetString(bytes);
            Check(exported["sourceEvidenceRevalidated"]!.GetValue<bool>() && exported["windowAnalysisRecomputed"]!.GetValue<bool>());
            Check((string?)exported["sourceArtifact"]!["sha256"] == Id(receipt));
            Check(Id(exported) == Id(new DiagnosticExperimentReportExport(f.Root).Export(Id(receipt), format)));
            if (format == "json")
            {
                combined = JsonNode.Parse(bytes)!.AsObject();
                combinedId = Id(exported);
                Check(JsonNode.DeepEquals(combined["sourceReport"], report));
                Check(combined["experiments"]!.AsArray().Count == 1 && combined["runs"]!.AsArray().Count == 2);
                Check(JsonNode.DeepEquals(combined["experiments"]![0]!["document"], experiment["experiment"]));
                Check((string?)combined["runs"]![0]!["beforeReport"]!["hypotheses"]![0]!["verdict"] == "supportedPrediction");
                Check(combined["experiments"]![0]!["document"]!["diagnosticCase"]!["alarmBindings"]!["bindings"]!.AsArray().Count == 3);
                Check(combined["experiments"]![0]!["document"]!["diagnosticCase"]!["testSuiteEvidence"]!["finding"]!["finding"]!["warningCount"]!.GetValue<int>() == 2);
            }
            else
            {
                Check(text.Contains("Complete combined JSON") && text.Contains("Window verdict") && text.Contains("Explicit lineage"));
                Check(text.Contains("Recorded samples", StringComparison.OrdinalIgnoreCase) || text.Contains("recorded samples", StringComparison.OrdinalIgnoreCase));
                if (format == "html")
                {
                    Check(!text.Contains("<script", StringComparison.OrdinalIgnoreCase) && !text.Contains("<a ", StringComparison.OrdinalIgnoreCase));
                    Check(text.Contains("&lt;script&gt;") && text.Contains("&lt;/td&gt;"));
                }
                else
                {
                    string readable = text[..text.IndexOf("Complete combined JSON", StringComparison.Ordinal)];
                    Check(!readable.Contains("[link](https://invalid.example)") && readable.Contains("\\[link\\]\\(https&#58;//invalid&#46;example\\)"));
                }
            }
        }
        foreach (var file in originals) Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value));
        // Existing single-pair v1 reports export as complete combined documents without rewriting originals.
        string singleId = report["runs"]![0]!["reviewId"]!.GetValue<string>();
        byte[] priorSingle = File.ReadAllBytes(Path.Combine(f.Root, "trace-diagnostics", singleId + ".json"));
        var singleExport = exporter.Export(singleId, "json");
        var singleCombined = JsonNode.Parse(File.ReadAllBytes(singleExport["artifact"]!["path"]!.GetValue<string>()))!;
        Check(singleCombined["runs"]!.AsArray().Count == 1 && (string?)singleCombined["sourceSchemaVersion"] == "diagnostic-experiment-review.v1");
        Check(priorSingle.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root, "trace-diagnostics", singleId + ".json"))));
        // Every quality defect is still an observation; none can be voted into a successful verdict.
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
            ("non-atomic", m => m["quality"]!["atomicAcrossSignals"] = false),
            ("unknown dropouts", m => m["quality"]!["dropoutsKnown"] = false),
            ("program unknown", m => m["quality"]!["programMatchVerified"] = false),
            ("reported dropout", m => m["quality"]!["reportedDroppedSamples"] = 1),
            ("missing interval", m => m["quality"]!.AsObject().Remove("expectedInterval")),
            ("gap", m => m["samples"]!.AsArray().RemoveAt(1)),
            ("partial window", m => m["samples"]!.AsArray().RemoveAt(0)) })
        {
            var changed = repeatedRequest.DeepClone().AsObject();
            changed["pairs"]![1]!["afterMeasurementId"] = Capture(name, mutate);
            var window = repeats.Compare(changed)["report"]!["runs"]![1]!["afterWindow"]!;
            Check(!window["qualityGatePassed"]!.GetValue<bool>() && (string?)window["hypotheses"]![0]!["verdict"] == "unresolved");
            Check(window["qualityGateReasons"]!.AsArray().Count > 0);
            if (name == "partial window") Check((string?)window["coverage"] == "partialRecordedSpan");
        }
        // The full-capture event is outside the requested window; it cannot count as a window match.
        var outside = repeatedRequest.DeepClone().AsObject();
        foreach (var pair in outside["pairs"]!.AsArray())
        { pair!["comparison"]!["beforeAnchorSeconds"] = 0; pair["comparison"]!["afterAnchorSeconds"] = 0; pair["comparison"]!["windowStartSeconds"] = 0; pair["comparison"]!["windowEndSeconds"] = 0.01m; }
        var outsideReport = repeats.Compare(outside)["report"]!;
        Check(outsideReport["runs"]![0]!["beforeWindow"]!["samplesInWindow"]!.GetValue<int>() == 2);
        Check(outsideReport["runs"]![0]!["beforeWindow"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 0);
        Check((string?)outsideReport["runs"]![0]!["beforeWindow"]!["hypotheses"]![0]!["verdict"] == "unresolved");
        foreach (var pair in outside["pairs"]!.AsArray())
        { pair!["comparison"]!["windowStartSeconds"] = 1; pair["comparison"]!["windowEndSeconds"] = 2; }
        Check((string?)repeats.Compare(outside)["report"]!["runs"]![0]!["beforeWindow"]!["coverage"] == "noRecordedSamples");
        foreach (var (code, mutate) in new (string, Action<JsonObject>)[] {
            ("InvalidRequest", r => r["ignored"] = true),
            ("InvalidRequest", r => r["pairs"]!.AsArray().RemoveAt(1)),
            ("InvalidRequest", r => r["pairs"]![1]!["id"] = "first"),
            ("InvalidRequest", r => r["pairs"]![0]!["ignored"] = true),
            ("ExperimentRepeatedInput", r => r["pairs"]![1]!["beforeMeasurementId"] = before1),
            ("ExperimentRepeatedInput", r => r["pairs"]![0]!["afterMeasurementId"] = before1),
            ("ExperimentWindowMismatch", r => r["pairs"]![1]!["comparison"]!["windowEndSeconds"] = 0.04m),
            ("MeasurementLineageMismatch", r => r["pairs"]![1]!["comparison"]!["signalMap"] = new JsonArray(new JsonObject { ["before"] = "final", ["after"] = "final" })) })
        {
            var invalid = repeatedRequest.DeepClone().AsObject(); mutate(invalid);
            var paths = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).ToHashSet(StringComparer.Ordinal);
            Reject(code, () => repeats.Compare(invalid));
            Check(paths.SetEquals(Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics"))));
        }
        foreach (var mutate in new Action<JsonObject>[] {
            r => r["hypotheses"]![0]!["after"]!["supportedPredictionRuns"] = 2,
            r => r["runs"]![0]!["beforeWindow"]!["samplesInWindow"] = 999,
            r => r["bugFixProven"] = true, r => r["independentRunsVerified"] = true })
        {
            var forged = report.DeepClone().AsObject(); mutate(forged); string id = Id(store.Save(forged));
            foreach (string format in new[] { "json", "markdown", "html" }) Reject("CaptureMismatch", () => exporter.Export(id, format));
        }
        var originalImport = store.Read(before1);
        string inputPath = originalImport["inputArtifact"]!["path"]!.GetValue<string>(); byte[] originalBytes = File.ReadAllBytes(inputPath);
        File.WriteAllText(inputPath, "tampered original");
        Reject("ArtifactChanged", () => exporter.Export(Id(receipt), "html"));
        File.WriteAllBytes(inputPath, originalBytes);
        File.Delete(inputPath);
        Reject("ArtifactUnavailable", () => exporter.Export(Id(receipt), "json"));
        File.WriteAllBytes(inputPath, originalBytes);
        Check(Id(exporter.Export(Id(receipt), "json")) == combinedId);
        // Rewrapping the same original input cannot manufacture a further repeat.
        var historical = store.Read(before1); historical["qualityAssessment"]!["detectedIntervalGaps"] = 999;
        var wrapped = repeatedRequest.DeepClone().AsObject(); wrapped["pairs"]![1]!["beforeMeasurementId"] = Id(store.Save(historical));
        Reject("ExperimentRepeatedInput", () => repeats.Compare(wrapped));
        // Exact direct instance replans still need explicit per-pair signal mappings.
        var nextRequest = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"),
            ["instance"] = new JsonObject { ["dbPath"] = Fixture.Block("DB_Line"), ["memberPath"] = new JsonArray("Conv1") },
            ["reason"] = "Explicit second-instance cohort; no fix claim.",
            ["signalMappings"] = new JsonArray(plan["plan"]!["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject {
                ["id"] = s!["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(),
                ["memberPath"] = new JsonArray(s["memberPath"]!.AsArray().Select(p => (JsonNode?)JsonValue.Create((string?)p == "Conv2" ? "Conv1" : (string?)p)).ToArray()),
                ["reason"] = "Corresponding explicit role on Conveyor 1." }).ToArray()) };
        var next = new DiagnosticReplanner(f.Root).Replan(Id(plan), nextRequest);
        var nextExperiment = new DiagnosticExperiments(f.Root).Plan(Id(next), ExperimentChecks.Request(f));
        string nextTrace = nextExperiment["experiment"]!["tracePlanId"]!.GetValue<string>();
        var mapped = repeatedRequest.DeepClone().AsObject();
        foreach (var pair in mapped["pairs"]!.AsArray())
        {
            var capture = f.Capture(nextTrace); capture["clock"] = "second instance " + (string?)pair!["id"];
            pair!["afterExperimentId"] = Id(nextExperiment); pair["afterMeasurementId"] = Id(f.Import(nextTrace, capture));
        }
        Reject("MeasurementLineageMismatch", () => repeats.Compare(mapped));
        foreach (var pair in mapped["pairs"]!.AsArray())
            pair!["comparison"]!["signalMap"] = new JsonArray(plan["plan"]!["signals"]!.AsArray()
                .Select(s => (JsonNode?)new JsonObject { ["before"] = s!["id"]!.DeepClone(), ["after"] = s["id"]!.DeepClone() }).ToArray());
        var mappedReport = repeats.Compare(mapped);
        var mappedExport = exporter.Export(Id(mappedReport), "json");
        var mappedCombined = JsonNode.Parse(File.ReadAllBytes(mappedExport["artifact"]!["path"]!.GetValue<string>()))!;
        Check(mappedCombined["experiments"]!.AsArray().Count == 2);
        Check(mappedCombined["runs"]!.AsArray().All(r => (string?)r!["review"]!["lineage"]!["kind"] == "explicitDirectReplan"));
        Check((string?)mappedCombined["experiments"]![1]!["document"]!["diagnosticCase"]!["selectedInstance"]!["memberPath"]![0] == "Conv1");
        var mixed = repeatedRequest.DeepClone().AsObject(); mixed["pairs"]![1] = mapped["pairs"]![1]!.DeepClone();
        Reject("ExperimentLineageMismatch", () => repeats.Compare(mixed));
        Console.WriteLine($"PASS repeated diagnostic experiments: {checks} checks; exact original sources and window quality, no native or causal qualification.");
    }
}
