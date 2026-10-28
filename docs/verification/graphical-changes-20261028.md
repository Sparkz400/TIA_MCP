# Graphical change kinds (X11/L01) — 28 October 2026

Evidence [`local.graphical_changes.20261028`](../../evidence/local.graphical_changes.20261028.json).
Runner [`scripts/qualify_graphical_changes.py`](../../scripts/qualify_graphical_changes.py), run `gr3`,
on a frozen Debug build in `D:\wp\b56` (sources extracted with `git archive`, with a
`source-manifest.json`). The run used one fresh owned scratch CPU 1511-1 PN V4.1 under TIA Portal V21
Update 3. Nothing was downloaded or simulated.

## Fixtures

The runner imports two genuine V21 exports from
[`fixtures/typed-graphical/native`](../../fixtures/typed-graphical/native): `Tgp_FC_LAD` and `Tgp_FC_FBD`.
Each is an FB whose one network calls the scratch's own FC `TiaMcp_IsEnabled`. The runner makes two
changes before import: it maps the export's culture to the project's culture, and it adds one Bool
input, `EnableAlternate`. The built-in `fixture_sources` blocks have no call, so they cannot carry
the call kinds.

## What ran

Phase A, for LAD and for FBD:

| Kind | Edit |
| --- | --- |
| `call_parameter_set` | the call's `Enable` argument moves to `EnableAlternate` |
| `graphical_network_patch` | one Access replaced in place: LAD symbol → `TRUE` constant, FBD constant → `Enable` |
| `graphical_network_insert` | a copy of the network with fresh object IDs, after the original |

Each change must pass checkpoint, compile, `native_preservation_verified` with a structured-edit
readback, and save. A complete snapshot must then differ only in the edited block, and
`change_status` must report the apply result's stages and checkpoint. On LAD, each kind first
refuses a wrong `previewHash` (`PreviewMismatch`) without stages or checkpoint. A fresh owned
Portal reopen showed the identical state.

Phase B starts from the reopened state. It applies one change per kind again (FBD argument back to
`Enable`, LAD `TRUE` Access replaced by `EnableAlternate`, a third FBD network). Each is restored
from its checkpoint into a new directory, which must equal the reopened state.

Nine typed prepare refusals left the PLC software unchanged:

| Code | Request |
| --- | --- |
| `NetworkNotFound`, `CallNotFound`, `ParameterNotFound` | `call_parameter_set` with an unknown network, call or parameter |
| `UnresolvedNetworkReference` | a patched wire to a missing part |
| `InvalidNetworkPatch` | a patch with no fragments |
| `UnsupportedLanguage` | a patch on the SCL block `TiaMcp_EnableGate` |
| `LanguageConversionNotSupported` | a LAD network inserted into the FBD block |
| `InvalidSimaticML` | an inserted network reusing existing object IDs |
| `AccessWiringUnsupported` | `call_connection_set` |

Verified: `change.call_parameter_set`, `change.graphical_network_patch`,
`change.graphical_network_insert` and `block.graphical.networks` (24 cells; matrix 569 of 2,019
open, catalog `0.4.57`).

## Findings and fixes

Exploratory probes on the same fixtures found three things that TIA V21 does on import:

1. **Object IDs are renumbered in document order.** An inserted network shifts the IDs of the
   block's own title, which follows the compile units. The exact readback check compared `ID`
   literally, so every insert failed with `NativePreservationMismatch`, although the readback was
   otherwise identical. The check now compares object `ID`s by position. Elements, attributes,
   text and order still have to match.
2. **UIds are renumbered, and Access parts are listed first.** A patch that adds or removes
   fragments comes back with different UIds and part order. It still fails closed with
   `NativePreservationMismatch` and needs its checkpoint. In-place replacements of existing nodes
   read back exactly, and only they are qualified.
3. **Every Access must be wired exactly once.** TIA refuses an Access without a wire and one used
   by several wires ("used multiple times at the cables"). `call_connection_set` moves a wire from one
   existing Access to another, which always produces both. Prepare now refuses such moves with
   `AccessWiringUnsupported`, and the catalog marks `change.call_connection_set` unavailable on V21.

## Earlier trials

- `gr1` (build `b54`) passed every applied, persisted and refused step and the fresh reopen. It then
  stopped in the runner's restore phase on the runner's own one-network assertion: after the
  reopen, each block holds two networks. It is retained as failed and not cited.
- `gr2` (build `b55`) passed completely but never called `change_status`, which these rows list
  among their tools, so the contract check refused it as evidence. `gr3` adds the `change_status`
  check and the stale-preview refusals; `gr2` is retained and not cited.

## Limits

- One LAD and one FBD network, each an FB calling an FC, on one CPU and firmware.
- Patches that add or remove fragments are not qualified (finding 2).
- Engineering only: no download, simulation or runtime behavior.

## Also recorded

`watch_table_force_document_edit` (D02) stays open. A fresh scratch's default force table has no
rows, and V21 has no API that creates force rows. Every force edit needs an existing row, so
qualifying it needs a force table authored in the TIA Portal editor (the force-fixture capture
route).
