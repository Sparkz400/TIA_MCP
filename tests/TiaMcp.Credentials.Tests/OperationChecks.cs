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

internal static class OperationChecks
{
    internal static void LostReply(string[] args)
    {
        var store = new EngineeringOperationStore(args[1]); var record = store.Read(args[3]);
        new CredentialVault(args[2]).DispatchOperation(args[1], record.Id, record.Revision, () => record.Plan.Target, values =>
        {
            if (store.Read(record.Id).State != "dispatchIntent" || values.Count != 1 || !values.ContainsKey("current")) throw new Exception("Admission missing");
            using var stream = new FileStream(Path.Combine(args[1], "calls.txt"), FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes("one synthetic provider invocation\n")); stream.Flush(true);
            Environment.Exit(17);
        });
    }
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root); var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        void Reject(Action action, string code, string label)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; }
            throw new Exception("Expected " + code + ": " + label);
        }
        string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        EngineeringArtifactReference Save(string name, object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json); File.WriteAllBytes(Path.Combine(root, name), bytes);
            return new(name, Hash(bytes));
        }
        var input = Save("input.json", new { synthetic = true });
        var store = new EngineeringOperationStore(root); string vaultRoot = Path.Combine(root, "vault"); var vault = new CredentialVault(vaultRoot);
        var binding = new CredentialBinding(new("fixture-provider", "fixture-service", "owner/one", "account", "reader"), "runtime-access", "operator");
        byte[] secret = Encoding.UTF8.GetBytes("SYNTHETIC-" + Guid.NewGuid().ToString("N"));
        var current = vault.Create(binding, secret); var recovery = vault.Create(binding, secret);
        EngineeringOperationPlan Plan(string name)
        {
            string project = Path.Combine(root, name + ".ap21");
            var reference = new EngineeringObjectReference(new(project, "/CPU", "/root", "/CPU", "hardware"), new("synthetic", new('a', 64)), new('b', 64));
            var target = new NativeTargetObservation(reference, project, "epoch", new('c', 64), new('d', 64), "CPU", null, true, []);
            var owner = binding.Resource with { ResourceKind = null, ResourceId = null };
            string key = EngineeringResourceKey.ForExternal(owner);
            return new("synthetic.access", target,
                [new(EngineeringResourceKey.ForPath("project", project), "project", "fixture", "projectArchiveToNewLocation"),
                 new(key, "external", "fixture", "externalStateReadback") { External = new(owner, new("present", input.Sha256), new("present", input.Sha256)) }],
                [input], [], ["project", EngineeringExternalEvidence.OutcomeGate(key)])
            { Credentials = [new("current", "dispatch", current.Reference, binding), new("previous", "recovery", recovery.Reference, binding)] };
        }
        EngineeringArtifactReference Observation(EngineeringOperationRecord r, string phase) => Save(Guid.NewGuid().ToString("N") + ".json",
            new EngineeringExternalObservation("engineering-external-observation.v1", r.Id, r.PlanSha256, r.Revision, r.Plan.Resources[1].ResourceKey,
                phase, Guid.NewGuid().ToString("N"), new("present", input.Sha256), input));
        EngineeringOperationRecord Ready(string name)
        {
            var r = store.Prepare(Plan(name), name); r = vault.AcquireOperation(root, r.Id, r.Revision);
            r = vault.BeginOperationCheckpoint(root, r.Id, r.Revision, () => r.Plan.Target);
            return store.Checkpoint(r.Id, r.Revision, [input, Observation(r, "checkpoint")]);
        }
        EngineeringOperationRecord Reconcile(EngineeringOperationRecord r) => store.Reconcile(r.Id, r.Revision,
            [new(r.Plan.Resources[0].ResourceKey, "unchanged", null, input), new(r.Plan.Resources[1].ResourceKey, "externalBaselineObserved", null, Observation(r, "reconciliation"))]);
        var plan = Plan("invalid");
        foreach (var target in new[] {
            plan.Target with { SessionEpoch = "bad\ud800" }, plan.Target with { HardwareIdentity = "bad\udc00" },
            plan.Target with { NativeIdentity = "bad\ud800" }, plan.Target with { Probes = [new("bad\ud800", "observed", new('e', 64))] } })
            Reject(() => store.Prepare(plan with { Target = target }, "invalid-native-text"), "InvalidIdentity", "Invalid native context text refuses before JSON replacement can alias a session or owner");
        var unicodeTarget = plan.Target with { SessionEpoch = "epoch-\u4e2d\ud83d\ude80", HardwareIdentity = "CPU-\u03a9", NativeIdentity = "owner-\ud83d\ude80" };
        var unicode = store.Prepare(plan with { Target = unicodeTarget }, "valid-native-text");
        Check(store.Read(unicode.Id).Plan.Target.SessionEpoch == unicodeTarget.SessionEpoch && store.Read(unicode.Id).Plan.Target.NativeIdentity == unicodeTarget.NativeIdentity,
            "Valid Unicode native context survives retained JSON exactly");
        store.Stop(unicode.Id, unicode.Revision);
        foreach (var bad in new[] {
            plan with { Credentials = [] },
            plan with { Credentials = [plan.Credentials![0], plan.Credentials[0]] },
            plan with { Credentials = [plan.Credentials![0] with { UsePhase = "automatic" }] },
            plan with { Credentials = [plan.Credentials![0] with { Name = "bad\ud800" }] } })
            Reject(() => store.Prepare(bad, Guid.NewGuid().ToString("N")), "CredentialInputInvalid", "Invalid public requirement refused before journal creation");
        var wrong = binding with { Resource = binding.Resource with { OwnerId = "other" } };
        Reject(() => store.Prepare(plan with { Credentials = [new("other", "dispatch", current.Reference with { BindingSha256 = CredentialVault.BindingHash(wrong) }, wrong)] }, "wrong"),
            "CredentialContextMismatch", "Unreserved external owner cannot bind a credential");
        Reject(() => store.Prepare(plan with { Credentials = [plan.Credentials![0], plan.Credentials[1] with { Reference = recovery.Reference with { VaultId = new('a', 32) } }] }, "mixed"),
            "CredentialContextMismatch", "Multiple vaults refuse before lock acquisition");
        var local = plan with { Credentials = null };
        Check(!JsonSerializer.SerializeToNode(local, BridgeProtocol.Json)!.AsObject().ContainsKey("credentials"), "Legacy plans omit optional references and retain serialized hashes");
        var first = store.Prepare(Plan("stop"), "stop");
        Reject(() => store.Acquire(first.Id, first.Revision), "CredentialAdmissionRequired", "Bare journal acquisition cannot bypass credential verification");
        first = vault.AcquireOperation(root, first.Id, first.Revision);
        Check(vault.List().All(s => s.OperationReferences?.Single().OperationId == first.Id), "All dispatch and recovery references pinned before acquisition");
        string acquiredRevision = Path.Combine(root, "engineering-operations", first.Id, "000001.json");
        File.Move(acquiredRevision, acquiredRevision + ".held");
        Reject(() => vault.Retire(current.Reference, binding), "CredentialInUse", "Pins with only prepared journal state remain held after interrupted acquisition");
        first = vault.AcquireOperation(root, first.Id, 0);
        Check(vault.Inspect(current.Reference, binding).OperationReferences!.Count == 1, "Retry completes acquisition without duplicating an already durable pin");
        Reject(() => vault.Retire(current.Reference, binding), "CredentialInUse", "Acquired operation prevents retirement");
        Reject(() => vault.Use(current.Reference, binding, _ => throw new Exception()), "CredentialAdmissionRequired", "Raw use cannot bypass operation admission");
        Reject(() => store.BeginCheckpoint(first.Id, first.Revision, () => throw new Exception()), "CredentialAdmissionRequired", "Missing credential verifier precedes native callback");
        store.Stop(first.Id, first.Revision);
        // Keep both active for later attempts; terminal pins remain immutable history.
        var ready = Ready("lost-reply");
        Reject(() => store.Dispatch(ready.Id, ready.Revision, () => throw new Exception()), "CredentialAdmissionRequired", "Bare dispatch refuses before native callback");
        Reject(() => vault.DispatchOperation(root, ready.Id, ready.Revision, () => ready.Plan.Target with { SessionEpoch = "reconnected" }, _ => throw new Exception()),
            "StaleNativeTarget", "Changed native session refuses before dispatch or consumer");
        Check(store.Read(ready.Id).State == "ready", "Failed freshness check leaves dispatch unattempted");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string arg in new[] { typeof(OperationChecks).Assembly.Location, "--lost-reply", root, vaultRoot, ready.Id }) start.ArgumentList.Add(arg);
        using (var child = Process.Start(start)!)
        {
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(true); throw new Exception("Lost reply test timed out"); }
            Check(child.ExitCode == 17, "Actual child exits after durable intent and synthetic side effect: " + stderr.GetAwaiter().GetResult());
        }
        ready = store.Read(ready.Id);
        Check(ready.State == "dispatchIntent" && File.ReadAllLines(Path.Combine(root, "calls.txt")).Length == 1, "Restart observes one persisted provider invocation and unknown completion");
        var reopened = new CredentialVault(vaultRoot);
        foreach (var status in new[] { current, recovery }) Reject(() => reopened.Retire(status.Reference, binding), "CredentialInUse", "Reply loss retains both current and recovery generations");
        Reject(() => reopened.DispatchOperation(root, ready.Id, ready.Revision, () => ready.Plan.Target, _ => throw new Exception()), "RecoveryRequired", "Retry never calls a second provider");
        ready = store.MarkUnknown(ready.Id, ready.Revision);
        Reject(() => reopened.Retire(current.Reference, binding), "CredentialInUse", "Unknown outcome retains references after restart");
        string journalDirectory = Path.Combine(root, "engineering-operations", ready.Id);
        Directory.Move(journalDirectory, journalDirectory + ".held");
        try { Reject(() => reopened.Retire(current.Reference, binding), "CredentialReservationUnresolved", "Missing operation journal cannot release a credential"); }
        finally { Directory.Move(journalDirectory + ".held", journalDirectory); }
        string revisionPath = Path.Combine(root, "engineering-operations", ready.Id, "000000.json"); byte[] original = File.ReadAllBytes(revisionPath);
        try
        {
            File.WriteAllText(revisionPath, "{}");
            Reject(() => reopened.Retire(current.Reference, binding), "CredentialReservationUnresolved", "Corrupt journal cannot release a reference");
        }
        finally { File.WriteAllBytes(revisionPath, original); }
        ready = Reconcile(ready);
        var failed = Ready("consumer-failure"); byte[]? borrowed = null;
        Reject(() => vault.DispatchOperation(root, failed.Id, failed.Revision, () => failed.Plan.Target, values =>
        {
            borrowed = values["current"]; Check(borrowed.SequenceEqual(secret) && !values.ContainsKey("previous"), "Consumer receives only exact dispatch slots");
            throw new Exception(Encoding.UTF8.GetString(borrowed));
        }), "CredentialConsumerFailed", "Consumer exception is sanitized");
        Check(borrowed!.All(b => b == 0) && store.Read(failed.Id).State == "outcomeUnknown", "Failure clears borrowed bytes and retains unknown ownership");
        Reconcile(store.Read(failed.Id));
        var succeeded = Ready("successful");
        succeeded = vault.DispatchOperation(root, succeeded.Id, succeeded.Revision, () => succeeded.Plan.Target, values => borrowed = values["current"]);
        Check(borrowed!.All(b => b == 0) && succeeded.State == "dispatchIntent", "Successful consumer clears bytes without claiming verification success");
        succeeded = store.RecordOutcome(succeeded.Id, succeeded.Revision, true,
            [new("project", input), new(EngineeringExternalEvidence.OutcomeGate(succeeded.Plan.Resources[1].ResourceKey), Observation(succeeded, "outcome"))]);
        Reject(() => store.ReserveRecovery(succeeded.Id, succeeded.Revision, [new(succeeded.Plan.Resources[0].ResourceKey, Path.Combine(root, "restore"))], _ => { }),
            "RecoveryRequired", "Terminal credential-bearing operation cannot reopen after retirement becomes legal");
        Check(vault.Retire(current.Reference, binding).State == "retired" && vault.Retire(recovery.Reference, binding).State == "retired", "Complete independent terminal evidence releases both generations for retirement");
        Check(vault.Retire(current.Reference, binding).OperationReferences!.Count == 4, "Idempotent retirement preserves every operation pin");
        var unavailable = store.Prepare(Plan("retired"), "retired");
        Reject(() => vault.AcquireOperation(root, unavailable.Id, unavailable.Revision), "CredentialRetired", "Retired reference cannot enter a new operation");
        string statePath = Path.Combine(vaultRoot, current.Reference.CredentialId + ".state.json"); byte[] stateBytes = File.ReadAllBytes(statePath);
        try
        {
            var state = JsonNode.Parse(stateBytes)!; state.AsObject().Remove("pins"); File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(state));
            Reject(() => vault.Inspect(current.Reference, binding), "CredentialUnavailable", "Version two lifecycle cannot lose pins and default to unreserved");
        }
        finally { File.WriteAllBytes(statePath, stateBytes); }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            Check(File.ReadAllBytes(file).AsSpan().IndexOf(secret) < 0, "Retained file has no synthetic plaintext: " + Path.GetRelativePath(root, file));
        CryptographicOperations.ZeroMemory(secret);
        Save("verification.json", new { completed = true, nativeExecuted = false, count = checks.Count, checks });
        Save("mcp-fixture.json", new { artifactRoot = root, operations = new { succeeded = succeeded.Id, reconciled = ready.Id }, current = vault.Inspect(current.Reference, binding) });
        Console.WriteLine($"PASS credential operation ownership: {checks.Count} checks; child reply loss, no native provider.");
    }
}
