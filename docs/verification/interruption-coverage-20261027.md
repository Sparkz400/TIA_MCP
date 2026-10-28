# Interruption and uncertain-outcome coverage — 27 October 2026

Release item: qualify native interruption/transport loss and uncertain-outcome
reconciliation for every advertised recoverable mutation, including
multi-resource recovery and no replay. This page lists every route and its
status: **qualified** (native evidence), **prepared** (implemented and offline-tested,
native run pending) or **refused** (reconciliation deliberately holds, with the reason).

The trusted reconciler and its proof rules are described in
[admission reconciliation](admission-reconciliation-20261024.md) and
[provisioning reconciliation](provision-reconciliation-20261024.md).

## What changed today

- **Save-time proof for interrupted applies.** `change_apply` proved a project
  `unchanged` when its fresh fingerprint equalled the retrieved checkpoint. That
  covers only what the fingerprint sees, so the eight target-dependent kinds
  (`to_*`, `tag_set`, `constant_set`, `hardware_attributes`, `network_configure`,
  `hmi_engineering`) were always held, even when nothing was saved. Now `unchanged`
  also requires `Project.LastModified` to be earlier than the checkpoint archive's
  write time. TIA persists project edits only by Save, and the archive is written
  after the checkpoint save, so a project last saved before it still is the
  checkpoint, whatever the kind. That releases interrupted target-dependent applies
  that never saved, and keeps any kind held once a save followed the checkpoint
  (an equal fingerprint then does not prove the change was not persisted). Library
  transactions apply the same rule to their project checkpoints. `LastModified` is
  recorded with 100 ns precision in UTC (retained `artifacts/f2` reconciliation).
  The rule is `AdmissionReconcileRules` (14 offline checks in
  `TiaMcp.HardwareEngineering.Tests`).
- **Durable apply stages.** The engineering journal was written only at
  `checkpointing`, `applying` and the end state. It is now also written after
  `mutation_applied` and `changed_project_saved`, so reconciliation evidence and the
  harness see how far an interrupted apply got.
- **Kind × point harness.** `scripts/qualify_interruption.py` gains 15
  `<kind>-<point>` scenarios: one representative per reconciliation route, killed at
  each durable boundary. Each asserts the exact reconciliation, no replay (journal
  bytes unchanged by reconciliation, exactly one apply admission), recovery from the
  checkpoint (retrieval into a new directory once released; refused `ResourceBusy`
  while held) and an independent fresh read of the original project.
  `scripts/test_qualify_interruption.py` keeps every catalog change kind mapped to a
  route and the kind sets equal to the worker's.
- **Native run.** All 15 cases passed in one batch at `e44f77e`
  (`local.engineering.interruption-matrix.20261027`). The only rejected tool calls
  are the expected `ResourceBusy` checkpoint retrievals of held admissions.

## Interruption points of `change_apply`

Each row passed natively for `source_upsert`, `hardware_plug` and `to_create`
(`local.engineering.interruption-matrix.20261027`).

| Point | Trigger (journal) | Interruption | Exact outcome | Original project |
| --- | --- | --- | --- | --- |
| `ckpt` | `checkpointing` | worker + owned TIA | held: no hash-recorded checkpoint | without the change |
| `mut` | `applying`, before `mutation_applied` | worker + owned TIA | released, project `unchanged` | without the change |
| `comp` | `mutation_applied`, before `changed_project_saved` | worker + owned TIA | released, project `unchanged` | without the change |
| `save` | `changed_project_saved`, not `applied` | worker + owned TIA | held: saved, no applied outcome journaled | with the change |
| `host` | `applying` | Host (transport loss); worker finishes | released `verifiedApplied`; target-dependent kinds held | with the change |

A held admission keeps its checkpoint reserved: automatic recovery is refused
until a human decides. Nothing is ever replayed.

## Coverage by route

### `change_apply` / `change_restore` — engineering kinds

