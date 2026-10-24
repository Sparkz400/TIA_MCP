using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class ProvisionReconciliationChecks
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
        string root = Path.Combine(Path.GetTempPath(), "provision-reconcile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string storage = Path.Combine(root, "simulation", "TiaMcp_owned"), project = Path.Combine(root, "scratch", "p.ap21"), observer = Path.Combine(root, "do_observer");
        Directory.CreateDirectory(storage);
        string scopeHash = new('c', 64);
        var scope = new EngineeringMutationScope(SimulationProvisionReconciliation.Operation, new('b', 64), scopeHash, "worker-a", new('d', 64), 123, new('f', 64), [
            new("project", observer, "createNewDestination"), new("project", project, "simulationCompilationOrCredentialPreparation"),
            new("workspace", storage, "ownedSimulatorStorageAndCard")]);
        var native = new ProvisionNative(scope);
        var dispatcher = new WatchTableDispatcher(native, root);
        string admissionId;
        try { await dispatcher.CallAsync(SimulationProvisionReconciliation.Operation, new { }); throw new Exception("Expected provisioning failure"); }
        catch (BridgeOperationException error) { admissionId = error.Details!.Value.GetProperty("mutationAdmission").GetProperty("admissionId").GetString()!; }
        var store = new EngineeringMutationAdmissionStore(root);
        Check(store.Read(admissionId).State == "outcomeUnknown", "Failed provisioning leaves an outcomeUnknown admission");

        void Report(string name, string hash) => File.WriteAllText(Path.Combine(storage, name),
            new JsonObject { ["recoveryBaseline"] = new JsonObject { ["nativeScope"] = new JsonObject { ["scopeSha256"] = hash } } }.ToJsonString());
        await Reject(() => dispatcher.CallAsync("simulation_provision_reconcile", new { admissionId }), "ReconciliationIncomplete");
        Check(!native.Calls.Contains("simulation_provision_reconcile_verify"), "No native verification without the admission's own report");
        Report("simulation-provision-other.json", new('9', 64));
        Report("simulation-provision-bound.json", scopeHash);
        File.WriteAllText(Path.Combine(storage, "simulation-provision-bound.checkpoints.jsonl"), "{}");

        async Task<JsonElement> Reconcile(Action<JsonObject> change)
        {
            native.Verification = new JsonObject { ["scopeSha256"] = scopeHash, ["processId"] = 456, ["nativeMutationInvoked"] = false,
                ["archiveHashMatches"] = true, ["cardManifestMatches"] = true, ["projectFingerprintMatches"] = true, ["observerProjectEmpty"] = true };
            change(native.Verification);
            return await dispatcher.CallAsync("simulation_provision_reconcile", new { admissionId });
        }
        foreach (var (label, change) in new (string, Action<JsonObject>)[] {
            ("project differs", v => v["projectFingerprintMatches"] = false),
            ("card differs", v => v["cardManifestMatches"] = false),
            ("archive changed", v => v["archiveHashMatches"] = false),
            ("observer not empty", v => v["observerProjectEmpty"] = false),
            ("same Portal process", v => v["processId"] = 123),
            ("other scope", v => v["scopeSha256"] = new string('9', 64)),
            ("mutation claimed", v => v["nativeMutationInvoked"] = true) })
        {
            var held = await Reconcile(change);
            Check(!held.GetProperty("released").GetBoolean() && held.GetProperty("reasons").GetArrayLength() == 1, "Held when " + label);
            Check(store.Read(admissionId).State == "outcomeUnknown", "Reservation stays owned when " + label);
        }
        Check(native.Requests.All(r => r["reportPath"]!.GetValue<string>() == Path.Combine(storage, "simulation-provision-bound.json") &&
            r["observerProjectPath"]!.GetValue<string>() == observer && r["projectPath"]!.GetValue<string>() == project), "Verification is bound to the admission's own report, project and observer");
        try { store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64)); throw new Exception("Expected ResourceBusy"); }
        catch (BridgeException error) when (error.Code == "ResourceBusy") { passed++; }

        var released = await Reconcile(_ => { });
        Check(released.GetProperty("released").GetBoolean(), "Full fresh match releases the admission");
        var record = store.Read(admissionId);
        Check(record.State == "reconciled" && record.Reconciliation.Count == 3 &&
            record.Reconciliation.Single(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", project)).Disposition == "unchanged", "Each resource carries its own evidence");
        Check(native.Calls.Count(c => c.StartsWith("mutation_", StringComparison.Ordinal)) == 3, "Reconciliation never replays provisioning");
        var next = store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64));
        Check(next.State == "dispatchIntent", "Released resources can be admitted again");
        await Reject(() => dispatcher.CallAsync("simulation_provision_reconcile", new { admissionId }), "InvalidOperationState");

        var unsupported = scope with { ScopeSha256 = new('7', 64), Resources = [.. scope.Resources, new("library", Path.Combine(root, "lib"), "readSourceAndCompanions")] };
        await Reject(() => Task.FromResult(SimulationProvisionReconciliation.Plan(root, new EngineeringMutationAdmission("x", 1, "outcomeUnknown", unsupported,
            new('a', 64), [], null, DateTimeOffset.UtcNow, null, []))), "ReconciliationUnsupported");
        Console.WriteLine($"PASS Provisioning reconciliation: {passed} checks; releases only on full fresh evidence, otherwise held; no native execution.");
    }

    private sealed class ProvisionNative(EngineeringMutationScope scope) : IBridgeClient
    {
        internal List<string> Calls { get; } = [];
        internal List<JsonObject> Requests { get; } = [];
        internal JsonObject Verification = new();
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            if (operation == "mutation_dispatch") throw new BridgeOperationException("SimulationProvisionCleanupFailed", "Synthetic failed provisioning", null);
            if (operation == "simulation_provision_reconcile_verify")
                Requests.Add(JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject());
            object value = operation switch {
                "mutation_begin" => new { leaseToken = "synthetic-lease", scope },
                "mutation_end" => new { released = true },
                "simulation_provision_reconcile_verify" => Verification.DeepClone(),
                _ => throw new Exception("Unexpected native operation: " + operation)
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }
    }
}
