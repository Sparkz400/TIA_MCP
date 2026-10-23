# TIA MCP continuation — 9 October 2026

Historical record. See [current status](../STATUS.md) and [remaining TODO](../TODO.md) for the latest checkpoint and work order.

Existing VM proof is accepted for this work. No VM rerun is a delivery gate.

## Clean stopping checkpoint

Stopped at the user's request after attempt 06 became terminal. The full TODO and
full runtime corpus are **not complete**. [Hash-bound checkpoint](../../artifacts/continuation-20261009/clean-stop-checkpoint16.json).

- Native runtime: **48 passed cases, 317 completed steps and 371 passing assertions**; one pre-start isolation failure and 16 unattempted cases. Boolean (8), counters (2) and branches (36) completed; two edge cases passed. [Independent final audit](../../artifacts/continuation-20261009/runtime06-final-audit.json).
- `runtime-edges/explicit-pulse` provisioning stopped before its credential access, protected-card staging or owned CPU startup. All 49 discovery snapshots retained `Teilnehmer` at `00-00-00-00-00-00` through the bounded 30-second wait. The source of that entry is unresolved; the bounded wait did not eliminate this issue.
- [Resource checkpoint audit](../../artifacts/continuation-20261009/audit-isolation06.json): all 85 original card files unchanged, no provisioning credential access/submission or provision claim, exact simulator unregistration, both workers exited zero, exact owned TIA exit verified, and a fresh empty simulator registry. This does not prove current discovery absence or rewrite the original failed admission state.
- Full regression and the strict 711-file package/relocated launcher checks passed. All 28 selected LAD/FBD engineering cases and their 489 XML comparisons remain passed separately from runtime.
- The corpus runner and observer have ended. No next feature implementation was started. Existing user MCP configuration, Siemens registration and unrelated user TIA processes were not changed.

**Credential scope clarification — 12 October:** the blocked cases in attempts 04 and 06 had already validated the generated scratch credential in the earlier offline `simulation_download` callback. The no-access claims above concern the later provisioning stage only. Original native reports and their hashes are retained.

### Resume order

1. Read this checkpoint and the failed `runtime-edges/explicit-pulse` report. Diagnose the persistent pre-start discovery entry using read-only observations and exact native identity evidence. Do not filter zero addresses, replay credential submission, claim absence from an empty registration list, or simply increase the wait until a run happens to pass.
2. Add graceful user-stop handling at a verified per-case cleanup boundary before another long batch. Persist explicit incomplete coverage and retain original reports.
3. [The exact remaining selection](../../artifacts/continuation-20261009/remaining-after-corpus06.json) contains the failed edge case plus 16 unattempted cases, bound to the original suite hashes. It is for a future explicitly separate diagnostic/subset run after isolation is resolved. A subset pass does not complete the full-corpus claim; that still requires one complete 65-case/358-step/478-assertion attempt on a frozen build.
4. Continue the broader PLC integration, graphical runtime, HMI and remote-I/O work listed below. Preserve the accepted VM proof; do not add a VM rerun gate.

## Implemented

- Persistent project capture, comparison, source/comment search and health reports: `project_snapshot_capture`, `project_snapshot_compare`, `project_snapshot_search`, `project_health_report`. Saved results work after host restart without starting TIA. Coverage stays explicit; omitted content is never assumed equal.
- Durable simulator creation receipts and `sim_creation_status`, with exact-identity ownership handoff after the previous worker exits. Unknown creation outcomes still stop admission and are never guessed from a matching name.
- Measured timer assertions, a stop condition that `--continue-on-failure` cannot bypass, bounded card-move retries, separate native artifact roots, project-path preflight and observed worker shutdown.
- Configured subnet, I/O controller and connector inspection through native object associations. An independent populated CPU/ET 200SP fixture now verifies one assigned and three unassigned connectors, reverse owner associations and save/reopen through two fresh MCP sessions.
- Corrected HMI profile discovery: the native hardware type identifier is joined to an exact installed catalog entry. The catalog version is separate from native runtime-version availability.
- Archive recovery now uses short unique staging names and pure checks of TIA's 143-character archive staging-directory limit before saving. Frozen build `0129950970e64a82a1f2f9571b759fc2` passed full regression including 130 core checks and supplies the current standard engineering batch. The fix is included in the pinned package below.
- Runtime lifetime correction: retain the PLCSIM runtime manager until worker disposal, release operation-specific interfaces explicitly and make the host exit nonzero on unverified/nonzero worker exit. Native attempt 02 passed its first case's three assertions but exceeded the ten-second worker-exit deadline. A separate exact-handle diagnostic observed both workers exit later. The pinned build uses a bounded 30-second worker wait and a 75-second launcher wait, drains structured shutdown receipts, and rejects uncertain/nonzero shutdown even after successful assertions. Full runtime qualification remains open.

