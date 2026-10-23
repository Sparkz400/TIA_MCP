using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class TechnologyProfileTools(HostOptions options)
{
    [McpServerTool(Name = "technology_profile_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review committed technology-profile-review-request.v1/v2/v3/v4 JSON against an exact saved technology object: supplied profile identity, parameters, relationships and explicit unknowns. No native compatibility, write authority or motion. 4 MiB; 4096 parameters and 1024 relationships per phase; at most 500 changed parameter/relationship proposals (including encoder changes in v4). v2 retains independently uploaded source files: up to 16 files, 16 MiB each and 64 MiB total. v3 adds supplied consumers and bounded same-PLC saved context. v4 retains up to 256 encoder observations per phase: module, splitModules, channel, addresses, dbMember, startdriveTelegram or unknown routes, exact saved selectors, explicit bit/byte/unknown units and sensor-index contradictions. Telegram targets bind original retained drive record/phase/ID in the same saved review; at most eight drive records and 64 MiB of drive records/inputs. Channel/member existence, wiring and eligibility remain unverified.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "technology_profile_report", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen retained technology evidence with original/source/dependency revalidation after restart. section: summary/observations/relationships/changes/context/sources/consumers/consumerChanges/impactPaths/impactTraversal; v4 also supports encoderConnections/encoderChanges/encoderCoverage. 1..200 rows within 128 KiB. Older records refuse encoder sections. Oversized rows are explicitly omitted; artifact_read retrieves complete originals and report. Source files and referenced drive inputs are reverified after upload release. No native write/target or source-authenticity certification.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<TechnologyProfileWorkflowStore, JsonObject> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { failed = true; result = new { error = new { code = (e as BridgeException)?.Code ?? "TechnologyProfileReviewFailed", message = e.Message, nativeAccessAttempted = false } }; }
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
