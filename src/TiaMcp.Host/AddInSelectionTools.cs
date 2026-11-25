using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class AddInSelectionTools(HostOptions options)
{
    [McpServerTool(Name = "addin_selection_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review a committed addin-selection.v1 JSON upload (<=1 MiB) against an exact saved reviewId. Resolves 1..64 projectTree/devicesAndNetworks references by original snapshot, session, project host/path, node and object path. Retains original bytes and declared V21 producer hashes. No name repair, native target admission, producer authentication, process launch or live freshness. This is supplied handoff evidence; the native Add-In companion is not installed or qualified.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "addin_selection_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable Add-In selection record after restart; revalidate retained input, exact saved review and report. section: summary/selections; 1..200 rows within 128 KiB. Download original and report through artifact_read. Expired saved snapshots remain historical evidence; no native selection, operation or UI action is resumed.")]
    public CallToolResult Page(string recordRef, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));
    private CallToolResult Run(Func<AddInSelectionStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "AddInSelectionFailed", null, false);
}
