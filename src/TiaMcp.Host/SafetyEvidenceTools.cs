using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class SafetyEvidenceTools(HostOptions options)
{
    [McpServerTool(Name = "safety_evidence_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two committed safety_inspect JSON uploads for one exact PLC in a saved review. Retains original bytes and compares signatures, runtime groups, settings and other reported content. Reports capture gaps, zero signatures and unavailable block services. Exact decimal signature text is provided for clients with limited number precision. 4 MiB per input; 128 signatures per scope and 4096 groups/settings. Supplied observations do not establish live identity, required profile coverage, native qualification or functional-safety approval. No Siemens access or program changes.")]
    public CallToolResult Review(string reviewId, string plcPath, string uploadRef, string comparisonUploadRef,
        [Description("Optional array of {uploadRef, phase, purpose}; phase before/after/context. Up to 16 opaque supporting sources, 16 MiB each, 64 MiB total. Original filename and bytes are retained; supplied roles do not certify authenticity or native association.")] JsonElement? sources = null) => Run(s =>
        s.Review(reviewId, plcPath, uploadRef, comparisonUploadRef, sources is null || sources.Value.ValueKind == JsonValueKind.Null ? null :
            JsonNode.Parse(sources.Value.GetRawText()) as JsonArray ?? throw new BridgeException("InvalidSafetyEvidence", "Supporting sources must be an array.")));

    [McpServerTool(Name = "safety_evidence_report", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a retained safety comparison and revalidate originals, saved selection and derived report after restart or upload release. section: summary/observations/signatures/issues/changes; 1..200 rows within 128 KiB. Oversized rows are explicitly omitted; artifact_read retrieves complete originals/report. Equal observations or signatures do not approve a safety program or authorize mutations or test execution.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));

    private CallToolResult Run(Func<SafetyEvidenceWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "SafetyEvidenceReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
