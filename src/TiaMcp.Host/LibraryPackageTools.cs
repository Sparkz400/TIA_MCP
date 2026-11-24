using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class LibraryPackageTools(HostOptions options)
{
    [McpServerTool(Name = "artifact_upload_begin", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactUpload))]
    [Description("Begin a bounded resumable upload to this TIA host. Supply the original display filename, exact byte count and SHA-256. The server chooses all storage paths and returns uploadRef, chunk limit and expiry. Does not start TIA.")]
    public CallToolResult Begin(string fileName, long expectedBytes, string sha256) => Run(() => ArtifactUpload.Of(Uploads().Begin(fileName, expectedBytes, sha256)));

    [McpServerTool(Name = "artifact_upload_chunk", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactUpload))]
    [Description("Append base64 bytes at receivedBytes for a server-issued uploadRef. Default maximum is 64 KiB decoded. Retrying previously persisted bytes succeeds only if they match; gaps and partial overlaps are rejected.")]
    public CallToolResult Chunk(string uploadRef, long offset, string data) => Run(() => ArtifactUpload.Of(Uploads().Write(Id(uploadRef), offset, data)));

    [McpServerTool(Name = "artifact_upload_status", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactUpload))]
    [Description("Read durable upload progress, contiguous received byte range, expiry and committed artifactRef from this host. Status supports resuming byte transfer only, never a native engineering mutation.")]
    public CallToolResult UploadStatus(string uploadRef) => Run(() => ArtifactUpload.Of(Uploads().Status(Id(uploadRef))));

    [McpServerTool(Name = "artifact_upload_commit", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactUpload))]
    [Description("Verify the expected complete length and SHA-256 and finalize an immutable artifactRef. Repeating commit verifies and returns the same artifact. Incomplete or mismatching uploads cannot be staged.")]
    public CallToolResult Commit(string uploadRef) => Run(() => ArtifactUpload.Of(Uploads().Commit(Id(uploadRef))));

    [McpServerTool(Name = "artifact_upload_abort", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ArtifactUpload))]
    [Description("Abort the specified bridge-owned incomplete upload and release its byte reservation. Committed artifacts cannot be aborted. Does not modify supplied source files or any TIA project.")]
    public CallToolResult Abort(string uploadRef) => Run(() => ArtifactUpload.Of(Uploads().Abort(Id(uploadRef))));

    [McpServerTool(Name = "artifact_upload_release", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Explicitly remove a committed, aborted or expired upload and release its storage/count quota. Active incomplete uploads must first be aborted. Staged packages keep independent verified copies; a global storage lock prevents release during staging or transfer. Only the server-issued upload directory is removed.")]
    public CallToolResult ReleaseUpload(string uploadRef) => Run(() =>
    {
        var released = JsonSerializer.SerializeToNode(Uploads().Release(Id(uploadRef)), BridgeProtocol.Json)!.AsObject();
        released.Remove("uploadId");
        var result = new System.Text.Json.Nodes.JsonObject { ["uploadRef"] = uploadRef };
        foreach (var (key, value) in released.ToArray()) { released.Remove(key); result[key] = value; }
        return result;
    });

    [McpServerTool(Name = "library_package_stage", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Copy a committed uploadRef OR explicit hostPath into managed intake storage on this TIA host. hostPath must name a .zip, a native .alXX/.zalXX/.apXX/.zapXX file or a library/project directory with an .alXX/.apXX marker at its top; others are refused before reading. Validates ZIP paths, retains hashes and classifies candidates without starting TIA or executing package scripts. sourceUrl is provenance only, never fetched.")]
    public CallToolResult Stage(string? uploadRef = null, string? hostPath = null, string? sourceUrl = null,
        CancellationToken cancellationToken = default) => Run(() => Packages().Stage(uploadRef, hostPath, sourceUrl, cancellationToken));

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

    // uploadRef is the store's upload ID behind the upload: prefix, the same handle the consuming tools take once it is committed.
    private static string Id(string uploadRef) => uploadRef is not null && uploadRef.StartsWith(ArtifactUpload.Prefix, StringComparison.Ordinal)
        ? uploadRef[ArtifactUpload.Prefix.Length..] : throw new BridgeException("InvalidRequest", "Use the uploadRef (upload:...) returned by artifact_upload_begin.");
    private PackageStore Packages() => new(options.ArtifactsDirectory, options.LibraryLimits);
    private static CallToolResult Run(Func<object> action)
    {
        try { return ToolResult.Of(action()); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        { return ToolResult.Failure(error, "PackageInvalid", error.Message); }
    }
}

/// <summary>An upload as the artifact_upload_* tools report it: the store status, with the upload addressed by uploadRef.</summary>
public sealed record ArtifactUpload(int SchemaVersion, string UploadRef, string FileName, long ExpectedBytes,
    string ExpectedSha256, long ReceivedBytes, long RemainingBytes, string State, string? ArtifactRef,
    DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, int MaxChunkBytes, string Host, string HostPath,
    long? AcceptedOffset = null, int? AcceptedBytes = null, string? ChunkSha256 = null, PackageIntakeLimits? Limits = null)
{
    public const string Prefix = "upload:";

    internal static ArtifactUpload Of(ArtifactUploadStatus s) => new(s.SchemaVersion, Prefix + s.UploadId, s.FileName, s.ExpectedBytes,
        s.ExpectedSha256, s.ReceivedBytes, s.RemainingBytes, s.State, s.ArtifactRef, s.CreatedUtc, s.ExpiresUtc, s.MaxChunkBytes, s.Host, s.HostPath,
        s.AcceptedOffset, s.AcceptedBytes, s.ChunkSha256, s.Limits);
}