The catalog contains **79 public tools, 52 prepared mutation kinds and 149 features**.

## Pinned package

Frozen build: `cb6299c72baf44d4b05278d878a536a0`.

Package: [tia-mcp-v21_20261009_081229_c1cbb7f3](../../artifacts/releases/tia-mcp-v21_20261009_081229_c1cbb7f3).

The 711-file package passed strict verification. Manifest SHA-256:
`98fa9f1c237fbf707fa045e70e56fb0ab872adc56f8da252d38f5b70a2423488`.
The [relocated local launcher check](../../artifacts/continuation-20261009/relocated-16-deployment.json)
passed startup, catalog consistency, shutdown and reconnect under a path with
spaces and Unicode. Its simulator and scratch-engineering stages were not requested.
The package is unchanged; this later evidence record is maintained outside it.

## Completed checks

| Scope | Result and exact evidence |
|---|---|
| Complete regression on the pinned build | Passed: 28 Python suites with 376 defined tests (375 passed, one task-local observation test skipped in the isolated tree), 138 core checks, 27 simulator checks, 59 HMI inspection checks, plus library, HMI engineering/model, project index and project-model suites. The identical sources separately passed all six project-lock tests against the retained native observation, including that skipped case. [Full log](../../artifacts/continuation-20261009/final-full-regression-16.log), [native-observation test](../../artifacts/continuation-20261009/project-lock-observed-regression-16-retry.log). |
| Persistent reviews and configured subnet | Passed all 23 tool calls, including source changes, exact selected export comparison, omitted-content reporting, search/health, subnet readback, exact owned TIA exit and offline host restart. [Report](../../artifacts/continuation-20261009/reviews-network-native-03.json). |
| Populated configured remote I/O | Passed independent CPU 1511-1 PN V4.1 → IM 155-6 PN ST V6.4 associations, four connector states, exact native owners/subnet, save/reopen and two fresh MCP sessions with observed owned process exits. Inspection only; module engineering, compile and runtime are separate. [Report](../../artifacts/continuation-20261009/network-populated-native-01.json), [native fixture](../../artifacts/n14/native.json). |
| Empty simulator ownership lifecycle | Passed creation receipt, live-owner refusal, exact surviving-instance adoption after worker exit, power-on to STOP, release and empty-registry checks. [Report](../../artifacts/continuation-20261009/creation-native-05.json). |
| LAD/FBD negative controls | All nine rejection cases passed, including stale declaration and planned caller/instance collision. Complete native XML was preserved and the deliberately changed callee was restored. [Report](../../artifacts/continuation-20261009/typed-negative-native-02/result.json). |
| Remaining standard LAD/FBD engineering | All 28 cells passed import/compile, preservation, save/reopen and checkpoint recovery/reopen on build `0129950970e64a82a1f2f9571b759fc2`. All 84 exact owned TIA exits verified. Independent audit passed 489 XML comparisons. [Batch](../../artifacts/continuation-20261009/typed-standard-engineering-03/batch.json), [audit](../../artifacts/continuation-20261009/typed-final-preservation.json). Runtime is separate. |
| Basic HMI inspection | All five available profiles passed actual inspection and explorer checks; 206 recorded response/row/provider contracts validated. [Native report](../../artifacts/continuation-20261009/hmi-basic-native-01.json), [contracts](../../artifacts/continuation-20261009/hmi-basic-contracts-01.log). |
| Rich HMI relationships | Both Unified profiles passed exact integrated PLC ownership, scalar/DB declarations, direct screen binding, alarm references, dynamic/broken retention, script inspection and explorer checks. 158 recorded contracts validated. Classic inspection passed; its fixture has no software connection rows to associate with the hardware connection. [Native report](../../artifacts/continuation-20261009/hmi-rich-native-02.json), [contracts](../../artifacts/continuation-20261009/hmi-rich-contracts-02.log). |
| Library lifecycle, preceding build `1c5dce3e17c54670a5aa8761a7a60029` | One uninterrupted fresh run passed 63 stages and 17 prepared changes: FB/UDT authoring/release, two archives, consumption, cleanup-risk rejection, referenced update, unrelated-content comparison and exact native links after fresh reopen. The second release changes metadata only. [Report](../../artifacts/l09/nt_69d941cf/qualification.json). |
| Revised timer family, preceding build `1c5dce3e17c54670a5aa8761a7a60029` | Both cases, 10 steps and 18 assertions passed with verified card restoration, simulator unregister and exact owned TIA exits. Elapsed was 233 ms inside measured bounds; completion returned 1000 ms and reset returned zero. [Native report](../../artifacts/continuation-20261009/timers-native-04/runtime-timers/live-result.json), [independent trace audit](../../artifacts/continuation-20261009/audit_timer04.json). |
| Strict retained-content audit | 38 XML pairs matched across Classic screen lifecycle observations and unrelated library-update objects, ignoring only `/Document/DocumentInfo/Created`. XML comments, processing instructions, IDs, whitespace and unknown fields remain significant. The Classic discrepancy was regular-screen enumeration order. [Audit](../../artifacts/continuation-20261009/audit_strict_native_preservation.json). |

