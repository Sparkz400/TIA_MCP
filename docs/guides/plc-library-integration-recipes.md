# PLC library integration recipes

`change_prepare` now accepts a **plan-only** `library_integrate` recipe for one exact FB version. It reads the complete native dependency closure, exports each pinned version, checks the FB interface against explicit existing tag mappings, and returns the concrete arguments for two existing prepared operations:

1. `library_instantiate` imports the exact FB and its native dependencies.
2. After exact import readback, a freshly prepared `graphical_typed_edit` creates the named global instance DB and either inserts the explicitly wired call in the selected LAD/FBD caller or creates a new standalone FC containing that call.

By default the recipe does **not** install the component. `change_apply` refuses it with `PlcIntegrationNotQualified` before idempotency claims, checkpoints, saves, compilation or native writes. A Host started with `--enable-library-integrate-apply` can install the verified shapes in one `change_apply`; see [combined apply](#combined-apply-opt-in). The existing Unified HMI integration route keeps its separate behavior. A `prepared` journal state means that the plan was captured; check `preview.applySupported`. It is false for PLC recipes unless that Host flag is set and the plan is an eligible shape.

Restricted recipe names, native network IDs and pinned dependency hashes must
match their complete grammar. Trailing line breaks are rejected without trimming;
this does not restrict general native tag symbols. See the
[offline boundary checks](../verification/component-text-boundaries-20261022.md).

Retained source and installed content artifacts are read into one bounded byte
snapshot; hash verification and XML decoding use those same bytes. Replacement
between separate file reads cannot substitute unverified XML. The 4 MiB limit and
existing encoding behavior remain in place. See the
[artifact verification](../verification/component-artifact-read-20261022.md).

Planning uses the original root and caller artifact hashes, including recorded
byte counts when present. Existing caller evidence is captured after export and
retained unchanged; changed files refuse before a plan is returned. See
[producer verification](../verification/component-plan-artifacts-20261023.md).

## Preparing a motor recipe

Use the [payload schema](../../contracts/plc-library-integration-recipe.v1.schema.json) and [motor example](../../fixtures/libraries/integration/motor-plan.example.json). The example GUIDs and paths are illustrative; replace them with native observations from the selected owned project/library.

Call `change_prepare` with `kind: "library_integrate"` and the recipe as `payload`. Set `action: "plc_component_plan"`, `planOnly: true` and `schemaVersion: 1`. Supply the exact library ID/current revision and one selected FB type/version pair. `integration.dependencyPins` must equal the full observed native dependency closure excluding that root. Even dependencies that seem unrelated to the mapped parameters must be pinned. The planner rejects missing/extra versions, duplicate type/version pins, noncommitted or DoNotUse versions, and target name collisions. Already installed objects are never adopted by name; exact reuse needs a native proof (see [broadened plans](#broadened-plans-27-october)).

Each mapping supplies the exported parameter name, its exact `Input`, `Output` or `InOut` direction and scalar type, plus an exact existing PLC tag path. Every exposed parameter must appear once. Native tag types and unique symbolic names are checked; safety tags and writing physical input tags are rejected. The recipe does not create tags or map local variables, arrays, UDT parameters, constants or omitted default parameters; DB and UDT-tag members are described under [broadened plans](#broadened-plans-27-october). It can still pin UDTs used internally by the selected FB. Physical aliasing between different tag addresses and cross-program writers are outside this planner's analysis.

The explicit instance DB name uses `numberPolicy: "firstAvailable"`, matching the current typed creation route. Its preview number is provisional and must be rechecked after library import. Exact numeric allocation is not offered. Existing-caller mode requires an unprotected LAD/FBD FC, FB or OB caller and one exact existing compile-unit ID in `insertAfterNetworkId`. All names used for the new FB/DB and parameters are simple identifiers. Software units, OB callers, multi-instances, reuse and member bindings are described under [broadened plans](#broadened-plans-27-october).

For a new standalone caller, use the [standalone example](../../fixtures/libraries/integration/motor-standalone-plan.example.json) and set:

```json
"caller": {"mode": "createStandaloneFc", "name": "MotorCaller", "language": "FBD", "numberPolicy": "firstAvailable"}
```

This explicit alternative requires that the name is absent from the complete standard-scope block/type inventory and distinct from the FB, dependencies and instance DB. It creates an FC in the standard root block group with an empty callable interface, using the existing typed `createBlock` and `createGlobal` routes after exact import. LAD is also accepted. Both block numbers are freshly allocated and reviewed in typed preparation. `blockPath` and `insertAfterNetworkId` are forbidden in this mode. The planner records the exact proposed path and initial absence; the coordinator rechecks absence after import and immediately before typed preparation. Calling the new FC from an OB remains a separate prepared change. No OB call is generated by installation.

The original caller object remains supported, with optional `mode: "existing"`. Its preservation guard follows the [insertion-ID scope decision](../plans/insertion-id-renumbering-decision-20261025.md): only the IDs after the inserted network may change, each by the inserted network's ID count.

The generated call executes unconditionally when that caller/network is executed: LAD uses a power rail; FBD uses a `Bool` true constant connected to `en`. This is explicit in the generated model. Conditional execution can be supplied through the FB's own mapped Enable input when its interface supports that behavior; it is not inferred by the planner.

## Broadened plans (27 October)

The plan covers five more shapes. Each one either yields exact, typed steps or refuses
with a structured code. These are offline planner tests only; none is natively qualified.

- **Software units and namespaces.** Set `target.scopePath` to `<plcPath>/units/<unit>`
  (safety units are refused). The FB keeps the namespace from its pinned export, the
  caller keeps its native namespace, and both appear in `plan.fb.path`, the call's
  `calleePath`, the typed step's `scopePath`/`groupPath`/`namespace` and the instance DB
  path. A new standalone FC uses the unit's namespace preset. A namespaced FB in the
  standard scope is refused (`UnsupportedIntegrationScope`).
- **OB callers.** An unprotected LAD/FBD OB is an ordinary existing caller. The call is
  inserted after the exact network, and `caller.kind` is `OB` with `obCallAdded: true`.
- **Multi-instance.** Replace `instanceDb` with `"multiInstance": {"name": "Motor1"}`.
  The caller must be an existing FB; the typed step uses `createMulti`. An existing
  member with that name is refused (`DeclarationCollision`) and never adopted.
- **Exact reuse.** A same-name object is reused only when the worker proves the exact
  installed type/version GUID, one native link and identical version metadata (the
  `library_instantiate` proof). Reused dependencies keep the import step. A reused FB
  drops it. An existing instance DB of the reused FB is reused (`existingGlobal`) only
  when native cross-references show no other user; otherwise the plan refuses with
  `InstanceUseUnproven` or `InstanceAlreadyUsed`. `plan.reuse` lists what is reused, and
  nothing is duplicated.
- **UDT and DB-member bindings.** A mapping may use
  `{"dbPath": "<exact DB path>", "members": ["cmd", "enable"]}` or a UDT-typed tag with
  `"tagPath"` plus `"members"`. The worker exports the DB or UDT declaration, and the
  final member must be scalar with the exact parameter type. Nested UDTs inside a UDT
  stay opaque.

A step the component coordinator cannot run yet has `planOnly: true` and
`planOnlyReasons`. The coordinator refuses any plan with such a step; the step is still
an exact proposal for separate preparation.

| Code | Cause |
|---|---|
| `ComponentScopeUnqualified` | Software-unit scope: readback covers the standard scope only, and namespaces of library instances in units are unqualified. |
| `ComponentRecoveryAnchorUnavailable` | Reused FB: no import step whose checkpoint anchors recovery. |
| `ComponentMultiInstanceUnqualified` | `createMulti` changes the caller interface; readback proves interface-preserving insertion only. |
| `ComponentReadbackUnavailable` | DB-member binding: readback re-resolves root tags only. |
| `UnsupportedOperandRoute` | UDT-typed tag member: the typed route binds root scalar tags only. |
| `UnsupportedScopeReference` | Tag binding inside a software unit: the typed route refuses unit tags. |

A combined HMI `instanceMember` tag needs a global `instanceDb`.

## Reviewing and applying separate operations

`preview.integrationPlan` contains native version export hashes/artifacts, the exported interface, mapped tag observations, provisional DB identity, concrete `steps[].prepareArguments`, blockers, recovery plan and qualification gates. Following native object IDs at the selected boundary are accepted when V21's renumbering is predictable: every caller ID is canonical uppercase hex and consecutive in document order. Otherwise `NativeIdPreservationUnsupported` stays in `blockers`. Insertion must still pass the production typed editor's preservation checks.

After installation, readback proves the caller independently of the typed prediction (`PlcComponentCallerInsertion`, evidence `callerInsertion`). It removes the one network directly after `insertAfterNetworkId` and subtracts its ID count from every following ID. The result must equal the original caller export exactly. A wrong shift amount, a changed order, an aliased or colliding ID, any change before the insertion point, or any other content change fails closed with `ComponentCallerPreservationMismatch`.

The two step argument objects are proposals for **fresh** preparation, not approved changes and not a combined transaction. After step 1, inspect exact FB/dependency version and content readback, refresh the project inventory and verify that native paths still match. Only then prepare step 2 and review its own native profiles, protection, mapping, numbering, preservation, compiler and checkpoint results. Neither step skips existing guards. An unsupported CPU/profile or native insertion boundary remains unavailable even when the recipe has no other blockers.

Retain a complete initial project checkpoint before the first native mutation; it covers the project library, imported types, caller, instance DB and unrelated content. Each separately applied change has its own checkpoint/journal. A second-step failure does not roll back the first. For an uncertain result, read `change_status`; never replay. Recovery into a new owned location must use the initial checkpoint to restore the pre-installation state, followed by fresh-reopen and native-content comparisons. No checkpoint is created merely by planning.

## Combined apply (opt-in)

Start the Host with `--enable-library-integrate-apply` to let `change_apply` install a prepared PLC recipe. The flag is off by default; without it nothing above changes. The worker gets the flag from the Host. `library_capabilities` then reports `plcIntegration.applySupported: true`, and each PLC recipe preview carries `combinedApply` with `eligible` and, if refused, the `refusal` reason. Only an enabled Host changes the preview, so the reviewed `previewHash` always says whether apply installs.

Only the natively verified shapes run ([fresh install](../verification/component-fresh-install-20261024.md), [existing caller](../verification/component-existing-caller-20261025.md)):

- a fresh import of the FB and its dependencies from a read-only global library source with its retained snapshot;
- a new global instance DB;
- a new standalone FBD FC, or an existing FBD FC inserting after its last network, in the standard PLC scope;
- root scalar tag bindings, no planner blockers and no `planOnly` steps.

Everything else is refused with `PlcIntegrationNotQualified` before any claim, even with the flag: OB insertion (add the OB call as its own reviewed change), LAD and FB callers, placements before another network, reuse of installed versions or instance DBs, multi-instances, software units, DB or UDT-tag member bindings, HMI targets and PLC/HMI recipes.

`change_apply` uses the same `previewHash`, `idempotencyKey` and `checkpointSave`/`compile`/`save=true` as any library change. It runs only under the Host's shared-mutation admission, which holds one exclusive lease for the whole sequence. Under that lease it rechecks the plan hash, the library fingerprint and the source hashes before the claim. It then drives the [internal component coordinator](#internal-component-coordinator) through every step: fresh `library_instantiate` preparation, apply and import readback, then fresh `graphical_typed_edit` preparation, apply and installation readback. Each freshly prepared step must match the reviewed plan. The instance DB uses the natively captured storage profile when the plan's installed FB/UDT content equals the captured component, and otherwise the shipped typed instance templates; a callee neither covers refuses with `InstanceCreationProfileUnavailable` (seen natively on 27 October before the captured profile was admitted on this route).

The import step's complete-project archive is taken before the first mutation. It is the change's single recovery checkpoint and appears in the change's `journal.checkpoints`; the typed step's own archive is retained but never used for recovery. Any failure stops at that step and leaves `recovery_required`. `journal.failure.failedStep` and `journal.combinedApply` name the step, the step change IDs and the coordinator journal. `change_restore` on the same change retrieves the initial checkpoint into a new location in an empty owned Portal. Originals are never overwritten, no step is replayed, and there is no automatic in-place rollback. Steps are not atomic.

This Host-gated route is natively qualified for the shapes above on owned de-DE-only scratch projects (`local.library_integrate.combined_apply.rerun.20261028`, [second native slot](../verification/wave10-native-slot-20261027.md#second-slot--28-october)). Other shapes, languages and targets stay refused.

The native runner `scripts/qualify_library_integrate_apply.py` qualifies this route. It starts the frozen Host with the flag, in owned TIA sessions and owned scratch projects only, and reuses the fixture and oracles of `qualify_plc_component.py`. For a new standalone FBD FC and for an existing FBD FC caller, it prepares the plan, requires `combinedApply.eligible` and applies once. It then checks by export and inventory that exactly the FB, the UDT, the instance DB (first free number) and the caller network were added and nothing else changed. It also checks the project library versions, a fresh reopen, a compile-only change with 0 errors, and `change_restore` of the initial checkpoint into a new location, which must show the pre-change inventory after retrieval and after reopen. With the flag, an FBD OB caller (built from the scratch `Main` and the peer FC's network) and a new LAD FC must be refused. A second Host without the flag must refuse the eligible plan. Each refusal must be `PlcIntegrationNotQualified`, with the plan still `prepared` and the project unchanged. Each new scratch is made exactly de-DE (de-DE becomes reference and editing language, then a second change removes every other active language), because the fixtures and the captured storage references are de-DE only; another active language adds text items to the installed FB/UDT exports and breaks their exact content binding. Use a Debug worker that is registered in the Openness allow-list.

```powershell
python -B scripts/qualify_library_integrate_apply.py `
  --host <frozen-host.exe> --worker <frozen-worker.exe> `
  --artifacts <short-artifacts-dir> --timeout 600 --run-native
```

Expected outcome: `<short-artifacts-dir>/i<6 hex>/result.json` with `status: "passed"`, all five `steps` passed, and `combinedApplyVerified`, `refusalsVerified` and `noFlagRefusalVerified` true. `runtimeVerified` stays false. A failed step stops the run with `status: "failed"`; nothing is restored or replayed automatically. Offline checks of its oracles: `python -B scripts/test_qualify_library_integrate_apply.py`.

## Verification and remaining qualification

The dedicated offline suite is `dotnet run --project tests/TiaMcp.PlcLibraryIntegration.Tests`. It covers pins, interface/direction/type consistency, collisions, scope/protection/read-only inputs, strict payload fields, insertion blockers, nonmutation of inputs, actual LAD/FBD compiler acceptance against retained profiles, and the application guard's source boundary. The Openness worker is also compiled against the installed V21 references. These checks do not run TIA or establish native installation success.

`scripts/qualify_plc_library_integration_plan.py` provides `qualify_plan(client, payload, artifacts, run_native=True)` for an initialized MCP client inside an explicitly owned `TiaMcpScratch_…` fixture. It captures native caller/inventory state, prepares the recipe, verifies retained version hashes, attempts the blocked apply once, then checks that no claim/checkpoint was created and that caller payload and block/type/tag inventories remain unchanged. It records failures and never retries, starts TIA, installs the proposed steps, closes a project or terminates a process. **This native harness is authored but unexecuted.** A native fixture must provide the selected released FB/dependency versions, existing scalar tags and eligible LAD/FBD caller.

Production combined installation is off by default; the opt-in [combined apply](#combined-apply-opt-in) is verified for its shapes (`local.library_integrate.combined_apply.rerun.20261028`). An explicit qualification-only launch now exposes the internal coordinator for an owned scratch fixture; native qualification still must cover exact dependency links/content, DB/call/bindings, compiler diagnostics, unrelated-content preservation, save/fresh reopen, interruption/no-replay behavior, initial-checkpoint recovery and independent simulator assertions. The existing separate-operation evidence is not combined-operation evidence.

## Internal component coordinator

The next implementation increment provides `PlcComponentCoordinator` and `NativePlcComponentOperations` in the Openness worker. The adapter delegates to existing `LibraryChangePrepare`/`LibraryChangeApply` and `ChangePrepare`/`ChangeApply`; it does not bypass their stale-project, dependency, protection, preservation, checkpoint or compilation guards. The library checkpoint already covers the complete target project and its project library, so the coordinator adopts that **first import checkpoint** as the initial recovery anchor instead of creating a second independent archive before import.

The exact internal native qualification factory is:

```csharp
CreatePlcComponentQualificationCoordinator(planChangeId, previewHash, absoluteArtifactRoot)
```

It accepts only the exact current-session prepared PLC plan in the worker's sole, owned scratch project. The private factory is reachable only through the explicit `--component-qualification` launch and the qualification kind described below. Normal host/worker launches reject it with `ComponentQualificationDisabled`; it is not advertised as a production capability. The adapter is implemented and compiled. The earlier existing-caller attempt stopped before import at its native metadata-preservation boundary; standalone installation still requires its own complete native qualification. On a Host without `--enable-library-integrate-apply`, applying the original production `library_integrate` change continues to return `PlcIntegrationNotQualified`.

The Siemens-independent coordinator API is directly callable by offline test/qualification code:

```csharp
var operation = PlcComponentCoordinator.Create(absoluteRoot, preparedPlan, operations);
var importPreview = operation.Advance();
operation.Advance(importPreview["library"]["prepared"].Value<string>("previewHash"));
operation.Advance(); // exact imported version/content/use and caller/tag readback
var typedPreview = operation.Advance(); // recheck, then freshly prepare typed edit
operation.Advance(typedPreview["graphical"]["prepared"].Value<string>("previewHash"));
var readback = operation.Advance(); // final caller/DB/version/tag checks
```

`operations` implements `IPlcComponentOperations`; offline tests provide a fake, while the private native factory provides `NativePlcComponentOperations`. Each `Advance` crosses at most one mutating native call boundary and returns durable progress. Preparation and application are separate calls; applying requires that step's exact fresh preview hash. The newly allocated DB number is reviewed in the actual typed preview, rather than assuming the recipe's provisional number. The caller's existing code (or the new standalone name's absence) and mapping observations must still match immediately before typed preparation.

The local `component-journal.json` is atomically replaced after flushing, has bounded size and hashes for its content and immutable recipe contract, and records exact native change IDs, previews, dispatch keys, outcomes and readback evidence. A project-path lock serializes coordinator calls using the same journal root. It does not claim a distributed project reservation or prevent external TIA edits; native exclusive access and fingerprint checks remain authoritative. Hashes detect accidental corruption; they are not signatures protecting against a writer who can replace both content and hash.

The journal records `dispatching_library` or `dispatching_graphical` **before** invoking a mutating existing operation. A native exception, lost response or failure to write the completed receipt never causes a retry. `PlcComponentCoordinator.Open(directory, operations)` reopens durable local progress; `Status()` is local read-only and `Reconcile()` requests only the existing change status. Reconciliation requires the exact change ID, preview hash and dispatch idempotency key. A terminal receipt can establish that a step finished, but reconciliation does not dispatch the next step. A changed native session token blocks `Advance`; prepared handles are never silently rebuilt after worker/session restart. Nonterminal outcomes remain unknown until explicit diagnosis/recovery.

The initial checkpoint must exist, match its recorded SHA-256, identify the selected full project, and have a completed archive event before native mutation dispatch. Completed import additionally requires compile and project-save evidence. Later typed receipts cannot replace that checkpoint. Recovery information directs the existing first-library-change `change_restore` into a new empty owned Portal and a new destination; it does not close the current project or retrieve anything automatically. If initial checkpoint creation itself is uncertain, inspect the underlying first-step journal; absence of a coordinator checkpoint field is not evidence that no native archive exists.

`installation_verified` is a narrow internal state: exact source/dependency pins and native uses, tag observations, successful compiler/save receipts, and caller/DB readback against the fresh typed artifacts. UDTs and versions without an explicit content policy retain raw export equality. The FB-only policy below can prove three precisely scoped native property additions using separately pinned full source and installed exports. This state does not assert whole-project unrelated-content preservation, fresh reopen, recovery success or runtime behavior; those flags remain false. Complete native standalone qualification remains required.

The existing dedicated test project also runs coordinator failure injection: dispatch ordering, two-stage sequencing, fresh preview review, lost responses, failed journal replacement, restart reconciliation, expired handles, foreign receipts, checkpoint bytes/order, compile/save failures, changed callers/tags/dependency content, instance identity, project-gate exclusion and corruption. These are offline executions; their passing results do not qualify the native adapter.

## Explicit owned-scratch qualification entrypoint

Start the host with `--component-qualification`. The host forwards the flag to the
V21 worker; the default is false at both launch boundaries and in the session
constructor. This opt-in permits only a newly created, exact worker-owned scratch
with one open project. Attaching to a user session, reopening an existing project,
changing the scratch, or using expired handles cannot authorize installation.

After ordinary `change_prepare(kind="library_integrate", payload=recipe)`, call:

```json
{"kind":"library_component_qualification","payload":{"schemaVersion":1,"action":"start","planChangeId":"<plan changeId>","planPreviewHash":"<plan previewHash>"}}
```

This is still `change_prepare`: it captures a coordinator journal and prepares the
existing `library_instantiate` operation without changing TIA content. Its response
has a `lib-component-…` change ID, `state=library_prepared`, the fresh step
`previewHash`, and its full native preparation under `review`. Review that step,
then call destructive `change_apply` with the wrapper change ID, exact current
step hash, an explicit caller idempotency key, and `checkpointSave`, `compile` and
`save` all true. The coordinator uses a fixed durable per-step dispatch key.

Use the following read-only preparation request once for import readback, again
for fresh typed preparation, and after typed apply for installation readback:

```json
{"kind":"library_component_qualification","payload":{"schemaVersion":1,"action":"advance","changeId":"lib-component-<id>"}}
```

Read-only advancement refuses both `library_prepared` and `graphical_prepared`;
it cannot dispatch either mutation. Review the newly returned typed preview and
apply it separately with its own hash. A lost/failed dispatch cannot be replayed.
In the same native session, `action="reconcile"` requests only the dispatched
underlying change's status; it never continues the workflow. After restart,
`change_status` can read the wrapper's durable journal, but the qualification
adapter cannot be reconstructed or resumed. The first underlying import change
ID/hash in `record.initialCheckpoint` remains the explicit recovery anchor.

The executable qualification driver authors the independent FB→UDT fixture,
publishes its native archive, creates a fresh consumer and runs that exact
protocol. It retains every preview/dispatch/receipt before proceeding, complete
PLC block/type/tag-table XML snapshots, an independent native call/instance/tag
wiring oracle, saved reopen, and first-checkpoint retrieval/reopen. It starts only
with `--run-native`, tracks owned process handles through the existing
`OwnedScratch` helper, and never automatically restores after any failed or
uncertain apply. Successful whole-project archive retrieval does not broaden its
XML comparison scope to hardware/HMI or imply runtime verification.

Report persistence uses the shared `run_block_tests.save_json` helper: only the
same-directory `result.json.pending` replacement is retried, for at most two
seconds and only Windows errors 5, 32 or 33. Mutation calls and recovery are never
inside that retry loop. Other replacement errors propagate immediately, preserving the
pending report and stopping continuation. The helper is included in retained
harness-file hashes.

```powershell
python -B scripts/qualify_plc_component.py `
  --host <isolated-host.exe> --worker <isolated-worker.exe> `
  --artifacts D:/03_Entwicklung/TIA_MCP/artifacts/p14/n `
  --artifact-policy-root D:/03_Entwicklung/TIA_MCP/artifacts `
  --output <fresh-evidence-directory> --timeout 600 --run-native
```

The current fixture requests a new standalone FC and retains a separate pre-existing FC as an unrelated preservation witness. Before import, the selected new caller is absent; after installation, exactly the FB, UDT, instance DB and caller are added. Every original PLC block/type/tag-table XML payload, including all OBs, must remain unchanged. The independent oracle checks the new FC identity, one network and exact FB/instance/tag/enable wiring. The same saved-reopen and first-import-checkpoint recovery comparisons remain mandatory. A failed or uncertain apply never triggers automatic restore or replay. Runtime stays false: the new FC is not called by an OB.

Keep the native artifact root short for Siemens archive/project path limits. `--caller-mode existing`
first creates the fixture caller FC from the same source, then inserts the component call after its
network. The qualifier's caller comparison reverses exactly the following-ID shift and requires the
native `callerInsertion` proof. This mode passed natively for an existing FBD FC caller
([existing-caller installation](../verification/component-existing-caller-20261025.md)). Use a Debug
worker build (registered in the Openness allow-list by its post-build step); an unregistered Release
worker makes TIA wait on its access prompt. Before the scope decision, the first native run stopped at this boundary:
FB/UDT authoring, dependency/archive validation and consumer staging succeeded,
then following native title IDs blocked insertion before import. Both owned
sessions exited cleanly. Combined installation was then still unqualified; see the
[third increment evidence](../verification/parallel-third-20261012.md#component-qualification).
The retained native02 attempt completed exact FB/UDT import, compile and save, then failed raw version-content comparison. The installed FB exposed three content attributes absent from its source `WithDefaults` export: `AutoNumber=true`, `IsIECCheckEnabled=false`, and `MemoryLayout=Optimized`. Native03 authored these settings explicitly but its global-library base export still omitted them, so the pre-consumer gate correctly stopped before import. The read-only xp01 diagnostic then exported the exact native02 source/installed versions with all four export-option combinations. No symmetric option gave equal FB bytes. `WithDefaults | WithReadOnly` did expose all three exact source values with `ReadOnly="true"`; their installed values and native instance getters agreed.

The explicit policy `v21-fb-explicit-source-oracle-three-properties-v1` records the base raw hash, full-source oracle raw hash/artifact, source global-library name and three exact values in the reviewed plan. The same exact native version object emits both source documents. The existing library import preparation binds these pins through an internal argument unavailable to normal payloads. Its prepared preview hash includes them, and its existing pre-mutation fingerprint re-exports and checks both source hashes under native exclusive access before any claim/checkpoint/import. Every post-import readback rechecks both source hashes and retains installed base/full hashes plus native FB setting getters.

The comparison preserves every remaining base XML node after verifying/removing only those three exact direct FB additions from the installed comparison copy. It separately compares the complete source and installed full-oracle trees, permitting only the three exact `ReadOnly="true"` markers to become absent, the version's direct `OriginalLibrary` to equal the reviewed source global name, and the content FB's direct `CreationDate` provenance value to differ. Both creation-date leaves must be explicit native UTC XML scalars with the expected read-only marker. Their bytes remain retained; native DateTime ticks/Kind are observations only, with no inferred timezone conversion or temporal-bound claim. All other read-only fields, timestamps, content, IDs, comments, unknown XML and significant whitespace remain protected by the full comparison. `DocumentInfo` is forbidden, so the generic native envelope exception cannot apply. Missing, duplicate, nondefault, namespaced-substitute, altered or extra fields fail closed. No generic default inference or metadata ignore list is used.

The revised qualification driver independently checks the pinned full-source oracle before consumer import and retains `sourceContentEvidenceAttempt` before validation. Returned readback observations remain durably retained as `importEvidenceAttempt`, `preTypedEvidenceAttempt` or `installationEvidenceAttempt` before validation; attempted fields never establish a verified stage. Successful evidence adds `contentVerification` with the policy ID, all four hashes and both full-comparison results. Native04 must qualify the complete standalone sequence on a new build/evidence freeze. Production combined apply and runtime remain unavailable under this evidence; OB insertion is planned but not natively qualified.

For an explicitly requested recovery of a failed run while preserving its original evidence, use a fresh output/native directory and append `--recover-from-report <failed-result.json>` to the command above. This separate mode validates the original first-import checkpoint identity and hash plus all baseline XML hashes, calls existing `project_retrieve` once in an empty owned session, compares the complete baseline after retrieval and fresh reopen, and verifies absence of component library types. It uses no installation `change_apply`, does not resume the component, and leaves the original first-import journal unchanged. A failed or lost retrieval response causes cleanup without retry. Success qualifies only first-checkpoint recovery; installation and runtime flags stay false.

Run offline checks with `python -B scripts/test_plc_component.py`, the existing
planner/coordinator test project, and the new
`TiaMcp.ComponentQualification.Boundary.Tests` net48 executable. The latter invokes
the actual worker dispatcher on unattached sessions to verify default-off and
owned-scratch refusal; it never constructs or starts a native TIA session.

## Combined PLC/UDT/HMI recipes

Two more plan-only `library_integrate` actions cover WinCC Unified. Schema:
[plc-hmi-integration-recipe.v1](../../contracts/plc-hmi-integration-recipe.v1.schema.json);
examples: [install](../../fixtures/libraries/plc-hmi/motor-hmi-plan.example.json) and
[update](../../fixtures/libraries/plc-hmi/motor-status-update-plan.example.json).
Both are validated completely before the worker selects a project. Apply returns
`PlcIntegrationNotQualified`, as for PLC recipes.

**Install (`plc_hmi_component_plan`).** Send the PLC recipe above unchanged, with
`action: "plc_hmi_component_plan"`, plus:

- `hmi`: `family: "unified"`, `profile` (`unified-comfort` or `unified-pc`, must match
  the observed target), the exact `hmiPath`, the integrated `connection` name and the
  `tagTable` name. Each of 1–64 `tags` has a name, a scalar `dataType` and a `source`:
  `{"kind":"plcTag","tagPath":...}` for an existing root PLC tag, or
  `{"kind":"instanceMember","members":["status","speed"]}` for a member of the new
  instance DB (one UDT level is expanded from the pinned dependency export). Optional
  `screenItems` place a new item with an exact rectangle on an existing screen.
- `expectedCompilerOutcomes`: `plc` and `hmi`, each `errors: 0` and `maxWarnings`.

The worker proves the connection's PLC owner natively and refuses a different PLC
(`HmiConnectionPlcMismatch`), existing tag or item names (`HmiNameConflict`), type
differences (`OperandTypeMismatch`) and rectangles outside the screen
(`HmiPlacementInvalid`). The plan keeps the two PLC steps and adds one
`hmi_engineering` `external_tag_create` proposal per root-tag mapping. Its blockers
stay explicit: `HmiMemberMappingUnqualified` (no operation creates member-bound tags),
`HmiScreenItemCreationUnavailable` and `HmiPreservationGateClosed`.

**Field-changing update (`plc_hmi_update_plan`).** Select one UDT version and one PLC
(`target.projectName`, `target.plcPath`). The PLC must contain exactly one use of that
type. The worker compares the installed and selected declarations and checks every
Unified HMI tag whose connection proves to be that PLC. A tag bound through the UDT
(for example `MotorDB.status.speed`) becomes an impact row with its status, before
and after type, and direct screen-item consumers. The plan is refused:

- `HmiBindingWouldBreak` when any row is `typeChanged`, `memberRemoved` or
  `structureChanged`;
- `HmiImpactIncomplete` when any binding cannot be proven: Classic HMIs, unproven
  connection owners, absolute or non-scalar spellings, UDT-typed tag-table tags, UDTs
  nested in another UDT, a member missing before the update or another profile.

The error `details` hold the full impact report (`memberChanges`, `bindings`,
`unresolved`, `breakingCount`, `affectedConsumerCount`). Nothing is rewired
automatically. A clean preview proposes a freshly prepared `library_update_project`
step; bindings must be previewed again after any type update.

The internal component coordinator refuses combined plans. Scripts, alarms and
faceplate ports are not parsed as consumers. Evidence and the native probe:
[combined recipes](../verification/plc-hmi-recipes-20261027.md).
