# Shared engineering identity, admission and recovery

The additive AC-02/AC-03 substrate is implemented in [NativeTargetObservation](../../src/TiaMcp.Core/Engineering/NativeTargetObservation.cs), [EngineeringOperationStore](../../src/TiaMcp.Core/EngineeringJobs/EngineeringOperationStore.cs) and the [operation contract](../../contracts/engineering-operation.v1.schema.json). It extends the existing [capture identities](engineering-capture-identities.md), `PackageStorage` path/lock/hash helpers and the established flushed write-ahead journal pattern. Existing native change/library journals are preserved.

The production Host/worker now integrates this store for the first scoped [watch workflow](watch-table-workflow.md), including native reacquisition, exclusive access, archive checkpoint, one-use dispatch, readback, fresh reopen and explicit recovery. Git mutations also use durable callbacks. This is not completed AC-02/AC-03 qualification: eight new watch/status tools retain partial implementation and unverified broad qualification. See the [production wave evidence](../verification/production-watch-wave-20261015.md).

## Native observations

A trusted native adapter supplies the exact captured object, physical project file, worker session epoch, adapter/profile SHA-256, hardware identity, optional exposed native identity, offline state and bounded named probes. `NativeTargetGuard.EnsureFresh` permits a new observation receipt ID but requires the same target, physical project, epoch, binary, profile, hardware and known source/content hashes. Reconnect or changed content requires preparation again. Probe states distinguish observed, empty, absent, blocked, failed and unknown; only observed satisfies a required prerequisite.

The record does not manufacture native GUIDs, licenses, current content or full preservation from supplied strings. The watch adapter computes and retains a bounded manifest from native observations; broader adapters must do the same for their domains. The callback is an internal trusted adapter seam, not a caller-provided authority. Native exclusive access surrounds watch reacquisition, journal dispatch and import. The store's OS lock alone cannot prevent another native client changing a project.

## Durable operations

Exact operation names, idempotency keys, effect descriptions, probe/gate names and artifact/recovery paths reject malformed Unicode before serialization can replace it. Incoming checkpoint, dispatch, outcome, interruption and reconciliation evidence is checked before freezing; refusals publish no revision and retain ownership. Valid Unicode is not trimmed, case-folded or normalized by the text guard. Existing filesystem canonicalization and package-path restrictions still apply.

Plans freeze exact target/profile, resource effects, immutable input references, required probes and verification gate names. Preparation validates the frozen copy and artifact bytes. A key deduplicates only the same canonical plan; reuse for different content refuses. Records use unique IDs and immutable numbered JSON revisions, with plan and predecessor hashes. Each write flushes before an atomic publish. Initial preparation publishes a staged directory only after its first record exists; abandoned prepublication staging cannot authorize native work or wedge unrelated admission.

| Transition | Meaning |
| --- | --- |
| prepared → acquired | Reserve all canonical resources together, refusing overlap with outstanding work. |
| acquired → checkpointIntent | Reacquire target/input freshness and persist before checkpoint save/archive effects. |
| checkpointIntent → ready | Verify one ordered checkpoint artifact per planned resource. |
| ready → dispatchIntent | Revalidate retained checkpoints, inputs and newly reacquired native target before dispatch. |
| dispatchIntent → dispatchIntent | Append exactly one immutable `dispatch.*` result artifact; this is evidence retention, never a second dispatch. Ownership remains held. |
| dispatchIntent → succeeded | Every required named evidence artifact exists with its bound hash. Domain adapters must validate its semantics. |
| dispatchIntent → recoveryRequired | Retain original diagnostics in a mandatory `failure` artifact; failure does not imply rollback. |
| acquired/ready/checkpointIntent/dispatchIntent/recoveryIntent → outcomeUnknown | Retain ownership after loss, timeout or interruption, including a lost native lease reply/release. Never infer cancellation or replay. |
| prepared/acquired → stopped | Release before any checkpoint/native intent, with native lease release accounted for. Later stop requests require reconciliation. |
| ready/outcomeUnknown/recoveryRequired/succeeded → recoveryIntent | Reacquire original resources, reserve distinct recovery destinations and verify selected-resource checkpoint hashes before any restore. |
| attempted state → reconciled | Independently resolve every original resource and every admitted recovery destination. This is not a successful engineering/test outcome. |

