using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class SoftwareUnitTools(HostOptions options)
{
    [McpServerTool(Name = "software_unit_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review a committed software-unit-review-request.v1 JSON upload (<=4 MiB) against an exact saved reviewId/PLC. Retain original bytes and a bounded before/after report for proposed direct relation, publication and namespace-preset edits. Unknown targets remain unresolved; no name-based rebinding, native mutations, compile or visibility certification. 128 units, 2048 objects/observed relations, 4096 supplied dependencies, 128 sequential proposed edits. Native create/move/template execution is not implemented.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "software_unit_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable software-unit review by recordId after restart; revalidate retained original, saved review and derived report. section: summary/units/objects/relations/dependencies/cycles/collisions/changes; 1..200 rows within 128 KiB. Exact input/report download references work with artifact_read. Supplied coverage never proves whole-project completeness or native compatibility.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<SoftwareUnitWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "UnitReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
