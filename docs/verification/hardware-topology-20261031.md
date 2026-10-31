# Hardware topology: ET 200SP, shared devices, MRP, address changes, profiles — 31 October 2026

The open part of the Hardware/remote I/O item (ET 200SP on a second controller, shared devices, GSD devices,
address-changing replacements, rings with media redundancy) and the deferred rows `hardware.distributed_io` and
`hardware.catalog_profiles` are now implemented against the installed V21 PublicAPI. Nothing here is natively qualified
yet: one native run of the [hardware runner](../../scripts/qualify_hardware_changes.py) covers all of it (below). Drive
devices stay blocked by D10.

## What was built

| Item | Route | Native basis (installed V21 `Siemens.Engineering.Base`) |
| --- | --- | --- |
| ET 200SP on a second controller | Runner only: an IM 155-6 PN ST V6.4 joins the second CPU's IO system with the declared generated server module (moved to slot 4), then two catalog-selected DI 8x24VDC ST pass the family gates | Existing kinds; no worker change |
| Shared device, one project | `network_io_assign` `sharedDevice: true`: a further connector of the same interface joins another PLC's IO system | `NetworkInterface.IoConnectors`, `IoConnector.ConnectToIoSystem` (four connectors on the IM 155-6 PN ST V6.4, `local.remote-io.*`) |
| Shared device, other project | New kind `hardware_shared_device_access`: `SharedDeviceAccessType` `PLC` / `OutsideOfProject` on one IO-device module | Device item attribute `SharedDeviceAccessType` (enum `Unknown`, `OutsideOfProject`, `PLC`) |
| MRP ring | New kind `network_mrp_set`: role and domain of an interface's MRP instance; the ring is closed with `hardware_port_connect` | `MrpInstancesOwner.MrpInstances`, `MrpInstance` (`ConnectedMrpDomain`, `RingPort1/2` read-only, attribute `MediaRedundancyRole`), `Subnet.GetService<MrpDomainOwner>().MrpDomains.Find/Create`, `MrpDomain.DomainParticipants.Add` (no removal) |
| Address-changing replacement | `hardware_replace` `acceptAddressChange: true` + `acknowledgedTags` (tags of every software scope: PLC root, software units, safety units); preview `addressImpact`, readback `addressMap` and `changedReferencedTags` | `DeviceItem.ChangeType`, `Address`, `PlcTag.LogicalAddress` |
| GSD device | Runner only: the project's own exported I-device GSD, once an operator installed it, is added, assigned and compiled; else `gsdNotInstalled` | Existing kinds; `hardware_add` with the `GSD:` identifier |
| Capability profiles | `contracts/hardware-catalog-profiles.v1.json` (+ schema), captured by the runner into `profiles.json`; `hardware_catalog_search` links `profile`; attribute writes check a profile when one exists | `HardwareCatalog.Find`, `GetAttributeInfos`, `Addresses`, `Channels`, `CanPlugNew` |
| Distributed I/O workflow | The composition of the kinds above (catalog row `hardware.distributed_io`) | — |

