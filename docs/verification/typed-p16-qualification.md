# Typed LAD/FBD continuation — 12 October 2026

All **12 advanced software-unit cases** (six LAD and six FBD) passed import/compile,
complete native XML preservation, saved changed-project reopen and checkpoint
recovery/reopen on frozen combined build
`fa0067bcbd32499485b72d8ea1cef38a`. The exact Int equality, numeric FC/FB and
existing/created global/multi-instance cells are recorded in the
batch (`artifacts/p16t/advanced-final-01/batch.json`). An independent
strict audit (`artifacts/p16t/advanced-final-01-audit.json`) verified **794 XML
exports, 636 complete comparisons and 36 exact owned TIA process exits**, with
clean host/worker shutdown. Its SHA-256 is
`c5cf63d665773017dcffbaeeeb4b0de735380534b89b74be80882b73f0129b17`.
The frozen harness manifest (`artifacts/p16t/advanced-final-harness/manifest.json`)
retains the exact inputs separately from later diagnostic work. Runtime was not
requested for any of these cases.
A supplemental compile-diagnostics audit (`artifacts/p16t/advanced-final-01-compile-audit.json`)
checked all 28 distinct compile objects and 226 diagnostic messages across these
12 reports: explicit successful target states, integer zero error counts,
complete message arrays and no Error or unknown diagnostic states. This check
addresses the separately observed native contradiction of zero aggregate errors
with an Error message; aggregate counts alone are not treated as success.

The first `typed-unit-eq-lad` pass on `artifacts/p15/merged5` remains a separate
historical attempt. Its report (`artifacts/p16t/advanced-first/typed-unit-eq-lad/result.json`)
and independent audit (`artifacts/p16t/advanced-first-audit.json`) retain 60
native XML exports and 48 complete comparisons, including all type and tag-table
peers. Three exact owned TIA processes, hosts and workers exited cleanly.

Runtime parameter/state/timer/counter acceptance, additional route captures and
native insertion acceptance remain pending until their separate reports are
completed. The refreshed [qualification ledger](typed-lad-fbd-qualification.md)
retains all original 92 attempts and adds these 13 reports: **105 attempts, 88
distinct cells, 85 with all four engineering gates, six with all five gates**.
Historical runtime evidence is not qualification of the new frozen build.

## Qualification runner changes

Local typed qualification now uses the established `OwnedScratch` lifetime
contract: every changed/recovered reopen gets a fresh host, worker and owned TIA
handle. Only paths recorded by this run can reopen. A SHA-checked archive may be
retrieved into a fresh owned session; it is not treated as authorization to open
the original project. All baseline types and tag tables join the complete XML
peer comparison, alongside every block.

`--stop-file` selects a presence-only stop sentinel; the default is
`<output>/stop-request.json`. The active case and verified cleanup finish before
admission stops. A blocked case or cleanup failure also stops the batch.
`batch.json` records exact completed, active and remaining fixture IDs, while
`index.json` retains completed case reports. Existing output directories cannot
be reused. Remaining entries require a new explicit run, never automatic replay.
Apply intent is persisted before dispatch, and a dispatched restore cannot be
repeated after an uncertain response. New runs pin the selected case and harness
hashes and check them at cleanup.

These are runner safety/evidence changes. They do not qualify runtime stopping
or turn native prerequisites into passed assertions. The current typed Python
suite passes **95 tests**, including the new admission, lifetime, peer coverage,
strict insertion, runtime-receipt and diagnostic-fixture checks. The guarded diagnostic seeder
built separately under `artifacts/p16t/seeder` with zero warnings/errors.

Independent integration review found that the legacy ElementTree comparison
dropped real XML comments and processing instructions and lost namespace bindings
used only inside QName values. The typed snapshot hash, insertion contract,
untouched-caller comparison, address-only oracle and direct-instance append
oracle now use the preserving DOM/native-payload comparator. New regression cases
remove actual XML comments/PIs, change inherited QName bindings and alter
`xml:space="preserve"` text. The first Eq case was separately re-audited under
this stricter comparator: all 60 exports and 48 comparisons passed in the
strict audit (`artifacts/p16t/advanced-first-strict-audit.json`).
Its original report and earlier audit remain unchanged; future reports identify
`native-payload.v2-comments-pi-qnames` explicitly.

Read-only historical re-audits also passed without altering their original
reports: nondefault multi-instance (`artifacts/p16t/p15-multi-strict-audit.json`)
(84 exports), six selected namespace cases (`artifacts/p16t/p15-namespace-strict-audit.json`)
(340 exports, 238 comparisons), and
stale-address/DB-ambiguity cases (`artifacts/p16t/p14-p15-stale-ambiguity-strict-audit.json`)
(270 exports, 215 comparisons). These improve preservation evidence for their
recorded binary lineages; they do not add fresh native or runtime attempts.

## One guarded runtime diagnostic

The typed runner now finishes all three engineering lifetimes before admitting
one independently authored live case in a fourth exact owned TIA lifetime.
`typed_graphical_runtime_preservation.py` records every file in the closed owned
project directory, including companion `.info` files, and takes a second complete
active baseline after guarded reopen and PLC readback. Each later project
comparison uses its matching lifecycle baseline; all closed-to-active changes
remain visible. The complete generated memory card is captured before
provisioning/RUN. Full card/project inventories and complete PLC XML are recorded
before owned cleanup, followed by final card/project file receipts after exact
TIA cleanup. Failed cleanup is explicitly labeled unverified. A failed card
comparison does not suppress independent PLC readback when the exact owned
session guard still passes. Added `.info` files, changed/removed files,
unreadable scope and uncertain cleanup remain failures.

