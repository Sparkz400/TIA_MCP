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
            throw new InvalidOperationException("Expected " + code);
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
        // A later write on the same scratch is refused, naming this admission and the provisioning reconciliation route.
        try { store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64)); throw new Exception("Expected ResourceBusy"); }
        catch (BridgeOperationException error) when (error.Code == "ResourceBusy")
        {
            var blocking = error.Details!.Value.GetProperty("blockingAdmissions").EnumerateArray().Single(x => x.GetProperty("admissionId").GetString() == admissionId);
            Check(blocking.GetProperty("reconciliationTool").GetString() == "simulation_provision_reconcile" &&
                blocking.GetProperty("remediation").GetString()!.Contains("simulation_provision_reconcile"), "ResourceBusy routes a failed provisioning to simulation_provision_reconcile");
        }
        // Saving the scratch now would change the saved state the reconciliation compares, so project_save is refused before dispatch.
        int callsBeforeSave = native.Calls.Count;
        try { await dispatcher.CallAsync("project_save", new { }); throw new Exception("Expected ResourceBusy for project_save"); }
        catch (BridgeOperationException error) when (error.Code == "ResourceBusy")
        {
            Check(error.Details!.Value.GetProperty("blockingAdmissionIds").EnumerateArray().Any(x => x.GetString() == admissionId), "project_save is refused while a provisioning admission holds the project");
        }
        Check(native.Calls.Count == callsBeforeSave, "A refused project_save never reaches the worker");

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
                ["archiveHashMatches"] = true, ["cardManifestMatches"] = true, ["projectFingerprintMatches"] = true, ["projectSavedBeforeArchive"] = true,
                ["storageMatchesBaseline"] = true, ["credentialNeverArmed"] = true, ["observerProjectEmpty"] = true, ["observerProjectAbsent"] = false };
            change(native.Verification);
            return await dispatcher.CallAsync("simulation_provision_reconcile", new { admissionId });
        }
        foreach (var (label, change) in new (string, Action<JsonObject>)[] {
            ("project differs", v => v["projectFingerprintMatches"] = false),
            ("card differs", v => v["cardManifestMatches"] = false),
            ("archive changed", v => v["archiveHashMatches"] = false),
            ("observer not empty", v => v["observerProjectEmpty"] = false),
            ("saved after the archive", v => v["projectSavedBeforeArchive"] = false),
            ("storage differs from its inventory", v => v["storageMatchesBaseline"] = false),
            ("credential may have been submitted", v => v["credentialNeverArmed"] = false),
            ("save time missing", v => v.Remove("projectSavedBeforeArchive")),
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

        // simulation_provision_status finds the held attempt from the Host's records and lists the recovery first.
        var status = SimulationProvisionRecovery.Status(root);
        var attempt = status["attempts"]!.AsArray().Single(a => a!["admissionId"]!.GetValue<string>() == admissionId)!;
        Check(attempt["stage"]!.GetValue<string>() == "held" && attempt["projectPath"]!.GetValue<string>() == project &&
            attempt["storagePath"]!.GetValue<string>() == storage && attempt["storage"]!["provisionReports"]!.GetValue<int>() == 2 &&
            attempt["nextSteps"]![0]!["tool"]!.GetValue<string>() == "simulation_provision_recover" &&
            attempt["nextSteps"]!.AsArray().Any(x => x!["tool"]!.GetValue<string>() == "project_save"), "Status names the held attempt, its storage and the exact next calls");
        await Reject(() => Task.FromResult(SimulationProvisionRecovery.Status(root, maxItems: 0)), "InvalidRequest");

        // simulation_provision_recover refuses before detaching while an open project has unsaved edits.
        native.Session = new JsonObject { ["attached"] = true, ["projects"] = new JsonArray(new JsonObject { ["name"] = "p", ["path"] = project, ["isModified"] = true }) };
        int callsBefore = native.Calls.Count;
        await Reject(() => SimulationProvisionRecovery.Recover(dispatcher, root, admissionId, CancellationToken.None), "UnsavedProject");
        Check(!native.Calls.Skip(callsBefore).Contains("detach"), "Recover never detaches over unsaved edits");
        // A held reconciliation stops after it: detach, start and reconcile ran, the project is not reopened.
        native.Session = new JsonObject { ["attached"] = true, ["projects"] = new JsonArray() };
        native.Verification = new JsonObject { ["scopeSha256"] = scopeHash, ["processId"] = 456, ["nativeMutationInvoked"] = false,
            ["archiveHashMatches"] = true, ["cardManifestMatches"] = true, ["projectFingerprintMatches"] = false, ["projectSavedBeforeArchive"] = true,
            ["storageMatchesBaseline"] = true, ["credentialNeverArmed"] = true, ["observerProjectEmpty"] = true, ["observerProjectAbsent"] = false };
        callsBefore = native.Calls.Count;
        var heldRecovery = await SimulationProvisionRecovery.Recover(dispatcher, root, admissionId, CancellationToken.None);
        var recoveryCalls = native.Calls.Skip(callsBefore).ToArray();
        Check(heldRecovery["recovered"]!.GetValue<bool>() == false && heldRecovery["held"]!.GetValue<bool>() &&
            heldRecovery["stoppedAt"]!.GetValue<string>() == "simulation_provision_reconcile" &&
            recoveryCalls.SequenceEqual(new[] { "session_status", "detach", "start", "simulation_provision_reconcile_verify" }), "A held reconciliation stops recovery before reopening");
        Check(store.Read(admissionId).State == "outcomeUnknown", "A held recovery leaves the admission held");

        // A full match: recover releases the admission and reopens the scratch project.
        native.Verification["projectFingerprintMatches"] = true;
        callsBefore = native.Calls.Count;
        var recovered = await SimulationProvisionRecovery.Recover(dispatcher, root, admissionId, CancellationToken.None);
        Check(recovered["recovered"]!.GetValue<bool>() && native.Calls.Skip(callsBefore).Last() == "project_open" &&
            native.Opened == project && recovered["status"]!["stage"]!.GetValue<string>() == "released", "Recover releases the admission and reopens the scratch project");
        Check(!native.Calls.Any(c => c is "sim_create" or "simulation_download" or "simulation_provision_scratch_credential" or "project_save"),
            "Recover never creates, downloads, provisions or saves");
        await Reject(() => SimulationProvisionRecovery.Recover(dispatcher, root, admissionId, CancellationToken.None), "InvalidOperationState");
        Check(SimulationProvisionRecovery.Status(root, admissionId)["attempts"]![0]!["nextSteps"]![0]!["tool"]!.GetValue<string>() == "project_open",
            "A released attempt starts the fresh-instance steps");
        var record = store.Read(admissionId);
        Check(record.State == "reconciled" && record.Reconciliation.Count == 3 &&
            record.Reconciliation.Single(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", project)).Disposition == "unchanged" &&
            record.Reconciliation.Single(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", observer)).Disposition == "verifiedApplied", "Each resource carries its own evidence");
        Check(native.Calls.Count(c => c.StartsWith("mutation_", StringComparison.Ordinal)) == 3, "Reconciliation never replays provisioning");
        var next = store.Acquire(scope with { ScopeSha256 = new('e', 64) }, new('a', 64));
        Check(next.State == "dispatchIntent", "Released resources can be admitted again");
        await Reject(() => dispatcher.CallAsync("simulation_provision_reconcile", new { admissionId }), "InvalidOperationState");

        // An observer destination that was never created releases as unchanged.
        var absent = SimulationProvisionReconciliation.Plan(root, new EngineeringMutationAdmission("y", 1, "outcomeUnknown", scope with { ScopeSha256 = scopeHash },
            new('a', 64), [], null, DateTimeOffset.UtcNow, null, []));
        var verification = new JsonObject { ["scopeSha256"] = scopeHash, ["processId"] = 456, ["nativeMutationInvoked"] = false, ["archiveHashMatches"] = true,
            ["cardManifestMatches"] = true, ["projectFingerprintMatches"] = true, ["projectSavedBeforeArchive"] = true, ["storageMatchesBaseline"] = true,
            ["credentialNeverArmed"] = true, ["observerProjectEmpty"] = false, ["observerProjectAbsent"] = true };
        Check(absent.Refusals(verification).Count == 0, "An absent observer destination does not hold the release");
        Check(absent.Dispositions(verification, (role, basis) => new EngineeringArtifactReference(role, new string('0', 64)))
            .Single(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", observer)).Disposition == "unchanged", "An absent observer destination is unchanged");

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
        internal JsonObject Session = new() { ["attached"] = false, ["projects"] = new JsonArray() };
        internal string? Opened;
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
                "session_status" => Session.DeepClone(),
                "detach" or "start" => new { },
                "project_open" => Open(arguments),
                _ => throw new Exception("Unexpected native operation: " + operation)
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(value, BridgeProtocol.Json));
        }

        private object Open(object? arguments)
        {
            Opened = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!["path"]!.GetValue<string>();
            return new { opened = true };
        }
    }
}
