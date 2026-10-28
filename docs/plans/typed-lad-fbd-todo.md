# Typed LAD/FBD checkpoint and remaining work

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

See the [consolidated TODO](../TODO.md) for the current work order. This page retains detailed acceptance criteria and dated progress.

## Instruction families — 27 October 2026

- The builder, payload validation, JSON schemas and reader accept DInt/Real comparisons, MOVE,
  ADD/SUB/MUL/DIV, TONR and R_TRIG/F_TRIG (IEC instance storage like the timer/counter family),
  with exact operand/pin/overload types and explicit refusals. Offline tests use unit-test-only
  profiles. Their 60 generated networks were checked once against the installed V21
  `SW.PlcBlocks.LADFBD_v5.xsd` (outside the repository, so not a committed test).
- [ ] Capture the families: author SIMATIC SD sources (the repository records no SIMATIC SD syntax
  for MOVE, arithmetic or R_TRIG/F_TRIG), run `capture_typed_graphical_profiles.py`, add their pin
  contracts to `extract_typed_graphical_profiles.py`, then qualify the cells. Until then every request
  returns `GraphicalProfileUnavailable`. Created TONR/R_TRIG/F_TRIG storage also needs captured global
  IEC storage documents and IEC dependent-instance effect members.
  - Prepared, not executed: 69 handwritten standard-route sources (LAD/FBD, local and global IEC
    storage), extractor pin contracts checked against the wired native pins, 66 cases and runtime
    expectations. The [wave 9 runbook](../verification/typed-wave9-families-runbook.md) lists the
    native steps from capture to qualification. IEC effect members for created storage remain open.
- [ ] A second CPU: profiles, creation/instance templates and dependent-effect manifests are keyed by
  the exact CPU TypeIdentifier, and every capture is CPU 1511-1 PN `OrderNumber:6ES7 511-1AL03-0AB0/V4.1`.
  No typed capture exists for any other CPU. Missing: a capture on that CPU
  (`capture_typed_graphical_profiles.py --cpu-type-identifier`), profile IDs that do not reuse the
  hard-coded `v21.fw41.` prefix in `extract_typed_graphical_profiles.py`, and effect manifests whose
  file names (`instance-effects*-<route>.v1.json`) do not collide across CPUs. The Boolean fallback
  profile in `TypedGraphical.Validation.cs` stays bound to CPU 1511 V4.1.

## Cross-scope calls — 27 October 2026

- The builder accepts calls to another namespace of the caller's unit through the route's
  `namespaceQualified` call profile, and calls into another software unit only with a direct
  `SoftwareUnit` relation, a `Published` callee and a separate `crossUnitQualified` profile.
  Callee scope, access, namespace and interface are in the preview and rechecked at apply.
  Offline tests only.
- [x] Capture `route-capture-crossscope-call-packet.v1.json`: both parts passed on 28 October
  (`artifacts/cap-crossns`, `artifacts/cap-crossunit` from the prepared archive
  `artifacts/crossunit-prep/838e0c54….zap21`). The cross-namespace callers name the callee
  absolutely, as the deployed namespace-route call profiles emit. The extractor binds the
  cross-unit slot; `v21.fw41.softwareUnit.{lad,fbd}.call.crossUnit.v1` are audited
  (`crossscope-candidates-20261028.audit.json`) and promoted, every qualification flag false.
- [ ] Run the authored cases `fixtures/typed-graphical/crossscope-cases.v1.json` (cross-namespace
  and cross-unit, LAD and FBD; engineering only) to qualify the builder for both calls; the
  command is in the packet's `afterCapture`.

## Lane A — 25 October 2026

- Software-unit baseline rebuilt from a fresh UnitSeeder seed; all 24 route cases passed all four
  engineering gates (`evidence/local.typed.routes.engineering.20261025.json`).
- The three objects the lost baseline held implicitly (`TgpNamespace_TemplateFB_LAD/FBD`,
  `TgpUnit_GlobalTimer`) are explicit inputs in `route-capture-prerequisites.v1.json`. One capture of
  those plus all 38 packet inputs imported, compiled and saved every input (48 blocks), preserved all
  existing XML, matched a fresh-process reopen and cleaned up both owned lifetimes
  (`evidence/local.typed.route-capture.20261025.json`). It is native reference evidence only.
