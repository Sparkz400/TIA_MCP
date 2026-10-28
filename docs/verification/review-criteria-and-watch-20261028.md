# Review-feature criteria and native watch tables — 28 October 2026

Two items from the [offline sweep ranking](host-only-qualification-20261027.md#remaining-offline-reachable-work-ranked-by-cells-per-effort):
rank 4 (review-family criteria) and rank 5 (`watch_table_*` / `qualification_watch_*`).

## Review-family criteria correction

40 catalog features carried `AC-native` and `AC-recovery` although their Host tools never
call the bridge. Each one's catalog `api` reason already says so ("Offline Host artifact
review only; no native product or execution API is invoked"), and the tool sources take no
`IBridgeClient` in those methods. `AC-native` asks for native compile/readback and
save/reopen, and `AC-recovery` for recovery from a partial native failure without replaying
uncertain writes. Neither can apply to a tool that reads committed uploads and writes Host
artifacts. The criteria were the default seven-criterion set, not a decision about these
tools.

The corrected set is `AC-identity`, `AC-completeness`, `AC-preview`, `AC-evidence`,
`AC-preserve`. That is the set the verified Host-only precedent `translation_review_page`
already carries. The 80 removed cells are inapplicable, not closed: the matrix total drops
from 2,099 to 2,019 non-deferred cells.

| Family | Features |
| --- | --- |
| Test Suite | `testsuite_style_plan_review`, `testsuite_result_import`, `testsuite_result_review` |
| Software units | `software_unit_review`, `software_unit_review_page` |
| Connections | `connection_review`, `connection_review_page` |
| DB snapshots | `db_snapshot_review`, `db_snapshot_review_page` |
| Drive profiles | `drive_profile_review`, `drive_profile_review_page` |
| Access | `access_review`, `access_review_page` |
| Public certificates | `public_certificate_review`, `public_certificate_review_page` |
| Web applications | `web_application_review`, `web_application_review_page` |
| Technology profiles | `technology_profile_review`, `technology_profile_report` |
| Safety evidence | `safety_evidence_review`, `safety_evidence_report` |
| HMI components | `hmi_component_review`, `hmi_component_review_page` |
| Add-In selection | `addin_selection_review`, `addin_selection_page` |
| Multiuser | `multiuser_observation_review`, `multiuser_observation_page` |
| Teamcenter | `teamcenter_observation_review`, `teamcenter_observation_page` |
| Composite engineering | `composite_engineering_review`, `_reopen`, `_page`, `_artifact_read`, `_list` |
| Runtime handoff | `runtime_handoff_observation`, `_review`, `_reopen`, `_page`, `_artifact_read`, `_list` |

### Host-only qualification

`scripts/Test-Offline.ps1 -Configuration Release` ran from a clean detached worktree of
`7aee9ef` (`D:\wp\off7a`) and passed: "Offline validation (All)", with every managed suite,
Python test and actual-Host MCP workflow. Its review-runner reports were curated into one
record per family, `local.hostonly.<family>.20261028`, each binding the Release Host tree
hash. A feature was verified only when its family's runs exercised every one of its tools
without error; all 40 qualified. The Test Suite style runner reran on a fresh fixture from
the same build, because its first fixture root already held the job; its report now states
completion and names its 46 checks. `scripts/curate_evidence.py` now reads the `shutdowns`
receipts these runners write.

Every record says Host-only: no TIA, Openness, PLCSIM or device is involved, and the
reviews compare supplied or synthetic observations, so no native compatibility,
communication, safety or runtime claim follows.

Kept as they were: `db_snapshot_capture` and `composite_engineering_capture` (they call the
bridge), `hmi_text_list_author` and `hmi_text_list_generate_set` (their output is meant for a
native import), and the `runtime_*_page` features, which wait on the UA runtime scope
decision.

## Native watch tables

Evidence [`local.watch.native.20261028`](../../evidence/local.watch.native.20261028.json).
Frozen Debug build of `18964f2` in `D:\wp\b11`: sources extracted with `git archive`, a
`source-manifest.json` of every file for the watch oracle, and the worker registered by its
own post-build step. `src/` equals `7aee9ef`. Each run created a fresh owned scratch
CPU 1511-1 PN V4.1 project; nothing touched a user project, a PLC or PLCSIM.

| Run | Runner and mode | Result |
| --- | --- | --- |
| `bool3` | `qualify_watch_production_host.py --mode bool` | Seed, saved fresh-reopen baseline, prepare, independent review, apply with readback and peer preservation, fresh-reopen verification, checkpoint recovery, restored and original reconciliation |
| `hostapply1` | `qualify_watch_production_host.py --mode host.watch_apply_native` | Host lost during native apply; replay and competing owner refused; checkpoint recovery and reconciliation without replay |
| `hostckpt1` | `qualify_watch_mcp.py --mode host.watch_checkpoint` | Host lost at the checkpoint, before any archive; replay and competing owner refused; `watch_table_reconcile_interrupted` against the original |
| `hostrestore1` | `qualify_watch_mcp.py --mode host.watch_restore_native` | Host lost during the native restore; replay refused; the recovered destination located, then restored and original reconciliation |

The independent `verify_watch_expansion` oracle passed each run, and every owned Portal and
worker process has an exit proof. Verified features: `watch_table_read`, `_prepare`,
`_apply`, `_verify_reopen`, `_recover`, `_reconcile_recovery`, `_reconcile_interrupted`,
`qualification_watch_fixture_seed`, `qualification_watch_compete`.

The runners now write `verification.json` (outcome and every MCP call) after the whole run
passed, so `scripts/curate_evidence.py` can read them.

Not cited: `bool2` stopped after `watch_table_recover` with "Admitted source/package bytes
changed", because the runner's own sources were edited while it ran. The frozen-source
guard worked as intended; the trial is kept as failed.

All three Host interruption points are covered. Still open for D02: the native worker-loss points (`checkpoint.*`, `import.*`, `save.*`,
`retrieve.*`) need a registered `OwnedPortalCleanup` helper, and `watch_table_force_document_edit`,
the create/rename/delete table and group features and the authoring features have no passing
native run.

### TIA Portal V21 Update 3 breaks the pinned authoring profile

This PC now runs TIA Portal V21 Update 3; the committed watch fixtures and the authoring
profile were captured under Update 2 on 15 October. Every native export embeds
`DocumentInfo/InstalledProducts`, so all hashes pinned in `WatchAuthoringProfile` (FB/DB
declarations, the empty force table, the peer profile, the candidate) differ. The
`author1` run (`qualify_watch_authoring.py`) therefore reported `scopeMatched: false`. Its
apply also failed readback (`VerificationFailed`): in create mode the candidate keeps the
packaged template's `DocumentInfo`, while TIA's readback carries the current installation's.
Apart from formatting whitespace, that metadata is the only difference. The apply was
recovered and reconciled; no replay. Re-qualifying authoring needs two things: the
candidate taking its `DocumentInfo` from a same-session export, and an independently
reviewed re-pin of the profile under Update 3.
