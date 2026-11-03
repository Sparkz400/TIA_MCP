# Classic screen workflow changes — draft checkpoint, 3 November 2026

This checkpoint retains the day's existing screen recovery fixes and the subsequent
workflow improvements. It is **not ready for merge**: native batch qualification
remains open. The original draft paused at the user's request; the offline
continuation below resumes review and validation without promoting native support.

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

The original handoff required review of the new cache and batch boundaries,
member-receipt and artifact-tampering tests, successful and failed native batches,
fresh source bindings, final offline/package/Explorer checks and green CI. It also
required the HMI guide to document the resulting scope and measured timing.
Native or runtime qualification must not be inferred from build or managed-test
results alone.

## Offline continuation

Review reproduced two preservation/recovery defects with failing regression tests:
missing batch receipts could take the legacy single-screen reconciliation path,
and scalar composition could silently remove XML comments or other annotations.
Terminal checks now require the complete ordered member list, matching identities,
dispatch/readback stages and the restored attempted prefix. Legacy single-screen
journals remain accepted. Composition refuses replacement of non-plain fields.

Host XML artifact inputs now reject malformed references, mixed inline/artifact
inputs, changed hashes, invalid UTF-8, excessive size and links outside the root.
They reuse the existing artifact-store path/link guard. The worker's shared
capture/composition/recovery artifact guard also refuses linked ancestors.
Diagnostic progress cannot claim a verified outcome or permission to retry.

The draft's failed CI run (`41653616274`) contained six release-owner errors and
one stale tool-reference assertion. The screen-edit feature now maps to X07;
the matrix and tool reference are regenerated. Catalog `0.4.131` includes all
487 features and 327 tools; the three unqualified screen workflow entries add
13 open cells, bringing the total to 28. No qualification or release-acceptance
flag is promoted.

Focused validation: 92 HMI engineering checks and 30 actual-Host artifact/progress
checks pass, with native workers absent from the latter. Explorer passes 26
unit checks and its browser workflow, with no page errors or network requests.
The complete Python discovery run passes 2,471 tests with one skipped. The final
Release worker build has zero warnings/errors, and its compiled lifecycle/export
safety guard passes without loading Siemens assemblies. Source-binding validation,
contract checks, release-matrix consistency, tool-reference tests and all 214
repository-file links in the changed Markdown pass. The 1.0 gate still refuses
with 28 open cells, six deferred cells and no accepted obligations.

Portable package quick validation passes strict hashes, exact ZIP contents and
actual MCP smoke checks before and after relocation. Quick mode does not run
every packaged workflow; those remain main/nightly checks. Local evidence is in
`artifacts/pr206-package-final/result.json`,
`artifacts/pr206-native-build-inputs.json` and the `artifacts/pr206-*.log` files.
The complete Python/managed runner writes
`artifacts/pr206-offline-final/result.json`; final commit acceptance is also
tracked by the checks on [PR #206](https://github.com/Sparkz400/TIA_MCP/pull/206).

An independent fixture attempt built `HmiSeeder` successfully against this
workstation's installed V21 API, then stopped at the Openness access prompt before
creating a project (`profiles` remained empty). The operator requested offline
continuation because they could not access the prompt. The owned Portal startup
was terminated after verifying its executable and parent process; the helper
exited. This is an aborted prerequisite attempt, not a native qualification run.
Local startup evidence is retained at `D:/h206s1/native-report.json` and
`D:/h206s1/offline-continuation.json`.

Still required before merge: an operator-admitted owned fixture, native batch
success and later-member failure, verified reverse-order restoration, lost-response
reconciliation, fresh reopen and measured latency. No current-batch performance
claim can be derived from the earlier 80.7-second capture observation.