- Native V21 boundaries: duplicate PLC tag names and unqualified nested-namespace UDT references are
  refused by TIA itself (`evidence/local.typed.native-boundaries.20261025.json`).
- Existing-caller insertion is qualified under the 25 October scope decision (section 5).
- Scoped operand/storage work is complete except runtime for global DB member operands (the harness
  drives only the DUT interface) and cross-unit DB owners (no unit-relation profile).

## Lane A — 26 October 2026

- The 46-input IEC packet (`route-capture-iec-packet.v1.json`) was captured in one owned TIA lifetime
  from the SHA-bound baseline: every input compiled clean, saved and added exactly its blocks, and the
  saved state matched a fresh-process reopen (`evidence/local.typed.route-iec-capture.20261026.json`).
  An earlier attempt stopped after 37 inputs when TIA Portal terminated during an export; a rerun of
  the failing input passed, so it was not input-specific.
- 20 TOF/TP/CTU/CTD/CTUD candidates for both routes passed the binding audit
  (`route-candidates-iec-20261026.audit.json`). The audit now ignores a re-exported block's own
  `Number` only when both exports declare `AutoNumber` true: capture order shifts TIA-allocated DB
  numbers. 13 re-exported prerequisites keep lineage files under `native/routes-20261026/`.
- All 40 enabled cases (local multi-instance and existing global storage, LAD and FBD) passed the
  four engineering gates ([ledger](../verification/typed-lad-fbd-route-iec-20261026.md),
  `evidence/local.typed.route-iec.20261026.json`). With the 25 October cases, all 80 reference route
  cells are engineering-qualified; `route-pending-references.v1.json` is empty.
- 48 created-storage route cases (TON/TOF/TP/CTU/CTD/CTUD, created multi-instance member or created
  namespace-qualified global DB, both routes, LAD and FBD) passed the four engineering gates
  ([ledger](../verification/typed-lad-fbd-route-created-storage-20261026.md),
  `evidence/local.typed.route-created-storage.20261026.json`).

## Lane A — 24 October 2026

The raw artifacts behind the 8–13 October evidence (`artifacts/tq*`, `p16t`, `p17t`,
the software-unit baseline archive and the reference packet inputs) are no longer on
this PC. Their ledger rows stay historical; new evidence comes from current runners.

- TIA's GUI language here is now `en-US`, so new scratch projects lack `de-DE`. Captured
  creation/instance templates carried empty `de-DE` text items and failed to import.
  The worker now retargets empty-text template items to the project's editing language and
  refuses text in an inactive culture. The qualifiers adapt fixture references the same way
  and record it.
- The typed qualifier now admits the runtime provisioning tools and takes `--artifacts`.
- Canary `typed-boolean-lad` on the current build passed import/compile, complete XML,
  save/reopen and checkpoint recovery, and all 8 runtime assertions. Its final verdict is
  still `blocked`: the runtime-preservation guard requires byte-identical card and project
  files, but provisioning saves the project and RUN writes retain data to the card. The
  rule for that is open with the integrator.
- Existing-caller insertion: export object `ID`s are document-local and sequential, so any
  inserted network renumbers the later block-title IDs. Strict preservation of those IDs is
  unattainable by construction; relaxing it is a user decision.

## Resumed continuation — 13 October 2026

The independent c02 audit passed its two prerequisite and eight TON-route inputs,
complete peer preservation, saved/fresh reopen and both exact owned cleanup
lifetimes. The independent c01 prefix audit passed eleven completed inputs; a
separate copied-project read-only finalizer and independent audit then proved
saved-prefix persistence for all 54 PLC objects while keeping all nine original
project/companion files unchanged. The original c01 outcome remains
`stopped_by_request`. Fresh c03 imported/compiled/saved all nineteen remaining
inputs and preserved its 55 final objects, but host cleanup failed before reopen.
That original failure remains `blocked`; a separate byte-verified copy-only f02
readback passed all 55 XML comparisons and exact new cleanup while preserving all
nine original files. The packet reconciliation now covers all 38 authored inputs
and 45 reference blocks across these separate evidence chains.