The HMI reports deliberately retain failed baseline compile gates and original
ordered Classic persistence/recovery comparisons. Their overall H0–H5 acceptance
is **not passed**. Production HMI mutation remains unavailable because complete
family-specific preservation and native mutation acceptance are unproved.
The strict audit is supplemental evidence, not a rewrite of the original reports.

The [post-run process audit](../../artifacts/continuation-20261009/completed-native-process-audit-01.json)
found all 15 recorded owned Portal process IDs from the completed HMI, library and
negative runs absent. No termination was performed. Where exact process handles
were used, their stronger exit evidence remains in the individual reports.

## Completed attempts and remaining scope

[Runtime attempt 06](../../artifacts/continuation-20261009/corpus-native-06/index.json) is terminal and failed pre-start isolation after 48 passing cases. Its [input manifest](../../artifacts/continuation-20261009/corpus-native-06-inputs.json) retains all 65 cases, 358 steps and 478 assertions unchanged from attempt 04 and binds the current build and prior-state reconciliation. The final audit and stopping checkpoint above describe the exact retained result; no cases are merged across attempts. Full regression, including all 138 core checks, passed before this attempt.

[Attempt 05](../../artifacts/continuation-20261009/corpus-native-05/index.json) stopped at its first `tia_start` with an Openness `AccessDenied` security timeout; no project or simulator operation ran. A new empty startup of the identical worker succeeded without registry changes and verified exact owned TIA and worker exits. The startup diagnostic also requested an unavailable `tia_status` tool, so its original report remains failed; a [separate reconciliation](../../artifacts/continuation-20261009/startup16-reconciliation.json) records only the verified startup/cleanup facts. Attempt 06 is a separate complete run on the same frozen build and unchanged vectors.

The first two observed batches publish [terminal status and audit locations](../../artifacts/continuation-20261009/native-batches-status.json): standard typed engineering passed, while runtime attempt 03 stopped on the long counter fixture's execution deadline. The observer ran read-only raw-sample/XML audits and wrote a separate typed qualification ledger. It did not retry native operations, change projects, terminate processes or convert failed cases to passes.

Runtime attempt 03 passed 15 cases and 329 evaluated assertions, then hit the default 120-second case budget during the long signed-counter saturation sequence. It completed 224 acknowledged calls with no value assertion failure before the timeout. Cleanup, both worker exits and the exact owned TIA exit passed; no uncertain runtime outcome remained. The fixture now has an explicit 300-second host execution budget, with all original inputs, expected values and observations retained. The fresh full run below uses that correction; attempt 03 stays failed and 49 cases remain unattempted in that report.

[Runtime attempt 04](../../artifacts/continuation-20261009/corpus-native-04/index.json) passed ten cases and 312 assertions, including the corrected full counter saturation/reset sequence. Provisioning stopped before its credential access in `runtime-boolean/truth-3` when pre-start PLCSIM discovery reported an existing zero-address endpoint. One case failed and 54 were unattempted. Its [input manifest](../../artifacts/continuation-20261009/corpus-native-04-inputs.json) verifies that only the counter case execution budget changed from attempt 03. The [separate retained-state audit](../../artifacts/continuation-20261009/audit-isolation04.json) verifies all 85 original card files, no provisioning credential submission or staging, exact unregistration and worker/TIA exits. A read-only native probe later found the PLCSIM route absent in ten snapshots; the prior entry's origin remains unknown. The current build requires two consecutive absent/empty snapshots within a bounded 30-second wait, retaining every nonempty result and rechecking exact ownership/OFF/Softbus before and after each read. No runtime cases are merged across attempts into a corpus pass.

