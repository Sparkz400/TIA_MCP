# Hardware, unit, project, technology and connection kinds — 28 October 2026

The [27 October audit](release-matrix-audit-20261027.md#closest-to-closable-by-owner) left the X10 hardware,
D07 software-unit, X01 project, D13 technology-object and D08 connection change kinds without a native run
that gates their own contract. Five new runners do that, in the same shape as the
[X11 PLC object mutations](wave10-native-slot-20261027.md) and [access changes](review-criteria-and-watch-20261028.md):

- **Apply:** every covered kind is prepared, its preview asserted against the request and the read tools,
  then applied with checkpoint, compile and save. `change_status` must equal the apply result.
- **Preserve:** after each change, a complete state must differ from the previous one in exactly the intended
  keys. The state is every block, type and tag-table export with every member and group path, plus the
  runner's domain (below). A TIA-coupled side effect is tolerated only where the runner names it, and is
  recorded under `sideEffects` with its before and after values.
- **Persist:** a fresh owned Portal reopens the saved project, which must show the identical state.
- **Restore:** from the reopened state, one more change per kind is applied and restored from its checkpoint
  in the applying Portal. The recovered copy must equal the pre-change state.
- **Refuse:** typed refusals must leave the complete state unchanged.

Everything ran in owned TIA Portal V21 Update 3 instances and owned scratch projects (CPU 1511-1 PN V4.1);
nothing touched a user project, a PLC or PLCSIM.

## Runs

| Runner | Domain state | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| [`qualify_software_unit_changes.py`](../../scripts/qualify_software_unit_changes.py) | each scope's name, kind, namespace preset and relations | `b17` | Passed, 105/105 steps | `local.software_units.20261028` |
| [`qualify_project_settings.py`](../../scripts/qualify_project_settings.py) | `project_properties` metadata, languages, text categories, used products | `b24` | Passed, 50/50 steps | `local.project_settings.20261028` |
| [`qualify_hardware_changes.py`](../../scripts/qualify_hardware_changes.py) | `hardware_inspect` items (complete attribute values, addresses, channels, ports, subnets), `device_inventory` rows, every PLC's program | `b27` | Passed, 127/127 steps | `local.hardware_changes.20261028` |
| [`qualify_technology_changes.py`](../../scripts/qualify_technology_changes.py) | per technology object: identity, `technology_read` connections, native XML export | `b31` | Passed, 132/132 steps | `local.technology_changes.20261028` |
| [`qualify_alarm_imports.py`](../../scripts/qualify_alarm_imports.py) | `plc_alarm_inspect` alarm classes, instance texts, supervisions, ProDiag settings, text lists | `b34` | Passed, 82/82 steps | `local.alarm_imports.20261028` |
| [`qualify_connection_changes.py`](../../scripts/qualify_connection_changes.py) | the hardware runner's state plus every `connection_inspect` row | `b39` | Passed, 76/76 steps | `local.connection_changes.20261028` |
| [`qualify_graph_changes.py`](../../scripts/qualify_graph_changes.py) | every program export (GRAPH documents checked byte for byte) | `b39` | Passed, 38/38 steps | `local.graph_changes.20261028` |
| [`qualify_project_settings.py`](../../scripts/qualify_project_settings.py), with `project_simulation_enable` | as above | `b38` | Passed, 64/64 steps | `local.project_simulation.20261028` |
| [`qualify_cax_export.py`](../../scripts/qualify_cax_export.py) | the hardware runner's state, unchanged by each export | `b40` | Passed, 12/12 steps | `local.cax_export.20261028` |
| [`qualify_cax_import.py`](../../scripts/qualify_cax_import.py) | the hardware runner's state; the imported device's hardware rows compared with the exported device's | `b48` | Passed, 35/35 steps | `local.cax_import.20261028` |
| [`qualify_vci_mappings.py`](../../scripts/qualify_vci_mappings.py) | every program export plus workspace and mapping identities; compare states and package files checked per change | `b45` | Passed, 89/89 steps | `local.vci_mappings.20261028` |

## Findings

- **Read-only projects refuse exports.** TIA refuses `Export` in a secondary project with "not permitted in a
  read-only context". `block_read` and the other export-based reads reached clients as an untyped
  `EngineeringError`. The worker now reports that refusal as `ReadOnlyProject` while a secondary project is
  open, and `project_open`'s description no longer promises export-based reads there.
- **Project attributes are read-only in V21.** Native attribute info declares Author, Copyright, Family and
  Version read-only on a project, so `project_attributes_set` can never apply. Each is refused with
  `AttributeNotWritable`. The row is recorded as unavailable on this target (`availability: unsupported`).
- **Languages touch every multilingual text.** Activating a language adds that culture's empty text item to
  the project comment and to every program export; removing it drops them. The project runner checks the
  culture in each changed export and records the effect.
- **S7-1500 racks must stay contiguous.** Deleting a module that leaves a gap fails the device compile
  ("There is a gap in the rack"), so the hardware runner deletes only the rack's last module.
- **Generated CPU security is scratch-only.** `hardware_add` with `configureScratchSecurity` is refused with
  `ScratchIsolationRequired` in a reopened project, by design, so its restore case runs before the reopen.
- **A new technology object brings generated objects.** The first PID_Compact copies the PID_Compact FB and its
  data types into the system groups; a positioning axis generates the know-how protected motion OBs
  (`MC_Interpolator`, `MC_Servo`) and their system constants. Protected objects cannot be exported, so the shared
  snapshot now compares every know-how protected block or type by identity.
- **Alarm imports always failed closed.** Two exports of an unchanged project hashed differently: every XLSX export
  draws fresh random relationship IDs, and a DAT package names its single entry after the export file and stamps
  its XML with `CreationDate`. So every supervision and alarm-class import failed with `StaleRevision`. The
  fingerprint now hashes decompressed parts, DAT entries by position without `CreationDate`, and XLSX relationship
  IDs renumbered by first use.
- **ProDiag settings start uninitialized.** A scratch project's ProDiag settings export fails ("Global settings
  are not initialized") until the first supervision import initializes them.
- **`Connection.Equals` is not reliable.** Every `connection_create` after an owner's first failed the worker's
  own preservation check: V21 reported the existing S7 connection as equal to the new TCP connection, so both rows
  dropped out of the comparison. Connections are now identified by their owner-scoped name and `LocalConnectionId`.
  A failed apply's own failure details (here, the differing rows) are now journaled; before, the apply wrapper
  replaced them with the change view.
- **TIA keeps a connection's partner counterpart in step.** Creating, changing or deleting a connection on one CPU
  does the same to the partner CPU's counterpart; the connection runner records it.
- **A GRAPH step rename renames its interface member too.** `graph_edit` replaces the step's name and its
  `G7_StepPlus` member in the block interface, and nothing else.
- **An imported S7-1500 CPU needs its password again.** `cax_export` of a secured CPU, checkpoint restore and
  `cax_import` with `RetainTiaDevice` worked natively, but the imported CPU fails the device compile: the AML carries
  no confidential-data password. An S7-300 CPU 315-2 PN/DP compiles without it, and its import reproduces the exported
  hardware rows exactly, so `cax_import` is qualified with that device. The Host also returned `cax_import`'s AML
  refusals as an untyped error; they are now typed like every other preparation refusal.
- **Drives cannot be compiled or observed yet.** A SINAMICS G120C PN exposes no device-level compile service, and the
  worker's device compile requires one on every device, so no device-compiled change (including the drive kinds) can
  run in a project that holds a drive. On the newly added drive, Startdrive Openness also cannot read the drive
  object number, so `drive_observe` returns no drive objects (reported as incomplete). The drive rows need a reviewed
  compile route and a Startdrive-configured fixture. *Update 30 October:* the device compile now compiles every device
  with a compiler service and lists the others under `notCompiled`, so device-compiled changes run in a project with a
  drive. The drive still cannot be read after compile and save. `scripts/qualify_drives.py`
  (`local.drives.probe.20261030`) added a G120C PN (6SL3210-1KE21-3AF1 V4.7.14), and a `project_compile` with save
  compiled the PLC station and listed the drive as not compiled. Before and after that compile and save, Startdrive
  answered `DriveObject.DriveObjectNumber` with "Drive object number could not be retrieved"
  (`EngineeringTargetInvocationException`). `drive_parameter_set` and `drive_telegram_set` therefore refuse at
  preparation with `EngineeringError`.
- **Online routes cannot authenticate.** Against a downloaded, credential-provisioned owned PLCSIM Advanced instance,
  going online asks for authentication, because the scratch CPU must be access-protected to compile. The online
  routes (`plc_online_compare`, `plc_station_upload`, the DB-snapshot kinds) never log in by design and refuse with
  `SimulationOnlineAuthenticationRequired`. They need a reviewed route that authenticates with the generated scratch
  credential, as `simulation_download` does.
- **VCI mappings.** `vci_mapping_add` exported the package and then failed with a `NullReferenceException`:
  `GetChildStatus` returns null for a mapped block. Mapping rows and `vci_workspace_inspect` now read it null-safe.
  Re-importing a mapped block from SCL source replaces the block, and TIA drops its mapping; in-place edits keep it.
- **An authored NodeSet crashed TIA.** Importing the minimal fixture `fixtures/opcua/machine-v1.xml` as a reference
  namespace crashed the owned TIA instance ("Unexpected exception"; the Portal was disposed). The OPC UA kinds stay
  open until a Siemens-compatible companion NodeSet is available.

## Fixtures

- **Units:** `UnitA` (namespace preset `Ns.A`) holding an SCL FB, `UnitB` relating to `UnitA`, and units created,
  related and deleted around them.
- **Project settings:** de-DE added as active and editing language and removed again; a `project_copy` of the
  saved project opened with `project_open` `readOnly`.
- **Hardware:** a second CPU 1511-1 PN V4.1 added with `configureScratchSecurity`, both X1 nodes on subnet
  `HwNet`, and two DI 16x24VDC HF modules (6ES7 521-1BH00-0AB0) in the CPU rack. The runner sets a module's
  Comment and Author, replaces its firmware V1.0 with V2.0, connects and disconnects the X1 ports of the two
  CPUs and deletes the rack's last module. TIA mirrors a module's Comment to its built-in submodule and sets the
  ports' cable defaults on connect; both are recorded side effects.
- **Technology objects:** PID_Compact 3.0 objects, a TO_PositioningAxis 10.0 created in virtual mode
  (`VirtualAxis.Mode = 1`) with TO_OutputCam 10.0 children, and two PLC output tags. The runner sets a PID limit,
  connects an output cam to a tag and disconnects it, and deletes PID objects. `technology_read` is checked for exact
  selection, a missing path, a one-item inventory and prefixed parameters.
- **Connections:** the hardware runner's two CPUs on one subnet; S7 and TCP connections between their X1 nodes.
- **GRAPH:** the authored native seed FB (`tests/TiaMcp.Graph.Tests/fixtures/native-v21/populated/seed.xml`, one step,
  one transition), exported, edited (step rename) and imported twice.
- **Alarm imports:** an SCL FB with a `Program_Alarm` and its instance DB. Each import file is a native export edited
  in one place: an alarm-class display name in the DAT, the en-US alarm text in the instance-text workbook and a
  ProDiag category name in the settings DAT. The supervision workbook is re-imported unchanged, because a scratch
  project has no ProDiag supervisions to edit.

## Verified

Catalogs `0.4.53` to `0.4.55` verify:

- `change.software_unit_create`, `_publication_set`, `_relation_set` and `_delete` (`local.software_units.20261028`);
- `change.project_languages_set`, `project_copy` and `project_open.read_only` (`local.project_settings.20261028`);
- `change.hardware_add`, `_attributes`, `_replace`, `_port_connect`, `_port_disconnect` and `_delete`
  (`local.hardware_changes.20261028`);
- `change.to_create`, `_child_create`, `_parameters`, `_connect`, `_disconnect`, `_delete` and `technology_read`
  (`local.technology_changes.20261028`);
- `change.plc_alarm_class_import`, `_instance_text_import`, `change.plc_supervision_import` and
  `_settings_import` (`local.alarm_imports.20261028`);
- `change.connection_create`, `_set` and `_delete` (`local.connection_changes.20261028`);
- `change.graph_import`, `graph_edit` and `graph_export` (`local.graph_changes.20261028`);
- `change.project_simulation_enable` (`local.project_simulation.20261028`);
- `cax_export` (`local.cax_export.20261028`) and `change.cax_import` (`local.cax_import.20261028`);
- `change.vci_mapping_add`, `change.vci_synchronize` and `change.vci_mapping_remove` (`local.vci_mappings.20261028`);
- `tag.member.crud` and `type.rename_delete`, whose kinds the X11 run (`local.plc_object_mutations.20261028`)
  already covers.

`change.project_attributes_set` is recorded as unavailable on V21 (`availability: unsupported`).

## Limits

- One owned scratch CPU 1511-1 PN V4.1 per runner (two CPUs for hardware); safety units and F-CPUs, other CPU
  families, ET 200SP/MP, HMI, drive and GSD devices are not exercised.
- Hardware: replacements only between firmware versions that keep addresses; no `acceptLoss`; port links only
  between two CPUs, with no media redundancy.
- Technology objects: PID_Compact, a virtual positioning axis and output cams; connections only from an output cam
  to a PLC tag. Real drives and encoders are not exercised.
- Alarms: `plc_supervision_import` re-imports an unchanged workbook; `plc_alarm_text_list_import` is only refused,
  because a scratch project has no user text list to export.
- Project settings: `project_attributes_set` cannot apply on V21; the copy is compared without program exports,
  which TIA refuses in a read-only project.
- Still open from these owners: the six D04 OPC UA kinds (TIA crashed on reference-namespace import with two
  different NodeSets), the D10 drive rows and the D09 online rows (see the findings above).
- Engineering only: nothing is downloaded, and no runtime, motion or alarm behavior is observed.
