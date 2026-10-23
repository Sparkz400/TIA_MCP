using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "hmi_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect bounded native HMI sections: devices, connections, tags, screens, items, alarms, scripts or resources. Select exact hmiPath/family and objectPath returned by discovery; repeated names are not identities. Classic screens include regular, template, popup, slide-in, global and overview roles with per-scope coverage; additional roles expose metadata without qualified item parsing. Per-section coverage distinguishes unavailable and unsupported data. Script inspection never executes project code. Request schema: contracts/hmi-inspection.v1.schema.json.")]
    public Task<CallToolResult> HmiInspect(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_inspect", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_references", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read bounded HMI relationships and native evidence for exact target identities. Dynamic references, ambiguous names and incomplete PLC mappings stay unresolved and cannot authorize rewiring. Does not execute scripts or connect online.")]
    public Task<CallToolResult> HmiReferences(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_references", HmiArguments(request), cancellationToken);

    [McpServerTool(Name = "hmi_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report optional Classic and Unified V21 installation/API evidence, bridge implementation, qualification gates and current target availability separately. Installation alone does not establish a license, access or native qualification.")]
    public Task<CallToolResult> HmiCapabilities(JsonElement request, CancellationToken cancellationToken)
        => Call("hmi_capabilities", ObjectArguments(request), cancellationToken);

    private System.Text.Json.Nodes.JsonObject HmiArguments(JsonElement request)
    {
        var result = System.Text.Json.Nodes.JsonNode.Parse(ObjectArguments(request).GetRawText())!.AsObject();
        result["artifactDirectory"] = Path.Combine(options.ArtifactsDirectory, "hmi");
        return result;
    }
}
