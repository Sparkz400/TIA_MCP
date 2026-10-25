# WinCC RT Professional fixture attempt — 25 October 2026

After WinCC Professional was installed and the PC restarted, the independent
`HmiSeeder` (built from `d94df80`, SHA-256 `7491aed1…cdb490`) retried the
`classic-rt-professional` profile. Its raw report
(`D:\h3\s1\native-report.json`, SHA-256
`18f209463240e7fc78fbba16c5cb476636b780774d192ebdc7f7016dbd2408e2`) completed
with the profile `blocked`. The production-bridge qualifier was not run, because
no fixture exists.

## Observed refusal

- The catalog has exactly one match: `WinCC RT Professional`,
  `OrderNumber:6AV2 105-0xxxx-xxxx/21.0.0.0`, path
  `PC systems\SIMATIC HMI application\WinCC RT Professional`.
- `Devices.CreateWithItem` with that identifier creates a `SIMATIC PC station`
  (`System:Rack.PC`) with only its `Stationmanager`, without an HMI application item,
  and returns no error.
- For both stations, `CanPlugNew` for the same identifier returns false on the device
  and on every device item in slots 1–32. No `PlugNew` was attempted, and TIA gave
  no error message.
- WinCC Unified PC RT, created the same way in the 25 October five-profile run,
  receives its application item (`HMI_RT_1`) and HMI software.

## Installed state

Uninstall entries show STEP 7 Prof / Safety / WinCC Adv + Unified + Prof V21 Upd3,
`WinCC Prof ES Single SetupPackage V21.0 Upd3`, and
`SIMATIC WinCC Runtime Professional V21.0` with its SCADA Runtime, DataMonitor,
WebNavigator and OPC servers, installed 25 October. The Automation License Manager
is V6.2 SP5 under `D:\Arbeit\Siemens\Automation License Manager`. It has no
non-interactive license listing, so the license keys were not read.

## Status

RT Professional stays blocked. TIA accepts the catalog identifier but refuses to place
the application item, without stating a reason. The likely missing prerequisite is a
WinCC Professional engineering license, or an activation step that only the license
manager GUI shows. Nothing was retried or worked around.
