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

## Verified

Catalog `0.4.53` verifies:

- `change.software_unit_create`, `_publication_set`, `_relation_set` and `_delete` (`local.software_units.20261028`);
- `change.project_languages_set`, `project_copy` and `project_open.read_only` (`local.project_settings.20261028`);
- `change.hardware_add`, `_attributes`, `_replace`, `_port_connect`, `_port_disconnect` and `_delete`
  (`local.hardware_changes.20261028`);
- `change.to_create`, `_child_create`, `_parameters`, `_connect`, `_disconnect`, `_delete` and `technology_read`
  (`local.technology_changes.20261028`);
- `tag.member.crud` and `type.rename_delete`, whose kinds the X11 run (`local.plc_object_mutations.20261028`)
  already covers.

`change.project_attributes_set` is recorded as unavailable on V21 (`availability: unsupported`).

## Limits
