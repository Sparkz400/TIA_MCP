# Fourth parallel increment — 12 October 2026

Standalone component installation, typed fixture coverage and independent review
ran in isolated source trees. The coordinator serialized TIA/PLCSIM operations.
All earlier reports and frozen builds retain their original bytes and outcomes.
The installed MCP package has not been updated.

## Component installation

The internal recipe/coordinator supports `caller.mode=createStandaloneFc` for
standard LAD/FBD, with an initially absent FC and a new global instance DB. It
rechecks absence before preparation, pins exact FB/UDT versions, generates typed
bindings, and retains the first library-import checkpoint. It adds no OB call.
The existing-caller native-ID preservation guard and production combined-apply
gate remain unchanged.

The first combined-worker startup timed out at Siemens Openness authorization,
before project creation. Its original owned TIA exit was not captured and remains
unverified. A separate startup-only check subsequently passed with exact owned
cleanup; this does not change the earlier result.

The next component attempt completed exact library import, compile, save and the
first checkpoint, then stopped at `ComponentReadbackMismatch` before typed FC/DB
preparation. Independent review found an identical UDT and exactly three added FB
settings in the installed library-version export: `AutoNumber=true`,
`IsIECCheckEnabled=false`, and `MemoryLayout=Optimized`. Both owned TIA lifetimes
and their hosts/workers exited cleanly. No complete post-import preservation or
native-link acceptance is claimed for this failed attempt.

The correction explicitly authors those properties in the input fixture and
requires the committed source export to retain them before consumer dispatch.
At that stage the generic strict byte comparison was unchanged. Returned readback evidence is
retained before validation so a rejected result remains diagnosable.

The third attempt confirmed the explicit input reached the authoring project:
its project-library export contains all three settings and matches the second
attempt's installed FB bytes. The published global-library export still omits
all three and matches the second attempt's source bytes. The new preconsumer
source gate rejected this before any component coordinator/import/typed dispatch.
Both owned TIA/host/worker lifetimes closed cleanly. Both comparison exports
already use `ExportOptions.WithDefaults`; changing to `ExportAsDocuments` does
not expose a `WithDefaults` option. The remaining issue requires a defined,
native-proved content contract or independent source-property oracle. Missing
attributes do not establish generic default values, and neither attempt added normalization.
The observed boundary includes publication, save/archive/retrieve and global
export; these results do not isolate which of those transitions causes omission.

A subsequent isolated read-only probe opened fresh copies of the failed second
attempt's consumer project and exact global library. All 16 exports succeeded:
FB and UDT, source and installed, under each of the four combinations of
`WithDefaults` and `WithReadOnly`, always with `DocumentInfoOptions.None`.
Raw FB bytes differed in every mode. The combined-flags source export explicitly
contains the three exact setting values with `ReadOnly="true"`; the installed
export contains the same values without that marker. The remaining differences
are the exact library-context `OriginalLibrary` field and direct FB creation
date. The UDT base exports remain byte-identical. The independent audit checked
all exports, 38 unchanged original-input hashes, 15 copied-file receipts and
the exact owned TIA/helper cleanup. This supplies observed source-property
evidence; it does not itself qualify installation or production apply.

A new isolated implementation defines that narrowly scoped content contract.
It pins both raw source exports, retains both installed exports, rejects changed
logic/interfaces/IDs/unknown XML, and accepts only the three proven settings plus
the exact observed context/provenance paths. Native getter ticks and DateTime.Kind
are retained without timezone or chronology inference. The full source oracle is
also rechecked inside the existing exclusive-access fingerprint before claim,
checkpoint or import. An independent source audit approved all 19 changed files;
190 C# and 38 Python focused checks passed. This increment has not yet passed a
fresh complete native installation sequence.

The fourth native attempt passed exact import, compile (zero errors and one
display-password warning), save, the new import content contract, and its
immediate pre-typed recheck. Independent review compared both complete base/full
export pairs; the UDT remained byte-identical. It then stopped with
`InstanceCreationProfileUnavailable`: the FB's persistent `State` UDT expands
to a nested `Active:Bool` member, which the existing instance-template guard
does not cover. No typed preparation receipt, FC/DB creation or typed apply
occurred. The component remains `recovery_required`; both exact owned
TIA/host/worker lifetimes closed cleanly. Save completed, but installed fresh
reopen and full installation preservation were not reached.

A separate recovery-only run retrieved that exact first checkpoint once into
a distinct owned project. Independent review checked 21 full PLC exports across
baseline, recovery and fresh reopen, confirmed the component types were absent,
and rehashed all 27 retained input artifacts without changes. Both exact owned
lifetimes closed cleanly. The failed installation remains failed.

