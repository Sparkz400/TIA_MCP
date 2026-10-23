# Test Suite finding to diagnostic case - 16 October 2026

Base: `d98d8d0d11db404095cd83647e23da6ec3a4f8d6`. No Siemens product, SDK,
simulator or PLC is available here. This increment connects the preceding
[supplied-result review](testsuite-result-review-20261016.md) to the existing
diagnostic planner, watch planner and manual measurement workflow. The base commit
passes hosted Explorer `38953860191` and all Python/managed/package/OPC UA jobs
in `38953860326`.

## Scope and source guarantees

`diagnostic_plan` accepts an optional receipt reference and finding index. It
reuses the existing result re-derivation/hash checks, requires the same saved
review and exact block binding, and refuses conflicting selections. The case
retains the complete finding and original/report/JUnit/receipt references with
unverified source/native provenance. A display page's oversized-row marker does
not truncate the evidence retained by the case.

The caller still supplies the report/hypotheses and explicitly selects the
instance and signals. A style finding is neither a root-cause conclusion nor
whole-project health. Existing watch/trace/measurement tools consume the exact
case ID; no second planner or native execution route was introduced.

`diagnostic_replan` leaves old findings in the immutable previous-case lineage.
A new selector is optional but must pass the new review/block checks. Historical
source IDs are never silently rebased. Prior evidence remains available through
`lineage.previousPlanId` even when the new case has no Test Suite evidence.

Contracts cover the optional selector, complete evidence and exact-binding-only
case output. Feature metadata and Host descriptions retain native limitations.
Both existing aggregate and portable-package runners invoke the expanded actual
MCP diagnostic harness; no new runner or test-only public tool was needed.

## Development verification

- `artifacts/testsuite-diagnostics-dev/managed-02.log`: 26 diagnostic groups pass,
  including 28 new checks for original counters/provenance, immutable reopen,
  watch/measurement use, explicit instance selection, invalid indexes/fields,
  conflicting blocks, stale/non-block/absent findings, tampered retained artifacts,
  changed reviews and explicit replan evidence.
- `testsuite-01.log`: 616 Test Suite checks pass, including complete direct finding
  access when bounded display paging uses an omission marker.
- `mcp-02/result.json`: 31 steps, four clean unforced Host lifetimes, no native
  workers. The same finding-linked case proceeds through upload release/restart,
  11 watch rows, manual trace specification, supplied measurement analysis and
  comparison, and explicit instance replan. Schema adversaries reject altered
  binding, native qualification and negative counters.
- `host-01.log`: Host builds with zero warnings/errors.

All inputs above are authored synthetic fixtures. They prove tested portable
behavior, not native results or current program identity.

## Frozen verification

Frozen source: `artifacts/testsuite-diagnostics-01/source`, Git tree
`b1aec3cf599596942df8b2ab64dc30e70d6db0ff`, 1,831 files. The manifest was written
before execution. Package/relocation validation passes from those exact bytes:

- Original/relocated startup, exact ZIP/checksums/notices, native refusal and restart.
- GRAPH, AutomationML, OPC UA, VCI, translation and supplied Test Suite workflows.
- All 26 diagnostic groups, including 28 integration assertions; the relocated
  diagnostic MCP workflow passes 31 steps across four clean unforced Host lifetimes.
- All 616 Test Suite checks and 39 supplied-result MCP checks.
- Strict final verification: 1,320 package files, no extra non-runtime files.

The frozen aggregate passes all 30 managed suites, 1,280 Python tests (one
unavailable-native-evidence skip), 24 Explorer model/client checks, contracts,
source bindings and all portable MCP workflows. Diagnostics passes 26 groups
with 28 new integration assertions and 31 actual MCP steps across four clean Host
lifetimes. Test Suite passes 616 managed checks, 46 existing style MCP checks and
39 result-intake MCP checks. Six retained historical inputs match; 106 absent
inputs remain unverified.

All implementation, contracts and harnesses match the frozen source manifest.
Only status/result documentation changes after execution. New browser visual,
native SDK/product, simulator, device and release qualification were not tested.
Published-commit CI must be read separately from these local results.

| Receipt | SHA-256 |
| --- | --- |
| `source/artifacts/checks/result.json` | `015915975c259f39f91366a93a31a08e7a10609c31a5df99cb5af56d6e297fc3` |
| `source/artifacts/checks/diagnostic-mcp/result.json` | `eac8729d148b39f5827cd5f5ee32765e4ebc530fef875929310f4e7d229a56d9` |
| `source-manifest.json` | `ab989a3be66da24ed9a358127a56abc0f40fbc97e8d153cacc390b5ea0cfb144` |
| `source/artifacts/package-checks/result.json` | `3ef27303fda536c27a608991c5f8833118d76ac4bb518f8a4b542c02823de0da` |
| `source/artifacts/package-checks/diagnostic-mcp/result.json` | `285240e132420f23fc0f076158935c4edebdb0fc7dd8a93825123a2b189ee261` |

Paths are relative to `artifacts/testsuite-diagnostics-01`. The ZIP is
`source/artifacts/releases/tia-mcp-offline_20261016_192618_c679d66d.zip`, SHA-256
`663d172ab9805aa2cb648176a1d9be063177bf0e4a64bfd22c967a7043194c83`.
Its package manifest hash is
`0ca177ad69fe48cb509578e8113a9a1dd317784f9953d95bbdad95a37fc497d5`.
Hashes establish byte consistency, not publisher authenticity.

## Remaining obligations

Automatic project-health aggregation, broader alarm/bug context composition,
supported native providers, independently observed effective scope/license,
native build/execution/recovery, acquisition and target behavior remain open.
The [44-workstream queue](../plans/offline-execution-20261016.md) is not complete.
