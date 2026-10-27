using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class CompositeEngineeringTools(IBridgeClient bridge, HostOptions options)
{
    private static readonly Lazy<bool> ToConnect = new(() => FeatureCatalog.LoadBundled().HasChangeKind("to_connect"));

    [McpServerTool(Name = "composite_engineering_capture", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Produce one composite phase (reviewId, automationMlRef, connections, drives, technology) from native read-only captures of the open project bound to an exact saved reviewId: cax_export AML, connection_inspect, drive_observe and technology_read for each saved technology object, each retained through its existing review workflow as a single observed state. Every capture reports native, unavailable (operation or product absent) or failed, with per-record refusals; at most 32 component records, truncation explicit. A response for another project refuses the whole capture. Use the phase as before/after of composite_engineering_review. No mutation; native qualification, freshness and complete coverage remain unverified.")]
    public async Task<CallToolResult> Capture(string reviewId, CancellationToken cancellationToken)
    {
        object result; bool failed = false;
        try { result = await CompositeEngineeringCapture.Capture(bridge, options.ArtifactsDirectory, reviewId, options.LibraryLimits, cancellationToken); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        { failed = true; result = new { error = new { code = e is BridgeException b ? b.Code : "CompositeCaptureFailed", message = e.Message } }; }
        return Result(result, failed);
    }

    [McpServerTool(Name = "composite_engineering_rebind_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan, never apply, technology-object hardware rebinds for a revalidated composite handle: each before-phase declared TO hardware connection that is missing, disconnected, unknown or unresolved after, whose target has one declared replacement (model correspondence, or AML correspondence through declared hardware mappings in both phases), becomes a to_connect change payload (technologyPath, role, sensorIndex, hardwarePath and carried-over channel selector). Payloads appear only when this build's catalog has the to_connect change kind; otherwise entries say changeKindUnavailable. At most 200 entries. Each change still needs change_prepare/apply with its own preview, checkpoint and compile.")]
    public CallToolResult RebindPlan(string recordRef) => Run(s => s.RebindPlan(recordRef, ToConnect.Value));

    [McpServerTool(Name = "composite_engineering_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain a combined engineering review from a committed composite-engineering-request.v1/v2/v3 upload (1 byte..4 MiB). Independently reopen exact AML, saved review, connection, drive and TO references; preserve explicit associations, unresolved/conflicting mappings, source warnings and bounded declared impact. V2 adds phase-local consumer context; v3 adds exact telegram/address/encoder selectors and original-reference constraints. Older evaluations stay unchanged. No native rebind, mutation authority, complete consumer coverage or source authenticity. Each invocation creates a new immutable record; discover lost replies with composite_engineering_list.")]
    public CallToolResult Review(string uploadRef) => Run(s => s.Review(uploadRef));

    [McpServerTool(Name = "composite_engineering_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an exact retained composite engineering handle after restart. Verify intent, receipt, original request, report and all referenced inputs; rebuild and compare the report. Missing or changed dependencies refuse. Hashes establish retained consistency only; native qualification and whole impact completeness remain false.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "composite_engineering_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page a revalidated combined report: selections/mappings/correspondences/impacts/coverageGaps/beforeComponents/afterComponents/amlChanges/beforeAmlImpacts/afterAmlImpacts. V2 also supports consumerContexts/consumerCoverage and beforeConsumerRoots/afterConsumerRoots, beforeConsumerAssociations/afterConsumerAssociations, beforeConsumerSavedLinks/afterConsumerSavedLinks, beforeConsumerCandidates/afterConsumerCandidates. 1..200 rows, 128 KiB response. Oversized rows are explicit omissions; exact complete content remains in report.json. No native authority or verified effects.")]
    public CallToolResult Page(string recordRef, string section = "mappings", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));

    [McpServerTool(Name = "composite_engineering_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download request.json, report.json or receipt.json from an exact composite handle, 1..65536 bytes per call, with complete length and SHA-256. Revalidate the complete input chain on every call. No arbitrary filesystem path; hashes are not producer authentication or native qualification.")]
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    [McpServerTool(Name = "composite_engineering_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover 1..20 composite records, including incomplete or verification-failed publications. Revalidate each completed candidate and its dependencies. Limits: 128 records, 512 MiB, 1024 entries including incomplete files. Sorted record-ID offsets are discovery-only; relist after additions. Never resumes or retries an interrupted publication.")]
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<CompositeEngineeringWorkflowStore, object> action)
    {
        object result; bool failed = false;
        try { result = action(new(options.ArtifactsDirectory, options.LibraryLimits)); }
        catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            failed = true; result = new { error = new { code = e is BridgeException b ? b.Code : "CompositeWorkflowFailed",
                message = e is BridgeException ? e.Message : "Composite input, retained integrity or publication failed. No native action occurred.", nativeAccessAttempted = false } };
        }
        return Result(result, failed);
    }

    private static CallToolResult Result(object result, bool failed)
    {
        string json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
