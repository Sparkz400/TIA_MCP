# Open TODO

Work resumed autonomously on 24 October; the [23 October checkpoint](verification/user-stop-20261023.md) remains historical evidence.
This list contains unresolved work only. Earlier progress and completed checklists
are retained in [history](history/todo-through-20261023.md) and [STATUS](STATUS.md).

## Resume and integration

- [ ] Finalize independent review of the two vendor intake receipts, licenses and prerequisites; qualify applicable native import/upgrade, readback and preservation. Vendor evidence (`artifacts/vendor-library-intake-20261023/`).
- [ ] Finish reconciling the feature catalog, support matrix and embedded catalog with exact current source, binary and evidence profiles. The empty-CPU `sim_state` profile is reconciled in the [24 October startup evidence](verification/simulator-startup-20261024.md); broader operation qualification remains open. Keep implementation, offline validation and native qualification separate.

## Native admission and runtime

- [ ] Qualify explicit discovery refresh for the owned PLCSIM Advanced CPU. Pre-start discovery leaves headless polling on the placeholder and provisioning refuses at `discoverExactRuntimeAddress`. The UI's scoped **Update accessible devices** action reveals the real endpoint. Delaying the first enumeration until CPU STOP also succeeds headlessly, but omits pre-start isolation proof; it is not a production remedy. A previously unqueried DownloadProvider, successful offline route selection, and saved project close/reopen all fail to refresh the warm cache. Compare a fresh Portal session while retaining the pre-start isolation evidence, then qualify fresh target identity before provisioning. [Discovery comparisons](verification/plcsim-startup-discovery-20261024.md#offline-route-selection-and-project-reload).
- [ ] Card staging failed once with access denied (`0x80070005`) for 15 s and then not again in later runs; cause unidentified. Implement trusted reconciliation for failed provisioning's `outcomeUnknown` admission. The runner now stops remaining cases even when the worker reports successful cleanup; errors identify the retained admission for read-only inspection. This does not release reservations. [Resume evidence](verification/runtime-admission-resume-20261024.md), [native stop](verification/native-stop-20261023.md).
  Actual provisioning verifies archive creation and durable recording before staging/startup. Both runtime callers explicitly checkpoint/compile/save after card generation; the exact save sequence passes a native experiment, but updated runner end-to-end acceptance remains open. A verified read/copy path now retains the unresolved reservation, retrieves its archive in a fresh TIA session and matches all four fixed scratch block payloads. Complete project preservation, opaque-storage preservation and trusted release remain open. [Checkpoint-copy retrieval](verification/runtime-admission-resume-20261024.md#verified-checkpoint-copy-retrieval).
- [ ] Qualify actual native requested stop, independent worker exit, lock release and owned cleanup before another long runtime batch. Empty-CPU MCP startup/shutdown passes without TIA. The latest TIA probe's owned file-storage helper exits naturally after about five minutes, beyond its deadline; earlier forced cleanup remains historical. Exclusive reads of eight project files passed, but normal TIA shutdown/native reopen and full project-lock release remain unqualified. [Startup probe](verification/plcsim-startup-discovery-20261024.md), [native stop](verification/native-stop-20261023.md), [stop requirements](guides/block-testing.md#cooperative-stopping-and-remaining-cases).
  Recovery-only runs now show zero exit codes for all nine retained processes, but the file-storage helper still misses the deadline. This does not close simulator-use shutdown or original-project lock-release gates. [Recovery lifecycle evidence](verification/runtime-admission-resume-20261024.md#verified-checkpoint-copy-retrieval).
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
