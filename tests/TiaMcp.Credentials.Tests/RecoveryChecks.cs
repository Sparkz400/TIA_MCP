using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.Security;
using TiaMcp.Host;

internal static class RecoveryChecks
{
    internal static void LostReply(string[] args)
    {
        var store = new EngineeringOperationStore(args[1]); var r = store.Read(args[3]);
        var reviewed = JsonSerializer.Deserialize<NativeTargetObservation>(File.ReadAllBytes(args[4]), BridgeProtocol.Json)!;
        new CredentialVault(args[2]).RecoverOperation(args[1], r.Id, r.Revision, [], reviewed, () => reviewed, values =>
        {
            var admitted = store.Read(r.Id);
            if (admitted.State != "recoveryIntent" || admitted.RecoveryTarget?.SessionEpoch != reviewed.SessionEpoch || values.Count != 1 || !values.ContainsKey("previous"))
                throw new Exception("Recovery intent/context missing");
            using var output = new FileStream(Path.Combine(args[1], "recovery-calls.txt"), FileMode.Append, FileAccess.Write, FileShare.None);
            output.Write(Encoding.UTF8.GetBytes("one synthetic recovery\n")); output.Flush(true);
            File.WriteAllBytes(Path.Combine(args[1], "provider-state.json"), File.ReadAllBytes(Path.Combine(args[1], "before.json")));
            Environment.Exit(18);
        });
    }

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root); var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        void Reject(Action action, string code, string label)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { Check(e.InnerException is null, label); return; }
            throw new Exception("Expected " + code + ": " + label);
        }
        string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        EngineeringArtifactReference Save(string name, object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json); File.WriteAllBytes(Path.Combine(root, name), bytes); return new(name, Hash(bytes));
        }
        var before = Save("before.json", new { synthetic = true, version = 1 }); var after = Save("after.json", new { synthetic = true, version = 2 });
        var projectCheckpoint = Save("project-checkpoint.json", new { syntheticProjectArchive = true });
        string provider = Path.Combine(root, "provider-state.json"); File.Copy(Path.Combine(root, before.RelativePath), provider);
        var store = new EngineeringOperationStore(root); string vaultRoot = Path.Combine(root, "vault"); var vault = new CredentialVault(vaultRoot);
        var binding = new CredentialBinding(new("fixture", "service", "owner", "account", "operator"), "recovery-fixture", "operator");
        byte[] currentSecret = Encoding.UTF8.GetBytes("SYNTHETIC-CURRENT-" + Guid.NewGuid().ToString("N"));
        byte[] priorSecret = Encoding.UTF8.GetBytes("SYNTHETIC-PRIOR-" + Guid.NewGuid().ToString("N"));
        var current = vault.Create(binding, currentSecret); var prior = vault.Create(binding, priorSecret);
        var owner = binding.Resource with { ResourceKind = null, ResourceId = null }; string externalKey = EngineeringResourceKey.ForExternal(owner);
        EngineeringArtifactReference Observe(EngineeringOperationRecord r, string phase)
        {
            byte[] bytes = File.ReadAllBytes(provider); string name = "state-" + Hash(bytes) + ".json"; File.WriteAllBytes(Path.Combine(root, name), bytes);
            var state = new EngineeringArtifactReference(name, Hash(bytes));
            return Save(Guid.NewGuid().ToString("N") + ".json", new EngineeringExternalObservation("engineering-external-observation.v1", r.Id, r.PlanSha256, r.Revision,
                externalKey, phase, Guid.NewGuid().ToString("N"), new("present", state.Sha256), state));
        }
        EngineeringOperationRecord Unknown(string name)
        {
            string project = Path.Combine(root, name + ".ap21");
            var obj = new EngineeringObjectReference(new(project, "/CPU", "/root", "/CPU", "hardware"), new("before", before.Sha256), before.Sha256);
            var target = new NativeTargetObservation(obj, project, "original-epoch", new('c',64), new('d',64), "owner/CPU", null, true, [new("access", "observed", before.Sha256)]);
            var plan = new EngineeringOperationPlan("fixture.recover", target,
                [new(EngineeringResourceKey.ForPath("project", project), "project", "fixture", "projectArchiveToNewLocation"),
                 new(externalKey, "external", "fixture", "externalStateReadback") { External = new(owner, new("present", before.Sha256), new("present", after.Sha256)) }],
                [before], ["access"], ["project", EngineeringExternalEvidence.OutcomeGate(externalKey)])
            { Credentials = [new("current", "dispatch", current.Reference, binding), new("previous", "recovery", prior.Reference, binding)] };
            File.WriteAllBytes(provider, File.ReadAllBytes(Path.Combine(root, before.RelativePath)));
            var r = store.Prepare(plan, name); r = vault.AcquireOperation(root, r.Id, r.Revision);
            r = vault.BeginOperationCheckpoint(root, r.Id, r.Revision, () => r.Plan.Target);
            r = store.Checkpoint(r.Id, r.Revision, [projectCheckpoint, Observe(r, "checkpoint")]);
            r = vault.DispatchOperation(root, r.Id, r.Revision, () => r.Plan.Target, _ => File.WriteAllBytes(provider, File.ReadAllBytes(Path.Combine(root, after.RelativePath))));
            return store.MarkUnknown(r.Id, r.Revision);
        }
        NativeTargetObservation Review(EngineeringOperationRecord r) => r.Plan.Target with
        {
            SessionEpoch = "recovery-epoch", Reference = r.Plan.Target.Reference with { Snapshot = new("recovery-review", after.Sha256), ContentSha256 = after.Sha256 },
            Probes = [new("access", "observed", after.Sha256)]
        };
        EngineeringOperationRecord Reconcile(EngineeringOperationRecord r) => store.Reconcile(r.Id, r.Revision,
            [new(r.Plan.Resources[0].ResourceKey, "unchanged", null, before), new(externalKey, "externalBaselineObserved", null, Observe(r, "reconciliation"))]);

        var first = Unknown("changed-content"); var reviewed = Review(first); int invoked = 0; byte[]? borrowed = null;
        Reject(() => NativeTargetGuard.EnsureFresh(first.Plan.Target, reviewed, ["access"]), "StaleNativeTarget", "Ordinary dispatch cannot use the changed recovery session");
        Reject(() => store.ReserveRecovery(first.Id, first.Revision, [], _ => { }), "RecoveryObservationRequired", "Trusted credential verification alone cannot authorize unbound recovery");
        foreach (var wrong in new[] { reviewed with { AdapterSha256 = new('f',64) }, reviewed with { ProfileSha256 = new('f',64) }, reviewed with { HardwareIdentity = "peer" },
            reviewed with { NativeIdentity = "peer" }, reviewed with { Offline = false }, reviewed with { ProjectFilePath = Path.Combine(root,"peer.ap21") } })
            Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], wrong, () => wrong, _ => invoked++), "StaleNativeTarget", "Recovery cannot change owner/profile/context");
        foreach (var wrong in new[] { reviewed with { SessionEpoch = "changed-again" }, reviewed with { ProfileSha256 = new('f',64) } })
            Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => wrong, _ => invoked++), "StaleNativeTarget", "Reacquisition must match the reviewed recovery context");
        Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => reviewed with { Reference = reviewed.Reference with { ContentSha256 = before.Sha256 } }, _ => invoked++),
            "StaleContent", "Recovery content cannot change between review and admission");
        Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => reviewed with { Probes = [] }, _ => invoked++),
            "TargetPrerequisiteMissing", "Required recovery probe must be freshly observed");
        Check(invoked == 0 && store.Read(first.Id).Revision == first.Revision, "All context refusals precede intent and consumer");
        string priorFile = Path.Combine(vaultRoot, prior.Reference.CredentialId + ".credential.json"); byte[] priorBytes = File.ReadAllBytes(priorFile);
        try
        {
            File.WriteAllText(priorFile, "{}");
            Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => throw new Exception("native callback reached"), _ => invoked++),
                "CredentialInputInvalid", "Unreadable recovery generation refuses before native callback");
        }
        finally { File.WriteAllBytes(priorFile, priorBytes); }
        string currentFile = Path.Combine(vaultRoot, current.Reference.CredentialId + ".credential.json"); byte[] currentBytes = File.ReadAllBytes(currentFile);
        string checkpointPath = Path.Combine(root, projectCheckpoint.RelativePath); byte[] checkpointBytes = File.ReadAllBytes(checkpointPath);
        try
        {
            File.WriteAllText(currentFile, "{}");
            File.Delete(checkpointPath);
            first = vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => reviewed with { Reference = reviewed.Reference with { Snapshot = reviewed.Reference.Snapshot with { ObservationId = "reacquired" } } }, values =>
            {
                invoked++; borrowed = values["previous"];
                Check(values.Count == 1 && borrowed.SequenceEqual(priorSecret), "Only the independently readable prior generation reaches recovery");
                var retained = store.Read(first.Id);
                Check(retained.State == "recoveryIntent" && retained.RecoveryTarget?.Reference.Snapshot.ObservationId == "reacquired", "Fresh external-only context is durable despite damaged dispatch ciphertext and an unavailable unselected project archive");
                File.WriteAllBytes(provider, File.ReadAllBytes(Path.Combine(root, before.RelativePath)));
            });
        }
        finally { File.WriteAllBytes(currentFile, currentBytes); File.WriteAllBytes(checkpointPath, checkpointBytes); }
        Check(invoked == 1 && borrowed!.All(b => b == 0) && first.State == "recoveryIntent", "Recovery return clears bytes and cannot claim reconciliation");
        foreach (var forged in new[] {
            first with { RecoveryDestinations = [new(first.Plan.Resources[0].ResourceKey, Path.Combine(root,"unadmitted-copy"))] },
            first with { RecoveryTarget = first.RecoveryTarget! with { SessionEpoch = "replacement" } },
            first with { RecoveryTarget = null } })
        {
            var next = forged with { Revision=first.Revision+1, PreviousSha256=CanonicalJson.Sha256(JsonSerializer.SerializeToNode(first,BridgeProtocol.Json)!), RecordedAt=DateTimeOffset.UtcNow };
            string path=Path.Combine(root,"engineering-operations",first.Id,next.Revision.ToString("D6")+".json");
            try
            {
                File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(next,BridgeProtocol.Json));
                Reject(()=>store.Read(first.Id),"JournalInvalid","A later revision cannot add a restore destination or replace/remove the admitted recovery context");
            }
            finally { File.Delete(path); }
        }
        Reject(() => vault.Retire(prior.Reference, binding), "CredentialInUse", "Recovery return retains both generations until independent evidence");
        Reject(() => vault.RecoverOperation(root, first.Id, first.Revision, [], reviewed, () => reviewed, _ => invoked++), "RecoveryRequired", "Returned recovery cannot be replayed");
        Reject(() => store.Reconcile(first.Id, first.Revision, [new(first.Plan.Resources[0].ResourceKey, "unchanged", null, before)]),
            "ReconciliationIncomplete", "Local proof alone cannot clear the external recovery");
        first = Reconcile(first);
        Check(first.RecoveryTarget is not null && first.State == "reconciled", "Independent endpoint readback retains admitted recovery context");

        var lost = Unknown("lost-recovery-reply"); reviewed = Review(lost); var reviewedFile = Save("reviewed-target.json", reviewed);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
        foreach (string arg in new[] { typeof(RecoveryChecks).Assembly.Location, "--recovery-lost-reply", root, vaultRoot, lost.Id, Path.Combine(root, reviewedFile.RelativePath) }) start.ArgumentList.Add(arg);
        using (var child = Process.Start(start)!)
        {
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(true); throw new Exception("Recovery child timeout"); }
            Check(child.ExitCode == 18, "Actual child loses reply after recovery intent and fixture side effect: " + stderr.GetAwaiter().GetResult());
        }
        lost = new EngineeringOperationStore(root).Read(lost.Id);
        Check(lost.State == "recoveryIntent" && lost.RecoveryTarget is not null && File.ReadAllLines(Path.Combine(root,"recovery-calls.txt")).Length == 1, "Restart sees a single recovery effect with unresolved completion");
        lost = store.MarkUnknown(lost.Id, lost.Revision);
        Reject(() => new CredentialVault(vaultRoot).RecoverOperation(root, lost.Id, lost.Revision, [], reviewed, () => reviewed, _ => invoked++), "RecoveryRequired", "Unknown recovery cannot replay even with zero local destinations");
        Reject(() => vault.Retire(current.Reference, binding), "CredentialInUse", "Unknown recovery retains dispatch generation");
        Reject(() => vault.Retire(prior.Reference, binding), "CredentialInUse", "Unknown recovery retains recovery generation");
        lost = Reconcile(lost);

        var failed = Unknown("consumer-error"); reviewed = Review(failed);
        Reject(() => vault.RecoverOperation(root, failed.Id, failed.Revision, [], reviewed, () => reviewed, values =>
        {
            borrowed = values["previous"]; throw new Exception(Encoding.UTF8.GetString(borrowed));
        }), "CredentialConsumerFailed", "Recovery consumer error has no secret-bearing inner exception");
        failed = store.Read(failed.Id);
        Check(borrowed!.All(b => b == 0) && failed.State == "outcomeUnknown" && failed.RecoveryTarget is not null, "Failed recovery retains context and clears borrowed bytes");
        Reject(() => store.ReserveRecovery(failed.Id, failed.Revision, [], _ => { }, reviewed, () => reviewed), "RecoveryRequired", "Core replay guard survives a failed consumer");
        File.WriteAllBytes(provider, File.ReadAllBytes(Path.Combine(root, before.RelativePath))); failed = Reconcile(failed);

        var mixed = Unknown("local-and-external"); reviewed = Review(mixed); string destination = Path.Combine(root, "retrieved-copy");
        try
        {
            File.Delete(checkpointPath);
            Reject(()=>vault.RecoverOperation(root,mixed.Id,mixed.Revision,[new(mixed.Plan.Resources[0].ResourceKey,destination)],reviewed,()=>reviewed,_=>invoked++),
                "ArtifactChanged","A selected local restoration cannot proceed without its checkpoint");
        }
        finally { File.WriteAllBytes(checkpointPath,checkpointBytes); }
        mixed = vault.RecoverOperation(root, mixed.Id, mixed.Revision, [new(mixed.Plan.Resources[0].ResourceKey, destination)], reviewed, () => reviewed, _ =>
        {
            Directory.CreateDirectory(destination); File.WriteAllBytes(Path.Combine(destination,"fixture.ap21"), File.ReadAllBytes(checkpointPath));
            File.WriteAllBytes(provider, File.ReadAllBytes(Path.Combine(root,before.RelativePath)));
        });
        Check(Hash(File.ReadAllBytes(Path.Combine(destination,"fixture.ap21")))==projectCheckpoint.Sha256,"Synthetic copied project matches the exact independently retained checkpoint");
        mixed = store.Reconcile(mixed.Id, mixed.Revision,
            [new(mixed.Plan.Resources[0].ResourceKey,"restoredToNewLocation",destination,before), new(externalKey,"externalBaselineObserved",null,Observe(mixed,"reconciliation"))],
            [new(mixed.Plan.Resources[0].ResourceKey,destination,"verifiedRestored",before)]);
        Check(mixed.RecoveryDestinations.Count == 1 && mixed.RecoveryResolutions.Count == 1 && mixed.RecoveryTarget is not null, "Combined local destination and independent external recovery use the same journal");
        var legacy = Unknown("legacy-admitted-copy"); string legacyDestination = Path.Combine(root,"legacy-copy");
        var oldIntent = legacy with { State="recoveryIntent",Revision=legacy.Revision+1,PreviousSha256=CanonicalJson.Sha256(JsonSerializer.SerializeToNode(legacy,BridgeProtocol.Json)!),
            RecoveryDestinations=[new(legacy.Plan.Resources[0].ResourceKey,legacyDestination)],RecordedAt=DateTimeOffset.UtcNow };
        File.WriteAllBytes(Path.Combine(root,"engineering-operations",legacy.Id,oldIntent.Revision.ToString("D6")+".json"),JsonSerializer.SerializeToUtf8Bytes(oldIntent,BridgeProtocol.Json));
        legacy=store.Read(legacy.Id);
        Check(legacy.State=="recoveryIntent" && legacy.RecoveryTarget is null,"Historical credential-bearing local recovery remains readable without inventing a context");
        var legacyReview=Review(legacy);
        Reject(()=>vault.RecoverOperation(root,legacy.Id,legacy.Revision,[],legacyReview,()=>legacyReview,_=>invoked++),"RecoveryRequired","Reading an old recovery intent never grants a new credential invocation");
        Check(!Directory.Exists(legacyDestination),"Legacy fixture has independent absence evidence for its unattempted destination");
        File.WriteAllBytes(provider,File.ReadAllBytes(Path.Combine(root,before.RelativePath)));
        legacy=store.Reconcile(legacy.Id,legacy.Revision,
            [new(legacy.Plan.Resources[0].ResourceKey,"unchanged",null,before),new(externalKey,"externalBaselineObserved",null,Observe(legacy,"reconciliation"))],
            [new(legacy.Plan.Resources[0].ResourceKey,legacyDestination,"verifiedAbsent",before)]);
        Check(vault.Retire(current.Reference,binding).State == "retired" && vault.Retire(prior.Reference,binding).State == "retired", "Every independent reconciliation releases both generations for retirement");
        foreach (string file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))
            Check(File.ReadAllBytes(file).AsSpan().IndexOf(currentSecret)<0 && File.ReadAllBytes(file).AsSpan().IndexOf(priorSecret)<0, "No retained plaintext: "+Path.GetRelativePath(root,file));
        CryptographicOperations.ZeroMemory(currentSecret); CryptographicOperations.ZeroMemory(priorSecret);
        Save("verification.json",new { completed=true,nativeExecuted=false,count=checks.Count,checks });
        Save("mcp-fixture.json",new { artifactRoot=root,operations=new { reconciled=mixed.Id },current=vault.Inspect(current.Reference,binding),recoveryTarget=mixed.RecoveryTarget,expectedPinCount=5,legacyRecovery=oldIntent,legacyId=legacy.Id });
        Console.WriteLine($"PASS credential recovery: {checks.Count} checks; changed content/session, actual child reply loss, no native provider.");
    }
}
