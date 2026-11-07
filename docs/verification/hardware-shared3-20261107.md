# Shared device among three PLCs — 7 November 2026

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.hardware_changes.shared3.20261107`](../../evidence/local.hardware_changes.shared3.20261107.json) | `qualify_hardware_changes.py --slice shared3` | 79/79 steps |

A `sharedDevice` disconnect gives the leaving PLC's `sharedSubmodules` to the remaining PLC, because V21 fails the device
compile of a submodule left assigned to a PLC that no longer controls the device. Until now that was offered only with
exactly one remaining PLC. `network_io_assign` now takes `remainingOwnerPath`, the PLC item of another assigned connector,
which takes them; it is required with several remaining PLCs and may be omitted with one. The worker and the preservation
rule both check it.

The slice adds two CPUs (`HwPeer`, `HwThird`) beside the scratch CPU and shares one ET 200SP station among all three:
connector 0 with HwPeerCpu, connector 1 with the scratch CPU (`SpDi3`) and connector 2 with HwThirdCpu through a new IO
system (`SpDi`). Then the scratch CPU's connector leaves:

- without `remainingOwnerPath`, and with it naming the leaving PLC, preparation is refused (`InvalidRequest`), nothing changed;
- with `remainingOwnerPath` HwThirdCpu it applies: SpDi3's assignment target and I/O address go to HwThirdCpu, the scratch
  CPU's station constants go and HwThirdCpu gains SpDi3's, with device compile and save. SpDi3's submodule keeps its start
  address, which is free in HwThirdCpu's address space;
- its checkpoint restores into a new directory equal to the snapshot before.

## Regression found on the way

The first run stopped at the station's first assignment, a step that passed on 1 and 2 November. Since #221 (7 November)
every compiling change first compiles its scope as a baseline. Compiling the ET 200SP station before its first assignment
generates the server module the assignment declares (`expectedServerModule`), so the assignment then refused with
`ObjectExists` after its checkpoint. That change kind now skips the baseline compile and keeps the clean-compile rule it
had before #221. Every hardware slice that assigns an ET 200SP station (the full run, `restores`, `refusals`) depended on
it; only `shared3` reran here.

Not covered: four PLCs, runtime behavior.
