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
    [Description("Read-only explorer of the open project (project-model.v1), the one tool for hierarchy, search, details, relationships and export. request.action selects: capture (projectName, plcPath?, maxVisited, maxDepth, maxBytes, lifetimeSeconds; native hierarchy including empty groups), children (snapshotId, parentId or null for roots, maxItems 1..200, cursor), search (snapshotId, text, kind, plcPath, scopePath, maxItems 1..200, cursor), details (reference {schemaVersion, snapshotId, sessionGeneration, projectPath, nodeId, objectPath}), relationships (reference; kinds, direction, depth 1..5, maxNodes 1..500, maxEdges 1..1000; providerScope names the evidence providers and unresolved endpoints stay explicit), verify, release and status (snapshotId), export (snapshotId, format json or html; returns a hashed artifact manifest, fetch it with artifact_read). Selections use exact references, never names; metadata freshness does not certify content or the whole project; configured network and port edges are not verified cables. No mutation or online access. Provider scopes: docs/reference/project-explorer-relationship-scope.md.")]
    public Task<CallToolResult> ProjectModel(JsonElement request, CancellationToken cancellationToken)
        => ModelRead(request, null, cancellationToken);

    [McpServerTool(Name = "project_model_capture", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=capture; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelCapture(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "capture", cancellationToken);

    [McpServerTool(Name = "project_model_children", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=children; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelChildren(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "children", cancellationToken);

    [McpServerTool(Name = "project_model_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=search; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelSearch(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "search", cancellationToken);

    [McpServerTool(Name = "project_model_details", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=details; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelDetails(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "details", cancellationToken);

    [McpServerTool(Name = "project_model_relationships", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=relationships; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelRelationships(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "relationships", cancellationToken);

    [McpServerTool(Name = "project_model_verify", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=verify; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelVerify(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "verify", cancellationToken);

    [McpServerTool(Name = "project_model_release", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=release; same request and result. Use project_model; this alias will be removed in a later major release.")]
    public Task<CallToolResult> ProjectModelRelease(JsonElement request, CancellationToken cancellationToken) => ModelRead(request, "release", cancellationToken);

    [McpServerTool(Name = "project_model_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated alias of project_model with request.action=export; same request and result. Use project_model; this alias will be removed in a later major release.")]
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
            return ToolResult.Of(result);
        }
        catch (Exception error) when (ToolResult.IsRefusal(error)) { return ToolResult.Failure(error, "ProjectModelFailed"); }
    }
}
