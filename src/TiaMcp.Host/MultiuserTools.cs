using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class MultiuserTools(HostOptions options)
{
    [McpServerTool(Name = "multiuser_observation_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare a committed multiuser-observations.v1 upload (<=4 MiB): supplied before/after server, groups, projects, sessions, locks and markings. Exact declared host/port must match. Retains original bytes, coverage failures and deleted-object marking omissions. Differences are declarations, not native additions/deletions or conflict-free proof. No server access, credential use, session open, lock acquisition, commit, native admission or outcome reconciliation.")]
    public CallToolResult Review(string uploadRef) => Run(s => s.Review(uploadRef));

    [McpServerTool(Name = "multiuser_observation_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen and revalidate an immutable supplied Project Server observation comparison after Host restart. section: summary/observations/changes/issues; 1..200 rows within 128 KiB. Original and report bytes are available through artifact_read. Unknown/incomplete queries and deleted-object omissions remain visible; supplied data never authorizes engineering work or resolves a native journal.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<MultiuserReviewStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "MultiuserReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