- [Runtime corpus attempt 01](../../artifacts/continuation-20261009/corpus-native-01/index.json) stopped after 13 passing cases and one worker-disconnection failure; 23 assertions passed and 51 cases were not started. Crash analysis locates the failure during native runtime-manager shutdown after card restoration. The [retained-card audit](../../artifacts/continuation-20261009/audit_corpus01_retained_card.json) verifies all 85 files against the original manifest, without replay or report rewriting. [Attempt 02](../../artifacts/continuation-20261009/corpus-native-02/index.json) retained all 65 cases but stopped after the first case's shutdown failure. The [focused shutdown trace](../../artifacts/continuation-20261009/diagnose_runtime_shutdown.json) explains the insufficient wait without changing any original report. [Attempt 03](../../artifacts/continuation-20261009/corpus-native-03/index.json) later stopped on the counter fixture deadline as described above. Its passing cases include zero-exit receipts from both workers. Attempt 06 is now terminal as recorded above; no full-corpus pass is claimed.
- The standard LAD/FBD engineering batch is complete: all 28 cells passed separately from runtime. Attempt 01 was rejected by the artifact-path preflight before archive retrieval or typed mutation. Attempt 02 compiled the first FBD multi-instance change and verified preservation, then recovery failed because TIA's archive staging directory was 152 characters (limit 143). Both original reports, changed project and checkpoint remain retained. A [separate exact-checkpoint audit](../../artifacts/continuation-20261009/recover_typed_checkpoint_03.json) passed ten complete XML comparisons across retrieval/fresh reopen and verified both owned TIA exits. Successful [attempt 03](../../artifacts/continuation-20261009/typed-standard-engineering-03/batch.json) uses build `0129950970e64a82a1f2f9571b759fc2`. The aggregate historical ledger now contains 92 attempts over 76 distinct cells, with 73 passing all four native engineering gates and six passing all five including runtime; gates are never combined across attempts. All nine negative controls are complete.
- Remaining graphical runtime and scoped-route cases need their own successful native evidence. Strict insertion still refuses envelopes that cause unrelated native IDs to be renumbered.
- Combined PLC component installation (`library_integrate` with explicit DB/call/parameter/tag mappings), true FB-to-UDT dependency fixtures, behavior-changing/breaking releases and interrupted native-write recovery remain open.
- Full HMI writes, Classic software/hardware connection association, nonempty resource/faceplate/translation coverage, valid HMI compile fixtures and the unavailable RT Professional target remain open. Native visual assessment stays at the user's prior stop.
- Distributed-I/O creation, complete module configurations and multiple controller/shared-device associations remain open. Current network evidence now includes one independently configured remote-I/O device with one assigned and three unassigned connectors; it does not claim physical topology or module/runtime qualification.

Historical failures, source snapshots, runner manifests and native project copies
are retained. No physical PLC/HMI deployment was performed.

## Recommended next increment

Before another long native batch, add a graceful stop request checked only after
the current case has recorded verified simulator, worker and owned TIA cleanup.
Persist an explicit incomplete/user-stopped result and the exact remaining case
selection. Resumed subset results must remain distinct from a single full-corpus
pass. Attempt 06 had no such stop protocol. It is now terminal, and the user-requested
checkpoint has been saved without starting the next feature implementation.

1. **Install a complete PLC component from one reviewed plan.** Extend `library_integrate` to select exact FB/UDT versions, create the instance DB, insert the call and bind explicitly named parameters and tags. This removes the manual coordination between the existing library and engineering operations. The first acceptance fixture should independently verify an actual behavior-changing update, dependency closure, unrelated-content preservation and recovery of every affected resource.
2. **Expand graphical behavior proof.** Add runtime vectors for the typed LAD/FBD routes already proven through compile, preservation and reopen, then qualify scoped destinations and additional instructions. The current generic runtime corpus and typed engineering ledger answer different questions; neither substitutes for typed graphical behavior evidence.
3. **Make HMI changes usable on valid native fixtures.** First establish clean baseline compilation and complete family-specific preservation, then add exact PLC tag bindings and reusable faceplate instances. This would connect PLC component reuse to the operator interface; current inspection success does not yet enable production writes.
4. **Turn configured network inspection into reviewed remote-I/O assembly.** Add exact catalog module selection, slot/channel/address plans and controller assignments, followed by compile, reopen and multi-controller fixtures. The current owner/subnet inspection provides the readback foundation, but does not create modules or prove physical topology.

Existing VM proof remains accepted. These are separate follow-on increments, with
the first item providing the most immediate reduction in repetitive engineering work.
