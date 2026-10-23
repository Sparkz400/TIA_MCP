using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class AccessTools(HostOptions options)
{
    [McpServerTool(Name = "access_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review committed access-review-request.v1 public metadata against an exact saved project/hardware owner. Compare supplied users/groups/roles, exact scoped grants, caller-defined required/allowed rights and incomplete/cyclic inheritance. Separate project UMAC, CPU runtime, legacy web, SIWAREX and OPC UA domains. No effective authorization, minimum-rights certification, credentials, native changes or app/certificate operations. 2 MiB; 512 entities per phase.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "access_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a public access review and revalidate its original input and exact saved review. section: summary/observations/assessments/changes; 1..200 rows within 128 KiB. artifact_read retrieves complete retained public metadata. Missing/disabled/unknown grants never prove effective rights or safe removal. No native access.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<AccessWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "AccessReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
