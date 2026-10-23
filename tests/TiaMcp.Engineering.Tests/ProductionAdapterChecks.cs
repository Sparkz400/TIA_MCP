using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class ProductionAdapterChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); }
            catch (BridgeException error) when (error.Code == code) { passed++; return; }
            throw new Exception("Expected refusal: " + code);
        }
        async Task<Fixture> Prepare(string name)
        {
            string root = Path.Combine(Path.GetTempPath(), "production-adapter-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root);
            var bridge = new InterceptBridge(native);
            var dispatcher = new WatchTableDispatcher(bridge, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = name }));
            return new(root, native, bridge, dispatcher, new(root), prepared["operation"]!["id"]!.GetValue<string>(), prepared["operation"]!["planSha256"]!.GetValue<string>());
        }
        foreach (string code in new[] { "TimeoutOutcomeUnknown", "WorkerDisconnected", "CancelledOutcomeUnknown" })
        {
            var f = await Prepare("begin-" + code);
            f.Bridge.After = (operation, response) => operation == "watch_begin" ? throw new BridgeException(code, "Injected lost lease reply") : response;
            await Reject(() => f.Apply(), code);
            var record = new EngineeringOperationStore(f.Root).Read(f.Id);
            Check(record.State == "outcomeUnknown" && record.Checkpoints.Count == 0 && record.Evidence.Count == 1,
                "Lost lease reply retains unknown ownership and diagnostics before checkpointing.");
            Check(f.Native.CheckpointCount == 0 && f.Native.ApplyCount == 0, "Lost begin cannot invoke archive or import.");
            var contender = f.Store.Prepare(record.Plan, "competing-owner");
            await Reject(() => Task.FromResult(f.Store.Acquire(contender.Id, contender.Revision)), "ResourceBusy");
            var restarted = new WatchTableDispatcher(f.Bridge, f.Root);
            await Reject(() => restarted.CallAsync("watch_apply", f.ApplyArguments), "RecoveryRequired");
            await Reject(() => restarted.CallAsync("watch_recover", new { operationId = f.Id }), "CheckpointRequired");
            f.Bridge.After = null; f.Native.NewInstance();
            await restarted.CallAsync("watch_reconcile_interrupted", new { operationId = f.Id });
            Check(f.Store.Read(f.Id).State == "reconciled" && f.Store.Pending().Count == 0 && f.Native.ApplyCount == 0,
                "A fresh baseline observation resolves lost begin without replay or invented archive.");
            f.Store.Acquire(contender.Id, contender.Revision);
            Check(f.Store.Pending().Single().Id == contender.Id, "A competing mutation is admitted only after readback reconciliation.");
        }
        foreach (string boundary in new[] { "watch_begin", "watch_checkpoint" })
        foreach (var change in new (string Name, string Code, Action<JsonObject> Alter)[] {
            ("project", "IdentityMismatch", s => s["observation"]!["reference"]!["object"]!["projectPath"] = Path.Combine(Path.GetTempPath(), "other.ap21")),
            ("physical-project", "StaleNativeTarget", s => s["observation"]!["projectFilePath"] = Path.Combine(Path.GetTempPath(), "other.ap21")),
            ("plc", "IdentityMismatch", s => s["observation"]!["reference"]!["object"]!["plcPath"] = "/other-plc"),
            ("object", "IdentityMismatch", s => s["observation"]!["reference"]!["object"]!["objectPath"] = "/watch/other"),
            ("epoch", "StaleNativeTarget", s => s["observation"]!["sessionEpoch"] = "new-session"),
            ("content", "StaleContent", s => s["observation"]!["reference"]!["contentSha256"] = new string('1', 64)),
            ("manifest", "StaleContent", s => s["observation"]!["reference"]!["snapshot"]!["sourceSha256"] = new string('1', 64))
        })
        {
            var f = await Prepare(boundary + change.Name);
            f.Bridge.After = (operation, response) => {
                if (operation == boundary) change.Alter(boundary == "watch_checkpoint" ? response["snapshot"]!.AsObject() : response);
                return response;
            };
            await Reject(() => f.Apply(), change.Code);
            Check(f.Native.ApplyCount == 0 && f.Native.CheckpointCount == (boundary == "watch_begin" ? 0 : 1),
                "Stale " + change.Name + " refuses at " + boundary + " before the next native effect.");
            Check(f.Store.Read(f.Id).State == (boundary == "watch_begin" ? "stopped" : "ready"),
                "A known pre-checkpoint refusal stops only after lease release; an archived refusal retains ownership.");
        }

        var release = await Prepare("stale-then-lost-release");
        release.Bridge.After = (operation, response) => {
            if (operation == "watch_begin") response["observation"]!["sessionEpoch"] = "stale";
            if (operation == "watch_end") throw new BridgeException("WorkerDisconnected", "Lost release response");
            return response;
        };
        await Reject(() => release.Apply(), "StaleNativeTarget");
        Check(release.Store.Read(release.Id).State == "outcomeUnknown" && release.Store.Pending().Count == 1,
            "A lost lease release cannot convert stale refusal into unowned stopped state.");

        var interruptedBegin = await Prepare("host-exit-after-acquire");
        interruptedBegin.Store.Acquire(interruptedBegin.Id, 0);
        var resumed = new WatchTableDispatcher(interruptedBegin.Bridge, interruptedBegin.Root);
        await Reject(() => resumed.CallAsync("watch_apply", interruptedBegin.ApplyArguments), "RecoveryRequired");
        await Reject(() => resumed.CallAsync("watch_reconcile_interrupted", new { operationId = interruptedBegin.Id }), "FreshInstanceRequired");
        Check(interruptedBegin.Store.Read(interruptedBegin.Id).State == "outcomeUnknown", "Interrupted acquisition is conservatively unknown on explicit reconciliation.");
        interruptedBegin.Native.NewInstance();
        await resumed.CallAsync("watch_reconcile_interrupted", new { operationId = interruptedBegin.Id });
        Check(interruptedBegin.Store.Pending().Count == 0 && interruptedBegin.Native.CheckpointCount == 0, "Restart after acquisition resolves by fresh read only.");

        var verificationLoss = await Prepare("lost-reopen-read");
        await verificationLoss.Apply(); verificationLoss.Native.NewInstance();
        verificationLoss.Bridge.After = (operation, reply) => operation == "watch_read_native" ? throw new BridgeException("WorkerDisconnected", "Lost fresh read") : reply;
        await Reject(() => verificationLoss.Dispatcher.CallAsync("watch_verify_reopen", new { operationId = verificationLoss.Id }), "WorkerDisconnected");
        Check(verificationLoss.Store.Read(verificationLoss.Id).State == "outcomeUnknown" && verificationLoss.Store.Read(verificationLoss.Id).Evidence.Any(e => e.Gate == "dispatch.readback"),
            "Fresh read loss marks unknown while retaining earlier apply evidence.");
        verificationLoss.Bridge.After = null; verificationLoss.Native.NewInstance();
        await verificationLoss.Dispatcher.CallAsync("watch_reconcile_interrupted", new { operationId = verificationLoss.Id });
        Check(verificationLoss.Store.Read(verificationLoss.Id).State == "reconciled" && verificationLoss.Native.ApplyCount == 1, "Fresh reconciliation resolves verification loss without replay.");

        var badResult = await Prepare("mismatched-readback");
        badResult.Bridge.After = (operation, reply) => { if (operation == "watch_apply_native") reply["peerSha256"] = new string('1', 64); return reply; };
        await Reject(() => badResult.Apply(), "VerificationFailed");
        Check(badResult.Store.Read(badResult.Id).Evidence.Any(e => e.Gate == "dispatch.readback") && badResult.Store.Pending().Count == 1,
            "Mismatched native results remain hash-bound for diagnosis and cannot release ownership.");

        var workerError = await Prepare("native-error-details");
        workerError.Bridge.After = (operation, reply) => operation == "watch_apply_native" ? throw new BridgeOperationException("VerificationFailed", "Native consistency failure", JsonSerializer.SerializeToElement(new { nativeDiagnostic = "retained" })) : reply;
        await Reject(() => workerError.Apply(), "VerificationFailed");
        var failed = workerError.Store.Read(workerError.Id);
        var failureArtifact = failed.Evidence.Single(e => e.Gate == "failure").Artifact;
        var diagnostics = JsonNode.Parse(File.ReadAllBytes(Path.Combine(workerError.Root, failureArtifact.RelativePath)))!;
        Check(failed.State == "recoveryRequired" && diagnostics["code"]!.GetValue<string>() == "VerificationFailed" && diagnostics["details"]!["nativeDiagnostic"]!.GetValue<string>() == "retained",
            "Worker failure retains structured native diagnostics, not just its message.");

        var applied = await Prepare("durable-readback");
        var response = Node(await applied.Apply());
        var result = new EngineeringOperationStore(applied.Root).Read(applied.Id);
        var retained = result.Evidence.SingleOrDefault(e => e.Gate == "dispatch.readback");
        Check(retained is not null && response["evidence"]!.Deserialize<EngineeringArtifactReference>(BridgeProtocol.Json) == retained.Artifact,
            "Applied native readback is reachable by hash from operation status after restart.");
        await Reject(() => Task.FromResult(applied.Store.RecordDispatchEvidence(result.Id, result.Revision, retained!)), "EvidenceAlreadyRecorded");
        await Reject(() => Task.FromResult(applied.Store.RecordDispatchEvidence(result.Id, result.Revision - 1, new("dispatch.another", retained!.Artifact))), "RevisionConflict");
        await Reject(() => Task.FromResult(applied.Store.Dispatch(result.Id, result.Revision, () => result.Plan.Target)), "RecoveryRequired");
        var anotherHost = new WatchTableDispatcher(applied.Bridge, applied.Root);
        await Reject(() => anotherHost.CallAsync("watch_apply", applied.ApplyArguments), "RecoveryRequired");
        Check(applied.Native.ApplyCount == 1, "Duplicate dispatch through another Host never invokes import twice.");
        applied.Native.NewInstance();
        await anotherHost.CallAsync("watch_verify_reopen", new { operationId = applied.Id });
        result = applied.Store.Read(applied.Id);
        Check(result.State == "succeeded" && result.Evidence.Contains(retained!), "Fresh reopen appends gates while retaining the original apply result.");
        await anotherHost.CallAsync("watch_recover", new { operationId = applied.Id });
        applied.Native.NewInstance();
        await anotherHost.CallAsync("watch_reconcile_recovery", new { operationId = applied.Id, role = "restored" });
        applied.Native.SelectOriginal();
        await anotherHost.CallAsync("watch_reconcile_recovery", new { operationId = applied.Id, role = "original" });
        Check(applied.Store.Read(applied.Id).State == "reconciled" && applied.Store.Read(applied.Id).Evidence.Contains(retained!) && applied.Native.RestoreCount == 1,
            "Reviewed edit, fresh reopen and checkpoint recovery preserve the original readback proof.");
        File.AppendAllText(Path.Combine(applied.Root, retained!.Artifact.RelativePath), "changed");
        await Reject(() => anotherHost.CallAsync("engineering_operation_status", new { operationId = applied.Id }), "ArtifactChanged");
        Console.WriteLine($"PASS Production adapter boundaries: {passed} checks; fake worker, durable restart/readback/recovery, no native execution.");
    }

    private sealed record Fixture(string Root, ProductionRecoveryChecks.FakeWatch Native, InterceptBridge Bridge,
        WatchTableDispatcher Dispatcher, EngineeringOperationStore Store, string Id, string Digest)
    {
        internal object ApplyArguments => new { operationId = Id, reviewedPlanSha256 = Digest };
        internal Task<JsonElement> Apply() => Dispatcher.CallAsync("watch_apply", ApplyArguments);
    }
    private sealed class InterceptBridge(IBridgeClient inner) : IBridgeClient
    {
        internal Func<string, JsonObject, JsonObject>? After;
        public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            var result = Node(await inner.CallAsync(operation, arguments, cancellationToken));
            return JsonSerializer.SerializeToElement(After?.Invoke(operation, result) ?? result, BridgeProtocol.Json);
        }
    }
    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
}
