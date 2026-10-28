# Scattered single rows: text-list import, operation status, composite capture, PLCSIM diagnostics — 28 October 2026

This lane took the open rows no other lane owned: `change.plc_alarm_text_list_import` (D11),
`engineering_operation_status` (F03), `hardware_gsd_export` (X10), `simulation_provision_scratch_credential`
(L03), the Softbus PLCSIM diagnostic rows (D02, D17), `composite_engineering_capture` and
`composite_engineering_rebind_plan` (C02), and `hmi_text_list_author` and `hmi_text_list_generate_set` (X07).
Eight rows are verified (34 cells) and four carry a recorded blocker.

Native runs used owned TIA Portal V21 Update 3 instances and owned scratch projects (CPU 1511-1 PN V4.1),
built and run from frozen `git archive` copies under `D:\wp\sr<NN>` with a `source-manifest.json`. Nothing
touched a user project or a PLC. The PLCSIM run used one owned PLCSIM Advanced V8 instance on Softbus, released at the end.

## Verified

| Row | Runner | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| `change.plc_alarm_text_list_import` | [`qualify_alarm_imports.py`](../../scripts/qualify_alarm_imports.py) | `sr04` (`99a3709`) | Passed, 104/104 steps | `local.alarm_text_list.20261028` |
| `engineering_operation_status` | [`qualify_operation_status.py`](../../scripts/qualify_operation_status.py) | `sr04` Host | Passed, 136 steps, 51 Host lifetimes | `local.operation_status.20261028` with `local.watch.native.20261028` |
| `composite_engineering_capture` | [`qualify_composite_capture.py`](../../scripts/qualify_composite_capture.py) | `sr04` (`99a3709`) | Passed, 57/57 steps | `local.composite_capture.20261028` |
| `simulation_provision_scratch_credential`, `diagnostic_plcsim_write`, `diagnostic_runtime_acquisition_compare`, `diagnostic_experiment_acquire_runtime`, `diagnostic_runtime_acquisition_export` | [`qualify_plcsim_diagnostics.py`](../../scripts/qualify_plcsim_diagnostics.py) | `sr07` (`13b5290`) | Passed, 49/49 steps | `local.plcsim_diagnostics.20261028` |

**Text-list import.** The alarm runner now covers all five D11 kinds. A fresh CPU has no user text list to
export, so the runner authors the first workbook in the layout V21's importer reads. The layout comes from
the importer itself (`Siemens.Automation.DomainServices.TextLists.Impl`): sheets `TextList` (`Name`,
`ListRange`, `Comment [culture]`) and `TextListEntry` (`Parent`, `From`, `To`, `Text [culture]`), custom
properties `FileVersion` = `1` and `FileContent` = `Alarm text lists`. The first workbook had inline-string
cells and no shared-string or styles part; `ImportFromXlsx` then threw instead of returning a result. With a
shared-string table and the styles part of a native export, it creates the Decimal list `TiaMcpStates`
(importOption `None`). The next import edits one entry in TIA's own export with `Override`; only the
text-list export changes. Refusals cover an unknown option, a changed hash, a DAT file and `None` for an
existing list (`ObjectExists`). Reopen and a checkpoint restore of a second `Override` pass, as for the other kinds.

**Operation status.** `engineering_operation_status` is a Host read of the shared durable journals, so the
runner reads the journals that curated native runs retained. It copies the operation and admission stores of
18 roots, with every artifact their revisions reference: the 12 watch runs of `local.watch.native.20261028`
(worker loss at eight points, Host loss at three), `author3` and `exact1` of `local.watch.authoring.20261028`,
and the alarm and hardware runs. Each root is bound to its evidence by the report's hash, and the Host runs
only on the copy. Per copy, every operation (`prepared`, `reconciled`, `stopped`) and admission
(`change_apply`, `change_restore`, `scratch_create`, `watch_fixture_seed`, `watch_authoring_source_negative`)
reads back exactly as its newest revision. The 14 status reads the watch runs recorded live, including
`outcomeUnknown` reads right after worker loss, equal the on-disk revision of the same number. Unknown IDs
refuse as `OperationNotFound`, and uppercase and traversal IDs as `InvalidRequest`. The reads change no store
file, and a second Host on the same store returns the same records. Hosts on tampered copies refuse an edited
earlier revision and a missing revision as `JournalInvalid`, and an altered admission response as `ArtifactChanged`.

**Composite capture.** The fixture is a second CPU on one subnet, an S7 connection, PID_Compact and a
virtual positioning axis, each added with checkpoint, compile and save. A review saved with
`project_snapshot_capture` captures into one composite phase. Each domain is checked against an independent
read: the AML record reopens, the connection review's observation rows equal `connection_inspect`, the drive
capture equals `drive_observe` (no drives: native, no records), and each technology record's relationship
rows equal `technology_read`'s exposed slots. The capture changes nothing. A fresh reopen captures the same
content. After a connection delete is restored from its checkpoint, the recovered copy refuses the original
review as `ProjectMismatch` and captures the same content from its own review.

