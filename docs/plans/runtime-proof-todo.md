# Runtime proof: remaining work

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

See the [consolidated TODO](../TODO.md) for the current work order. This page retains detailed acceptance criteria and dated progress.

## Controlled comparisons — 12 October 2026

Four fresh comparisons have not reproduced the historical zero Address. The p13
OFF-registration and p14 same-process runtime/interface probes remain accepted
within their original scope. In p15, 111 complete observations around fresh
offline download were route-absent. A separate empty-CPU STOP/power-off/release
lifecycle produced 17 normal IP/MAC endpoints in STOP and 99 absent routes,
including a timely query 64.660 seconds after release. Exact owned cleanup and
source/input/binary preservation were independently audited. These experiments
do not establish the historical row's origin and do not qualify the corpus.

Evidence: `artifacts/p15/audit/offline-download-01-independent.json` and
`artifacts/p15/audit/empty-lifecycle-01-independent.json`. The subsequent fresh
protected-program RUN/shutdown diagnostic retained 64 complete observations:
two normal STOP/RUN endpoints and 62 route-absent results, including three timely
queries starting more than 60 seconds after release. No zero address appeared;
the selected one-call/two-assertion case and exact owned cleanup passed. The
diagnostic itself remains failed because post-RUN card equality failed; all nine
original project companions were unchanged, but one new `.info` file changed
the inventory. The final full PLC snapshot was not reached.
See `artifacts/p15/programmed-runtime-01/report.json`. This does not reproduce
the prior 48-case shared history or identify the old row. Preserve current
isolation checks and single-dispatch rules; do not repeat identical probes or
relax the zero-address guard. Actual runtime requested-stop cleanup and one
frozen 65-case / 358-step / 478-assertion pass remain required.

## Continuation — 9 October 2026

The user resumed implementation and accepted the existing VM proof; no VM rerun is a delivery gate. The 8 October checkpoint below is retained as historical evidence.

