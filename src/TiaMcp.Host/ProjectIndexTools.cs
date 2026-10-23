using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "project_index", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Capture/query/verify/release a bounded immutable offline metadata index. Capture accepts projectName, optional plcPath, maxVisited (100..50000) and lifetimeSeconds (30..900). Query accepts indexId, text, kind, plcPath, scopePath, maxItems (1..200), cursor and verifyCurrent. Repeat filters with cursor. Cached pages do not detect external edits: verify explicitly recaptures metadata. Revision covers indexed metadata only, never PLC logic or the whole project. No online access.")]
    public Task<CallToolResult> ProjectIndex(JsonElement request, CancellationToken cancellationToken)
        => Call("project_index", ObjectArguments(request), cancellationToken);
}