Twenty-two new comparison candidates plus two retained Unit Eq references cover
all 24 scoped comparison reference cells. Four TON and two auxiliary namespace
call candidates are also staged under artifacts. All remain inactive. The two
namespace template callees and Unit global timer DB exist in the native baseline;
their candidate authored-source lineage still needs completion. No builder/runtime
gate was inferred from capture. The local capture compile gate accepts successful
`Information` states with complete zero-error diagnostics. Cleanup diagnostics now
retain exact host identity, full observed stderr/EOF and failure tracebacks.
See [p17 evidence and exact remaining boundaries](../verification/typed-p17-qualification.md).

## Continuation — 9 October 2026

The complete nine-case native rejection run passed on build `b2a680e2694d441384ccb963fe5298d0`, including planned caller/global-DB collision and stale declaration, full unchanged-XML checks and deliberate-mutation restoration: report (`artifacts/continuation-20261009/typed-negative-native-02/result.json`). All **28 remaining standard engineering cells** passed all four gates on build `0129950970e64a82a1f2f9571b759fc2`: 12 storage-creation cells, four numeric FC/FB cells and 12 existing-global IEC cells. The batch (`artifacts/continuation-20261009/typed-standard-engineering-03/batch.json`) records 84 exact owned TIA process exits. The independent preservation audit (`artifacts/continuation-20261009/typed-final-preservation.json`) passed all 489 complete XML comparisons. Original path-limit failures and separate checkpoint recovery remain retained. The latest full regression and strict relocated package checks passed separately on build `cb6299c72baf44d4b05278d878a536a0`; engineering-only evidence is not runtime qualification. Existing VM proof is accepted. See [current delivery/evidence](../history/continuation-20261009.md).

Stopped at the user's requested checkpoint on 8 October 2026. **The full
LAD-010–LAD-070 workstream is not complete.** Implementation spans all seven
packages; the remaining acceptance work below must not be relabeled as passed.

## Implemented at this checkpoint

Schema v1 has separate LAD power flow and FBD dataflow, a partial native reader
with opaque XML preservation, exact scalar operand/callee/instance resolution,
deterministic native IDs, and hash-bound native instruction profiles. Implemented
families are contacts, ordinary coils, series/parallel branches, Boolean gates,
six `Int` comparisons, scalar FC/FB calls, `Time` TON/TOF/TP and `Int` CTU/CTD/CTUD.
Existing global/multi instances and explicit global DB/multi-instance creation
use captured native templates. Supported replacement, bounded edits and FC/FB
creation use the existing prepare/apply/status/restore workflow, including
declaration/dependency effects. Read-only network data is exposed through
`engineering_read` with `selectors.scope=graphical`.

Unsupported instruction, type, target and route combinations return explicit
diagnostics. Namespace calls require independently captured qualified-name
encoding and matching caller/callee namespaces. Unit tag operands and namespaced
DB operands remain explicitly unsupported pending native reference evidence.

## Exact validation status

The [qualification ledger](../verification/typed-lad-fbd-qualification.md), updated 9 October,
contains **92 completed attempts over 76 distinct cells**. **73 cells passed all four native gates**:
import/compilation, complete expected XML and peer preservation, save/reopen,
checkpoint restoration and recovered-project reopen. Six cells passed all five
gates, including independent simulator assertions: LAD `ne`, `lt`, `gt`, TOF,
TP and CTU. These results apply to each report's recorded binary/profile/program
lineage; they do not automatically qualify a newer build.

Additional checkpoint checks:

- `artifacts/typed-checkpoint-full-tests.log`: **118/118** C# tests passed;
  no TIA API calls were made. The earlier load-sensitive bridge timeout did not
  recur after the native batches stopped.
- `artifacts/typed-checkpoint-python-tests.log`: **28/28** typed Python tests
  passed, including the configured-transport adapter tests.
- `artifacts/typed-checkpoint-contracts.log`: contract validation passed for
  **74 MCP tools, 52 mutation kinds and 144 feature entries**.
- `artifacts/typed-checkpoint-worker-build.log` and
  `typed-checkpoint-tests-build.log`: both builds passed with zero errors/warnings.
- `scripts/Test-Deployment.ps1`: deployment guards passed; the verifier requires
  all four actual typed schema files (including plural
  `typed-graphical-profiles.schema.json`), the profile catalog and instance-effects
  manifest. Nine guest bootstrap tests also passed. These are packaging checks,
  not guest/native qualification.
