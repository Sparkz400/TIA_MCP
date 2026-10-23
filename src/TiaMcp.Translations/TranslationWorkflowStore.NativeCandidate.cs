using System.Text;
using System.Text.Json;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed record TranslationImportCandidate(TranslationWorkbook Baseline, TranslationWorkbook Candidate,
    string CandidateRef, string ReceiptSha256, string ApprovalSha256, string ReviewSha256);

public sealed partial class TranslationWorkflowStore
{
    // Trusted library seam only. The ten offline MCP tools neither call this nor
    // accept native observations. Approval still comes only from the durable review.
    internal TranslationImportCandidate NativeCandidate(string candidateRef, string exactReceiptSha256)
    {
        using var gate = retained.Lock();
        var candidate = RequireKind(retained.Load(candidateRef), "candidate");
        Require(candidate.RecordSha256 == exactReceiptSha256, "ReviewApprovalMismatch", "Bind the exact completed candidate receipt.");
        var review = RequireKind(retained.Load(Text(candidate.Metadata, "reviewRef")), "review");
        var approval = RequireKind(retained.Load(TranslationRetention.Reference("approval", review.Id)), "approval");
        var (baseline, batch, _) = VerifiedReview(review);
        Require(Text(candidate.Metadata, "approvalSha256") == approval.RecordSha256 &&
            Text(approval.Metadata, "reviewRef") == TranslationRetention.Reference(review) &&
            Text(approval.Metadata, "reviewSha256") == Text(review.Metadata, "reviewSha256"),
            "ReviewApprovalMismatch", "Candidate must retain this review's explicit approval.");
        var approved = JsonSerializer.Deserialize<TranslationProposalBatch>(retained.Read(approval, "approved-proposals.json"), TranslationJson.Options)!;
        var expected = batch with { Edits = batch.Edits.Select(e => e with { Approved = true, Reviewer = Text(approval.Metadata, "reviewer") }).ToArray() };
        Require(HashJson(approved) == HashJson(expected), "TranslationIntegrityFailure", "Approved proposals differ from the exact review.");
        var rebuilt = TranslationReview.BuildCandidate(baseline, baseline, approved.Edits,
            Text(approval.Metadata, "approvedReviewSha256"), approved.Options);
        var bytes = retained.Read(candidate, "candidate.xlsx");
        Require(Sha256(bytes) == Sha256(rebuilt.Bytes) &&
            HashJson(ReadObject(candidate, "candidate-manifest.json")) == HashJson(rebuilt.Manifest) &&
            HashJson(ReadObject(approval, "approved-review.json")) == HashJson(rebuilt.Manifest["review"]),
            "TranslationIntegrityFailure", "Recomputed candidate or complete approved review differs.");
        return new(baseline, TranslationWorkbook.Open(bytes, baseline.Profile, baseline.Project, Sha256(bytes)),
            candidateRef, candidate.RecordSha256, approval.RecordSha256, Text(review.Metadata, "reviewSha256"));
    }
}
