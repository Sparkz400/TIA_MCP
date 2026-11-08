# Motion TO on a SINAMICS drive telegram — 8 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.technology.drive_telegram.20261108`](../../evidence/local.technology.drive_telegram.20261108.json) | `qualify_motion_connections.py` | passed |

The [30 October motion run](motion-connections-20261030.md) never reached a drive telegram: a G120C PN added through
Openness exposes no telegram device item, so `Connect(DeviceItem)` has nothing to take. Startdrive adds
`AxisEncoderHardwareConnectionSDRInterface.Connect(Telegram)` and `TorqueHardwareConnectionSDRInterface.Connect(Telegram)`.
`to_connect` now takes a drive's `hardwarePath`, the drive-object handle `drive_observe` reports and a `telegramType`
(`MainTelegram`, `SupplementaryTelegram` or `TorqueTelegram`; `SafetyTelegram` is refused with `SafetyBoundary`). The drive
object and its telegrams join the change fingerprint.

On the run's G120C (drive object 0 by index, telegram 1, bytes 256 in and out):

| Case | Result |
| --- | --- |
| Real `TO_SpeedAxis` 10.0 actor on `MainTelegram` | applied, compiled, read back `IsConnected` at bit addresses 2048/2048 |
| The same, replaced with `connectOption: Default` | applied, read back the same |
| Unknown `telegramType`, a channel index beside a telegram | `InvalidRequest`, slot unchanged |
| `SafetyTelegram` | `SafetyBoundary`, slot unchanged |
| Virtual `TO_PositioningAxis` torque on `MainTelegram` | V21: "Target not available for input=2048 and output=2048"; restored from its checkpoint |

The slot reports bit addresses (byte 256 reads back as 2048), so apply compares eight times the telegram's byte start. A
fresh Portal reopen showed every connection as saved. The earlier encoder, measuring-input and analog-actor cases reran
unchanged.

Not covered: supplementary and torque telegrams, sensor and encoder roles on a telegram, other drives and S120 drive objects;
nothing is downloaded and no axis moves.
