using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class VciTools(HostOptions options)
{
    [McpServerTool(Name = "vci_package_capture", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Capture an offline package manifest from an exact VCI preview/revision and one committed vci-mapping-evidence.v1 JSON upload, at most 1 MiB and 1000 packages/expected files. Retains exact mapping evidence and hashes all retained raw files. Reports missing companions, ownership collisions and unmanaged files. Supplied identities/status are historical assertions, never native freshness or synchronization admission.")]
    public Task<CallToolResult> CapturePackages(string sourceRecordRef, string mappingUploadRef) =>
        Run(s => Task.FromResult(s.CapturePackages(sourceRecordRef, mappingUploadRef)));

    [McpServerTool(Name = "vci_package_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain a three-way package conflict report from exact baseline/project/workspace manifest handles and their reviewed manifest SHA-256 values. Revalidates every mapping and raw source. Classifies two-sided edits, deletions, missing companions, stale hashes and identity/ownership conflicts conservatively. Reports are downloadable through review_artifact_read (kind vci); canSynchronize and resolutionApplied always remain false. No merge, native mutation or Git write occurs.")]
    public Task<CallToolResult> ComparePackages(string baselineRef, string projectRef, string workspaceRef, VciExpectedHashes expectedHashes) =>
        Run(s => Task.FromResult(s.ComparePackages(baselineRef, projectRef, workspaceRef, expectedHashes)));

    [McpServerTool(Name = "vci_prepare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Prepare an offline local Git revision from 1..1000 selected files and committed uploads (16 MiB/file, 64 MiB tree). Null uploadRef with delete=false creates an empty file; delete=true removes a parent file. parentRevisionRef (optional) must be Host-issued. Optional branchName names or creates a new branch at the parent commit; existing branches are never reset or switched into. Returns planSha256 for commit review. No native export/import; push with vci_push.")]
    public Task<CallToolResult> Prepare(VciUploadFile[] files, GitAuthor author, string message, string? parentRevisionRef = null, string? branchName = null,
        CancellationToken cancellationToken = default) => Run(s => s.PrepareAsync(files, author, message, parentRevisionRef, cancellationToken, branchName));

    [McpServerTool(Name = "vci_commit", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Commit exactly the retained previewRef and reviewedPlanSha256 to its private local Git repository. Rechecks all bytes, HEAD/index/config and the configured executable. Durable intent forbids replay after interruption; inspect the candidate and branch from a previous completed revision. Prior revisions remain unchanged. Git success never establishes Siemens compatibility or native qualification.")]
    public Task<CallToolResult> Commit(string previewRef, string reviewedPlanSha256, CancellationToken cancellationToken = default) =>
        Run(s => s.CommitAsync(previewRef, reviewedPlanSha256, cancellationToken));

    // Served by review_reopen (ReviewRecordTools).
    public Task<CallToolResult> Reopen(string recordRef) => Run(s => Task.FromResult(s.Reopen(recordRef)));

    [McpServerTool(Name = "vci_reconcile", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Recover a missing completion receipt only from an exact retained verified commit callback and fresh matching HEAD/tree/index/raw files/executable. Supply previewRef and commitJournal.sha256 from reopen. Writes the missing local receipt without replaying Git mutation. Earlier, damaged or mismatched outcomes remain unknown; no native recovery is inferred.")]
    public Task<CallToolResult> Reconcile(string previewRef, string expectedCallbackSha256, CancellationToken cancellationToken = default) =>
        Run(s => s.ReconcileAsync(previewRef, expectedCallbackSha256, cancellationToken));

    [McpServerTool(Name = "vci_history", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read 1..50 commits from an exact Host-issued local revision. Returns at most 64 KiB of documented NUL-delimited Git history; use a smaller count if exceeded. Rejects changed repository state. No network or native engineering.")]
    public Task<CallToolResult> History(string revisionRef, int count = 20, CancellationToken cancellationToken = default) =>
        Run(s => s.HistoryAsync(revisionRef, count, cancellationToken));

    [McpServerTool(Name = "vci_branches", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect fresh local branch refs in one exact retained revision repository. Returns 1..50 rows, at most 64 KiB. Pin the returned inventorySha256 as expectedInventorySha256 on every subsequent page; changed inventories fail instead of mixing results. No remotes, branch resets, arbitrary paths or native calls.")]
    public Task<CallToolResult> Branches(string revisionRef, int offset = 0, int count = 20, string? expectedInventorySha256 = null,
        CancellationToken cancellationToken = default) => Run(s => s.BranchesAsync(revisionRef, offset, count, expectedInventorySha256, cancellationToken));

    [McpServerTool(Name = "vci_materialize", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Materialize the exact commit from a Host-issued revision into a new managed directory and downloadable revision.zip plus SHA-256 file manifest. Never checks out into or changes an existing revision. Preserves raw file bytes and paths; does not reconstruct TIA projects or OS permissions. Interrupted exports remain discoverable incomplete records.")]
    public Task<CallToolResult> Materialize(string revisionRef, CancellationToken cancellationToken = default) =>
        Run(s => s.MaterializeAsync(revisionRef, cancellationToken));

    [McpServerTool(Name = "vci_push", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Push the exact commit of a Host-issued committed or fetched revision to branchName on the one Host-configured remote. Compare-and-swap: the remote branch must equal expectedRemoteCommit (omit if it must not exist) and the commit must descend from it; no forced update; reads back. Refuses a local remote that is not a minimal bare repository. A push without receipt stays outcomeUnknown; inspect the remote before repeating. No credentials or caller URLs.")]
    public Task<CallToolResult> Push(string revisionRef, string branchName, string? expectedRemoteCommit = null, CancellationToken cancellationToken = default) =>
        Run(s => s.PushAsync(revisionRef, branchName, expectedRemoteCommit, cancellationToken));

    [McpServerTool(Name = "vci_fetch", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Fetch branchName from the one Host-configured remote into a new fetched-revision record. With parentRevisionRef (a revision on the same branch name) the copy fast-forwards and refuses divergent history; without it a new repository takes the remote commit. Optional expectedRemoteCommit pins the reviewed commit. The remote tree must hold only regular files, no .gitattributes/.gitmodules. Prior records never change; no native engineering.")]
    public Task<CallToolResult> Fetch(string branchName, string? parentRevisionRef = null, string? expectedRemoteCommit = null, CancellationToken cancellationToken = default) =>
        Run(s => s.FetchAsync(branchName, parentRevisionRef, expectedRemoteCommit, cancellationToken));

    // Served by review_list (ReviewRecordTools).
    public Task<CallToolResult> List(int offset = 0, int count = 20) => Run(s => Task.FromResult(s.List(offset, count)));

    // Served by review_artifact_read (ReviewRecordTools).
    public Task<CallToolResult> ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) =>
        Run(s => Task.FromResult(s.ReadArtifact(recordRef, name, offset, count)));

    [McpServerTool(Name = "vci_file_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Review exact raw file bytes from a VCI preview/revision using a relativePath listed in files.json. At most 64 KiB per call, with complete file hash and byte count. New files not present in tracked Git diff must be reviewed here. Rejects foreign paths or changed retained contents.")]
    public Task<CallToolResult> FileRead(string recordRef, string relativePath, long offset = 0, int count = 65536) =>
        Run(s => Task.FromResult(s.ReadFile(recordRef, relativePath, offset, count)));

    private Task<CallToolResult> Run(Func<VciWorkspaceStore, Task<object>> action) => ToolResult.GuardAsync(async () =>
    {
        if (options.GitExecutable is null) throw new BridgeException("GitNotConfigured", "Start the Host with --git-executable and an absolute installed Git path.");
        return await action(new VciWorkspaceStore(options.ArtifactsDirectory, options.GitExecutable, options.LibraryLimits, options.GitRemote));
    }, "VciWorkflowFailed", "Local VCI input, integrity or publication failed. Inspect retained records before continuing.");
}
