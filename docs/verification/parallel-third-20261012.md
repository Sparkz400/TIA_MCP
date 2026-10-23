# Third parallel increment — 12 October 2026

Component integration, typed fixture hardening and independent review ran in
isolated source trees. The root serialized every TIA/PLCSIM operation. Previous
reports and frozen builds remain unchanged; source integration does not deploy
the installed MCP package.

## Typed resolution

The corrected fixture harness verifies that the deliberate stale-tag mutation
changes only the selected address, comparing all remaining target-table XML and
all peer objects. It durably records the attempted typed apply before dispatch.
An uncertain response, unrelated error, partial failure or unexpected success
permits only status inspection and cleanup; recovery of the earlier deliberate
fixture is suppressed. An exact stale-declaration/revision rejection permits one
restore of that deliberate change. Offline typed checks pass 53/53, including 20
focused harness checks. Independent review found no remaining concrete issue.

The remaining six standard LAD cases passed on the frozen p13 combined build:
two exact DB-owner positive cases passed apply/compile, preservation, changed
project reopen and checkpoint recovery/reopen; four negative cases returned the
expected rejection and preserved all native content. Five owned TIA lifetimes
and their hosts/workers exited cleanly. Together with the separately retained p13
tag-positive result, all seven standard LAD fixture cases have engineering
evidence on that binary lineage. This does not qualify runtime or other phases.

All seven standard FBD cases then passed on the same frozen build: three positive
cases passed all four engineering gates and four negative cases passed exact
rejection/preservation checks. Seven exact owned TIA lifetimes and their hosts and
workers exited cleanly. These are fourteen distinct language/case combinations
across the retained p13 LAD positive and the two p14 runs, not a new runtime corpus.
Independent audits checked 180 native XML exports across 20 LAD snapshots and
234 exports across 26 FBD snapshots, including exact owner/start-value selection,
wiring, untouched caller content and peers, recovery and cleanup receipts.

- `artifacts/p14/typed-standard-LAD-01/result.json`
- `artifacts/p14/typed-standard-FBD-01/result.json`
- `artifacts/p13/typed-tag-LAD-02/result.json`
- `artifacts/p14/audit/typed-standard-LAD-01-independent.json`
- `artifacts/p14/audit/typed-standard-FBD-01-independent.json`
- [Fixture changes and commands](typed-resolution-p14-qualification.md)

The first stale-tag run preserved the deliberate address-only edit and received
`StaleRevision`, then stopped because Windows refused replacement of its report
file. It inspected the exact typed change status and cleaned up without replay or
restore; its incomplete recovery remains recorded. The six reported inputs were
archived before correction. Both new harnesses now use the existing bounded
two-second report-only replacement retry for Windows errors 5/32/33. A native
operation is never included in that retry; permanent report failures remain terminal.

A fresh run on the p14 combined Release build passed the duplicate-tag setup guard
and actual stale-address rejection, complete address-only/peer preservation,
checkpoint recovery and recovered-project fresh reopen. Both owned TIA lifetimes
and their hosts/workers exited cleanly. The duplicate-tag result qualifies the
production collision guard, not native ambiguous-tag binding. Runtime remains false.
Independent review compared 63 XML exports across seven snapshots and verified
one stale typed apply, one deliberate-change restore and the exact cleanup receipts.

- `artifacts/p14/typed-stale-LAD-01/result.json`
- `artifacts/p14/typed-stale-LAD-02/result.json`
- `artifacts/p14/audit/typed-stale-LAD-01-independent.json`
- `artifacts/p14/audit/typed-stale-LAD-02-independent.json`
- `artifacts/p14/audit/typed-report-persistence-source-review.json`

## Component qualification

An explicit `--component-qualification` launch flag enables the internal adapter
only for the worker's exact newly created, sole owned scratch project. The normal
launch remains disabled. Read-only preparation cannot cross mutation states;
each native apply requires the current step's exact reviewed hash and explicit
checkpoint/compile/save fields. Restart inspection is journal-only and cannot
resume expired native handles. Production combined PLC apply remains gated.

