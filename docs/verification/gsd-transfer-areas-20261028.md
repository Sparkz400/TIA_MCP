# I-device transfer areas and GSD export — 28 October 2026

`hardware_gsd_export` (X10) was blocked: on a CPU 1511-1 PN V4.1, `hardware_attributes` makes the PROFINET
interface an I-device (`InterfaceOperatingMode` `"IoController, IoDevice"`), but the device compile then fails
with "Error in I-device: There is no IO-controller/device address assignment"
([scattered rows](scattered-rows-20261028.md)). A new I-device needs a transfer area, and no change kind created one.

## New kinds

`hardware_transfer_area_create` and `hardware_transfer_area_delete` take `hardwarePath` (a PROFINET interface in
I-device mode) and `name`. Create calls `TransferAreaComposition.Create(name, TransferAreaType.CD)`; delete calls
`TransferArea.Delete` on the area with that exact name. Both use the X10 change workflow: asserted preview, checkpoint,
device compile, save, and configured-hardware preservation. Every preservation manifest and `hardware_inspect`
(`network.transferAreas`) now list each I-device interface's transfer areas with type, direction, position number,
lengths and local and partner addresses. A change may add or remove only the named area of the selected interface.
Payloads and boundaries: [network inspection](../guides/network-inspection.md#port-topology-module-replacement-and-gsd-export).

## Native run

| Rows | Runner | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| `change.hardware_transfer_area_create`, `change.hardware_transfer_area_delete`, `hardware_gsd_export` | [`qualify_gsd_export.py`](../../scripts/qualify_gsd_export.py) | `D:\wp\ta04` (`f0ca485`) | Passed, 80/80 steps | `local.gsd_transfer_areas.20261028` |

One owned TIA Portal V21 Update 3 instance and one owned scratch project, built and run from a frozen `git archive`
copy with a `source-manifest.json`. Nothing touched a user project or a PLC.

- **Phase A.** X1 joins subnet `GsdNet`. The interface becomes an I-device with `hardware_attributes`, applied with
  `compile: false` and `save: false`; the checkpoint of the next change saves it. `TiaMcpArea`, `TiaMcpSpare` and
  `TiaMcpTemp` are created and `TiaMcpTemp` deleted. Each change compiles the device with 0 errors, and the complete
  state (`hardware_inspect`, `device_inventory`, the PLC's program exports) differs in exactly the intended keys.
  The apply readback equals `hardware_inspect`. Only the I-device interface then reports `gsdExportAvailable`, and
  `hardware_gsd_export` writes `GSDML.xml` (7,823 bytes), a well-formed ISO 15745 profile naming the designation,
  without changing the project.
- **Reopen.** A fresh owned Portal reopens the saved project: the state is identical and the export is byte-identical.
- **Phase B.** From the reopened state, a create of `TiaMcpTemp`, a delete of `TiaMcpSpare` and the switch back to
  `IoController` are each restored from their checkpoints; every recovered copy equals the reopened state, and the
  last one exports the same GSDML.
- **Refusals** (each leaves the complete state unchanged): `HardwareNotFound` (invented path), `UnsupportedCapability`
  (export or create before the I-device mode, the CPU display, the export after the switch back), `InvalidRequest`
  (blank designation, missing area name, unknown operating-mode names), `ObjectExists` (an existing area name in
  another case), `ObjectNotFound` (an absent area, an exact-name mismatch).

## Native observations

- A new CD area gets direction `LocalToPartner`, 1 byte each way, the next free output byte (0, 1, 2) and position
  numbers from 1000.
- TIA adds the system constant `Local~PROFINET_interface_1~IODevice` (`Hw_Device`) with the I-device mode and one
  `Hw_SubModule` constant `Local~PROFINET_interface_1~<area>` per area; the runner records them as side effects.
  Switching back to `IoController` removes the areas and all these constants.
- `CalculatedBandwidthForCyclicIoData` of the interface (7040 after the device compile) is not saved: a fresh open
  reads 0 until the next compile. The reopen comparison excludes it and records both values.
- The Host binds `designation` as a required parameter, so a call without it never reaches the worker; a blank
  designation is the typed `InvalidRequest`.

Trials `ta01` to `ta03` stopped on the runner's own assertions (the unnamed system constant, the missing-designation
call, the derived bandwidth on reopen) and are not cited.

## Limitations

Controller-device areas only, on an I-device without a higher-level IO controller. F-CD, PROFIsafe, shared-device
and mapping-rule areas, direction and length edits and controller assignment are not exercised. The GSDML is not
imported into another tool. Engineering only: nothing is downloaded and no PROFINET communication is observed.

Matrix: 527 of 2,031 cells open (catalog `0.4.60`; the two new rows add 12 cells).
