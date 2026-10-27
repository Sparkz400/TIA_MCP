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
scratch. Rerun pending.

## Next slot

1. `scripts/qualify_library_integrate_apply.py` on a fresh frozen build (with fixtures and AllowList).
2. `scripts/probe_wave_kinds.py` for the web activation path.
3. `scripts/probe_safety_kinds.py` (F-CPU, ET 200SP F-DI, `safety_fparameter_set`, `safety_printout`).
4. Typed wave 9 engineering qualification (runbook step 6) without the two existing-global edge cases.
