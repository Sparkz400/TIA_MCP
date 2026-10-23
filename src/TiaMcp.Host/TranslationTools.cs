using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Translations;

namespace TiaMcp.Host;

/// <summary>Offline tools intentionally have no IBridgeClient or native adapter dependency.</summary>
[McpServerToolType]
public sealed class TranslationTools(HostOptions options)
{
    [McpServerTool(Name = "translation_capture", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain a captured XLSX, explicit WorkbookProfile JSON and exact EngineeringObjectReference JSON from three committed upload: handles. Returns immutable Host handles, never accepts filesystem paths. Caller-supplied capture provenance is not native freshness. No TIA startup, export or import.")]
    public CallToolResult Capture(string workbookUploadRef, string profileUploadRef, string projectUploadRef) =>
        Run(s => s.Capture(workbookUploadRef, profileUploadRef, projectUploadRef));

    [McpServerTool(Name = "translation_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page exact key/culture translation states, hashes and gap findings in a retained capture. 1..1000 cells and a 192 KiB row budget. Cursor binds workbook/profile/project hashes. Oversized rows are explicitly omitted with artifact handles; missing, empty and whitespace remain distinct. Captured evidence only.")]
    public CallToolResult Inspect(string captureRef, int pageSize = 100, TranslationInspectionCursor? cursor = null) =>
        Run(s => s.Inspect(captureRef, pageSize, cursor));

    [McpServerTool(Name = "translation_prepare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Prepare an immutable complete review from a capture and committed proposal-batch JSON {edits, options}, or reconciliation proposals handle. At most 10000 edits, each with exact nativeKey, culture, kind, value, expectedSourceSha256 and expectedTargetSha256. approved must be false and reviewer null. Retains all proposals, issues and review digest. No approval or candidate is inferred.")]
    public CallToolResult Prepare(string captureRef, string proposalsArtifactRef) => Run(s => s.Prepare(captureRef, proposalsArtifactRef));

    [McpServerTool(Name = "translation_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read an immutable review section (edits, issues, completeness, alarmInheritance), or reconciliation section (rows, proposals). 1..1000 entries with 192 KiB row budget and a bound cursor. Alarm inheritance covers explicit mapped language fields only. Full artifact retains exact unabridged content. Review every change and issue, including omitted entries through translation_artifact_read, before explicit approval.")]
    public CallToolResult ReviewPage(string recordRef, string section = "edits", int pageSize = 100, TranslationReviewCursor? cursor = null) =>
        Run(s => s.ReviewPage(recordRef, section, pageSize, cursor));

    [McpServerTool(Name = "translation_approve", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Record explicit caller approval of every change in the exact complete review digest after review. Requires reviewer and approveEveryChange=true; never infer consent from text, suggestions or previous approvals. Retains immutable approval receipt; unresolved profile/state/preservation errors block approval. This grants only offline candidate creation, never native import.")]
    public CallToolResult Approve(string reviewRef, string exactReviewSha256, string reviewer, bool approveEveryChange) =>
        Run(s => s.Approve(reviewRef, exactReviewSha256, reviewer, approveEveryChange));

    [McpServerTool(Name = "translation_candidate", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Create one preserving offline candidate from the exact review and retained approval recordSha256. Revalidates all retained inputs and library preservation checks. Returns candidate, manifest and disabled native-import handoff handles. Publication is immutable and receipt-last; incomplete attempts require a new review, never overwrite or imply success. No native import.")]
    public CallToolResult Candidate(string reviewRef, string exactReviewSha256, string approvalSha256) =>
        Run(s => s.Candidate(reviewRef, exactReviewSha256, approvalSha256));

    [McpServerTool(Name = "translation_reconcile", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Three-way offline reconciliation of exact baseline capture, edited workbook (committed upload or candidate XLSX handle) and newer current capture. Retains conflicts and fresh unapproved proposals bound to current states. Uploaded edits explicitly declare baseline provenance; this does not prove native freshness. Subsequent review/candidate must use current; no previous approval is reused.")]
    public CallToolResult Reconcile(string baselineCaptureRef, string editedWorkbookArtifactRef, string currentCaptureRef) =>
        Run(s => s.Reconcile(baselineCaptureRef, editedWorkbookArtifactRef, currentCaptureRef));

    [McpServerTool(Name = "translation_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a retained translation record after restart. Verify all input and artifact hashes; recompute reviews. Review summaries include related approval and candidate publication states. Changed retained inputs fail closed and require a new review. Returns bounded summaries and artifact handles.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "translation_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover retained capture, review, approval, candidate or reconciliation records after restart, including incomplete or integrity-failed publications. Count 1..100 with bounded responses. Sorted record-ID offsets are for discovery; relist after concurrent additions. No native operation is retried.")]
    public CallToolResult List(string kind, int offset = 0, int count = 20) => Run(s => s.List(kind, offset, count));

    [McpServerTool(Name = "translation_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Retrieve at most 64 KiB of a completed immutable translation artifact by Host-issued handle, with SHA-256 and byte offset. Revalidates retained inputs; accepts no filesystem path. Use to read full reviews and download original/candidate XLSX, profiles, manifests and disabled native handoffs.")]
    public CallToolResult ReadArtifact(string artifactRef, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(artifactRef, offset, count));

    private CallToolResult Run(Func<TranslationWorkflowStore, object> action)
    {
        try { return Format(action(new TranslationWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)), false); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return Format(new { error = new { code = (error as BridgeException)?.Code ?? "TranslationWorkflowFailed",
                message = error is BridgeException ? error.Message : "Offline translation input or publication failed. List retained records to inspect incomplete publication; no native action occurred.",
                details = (error as BridgeOperationException)?.Details } }, true);
        }
    }

    private static CallToolResult Format(object value, bool error)
    {
        string json = JsonSerializer.Serialize(value, BridgeProtocol.Json);
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
