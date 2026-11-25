using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Graph;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class GraphTools(IBridgeClient bridge, HostOptions options)
{
    [McpServerTool(Name = "graph_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export one exact offline GRAPH block (blockPath from project_inventory, or an unambiguous blockName) as unchanged native SimaticML into the Host artifact directory, then retain those bytes with their SHA-256 as a graph_* document record for review_page (kind graph), graph_compare and the graph_import change kind. Non-GRAPH and protected blocks are refused. No project change; native and behavioral qualification stay false.")]
    public async Task<CallToolResult> Export(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, string? scopePath = null)
    {
        JsonElement read;
        try { read = await bridge.CallAsync("block_read", new { projectName, plcPath, blockPath, blockName, scopePath, includeXml = false, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken); }
        catch (BridgeException error) { return Run(_ => throw error); }
        return Run(s =>
        {
            JsonElement block = read.GetProperty("block"), artifact = read.GetProperty("artifact");
            if (block.GetProperty("programmingLanguage").GetString() != "GRAPH") throw new BridgeException("UnsupportedLanguage", "graph_export requires a block whose ProgrammingLanguage is GRAPH.");
            return new { block, artifact, document = s.Retain(artifact.GetProperty("path").GetString()!, artifact.GetProperty("sha256").GetString()!), nativeQualified = false };
        });
    }

    [McpServerTool(Name = "graph_inspect", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect a committed GRAPH XML upload, 1 byte..8 MiB, using the offline v6 structure reader. Retain exact original bytes, a complete inspection report and self-contained sequence HTML with escaped source. Unsupported content and diagnostics stay explicit. No native worker, XML external resolution, condition/script execution, native import or runtime qualification.")]
    public CallToolResult Inspect(string xmlUploadRef) => Run(s => s.Inspect(xmlUploadRef));

    [McpServerTool(Name = "graph_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two exact retained GRAPH document handles. Retain structural/content differences and before/after diagnostics, preserving opaque content and unreliable matching. Neither matching text nor an empty difference report certifies behavioral equivalence, native validity or a safe import.")]
    public CallToolResult Compare(string beforeRef, string afterRef) => Run(s => s.Compare(beforeRef, afterRef));

    [McpServerTool(Name = "graph_edit", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Apply 1..32 typed edits in order to one exact retained GRAPH document with one chart: addStep, addTransition, removeStep/removeTransition (linear chain), renameStep/renameTransition, setStepComment, setSequenceTitle, setTransitionCondition; condition is 1..16 {operand,negated?} on exact Bool interface members. Invalid graphs refuse. Retains a new document plus a graph_compare record. No project change; apply with the graph_import change kind. Not natively qualified.")]
    public CallToolResult Edit(string sourceRef, JsonElement operations) => Run(s => s.Edit(sourceRef, operations));

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_page (ReviewRecordTools).
    public CallToolResult ReportPage(string recordRef, string section, int offset = 0, int count = 50) => Run(s => s.ReportPage(recordRef, section, offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<GraphWorkflowStore, object> action)
    {
        try { return ToolResult.Of(action(new GraphWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits))); }
        catch (GraphException error) { return ToolResult.Error(error.Code, error.Message); }
        catch (Exception error) when (ToolResult.IsRefusal(error))
        { return ToolResult.Failure(error, "GraphWorkflowFailed", "GRAPH input, retained integrity or publication failed. No native action occurred."); }
    }
}