- Native negative tests observed five correct rejections with complete original
  XML unchanged. The remaining negative cases were not completed.

The machine-readable ledger is
`fixtures/typed-graphical/qualification.v1.json`. It preserves report hashes,
target/profile IDs, operation, binary hashes, diagnostics and assertion counts.
Source-reference import/compile evidence and pure tests are deliberately separate.

## Safe stopping point

The final completed local runtime case was `typed-ctu-lad` at 13:54:37 UTC;
all five gates passed. The eager runner began the next owned scratch setup before
the stop was delivered. `typed-ctd-lad` issued no `change_prepare` or `change_apply`;
its scratch setup outcome is unconfirmed and is **not** a qualified case.
`artifacts/tq05/user-stop-checkpoint.json` records retained files and verified exit
of that exact owned Portal PID 60904 and its qualifier/worker.

The storage batch stopped after FBD global TON completed all four native gates;
the next case had only begun baseline retrieval. Its owned process exit is
recorded in `artifacts/tqi05/checkpoint-stop.json`. Scoped creation finished all
eight selected cases and detached every connection with host exit 0; that API
does not itself certify native Portal process exit. No further native batches
were started after the checkpoint request. No unrelated project or physical
controller was modified.

## TODO, in continuation order

### 1. Produce and qualify a final immutable build

- [x] Re-run `scripts/New-IsolatedBuild.ps1`, then `scripts/Package.ps1` against
  its exact completed snapshot. Two attempts were correctly rejected by the
  compare-and-swap guard because concurrent `HmiInspectionRules.cs` edits changed
  the source snapshot: `artifacts/typed-isolated-build-01.log` and `-02.log`.
  Do not bypass this guard or package an incomplete snapshot.
- [x] Include the last integration fixes: reserved planned caller/global-DB name
  collision, nested UDT namespace resolution, scoped call encoding, reader
  boundary checks, native FBD ordering and software-unit AutoNumber behavior.
  `artifacts/typed-checkpoint-build` is a successful development build, **not** an
  immutable packaged release and has no matching final native qualification.
- [x] Verify the complete bundle and a relocated copy; explicitly select packaged
  binaries and confirm all profile/source/schema hashes resolve. Retain concurrent
  HMI, explorer, library and simulator work. Hash consistency is not signing.

### 2. Finish standard engineering, resolver and storage acceptance

- [x] Finish the **12 pending storage-creation cells**, the **four numeric FC/FB
  parameter-flow cells**, and **12 existing-global IEC cells**. Exact IDs,
  corrected fixtures, owned baseline archive SHA and commands are in
  [remaining tests](../history/typed-lad-fbd-remaining-tests.md).
- [x] Run all eight corrected negative cases and the ninth planned caller/DB-name
  collision case on a binary containing that guard. Earlier FBD request fixtures
  used native `TRUE` instead of typed JSON `true`; the authored requests are fixed.
  Valid language-conversion rejection is `LanguageConversionNotSupported`.