Every load validates the complete state/evidence chain, including the final revision. A forged successful tail without required gates cannot release reservations. Original failed revisions and diagnostics remain required after reconciliation. Recovery receipts and separate original/restored observations are append-only, hash-bound evidence. Compare-and-swap revision checks prevent stale callers from appending work.

The watch adapter retains returned apply readback before validating it, so a rejected result also remains inspectable through operation status. Final success/failure appends to that evidence instead of replacing it. A lost fresh-reopen reply marks the outcome unknown with the earlier readback still bound. Restart after acquisition requires explicit fresh readback reconciliation; it cannot resume the lost lease. See the [adapter boundary verification](../verification/ac0203-adapter-hardening-20261016.md).

## Ownership and restore boundaries

All cooperating hosts must use the same configured artifact root. Reservations are durable within that store and never expire by elapsed time. A process dying releases its OS lock but retains intent and resource reservations. Cross-host/global resource admission is not established merely by constructing separate stores.

`EngineeringResourceKey.ForPath` is the shared factory for project, library and workspace keys. It resolves absolute local paths, rejects traversal/device/reparse aliases, canonicalizes Windows final paths through filesystem handles, refuses multiply linked files and network/mapped paths, and checks ancestor/descendant overlap. The native reservation provider is currently a local Windows profile; remote server identity needs a separate provider. These guards do not isolate hostile concurrent OS writers or provide a global native transaction.

Recovery strategies distinguish project archives, library copies, immutable workspace versions and external commit reconciliation. Restores must be admitted at distinct nonoverlapping destinations. Each admitted destination then needs its own verified-restored, verified-absent or retained-partial evidence, even when the original resource is unchanged. An unknown destination cannot silently disappear from reconciliation. A local restore cannot undo an external commit.

The substrate validates evidence identity/retention, not arbitrary evidence contents. Each domain must independently validate compile, readback, full preservation, fresh reopen and cleanup as applicable. [Test Suite style integration](testsuite-durable-jobs.md) adds concrete result/scope validation; [Git integration](vci-git-runner.md) describes the implemented durable callback adapter. The watch Host dispatcher independently checks its concrete evidence. Qualification helpers remain separate evidence collectors.

Lost checkpoint/retrieval responses can leave no bound receipt. The watch interrupted-reconciliation route requires a fresh saved original and independently verified baseline or reviewed outcome. Any admitted destination must be verified absent/empty or retained as partial output using bounded path and hash inspection. Alternatively, an independently observed baseline inside the exact admitted retrieval destination can be bound as `recovery.located`; another fresh restored reopen and separate fresh original observation remain mandatory. No retrieval, deletion or mutation is replayed. Uncertain outputs retain reservations. A Host exit after the last role proof is persisted can resume the terminal transition from both retained, independently revalidated proofs without native calls.

## Shared admission for existing mutation routes

`EngineeringMutationAdmissionStore` and the native private `mutation_begin/dispatch/end` protocol now cover `change_apply`, `change_restore`, `simulation_download`, `simulation_provision_scratch_credential`, `library_open`, `scratch_create`, `project_retrieve`, and qualification fixture seeding. The adapter resolves exact project/library/workspace resources and plans new destinations while holding native exclusive access. The dispatcher commits every resource before one-use dispatch; both journal families inspect each other under the same OS lock. Requests outside the configured artifact root and raw native admission bypasses refuse. Completed same-key change/restore responses preserve their existing idempotent semantics.

The additive [admission contract](../../contracts/engineering-mutation-admission.v1.schema.json) separates `dispatchIntent`, `outcomeUnknown`, `returned` and `reconciled`. A retained native response releases only the dispatch reservation; existing domain journals still govern their engineering outcome and recovery. A lost response retains every resource indefinitely. Reconciliation requires a trusted domain adapter, not arbitrary caller-provided outcome strings. Existing historical unresolved journals are not silently adopted. Multi-step component qualification remains refused until its complete resource manifest is available. These routes have managed adversarial coverage but the newly built adapter's native qualification is blocked by startup timeouts; see the [recovery expansion wave](../verification/recovery-expansion-wave-20261015.md).

## Verification and residuals

Failed admitted calls that return a bridge error include
`details.mutationAdmission.admissionId`. Pass that ID as `operationId` to the
existing `engineering_operation_status` tool to inspect the retained journal
without a native call. The original error code/message and object detail fields
remain unchanged; non-object native details are retained under `nativeDetails`.
The identifier does not assert a terminal state, release a reservation or permit
automatic replay. If a journal update fails, its earlier dispatch intent still
owns the resources. Failures before acquiring an admission do not invent an ID.

