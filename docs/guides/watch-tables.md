# Offline watch-table lifecycle

Six tools prepare offline lifecycle changes to V21 watch tables and their user groups. They change stored engineering data only. They never monitor values, write online, arm a modification, force anything or change web/display access rules. Force tables have no lifecycle tools; their rows are edited as documents (see [Force-table document edits](#force-table-document-edits)).

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
2. Apply with `watch_table_apply` using the returned `operationId` and `planSha256`. Each operation has its own mutation profile, for example `watch.table-create.v1` or `watch.group-delete.v1`. All six passed natively in the isolated qualification harness on 28 October 2026 ([record](../verification/watch-lifecycle-20261028.md)), but none is promoted to production. Production Hosts therefore refuse apply with `UnsupportedProfile`, while the review stays available. Only the isolated qualification harness dispatches.
3. Dispatch uses the existing watch lease. The worker requires a saved scratch project that it created itself. It holds exclusive access, rechecks the subtree and peers, archives a checkpoint and performs the single native call. Before and after saving, it requires the exact expected subtree and an unchanged peer manifest. It never replays.
4. Verify persistence with `watch_table_verify_reopen` from a fresh native instance. On failure, use `watch_table_recover`, `watch_table_reconcile_recovery` and `watch_table_reconcile_interrupted` as for document edits. The checkpoint archive is the only route back from a delete.

## Force-table document edits

V21 has no setter on any `PlcForceTableEntry` property or on `PlcForceTable.Name`, and no force-table `Create` or `Delete`. Force rows therefore change only through the reviewed exported document: `PlcForceTable.Export(WithDefaults)`, the same preserving editor as watch documents, then `PlcForceTableComposition.Import(..., ImportOptions.Override)` into the table's exact group.

Use `watch_table_read` and `watch_table_prepare` with `forceTable=true` and the force table's exact `tableName` and `group`. One prepared operation holds one edit:

| `editKind` | Effect |
| --- | --- |
| `field` | Set `Name` (symbol), `Address`, `DisplayFormat` or `ForceValue` of one row to `value`. The field must already exist as plain text in the export. |
| `comment` | Replace one existing culture's comment text. |
| `clone`, `delete`, `reorder` | Copy a captured row to `insertAt`, remove one row, or apply a complete permutation. |

Apply, fresh-reopen verification and recovery use `watch_table_apply`, `watch_table_verify_reopen` and the recovery tools exactly as for watch documents. The review selector keeps `targetKind: forceTable`, so every later observation targets the force table, and every watch table stays in the peer manifest.

Forcing is never selected or activated. `ForceIntention` cannot be set; asking for it, for a field the export lacks or for a row out of range is refused with `UnsupportedProfile`. A force table whose typed rows or exported XML show any `ForceIntention` other than false is refused at prepare, before import and before save. Nothing is monitored, forced or written online.

Force edits are expanded document profiles. Production Hosts refuse both prepare and apply with `UnsupportedProfile`; only the isolated qualification harness (`--component-qualification --watch-qualification`) dispatches them until native lifecycle evidence exists.

## Limits

- The code is compiled against the V21 PublicAPI only and has not run natively. The manual describes `PlcWatchTable.Name` as read-only, but the installed metadata has a setter. A native run must show which is right.
- References to a table outside the watch/force manifest are not inventoried, for example from other products. Access rules are checked only to refuse rename or delete.
- There is no recursive group delete, no group or table move or copy, and no master-copy creation (`CreateFrom`). See the [watch and force table plan](../plans/expansion-plans/watch-and-force-table-engineering.md).
- Force tables: the installed metadata does not state that a PLC has exactly one force table (the manual does), so edits target the exact named table and all other tables stay peers. Typed readback treats a field the export omits as empty, so rows with omitted fields (for example an `Undef` display format) are refused. Force rows get no document ID reindex. The planner-backed `watch_table_prepare_authoring` route stays watch-only.
- Access rules live on the `WatchAndForceTableAccessManager` service of the PLC or display `DeviceItem`, not on the tables. They are not edited; they stay in the peer manifest, so an import that drops a rule fails verification. Live monitoring and forcing remain separate obligations.
