# Existing-caller component installation — 25 October 2026

Lane B qualification of installing the component call into an **existing** FBD FC
caller, under the [insertion-ID scope decision](../plans/insertion-id-renumbering-decision-20261025.md).
Offline support merged in Sparkz400/TIA_MCP#52, building on lane A's typed prediction
(Sparkz400/TIA_MCP#50).

**Status: passed** in run b07 (`local.plc_component.existing_caller.20261025`,
source 13b9427). The installation passed full compile and save, complete
preservation, fresh reopen and first-checkpoint recovery. V21 renumbered exactly
as predicted: the inserted network took ID `8` and the following block-title IDs
moved `8→D` and `9→E` (+5). Reversing that one shift recovered the original caller
exactly, both in the native `callerInsertion` proof and in the qualifier.
Earlier runs found three defects, all fixed before b07.

## Runs

All runs: `qualify_plc_component.py --run-native --caller-mode existing`, CPU 1511-1 PN
V4.1, TIA V21 Update 2, each under `scripts/native_lane.py`. Raw reports stay in the
ignored `artifacts/` trees of the short worktrees `D:/tbb` … `D:/tbf`.

| Run | Reached | Result |
| --- | --- | --- |
| b01 | FB/UDT import, compile, save, import readback | Typed preparation: `InstanceCreationProfileUnavailable`. The captured component storage profile was limited to new standalone FC callers. |
| b02 | Typed preparation | V21 renumbering predicted exactly: inserted network `8`, block title `8→D`, `9→E` (+5). The qualifier did not expect the `graphical_typed_edit` effect for an edited existing block and stopped before apply. |
| b03 | `tia_start` | Portal did not answer within 600 s. Nothing created. |
| b04 | Reviewed typed apply | Coordinator rejected its own journal (`ComponentJournalInvalid`): a native timestamp `…22.8245720Z` was reparsed as `DateTime` and reserialized as `…824572Z`. Nothing dispatched. |
| b05, b06 | `tia_start` | Portal did not answer within 600 s. The harness retained the owned worker/Portal (no automatic termination); they were stopped by exact PID after verifying image paths. Nothing created. |
| b07 | Complete sequence | **Passed.** Debug build in `D:/tbf`, registered by the project's post-build allow-list step. |

**Why b03, b05 and b06 hung:** these runs used Release worker builds. Only Debug
builds register themselves in the Openness allow-list
(`scripts/Register-DevOpennessWorker.ps1`), so TIA waited on its access prompt.
`D:/tbe`, used by b05 and b06, never got an entry. Use a Debug build, or register
the Release worker, before a native run.

No run replayed a mutation. Every failure stopped before the next native mutation boundary.

## Fixes

- `OpennessSession.CapturedStorage.cs`: the captured storage profile fixes only the
  global instance DB, so an existing FBD FC caller (typed `insertNetwork`) is eligible
  too. LAD, OB, FB and non-standard scopes stay refused.
- `qualify_plc_component.py`: in existing mode the typed review requires exactly one
  `graphical_typed_edit` effect for the reviewed unprotected FBD FC.
- `PlcComponentCallerInsertion`: the reversal also removes the inserted network's
  indentation, since native exports are read with whitespace preserved.
- `PlcComponentCoordinator`: the journal is read with `DateParseHandling.None`, so
  date-like strings keep their exact bytes. This was latent for every component run.
  The standalone `c09` run passed only because none of its timestamps ended in `0`.

## Still open

- **Other callers:** only an existing FBD FC, inserting after its last network, is
  qualified. LAD, FB and multi-network placements have no native run; OB callers are refused.
- **Runtime:** the existing caller is not called from an OB in b07.
- `LibraryJournalStore.Read` (library change journal, lanes C/E) parses dates the same
  way and can hit the same hash mismatch.
- Production combined `library_integrate` apply and OB insertion stay disabled.
