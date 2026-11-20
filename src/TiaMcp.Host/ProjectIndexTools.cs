using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "project_index", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Deprecated: project_model capture, search, verify and release cover this; will be removed in a later major release. Bounded immutable offline metadata index. request.action capture (projectName, plcPath?, maxVisited 100..50000, lifetimeSeconds 30..900), query (indexId, text, kind, plcPath, scopePath, maxItems 1..200, cursor, verifyCurrent), verify, release. Cached pages do not detect external edits; verify recaptures metadata. Covers indexed metadata only, never PLC logic. No online access.")]
    public Task<CallToolResult> ProjectIndex(JsonElement request, CancellationToken cancellationToken)
        => Call("project_index", ObjectArguments(request), cancellationToken);
}
