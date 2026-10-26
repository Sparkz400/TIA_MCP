# Open TODO

Work resumed autonomously on 24 October; the [23 October checkpoint](verification/user-stop-20261023.md) remains historical evidence.
This list contains unresolved work only. Earlier progress and completed checklists
are retained in [history](history/todo-through-20261023.md) and [STATUS](STATUS.md).

## Resume and integration

- [x] Independently review vendor receipts, licenses and prerequisites. All seven retained PAL/DMC ZIPs, recorded license hashes and inner-archive hashes match. Version-specific notices, older setup guides and the unreviewed toolbox dependency boundary are recorded in the [independent review](verification/vendor-intake-review-20261025.md). Native compatibility, DMC CPU setup and HMI/runtime gates remain separate.
- [ ] Qualify DMC's remaining prerequisite: system and clock memory bytes on every CPU. They are outside the reviewed hardware write allowlist, so DMC FB compilation is not claimed.

## Native admission and runtime

- [x] Fresh-process PLCSIM discovery is integrated into credential provisioning and passed end to end on 24 October. [Production integration](verification/plcsim-startup-discovery-20261024.md#production-integration).
- [ ] Card staging failed once with access denied (`0x80070005`) for 15 s and then not again in later runs; cause unidentified. Trusted reconciliation for failed provisioning's `outcomeUnknown` admission is implemented and released one real admission ([reconciliation](verification/provision-reconciliation-20261024.md)). The runner now stops remaining cases even when the worker reports successful cleanup; errors identify the retained admission for read-only inspection. [Resume evidence](verification/runtime-admission-resume-20261024.md), [native stop](verification/native-stop-20261023.md).
  Actual provisioning verifies archive creation and durable recording before staging/startup. Both runtime callers explicitly checkpoint/compile/save after card generation; the exact save sequence passes a native experiment, but updated runner end-to-end acceptance remains open. A verified read/copy path now retains the unresolved reservation, retrieves its archive in a fresh TIA session and matches all four fixed scratch block payloads. Complete project preservation, opaque-storage preservation and trusted release remain open. [Checkpoint-copy retrieval](verification/runtime-admission-resume-20261024.md#verified-checkpoint-copy-retrieval).
- [x] Native requested stop, independent worker exit, lock release and owned cleanup are qualified ([requested stop](verification/requested-stop-20261024.md), [card inventory and lock release](verification/card-inventory-and-lock-release-20261024.md), [FileStorage exit](verification/file-storage-exit-20261024.md)).
  Recovery-only runs now show zero exit codes for all nine retained processes, but the file-storage helper still misses the deadline. This does not close simulator-use shutdown or original-project lock-release gates. [Recovery lifecycle evidence](verification/runtime-admission-resume-20261024.md#verified-checkpoint-copy-retrieval).
- [x] One frozen 65-case / 358-step / 478-assertion runtime run passed with an independent audit on 24 October ([frozen corpus](verification/runtime-frozen-20261024.md)).

### Other native machine (VM), not this PC

Only relevant if work resumes on that machine; its guard registry and evidence are not here.

- [ ] Resolve native dispatch admission there. The startup timeout left ticket `090f8915a47c401eb70201c452af2585` unresolved because complete process-tree capture was unavailable. Preserve historical tickets and failed outcomes; do not replay uncertain operations or bypass the guard. [Native checkpoint](verification/native-resume-20261023.md).
- [ ] Reconcile the VM's uncertain registration/write/restoration outcomes around its zero-address entry. [Runtime plan](plans/runtime-proof-todo.md).
- [ ] Explain the added post-RUN card `.info` inventory and prove complete project/card and PLC preservation. On this PC no `.info` appears and the post-RUN change is one identical pattern ([card inventory](verification/card-inventory-and-lock-release-20261024.md)); the VM observation stays open.

## Engineering workstreams

- [ ] **Typed LAD/FBD:** finish candidate lineage, builder engineering, unrelated-content preservation, persistence and recovery before enabling inactive profiles. Complete scoped operand/storage qualification; existing-caller insertion passed under the renumbering decision (`local.typed.insertion.20261025`), 24 software-unit/namespace route cases passed engineering (`local.typed.routes.engineering.20261025`), and all 80 reference route cells now pass the four engineering gates, including TOF/TP/CTU/CTD/CTUD (`local.typed.route-iec.20261026`); namespace resolution passes and duplicate-tag ambiguity is a native V21 refusal (`local.typed.resolution.20261025`). [Typed TODO](plans/typed-lad-fbd-todo.md).
- [ ] **Typed runtime:** scoped operands/storage remain open. Boolean/comparison and FBD cells, independent FB state, counter signed limits/saturation/reset and timer cancellation/restart/completion pass on the standard route (all 38 cells, [typed qualification](verification/typed-lad-fbd-qualification-20261024.md)). Native templates do not substitute for candidate lineage. [Evidence boundaries](verification/typed-p17-qualification.md).
- [ ] **PLC components:** audit native05's recovery (its artifacts are not on this PC). A fresh complete FC/DB/call/bindings installation with behavior ([fresh install](verification/component-fresh-install-20261024.md)) and installation into an existing FBD FC caller under the [title-ID decision](plans/insertion-id-renumbering-decision-20261025.md) ([existing caller](verification/component-existing-caller-20261025.md)) passed. Keep combined apply disabled and OB insertion separate. [Component workflow](guides/plc-library-integration-recipes.md).
- [ ] **Libraries:** qualify behavior-changing and breaking releases with independent behavior, protected objects and software-unit relations. Broader dependency, exact reuse, collision/default, lifecycle and cleanup cases, multi-PLC scope, native interruption and multi-resource recovery passed ([lane C](verification/library-lane-c-20261024.md)). [Open library checklist](plans/library-management-todo.md).
- [ ] **Classic/Unified HMI:** qualify complete content preservation, compiler scope, normal lifecycle and independent lock release. Production-bridge archive retrieval, read-only inspection/reference/Explorer behavior and persistence/recovery passed for five profiles ([bridge](verification/hmi-bridge-retrieval-20261025.md)). [HMI plan](plans/hmi-remaining-work.md).
- [ ] **HMI authoring:** implement/qualify preserving Classic faceplates, actual Unified binding, HMI-owned UDTs, external PLC connections/tags, scripts, graphics, translations and combined PLC/HMI update recipes. Complete visual/runtime acceptance for each advertised family. [HMI scope](plans/library-management-todo.md#p1--classic-and-unified-hmi).
- [ ] **HMI prerequisites:** obtain eligible targets for outstanding families, including RT Professional, and test optional products absent on real installations. Family-specific evidence is not interchangeable.
- [ ] **Hardware/remote I/O:** extend hardware families, companion layouts, controllers and shared topology. The reviewed writable channel attribute (`Smoothing`) and the full single-controller remote-I/O route passed (`local.remote-io.full-route.20261024`). Physical runtime remains separate. [Network boundary](guides/network-inspection.md).
- [ ] **Explorer/reviews:** establish complete native relationship providers/collection scope and fresh desktop/native transport acceptance. VS Code retained-snapshot rendering and interaction passed; the native roundtrip stays open ([desktop evidence](verification/explorer-desktop-20261025.md)). [Explorer plan](verification/project-explorer-qualification.md).
- [ ] **Compatibility:** finish older-library upgrades, protected sources, existing diagnostics/unsaved edits and version/target cases. Locked sources are refused and one software-unit destination passed (`local.library_units.20261025`). [Library matrix](plans/library-management-plan.md).
- [ ] **Scale:** measure native 10,000-element traversal and checkpoint cost, qualify combined 100-component behavior and representative large vendor byte volumes. Generated inputs and separate single-component plans do not close these native requirements. [Scale boundaries](verification/library-scale-20261023.md).
- [ ] **Remaining roadmap:** complete every outstanding feasible obligation in the [44-workstream release ledger](plans/release-obligations-20261015.md) and [machine-readable ledger](plans/release-obligations-20261015.json), including native diagnostics/acquisition, security, persistence, recovery and runtime gates. This priority list does not narrow 1.0 scope.

## Release acceptance

- [ ] Requalify `deployment.vm_qualification` (needs the VMware guest); the other 35 demoted features are verified. See [requalification](verification/catalog-requalification-20261024.md).
- [x] Freeze the advertised V21 matrix. [`docs/plans/release-matrix.v1.json`](plans/release-matrix.v1.json) is generated from the catalog and obligation ledger by `scripts/release_matrix.py` and checked by `scripts/test_release_matrix.py`. Its 339 rows are 35 shipped, 14 limited, 279 unqualified, 1 unavailable target and 10 deferred. Each bound cell names its evidence records and the SHA-256 of their retained artifacts.
- [ ] Close every open matrix cell. 1,379 of 1,542 non-deferred feature × acceptance-criterion cells are open; the matrix summary counts them per owning obligation. Cells are per feature. CPU/firmware/language/HMI-profile variants appear only where evidence records a profile, so per-variant cells still have to be added as qualification names them.
- [ ] Qualify native interruption/transport loss and uncertain-outcome reconciliation for every advertised recoverable mutation, including multi-resource recovery and no replay.
- [ ] Complete missing feature-specific remote/VM transfer, interrupted upload/reconnect and machine-prerequisite checks. Do not repeat accepted general VM evidence without a new reason.
- [ ] Validate the intended immutable engineering package, launcher and deployment against the full matrix. An offline bundle does not establish native support or authorize installation.

## Repository and hosted CI

- [ ] Review subsequent dependency PRs, especially native NuGet changes, and perform applicable SDK/runtime qualification.
- [ ] Require independent reviews when additional contributors participate.
- [ ] After full acceptance, choose the version, create the tag, write release notes with exact limitations, and attach the verified package, license and integrity manifest.
