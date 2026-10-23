using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

// Installation-owned operator-reviewed admission data, loaded only from the fixed
// deployment registry. No MCP upload/path or environment switch grants admission.
// HostPackageSha256 hashes the complete deployed Host dependency inventory,
// including TiaMcp.Host.dll; the stable .exe apphost alone is never sufficient.
internal sealed record TranslationAdmissionBinaries(string HostPackageSha256, string TranslationAssemblySha256,
    string WorkerSha256, string SiemensApiSha256);
internal sealed record TranslationAdmissionRow(string NativeKey, string Category, string ViewPath,
    string SourceStateSha256, string OriginalTargetStateSha256, string CandidateTargetStateSha256);
internal sealed record TranslationAdmissionPeer(string Identity, string ComparisonProfile, string SemanticSha256);
internal sealed record TranslationAdmissionKey(string Contract, TranslationAdmissionBinaries Binaries,
    string ProjectResourceKey, int TiaVersion, string HardwareIdentity, string? NativeIdentity,
    string ProfileSha256, TranslationImportScope Scope, string BaselineContentSha256, string CandidateContentSha256,
    TranslationAdmissionRow[] Rows, TranslationAdmissionPeer[] Peers);
internal sealed record TranslationAdmissionProof(string Case, string KeySha256, string SourcePackageSha256,
    string EvidenceSha256, string Mode);
internal sealed record TranslationAdmissionProfile(string Id, TranslationAdmissionKey Key,
    string SourcePackageSha256, TranslationAdmissionProof[] Proofs);

/// <summary>
/// Exact, finite admission proposal. A successful fixture lifecycle is not a broad
/// language/category/CPU profile. Every original key, source/target state, peer and
/// permitted resulting value is bound along with all executable/API identities.
/// Installation remains unadmitted until reviewed evidence closes every case.
/// </summary>
internal static class TranslationProductionAdmission
{
    internal const string Contract = "translation.whole-project-de-DE-en-GB-main-ob1.exact.v1";
    internal static readonly string[] NativeCases = [
        "complete-inventory", "native-import-readback", "preservation", "compile-save-fresh-reopen",
        "distinct-recovery-fresh-reopen", "lost-restore-response-reconciliation", "lost-import-response-reconciliation",
        "lost-compile-response-reconciliation", "lost-save-response-reconciliation", "lost-reopen-response-reconciliation",
        "interruption-before-dispatch", "stale-source-refusal", "existing-recovery-destination-refusal", "exact-process-cleanup"
    ];
    internal static readonly string[] PortableCases = [
        "exact-key-and-binary-refusal", "stale-plan-revision-replay-refusal", "source-clear-empty-richtext-refusal",
        "key-source-target-culture-peer-drift-refusal", "artifact-tampering-refusal", "lost-cleanup-response-reservation"
    ];

