# TS-03 production Host integration

**TS-05 application tests:** [Application-test inventory, strict import and owned-simulator run reviews](#ts-05-application-tests) reuse this job lifecycle. Production execution stays behind the same disabled profile gate; the [TS-Q02 qualification mode](#ts-q02-qualification-mode) runs one owned fixture case on an owned PLCSIM instance.

**Offline supplied-result review:** Two separate [result intake/review tools](testsuite-result-review.md) now retain supplied packets, page exact findings and export JUnit without invoking this native execution queue. The ten style tools below retain their original gating.

**16 October owning-worker continuation:** [Gated worker integration and scope-editor evidence](../verification/testsuite-worker-integration-20261016.md) now connect the existing Host adapter to the owning-worker protocol and optional library. The earlier “not loaded or registered” statement below describes the initial TS-03 handoff and is superseded. An owned GUI scratch supplied independent selected-scope screenshots and exact normal cleanup receipts; the historical observation is not a repeatable worker-bound scope provider. Native effective-scope, current execution-license and complete lifecycle providers remain unqualified; execution is still disabled and TS-Q01 execution has not run.

Implemented on 16 October 2026. The managed baseline was independently rerun at **233 checks** before changes. That initial expanded suite passed **354 checks**, including **121 new queue/adapter/recovery checks**. These are authored fake observations and retained-file tests, not Siemens execution or native qualification.

**Offline identity-text repair:** The [current verification record](../verification/testsuite-identity-text-20261016.md) covers exact Unicode identities, refusal before serialization/hashing, retained review validation and four actual MCP Host lifetimes. The targeted managed suite passes 549 checks; the Host harness passes 46. JSON controls remain permitted in raw diagnostic text, but are refused in bounded identity selectors. Malformed escaped UTF-16 in transport arguments is rejected by the installed MCP SDK before tool dispatch with a generic RPC error; Core retained-artifact parsing returns `InvalidTestSuiteArtifact`. Neither refusal opens a native worker.

`TiaTools` exposes ten local tools through [TestSuiteTools.cs](../../src/TiaMcp.Host/TestSuiteTools.cs). Registration uses the existing partial type; no native worker starts to prepare, review, query, retrieve results, stop or mark an interruption. All current production execution/recovery profiles are disabled in code. There is no Host flag, JSON Boolean or caller-authored qualification receipt that enables them.

| Tool | Bounded operation |
| --- | --- |
| `testsuite_style_plan_prepare` | Select exact captured definitions and scope into one to 32 ordered runs; retain complete plans, original definition bundles, project effect/checkpoint obligations and profile. |
| `testsuite_style_plan_review` | Reopen the complete review and restore every immutable bundle; no truncated membership preview. |
| `testsuite_style_job_prepare` | Bind the reviewed digest and idempotency key to all shared durable operation IDs. Preparation has no native effects or reservation. |
| `testsuite_style_job_status` | Reopen operation revisions, progress, pending reconciliation and persistent stop state. |
| `testsuite_style_job_execute_next` | Explicitly execute exactly the next unattempted run, once, through an admitted adapter; currently refuses with `TestSuiteProfileUnqualified`. |
| `testsuite_style_job_results` | Return raw/result/scope/assessment artifact references and operation evidence, rechecking retained hashes and recomputing reports. |
| `testsuite_style_job_stop_request` | Persist stop before stopping remaining prepared operations; stop remains effective after Host restart or an interrupted stop response. |
| `testsuite_style_job_mark_interrupted` | Explicitly mark checkpoint/dispatch/recovery ambiguity unknown, retaining reservations. Refuses while that job's execution lock is held. |
| `testsuite_style_job_recover` | Reserve exact new recovery destinations before a single restore; currently qualification-gated. |
| `testsuite_style_job_reconcile` | Obtain independent original/destination observations through an adapter before shared-store reconciliation; currently qualification-gated. |

The plan request contains `target` (`NativeTargetObservation`), `executionProfile`, `definitions:[{manifest,originalArtifact:{relativePath,sha256}}]`, and `runs:[{selectedDefinitionIds,excludedDefinitionIds}]`. Originals must already exist under the configured Host artifact root. Up to 1,000 definitions and 32 MiB of originals are admitted for one review; each retained artifact is bounded to 64 MiB. Existing planner/bundle limits still apply. Application/system definitions, mixed resources, unresolved syntax, excluded required coverage and incomplete review scopes cannot become style operations. Caller-authored observations can describe a review but cannot establish native admission.

Each review returns its complete content and an artifact reference. Review its SHA-256, then supply it as `reviewedSha256` to job preparation and mutations. Execution additionally requires the exact `operationId` and current `expectedRevision`. There is no automatic runner, restart resumption, timeout takeover, retry, import or scope mutation. Repeated preparation is idempotent; repeated execution of an attempted operation is refused, including when the original successful response was lost.

## Persistence and adapter contract

[TestSuiteStyleJobs](../../src/TiaMcp.Core/TestSuite/TestSuiteStyleJobs.cs) retains content-addressed blobs and immutable job membership under `testsuite-jobs/`. All native progress, checkpoint/dispatch/recovery intents and resource reservations remain authoritative in the existing `EngineeringOperationStore`. Its canonical project resource key also conflicts with other engineering domains and legacy mutation admissions. All cooperating Hosts must use the same artifact root.

One process-independent execution lock covers a job attempt. A separate short control lock orders stop requests against checkpoint/dispatch admission, allowing stops and status during a blocking native call. A stop before intent prevents it; a stop after dispatch intent leaves that invocation in flight and prevents the next run. A stop after checkpoint cannot release project ownership without reconciliation. A native list call, if a future profile qualifies it, stays one dispatch; it is never advertised as cancellable or split after it starts.

[ITestSuiteStyleAdapter](../../src/TiaMcp.Core/TestSuite/TestSuiteStyleAdapter.cs) is a trusted code boundary, not an MCP-supplied object. Qualification must succeed before native lease acquisition. Its session owns native exclusive access across reobservation, flushed intent and invocation. The orchestrator restores the original bundle and checks:

- Exact project, source/content, session epoch, adapter/profile, hardware and native identities at both checkpoint and dispatch boundaries.
- Current observed product/license/style-service/scope probes with retained receipts bound to that exact installation/profile/project/session.
- Every reacquired original definition byte against the retained bundle.
- Independent scope observations and their exact run-bound receipt, including missing, ignored and unexpected objects. Only actual readback or a separately qualified exhaustive proof profile can qualify; SetScope returns and diagnostic mentions cannot.

The adapter must provide genuine checkpoint/preservation material and independent compile, preservation, fresh reopen and cleanup gates. The operation store keeps exactly one checkpoint per planned resource, so the worker returns one `checkpoint.json` manifest that pins the project archive and the preservation baseline by SHA-256; restore and reconciliation read both through it. Fresh reopen must compare the frozen preservation expectation in a different native session epoch; it cannot reuse the old operation for execution in that epoch. Managed artifact validation proves retention and binding, not the truth of those native observations. No synthetic production probe or passing verification implementation is supplied.

Native raw response bytes are flushed before parsing, result normalization or verification. `dispatch.rawResults` is committed while ownership is held. Unknown fields, duplicate JSON fields and malformed captures cannot disappear through normalization. Successful reports bind raw, normalized result, independent requested/effective scope and assessment hashes. Reopened result classification is independently recomputed; a forged passing label is refused. Assertion failure, execution failure and incomplete evidence retain a `recoveryRequired` operation. A missing response retains `outcomeUnknown`. Resource reconciliation does not mean tests passed and cannot manufacture a missing result. A cleanup failure cannot release a successful-looking result or reconciliation. Use the existing bounded `artifact_read` tool with a returned `relativePath` to transfer original raw/report bytes.

Recovery reuses `ReserveRecovery`, `RecordRecoveryEvidence` and `Reconcile`. The retained archive hash is checked before destination admission; originals and destinations cannot overlap. A returned restore stays reserved pending independent evidence. A lost restore response is not retried. Every admitted destination needs `verifiedRestored`, `verifiedAbsent` or `retainedPartial` evidence, and every original resource needs its own disposition.

## Native boundary and remaining prerequisites

[TiaMcp.TestSuite.V21](../../src/TiaMcp.TestSuite.V21/) is an optional **library**, compiled against the installed public V21 Base/TestSuite assemblies. It contains the evidenced single-rule `RuleSets.Find`, `RuleSet.SaveToFile`, `GetService<RuleSetExecutor>` and `RuleSetExecutor.Run(RuleSet)` routes, a one-invocation guard, immediate flushed return receipt and bounded recursive raw result serialization. It starts/attaches no TIA instance, imports nothing and never calls SetScope. It makes no assertion classification from diagnostic text. Missing effective-scope proof stays false.

The native-side gate, as well as the Host-side gate, currently refuses every profile. The optional library is **not loaded or registered into the owning worker yet**: the missing current product/license observation provider, complete effective-scope provider and qualified checkpoint/preservation/fresh-reopen/recovery session must be supplied and qualified together before connecting it. This is deliberate fail-closed behavior, not an enabled production execution profile. The full durable workflow is exercised through the same managed adapter interface using fakes; no native call was made in this lane.

Smallest proposed **TS-Q01** packet, to be scheduled solely by the native queue owner after WT-05:

1. Freeze the exact Host/Core/optional-adapter source and binary hashes, public API/product identities, owned scratch project and original definitions, independent expected findings, complete inventory/preservation baseline and exact process/session ownership. Use one existing rule per run and no simulator or endpoint.
2. Resolve and review a complete scope route **first**: independently readable scope, or a separately reviewed exhaustive owned-container proof profile covering every applicable object, silent clean objects, ignored objects and out-of-scope controls. A successful SetScope or the retained three diagnostics cannot satisfy this step. If unavailable, retain refusal and stop before checkpoint/Run.
3. Observe fresh product, execution-license and service prerequisites without treating service/DLL presence or the old availability return as license proof. Test missing product/license, stale definition/session and incomplete scope refusals before effects.
4. Run one independently authored clean and one deliberately violating fixture as two separately reviewed single dispatches. Retain genuine archives/manifests, flushed intent/return/raw trees, exact object findings, independent scope, compile, unchanged peers, save and fresh reopen. Stop after the first run and verify the second remains uninvoked.
5. In a separately admitted failure window, interrupt after dispatch intent/return, reopen the Host, prove replay refusal and retained project contention, then perform explicit archive restoration to a new reserved location. Independently reconcile original and recovered copies and verify actual cleanup. Only those exact demonstrated profiles may be proposed for enablement.

Application/system execution, scope authoring/import, native list/group profiles and CI provisioning remain outside this increment.

## TS-05 application tests

The inventory and the `.tat` import are natively qualified (`local.testsuite.definitions.20261028`); `.ttst` imports have not run natively ([record](../verification/testsuite-native-20261028.md)). Execution ran natively only through the [TS-Q02 qualification mode](#ts-q02-qualification-mode) ([record](../verification/testsuite-application-20261030.md)). Ledger D06.

| Entry | What it does |
| --- | --- |
| `testsuite_application_inventory` (read-only) | Lists the project's `ApplicationTestGroup`: test cases with `TestCase.GetScope()`, test sets and counts, bounded by `maxItems` with `truncated`. `export=true` saves each listed case with `TestCase.SaveToFile` as `.tat` and returns path and SHA-256. Sets are listed by name only: V21 Openness has no `ApplicationTestSet` export. |
| `change_prepare` kind `testsuite_application_import` | Strict import of a supplied `.tat` (`TestCaseComposition.LoadFromFile`) or `.ttst` (`ApplicationTestSystemGroup.LoadFromFile`) with `ImportOptions.None` and load options `None`. Existing names refuse at prepare and apply. The file is copied byte-exact into the change directory. The native return value and `Find` readback must equal the reviewed names. Usual checkpoint, apply and restore; see the [kind table](../guides/engineering-workflows.md). |
| `testsuite_application_plan_prepare` | Same request as `testsuite_style_plan_prepare`, plus `executionMode` (`systemManaged` or `externallyManaged`), `simulatorTicket` (the `downloadTicket` of an owned `sim_create` instance) and `wholeGroup`. Definitions use the `application` (`.tat`) and `applicationSet` (`.ttst`) families. The review then runs through the unchanged `testsuite_style_job_*` tools. |

Each reviewed run is one native `TestCaseExecutor.Run` call, chosen by [TestSuiteApplicationRun.Route](../../src/TiaMcp.Core/TestSuite/TestSuiteApplicationRun.cs): one case, a case list, one set, a set list, or the whole `ApplicationTestSystemGroup` (`wholeGroup`, every reviewed case and nothing excluded). One call never mixes cases and sets. The operation is `testSuite.application.run`. Its required probes add `testsuite.applicationService` and `simulator.ownedInstance`.

In the owning worker, observation and dispatch both decode the ticket with the `simulation_download` ownership rules: DPAPI ticket, on-disk marker, registered instance ID, name and storage, Softbus and runtime V8. The instance does not need to be OFF, because Test Suite drives the CPU state. The ticket expires 15 minutes after `sim_create`. Before the single Run, every reviewed case must re-export to its original bytes; sets resolve by exact name only. A whole-group run refuses if the group holds unreviewed cases. `SetScope(PlcSoftware, instanceName, ExecutionMode)` is then written to every reviewed case. That persists in the project, so it runs after the checkpoint and is never replayed. The `GetScope` readback goes into the execution-return receipt. `scopeVerified` stays false, except in the TS-Q02 qualification mode below.

The native `TestResults` tree is retained losslessly in the existing raw format (`NativeTestResult`). One call returns one tree for all its units. `TestSuiteApplicationRun.Captures` gives the whole tree to every required, non-excluded definition. Only a run whose closure is exactly one definition owns it. Lists, set members and groups stay `inconclusive` per unit until a per-case tree profile is qualified. `TestSuiteResultNormalizer` and the JUnit export are unchanged.

Open gaps: production `RequireQualification` refuses on both Host and worker. System-managed mode still receives the owned instance's name, although Test Suite creates its own instance there. Checkpoint capture refuses projects that contain test sets, because sets have no export route. Outside the TS-Q02 fixture, preservation does not yet account for the intended `SetScope` change.

### TS-Q02 qualification mode

A `TestSuiteQualification=true` build started with `--testsuite-qualification` admits application execution for one exact profile, `v21.owned-plcsim.application.qualification-v1` ([TestSuiteApplicationQualification](../../src/TiaMcp.Core/TestSuite/TestSuiteApplicationQualification.cs) on the Host, [ApplicationQualificationFixture](../../src/TiaMcp.TestSuite.V21/ApplicationQualificationFixture.cs) in the worker). Both refuse with `TestSuiteProfileUnqualified` unless the review dispatches exactly one fixture case (`TiaMcp_Q02_Pass` or `TiaMcp_Q02_Fail`) with `Run(TestCase)` in `externallyManaged` mode, and with `ProjectIsolationRequired` unless the project lies in the Host's own `scratch` folder. System-managed mode is refused because Test Suite would create its own, unowned instance. The worker decodes the ticket at prepare, at both observations and before the Run, so a tampered ticket refuses with `InvalidSimulationTicket` and a released instance with `SimulationTargetMismatch`. Production admission is unchanged.

- **Prepare:** `qualification_testsuite_prepare` with `fixture` `applicationPass` or `applicationFail` and `simulatorTicket` (an owned `sim_create` instance with the scratch program downloaded and in RUN). The worker imports both cases into the worker-created scratch, sets their scope to that instance (`ExternallyManagedPLCSIMInstance`), compiles and saves, and reviews the selected case. Pass asserts `"TiaMcp_EnableGate_DB".Active = TRUE`, which Main produces; Fail deliberately asserts `FALSE`.
- **Scope:** `TestCase.GetScope()` names only the controller, so the complete scope reads back from both: `GetScope` must be the PLC and the case's own `SaveToFile` export must name the owned instance and the external mode. The required scope is that PLC and that instance. Because prepare already wrote the scope the Run writes again, the Run's `SetScope` is idempotent and preservation still compares the whole fixture. After `SetScope` the Run re-exports the case; only a byte-identical export marks the result `scopeVerified`.
- **License:** V21 documents `LicenseNotFoundException` for `TestCase.SaveToFile`, so the export in the current session is the pre-run license observation. Execution acceptance is the returned Run itself. No operator observation is needed.
- **Lifecycle:** `qualification_testsuite_execute`, `_recover` and `_reconcile` run the unchanged durable job: checkpoint archive, one Run, compile, preservation, fresh reopen with the scope read again, cleanup, and the `beforeRun`/`afterRun` lost-reply boundaries. Reconciliation compares the original and the restored copy with the checkpoint's preservation baseline.

[`qualify_testsuite_application.py`](../../scripts/qualify_testsuite_application.py) runs one case per Host: `pass`, `fail`, `afterRun` and `released`. All four passed natively on 30 October (`local.testsuite.application.20261030`, [record](../verification/testsuite-application-20261030.md)).

## TS-06 system tests

The inventory and the `.tst` import are natively qualified (`local.testsuite.definitions.20261028`); execution has not run natively ([record](../verification/testsuite-native-20261028.md)). Ledger D06.

| Entry | What it does |
| --- | --- |
| `testsuite_system_inventory` (read-only) | Lists the project's `SystemTestGroup`: test cases with `OPCUAServerAddress`, `OPCUAServerInterfaceType` and `OPCUAServerInterfaceFolderPath`, and the count, bounded by `maxItems` with `truncated`. `export=true` saves each listed case with `SystemTestCase.SaveToFile` as `.tst` and returns path and SHA-256. |
| `change_prepare` kind `testsuite_system_import` | Strict import of a supplied `.tst` (`SystemTestCaseComposition.LoadFromFile`, `ImportOptions.None`, `TCLoadOptions.None`). Existing names refuse at prepare and apply; the native return value and `Find` readback must equal the reviewed names. See the [kind table](../guides/engineering-workflows.md). |
| `testsuite_system_plan_prepare` | Same request as `testsuite_style_plan_prepare`, plus `simulatorTicket` (the `downloadTicket` of an owned `sim_create` instance), `serverAddress` (canonical `opc.tcp://<IPv4>:<port>/` of that instance) and `wholeGroup`. Definitions use the `system` (`.tst`) family. The review runs through the unchanged `testsuite_style_job_*` tools. |

The review reuses the application binding with `executionMode: endpoint`. Each run is one native `SystemTestCaseExecutor.Run` call, chosen by [TestSuiteApplicationRun.Route](../../src/TiaMcp.Core/TestSuite/TestSuiteApplicationRun.cs): one case, a case list or the whole `SystemTestSystemGroup`. The operation is `testSuite.system.run`. Its required probes add `testsuite.systemService` and `simulator.ownedInstance`.

System tests read and write live OPC UA nodes, so the target must be the owned PLCSIM instance. Observation and dispatch decode the ticket as for application tests, and `serverAddress` must be one of the instance's `ControllerIP` addresses that Windows routes through the PLCSIM Virtual Ethernet Adapter (the [owned PLCSIM write](physical-runtime-sessions.md#owned-plcsim-writes-and-runstop) rule). Another address or a route through another network card refuses with `RuntimeTargetNotOwnedPlcsim`, an undetermined route with `RuntimeTargetRouteUnverified`. Before the single Run, every reviewed case must re-export to its original bytes, and a whole-group run refuses unreviewed cases. `SetScope(serverAddress, ServerInterfaces.StandardSIMATIC)` is then written to every reviewed case after the checkpoint and is never replayed.

Results use the same raw format and `TestSuiteApplicationRun.Captures`: one tree per Run, owned exactly only by a one-definition run. Warning and Information states, which include inconclusive runtime outcomes, never pass.

Open gaps: execution stays disabled behind the same code-owned gate. Only the StandardSIMATIC interface is offered; UserDefined and SiOME need `SetScope(..., DirectoryInfo)` with a hash-pinned interface folder. The V21 API has no credential or security-policy argument, so only anonymous OPC UA endpoints are possible. Interface folders are neither exported nor imported. V21 has no system-test set type.

## Verification and integration handoff

Evidence is isolated under `artifacts/ts03-production-20261016/`. The handoff (`artifacts/ts03-production-20261016/handoff.json`) records final commands, results, changed-file ownership and source hashes. Shared catalog handoff (`artifacts/ts03-production-20261016/shared-catalog-handoff.json`) records the pre/post hashes of append-only TS entries; unrelated current records were preserved. No shared Host startup, worker dispatch, project references, solution, WatchTable files, native reservations, frozen binaries or historical receipts were changed by this lane.

The [review](../../contracts/testsuite-style-review.v1.schema.json) and [job](../../contracts/testsuite-style-job.v1.schema.json) schemas add structural contracts. Runtime reconstruction and the shared journal still enforce hashes, revisions, idempotency, complete evidence and state transitions. The actual MCP registration/restart harness uses deliberately nonexistent engineering and simulator workers.

```powershell
dotnet run --project tests/TiaMcp.TestSuite.Tests/TiaMcp.TestSuite.Tests.csproj --artifacts-path artifacts/your-ts03-build -- artifacts/your-ts03-results
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj --artifacts-path artifacts/your-ts03-host
dotnet build src/TiaMcp.TestSuite.V21/TiaMcp.TestSuite.V21.csproj --artifacts-path artifacts/your-ts03-native-compile
python scripts/test_testsuite_production_host.py --host <isolated-host.exe> --fixture artifacts/your-ts03-results-production/host-fixture.json --report <fresh-report.json>
python scripts/test_testsuite_style_contracts.py --fixture artifacts/your-ts03-results-production/host-fixture.json --report <fresh-contract-report.json>
```

The schema check requires `jsonschema` and `referencing`. This lane used the existing retained schema-tool installation with `python -B` to avoid writing to historical artifacts; no dependency download was needed.