A separate native capture independently authored an instance DB through SCL in
a fresh copy of the fourth attempt's consumer project. Creation, compile, save,
full peer preservation and fresh reopen passed. Its own before-DB checkpoint was
retrieved once into another fresh project; full recovery and recovered fresh
reopen passed. The five snapshots retain 47 XML exports, and all four exact owned
TIA/host/worker lifetimes closed cleanly. Independent audit checked all 47 raw
exports, five phases of exact native FB/UDT links and dependency closure, and
140 unchanged inputs plus nine unchanged original project companions. The peer
comparisons retain exact bytes after removing only export provenance. This establishes an observed DB
shape only: omitted start/default values remain unobserved. The generic storage
guard is unchanged. The subsequent source increment implements a qualification-only
exact-identity FBD profile and source-archive reuse harness; a complete fresh-consumer
installation sequence remains to be qualified.

A separate recovery-only run retrieved the failed second attempt's exact first
checkpoint into a new owned project and passed complete baseline comparison and
fresh reopen. Independent review compared 21 exports across the three seven-object
snapshots, confirmed the component library types were absent, and verified one
retrieval with no apply/replay. All 27 input hashes, including the original failed
report, journal and archive, were unchanged. Both owned TIA/host/worker lifetimes
closed cleanly. This qualifies recovery of that checkpoint; the installation
attempt remains failed.

## Typed engineering

FBD stale-address rejection, address-only preservation, full peer preservation,
checkpoint recovery and recovered fresh reopen passed on the frozen p14 build.
The independent audit checked 45 XML exports across five snapshots and both
owned TIA lifetimes. One typed apply returned `StaleRevision`; only the deliberate
address change was restored. Runtime remains unqualified.

The new `createMulti` fixture independently authors a caller marker default of
17 and an existing direct instance DB start value of 42. Its append oracle uses
the observed existing same-type child and preserves all prior DB XML, metadata,
caller content outside the selected network and peer objects. The competing-DB
fixture requires exactly the deliberately added namespace-qualified DB and its
authored values. These are scoped engineering fixtures, not runtime proof.

Both LAD/FBD direct-instance runs passed on the first p15 combined build. Per
language, independent review checked 42 XML exports across six snapshots,
independently reconstructed the call/instance/wiring, verified one typed apply
and one checkpoint restore, compared both fresh reopens and confirmed three exact
owned TIA/host/worker cleanup lifetimes. The existing DB start value remained 42
while the FB declaration default remained 17.

The first LAD namespace attempt stopped during fixture setup with
`BlockNumberCollision`: the software-unit seeder had occupied the template FC's
number. No namespace test case ran. The original report remains blocked.
The corrected numbering fixture omits only the new template's historical Number,
retaining AutoNumber, native IDs and collision guards. A second fresh attempt
then stopped during Alternate UDT source generation: Siemens could not resolve
the bare Inner type. Checkpoint retrieval returned restored and exact cleanup
passed; full payload/reopen recovery was not reached. Neither attempt qualifies
namespace resolution.

The third LAD attempt, using the final harness with the corrected combined
build, passed the fully qualified nested-UDT positive case: apply/compile,
unchanged caller/peer content, changed fresh reopen and checkpoint recovery/reopen.
The source explicitly selects `TiaMcp_UnitA.Alternate.Tgp_ResolutionInner`, with
independent exact scope/namespace checks and a parent-namespace decoy. Unqualified
nested namespace propagation remains native-unqualified.

All three FBD namespace cases also passed on that corrected combined build.
Independent review compared 170 complete XML exports: 102 for the positive
sequence and 34 per negative case. The wrong-type and missing-member cases
returned exactly `OperandTypeMismatch` and `OperandNotFound`, respectively, with
all 17 fixture objects unchanged. The positive case's prepared/native content,
FBD wiring, fresh reopens and one restore matched; all three owned TIA lifetimes
closed cleanly. The LAD positive audit separately verified 102 XML exports and
three exact owned cleanup lifetimes.

The two remaining LAD negative cases passed in a separate fresh run. Independent
review checked 68 exports across four snapshots, exact wrong-type/missing-member
prepare rejections, unchanged 17-object snapshots and one exact owned cleanup
lifetime. Together the retained runs cover all six selected namespace
case/language combinations; they are not a runtime corpus or unqualified-name proof.

