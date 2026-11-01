# Test Suite style jobs in the TS-Q01 qualification mode — 1 November 2026

D06 style execution is held by a missing scope read route: V21 Update 3 has no rule-set scope getter
([28 October record](testsuite-native-20261028.md)), so the TS-Q01 qualification build waits at every scope
observation for an operator at the desk. This record covers the six TS-Q01 cases that ran with that operator on
1 November 2026, 16:43–17:08 UTC. Production style execution is a V21 boundary (see [below](#v21-boundary)):
`testsuite_style_job_execute_next`, `_recover` and `_reconcile` refuse with `TestSuiteProfileUnqualified`.

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
- **V21 boundary** (`unavailable_target`, [below](#v21-boundary)): `testsuite_style_job_execute_next`, `_recover`,
  `_reconcile`, `testsuite_style_plan_prepare` and `testsuite_style_job_mark_interrupted` (35 cells, blocked).
- **Still open:** `testsuite_style_job_prepare`. It also queues application reviews, whose execution is qualified in
  the TS-Q02 mode, so it does not follow the style boundary. The TS-Q02 pass case called it natively, but that job was
  refused at the production gate and never ran, and TS-Q01 prepares its jobs inside `qualification_testsuite_prepare`.
  A native run that calls it on a qualification review and dispatches that job through `qualification_testsuite_execute`
  verifies it; TS-Q02 needs no operator.

## V21 boundary

Decided by the maintainer on 1 November 2026: production style execution is a V21 boundary, and no operator-based
production profile is offered. V21 Update 3 offers no API to re-check a style rule's scope at execution time (the live
rule set has only `Name`, `CopyScope`, `Delete`, `SaveToFile`, `SetScope` and `ShowInEditor`;
`local.testsuite.scope-probe.20261028`), so a production run cannot prove the scope it executed. The qualification mode
above, with the operator observing the scope editor at every challenge, is the only route that ran.

| Row | Why it is blocked |
| --- | --- |
| `testsuite_style_job_execute_next` | refuses with `TestSuiteProfileUnqualified`; the run cannot prove its scope |
| `testsuite_style_job_recover` | a production style operation never reaches a recoverable state |
| `testsuite_style_job_reconcile` | reconciliation would need the same scope re-check |
| `testsuite_style_plan_prepare` | its style reviews have no production consumer but `_execute_next` |
| `testsuite_style_job_mark_interrupted` | it acts only on in-flight intents, which production never creates |

The ledger's `dispositions` record each row (`docs/plans/release-obligations-20261015.json`); the rows do not count
toward 1.0 and are not a pass. Application reviews carried by `_execute_next`, `_recover` and `_reconcile` run only in
the TS-Q02 qualification mode ([record](testsuite-application-20261030.md)); their production admission stays closed.
If a later V21 update adds a scope read route, the rows reopen.

## Limits

One packaged single-rule fixture (`CodeBlock (FB,FC,OB)`) per Run, in a worker-created scratch CPU 1511-1 PN V4.1
under the English UI. Scope and license rest on the operator's observations. Only response loss was injected:
worker, Portal and Host process loss for style jobs is not covered. Production style execution is a V21 boundary.
