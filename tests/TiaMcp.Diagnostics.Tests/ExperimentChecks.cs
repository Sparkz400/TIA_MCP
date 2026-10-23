using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class ExperimentChecks
{
    internal static JsonObject Request(Fixture f) => new() { ["reason"] = "Test the separately authored conveyor output-writer prediction.",
        ["watch"] = new JsonObject { ["tableName"] = "Conveyor experiment", ["tiaVersion"] = 21,
            ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Observe enable actual and final output beside the selected FB instance." }) },
        ["trace"] = f.Settings() };
    internal static JsonObject Comparison() => new() { ["reason"] = "Compare explicit event windows without inferring clock synchronization.",
        ["beforeAnchorSeconds"] = 0.02m, ["afterAnchorSeconds"] = 0.02m, ["windowStartSeconds"] = -0.02m, ["windowEndSeconds"] = 0.03m };
    internal static JsonObject ReviewRequest(string experiment, string before, string after) => new() {
        ["afterExperimentId"] = experiment, ["beforeMeasurementId"] = before, ["afterMeasurementId"] = after, ["comparison"] = Comparison() };
    internal static string Id(JsonNode value) => value["id"]!.GetValue<string>();
    internal static JsonObject Finding(Fixture f)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(SuppliedTestSuiteChecks.Packet(f), BridgeProtocol.Json);
        var uploads = new ArtifactUploadStore(f.Root);
        var begun = uploads.Begin("experiment-results.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        uploads.Write(begun.UploadId, 0, Convert.ToBase64String(bytes));
        var imported = new ProjectReviewStore(f.Root).ImportTestSuiteResults(f.ReviewId, uploads.Commit(begun.UploadId).ArtifactRef!);
        uploads.Release(begun.UploadId);
        var selector = imported["receipt"]!.DeepClone().AsObject(); selector["index"] = 0; return selector;
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Experiment check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        using var f = new Fixture(); AlarmBindingChecks.AddEvidence(f);
        var request = AlarmBindingChecks.Request(f); request["testSuiteFinding"] = Finding(f);
        var diagnostic = f.Planner.Plan(request); string caseId = Id(diagnostic);
        var store = new DiagnosticStore(f.Root); var experiments = new DiagnosticExperiments(f.Root);
        var prepare = Request(f); string input = prepare.ToJsonString();
        var receipt = experiments.Plan(caseId, prepare); string experimentId = Id(receipt);
        var experiment = receipt["experiment"]!.AsObject(); string traceId = experiment["tracePlanId"]!.GetValue<string>();
        Check(input == prepare.ToJsonString());
        Check(experiment["compositionGaps"]!.AsArray().Count == 0);
        Check(JsonNode.DeepEquals(diagnostic["plan"], experiment["diagnosticCase"]));
        Check(experiment["diagnosticCase"]!["testSuiteEvidence"]!["finding"]!["finding"]!["warningCount"]!.GetValue<int>() == 2);
        Check(experiment["diagnosticCase"]!["alarmBindings"]!["bindings"]!.AsArray().Count == 3);
        Check(experiment["tests"]![0]!["signals"]!.AsArray().Count == 11);
        var trace = store.Read(traceId); var watch = store.Read(experiment["watchPlanId"]!.GetValue<string>())["watchPlan"]!;
        // Independent source oracle, not a copy of the composite producer's output.
        foreach (var expected in f.Oracle["signals"]!.AsArray())
        {
            string id = expected!["id"]!.GetValue<string>();
            var signal = trace["signals"]!.AsArray().Single(s => (string?)s!["id"] == id)!;
            Check((string?)signal["datatype"] == (string?)expected["type"]);
            Check(JsonNode.DeepEquals(signal["memberPath"], expected["memberPath"]));
            Check((string?)signal["storagePath"] == Fixture.Block(expected["db"]!.GetValue<string>()));
            Check(watch["rows"]!.AsArray().Count(r => (string?)r!["candidateId"] == id) == 1);
        }
        var beforeCapture = f.Capture(traceId); var afterCapture = f.Capture(traceId);
        foreach (var sample in afterCapture["samples"]!.AsArray()) { sample!["values"]!["final"] = true; sample["values"]!["override"] = false; }
        string before = Id(f.Import(traceId, beforeCapture)), after = Id(f.Import(traceId, afterCapture));
        var reviewRequest = ReviewRequest(experimentId, before, after);
        var reviewed = experiments.Review(experimentId, reviewRequest); var report = reviewed["review"]!;
        Check((string?)report["beforeHypotheses"]![0]!["verdict"] == "supportedPrediction");
        Check(report["beforeHypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 3);
        Check((string?)report["afterHypotheses"]![0]!["verdict"] == "unresolved");
        Check(report["afterHypotheses"]![0]!["matchedSamples"]!.GetValue<int>() == 0);
        foreach (string field in new[] { "sourceAuthenticityVerified", "clockSynchronizationVerified", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted" })
            Check(report[field]!.GetValue<bool>() == false);
        var compared = store.Read(report["comparisonId"]!.GetValue<string>());
        var final = compared["signals"]!.AsArray().Single(s => (string?)s!["beforeSignalId"] == "final")!;
        Check(final["before"]!["falseSamples"]!.GetValue<int>() == 3 && final["after"]!["falseSamples"]!.GetValue<int>() == 0);
        Check(final["before"]!["transitionCount"]!.GetValue<int>() == 2 && final["after"]!["transitionCount"]!.GetValue<int>() == 0);
        var originals = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics"), "*.json").ToDictionary(p => p, File.ReadAllBytes);
        Check(Id(new DiagnosticExperiments(f.Root).Plan(caseId, prepare)) == experimentId);
        Check(Id(new DiagnosticExperiments(f.Root).Review(experimentId, reviewRequest)) == Id(reviewed));
        foreach (var file in originals) Check(File.ReadAllBytes(file.Key).SequenceEqual(file.Value));
        var badPrepare = prepare.DeepClone().AsObject(); badPrepare["trace"]!["settings"]!["trigger"]!["signalId"] = "missing";
        int count = Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length;
        Reject("InvalidRequest", () => experiments.Plan(caseId, badPrepare));
        Check(Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length == count);
        var badReview = reviewRequest.DeepClone().AsObject(); badReview["comparison"]!["signalMaps"] = new JsonArray();
        Reject("InvalidRequest", () => experiments.Review(experimentId, badReview));
        Check(Directory.GetFiles(Path.Combine(f.Root, "trace-diagnostics")).Length == count);
        foreach (var mutate in new Action<JsonObject>[] {
            e => e["nativeExecutable"] = true, e => e["tracePlanId"] = new string('a',64),
            e => e["diagnosticCase"]!["signals"]![0]!["datatype"] = "Int",
            e => e["diagnosticCase"]!["selectedInstance"]!["memberPath"] = new JsonArray("Conv1"),
            e => e["tests"]![0]!["signals"]![0]!["reason"] = "invented reason" })
        {
            var changed = experiment.DeepClone().AsObject(); mutate(changed);
            Reject("CaptureMismatch", () => experiments.Review(Id(store.Save(changed)), reviewRequest));
        }
        var changedCase = diagnostic["plan"]!.DeepClone().AsObject();
        changedCase["testSuiteEvidence"]!["finding"]!["finding"]!["warningCount"] = 0;
        Reject("CaptureMismatch", () => experiments.Plan(Id(store.Save(changedCase)), prepare));
        var changedImport = store.Read(before); changedImport["measurement"]!["samples"]![2]!["values"]!["run"] = false;
        var changedReview = reviewRequest.DeepClone().AsObject(); changedReview["beforeMeasurementId"] = Id(store.Save(changedImport));
        Reject("CaptureMismatch", () => experiments.Review(experimentId, changedReview));
        // A prior quality assessment is historical metadata; current counts come from exact original samples.
        var historical = store.Read(before); historical["qualityAssessment"]!["detectedIntervalGaps"] = 999;
        string historicalId = Id(store.Save(historical)); byte[] historicalBytes = File.ReadAllBytes(Path.Combine(f.Root, "trace-diagnostics", historicalId + ".json"));
        var historicalReview = reviewRequest.DeepClone().AsObject(); historicalReview["beforeMeasurementId"] = historicalId;
        Check(experiments.Review(experimentId, historicalReview)["review"]!["beforeQualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>() == 0);
        Check(historicalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root, "trace-diagnostics", historicalId + ".json"))));
        // Missing input and post-import source changes remain visible even when normalized bytes were retained.
        var artifact = store.Read(before)["inputArtifact"]!;
        string sourcePath = artifact["path"]!.GetValue<string>(); byte[] raw = File.ReadAllBytes(sourcePath);
        File.WriteAllText(sourcePath, "changed original");
        Reject("ArtifactChanged", () => experiments.Review(experimentId, reviewRequest));
        File.WriteAllBytes(sourcePath, raw);
        var noPrediction = f.Request(); noPrediction["hypotheses"]![0]!.AsObject().Remove("prediction");
        var unpredicted = experiments.Plan(Id(f.Planner.Plan(noPrediction)), prepare)["experiment"]!;
        Check(unpredicted["compositionGaps"]!.AsArray().Count == 3 && (string?)unpredicted["tests"]![0]!["assessment"] == "unresolvedWithoutPrediction");
        var otherCase = f.Request(); otherCase["report"] = "Unrelated similar equipment report.";
        var unrelated = experiments.Plan(Id(f.Planner.Plan(otherCase)), prepare);
        var wrong = reviewRequest.DeepClone().AsObject(); wrong["afterExperimentId"] = Id(unrelated);
        Reject("ExperimentLineageMismatch", () => experiments.Review(experimentId, wrong));
        var otherTrace = f.Settings(); otherTrace["settings"]!["postSamples"] = 200;
        var alternate = f.Traces.Prepare(caseId, otherTrace);
        var wrongImport = f.Import(Id(alternate), f.Capture(Id(alternate)));
        wrong = reviewRequest.DeepClone().AsObject(); wrong["afterMeasurementId"] = Id(wrongImport);
        Reject("MeasurementLineageMismatch", () => experiments.Review(experimentId, wrong));
        // Changed program identity needs explicit admission, even on an unchanged saved case.
        var otherProgram = f.Capture(traceId); otherProgram["programIdentity"] = "second caller-declared program";
        string changedProgram = Id(f.Import(traceId, otherProgram));
        wrong = ReviewRequest(experimentId, before, changedProgram);
        Reject("ProgramRevisionMismatch", () => experiments.Review(experimentId, wrong));
        wrong["comparison"]!["allowChangedSource"] = true;
        Check(experiments.Review(experimentId, wrong)["review"]!["programIdentityChanged"]!.GetValue<bool>());
        // Source revision and instance changes are direct explicit replans, never ID-only mapping.
        f.ReplaceXml("FB_Conveyor", xml => xml.Replace("</Document>", "<!--second saved revision--></Document>"));
        var nextRequest = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"),
            ["instance"] = new JsonObject { ["dbPath"] = Fixture.Block("DB_Line"), ["memberPath"] = new JsonArray("Conv1") },
            ["reason"] = "Test explicit reuse on Conveyor 1; not a fix claim.",
            ["signalMappings"] = new JsonArray(diagnostic["plan"]!["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject {
                ["id"] = s!["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(),
                ["memberPath"] = new JsonArray(s["memberPath"]!.AsArray().Select(p => (JsonNode?)JsonValue.Create((string?)p == "Conv2" ? "Conv1" : (string?)p)).ToArray()),
                ["reason"] = "Explicit matching role on the second conveyor." }).ToArray()) };
        var next = new DiagnosticReplanner(f.Root).Replan(caseId, nextRequest); var nextExperiment = experiments.Plan(Id(next), Request(f));
        string nextTrace = nextExperiment["experiment"]!["tracePlanId"]!.GetValue<string>();
        string nextMeasurement = Id(f.Import(nextTrace, f.Capture(nextTrace)));
        var mapped = ReviewRequest(Id(nextExperiment), before, nextMeasurement);
        Reject("ProgramRevisionMismatch", () => experiments.Review(experimentId, mapped));
        mapped["comparison"]!["allowChangedSource"] = true;
        Reject("MeasurementLineageMismatch", () => experiments.Review(experimentId, mapped));
        mapped["comparison"]!["signalMap"] = new JsonArray(diagnostic["plan"]!["signals"]!.AsArray()
            .Select(s => (JsonNode?)new JsonObject { ["before"] = s!["id"]!.DeepClone(), ["after"] = s["id"]!.DeepClone() }).ToArray());
        var mappedReport = experiments.Review(experimentId, mapped)["review"]!;
        Check((string?)mappedReport["lineage"]!["kind"] == "explicitDirectReplan");
        Check(mappedReport["sourceChanged"]!.GetValue<bool>());
        Check(nextExperiment["experiment"]!["diagnosticCase"]!["testSuiteEvidence"] is null);
        Check(nextExperiment["experiment"]!["diagnosticCase"]!["alarmEvidence"] is null);
        foreach (var mutate in new Action<JsonObject>[] { p => p["lineage"]!["mapping"]![0]!["memberPath"] = new JsonArray("Other"),
            p => p["hypotheses"]![0]!["statement"] = "Replaced hypothesis", p => p["lineage"]!["previousPlanId"] = new string('a',64) })
        {
            var forged = next["plan"]!.DeepClone().AsObject(); mutate(forged);
            var forgedExperiment = experiments.Plan(Id(store.Save(forged)), Request(f));
            var rejected = mapped.DeepClone().AsObject(); rejected["afterExperimentId"] = Id(forgedExperiment);
            Reject("ExperimentLineageMismatch", () => experiments.Review(experimentId, rejected));
        }
        Console.WriteLine($"PASS diagnostic experiment: {checks} independent composition/provenance/lineage checks; no native acquisition");
    }
}
