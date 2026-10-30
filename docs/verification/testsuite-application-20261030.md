# Test Suite application tests on owned PLCSIM (TS-Q02) — 30 October 2026

D06 application execution was held only by this repository's production-profile gate. This record covers the
[TS-Q02 qualification mode](../reference/testsuite-production-jobs.md#ts-q02-qualification-mode) and its native runs.
Production admission is unchanged: `testsuite_style_job_execute_next` still refuses with `TestSuiteProfileUnqualified`.

Everything ran in owned TIA Portal V21 Update 3 instances (English UI) with Test Suite Advanced V21, in a worker-created
scratch CPU 1511-1 PN V4.1, against one owned PLCSIM Advanced V8 instance on Softbus per case. No user project or
physical PLC was touched; every instance was released.

## Runs

All four qualifying cases used one frozen Debug build of `d726fc9f` (`TestSuiteQualification=true`, `artifacts/ta3`,
`git archive`), one Host per case with `--testsuite-qualification`, and
[`qualify_testsuite_application.py`](../../scripts/qualify_testsuite_application.py): 72/72 steps. Evidence: `local.testsuite.application.20261030`
(reports `artifacts/q/ta/tq_c7a9864d`, `tq_02c98406`, `tq_de9048c2`, `tq_ed75fda3`).

| Case | What ran | Result |
| --- | --- | --- |
| `pass` | `TiaMcp_Q02_Pass` (`ASSERT.Equal("TiaMcp_EnableGate_DB".Active, true)`) | `succeeded`, classification `passed`, all seven gates |
| `fail` | `TiaMcp_Q02_Fail` (deliberately asserts `false`) | `recoveryRequired`, classification `assertionFailure`; recovery to a new folder and reconciliation `verifiedRestored` |
| `afterRun` | the passing case with the injected lost Run reply | `outcomeUnknown` (no result recorded, no replay); recovery and reconciliation `verifiedRestored` |
| `released` | the instance released between prepare and execute | `SimulationTargetMismatch` at observation, before any checkpoint or Run; the operation stays `outcomeUnknown` |

Each case: `tia_start`, `tia_create_scratch`, `project_simulation_enable`, `sim_create`, `simulation_download`, the
generated-state save, `simulation_provision_scratch_credential`, power on, RUN, then `qualification_testsuite_prepare`
(fixture import, scope set to the owned instance, compile, save, review) and `qualification_testsuite_execute`:
observation, checkpoint archive, second observation, one `TestCaseExecutor.Run(TestCase)`, compile, preservation, fresh
reopen with the scope read again, and cleanup.

Refusals in the `pass` case, none of which changed the job or the project: a released instance's ticket
(`SimulationTargetMismatch`), a tampered ticket (`InvalidSimulationTicket`), a project outside the owned scratch
(`ProjectIsolationRequired`), an application fixture without a ticket (`InvalidRequest`), the production gate on the
qualified review (`TestSuiteProfileUnqualified`, operation still `prepared` at revision 0) and the same case reviewed as
system-managed, where Test Suite would create its own instance (`TestSuiteProfileUnqualified`, before any native call).

## Findings

- **Scope reads back.** `TestCase.GetScope()` returned `ScratchPLC`; the case's `SaveToFile` export carries
  `PLCSIM_INSTANCE_NAME` and the external mode. After the Run's `SetScope` the export was byte-identical to the reviewed
  original, so the results are `scopeVerified` and preservation compared the whole fixture.
- **Result tree.** One Run returns `Application test` → the case (`ScratchPLC[PLCSIM Instance : <owned instance>]`) →
  the step (`Pass`/`Fail`) → the assertion (`Actual: True, Expected: True|False, Assert type: Equal`), plus an
  `Information` footer `Test case(s) execution completed.`. The footer made the exploratory passing run `inconclusive`
  and the failing one an execution error (frozen `b14d63c2`, `artifacts/q/ta/tq_59a7e531` and `tq_ec2a9eac`). The exact
  fixture tree now has a profile ([TestSuiteApplicationResultProfile](../../src/TiaMcp.Core/TestSuite/TestSuiteApplicationResultProfile.cs));
  any other tree, instance or UI language fails closed.
- **Reconciliation compared types, not content.** With frozen `9557c2c5` the `fail` and `afterRun` reconciliations
  refused with `PreservationFailed` for unchanged projects: the checkpoint baseline read back from its file holds
  `Null` tokens where the live capture holds null `String` tokens, and `JToken.DeepEquals` tells them apart. Manifests
  are now compared as they serialize (`d726fc9f`). The TS-Q01 style reconcile path had the same defect.
- **License.** V21 documents `LicenseNotFoundException` for `TestCase.SaveToFile`; that export in the current session is
  the pre-run license observation, and the returned Run is the execution acceptance. No operator observation was needed.

## Limits

One single-step case per Run, externally managed, on Softbus. System-managed mode, case lists, sets (`.ttst`), whole-group
runs, other UI languages and system tests (`SystemTestCaseExecutor.Run`) stay unqualified, and production admission
stays closed. The simulator ticket expires 15 minutes after `sim_create`, so download, prepare and execute run back to back.