| Route | Kinds | Representative | Status |
| --- | --- | --- | --- |
| Project fingerprint | `source_upsert`, `type_source_upsert`, `block_import_xml`, `plc_type_import_xml`, `tag_table_import_xml`, `block_rename`, `block_delete`, `graphical_operand_set`, `graphical_network_patch`, `graphical_network_insert`, `graphical_typed_edit`, `call_parameter_set`, `call_connection_set`, `type_rename`, `type_delete`, `tag_delete`, `constant_delete`, `group_create`, `group_rename`, `group_delete`, `hardware_add`, `hardware_delete`, `safety_import_xml`, `project_simulation_enable`, `plc_compile` | `source_upsert` | **qualified** at all five points with checkpoint recovery (`su-*`); cancellation and restore on [24 October](admission-reconciliation-20261024.md#native-qualification) |
| Project + configured hardware | `hardware_plug`, `hardware_move`, `hardware_address_set`, `hardware_channel_attributes`, `hardware_cpu_memory_bytes`, `network_io_assign` | `hardware_plug` | **qualified** at all five points with checkpoint recovery (`hw-*`); the remote-I/O route's own checkpoint restore is qualified (`local.remote-io.lifecycle.20261024`) |
| Target-dependent | `to_create`, `to_parameters`, `to_delete`, `tag_set`, `constant_set`, `hardware_attributes`, `network_configure`, `block_attributes_set`, `project_attributes_set`, `connection_set` | `to_create` | **qualified** (`to-*`): unsaved interruption releases with checkpoint recovery; **refused** (qualified as held) after a save or on transport loss — the outcome fingerprint needs prepared in-memory targets that a fresh session cannot rebuild, so the admission stays held for a human decision |
| | `hmi_engineering` | — | **refused**: no eligible target (catalog `unavailable_target`); same held rule as above |

Within a route, kinds differ only in the native call between `applying` and
`mutation_applied`. Reconciliation reads the journal, checkpoint and project, never
the kind's payload, except to recompute the applied outcome. The representative
therefore qualifies the route's reconciliation, not each kind's native mutation.

### Library transactions (multi-resource)

All 18 `library_*` kinds journal per-resource checkpoints (project and each written
global library) and reconcile each resource on its own.
`library_integrate` for PLC targets is plan-only and refuses apply before any claim.

| Point | Status |
| --- | --- |
| checkpoint archive, native mutation, compile | **qualified**: `local.library_interruption.20261024`, `local.library_interruption.20261025`, `local.engineering.interruption.rerun.20261024` (project + global library released, both restored, repeated restore idempotent) |
| project save after the global library saved | **qualified as held**: global library differs from its checkpoint; restore blocked for a human decision (`local.library_interruption.20261025`) |
| global library save | **refused**: completes too quickly to interrupt reliably; the following project-save kill covers the partially saved outcome |
| two-checkpoint restore, killed and completed | **qualified** (`local.engineering.interruption.rerun.20261024`, `local.engineering.boundaries.20261024`) |

### Other admitted operations

The first five rows ran in the 24 October first batch
(`evidence/local.engineering.interruption.20261024.json`). That record is `failed`
overall because of a qualifier fault in two other cases, which the rerun fixed
(`local.engineering.interruption.rerun.20261024`); per-case outcomes are in the
[24 October table](admission-reconciliation-20261024.md#native-qualification).

| Operation | Status |
| --- | --- |
| `change_restore` | **qualified**: preserving (released), retrieving (held, partial destination) |
| `project_retrieve` | **qualified**: released, destination equals archive |
| `scratch_create` | **qualified as held**: partial project cannot be opened |
| `library_open` | **qualified**: released before receipt; held with partial working copy |
| `simulation_download` | **qualified**: held admission released after a worker crash (`artifacts/f2`) |
| `simulation_provision_scratch_credential` | **qualified** release path (`local.sim.reconcile.20261024`); refusal paths are managed tests |
| `watch_authoring_source_negative` | own reconciler (`qualification_watch_authoring_source_reconcile`); qualification-only tool |
| `watch_fixture_seed` | **refused**: qualification-only fixture seeder without a trusted reconcile adapter (`ReconciliationUnsupported`); its admission stays held |

### Mutations with their own durable operation route

`watch_table_apply`, `translation_native_apply`, `hmi_text_list_author`,
`hmi_text_list_generate_set` and the Test Suite style jobs do not use the shared
admission. They keep their own journals and `*_recover`/`*_reconcile` tools, owned
by D02, D03, X07/X08 and D06. Their native interruption stays with those owners and
is not claimed here. On 28 October D02 qualified `watch_table_apply` at its three Host
points and eight native worker-loss points ([record](review-criteria-and-watch-20261028.md)).

## Limits

- Interruption is process termination of the worker and its owned TIA, or of the
  Host. Power loss, disk faults and remote transport are not exercised.
- The save-time rule trusts the checkpoint archive's file write time; the archive's
  hash is verified, its timestamp is not.
- Fingerprints exclude generated payload bytes and unexposed vendor data; library
  revisions do not cover unexposed document content.