The LAD native DB-ambiguity case passed. The deliberate setup added exactly one
UnitB DB with `Flag=false` and marker 91, preserving all 13 baseline XML objects.
Fresh typed preparation returned `AmbiguousOperand`; the old prepared change
returned `StaleRevision` once and retained an unclaimed, prepared journal. Only
the deliberate DB addition was restored. Independent review checked 81 XML
exports across six snapshots, complete recovered fresh reopen and both exact
owned cleanup lifetimes. This is native DB ambiguity/stale-rejection evidence,
not a successful typed apply or native duplicate-tag ambiguity proof. The FBD
counterpart independently passed the same checks: 81 XML exports across six
snapshots, one fresh ambiguity rejection, one stale apply rejection, one
deliberate-change restore, full recovered fresh reopen and two exact owned
cleanup lifetimes.

## PLCSIM discovery

The historical row has a zero-filled **Address**; the original report did not
capture its MAC. Its identity and origin remain unresolved.

A fresh controlled offline-download experiment sampled before compile, after
compile, after native download while holding the exact interface, and after
interface disposal. All 111 complete observations reported the PLCSIM route
absent. The 85 newly generated public card files retained matching hashes, and
complete PLC snapshots were unchanged; two owned TIA, four worker and two host
lifetimes closed cleanly.
The experimental instrumentation is isolated and excluded from product source.
No provisioning or CPU startup was requested in this experiment.

A separate empty-CPU lifecycle experiment completed one power-on to STOP,
power-off and exact release. Of 116 complete observations, 17 STOP observations
showed the normal endpoint `192.168.0.1` / MAC `FF-FF-C0-A8-00-01`; the other 99
reported the route absent. No zero address appeared. The final timely query
started 64.660 seconds after release, exceeding the historical 59.64-second gap.
Exact registry/state/storage guards bracketed every sample. The owned TIA,
observer, host and simulator worker exited cleanly, and the source project,
120 inputs and 14 binaries were unchanged. No program download, RUN or credential
request occurred in this experiment.

These results demonstrate that the observer can see the normal live route and
did not reproduce the historical row under these tested conditions. They do not identify
the historical row or qualify runtime stopping/corpus admission.

A subsequent single protected-program diagnostic exercised the exact historical
`fresh-retained-state` case in a fresh owned CPU, retaining the production
isolation/credential path and an additional fresh two-empty OFF gate. The one
application call and its two assertions passed. There was one outer
create/download/provision/powerOn/RUN/OFF/release sequence; provisioning retains
its own existing internal empty-CPU lifecycle. All three exact TIA lifetimes,
the observer and four workers exited cleanly, and the simulator registry was empty.
The 64 complete observations contained two normal endpoints (STOP and RUN) and
62 route-absent results; no zero address appeared. Three timely post-release
queries began at 60.802, 62.183 and 63.598 seconds. The last query began at 64.978
seconds but completed late and does not establish timely coverage.

That diagnostic remains **failed**. Its final post-RUN comparison reused the
strict pre-start memory-card equality check: 82 of the 85 original files still
matched, one consistency file changed, two original files were absent and ten
additional runtime paths existed. Only names and lengths of those added files
were inspected. All nine original project companions retained their hashes,
but one new 256-byte `.info` file changed the project inventory; its contents
were not inspected. The final full PLC snapshot was not reached. The retained discovery observations and successful
selected case do not establish complete preservation, requested-stop acceptance,
historical identity or corpus qualification. The original failed report is
unchanged; no repeat or automatic replay was performed.

## Evidence and remaining gates

The corrected combined Release build passed with zero warnings/errors. Full
regression reported 458 Python tests across 34 suites (457 passed, one existing
skip), 132 planner/coordinator/gate checks, five actual worker routing checks,
144 core checks and 27 simulator-helper checks. Siemens APIs were not initialized
by simulator self-tests. The final seven-file fixture/harness/document overlay
changes no product source and passed all affected suites: 35 component Python
and 61 typed Python checks. The full regression's exact source lineage remains
recorded separately; the overlay is not represented as a repeated full run.

Final source validation binds 878 source files and the 14 unchanged combined-build
binaries. Twenty-eight reviewed source files were promoted after baseline hash
checks; diagnostic runtime instrumentation was excluded. Source promotion does
not update the installed package or qualify the remaining native/runtime gates.