- [x] Add actual MCP positive/negative resolution cases for exact tag-table and
  DB/UDT ownership, duplicate names and nested UDT namespace selection. Pure or
  source review alone does not qualify these native paths. The executable fixture
  harness is implemented. All seven standard cases in both languages passed
  their positive engineering or negative rejection/preservation gates on the p13
  build. LAD/FBD stale-address rejection/recovery and LAD duplicate-tag setup rejection
  passed on p14. All six selected fully qualified namespace/UDT case/language
  combinations passed in separate p15 runs, with complete native preservation,
  positive reopen/recovery and independent audits. LAD/FBD native DB ambiguity
  also passed exact addition-only preservation, fresh `AmbiguousOperand`, stale
  `StaleRevision`, deliberate-change recovery and fresh reopen. Native
  duplicate-tag ambiguity, unqualified nested namespace propagation and runtime
  remain pending.
  Closed 26 October: `local.typed.resolution.20261025` passes the standard, namespace and stale phases in
  both languages, and duplicate tags and unqualified nested namespaces are native V21 refusals
  (`local.typed.native-boundaries.20261025`). Resolution fixtures carry no behavior; typed runtime cells cover it.
  [Earlier evidence](../verification/parallel-third-20261012.md#typed-resolution).
- [x] Exercise a typed `createMulti` change when a direct caller InstanceDB already
  exists, including a preserved nondefault start value. Both LAD/FBD p15 runs
  passed apply/compile, complete preservation, changed fresh reopen and checkpoint
  recovery/reopen. Independent audits verified 42 XML exports and three exact
  owned TIA lifetimes per language. The DB value 42 remains distinct from the
  caller declaration default 17. Runtime independent-instance behavior remains
  a separate gate. Evidence: `artifacts/p15/typed-multi-LAD-01/result.json`,
  `artifacts/p15/typed-multi-FBD-01/result.json` and their independent audits.
- [ ] Keep unprofiled transitive parent multi-instance layout changes and custom
  declaration shapes rejected until their effects are captured and qualified.
  Direct FB and IEC instance-DB effects are captured for the software-unit and namespace routes
  (`local.typed.scoped-capture.20261026`, `local.typed.iec-effect-capture.20261026`). Standard-route transitive
  parents are captured (`local.typed.transitive-capture.20261026`: every embedded multi-instance of the changed FB,
  at depth 1 and 2, in parent FBs and their DBs, gains the member in the direct DB shape without its top-level
  attributes) and the worker follows it; all 14 created multi-instance cells with a two-level parent chain pass the
  four engineering gates (`local.typed.transitive-effects.20261026`). Software-unit/namespace transitive parents
  in the caller's namespace are captured the same way (`local.typed.route-transitive-capture.20261027`: the
  rule holds with namespace-qualified type names) and all 28 route cells pass the four engineering gates
  (`local.typed.route-transitive-effects.20261027`). Parents in another namespace or unit and
  custom-start/structured shapes still have no captured effect and stay rejected.

### 3. Complete runtime assertions separately

- [x] Continue the 32 standard cases, retrying failed prerequisites in fresh owned
  instances. Six LAD cases are already runtime-qualified on their recorded builds;
  all FBD runtime cases, Boolean logic, FC/FB calls, TON and the remaining comparison/
  counter cells still need successful assertions on the intended final lineage.
- [x] Run numeric FC input/output/in-out/return checks and two-instance FB state
  independence. Engineering-only results do not prove parameter behavior.
- [x] Run the two `counter-limit-cases.v1.json` suites. Their independently authored
  bounded SCL drivers pulse the actual typed CTU FB at most 512 times per harness
  call, testing 32767 saturation, the 32768th attempted edge and reset through the
  existing executor. The vectors are authored; **no saturation assertion ran**.
  Closed: `local.typed.runtime.20261024` (34 standard cells) and `local.typed.runtime.reruns.20261024`
  (numeric FB two-instance independence, CTU 32767 saturation, the 32768th edge and reset) pass all five
  gates in LAD and FBD; together all 38 standard-route cells pass.
- [x] Exercise CTD/CTUD signed limits and timer cancellation/restart/completion
  (`local.typed.runtime.20261024`). Host timing/application-call limitations stay explicit.
- [x] Run the standard-route created storage variants (`createMulti`/`createGlobal`) at runtime. The standard
  IEC dependent-instance effect pair is captured (`local.typed.iec-effect-standard.20261026`; all 12 standard
  timer/counter profile declarations match), and all 28 standard created-storage cells (FB multi-instance and
  the six IEC types, both storage kinds, LAD and FBD) pass all five gates
  (`local.typed.standard-created-storage-runtime.20261026`).
- [x] Diagnose prerequisite failures without weakening isolation: observed failures
  include another registered instance, protected-card filesystem access denial
  before credential access, and native power-on timeout. Owned cleanup passed in
  the completed runs. Later shared simulator code adds guarded filesystem retries;
  that newer binary still needs actual runtime evidence. Never stop an unrelated
  instance or replay uncertain credential/native operations.
  Closed: none recurred in the frozen 65-case corpus, the 38 standard cells or the 208 scoped live cases on
  current binaries. The single unexplained card-staging access denial stays tracked in `docs/TODO.md`.

### 4. Finish route profiles and qualification

