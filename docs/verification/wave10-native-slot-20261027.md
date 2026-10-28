# Wave 10 native slot — 27/28 October 2026

One TIA slot (about 2.5 h, CPU 1511-1 PN V4.1 scratch projects, TIA Portal V21) on branch
`claude/impl-wave10`. Everything ran against owned Portal processes and owned scratch projects;
nothing touched a user project, a physical PLC or PLCSIM.

## Setup lessons

- A frozen copy of the worker (`D:\wp\bN\worker`) is a new Openness application. Without an
  AllowList entry TIA waits on its access prompt and `tia_start` times out after 900 s
  (`TimeoutOutcomeUnknown`). Register each frozen copy with
  `scripts/Register-DevOpennessWorker.ps1 -WorkerPath <exe>` before the run.
- Typed graphical edits read `fixtures/typed-graphical` relative to the worker binaries. A frozen
  worker needs that folder copied beside it, or creation refuses with `CreationProfileUnavailable`.
- Scratch projects under a worktree's `artifacts/` exceed TIA's 143-character project path limit
  (`ProjectPathTooLong`); use a short output root such as `D:\wp`.

## Wave kinds probe

`scripts/probe_wave_kinds.py`, evidence [`local.wave-kinds.probe.20261027`](../../evidence/local.wave-kinds.probe.20261027.json).
Exploratory: each apply has its own readback, but there are no reopen, recovery or interruption gates.

| Kind or tool | Native observation |
| --- | --- |
| `certificate_create` | Applied; public readback equals the request (subject, SAN, validity) after the fix below |
| `certificate_assign` | `PlcCommunicationCertificate` assigned to the new certificate and back |
| `certificate_delete` | Deleted the unassigned certificate |
| `umac_role_set` | `create`, `grant` (device right), `assign`, `unassign`, `revoke` all applied |
| `vci_workspace_create` / `_delete` | Created and deleted a workspace; a root under the artifact store refuses `VciUnsafeRoot` |
| `instance_lookup` | Found the single-instance DB and the call site from both cross-references and SimaticML |
| `webserver_page_set` | Refuses `WebServerNotActivated` on a scratch CPU (web serving off) |

Fixed from these runs:

- **Certificate validity.** TIA reads `CertificateTemplate.ValidFrom`/`ValidUntil` as local
  wall-clock time whatever the `DateTime` kind: a 22:16Z request came back as 21:16Z (CET) and its
  2027 end as 20:16Z (CEST). The worker now passes local time, and the readback is journaled
  before its checks so a refusal keeps the observed values.
- **Web activation.** No kind could switch web serving on, so page settings could only be probed
  for their refusal. `webserver_page_set` gained `webServerActive: { expected, value }`. Not yet
  run natively.

## Typed wave 9 families: native capture

Runbook: [typed-wave9-families-runbook.md](typed-wave9-families-runbook.md). Evidence
[`local.typed.wave9-capture.20261027`](../../evidence/local.typed.wave9-capture.20261027.json)
(summary outcome `failed`, because each of the three runs stopped at one source; the per-source
engineering checks passed).

- 63 independently authored references captured from the SHA-bound standard baseline:
  DInt/Real compare (eq..ge), MOVE Int/DInt/Real, ADD/SUB/MUL/DIV Int/DInt/Real, TONR
  (multi-instance and `Tgp_GlobalTonr`), R_TRIG/F_TRIG multi-instance; LAD and FBD.
- 60 profiles staged, audited (`fixtures/typed-graphical/wave9-candidates-20261027.audit.json`,
  no findings) and promoted into `fixtures/typed-graphical/profiles.v1.json`.
- **R_TRIG/F_TRIG global storage is not capturable from SCL.** `DATA_BLOCK "x" "R_TRIG"` (quoted
  or not) is generated, passes the post-generation check and is gone after compile and save. The
  6 global-storage sources stay uncaptured; the edge profiles carry no `globalInstanceType`.
- **`source_upsert` now rechecks its declared blocks after compile and save**; before, it reported
  `applied` without the block. Run 3 confirmed the new check (`BlockNotFound`).
- **Protected system FBs.** TIA copies a know-how protected `R_TRIG` FB into
  *System blocks/Program resources* on first use. Owned snapshots now record such blocks by
  identity, and reference capture treats them as generated.
- The extractor accepts an identical re-capture of a source from a different change (recorded as
  `confirmingCaptures`); the audit takes several capture reports of one CPU.

Engineering (four gates) and runtime qualification of the 66 cases have not run. The 2
`existingglobal-rtrig`/`-ftrig` cases will refuse until global edge storage has a profile.

## `library_integrate` combined apply

`scripts/qualify_library_integrate_apply.py` with `--enable-library-integrate-apply`:

1. `D:\wp\c\i4e087a`: authoring passed; the first install refused `CreationProfileUnavailable`
   (frozen worker without fixtures, see setup).
2. `D:\wp\c\i37f9a5`: `library_instantiate` applied, then the typed step refused
   `InstanceCreationProfileUnavailable`: the production route passed no captured-storage context
   and the shipped templates do not cover the component FB's structured persistent declarations.
   The change was left `recovery_required` in the scratch.

