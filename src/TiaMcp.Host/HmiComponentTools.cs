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
    [Description("Generate or reopen retained offline Unified text-list sets from a revalidated v7 review; supply exactly one committed request uploadRef or retained recordId. v1 retains coordinated Bool/integer child authors with fixed identities. v2 evolves an exact previous set with explicit add/update/rename output rows (previousOutputId, listName, bindingAction preserve or rebind, removeOutputIds, retainOutputIds); every predecessor and current declared output must be accounted for, and baseline/current pairs must equal the predecessor bytes. v3 allows integer-value-preserving.v1 typeConversion among SInt to UDInt when every listed value fits both types; PLC values are never converted. Any conflict suppresses all candidates; renames, swaps and removals are previews only. Limits: 32 candidate files, 64 MiB, 4 MiB input, lineage depth 16. Download with artifact_read. No native, TIA or runtime access, ownership or preservation claim. Details: docs/reference/unified-text-list-set-evolution.md and docs/reference/tool-notes.md#hmi_text_list_generate_set.")]
    public CallToolResult GenerateTextListSet(string? uploadRef = null, string? recordId = null) => Run(store => store.GenerateTextListSet(uploadRef, recordId));

    [McpServerTool(Name = "hmi_text_list_author", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Author downloadable plain Unified text-list configuration and TextLibrary YAML candidates from a committed unified-text-list-author-request v1/v2/v3 uploadRef, or reopen and revalidate a retained recordId; supply exactly one. v2 derives one Bool list from a revalidated v7 hmiResource output and its binding (False/True translations); v3 supports the integer types SInt to LInt with 1..256 explicit disjoint SingleValue/Range states. Languages, translations, entries and references are explicit; overlaps, duplicate names, missing or unused translations, renames and implicit type conversion refuse. requireFullTypeCoverage blocks candidates with value gaps. Regeneration needs the previous author record and matching baseline/current candidate pairs; conflicts suppress both candidates; retainOutputIds keeps selected peer bytes. Limits: 4 MiB request, 128 lists, 4096 entries, 32 languages. Candidates survive upload release and restart; download them with artifact_read and review them with hmi_component_review. No native import, existing-file rewriting, project language activation or qualification claim. Details: docs/reference/unified-text-list-authoring.md and docs/reference/tool-notes.md#hmi_text_list_author.")]
    public CallToolResult AuthorTextLists(string? uploadRef = null, string? recordId = null) => Run(store => store.AuthorTextLists(uploadRef, recordId));

    [McpServerTool(Name = "hmi_component_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Review a committed hmi-component-review-request upload (versions v1 to v7, up to 4 MiB) against exact saved PLC component and HMI destination reviews. Compares explicit component/interface mappings, proposed HMI outputs and independent baseline/current/proposed files; reports type changes, collisions, ownership and drift. Later versions add consumer paths (v2, requires impactLimits), post-operation observations (v3), classic screen BackColor expectations (v4), FB library-version associations (v5), dependency mappings (v6) and consumer selections (v7). Identifier agreement and declared completeness never prove native identity, content, dependencies, effects or qualification; there are no name joins, scripts, native writes or complete-coverage claims. Originals are retained across upload release and restart. Per-version fields and bounds: docs/reference/hmi-component-review.md and docs/reference/tool-notes.md#hmi_component_review.")]
    public CallToolResult Review(string uploadRef) => Run(store => store.Review(uploadRef));

    [McpServerTool(Name = "hmi_component_review_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable component/HMI review by recordId. Revalidate request, all independent files and exact saved reviews. section: summary/components/outputs/bindings/files; v2 adds consumerPaths/consumerCoverage; v3 adds effectChecks; v4 adds derivedEffects/expectationCoverage; v5 adds libraryAssociations/libraryCoverage; v6 adds dependencyAssociations/dependencyCoverage/dependencyIdentities; v7 adds dependencyImpacts/dependencyImpactCoverage. Older records retain original results and reject newer sections. Nonnegative offset and 1..200 rows bounded to 128 KiB. Download original files/full report with artifact_read. No conflict-free report grants native write, preservation or regeneration acceptance.")]
    public CallToolResult Page(string recordId, string section = "summary", int offset = 0, int count = 50) => Run(store => store.Page(recordId, section, offset, count));
    private CallToolResult Run(Func<HmiComponentWorkflowStore, JsonObject> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory)), "HmiComponentReviewFailed", null, false);
}