Payloads and preservation rules: [network inspection](../guides/network-inspection.md#shared-devices),
[MRP](../guides/network-inspection.md#media-redundancy-mrp),
[address changes](../guides/network-inspection.md#address-changing-replacement),
[profiles](../guides/network-inspection.md#module-capability-profiles). The MRP API shape was confirmed against the Siemens
V21 Openness documentation page "Accessing domain settings" (attributes `IsDefault`, `ManagerOutsideOfProjectActive` on
`MrpDomain`; `MediaRedundancyRole` on `MrpInstance`). The Siemens documentation has no shared-device page; the routes above
use only what the installed metadata declares.

Offline: `tests/TiaMcp.HardwareEngineering.Tests` (309 checks, 70 new: shared-device assignment and disconnect, MRP role,
domain and domain creation, shared access, address map and tag acknowledgement, profile write checks on module and submodule
rows, versionless profile keys, contract parsing, and a guard that every attribute write of the verified hardware evidence stays
admitted by a bundled profile), `scripts/test_qualify_hardware_changes.py` (ring topology and restore order, rack lookup,
discovery mode, GSD not installed), `scripts/test_hardware_profiles.py` and `scripts/test_curate_evidence.py` (a discovery
pass is never curated as completed).

## V21 boundary: GSD devices

Ruling (controller, 31 October): GSD installation is a recorded V21 boundary; an operator-installed GSD is accepted as a
prerequisite for the GSD-device gate.

V21 Openness cannot install a GSD file. Metadata inventory of the 15 public assemblies in
`D:/Arbeit/Siemens/Portal V21/PublicAPI/V21/net48` (`scripts/Inspect-PublicApi.ps1`; `Siemens.Engineering.Base.dll`
SHA-256 `8c12b0fa70c298f1cd1221105880752ab204ed63c9f6d9dcb68833d977bcb5d0`, `.xml` `0bf27f4a…2cea43`):

- `Siemens.Engineering.HW.HardwareCatalog.HardwareCatalog` has only `Find(String)` and `Find(String filter, String typeidentifier)`.
- The only GSD members are read-only identities (`Device.IsGsd`, `GsdDevice`, `GsdDeviceItem` with `GetPrmData`/`SetPrmData`) and
  the I-device export `GsdExportProvider.Export(FileInfo, String, String)`.
- The only installer in the API is `Siemens.Engineering.MC.Drives.TechnologyExtensionInstallationProvider` (drive technology
  extensions), unrelated to GSD.

So the project's own exported I-device GSD (`local.gsd_transfer_areas.20261028`) cannot be imported through the MCP. A GSD
device becomes available only after an operator installs the file in TIA, which changes the installation's hardware catalog;
`hardware_add` then takes its `GSD:` type identifier and the existing kinds apply.

**One-time manual step (desk session, before the qualification run):** run `scripts/qualify_gsd_export.py` (or
`hardware_gsd_export` on an I-device interface) with designation `TiaMcpIDevice` and keep the exported GSDML folder; in TIA
Portal V21 open Options > Manage general station description files (GSD), select that folder and install the file. The
hardware runner's `gsd_device` step then finds the `GSD:` catalog entry named `TiaMcpIDevice`, adds it as device `HwGsd`
(checkpoint, no compile while unassigned), puts its interface on `HwNet` and assigns it to the second CPU's IO system with
the device compile. Without the installed file it records `gsdNotInstalled` and goes on; that is not a failure.

## Native runs needed

One owned TIA Portal V21 instance, one owned scratch project, a frozen `git archive` build of this branch, about 60 to 90
minutes each:

0. **Prerequisite for the GSD gate** (optional): the one-time manual GSD installation above.
1. **Key discovery** (not evidence; `scripts/curate_evidence.py` never counts a `discovery` report as completed): `python -B scripts/qualify_hardware_changes.py --host <frozen Host> --worker <frozen worker>
   --artifacts D:/Entwicklung/TIA_MCP-public/artifacts/<run-id> --run-native --discover --timeout 900`. The new steps' exact
   state deltas (ET 200SP head items and server, shared-device system constants and address owners, MRP attributes, the
   DI 32 replacement subtree) are not known before TIA shows them; `--discover` records every difference under `discovered`
   instead of failing, as the families run's earlier key-discovery pass did.
2. **Qualification**: the same command without `--discover`, after the expectations are fixed from run 1. Its
   `profiles.json` fills `contracts/hardware-catalog-profiles.v1.json` once curated.

## Discovery passes (not evidence)

Two `--discover` passes ran on 31 October in one owned TIA Portal V21 instance each, from frozen Debug builds (no PLCSIM):

| Pass | Build | Stopped at | Finding |
| --- | --- | --- | --- |
| `artifacts/hw1/hw_9fefba84` | `artifacts/hf1` (`743daa3f`) | snapshot after adding the ET 200SP station (55 steps) | The IM 155-6 PN ST V6.4 interface's ports sit on its BusAdapter `BA 2xRJ45` (`6ES7 193-6AR00-0AA0/V0.0`, slot 127), a sibling of the head; the profile builder refused them. Fixed in `b7af3ee4` (ports named `device:items/...`). The station's rack is `Rack_0` (`System:Rack.ET200SP`). |
| `artifacts/hw2/hw_28ac766e` | `artifacts/hf2` (`b7af3ee4`) | apply of the first ET 200SP DI at slot 2 (114 steps) | V21 device compile: "The module 'SpDi' in slot 2 requires a new potential group (light-colored BaseUnit)", and "On IO device HwSpHead, the update time has changed from 128 ms to 2 ms". Before that, the ET 200SP assignment to the second CPU (with the declared generated server module, moved to slot 4) applied and compiled; its preview carries the companion as a second effect, and the BusAdapter's `Port_1`/`Port_2` take `SharedDeviceAccessType` `PLC` like the head's items. |

Everything before these points matched the runner's expectations (S7-1500, S7-1200, ET 200MP families up to the
ET 200SP steps). The runner now plugs the first ET 200SP module at slot 1, where TIA gives it a light BaseUnit, and expects
the update-time change there (`216f2984`). No shared-device, MRP, shared-access,
address-change or GSD step has run natively yet.

## Still open

- I-devices under a higher-level controller; HMI and drive devices (D10).
- Absolute `%I`/`%Q` accesses in program code on a replaced module's addresses (only tags are checked).
- A second project's controller for `OutsideOfProject`; MRP domain attributes, MRP interconnection and MRPD.
- Profiles of types the run does not create, and of HSP modules not installed here.
- Native check for the discovery run: every type the run creates is an `OrderNumber:` identifier; a versionless (`System:`,
  `GSD:`) profile (the installed I-device GSD) keys on the item's own `FirmwareVersion` value, and a write whose firmware matches
  no profile of its type fails closed. Run 1 shows whether the GSD device reports a `FirmwareVersion` at all (else it is skipped
  under `profilesSkipped`).
