# Typed LAD/FBD checkpoint and remaining work

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

See the [consolidated TODO](../TODO.md) for the current work order. This page retains detailed acceptance criteria and dated progress.

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
- [ ] Add actual MCP positive/negative resolution cases for exact tag-table and
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

### 3. Complete runtime assertions separately

- [ ] Continue the 32 standard cases, retrying failed prerequisites in fresh owned
  instances. Six LAD cases are already runtime-qualified on their recorded builds;
  all FBD runtime cases, Boolean logic, FC/FB calls, TON and the remaining comparison/
  counter cells still need successful assertions on the intended final lineage.
- [ ] Run numeric FC input/output/in-out/return checks and two-instance FB state
  independence. Engineering-only results do not prove parameter behavior.
- [ ] Run the two `counter-limit-cases.v1.json` suites. Their independently authored
  bounded SCL drivers pulse the actual typed CTU FB at most 512 times per harness
  call, testing 32767 saturation, the 32768th attempted edge and reset through the
  existing executor. The vectors are authored; **no saturation assertion ran**.
- [ ] Exercise CTD/CTUD signed limits, timer cancellation/restart/completion and
  every advertised storage variant. Keep host timing/application-call limitations
  explicit; they are not exact PLC scan measurements.
- [ ] Diagnose prerequisite failures without weakening isolation: observed failures
  include another registered instance, protected-card filesystem access denial
  before credential access, and native power-on timeout. Owned cleanup passed in
  the completed runs. Later shared simulator code adds guarded filesystem retries;
  that newer binary still needs actual runtime evidence. Never stop an unrelated
  instance or replay uncertain credential/native operations.

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
- [ ] Enable only independently accepted instruction/target combinations after
  candidate lineage and builder qualification. The pending-reference
  manifest records **80 unqualified route cells**; it is not a support promise.
- [ ] Diagnose scoped IEC `Missing instance DB` errors with the independently
  authored native XML probes. They are not enabled profiles. SIMATIC SD import
  failure does not prove that the native software-unit target cannot use IEC
  timers/counters.
- [ ] Capture scoped global operand encodings and scoped dependent-DB effects
  before enabling them. No scoped runtime assertions have run.
- [ ] Retain failed-source recovery evidence: some incomplete native exports leave
  `afterFingerprint` unknown, so `change_restore` correctly refuses with
  `StaleRevision`. Exact checkpoint retrieval into a new owned project was verified
  separately; it is **not** a successful `change_restore` qualification. Do not
  weaken the stale guard or merge these different recovery claims.

### 5. Resolve the strict insertion acceptance constraint

- [ ] Find and independently qualify a native insertion shape that preserves
  unrelated document IDs in both languages. Current normal block envelopes cause
  TIA to renumber existing title IDs after insertion. Native compilation passed,
  full preservation failed, and the original was recovered.
- [ ] Keep `NativeIdPreservationUnsupported` for these envelopes. Terminal insertion
  without following object IDs is pure-supported but has no qualified native
  fixture. Do not remove comments/titles or normalize away ID differences. If native
  export makes the requested constraint impossible, leave this acceptance criterion
  explicitly unmet; changing that requirement needs a user decision.

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
- [ ] Refresh the per-case ledger, feature evidence and documentation from completed
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
