# Selected Test Suite health evidence and relationship repair - 16 October 2026

Base: `eac302b089973075491b6e43f61be47e23bfeca7`. This workstation has no Siemens
product, SDK, simulator or PLC. The preceding [diagnostic integration](testsuite-diagnostics-20261016.md)
is extended through the existing `project_health_report` tool. The base commit
passes hosted Explorer `38956009787` and all Python/managed/package/OPC UA jobs
in `38956009868`.

## Reproduced defect and delivered behavior

Health checked `source.resolution` and `target.resolution`, but project-model.v1
records canonical relationship resolution under `evidence`. A supported resolved
edge without legacy endpoint flags was incorrectly reported as an unresolved
reference. Conversely, legacy endpoint flags could hide partial/inferred edge
evidence. `artifacts/testsuite-health-dev/health-before.log` reproduces the false
positive: expected zero findings, actual one; 150/151 core checks pass on that source.

Health now uses supported edge evidence, unresolved details and exact native
endpoint IDs/paths. The endpoint check reuses the existing impact traversal rule.
Known virtual containment groups are presentation structure; native calls to a
virtual group still produce a coverage gap. `health-after-01.log` passes all
151 core checks, including regressions for resolved edges, partial/inferred
evidence, unresolved details, wrong IDs/paths and virtual containment/calls.

The optional `testSuiteResults` selection accepts at most 32 distinct retained
result-review receipts. Every selected input/report/JUnit is re-derived and
checked against the exact saved review. The separate `testSuite` health section
preserves per-packet outcomes, original artifact references, and resolved versus
unresolved message counts. It never sums native counters, deduplicates distinct
attempts into an invented pass, or promotes selected coverage to whole-project
health. Empty selection is explicitly non-passing. Duplicate packet aliases,
foreign review receipts and tampered inputs refuse rather than yielding a partial
success. Metadata health paging remains independent.

Existing result-review pages and diagnostic selectors consume the exact returned
receipt. No new tool, worker, package runner or native execution profile was added.
See the [workflow and count semantics](../reference/testsuite-result-review.md#selected-evidence-in-project-health).

## Development verification

- `testsuite-02.log`: 634 managed checks, including 54 result-intake checks.
  New cases cover empty/passing/mixed selections, clean but stale scope, missing
  results/scope, root errors without messages, exact receipts, aliases, tamper,
  32 complete packets, the 33-packet refusal and independent metadata paging.
- `mcp-01/verification.json`: 55 actual Host checks across two clean unforced
  lifetimes, including release/restart, duplicate/foreign/unknown-field refusals,
  health-schema adversaries, conflicting attempts and root counters.
- `diagnostic-mcp-01/result.json`: the 32-step diagnostic workflow consumes a
  health-report receipt through case creation, watch planning, measurement review
  and explicit replan; four clean Host lifetimes, no workers. Development samples
  came from the preceding diagnostic fixture; final validation regenerates them.
- `host-01.log`: zero-warning/error Host build.

Development paths above are under `artifacts/testsuite-health-dev`. Fixtures are
authored synthetic observations, not new native evidence.

## Frozen verification

The first frozen source, `artifacts/testsuite-health-01/source`, is the full
aggregate baseline. During its run, an additional regression established that a
virtual node carrying a nonempty object path could still satisfy the shared
native endpoint predicate: `virtual-before.log` expected one coverage gap but
observed zero. The final predicate explicitly excludes virtual node origins.
Regressions cover both health and impact traversal, including virtual source and
target endpoints. `virtual-after.log` passes all 151 core checks.

The first freeze passes the complete 30-suite/Python/Explorer/MCP aggregate and
package/relocation. Its manifest SHA-256 is
`9fe4d587bf72dab84ec3c4b208e7e5fb9df375928a3f672c80df586d0894fe3f`; aggregate
receipt `source/artifacts/checks/result.json` is
`5e32f7f4565e36b0af81084bee8dca784d81639620d1f2305a36979d25fd8f03`.
It is baseline evidence and does not prove the later predicate correction.

Final frozen source: `artifacts/testsuite-health-02/source`, Git tree
`b424fb2f7a86947081552b680fab6ce08b0a0797`, 1,833 files. Its manifest was written
before execution. Final package/relocation passes:

- Original/relocated startup, exact ZIP/checksums/notices, native refusal and restart.
- GRAPH, AutomationML, OPC UA, VCI, translation and diagnostic workflows.
- All 26 diagnostic groups and the 32-step actual MCP diagnostic workflow across
  four clean unforced Host lifetimes, consuming the health-provided receipt.
- All 634 Test Suite checks and 55 result/health MCP checks across two clean Host
  lifetimes, with no native workers.
- Strict final package verification: 1,322 files, no extra non-runtime files.

The final aggregate passes all 30 managed suites, 1,280 Python tests (one
unavailable-native-evidence skip), 24 Explorer model/client checks, contracts,
source bindings and every portable MCP workflow. Core includes all 151 checks
with health/impact virtual-node regressions. Test Suite includes 634 checks,
46 existing style MCP checks and 55 result/health MCP checks. Diagnostics passes
26 groups and 32 actual MCP workflow steps. The retained evidence audit still has
six byte matches and 106 unavailable historical inputs; missing is not verified.

Implementation, contracts and harnesses match the final frozen manifest. Only
result/status and count-explanation documentation changes after validation.
No new browser visual, native SDK/product, simulator, device or release
qualification is claimed. Published-commit CI is tracked separately; exact base
CI runs are recorded above.

| Final receipt | SHA-256 |
| --- | --- |
| `source/artifacts/checks/result.json` | `10cba1df53763f32b18a24db6f9683cb4360073593e91431c157ce7afb283454` |
| `source/artifacts/checks/testsuite-result-mcp/verification.json` | `6d064c2773da0a193dbb6bb3b485d76713fbd7b84894bc419c886ca379f86ee6` |
| `source/artifacts/checks/diagnostic-mcp/result.json` | `bc166140aba64a9099301a9d6529d9b15b9e8b0a53a519d9afa2aeb7c14fd418` |
| `source-manifest.json` | `3d556bec12263579f9defe4f7e718c451952883b185ae374b19780f9d27d26da` |
| `source/artifacts/package-checks/result.json` | `fde7e45a897e0722f6ecbd9644900c2ea7d98288144c6fcd9028c0fe4c05529b` |
| `source/artifacts/package-checks/diagnostic-mcp/result.json` | `6fbd6a4a615cc27c56a968a14c7797fb0a8f7188e3f62367e4b5bb61b071d65c` |
| `source/artifacts/package-checks/testsuite-result-mcp/verification.json` | `308aa5f98372d17e80a27010bd9d04cf63e225387b9c2168a7aee888a7e1336d` |

Final paths above are relative to `artifacts/testsuite-health-02`. The ZIP is
`source/artifacts/releases/tia-mcp-offline_20261016_195156_134b4bac.zip`, SHA-256
`627c25aa94840847c8549b5bccd12bd1a38f34bf367dbe8ef94c1f131b5445cc`.
Its package manifest hash is
`4f1076caafbd60653d8cd74b21d5b7e06823aa61d3e81752b254a285e2d221be`.
Hashes establish byte consistency, not publisher authenticity.

## Remaining work

Selected test evidence is not complete project health, authenticated source,
current program identity, native compile/safety/physical inspection or execution
qualification. Broader alarm/context orchestration, native provider/scope/license,
recovery and runtime obligations remain open in the [full queue](../history/offline-execution-20261016.md).
