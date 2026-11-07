# SINAMICS drives (Startdrive)

Two native routes use the optional SINAMICS Startdrive V21 Openness assembly
`Siemens.Engineering.Startdrive.dll` from the installed `PublicAPI\V21\net48`
directory. Both work on the offline project only. Without Startdrive the worker
starts and every other tool works; these routes return `UnsupportedCapability`.
`target_capabilities` on a drive hardware item reports whether the
`DriveObjectContainer` service is present.

`drive_observe`, `drive_parameter_set` and `drive_telegram_set` (the main
telegram number change) are natively qualified on one SINAMICS G120C PN
(6SL3210-1KE21-3AF1, firmware 4.7.14) in an owned scratch, offline values only
([record](../verification/drives-index-20261101.md)). The
[Startdrive plan](../plans/expansion-plans/startdrive.md) lists the remaining
work (telegram size and PKW edits, TO connection, DRIVE-CLiQ, online access,
download, activation, security, safety).

## Observe drives: `drive_observe`

`drive_observe(hardwarePath?, projectName?, maxItems, parameterOffset,
parameterNumbers?, motorDataSets?, encoderNumbers?)` reads one exact drive
`hardwarePath` (from `project_inventory`/`hardware_inspect`) or every hardware
item that exposes a `DriveObjectContainer`. It changes nothing.

Each `drives[].driveObjects[]` row has the `driveObjectNumber` and a `snapshot`
in the exact v2 snapshot format of the
[supplied drive review](../reference/drive-profile-review.md). Startdrive cannot
read the number of some drives (a G120C PN V4.7 answers "Drive object number could
not be retrieved"). On a drive where any number cannot be read, every row reports
`driveObjectNumber: null` and `driveObjectIndex`, its zero-based position in the
drive's `DriveObjects`, and a warning names the native error; one drive never mixes
the two handles.

- `identity`: `driveId` is the local handle `DO<driveObjectNumber>`, or
  `DO@<driveObjectIndex>` on a drive addressed by index; order number
  and firmware come from the hosting hardware item's attributes; family and
  parameter set are null with a reason.
- `parameters`: name, number, arrayIndex, unit, value and metadata (parameter
  text, array length, min/max, enum labels) from `DriveObject.Parameters`.
  Values are `scalar` (CLR type and invariant text), `reference` for a BICO
  source (`DO<n>` and the exact source name; the owner stays null with a reason when
  its number cannot be read) or `unavailable` with a reason.
  `access` is always `unknown`: Openness does not expose per-parameter write access.
- `telegrams`: type, number, input/output size in bytes and the telegram's
  `Address` ranges (start byte, length in bits). PLC ownership is not resolved.

`maxItems` (1..2000) bounds all rows. Parameters page per drive object: pass the
reported `nextParameterOffset` as `parameterOffset`. `parameterNumbers` keeps
only those exact numbers. `parametersComplete` is true only for an unfiltered,
unpaged, untruncated capture.

Motor and encoder configuration entries (`HardwareProjection.GetCurrentMotorConfiguration`
/ `GetCurrentEncoderConfiguration`) are read only for the dataset and encoder
numbers you list, because Siemens does not document their index ranges.

To compare two states, put one snapshot in `before` and another in `after` of a
`drive-profile-review-request.v2` with the same `hardwarePath`, upload it and call
`drive_profile_review`.

## Drive inventory: `drive_inventory`

`drive_inventory` joins what several reads report into one view of the open project, and changes nothing:

- every drive object from `drive_observe`, with each telegram's number and input/output byte ranges
- the PLC tags whose addresses overlap each range (`software_inspect` of every PLC); Startdrive does not say which PLC owns a telegram, so a match in several PLCs is ambiguous
- the technology objects whose resolved hardware references reach the drive (`technology_read`)

Names are never used to join. `coverage` reports each source separately. `noDrivesConfirmed` is true only when Startdrive read the whole project and found no drive. An unavailable, truncated or partial read is never reported as "no drives". `reconciliation` lists telegram ranges without tags, drives without a technology object, technology objects without a discovered drive and duplicate display names. `blockers` lists every gap or ambiguity that a dependent write (safety tags, servo calls) must not proceed through; `writeReady` is false while any exists. A drive whose drive object numbers Startdrive cannot read uses index handles (`DO@<index>`) and still counts as complete. Natively checked with two G120C PN drives on a CPU's IO system (`local.drives.inventory.20261107`).

## Plan servo calls: `servo_call_plan`

`servo_call_plan` plans, never applies, one caller FB that calls an existing servo wrapper FB once per reviewed drive: one multi-instance and one fault Bool output per drive, wired to that drive's status and control tags from `drive_inventory`. Name the wrapper's own parameters in `parameters` (`status`, `control`, `reset`, `ss1`, `fault`); each is checked against the wrapper's native interface section and type, and nothing is guessed from names. `resetExpression` and `ss1Expression` are applied exactly as given, as confirmed design. The plan is blocked by any `drive_inventory` blocker, a missing or mistyped parameter, not exactly one status and one control tag per drive, a drive or name listed twice, and any other block that already writes a selected drive's control tag. Remove or replace that write explicitly first. When `writeReady`, `change` holds the `source_upsert` payload for `change_prepare`; apply it with `compile: true`. Natively checked with two G120C PN drives and a synthetic wrapper (`local.drives.servo-calls.20261107`). A clean compile is engineering evidence, not machine safety acceptance.

## Write parameters: `drive_parameter_set`

A prepared change (`change_prepare` → `change_apply` → `change_status` /
`change_restore`) that writes scalar parameters on one drive object of the
offline project:

```json
{ "hardwarePath": "/projects/P/devices/Drive/items/CU",
  "driveObjectNumber": 2,
  "parameters": [
    { "name": "p1082[0]", "expected": { "kind": "scalar", "type": "System.Single", "text": "1500" }, "value": 1800 } ] }
```

- Name the drive object by exactly one handle, the one `drive_observe` reports:
  `driveObjectNumber`, or `driveObjectIndex` on a drive addressed by index. The
  other handle refuses with `DriveObjectHandleMismatch`. Prepare and apply resolve
  the object again; its identity (hardware path, drive object count, order number,
  firmware, parameter count) is part of the change fingerprint, so apply refuses
  with `StaleRevision` when it changed. `drive_telegram_set` takes the same handle.
- `expected` is the value object exactly as `drive_observe` reported it. Prepare
  and apply refuse any difference before the first write.
- `value` is a JSON scalar converted to the parameter's current CLR type; enums by name.
- Refused: BICO interconnections, unavailable values, Safety Integrated
  parameters (p9300..p10999) and commissioning, reset, save and motor
  identification parameters (p0009, p0010, p0970..p0972, p0976, p0977, p1900,
  p1910, p1960, p3900).
- Apply writes every value, then reads each back; a difference fails with
  `MutationVerificationFailed`. A read-only parameter fails at the native setter.
  Both leave the change in recovery with the usual checkpoint archive.
- Startdrive may recalculate dependent parameters. Only the listed parameters are
  read back; observe before and after and compare with `drive_profile_review`.
- `compile=true` compiles all project devices. Nothing is written online,
  downloaded or copied RAM-to-ROM.
