using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;
using static TiaMcp.Translations.TranslationRetention;

namespace TiaMcp.Translations;

public sealed record TranslationProposalBatch(TranslationEdit[] Edits, TranslationReviewOptions? Options = null);
public sealed record TranslationReviewCursor(string RecordRef, string RecordSha256, string Section, int Offset);

/// <summary>Durable offline workflow. No bridge, Siemens service, native admission or import dependency.</summary>
public sealed partial class TranslationWorkflowStore
{
    private readonly TranslationRetention retained;
    private readonly ArtifactUploadStore uploads;
    private const int InputJsonLimit = 16 * 1024 * 1024;
    private const int PageBytes = 192 * 1024;

    public TranslationWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits = null)
        : this(artifactsDirectory, uploadLimits, null) { }

    internal TranslationWorkflowStore(string artifactsDirectory, PackageIntakeLimits? uploadLimits, Action<string>? publicationObserver)
    {
        retained = new(artifactsDirectory, publicationObserver);
        uploads = new(artifactsDirectory, uploadLimits);
    }

    public JsonObject Capture(string workbookUploadRef, string profileUploadRef, string projectUploadRef)
    {
        using var gate = retained.Lock();
        byte[] bytes = Uploaded(workbookUploadRef, WorkbookLimit);
        byte[] profileBytes = Uploaded(profileUploadRef, InputJsonLimit);
        byte[] projectBytes = Uploaded(projectUploadRef, InputJsonLimit);
        var profile = Parse<WorkbookProfile>(Encoding.UTF8.GetString(profileBytes));
        var project = Parse<EngineeringObjectReference>(Encoding.UTF8.GetString(projectBytes));
        var workbook = TranslationWorkbook.Open(bytes, profile, project, Sha256(bytes));
        CheckMappedTextBudget(workbook);
        var record = retained.Publish("capture", NewId(), new JsonObject
        {
            ["workbookSha256"] = workbook.WorkbookSha256, ["profileSha256"] = workbook.ProfileSha256,
            ["projectSha256"] = HashJson(project), ["profileKind"] = profile.Kind,
            ["mappedKeys"] = profile.Rows.Length, ["mappedTargets"] = profile.Rows.Sum(r => r.Targets.Length),
            ["provenance"] = "caller-supplied captured identity; no native freshness observation"
        }, new Dictionary<string, byte[]>
        {
            ["original.xlsx"] = bytes, ["profile.json"] = profileBytes, ["project.json"] = projectBytes,
            ["upload-provenance.json"] = JsonBytes(new { workbookUploadRef, profileUploadRef, projectUploadRef })
        });
        return Summary(record);
    }

    public JsonObject Inspect(string captureRef, int pageSize = 100, TranslationInspectionCursor? cursor = null)
    {
        Require(pageSize is > 0 and <= 1000, "TranslationPageLimit", "Inspection page size must be 1..1000.");
        var record = RequireKind(retained.Load(captureRef), "capture");
        var workbook = OpenCapture(record);
        // Obtain one library row at a time: a shared 1 Mi-character source can
        // otherwise be replicated into every culture before byte paging applies.
        var page = TranslationInspection.Page(workbook, 1, cursor);
        int offset = page["offset"]!.GetValue<int>();
        var bounded = new JsonArray();
        int responseBytes = 0;
        var currentPage = page;
        for (int i = 0; i < pageSize && currentPage["rows"]!.AsArray().Count != 0; ++i)
        {
            var row = BoundedRows(currentPage["rows"]!.AsArray(), 0, 1, ArtifactRef(record, "original.xlsx"), offset + i)[0]!;
            int bytes = Encoding.UTF8.GetByteCount(row.ToJsonString());
            if (responseBytes + bytes > PageBytes) break;
            bounded.Add(row.DeepClone()); responseBytes += bytes;
            if (row["omitted"]?.GetValue<bool>() == true || currentPage["nextCursor"] is null || i + 1 == pageSize) break;
            currentPage = TranslationInspection.Page(workbook, 1, Parse<TranslationInspectionCursor>(currentPage["nextCursor"]!.ToJsonString()));
        }
        int next = offset + bounded.Count;
        page["rows"] = bounded;
        page["captureRef"] = captureRef;
        page["profileArtifactRef"] = ArtifactRef(record, "profile.json");
        page["nextCursor"] = next == page["totalMappedTargets"]!.GetValue<int>() ? null : Node(new TranslationInspectionCursor(
            Text(record.Metadata, "workbookSha256"), Text(record.Metadata, "profileSha256"), Text(record.Metadata, "projectSha256"), next));
        page.Remove("pageSha256");
        page["pageSha256"] = CanonicalJson.Sha256(page);
        return page;
    }

    public JsonObject Prepare(string captureRef, string proposalsArtifactRef)
    {
        using var gate = retained.Lock();
        var capture = RequireKind(retained.Load(captureRef), "capture");
        TranslationRecord? reconciliation = null;
        byte[] bytes;
        if (proposalsArtifactRef?.StartsWith("upload:", StringComparison.Ordinal) == true) bytes = Uploaded(proposalsArtifactRef, InputJsonLimit);
        else
        {
            var (kind, id, name) = ParseRef(proposalsArtifactRef!, true);
            Require(kind == "reconciliation" && name == "proposals.json", "InvalidTranslationReference", "Use committed proposals or a reconciliation proposals artifact.");
            reconciliation = retained.Load(Reference(kind, id));
            Require(Text(reconciliation.Metadata, "currentCaptureRef") == captureRef, "StaleWorkbook", "Reconciled proposals must be reviewed against their exact current capture.");
            bytes = retained.Read(reconciliation, name!);
        }
        var batch = Parse<TranslationProposalBatch>(Encoding.UTF8.GetString(bytes));
        Require(batch.Edits is not null && batch.Edits.Length <= 10000, "TranslationProposalLimit", "A review accepts at most 10,000 explicit edits. Split larger proposals into separately reviewed batches.");
        Require(batch.Edits.All(e => e is not null && !e.Approved && e.Reviewer is null), "TranslationApprovalNotAllowed", "Proposals must remain unapproved with no reviewer. Use explicit review approval after inspecting the complete review.");
        var workbook = OpenCapture(capture);
        CheckReviewBudget(workbook, batch);
        var report = TranslationReview.Review(workbook, workbook, batch.Edits, batch.Options);
        var record = retained.Publish("review", NewId(), new JsonObject
        {
            ["captureRef"] = captureRef, ["reviewSha256"] = Text(report, "reviewSha256"),
            ["changedCells"] = report["changedCells"]!.DeepClone(), ["issueCount"] = report["issues"]!.AsArray().Count,
            ["blockingIssues"] = BlockingIssues(report), ["approved"] = false,
            ["reconciliationConflicts"] = reconciliation?.Metadata["conflicts"]?.DeepClone(),
            ["scope"] = "all supplied proposals; explicit-profile-rows-only"
        }, new Dictionary<string, byte[]> { ["proposals.json"] = bytes, ["review.json"] = JsonBytes(report) },
            reconciliation is null ? [capture] : [capture, reconciliation]);
        return Summary(record);
    }

    public JsonObject ReviewPage(string recordRef, string section = "edits", int pageSize = 100, TranslationReviewCursor? cursor = null)
    {
        Require(pageSize is > 0 and <= 1000, "TranslationPageLimit", "Review page size must be 1..1000.");
        var record = retained.Load(recordRef);
        JsonObject report;
        string artifact;
        if (record.Kind == "review")
        {
            Require(section is "edits" or "issues" or "completeness" or "alarmInheritance", "InvalidTranslationSection", "Review sections: edits, issues, completeness, alarmInheritance.");
            report = VerifiedReview(record).Report;
            artifact = "review.json";
        }
        else
        {
            RequireKind(record, "reconciliation");
            Require(section is "rows" or "proposals", "InvalidTranslationSection", "Reconciliation sections: rows, proposals.");
            report = ReadObject(record, "reconciliation.json");
            artifact = "reconciliation.json";
        }
        int offset = cursor?.Offset ?? 0;
        Require(cursor is null || (cursor.RecordRef == recordRef && cursor.RecordSha256 == record.RecordSha256 && cursor.Section == section),
            "StaleTranslationCursor", "Cursor belongs to a different immutable review or section.");
        var all = section == "alarmInheritance" && report[section] is null ? new JsonArray() : report[section]!.AsArray();
        Require(offset >= 0 && offset <= all.Count, "InvalidTranslationCursor", "Review offset is outside this section.");
        var rows = BoundedRows(all, offset, pageSize, ArtifactRef(record, artifact), offset);
        int next = offset + rows.Count;
        return new JsonObject { ["recordRef"] = recordRef, ["recordSha256"] = record.RecordSha256,
            ["reviewSha256"] = record.Metadata["reviewSha256"]?.DeepClone(), ["section"] = section,
            ["offset"] = offset, ["total"] = all.Count, ["rows"] = rows,
            ["completeArtifactRef"] = ArtifactRef(record, artifact),
            ["nextCursor"] = next == all.Count ? null : Node(new TranslationReviewCursor(recordRef, record.RecordSha256, section, next)),
            ["nativeImportAllowed"] = false };
    }

    public JsonObject Approve(string reviewRef, string exactReviewSha256, string reviewer, bool approveEveryChange)
    {
        using var gate = retained.Lock();
        Require(approveEveryChange && !string.IsNullOrWhiteSpace(reviewer) && reviewer.Length <= 256 && !reviewer.Any(char.IsControl),
            "ExplicitTranslationApprovalRequired", "Caller must explicitly approve every change in this exact complete review and identify the reviewer.");
        var review = RequireKind(retained.Load(reviewRef), "review");
        Require(Text(review.Metadata, "reviewSha256") == exactReviewSha256, "ReviewApprovalMismatch", "Approval must name the exact unapproved review digest shown to the caller.");
        var (workbook, batch, report) = VerifiedReview(review);
        Require(BlockingIssues(report) == 0, "TranslationReviewBlocked", "Resolve review errors in a new review before approval.");
        if (retained.Exists("approval", review.Id))
        {
            var existing = retained.Load(Reference("approval", review.Id));
            Require(Text(existing.Metadata, "reviewer") == reviewer && Text(existing.Metadata, "reviewSha256") == exactReviewSha256,
                "ReviewApprovalMismatch", "This review already has a different immutable approval.");
            return Summary(existing);
        }
        // This is the only place consent is materialized, after the caller's explicit
        // digest-bound approval. Original proposals and review remain unapproved.
        var approved = batch with { Edits = batch.Edits.Select(e => e with { Approved = true, Reviewer = reviewer }).ToArray() };
        var approvedReport = TranslationReview.Review(workbook, workbook, approved.Edits, approved.Options);
        Require(approvedReport["canWriteOfflineCandidate"]!.GetValue<bool>(), "TranslationReviewBlocked", "Approved review still contains errors.");
        var record = retained.Publish("approval", review.Id, new JsonObject
        {
            ["reviewRef"] = reviewRef, ["reviewSha256"] = exactReviewSha256, ["reviewer"] = reviewer,
            ["approveEveryChange"] = true, ["approvedReviewSha256"] = Text(approvedReport, "reviewSha256")
        }, new Dictionary<string, byte[]> { ["approved-proposals.json"] = JsonBytes(approved), ["approved-review.json"] = JsonBytes(approvedReport) }, review);
        return Summary(record);
    }

    public JsonObject Candidate(string reviewRef, string exactReviewSha256, string approvalSha256)
    {
        using var gate = retained.Lock();
        var review = RequireKind(retained.Load(reviewRef), "review");
        Require(Text(review.Metadata, "reviewSha256") == exactReviewSha256, "ReviewApprovalMismatch", "Candidate request must bind the exact reviewed digest.");
        Require(retained.Exists("approval", review.Id), "ExplicitTranslationApprovalRequired", "This review has no completed explicit caller approval.");
        var approval = retained.Load(Reference("approval", review.Id));
        Require(approval.RecordSha256 == approvalSha256 && Text(approval.Metadata, "reviewRef") == reviewRef &&
            Text(approval.Metadata, "reviewSha256") == exactReviewSha256, "ReviewApprovalMismatch", "Candidate request does not match this review's retained approval.");
        var (workbook, batch, _) = VerifiedReview(review);
        // This Host-generated file has already passed its immutable receipt hash.
        // Reviewer annotations can exceed the external 16 MiB input budget.
        var approved = System.Text.Json.JsonSerializer.Deserialize<TranslationProposalBatch>(retained.Read(approval, "approved-proposals.json"), TranslationJson.Options)!;
        var expected = batch with { Edits = batch.Edits.Select(e => e with { Approved = true, Reviewer = Text(approval.Metadata, "reviewer") }).ToArray() };
        Require(HashJson(approved) == HashJson(expected), "TranslationIntegrityFailure", "Approved edits differ from the caller-approved proposals.");
        var candidate = TranslationReview.BuildCandidate(workbook, workbook, approved.Edits, Text(approval.Metadata, "approvedReviewSha256"), approved.Options);
        Require(HashJson(candidate.Manifest["review"]) == HashJson(ReadObject(approval, "approved-review.json")),
            "TranslationIntegrityFailure", "Retained approved review differs from recomputed candidate review.");
        if (retained.Exists("candidate", review.Id)) return Summary(retained.Load(Reference("candidate", review.Id)));
        var manifest = candidate.Manifest;
        var handoff = NativeHandoff(workbook, review, approval, manifest);
        var record = retained.Publish("candidate", review.Id, new JsonObject
        {
            ["reviewRef"] = reviewRef, ["reviewSha256"] = exactReviewSha256, ["approvalSha256"] = approval.RecordSha256,
            ["approvedReviewSha256"] = Text(approval.Metadata, "approvedReviewSha256"),
            ["captureRef"] = Text(review.Metadata, "captureRef"), ["outputSha256"] = Text(manifest, "outputSha256"),
            ["manifestSha256"] = Text(manifest, "manifestSha256"), ["preservationVerified"] = true
        }, new Dictionary<string, byte[]>
        {
            ["candidate.xlsx"] = candidate.Bytes, ["candidate-manifest.json"] = JsonBytes(manifest), ["native-handoff.json"] = JsonBytes(handoff)
        }, approval);
        return Summary(record);
    }

    public JsonObject Reconcile(string baselineCaptureRef, string editedWorkbookArtifactRef, string currentCaptureRef)
    {
        using var gate = retained.Lock();
        var baselineRecord = RequireKind(retained.Load(baselineCaptureRef), "capture");
        var currentRecord = RequireKind(retained.Load(currentCaptureRef), "capture");
        var baseline = OpenCapture(baselineRecord);
        var current = OpenCapture(currentRecord);
        TranslationRecord? candidate = null;
        byte[] editedBytes;
        if (editedWorkbookArtifactRef?.StartsWith("upload:", StringComparison.Ordinal) == true) editedBytes = Uploaded(editedWorkbookArtifactRef, WorkbookLimit);
        else
        {
            var (kind, id, name) = ParseRef(editedWorkbookArtifactRef!, true);
            Require(kind == "candidate" && name == "candidate.xlsx", "InvalidTranslationReference", "Use a committed edited workbook or a completed candidate workbook handle.");
            candidate = retained.Load(Reference(kind, id));
            Require(Text(candidate.Metadata, "captureRef") == baselineCaptureRef, "StaleWorkbook", "Edited candidate belongs to a different baseline capture.");
            editedBytes = retained.Read(candidate, name!);
        }
        var edited = TranslationWorkbook.Open(editedBytes, baseline.Profile, baseline.Project, Sha256(editedBytes));
        CheckMappedTextBudget(edited);
        CheckReconciliationBudget(baseline, edited, current);
        var report = TranslationReconciliation.Reconcile(baseline, edited, current);
        var proposals = Parse<TranslationEdit[]>(report["proposals"]!.ToJsonString());
        var record = retained.Publish("reconciliation", NewId(), new JsonObject
        {
            ["baselineCaptureRef"] = baselineCaptureRef, ["currentCaptureRef"] = currentCaptureRef,
            ["editedWorkbookArtifactRef"] = editedWorkbookArtifactRef, ["reconciliationSha256"] = Text(report, "reconciliationSha256"),
            ["conflicts"] = report["conflicts"]!.DeepClone(), ["alreadyApplied"] = report["alreadyApplied"]!.DeepClone(),
            ["proposals"] = proposals.Length, ["requiresFreshReview"] = true, ["approved"] = false
        }, new Dictionary<string, byte[]>
        {
            ["edited.xlsx"] = editedBytes, ["reconciliation.json"] = JsonBytes(report),
            ["proposals.json"] = JsonBytes(new TranslationProposalBatch(proposals))
        }, candidate is null ? [baselineRecord, currentRecord] : [baselineRecord, currentRecord, candidate]);
        return Summary(record);
    }

    public JsonObject Reopen(string recordRef)
    {
        var record = retained.Load(recordRef);
        if (record.Kind == "review") _ = VerifiedReview(record);
        var result = Summary(record);
        if (record.Kind == "review")
        {
            result["approval"] = Related("approval", record.Id);
            result["candidate"] = Related("candidate", record.Id);
        }
        return result;
    }

    public JsonObject List(string kind, int offset = 0, int count = 20) => retained.List(kind, offset, count);

    public JsonObject ReadArtifact(string artifactRef, long offset = 0, int count = 65536)
    {
        Require(offset >= 0 && count is > 0 and <= 65536, "TranslationPageLimit", "Artifact reads require nonnegative offset and 1..65536 bytes.");
        var (kind, id, name) = ParseRef(artifactRef, true);
        var record = retained.Load(Reference(kind, id));
        byte[] bytes = retained.Read(record, name!);
        Require(offset <= bytes.LongLength, "InvalidTranslationCursor", "Artifact offset is beyond its bytes.");
        int length = (int)Math.Min(count, bytes.LongLength - offset);
        return new JsonObject { ["artifactRef"] = artifactRef, ["bytes"] = bytes.Length, ["sha256"] = Sha256(bytes),
            ["offset"] = offset, ["count"] = length, ["nextOffset"] = offset + length, ["complete"] = offset + length == bytes.LongLength,
            ["encoding"] = "base64", ["data"] = Convert.ToBase64String(bytes, (int)offset, length) };
    }

    private JsonObject? Related(string kind, string id)
    {
        if (!retained.Exists(kind, id)) return null;
        try { return Summary(retained.Load(Reference(kind, id))); }
        catch (BridgeException ex) when (ex.Code == "TranslationPublicationIncomplete")
        { return new JsonObject { ["recordRef"] = Reference(kind, id), ["state"] = "incomplete", ["nativeImportAllowed"] = false }; }
    }

    private (TranslationWorkbook Workbook, TranslationProposalBatch Batch, JsonObject Report) VerifiedReview(TranslationRecord review)
    {
        var capture = RequireKind(retained.Load(Text(review.Metadata, "captureRef")), "capture");
        var workbook = OpenCapture(capture);
        var batch = Parse<TranslationProposalBatch>(Encoding.UTF8.GetString(retained.Read(review, "proposals.json")));
        CheckReviewBudget(workbook, batch);
        var report = TranslationReview.Review(workbook, workbook, batch.Edits, batch.Options);
        Require(Text(report, "reviewSha256") == Text(review.Metadata, "reviewSha256") && HashJson(report) == HashJson(ReadObject(review, "review.json")),
            "TranslationIntegrityFailure", "Recomputed review differs from retained inputs. Prepare a new review; approval cannot be reused.");
        return (workbook, batch, report);
    }

    private TranslationWorkbook OpenCapture(TranslationRecord record)
    {
        var profile = Parse<WorkbookProfile>(Encoding.UTF8.GetString(retained.Read(record, "profile.json")));
        var project = Parse<EngineeringObjectReference>(Encoding.UTF8.GetString(retained.Read(record, "project.json")));
        var workbook = TranslationWorkbook.Open(retained.Read(record, "original.xlsx"), profile, project, Text(record.Metadata, "workbookSha256"));
        Require(workbook.ProfileSha256 == Text(record.Metadata, "profileSha256") && HashJson(project) == Text(record.Metadata, "projectSha256"),
            "TranslationIntegrityFailure", "Capture profile or project observation differs.");
        CheckMappedTextBudget(workbook);
        return workbook;
    }

    private static void CheckMappedTextBudget(TranslationWorkbook workbook)
    {
        // Shared strings can expand to many logical mapped values without a large
        // ZIP/XML payload. Bound that amplification before completeness/review.
        long characters = 0;
        foreach (var row in workbook.Mapping.Rows)
        {
            characters += row.NativeKey.Length + (workbook.ReadText(row.SourceCell).Text?.Length ?? 0);
            foreach (var target in row.Targets) characters += workbook.ReadText(target.Location).Text?.Length ?? 0;
            Require(characters <= 64L * 1024 * 1024, "TranslationMappedTextLimit", "Host captures are limited to 64 Mi UTF-16 code units across mapped keys, sources and targets, counting shared references separately.");
        }
    }

    private static void CheckReviewBudget(TranslationWorkbook workbook, TranslationProposalBatch batch)
    {
        // Reserve worst-case JSON escaping plus row/issue metadata before building
        // a complete report; a small proposal can refer to very large source text.
        long estimated = 65536 + JsonBytes(batch.Options ?? new TranslationReviewOptions()).LongLength;
        foreach (var edit in batch.Edits)
        {
            long characters = 3L * (edit.NativeKey?.Length ?? 0) + 2L * (edit.Value?.Length ?? 0) + (edit.Rationale?.Length ?? 0);
            try
            {
                if (!string.IsNullOrEmpty(edit.NativeKey) && !string.IsNullOrEmpty(edit.Culture))
                {
                    var pair = workbook.Read(edit.NativeKey, edit.Culture);
                    characters += (pair.Source.Text?.Length ?? 0) + (pair.Target.Text?.Length ?? 0);
                }
            }
            catch (BridgeException) { /* The complete review retains the exact identity error. */ }
            estimated += 4096 + characters * 6;
            Require(estimated <= 32L * 1024 * 1024, "TranslationReviewLimit", "Worst-case complete review exceeds the 32 MiB review budget. Split into smaller explicitly reviewed batches.");
        }
    }

    private static void CheckReconciliationBudget(TranslationWorkbook baseline, TranslationWorkbook edited, TranslationWorkbook current)
    {
        // Leave profile mismatch diagnosis to the existing reconciliation guard.
        if (baseline.ProfileSha256 != current.ProfileSha256) return;
        long estimated = 65536;
        int changes = 0;
        foreach (var (row, target) in baseline.OrderedTargets)
        {
            var before = baseline.ReadText(target.Location);
            var after = edited.ReadText(target.Location);
            if (before == after) continue;
            var now = current.ReadText(target.Location);
            estimated += 4096 + 6L * (3L * row.NativeKey.Length + (before.Text?.Length ?? 0) +
                3L * (after.Text?.Length ?? 0) + (now.Text?.Length ?? 0));
            Require(++changes <= 10000 && estimated <= 32L * 1024 * 1024, "TranslationReconciliationLimit",
                "Complete reconciliation exceeds 10,000 changes or its 32 MiB worst-case report budget. Supply a smaller explicit edited batch; no conflict is truncated.");
        }
    }

    private byte[] Uploaded(string reference, int limit)
    {
        var input = uploads.ResolveCommitted(reference);
        byte[] bytes = ReadBounded(input.Path, limit);
        Require(Sha256(bytes) == input.Sha256, "TranslationIntegrityFailure", "Committed upload changed while retaining it.");
        return bytes;
    }

    private JsonObject ReadObject(TranslationRecord record, string name) => JsonNode.Parse(retained.Read(record, name))!.AsObject();
    private static TranslationRecord RequireKind(TranslationRecord record, string kind)
    { Require(record.Kind == kind, "InvalidTranslationReference", "Expected a retained " + kind + " handle."); return record; }
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Text(JsonObject node, string name) => node[name]!.GetValue<string>();
    private static int BlockingIssues(JsonObject report) => report["issues"]!.AsArray().Count(i => i!["code"]!.GetValue<string>() != "UnapprovedTranslationEdit");

    private static JsonArray BoundedRows(JsonArray all, int start, int count, string artifactRef, int logicalOffset)
    {
        var rows = new JsonArray();
        int size = 0;
        foreach (var item in all.Skip(start).Take(count))
        {
            int bytes = Encoding.UTF8.GetByteCount(item!.ToJsonString());
            JsonNode selected = item;
            if (bytes > PageBytes)
            {
                selected = new JsonObject { ["entryIndex"] = logicalOffset + rows.Count, ["omitted"] = true,
                    ["reason"] = "Entry exceeds page byte budget. Read exact content from the complete artifact before approval.", ["artifactRef"] = artifactRef };
                bytes = Encoding.UTF8.GetByteCount(selected.ToJsonString());
            }
            if (size + bytes > PageBytes) break;
            rows.Add(selected.DeepClone()); size += bytes;
        }
        return rows;
    }

    private static JsonObject NativeHandoff(TranslationWorkbook workbook, TranslationRecord review, TranslationRecord approval, JsonObject manifest) => new()
    {
        ["schemaVersion"] = 1, ["kind"] = "translation-native-import-handoff", ["state"] = "disabled-pending-qualification",
        ["destination"] = Node(workbook.Project), ["captureRef"] = Text(review.Metadata, "captureRef"),
        ["reviewRef"] = Reference(review), ["callerApprovedReviewSha256"] = Text(review.Metadata, "reviewSha256"),
        ["approvalSha256"] = approval.RecordSha256, ["candidateSha256"] = Text(manifest, "outputSha256"),
        ["profileSha256"] = workbook.ProfileSha256, ["nativeFreshnessVerified"] = false,
        ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false,
        ["adapterContract"] = "EngineeringOperationStore + trusted NativeTargetObservation; this artifact is not an admitted EngineeringOperationPlan",
        ["requiredGates"] = Node(new[] { "exact-destination-and-active-language-observation", "fresh-native-export-comparison-all-keys-cultures-peers",
            "new-review-on-drift", "shared-resource-admission-and-native-exclusive-access", "retained-archive-checkpoint",
            "durable-dispatch-intent-one-import-no-replay", "retained-native-result-and-complete-log", "independent-native-readback-and-preservation",
            "applicable-compile-save-and-fresh-reopen", "unknown-outcome-reconciliation", "restore-to-new-owned-destination-and-recovered-reopen" }),
        ["qualificationBlockers"] = Node(new[] { "Workbook write/clear/missing-cell and rich-text semantics remain unqualified.",
            "Recorded category-filter mismatches and empty inconclusive controls remain unresolved; do not assume category intersections.",
            "No native freshness, import acceptance, lifecycle or recovery evidence is created by this offline workflow." })
    };
}
