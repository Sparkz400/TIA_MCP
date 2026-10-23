using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class ConnectionTools(HostOptions options)
{
    [McpServerTool(Name = "connection_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review a committed connection-review-request.v1 JSON upload (<=4 MiB) against an exact saved reviewId. Retain original bytes and before/after observations, explicit connection mappings, captured hardware ancestry and declared consumers. 512 connections and 2048 consumers per phase. No implicit name/address matching, native property/resource validation, connection edits or communication claims.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "connection_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable supplied connection review by recordId; revalidate original, saved review and derived report after restart. section: summary/observations/comparisons/consumers/matches; 1..200 rows within 128 KiB. Exact original/report downloads use artifact_read. Coverage is supplied evidence, not completeness or native compatibility certification.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<ConnectionWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "ConnectionReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
