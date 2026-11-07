# Scoped tag-table import: native qualification (7 November 2026)

Evidence: `local.tag-table.scoped-import.20261107`
([summary](../../evidence/local.tag-table.scoped-import.20261107.json)), issue
[#209](https://github.com/Sparkz400/TIA_MCP/issues/209). It covers the
`tag-table-content-and-project-metadata-v1` policy described in
[tag-table import scope](../guides/engineering-workflows.md#tag-table-import-scope).

## Run

`scripts/qualify_tag_table_scope.py` ran on Debug builds of `e5445ef6`
(TIA Portal V21 Update 3, PCNico). It used one scratch project with a CPU 1511-1 PN,
created and owned by the run. 16 of 16 steps passed. The summary's source commit
`8c5f32f7` adds only a curator change on top of `e5445ef6`; `src` and every
executable that ran are the same.

| Step | Result |
| --- | --- |
| Fixture seeds tables `Signals` (`Existing` `%M50.0`) and `Other` (`Untouched` `%M51.0`) and an inconsistent SCL FC `TagScopeBroken` | Block reported `isConsistent: false` |
| Candidate without `Existing` | `TagScopeMismatch` at prepare |
| Add `Added` (Bool `%M50.1`) | Prepares despite the inconsistent block; the plan's scope policy is the scoped one |
| Apply with checkpoint, compile, save | `applied`. One baseline error (`Tag "UnresolvedFlag" not defined`), the same error after the change |
| Native readback | `Added` and `Existing` present |
| `change_restore` | `ScopedRecoveryRequired` |
| External edit of the unrelated table `Other` after prepare | `StaleRevision` at apply |
| Native consumer proof | `TagScopeConsumer` (FC3) reading `Added` compiles as Bool; with `Added` as Int at `%MW60` TIA reports `Data type 'Int' cannot be converted implicitly into data type 'Bool'`; the tag is then restored |
| Same type change through MCP | `CompileFailed`, `recovery_required`, `before.zap21` kept; diagnostics are the baseline error plus the consumer error |
| Shutdown | Worker exited on its own with code 0; project lock release not verified |

## Negative case

The first negative case imported an unused tag at the out-of-range address
`%M20000.0`. V21 Update 3 accepted it without a compiler error, so the case could
not fail. A tag only produces a compile error when a block uses it. The fixture now
generates a consistent block that reads the tag, and proves the native diagnostic
before the qualifier applies the same change through MCP.

## Not covered

Software units, namespaces, user constants, multilingual comments, F-PLCs,
reopened projects and interrupted outcomes. Project-lock release was not verified.
The evidence record covers the Debug build only.

## Installed bundle

The engineering bundle `tia-mcp-v21-0.9.0_20261107_074331_468f5c5f` was packaged
from main `e2bc06a0` and passed strict bundle verification. Claude Desktop's
`tia-v21` server now starts it. That server previously ran the offline bundle of
24 October, which starts without native workers. The bundle worker was added to
the Openness AllowList.

- The configured launch command started Host 0.9.0, advertised 307 tools including
  `tia_start`, `change_apply` and `tag_table_read`, and reported catalog `0.4.135`
  with `local.tag-table.scoped-import.20261107`.
- `qualify_tag_table_scope.py --host … --worker …` with the bundle's own Host and
  worker passed the same 16 steps (local report `artifacts/ts209/b01`). Its preview
  reported the scope policy `tag-table-content-and-project-metadata-v1`, and its
  worker exited on its own with code 0.

That bundle predates the `compilerSucceeded` fix below. It was replaced by
`tia-mcp-v21-0.9.0_20261107_081938_0085f546`, packaged from main `575f767d`, which
Claude Desktop's `tia-v21` now starts instead. Its worker is in the AllowList, its
configured launch reported catalog `0.4.137` with
`local.tag-table.replacement-compile.20261107`, and the extended qualifier passed
18 of 18 steps with its own Host and worker (local report `artifacts/ts210/b01`),
including `compilerSucceeded=false` on the failed apply.

## Replacement and compile results

Evidence: `local.tag-table.replacement-compile.20261107`
([summary](../../evidence/local.tag-table.replacement-compile.20261107.json)), issues
[#210](https://github.com/Sparkz400/TIA_MCP/issues/210) and
[#211](https://github.com/Sparkz400/TIA_MCP/issues/211). The extended qualifier
ran on Debug builds of `f1a78204` and passed 18 of 18 steps: every step above, plus
these.

| Check | Result |
| --- | --- |
| Fresh export of `Signals` with `Existing` renamed to `Renamed` | `TagScopeMismatch` at prepare; a second export has the same members, types and addresses (only the export's `Created` timestamp differs) |
| Stages of the applied import | `prechange_project_saved`, `mutation_applied`, `compile_finished`, `tag_table_content_verified`, `changed_project_saved`, in that order |
| Failed type change | `compile.compilerSucceeded=false`; `mutation_applied` and `compile_finished` without `changed_project_saved`; the message no longer mentions screen rollback |

Before `f1a78204`, a failed apply threw before `compilerSucceeded` was written,
and the failure message mentioned Classic screen rollback for every change kind.
Explicit renames and deletions use `tag_set` and `tag_delete`, which are qualified
in `local.plc_object_mutations.rerun.20261028`, not in this run.

## Scoped restore

Evidence: `local.tag-table.scoped-restore.20261107`
([summary](../../evidence/local.tag-table.scoped-restore.20261107.json)), issue
[#218](https://github.com/Sparkz400/TIA_MCP/issues/218). Debug builds of `e01f1ac1`
passed 26 of 26 steps: every step above, then these.

| Check | Result |
| --- | --- |
| Edit of the unrelated table `Other` after the failed type change, then `change_restore` | `StaleRevision`; nothing restored |
| `Other` returned to its recorded state, then `change_restore` | `restored`: `Signals` re-imported from its retained export, every captured table equal to the before capture, the one baseline error unchanged, project saved; `Added` is Bool `%M50.1` again |
| `change_restore` of the earlier applied import | `StaleRevision` |
| A new import | Prepares without admission cleanup |
| Fresh owned TIA, `project_open` | Same `Signals` members; the consumer FC is consistent |

Found while qualifying: shared admission compared the project fingerprint with
the after-change fingerprint for every restore, which a failed import does not
have; the scoped restore is exempt and checks every table itself. Deleting a table
the import created is implemented but was not exercised natively.
