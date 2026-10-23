using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class DbSnapshotTools(HostOptions options)
{
    [McpServerTool(Name = "db_snapshot_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect supplied InterfaceSnapshot XML from a committed upload, optionally compare a second upload. Exact saved reviewId and captured DB objectPath required. Retains original bytes/encodings; compares exact path/type/text. No native CPU/program identity, freshness, XSD compatibility, value conversion or online access. 4 MiB and 20000 values per input.")]
    public CallToolResult Review(string reviewId, string objectPath, string uploadRef, string? comparisonUploadRef = null) => Run(s => s.Review(reviewId, objectPath, uploadRef, comparisonUploadRef));

    [McpServerTool(Name = "db_snapshot_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable supplied snapshot review, revalidating original XML, saved review and report after restart. section: summary/beforeValues/afterValues/changes; 1..200 rows within 128 KiB. Oversized rows explicitly omitted; exact original/report downloads use artifact_read. No current PLC values or mutation authority.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<DbSnapshotWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is System.Xml.XmlException or BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "DbSnapshotReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
