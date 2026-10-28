# Watch-table authoring under TIA Portal V21 Update 3 — 28 October 2026

Evidence [`local.watch.authoring.20261028`](../../evidence/local.watch.authoring.20261028.json).
This PC moved from TIA Portal V21 Update 2 to Update 3, and authoring failed in run `author1`
([previous record](review-criteria-and-watch-20261028.md#tia-portal-v21-update-3-breaks-the-pinned-authoring-profile)).
Two changes fix it. Every run created a fresh owned scratch CPU 1511-1 PN V4.1 project with
active culture `de-DE`; nothing touched a user project, a PLC or PLCSIM.

## Changes

**The candidate takes `DocumentInfo` from a same-session export.** A table created from the
packaged template (`WatchTableNativeTemplate`, the Update 2 export
`fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml`) kept that template's
`DocumentInfo`. TIA's readback names the current installation, so `watch_table_apply` failed
with `VerificationFailed`. `watch_table_prepare_authoring` now hash-checks the baseline's first
watch/force export (the PLC's force table in these runs), copies its `DocumentInfo` into the
candidate (`WatchTableDocument.AdoptDocumentInfo`), records it as `template.documentInfoSource`
and keeps it as a plan input. The independent oracle `verify_watch_authoring.py` builds the
expected candidate the same way and still compares `Created` and the IDs exactly.

**The exact profile is re-pinned.** Every export embeds `DocumentInfo/InstalledProducts`, so
the FB/DB, empty force table, peer profile and candidate hashes in `WatchAuthoringProfile`
changed with the update. The new values come from run `author2`. Its exports are the fixture
[`native-authoring-v21-update3`](../../fixtures/watch-tables/native-authoring-v21-update3/README.md);
the Update 2 fixtures stay as history, and the managed test asserts they now differ only in
the four installation-bound checks.

| Pin | Update 3 value |
| --- | --- |
| FB `TiaMcp_EnableGate` | `326bc409…44674d` |
| Instance DB `TiaMcp_EnableGate_DB` | `fff585da…16a` |
| Empty force table | `c7b378c7…a309a` |
| Peer and hardware profile | `50ac1048…5847a1` |
| Candidate | `badf398c…7efac0` |

Three sources agree on these values. `author1` and `author2`, two separate scratch projects,
give the same FB, DB, force-table and peer-profile hashes, and `author1`'s readback content
hash equals the candidate pin. An independent reviewer, given only the exports and the hash
definitions, recomputed all five (C# `XDocument` for the XML hashes; .NET and a separate
Python serializer for the peer profile). The reviewer found no mismatch, and confirmed that
the candidate's `DocumentInfo` equals the force export's byte for byte.

## Runs

| Run | Build | Runner | Result |
| --- | --- | --- | --- |
| `author1` | `b11` | `qualify_watch_authoring.py` | Failed: readback differs in `DocumentInfo` only; recovered and reconciled. Not cited |
| `author2` | `b12pre` (`4cd016d`: fix, Update 2 pins) | `qualify_watch_authoring.py` | Passed; the capture for the re-pin. Not cited |
| `author3` | `b12` (`26a09a7`) | `qualify_watch_authoring.py` | Passed; profile matched |
| `exact1` | `b12` (`26a09a7`) | `qualify_watch_exact_profile.py` | Passed; profile matched |

Both `b12` runs are cited. `D:\wp\b12` holds `git archive` sources of `26a09a7` with a
`source-manifest.json`, the Debug Host and worker, and the worker registered by its own
post-build step. The runners ran from the frozen `scripts/` copy.

`author3`: generation from the native FB and instance DB capture, prepare, independent
file-only review, apply with readback and six preserved peers, saved fresh reopen, checkpoint
recovery to absence at a distinct destination, and restored and original reconciliation.

`exact1` repeats that lifecycle and adds the negatives. `qualification_watch_authoring_source_negative`
saved a real FB `HeaderAuthor` change. The native `VerifyWatchSource` guard refused it, and the
helper restored every peer. Then came a fresh source capture, three Host prepare collisions
(exact name, case-folded name, force-table name), and a second prepared operation that was
refused after the fresh reopen as stale, before any checkpoint.

The independent oracles `verify_watch_authoring` and `verify_watch_exact_profile` passed both
runs, and every owned Portal and worker process has an exit proof.

## Catalog

Verified (catalog `0.4.49`, 19 cells): `watch_table_generate`,
`watch_table_prepare_authoring`, `qualification_watch_authoring_source_negative`.

Not verified by these runs: `qualification_watch_authoring_source_reconcile`. It only
reconciles a source-negative admission left in `dispatchIntent` or `outcomeUnknown`, and the
exact-profile runner calls it only when the negative itself fails. Neither run left such an
admission. It passed later the same day after an actual Host loss
([below](#source-negative-reconciliation-after-host-loss)).

## Source-negative reconciliation after Host loss

Evidence [`local.watch.source_reconcile.20261028`](../../evidence/local.watch.source_reconcile.20261028.json).
Runner [`scripts/qualify_watch_source_reconcile.py`](../../scripts/qualify_watch_source_reconcile.py),
run `src3`, on a frozen Debug build in `D:\wp\b58`. A new qualification-only Host point,
`host.watch_authoring_source_negative`, stops the Host after the admitted native dispatch of
the helper returned and before the Host records the outcome. The worker had finished: the saved
`HeaderAuthor` drift, the `StaleSource` refusal and the restoration are in the Host's
interruption marker.

What the run proves:

- The Host exited 74; its worker and Portal exited 0. The shared admission stayed in
  `dispatchIntent` and owned the project.
- A second Host opened the original in a fresh owned Portal. A replay of the helper was refused
  with `ResourceBusy` before any native effect.
- `qualification_watch_authoring_source_reconcile` compared the retained pre-effect baseline with
  the fresh snapshot: absent table, identical content and peers, new session and process. It
  reconciled the project as `unchanged` without a native mutation.
- A second reconcile was refused with `InvalidOperationState`, `engineering_operation_status`
  read the admission as reconciled, and a fresh capture reproduced the original FB and
  instance DB hashes.

Fixed on the way: the two source-negative qualification tools passed bridge exceptions through,
so a refusal reached the client as an untyped `McpToolError` (trial `src1`). They now return typed
errors like every engineering tool. Trial `src2` stopped before starting a Host because of a wrong
path. Neither trial is cited.

A worker loss inside the helper, between the drift and the restoration, leaves a changed
project. The reconciler refuses that by design (`ReconciliationIncomplete`); it needs checkpoint
recovery instead and is not exercised.

## Still open

- Creation response loss passed later the same day ([below](#creation-response-loss)).
- Production authoring stays disabled. The profile covers one created root table with one
  direct-instance Active Bool row and the default reason. Additions to existing tables,
  removal, reorder, explicit comments, other types, CPUs, firmware and languages stay separate.
- The pins follow the installed products. After the next TIA update they must be re-derived
  from a fresh capture, as here.

## Creation response loss

Evidence [`local.watch.creation_loss.20261028`](../../evidence/local.watch.creation_loss.20261028.json).
Runner [`scripts/qualify_watch_authoring_loss.py`](../../scripts/qualify_watch_authoring_loss.py), run
`cl2`, on a frozen Debug build in `D:\wp\b59`. It creates root `WT05_BoolCreation` with one Active Bool
row through `watch_table_apply`, under a Host started with `--watch-qualification host.watch_apply_native`.

- The native creation was applied and saved. The Host exited 74 before the reply.
- A second Host refused the replay (`RecoveryRequired`) and a competing owner (`ResourceBusy`)
  without native dispatch.
- It recovered the checkpoint to a distinct destination, whose fresh reopen shows the table
  absent, and reconciled the original as `verifiedApplied` without adopting the lost reply.
- The independent oracle `verify_watch_authoring_loss` passed: a single dispatch, six preserved
  peer exports, and exact lifetimes (first Host 74, every worker, Portal and the second Host 0).

Fixed on the way: the oracle required the lost Host's process tree to report all-zero exits.
The tree's completion flag excludes any non-zero exit, so a Host-loss trial could never pass,
although the per-process check already expected 74 for that Host. The oracle now checks the
tree's other conditions explicitly for that Host. Trial `cl1` (build `b58`) passed the same
native flow and failed only on that check; it is retained and not cited.
