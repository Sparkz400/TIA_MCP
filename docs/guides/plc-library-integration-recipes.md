# PLC library integration recipes

`change_prepare` now accepts a **plan-only** `library_integrate` recipe for one exact FB version. It reads the complete native dependency closure, exports each pinned version, checks the FB interface against explicit existing tag mappings, and returns the concrete arguments for two existing prepared operations:

1. `library_instantiate` imports the exact FB and its native dependencies.
2. After exact import readback, a freshly prepared `graphical_typed_edit` creates the named global instance DB and either inserts the explicitly wired call in the selected LAD/FBD caller or creates a new standalone FC containing that call.

The recipe itself does **not** install the component. `change_apply` refuses it with `PlcIntegrationNotQualified` before idempotency claims, checkpoints, saves, compilation or native writes. The existing Unified HMI integration route keeps its separate behavior. A `prepared` journal state means that the plan was captured; check `preview.applySupported`, which is always false for PLC recipes.

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

Call `change_prepare` with `kind: "library_integrate"` and the recipe as `payload`. Set `action: "plc_component_plan"`, `planOnly: true` and `schemaVersion: 1`. Supply the exact library ID/current revision and one selected FB type/version pair. `integration.dependencyPins` must equal the full observed native dependency closure excluding that root. Even dependencies that seem unrelated to the mapped parameters must be pinned. The planner rejects missing/extra versions, duplicate type/version pins, noncommitted or DoNotUse versions, and target name collisions. Already installed objects are never silently adopted by name or GUID.

Each mapping supplies the exported parameter name, its exact `Input`, `Output` or `InOut` direction and scalar type, plus an exact existing PLC tag path. Every exposed parameter must appear once. Native tag types and unique symbolic names are checked; safety tags and writing physical input tags are rejected. This initial recipe does not create tags or map local variables, DB members, arrays, UDT parameters, constants or omitted default parameters. It can still pin UDTs used internally by the selected FB. Physical aliasing between different tag addresses and cross-program writers are outside this planner's analysis.

The explicit instance DB name uses `numberPolicy: "firstAvailable"`, matching the current typed creation route. Its preview number is provisional and must be rechecked after library import. Exact numeric allocation and reuse of an existing instance DB are not offered. The planner requires a standard PLC scope and empty namespace. Existing-caller mode requires a standard unprotected LAD/FBD caller and one exact existing compile-unit ID in `insertAfterNetworkId`. All names used for the new FB/DB and parameters are simple identifiers.

For a new standalone caller, use the [standalone example](../../fixtures/libraries/integration/motor-standalone-plan.example.json) and set:

```json
"caller": {"mode": "createStandaloneFc", "name": "MotorCaller", "language": "FBD", "numberPolicy": "firstAvailable"}
```

This explicit alternative requires that the name is absent from the complete standard-scope block/type inventory and distinct from the FB, dependencies and instance DB. It creates an FC in the standard root block group with an empty callable interface, using the existing typed `createBlock` and `createGlobal` routes after exact import. LAD is also accepted. Both block numbers are freshly allocated and reviewed in typed preparation. `blockPath` and `insertAfterNetworkId` are forbidden in this mode. The planner records the exact proposed path and initial absence; the coordinator rechecks absence after import and immediately before typed preparation. Calling the new FC from an OB remains a separate prepared change. No OB call is generated by installation.

The original caller object remains supported, with optional `mode: "existing"`; its unchanged-ID preservation guard still applies.

The generated call executes unconditionally when that caller/network is executed: LAD uses a power rail; FBD uses a `Bool` true constant connected to `en`. This is explicit in the generated model. Conditional execution can be supplied through the FB's own mapped Enable input when its interface supports that behavior; it is not inferred by the planner.

## Reviewing and applying separate operations

