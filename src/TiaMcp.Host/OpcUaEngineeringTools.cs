using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.OpcUa;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "opcua_engineering_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the offline OPC UA server configuration of one PLC CPU (S7-1500 target profile), selected by hardwarePath or plcPath: OpcUa-named CPU and sub-item attributes, user-defined and SIMATIC server interfaces, reference namespaces, namespace access restrictions and role mappings with namespace permissions. maxItems 1..2000 bounds attributes and rows; truncation and native read failures stay explicit. No online access, export or project change.")]
    public Task<CallToolResult> OpcUaEngineeringInspect(CancellationToken cancellationToken, string? hardwarePath = null, string? plcPath = null, string? projectName = null, int maxItems = 500)
        => Call("opcua_engineering_inspect", new { projectName, hardwarePath, plcPath, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "opcua_engineering_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export native OPC UA XML of one PLC CPU selected by hardwarePath or plcPath. category: server (whole address space through the OPC UA export hardware utility), serverInterface, simaticInterface or referenceNamespace; optional exact name; maxItems 1..32. Writes new files with SHA-256 under the Host artifact directory and registers each as an opcua_inspect model record for opcua_compare and opcua_dependencies. A file that is not a valid bounded NodeSet keeps its artifact and reports registrationError. No project change, import or online access.")]
    public async Task<CallToolResult> OpcUaEngineeringExport(string category, CancellationToken cancellationToken, string? name = null,
        string? hardwarePath = null, string? plcPath = null, string? projectName = null, int maxItems = 32)
    {
        try
        {
            if (maxItems is < 1 or > 32) throw new BridgeException("InvalidRequest", "maxItems must be between 1 and 32.");
            var result = JsonNode.Parse((await bridge.CallAsync("opcua_engineering_export", new { category, name, projectName, hardwarePath, plcPath, maxItems,
                artifactDirectory = Path.Combine(ExportDirectory, "opcua") }, cancellationToken)).GetRawText())!.AsObject();
            var store = new OpcUaWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits);
            foreach (var file in result["files"]!.AsArray())
            {
                // Same artifact boundary and hash check as other worker exports; one bad file does not hide the others.
                try { file!["record"] = store.InspectExported(ProjectReviewStore.ReadArtifact(options.ArtifactsDirectory, file["artifact"]!.AsObject(), OpcUaModelReview.MaxBytes)); }
                catch (BridgeException error) { file!["registrationError"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message }; }
            }
            return Result(result.ToJsonString(), false);
        }
        catch (Exception error) when (ReviewError(error))
        {
            return Result(JsonSerializer.Serialize(new { error = new { code = (error as BridgeException)?.Code ?? "OpcUaEngineeringExportFailed",
                message = error.Message, details = (error as BridgeOperationException)?.Details } }, BridgeProtocol.Json), true);
        }
    }
}
