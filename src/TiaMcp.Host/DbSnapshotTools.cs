using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class DbSnapshotTools(HostOptions options, IBridgeClient bridge)
{
    [McpServerTool(Name = "db_snapshot_capture", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export the snapshot stored on one exact DB of the open project (InterfaceSnapshot.Export through the worker; the project is unchanged) and review it like db_snapshot_review. The record keeps the export's project/PLC/DB identity as a captured native source, which must match the saved review's DB and project path; its recordId is what change kind db_snapshot_load_actual requires. Stored snapshot values only: no online access, no current CPU values. A DB without a stored snapshot refuses.")]
    public async Task<CallToolResult> Capture(string reviewId, string objectPath, CancellationToken cancellationToken, string? projectName = null)
    {
        JsonObject? export = null; BridgeException? failure = null;
        try { export = JsonNode.Parse((await bridge.CallAsync("db_snapshot_export", new { projectName, blockPath = objectPath,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken)).GetRawText()) as JsonObject; }
        catch (BridgeException error) { failure = error; }
        return Run(s => s.Capture(reviewId, objectPath, export ?? throw failure ?? new BridgeException("InvalidDbSnapshot", "The worker returned no export.")), true);
    }

    [McpServerTool(Name = "db_snapshot_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect supplied InterfaceSnapshot XML from a committed upload, optionally compare a second upload. Exact saved reviewId and captured DB objectPath required. Retains original bytes/encodings; compares exact path/type/text. No native CPU/program identity, freshness, XSD compatibility, value conversion or online access. 4 MiB and 20000 values per input.")]
    public CallToolResult Review(string reviewId, string objectPath, string uploadRef, string? comparisonUploadRef = null) => Run(s => s.Review(reviewId, objectPath, uploadRef, comparisonUploadRef));

    [McpServerTool(Name = "db_snapshot_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable supplied snapshot review, revalidating original XML, saved review and report after restart. section: summary/beforeValues/afterValues/changes; 1..200 rows within 128 KiB. Oversized rows explicitly omitted; exact original/report downloads use artifact_read. No current PLC values or mutation authority.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<DbSnapshotWorkflowStore, JsonObject> action, bool native = false)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is System.Xml.XmlException or BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "DbSnapshotReviewFailed", message = e.Message, nativeAccessAttempted = native } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
