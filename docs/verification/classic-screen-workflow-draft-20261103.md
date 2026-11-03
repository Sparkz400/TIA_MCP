# Classic screen workflow changes — verification, 3 November 2026

This record retains the original draft and offline review, followed by the
operator-assisted native continuation below. Native evidence is bounded to the
independently seeded RT Advanced fixture; profile-wide qualification and runtime
behavior remain unverified.

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

Validation completed before the original draft checkpoint:

- Debug and Release solution builds, including the compiled worker lifecycle guard.
- A subsequent Release Host and worker build after the final implementation edits.
- Contract catalog check: 327 tools, 137 mutation kinds, 487 feature entries.
- Direct native capture against an isolated, authenticated Classic RT Advanced
  copy (80.7 seconds on the intermediate build). This does not benchmark the
  later exact-owner discovery change.

At that checkpoint the full managed suite had started, but its final result and coverage of the
latest edits had not been accepted. The new native batch success, failure on a
later member, verified rollback, lost-response reconciliation and timing tests
were outstanding. Earlier single-screen production evidence is recorded
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

The offline handoff still required an operator-admitted owned fixture, native batch
success and later-member failure, verified reverse-order restoration, lost-response
reconciliation, fresh reopen and measured latency. No current-batch performance
claim can be derived from the earlier 80.7-second capture observation.

## Operator-assisted native continuation

The independent seeder created two Classic RT Advanced HMIs with two regular
screens each. The guarded preparation coordinator retrieved a new owned copy.
The operator set `Overview_A` as both HMIs' start screen and saved. The helper
verified zero errors and warnings on both compiles, archived the configured
fixture, and closed its exact owned processes. The original source archive and
report remained unchanged.

The actual compose-to-prepare flow exposed a Host input mismatch: composition's
artifact reference includes `bytes`, while preparation previously admitted only
`path` and `sha256`. A failing actual-Host regression reproduced it. Preparation
now accepts the optional integer byte count and requires an exact file-length
match, retaining all path, hash, size, UTF-8 and link checks. The actual-Host suite
passes 35 checks. Both file-only preparation and managed admission tests cover
the newly admitted RT Advanced start-screen prerequisite.

`scripts/qualify_hmi_classic_screen.py` exercises the public stdio MCP tools on a
fresh retrieved fixture and retains each response, its hash and wall time. It
checks native composition, two-screen batch import, every dispatch/readback
receipt, identical same-key journal replay, and independently exported XML.
An invalid color in the second member causes a real Siemens import refusal
after the first member changed. Both attempted screens restore in reverse order,
compile and save, and a new preparation is accepted in the same process.

The lost-response case attaches a separate Host to the owned TIA, dispatches a
two-screen color edit and terminates only that Host after the first member's
durable import-start stage. The worker finishes and saves before exiting with a
broken transport. A fresh attached Host reconciles the retained `dispatchIntent`
admission in the still-open project: released, not held, no automatic replay.
The terminal change journal and admission count remain unchanged. Independent
exports match both candidates. The owner then closes normally; a fresh TIA
process opens the saved project read-write on the first attempt and verifies it.

The intermediate timing harness attempt at `D:/h206b2` stopped before its first
single-screen write because it supplied an empty `additionalScreens` array.
The API correctly refused that malformed request. The runner now omits the field
for single-screen requests, with a file-only regression. Failed-run evidence and
its successful owned-process cleanup are retained; it is not a passed run.

All tests use owned offline copies. They do not run HMI runtime or project
scripts, download equipment, verify visual appearance, cover every component or
eight-screen batch, or establish shared-resource/other-HMI preservation.
Catalog qualification and 1.0 release acceptance are not promoted by this record.

The final run at `D:/h206b4/report.json` passed all ten named checks. Both owned
TIA lifetimes closed normally, as did the fresh attached reconciler. The
deliberately terminated attached Host and its worker exited with code 1 after
transport loss; those exits are interruption evidence, not normal cleanup.
The configured archive SHA-256 remained
`c6007f613c89423318e36b3199e3d8b2445bc719e4d91501770dc684a49ed46b`.
The [committed evidence summary](../../evidence/local.hmi.classic-screen.batch.20261103.json)
pins the full Host, worker and helper binary trees and the native/preparation
reports. The reports are also retained under the primary checkout's ignored
`artifacts/evidence/` store; original XML, archives and step responses stay local.

Measured prepare/apply wall times on that fixture:

| Workload | Seconds |
| --- | ---: |
| First batch, including component insertion and cold compile | 21.110 |
| Two separate warm screen color edits | 15.666 |
| One warm batch containing two screen color edits | 6.476 |

Each warm mode has one sample, with separate edits before the batch; captures
are excluded. The warm batch used about 59% less prepare/apply time in this run.
Ordering, compiler caches and workload size limit the comparison; it is not a
general latency guarantee or a benchmark of the earlier intermediate build.

## Final offline follow-up

`Test-Offline.ps1 -Suite All` completed successfully with 2,474 Python tests
(one existing historical native-observation check skipped), all 45 managed suites
and every actual-Host offline/compatibility workflow. This includes the 35
Classic screen artifact/progress checks and 59 file-only HMI repair admission
checks. The final result is `artifacts/pr206-offline-push/result.json`.
An earlier attempt stopped on stale source bindings before they finished
refreshing; it is retained as a failed run, not counted as acceptance.

Fresh portable-package quick validation passed strict hashes, exact ZIP contents
and actual MCP checks before and after relocation
(`artifacts/pr206-package-push/result.json`). Full packaged workflows were not
rerun locally; they remain main/nightly checks. The 35 native step-response hashes,
runner and configured-fixture pins still match. The evidence audit reports 590
retained reports with no new missing or changed entries; previously recorded
historical losses remain explicit. All 737 repository links in the changed
Markdown resolve. The release matrix is current and its 1.0 gate still refuses
with 28 open cells, six deferred cells and 0/44 accepted obligations.

CI on `f90405a4` caught an unregistered native-evidence summary. That summary
was added after the local runner's contract stage, so the successful local run
did not validate the final evidence inventory. Catalog `0.4.132` registers it
and links it to screen editing, capture and composition, retaining `unverified`
qualification for all three features. Contract checks, evidence audit, generated
reference/matrix checks and source bindings are rerun on the complete change.