**Stopped checkpoint, attempt 06:** [Full native run](../../artifacts/continuation-20261009/corpus-native-06/index.json) ended with **48 passing cases, 371 passing assertions, one pre-start isolation failure and 16 unattempted cases** on frozen build `cb6299c72baf44d4b05278d878a536a0`. The 49 nonempty discovery snapshots in `runtime-edges/explicit-pulse` did not clear within the 30-second bound. The [independent audit](../../artifacts/continuation-20261009/runtime06-final-audit.json) verifies the 48 cases/317 steps/371 assertions; full-corpus qualification remains false. The [resource audit](../../artifacts/continuation-20261009/audit-isolation06.json) verifies the unchanged 85-file card, no provisioning credential access/protected-card staging/startup, exact unregistration and worker/TIA exits, and a fresh empty registry. Discovery origin/absence remains unresolved. The user requested stopping here; no next feature or new native attempt was started. See [resume order and complete checkpoint](../history/continuation-20261009.md#clean-stopping-checkpoint).

**Attempt 05 startup rejection:** the first `tia_start` returned a terminal Openness security timeout before any project or simulator operation. A later empty startup of the exact same worker succeeded and verified owned TIA/worker exits without registration changes; [separate reconciliation](../../artifacts/continuation-20261009/startup16-reconciliation.json). The diagnostic itself retains an invalid status-tool request as a harness error. Attempt 06 retained unchanged vectors and subsequently stopped as recorded above.

**Attempt 04 isolation stop:** the corrected counter case passed all 260 assertions, including saturation and reset; both counter cases total 276 assertions. Edges, latches and three Boolean cases also passed. The run then stopped at `runtime-boolean/truth-3` because pre-start PLCSIM discovery returned a `Teilnehmer` entry at `00-00-00-00-00-00`, before provisioning credential access, protected-card staging or owned CPU startup. The original report remains failed with admission stopped: ten passing cases, one failed case, 312 passing assertions and 54 unattempted cases. The registry later read empty, and an independent read-only probe observed the PLCSIM route absent in ten snapshots. The [retained-state audit](../../artifacts/continuation-20261009/audit-isolation04.json) verifies all 85 original card files, no provisioning credential opening/submission, exact unregistration and both worker/TIA exits. This does not identify the earlier discovery entry.

**Credential scope clarification — 12 October:** attempts 04 and 06 had already completed an offline `simulation_download` callback that validated the generated scratch credential. The later provisioning rejection happened before opening/submitting that credential for provisioning. The original reports remain unchanged; the two phases must not be conflated.

The current build requires two consecutive absent/empty discovery observations, bounded by 30 seconds and 61 attempts, with exact identity/OFF/Softbus and offline-project checks around every read. Every nonempty snapshot remains significant, including zero-address or unnamed entries. Only discovery reads repeat; exceptions and late results never authorize startup. All 138 core checks and the full regression passed. Attempt 06 observed 48 passing cases and then the persistent-entry rejection, so this wait does not establish that the discovery issue is fixed.

**Earlier attempt 03, 9 October:** [corpus attempt 03](../../artifacts/continuation-20261009/corpus-native-03/index.json) passed arithmetic, timers, arrays/UDTs and FB instances, then stopped in the second counter case at the default 120-second execution deadline. Fifteen cases passed; the signed-storage-limit case completed 224 acknowledged calls and 224 assertions before timeout, with no value assertion failure. Across the attempt, 329 assertions passed and 49 cases were not started. Both worker exits, simulator unregistration and exact owned TIA exits passed, with no uncertain runtime outcome. The test fixture now gives this long sequence an explicit 300-second deadline; stimuli, expected values, observation granularity and isolation are unchanged. A fresh full-corpus run is required. [Independent audit of the 15 completed passing cases](../../artifacts/continuation-20261009/runtime-final-audit.json).

Both timer cases also passed on build `0fa1d22835e9487d88b8c48a32da154c` in attempt 03, including all 18 assertions and the structured zero-exit receipts from both workers. This supersedes neither historical failures nor their exact-build evidence.

**Earlier attempts 01 and 02:** [corpus attempt 01](../../artifacts/continuation-20261009/corpus-native-01/index.json) passed all eight Boolean cases and five arithmetic cases (23 evaluated assertions), then stopped all admission after arithmetic case 6 lost the engineering worker during credential provisioning. The simulator was unregistered and the exact owned TIA process exited. A [separate retained-card audit](../../artifacts/continuation-20261009/audit_corpus01_retained_card.json) verified all 85 original card files and the journal's returned credential call. The report remains failed; the remaining 51 cases were not started. Local crash analysis identifies an access violation on a PLCSIM native background thread while the main thread called `SimulationRuntimeManager.Shutdown()`; the original evidence is retained.

Frozen build `667e28ce6451424cb7a29ae454b5b6b9` moves that runtime-manager lifetime from individual download/provision operations to the engineering worker. Instance interfaces still release per operation. The host now exits nonzero if either started worker fails to exit cleanly. Full regression passed with 375 Python tests, 135 core checks and 27 simulator checks. [Corpus attempt 02](../../artifacts/continuation-20261009/corpus-native-02/index.json) passed its first arithmetic case's three assertions and all owned CPU/TIA cleanup, but stopped because the host returned 1 during shutdown.

A [fresh one-case shutdown diagnostic](../../artifacts/continuation-20261009/diagnose_runtime_shutdown.json) retained exact child handles and structured exit receipts. The engineering worker exceeded the host's ten-second exit wait, then both exact workers were observed exited; the simulator worker reported exit 0. No native process was terminated. This does not certify the late engineering worker's exit code or turn the original failed case into a pass. The follow-up raises the worker exit wait to a bounded 30 seconds, lets qualification launchers wait 75 seconds for two sequential worker waits, and retains structured worker receipts in runtime reports. A nonzero, unverified or forced exit still prevents qualification.

**Current timer result:** [attempt 04](../../artifacts/continuation-20261009/timers-native-04/runtime-timers/live-result.json) passed on frozen build `1c5dce3e17c54670a5aa8761a7a60029`: both cases, ten steps and all 18 assertions, native compile/save/reopen, protected-card restoration, unregister and exact owned TIA process exit. Elapsed was 233 ms within measured bounds of approximately 99–391 ms. The completion fixture now uses a one-second preset and independently checks `Elapsed = 1000`; reset returns zero. Attempt 03's 200 ms transition could not be resolved between host calls and remains an inconclusive report. This qualifies the revised timer family, not the complete nine-family corpus.

The same build passed the expanded full regression: 28 Python suites / 375 tests, 127 core checks, 27 simulator checks, and the library/HMI/index/project-model checks in `final-full-regression-09.log`. Its immutable 710-file package passed strict verification and local relocation with spaces/Unicode, shutdown and reconnect. The 15-second card-move retry has native normal-path proof and pure contention/deadline tests; the passing timer run did not reproduce the earlier Windows lock.

- Durable current-user creation receipts now precede native registration. The [13-step native lifecycle run](../../artifacts/continuation-20261009/creation-native-04.json) passed live-owner rejection, exact surviving-instance adoption after owner exit, deletion and empty-registry checks. A separate observer retained the registration; an absent instance or incomplete receipt still cannot reconcile an unknown native call.
- Bridge shutdown now observes worker exit. A redundant broken-pipe flush after clean EOF was fixed; the lifecycle and review runs independently recorded clean native worker exit. Exact owned TIA process handles are also waited by the runtime runner.
- `--continue-on-failure` cannot bypass an uncertain native outcome, including when cleanup itself succeeds. The full gate includes the previously omitted process, persistence, uncertainty and case-isolation regressions.
- Measured timer assertions account for host-call bounds. In [timer attempt 02](../../artifacts/continuation-20261009/timers-native-02/runtime-timers/live-result.json), the start/elapsed/cancel/restart case passed: the elapsed observation was 202 ms, inside measured bounds of approximately 91–337 ms. The second case was blocked before credential submission by Windows access denial while moving the original card. The original card and owned cleanup were verified; this is **not** a passing timer family.
- The two-second filesystem move budget admitted only two attempts in that blocked case because identity, manifest and durable-checkpoint work consumed the remaining budget. The source now uses a shared, independently testable 15-second/61-call maximum. Only the filesystem move may repeat; native CPU actions and credential setters remain single-attempt. Native qualification of that extension is still required.
- The runner now accepts a short `--artifacts` root independently of the frozen binaries. [Attempt 01](../../artifacts/continuation-20261009/timers-native-01/runtime-timers/live-result.json) demonstrated TIA's 143-character project-path limit and clean shutdown before runtime setup; scratch creation now rejects an overlong path before project creation.

The full frozen-build regression passed on `59fb741484b34684a8e12fdf760777af`, including 125 core checks and 27 simulator checks. The later artifact-root changes passed all 163 block-test Python regressions. Newer native attempts must retain their own complete binary/runner evidence; none of these successes rewrite corpus02 or resolve its historical unknown outcomes.

Checkpoint: 8 October 2026, after the corpus finished at 16:01 Europe/Berlin. Work stopped here at the user's request. No rebuild or subsequent native test was started. The protected baseline passes, but the nine-family corpus is not qualified.

## Verified so far

- The installer-required restart occurred at 11:08:12 Europe/Berlin, after installer completion. A fresh empty CPU reached STOP with `PowerOn: OK` and completed owned cleanup.
- [Protected baseline attempt seventeen](../../artifacts/runtime-proof-20261008/protected-program-provisioned-17.json) reached RUN, verified good-quality typed reads and BOOL/INT16 write/readback, and passed all eight assertions. Generated credential provisioning kept protection enabled and restored the original program card with matching hashes. Owned release, connection disposal and host exit 0 passed.
- The [baseline snapshot](../../artifacts/runtime-proof-20261008/evidence-snapshot-20261008T113602982412Z.json) and [binary manifest](../../artifacts/runtime-proof-20261008/provisioned-17-binaries.json) identify the tested build. TIA connection disposal does not independently prove process exit or project-lock release.
- Scoped first-use certificate trust was explicitly authorized for each fresh owned empty CPU. This authorization does not authenticate arbitrary endpoints or permit reuse of a previous CPU's fingerprint.

## Current corpus checkpoint

The [second corpus run](../../artifacts/block-tests/runtime-all-20261008-owned-tls-02/index.json) ended with supervisor exit code 1, `engineeringVerified: true` and `runtimeVerified: false`. It used the retained baseline-seventeen binaries and its [original runner snapshot](../../artifacts/runtime-proof-20261008/corpus02-runner-snapshot/manifest.json). Source fixes made while it ran did not apply to that loaded process.

The [terminal summary](../../artifacts/runtime-proof-20261008/corpus02-terminal-summary-20261008T140209854324Z.json) hashes the index and all nine reports. It records **32 passing, 30 blocked and 3 failed cases**, with **104 passing and 1 failing evaluated assertions**. Of the 477 authored assertions, 372 had no evaluated result. The [final evidence snapshot](../../artifacts/runtime-proof-20261008/evidence-snapshot-20261008T140210455644Z.json) independently reconfirms restart, empty startup and the protected baseline, while keeping nine-family qualification false.

| Family | Report status | Passed cases | Blocked cases | Failed cases |
| --- | --- | ---: | ---: | ---: |
| Boolean | Blocked | 6 | 2 | 0 |
| Arithmetic | Blocked | 5 | 3 | 0 |
| Branches | Failed | 16 | 19 | 1 |
| Edges | Blocked | 2 | 1 | 0 |
| Latches | Passed | 2 | 0 | 0 |
| Timers | Failed | 0 | 1 | 1 |
| Counters | Blocked | 1 | 1 | 0 |
| FB instances | Blocked | 0 | 2 | 0 |
| Arrays/UDTs | Failed | 0 | 1 | 1 |

The three failed cases are the branch report-replacement error, the timer elapsed assertion, and a disconnected engineering worker during the final array/UDT credential-provisioning attempt. Only the latches family reported a complete pass. These are retained report outcomes, not a claim that later successes reconcile earlier unknown native outcomes.

The original target is nine families, 65 cases, 358 steps and 477 assertions. Partial passing cases do not qualify a complete family or the corpus. Historical failures must remain visible; this run is not eligible for a composite pass from selected retries.

## P0 — reconcile resources before another native run

- [x] Record a post-run read-only inventory. [PLCSIM status at 16:02:27 Europe/Berlin](../../artifacts/runtime-proof-20261008/residual-sim-status-20261008T1402Z.json) showed no registered instances and host exit 0. The [process observation at 16:02:59](../../artifacts/runtime-proof-20261008/checkpoint-processes-20261008T1403Z.json) found no process with a visible executable path under the proof directory and no former supervisor PID. Its only matched TIA candidate was the Portal server started at boot, with an unavailable executable path. This limited observation does not establish all native child exits or resolve historical unknown outcomes.
- [ ] Refresh exact instance/process ownership and state immediately before resuming. Do not terminate or adopt unrelated instances or processes.
- [ ] Resolve `runtime-branches/value-10-mode-1`: native `sim_create` timed out without returning an identity. The old caller treated missing identity as no instance and continued. Determine whether registration occurred using durable native evidence; do not infer ownership from an ID, timestamp or matching prefix alone.
- [ ] Resolve the three failed protected-card restorations: `value--10-mode-2`, `value-0-mode-2` and `value-9-mode-1`. The original cards remain retained with matching 85-file manifests. Confirm exact ownership, OFF state and handle release before any recovery. Preserve the staging/empty-card backups and helper journals; do not replay credential submission.
- [ ] Reconcile the branch report-write failure at `value--10-mode-0`. A `sim_write` audit entry remains `started` without a response. The native outcome remains unproved even though persistence-before-dispatch is a plausible explanation.
- [ ] Investigate the [final arrays/UDTs failure](../../artifacts/runtime-proof-20261008/arrays-final-failure-audit-20261008T172131480112Z.json), `fresh-inout-initialization`: provisioning returned `WorkerDisconnected`, and subsequent TIA detach returned `BridgeFaulted`. The helper recorded only `attemptPrepared`; its summary is empty, so later credential submission, card restoration and the exact failure phase remain unknown. Exact simulator unregistration and host exit 0 passed. Establish the interrupted native outcome and preservation evidence before further testing; a later empty registry does not settle that operation.
- [ ] Establish why the singleton provisioning guard found other registered instances. Concurrent native clients were observed, but their contribution to any particular failure is not proved. Arrange a suitable exclusive test window before resuming; leave other users' work untouched.

The [helper diagnosis](../../artifacts/runtime-proof-20261008/corpus02-helper-diagnosis-20261008T132339427781Z.json) records exact phases, error codes, paths and retained-card hashes. Three additional cases failed while staging the original card, before opening/submitting the credential. Windows access denial is proved; native handle contention, another client and memory pressure are not established causes.

## P0 — finish the corrections already prepared

- [ ] Integrate and build the guarded filesystem move retry in [OpennessSession.SimulationProvision.cs](../../src/TiaMcp.Openness.V21/OpennessSession.SimulationProvision.cs) into a **new isolated build directory**, preserving the tested baseline-seventeen files. This source change is unbuilt. Seventeen source-extracted pure checks passed; native restoration remains unverified.
- [ ] Verify the new process-lifetime, project-open and report-persistence behavior in [run_block_tests.py](../../scripts/run_block_tests.py) and [block_test_process.py](../../scripts/block_test_process.py). The source retains an exact owned process handle, waits for observed process exit, bounds retries to the exact project-lock error before CPU creation, and retries only permitted report-replacement errors. Process exit alone does not establish lock release.
- [ ] Verify the new uncertainty gates in [block_test_runtime.py](../../scripts/block_test_runtime.py) and [block_test_executor.py](../../scripts/block_test_executor.py). Unknown creation, transport/native outcomes, failed card restoration and unresolved isolation must stop further cases after independent cleanup attempts. `--continue-on-failure` must not bypass `stopped_due_to_uncertain_runtime`. Focused pure regressions and independent review passed; the current corpus uses the earlier behavior.
- [ ] Add durable native creation intent before `RegisterInstance` and an explicit exact-identity reconciliation route for registration timeouts. The new Python gate stops continuation but does not recover an instance whose identity was never returned.
- [ ] Qualify native worker exit separately from host exit and connection disposal. Simulator interface disposal/Shutdown does not power off or unregister an instance, and disposing a bridge process handle does not wait for process exit.

The [checkpoint source archive](../../artifacts/runtime-proof-20261008/checkpoint-source-20261008T135923361968Z/manifest.json) retains the prepared implementation and regression checks separately from the loaded corpus runner. The card retry is restricted to the three filesystem move sites, qualifying Windows access/sharing/lock errors, at most nine move calls and a two-second retry-admission budget. Every retry rechecks the owned target, OFF state, paths and original manifest. Guard/native calls can exceed that budget. Credential setters and native CPU actions are never retried. Empty runtime-card metadata is not treated as proof of byte preservation; do not read or hash opaque protection-store contents.

## P0 — correct the timer test's timing basis

- [ ] Preserve the [timer assertion failure and independent audit](../../artifacts/runtime-proof-20261008/timers-failure-audit-20261008T135715230644Z.json): `start-elapsed-cancel-restart/elapsed` observed `Elapsed = 239 ms` against `60 ± 50 ms`. Both the output and native timer state returned 239 with good quality. The actual DUT-call separation is only bounded at approximately 90–493 ms by the retained host-call intervals, so this result does not establish a PLC timer defect.
- [ ] Replace the assumption that a requested host sleep equals elapsed PLC execution time with a measured, documented timing basis. Account for transport, polling, report persistence and scheduling. Do not merely widen the tolerance until this sample passes.
- [ ] Recheck cancellation, restart, completion window and reset. The first case stopped after the failed elapsed assertion; the second case was blocked at project open. These behaviors remain untested in this run. Hash and archive the revised suite as a new test scope.

## P1 — qualify the new build and complete the corpus

- [ ] Record the new source, runner, suite and binary manifests, including relevant dependencies and installed native versions. Run the required build, feature-contract and worker-lifecycle checks against that exact build. Keep concurrently edited project features in their own evidence scopes.
- [ ] Rerun the fresh empty-CPU check, then a new protected baseline (next available attempt number, expected eighteen). Require RUN, typed reads/write/readback, all eight assertions, unchanged protected-card hashes and verified owned cleanup. Reuse the already authorized scoped trust policy; no blanket trust or protection disablement.
- [ ] Start a fresh nine-family run only after the resource and cleanup uncertainties above are resolved. Use the corrected runner and new immutable output directory with explicit host, engineering-worker and simulator-worker paths. Do not overwrite corpus01/corpus02 or combine their unresolved histories into a pass.
- [ ] Require every family/case/step/assertion in the authored manifest, including retained-state, counter boundaries, timer sequences, FB independence and array/UDT coverage. The current frozen target is 65 cases / 358 steps / 478 assertions; older 477-assertion counts are historical. Record and independently audit any further replacement before launch.
- [ ] Independently check recorded native values, quality, exact identities, original/restored revisions, exports, assertions and cleanup; create a new evidence snapshot. Selected-case retries can diagnose a problem but do not qualify the full corpus.
- [ ] Update the current verification/deployment documents and catalog with the final exact scope. A local pass does not qualify the deployment VM, arbitrary PLCs, untested instruction profiles or every supported data type.

## Evidence and stop decision

Preserve the protected baseline reports, both corpus directories, active-run source snapshot, native helper journals and retained memory-card directories. Readers of active JSON reports must use Windows sharing that permits delete/rename; an ordinary reader caused a report replacement failure during this run.

Automatic approval review rejected force-stopping the Python supervisor because native workers, simulator instances or locks could remain unreconciled. The user chose to let it finish; no supervisor stop was executed. The [decision record](../../artifacts/runtime-proof-20261008/corpus02-stop-decision.json) retains that outcome. The prepared `stop-proof-supervisor.ps1` is not a resume instruction.