[Engineering tests](../../tests/TiaMcp.Engineering.Tests/EngineeringOperationChecks.cs) exercise separate-process ownership/restart/locking, exact freshness, changed artifacts, lost outcomes, no replay, invalid terminal journals, interrupted preparation, retained diagnostics, recovery destinations, absent checkpoints, path overlap and real NTFS hard links. All callbacks and archive bytes in this suite are synthetic. Native watch/workbook/VCI evidence is separately recorded in the [current wave report](../verification/expansion-foundation-wave-20261015.md).

Still required: native qualification of migrated routes, historical unknown-journal reconciliation, complete component resource manifests, broader native manifests/probe providers, cross-host configuration, complete queued cancellation/deadline behavior, every native interruption point and resource-specific restoration profile, broader target profiles, bounded retention/administration, and the remaining AC-01 semantic/schema/manual mapping. The scoped native outcomes are recorded separately; they do not close these 1.0 obligations.

## External resources (WA-09 portable portion)

The same journal now accepts `kind: external` effects alongside the mandatory
project resource. [External observations](../../contracts/engineering-external-observation.v1.schema.json)
bind the operation ID, canonical plan hash, prior revision, resource key, phase,
observation ID and independently retained public state bytes. Before/after states
are known `present` or `absent`, each with a SHA-256; unknown/unreadable state never
satisfies a checkpoint, success gate or reconciliation obligation.

`EngineeringResourceKey.ForExternal` derives an exact owner key from provider,
authority and owner ID, then an optional resource key from kind and ID. Reserving
an owner conflicts with each of its resources, even across different projects.
Separate resources and authorities remain independent. Selectors are bounded,
case-sensitive public identifiers; they must not contain credentials. A trusted
provider must canonicalize aliases and authenticate the physical authority. The
reservation scope is this configured artifact store, not a distributed lock across
hosts or independently configured stores.

Each external effect requires `externalStateReadback` and its fixed
`EngineeringExternalEvidence.OutcomeGate` success gate. Its ordered checkpoint
must independently bind the expected baseline; successful dispatch requires a
separate desired-state observation. A checkpoint cannot substitute for a later
readback. All phases bind the exact prior journal revision, and loading a journal
revalidates historical wrappers and retained provider bytes. Old local-only
resource serialization omits the additive field, preserving old plan/record hashes.

Loss after dispatch remains pending/unknown with reservations held. Diagnostic
observations can be appended without authorizing retries. Reconciliation requires
`externalBaselineObserved` or `externalDesiredStateObserved` for each external
resource, plus independent resolution of every local resource and admitted restore
destination. Neither local project restoration, an external Git commit, a generic
`unchanged` label nor an unreadable endpoint clears external ownership. External
resources cannot be assigned local recovery destinations. Reconciliation records
an observed state; it does not assert that a restore action occurred.

Only the existing read-only `engineering_operation_status` Host tool exposes this
extension. There is no caller-facing external dispatch/reconcile API and no live
provider. Typed receipts validate context and retained bytes, not provider trust,
content semantics, endpoint reachability or restoration of private keys/passwords.
A future trusted domain adapter must acquire and validate those observations and
apply its own native admission policy. The synthetic provider tests do not qualify
Siemens, UMC, certificate trust or deployment behavior. Public proof artifacts are
not a secret vault. The separate [local protected credential commands](protected-credentials.md)
now provide DPAPI references, secure input and local retirement. Trusted native
provider integration and profile/endpoint recovery remain open WA-09 work.

## Protected references in operation plans

Optional public credential requirements now bind exact vault references and expected
external owner/resource context into the immutable plan. The trusted Host vault
admission pins both dispatch and recovery generations before acquiring the journal;
checkpoint/dispatch revalidate them, and durable intent prevents replay. Retirement
requires every pinned journal to be terminal and valid. See the [credential ownership
workflow](protected-credentials.md#operation-ownership-and-single-dispatch) for
lock ordering and version compatibility. Trusted credential recovery now retains a
freshly reacquired `recoveryTarget` before one-use consumption, including external-only
recovery with no local destination. Old admitted local recoveries remain readable
without that field and cannot be consumed again. All resource/destination outcomes
still require independent reconciliation; native provider qualification remains open.