Fixed: production combined apply now passes the natively captured storage profile, still bound by
exact installed FB/UDT content equality, with an offline project instead of the qualification
scratch.
3. `D:\wp\c\iaf4c3a`: `library_instantiate` applied, then the typed step's prepare refused
   `CapturedStorageProfileUnavailable` (complete reference callee/type content differs). The
   runner's scratches kept en-US active next to de-DE, so every `MultilingualText` in the installed
   FB (4) and UDT (2) exported an extra empty `<Culture>en-US</Culture>` item, and every later
   ID shifted. With those items removed and IDs renumbered, both exports equal the captured
   component04 references exactly; nothing else differs.

Fixed: the runner now makes each scratch exactly de-DE, like the qualification Host: a second
`project_languages_set` removes every other active language. The content binding is unchanged.
Rerun pending.

## Second slot — 28 October

Frozen build `D:\wp\b6` (commit 9ff587c, AllowList entry, `fixtures/typed-graphical` beside the
worker), owned scratch CPU 1511-1 PN projects only.

| Run | Outcome | Evidence |
| --- | --- | --- |
| `scripts/qualify_plc_object_mutations.py` | 369/369 steps; 22 X11 kinds verified (applied with exact preview and complete-export diff, refused per family, reopened in a fresh Portal, restored from their own checkpoint) | `local.plc_object_mutations.20261028` |
| `scripts/validate_milestones.py --skip-simulation` | 11 changes with `change_status`, exact readback, fresh-Portal reopen; 8 restores with recovered reopen; `graphical_operand_set` verified, STL added to `source_upsert` | `local.milestones.reopen.20261028` |
| `scripts/qualify_library_integrate_apply.py` | passed after the de-DE-only scratch fix: new and existing FBD FC callers installed, OB/LAD refused, Host without the flag refused | `local.library_integrate.combined_apply.20261028` |
| `scripts/probe_wave_kinds.py` | `webServerActive` together with system pages applied; switching web serving off failed readback because TIA also clears `EnableSystemWebPages` (now previewed) | retained `D:\wp\a\wp_c236a724` |
| `scripts/prepare_crossunit_capture_archive.py` | first native `software_unit_relation_set` and `software_unit_publication_set`, each with readback; archive `artifacts/crossunit-prep/838e0c54….zap21` | receipt next to the archive |
| `capture_typed_routes_owned.py` crossUnit / crossNamespace | both passed with persistence (2 and 3 sources) | `artifacts/cap-crossunit`, `artifacts/cap-crossns` |

Matrix: bound cells 441 → 585, open 1,658 → 1,514.

Findings:

- **`change_restore` after a failed compile.** A change whose compile fails leaves an inconsistent
  user block; the recovery guard cannot fingerprint it and refuses (`FingerprintUnavailable`) by
  design. The checkpoint stays retained. The X11 runner records this refusal.
- **R_TRIG/F_TRIG instance DBs.** TIA deletes an unreferenced R_TRIG/F_TRIG instance DB at compile,
  whether created from SCL or with `CreateInstanceDB`, even after an R_TRIG use (`D:\wp\w9e`, a
  scratch experiment). `instance_db_create` now refuses them; global edge storage needs its call in
  the same compile.
- **Safety probe blocked.** An F-CPU added to a scratch fails compile: "At least one local user must
  have the following runtime right: Full access including fail-safe". Scratch provisioning
  deliberately never grants that right, so `safety_fparameter_set`/`safety_printout` were not run.
- **Combined apply content.** A scratch that keeps en-US next to de-DE adds empty en-US text items to
  every export, which fails the captured-storage content binding; the runner now makes its scratches
  de-DE only.

Still open natively: the safety kinds (blocked as above), web deactivation after the preview fix,
typed wave 9 builder qualification (66 cases), cross-scope call profiles (extraction pending) and
`webserver_user_pages_generate`, `certificate_import`, `graph_edit` → `graph_import`.

## Typed qualification — 28 October

`scripts/qualify_typed_graphical.py --engineering-only`, worktree Debug builds, each case in a fresh owned scratch.

| Batch | Result |
| --- | --- |
| Wave 9, `w9q` | 14 passed, then `typed-sub-int-lad` refused: TIA rejects the `Card=2` it exports on SUB/DIV |
| Wave 9, `w9q2`/`w9q3` | SUB reference imported after dropping `Card`; the reader then saw the Card-less network as opaque (fixed: SUB/DIV match ignores `Card=2`) |
| Wave 9, `w9q4` | 8 passed, then `typed-rtrig-lad` stopped exporting the protected R_TRIG system FB (fixed: identity-only snapshot) |
| Wave 9, `w9q5` | 40 passed, including all 12 SUB/DIV cases |
| Cross-scope, `xsqo` | 4 passed: cross-namespace and cross-unit numeric FC calls, LAD and FBD |

Wave 9 total: 62 of 66 cases pass all four engineering gates; the 4 `existingglobal-rtrig/ftrig`
cases have no profile. Evidence: `local.typed.wave9.engineering.20261028` (w9q5),
`local.typed.wave9.engineering-early.20261028` (w9q, w9q4; outcome failed because both batches stopped),
`local.typed.crossscope.engineering.20261028`.

## network_configure — 28 October

`network_configure` became a hardware-rule kind (configured-hardware preservation, fresh-session
reconciliation). A milestones rerun on build ba861fc (`D:\wp\b7`) passed with no failures and the
stage `configured_hardware_preservation_verified` on the offline address change, plus change_status,
fresh-Portal reopen, restore and recovered reopen: `local.milestones.network.20261028`. The row is
verified; bound cells 585 → 591.
