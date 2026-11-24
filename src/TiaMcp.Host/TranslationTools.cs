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
    [Description("Page exact key/culture translation states, hashes and gap findings in a retained capture. offset/count pages 1..1000 cells within a 192 KiB row budget; continue at nextOffset. Oversized rows are explicitly omitted with artifact handles; missing, empty and whitespace remain distinct. Captured evidence only.")]
    public CallToolResult Inspect(string captureRef, int offset = 0, int count = 100) =>
        Run(s => s.Inspect(captureRef, offset, count));

    [McpServerTool(Name = "translation_prepare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Prepare an immutable complete review from a capture and committed proposal-batch JSON {edits, options}, or reconciliation proposals handle. At most 10000 edits, each with exact nativeKey, culture, kind, value, expectedSourceSha256 and expectedTargetSha256. approved must be false and reviewer null. Retains all proposals, issues and review digest. No approval or candidate is inferred.")]
    public CallToolResult Prepare(string captureRef, string proposalsArtifactRef) => Run(s => s.Prepare(captureRef, proposalsArtifactRef));

    // Served by review_page (ReviewRecordTools).
    public CallToolResult ReviewPage(string recordRef, string section = "edits", int offset = 0, int count = 100) =>
        Run(s => s.ReviewPage(recordRef, section, offset, count));

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

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(string kind, int offset = 0, int count = 20) => Run(s => s.List(kind, offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ReadArtifact(string artifactRef, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(artifactRef, offset, count));

    private CallToolResult Run(Func<TranslationWorkflowStore, object> action)
    {
        try { return ToolResult.Of(action(new TranslationWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits))); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return ToolResult.Failure(error, "TranslationWorkflowFailed", "Offline translation input or publication failed. List retained records to inspect incomplete publication; no native action occurred."); }
    }
}
