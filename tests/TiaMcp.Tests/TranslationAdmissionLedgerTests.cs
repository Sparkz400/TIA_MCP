using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

// Worker-side ledger rules of an installed translation admission (TranslationAdmissionLedger.cs, linked; no worker load).
internal static class TranslationAdmissionLedgerTests
{
    private static readonly string Deployment = Path.Combine(Path.GetTempPath(), "tia-mcp-ledger", "deploy", "worker");
    private static readonly string[] DefaultMode = ["default-mode-lifecycle", "worker-loss-at-checkpoint-intent", "worker-loss-during-import", "host-loss-during-import"];

    private static JObject Entry(string contract, int cases, string[]? defaultMode = null, string? qualifiedWorker = null, string? run2 = null)
    {
        var proofs = new JArray(Enumerable.Range(0, cases).Select(i => new JObject { ["case"] = "case-" + i, ["evidenceSha256"] = new string('5', 64) }));
        foreach (string name in defaultMode ?? []) proofs.Add(new JObject { ["case"] = name, ["evidenceSha256"] = run2 ?? new string('7', 64) });
        var entry = new JObject { ["profile"] = new JObject { ["key"] = new JObject { ["contract"] = contract }, ["proofs"] = proofs } };
        if (qualifiedWorker is not null) entry["qualification"] = new JObject { ["hostDirectory"] = "unused", ["workerDirectory"] = qualifiedWorker };
        return entry;
    }

    private static async Task Refused(string code, JObject entry) =>
        Check.Equal(code, (await Check.ThrowsAsync<WorkerError>(() => { TranslationAdmissionLedger.Check(entry, Deployment); return Task.CompletedTask; })).Code);

    internal static async Task ProvisionalAndFinalRestrictedLedgers()
    {
        // Whole project: 20 proofs, never provisional.
        TranslationAdmissionLedger.Check(Entry(TranslationAdmissionLedger.WholeProjectContract, 20), Deployment);
        await Refused("TranslationAdmissionProvisional", Entry(TranslationAdmissionLedger.WholeProjectContract, 20, qualifiedWorker: Deployment));
        // Provisional restricted: 21 proofs, only in its own worker directory.
        TranslationAdmissionLedger.Check(Entry(TranslationAdmissionLedger.RestrictedContract, 21, qualifiedWorker: Deployment + Path.DirectorySeparatorChar), Deployment);
        await Refused("TranslationAdmissionProvisional", Entry(TranslationAdmissionLedger.RestrictedContract, 21, qualifiedWorker: Path.Combine(Deployment, "..", "other")));
        await Refused("TranslationAdmissionProvisional", Entry(TranslationAdmissionLedger.RestrictedContract, 21, qualifiedWorker: "relative/worker"));
        // Final restricted: 25 proofs with the four default-mode cases on their own receipt.
        TranslationAdmissionLedger.Check(Entry(TranslationAdmissionLedger.RestrictedContract, 21, DefaultMode), Deployment);
        await Refused("TranslationAdmissionIncomplete", Entry(TranslationAdmissionLedger.RestrictedContract, 21));
        await Refused("TranslationAdmissionIncomplete", Entry(TranslationAdmissionLedger.RestrictedContract, 21, DefaultMode, run2: new string('5', 64)));
        await Refused("TranslationAdmissionIncomplete", Entry(TranslationAdmissionLedger.RestrictedContract, 21, [.. DefaultMode[..3], DefaultMode[0]]));
        await Refused("TranslationAdmissionIncomplete", Entry(TranslationAdmissionLedger.RestrictedContract, 17, DefaultMode, qualifiedWorker: Deployment));
        await Refused("TranslationAdmissionMismatch", Entry("translation.other.v1", 20));
    }
}
