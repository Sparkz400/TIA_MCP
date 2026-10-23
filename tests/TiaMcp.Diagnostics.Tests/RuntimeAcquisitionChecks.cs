using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.PhysicalRuntime;

internal static class RuntimeAcquisitionChecks
{
    internal static JsonObject? Samples;
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Runtime acquisition check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        string Id(JsonNode node) => node["id"]!.GetValue<string>();
        using var f = new RuntimeAnalysisChecks.CaptureFixture();
        var service = new DiagnosticRuntimeAcquisitions(f.Source.Root); var store = new DiagnosticStore(f.Source.Root);
        string experiment = Id(new DiagnosticExperiments(f.Source.Root).Plan(f.Plan.Association!.CaseId, ExperimentChecks.Request(f.Source)));
        var request = new JsonObject { ["reason"] = "Retain explicitly associated polling observations without behavioral assertion.",
            ["units"] = new JsonObject { ["enable"] = "1", ["step"] = "count" } };
        var writer = f.Store.Create(f.Plan); string prefix = writer.Reference;
        var active = service.Acquire(f.Store, experiment, prefix, request);
        Check(active["acquisition"]!["captureSource"]!["uncommittedDataUnknown"]!.GetValue<bool>());
        f.Append(writer, 0, true, "65535"); f.Append(writer, 1, false, "65534", backwardsClock: true);
        f.Append(writer, 2, null, null); writer.Append(new(writer.Rows, "gap", null, 2, 200, 203, f.Start, null, null, 2));
        f.Append(writer, 5, true, "\"65535\""); f.Append(writer, 6, true, "70000");
        f.Append(writer, 7, true, "1"); f.Append(writer, 8, true, "1e-100");
        writer.RecordDroppedResult(); writer.Finish("byteLimit", 804);
        string originalRequest = request.ToJsonString();
        var receipt = service.Acquire(f.Store, experiment, writer.Reference, request);
        var acquisition = receipt["acquisition"]!; var source = acquisition["captureSource"]!; var analysis = acquisition["analysis"]!;
        Check(request.ToJsonString() == originalRequest);
        Check(source["rows"]!.AsArray().Count == 15 && !source["rowsTruncated"]!.GetValue<bool>());
        Check(source["originalFiles"]!.AsArray().Count == 2);
        Check(source["recordedProgress"]!["terminalReason"]!.GetValue<string>() == "byteLimit");
        Check(source["recordedProgress"]!["droppedReadResults"]!.GetValue<long>() == 1);
        Check(analysis["qualityAssessment"]!["cyclesObserved"]!.GetValue<int>() == 7);
        Check(analysis["qualityAssessment"]!["hostReadErrors"]!.GetValue<int>() == 1);
        Check(analysis["qualityAssessment"]!["providerFailures"]!.GetValue<int>() == 1);
        Check(analysis["qualityAssessment"]!["unsupportedValues"]!.GetValue<int>() == 3);
        Check(analysis["qualityAssessment"]!["skippedPolls"]!.GetValue<long>() == 2);
        Check(analysis["qualityAssessment"]!["missingValues"]!.GetValue<int>() == 68);
        Check(analysis["qualityAssessment"]!["uncapturedSignalIds"]!.AsArray().Count == 9);
        Check(analysis["hypotheses"]![1]!["matchedCycles"]!.GetValue<int>() == 1);
        Check(analysis["hypotheses"]![1]!["contradictedCycles"]!.GetValue<int>() == 1);
        Check(analysis["hypotheses"]![1]!["unknownCycles"]!.GetValue<int>() == 4);
        foreach (var hypothesis in analysis["hypotheses"]!.AsArray()) Check((string?)hypothesis!["verdict"] == "unresolved");
        foreach (string flag in new[] { "atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "addressMatchVerified",
            "sourceAuthenticityVerified", "clockSynchronizationVerified", "assertionsAdmitted", "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted" })
            Check(acquisition[flag]!.GetValue<bool>() == false);
        foreach (var original in source["originalFiles"]!.AsArray())
        {
            byte[] raw = Convert.FromBase64String(original!["base64"]!.GetValue<string>());
            Check(raw.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Source.Root, original["relativePath"]!.GetValue<string>()))));
            Check(Convert.ToHexStringLower(SHA256.HashData(raw)) == original["sha256"]!.GetValue<string>());
            Check(raw.Length == original["bytes"]!.GetValue<int>());
        }
        var originalRows = JsonNode.Parse(Convert.FromBase64String(source["originalFiles"]![1]!["base64"]!.GetValue<string>()))!["rows"]!;
        Check(JsonNode.DeepEquals(originalRows, source["rows"]));
        Check(source["rows"]![3]!["readResultJson"]!.GetValue<string>().Contains("\"hostClockWentBackwards\":true"));
        Check(source["rows"]![4]!["errorCode"]!.GetValue<string>() == "RuntimeTimeout");
        Check(source["rows"]![6]!["kind"]!.GetValue<string>() == "gap");
        Check(Id(service.Acquire(f.Store, experiment, writer.Reference, request)) == Id(receipt));
        Check(Id(service.Acquire(f.Store, experiment, prefix, request)) == Id(active));
        Check(Id(service.Export(f.Store, Id(active), "json")) == Id(active));
        foreach (string format in new[] { "json", "markdown", "html" })
        {
            var exported = service.Export(f.Store, Id(receipt), format);
            var text = File.ReadAllText(exported["artifact"]!["path"]!.GetValue<string>());
            Check(text.Contains("unresolved") && text.Contains("65535") && text.Contains("byteLimit"));
            Check(text.Contains("requestStartedUtc") && text.Contains("responseReceivedUtc") && text.Contains("originalFiles"));
            if (format == "json") Check(Id(exported) == Id(receipt));
            if (format == "html") Check(!text.Contains("<script>") && !text.Contains("<img"));
        }
        var advancing = f.Store.Create(f.Plan with { CaptureId = Guid.NewGuid().ToString("N") });
        f.Append(advancing, 0, true, "1"); f.Append(advancing, 10, false, "2");
        string committedPrefix = advancing.Reference;
        var observedPrefix = service.Acquire(f.Store, experiment, committedPrefix, request);
        Check(observedPrefix["acquisition"]!["captureSource"]!["rows"]!.AsArray().Count == 3);
        advancing.Finish("stopped", 1003);
        Check(Id(service.Acquire(f.Store, experiment, committedPrefix, request)) == Id(observedPrefix));
        Check(Id(service.Export(f.Store, Id(observedPrefix), "json")) == Id(observedPrefix));
        // These frozen synthetic files seed the actual Host test. They are never presented as PLC observations.
        var files = new JsonObject();
        foreach (string path in Directory.GetFiles(f.Source.Root, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal)))
            files[Path.GetRelativePath(f.Source.Root, path).Replace('\\', '/')] = Convert.ToBase64String(File.ReadAllBytes(path));
        Samples = new JsonObject { ["authority"] = "syntheticManagedProducer", ["nativeQualification"] = false,
            ["files"] = files, ["experimentId"] = experiment, ["snapshotRef"] = writer.Reference, ["prefixRef"] = committedPrefix,
            ["request"] = request.DeepClone(), ["acquisitionId"] = Id(receipt), ["prefixAcquisitionId"] = Id(observedPrefix),
            ["analysisId"] = acquisition["analysisId"]!.DeepClone() };
        int count = Directory.GetFiles(Path.Combine(f.Source.Root, "trace-diagnostics")).Length;
        foreach (string extra in new[] { "programMatchVerified", "atomicAcrossSignals", "quality", "signalMap" })
        {
            var invalid = request.DeepClone().AsObject(); invalid[extra] = true;
            Reject("InvalidRequest", () => service.Acquire(f.Store, experiment, writer.Reference, invalid));
            Check(count == Directory.GetFiles(Path.Combine(f.Source.Root, "trace-diagnostics")).Length);
        }
        var badUnits = request.DeepClone().AsObject(); badUnits["units"]!["step"] = "ms";
        Reject("RuntimeCaptureAnalysisMismatch", () => service.Acquire(f.Store, experiment, writer.Reference, badUnits));
        Check(count == Directory.GetFiles(Path.Combine(f.Source.Root, "trace-diagnostics")).Length);
        string unrelated = Id(new DiagnosticExperiments(f.Source.Root).Plan(Id(f.Source.Plan()), ExperimentChecks.Request(f.Source)));
        Reject("RuntimeAcquisitionLineageMismatch", () => service.Acquire(f.Store, unrelated, writer.Reference, request));
        var unassociated = f.Store.Create(f.Plan with { CaptureId = Guid.NewGuid().ToString("N"),
            Request = f.Plan.Request with { Association = null }, Association = null });
        unassociated.Finish("stopped", 0);
        Reject("RuntimeAcquisitionLineageMismatch", () => service.Acquire(f.Store, experiment, unassociated.Reference, request));
        foreach (var mutation in new Action<JsonObject>[] {
            d => d["analysis"]!["hypotheses"]![1]!["verdict"] = "supportedPrediction",
            d => d["analysis"]!["hypotheses"]![1]!["matchedCycles"] = 999,
            d => d["captureSource"]!["rows"]![0]!["elapsedMilliseconds"] = 42,
            d => d["captureSource"]!["rows"]![4]!["errorCode"] = null,
            d => d["captureSource"]!["originalFiles"]![0]!["base64"] = "e30=",
            d => d["captureSource"]!["plan"]!["association"]!["signals"]![0]!["signalId"] = "step",
            d => d["atomicAcrossSignals"] = true, d => d["analysisId"] = new string('a',64) })
        {
            var changed = acquisition.DeepClone().AsObject(); mutation(changed);
            Reject("CaptureMismatch", () => service.Export(f.Store, Id(store.Save(changed)), "json"));
        }
        // Restarts reopen original immutable files, not trusted derived documents.
        f.Store.Dispose(); f.Store = new RuntimeCaptureStore(f.Source.Root);
        Check(Id(new DiagnosticRuntimeAcquisitions(f.Source.Root).Export(f.Store, Id(receipt), "json")) == Id(receipt));
        string reviewPath = Path.Combine(f.Source.Root, "project-reviews", f.Source.ReviewId + ".json");
        byte[] reviewBytes = File.ReadAllBytes(reviewPath); File.AppendAllText(reviewPath, " ");
        Reject("ArtifactChanged", () => service.Export(f.Store, Id(receipt), "json")); File.WriteAllBytes(reviewPath, reviewBytes);
        string chainPath = Path.Combine(f.Source.Root, source["originalFiles"]![1]!["relativePath"]!.GetValue<string>());
        byte[] chainBytes = File.ReadAllBytes(chainPath); File.AppendAllText(chainPath, " ");
        Reject("RuntimeCaptureIntegrityFailure", () => service.Export(f.Store, Id(receipt), "json")); File.WriteAllBytes(chainPath, chainBytes);
        File.Delete(chainPath);
        Reject("RuntimeAcquisitionSourceUnavailable", () => service.Export(f.Store, Id(receipt), "json")); File.WriteAllBytes(chainPath, chainBytes);
        using var large = new RuntimeAnalysisChecks.CaptureFixture();
        string largeExperiment = Id(new DiagnosticExperiments(large.Source.Root).Plan(large.Plan.Association!.CaseId, ExperimentChecks.Request(large.Source)));
        var largePlan = large.Plan with { Request = large.Plan.Request with { MaximumSamples = 11000, MaximumBytes = 32 << 20, DurationSeconds = 3600 } };
        var largeWriter = large.Store.Create(largePlan);
        for (int cycle = 0; cycle < 5001; cycle++) large.Append(largeWriter, cycle, true, "1");
        largeWriter.Finish("stopped", 500003);
        count = Directory.GetFiles(Path.Combine(large.Source.Root, "trace-diagnostics")).Length;
        Reject("ArtifactCapacity", () => new DiagnosticRuntimeAcquisitions(large.Source.Root).Acquire(large.Store, largeExperiment, largeWriter.Reference, request));
        Check(count == Directory.GetFiles(Path.Combine(large.Source.Root, "trace-diagnostics")).Length);
        using var byteBoundary = new RuntimeAnalysisChecks.CaptureFixture();
        string byteExperiment = Id(new DiagnosticExperiments(byteBoundary.Source.Root).Plan(byteBoundary.Plan.Association!.CaseId, ExperimentChecks.Request(byteBoundary.Source)));
        var bytePlan = byteBoundary.Plan with { Request = byteBoundary.Plan.Request with { MaximumBytes = 8 << 20 } };
        var byteWriter = byteBoundary.Store.Create(bytePlan);
        foreach (int cycle in new[] { 0, 10, 20 }) byteBoundary.Append(byteWriter, cycle, true, "1");
        byteWriter.Finish("stopped", 2003);
        string byteDirectory = Path.Combine(byteBoundary.Source.Root, "runtime-captures", bytePlan.CaptureId);
        byte[] planBytes = File.ReadAllBytes(Path.Combine(byteDirectory, "plan.json"));
        string previousHash = Convert.ToHexStringLower(SHA256.HashData(planBytes));
        for (int index = 1; index <= 3; index++)
        {
            string segmentPath = Path.Combine(byteDirectory, index.ToString("D6") + ".segment");
            var segment = JsonNode.Parse(File.ReadAllBytes(segmentPath))!.AsObject(); segment["previousSha256"] = previousHash;
            byte[] body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(segment, BridgeProtocol.Json);
            byte[] padded = Enumerable.Repeat((byte)' ', (1 << 20) - (index == 3 ? planBytes.Length : 0)).ToArray();
            body.CopyTo(padded, 0); File.WriteAllBytes(segmentPath, padded);
            previousHash = Convert.ToHexStringLower(SHA256.HashData(padded));
        }
        string exactRef = $"capture:{bytePlan.CaptureId}:3:{previousHash}";
        var exact = new DiagnosticRuntimeAcquisitions(byteBoundary.Source.Root).Acquire(byteBoundary.Store, byteExperiment, exactRef, request);
        Check(exact["acquisition"]!["captureSource"]!["originalByteCount"]!.GetValue<int>() == 3 << 20);
        string finalSegment = Path.Combine(byteDirectory, "000003.segment"); File.AppendAllText(finalSegment, " ");
        string overRef = $"capture:{bytePlan.CaptureId}:3:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(finalSegment)))}";
        Reject("ArtifactCapacity", () => new DiagnosticRuntimeAcquisitions(byteBoundary.Source.Root).Acquire(byteBoundary.Store, byteExperiment, overRef, request));
        using var capacity = new RuntimeAnalysisChecks.CaptureFixture();
        string capacityExperiment = Id(new DiagnosticExperiments(capacity.Source.Root).Plan(capacity.Plan.Association!.CaseId, ExperimentChecks.Request(capacity.Source)));
        var capacityWriter = capacity.Store.Create(capacity.Plan); capacity.Append(capacityWriter, 0, true, "1"); capacityWriter.Finish("stopped", 3);
        var child = capacity.Analyze(capacityWriter.Reference); string childPath = child["artifact"]!["path"]!.GetValue<string>();
        long childLength = new FileInfo(childPath).Length; File.Delete(childPath);
        string directory = Path.Combine(capacity.Source.Root, "trace-diagnostics");
        long used = Directory.GetFiles(directory).Sum(p => new FileInfo(p).Length);
        using (var quota = new FileStream(Path.Combine(directory, "capacity-fixture.bin"), FileMode.CreateNew))
            quota.SetLength((256L << 20) - used - childLength - 128);
        count = Directory.GetFiles(directory).Length;
        Reject("ArtifactCapacity", () => new DiagnosticRuntimeAcquisitions(capacity.Source.Root).Acquire(capacity.Store, capacityExperiment, capacityWriter.Reference, request));
        Check(Directory.GetFiles(directory).Length == count + 1 && File.Exists(childPath));
        Check(store.Read(Id(receipt))["schemaVersion"]!.GetValue<string>() == "diagnostic-runtime-acquisition.v1");
        Console.WriteLine($"PASS runtime acquisition: {checks} original-chain, timing, quality, lineage, restart and refusal checks; synthetic managed producer only.");
    }
}
