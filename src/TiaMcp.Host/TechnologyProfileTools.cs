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
    [Description("Review committed technology-profile-review-request.v1/v2/v3/v4 JSON against an exact saved technology object: supplied profile identity, parameters, relationships and explicit unknowns. No native compatibility, write authority or motion. 4 MiB; 4096 parameters, 1024 relationships per phase; at most 500 changed proposals. v2 adds source files, v3 consumers, v4 encoder observations. Channel/member existence, wiring and eligibility unverified.")]
    public CallToolResult Review(string reviewId, string uploadRef) => Run(s => s.Review(reviewId, uploadRef));

    [McpServerTool(Name = "technology_profile_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen retained technology evidence with original/source/dependency revalidation after restart. section: summary/observations/relationships/changes/context/sources/consumers/consumerChanges/impactPaths/impactTraversal; v4 also supports encoderConnections/encoderChanges/encoderCoverage. 1..200 rows within 128 KiB. Older records refuse encoder sections. Oversized rows are explicitly omitted; artifact_read retrieves complete originals and report. Source files and referenced drive inputs are reverified after upload release. No native write/target or source-authenticity certification.")]
    public CallToolResult Page(string recordRef, string section = "summary", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));
    private CallToolResult Run(Func<TechnologyProfileWorkflowStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "TechnologyProfileReviewFailed", null, false);
}
