# SINAMICS drives (Startdrive)

Two native routes use the optional SINAMICS Startdrive V21 Openness assembly
`Siemens.Engineering.Startdrive.dll` from the installed `PublicAPI\V21\net48`
directory. Both work on the offline project only. Without Startdrive the worker
starts and every other tool works; these routes return `UnsupportedCapability`.
`target_capabilities` on a drive hardware item reports whether the
`DriveObjectContainer` service is present.

Neither route is natively qualified yet: the code is compiled against the V21
PublicAPI only. The [Startdrive plan](../plans/expansion-plans/startdrive.md)
lists the remaining work (telegram edits, TO connection, DRIVE-CLiQ, online
access, download, activation, security, safety).

## Observe drives: `drive_observe`

`drive_observe(hardwarePath?, projectName?, maxItems, parameterOffset,
parameterNumbers?, motorDataSets?, encoderNumbers?)` reads one exact drive
`hardwarePath` (from `project_inventory`/`hardware_inspect`) or every hardware
item that exposes a `DriveObjectContainer`. It changes nothing.

Each `drives[].driveObjects[]` row has the `driveObjectNumber` and a `snapshot`
in the exact v2 snapshot format of the
[supplied drive review](../reference/drive-profile-review.md):

- `identity`: `driveId` is the local handle `DO<driveObjectNumber>`; order number
  and firmware come from the hosting hardware item's attributes; family and
  parameter set are null with a reason.
- `parameters`: name, number, arrayIndex, unit, value and metadata (parameter
  text, array length, min/max, enum labels) from `DriveObject.Parameters`.
  Values are `scalar` (CLR type and invariant text), `reference` for a BICO
  source (`DO<n>` and the exact source name) or `unavailable` with a reason.
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
