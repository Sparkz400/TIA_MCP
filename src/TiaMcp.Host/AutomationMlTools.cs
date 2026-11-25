using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;


namespace TiaMcp.Host;

[McpServerToolType]
public sealed class AutomationMlTools(HostOptions options)
{
    [McpServerTool(Name = "automationml_inspect", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect a committed CAEX 2.15 or 3.0 AML/XML upload, 1 byte..8 MiB. Validate against pinned embedded XSDs; retain exact bytes and a bounded report of declared hierarchy, attributes, class references, links, opaque content and diagnostics. No external fetches, inheritance expansion, native worker or importability certification.")]
    public CallToolResult Inspect(string xmlUploadRef) => Run(s => s.Inspect(xmlUploadRef));

    [McpServerTool(Name = "automationml_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two retained AutomationML documents. Retain exact before/after inputs, unique-ID and class-path differences, unmatched/ambiguous declarations and conservative declared hierarchy/link/class-consumer impacts. Every byte change requires whole-document review; complete native impact and importability remain uncertified.")]
    public CallToolResult Compare(string beforeRef, string afterRef) => Run(s => s.Compare(beforeRef, afterRef));

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_page (ReviewRecordTools).
    public CallToolResult ReportPage(string recordRef, string section, int offset = 0, int count = 50) => Run(s => s.ReportPage(recordRef, section, offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<AutomationMlWorkflowStore, object> action) =>
        ToolResult.Guard(() => action(new AutomationMlWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)), "AutomationMlWorkflowFailed", "AutomationML input, retained integrity or publication failed. No native action occurred.");
}
