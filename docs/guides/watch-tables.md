# Offline watch-table lifecycle

Six tools prepare offline lifecycle changes to V21 watch tables and their user groups. They change stored engineering data only. They never monitor values, write online, arm a modification, force anything or change web/display access rules. Force tables are not edited.

| Tool | Native effect after apply | Extra refusals |
| --- | --- | --- |
| `watch_table_prepare_create` | `PlcWatchTableComposition.Create(tableName)` in the exact `group` | A watch or force table with that name (case-insensitive) anywhere in the PLC |
| `watch_table_prepare_rename` | `PlcWatchTable.Name = newName` | A name collision anywhere in the PLC, a case-only rename, or a web/display access rule on the table |
| `watch_table_prepare_delete` | `PlcWatchTable.Delete()` | A web/display access rule on the table |
| `watch_table_group_prepare_create` | `PlcWatchAndForceTableUserGroupComposition.Create(groupName)` under the parent `group` | A sibling group with that name (case-insensitive) |
| `watch_table_group_prepare_rename` | `PlcWatchAndForceTableUserGroup.Name = newName` | A sibling group collision or a case-only rename |
| `watch_table_group_prepare_delete` | `PlcWatchAndForceTableUserGroup.Delete()` | Any contained watch table, force table or subgroup. Only empty groups can be deleted. |

`group` is the exact parent group path from `watch_table_read`: `root` or `root/<escaped name>/...`. Optional `projectName`/`plcPath` select the PLC as they do for the other watch tools.

## Workflow

1. Call a prepare tool with a unique `idempotencyKey`. The Host captures the parent group's complete subtree: every contained watch and force table with its export hash and row count, and every subgroup. It also captures the full peer manifest outside that subtree, as in [the watch workflow](../reference/watch-table-workflow.md). The review stores the exact expected subtree after the change. A renamed table is expected to export as the captured document with only its name changed. A new table's export hash is known only from readback, so the review expects exactly one added table with zero rows.
2. Apply with `watch_table_apply` using the returned `operationId` and `planSha256`. Each operation has its own mutation profile, for example `watch.table-create.v1` or `watch.group-delete.v1`. None has native lifecycle evidence yet. Production Hosts therefore refuse apply with `UnsupportedProfile`, while the review stays available. Only the isolated qualification harness dispatches.
3. Dispatch uses the existing watch lease. The worker requires a saved scratch project that it created itself. It holds exclusive access, rechecks the subtree and peers, archives a checkpoint and performs the single native call. Before and after saving, it requires the exact expected subtree and an unchanged peer manifest. It never replays.
4. Verify persistence with `watch_table_verify_reopen` from a fresh native instance. On failure, use `watch_table_recover`, `watch_table_reconcile_recovery` and `watch_table_reconcile_interrupted` as for document edits. The checkpoint archive is the only route back from a delete.

## Limits

- The code is compiled against the V21 PublicAPI only and has not run natively. The manual describes `PlcWatchTable.Name` as read-only, but the installed metadata has a setter. A native run must show which is right.
- References to a table outside the watch/force manifest are not inventoried, for example from other products. Access rules are checked only to refuse rename or delete.
- There is no recursive group delete, no group or table move or copy, and no master-copy creation (`CreateFrom`). Force-table edits, access-rule changes and live monitoring/forcing remain separate obligations. See the [watch and force table plan](../plans/expansion-plans/watch-and-force-table-engineering.md).
