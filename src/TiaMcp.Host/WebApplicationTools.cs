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
    [Description("Review public web application configuration and one application's before/after ZIP archives against an exact saved owner. Direct web-application-review-request.v1 JSON references committed ZIP uploads by SHA-256. Non-executing intake; compares settings, declarations and file hashes. Unknown content never implies deletion. No browser, network, script execution, Siemens compatibility, block generation, native configuration or deployment. Public data only; not secret storage.")]
    public CallToolResult Review(JsonElement request) => Run(s => s.Review(System.Text.Encoding.UTF8.GetBytes(request.GetRawText())));

    [McpServerTool(Name = "web_application_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen and revalidate retained web application input, exact saved owner and original ZIP bytes. section: summary/observations/configurationChanges/applicationChanges/fileChanges; 1..200 rows within 128 KiB. ZIPs are bounded and re-inspected without scripts/network. artifact_read downloads full untrusted originals/reports. Supplied observations do not establish native identity, generated DBs or deployed state.")]
    public CallToolResult Page(string recordRef, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));
    private CallToolResult Run(Func<WebApplicationWorkflowStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "WebApplicationReviewFailed", "Web application review failed.", false);
}
