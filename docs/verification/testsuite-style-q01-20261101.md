# Test Suite style jobs in the TS-Q01 qualification mode — 1 November 2026

D06 style execution is held by a missing scope read route: V21 Update 3 has no rule-set scope getter
([28 October record](testsuite-native-20261028.md)), so the TS-Q01 qualification build waits at every scope
observation for an operator at the desk. This record covers the six TS-Q01 cases that ran with that operator on
1 November 2026, 16:43–17:08 UTC. Production admission is unchanged: `testsuite_style_job_execute_next`, `_recover`
and `_reconcile` still refuse with `TestSuiteProfileUnqualified`.

Everything ran in owned TIA Portal V21 Update 3 instances (English UI) with Test Suite Advanced V21, in one
worker-created scratch CPU 1511-1 PN V4.1 per case. No user project, PLC or PLCSIM instance was touched.

## Runs

All six cases used one frozen Debug build of `da5485d9` (`artifacts/tq9`, `git archive`,
`TestSuiteQualification=true`), one Host per case with `--testsuite-qualification`, and
[`qualify_testsuite_native.py`](../../scripts/qualify_testsuite_native.py) `--case <case>`. Evidence:
`local.testsuite.style-q01.20261101` (case roots `artifacts/q/ts5/clean`, `violating`, `stop`, `beforeRun`,
`afterRun` and `ar`, each with its log beside it, and `cases.log`). The summary pins `0ed68e85`, which adds only the
curator fix below to `da5485d9`.

| Case | Fixture | What ran | Result |
| --- | --- | --- | --- |
| `clean` | `TiaMcp_Q01_Clean` | one Run | `succeeded`, classification `passed` |
| `violating` | `TiaMcp_Q01_Violating` | one Run | `recoveryRequired`, classification `assertionFailure`; recovery into a new folder and reconciliation `verifiedRestored` |
| `stop` | `TiaMcp_Q01_Clean`, two reviewed runs | `testsuite_style_job_stop_request` after the first | `[succeeded, stopped]`, classification `passed`; the second run was never dispatched |
| `beforeRun` | `TiaMcp_Q01_Clean` | Run reply lost before the Run | `outcomeUnknown`, no result; recovery and reconciliation `verifiedRestored` |
| `afterRun` | `TiaMcp_Q01_Clean` | Run reply lost after the Run | `outcomeUnknown`, no result; recovery and reconciliation `verifiedRestored` |
| `afterRestore` (root `ar`) | `TiaMcp_Q01_Violating` | restore reply lost | `assertionFailure`; `outcomeUnknown` after the lost restore; reconciliation `verifiedRestored` |

Each case: `tia_start` (owned empty Portal), `tia_create_scratch`, `qualification_testsuite_prepare` (fixture import,
scope on the program blocks, compile, save, durable job), `qualification_testsuite_execute` (observation, checkpoint
archive, one `RuleSetExecutor.Run`, compile, preservation, fresh reopen, cleanup), `testsuite_style_job_results` and
`tia_detach`. The preexisting TIA processes were unchanged, and Host, worker and TIA exited normally. Each lost reply
left exactly one attempt; nothing was replayed.

One refused attempt is not part of the evidence: root `artifacts/q/ts5/afterRestore` stopped at `tia_create_scratch`
with `ProjectPathTooLong`. Its 12-character folder name put the planned scratch path over the native limit, and the
worker refused before any native change. Host, worker and TIA exited cleanly, and the case ran again under root `ar`.

## Operator observations

Every `ui-*` challenge under `<root>/h/testsuite-qualification/` was answered by the operator (the maintainer at the
desk) with screenshots of the native style-guide scope editor and, for prepare, of the Automation License Manager
"Check License Keys" dialog (Test Suite Advanced 21.0: 1 OK, 0 defective). `StyleUiEvidence.Validate` accepted each.
What the screenshots show and what was confirmed otherwise:

- **clean, prepare:** the screenshot showed neither the object selector nor the enabled state; the operator confirmed
  both in chat. All later screenshots show the rule table (`CodeBlock (FB,FC,OB)`, Enabled).
- **WebP:** many screenshots were pasted as WebP. The validator accepts only PNG and JPEG, so they were re-encoded to
  PNG with unchanged pixels; the pasted originals are kept beside each challenge folder as
  `<challenge>-<purpose>-original.webp`.
- **violating, reconcile-recovered:** the screenshot has no title bar. The open project path
  (`...\violating\h\recovered\...`) was confirmed by reading the TIA process's window title (read-only, no Openness
  attach). Later screenshots include the title bar.

## Findings

- **Curation.** The runner keeps each MCP call as `{name, wire}`, which
  [`curate_evidence.py`](../../scripts/curate_evidence.py) did not read, so every run listed no tools. The curator now
  reads the wire's `isError` (`0ed68e85`, with a test).
- **Interruption is response loss.** The three loss cases throw `QualificationResponseLost` inside the worker at
  `beforeRun`, `afterRun` and `afterRestore` (`processCrashClaimed: false`); the worker and TIA keep running. Each
  reconciled without replay. No worker, Portal or Host process was terminated.
- **Reopen ownership.** The fresh reopen kept the scratch's status, so the scratch branch of the 1 November reopen fix
  (`87eb1418`, in this build) ran natively; its watch-owned branch did not.

## Rows

- **Verified:** `testsuite_style_job_status`, `testsuite_style_job_results` and `testsuite_style_job_stop_request`
  (21 cells), which the runner called natively. The four `qualification_testsuite_*` tools now also cite this record
  for the style fixtures.
- **Still open:** `testsuite_style_plan_prepare` and `testsuite_style_job_prepare`: the run builds its review and job
  inside `qualification_testsuite_prepare` through the same `TestSuiteStyleJobs` core, and neither production tool was
  called. `testsuite_style_job_execute_next`, `_recover` and `_reconcile`: production refuses before any native call,
  because no production profile is qualified; the qualification tools share their core but admit only the exact
  UI-observed qualification profile. A production profile needs a repeatable scope observation that V21 Update 3 does
  not offer, or a maintainer decision to admit an operator-observed one. `testsuite_style_job_mark_interrupted` needs
  a process loss that leaves an in-flight intent.

## Limits

One packaged single-rule fixture (`CodeBlock (FB,FC,OB)`) per Run, in a worker-created scratch CPU 1511-1 PN V4.1
under the English UI. Scope and license rest on the operator's observations. Only response loss was injected:
worker, Portal and Host process loss for style jobs is not covered. Production admission stays closed.
