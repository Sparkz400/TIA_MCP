# Classic screen workflow changes — draft checkpoint, 3 November 2026

This checkpoint retains the day's existing screen recovery fixes and the subsequent
workflow improvements. It is **not ready for merge**. Work stopped at the user's
request after creating the draft pull request.

The new implementation adds lease-bound observation reuse, exact-owner HMI
discovery, direct baseline capture, native component composition and preflight,
bounded batches of up to eight existing regular screens, per-screen rollback
receipts, compact responses and phase progress. `additionalScreens` extends
`hmi_classic_screen_edit`; the first screen retains the existing `target`/`xml`
shape. The Host also accepts a hashed `xmlArtifact` under its artifact root.

`hmi_classic_screen_capture` returns a scope hash and native artifact references.
`hmi_classic_screen_compose` clones captured native items with explicit scalar,
tag and caption replacements. Preparation checks native IDs, geometry, known
graphic sizes, visibility composition ordering and native structured-tag paths.
The native preservation comparer and compiler diagnostic comparison remain in
place. `change_status(progressOnly=true)` reads diagnostic progress without
waiting for the native worker; it never authorizes replay.

Validation completed before this checkpoint:

- Debug and Release solution builds, including the compiled worker lifecycle guard.
- A subsequent Release Host and worker build after the final implementation edits.
- Contract catalog check: 327 tools, 137 mutation kinds, 487 feature entries.
- Direct native capture against an isolated, authenticated Classic RT Advanced
  copy (80.7 seconds on the intermediate build). This does not benchmark the
  later exact-owner discovery change.

The full managed suite was started, but its final result and coverage of the
latest edits have not been accepted. The new native batch success, failure on a
later member, verified rollback, lost-response reconciliation and timing tests
remain outstanding. Earlier single-screen production evidence is recorded
separately and must not be treated as qualification of this new batch path.

A protected archive retrieval extracted its destination before authentication
failed. Its unresolved admission was retained. An independent fixture was then
prepared from the unchanged archive and opened through TIA's login UI; no batch
write was dispatched before work stopped. The original project was not edited
by this qualification attempt. Private project data and local test artifacts are
excluded from the pull request.

Before merge: review the new cache and batch boundaries; finish tests for member
receipts and artifact tampering; exercise successful and failed native batches
on the independent fixture; refresh source bindings after any changes; run final
offline/package/Explorer checks and require green CI. Update the HMI guide with
the resulting supported scope and measured timing. Do not promote native or
runtime qualification from build or managed-test results alone.
