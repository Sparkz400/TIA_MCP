using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class AdmissionReconciliationChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; }
            throw new Exception("Expected " + code);
        }
        string root = Path.Combine(Path.GetTempPath(), "admission-reconcile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string project = Path.Combine(root, "p", "p.ap21"), journal = Path.Combine(root, "change-" + new string('1', 32)), input = Path.Combine(journal, "requested.scl");
        var scope = new EngineeringMutationScope("change_apply", new('b', 64), new('c', 64), "worker-a", new('d', 64), 123, new('f', 64), [
            new("project", project, "checkpointMutationCompileSave"), new("workspace", journal, "legacyJournalCheckpointOutput"),
            new("workspace", input, "reviewedMutationInput")]);
        var native = new ReconcileNative(scope);
        var dispatcher = new WatchTableDispatcher(native, root);
        var store = new EngineeringMutationAdmissionStore(root);
        string admissionId;
        try { await dispatcher.CallAsync("change_apply", new { }); throw new Exception("Expected interrupted apply"); }
        catch (BridgeOperationException error) { admissionId = error.Details!.Value.GetProperty("mutationAdmission").GetProperty("admissionId").GetString()!; }
        Check(store.Read(admissionId).State == "outcomeUnknown", "Interrupted apply leaves an outcomeUnknown admission");

        JsonObject Fresh() => new()
        {
            ["scopeSha256"] = scope.ScopeSha256, ["processId"] = 456, ["nativeMutationInvoked"] = false, ["originalProcessRunning"] = false,
            ["resources"] = new JsonArray(scope.Resources.Select(r => (JsonNode)new JsonObject { ["kind"] = r.Kind, ["path"] = r.Path, ["effect"] = r.Effect,
                ["disposition"] = r.Kind == "project" ? "unchanged" : "verifiedApplied", ["reasons"] = new JsonArray() }).ToArray())
        };
        async Task<JsonElement> Reconcile(Action<JsonObject> change)
        {
            native.Verification = Fresh(); change(native.Verification);
            return await dispatcher.CallAsync("engineering_admission_reconcile", new { admissionId });
        }
        JsonObject Row(JsonObject v, string effect) => v["resources"]!.AsArray().OfType<JsonObject>().Single(r => r["effect"]!.GetValue<string>() == effect);
        foreach (var (label, change) in new (string, Action<JsonObject>)[] {
            ("project not proven", v => { Row(v, "checkpointMutationCompileSave")["disposition"] = null; Row(v, "checkpointMutationCompileSave")["reasons"] = new JsonArray("differs from checkpoint"); }),
            ("disposition outside the allowed set", v => Row(v, "reviewedMutationInput")["disposition"] = "restored"),
            ("original TIA process still running", v => v["originalProcessRunning"] = true),
            ("process state not reported", v => v.Remove("originalProcessRunning")),
            ("same Portal process", v => v["processId"] = 123),
            ("no process reported", v => v.Remove("processId")),
            ("other scope", v => v["scopeSha256"] = new string('9', 64)),
            ("mutation claimed", v => v["nativeMutationInvoked"] = true),
            ("resource missing", v => v["resources"]!.AsArray().RemoveAt(0)),
            ("effect rewritten", v => Row(v, "reviewedMutationInput")["effect"] = "readCheckpoint") })
        {
            var held = await Reconcile(change);
            Check(!held.GetProperty("released").GetBoolean() && held.GetProperty("held").GetBoolean() && held.GetProperty("reasons").GetArrayLength() >= 1, "Held when " + label);
            Check(store.Read(admissionId).State == "outcomeUnknown", "Reservation stays owned when " + label);
        }
        var withReason = await Reconcile(v => { Row(v, "checkpointMutationCompileSave")["disposition"] = null; Row(v, "checkpointMutationCompileSave")["reasons"] = new JsonArray("differs from checkpoint"); });
        Check(withReason.GetProperty("reasons")[0].GetString()!.Contains("differs from checkpoint"), "Native hold reasons reach the caller");
        var request = native.Requests.Last();
        Check(request["operation"]!.GetValue<string>() == "change_apply" && request["admittedProcessId"]!.GetValue<int>() == 123 &&
            request["resources"]!.AsArray().Count == 3 && request["observationSha256"]!.GetValue<string>() == scope.ObservationSha256, "Native request carries the exact admitted scope");
        Check(DateTimeOffset.Parse(request["admittedAtUtc"]!.GetValue<string>()) == store.AdmittedAt(admissionId) &&
            store.AdmittedAt(admissionId) < store.Read(admissionId).RecordedAt, "Write-time evidence is bound to the dispatch intent, not the later unknown outcome");
        try { store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64)); throw new Exception("Expected ResourceBusy"); }
        catch (BridgeException error) when (error.Code == "ResourceBusy") { passed++; }

        var released = await Reconcile(_ => { });
        Check(released.GetProperty("released").GetBoolean(), "Every resource proven releases the admission");
        var record = store.Read(admissionId);
        Check(record.State == "reconciled" && record.Reconciliation.Count == 3 && record.Reconciliation.Select(r => r.Evidence.RelativePath).Distinct().Count() == 3 &&
            record.Reconciliation.Single(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", project)).Disposition == "unchanged", "Each resource carries its own evidence");
        Check(native.Calls.Count(c => c == "mutation_dispatch") == 1, "Reconciliation never replays the mutation");
        Check(store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64)).State == "dispatchIntent", "Released resources can be admitted again");
        await Reject(() => dispatcher.CallAsync("engineering_admission_reconcile", new { admissionId }), "InvalidOperationState");

        // A Host that died after committing intent leaves dispatchIntent; it is reconcilable on the same evidence.
        string orphanRoot = Path.Combine(root, "orphan"); Directory.CreateDirectory(orphanRoot);
        var orphanStore = new EngineeringMutationAdmissionStore(orphanRoot);
        var orphan = orphanStore.Acquire(scope, new('a', 64));
        var orphanDispatcher = new WatchTableDispatcher(native, orphanRoot);
        native.Verification = Fresh();
        var orphanResult = await orphanDispatcher.CallAsync("engineering_admission_reconcile", new { admissionId = orphan.Id });
        Check(orphanResult.GetProperty("released").GetBoolean() && orphanStore.Read(orphan.Id).State == "reconciled", "A dispatchIntent left by a dead Host is released on full evidence");

        var unsupported = orphanStore.Acquire(scope with { Operation = "watch_fixture_seed", ScopeSha256 = new('7', 64), Resources = [new("project", Path.Combine(root, "w.ap21"), "ownedWatchFixtureSeed")] }, new('a', 64));
        await Reject(() => orphanDispatcher.CallAsync("engineering_admission_reconcile", new { admissionId = unsupported.Id }), "ReconciliationUnsupported");
        var provisioning = orphanStore.Acquire(scope with { Operation = SimulationProvisionReconciliation.Operation, ScopeSha256 = new('8', 64), Resources = [
            new("project", Path.Combine(root, "s.ap21"), "simulationCompilationOrCredentialPreparation"), new("workspace", Path.Combine(root, "storage"), "ownedSimulatorStorageAndCard")] }, new('a', 64));
        await Reject(() => orphanDispatcher.CallAsync("engineering_admission_reconcile", new { admissionId = provisioning.Id }), "InvalidOperationState");
        Check(native.Calls.Count(c => c == "engineering_admission_reconcile_verify") == 13, "Unsupported and provisioning admissions never reach the generic native verifier");
        Console.WriteLine($"PASS Admission reconciliation: {passed} checks; releases only when every resource is proven, otherwise held; no replay.");
    }

    private sealed class ReconcileNative(EngineeringMutationScope scope) : IBridgeClient
    {
        internal List<string> Calls { get; } = [];
        internal List<JsonObject> Requests { get; } = [];
        internal JsonObject Verification = new();
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            if (operation == "mutation_dispatch") throw new BridgeException("WorkerDisconnected", "Synthetic interrupted apply");
            if (operation == "engineering_admission_reconcile_verify") Requests.Add(JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject());
            object value = operation switch {
                "mutation_begin" => new { leaseToken = "synthetic-lease", scope },
                "mutation_end" => new { released = true },
                "engineering_admission_reconcile_verify" => Verification.DeepClone(),
                _ => throw new Exception("Unexpected native operation: " + operation)
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }
    }
}
