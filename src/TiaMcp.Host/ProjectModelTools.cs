using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    private static readonly HashSet<string> ModelReadActions = new(StringComparer.Ordinal)
        { "capture", "children", "search", "details", "relationships", "verify", "release", "export", "status" };

    [McpServerTool(Name = "project_model", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read-only project-model.v1 explorer. request.action: capture, children, search, details, relationships, verify, release, export, status. Native/virtual ancestry and provider coverage are explicit. Selection uses exact snapshot/session/project/node/object reference; metadata revision never certifies content. No project mutation or online access. Export formats: json, html (portable offline viewer).")]
    public Task<CallToolResult> ProjectModel(JsonElement request, CancellationToken cancellationToken)
        => ModelRead(request, null, cancellationToken);

    [McpServerTool(Name = "project_model_capture", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Capture bounded native hierarchy including empty groups. request: projectName, optional plcPath, maxVisited, maxDepth, maxBytes, lifetimeSeconds. Returns project-model.v1 snapshot header, roots, measured native capture and provider coverage. Metadata freshness excludes content.")]
    public Task<CallToolResult> ProjectModelCapture(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "capture", cancellationToken);

    [McpServerTool(Name = "project_model_children", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read deterministic cached children. request: snapshotId, parentId (null for roots), maxItems (1..200), cursor. Repeat selectors with authenticated cursor. Expired or invalidated snapshots require recapture.")]
    public Task<CallToolResult> ProjectModelChildren(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "children", cancellationToken);

    [McpServerTool(Name = "project_model_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search cached project metadata with exact ancestry. request: snapshotId, text, kind, plcPath, scopePath, maxItems (1..200), cursor. Results preserve case-sensitive canonical identities even for identical display names.")]
    public Task<CallToolResult> ProjectModelSearch(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "search", cancellationToken);

    [McpServerTool(Name = "project_model_details", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect an exact selected object after native metadata verification. request.reference contains schemaVersion, snapshotId, sessionGeneration, projectPath, nodeId, objectPath. Returns separately timestamped content/detail revision and typed unavailable/protected states. No same-name repair after rename/delete.")]
    public Task<CallToolResult> ProjectModelDetails(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "details", cancellationToken);

    [McpServerTool(Name = "project_model_relationships", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read evidence-backed relationships for request.reference; optional kinds, direction, depth (1..5), maxNodes (1..500), maxEdges (1..1000). Defaults 1/100/200. Calls, instances, reads, writes, type/dependency and configured I/O evidence remain distinct; unresolved endpoints and partial coverage remain explicit.")]
    public Task<CallToolResult> ProjectModelRelationships(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "relationships", cancellationToken);

    [McpServerTool(Name = "project_model_verify", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Recapture the original bounded scope to verify current metadata for request.snapshotId. This does not verify logic/content or a whole-project revision. Stale selections require explicit repair and cannot fall back to names.")]
    public Task<CallToolResult> ProjectModelVerify(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "verify", cancellationToken);

    [McpServerTool(Name = "project_model_release", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Release cached explorer snapshot request.snapshotId and its cursors. Does not alter the TIA project.")]
    public Task<CallToolResult> ProjectModelRelease(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "release", cancellationToken);

    [McpServerTool(Name = "project_model_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export request.snapshotId as json or self-contained html (request.format). Returns hashed artifact manifest, coverage and host-owned paths; transfer with artifact_read. HTML needs no network and preserves exact selections, evidence, capture times and freshness limitations.")]
    public Task<CallToolResult> ProjectModelExport(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "export", cancellationToken);

    private async Task<CallToolResult> ModelRead(JsonElement request, string? action, CancellationToken cancellationToken)
    {
        try
        {
            if (request.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidRequest", "request must be an object.");
            var args = JsonNode.Parse(request.GetRawText())!.AsObject();
            action ??= args["action"]?.GetValue<string>() ?? "capture";
            if (!ModelReadActions.Contains(action)) throw new BridgeException("InvalidRequest", "Only the documented read-only project model actions are allowed.");
            args["action"] = action;
            // Callers never choose a server-side output path or arbitrary worker operation.
            args.Remove("artifactDirectory");
            args["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "project-explorer");
            var format = args["format"]?.GetValue<string>() ?? "json";
            if (action == "export" && format is not ("json" or "html"))
                throw new BridgeException("InvalidRequest", "Export format must be json or html; graph SVG/PNG exports are available in the viewer.");
            var result = await bridge.CallAsync("project_model", args, cancellationToken);
            if (action == "export" && format == "html")
                result = ProjectExplorerArtifacts.CreateViewer(result, options.ArtifactsDirectory);
            return Result(result.GetRawText(), false);
        }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return Result(JsonSerializer.Serialize(new { error = new { code = (error as BridgeException)?.Code ?? "ProjectModelFailed", message = error.Message } }, BridgeProtocol.Json), true);
        }
    }
}
