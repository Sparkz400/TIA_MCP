# Composite consumer context and retained-v1 compatibility — 18 October 2026

This increment extends C02 with versioned, bounded consumer context. It does
not complete C02, C03 or the immutable
[44-workstream scope](../history/offline-scope-reconciliation-20261017.md).

## Behavior

Explicit AML mapping destinations seed separate before/after context. Captured
hardware ancestry, supplied connection consumers and same-PLC technology-object
relationships/consumers expand that context alongside supported original saved
dependencies. Each supplied association retains its original record and row;
saved edges remain a separate evidence category. Unknown identities, ambiguous
nodes, virtual objects, mismatched endpoints and unsupported scope cannot become
candidates. A cross-PLC connection declaration cannot weaken the TO scope guard.

The existing 10,000-edge visit budget covers declared mappings and both consumer
phases. The 128-hop limit includes each retained declared witness depth; it does
not reset at a component boundary. A witness is not a shortest-path certificate.
Per-phase association, candidate, saved-link and saved-path byte bounds remain
explicit, and all added evidence shares the existing 32 MiB report limit.
Truncation and missing declarations remain visible. Candidate membership never
certifies native impact, typed member compatibility or complete impact coverage.

The existing five MCP tools accept the explicit v2 request and expose ten new
consumer page sections. Their verified retained readers still rebuild results
from the original inputs. Existing v1 requests, reports and summaries keep their
original semantics and exact bytes. See the
[reference](../reference/composite-engineering-review.md),
[v2 request](../../contracts/composite-engineering-request.v2.schema.json) and
[v2 report](../../contracts/composite-engineering-review.v2.schema.json).

## Evidence

Local evidence is retained under `artifacts/composite-consumers-01/`. The final
focused run (`focused-05`) passes 44 evaluator, 177 store/recovery and 49 consumer
checks. An independent Python oracle reopens the exact original component rows,
checks saved-edge provenance and independently computes the fixture candidate
sets and depths. The nine contract unit tests also pass.

The first capacity test failed because the existing 1 MiB saved-path byte bound
was reached before the 2,048-row bound. Separate fixtures now exercise both.
That failed run remains retained. Further fixtures cover mixed cycles, phase
separation, duplicate IDs, missing PLC scope, invalid identity text, original
row indices, shorter known routes, total depth/edge limits and a real retained
input set that exceeds the shared output-byte bound.

Final focused public-MCP runs pass 50 v1 checks, 59 v2 checks and 22 prior-producer
compatibility checks. Each harness observes two clean Host lifetimes without
native workers. Oversized rows remain downloadable; changing original retained
inputs refuses subsequent reads.

The [retained v1 corpus](../../fixtures/composite-engineering/retained-v1/README.md)
contains 19 exact files produced by the previous packaged implementation
(`e5ab78b7d4818be46baea9eaca6b64fd8396a951`). The current Host reopens its original
summary, pages all original sections, downloads original artifacts byte for byte
and preserves every file across restart. This fixture was captured from that
actual prior binary, rather than recreated with the current implementation.

Two frozen snapshots contain the same 2,217 files, based on that commit plus
this increment:

| Source manifest | SHA-256 |
| --- | --- |
| `aggregate-source-manifest.json` | `39850839a3c61a09ce3ab721a879cb7d4ede76330da886c93d461ab98a537c3c` |
| `package-source-manifest.json` | `e3cb5fcb7a6cd50b4443674f922c63e9f41fa0ab56b38e56a3a0bf7bd908b6cd` |

The complete aggregate passes all 44 managed suites (242 general checks),
1,492 Python tests with one skip, Explorer model/client checks and all configured
Host workflows. All 32 package/relocation stages pass. Both runs pass 44 evaluator,
177 retention/recovery, 49 consumer, 50 v1 MCP, 59 v2 MCP and 22 actual prior-producer
compatibility checks, with independent source/schema checks. Each managed fixture
includes four actual publisher interruptions. Each set of three composite Host
harnesses records six clean lifetimes without native workers.

The package contains 1,563 files. Its manifest SHA-256 is
`bee0aabc927dd4a5ea4e916ebd07512329225ca6898882c87d78c85578142632`. Runtime checks total
645 in the aggregate and 641
in the package; the evidence verifier compares the fixed scenario set and
reconciles variable polling/file-count checks against retained files.

The first summary-verifier pass rejected a runtime counter difference: the
package observed one fewer idle-session status poll than the older baseline.
The corrected verifier checks every status response against retained observations,
identifies the single one-second idle attempt, verifies its transition to closed
and then compares the remaining fixed scenario counts. No product or test-source
change or rerun was needed for this evidence-accounting correction.

`verification.json` and `record-validation.py` record exact manifests, results,
preserved historical evidence and post-freeze documentation changes. Production
code, schemas, tests and configuration are unchanged after the freeze.
`integration.json` records the pushed commit, exact-head hosted CI observations
and final Git/worktree state. Hosted execution is a separate gate from these
local results.

## Remaining work

Typed telegram/encoder metadata semantics and C03 security/runtime handoff are
next. The original workstream scope remains active. Native exchange/rebind,
preservation, compile/reopen, physical communication/motion, source authenticity
and live freshness require separately qualified evidence. Actual browser,
Siemens SDK/products, physical targets, power-loss and real disk-full execution
are not claimed by this increment.

All 112 historical evidence entries retain their metadata and hashes, including
106 unavailable inputs. The immutable reconciliation files remain unchanged.
