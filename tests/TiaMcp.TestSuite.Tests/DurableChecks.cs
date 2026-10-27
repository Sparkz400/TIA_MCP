using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

internal static class DurableChecks
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new Exception("Use a fresh durable Test Suite fixture directory.");
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected refusal: " + code);
        }
        EngineeringArtifactReference Save(string path, string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(Path.Combine(root, path), bytes);
            return new(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        string Canonical<T>(T value) => CanonicalJson.Serialize(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
        var targetReference = new EngineeringObjectReference(new("Project/DurableSynthetic", "PLC/A", "Root", "Project", "project"), new("authored", new('a', 64)), new('b', 64));
        var scope = targetReference with { Object = targetReference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" } };
        var definition = new TestSuiteDefinitionCapture("style", "style", "Style", ".xml",
            targetReference with { Object = targetReference.Object with { ObjectPath = "Rules/Style", Kind = "ruleSet" }, ContentSha256 = null },
            Encoding.UTF8.GetBytes("<AuthoredRule/>"), "authored-v1", [scope], [], true, true, []);
        var run = TestSuiteRunPlanner.Prepare([definition], ["style"], [], "style");
        var runArtifact = Save("run.json", Canonical(run));
        var bundle = TestSuiteDefinitionBundle.Capture(run, [definition]);
        var definitionArtifact = Save("definitions.json", Encoding.UTF8.GetString(bundle.Bytes));
        var target = new NativeTargetObservation(targetReference, Path.Combine(root, "owned-synthetic.ap21"), "epoch-authored", new('c', 64), new('d', 64),
            "authored-hardware", null, true, new[] { "testsuite.product", "testsuite.license", "testsuite.styleService", "testsuite.scope" }
                .Select(p => new TargetProbe(p, "observed", new('e', 64))).ToArray());
        var plan = TestSuiteStyleOperation.BuildPlan(run, target, runArtifact, definitionArtifact, bundle);
        Check(plan.RequiredGates.Count == 7 && plan.Resources[0].ResourceKey == EngineeringResourceKey.ForPath("project", target.ProjectFilePath), "Style operation reserves the shared canonical project resource and complete gates.");
        Reject(() => TestSuiteStyleOperation.BuildPlan(run, target with { Probes = [] }, runArtifact, definitionArtifact, bundle), "TargetPrerequisiteMissing");
        Reject(() => TestSuiteStyleOperation.BuildPlan(run, target with { Probes = target.Probes.Select(p => p.Name == "testsuite.license" ? p with { State = "blocked" } : p).ToArray() }, runArtifact, definitionArtifact, bundle), "TargetPrerequisiteMissing");
        Reject(() => TestSuiteStyleOperation.BuildPlan(run, target, runArtifact with { Sha256 = new('f', 64) }, definitionArtifact, bundle), "TestSuiteArtifactMismatch");
        Reject(() => TestSuiteStyleOperation.BuildPlan(run, target, runArtifact, definitionArtifact with { Sha256 = new('f', 64) }, bundle), "TestSuiteArtifactMismatch");
        Reject(() => TestSuiteStyleOperation.BuildPlan(run, target, runArtifact, definitionArtifact, null!), "InvalidDefinitionBundle");
        var restoredBundle = TestSuiteDefinitionBundle.Restore(run, File.ReadAllBytes(Path.Combine(root, definitionArtifact.RelativePath)));
        Check(restoredBundle.Sha256 == bundle.Sha256 && restoredBundle.OriginalBytes("style").SequenceEqual(definition.OriginalBytes), "A reopened retained bundle verifies the exact original definition bytes.");
        var unreviewed = new TestSuiteDefinitionCapture("style", "style", "Style", ".xml", definition.Manifest.Reference,
            definition.OriginalBytes, "authored-v1", [scope], [], false, true, []);
        var blockedRun = TestSuiteRunPlanner.Prepare([unreviewed], ["style"], [], "style");
        Reject(() => TestSuiteStyleOperation.BuildPlan(blockedRun, target, runArtifact, definitionArtifact, bundle), "TestSuiteStyleNotReady");
        var application = new TestSuiteDefinitionCapture("application", "application", "Application", ".tat",
            definition.Manifest.Reference with { ContentSha256 = null }, Encoding.UTF8.GetBytes("opaque synthetic application"), "authored-v1", [scope], [], true, true, []);
        var applicationRun = TestSuiteRunPlanner.Prepare([application], ["application"], [], "systemManaged");
        Reject(() => TestSuiteStyleOperation.BuildPlan(applicationRun, target, runArtifact, definitionArtifact, bundle), "TargetPrerequisiteMissing");
        var systemRun = TestSuiteRunPlanner.Prepare([new TestSuiteDefinitionCapture("system", "system", "System", ".tst", definition.Manifest.Reference with { ContentSha256 = null },
            Encoding.UTF8.GetBytes("opaque synthetic system"), "authored-v1", [scope], [], true, true, [])], ["system"], [], "endpoint");
        Reject(() => TestSuiteStyleOperation.BuildPlan(systemRun, target, runArtifact, definitionArtifact, bundle), "TestSuiteStyleNotReady");
        var store = new EngineeringOperationStore(root);
        var unrelated = Save("unrelated-definition-bundle.json", "{\"authoredSyntheticFixture\":true}");
        var misidentified = TestSuiteStyleOperation.BuildPlan(run, target, runArtifact, unrelated with { Sha256 = bundle.Sha256 }, bundle);
        Reject(() => store.Prepare(misidentified, "style-synthetic-unrelated-bundle"), "ArtifactChanged");
        var prepared = store.Prepare(plan, "style-synthetic-1");
        Check(new EngineeringOperationStore(root).Read(prepared.Id).State == "prepared", "Prepared style operation survives store reopen.");
        Check(store.Prepare(plan, "style-synthetic-1").Id == prepared.Id, "Identical reviewed dispatch key deduplicates preparation.");
        var acquired = store.Acquire(prepared.Id, prepared.Revision);
        var competitor = store.Prepare(plan, "style-synthetic-2");
        Reject(() => store.Acquire(competitor.Id, competitor.Revision), "ResourceBusy");
        int reacquisitions = 0;
        NativeTargetObservation Reacquire() { reacquisitions++; return target with { Reference = targetReference with { Snapshot = targetReference.Snapshot with { ObservationId = "reacquired" } } }; }
        var checkpointIntent = store.BeginCheckpoint(acquired.Id, acquired.Revision, Reacquire);
        var checkpoint = Save("checkpoint.synthetic", "authored checkpoint, no native archive performed");
        var ready = store.Checkpoint(checkpointIntent.Id, checkpointIntent.Revision, [checkpoint]);
        var dispatchIntent = store.Dispatch(ready.Id, ready.Revision, Reacquire);
        Check(reacquisitions == 2 && dispatchIntent.State == "dispatchIntent", "Checkpoint and execution each reacquire before persisted intent.");
        var restarted = new EngineeringOperationStore(root);
        Check(restarted.Read(dispatchIntent.Id).State == "dispatchIntent", "Restart retains intent; it does not guess invocation completion.");
        Reject(() => restarted.Dispatch(dispatchIntent.Id, dispatchIntent.Revision, Reacquire), "RecoveryRequired");
        var unknown = restarted.MarkUnknown(dispatchIntent.Id, dispatchIntent.Revision);
        Reject(() => restarted.Acquire(competitor.Id, competitor.Revision), "ResourceBusy");
        Reject(() => restarted.Stop(unknown.Id, unknown.Revision), "RecoveryRequired");
        var reconciliation = Save("reconciliation.synthetic.json", "{\"authoredSyntheticFixture\":true,\"observedNative\":false}");
        var reconciled = restarted.Reconcile(unknown.Id, unknown.Revision, [new(plan.Resources[0].ResourceKey, "unchanged", null, reconciliation)]);
        Check(reconciled.State == "reconciled", "Every owned resource must reconcile before the next run.");
        var admittedNext = restarted.Acquire(competitor.Id, competitor.Revision);
        var stopped = restarted.Stop(admittedNext.Id, admittedNext.Revision);
        Check(stopped.State == "stopped", "Cooperative stop between native runs prevents the next checkpoint/dispatch.");

        var successPrepared = store.Prepare(plan, "style-synthetic-success");
        var successAcquired = store.Acquire(successPrepared.Id, successPrepared.Revision);
        var successCheckpointIntent = store.BeginCheckpoint(successAcquired.Id, successAcquired.Revision, Reacquire);
        var successReady = store.Checkpoint(successCheckpointIntent.Id, successCheckpointIntent.Revision, [checkpoint]);
        var successDispatch = store.Dispatch(successReady.Id, successReady.Revision, Reacquire);
        var results = TestSuiteResultNormalizer.Normalize(run, [new("style", definition.Manifest.Reference, "Success", 0, 0, [], true, true, true)]);
        var coverage = TestSuiteScopeCoverage.Compare(run, [new("style", definition.Manifest.Reference, [scope], [], true)]);
        var evidence = plan.RequiredGates.Select(g => new EngineeringGateEvidence(g, g switch
        {
            "testsuite.results" => Save("results.json", Canonical(results)),
            "testsuite.scope" => Save("scope.json", Canonical(coverage)),
            _ => Save(g.Replace('.', '-') + ".synthetic.json", Canonical(new { gate = g, authoredSyntheticFixture = true, observedNative = false }))
        })).ToArray();
        Reject(() => TestSuiteStyleOperation.SuccessfulEvidence(run, results, TestSuiteScopeCoverage.Compare(run, []), evidence), "TestSuiteCoverageIncomplete");
        Reject(() => TestSuiteStyleOperation.SuccessfulEvidence(run, results, coverage, evidence[..^1]), "VerificationIncomplete");
        Reject(() => TestSuiteStyleOperation.SuccessfulEvidence(run, results, coverage, evidence.Select(e => e.Gate == "testsuite.results" ? e with { Artifact = e.Artifact with { Sha256 = new('f', 64) } } : e).ToArray()), "TestSuiteArtifactMismatch");
        var verifiedEvidence = TestSuiteStyleOperation.SuccessfulEvidence(run, results, coverage, evidence);
        Save("assessment.json", Canonical(TestSuiteScopeCoverage.Assess(run, results, coverage)));
        Reject(() => store.RecordOutcome(successDispatch.Id, successDispatch.Revision, true, verifiedEvidence[..^1]), "VerificationIncomplete");
        var success = store.RecordOutcome(successDispatch.Id, successDispatch.Revision, true, verifiedEvidence);
        Check(new EngineeringOperationStore(root).Read(success.Id).State == "succeeded", "Validated Test Suite coverage and all retained shared gates support durable completion.");
        Save("receipt.json", Canonical(new { checks, authoredSyntheticFixture = true, nativeExecuted = false, releaseAccepted = false,
            plan.Operation, scale = "store integration only", outcomes = new[] { reconciled.State, stopped.State, success.State } }));
        return checks;
    }
}
