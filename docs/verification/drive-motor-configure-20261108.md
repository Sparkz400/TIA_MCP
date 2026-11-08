# Drive motor projection — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.drives.motor-configure.20261108`](../../evidence/local.drives.motor-configure.20261108.json) | `qualify_drive_motor.py` | passed |

Startdrive's `HardwareProjection` holds the motor of a drive data set: `GetCurrentMotorConfiguration` returns its
nameplate entries, `ProjectMotorConfiguration` writes them, and `SetMotorType` chooses a motor without a Siemens code
number. A read-only probe found that a SINAMICS G120C PN answers every motor and encoder read with
`EngineeringTargetInvocationException`, while a G220 (V6.7) reads motor data sets 0 and 1: 13 required entries (p133,
p304–p311, p322, p335, p601) and 6 optional ones.

The new change kind `drive_motor_configure` takes the drive, its drive object handle, `driveDataSet`, an optional
`motorType` and entries with their expected current values. On an owned scratch CPU 1511-1 PN V4.1 with a G220:

| Case | Result |
| --- | --- |
| p304 400 V, p305 2.6 A, p307 1.1 kW, p310 50 Hz, p311 1440 rpm, p308 0.82 on data set 0 | applied (checkpoint, device compile, save), read back through the change and `drive_observe` |
| `change_restore` of that change | the original entries (230 V, 0, 0, 50, 0, 0) again |
| Stale expected value, unknown entry, a number for a text entry, a code-number motor type, the other handle | `ExpectedValueMismatch`, `ParameterNotFound`, `InvalidRequest`, `UnsupportedCapability`, `DriveObjectHandleMismatch`; nothing changed |
| `motorType: InductionMotor` | refused by Startdrive at apply: "use PlugNew to create the motor first" (the G220's motor is a plugged hardware item); restored from the checkpoint, data set unchanged |

Not covered: the motor type on a drive that accepts it, other data sets, encoder projection, motor identification,
online access and download.