**PLCSIM provisioning and diagnostics.** PLCSIM Advanced was on TCP/IP for the
[runtime real-server gate](../plans/runtime-real-server-plan.md) for part of this lane; these rows ran after it
returned to Softbus. The fixture is an FB `DiagGate` (input `Enable`, output `Active := Enable`) whose instance DB
`Main` calls without assigning `Enable`. A saved review is the source of a diagnostic case on that instance, with
two Bool hypotheses (`follows`: Active is true with Enable true; `releases`: Active is false with Enable false),
and of its experiment.

- **Provisioning (L03).** After the download and the generated-state save, provisioning leaves the complete
  offline project state (hardware, device inventory, every program export) equal to the state just before it.
  This was the missing `AC-preserve` gate. The original card is restored, and the instance runs, stops, switches
  off and is released. A malformed ticket refuses as `InvalidSimulationTicket`.
- **Acquisition and export (D17).** Two captures associated with the case (60 rows before the write, 80 after)
  are acquired into the experiment. An independent decoder of the original plan and segments reproduces every
  row and the per-hypothesis counts: `releases` matches 30 cycles before, `follows` 40 after, with no
  contradictions. JSON, Markdown and HTML exports are schema-valid and hash-exact; the JSON export is the
  acquisition itself.
- **Write and compare (D02).** A wrong expected value returns `preconditionMismatch` and dispatches nothing. The
  write of `Enable` (false → true) and the restoring write each read back, and `sim_read` shows `Active`
  following. The comparison reports `follows` going from `notApplicable` to `matchedOnly` and `releases` the
  reverse, with verdicts unresolved.
- **Refusals:** `ArtifactUnavailable` (unknown experiment or acquisition), `RuntimeCaptureReferenceInvalid`,
  `InvalidRequest` (missing units, unknown format, comparison fields beyond `reason`),
  `RuntimeAcknowledgementRequired`, `RuntimeCaptureAssociationMismatch` (signal outside the case),
  `PlcsimCaptureInvalid` (datatype differing from the declaration), `RuntimeCaptureSessionBusy` (write while a
  capture holds the instance) and `MeasurementLineageMismatch` (an acquisition compared with itself).

## Defects fixed

- **A failed apply hid TIA's reason.** `change_apply` journaled only the exception's `Message`, which for an
  Openness call is often just "Error when calling method …". The text-list import failed that way with no
  log. The journal now keeps the exception chain with `MessageData` and `DetailMessageData` (bounded,
  `native-exception` readback); covered by `TiaMcp.HardwareEngineering.Tests`.
- **GSD export was offered everywhere.** V21 returns a `GsdExportProvider` for every device item, so
  `hardware_inspect` reported `gsdExportAvailable` everywhere. An export from a CPU display failed untyped
  (`EngineeringError`: "available only on an I-device interface"). Export and the flag now require a PROFINET
  interface whose `InterfaceOperatingMode` includes `IoDevice`; other items refuse as `UnsupportedCapability`.
- **`[Flags]` enum attributes could not take a set.** V21 refuses `IoDevice` alone on the CPU's interface
  ("cannot be set to the value 2"); it becomes an I-device as `IoController, IoDevice`. Attribute writes took
  exactly one enum name. A `[Flags]` enum now takes its names in `Enum.ToString` form; every name must exist,
  and other enums still take one name (`TiaMcp.Tests` attribute scalars).

`hardware_attributes` allowlists `InterfaceOperatingMode` for this purpose. It is not natively qualified,
because the device compile then fails (below).

## Blocked, recorded in the catalog

- **`hardware_gsd_export`.** `"IoController, IoDevice"` applies, but the device compile fails with "Error in
  I-device: There is no IO-controller/device address assignment" (runs `sr05/gs`). A new I-device needs a
  transfer area, and no change kind creates one, although V21 offers `TransferAreaComposition.Create`. The GSD
  runner ([`qualify_gsd_export.py`](../../scripts/qualify_gsd_export.py)) is ready for that kind.
- **`composite_engineering_rebind_plan`.** The plan acts only on before-phase TO hardware relationships that
  are declared (connected to one resolved module or channel). The verified `to_connect` route connects output
  cams to PLC tags only, so over the two native phases the plan reports no broken connection. Qualification
  needs a fixture TO on a module or channel, such as TO_ExternalEncoder on a TM Count. Resolved later the same
  day with a virtual axis on a TM PosInput 2 channel ([record](composite-rebind-20261028.md)).
- **`hmi_text_list_author`, `hmi_text_list_generate_set`.** Both keep `AC-native`, because their output is
  meant for a native import. No worker route imports Unified text-list YAML: V21 offers
  `HmiTextListComposition.Import`, but the worker only exports Unified text lists and imports Classic XML.

## Runs not cited

- `sr01/al`: the text-list import threw on the inline-string workbook (49 steps passed first); it led to the
  failure-detail fix and the shared-string workbook.
- `sr01/gs`, `sr04/gs`: the GSD runner found the export and enum defects above.
- `sr05/gs`: the I-device compile failure behind the `hardware_gsd_export` blocker.
- `sr01/os`: the operation-status runner on the earlier `sr01` Host, superseded by the `sr04` run.
- `sr06/ps`: the PLCSIM runner's first attempts: a relative simulator-worker path, then a wrong assumption about the
  JSON export's shape (the run had already passed provisioning, the first capture and its acquisition).
