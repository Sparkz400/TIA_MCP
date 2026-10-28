# Watch-table lifecycle (D02) — 28 October 2026

Evidence [`local.watch.lifecycle.20261028`](../../evidence/local.watch.lifecycle.20261028.json).
Runner [`scripts/qualify_watch_lifecycle.py`](../../scripts/qualify_watch_lifecycle.py), run `lc2`,
on a frozen Debug build in `D:\wp\b53` (sources extracted with `git archive`, with a
`source-manifest.json`). The run used one fresh owned scratch CPU 1511-1 PN V4.1 under TIA Portal V21
Update 3, through the isolated qualification Host (`--component-qualification --watch-qualification none`).
Nothing touched a user project, a PLC or PLCSIM.

## What ran

The scratch is seeded with the `MilestoneWatch` fixture through `qualification_watch_fixture_seed`.
The seed registers the project as worker-owned after a reopen and leaves three root peers
(`MilestoneWatch`, `UnchangedPeer` and the scratch's force table). Six operations then chain on
the saved original project:

| # | Tool | Operation | Typed refusals prepared afterwards |
| --- | --- | --- | --- |
| 1 | `watch_table_group_prepare_create` | create `WTL_Group` in root | same group again: `WatchGroupCollision` |
| 2 | `watch_table_prepare_create` | create `WTL_Table` in `root/WTL_Group` | same name in root: `WatchTableCollision` (PLC-wide); delete the group: `WatchGroupNotEmpty` |
| 3 | `watch_table_group_prepare_rename` | rename the group to `WTL_Renamed` with the table inside | old name: `WatchGroupNotFound` |
| 4 | `watch_table_prepare_rename` | rename the table to `WTL_Moved` | old name: `WatchTableNotFound` |
| 5 | `watch_table_prepare_delete` | delete `WTL_Moved` | again: `WatchTableNotFound` |
| 6 | `watch_table_group_prepare_delete` | delete `WTL_Renamed` | again: `WatchGroupNotFound` |

Each operation runs these steps:

1. **Prepare** through its tool.
2. **Independent review** from the retained files only: the canonical plan digest, the plan
   inputs (review and captured subtree), the profile gate (`watch.<object>-<verb>.v1`) and the
   expected group subtree, recomputed in Python from the captured manifest.
3. **Apply** once through `watch_table_apply` with the approved digest. The readback must match
   the expected subtree, with the new table's export hash as the only unknown, and the peer
   manifest must be unchanged.
4. **Fresh reopen**, then `watch_table_verify_reopen`; the reopened subtree must equal the
   applied one.
5. **Recovery:** `watch_table_recover` restores the checkpoint into a new location. Its subtree
   must equal the pre-operation subtree. Then `watch_table_reconcile_recovery` runs for the
   restored project (freshly reopened) and for the original. The journal ends `reconciled`.
6. **Baseline chain:** the next operation's captured baseline must equal this verified subtree.
   So recovery, reconciliation and the refusals left the original unchanged.

The group rename kept the contained table's semantic hash under the new path. The table rename
changed only that hash, to the value the review predicted (`renamedSemanticSha256`). After the
last operation the root holds exactly the three seeded peers again. The run recorded 128 tool calls;
the seven rejected ones are the seven expected refusals. Every owned Portal and worker
lifetime closed with an exit proof.

Verified: `watch_table_prepare_create`, `watch_table_prepare_rename`, `watch_table_prepare_delete`,
`watch_table_group_prepare_create`, `watch_table_group_prepare_rename`,
`watch_table_group_prepare_delete` (42 cells; matrix 593 of 2,019 open, catalog `0.4.56`).

## Failed trial

`lc1` (`D:\wp\wt\lc1`, build `b52`) did not seed the scratch. The first operation applied
on the newly created scratch. After the fresh reopen, the second apply was refused with
`ProjectIsolationRequired`: the worker's ownership of a newly created scratch does not survive a
reopen, and only the seed registers the path. The trial is retained as failed and not cited.

## Limits

- Production apply of the lifecycle profiles stays disabled
  (`WatchMutationProfiles.Status` still reports `productionApplyEnabled: false`). Promoting a
  profile is a separate, reviewed change.
- One user-group level below root, ASCII names and one empty table. Force tables, deeper
  nesting, escaped names and tables with web/display access rules are not exercised.
- No Host or worker loss during a lifecycle operation. Offline stored configuration only:
  no monitoring, modify or forcing.