The later oracle-contract source freeze (`merged3`) binds 885 source files and
14 newly built product binaries. Its full Release build passed with zero warnings
and errors, and its separate complete regression passed: 34 Python suites reported
464 tests (463 passed, one existing skip), 190 component checks, 144 core checks,
five routing checks and 27 simulator checks with Siemens APIs uninitialized.
Independent review rehashed all source, binaries and logs. A preceding regression
launch failed on its log destination; that incomplete invocation is retained and
is not represented as a test pass. The successful run used a new temporary
directory and an absolute log destination.
After independent source and native content-contract audits, the 19 reviewed
oracle-contract files were promoted using canonical baseline hash checks and a
new receipt. The installed package and production combined-apply gate remain
unchanged.

The later storage/reuse source freeze initially combined an FBD recipe with a
LAD-only profile. Its full build passed, but independent prelaunch review rejected
that mismatch before full regression or any native run. That immutable `merged4`
candidate remains retained. The corrected `merged5` profile explicitly selects
the unchanged FBD recipe and existing captured FBD FC creation template, with a
new cross-lane regression covering their agreement. It changes no archived
version identities or captured DB XML and enables no generic nested-storage route.

The corrected freeze binds 907 source files and 14 product binaries. Its full
Release build passed with zero warnings/errors, followed by a complete regression:
35 Python suites reported 488 tests (487 passed and one existing skip), with 24
new source-reuse tests included. C# checks include 24 captured-storage, eight
routing, 190 component/coordinator, 144 core and 27 simulator self-tests; the
simulator API remained uninitialized. Independent review verified all source,
binary and log hashes and the exact FBD recipe/profile agreement.

After baseline hash checks, 27 reviewed files were integrated into the current
project source. Two unreferenced top-level verification receipts remain only in
artifacts, explicitly recorded as exclusions; all runtime fixture provenance is
included. The source contains the exact FBD-only owned-scratch storage profile
and source-reuse harness. Full native installation and runtime qualification remain
pending, production combined apply remains disabled, and the installed MCP package
is unchanged. Source integration is separate from native acceptance.

- `artifacts/p15/combined-validation.json`
- `artifacts/p15/combined2-validation.json`
- `artifacts/p15/final-source-validation.json`
- `artifacts/p15/integration-manifest.json`
- `artifacts/p15/merged3-freeze.json`
- `artifacts/p15/combined3-validation.json`
- `artifacts/p15/component-oracle/delivery-manifest.json`
- `artifacts/p15/integration-oracle-manifest.json`
- `artifacts/p15/merged4-prelaunch-result.json`
- `artifacts/p15/merged5-freeze.json`
- `artifacts/p15/combined5-validation.json`
- `artifacts/p15/integration-storage-manifest.json`
- `artifacts/p15/component-native-01/result.json`
- `artifacts/p15/combined-startup-01.json`
- `artifacts/p15/component-native-02/result.json`
- `artifacts/p15/component-native-03/result.json`
- `artifacts/p15/component-native-04/result.json`
- `artifacts/p15/component-recovery-02/result.json`
- `artifacts/p15/component-recovery-04/result.json`
- `artifacts/p15/component-storage-capture2/storage-delivery-manifest.json`
- `artifacts/p15/sc01/result.json`
- `artifacts/p15/xp01/result.json`
- `artifacts/p15/component-export-analysis-xp01/analysis.json`
- `artifacts/p15/typed-stale-FBD-01/result.json`
- `artifacts/p15/typed-namespace-LAD-01/result.json`
- `artifacts/p15/typed-namespace-LAD-02/result.json`
- `artifacts/p15/typed-namespace-LAD-03/result.json`
- `artifacts/p15/typed-namespace-FBD-01/result.json`
- `artifacts/p15/typed-namespace-LAD-negative-01/result.json`
- `artifacts/p15/typed-ambiguity-LAD-01/result.json`
- `artifacts/p15/typed-ambiguity-FBD-01/result.json`
- `artifacts/p15/typed-multi-LAD-01/result.json`
- `artifacts/p15/typed-multi-FBD-01/result.json`
- `artifacts/p15/offline-download-01/report.json`
- `artifacts/p15/empty-lifecycle-01/report.json`
- `artifacts/p15/programmed-runtime-01/report.json`
- `artifacts/p15/programmed-runtime-card-diff.json`
- `artifacts/p15/audit/` (independent source, preservation, recovery and resource audits)

The historical runtime attempt remains **48/65 passing cases**. One frozen
65-case / 358-step / 478-assertion pass and its independent audit are still
required; successful separate subsets cannot replace it. Production combined
component apply remains disabled until the complete engineering and runtime
sequence is qualified. Existing VM evidence remains accepted and the prior HMI
visual stop remains in effect.
