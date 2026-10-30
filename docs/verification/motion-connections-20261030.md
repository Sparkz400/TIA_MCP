# Motion connections, software-unit copy and library compile with a drive: native runs of 30 October 2026

Three owned-scratch runs on CPU 1511-1 PN V4.1 (fail-safe access off), each from a frozen Debug build under
`artifacts/um*` and curated from a clean worktree at its build commit:

| Evidence | Runner | Build | Result |
| --- | --- | --- | --- |
| `local.technology.motion_connections.20261030` | `scripts/qualify_motion_connections.py` | `79245419` (um5) | 44/44 steps |
| `local.software_units.lifecycle.20261030` | `scripts/qualify_software_unit_changes.py` | `43749ff6` (um6) | 161/161 steps |
| `local.library.drive_compile.20261030` | `scripts/qualify_library_drive_compile.py` | `eb127b79` (um3) | 9/9 steps |

## to_connect roles and targets

Fixture: TM PosInput 2 V2.0 (channel 0 in MotionControl mode), AQ 2xU/I ST, and a SINAMICS G120C PN (6SL3210-1KE21-3AF1
V4.7.14) put on one subnet and IO system with the CPU; a virtual TO_PositioningAxis 10.0 compiled first.

Applied, compiled, saved, read back through `technology_read`, and equal after a fresh Portal reopen:

- encoder (TO_ExternalEncoder 10.0) on the TM PosInput item: `Connect(DeviceItem)`;
- the same slot replaced through `Connect(DeviceItem, DeviceItem, ConnectOption.Default)` (input and output item both
  the TM PosInput item) and through `Connect(Int32 0, Int32 0, ConnectOption.Default)`, each verified by
  `InputModule`/`OutputModule` or `InputAddress`/`OutputAddress`;
- the replacement on the TM channel was applied and restored from its checkpoint to the previous slot state.

Refused by V21 (exact errors; each failed apply restored from its checkpoint):

- measuring input (TO_MeasuringInput 10.0) on the TM PosInput item with channel index 0 and on its channel:
  `MeasuringInputHardwareConnectionProvider.Connect`: "Target not available for input=0 and output=-1";
- TO_SpeedAxis 10.0 actor on the AQ 2xU/I ST analog output channel 0, with the default actor type and with
  `Actor.Type = 0` on a virtual axis: "Target not available for input=-1 and output=-1";
- encoder on the DB member `MoEncDb.Tel` (a global DB holding a `PD_TEL83` member): "No valid assignment for
  Encoder(MoEncoder)";
- `Connect` on a connected slot: "Already connected: Encoder(MoEncoder) to Encoder_1(MoEnc)" (build eb127b79). `to_connect`
  now disconnects and connects within one checkpoint and compile (`replacesConnection`).

Not reachable on this fixture: the G120C PN added through Openness and assigned to the IO system exposes no telegram
device item (rack, head, PM G120C, PROFINET interface and ports, drive item; none carries I/O addresses), so actor and
torque on a drive telegram were not attempted. This is the same root as the D10 blocker: Startdrive cannot read
`DriveObject.DriveObjectNumber` of a drive that was not commissioned in its UI.

Typed refusals before any native call, each leaving the slot unchanged: a measuring input on a tag and an actor with
`connectOption` on a single module or with `channelIndex` (`UnsupportedTarget`); a measuring input with an output
address, an actor with one address and an encoder with only an input address (`InvalidRequest`).

Route findings fixed during the runs: V21 cannot export an inconsistent technology object ("Inconsistent blocks and
PLC data types (UDT) cannot be exported"), so the `to_` kinds skip the XML backup for one; the first motion object adds
`MC_Servo`/`MC_Interpolator`, which stay inconsistent without compile, so an object created with `compile=false` before
any compiled motion object fails the change fingerprint (`FingerprintUnavailable`).

## Software units

Rename and creation from a unit master copy (the copy route) ran with apply, refusal, fresh-Portal persistence and
checkpoint restore in `local.software_units.lifecycle.20261030`. A unit master copy's `ContentDescriptions` list only
the unit (`UnitT`, `Siemens.Engineering.SW.Units.PlcUnit`). The copy of an empty unit keeps the namespace preset and the
outgoing relations. In the same PLC, `MasterCopyMode.Rename` also renames members: build 34d77f1f created `UnitA2`
holding `UnitFb_1 (FB3)`, a renamed copy of `UnitFb`, and the default tag table `_3` became `_4`. The
`createdUnitMembers` readback now lists such renames. In the qualified run, the same copy fails after the compile with
`MutationVerificationFailed` ("MasterCopyMode.Rename renamed blocks or PLC data types of the copy (UnitFb_1)") and is
restored from its checkpoint. Refusing inside the mutation (build 79245419) had left `UnitFb_1` uncompiled, and
`change_restore` then answered `FingerprintUnavailable`. The empty-unit copy reports only its renamed default tag table.

## Library compile with a drive

`library_create_master_copy` in a project holding a G120C PN compiled the PLC station and listed only the drive under
`notCompiled`. The plan, the applied change and the original lock-manifest bytes validate as library observations,
with `compilerNotCompiled` naming the drive and `compilerScopeBinding: prepared-device-paths`.