`preview.integrationPlan` contains native version export hashes/artifacts, the exported interface, mapped tag observations, provisional DB identity, concrete `steps[].prepareArguments`, blockers, recovery plan and qualification gates. Existing following native object IDs at the selected insertion boundary produce `NativeIdPreservationUnsupported` in `blockers`. Resolve that boundary before using any proposed steps; insertion must still pass the production typed editor's preservation checks.

The two step argument objects are proposals for **fresh** preparation, not approved changes and not a combined transaction. After step 1, inspect exact FB/dependency version and content readback, refresh the project inventory and verify that native paths still match. Only then prepare step 2 and review its own native profiles, protection, mapping, numbering, preservation, compiler and checkpoint results. Neither step skips existing guards. An unsupported CPU/profile or native insertion boundary remains unavailable even when the recipe has no other blockers.

Retain a complete initial project checkpoint before the first native mutation; it covers the project library, imported types, caller, instance DB and unrelated content. Each separately applied change has its own checkpoint/journal. A second-step failure does not roll back the first. For an uncertain result, read `change_status`; never replay. Recovery into a new owned location must use the initial checkpoint to restore the pre-installation state, followed by fresh-reopen and native-content comparisons. No checkpoint is created merely by planning.

## Verification and remaining qualification

The dedicated offline suite is `dotnet run --project tests/TiaMcp.PlcLibraryIntegration.Tests`. It covers pins, interface/direction/type consistency, collisions, scope/protection/read-only inputs, strict payload fields, insertion blockers, nonmutation of inputs, actual LAD/FBD compiler acceptance against retained profiles, and the application guard's source boundary. The Openness worker is also compiled against the installed V21 references. These checks do not run TIA or establish native installation success.

`scripts/qualify_plc_library_integration_plan.py` provides `qualify_plan(client, payload, artifacts, run_native=True)` for an initialized MCP client inside an explicitly owned `TiaMcpScratch_…` fixture. It captures native caller/inventory state, prepares the recipe, verifies retained version hashes, attempts the blocked apply once, then checks that no claim/checkpoint was created and that caller payload and block/type/tag inventories remain unchanged. It records failures and never retries, starts TIA, installs the proposed steps, closes a project or terminates a process. **This native harness is authored but unexecuted.** A native fixture must provide the selected released FB/dependency versions, existing scalar tags and eligible LAD/FBD caller.

Production combined installation remains unavailable. An explicit qualification-only launch now exposes the internal coordinator for an owned scratch fixture; native qualification still must cover exact dependency links/content, DB/call/bindings, compiler diagnostics, unrelated-content preservation, save/fresh reopen, interruption/no-replay behavior, initial-checkpoint recovery and independent simulator assertions. The existing separate-operation evidence is not combined-operation evidence.

## Internal component coordinator

The next implementation increment provides `PlcComponentCoordinator` and `NativePlcComponentOperations` in the Openness worker. The adapter delegates to existing `LibraryChangePrepare`/`LibraryChangeApply` and `ChangePrepare`/`ChangeApply`; it does not bypass their stale-project, dependency, protection, preservation, checkpoint or compilation guards. The library checkpoint already covers the complete target project and its project library, so the coordinator adopts that **first import checkpoint** as the initial recovery anchor instead of creating a second independent archive before import.

The exact internal native qualification factory is:

```csharp
CreatePlcComponentQualificationCoordinator(planChangeId, previewHash, absoluteArtifactRoot)
```

It accepts only the exact current-session prepared PLC plan in the worker's sole, owned scratch project. The private factory is reachable only through the explicit `--component-qualification` launch and the qualification kind described below. Normal host/worker launches reject it with `ComponentQualificationDisabled`; it is not advertised as a production capability. The adapter is implemented and compiled. The earlier existing-caller attempt stopped before import at its native metadata-preservation boundary; standalone installation still requires its own complete native qualification. Applying the original production `library_integrate` change continues to return `PlcIntegrationNotQualified`.

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

