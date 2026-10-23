# Library management: open work

Work is paused at the [23 October checkpoint](../verification/user-stop-20261023.md).
Completed checklists and dated progress are in [history](../history/library-todo-through-20261023.md).
The [main TODO](../TODO.md) sets priorities; the [release ledger](release-obligations-20261015.md) retains the full required scope.

## P0 — native admission and release evidence

- [ ] Resolve current native admission before a fresh owned-fixture run. Preserve unresolved tickets, failed outcomes and original bytes. [Admission checkpoint](../verification/native-resume-20261023.md).
- [ ] Qualify an implementation-changing FB release and a breaking-interface release. Independently assert call/instance behavior, expected diagnostics, exact versions and unrelated-content preservation.
- [ ] Expand dependency qualification to nested UDTs, arrays and dependent FBs. Test installed exact dependencies, same-name/different-GUID collisions, divergent content under identical identities, older/newer defaults and unsupported downgrades.
- [ ] Qualify cleanup with unrelated unused types, isolated cycles, references removed by updates, multiple PLCs and software units. Prove refusal before checkpoint/native mutation when reachability is missing; retain conservative refusal for uses reachable only through an updated object.
- [ ] Inject worker/transport failures around native checkpoint, mutation, compile and save. Prove durable reconciliation, no replay, multi-resource integrity and recovery followed by fresh reopen, including a crash during a Siemens mutation.
- [ ] Update catalog/support evidence with exact source, binary and profile limits; rebuild the embedded catalog and check contracts. Do not promote all library/HMI features from scoped evidence.

## P1 — PLC integration and version workflows

- [ ] Qualify the complete `library_integrate` sequence: exact type/UDT versions, instance DB, standalone FC, directions/mappings, native readback, compile/save/reopen, full preservation, first-checkpoint recovery and runtime. Keep combined apply disabled until its gates pass; OB insertion remains separate.
- [ ] Implement deliberate no-op reuse of installed exact dependencies only where native identity/content proof permits it. Never infer identity from a matching name.
- [ ] Qualify edit/discard, defaults, type-policy flags, copy/publication into existing libraries, folder changes, deletion constraints and recovery. Reject malformed mixed policy flags before mutation.
- [ ] Test multiple instances/selected types, numbering/namespace conflicts, software-unit destinations, protected/unsupported contents and projects with diagnostics or unsaved edits.
- [ ] Decide/document permitted update-scope expansion. Retain `entireSelectedPlc`, no forced downgrade and no arbitrary per-instance version assignment until the native route is proved.
- [ ] Complete independent post-operation preservation/readback, selector resolution and referenced-content verification for every recipe. Serialized previews do not establish native absence, master-copy eligibility, complete preservation or behavior.

## P1 — Classic and Unified HMI

- [ ] Qualify exact production-bridge retrieval/inspection of the configured owned fixture, including independent process/lock release and complete preservation. [Native boundary](../verification/native-resume-20261023.md).
- [ ] Build/qualify a preserving Classic faceplate XML adapter from independent native exports, covering exact versions, placement, interfaces, bindings, resources and missing dependencies.
- [ ] Qualify Unified faceplate creation/binding through the MCP worker with exact GUID readback. Add HMI-owned UDTs, external PLC connections/tags and explicit UDT/interface mappings.
- [ ] Exercise eligible HMI create/edit/test/release/publish/fresh-consumer lifecycles for each advertised category. Unsupported API routes must remain explicitly unsupported.
- [ ] Add combined PLC/UDT/HMI recipes, including a field-changing update and broken-binding failure with a precise impact report.
- [ ] Qualify screen/resource/event/script/multilingual preservation, compiler scope, save/reopen, recovery, and visual/runtime assertions for layout, navigation, values and PLC communication.
- [ ] Qualify Comfort, Advanced, Professional, Basic, Unified Comfort and Unified PC separately; obtain missing targets and test actual installations with optional products absent.

## P2 — vendor packages, compatibility and delivery

- [ ] Finalize independent vendor-intake evidence and license/prerequisite review, then qualify applicable native import/upgrade/readback for retained PracticalSeries PLC and DMC PLC/HMI distributions. Inputs and receipts (`artifacts/vendor-library-intake-20261023/`). Intake metadata does not establish native compatibility.
- [ ] Qualify V14–V20 upgrades into V21 on owned copies, unsupported/newer formats, complete native directories, mixed example/source packages, locked/read-only sources and preservation.
- [ ] Test missing feature-specific remote/VM transfer and interrupted upload/reconnect with the final bundle, Unicode/space paths and machine prerequisites.
- [ ] Measure native 10,000-element traversal, checkpoint cost, combined 100-component behavior and representative large vendor byte volumes. Managed measurements do not close native gates. [Measurement boundaries](../verification/library-scale-20261023.md).
- [ ] Reconcile interrupted regression/package batches and complete fresh validation against the intended immutable full engineering release. Hosted CI, native acceptance and installation remain separate. [Stop checkpoint](../verification/user-stop-20261023.md).
