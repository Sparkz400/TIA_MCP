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

    [McpServerTool(Name = "automationml_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an exact retained AutomationML document/comparison after restart. Verify receipt, artifacts and original comparison inputs before returning bounded counts and download names. No native qualification.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "automationml_report_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read 1..200 rows within 128 KiB from an exact retained report array: /objects, /attributes, /references, /links, /opaqueContent, /diagnostics, /changes, /beforeImpacts or /afterImpacts. Oversized rows are explicit omissions downloadable in report.json. Offset and section bind the immutable handle.")]
    public CallToolResult ReportPage(string recordRef, string section, int offset = 0, int count = 50) => Run(s => s.ReportPage(recordRef, section, offset, count));

    [McpServerTool(Name = "automationml_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download original.aml, report.json or receipt.json from an exact retained AutomationML handle, at most 64 KiB per call with full SHA-256 and length. Revalidate exact inputs; reports are limited to 64 MiB. No arbitrary filesystem paths.")]
    public CallToolResult ArtifactRead(string recordRef, string name, long offset = 0, int count = 65536) => Run(s => s.ReadArtifact(recordRef, name, offset, count));

    [McpServerTool(Name = "automationml_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover 1..50 retained AutomationML records, including incomplete and integrity-failed publications. Storage limits 128 records and 2 GiB include failed publications. Sorted record-ID offsets are discovery-only; relist after additions.")]
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    private CallToolResult Run(Func<AutomationMlWorkflowStore, object> action)
    {
        object result; bool failed = false;
        try { result = action(new AutomationMlWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            failed = true; result = new { error = new { code = error is BridgeException bridge ? bridge.Code : "AutomationMlWorkflowFailed",
                message = error is BridgeException ? error.Message : "AutomationML input, retained integrity or publication failed. No native action occurred." } };
        }
        var json = JsonSerializer.Serialize(result, BridgeProtocol.Json);
        return new() { IsError = failed, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
