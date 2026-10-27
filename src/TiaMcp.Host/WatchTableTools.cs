using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TiaMcp.Host;
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
    [Description("Export an exact offline V21 watch table and bounded peer preservation manifest. No monitoring or force activation.")]
    public Task<CallToolResult> WatchRead(string tableName, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string group = "root")
        => Call("watch_read", new { tableName, projectName, plcPath, group }, cancellationToken);
    [McpServerTool(Name = "watch_table_prepare", Destructive = false, OpenWorld = false)]
    [Description("Prepare one exact offline Bool watch row's modifyValue as TRUE or FALSE in a worker-owned scratch. Freezes complete XML and peer manifest for review. Comment, clone, delete and reorder edits remain restricted to the isolated qualification harness.")]
    public Task<CallToolResult> WatchPrepare(string tableName, int ordinal, string idempotencyKey, CancellationToken cancellationToken, string? modifyValue = null, string? projectName = null, string? plcPath = null, string group = "root", string editKind = "modifyValue", string? culture = null, string? text = null, int? insertAt = null, int[]? order = null, bool reindexDocumentIds = false)
        => Call("watch_prepare", new { tableName, ordinal, modifyValue, idempotencyKey, projectName, plcPath, group, editKind, culture, text, insertAt, order, reindexDocumentIds }, cancellationToken);
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
