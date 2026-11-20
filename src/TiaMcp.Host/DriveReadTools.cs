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
    [Description("One read-only drive inventory of the open project: every SINAMICS drive object with its telegrams (number, input/output byte ranges), the PLC tags whose addresses overlap each telegram range, and the technology objects whose resolved hardware connections reach the drive. Joins use native paths and addresses, never names. coverage reports drives, tags and technology objects per source; noDrivesConfirmed is true only for a complete Startdrive read with no drive. blockers lists every gap or ambiguity (partial or unavailable coverage, tags in several PLCs on one telegram, a tag spanning telegrams, a technology object resolving to several drives, several technology objects on one drive); dependent writes must not proceed while writeReady is false. Read-only; changes nothing.")]
    public Task<CallToolResult> DriveInventoryRead(CancellationToken cancellationToken, string? projectName = null) =>
        ToolResult.GuardAsync(async () => await DriveInventory.Capture(bridge, projectName, cancellationToken), "DriveInventoryFailed");

    [McpServerTool(Name = "servo_call_plan", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Plan, never apply, one caller FB that calls an existing servo wrapper FB once per reviewed drive: one multi-instance and one fault Bool output per drive, wired to the drive's own status and control tags from drive_inventory. request: plcPath, callerName, wrapperName, parameters {status, control, reset, ss1, fault} naming the wrapper's own parameters (checked against its native interface; nothing is guessed from names), resetExpression and ss1Expression (one SCL expression each, applied as confirmed design), optional faultNaming containing {instance}, drives [{hardwarePath, instanceName}]. blockers lists every gap: drive_inventory blockers, missing or mistyped wrapper parameters, not exactly one status/control tag per drive, duplicate drives or names, and any other block already writing a selected drive's control tag (symbol_trace). When writeReady, change holds the source_upsert payload to prepare and apply with compile=true.")]
    public Task<CallToolResult> ServoCallPlanRead(JsonElement request, CancellationToken cancellationToken) => ToolResult.GuardAsync(async () =>
        await ServoCallPlan.Plan(bridge, JsonNode.Parse(request.GetRawText())?.AsObject() ?? throw new BridgeException("InvalidRequest", "request must be an object."),
            Path.Combine(options.ArtifactsDirectory, "exports"), cancellationToken), "ServoCallPlanFailed");
}
