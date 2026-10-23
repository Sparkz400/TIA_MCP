# Second parallel increment — 12 October 2026

This increment used three isolated implementation/review lanes. Component execution
and typed qualification fixtures were developed independently; a third agent audited
both. The root ran all native operations serially, then merged and tested the source.
Existing native reports, frozen packages and installed MCP configuration are retained.

## Component coordination

The internal coordinator now records staged progress before native dispatch, requires
the exact reviewed preview for each step, revalidates imported versions/content and
mapped declarations, prepares the graphical step freshly, and retains the first full
project checkpoint as its recovery anchor. Lost responses and uncertain results allow
status reconciliation, never automatic replay. The planner/coordinator suite passes
82 checks: 52 planner checks and 30 coordinator checks.

The native adapter compiles but its factory is private and has no tool dispatch route.
Combined installation remains unavailable through production `change_apply`. Native
composition, fresh reopen, recovery and simulator behavior are unqualified. The
[recipe guide](../guides/plc-library-integration-recipes.md) names the callable internal
API and these remaining boundaries.

## Qualification harness

The new executable `scripts/qualify_typed_resolution.py` creates a separate owned
scratch fixture, retains exact complete XML snapshots, independently checks selected
bindings/wires, and exercises compile, persistence and recovery gates. Namespace and
stale-declaration phases have explicit setup requirements. Unsupported duplicate-tag
setup is distinguished from a successful native ambiguity test.

Fifteen new harness checks cover owned cleanup, identity, preservation, namespace
decoys and lost/partial recovery responses. The typed Python suites pass 48 checks.
Review found and fixed a possible second restore dispatch after uncertain recovery;
the durable attempt marker now prevents it. See the
[native harness guide](typed-resolution-native-harness-20261012.md).

The first native LAD attempt stopped during fixture setup: the new CPU's default
tag table contained 47 generated system constants in inventory. Separate retained
native exports showed that these inventory constants need not appear in tag-table
XML. The corrected template selector permits only
those system inventory constants and still requires empty exported XML. Setup creates
a separate fixture table and retains the original default table. User constants,
unknown constant kinds, foreign scopes and existing fixture tables remain rejected.
The original attempt and its exact harness source snapshot are retained under
`artifacts/p13/typed-tag-LAD-01`.

The corrected combined-build attempt passed the single `standard` / LAD /
`tag-exact-owner` case. Native apply/compile, independent Boolean topology,
preservation, saved changed-project reopen, checkpoint recovery and recovered-project
reopen all passed. The original default table remained unchanged. Three exact owned
TIA lifetimes and their hosts/workers exited cleanly. No runtime verification was
performed. The other standard cases, FBD, namespace and stale phases remain pending.
Independent audit rehashed 54 native XML files across six snapshots, verified the
exact LAD wiring, unchanged default-table inventory including all 47 constants,
the recovery archive and prepared artifact, and the single apply/restore dispatches.

Reports and correction review:

- `artifacts/p13/typed-tag-LAD-02/result.json`
- `artifacts/p13/audit/typed-template-correction-review.json`
- `artifacts/p13/audit/typed-tag-LAD-02-independent.json`

## Native runtime diagnosis

The pre-start evidence now records MAC addresses in addition to name, address and
device series, plus explicit mode/interface/provider state. Admission still requires
the existing absence checks; no zero-address entry is ignored.

The first diagnostic attempt timed out at Siemens Openness authorization, before
opening a project or registering a simulator. Its original failure remains retained.
After the user's authorization, the corrected attempt completed a controlled sequence
with the simulator worker already loaded: empty registry, one exact owned OFF/Softbus
registration, and exact release. All nine TIA discovery snapshots reported the PLCSIM
route absent. All ten original scratch-project files remained unchanged; the owned
registration was removed and host, worker and TIA exited cleanly.

This experiment did not reproduce the placeholder. It does not identify the historical
zero-address endpoint or resolve the runtime blocker. No CPU startup, program download
or credential access was requested. Reports and independent audit:

- `artifacts/p13/off-registration-01/report.json`
- `artifacts/p13/off-registration-02/report.json`
- `artifacts/p13/audit/off-registration-02-independent.json`

## Native cooperative stopping

The engineering-only run used the immutable first-increment build under `artifacts/p12/m`.
The stop sentinel was created at 04:57:26.950 UTC, during an active `change_apply` call
(04:57:26.906–04:57:29.469 UTC). The active Boolean suite completed compilation, native
XML checks, save/fresh reopen and both exact owned TIA exits. Host and worker exits were
clean. The next counters suite remained prepared and never started. The aggregate
correctly remained blocked with `stopped_by_request`, not a complete-suite pass.

This is native engineering suite-boundary acceptance. Runtime case stopping and
simulator cleanup are still separate pending acceptance. Reports:

- `artifacts/p13/engineering-stop-01/qualification.json`
- `artifacts/p13/audit/engineering-stop-01-independent.json`

## Combined validation and remaining work

The combined Release build has zero warnings/errors. The first regression invocation
hit an existing boundary-test fixture limitation because the isolated temporary path
was too long; its failure log is retained. The complete gate then passed using the existing
`-TestTempDirectory` option and a short task-owned directory: 144 core checks, 82
planner/coordinator checks and 27 simulator-helper checks, alongside all other existing
suites and the new typed harness tests. One existing task-local native-observation test
remains skipped. Simulator self-tests did not initialize Siemens APIs.

Logs: `artifacts/p13/merged/artifacts/parallel-build-final.log`, original
`parallel-regression-final.log`, successful `parallel-regression-short-temp.log`,
and final corrected-harness regression `parallel-regression-final-verified.log`.
The final source/binary/evidence manifest is `artifacts/p13/final-validation.json`;
source promotion is recorded separately in `artifacts/p13/integration-manifest.json`.

The full 65-case runtime attempt, the discovery origin, runtime requested-stop proof,
and native combined-component installation remain open. Production HMI writes and the
previous native HMI visual stop are unchanged; existing VM proof remains accepted.
