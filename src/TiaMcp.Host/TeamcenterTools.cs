using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TeamcenterTools(HostOptions options)
{
    [McpServerTool(Name = "teamcenter_observation_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare a committed teamcenter-observations.v1 upload (<=4 MiB): supplied before/after connection, items, revisions, datasets, locks and local copies. Exact server/instance must match. Retains up to 16 separately committed local manifests (one MiB each); their SHA-256 and root must match. Manifest file hashes remain declarations, not verified file contents. Differences are declarations, not native additions/deletions, checkout ownership or cache verification. No server access, credential use, session open, lock acquisition, commit, native admission or outcome reconciliation.")]
    public CallToolResult Review(string uploadRef) => Run(s => s.Review(uploadRef));

    [McpServerTool(Name = "teamcenter_observation_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen and revalidate an immutable supplied Teamcenter observation comparison after Host restart. section: summary/observations/changes/issues/manifests; 1..200 rows within 128 KiB. Original and report bytes are available through artifact_read. Unknown/incomplete queries, version mismatches and local manifest gaps remain visible; supplied data never authorizes engineering work or resolves a native journal.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<TeamcenterReviewStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "TeamcenterReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