The executable fixture independently specifies an FB with a UDT dependency,
signals, instance DB and FBD call. It checks exact versions/dependencies, full
unrelated content, saved reopen and recovery from the first import checkpoint.
It preserves the native terminal-insertion guard rather than rewriting native IDs.
Offline checks pass 108 planner/coordinator/gate tests, five actual worker routing
boundary tests without a TIA startup, and 24 Python fixture tests.

The native run authored committed FB/UDT versions with the exact FB-to-UDT
dependency, published their archive and staged a fresh consumer plan. It stopped
before component import at `NativeIdPreservationUnsupported`: native caller
network `3` has following `MultilingualText` ID `8` and `MultilingualTextItem` ID
`9`. The native export and strict preservation guard were retained. Both owned
TIA lifetimes and their hosts/workers exited cleanly. Combined installation,
full preservation, fresh reopen, first-checkpoint recovery and runtime remain
unqualified; the failed attempt is preserved. The recommended next implementation
is an explicit new standalone FC caller mode, using the existing typed
`createBlock` and `createGlobal` DB operations after import. That requires recipe
and coordinator support for an initially absent caller, then the complete
acceptance sequence. Calling the new FC from an OB remains a separate change.
Existing-caller insertion stays blocked under the current unchanged-ID contract;
supporting known title-ID reallocation would require a separately specified
preservation contract and native multilingual metadata qualification.

- [Qualification route and fixture](../guides/plc-library-integration-recipes.md)
- `artifacts/p14/audit/component-qualification-source-review.json`
- `artifacts/p14/component-native-01/result.json`
- `artifacts/p14/audit/component-native-01-independent.json`

## Runtime discovery

Review identified an untested difference from the previous OFF-registration
experiment: the historical failing engineering worker had initialized its own
PLCSIM runtime API during offline simulation download. The p13 probe initialized
that API only in a separate simulator worker. The next controlled comparison
isolates same-process initialization and exact OFF-instance interface acquisition.
The six-phase experiment completed: no runtime API request; same-process runtime
initialization; one exact owned OFF registration; exact interface acquisition;
interface disposal; and exact external unregistration. All eighteen short snapshots
reported the PN/IE PLCSIM route absent. The assembly-load record shows no simulation
assembly in phase zero and its first load in phase one. Source project files,
helper inputs and binaries were unchanged, and owned cleanup completed.
This did not reproduce the original endpoint and does not resolve its identity.
No offline download, protected-card manipulation, credential access or CPU startup
was requested by this diagnostic. It is not a full historical-sequence reproduction.

The historical zero-filled value was the discovery `Address`; no MAC address was
recorded in that original run. MAC evidence was added in p13. A second precision
correction distinguishes credential phases: attempts 04 and 06 completed the
earlier offline download's generated-scratch-credential callback, then stopped
before provisioning credential access, protected-card staging or CPU startup.
Current documentation clarifies that distinction without rewriting native reports.

- `artifacts/p14/audit/discovery-origin-next-review.json`
- `artifacts/p14/process-runtime-01/report.json`
- `artifacts/p14/audit/process-runtime-01-independent.json`

## Validation and open gates

The combined Release build passed with zero warnings/errors. Full regression
passed, including 144 core, 108 planner/coordinator/gate, five actual routing
boundary, 24 component Python, 53 typed Python and 27 simulator-helper checks.
One existing native-observation test remains skipped. Simulator self-tests did
not initialize Siemens APIs. Final source/binary/evidence hashes and the source
promotion receipt are retained in `artifacts/p14/final-validation.json` and
`artifacts/p14/integration-manifest.json`.

Logs: `artifacts/p14/merged/artifacts/parallel-build-final.log` and
`artifacts/p14/merged/artifacts/parallel-regression-final.log`.
The historical full runtime attempt remains 48/65 passing cases. Discovery origin,
native runtime requested-stop acceptance, the complete runtime corpus, other typed
phases and native combined-component acceptance remain separate gates. Existing
VM proof is accepted; the prior native HMI visual stop remains in effect.
