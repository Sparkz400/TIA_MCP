# Test Suite definitions, scope probe and execution blockers — 28 October 2026

D06 (Native Siemens Test Suite) owned 20 release-matrix rows with 119 open cells. This record covers the
native gate, two native runs that verify five rows, and the blocker recorded for the other fifteen.

Everything ran in owned TIA Portal V21 Update 3 instances and owned scratch projects (CPU 1511-1 PN V4.1);
nothing touched a user project, a PLC or PLCSIM, and no Test Suite executor ran.

## Gate: product and service

Test Suite Advanced V21 is installed (`HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP21\TestSuiteAdvanced`,
`Siemens.Engineering.TestSuite.dll` in the V21 PublicAPI). On a fresh owned scratch, `testsuite_application_inventory`
and `testsuite_system_inventory` both observed the `TestSuiteService` and reported empty, untruncated groups. Strict
imports and `SaveToFile` exports of application and system test cases succeeded without a license refusal. The gate
passed, so the definition rows were qualified natively.

## Runs

Both runs used one frozen Debug build of `8398c50` (`D:\wp\ts02`, source manifest and commit retained there).

| Runner | Domain state | Result | Evidence |
| --- | --- | --- | --- |
| [`qualify_testsuite_definitions.py`](../../scripts/qualify_testsuite_definitions.py) | every program export and member, plus both Test Suite inventories with each case's `GetScope` target or OPC UA settings and the SHA-256 of a fresh native `.tat`/`.tst` export | Passed, 57/57 steps | `local.testsuite.definitions.20261028` |
| [`qualify_testsuite_definitions.py --scope-probe`](../../scripts/qualify_testsuite_definitions.py) | every program export and member, plus both (empty) Test Suite inventories | Passed, 16/16 steps | `local.testsuite.scope-probe.20261028` |

The definitions run follows the [engineering-kinds](engineering-kinds-20261028.md) shape:

- **Apply:** `testsuite_application_import` imports the packaged native `.tat` and a renamed copy;
  `testsuite_system_import` imports the packaged native `.tst`. Each preview is asserted (case names, source SHA-256,
  `ImportOptions`/load options `None`) and applied with checkpoint, PLC compile and save. `change_status` must equal
  the apply result.
- **Preserve:** after each import, the complete state differs from the previous one in exactly the new case and the
  group count. No side effect was tolerated or observed.
- **Complete:** both inventories report an observed empty group first; with two application cases, `maxItems=1`
  reports `truncated` with `testCaseCount` 2.
- **Persist:** a fresh owned Portal reopens the saved project with the identical state.
- **Restore:** one more import per kind restores from its checkpoint to the reopened state. An import whose reviewed
  name misses the file's case imports natively, fails readback with `MutationVerificationFailed` before saving, stays
  `recovery_required`, and restores to the reopened state.
- **Refuse:** an unknown `projectName` (`ProjectNotFound`, both inventories); relative paths, the other family's file,
  set names with a `.tat`, empty name lists (`InvalidRequest`); a wrong `previewHash` (`PreviewMismatch`); an existing
  case (`ObjectExists`). None changed the state.

The scope-probe run uses a Host started with `--testsuite-scope-probe`, whose worker owns a visible Portal. It
refuses read before prepare (`InvalidRequest`) and a foreign project path (`ProjectIsolationRequired`). Prepare then
imports the packaged style rule and sets its scope on Program blocks once, saved, with a flushed intent and
`SetScope` return and no executor. Every program export and member and both empty Test Suite inventories stay
unchanged. A second prepare is refused with `RecoveryRequired` and not replayed, and read returns the rule with an
identical native export. After a fresh owned Portal reopens the identical state, the probe refuses to adopt the
reopened project (`ProjectIsolationRequired`).

## Findings

- **No scope read route on Update 3.** The live rule set exposes only the `Name` attribute and the invocations
  `CopyScope`, `Delete`, `SaveToFile`, `SetScope` and `ShowInEditor` (scope-probe observation). Effective style scope
  therefore still cannot be read back through Openness.

- **Export round trip.** A fresh export of an imported case equals the imported file except for two trailing empty
  lines that TIA appends. Repeated exports of one case are byte-identical, so the inventory state is stable.
- **Scope-probe refusals were untyped.** `qualification_testsuite_scope_probe` returned raw JSON and let the
  worker's `BridgeException` escape, so the MCP SDK replaced every refusal with its generic message and the code
  (`ProjectIsolationRequired`, `RecoveryRequired`) was lost. The tool now returns a typed error result like the other
  tools. `scripts/test_testsuite_production_host.py` now asserts the code; it fails against the previous Host
  (`KeyError: 'structuredContent'`) and passes 46/46 against the fixed one.
- **Imports compile the PLC software.** The import kinds take the PLC compile (one target, the sole PLC's software),
  not the all-devices compile.

## Blocked rows

**Style job lifecycle** (`testsuite_style_plan_prepare`, `testsuite_style_job_prepare`, `_status`, `_stop_request`,
`_execute_next`, `_results`, `_mark_interrupted`, `_recover`, `_reconcile`): no production Test Suite profile is
qualified, so `_execute_next`, `_recover` and `_reconcile` refuse with `TestSuiteProfileUnqualified` on Host and
worker. No native run, result, interruption or recovery exists for `AC-native` and `AC-recovery`. Qualifying the
TS-Q01 style profile needs complete effective scope, and the installed V21 Update 3 PublicAPI still exposes
`RuleSet.SetScope` and `CopyScope` but no scope getter.

**TS-Q01 qualification tools** (`qualification_testsuite_prepare`, `_execute`, `_recover`, `_reconcile`): the
qualification build (`--testsuite-qualification`, [`qualify_testsuite_native.py`](../../scripts/qualify_testsuite_native.py))
substitutes an operator observation for the missing getter. At each prepare, reopen and reconcile observation it
writes a challenge and waits up to 12 minutes for a screenshot of the expanded scope tree in the TIA rule-set editor
and, at prepare, an Automation License Manager key check. That needs a person at this PC's desktop. It was not run:
driving the user's live desktop from an agent session was ruled out.

**Application and system execution** (`testsuite_application_plan_prepare`, `testsuite_system_plan_prepare`):
`TestCaseExecutor.Run` and `SystemTestCaseExecutor.Run` have no qualification mode, and the production gate refuses
them, so `AC-native`, `AC-recovery`, `AC-runtime` and `AC-simulator-lifecycle` have no native run. Qualifying them
needs a TS-Q02 qualification mode with an owned PLCSIM Advanced instance.
*Update 30 October:* application execution now has that mode and ran natively on an owned PLCSIM instance
([TS-Q02 record](testsuite-application-20261030.md)); system execution is still blocked.

**Interruption.** Native interruption of Test Suite style jobs (the open interruption TODO item) needs a dispatched
style job, so it is blocked with the style lifecycle. The two import kinds are target-dependent `change_apply` kinds
and are covered by that route's qualification ([interruption coverage](interruption-coverage-20261027.md)).

## Next step

A user-assisted TS-Q01 run on this PC, from a `TestSuiteQualification=true` build:
`python -B scripts/qualify_testsuite_native.py --host <host> --worker <worker> --root <new dir> --case clean`
(then `violating`, `stop`, `beforeRun`, `afterRun`, `afterRestore`). For each challenge, the operator expands the
scope tree of the shown rule set, takes the screenshots, runs the ALM check and writes `observation.json` next to
`challenge.json` in the form `StyleUiEvidence.Validate` requires.