Keep the native artifact root short for Siemens archive/project path limits. In existing-caller mode a
normal native block title after the final compile unit may prevent terminal
insertion; the driver retains the exact caller and planner blocker and stops
before component import. The first native run reached exactly that boundary:
FB/UDT authoring, dependency/archive validation and consumer staging succeeded,
then following native title IDs blocked insertion before import. Both owned
sessions exited cleanly. Combined installation remains unqualified; see the
[third increment evidence](../verification/parallel-third-20261012.md#component-qualification).
The retained native02 attempt completed exact FB/UDT import, compile and save, then failed raw version-content comparison. The installed FB exposed three content attributes absent from its source `WithDefaults` export: `AutoNumber=true`, `IsIECCheckEnabled=false`, and `MemoryLayout=Optimized`. Native03 authored these settings explicitly but its global-library base export still omitted them, so the pre-consumer gate correctly stopped before import. The read-only xp01 diagnostic then exported the exact native02 source/installed versions with all four export-option combinations. No symmetric option gave equal FB bytes. `WithDefaults | WithReadOnly` did expose all three exact source values with `ReadOnly="true"`; their installed values and native instance getters agreed.

The explicit policy `v21-fb-explicit-source-oracle-three-properties-v1` records the base raw hash, full-source oracle raw hash/artifact, source global-library name and three exact values in the reviewed plan. The same exact native version object emits both source documents. The existing library import preparation binds these pins through an internal argument unavailable to normal payloads. Its prepared preview hash includes them, and its existing pre-mutation fingerprint re-exports and checks both source hashes under native exclusive access before any claim/checkpoint/import. Every post-import readback rechecks both source hashes and retains installed base/full hashes plus native FB setting getters.

The comparison preserves every remaining base XML node after verifying/removing only those three exact direct FB additions from the installed comparison copy. It separately compares the complete source and installed full-oracle trees, permitting only the three exact `ReadOnly="true"` markers to become absent, the version's direct `OriginalLibrary` to equal the reviewed source global name, and the content FB's direct `CreationDate` provenance value to differ. Both creation-date leaves must be explicit native UTC XML scalars with the expected read-only marker. Their bytes remain retained; native DateTime ticks/Kind are observations only, with no inferred timezone conversion or temporal-bound claim. All other read-only fields, timestamps, content, IDs, comments, unknown XML and significant whitespace remain protected by the full comparison. `DocumentInfo` is forbidden, so the generic native envelope exception cannot apply. Missing, duplicate, nondefault, namespaced-substitute, altered or extra fields fail closed. No generic default inference or metadata ignore list is used.

The revised qualification driver independently checks the pinned full-source oracle before consumer import and retains `sourceContentEvidenceAttempt` before validation. Returned readback observations remain durably retained as `importEvidenceAttempt`, `preTypedEvidenceAttempt` or `installationEvidenceAttempt` before validation; attempted fields never establish a verified stage. Successful evidence adds `contentVerification` with the policy ID, all four hashes and both full-comparison results. Native04 must qualify the complete standalone sequence on a new build/evidence freeze. Production combined apply, OB insertion and runtime remain unavailable under this evidence.

For an explicitly requested recovery of a failed run while preserving its original evidence, use a fresh output/native directory and append `--recover-from-report <failed-result.json>` to the command above. This separate mode validates the original first-import checkpoint identity and hash plus all baseline XML hashes, calls existing `project_retrieve` once in an empty owned session, compares the complete baseline after retrieval and fresh reopen, and verifies absence of component library types. It uses no installation `change_apply`, does not resume the component, and leaves the original first-import journal unchanged. A failed or lost retrieval response causes cleanup without retry. Success qualifies only first-checkpoint recovery; installation and runtime flags stay false.

Run offline checks with `python -B scripts/test_plc_component.py`, the existing
planner/coordinator test project, and the new
`TiaMcp.ComponentQualification.Boundary.Tests` net48 executable. The latter invokes
the actual worker dispatcher on unattached sessions to verify default-off and
owned-scratch refusal; it never constructs or starts a native TIA session.
