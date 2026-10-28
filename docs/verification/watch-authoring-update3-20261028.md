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

Not verified: `qualification_watch_authoring_source_reconcile`. It only reconciles a
source-negative admission left in `dispatchIntent` or `outcomeUnknown`, and the exact-profile
runner calls it only when the negative itself fails. Neither run left such an admission, and
no runner has an interruption point inside the helper. Verifying it needs a worker-loss point
in `watch_authoring_source_negative`.

## Still open

- Creation response loss (`qualify_watch_authoring_loss.py`) has not run under Update 3.
- Production authoring stays disabled. The profile covers one created root table with one
  direct-instance Active Bool row and the default reason. Additions to existing tables,
  removal, reorder, explicit comments, other types, CPUs, firmware and languages stay separate.
- The pins follow the installed products. After the next TIA update they must be re-derived
  from a fresh capture, as here.
