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

Force edits are expanded document profiles. The ForceValue, Address and DisplayFormat field edits, comment, clone and delete passed natively on operator-authored rows (`local.watch.force-edits.20261029`) and run on production Hosts (maintainer decision of 29 October 2026). Reorder (TIA renumbers force rows' document IDs on export) and the Name field are refused there with `UnsupportedProfile`; only the isolated qualification harness (`--component-qualification --watch-qualification`) dispatches them.

## Live monitoring

`watch_table_monitor` shows the current values of a table's rows on a PLCSIM Advanced instance this Host owns. Pass the table selector of `watch_table_read` (`tableName`, `group`, `forceTable`, optional `projectName`/`plcPath`) and the owned instance: `instanceId`, `instanceName` and the `creationId` it was created or adopted with.

1. The ownership check runs first; a foreign instance, name or `creationId` is refused before anything is read.
2. The table is read offline as `watch_table_read` does, so TIA Portal must not be online with the PLC (`OfflineRequired`).
3. Rows `offset` to `offset + count` (`count` 1 to 64) resolve by symbol against the instance's own tag list. TIA writes `"DB".Member`, PLCSIM may write `DB.Member`; both sides are compared without quotes, and the tag list's spelling is what is read.
4. The resolved symbols are read `samples` times (1 to 20), `intervalMs` apart (100 to 5000).

Each row reports its stored fields and a `resolution`: `resolved` (with symbol, read type and PLC data type), `comment`, `unsupportedType` (Struct, Char, WChar and other types without a typed PLCSIM read), or `unresolved` with a reason (`absoluteAddressOnly`, `notInTargetTagList`, `ambiguousInTargetTagList`, `targetTagListTruncated`). Only resolved rows are read. `page.nextOffset` is null on the last page.

The tool only reads. The values of one sample come from reads made one after another, not from one scan. `ModifyValue`, `ForceValue` and `ForceIntention` are stored configuration and are never applied. The instance's program is not compared with the project (`programMatchVerified: false`). Physical PLCs, the Web API and OPC UA are not used.

It passed natively on an owned CPU 1511 instance on Softbus, with two Bool rows of an instance DB, in RUN and STOP (`local.watch.monitor.20261030`). There, PLCSIM's tag list spelled the symbols without quotes (`TiaMcp_EnableGate_DB.Active`).

Forcing is not available: V21 Openness, the PLCSIM Advanced V8 API, the S7-1500 Web API and OPC UA have no force route, and a write is an assignment, not a force. The evidence is in the [live monitoring and forcing plan](../plans/live-monitoring-forcing-plan.md).

## Limits

- The code is compiled against the V21 PublicAPI only and has not run natively. The manual describes `PlcWatchTable.Name` as read-only, but the installed metadata has a setter. A native run must show which is right.
- References to a table outside the watch/force manifest are not inventoried, for example from other products. Access rules are checked only to refuse rename or delete.
- There is no recursive group delete, no group or table move or copy, and no master-copy creation (`CreateFrom`). See the [watch and force table plan](../plans/expansion-plans/watch-and-force-table-engineering.md).
- Force tables: the installed metadata does not state that a PLC has exactly one force table (the manual does), so edits target the exact named table and all other tables stay peers. Typed readback treats a field the export omits as empty, so rows with omitted fields (for example an `Undef` display format) are refused. Force rows get no document ID reindex. The planner-backed `watch_table_prepare_authoring` route stays watch-only.
- Access rules live on the `WatchAndForceTableAccessManager` service of the PLC or display `DeviceItem`, not on the tables. They are not edited; they stay in the peer manifest, so an import that drops a rule fails verification.
- Monitoring reads owned PLCSIM instances only and does not read absolute-address rows. Forcing has no public route (see [Live monitoring](#live-monitoring)).
