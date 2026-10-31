using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

internal static class EngineeringOperationChecks
{
    internal static void Probe(string[] args)
    {
        try
        {
            var store = new EngineeringOperationStore(args[1]);
            var r = store.Read(args[2]);
            if (args.Length > 3) r = store.Acquire(r.Id, r.Revision);
            Console.WriteLine(r.State);
        }
        catch (BridgeException e) { Console.WriteLine(e.Code); }
    }

    internal static void Run()
    {
        LongPaths();
        int passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { passed++; return; }
            throw new Exception("Expected " + code);
        }
        string root = Path.Combine(Path.GetTempPath(), "engineering-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        EngineeringArtifactReference Artifact(string name, string content)
        {
            File.WriteAllText(Path.Combine(root, name), content);
            return new(name, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))));
        }
        var input = Artifact("input.json", "{\"reviewed\":true}");
        var checkpoint = Artifact("before.zap21", "synthetic checkpoint bytes - no native archive claim");
        var proof = Artifact("proof.json", "synthetic independent expected evidence");
        string project = Path.Combine(root, "owned", "fixture.ap21");
        var reference = new EngineeringObjectReference(new("/projects/fixture", "/plc/one", "/root", "/watch/one", "watchTable"), new("capture-a", new('a', 64)), new('b', 64));
        var target = new NativeTargetObservation(reference, project, "worker-epoch-1", new('c', 64), new('d', 64), "CPU/FW/owner", null, true,
            [new("watchExport", "observed", new('e', 64))]);
        var fresh = target with { Reference = reference with { Snapshot = reference.Snapshot with { ObservationId = "capture-b" } } };
        NativeTargetGuard.EnsureFresh(target, fresh, ["watchExport"]); passed++;
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { SessionEpoch = "worker-epoch-2" }, ["watchExport"]), "StaleNativeTarget");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { HardwareIdentity = "other" }, []), "StaleNativeTarget");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { ProjectFilePath = Path.Combine(root, "other.ap21") }, []), "StaleNativeTarget");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { NativeIdentity = "invented" }, []), "StaleNativeTarget");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { Offline = false }, []), "StaleNativeTarget");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { Reference = reference with { ContentSha256 = null } }, []), "StaleContent");
        Reject(() => NativeTargetGuard.EnsureFresh(target, fresh with { Probes = [new("watchExport", "empty", new('e', 64))] }, ["watchExport"]), "TargetPrerequisiteMissing");
        string key = EngineeringResourceKey.ForPath("project", project);
        Reject(() => EngineeringResourceKey.ForPath("project", "relative.ap21"), "InvalidResourceKey");
        if (OperatingSystem.IsWindows()) Check(key == EngineeringResourceKey.ForPath("project", project.ToUpperInvariant()), "Case aliases reserve the same native project");
        var plan = new EngineeringOperationPlan("watch.update", target,
            [new(key, "project", "update one row", "projectArchiveToNewLocation")], [input], ["watchExport"], ["readback", "preservation", "freshReopen"]);
        var store = new EngineeringOperationStore(root);
        var prepared = store.Prepare(plan, "review-1");
        Check(store.Prepare(plan, "review-1").Id == prepared.Id, "Idempotency survives prepare replay");
        Reject(() => store.Prepare(plan with { Operation = "other" }, "review-1"), "IdempotencyConflict");
        Reject(() => store.Prepare(plan with { Resources = [] }, "invalid"), "InvalidOperationPlan");
        if (OperatingSystem.IsWindows()) Reject(() => store.Prepare(plan with { Resources = [new("project:" + project, "project", "write", "projectArchiveToNewLocation")] }, "invalid-key"), "InvalidResourceKey");
        var acquired = store.Acquire(prepared.Id, 0);
        Check(acquired.State == "acquired", "Durable resource reservation acquired");
        Check(Child(root, prepared.Id) == "acquired", "Separate process reads durable admission");
        var contender = store.Prepare(plan, "review-2");
        Check(Child(root, contender.Id, true) == "ResourceBusy", "Separate process cannot steal a live/durable resource reservation");
        Reject(() => store.Acquire(prepared.Id, 0), "RevisionConflict");
        Reject(() => store.Dispatch(acquired.Id, acquired.Revision, () => fresh), "RecoveryRequired");
        Reject(() => store.BeginCheckpoint(acquired.Id, acquired.Revision, () => fresh with { SessionEpoch = "wrong" }), "StaleNativeTarget");
        Check(store.Read(acquired.Id).State == "acquired", "Stale target refusal precedes checkpoint intent");
        var intent = store.BeginCheckpoint(acquired.Id, acquired.Revision, () => fresh);
        Reject(() => store.Stop(intent.Id, intent.Revision), "RecoveryRequired");
        Reject(() => store.Checkpoint(intent.Id, intent.Revision, []), "CheckpointRequired");
        var ready = store.Checkpoint(intent.Id, intent.Revision, [checkpoint]);
        File.AppendAllText(Path.Combine(root, input.RelativePath), "stale");
        Reject(() => store.Dispatch(ready.Id, ready.Revision, () => fresh), "ArtifactChanged");
        File.WriteAllText(Path.Combine(root, input.RelativePath), "{\"reviewed\":true}");
        var dispatched = store.Dispatch(ready.Id, ready.Revision, () => fresh);
        Check(Child(root, dispatched.Id) == "dispatchIntent", "Separate process sees durable native intent before any result");
        Reject(() => new EngineeringOperationStore(root).Dispatch(dispatched.Id, dispatched.Revision, () => fresh), "RecoveryRequired");
        var unknown = new EngineeringOperationStore(root).MarkUnknown(dispatched.Id, dispatched.Revision);
        Check(Child(root, contender.Id, true) == "ResourceBusy", "Unknown outcome retains ownership across restart");
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, []), "ReconciliationIncomplete");
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, [new(key, "restoredToNewLocation", project, proof)]), "InvalidRecovery");
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, [new(key, "externalCommitRetained", null, proof)]), "InvalidRecovery");
        string recoveryPath = Path.Combine(root, "recovered", "copy.ap21");
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, [new(key, "restoredToNewLocation", recoveryPath, proof)]), "RecoveryAdmissionRequired");
        unknown = store.ReserveRecovery(unknown.Id, unknown.Revision, [new(key, recoveryPath)]);
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, [new(key, "unchanged", null, proof)]), "ReconciliationIncomplete");
        var recovered = store.Reconcile(unknown.Id, unknown.Revision, [new(key, "restoredToNewLocation", recoveryPath, proof)], [new(key, recoveryPath, "verifiedRestored", proof)]);
        Check(recovered.State == "reconciled", "Explicit independently evidenced recovery releases reservation");
        Check(Directory.GetFiles(Path.Combine(root, "engineering-operations", recovered.Id), "*.json").Length == recovered.Revision + 1, "Failed/unknown history remains immutable");
        var second = store.Acquire(contender.Id, contender.Revision);
        second = store.BeginCheckpoint(second.Id, second.Revision, () => fresh);
        second = store.Checkpoint(second.Id, second.Revision, [checkpoint]);
        second = store.Dispatch(second.Id, second.Revision, () => fresh);
        Reject(() => store.RecordOutcome(second.Id, second.Revision, true, [new("readback", proof)]), "VerificationIncomplete");
        Reject(() => store.RecordOutcome(second.Id, second.Revision, false, []), "FailureEvidenceRequired");
        var failed = store.RecordOutcome(second.Id, second.Revision, false, [new("failure", proof)]);
        Check(failed.State == "recoveryRequired", "Failure does not imply rollback");
        var third = store.Prepare(plan, "review-3");
        Reject(() => store.Acquire(third.Id, third.Revision), "ResourceBusy");
        store.Reconcile(failed.Id, failed.Revision, [new(key, "unchanged", null, proof)]);
        third = store.Acquire(third.Id, third.Revision);
        third = store.BeginCheckpoint(third.Id, third.Revision, () => fresh);
        third = store.Checkpoint(third.Id, third.Revision, [checkpoint]);
        third = store.Dispatch(third.Id, third.Revision, () => fresh);
        third = store.RecordOutcome(third.Id, third.Revision, true, plan.RequiredGates.Select(g => new EngineeringGateEvidence(g, proof)).ToArray());
        Check(third.State == "succeeded", "Success requires every named evidence gate");
        var stop = store.Prepare(plan, "review-stop");
        stop = store.Acquire(stop.Id, stop.Revision); stop = store.Stop(stop.Id, stop.Revision);
        Check(stop.State == "stopped", "Stop before native intent is safe and durable");
        string workspace = EngineeringResourceKey.ForPath("workspace", Path.Combine(root, "owned"));
        var widePlan = plan with { Resources = [plan.Resources[0], new(workspace, "workspace", "publish files", "immutableWorkspaceVersion")] };
        var wide = store.Prepare(widePlan, "wide"); wide = store.Acquire(wide.Id, wide.Revision);
        var otherProject = plan with { Target = target with { ProjectFilePath = Path.Combine(root, "owned", "other.ap21") }, Resources = [new(EngineeringResourceKey.ForPath("project", Path.Combine(root, "owned", "other.ap21")), "project", "write", "projectArchiveToNewLocation")] };
        var overlapping = store.Prepare(otherProject, "overlap");
        Reject(() => store.Acquire(overlapping.Id, overlapping.Revision), "ResourceBusy");
        wide = store.BeginCheckpoint(wide.Id, wide.Revision, () => fresh);
        Reject(() => store.Checkpoint(wide.Id, wide.Revision, [checkpoint]), "CheckpointRequired");
        using (var held = new FileStream(Path.Combine(root, "engineering-operations", "store.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(Child(root, wide.Id) == "IntakeBusy", "OS lock serializes separate processes");
        string history = Path.Combine(root, "engineering-operations", third.Id, "000000.json");
        string original = File.ReadAllText(history);
        File.WriteAllText(history, original.Replace("watch.update", "watch.delete"));
        Reject(() => store.Read(third.Id), "JournalInvalid");
        File.WriteAllText(history, original);
        string tail = Path.Combine(root, "engineering-operations", wide.Id, wide.Revision.ToString("D6") + ".json");
        string tailOriginal = File.ReadAllText(tail);
        File.WriteAllText(tail, tailOriginal.Replace("checkpointIntent", "succeeded"));
        Reject(() => store.Read(wide.Id), "JournalInvalid");
        File.WriteAllText(tail, tailOriginal);
        Directory.CreateDirectory(Path.Combine(root, "engineering-operations", ".preparing-" + Guid.NewGuid().ToString("N")));
        Check(store.Prepare(plan, "after-interrupted-prepare").State == "prepared", "Interrupted staging cannot wedge unrelated admission");
        wide = store.MarkUnknown(wide.Id, wide.Revision);
        Reject(() => store.ReserveRecovery(wide.Id, wide.Revision, [new(key, project)]), "InvalidRecovery");
        Reject(() => store.ReserveRecovery(wide.Id, wide.Revision, [new(key, Path.Combine(root, "uncheckpointed-copy.ap21"))]), "CheckpointRequired");
        if (OperatingSystem.IsWindows())
        {
            string originalFile = Path.Combine(root, "hard-link-original.ap21"), aliasFile = Path.Combine(root, "hard-link-alias.ap21");
            File.WriteAllText(originalFile, "authored filesystem identity fixture");
            if (!CreateHardLink(aliasFile, originalFile, IntPtr.Zero)) throw new Exception("Could not create the isolated hard-link regression fixture.");
            Reject(() => EngineeringResourceKey.ForPath("project", originalFile), "InvalidResourceKey");
            Reject(() => EngineeringResourceKey.ForPath("project", aliasFile), "InvalidResourceKey");
        }
        // A scan reads a finished record for its chain only: a succeeded operation's deleted checkpoint archive no longer
        // blocks every later admission, while reading that operation by id still refuses.
        string finishedRoot = Path.Combine(root, "finished"); Directory.CreateDirectory(finishedRoot);
        foreach (string name in new[] { input.RelativePath, checkpoint.RelativePath, proof.RelativePath }) File.Copy(Path.Combine(root, name), Path.Combine(finishedRoot, name));
        var finishedStore = new EngineeringOperationStore(finishedRoot);
        EngineeringOperationPlan Owned(string name)
        {
            string path = Path.Combine(finishedRoot, name + ".ap21");
            return plan with { Target = target with { ProjectFilePath = path }, Resources = [new(EngineeringResourceKey.ForPath("project", path), "project", "write", "projectArchiveToNewLocation")] };
        }
        EngineeringOperationRecord Succeed(string name)
        {
            var p = Owned(name); var r = finishedStore.Prepare(p, name); r = finishedStore.Acquire(r.Id, r.Revision);
            r = finishedStore.BeginCheckpoint(r.Id, r.Revision, () => p.Target); r = finishedStore.Checkpoint(r.Id, r.Revision, [checkpoint]);
            r = finishedStore.Dispatch(r.Id, r.Revision, () => p.Target);
            return finishedStore.RecordOutcome(r.Id, r.Revision, true, p.RequiredGates.Select(g => new EngineeringGateEvidence(g, proof)).ToArray());
        }
        var finished = Succeed("finished");
        File.Delete(Path.Combine(finishedRoot, checkpoint.RelativePath));
        var later = finishedStore.Prepare(Owned("later"), "later");
        Check(finishedStore.Acquire(later.Id, later.Revision).State == "acquired", "A succeeded operation whose checkpoint archive is gone does not block unrelated admission");
        Reject(() => finishedStore.Read(finished.Id), "ArtifactChanged");
        File.Copy(Path.Combine(root, checkpoint.RelativePath), Path.Combine(finishedRoot, checkpoint.RelativePath));
        int bound = EngineeringOperationStore.ArchiveAbove; EngineeringOperationStore.ArchiveAbove = 0;
        try
        {
            var archived = Succeed("archived");
            Check(!Directory.Exists(Path.Combine(finishedRoot, "engineering-operations", archived.Id)) && finishedStore.Read(archived.Id).State == "succeeded",
                "A finished operation leaves the store the growth bound counts and still reads by id");
            Check(finishedStore.Prepare(Owned("archived"), "archived").Id == archived.Id, "An archived operation keeps its idempotency key");
            var recovering = finishedStore.ReserveRecovery(archived.Id, archived.Revision, [new(archived.Plan.Resources[0].ResourceKey, Path.Combine(finishedRoot, "recovered.ap21"))]);
            var rival = finishedStore.Prepare(Owned("archived"), "archived-contender");
            Reject(() => finishedStore.Acquire(rival.Id, rival.Revision), "ResourceBusy");
            Check(recovering.State == "recoveryIntent" && Directory.Exists(Path.Combine(finishedRoot, "engineering-operations", archived.Id)), "Recovery of an archived success owns its resources in the scanned store again");
        }
        finally { EngineeringOperationStore.ArchiveAbove = bound; }
        Console.WriteLine($"PASS Native target guards and durable engineering operations: {passed} checks; synthetic callbacks only, no native execution. Receipts retained at {root}");
    }

    internal static void LongPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root=Path.Combine(Path.GetTempPath(),"resource-path-"+Guid.NewGuid().ToString("N"));
        string directory=root;
        while(directory.Length<320)directory=Path.Combine(directory,new string('a',60));
        Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"journal.json");File.WriteAllText(file,"{}");
        string expected="workspace:"+Path.GetFullPath(file).ToUpperInvariant();
        if(EngineeringResourceKey.ForPath("workspace",file)!=expected || EngineeringResourceKey.ForPath("workspace",file.ToUpperInvariant())!=expected)
            throw new Exception("Long local file aliases must retain one native resource identity");
        string absent=Path.Combine(directory,"future","journal.json");
        if(EngineeringResourceKey.ForPath("workspace",absent)!="workspace:"+absent.ToUpperInvariant())
            throw new Exception("Long missing descendant must resolve from its existing ancestor");
        foreach(string prefix in new[]{@"\\?\",@"\\.\"})
        {
            try { EngineeringResourceKey.ForPath("workspace",prefix+file); }
            catch(BridgeException error) when(error.Code=="InvalidResourceKey") { continue; }
            throw new Exception("Caller-supplied device namespaces must remain refused");
        }
        Console.WriteLine("PASS long resource paths: existing file, case alias, absent descendant and refused device namespaces");
    }

    private static string Child(string root, string id, bool acquire = false)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(EngineeringOperationChecks).Assembly.Location); start.ArgumentList.Add("--operation-probe");
        start.ArgumentList.Add(root); start.ArgumentList.Add(id); if (acquire) start.ArgumentList.Add("acquire");
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Durable child process failed: " + error);
        return output.Trim();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
