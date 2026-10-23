using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

internal static partial class ProductionJobChecks
{
    private static async Task<int> WorkerChecks(string root)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); }
        async Task Refused(Func<Task> work, string code)
        {
            try { await work(); } catch (BridgeException error) when (error.Code == code) { checks.Add(code); return; }
            throw new Exception("Expected " + code);
        }
        Fixture Make(string name, int count = 1) => new(Path.Combine(root, name), count);
        var f = Make("production"); var wire = new WorkerWire(f);
        await Refused(() => f.Execute(new TestSuiteProductionAdapter(wire)), "TestSuiteProfileUnqualified");
        Check(wire.Calls.Count == 0 && f.State.State == "prepared", "Wired production gate remains before journal acquisition and worker launch");

        f = Make("complete"); wire = new(f); await f.Execute(wire.Adapter);
        Check(wire.Calls.SequenceEqual(new[] { "begin", "observe", "checkpoint", "observe", "run", "verify", "end" }), "All lifecycle stages use one bridge and one exact lease");
        Check(wire.States.SequenceEqual(new[] { "acquired", "acquired", "checkpointIntent", "ready", "dispatchIntent", "dispatchIntent" }), "Worker receives current flushed journal revision at every boundary");
        Check(f.State.State == "succeeded" && f.Adapter.RawRetainedBeforeVerify, "Transport retains exact raw bytes before preservation and reopen verification");
        Check(wire.Ends == 1, "Normal native lease is ended exactly once");

        foreach (var item in new[] {
            ("product", "TargetPrerequisiteMissing"), ("license", "TargetPrerequisiteMissing"), ("service", "TargetPrerequisiteMissing"),
            ("scope", "TestSuiteScopeIncomplete"), ("diagnosticScope", "TestSuiteScopeIncomplete"), ("scopeArtifact", "TestSuiteScopeIncomplete"),
            ("staleEpoch", "StaleNativeTarget"), ("staleSource", "StaleContent"), ("staleDefinition", "StaleContent"),
            ("foreignProbe", "TargetPrerequisiteMissing"), ("probeArtifact", "TargetPrerequisiteMissing") })
        {
            f = Make(item.Item1); f.Adapter.Mode = item.Item1; wire = new(f);
            await Refused(() => f.Execute(wire.Adapter), item.Item2);
            Check(!wire.Calls.Contains("checkpoint") && !wire.Calls.Contains("run") && wire.Ends == 1, item.Item1 + " refuses before effects and ends exact lease");
        }
        foreach (var item in new[] { ("lostCheckpoint", "outcomeUnknown"), ("lostResponse", "outcomeUnknown"), ("verifyLost", "recoveryRequired") })
        {
            f = Make(item.Item1); f.Adapter.Mode = item.Item1; wire = new(f);
            await Refused(() => f.Execute(wire.Adapter), item.Item1 == "verifyLost" ? "FakeVerificationFailure" : "FakeResponseLost");
            Check(f.State.State == item.Item2 && wire.Ends == 1, item.Item1 + " retains unknown/failed disposition across transport");
            int calls = wire.Calls.Count;
            await Refused(() => f.Jobs.ExecuteNext(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, wire.Adapter), "RecoveryRequired");
            Check(wire.Calls.Count == calls, item.Item1 + " cannot repeat begin or native stage");
        }
        foreach (string mode in new[] { "malformedRaw", "unknownRawField", "duplicateRawField" })
        {
            f = Make(mode); f.Adapter.Mode = mode; wire = new(f);
            await Refused(() => f.Execute(wire.Adapter), "InvalidTestSuiteArtifact");
            var raw = f.State.Evidence.Single(e => e.Gate == "dispatch.rawResults").Artifact;
            Check(f.Jobs.ReadBytes(raw).SequenceEqual(wire.Raw!), mode + " survives worker-file transport byte for byte");
        }
        f = Make("stop", 2); wire = new(f);
        f.Adapter.BeforeRunReturn = () => { f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256); return Task.CompletedTask; };
        await f.Execute(wire.Adapter);
        Check(f.Jobs.Status(f.Job.Job.Id).Operations[1].State == "stopped" && wire.Calls.Count(c => c == "run") == 1, "Persistent stop while call active does not cancel it or dispatch next run");

        f = Make("restore"); wire = new(f); f.Adapter.Mode = "lostResponse";
        await Refused(() => f.Execute(wire.Adapter), "FakeResponseLost");
        f.Adapter.Mode = "ok";
        var destinations = new[] { new EngineeringRecoveryDestination(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "new.ap21")) };
        await f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, wire.Adapter);
        Check(wire.Calls.TakeLast(3).SequenceEqual(new[] { "begin", "restore", "end" }) && f.State.State == "recoveryIntent", "Recovery gets separate exact worker lease and remains reserved");
        await f.Jobs.Reconcile(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, wire.Adapter);
        Check(wire.Calls.TakeLast(3).SequenceEqual(new[] { "begin", "reconcile", "end" }) && f.State.State == "reconciled", "Independent original/destination observations use owning session without replay");

        f = Make("restoreLost"); wire = new(f); f.Adapter.Mode = "lostResponse";
        await Refused(() => f.Execute(wire.Adapter), "FakeResponseLost"); f.Adapter.Mode = "lostRestore";
        destinations = [new(f.State.Plan.Resources[0].ResourceKey, Path.Combine(f.Root, "new.ap21"))];
        await Refused(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, wire.Adapter), "FakeResponseLost");
        await Refused(() => f.Jobs.Recover(f.Job.Job.Id, f.Review.Sha256, f.State.Id, f.State.Revision, destinations, wire.Adapter), "RecoveryRequired");
        Check(f.Adapter.Restores == 1 && f.State.State == "outcomeUnknown", "Lost restore response never repeats retrieval");

        f = Make("cleanup"); wire = new(f) { FailEnd = true };
        await Refused(() => f.Execute(wire.Adapter), "NativeCleanupUnverified");
        Check(f.State.State == "recoveryRequired" && wire.Ends == 1, "Unproved worker lease release prevents successful resource release");
        f = Make("stopBeforeCheckpoint", 2); wire = new(f);
        f.Adapter.AfterOpen = () => f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256);
        await f.Execute(wire.Adapter);
        Check(f.State.State == "stopped" && wire.EndStates.SequenceEqual(new[] { "acquired" }) &&
            !wire.Calls.Contains("checkpoint") && !wire.Calls.Contains("run"),
            "Stop before checkpoint retains durable ownership until worker cleanup returns");
        f = Make("stopBeforeCheckpointCleanupLost", 2); wire = new(f) { FailEnd = true };
        f.Adapter.AfterOpen = () => f.Jobs.RequestStop(f.Job.Job.Id, f.Review.Sha256);
        await Refused(() => f.Execute(wire.Adapter), "NativeCleanupUnverified");
        Check(f.State.State == "outcomeUnknown" && new EngineeringOperationStore(f.Root).Pending().Any(o => o.Id == f.State.Id) &&
            f.Jobs.Status(f.Job.Job.Id).Operations[1].State == "stopped" && wire.Ends == 1 &&
            !wire.Calls.Contains("checkpoint") && !wire.Calls.Contains("run"),
            "Failed cleanup during stop preserves the active reservation and stops untouched runs");
        var stopCompetitor = f.Jobs.Prepare(f.Review, f.Review.Sha256, "competitor");
        await Refused(() => f.Jobs.ExecuteNext(stopCompetitor.Job.Id, f.Review.Sha256,
            stopCompetitor.Operations[0].Id, 0, wire.Adapter), "ResourceBusy");

        foreach (bool lostResponse in new[] { false, true })
        {
            f = Make(lostResponse ? "repeatLostEnd" : "repeatRefusedEnd");
            wire = new(f) { FailEnd = !lostResponse, LoseEnd = lostResponse };
            var operation = new EngineeringOperationStore(f.Root).Acquire(f.State.Id, 0);
            var run = f.Jobs.ReadReview(f.Review).Runs[0].Run;
            var bundle = TestSuiteDefinitionBundle.Restore(run, f.Jobs.ReadBytes(operation.Plan.Inputs[1]));
            var held = await wire.Adapter.OpenExclusive(new(f.Root, run, bundle, operation, "authored-test-profile"), default);
            await Refused(() => held.DisposeAsync().AsTask(), lostResponse ? "FakeResponseLost" : "NativeCleanupUnverified");
            await Refused(() => held.DisposeAsync().AsTask(), "NativeCleanupUnverified");
            Check(wire.Ends == 1, "Uncertain end stays failed without repeating worker release: " + lostResponse);
        }
        f = Make("corruptRaw"); wire = new(f) { CorruptRaw = true };
        await Refused(() => f.Execute(wire.Adapter), "ArtifactChanged");
        Check(f.State.State == "outcomeUnknown" && !wire.Calls.Contains("verify"), "Raw artifact mismatch cannot become a normalized result");
        f = Make("badLease"); wire = new(f) { BadLease = true };
        await Refused(() => f.Execute(wire.Adapter), "InvalidLease");
        Check(f.State.State == "outcomeUnknown" && wire.Calls.SequenceEqual(new[] { "begin" }), "Ambiguous begin response retains ownership without retry or guessed cleanup token");

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(new { checks = checks.Count, names = checks, nativeExecuted = false }, BridgeProtocol.Json));
        return checks.Count;
    }

    private sealed class WorkerWire(Fixture f) : IBridgeClient
    {
        public List<string> Calls = [], States = [], EndStates = [];
        public byte[]? Raw;
        public int Ends;
        public bool FailEnd, LoseEnd, CorruptRaw, BadLease;
        private ITestSuiteStyleSession? session;
        private string? operationId;
        public ITestSuiteStyleAdapter Adapter => new TestSuiteWorkerAdapter(this, (_, _) => { }); // Authored fake admission only.
        public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            string stage = operation.Replace("testsuite_style_", ""); Calls.Add(stage);
            var args = JsonSerializer.SerializeToElement(arguments, BridgeProtocol.Json);
            if (stage == "begin")
            {
                var op = args.GetProperty("operation").Deserialize<EngineeringOperationRecord>(BridgeProtocol.Json)!;
                var run = args.GetProperty("run").Deserialize<TestSuiteRunPlan>(BridgeProtocol.Json)!;
                var bundle = TestSuiteDefinitionBundle.Restore(run, args.GetProperty("definitionBundle").GetBytesFromBase64());
                States.Add(op.State); operationId = op.Id;
                session = await f.Adapter.OpenExclusive(new(f.Root, run, bundle, op, args.GetProperty("executionProfile").GetString()!), cancellationToken);
                return JsonSerializer.SerializeToElement(new { leaseToken = "authored-lease", operationId = BadLease ? "foreign-operation" : op.Id, op.PlanSha256 }, BridgeProtocol.Json);
            }
            if (session is null || args.GetProperty("leaseToken").GetString() != "authored-lease") throw new Exception("Wrong owning worker lease");
            if (stage == "end")
            {
                Ends++; if (args.GetProperty("operationId").GetString() != operationId) throw new Exception("Wrong end identity");
                EndStates.Add(new EngineeringOperationStore(f.Root).Read(operationId!).State);
                await session.DisposeAsync(); session = null;
                if (LoseEnd) throw new BridgeException("FakeResponseLost", "Authored loss after worker lease release");
                return JsonSerializer.SerializeToElement(new { released = !FailEnd, operationId }, BridgeProtocol.Json);
            }
            var current = args.GetProperty("operation").Deserialize<EngineeringOperationRecord>(BridgeProtocol.Json)!;
            States.Add(current.State);
            if (current.Id != operationId || current.Revision != new EngineeringOperationStore(f.Root).Read(current.Id).Revision) throw new Exception("Stale worker intent");
            if (stage is "checkpoint" or "run" or "verify" or "restore")
                if (cancellationToken.CanBeCanceled) throw new Exception("Native irrevocable stages cannot receive caller cancellation");
            object value;
            switch (stage)
            {
                case "observe": value = await session.Observe(cancellationToken); break;
                case "checkpoint": value = await session.Checkpoint(cancellationToken); break;
                case "run":
                    Raw = await session.Run(cancellationToken); var retained = f.Jobs.RetainBytes(Raw);
                    if (CorruptRaw) File.WriteAllText(Path.Combine(f.Root, retained.RelativePath), "changed");
                    value = new { rawResults = retained }; break;
                case "verify": value = await session.Verify(cancellationToken); break;
                case "restore": value = await session.Restore(args.GetProperty("payload").Deserialize<EngineeringRecoveryDestination[]>(BridgeProtocol.Json)!, cancellationToken); break;
                case "reconcile": value = await session.Reconcile(cancellationToken); break;
                default: throw new Exception(stage);
            }
            return JsonSerializer.SerializeToElement(new { value }, BridgeProtocol.Json);
        }
    }
}
