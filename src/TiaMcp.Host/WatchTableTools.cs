using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;
using TiaMcp.Core.WatchTables;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed class WatchMonitorTools(IBridgeClient bridge, SimulatorClient simulator)
{
    [McpServerTool(Name = "watch_table_monitor", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Monitor the rows of an offline V21 watch table (or, with forceTable=true, a force table) on a PLCSIM Advanced instance this Host owns: instanceId, instanceName and the creationId it was created or adopted with. Reads the table as watch_table_read does, resolves rows [offset, offset+count) (count 1..64) by symbol against the instance's own tag list, then reads the resolved symbols samples times (1..20, intervalMs 100..5000 apart) over the PLCSIM API. Comment rows, absolute-address-only rows, symbols missing from or ambiguous in the tag list and types without a typed PLCSIM read (Struct, Char, WChar, ...) are reported per row and not read. Read only: the values of one sample come from sequential reads, not one scan; ModifyValue, ForceValue and ForceIntention are stored configuration and are never applied. Nothing is modified or forced (no public V21, PLCSIM, Web API or OPC UA route forces a value). The instance's program is not compared with the project. Never a physical PLC.")]
    public async Task<CallToolResult> Monitor(string tableName, int instanceId, string instanceName, string creationId, CancellationToken cancellationToken,
        string? projectName = null, string? plcPath = null, string group = "root", bool forceTable = false, int offset = 0, int count = 32, int samples = 1, int intervalMs = 500)
    {
        string json; bool error = false;
        try { json = (await WatchTableMonitor.Run(bridge, simulator.Bridge, tableName, projectName, plcPath, group, forceTable, instanceId, instanceName, creationId,
            offset, count, samples, intervalMs, cancellationToken)).ToJsonString(BridgeProtocol.Json); }
        catch (BridgeException ex)
        { json = JsonSerializer.Serialize(new { error = new { code = ex.Code, message = ex.Message, details = (ex as BridgeOperationException)?.Details } }, BridgeProtocol.Json); error = true; }
        return new CallToolResult { IsError = error, Content = [new TextContentBlock { Text = json }], StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
    }
}

public sealed partial class TiaTools
{
    [McpServerTool(Name = "watch_table_generate", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Generate diagnostic watch intent from an exact saved offline FB and direct instance DB. Reuses native instance resolution and captured declarations; retains reasons and unresolved suggestions. Returns immutable generationId/generationSha256 for review. Materializes symbolic scalar rows with separate native datatype profile gates; no monitoring, online writing or forcing.")]
    public Task<CallToolResult> WatchGenerate(string projectPath, string plcPath, string blockPath, string instanceDbPath, string tableName, CancellationToken cancellationToken, int maxRows = 64)
        => Call("watch_generate", new { projectPath, plcPath, blockPath, instanceDbPath, tableName, maxRows }, cancellationToken);

    [McpServerTool(Name = "watch_table_prepare_authoring", Destructive = false, OpenWorld = false)]
    [Description("Review planner-backed watch creation/update without native mutation. request: generationId, generationSha256, idempotencyKey, mode:create|update, destination:{projectPath,plcPath,group,tableName}, optional template:{group,tableName}, steps:[{kind:add,candidateId,templateOrdinal,insertAt?,comments?},{kind:remove,ordinal},{kind:reorder,order:[all ordinals]},{kind:comment,ordinal,comments:{culture:text}}]. Defaults to captured target rows for update or a packaged genuine native row template for create/empty target. Steps use sequential zero-based ordinals; bounds 128 steps/256 rows, active project cultures only. Exact source freshness, absent creation destination/collisions, full before/after rows/XML and every mutation profile gate are retained. Apply requires native verification of each profile; unqualified profiles remain reviewable but cannot mutate in production.")]
    public Task<CallToolResult> WatchPrepareAuthoring(JsonElement request, CancellationToken cancellationToken)
        => Call("watch_prepare_authoring", request, cancellationToken);
    [McpServerTool(Name = "watch_table_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export an exact offline V21 watch table (or, with forceTable=true, the exact force table with typed ForceValue/ForceIntention rows) and bounded peer preservation manifest. No monitoring or force activation.")]
    public Task<CallToolResult> WatchRead(string tableName, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root", bool forceTable = false)
        => Call("watch_read", new { tableName, projectName, plcPath, group, targetKind = forceTable ? "forceTable" : null }, cancellationToken);
    [McpServerTool(Name = "watch_table_prepare", Destructive = false, OpenWorld = false)]
    [Description("Prepare one exact offline Bool watch row's modifyValue as TRUE or FALSE in a worker-owned scratch. Freezes complete XML and peer manifest for review. Comment, clone, delete, reorder and field (editKind=field with field Name|Address|DisplayFormat|ModifyValue and value) edits remain restricted to the isolated qualification harness. forceTable=true edits the existing offline force table document instead: field accepts Name|Address|DisplayFormat|ForceValue; ForceIntention is never set, any selected (ForceIntention true) row refuses the edit, and nothing is forced or monitored online.")]
    public Task<CallToolResult> WatchPrepare(string tableName, int ordinal, string idempotencyKey, CancellationToken cancellationToken, string? modifyValue = null, string? projectName = null, string? plcPath = null, string group = "root", string editKind = "modifyValue", string? culture = null, string? text = null, int? insertAt = null, int[]? order = null, bool reindexDocumentIds = false, bool forceTable = false, string? field = null, string? value = null)
        => Call("watch_prepare", new { tableName, ordinal, modifyValue, idempotencyKey, projectName, plcPath, group, editKind, culture, text, insertAt, order, reindexDocumentIds, targetKind = forceTable ? "forceTable" : null, field, value }, cancellationToken);
    [McpServerTool(Name = "watch_table_prepare_create", Destructive = false, OpenWorld = false)]
    [Description("Review creating one empty watch table named tableName in the exact existing group (root or root/<escaped segments>) without native mutation. Freezes the group's complete subtree, all peers and the PLC-wide watch/force name check. Apply with watch_table_apply in a worker-owned saved scratch; the profile stays production-gated pending native lifecycle evidence. No rows, monitoring or forcing.")]
    public Task<CallToolResult> WatchPrepareCreate(string tableName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "createTable", name = tableName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_prepare_rename", Destructive = false, OpenWorld = false)]
    [Description("Review renaming one exact watch table in its group to newName without native mutation. Expected readback is the captured table with only its name changed; refuses PLC-wide name collisions and tables with web/display access rules. Apply with watch_table_apply; production-gated pending native lifecycle evidence.")]
    public Task<CallToolResult> WatchPrepareRename(string tableName, string newName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "renameTable", name = tableName, newName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_prepare_delete", Destructive = false, OpenWorld = false)]
    [Description("Review deleting one exact watch table from its group without native mutation. Refuses tables with web/display access rules; the checkpoint archive is the only restore route. Apply with watch_table_apply; production-gated pending native lifecycle evidence.")]
    public Task<CallToolResult> WatchPrepareDelete(string tableName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "deleteTable", name = tableName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_group_prepare_create", Destructive = false, OpenWorld = false)]
    [Description("Review creating one empty user watch-table group named groupName under the exact parent group without native mutation. Freezes the parent subtree and all peers. Apply with watch_table_apply; production-gated pending native lifecycle evidence.")]
    public Task<CallToolResult> WatchGroupPrepareCreate(string groupName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "createGroup", name = groupName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_group_prepare_rename", Destructive = false, OpenWorld = false)]
    [Description("Review renaming one direct user group of the exact parent group to newName without native mutation. Contained tables and subgroups must keep their exact content under the new path. Apply with watch_table_apply; production-gated pending native lifecycle evidence.")]
    public Task<CallToolResult> WatchGroupPrepareRename(string groupName, string newName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "renameGroup", name = groupName, newName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_group_prepare_delete", Destructive = false, OpenWorld = false)]
    [Description("Review deleting one empty direct user group of the exact parent group without native mutation. Groups holding any table or subgroup are refused; there is no recursive delete. Apply with watch_table_apply; production-gated pending native lifecycle evidence.")]
    public Task<CallToolResult> WatchGroupPrepareDelete(string groupName, string idempotencyKey, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_prepare_lifecycle", new { operation = "deleteGroup", name = groupName, idempotencyKey, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_apply", Destructive = true, OpenWorld = false)]
    [Description("Apply an exact reviewed watch plan once in its owned scratch. Rechecks native identity under exclusive access, archives before import, verifies typed/XML readback and peers, saves. Ownership remains until fresh reopen verification; interrupted dispatch is never replayed.")]
    public Task<CallToolResult> WatchApply(string operationId, string reviewedPlanSha256, CancellationToken cancellationToken)
        => Call("watch_apply", new { operationId, reviewedPlanSha256 }, cancellationToken);
    [McpServerTool(Name = "watch_table_verify_reopen", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Verify the applied watch change and unchanged peers after opening the saved project in a fresh owned native instance. Releases durable ownership only on matching evidence.")]
    public Task<CallToolResult> WatchVerify(string operationId, CancellationToken cancellationToken) => Call("watch_verify_reopen", new { operationId }, cancellationToken);
    [McpServerTool(Name = "engineering_operation_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read and validate a retained shared operation journal. Does not retry or resume native actions.")]
    public Task<CallToolResult> EngineeringOperationStatus(string operationId, CancellationToken cancellationToken) => Call("engineering_operation_status", new { operationId }, cancellationToken);
    [McpServerTool(Name = "watch_table_recover", Destructive = true, OpenWorld = false)]
    [Description("Explicitly restore a watch checkpoint into a newly reserved location in an empty owned native instance. Keeps original project and failed receipts; no automatic replay. Recovery remains reserved pending fresh recovered reopen and original-resource reconciliation.")]
    public Task<CallToolResult> WatchRecover(string operationId, CancellationToken cancellationToken) => Call("watch_recover", new { operationId }, cancellationToken);
    [McpServerTool(Name = "watch_table_reconcile_recovery", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Observe the exact original or freshly reopened restored project, verify its native manifest, and release recovery reservations only after both resources have evidence. role is original or restored.")]
    public Task<CallToolResult> WatchReconcile(string operationId, string role, CancellationToken cancellationToken) => Call("watch_reconcile_recovery", new { operationId, role }, cancellationToken);
    [McpServerTool(Name = "watch_table_reconcile_interrupted", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Evidence-only reconciliation when checkpoint/restore response was lost before its artifact receipt. Requires a fresh exact original project observation; any admitted destination is verified absent or explicitly retained partial. Never replays a checkpoint, import or restore.")]
    public Task<CallToolResult> WatchReconcileInterrupted(string operationId, CancellationToken cancellationToken, string? destinationDisposition = null)
        => Call("watch_reconcile_interrupted", new { operationId, destinationDisposition }, cancellationToken);
}
