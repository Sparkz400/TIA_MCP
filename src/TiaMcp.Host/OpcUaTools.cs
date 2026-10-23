using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.OpcUa;

namespace TiaMcp.Host;

/// <summary>Offline artifact review tools, deliberately independent of any worker or bridge.</summary>
[McpServerToolType]
public sealed class OpcUaTools(HostOptions options)
{
    [McpServerTool(Name = "opcua_inspect", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Inspect one committed upload: NodeSet artifact (1 byte..16 MiB, up to 20000 nodes). Retain exact original bytes and a pinned-schema offline report under immutable Host handles. Returns bounded summary and pageable sections. No filesystem paths, remote resolution, TIA startup or native qualification.")]
    public CallToolResult Inspect(string nodeSetUploadRef) => Run(s => s.Inspect(nodeSetUploadRef));

    [McpServerTool(Name = "opcua_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two retained model handles. Optional committed consumer-manifest upload uses opcua-consumers.v1 and must bind the exact before sourceSha256. Retains immutable comparison and exact supplied manifest. Known direct consumer impacts only; unknownConsumers stays true and compatibilityCertified stays false.")]
    public CallToolResult Compare(string beforeRef, string afterRef, string? consumerManifestUploadRef = null) =>
        Run(s => s.Compare(beforeRef, afterRef, consumerManifestUploadRef));

    [McpServerTool(Name = "opcua_dependencies", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Analyze 1..32 explicit retained model handles, at most 64 MiB and 100000 nodes per set. Retains a model-set digest and bounded pageable dependency report. Never discovers or downloads dependencies. Duplicate definitions remain ambiguous, versions and unsupported embedded dependency kinds remain unevaluated; dependencyClosureComplete stays false.")]
    public CallToolResult Dependencies(string[] modelRefs) => Run(s => s.Dependencies(modelRefs));

    [McpServerTool(Name = "opcua_dependency_compare", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Compare two retained explicit dependency-set handles with conservative direct/transitive consumer impacts. Optional committed opcua-consumers.v2 JSON must bind the exact before modelSetSha256. Retains immutable report and manifest. No full compatibility, version evaluation, target probing or native engineering admission.")]
    public CallToolResult CompareDependencies(string beforeSetRef, string afterSetRef, string? consumerManifestUploadRef = null) =>
        Run(s => s.CompareDependencies(beforeSetRef, afterSetRef, consumerManifestUploadRef));

    [McpServerTool(Name = "opcua_report_page", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Page an exact section from an OPC UA record summary, including nested before/after sections. 1..500 rows with 128 KiB row budget; cursor binds record, report hash and section. Oversized rows are explicitly marked omitted and remain downloadable in the complete report artifact. All retained input hashes are verified.")]
    public CallToolResult ReportPage(string recordRef, string section, int pageSize = 100, OpcUaReportCursor? cursor = null) =>
        Run(s => s.ReportPage(recordRef, section, pageSize, cursor));

    [McpServerTool(Name = "opcua_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Reopen an immutable OPC UA record after restart. Verify manifest, all artifacts and exact retained inputs before returning bounded summary, pageable sections and download handles. Any change fails closed. Uploaded evidence never establishes native freshness.")]
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    [McpServerTool(Name = "opcua_list", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover retained offline OPC UA records and incomplete or integrity-failed publications after restart. Count 1..100, bounded responses. Sorted publication-ID offsets are discovery-only; relist after concurrent additions. Does not retry any native operation.")]
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    [McpServerTool(Name = "opcua_artifact_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Download exact original NodeSet, supplied consumer JSON or complete report by Host-issued OPC UA artifact handle, at most 64 KiB per call. Returns SHA-256 and byte offset. Revalidates retained manifest and inputs. No filesystem path input and no overwrite operation.")]
    public CallToolResult ReadArtifact(string artifactRef, long offset = 0, int count = 65536) =>
        Run(s => s.ReadArtifact(artifactRef, offset, count));

    private CallToolResult Run(Func<OpcUaWorkflowStore, object> action)
    {
        try { return Format(action(new OpcUaWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)), false); }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            return Format(new { error = new { code = (error as BridgeException)?.Code ?? "OpcUaWorkflowFailed",
                message = error is BridgeException ? error.Message : "Offline OPC UA input, retained integrity or publication failed. No native action occurred.",
                details = (error as BridgeOperationException)?.Details } }, true);
        }
    }

    private static CallToolResult Format(object value, bool error)
    {
        string json = JsonSerializer.Serialize(value, BridgeProtocol.Json);
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}