    internal static TranslationAdmissionKey Describe(TranslationImportContext context, TranslationAdmissionBinaries binaries)
    {
        foreach (string digest in new[] { binaries.HostPackageSha256, binaries.TranslationAssemblySha256, binaries.WorkerSha256, binaries.SiemensApiSha256 })
            EngineeringIdentityGuard.Sha256(digest, "admissionBinarySha256");
        var target = context.Operation.Plan.Target;
        Require(target.AdapterSha256 == binaries.WorkerSha256 && target.Offline,
            "TranslationAdmissionMismatch", "The observed offline worker must match the exact proposed binaries.");
        // Status performs the full original approval/candidate/plan/peer integrity
        // verification before any proposal can describe the retained binding.
        var operation = new TranslationImportWorkflow(context.ArtifactRoot).Status(context.Operation.Id);
        Require(HashJson(operation) == HashJson(context.Operation), "TranslationAdmissionMismatch", "Exact current operation required.");
        var retained = new TranslationImportArtifacts(context.ArtifactRoot);
        var binding = retained.Read<TranslationImportBinding>(operation.Plan.Inputs[0]);
        Require(context.CandidateRef == binding.CandidateRef && context.CandidateReceiptSha256 == binding.CandidateReceiptSha256 &&
            context.CandidateSha256 == binding.Candidate.Sha256 && HashJson(context.Scope) == HashJson(binding.Baseline.Scope),
            "TranslationAdmissionMismatch", "Admission context differs from the retained candidate and scope.");
        var candidate = new TranslationWorkflowStore(context.ArtifactRoot).NativeCandidate(binding.CandidateRef, binding.CandidateReceiptSha256);
        var scope = binding.Baseline.Scope;
        Require(scope.SourceCulture == "de-DE" && scope.TargetCulture == "en-GB" &&
            scope.ActiveCultures.SequenceEqual(new[] { "de-DE", "en-GB" }) &&
            scope.ObjectSelections.SequenceEqual(new[] { "whole-project:legacy-bilingual" }),
            "TranslationAdmissionScopeUnqualified", "Only the exact whole-project de-DE/en-GB inventory is eligible; restricted selections and additional languages need independent evidence.");
        var rows = candidate.Baseline.Profile.Rows.Select(row => {
            Require(row.Targets.Length == 1 && row.Targets[0].Culture == "en-GB", "TranslationAdmissionScopeUnqualified", "Exactly one English target per native row required.");
            var cell = row.Targets[0].Location;
            return new TranslationAdmissionRow(row.NativeKey, row.Category,
                candidate.Baseline.ReadText(new(row.SourceCell.Part, "B" + row.SourceCell.Cell[1..])).Text ?? "",
                HashJson(candidate.Baseline.ReadText(row.SourceCell)), HashJson(candidate.Baseline.ReadText(cell)), HashJson(candidate.Candidate.ReadText(cell)));
        }).OrderBy(row => row.NativeKey, StringComparer.Ordinal).ToArray();
        var changed = rows.Where(row => row.OriginalTargetStateSha256 != row.CandidateTargetStateSha256).ToArray();
        Require(changed.Length <= 1, "TranslationAdmissionScopeUnqualified", "The finite proposal permits only no-op or one exact Main OB1 comment replacement.");
        if (changed.Length == 1)
        {
            string[] view = changed[0].ViewPath.Split('\\');
            Require(changed[0].Category == "<BlockCommentCategoryData>" && view.Length == 5 &&
                view[0] == Path.GetFileNameWithoutExtension(target.ProjectFilePath) && view[2] == "Programmbausteine" &&
                view[3] == "Main" && view[4] == "Bausteinkommentar", "TranslationAdmissionScopeUnqualified", "Only the exact captured Main OB1 block-comment field is eligible.");
        }
        return new(Contract, Clone(binaries), operation.Plan.Resources.Single().ResourceKey,
            target.Reference.Snapshot.TiaVersion, target.HardwareIdentity, target.NativeIdentity, target.ProfileSha256,
            Clone(scope), binding.BaselineContentSha256, binding.CandidateContentSha256, rows,
            binding.Baseline.Peers.OrderBy(peer => peer.Identity, StringComparer.Ordinal)
                // Preserve and reverify the original bytes through Status and
                // ReadBytes. Repeated exports may vary only where the existing
                // narrow peer comparer already permits it, never by path/key
                // removal or by a caller-provided semantic hash.
                .Select(peer => new TranslationAdmissionPeer(peer.Identity, peer.ComparisonProfile,
                    Sha256(TranslationNativeComparison.Peer(retained.ReadBytes(peer.Evidence), peer.ComparisonProfile, candidate)))).ToArray());
    }

    internal static void RequireComplete(TranslationAdmissionProfile profile, TranslationAdmissionKey actual)
    {
        Require(!string.IsNullOrWhiteSpace(profile.Id) && profile.Key.Contract == Contract &&
            HashJson(profile.Key) == HashJson(actual), "TranslationAdmissionMismatch", "No wildcard, altered text, location, peer, culture, source package or binary may inherit an exact profile.");
        EngineeringIdentityGuard.Sha256(profile.SourcePackageSha256, "sourcePackageSha256");
        string key = HashJson(actual);
        Require(profile.Proofs is not null && profile.Proofs.Length == NativeCases.Length + PortableCases.Length &&
            profile.Proofs.Select(proof => proof.Case).Distinct(StringComparer.Ordinal).Count() == profile.Proofs.Length,
            "TranslationAdmissionIncomplete", "Exactly one independent receipt for every required case is necessary.");
        foreach (string name in NativeCases.Concat(PortableCases))
        {
            var proof = profile.Proofs!.SingleOrDefault(proof => proof.Case == name);
            Require(proof is not null && proof.KeySha256 == key && proof.SourcePackageSha256 == profile.SourcePackageSha256 &&
                proof.Mode == (NativeCases.Contains(name) ? "native" : "portable"),
                "TranslationAdmissionIncomplete", "Missing, synthetic, foreign-key or foreign-package evidence for " + name + ".");
            EngineeringIdentityGuard.Sha256(proof!.EvidenceSha256, "admissionEvidenceSha256");
        }
    }
}
