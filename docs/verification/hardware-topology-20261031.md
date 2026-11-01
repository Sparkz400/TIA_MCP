# Hardware topology: ET 200SP, shared devices, MRP, address changes, profiles — 31 October 2026

The open part of the Hardware/remote I/O item (ET 200SP on a second controller, shared devices, GSD devices,
address-changing replacements, rings with media redundancy) and the deferred rows `hardware.distributed_io` and
`hardware.catalog_profiles` are implemented and natively qualified (`local.hardware_changes.topology.20261101`, below), except GSD
devices (a V21 installation boundary) and the `sharedDevice` disconnect. Drive devices stay blocked by D10.

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

## Qualification run

| Rows | Runner | Build | Result | Evidence |
| --- | --- | --- | --- | --- |
| `change.network_mrp_set`, `change.hardware_shared_device_access`, `change.network_io_assign` (sharedDevice), `change.hardware_replace` (acceptAddressChange), `hardware.catalog_profiles`, `hardware.distributed_io`, with every other hardware kind of the runner | [`qualify_hardware_changes.py`](../../scripts/qualify_hardware_changes.py) | `artifacts/hf6` (`b8885c6c`) | Passed, 460/460 steps | `local.hardware_changes.topology.20261101` |

One owned TIA Portal V21 instance and one owned scratch project, built and run from a frozen `git archive` copy; nothing
touched a user project, a PLC or PLCSIM. Every applied kind persisted across a fresh owned Portal reopen, and every new
kind was applied, refused and restored at least once, though not in every mode (see the review below):

- **ET 200SP on the second CPU:** the IM 155-6 PN ST V6.4 joined HwPeerIo with its generated server module (moved to slot 4).
  `SpDi3` at slot 1 holds the light BaseUnit and raises the update time to 2 ms; the family modules at slots 2 and 3
  continue its potential group. Two DI 8x24VDC ST were plugged, commented, replaced (6ES7 131-6BF00 V1.1 to -6BF01 V0.0)
  and deleted, each with a restore.
- **Shared device in one project:** connector 1 joined a new IO system `HwScratchIo` of the scratch CPU, and `SpDi3` was
  assigned to it in the same change. Its address moved into the scratch CPU's space, and its system constant moved tables.
- **Shared access:** `SpDi4` was set `OutsideOfProject` with one external IO controller (addresses released, constant
  removed), then restored to `PLC` on the second CPU.
- **MRP ring:** the roles were set while the members' ports were unlinked, the line was relinked, and the ring was closed;
  the first client's join and the closure were restored once. `ManagerAuto` was restored after the reopen.
- **Address-changing replacement:** DI 16x24VDC HF to DI 32x24VDC HF V2.2 was refused as `AddressReferenced` until the tag
  `HwInput` was acknowledged, then applied (map and tag impact read back) and restored.
- **GSD:** `gsdNotInstalled` (no installed I-device GSD).
- **Profiles:** 13 module capability profiles are now in `contracts/hardware-catalog-profiles.v1.json`.

The run used three discovery passes in this slot (hw3 to hw5, below). The `sharedDevice` disconnect is not exercised.

### Review: narrowed claims and the restore slice

The review of `aa87e82b..1584d389` found the catalog claims wider than the run:

- The qualified worker bundled `"profiles": []`; the contract was filled only after the run. No profile refusal and no
  `hardware_catalog_search` profile link ran natively.
- No checkpoint restore covers the shared-device assignment, `OutsideOfProject`, the `createDomain` manager or the IM 155-5 PN ST
  client.

`hardware.catalog_profiles`, `hardware.distributed_io`, `change.network_mrp_set` and `change.hardware_shared_device_access` are
therefore unverified again; `change.network_io_assign` keeps its earlier evidence, and its `sharedDevice` mode is described as
applied and persisted only. The same review tightened the worker:

- A `sharedDevice` assignment now captures every item's shared-device attributes. Inside the `sharedSubmodules` subtrees every
  I/O address must end owned by exactly the new PLC, and each named item must move its target and an address. Every other row
  stays exact. The named items must own I/O addresses, must not yet be assigned to that PLC, and pass the profile check.
- `hardware_shared_device_access` refuses a direct PLC-to-PLC move, and the subtree's inputs may take only the values the access
  gives.
- `network_mrp_set` refuses a role that would link a ring port to a non-ring port (`NetworkPrerequisite`).

`scripts/qualify_hardware_changes.py --slice restores` is the short native run that restores the claims. It runs on a build
that bundles the contract and covers:

