using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class LibraryPackageTools(HostOptions options)
{
    [McpServerTool(Name = "artifact_upload_begin", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Begin a bounded resumable upload to this TIA host. Supply the original display filename, exact byte count and SHA-256. The server chooses all storage paths and returns uploadId, chunk limit and expiry. Does not start TIA.")]
    public CallToolResult Begin(string fileName, long expectedBytes, string sha256) => Run(() => Uploads().Begin(fileName, expectedBytes, sha256));

    [McpServerTool(Name = "artifact_upload_chunk", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Append base64 bytes at receivedBytes for a server-issued uploadId. Default maximum is 64 KiB decoded. Retrying previously persisted bytes succeeds only if they match; gaps and partial overlaps are rejected.")]
    public CallToolResult Chunk(string uploadId, long offset, string data) => Run(() => Uploads().Write(uploadId, offset, data));

    [McpServerTool(Name = "artifact_upload_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read durable upload progress, contiguous received byte range, expiry and committed artifactRef from this host. Status supports resuming byte transfer only, never a native engineering mutation.")]
    public CallToolResult UploadStatus(string uploadId) => Run(() => Uploads().Status(uploadId));

    [McpServerTool(Name = "artifact_upload_commit", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Verify the expected complete length and SHA-256 and finalize an immutable artifactRef. Repeating commit verifies and returns the same artifact. Incomplete or mismatching uploads cannot be staged.")]
    public CallToolResult Commit(string uploadId) => Run(() => Uploads().Commit(uploadId));

    [McpServerTool(Name = "artifact_upload_abort", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Abort the specified bridge-owned incomplete upload and release its byte reservation. Committed artifacts cannot be aborted. Does not modify supplied source files or any TIA project.")]
    public CallToolResult Abort(string uploadId) => Run(() => Uploads().Abort(uploadId));

    [McpServerTool(Name = "artifact_upload_release", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Explicitly remove a committed, aborted or expired upload and release its storage/count quota. Active incomplete uploads must first be aborted. Staged packages keep independent verified copies; a global storage lock prevents release during staging or transfer. Only the server-issued upload directory is removed.")]
    public CallToolResult ReleaseUpload(string uploadId) => Run(() => Uploads().Release(uploadId));

    [McpServerTool(Name = "library_package_stage", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Copy a committed upload artifactRef OR explicit hostPath (file or complete library directory) into managed intake storage on this TIA host. Validate ZIP paths and bounded nested extraction, retain hashes and classify all candidates without starting TIA or executing package scripts. Paths refer to the host machine. sourceUrl is provenance only, never fetched. A lone .alXX is incomplete; native archives remain opaque until Siemens retrieval.")]
    public CallToolResult Stage(string? artifactRef = null, string? hostPath = null, string? sourceUrl = null,
        CancellationToken cancellationToken = default) => Run(() => Packages().Stage(artifactRef, hostPath, sourceUrl, cancellationToken));

    [McpServerTool(Name = "library_package_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page an immutable staged package by section: candidates, files, documents or diagnostics. At most 200 items per page. Candidates distinguish global library directories/archives, example projects and loose sources. Classification does not establish native compatibility; no candidate is chosen automatically.")]
    public CallToolResult Inspect(string packageId, string section = "candidates", int offset = 0, int count = 100) =>
        Run(() => Packages().Inspect(packageId, section, offset, count));

    [McpServerTool(Name = "library_package_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read package staging state, hashes, configured limits and retention references. Failed/interrupted staging stays unusable and retains evidence until explicit release. No automatic native replay occurs.")]
    public CallToolResult Status(string packageId) => Run(() => Packages().Status(packageId));

    [McpServerTool(Name = "library_package_release", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Explicitly remove only this managed staged package, including failed extraction evidence. Refuses packages retained by active sessions, plans, lock manifests or recovery journals. Source files and committed uploads remain untouched.")]
    public CallToolResult Release(string packageId) => Run(() => Packages().Release(packageId));

    private ArtifactUploadStore Uploads() => new(options.ArtifactsDirectory, options.LibraryLimits);
    private PackageStore Packages() => new(options.ArtifactsDirectory, options.LibraryLimits);
    private static CallToolResult Run(Func<object> action)
    {
        try { return Format(action(), false); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            return Format(new { error = new { code = (error as BridgeException)?.Code ?? (error is OperationCanceledException ? "OperationCancelled" : "PackageInvalid"),
                message = error.Message, details = (error as BridgeOperationException)?.Details } }, true);
        }
    }
    private static CallToolResult Format(object value, bool error)
    {
        var json = JsonSerializer.Serialize(value, BridgeProtocol.Json);
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
