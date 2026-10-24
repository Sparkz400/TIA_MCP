# Open TODO

Work resumed autonomously on 24 October; the [23 October checkpoint](verification/user-stop-20261023.md) remains historical evidence.
This list contains unresolved work only. Earlier progress and completed checklists
are retained in [history](history/todo-through-20261023.md) and [STATUS](STATUS.md).

## Resume and integration

- [ ] Finalize independent review of the two vendor intake receipts, licenses and prerequisites; qualify applicable native import/upgrade, readback and preservation. Vendor evidence (`artifacts/vendor-library-intake-20261023/`).
- [ ] Reconcile the feature catalog, support matrix and embedded catalog with exact current source, binary and evidence profiles. Keep implementation, offline validation and native qualification separate.

## Native admission and runtime

- [ ] After startup the owned PLCSIM Advanced CPU (`192.168.0.1`) never appears in PN/IE / PLCSIM discovery, so provisioning refuses at `discoverExactRuntimeAddress`. A fresh empty-CPU probe still sees only the placeholder in 61 samples over 62 seconds; a timeout increase alone is unsupported. Investigate the discovery semantics/local route. [Startup probe](verification/plcsim-startup-discovery-20261024.md).
- [ ] Card staging failed once with access denied (`0x80070005`) for 15 s and then not again in later runs; cause unidentified. Implement trusted reconciliation for failed provisioning's `outcomeUnknown` admission. The runner now stops remaining cases even when the worker reports successful cleanup; errors identify the retained admission for read-only inspection. This does not release reservations. [Resume evidence](verification/runtime-admission-resume-20261024.md), [native stop](verification/native-stop-20261023.md).
- [ ] Qualify actual native requested stop, independent worker exit, lock release and owned cleanup before another long runtime batch. Installed prerequisite metadata passes and the placeholder exclusion is integrated. Exact CPU discovery remains blocked; the fresh empty-CPU probe also needed forced cleanup of its owned file-storage helper after worker/Portal exit. Exclusive reads of its eight project files passed, but normal shutdown/native reopen and full project-lock release remain unqualified. [Startup probe](verification/plcsim-startup-discovery-20261024.md), [native stop](verification/native-stop-20261023.md), [stop requirements](guides/block-testing.md#cooperative-stopping-and-remaining-cases).
- [ ] Complete one frozen 65-case / 358-step / 478-assertion runtime attempt and independent audit after admission and preservation prerequisites pass. Separate subsets do not replace the full attempt.

### Other native machine (VM), not this PC

Only relevant if work resumes on that machine; its guard registry and evidence are not here.

- [ ] Resolve native dispatch admission there. The startup timeout left ticket `090f8915a47c401eb70201c452af2585` unresolved because complete process-tree capture was unavailable. Preserve historical tickets and failed outcomes; do not replay uncertain operations or bypass the guard. [Native checkpoint](verification/native-resume-20261023.md).
- [ ] Reconcile the VM's uncertain registration/write/restoration outcomes around its zero-address entry. [Runtime plan](plans/runtime-proof-todo.md).
- [ ] Explain the added post-RUN card `.info` inventory and prove complete project/card and PLC preservation.

## Engineering workstreams

- [ ] **Typed LAD/FBD:** finish candidate lineage, builder engineering, unrelated-content preservation, persistence and recovery before enabling inactive profiles. Complete namespace/ambiguity, scoped operand/storage and strict insertion-ID qualification. [Typed TODO](plans/typed-lad-fbd-todo.md).
- [ ] **Typed runtime:** qualify Boolean/comparison and FBD cells, scoped operands/storage, independent FB state, counter signed limits/saturation/reset and timer cancellation/restart/completion. Native templates do not substitute for candidate lineage. [Evidence boundaries](verification/typed-p17-qualification.md).
- [ ] **PLC components:** finish a fresh complete FC/DB/call/bindings installation, preservation, save/reopen, first-checkpoint recovery and behavior. Qualify existing-caller metadata preservation separately. Keep combined apply disabled and OB insertion separate. [Component workflow](guides/plc-library-integration-recipes.md).
- [ ] **Libraries:** qualify behavior-changing and breaking releases; broader dependency, exact reuse, collision/default, lifecycle and cleanup cases; native interruption and multi-resource recovery. [Open library checklist](plans/library-management-todo.md).
- [ ] **Classic/Unified HMI:** qualify exact production-bridge archive retrieval, read-only inspection/reference/Explorer behavior, complete content preservation, compiler scope, persistence/recovery, normal lifecycle and independent lock release. [HMI plan](plans/hmi-remaining-work.md).
- [ ] **HMI authoring:** implement/qualify preserving Classic faceplates, actual Unified binding, HMI-owned UDTs, external PLC connections/tags, scripts, graphics, translations and combined PLC/HMI update recipes. Complete visual/runtime acceptance for each advertised family. [HMI scope](plans/library-management-todo.md#p1--classic-and-unified-hmi).
- [ ] **HMI prerequisites:** obtain eligible targets for outstanding families, including RT Professional, and test optional products absent on real installations. Family-specific evidence is not interchangeable.
- [ ] **Hardware/remote I/O:** qualify a reviewed writable channel attribute, complete the remote-I/O route, and extend hardware families, companion layouts, controllers and shared topology. Physical runtime remains separate. [Network boundary](guides/network-inspection.md).
- [ ] **Explorer/reviews:** establish complete native relationship providers/collection scope, actual desktop MCP Apps rendering and current packaged/native transport acceptance. [Explorer plan](verification/project-explorer-qualification.md).
- [ ] **Compatibility:** finish older-library upgrades, locked/protected sources, software-unit destinations, existing diagnostics/unsaved edits and version/target cases. [Library matrix](plans/library-management-plan.md).
- [ ] **Scale:** measure native 10,000-element traversal and checkpoint cost, qualify combined 100-component behavior and representative large vendor byte volumes. Generated inputs and separate single-component plans do not close these native requirements. [Scale boundaries](verification/library-scale-20261023.md).
- [ ] **Remaining roadmap:** complete every outstanding feasible obligation in the [44-workstream release ledger](plans/release-obligations-20261015.md) and [machine-readable ledger](plans/release-obligations-20261015.json), including native diagnostics/acquisition, security, persistence, recovery and runtime gates. This priority list does not narrow 1.0 scope.

## Release acceptance

- [ ] Freeze the advertised V21 operation/profile/target matrix and bind each cell to applicable engineering, preservation, persistence, recovery and runtime evidence with exact lineage.
- [ ] Qualify native interruption/transport loss and uncertain-outcome reconciliation for every advertised recoverable mutation, including multi-resource recovery and no replay.
- [ ] Complete missing feature-specific remote/VM transfer, interrupted upload/reconnect and machine-prerequisite checks. Do not repeat accepted general VM evidence without a new reason.
- [ ] Validate the intended immutable engineering package, launcher and deployment against the full matrix. An offline bundle does not establish native support or authorize installation.

## Repository and hosted CI

- [ ] Review subsequent dependency PRs, especially native NuGet changes, and perform applicable SDK/runtime qualification.
- [ ] Require independent reviews when additional contributors participate.
- [ ] After full acceptance, choose the version, create the tag, write release notes with exact limitations, and attach the verified package, license and integrity manifest.