- [x] Run the **12 authored advanced unit cases** for comparison, numeric FC/FB,
  existing global/multi storage and explicit new storage using the current qualified
  name encoding. See [remaining native work](../history/typed-lad-fbd-remaining-native.md).
  All 12 passed all four native engineering gates on frozen combined build
  `fa0067bcbd32499485b72d8ea1cef38a`: six LAD and six FBD, 794 native XML exports,
  636 independent strict comparisons and 36 exact owned TIA exits. The first
  LAD Eq pass on `p15/merged5` remains separate historical evidence.
  All scoped runtime remains pending. See
  [p16 evidence and strengthened runner](../verification/typed-p16-qualification.md).
- [x] Retain independent import/compile/save and persistence evidence for all
  **38 authored reference inputs** and 45 blocks. c01 stopped-prefix and c03
  saved-source evidence use separate copied-project readbacks; c03's original
  failed cleanup remains failed. See [p17 evidence](../verification/typed-p17-qualification.md).
- [x] Enable only independently accepted instruction/target combinations after
  candidate lineage and builder qualification. All 80 former pending route cells are
  activated after their binding audits and pass the four engineering gates; the
  pending-reference manifest is empty. Runtime remains unqualified.
- [x] Diagnose scoped IEC `Missing instance DB` errors with the independently
  authored native XML probes. SIMATIC SD import
  failure does not prove that the native software-unit target cannot use IEC
  timers/counters.
  The native XML probes import, compile and persist in both routes with local and existing
  global storage (`local.typed.route-iec-capture.20261026`), so the target supports scoped IEC
  timers/counters; the SIMATIC SD import failure itself was not pursued.
- [x] Capture scoped global operand encodings and scoped dependent-DB effects
  before enabling them. `local.typed.scoped-capture.20261026` captured the 13-input packet: both
  route instance-effect pairs (the DB keeps its full payload and appends only `Child`; profiles in
  `instance-effects-{softwareUnit,namespace}.v1.json`) and namespaced global DB member operands in LAD/FBD.
  Unit tag operands are a native V21 boundary: an M-memory unit tag caller compiles with "Accessing this
  type of tag is not possible in software units" (packet `nativeBoundaries`; %I/%Q unobserved).
- [x] Enable a builder operand profile for namespaced global DB members from the captured encodings.
  The builder emits the captured shape (qualified DB name, then the member), accepts only DB owners in the
  caller's software scope, and checks and revalidates uniqueness by qualified name. Four route cases pass
  the four engineering gates ([ledger](../verification/typed-lad-fbd-route-dboperand-20261026.md)).
- [x] Capture IEC timer/counter dependent-instance effect pairs and let the worker profile carry
  several member shapes. `local.typed.iec-effect-capture.20261026` captured one FB per route gaining a
  member of each IEC type; `instance-effects-iec-<route>.v1.json` maps each type to its captured pair and
  the worker appends the captured DB member only when the new declaration equals the captured caller
  member. All 24 created timer/counter route cells pass all five gates
  ([ledger](../verification/typed-lad-fbd-route-runtime-iec-createmulti-20261026.md)).
- [x] Scoped runtime: `qualify_typed_graphical.py --live --unit-seeder` seeds the unit into a fresh
  scratch (keeping credential provenance) and generates the harness inside the DUT's unit and
  namespace (named cycle OB, `NAMESPACE` source, qualified references and PLCSIM symbols). 112 of 140
  route cells pass all five gates, all 140 with the FB and IEC effect profiles
  ([ledger](../verification/typed-lad-fbd-route-runtime-20261026.md),
  reruns: [global numeric FB](../verification/typed-lad-fbd-route-runtime-reruns-20261026.md),
  [local numeric FB](../verification/typed-lad-fbd-route-runtime-local-fb-20261026.md)). The 28 created
  multi-instance cells are refused (`DependencyProfileUnavailable`) until a route effect pair exists.
  The seeder must be the Openness-allowlisted lane A binary; a rebuilt seeder needs a new allowlist entry.
- [x] Retain failed-source recovery evidence: some incomplete native exports leave
  `afterFingerprint` unknown, so `change_restore` correctly refuses with
  `StaleRevision`. Exact checkpoint retrieval into a new owned project was verified
  separately; it is **not** a successful `change_restore` qualification. Do not
  weaken the stale guard or merge these different recovery claims.
  Retained 26 October (`local.typed.failed-source-recovery.20261026`): a compile-failed scoped source left
  `recovery_required` with an unknown after-fingerprint and `change_restore` refused (`FingerprintUnavailable`);
  `qualify_failed_source_recovery.py` retrieved its SHA-bound checkpoint into a fresh owned project and all 21
  blocks/types/tag tables equal the pre-change snapshot. This stays separate from `change_restore`.

