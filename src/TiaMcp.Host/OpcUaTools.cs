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

    // Served by review_page (ReviewRecordTools).
    public CallToolResult ReportPage(string recordRef, string section, int offset = 0, int count = 100) =>
        Run(s => s.ReportPage(recordRef, section, offset, count));

    // Served by review_reopen (ReviewRecordTools).
    public CallToolResult Reopen(string recordRef) => Run(s => s.Reopen(recordRef));

    // Served by review_list (ReviewRecordTools).
    public CallToolResult List(int offset = 0, int count = 20) => Run(s => s.List(offset, count));

    // Served by review_artifact_read (ReviewRecordTools).
    public CallToolResult ReadArtifact(string artifactRef, long offset = 0, int count = 65536) =>
        Run(s => s.ReadArtifact(artifactRef, offset, count));

    private CallToolResult Run(Func<OpcUaWorkflowStore, object> action) =>
        ToolResult.Guard(() => action(new OpcUaWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits)), "OpcUaWorkflowFailed",
            "Offline OPC UA input, retained integrity or publication failed. No native action occurred.");
}
