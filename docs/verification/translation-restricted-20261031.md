# Restricted translation category profile under V21 Update 3 — 31 October 2026

These runs qualify the one restricted translation profile: the Host-filtered `BlockCommentCategoryData` capture of one PLC
([restricted categories](../reference/translation-native-import.md#generalized-route-qualification-only), contract
`translation.plc-block-comments-de-DE-en-GB-main-ob1.exact.v1`, [admission](../reference/translation-production-admission.md#restricted-block-comment-contract)).
They also settle the populated-Main control that was inconclusive on 28 October. Everything ran in owned Portals and owned
scratch projects (CPU 1511-1 PN V4.1, English TIA UI, de-DE reference and en-GB target). Nothing touched a user project, a PLC or PLCSIM.

## Builds

| Tree | Commit | Use |
| --- | --- | --- |
| `artifacts/tr09/source` | `d9fc9081` | First attempt; failed before any native call (below) |
| `artifacts/tr09/f2cb93da` | `f2cb93da` | Second attempt; failed in the runner's comparison after the category step (below) |
| `artifacts/tr09/fa013be2` | `fa013be2` | Debug Host and worker for both accepted runs; the provisional registry is installed next to them |
| `artifacts/tr09/final` | `fa013be2` | Byte-identical copy of the `fa013be2` Host and worker with the finalized registry; no native run |

Each tree is `git archive` of its commit, hashed into `<commit>-manifest.json` before building.

## Runs

| Run | Runner | Result | What it covers |
| --- | --- | --- | --- |
| `tr09/si3` | `qualify_translation_import.py --same-instance --restricted-lifecycle` | Passed; independent `verify_translation_admission.py` (`2b11b47b`) passed | Category probes; Host-filtered captures; on the filtered block-comment scope the seven forward cases (interruption before dispatch, source drift, lost import, compile, save, reopen and restore replies), three reviewed reverse edits, 8 imports and 10 distinct restorations, all reconciled; final unattempted plan with the same restricted-contract key (`a438fc9b…`) |
| `tr09/p1` | `qualify_translation_native.py --interrupt worker:checkpointIntent --interrupt worker:dispatchIntent --interrupt host:dispatchIntent` | Passed | Default mode on the provisional registry built from `si3`: the three interruptions, each reconciled `unchanged` without replay, and the clean lifecycle with its refusals, import, recovery and reconciliation |

Evidence: `local.translation.restricted.qualification.20261031` (`si3`) and `local.translation.restricted.native.20261031` (`p1`).

After `p1`, `build_translation_admission.py --finalize` produced the 25-proof final entry in `tr09/final`. The Host
registry loader accepts it there (`loader-check.log`). It accepts the provisional entry only in its own deployment, and
refuses a copy of the provisional registry in another deployment (`TranslationAdmissionProvisional`).

## Findings

- **The Host filter is exact on native exports.** Each restricted request returned all six PLC rows again. The Host
  kept exactly the requested rows and listed every other PLC row as excluded:

  | Category | Kept | Excluded | Native rows |
  | --- | --- | --- | --- |
  | `BlockCommentCategoryData` | 5 | 1 | 6 |
  | `HwcnCommentTextCategoryData` | 1 | 5 | 6 |
  | `AlarmClassDisplayNameCategory` | 0 | 6 | 6 |

  Kept rows equal the category probe's independently decoded `plc-all` rows by key, category and texts.
- **The populated-Main control is conclusive.** It now accepts the English view paths, with the seeded
  `Prueftext Main {0}` block comment as its sentinel. The Main selection holds three rows, all under Main and equal to
  the PLC baseline. The Main requests leak too: hardware and alarm-class requests on Main returned all three Main rows.
- **TIA imports the Host-filtered workbook.** The workbook has the hardware row removed and the rows renumbered.
  Readback, compile, save, fresh reopen and checkpoint restoration all passed on it. The read-only warnings for Main's
  `Initial_Call`/`Remanence` rows matched the accepted log shape.
- **The whole-project export also holds four project-level alarm-class rows.** No PLC-owner export returns them, so the
  whole-project capture is not a baseline for PLC captures. This caused the second attempt's failure.

## Failed attempts (retained)

- `tr09/si` (`d9fc9081`) stopped before its first tool call. Since `28d50f91`, `smoke_mcp.Client.request` prefetches
  `tools/list` without params, and `ExactClient.request` still required them. The exact close then held for a worker
  that had never started. The runner and its Host were ended by PID; no worker, Portal or project existed.
  Fixed in `f2cb93da`.
- `tr09/si2` (`f2cb93da`) ran the category step and the restricted captures. The runner then compared them with the
  whole-project capture, whose alarm-class rows the PLC export cannot contain, so 0 kept rows failed against 4. No
  import was attempted, and the run closed cleanly. Fixed in `fa013be2`: the runner compares with the decoded `plc-all`
  export. The same check passes offline on `si2`'s retained responses.
- `verify_translation_admission.py` first refused `si3`'s source-drift case, because it compared the raw export, which
  includes the excluded rows. Fixed in `2b11b47b` to compare the admitted-category rows. This was an offline verifier
  change, not a native rerun.

## Limits

- One scratch, one PLC, de-DE/en-GB, target-only text, and only the `BlockCommentCategoryData` restriction was
  imported. Other categories, several PLCs, block selections and an empty restricted import have no evidence.
- The registry is instance-bound to the `si3` scratch and was installed only into test deployments. The worker side of
  the finalized entry was not run natively.
- The read-only warning is matched in its English wording only.
