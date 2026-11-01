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

## V21 boundary — 1 November 2026

The license hypothesis above is superseded. On 1 November the operator created a WinCC RT
Professional station with a screen in the TIA Portal UI and saved the project; its
`System/PEData.plf` contains "RT Professional" and `Screen_1`, so the product is installed
and usable in the editor. An owned copy of that project, opened through Openness, shows
`PC-System_1` with only `SIMATIC PC station` and `Stationmanager`: `hardware_inspect` of the
rack lists the same two items, and `hmi_inspect` finds no HMI device. Together with the
25 October refusal (`CanPlugNew` false in every slot), V21 Openness neither creates nor
exposes WinCC RT Professional.

Retained locally (main checkout, ignored `artifacts/`): the owned copy made by `project_copy`
(`artifacts/rtp2/copies/r_c5c17770/Scratch/Scratch.ap21`; the operator's original was opened
without saving), the `device_inventory` view (`artifacts/rtp3/rtp-inventory.json`) and the
`hardware_inspect` reads of the PC station (`artifacts/rtp4`), all on build `10763a35`.

This is a recorded V21 boundary, not a missing prerequisite: the X07 ledger entry no longer
asks for an RT Professional target, and no release-matrix row is added. Catalog texts say
"not exposed by V21 Openness". If a later Openness version exposes the runtime, the profile
reopens as its own work item.
