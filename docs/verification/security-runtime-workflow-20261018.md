# Retained security/runtime MCP workflow — 18 October 2026

This C03 increment exposes the existing evaluator through six offline MCP tools.
It does not complete C03 runtime/capture integration or the full 44-workstream
goal. The [workflow reference](../reference/security-runtime-handoff.md) describes
the caller flow, byte preservation and explicit authority limits.

Implemented: canonical retained observation intake, exact request/report
publication, intent/receipt verification, reopen, bounded paging, exact original
artifact downloads and discovery of complete/incomplete/damaged records. Both
observation intake and evaluation use the same strict parser. Every read checks
all original dependencies and rebuilds the report. Supplied evidence never
creates a session or admits a runtime operation/assertion.

Focused verification under `artifacts/security-runtime-workflow-01/`:

- Release Host compilation passes without warnings.
- `managed-03`: the existing 147 access/evaluator checks and 268 new workflow
  checks pass. Six actual publisher processes are stopped at flushed observation
  and record boundaries. Competing writers refuse while the publisher holds its
  lock; completed records remain readable. Partial files are never adopted.
- Independent original-byte verification checks three completed records, all six
  interrupted publications, exact receipt/observation hashes and charged byte
  counts. Manifest mutation controls refuse added authority and reordered names.
- All 55 evaluator cases and eight altered-report controls still pass their
  independent original-input/schema checks.
- `handoff-mcp-05`: 110 checks pass across two clean Host lifetimes. Public tools
  import the saved review, reproduce the original access record, retain original
  observations and publish/reopen/page/download reviews. Checks cover released
  uploads, strict fields/provenance, changed-program invalidation, oversized
  observation paging and complete downloads, discovery, dependency tampering
  and self-consistent forged output hashes. No native workers launch.

A new exact-boundary regression fails on the previous paging code: the original
row occupies 126,976 bytes, but adding its index/value envelope exceeds the row
budget and returns an empty page with an unchanged offset. A shared row builder
now measures the complete encoded row before omission in the handoff, composite,
AutomationML and GRAPH stores. The repaired regression advances to completion
and preserves full original downloads. Focused checks also cover exact-fit null,
escaped Unicode, combined row budgets, oversized rows and impossible omission
budgets. The negative control is retained in `pagination-negative-01.log`.
The smaller boundary fixture uses fewer download chunks, so the total check
count is 268 despite adding nine assertions to the prior 267-check workflow.

The first frozen aggregate refused the missing release-obligation mappings for
the six new catalog entries before running tests. All six now map explicitly to
C03/D12/D16/X03/F02/L05, and the contract check passes with 233 tools, 56 mutation
kinds and 307 feature entries. The corrected aggregate and package runs use two
identical 2,310-file source snapshots (`final-aggregate-source-manifest.json` and
`final-package-source-manifest.json`) under the evidence directory. Both corrected runs pass; the earlier package run remains separate evidence
and does not qualify the corrected registry snapshot. Historical evidence references are unchanged.

## Full validation

The identical frozen source passes all 44 managed suites (242 general checks),
1,508 Python tests (one existing skip), Explorer model/client checks and all
configured Host workflows. All 37 package/relocation stages pass. Both runs
include 268 handoff workflow checks with six actual process interruptions, 110
handoff MCP checks across two Host lifetimes, 147 access/evaluator checks, 55
independent original-input cases, eight report mutation controls and 36 actual
prior-access compatibility checks. The existing GRAPH, AutomationML, composite,
drive/encoder and other workflows also pass.

A separate probe executes the 147 access/evaluator and 268 workflow checks
against the exact Core and Graph DLLs from the finished package, including the
pagination boundary and six actual process interruptions. Independent verifiers
check the original inputs, three retained reviews and incomplete publications.

| Frozen source manifest | SHA-256 |
| --- | --- |
| final-aggregate-source-manifest.json | `a27420fcbb54e3334e472a46a7c640807dee453d20b40a82e0f71a556a8edb99` |
| final-package-source-manifest.json | `720a8a615df72251d2cc9b739dec4629ff6071408b06f8cd6e2cd9604a779dc8` |

The package contains 1,644 files; manifest SHA-256
`2fa08b9c51df9c92017cb7c929f5ef5f271e3c042f42405f8d87d1a3cd614405`. Runtime MCP checks total 640
in the aggregate and 647 in the package. Variable polling/file
counts are reconciled against retained observations and fixed scenarios.

`artifacts/security-runtime-workflow-01/verification.json` binds the checks to
these snapshots and records documentation and verified CI upload-rule changes afterward. The integration
receipt records the pushed commit and exact-head hosted CI outcome. All 112
historical evidence entries are preserved; 106 inputs remain unavailable.

CI artifact retention initially omitted saved access/model dependencies. The two
upload configurations now retain those exact public fixture trees plus the MCP
store's original handoff/access/model records. A separate isolated-copy check
applies each job's path selection and independently verifies all seven completed
records and six interrupted publications per job. `ci-retention-verification.json`
binds this check to the final workflow-file hash. Only those ten artifact-path
additions differ from the frozen workflow; jobs and executable sources are unchanged.

## Remaining work

Existing runtime-session/capture integration, independent quality/gap assessment
and migration/recovery evidence remain open. The full C03 contract and remaining
44-workstream obligations are not complete. No actual browser, Siemens SDK/product,
physical device, true disk-full, power-loss or native admission result is claimed.
Hosted CI billing remains a separate gate. Supplied or synthetic observations
never create a live session, establish native identity or grant operation/assertion
authority.
