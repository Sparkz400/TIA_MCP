# Hardware refusals and topology paths natively — 2 November 2026

The audit follow-up "Hardware, not native" ([TODO](../TODO.md#release-mechanics-and-policy)) listed hardware refusals and
paths that had passed only offline: the MRP port-link refusal (ring and non-ring ports), the PLC-to-PLC shared-access refusal,
the `sharedDevice` disconnect, the channel and versionless capability-profile refusals, an address-changing replacement that
moves the start address, a second project's controller for `OutsideOfProject`, and the GSD device. A new slice of the
[hardware runner](../../scripts/qualify_hardware_changes.py), `--slice refusals`, runs all of them in owned scratches. It found
one product defect, fixed below: no `sharedDevice` disconnect could compile.

| Evidence | Runner | Build | Result |
| --- | --- | --- | --- |
| `local.hardware_changes.refusals.20261102` | `qualify_hardware_changes.py --slice refusals` | `artifacts/rf3` (`45f7155d`) | Passed, 199/199 steps (`rq2/hw_b7f35015`) |

One owned TIA Portal V21 instance per project (nothing downloaded, no PLCSIM), built and run from frozen `git archive` copies.
The runner created both scratch projects through the MCP Host; every typed refusal is asserted by its exact code and the rule
named in its message, and the complete state (hardware items with attribute values, ports and subnets, device inventory, every
PLC's program) must be unchanged after it. Every apply passes checkpoint, device compile, save and an exact state delta.

Offline: `tests/TiaMcp.HardwareEngineering.Tests` (508 checks; new: the disconnect's give-back preservation, the catalog link
of versionless types and the bundled GSD head profile with its refusals) and `scripts/test_qualify_hardware_changes.py` (the
slice's order and gate, the station on its own PLC, both port-link refusals, the disconnect payload, the two-project
comparison and the moved-start check).

## First project

An owned scratch CPU 1511-1 PN V4.1 with an added CPU 1511-1 PN V4.1 (`HwPeer`) on one subnet, two DI 16x24VDC HF in the
scratch rack, an ET 200SP IM 155-6 PN ST V6.4 station (`HwSp`) whose assignment creates `HwPeerIo`, three DI 8x24VDC ST
(6ES7 131-6BF00-0BA0 V1.1) at slots 1-3, and the operator-installed I-device GSD device on `HwPeerIo`.

- **Address change moving the start:** with `HwDi2` holding the input bytes after `HwDi`, a tag on `HwDi`'s first input bit
  made the replacement by a DI 32x24VDC HF V2.2 refuse as `AddressReferenced`; acknowledged, it applied, the readback's
  `changedReferencedTags` moved the tagged range's start from byte 0 to byte 4, and the checkpoint restored the pre-change
  state. The tag's address is not rewritten.
- **Channel profile refusal:** `hardware_channel_attributes` of `InputDelay` on channel 0 of a DI 8x24VDC ST was refused by
  the module's capability profile, which declares it read-only (`AttributeNotWritable`, "does not declare InputDelay writable
  on items/{module}, this channel").
- **Versionless profile:** the contract now bundles the GSD head's profile (`GSD:...TIAMCPIDEVICE-20261101.XML/DAP/DAP1`,
  keyed by its `FirmwareVersion` V4.1). `hardware_catalog_search` named it, and a write of `InformationText` on the head was
  refused by it (`AttributeNotWritable`, "The capability profile of GSD:... does not declare InformationText writable on the
  module"). The search had named no versionless profile at all, fixed below.
- **PLC-to-PLC refusal:** with `SpDi3` shared to the scratch CPU, `hardware_shared_device_access` `PLC` of `SpDi` from
  `HwPeerCpu` straight to the scratch CPU was refused (`UnsupportedCapability`, "A direct move between two PLCs of the station
  is not covered").
- **`sharedDevice` disconnect:** the scratch CPU's connector left the station with `SpDi3` given back to `HwPeerCpu` in the
  same change (below), and the checkpoint restored the shared state. TIA removed the scratch CPU's station constants, gave
  `SpDi3`'s back to `HwPeerCpu`, emptied the scratch CPU interface's assigned connectors and cleared its `LoadLimitation`; the
  emptied IO system `HwScratchIo` stays.
- **MRP port links:** with the two CPUs' `Port_2` linked as plain ports, `network_mrp_set` `Manager` on the scratch CPU was
  refused (`NetworkPrerequisite`, "...Port_2 would be a ring port while linked to HwPeer/HwPeerCpu/PROFINET interface_1/Port_2,
  which is not a ring port"). After the link was opened, both took `Manager` and `Client` in a new domain `HwRing` and were
  linked again, `NotInRing` on `HwPeerCpu` was refused ("...would leave the ring while linked to ...Port_2, a ring port").
- `SpDi4` went `OutsideOfProject` with one external IO controller, for the second project.

## Second project

A second owned scratch, created after the first was closed. Its own CPU 1511-1 PN V4.1 (192.168.0.6) takes the same
`HwSp` station (device name and address 192.168.0.5 as in the first) into a new IO system `HwOtherIo`, with the same three DI
8x24VDC ST at the same slots. `SpDi3` and `SpDi` go `OutsideOfProject`, with two external IO controllers on the station's
interface; `SpDi4` stays its own. The runner compared the two final stations: each module has the same type and slot in both
projects and is owned (`SharedDeviceAccessType` `PLC`) in exactly one, and each interface counts the other project's
controllers (1 in the first project, 2 in the second). V21 checks nothing across projects; the head, interface, ports and
server module stay assigned in both, which a downloaded configuration would have to resolve.

## Fixed: the `sharedDevice` disconnect could not compile

Discovery pass `rd1` (`artifacts/rf1`, `ae11ebcb`) applied the disconnect as built: the connector left, and the device
compile failed with "An attempt was made to compile a submodule that is not assigned" on `SpDi3`, still assigned to the
leaving scratch CPU (state `recovery_required`; the pass restored its checkpoint). V21 also refuses a connected PLC without an
assigned submodule, so no disconnect could pass. Now (`c0d006b6`) a `sharedDevice` disconnect names every item assigned to the
leaving PLC in `sharedSubmodules`; a missing or foreign item is refused at prepare, and so is a station with more than one
remaining PLC. The worker assigns them to the remaining PLC before the connector leaves; preservation requires their I/O
addresses owned by that PLC with type and length (the start may move into its space) and their target moved to it, every
other row exact. The qualification run passed with it ([guide](../guides/network-inspection.md#shared-devices)).

## Fixed: the catalog search named no versionless profile

`hardware_catalog_search` looked a profile up with the catalog entry's `Version`. A GSD entry's version is its file name
(`(GSDML-V2.45-SIEMENS-TIAMCPIDEVICE-20261101.XML)`), never a firmware, so a versionless profile was never named. The search
now names a type's profiles by type; a write still selects one by the item's `FirmwareVersion`.

## Runs

| Run | Build | Outcome |
| --- | --- | --- |
| `rd1/hw_f82e1c2e` (`--discover`, not evidence) | `rf1` (`ae11ebcb`) | 195 steps; the disconnect failed compile as above; the GSD gate found no bundled profile (native `AttributeAccessUnknown`); captured the GSD head's profile |
| `rd1/hw_8a15f807` (`--discover`, not evidence) | `rf1` (`ae11ebcb`) | A second pass of the same build, started by a lock waiter that outlived its stopped task; same findings |
| `rq1/hw_b11920ec` | `rf2` (`c0d006b6`) | Stopped at the disconnect's state check: it applied, compiled and passed preservation, and the leaving CPU's interface row (assigned connectors, `LoadLimitation`) changed, which the runner had not listed |
| `rq2/hw_b7f35015` | `rf3` (`45f7155d`) | Passed, 199/199 steps; curated |

## Still open

- A versionless firmware matching no profile (fail closed) stays offline: the installed GSD has one DAP with one firmware.
- The second project shows the complementary configuration in two projects; no cross-project consistency check exists in
  V21, and head, interface, ports and server module are not handed between the projects.
- A disconnect with two or more remaining PLCs is refused rather than offered a choice of PLC (since 7 November it names the PLC in `remainingOwnerPath`: [record](hardware-shared3-20261107.md)).
