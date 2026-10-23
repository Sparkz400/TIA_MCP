using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "library_open", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Open a staged native V21 library candidate, an explicit host path plus SHA-256, or kind=project. Native directories are copied and archives retrieved into unique owned working directories. Original sources remain untouched. writableWorkingCopy must explicitly be true for later library maintenance. Older libraries use prepared library_upgrade. Request supports packageId/candidateId OR path/expectedSha256, kind, projectName. HMI and PLC changes always use change_prepare/apply.")]
    public async Task<CallToolResult> LibraryOpen(JsonElement request, CancellationToken cancellationToken)
    {
        var args = JsonNode.Parse(ObjectArguments(request).GetRawText())!.AsObject();
        args["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "libraries");
        string? packageId = args["packageId"]?.GetValue<string>();
        string? packageReference = null;
        if (packageId is not null)
        {
            try
            {
                var packages = new PackageStore(options.ArtifactsDirectory, options.LibraryLimits);
                string candidateId = args["candidateId"]?.GetValue<string>() ?? throw new BridgeException("PackageAmbiguous", "An exact candidateId returned by library_package_inspect is required.");
                if (args["path"] is not null) throw new BridgeException("InvalidRequest", "Use a package candidate or host path, not both.");
                string reference = "native-open-" + Guid.NewGuid().ToString("N");
                packageReference = reference;
                packages.Retain(packageId, reference);
                var resolved = packages.ResolveCandidateReceipt(packageId, candidateId);
                string path = resolved.Path;
                if (!File.Exists(path)) throw new BridgeException("PackageInvalid", "Candidate does not identify a native library/archive file.");
                args["path"] = path; args["expectedSha256"] = resolved.Sha256;
                if (Path.GetExtension(path).StartsWith(".al", StringComparison.OrdinalIgnoreCase))
                    args["expectedFiles"] = JsonSerializer.SerializeToNode(packages.GetCandidateFiles(packageId, candidateId), BridgeProtocol.Json);
                args["packageRetentionReference"] = reference;
                // Keep the reference after uncertain native outcomes. Native journals and recovery copies
                // may still depend on it; a lost response is not evidence that the open did nothing.
            }
            catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException)
            {
                if (packageReference is not null) new PackageStore(options.ArtifactsDirectory, options.LibraryLimits).Unretain(packageId, packageReference);
                return Result(JsonSerializer.Serialize(new { error = new { code = (error as BridgeException)?.Code ?? "PackageInvalid", message = error.Message } }, BridgeProtocol.Json), true);
            }
        }
        if (System.Text.Encoding.UTF8.GetByteCount(args.ToJsonString(BridgeProtocol.Json)) > 3 * 1024 * 1024)
        {
            if (packageId is not null && packageReference is not null) new PackageStore(options.ArtifactsDirectory, options.LibraryLimits).Unretain(packageId, packageReference);
            return Result(JsonSerializer.Serialize(new { error = new { code = "PackageLimitExceeded", message = "The complete companion manifest exceeds the native request budget. Supply a native library archive; companion entries are never truncated." } }, BridgeProtocol.Json), true);
        }
        var result = await Call("library_open", args, cancellationToken);
        // This one worker error is emitted before copying or opening any native
        // resource. Other failures, transport loss and cancellation remain pinned.
        bool rejectedBeforeNativeAccess = result.IsError == true
            && result.StructuredContent is JsonElement response && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("error", out var responseError) && responseError.ValueKind == JsonValueKind.Object
            && responseError.TryGetProperty("code", out var responseCode) && responseCode.ValueKind == JsonValueKind.String
            && responseCode.GetString() == "LibraryUpgradeRequired";
        if (packageId is not null && packageReference is not null && (result.IsError != true || rejectedBeforeNativeAccess))
        {
            // Success owns an independent snapshot; the allowlisted rejection
            // created no native dependency. Both release this attempt's pin only.
            new PackageStore(options.ArtifactsDirectory, options.LibraryLimits).Unretain(packageId, packageReference);
        }
        return result;
    }

    [McpServerTool(Name = "library_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Register/list currently open project and global libraries, their session IDs, metadata revisions, ownership and write eligibility. Externally owned global libraries are read-only to this bridge. Also lists installed library catalog entries.")]
    public Task<CallToolResult> LibraryList(CancellationToken cancellationToken) => Call("library_list", new { }, cancellationToken);

    [McpServerTool(Name = "library_query", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read library elements, versions, dependencies or uses. Request requires libraryId and query; version-specific queries require exact typeGuid/versionGuid. maxItems 1..200; subsequent offset pages require revision from the first page. Native use searches require an exact project scope and report incomplete software coverage. HMI query actions hmi_targets/hmi_inventory/hmi_capabilities use separate installed adapters.")]
    public Task<CallToolResult> LibraryQuery(JsonElement request, CancellationToken cancellationToken)
    {
        var args = JsonNode.Parse(ObjectArguments(request).GetRawText())!.AsObject();
        args["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "library-exports");
        return Call("library_query", args, cancellationToken);
    }

    [McpServerTool(Name = "library_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare two native libraries using leftLibraryId/rightLibraryId, or exact selected versions using left/right objects with typeGuid/versionGuid. Returns bounded Siemens comparison results. Comparison does not establish runtime compatibility.")]
    public Task<CallToolResult> LibraryCompare(JsonElement request, CancellationToken cancellationToken) => Call("library_compare", ObjectArguments(request), cancellationToken);

    [McpServerTool(Name = "library_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export the exact project-library typeGuid/versionGuid as native documents into a unique managed directory, with SHA-256 manifest and version info. Requires libraryId, typeGuid, versionGuid and one supportedExportFormats value as format. No native library/project modification; global versions must first be copied to a project library. Returns documentDirectory and documentName for prepared authoring.")]
    public Task<CallToolResult> LibraryExport(JsonElement request, CancellationToken cancellationToken)
    {
        var args = JsonNode.Parse(ObjectArguments(request).GetRawText())!.AsObject(); args["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "library-exports");
        return Call("library_export", args, cancellationToken);
    }

    [McpServerTool(Name = "library_close", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Release a library session ID. Only unchanged owned user-library handles are closed; externally owned libraries and projects stay open. Working directories and recovery evidence are retained. Save modified working libraries through library_save first.")]
    public Task<CallToolResult> LibraryClose(string libraryId, CancellationToken cancellationToken) => Call("library_close", new { libraryId }, cancellationToken);

    [McpServerTool(Name = "library_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect compiled library APIs, implemented typed change kinds, limits and native qualification boundaries. Does not infer target eligibility from the existence of an API. Use source identities and preparation for exact preflight.")]
    public Task<CallToolResult> LibraryCapabilities(CancellationToken cancellationToken) => Call("library_capabilities", new { }, cancellationToken);
}
