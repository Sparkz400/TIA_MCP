using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

internal static class OperationTextChecks
{
    internal static void Run(string? destination = null)
    {
        string root = Path.GetFullPath(destination ?? Path.Combine(Path.GetTempPath(), "operation-text-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(root)) throw new Exception("Fresh operation text evidence directory required.");
        Directory.CreateDirectory(root);
        var checks = new List<string>(); var failures = new List<string>(); int sequence = 0;
        void Check(bool ok, string name) { if (!ok) failures.Add(name); else checks.Add(name); }
        (EngineeringOperationStore Store, EngineeringOperationPlan Plan, EngineeringArtifactReference Proof) Fixture()
        {
            string folder = Path.Combine(root, (++sequence).ToString()); Directory.CreateDirectory(folder);
            string proof = Path.Combine(folder, "proof-\uFFFD.json"); File.WriteAllText(proof, "synthetic evidence only");
            var artifact = new EngineeringArtifactReference(Path.GetFileName(proof), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(proof))));
            string project = Path.Combine(folder, "project.ap21");
            var target = new NativeTargetObservation(new(new("project", "plc", "root", "block", "block"), new("capture", new('a', 64)), new('b', 64)), project,
                "epoch", new('c', 64), new('d', 64), "owner", null, true, []);
            return (new(folder), new("edit", target, [new(EngineeringResourceKey.ForPath("project", project), "project", "edit", "projectArchiveToNewLocation")],
                [artifact], [], ["dispatch.\uFFFD"]), artifact);
        }
        EngineeringOperationRecord Intent(EngineeringOperationStore store, EngineeringOperationPlan plan, EngineeringArtifactReference proof, bool dispatch = true)
        {
            var r = store.Prepare(plan, plan.Operation); r = store.Acquire(r.Id, r.Revision);
            r = store.BeginCheckpoint(r.Id, r.Revision, () => plan.Target);
            if (!dispatch) return r;
            r = store.Checkpoint(r.Id, r.Revision, [proof]); return store.Dispatch(r.Id, r.Revision, () => plan.Target);
        }
        void Reject(Action action, string name, EngineeringOperationStore store, EngineeringOperationRecord? before = null)
        {
            string folder = Path.Combine(root, sequence.ToString());
            string Snapshot() => string.Join("\n", Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(p => p + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))));
            string original = Snapshot();
            bool refused = false;
            try { action(); } catch (BridgeException e) when (e.Code == "InvalidOperationPlan") { refused = true; }
            catch (Exception e) { failures.Add(name + ": unexpected " + e.GetType().Name); }
            Check(refused, name);
            Check(Snapshot() == original, name + " preserves retained files");
            if (before is not null) Check(JsonSerializer.Serialize(store.Read(before.Id), BridgeProtocol.Json) == JsonSerializer.Serialize(before, BridgeProtocol.Json), name + " preserves durable revision and ownership");
        }
        foreach (string malformed in new[] { "\uD800", "\uDC00", "x\uD800y", "\uD800\uD800", "\uDC00\uD800" })
        foreach (string field in new[] { "operation", "effect", "probe", "gate", "key", "input" })
        {
            var (store, plan, proof) = Fixture();
            var changed = field switch
            {
                "operation" => plan with { Operation = malformed },
                "effect" => plan with { Resources = [plan.Resources[0] with { Effect = malformed }] },
                "probe" => plan with { RequiredProbes = [malformed] },
                "gate" => plan with { RequiredGates = [malformed] },
                "input" => plan with { Inputs = [proof with { RelativePath = "proof-" + malformed + ".json" }] },
                _ => plan
            };
            Reject(() => store.Prepare(changed, field == "key" ? malformed : "valid"), field + " malformed case " + sequence, store);
        }
        foreach (string entry in new[] { "outcome", "dispatch", "recovery", "outcome-artifact", "dispatch-artifact", "recovery-artifact", "checkpoint", "interruption", "reconcile", "destination", "resolution", "resolution-path" })
        {
            var (store, plan, proof) = Fixture(); var r = Intent(store, plan, proof, entry != "checkpoint");
            var badArtifact = proof with { RelativePath = "proof-\uD800.json" };
            string key = plan.Resources[0].ResourceKey;
            if (entry.StartsWith("recovery") || entry.StartsWith("resolution") || entry == "destination") r = store.MarkUnknown(r.Id, r.Revision);
            if (entry.StartsWith("recovery") || entry.StartsWith("resolution")) r = store.ReserveRecovery(r.Id, r.Revision, [new(key, plan.Target.ProjectFilePath + "\uFFFD.copy")]);
            Action action = entry switch
            {
                "outcome" => () => store.RecordOutcome(r.Id, r.Revision, true, [new("dispatch.\uD800", proof)]),
                "dispatch" => () => store.RecordDispatchEvidence(r.Id, r.Revision, new("dispatch.\uD800", proof)),
                "recovery" => () => store.RecordRecoveryEvidence(r.Id, r.Revision, new("recovery.\uD800", proof)),
                "outcome-artifact" => () => store.RecordOutcome(r.Id, r.Revision, true, [new("dispatch.\uFFFD", badArtifact)]),
                "dispatch-artifact" => () => store.RecordDispatchEvidence(r.Id, r.Revision, new("dispatch.\uFFFD", badArtifact)),
                "recovery-artifact" => () => store.RecordRecoveryEvidence(r.Id, r.Revision, new("recovery.valid", badArtifact)),
                "checkpoint" => () => store.Checkpoint(r.Id, r.Revision, [badArtifact]),
                "interruption" => () => store.MarkUnknown(r.Id, r.Revision, badArtifact),
                "reconcile" => () => store.Reconcile(r.Id, r.Revision, [new(key, "unchanged", null, badArtifact)]),
                "destination" => () => store.ReserveRecovery(r.Id, r.Revision, [new(key, plan.Target.ProjectFilePath + "\uD800.copy")]),
                "resolution-path" => () => store.Reconcile(r.Id, r.Revision, [new(key, "unchanged", null, proof)], [new(key, plan.Target.ProjectFilePath + "\uD800.copy", "verifiedAbsent", proof)]),
                _ => () => store.Reconcile(r.Id, r.Revision, [new(key, "unchanged", null, proof)], [new(key, plan.Target.ProjectFilePath + "\uFFFD.copy", "verifiedAbsent", badArtifact)])
            };
            Reject(action, entry + " refuses serialization alias", store, r);
        }
        foreach (string valid in new[] { "\uFFFD", "\U0001F680", "e\u0301", "é" })
        {
            var (store, plan, proof) = Fixture(); plan = plan with { Operation = valid, RequiredGates = ["dispatch." + valid] };
            var r = Intent(store, plan, proof); r = store.RecordDispatchEvidence(r.Id, r.Revision, new("dispatch." + valid, proof));
            r = store.RecordOutcome(r.Id, r.Revision, true, []);
            var reopened = new EngineeringOperationStore(Path.GetDirectoryName(plan.Target.ProjectFilePath)!).Read(r.Id);
            Check(reopened.State == "succeeded" && reopened.Plan.Operation == valid && reopened.Evidence[0].Gate == "dispatch." + valid &&
                reopened.IdempotencyKey == valid && store.Prepare(plan, valid).Id == r.Id,
                "Valid Unicode preserves exact plan, gate and successful reopen " + valid);
        }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { checks, failures, nativeExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        if (failures.Count != 0) throw new Exception(string.Join("; ", failures));
        Console.WriteLine($"PASS operation text boundaries: {checks.Count} checks; no native execution. Receipts retained at {root}");
    }
}
