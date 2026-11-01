# Test Suite job preparation dispatched in TS-Q02 — 1 November 2026

`testsuite_style_job_prepare` stayed open after the style V21 boundary because it also queues application reviews,
whose execution is qualified in the [TS-Q02 mode](testsuite-application-20261030.md). Before this run no native job it
prepared had been dispatched: the TS-Q02 `pass` case called it, but that job was refused at the production gate, and
TS-Q01 and TS-Q02 prepare their jobs inside `qualification_testsuite_prepare`. This record covers two TS-Q02 cases that
queue the fixture case through the public tools and dispatch that job.

Everything ran in owned TIA Portal V21 Update 3 instances (English UI) with Test Suite Advanced V21, in a
worker-created scratch CPU 1511-1 PN V4.1, against one owned PLCSIM Advanced V8 instance on Softbus per case. No user
project or physical PLC was touched; every instance was released, and no operator was needed.

## Runs

Both cases used one frozen Debug build of `4ced3b90` (`TestSuiteQualification=true`, `artifacts/tj1`, `git archive`),
one Host per case with `--testsuite-qualification`, and
[`qualify_testsuite_application.py`](../../scripts/qualify_testsuite_application.py) `--case jobPass` and
`--case jobFail` (42 steps, 1 November 2026 22:27–22:32 UTC, both on the first attempt). Evidence:
`local.testsuite.job-prepare.20261101` (reports `artifacts/q/tj/tq_68318815` and `tq_56f8e66f`, logs beside them).

| Case | Fixture | Result |
| --- | --- | --- |
| `jobPass` | `TiaMcp_Q02_Pass` | the queued job `succeeded`, classification `passed` |
| `jobFail` | `TiaMcp_Q02_Fail` | `recoveryRequired`, classification `assertionFailure`; `qualification_testsuite_recover` into a new folder and `qualification_testsuite_reconcile` `verifiedRestored` |

Each case: the TS-Q02 setup (`tia_start`, `tia_create_scratch`, `project_simulation_enable`, `sim_create`,
`simulation_download`, the generated-state save, `simulation_provision_scratch_credential`, power on, RUN) and
`qualification_testsuite_prepare` (fixture import, scope on the owned instance, compile, save, review and a job that is
never dispatched). Then:

1. `testsuite_application_plan_prepare` reviews the same case again, externally managed on the owned instance with the
   TS-Q02 profile, from the retained manifest and original export. The review is byte-identical to the qualification
   review (same SHA-256), so the public plan tool reproduces it exactly.
2. `testsuite_style_job_prepare` queues it under a new key: a wrong reviewed hash refuses with `ReviewMismatch`, the
   repeated key returns the same job and operation, and the key with a two-run review refuses with
   `IdempotencyConflict`. `testsuite_style_job_status` shows a separate job, one operation, `prepared` at revision 0.
3. `qualification_testsuite_execute` dispatches that job: observation, checkpoint archive, second observation, one
   `TestCaseExecutor.Run(TestCase)`, compile, preservation, fresh reopen with the scope read again, and cleanup.
   `testsuite_style_job_results` returns the classification.

The job `qualification_testsuite_prepare` made alongside stayed `prepared` at revision 0; a prepared operation holds no
resource, so it did not block the dispatch.

## Rows

- **Verified:** `testsuite_style_job_prepare` (7 cells). It queues style, application and system reviews; only
  application reviews have a consumer that runs natively, the TS-Q02 qualification mode. Style reviews have no
  production consumer ([V21 boundary](testsuite-style-q01-20261101.md#v21-boundary)), and system reviews have no
  qualification mode ([28 October record](testsuite-native-20261028.md)).

## Limits

One single-step application case per Run, externally managed, on Softbus, in the qualification build. Production
admission stays closed: `testsuite_style_job_execute_next` still refuses with `TestSuiteProfileUnqualified`. The
result profile matches the exact TS-Q02 fixture tree under the English UI.
