using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

internal static partial class ProductionJobChecks
{
    public static async Task<int> Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new Exception("Fresh TS-03 test output required.");
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (BridgeException error) when (error.Code == code) { checks.Add("refused: " + code); return; }
            throw new Exception("Expected " + code);
        }
        Task RejectSync(Action action, string code) => Reject(() => { action(); return Task.CompletedTask; }, code);
        Fixture F(string name, int runs = 1) => new(Path.Combine(directory, name), runs);

        var f = F("complete", 2);
        var review = f.Jobs.ReadReview(f.Review);
        Check(review.Runs.Length == 2 && review.Runs.All(r => r.Run.Definitions.Length == 1 && r.Operation.RequiredGates.Count == 7), "Complete review contains every run, original closure and seven gates");
        Check(review.Runs[0].Operation.Resources[0].ResourceKey == EngineeringResourceKey.ForPath("project", f.Target.ProjectFilePath), "Shared exact project key");
        Check(f.Jobs.Prepare(f.Review, f.Review.Sha256, "job").Job.Id == f.Job.Job.Id, "Idempotent prepare survives complete membership");
        await RejectSync(() => f.Jobs.Prepare(f.Review, new('a', 64), "job"), "ReviewMismatch");
        var retainedBeforeTextRefusals = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        foreach (string invalid in IdentityTextChecks.InvalidText)
        {
            await RejectSync(() => f.Jobs.Review([f.Definition], [new(["style"], [])], f.Target, invalid), "TestSuiteJobLimit");
            await RejectSync(() => f.Jobs.Prepare(f.Review, f.Review.Sha256, invalid), "InvalidRequest");
        }
        Check(Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order().SequenceEqual(retainedBeforeTextRefusals), "Invalid identity text refuses before retaining review or operation files");
        foreach (string invalid in new[] { "bad\u0000", "bad\n", "bad\ufffe", "bad\uffff", new string('x',257) })
            await RejectSync(() => f.Jobs.ReadReview(f.Jobs.Retain(review with { ExecutionProfile = invalid })), "InvalidTestSuiteJob");
        var unicodeReview = f.Jobs.Review([f.Definition], [new(["style"], [])], f.Target, "日本語 Ω 😀 e\u0301 \ufffd");
        Check(f.Jobs.ReadReview(unicodeReview).ExecutionProfile == "日本語 Ω 😀 e\u0301 \ufffd", "Execution profile Unicode survives exact reopen");
        var unicodeJob = f.Jobs.Prepare(unicodeReview, unicodeReview.Sha256, "job-😀-e\u0301-\ufffd");
        Check(new TestSuiteStyleJobs(f.Root).Status(unicodeJob.Job.Id).Job.IdempotencyKey == "job-😀-e\u0301-\ufffd", "Job key Unicode survives exact restart");
        Check(f.Jobs.Prepare(unicodeReview, unicodeReview.Sha256, "job-é").Job.Id != f.Jobs.Prepare(unicodeReview, unicodeReview.Sha256, "job-e\u0301").Job.Id, "Unicode normalization cannot merge two idempotency keys");

        var otherReview = f.Jobs.Review([f.Definition], [new(["style"], [])], f.Target, "authored-test-profile");
        await RejectSync(() => f.Jobs.Prepare(otherReview, otherReview.Sha256, "job"), "IdempotencyConflict");
        await Reject(() => f.Execute(new TestSuiteProductionAdapter()), "TestSuiteProfileUnqualified");
        Check(f.State.State == "prepared" && f.Adapter.Opens == 0, "Production refusal precedes acquisition and native access");
        await Reject(() => f.Jobs.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, f.State.Id, 4, f.Adapter), "RevisionConflict");
        var passed = await f.Execute();
        Check(passed.Operations[0].State == "succeeded" && f.Adapter.Runs == 1 && f.Adapter.Checkpoints == 1 && f.Adapter.Observations == 2, "Full workflow checkpoints then reacquires and invokes once");
        Check(f.Adapter.ObservedDispatchIntent && f.Adapter.RawRetainedBeforeVerify, "Intent flushed before call; raw retained before verification");
        var reopened = new TestSuiteStyleJobs(f.Root).Status(f.Job.Job.Id);
        Check(reopened.NextOperationId == reopened.Operations[1].Id, "Restart retains completed progress and exact next run");
        Check(f.Result.GetProperty("result").GetProperty("classification").GetString() == "passed", "Raw/report/scope/assessment reread and recompute");
        await Reject(() => f.Execute(), "RevisionConflict");
        await Reject(() => f.Jobs.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "RecoveryRequired");
        Check(f.Adapter.Runs == 1, "Lost success response never redispatches");
        new TestSuiteStyleJobs(f.Root).RequestStop(f.Job.Job.Id, f.Review.Sha256);
        var stopped = new TestSuiteStyleJobs(f.Root).Status(f.Job.Job.Id);
        Check(stopped.StopRequested && stopped.Operations[1].State == "stopped" && stopped.NextOperationId is null, "Stop after first run survives restart and stops remaining queue");
        Check(stopped.NativeCancellationSupported == false, "No native cancellation claim");

        f = F("pre-stop"); f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256);
        await Reject(() => f.Jobs.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "RecoveryRequired");
        Check(f.Adapter.Opens == 0 && f.State.State == "stopped", "Pre-dispatch stop prevents native access");

        foreach (string mode in new[] { "product", "license", "service", "probeArtifact", "foreignProbe", "scope", "diagnosticScope", "scopeArtifact", "staleDefinition", "staleEpoch", "staleSource", "staleProfile", "staleProject" })
        {
            f = F("preflight-" + mode); f.Adapter.Mode = mode;
            string code = mode switch { "product" or "license" or "service" or "probeArtifact" or "foreignProbe" => "TargetPrerequisiteMissing",
                "scope" or "diagnosticScope" or "scopeArtifact" => "TestSuiteScopeIncomplete", "staleDefinition" or "staleSource" => "StaleContent", _ => "StaleNativeTarget" };
            await Reject(() => f.Execute(), code);
            Check(f.Adapter.Runs == 0 && f.Adapter.Checkpoints == 0, mode + " refuses before native side effects");
        }
        f = F("stale-second-observation"); f.Adapter.Mode = "secondScope";
        await Reject(() => f.Execute(), "TestSuiteScopeIncomplete");
        Check(f.Adapter.Checkpoints == 1 && f.Adapter.Runs == 0 && f.State.State == "outcomeUnknown", "Scope is reacquired immediately before dispatch; checkpoint ownership retained");

        foreach (string phase in new[] { "acquired", "checkpointIntent", "ready", "dispatchIntent" })
        {
            f = F("restart-" + phase); var store = new EngineeringOperationStore(f.Root);
            var op = store.Acquire(f.State.Id, 0);
            if (phase != "acquired") op = store.BeginCheckpoint(op.Id, op.Revision, () => f.Target);
            if (phase is "ready" or "dispatchIntent") op = store.Checkpoint(op.Id, op.Revision, [f.Jobs.Retain(new { syntheticArchive = true })]);
            if (phase == "dispatchIntent") op = store.Dispatch(op.Id, op.Revision, () => f.Target);
            var restarted = new TestSuiteStyleJobs(f.Root);
            Check(restarted.Status(f.Job.Job.Id).NextOperationId is null, phase + " is never automatically resumed");
            await Reject(() => restarted.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, op.Id, op.Revision, f.Adapter), "RecoveryRequired");
            restarted.MarkInterrupted(f.Job.Job.Id, op.Id, op.Revision);
            Check(f.State.State == "outcomeUnknown" && store.Pending().Any(r => r.Id == op.Id), phase + " explicitly marks unknown and retains resource");
        }

        f = F("lost-run-response"); f.Adapter.Mode = "lostResponse";
        await Reject(() => f.Execute(), "FakeResponseLost");
        Check(f.Adapter.Runs == 1 && f.State.State == "outcomeUnknown", "Lost native response retains unknown outcome");
        await Reject(() => f.Jobs.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "RecoveryRequired");
        var competitor = f.Jobs.Prepare(f.Review, f.Review.Sha256, "competitor");
        await Reject(() => f.Jobs.ExecuteNext(competitor.Job.Id, f.Review.Sha256, competitor.Operations[0].Id, 0, f.Adapter), "ResourceBusy");
        Check(f.Adapter.Runs == 1, "Unknown outcome blocks another job on same project");
        await Reject(() => f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, new TestSuiteProductionAdapter()), "TestSuiteProfileUnqualified");
        f.Adapter.Mode = "ok";
        var reconciled = await f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter);
        Check(reconciled.Operations[0].State == "reconciled" && f.Adapter.Runs == 1, "Independent reconciliation releases resource without replay or passing claim");
        Check(f.Result.GetProperty("result").ValueKind == JsonValueKind.Null, "Reconciliation does not invent a missing result");
        var next = await f.Jobs.ExecuteNext(competitor.Job.Id, f.Review.Sha256, competitor.Operations[0].Id, 0, f.Adapter);
        Check(next.Operations[0].State == "succeeded", "Next explicitly prepared job can run after reconciliation");

        foreach (string mode in new[] { "assertion", "execution", "partialResults", "postScope", "warning" })
        {
            f = F("outcome-" + mode); f.Adapter.Mode = mode; await f.Execute();
            string wanted = mode == "assertion" ? "assertionFailure" : mode == "execution" ? "executionFailure" : "incompleteEvidence";
            Check(f.Result.GetProperty("result").GetProperty("classification").GetString() == wanted && f.State.State == "recoveryRequired", mode + " classification and retained ownership");
            Check(f.State.Evidence.Any(e => e.Gate == "dispatch.rawResults"), mode + " retains raw before normalization");
        }
        foreach (string mode in new[] { "malformedRaw", "unknownRawField", "duplicateRawField", "missingGate", "cleanupFailure", "verifyLost" })
        {
            f = F("evidence-" + mode); f.Adapter.Mode = mode;
            string code = mode is "malformedRaw" or "unknownRawField" or "duplicateRawField" ? "InvalidTestSuiteArtifact" : mode == "missingGate" ? "VerificationIncomplete" : "FakeVerificationFailure";
            await Reject(() => f.Execute(), code);
            Check(f.State.State == "recoveryRequired" && f.State.Evidence.Any(e => e.Gate == "dispatch.rawResults"), mode + " retains unmodified response and prevents resource release");
        }

        f = F("stop-active", 2);
        f.Adapter.BeforeRunReturn = async () =>
        {
            var stop = new TestSuiteStyleJobs(f.Root).RequestStop(f.Job.Job.Id, f.Review.Sha256);
            Check(stop.Operations[0].State == "dispatchIntent" && stop.Operations[1].State == "stopped", "Stop while native call active leaves active intent and stops next run");
            await RejectSync(() => new TestSuiteStyleJobs(f.Root).MarkInterrupted(f.Job.Job.Id, f.State.Id, f.State.Revision), "IntakeBusy");
        };
        await f.Execute();
        Check(f.State.State == "succeeded" && f.Adapter.Runs == 1 && f.Jobs.Status(f.Job.Job.Id).StopRequested, "Active call finishes without invented cancellation; no second dispatch");
        f = F("stop-checkpoint"); f.Adapter.AfterCheckpoint = () => { f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256); };
        await Reject(() => f.Execute(), "TestSuiteStopRequested");
        Check(f.State.State == "outcomeUnknown" && f.Adapter.Runs == 0, "Stop after checkpoint needs reconciliation and prevents Run");
        f = F("stop-open"); f.Adapter.AfterOpen = () => { f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256); };
        await f.Execute();
        Check(f.State.State == "stopped" && f.Adapter.Checkpoints == 0, "Stop under lease before checkpoint safely stops acquired operation");

        f = F("recovery"); f.Adapter.Mode = "lostResponse"; await Reject(() => f.Execute(), "FakeResponseLost");
        var destinations = new[] { new EngineeringRecoveryDestination(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "restored.ap21")) };
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, new TestSuiteProductionAdapter()), "TestSuiteProfileUnqualified");
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision,
            [destinations[0] with { RecoveryLocation = f.Target.ProjectFilePath }], f.Adapter), "InvalidRecovery");
        f.Adapter.Mode = "lostRestore";
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, f.Adapter), "FakeResponseLost");
        Check(f.State.State == "outcomeUnknown" && f.State.RecoveryDestinations.Count == 1 && f.Adapter.Restores == 1, "Lost restore response retains destination reservation");
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, f.Adapter), "RecoveryRequired");
        f.Adapter.Mode = "missingDestination";
        await Reject(() => f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "ReconciliationIncomplete");
        f.Adapter.Mode = "ok";
        await f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter);
        Check(f.State.State == "reconciled" && f.State.RecoveryResolutions.Count == 1 && f.Adapter.Restores == 1, "Original and destination reconcile independently without replay");
        f = F("recovery-return"); f.Adapter.Mode = "lostResponse"; await Reject(() => f.Execute(), "FakeResponseLost");
        f.Adapter.Mode = "ok";
        await f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision,
            [new(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "new.ap21"))], f.Adapter);
        Check(f.State.State == "recoveryIntent" && f.State.Evidence.Any(e => e.Gate == "recovery.return"), "Returned restore remains reserved until independent reconciliation");
        f = F("corrupt-checkpoint"); f.Adapter.Mode = "lostResponse"; await Reject(() => f.Execute(), "FakeResponseLost");
        File.WriteAllText(Path.Combine(f.Root, f.State.Checkpoints[0].RelativePath), "tampered");
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision,
            [new(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "new.ap21"))], f.Adapter), "ArtifactChanged");
        Check(f.Adapter.Restores == 0, "Corrupt checkpoint refused before restore");
        f = F("tampered-baseline"); f.Adapter.Mode = "lostResponse"; await Reject(() => f.Execute(), "FakeResponseLost");
        Check(f.State.Checkpoints.Count == f.State.Plan.Resources.Count, "The worker's checkpoint shape is one pinned manifest per planned resource");
        var pinned = TestSuiteStyleJobs.Parse<PinnedCheckpoint>(f.Jobs.ReadBytes(f.State.Checkpoints[0]));
        File.WriteAllText(Path.Combine(f.Root, pinned.Preservation.RelativePath), "tampered"); f.Adapter.Mode = "ok";
        await Reject(() => f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "ArtifactChanged");
        Check(f.State.State == "outcomeUnknown", "A tampered checkpoint baseline cannot pass reconciliation or release the project");
        f = F("shared-mutation-contention");
        var legacy = new EngineeringMutationAdmissionStore(f.Root);
        legacy.Acquire(new EngineeringMutationScope("change_apply", new('a',64), new('b',64), "authored-session", new('c',64), 123, new('d',64),
            [new("project", f.Target.ProjectFilePath, "authoredMutation")]), new('e',64));
        await Reject(() => f.Execute(), "ResourceBusy");
        Check(f.Adapter.Opens == 0, "Other engineering domains block style before native lease");
        f = F("checkpoint-response-lost"); f.Adapter.Mode = "lostCheckpoint";
        await Reject(() => f.Execute(), "FakeResponseLost");
        Check(f.State.State == "outcomeUnknown" && f.Adapter.Runs == 0, "Checkpoint interruption cannot become dispatchable");
        await Reject(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision,
            [new(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "new.ap21"))], f.Adapter), "CheckpointRequired");
        f = F("reconcile-cleanup-failure"); f.Adapter.Mode = "lostResponse"; await Reject(() => f.Execute(), "FakeResponseLost");
        f.Adapter.Mode = "cleanupFailure";
        await Reject(() => f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, f.Adapter), "FakeVerificationFailure");
        Check(f.State.State == "outcomeUnknown", "Reconciliation cleanup failure cannot release resources");
        f = F("raw-corruption"); await f.Execute();
        var rawReference = f.State.Evidence.Single(e => e.Gate == "dispatch.rawResults").Artifact;
        File.WriteAllText(Path.Combine(f.Root, rawReference.RelativePath), "tampered");
        await RejectSync(() => f.Jobs.Results(f.Job.Job.Id, f.Job.Operations[0].Id), "ArtifactChanged");
        f = F("forged-classification");
        var forgedStore = new EngineeringOperationStore(f.Root);
        var forgedOp = forgedStore.Acquire(f.State.Id, 0);
        forgedOp = forgedStore.BeginCheckpoint(forgedOp.Id, forgedOp.Revision, () => f.Target);
        forgedOp = forgedStore.Checkpoint(forgedOp.Id, forgedOp.Revision, [f.Jobs.Retain(new { authoredArchive = true })]);
        forgedOp = forgedStore.Dispatch(forgedOp.Id, forgedOp.Revision, () => f.Target);
        var forgedRun = f.Jobs.ReadReview(f.Review).Runs[0].Run;
        NativeTestResult[] failedCaptures = [new("style", f.Definition.Manifest.Reference, "Error", 1, 0, [], true, true, true, "assertion")];
        var failedResults = TestSuiteResultNormalizer.Normalize(forgedRun, failedCaptures);
        var exactScope = TestSuiteScopeCoverage.Compare(forgedRun, [new("style", f.Definition.Manifest.Reference, f.Definition.Manifest.RequiredScope, [], true)]);
        var failedRaw = f.Jobs.Retain(failedCaptures);
        forgedOp = forgedStore.RecordDispatchEvidence(forgedOp.Id, forgedOp.Revision, new("dispatch.rawResults", failedRaw));
        var forgedResult = new TestSuiteStyleRetainedResult("passed", failedRaw, f.Jobs.Retain(failedResults), f.Jobs.Retain(exactScope),
            f.Jobs.Retain(TestSuiteScopeCoverage.Assess(forgedRun, failedResults, exactScope)));
        forgedStore.RecordDispatchEvidence(forgedOp.Id, forgedOp.Revision, new("dispatch.result", f.Jobs.Retain(forgedResult)));
        await RejectSync(() => f.Jobs.Results(f.Job.Job.Id, f.Job.Operations[0].Id), "TestSuiteArtifactMismatch");
        f = F("corrupt-input"); File.WriteAllText(Path.Combine(f.Root, f.State.Plan.Inputs[1].RelativePath), "tampered");
        await Reject(() => f.Execute(), "ArtifactChanged");
        Check(f.Adapter.Opens == 0, "Retained bundle corruption refused before native lease");
        f = F("host-fixture");
        File.WriteAllText(Path.Combine(directory, "host-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = f.Root, jobId = f.Job.Job.Id,
            review = f.Review, request = f.HostRequest }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(directory, "checks.json"), JsonSerializer.Serialize(new { checks = checks.Count, names = checks,
            nativeExecuted = false, nativeQualified = false }, BridgeProtocol.Json));
        return checks.Count + await WorkerChecks(Path.Combine(directory, "worker-boundary"));
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public TestSuiteStyleJobs Jobs { get; }
        public TestSuiteDefinitionCapture Definition { get; }
        public NativeTargetObservation Target { get; }
        public EngineeringArtifactReference Review { get; }
        public TestSuiteStyleJobStatus Job { get; }
        public FakeAdapter Adapter { get; }
        public EngineeringOperationRecord State => new EngineeringOperationStore(Root).Read(Job.Operations[0].Id);
        public JsonElement Result => JsonSerializer.SerializeToElement(Jobs.Results(Job.Job.Id, State.Id), BridgeProtocol.Json);
        public object HostRequest { get; }
        public Fixture(string root, int runs)
        {
            Root = root; Jobs = new(root);
            var reference = new EngineeringObjectReference(new("Project/Authored", "PLC/A", "Root", "Project", "project"), new("authored", new('a', 64)), new('b', 64));
            var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/Owned", Kind = "block" } };
            Definition = new("style", "style", "Authored", ".xml", reference with { Object = reference.Object with { ObjectPath = "Rules/Authored", Kind = "ruleSet" }, ContentSha256 = null },
                Encoding.UTF8.GetBytes("<AuthoredOnly/>") , "authored-xml", [scope], [], true, true, []);
            Target = new(reference, Path.Combine(root, "owned.ap21"), "authored-epoch", new('c', 64), new('d', 64), "authored-hardware", null, true, []);
            var probes = new[] { "testsuite.product", "testsuite.license", "testsuite.styleService", "testsuite.scope" }.Select(name => Jobs.Retain(new TestSuiteStyleProbeReceipt(name, "observed",
                EngineeringResourceKey.ForPath("project", Target.ProjectFilePath), Target.SessionEpoch, Target.AdapterSha256, Target.ProfileSha256,
                Target.Reference.Snapshot.SourceSha256, Target.Reference.ContentSha256!, Target.HardwareIdentity, Target.NativeIdentity))).ToArray();
            Target = Target with { Probes = probes.Select((p, i) => new TargetProbe(new[] { "testsuite.product", "testsuite.license", "testsuite.styleService", "testsuite.scope" }[i], "observed", p.Sha256)).ToArray() };
            var selections = Enumerable.Range(0, runs).Select(_ => new TestSuiteStyleSelection(["style"], [])).ToArray();
            Review = Jobs.Review([Definition], selections, Target, "authored-test-profile");
            Job = Jobs.Prepare(Review, Review.Sha256, "job"); Adapter = new(this, probes);
            HostRequest = new { target = Target, executionProfile = "authored-test-profile", definitions = new[] { new { manifest = Definition.Manifest, originalArtifact = Jobs.RetainBytes(Definition.OriginalBytes) } }, runs = selections };
        }
        public Task<TestSuiteStyleJobStatus> Execute(ITestSuiteStyleAdapter? adapter = null) => Jobs.ExecuteNext(Job.Job.Id, Review.Sha256, Job.Operations[0].Id, 0, adapter ?? Adapter);
    }

    private sealed record PinnedCheckpoint(EngineeringArtifactReference Archive, EngineeringArtifactReference Preservation);

    private sealed class FakeAdapter(Fixture fixture, EngineeringArtifactReference[] probes) : ITestSuiteStyleAdapter
    {
        public string Mode = "ok";
        public int Opens, Runs, Checkpoints, Observations, Restores;
        public bool ObservedDispatchIntent, RawRetainedBeforeVerify;
        public Func<Task>? BeforeRunReturn;
        public Action? AfterCheckpoint, AfterOpen;
        public void RequireQualification(TestSuiteStyleContext context, string action) { /* Authored fake only; never registered in Host. */ }
        public Task<ITestSuiteStyleSession> OpenExclusive(TestSuiteStyleContext context, CancellationToken token)
        { Opens++; AfterOpen?.Invoke(); return Task.FromResult<ITestSuiteStyleSession>(new Session(this, fixture, probes, context)); }
        private sealed class Session(FakeAdapter owner, Fixture f, EngineeringArtifactReference[] probes, TestSuiteStyleContext ctx) : ITestSuiteStyleSession
        {
            TestSuiteScopeObservation[] Scope(bool complete = true) => [new("style", f.Definition.Manifest.Reference, f.Definition.Manifest.RequiredScope, [], complete)];
            EngineeringArtifactReference ScopeArtifact(TestSuiteScopeObservation[] scope) => f.Jobs.Retain(new TestSuiteStyleScopeReceipt(ctx.Run.PlanSha256, "nativeReadback", scope));
            public Task<TestSuiteStyleObservation> Observe(CancellationToken token)
            {
                owner.Observations++; var target = f.Target; var originals = new Dictionary<string, byte[]> { ["style"] = f.Definition.OriginalBytes };
                string probeName = owner.Mode switch { "product" => "testsuite.product", "license" => "testsuite.license", _ => "testsuite.styleService" };
                if (owner.Mode is "product" or "license" or "service") target = target with { Probes = target.Probes.Select(p => p.Name == probeName ? p with { State = "unknown" } : p).ToArray() };
                if (owner.Mode == "staleEpoch") target = target with { SessionEpoch = "different" };
                if (owner.Mode == "staleSource") target = target with { Reference = target.Reference with { Snapshot = target.Reference.Snapshot with { SourceSha256 = new('f', 64) } } };
                if (owner.Mode == "staleProfile") target = target with { ProfileSha256 = new('f', 64) };
                if (owner.Mode == "staleProject") target = target with { ProjectFilePath = Path.Combine(f.Root, "another.ap21") };
                if (owner.Mode == "staleDefinition") originals["style"] = Encoding.UTF8.GetBytes("<Different/>");
                if (owner.Mode == "foreignProbe")
                {
                    var forged = f.Jobs.Retain(new TestSuiteStyleProbeReceipt("testsuite.product", "observed", "foreign", "foreign", new('c',64), new('d',64), new('a',64), new('b',64), "foreign", null));
                    probes = [forged, .. probes.Skip(1)]; target = target with { Probes = target.Probes.Select(p => p.Name == "testsuite.product" ? p with { EvidenceSha256 = forged.Sha256 } : p).ToArray() };
                }
                var scope = Scope(owner.Mode != "scope" && !(owner.Mode == "secondScope" && owner.Observations == 2));
                return Task.FromResult(new TestSuiteStyleObservation(target, originals, scope,
                    owner.Mode == "diagnosticScope" ? "diagnosticMentions" : "nativeReadback", owner.Mode == "scopeArtifact" ? ScopeArtifact(Scope(false)) : ScopeArtifact(scope), owner.Mode == "probeArtifact" ? [] : probes));
            }
            public Task<EngineeringArtifactReference[]> Checkpoint(CancellationToken token)
            {
                owner.Checkpoints++;
                if (new EngineeringOperationStore(f.Root).Read(ctx.Operation.Id).State != "checkpointIntent") throw new Exception("Missing checkpoint intent");
                if (owner.Mode == "lostCheckpoint") throw new BridgeException("FakeResponseLost", "Authored checkpoint interruption");
                // The worker's shape: one manifest per planned resource that pins the archive and the preservation baseline.
                var manifest = f.Jobs.Retain(new PinnedCheckpoint(f.Jobs.Retain(new { syntheticArchive = true, nativeExecuted = false }),
                    f.Jobs.Retain(new { authoredBaseline = "immutable inventory" })));
                owner.AfterCheckpoint?.Invoke(); return Task.FromResult(new[] { manifest });
            }
            public async Task<byte[]> Run(CancellationToken token)
            {
                owner.Runs++; owner.ObservedDispatchIntent = new EngineeringOperationStore(f.Root).Read(ctx.Operation.Id).State == "dispatchIntent";
                if (owner.BeforeRunReturn is not null) await owner.BeforeRunReturn();
                if (owner.Mode == "lostResponse") throw new BridgeException("FakeResponseLost", "Authored lost return");
                if (owner.Mode == "malformedRaw") return Encoding.UTF8.GetBytes("{broken");
                var capture = new NativeTestResult("style", f.Definition.Manifest.Reference, owner.Mode is "assertion" or "execution" ? "Error" : owner.Mode == "warning" ? "Warning" : "Success",
                    owner.Mode is "assertion" or "execution" ? 1 : 0, owner.Mode == "warning" ? 1 : 0, [], true, owner.Mode != "partialResults", true, owner.Mode == "assertion" ? "assertion" : null);
                string raw = JsonSerializer.Serialize(new[] { capture }, BridgeProtocol.Json);
                if (owner.Mode == "unknownRawField") raw = raw.Replace("\"state\":", "\"unreviewed\":true,\"state\":");
                if (owner.Mode == "duplicateRawField") raw = raw.Replace("\"state\":", "\"state\":\"Error\",\"state\":");
                return Encoding.UTF8.GetBytes(raw);
            }
            public Task<TestSuiteStyleVerification> Verify(CancellationToken token)
            {
                owner.RawRetainedBeforeVerify = new EngineeringOperationStore(f.Root).Read(ctx.Operation.Id).Evidence.Any(e => e.Gate == "dispatch.rawResults");
                if (owner.Mode == "verifyLost") throw new BridgeException("FakeVerificationFailure", "Authored incomplete verification");
                string[] names = ["project.compile", "project.preservation", "project.freshReopen", "resource.cleanup"];
                var gates = names.Select(name => new EngineeringGateEvidence(name, f.Jobs.Retain(new { authoredFakeObservation = true, gate = name }))).ToArray();
                var scope = Scope(owner.Mode != "postScope");
                return Task.FromResult(new TestSuiteStyleVerification(scope, "nativeReadback", ScopeArtifact(scope), owner.Mode == "missingGate" ? gates[..^1] : gates));
            }
            public Task<EngineeringArtifactReference> Restore(EngineeringRecoveryDestination[] destinations, CancellationToken token)
            {
                owner.Restores++;
                if (new EngineeringOperationStore(f.Root).Read(ctx.Operation.Id).State != "recoveryIntent") throw new Exception("Missing recovery intent");
                if (owner.Mode == "lostRestore") throw new BridgeException("FakeResponseLost", "Authored restore interruption");
                return Task.FromResult(f.Jobs.Retain(new { authoredFakeRestore = true, destinations }));
            }
            public Task<TestSuiteStyleReconciliation> Reconcile(CancellationToken token)
            {
                // Like the worker, compare against the baseline that the durable record pins through the manifest.
                if (ctx.Operation.Checkpoints.Count == 1) _ = f.Jobs.ReadBytes(TestSuiteStyleJobs.Parse<PinnedCheckpoint>(f.Jobs.ReadBytes(ctx.Operation.Checkpoints[0])).Preservation);
                var artifact = f.Jobs.Retain(new { authoredFakeIndependentObservation = true });
                return Task.FromResult(new TestSuiteStyleReconciliation([new(ctx.Operation.Plan.Resources[0].ResourceKey, "unchanged", null, artifact)],
                    owner.Mode == "missingDestination" ? [] : ctx.Operation.RecoveryDestinations.Select(d => new EngineeringRecoveryResolution(d.ResourceKey, d.RecoveryLocation, "retainedPartial", artifact)).ToArray()));
            }
            public ValueTask DisposeAsync() => owner.Mode == "cleanupFailure" ? ValueTask.FromException(new BridgeException("FakeVerificationFailure", "Authored lease cleanup failure")) : ValueTask.CompletedTask;
        }
    }
}
