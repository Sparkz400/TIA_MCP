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

The 1.0 release gate validates the record, not just its approval flag. The reviewed commit must
be an ancestor available in Git history, and its matrix bytes must match `matrixSha256`. The
obligation's rows and accepted matrix evidence must still match, every owned blocked row must
have a disposition, and every listed blocker needs a nonempty resolution or explanation.
Evidence IDs must cover the owned rows and name current matrix evidence or a retained curated
record in `evidence/`. Source, tests, contracts, scripts, evidence and build inputs must already
be frozen: changing them after acceptance requires a new review. Ledger and matrix edits that
record acceptance do not invalidate it. The release checkout fetches full history for this check.

## Dispositions

The ledger's top-level `dispositions` holds the maintainer's decisions on rows that cannot close.
Each entry names the feature, its obligations, the decision, its date, the boundary and the
evidence. A dispositioned row is not a pass; it leaves the 1.0 count. The decision of
29 October covers five `unavailable_target` rows (31 cells); `block.move` and
`safety.logic_changes` joined on 1 November (seven rows, 37 cells), and the five Test Suite style
execution rows later that day (twelve rows, 72 cells). Four of the first seven rest on recorded V21
behavior; `change.hmi_engineering` rests on V21's missing `Screen.Export` plus the repository's
preservation gate. The three block-protection kinds were blocked only for a missing repository
route; the maintainer decided the same day to build it for 1.0, so they count as open work.
The two `deferred` rows have no disposition and stay 1.0 work
([TODO](../TODO.md#deferred-rows)).

## Current acceptance preparation — 2 November 2026

No obligation has a maintainer acceptance record. Matrix closure is only the first condition:
remaining work outside the matrix, the current applicability of blockers and any accepted
limitations must be reviewed on the frozen candidate too.

- Open or deferred matrix work: L05, L06, D06, D10, D16, X12.
- No open or deferred matrix cell: F00, F01, F02, F03, L01, L02, L03, L04, D01, D02, D03, D04, D05, D07, D08, D09, D11, D12, D13, D14, D15, D17, X01, X02, X03, X04, X05, X06, X07, X08, X09, X10, X11, X13, C01, C02, C03, C04. This includes obligations with no rows; it does not establish their completion.

The old candidate list described catalog 0.4.76 and is superseded. In particular, external Git,
restricted translation and the native desktop roundtrip are now qualified. The collector already
exists; semantic/manual/profile review is the remaining F01 work. The current JSON ledger records
those distinctions. F00 closes last, after the other acceptance records have been reconciled.

## TODO

The TODO's [obligation acceptance](../TODO.md#obligation-acceptance) section has one box per
obligation group; a box closes when every obligation in it has an acceptance record.
