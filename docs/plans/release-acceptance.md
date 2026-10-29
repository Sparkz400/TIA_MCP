# Release acceptance

1.0 ships when every obligation in the [release ledger](release-obligations-20261015.md) is
accepted and the [TODO](../TODO.md) is closed. This page says how an obligation is accepted.
No obligation is accepted yet.

## Who accepts

The maintainer. An agent or CI may prepare an acceptance record for review; only the maintainer
sets `releaseAccepted: true`.

## When an obligation can be accepted

1. Its [matrix](release-matrix.v1.json) rows have no `open` and no `deferred` cells on the commit
   under review, and `python -B scripts/release_matrix.py --check` passes there.
2. Every `blocked` row it owns has a maintainer disposition in the ledger's `dispositions`.
3. Each blocker it lists (`blockers`, all release-blocking) is resolved, or the record says why
   that blocker does not bind the obligation's remaining scope.
4. Its work outside the matrix in the TODO is closed or accepted as a stated limitation.

## The record

The record is stored on the obligation in the
[machine-readable ledger](release-obligations-20261015.json): `releaseAccepted: true` plus an
`acceptance` object.

```json
"acceptance": {
  "date": "2026-11-02",
  "acceptedBy": "maintainer",
  "commit": "<main commit reviewed>",
  "matrixSha256": "<SHA-256 of docs/plans/release-matrix.v1.json at that commit>",
  "evidenceIds": ["<every evidence ID bound in its rows, plus out-of-matrix records>"],
  "dispositions": ["<featureId of each blocked row it relies on>"],
  "blockers": {"B01": "resolved | <why it does not bind>"},
  "limitations": ["<accepted out-of-matrix limits, if any>"]
}
```

`matrixSha256` names the matrix the maintainer reviewed, before the acceptance edit; writing
the record changes the ledger, so regenerate the matrix afterwards
(`python -B scripts/release_matrix.py`), which records the new ledger hash.

## Dispositions

The ledger's top-level `dispositions` holds the maintainer's decisions on rows that cannot close.
Each entry names the feature, its obligations, the decision, its date, the boundary and the
evidence. A dispositioned row is not a pass; it leaves the 1.0 count. The decision of
29 October covers five `unavailable_target` rows (31 cells). Four rest on recorded V21
behavior; `change.hmi_engineering` rests on V21's missing `Screen.Export` plus the repository's
preservation gate. The three block-protection kinds were blocked only for a missing repository
route; the maintainer decided the same day to build it for 1.0, so they count as open work.
The ten `deferred` rows have no disposition and stay 1.0 work
([TODO](../TODO.md#deferred-rows)).

## Candidates on 29 October (catalog `0.4.76`)

No obligation meets all four conditions. With no open or deferred cell and no blocker:

| Obligation | What still stands |
| --- | --- |
| F01 API coverage, F02 shared contracts | 6 cells bound each; their `remainingWork` is the unrefreshed 15 October audit text, which the maintainer has to confirm as done or restate. |
| D01 VCI and external Git | 81 cells bound; the external Git round trip is open. |
| D03 Project translations | 141 cells bound; category leakage keeps restricted profiles disabled. |

22 more obligations have every row bound (D04 and X01 with dispositioned blocked rows) and are
held only by their blockers: B01 (F03, D02, X01, L02, L03, D04, X02, D17), B02 (L02, L03),
B03 (X04, D17), B04 (D04, D05, D08, D11, D12, X03, X06, C02), B05 (D09, D12, X03, C02, C03, C04),
B06 (D17, C04), B07 (L04), B08 (X02) and B10 (X05, X09). Blockers were attached per obligation by
the 15 October audit; deciding which still bind is part of each acceptance. F00 closes last.

## TODO

The TODO's [obligation acceptance](../TODO.md#obligation-acceptance) section has one box per
obligation group; a box closes when every obligation in it has an acceptance record.
