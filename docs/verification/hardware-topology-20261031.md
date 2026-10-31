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
| Address-changing replacement | `hardware_replace` `acceptAddressChange: true` + `acknowledgedTags`; preview `addressImpact`, readback `addressMap` and `changedReferencedTags` | `DeviceItem.ChangeType`, `Address`, `PlcTag.LogicalAddress` |
| Capability profiles | `contracts/hardware-catalog-profiles.v1.json` (+ schema), captured by the runner into `profiles.json`; `hardware_catalog_search` links `profile`; attribute writes check a profile when one exists | `HardwareCatalog.Find`, `GetAttributeInfos`, `Addresses`, `Channels`, `CanPlugNew` |
| Distributed I/O workflow | The composition of the kinds above (catalog row `hardware.distributed_io`) | — |

Payloads and preservation rules: [network inspection](../guides/network-inspection.md#shared-devices),
[MRP](../guides/network-inspection.md#media-redundancy-mrp),
[address changes](../guides/network-inspection.md#address-changing-replacement),
[profiles](../guides/network-inspection.md#module-capability-profiles). The MRP API shape was confirmed against the Siemens
V21 Openness documentation page "Accessing domain settings" (attributes `IsDefault`, `ManagerOutsideOfProjectActive` on
`MrpDomain`; `MediaRedundancyRole` on `MrpInstance`). The Siemens documentation has no shared-device page; the routes above
use only what the installed metadata declares.

Offline: `tests/TiaMcp.HardwareEngineering.Tests` (289 checks, 50 new: shared-device assignment and disconnect, MRP role and
domain, shared access, address map and tag acknowledgement, profile write checks and contract parsing),
`scripts/test_qualify_hardware_changes.py` (ring topology, rack lookup, discovery mode) and `scripts/test_hardware_profiles.py`.

## V21 boundary: GSD devices

V21 Openness cannot install a GSD file. Metadata inventory of the 15 public assemblies in
`D:/Arbeit/Siemens/Portal V21/PublicAPI/V21/net48` (`scripts/Inspect-PublicApi.ps1`; `Siemens.Engineering.Base.dll`
SHA-256 `8c12b0fa70c298f1cd1221105880752ab204ed63c9f6d9dcb68833d977bcb5d0`, `.xml` `0bf27f4a…2cea43`):

- `Siemens.Engineering.HW.HardwareCatalog.HardwareCatalog` has only `Find(String)` and `Find(String filter, String typeidentifier)`.
- The only GSD members are read-only identities (`Device.IsGsd`, `GsdDevice`, `GsdDeviceItem` with `GetPrmData`/`SetPrmData`) and
  the I-device export `GsdExportProvider.Export(FileInfo, String, String)`.
- The only installer in the API is `Siemens.Engineering.MC.Drives.TechnologyExtensionInstallationProvider` (drive technology
  extensions), unrelated to GSD.

So the project's own exported I-device GSD (`local.gsd_transfer_areas.20261028`) cannot be imported through the MCP. A GSD
device becomes available only after an operator installs the file in TIA (Options > Manage general station description
files), which changes the installation's hardware catalog; `hardware_add` then takes its `GSD:` type identifier and the
existing kinds apply. The runner records whether the catalog holds any `GSD:` entry (`gsdCatalog`).

Proposed disposition for the maintainer: GSD devices are a V21 boundary for installation; the GSD-device gate closes as
"blocked by V21 (no GSD installation API)" unless an operator-installed GSD is accepted as a prerequisite, in which case one
more runner step (add, assign, compile) qualifies it.

## Native runs needed

One owned TIA Portal V21 instance, one owned scratch project, a frozen `git archive` build of this branch, about 60 to 90
minutes each:

1. **Key discovery** (not evidence): `python -B scripts/qualify_hardware_changes.py --host <frozen Host> --worker <frozen worker>
   --artifacts D:/Entwicklung/TIA_MCP-public/artifacts/<run-id> --run-native --discover --timeout 900`. The new steps' exact
   state deltas (ET 200SP head items and server, shared-device system constants and address owners, MRP attributes, the
   DI 32 replacement subtree) are not known before TIA shows them; `--discover` records every difference under `discovered`
   instead of failing, as the families run's earlier key-discovery pass did.
2. **Qualification**: the same command without `--discover`, after the expectations are fixed from run 1. Its
   `profiles.json` fills `contracts/hardware-catalog-profiles.v1.json` once curated.

## Still open

- I-devices under a higher-level controller; HMI and drive devices (D10).
- A second project's controller for `OutsideOfProject`; MRP domain attributes, MRP interconnection and MRPD.
- Profiles of types the run does not create, and of HSP modules not installed here.
