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
    [Description("Produce one composite phase from native read-only captures of the open project bound to an exact saved reviewId: cax_export AML, connection_inspect, drive_observe and technology_read per saved technology object. Each capture reports native, unavailable or failed; at most 32 component records. A response for another project refuses the whole capture. Use as before/after of composite_engineering_review. No mutation; qualification and coverage unverified.")]
    public Task<CallToolResult> Capture(string reviewId, CancellationToken cancellationToken) => ToolResult.GuardAsync(async () =>
        await CompositeEngineeringCapture.Capture(bridge, options.ArtifactsDirectory, reviewId, options.LibraryLimits, cancellationToken), "CompositeCaptureFailed");

    [McpServerTool(Name = "composite_engineering_rebind_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan, never apply, technology-object hardware rebinds for a revalidated composite handle: each before-phase TO hardware connection missing, disconnected, unknown or unresolved after, with one declared replacement, becomes a to_connect change payload (only if this build has that change kind, else changeKindUnavailable). At most 200 entries. Each change still needs change_prepare/apply with its own preview, checkpoint and compile.")]
    public CallToolResult RebindPlan(string recordRef) => Run(s => s.RebindPlan(recordRef, ToConnect.Value));

    [McpServerTool(Name = "composite_engineering_review", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Retain a combined engineering review from a committed composite-engineering-request.v1/v2/v3 upload (up to 4 MiB). Independently reopens exact AML, saved review, connection, drive and TO references; preserves explicit associations, unresolved/conflicting mappings and declared impact. No native rebind, mutation authority, complete consumer coverage or source authenticity. Each call creates a new immutable record; discover lost replies with review_list.")]
    public CallToolResult Review(string uploadRef) => Run(s => s.Review(uploadRef));

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_page (ReviewRecordTools).
    public CallToolResult Page(string recordRef, string section = "mappings", int offset = 0, int count = 50) => Run(s => s.Page(recordRef, section, offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<CompositeEngineeringWorkflowStore, object> action) =>
        ToolResult.Guard(() => action(new(options.ArtifactsDirectory, options.LibraryLimits)), "CompositeWorkflowFailed",
            "Composite input, retained integrity or publication failed. No native action occurred.", false);
}
