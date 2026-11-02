# Typed LAD/FBD on a second CPU — 2 November 2026

Until now every typed profile, creation/instance template and dependent-effect manifest was captured on CPU 1511-1 PN
`OrderNumber:6ES7 511-1AL03-0AB0/V4.1`. This record adds a second target, CPU 1513-1 PN
`OrderNumber:6ES7 513-1AM03-0AB0/V4.1`, for the base families on the standard route
([typed TODO](../plans/typed-lad-fbd-todo.md), [TODO](../TODO.md#typed-ladfbd-variants)).

## What changed

- **Profile ids.** `extract_typed_graphical_profiles.py` derives the id prefix from the captured CPU. The first target
  keeps `v21.fw41.`; any other CPU uses its order number and firmware, `v21.6es7-513-1am03-0ab0-v4-1.`. Profiles were
  always selected by their `cpuFamily`; the ids now no longer collide.
- **Native folder.** Another CPU's references go to `fixtures/typed-graphical/native/<order-firmware>/`, so its
  `Tgp_*.xml` never share a file name with the first target's.
- **Effect manifests.** The worker reads `instance-effects[-iec|-transitive][-route][.<order-firmware>].v1.json` for
  the CPU of the change's PLC (`TypedGraphicalMultiInstanceTemplate.EffectManifestName`, mirrored by `effect_name` in
  the extractor). CPU 1511 V4.1 keeps its deployed names; the manifest's `cpuFamily` is still checked.
- **Capture into a scratch.** `capture_typed_routes_owned.py --cpu-type-identifier <id> --unit ''` captures into a new
  owned scratch of that exact catalog CPU instead of retrieving the CPU 1511 baseline archive, with the same per-source
  added-block and unchanged-XML checks and the fresh-process reopen.
- The candidate audit, the transitive extractor and the qualification summary name reports and sources from a frozen
  build or the main checkout relative to the checkout that holds them.

The Boolean fallback profile in `TypedGraphical.Validation.cs` stays bound to CPU 1511 V4.1; on CPU 1513 the Boolean
cells use the captured contact, coil, assign, AND/OR and NOT profiles.

## CPU choice

A read-only catalog probe in an owned scratch listed the installed S7-1500 entries. CPU 1513-1 PN 6ES7 513-1AM03-0AB0
V4.1 is the newest 1513 article and firmware and has the same single PN interface as the first target; CPU 1516-3 PN/DP
(6ES7 516-3AP03-0AB0 V4.1) and CPU 1515-2 PN were also available.

## Captures

The unchanged standard-route reference programs ([packet](../../fixtures/typed-graphical/second-cpu-capture-packet.v1.json),
written by `author_second_cpu.py`), each run in a new owned scratch of CPU 1513 V4.1, from frozen commit `8e9ed202`:

| Run | Inputs | Contents | Result |
| --- | --- | --- | --- |
| `c2a` | 28 | Boolean LAD/FBD, AND, NOT, Int eq..ge, FC, FB multi/global and numeric FC/FB calls | passed, reopen matched |
| `c2b` | 8 | FC/FB creation templates, global and multi-instance templates | passed, reopen matched |
| `c2c` | 26 | TON/TOF/TP and CTU/CTD/CTUD, local and existing global storage | passed, reopen matched |
| `c2d` | 7 | Template FB, then the FB, IEC and transitive dependent-effect pairs (`--modify`/`--observe`) | passed, reopen matched |

Every input compiled clean, saved and added exactly its declared blocks while every other object's XML stayed unchanged.
`c2a`–`c2c` staged 36 profiles and 8 templates from 63 references; the audit passed with no findings
(`fixtures/typed-graphical/second-cpu-candidates-20261102.audit.json`) and they were promoted. `c2d` gave the three
effect manifests of this CPU. All 63 exported references are structurally identical to the CPU 1511 V4.1 references
(ignoring `DocumentInfo`, the empty-text culture and TIA-allocated block numbers); they are kept as separate,
CPU-bound captures anyway, because only a capture on the target binds its profile.

Evidence: `local.typed.second-cpu-capture.20261102`.

## Engineering

All **102/102** exact cases passed on frozen build `et3` (`0a9c1fef`): import/compile, complete XML preservation, save/reopen and checkpoint recovery/reopen. Runtime was not run.

The [case ledger](../../fixtures/typed-graphical/qualification-second-cpu-20261102.v1.json) records every attempt, target, profile, binary hash and report hash; the [curated evidence](../../evidence/local.typed.second-cpu.engineering.20261102.json) retains the aggregate and all 102 original result reports in the main checkout evidence store. These are the actual frozen-build results, not a qualification of later integrated binaries.

## Limits

- Standard route and the base families only. DInt/Real compare, MOVE, ADD/SUB/MUL/DIV, TONR and R_TRIG/F_TRIG, the
  software-unit and namespace routes and cross-scope calls have no CPU 1513 profile; the builder refuses them there
  (`GraphicalProfileUnavailable`). Insert-first cells need the uncaptured network-free probe.
- Engineering only: runtime on PLCSIM was not run for CPU 1513.
- Other CPUs and firmware versions still need their own capture.
