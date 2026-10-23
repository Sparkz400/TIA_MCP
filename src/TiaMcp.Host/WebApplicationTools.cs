using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class WebApplicationTools(HostOptions options)
{
    [McpServerTool(Name = "web_application_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review supplied public web application configuration and one selected application's before/after ZIP archives against an exact saved owner. Direct web-application-review-request.v1 JSON references committed ZIP uploads by exact SHA-256. Reuses bounded non-executing intake, selects an exact root/default file, compares public settings/application declarations and file hashes, and retains originals. Unknown content never implies deletion. No browser, network, script execution, Siemens compatibility, block generation, native configuration or deployment. Supply public data only; this is not secret storage or content sanitization.")]
    public CallToolResult Review(JsonElement request) => Run(s => s.Review(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())));

    [McpServerTool(Name = "web_application_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen and revalidate retained web application input, exact saved owner and original ZIP bytes. section: summary/observations/configurationChanges/applicationChanges/fileChanges; 1..200 rows within 128 KiB. ZIPs are bounded and re-inspected without scripts/network. artifact_read downloads full untrusted originals/reports. Supplied observations do not establish native identity, generated DBs or deployed state.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<WebApplicationWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "WebApplicationReviewFailed", message = e is BridgeException ? e.Message : "Web application review failed.", nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