- the second CPU with the ET 200MP and ET 200SP stations and three DI 8x24VDC ST;
- the catalog search naming the plugged module's profile, and a profile refusal of `InformationText`;
- apply and restore of the shared assignment, `OutsideOfProject`, the `createDomain` manager and the IM 155-5 PN ST client.

## Discovery passes (not evidence)

Two `--discover` passes ran on 31 October in one owned TIA Portal V21 instance each, from frozen Debug builds (no PLCSIM):

| Pass | Build | Stopped at | Finding |
| --- | --- | --- | --- |
| `artifacts/hw1/hw_9fefba84` | `artifacts/hf1` (`743daa3f`) | snapshot after adding the ET 200SP station (55 steps) | The IM 155-6 PN ST V6.4 interface's ports sit on its BusAdapter `BA 2xRJ45` (`6ES7 193-6AR00-0AA0/V0.0`, slot 127), a sibling of the head; the profile builder refused them. Fixed in `b7af3ee4` (ports named `device:items/...`). The station's rack is `Rack_0` (`System:Rack.ET200SP`). |
| `artifacts/hw2/hw_28ac766e` | `artifacts/hf2` (`b7af3ee4`) | apply of the first ET 200SP DI at slot 2 (114 steps) | V21 device compile: "The module 'SpDi' in slot 2 requires a new potential group (light-colored BaseUnit)", and "On IO device HwSpHead, the update time has changed from 128 ms to 2 ms". Before that, the ET 200SP assignment to the second CPU (with the declared generated server module, moved to slot 4) applied and compiled; its preview carries the companion as a second effect, and the BusAdapter's `Port_1`/`Port_2` take `SharedDeviceAccessType` `PLC` like the head's items. |
| `artifacts/hw3/hw_bf9b0014` | `artifacts/hf3` (`aa87e82b`) | completed (387 steps) | Shared device: the second connector's assignment compiles only with a submodule of its own ("Connected PLCs must have at least one assigned submodule as a shared device"). `SharedDeviceAccessType` is read-only; its writable inputs are `SharedDeviceAccessConfigured` (Boolean) and `SharedDeviceAssignmentTarget` (a DeviceItem). MRP: "Ring port ScratchPLC\PROFINET interface_1 [X1]\Port_2 [X1 P2R] is connected to port HwPeerCpu\...\Port_2 [X1 P2R], which is not a ring port": a role compiles only while the ports link to ring ports. The DI 32x24VDC HF replacement applied and restored with the tag impact (its submodule's device_inventory row stays). |
| `artifacts/hw4/hw_173bb7c3` | `artifacts/hf4` (`3360c85d`) | completed (454 steps) | The MRP ring closed: manager and clients in a new MRP domain `HwRing`, the first client's join and the ring closure restored, ManagerAuto restored after the reopen; a role declares `RingPort` on the interface's ports. Shared device: the second controller with `SpDi3` assigned compiles (0 errors); SpDi3 takes a start address in the scratch CPU's space (0 to 2). `OutsideOfProject` compiles (warning "The number of IO controllers outside the project with access to HwSpHead is 0" without an external controller) and releases the module's addresses. Both were refused only by the bridge's preservation rules, now widened to exactly these effects (`724e73fc`). |
| `artifacts/hw5/hw_aff707d7` | `artifacts/hf5` (`724e73fc`) | completed (460 steps) | Every topology step applied natively: the shared device (second IO system with SpDi3 assigned to the scratch CPU; SpDi3's system constant leaves HwPeerCpu's tag table), SpDi4 `OutsideOfProject` with one external controller (its constant leaves, and comes back when it returns to `PLC` for HwPeerCpu), the MRP ring, the address-changing replacement. Then the last HwPeerCpu-owned ET 200SP module's deletion resets that connector's update time to 128 ms. The remaining differences were runner expectations, fixed for the qualification run. |

Pass 2's snapshots also show what the later steps meet: `MediaRedundancyRole` is an attribute of the CPU 1511-1 PN,
IM 155-5 PN ST and IM 155-6 PN ST interface items (value `NotInRing`), the CPU 1214C interface has none, and no interface
exposes `MrpInstancesOwner`; every item of an assigned IO device (head, interface, ports, modules) carries
`SharedDeviceAccessType` `PLC` and `SharedDeviceAccessConfigured`, and its interface `NumberOfExternalIoControllers` `0`. The
worker now writes the role on the interface item when no MRP instance exists, and `hardware_shared_device_access` takes an
optional `externalIoControllers`. The runner marks the shared-device, shared-access, MRP, address-change and GSD-assignment
steps as boundaries: a `--discover` pass records a refusal, restores a failed change's checkpoint and goes on (a named MRP
domain falls back to the subnet's default domain; shared access retries with one external controller).

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