These observations do not change production simulator admission, isolation,
credential, TLS or cleanup guards. The backend, executor, program generator and
independent expected behavior are hash-bound with the harness. Behavioral
assertions and preservation acceptance are reported separately; there is no
automatic second attempt, recovery reopen after runtime failure, or claim that
the historical zero-address discovery origin has been resolved. The diagnostic
still awaits final combined frozen binaries and closure of every offline native
qualification session. No new runtime result is claimed here.

## Existing-caller insertion preservation contract

`assert_existing_caller_insertion` independently requires precisely one new
direct compile unit at the exact observed insertion boundary. Removing that
single unit from the prepared document must recover the complete original caller
payload. Existing document IDs, per-network UIds, titles, comments, declarations,
opaque extension data, ordering and attributes remain exact. Numeric aliases or
collisions between new and existing document IDs fail. Comparison excludes only
the established export `DocumentInfo` provenance and serializer indentation.
Native readback must then match the complete prepared XML, with all unrelated
project XML also preserved, followed by saved reopen and recovery/reopen gates.

Existing global or multi-instance bindings can accompany this network-only
contract. Creation of storage/declarations requires a separately captured
composite effect contract and is rejected by this qualifier. The production
`NativeIdPreservationUnsupported` rejection remains intact for normal envelopes
whose block-title IDs follow the selected network. Offline contract tests are
not native insertion acceptance, and no title/comment/ID exception was granted.

## Namespace and duplicate-tag diagnostics

`qualify_typed_resolution.py --phase namespace --namespace-unqualified` creates
the known qualified fixture first, then derives exactly one native UDT change:
Alternate Outer's `Child` references unqualified `Tgp_ResolutionInner`.
The selected Alternate Inner remains Bool and the parent-namespace Inner decoy
remains Int. Native readback must actually retain the unqualified spelling and
all other XML. If TIA rejects or canonicalizes it, the attempt stays blocked,
retains the observed spelling and invokes the exact checkpoint recovery once
where its tracked state permits. A qualified native export is never counted as
unqualified propagation coverage.

`qualify_typed_tag_ambiguity.py` uses the independently compiled
`ResolutionSeeder` only against the exact new owned scratch receipt. It creates
one second table and attempts native creation of another `Ready` tag. Production
MCP import collision guards are unchanged. Both native acceptance and vendor
rejection require an exact addition-only snapshot with every pre-existing XML
payload unchanged. If native creation rejects or the resulting fixture does not
compile cleanly, native typed ambiguity remains unqualified. A successful fixture
must then demonstrate the actual MCP `AmbiguousOperand` rejection with complete
unchanged XML. Neither diagnostic replays an uncertain native operation.

The fresh unqualified namespace attempt (`artifacts/p16t/ns-unqualified-LAD-01/result.json`)
on `fa0067` reached the native UDT import, which rejected
`Tgp_ResolutionInner` as an unknown datatype. The exact checkpoint was restored
once, with all 16 complete native payloads matching the original; all 32 retained
exports and the exact owned cleanup passed the independent
audit (`artifacts/p16t/ns-unqualified-LAD-01-audit.json`). Thus the fixture
prerequisite is blocked by the observed native import, and unqualified propagation
is still unqualified. The original report's local probe status remains its
pre-dispatch marker; its failed native call and successful restore, both retained
unchanged, establish the actual outcome. Repeating this language-independent UDT
rejection in FBD would not provide a new resolver qualification.

The direct duplicate-tag attempt (`artifacts/p16t/tag-native-LAD-01/result.json`)
also reached the native API: `PlcTagComposition.Create` rejected the second
`Ready` declaration because the object already exists. Compile remained zero
errors/warnings, and the independent audit (`artifacts/p16t/tag-native-LAD-01-audit.json`)
verified all 19 native exports, nine unchanged original payloads, exactly one new
empty table and exact owned cleanup. Thus the native ambiguity fixture remains
blocked; a production MCP collision rejection was not substituted for this
direct vendor diagnostic. No failed operation or language-independent rejection
was repeated.

## Additional route packet

`route-capture-packet.v1.json` pins the retained 38-input packet and its SHA-bound
source archive. Static review found 45 distinct target block names: ten inputs
for alternate-namespace numeric callees/storage/callers/Eq, eight native TON
diagnostic probes (four per scoped route), and twenty remaining comparison
references (ten per route). The old tgr04 inventory includes the required
Namespace and Unit template FBs; its checkpoint precedes the failed CTD import
and contains the successful default-unit storage declarations. All 38 authored
source hashes, explicit namespaces, formats and declaration names pass offline
checks. No new profile has been enabled from this packet.

`capture_typed_routes_owned.py` retrieves that archive into a fresh owned project
and admits each source once. It rejects overwriting existing reference blocks,
checks exact declared additions and all prior block/type/table XML, retains
durable apply intent and all native exports, verifies a fresh saved reopen on a
completed packet, and closes every exact owned native lifetime. Stop requests
and the first failure halt further admission. A tracked failed source may invoke
its checkpoint restore once; an uncertain or stale restoration is retained as
a blocker. It never modifies or resumes the historical tgr05 disk project.

The eight TON entries remain diagnostic native XML derivations, not native-
accepted route profiles. Earlier SIMATIC SD `Missing instance DB` reports do not
establish that these native probes will fail or that software units prohibit
IEC instructions. Route-specific IEC families/storage, scoped ordinary DB
operand encoding and scoped dependent-DB layouts need their own native captures;
the packet does not establish those broader routes or any runtime behavior.
