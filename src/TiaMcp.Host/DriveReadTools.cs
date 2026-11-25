using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Host;

// Read-only drive tools that call the worker through the shared bridge; the offline drive reviews are in DriveProfileTools.
public sealed partial class TiaTools
{
    [McpServerTool(Name = "drive_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("One read-only drive inventory of the open project: every SINAMICS drive object with its telegrams (number, I/O byte ranges), the PLC tags overlapping each telegram and the technology objects whose hardware connections reach the drive. Joins use native paths and addresses, never names. noDrivesConfirmed is true only for a complete Startdrive read with no drive. blockers lists every gap or ambiguity; dependent writes must not proceed while writeReady is false. Changes nothing.")]
    public Task<CallToolResult> DriveInventoryRead(CancellationToken cancellationToken, string? projectName = null) =>
        ToolResult.GuardAsync(async () => await DriveInventory.Capture(bridge, projectName, cancellationToken), "DriveInventoryFailed");

    [McpServerTool(Name = "servo_call_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan, never apply, one caller FB calling an existing servo wrapper FB once per reviewed drive, wired to its drive_inventory status/control tags. request: plcPath, callerName, wrapperName, parameters {status, control, reset, ss1, fault} (checked natively), resetExpression, ss1Expression, optional faultNaming, drives [{hardwarePath, instanceName}]. blockers lists every gap; when writeReady, change holds a source_upsert payload to prepare and apply with compile=true.")]
    public Task<CallToolResult> ServoCallPlanRead(JsonElement request, CancellationToken cancellationToken) => ToolResult.GuardAsync(async () =>
        await ServoCallPlan.Plan(bridge, JsonNode.Parse(request.GetRawText())?.AsObject() ?? throw new BridgeException("InvalidRequest", "request must be an object."),
            Path.Combine(options.ArtifactsDirectory, "exports"), cancellationToken), "ServoCallPlanFailed");
}