### 5. Existing-caller insertion

- [x] User scope decision, 25 October 2026 ([decision](insertion-id-renumbering-decision-20261025.md)):
  the document-local IDs V21 renumbers after an inserted network count as preserved,
  provided they rise by exactly the inserted ID count and everything else stays exact.
- [x] The builder predicts that renumbering (the inserted unit takes the IDs of the
  objects after it; those shift by its count) and still refuses documents whose IDs
  are not consecutive. The qualifier accepts only that exact shift.
- [x] Native acceptance in both languages: `typed-insert-lad` and `typed-insert-fbd`
  passed all four engineering gates ([evidence](../verification/typed-lad-fbd-insertion-20261025.md),
  `evidence/local.typed.insertion.20261025.json`).
- [x] First-network insertion into a network-free caller. Native exports always place compile units
  before the block title (174/174), so the builder inserts the first network there with a captured native
  envelope and shifts the title IDs; both languages pass the four engineering gates
  ([ledger](../verification/typed-lad-fbd-insertion-first-20261026.md), `local.typed.insertion-first.20261026`).
- [x] Insertion combined with storage creation needs its own composite effect contract. The
  `network-plus-storage-creation-v1` contract admits only the declared created member (new, shaped exactly like its
  native template) next to the inserted network; standard-route TON `createMulti`/`createGlobal` insertion passes the
  four engineering gates in LAD and FBD (`local.typed.insertion-storage.20261026`). Runtime is not claimed.

### 6. Complete remote deployment and release reporting

- [ ] Restore availability of the exact existing guest before testing. The strict
  configured SSH endpoint timed out; `vmrun list` reported **zero running VMs**.
  No guest, account, firewall or VM state was changed. Evidence and the actual
  configuration-based command packet are in
  [model/remote handoff](../history/typed-lad-fbd-remaining-model.md).
- [ ] Deploy a final verified bundle, generate its external strict SSH/InteractiveUser
  configuration, and run both languages through the configured MCP transport and
  native gates. The adapter verifies remote artifact chunks and complete hashes;
  local transport tests do not qualify the guest. Remote runtime remains unsupported
  by that qualification adapter and requires a separately bounded route.
- [x] Refresh the per-case ledger, feature evidence and documentation from completed
  reports. Keep all five gates separate and retain superseded/blocked attempts.
  Do not set broad profile or workstream qualification flags from partial results.
- [x] Add a cooperative stop-between-cases option to the typed batch runner.
  `--stop-file` prevents admission after active-case cleanup; blocked or uncertain
  cases also halt admission. Exact remaining fixture IDs and immutable reports
  are retained. Offline admission/lifetime tests passed; runtime-case stopping
  remains a separate unqualified gate.

## Changed files and boundaries

The implementation is concentrated in `TypedGraphical*.cs`,
`OpennessSession.TypedGraphical.cs`, `OpennessSession.TypedInstances.cs` and
`OpennessSession.TypedDependencyProfiles.cs`. Shared integration changes are
additive in `OpennessSession.Changes.cs`, `Engineering.cs`, `Capabilities.cs`,
`EngineeringSources.cs`, host `EngineeringTools.cs`, the feature catalog, test
registration/project files, deployment checks and existing documentation.

Four schemas, independent source/native fixtures, profile/template manifests,
behavior vectors, qualifier/capture/transport scripts and typed test suites are
under `contracts/`, `fixtures/typed-graphical/`, `scripts/` and `tests/`.
`artifacts/typed-checkpoint-files.json` records their checkpoint hashes and the
shared-file boundary. This is a shared workspace without Git metadata; do not
attribute or overwrite unrelated concurrent edits.

Detailed continuation records:
[tests](../history/typed-lad-fbd-remaining-tests.md),
[native routes](../history/typed-lad-fbd-remaining-native.md),
[model and remote adapter](../history/typed-lad-fbd-remaining-model.md).
