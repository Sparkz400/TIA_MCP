# Overnight work — 22 October 2026

The persistent goal remains active. All changes are local; nothing has been
published or merged. This report is updated as increments finish. Work continued into 23 October.

## Implemented

- Added explicit diagnostic experiment bindings before OPC UA polling or
  subscription capture. Original experiment/source evidence and exact session
  attempt/node order are checked before capture starts.
- Added retained request compatibility, rejection and restart tests, a real
  encrypted loopback Host workflow, public contracts, feature registration and
  usage documentation. Existing unbound captures retain their behavior.
- Added the workflow to the offline aggregate and relocated-package runners.
- Added retained UA observation reports with complete original bytes, per-signal
  quality/timing evidence, unresolved hypotheses, readable exports and Explorer
  import/download. Reports and exports revalidate original sources; unbound
  captures cannot gain a diagnostic association retroactively.

- Fixed typed graphical identifiers accepting a trailing newline. Worker validation
  and public schemas now require the entire identifier to match. General symbolic
  components and comments retain their existing behavior.
- Repaired filesystem-specific Add-In test expectations after reproducing different
  Windows refusal exceptions; retained-state and no-replay assertions still run.

- Applied the same complete-string correction to component recipe preflight and
  dependency hashes, with the public recipe schema and retained regressions.

- Fixed a reproduced component artifact race: hash verification and XML decoding
  now consume the same bounded byte snapshot, even if the path changes.

- Bound component producer planning and policy construction to the exact retained
  artifact hashes and optional original byte counts. Root/caller XML and returned
  evidence descriptors now agree.

- Extended ordinary Classic screen inventory and Explorer metadata to all six
  screen roles with distinct paths, bounded traversal and explicit scope coverage.

## Validation

Final focused qualification passed 54 binding checks, all 45 diagnostic groups,
475 UA runtime checks, 93 Host/loopback checks and 43 focused Python tests.
The rebuilt Host has zero build warnings/errors; the embedded feature catalog
and source binding registry pass. See
[verification](../verification/diagnostic-ua-binding-20261022.md) and
`artifacts/overnight-20261022/` for the final receipts.

The observation increment additionally passes all 46 diagnostic groups (35 report
checks), 902 real Host/loopback checks, 112 actual prior-producer replay checks and
129 browser/model checks. All 1,652 Python tests pass, and the aggregate completed all 45 managed suites
(including 242 general checks) and every Host workflow. The baseline relocated
package passes all 53 stages. The final viewer rebuild has separate focused
qualification: its packaged Host additionally passes 912 checks
and its exact relocated viewer passes 129 browser/model checks, including refusal
when diagnostic binding evidence is missing. See [observation verification](../verification/diagnostic-ua-observations-20261022.md).

The subsequent typed increment passes all 243 general managed checks (including
156 new boundary checks), 138 typed Python tests, six UA contract tests and 64
ECMAScript pattern checks. The Host catalog remains at 261 tools/56 mutation kinds/
335 features. These focused checks apply to the current tree; the earlier package
receipts apply to their recorded frozen sources. See
[typed verification](../verification/typed-text-boundaries-20261022.md).

The component follow-up passes all 213 managed integration groups, 43 related
Python tests and 50 JavaScript pattern checks. The original five planner failures
and ten schema failures are retained. See
[component verification](../verification/component-text-boundaries-20261022.md).

The artifact-read follow-up passes 227 component integration groups, 43 related
Python tests and 15 exact-source .NET Framework checks. A filesystem race reproduces
the original wrong-byte read; the same probe observes no mismatch in 20,000 fixed
attempts. See [artifact verification](../verification/component-artifact-read-20261022.md).

The producer follow-up passes 245 component groups and 43 related Python tests.
The full worker also compiles against the installed SDK with zero warnings/errors;
it was not launched. Exact source/binary/reference hashes and verification limits
are in [producer verification](../verification/component-plan-artifacts-20261023.md).

The Classic screen inventory increment passes 99 inspection checks, 33 model
checks and 76 Python/contract tests. Full SDK worker and Host builds have zero
warnings/errors; the actual Host probe uses `--no-native-workers` and exits cleanly.
Native acceptance for added roles remains open. See
[screen inventory verification](../verification/classic-screen-inventory-20261023.md).

The next combined aggregate exposed a Windows Git path failure after 1,662
Python tests (one skip), 25 Explorer checks and eight managed suites passed.
The repair passes 123 broad Git checks, 80 actual workflow checks and focused
long-path/repository-selection controls. The original failed aggregate and all
probe failures remain retained. [Git path verification](../verification/git-paths-20261023.md).

The library contract follow-up adds external identity/lock schemas and a bounded
original-byte validator. Fourteen tests, 16 historical native lock receipts and
15 extracted identity views pass. These additions postdate the repaired frozen
runtime snapshot and have separate [verification](../verification/library-observation-contracts-20261023.md).
The next contract increment adds preview and live/recovered response envelopes,
with 16 more tests, the 14 existing compatibility tests and 32 retained native
response/preview pairs passing. Cross-field and recovered-state consistency are
checked without replay or embedded-file access. Operation-specific plan/result
payloads and inspection contracts remain open. See [change-observation verification](../verification/library-change-observations-20261023.md).

## Next work

Freeze the repaired combined source and rerun the complete offline aggregate and
package qualification, then continue eligible library contract work.
The typed text-boundary repair is integrated and verified. Native and physical
gates remain deferred.

## Deferred during the unattended run

- TIA/PLCSIM startup, authentication, native project/editor operations and native
  qualification, per the overnight scope.
- Physical PLC/server acceptance and operations requiring interactive approval.
- Hosted CI billing, publication clearance, releases and merging. Existing
  repository notes retain their separate unresolved gates.

No skipped native or external requirement is recorded as a pass.

## User-requested baseline — 23 October 2026

The current working tree is checkpointed as a baseline on
`work/goal-wave6-20261021` at the user's request. It includes all non-ignored
source, tests, synthetic fixtures and documentation changes. Generated builds,
local evidence and configuration remain excluded by the existing ignore rules.
The repaired frozen aggregate is still running; full package qualification is
pending. This baseline is not a declaration of release acceptance.

The in-progress library inspection contract covers paged elements, versions,
dependencies and uses, with query-specific row shapes, bounded pages and
consistent count/completeness checks. Five initial tests and 26 historical native
uses responses pass, alongside the existing 30 identity/lock/change tests.
Broader native inspection and detailed operation payload/result coverage remain
open. Evidence: `artifacts/overnight-20261022/library-contracts/inspection-baseline-tests.log`,
`inspection-native-baseline.json` and `baseline-tests.log`. No native process was
started. These additions postdate the frozen aggregate snapshot.
