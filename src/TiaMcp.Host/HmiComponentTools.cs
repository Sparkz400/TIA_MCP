using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class HmiComponentTools(HostOptions options)
{
    [McpServerTool(Name = "hmi_text_list_generate_set", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Generate or reopen retained offline Unified text-list sets from a revalidated v7 review; supply exactly one uploadRef or recordRef. v2 evolves a previous set with explicit add/update/rename rows covering every output; v3 allows integer-value-preserving type conversion (PLC values never converted). Any conflict suppresses all candidates; renames and removals are previews only. Download with artifact_read. No native, TIA or runtime access or preservation claim.")]
    public CallToolResult GenerateTextListSet(string? uploadRef = null, string? recordRef = null) => Run(store => store.GenerateTextListSet(uploadRef, recordRef));

    [McpServerTool(Name = "hmi_text_list_author", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Author Unified text-list and TextLibrary YAML candidates from a committed unified-text-list-author-request v1/v2/v3 uploadRef, or reopen a retained recordRef; supply exactly one. v2 adds Bool lists from v7 hmiResource outputs, v3 SInt to LInt. Overlaps, duplicates, missing translations, renames and implicit type conversion refuse. Fetch with artifact_read; review with hmi_component_review. No native import, file rewriting, language activation or qualification claim.")]
    public CallToolResult AuthorTextLists(string? uploadRef = null, string? recordRef = null) => Run(store => store.AuthorTextLists(uploadRef, recordRef));

    [McpServerTool(Name = "hmi_component_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review a committed hmi-component-review-request upload (v1 to v7, up to 4 MiB) against exact saved PLC component and HMI destination reviews. Compares explicit mappings, proposed HMI outputs and baseline/current/proposed files; reports type changes, collisions, ownership and drift. v2 requires impactLimits. Never proves native identity, content, effects or qualification; no name joins, scripts, native writes or complete-coverage claims.")]
    public CallToolResult Review(string uploadRef) => Run(store => store.Review(uploadRef));

    [McpServerTool(Name = "hmi_component_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable component/HMI review by recordRef. section: summary/components/outputs/bindings/files, plus by version consumerPaths/consumerCoverage, effectChecks, derivedEffects/expectationCoverage, libraryAssociations/libraryCoverage, dependencyAssociations/dependencyCoverage/dependencyIdentities, dependencyImpacts/dependencyImpactCoverage; older records refuse newer. Offset, 1..200 rows. Grants no native write, preservation or regeneration acceptance.")]
    public CallToolResult Page(string recordRef, string section = "summary", int offset = 0, int count = 50) => Run(store => store.Page(recordRef, section, offset, count));
    private CallToolResult Run(Func<HmiComponentWorkflowStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "HmiComponentReviewFailed", null, false);
}
