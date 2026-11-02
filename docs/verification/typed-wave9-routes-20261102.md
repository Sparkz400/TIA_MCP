# Typed wave 9 on the software-unit and namespace routes — 2 November 2026

The wave 9 families (DInt/Real compare, MOVE, ADD/SUB/MUL/DIV, TONR, R_TRIG/F_TRIG) had profiles on the standard route
only, so the builder refused them in a software unit (`GraphicalProfileUnavailable`). This record captures and
qualifies their 120 route profiles on CPU 1511-1 PN V4.1, as the route IEC families were on 26 October
([record](typed-lad-fbd-route-iec-20261026.md), [TODO](../TODO.md#typed-ladfbd-variants)).

## References

`author_wave9_route_sources.py` writes, for the default unit namespace `TiaMcp_UnitA` (`softwareUnit` route) and for
`TiaMcp_UnitA.Alternate` (`namespace` route):

- DInt/Real eq..ge, MOVE and ADD/SUB/MUL/DIV on Int/DInt/Real: the standard wave 9 SIMATIC SD programs in the route
  namespace (`route-<Route>-qualified-*.s7dcl`), as for the Int families.
- TONR (local and existing global) and R_TRIG/F_TRIG (local): native derivations of the captured standard references
  (`route-<Route>-native-*.xml` with `.provenance.json`), as for TON..CTUD, because SIMATIC SD unit IEC probes keep TIA's
  `Missing instance DB` diagnostic. Only routing attributes, name, number and the global DB component change.
- `route-<Route>-global-tonr-storage.scl`: the route's `IEC_TIMER` DB for the existing-global TONR caller.

[Packet](../../fixtures/typed-graphical/route-capture-wave9-packet.v1.json): 126 references in two runs per route.

## Capture

`capture_typed_routes_owned.py` from frozen commit `b2640c66`, each run in its own owned project retrieved from the
SHA-bound software-unit baseline (`39cea56a…`):

| Run | Route | Inputs | Contents | Result |
| --- | --- | --- | --- | --- |
| `w9ua` | softwareUnit | 30 | DInt/Real compare, MOVE | passed, reopen matched, 19 min |
| `w9ub` | softwareUnit | 33 | TONR storage, arithmetic, TONR local/global, R_TRIG/F_TRIG | passed, reopen matched, 25 min |
| `w9na` | namespace | 30 | DInt/Real compare, MOVE | passed, reopen matched, 19 min |
| `w9nb` | namespace | 33 | TONR storage, arithmetic, TONR local/global, R_TRIG/F_TRIG | passed, reopen matched, 25 min |

Every input compiled clean, saved and added exactly its declared blocks while every existing object's XML stayed
unchanged. The extractor staged 120 profiles (per route: 24 compare, 6 MOVE, 24 arithmetic, 2 TONR with
`globalInstanceType` `IEC_TIMER`, 4 edge); the audit passed with no findings
(`fixtures/typed-graphical/wave9-route-candidates-20261102.audit.json`) and they were promoted with every
qualification flag false. Evidence: `local.typed.wave9-route-capture.20261102`.

A first `w9ua` attempt with its output inside the frozen tree was refused before any change: the retrieved project
directory would have exceeded TIA's path limit (`RecoveryPathTooLong`). The runs then wrote to the main checkout's
`artifacts/w9ua` and so on.

## Engineering

All **124/124** exact cases passed on frozen build `et4` (`4640a41f`): import/compile, complete XML preservation, save/reopen and checkpoint recovery/reopen. Runtime was not run.

The [case ledger](../../fixtures/typed-graphical/qualification-wave9-routes-20261102.v1.json) records every attempt, target, profile, binary hash and report hash; the [curated evidence](../../evidence/local.typed.wave9-routes.engineering.20261102.json) retains the aggregate and all 124 original result reports in the main checkout evidence store. These are the actual frozen-build results, not a qualification of later integrated binaries.

## Limits

- CPU 1511-1 PN V4.1 only.
- No R_TRIG/F_TRIG global storage on any route: V21 marks an edge instance DB know-how protected
  (`local.typed.edge-global-storage.20261030`).
- No created TONR/R_TRIG/F_TRIG storage on the routes (`createMulti`/`createGlobal`, transitive parents): that needs
  route IEC effect members for `TONR_TIME`, `R_TRIG` and `F_TRIG`, which the route IEC effect manifests do not have.
- Engineering only: the scoped runtime harness was not run for these cells.
