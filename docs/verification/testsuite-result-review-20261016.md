# Supplied Test Suite result intake and review - 16 October 2026

Base: `bbbddba88d1da45cd69004b9545eb30e76b1612c`. This workstation has no
Siemens product, SDK, TIA, PLCSIM or PLC. The base commit passes hosted Explorer
run `38949281252` and all Python/managed/package/OPC UA jobs in `38949281447`.
Earlier account billing/spending-limit refusals remain historical evidence.

## Delivered increment

Two portable Host tools accept the bridge's supplied-result packet, retain its
original bytes plus derived report/JUnit, and reopen bounded pages after upload
release and Host restart. They reuse the existing upload, saved-review,
content-addressed Test Suite artifact and result/scope/identity implementations.
No dependency, native execution profile or competing provider interface is added.

Finding association checks exact source observation, project, PLC and scope.
Same-path results from another observation remain unresolved candidates; foreign
PLC paths are not candidates. Required-scope content mismatches cannot pass the
saved-review gate. Root counters and original capture indexes remain visible.
Output never authenticates the source, freshness, TIA version or native execution.

JUnit adds one explicitly named offline evidence check, which fails for missing
independent scope or saved-review binding even when the supplied raw result says
success. This is separate from captured native execution units/assertions.

The [user workflow](../reference/testsuite-result-review.md) documents the input,
binding statuses, bounds, downloads, JUnit meaning and remaining integration.
Contracts and feature bindings cover 183 MCP tools, 55 mutations and 256 features.
The aggregate and relocated portable-package runner both exercise the new flow.

## Development evidence

`artifacts/testsuite-intake-dev/after-05.log` passes 615 managed checks, including
35 new intake checks. They cover original retention, strict malformed inputs,
source mismatch, content mismatch, tampering with originals/reviews/derived
reports, report expansion refusal before publication, 1,000 fully paged findings,
100,000 supplied messages and exact terminal-page retention. Oversized rows are
explicit download markers; a wrapper crossing the page budget also advances the
cursor. These are authored synthetic observations, not genuine new Siemens runs.

`mcp-03/verification.json` passes 39 actual Host checks across two clean lifetimes,
including schema validation, Unicode upload/download, idempotent retention,
release/restart, missing-scope JUnit failure and tamper refusal. This development
run precedes the final wrapper-budget fix; final frozen checks below supersede it.

Earlier attempts are retained: `mcp-01` failed because the harness used the wrong
upload argument (`base64` instead of existing `data`); the harness was corrected.
`after-04.log` refused a wrongly sized boundary test fixture before its assertions;
`after-05` corrects the fixture. Neither failure is counted as passing evidence.

## Frozen validation

Frozen source: `artifacts/testsuite-result-review-01/source`, Git tree
`10f6dfb425ca1556ad6287b9dd00a9f91f5e5acd`, 1,829 files. The source manifest was
written before execution. Portable package/relocation passes from those bytes:

- Original and relocated MCP startup, exact ZIP/checksums, notices, native refusal,
  local Git, resources and restart.
- GRAPH, AutomationML, OPC UA, VCI and translation workflows, plus saved-review,
  diagnostic watch and supplied measurement review.
- All 615 Test Suite managed checks and 39 new result-review MCP checks through
  the relocated Host, with two clean unforced Host lifetimes and no workers.
- Strict relocated package verification: 1,319 files, no extra non-runtime files.

The full aggregate passes all 30 managed suites, 1,280 Python tests (one
unavailable-native-evidence skip), 24 Explorer model/client checks, contracts,
source bindings and every portable MCP workflow. Test Suite includes 615 managed
checks, 46 existing style MCP checks across four clean Host lifetimes and 39 new
result-review MCP checks across two clean Host lifetimes. Historical evidence
inventory remains six byte matches and 106 missing inputs; missing is not verified.

Implementation, contracts and harness bytes match the frozen manifest. Only
result/status documentation changes after execution. No new browser visual,
native SDK/product, license/scope provider, simulator, device or release
qualification is claimed. Published-commit CI is tracked separately from these
local results; the exact base runs above are green.

| Receipt | SHA-256 |
| --- | --- |
| `source/artifacts/checks/result.json` | `7d322cc81916871be0a0380d9970faf053da4a60ce99a9c54ac6ce7071ef227a` |
| `source/artifacts/checks/testsuite-result-mcp/verification.json` | `843a2757f2b98c84775b72f733c9a0d1e61d40ea63624c63e9b27e8d0c231f5e` |
| `source-manifest.json` | `cf8cacc07403eeebbb2e1798dc79f480589ae87b5719e034e0681636ecde5c42` |
| `source/artifacts/package-checks/result.json` | `b54b0f4f88aea162a6f8fb2800bfadabcec6f87be0f0b10117353ba71c149e14` |
| `source/artifacts/package-checks/testsuite-result-mcp/verification.json` | `2d0d38e7ce42ddaf0a34ddc78cc031b7964b2f915ff39b7ebec54a8c47c64ad7` |

Receipt paths above are relative to `artifacts/testsuite-result-review-01`.
The offline ZIP is `source/artifacts/releases/tia-mcp-offline_20261016_190851_48d9875e.zip`,
SHA-256 `9abce8f9b6f7b5a6383b021188f0bae24d5eeb523f09098b568c56324e984f38`;
its package manifest hash is
`924908c3ec280bf2b72c490bb57d01ab071c35985688ebf6098f1b9c71bcd5e8`.
These hashes establish byte consistency, not publisher authenticity.

## Remaining work

D06/C04 remain open: automatic project-health/diagnostic composition, application
and system execution providers, genuine current scope/license observations,
native compilation, recovery, endpoint/runtime behavior and release qualification.
Historical native snapshots must not be rebased to fabricate exact associations.
The full [44-workstream queue](../plans/offline-execution-20261016.md) remains active.
