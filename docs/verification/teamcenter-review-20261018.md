# Supplied Teamcenter metadata and local manifests — 18 October 2026

Baseline: `d333a2e4969f1620922d0df790a5556cc83a45f3`. Implements the portable TC-02 slice of the [full X09 plan](../plans/expansion-plans/teamcenter-gateway.md), using [two offline review tools](../reference/teamcenter-observations.md). No Teamcenter adapter, service connection, authentication, native cache operation or publication was executed.

## Delivered behavior

Strict before/after connection, item, revision, dataset, lock and local-copy declarations retain exact identities, opaque revision/version strings, public metadata, unknown/failed coverage, missing parents and cache/server version differences. Project and global-library identity remain distinct. Supplied lock state never establishes operation-owned checkout or permission to publish.

Up to sixteen separately committed local manifests are verified against their declared SHA-256 and root, retained independently, and revalidated on reopening. File declarations reject unsafe Windows paths, case aliases, file/ancestor collisions and inexact/negative byte counts. Manifest bytes are verified; file contents, complete native cache, source authenticity and live freshness remain unverified. Reports cannot satisfy native operation-journal reconciliation.

## Focused execution

- **77 managed checks:** identity, coverage, lock uncertainty, credential-field refusals, partial manifests, source hashes/roots, malformed text, unsafe paths, row and byte bounds, maximum 1,024 datasets, sixteen manifests, over-limit refusals and global-library identities.
- **75 actual MCP checks:** independent schemas, original request/manifest bytes, bounded complete pages, oversized-row omission, invalid metadata/manifests, upload release, two clean Host lifetimes, tampered artifacts and newly hashed forged report/missing/extra manifest mappings. Native workers are disabled and absent.
- **Contract coverage:** 222 tools, 56 mutation kinds and 296 features. All 106 unavailable historical evidence records retain their original bindings.

The initial compile caught a local variable-name collision between manifest tracking and the page byte counter. It was corrected before the first successful Host workflow test; the subsequent focused tests passed. No failure is relabeled as native evidence.

The aggregate Python run also exposed an existing `ResourceWarning` for an unclosed whole-file read in `scripts/smoke_safety_direct.py:296`; the same pattern occurs at line 256. The source is unchanged in this increment. The warning and exact source hash are retained in `artifacts/teamcenter-review-01/safety-read-followup.json` for the next targeted bug fix; it is not hidden by the passing test result.

## Frozen regression scope

Both snapshots contain identical maps of 2,144 source files, including implementation, schemas, fixtures, tests and runners:

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/teamcenter-review-01/aggregate-source-manifest.json` | `ea9e679ca5bc46d456aa13c97132217b2775838369c20dd559a1048957e0decd` |
| `artifacts/teamcenter-review-01/package-source-manifest.json` | `4b31ab5e19d36f8111383a300f5b2932ca8b971d06b63d74edcfe92e71e9d2df` |

The full aggregate passed 43 managed suites, 1,464 Python tests (one skip), Explorer checks and all configured MCP workflows. All 29 relocated-package stages passed, including 75 Teamcenter MCP checks and 644 runtime checks. The bundle contains 1,507 files; its manifest SHA-256 is `271741ef48fac28f7fabcdee1e1b5c34b4a570c21c7250a79264cec453fa82ff`. Exact hashes and relocation checks establish package integrity, not publisher signing.

Final results and exact post-freeze documentation changes are recorded in `artifacts/teamcenter-review-01/verification.json`. All 106 missing historical evidence bindings remain unchanged. Implementation, fixtures and tests match the frozen source; subsequent edits only document these results.

## Remaining work

Next: repair the observed safety-harness file reads, then continue AI-03 authenticated Add-In ownership/transport. Native Teamcenter connection/SSO, search, cache download/open, checkout/checkin/cancel, save/new item/revision/proxy objects, independent outcome reconciliation and combined Project Server workflows remain open under TC-03–10. File-byte verification and native cache completeness require separate evidence; manifest retention does not supply it. The full 44-workstream goal remains active. Exact-head push/CI and clean checkout state are recorded in the integration receipt; account-related CI refusal is not a passed run.
