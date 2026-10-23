using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class DriveProfileTools(HostOptions options)
{
    [McpServerTool(Name = "drive_profile_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review committed drive-profile-review-request.v1/v2 JSON against an exact saved hardware selection. V1 retains exact parameter/BICO/access semantics; v2 adds typed supplied telegram numbers/types, directional byte sizes and independently unit-tagged addresses. Only explicit bit/byte values normalize; unknown units stay unknown. No native identity, permission, compatibility, transfer, activation or motion. 4 MiB; per phase 4096 parameters and 128 telegrams with up to 16 addresses each.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "drive_profile_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen retained drive evidence, revalidating original JSON and exact saved review after restart. section: summary/observations/changes; v2 also telegrams/telegramChanges. V1 refuses v2-only sections. 1..200 rows within 128 KiB. artifact_read downloads complete originals/reports; oversized rows are explicitly omitted. Supplied declarations never certify native access, ownership or target compatibility.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<DriveProfileWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "DriveProfileReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
