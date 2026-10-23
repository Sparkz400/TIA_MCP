using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Graph;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class GraphTools(HostOptions options)
{
    [McpServerTool(Name = "graph_inspect", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect a committed GRAPH XML upload, 1 byte..8 MiB, using the offline v6 structure reader. Retain exact original bytes, a complete inspection report and self-contained sequence HTML with escaped source. Unsupported content and diagnostics stay explicit. No native worker, XML external resolution, condition/script execution, native import or runtime qualification.")]
    public CallToolResult Inspect(string xmlUploadRef) => Run(s => s.Inspect(xmlUploadRef));

    [McpServerTool(Name = "graph_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two exact retained GRAPH document handles. Retain structural/content differences and before/after diagnostics, preserving opaque content and unreliable matching. Neither matching text nor an empty difference report certifies behavioral equivalence, native validity or a safe import.")]
    public CallToolResult Compare(string beforeRef, string afterRef) => Run(s => s.Compare(beforeRef, afterRef));

    [McpServerTool(Name = "graph_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen a retained GRAPH document/comparison after restart. Verify receipt, artifacts and exact comparison inputs before returning bounded counts, diagnostics flags and download names. Native qualification stays false.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "graph_report_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read 1..200 rows within a 128 KiB response from an exact retained report array pointer: /diagnostics, /unsupported, /changes or /graphs/0/sequences/0/steps, transitions, branches, connections, actions, interlocks or supervisions. Oversized rows are explicit omissions available in report.json. Offset and section always bind the immutable record handle.")]
    public CallToolResult ReportPage(string recordRef, string section, int offset = 0, int count = 50) => Run(s => s.ReportPage(recordRef, section, offset, count));

    [McpServerTool(Name = "graph_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download an exact retained GRAPH artifact name: original.xml, report.json, viewer.html or receipt.json. Read at most 64 KiB per call with complete SHA-256 and length; verify retained input chains. Reports and HTML are bounded to 96 MiB. No arbitrary filesystem path input.")]
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    [McpServerTool(Name = "graph_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover 1..50 retained GRAPH publications, including incomplete and integrity-failed records. Storage limit 128 records and 2 GiB includes failed publications. Sorted record-ID offsets are discovery-only; relist after additions. Does not overwrite, resume or retry native work.")]
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<GraphWorkflowStore, object> action)
    {
        object result; bool failed = false;
        try { result = action(new GraphWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)); }
        catch (Exception error) when (error is BridgeException or GraphException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            failed = true; result = new { error = new { code = error is BridgeException bridge ? bridge.Code : error is GraphException graph ? graph.Code : "GraphWorkflowFailed",
                message = error is BridgeException or GraphException ? error.Message : "GRAPH input, retained integrity or publication failed. No native action occurred." } };
        }
        var json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
