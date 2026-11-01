# Drive objects by container index: native runs of 1 November 2026

Two owned-scratch runs of `scripts/qualify_drives.py --run-native` on CPU 1511-1 PN V4.1 with an added SINAMICS
G120C PN (6SL3210-1KE21-3AF1, firmware 4.7.14), not connected to the CPU's IO system, each from a frozen Debug build:

| Run | Build | Result |
| --- | --- | --- |
| `artifacts/dvn` | `f8dd1e64` (dvx) | parameter set passed; every telegram prepare refused (defect, fixed below) |
| `artifacts/dvo` | `027212a2` (dvy) | 26/26 steps, no boundary: `local.drives.index.20261101` |
| `artifacts/dv3` | `bfa633fe` (dv2) | 28/28 steps, Host exit 0, owned TIA exited: `local.drives.index-status.20261101` |

The second run did not call `change_status`, which `drive_parameter_set` and `drive_telegram_set` bind, so
`scripts/test_contracts.py` kept them unverified. The third run adds a `change_status` check after each apply (both
report the applied stages) and the owned TIA's exit after detach and Host close (279 ms). Catalog 0.4.95 verifies
all three rows with both records; the 15 D10 cells are bound.

## Finding

Startdrive answers `DriveObject.DriveObjectNumber` of this drive with `EngineeringTargetInvocationException` "Drive
object number could not be retrieved", before and after compile and save, and whether or not the drive was commissioned
in the Startdrive UI. `drive_observe` used to drop the drive object, so `driveObjects` was empty and every D10 kind
was blocked (`local.drives.probe.20261030`). A probe worker that only skipped the number read the same object: 1,998
parameters up to the item budget and its telegrams. The block was the design's reliance on the number, not V21.

## Change

- `drive_observe` keeps `DO<driveObjectNumber>` when every drive object of a drive has a readable number. Otherwise
  every object of that drive reports `driveObjectNumber: null`, `driveObjectIndex` (its zero-based position in
  `DriveObjects`), `driveId` `DO@<index>` and a warning naming the native error; one drive never mixes the two.
- `drive_parameter_set` and `drive_telegram_set` take exactly one of `driveObjectNumber` or `driveObjectIndex`; the
  handle the drive does not use refuses with `DriveObjectHandleMismatch`. Prepare and apply resolve the object again,
  and its identity (hardware path, drive object count of the container, order number, firmware, parameter count) is
  part of the change fingerprint, so apply refuses with `StaleRevision` when it changed.
- The rules are in `src/TiaMcp.Openness.V21/DriveObjectRules.cs`, checked by `tests/TiaMcp.HardwareEngineering.Tests`.

## Results (`local.drives.index.20261101`, `local.drives.index-status.20261101`)

- **Observe:** the one drive object as `driveObjectIndex` 0 (`DO@0`) with the number warning; parameters paged and
  filtered by number; `MainTelegram` 1 (4/4 input/output bytes, no address ranges off the IO system).
- **Parameters:** `p1082[0]` (`System.Single`) 1500 to 12.5 prepared by index; refused: a stale expected value
  (`ExpectedValueMismatch`), `p99999` (`ParameterNotFound`), the number handle (`DriveObjectHandleMismatch`) and `p971`
  (`SafetyBoundary`). Applied with checkpoint, device compile (the PLC station compiled, the drive under `notCompiled`)
  and save, read back through `drive_observe`, restored from its checkpoint to 1500.
- **Telegrams:** the main telegram 1 to 20 prepared by index; refused: a safety telegram (`SafetyBoundary`) and stale
  `expectedTelegrams` (`ExpectedValueMismatch`). Applied with checkpoint, device compile and save; the readback shows
  telegram 20 (12/4 bytes), no address shift and no conflicts; the checkpoint restore brought back the exact rows.

## Defects found

- **String-typed null (first run):** `DriveTelegram` built `unknownReason` (and an address `direction`) as a C# string
  that could be null. Newtonsoft stores that as a String-typed `JValue`, which prints as `null` but never
  `DeepEquals` the null of the echoed `expectedTelegrams`, so every telegram prepare refused with
  `ExpectedValueMismatch` although the rows were identical. `027212a2` emits real JSON nulls
  (`DriveObjectRules.Text`), with a regression check.
- **Owned TIA left running (second run):** after the last restore the Harness detached (`tia_detach` returned), but
  the worker's exit was not verified within the Host's 120 s and the Openness-started TIA kept running until it was
  closed by hand. `qualify_drives.py` now captures the owned TIA process after `tia_start`, and the Harness requires
  that it exits after detach and Host close; otherwise the run fails with the wait evidence (`tiaProcessExit`). In the
  third run the Host exited with 0 and the owned TIA 279 ms after; why it lingered in the second run is not known,
  and the runner cannot close it (no kill route by design).

## Not covered

Drives with readable numbers or several drive objects, motor and encoder configuration entries, BICO references
(their owner stays unresolved on index-addressed drives), telegram address ranges (the drive is not on an IO system,
so the address-shift report and overlap check ran on empty ranges), supplementary and additional telegrams, several
parameters per change, online values, upload, download and RAM-to-ROM.
