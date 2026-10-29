# Retained composite engineering workflow — 18 October 2026

This increment delivers five offline MCP routes for C02: retain a supplied
combined review, reopen it, page its evidence, download exact artifacts and
discover complete or interrupted publications. It does not complete C02, C03
or the original [44-workstream scope](../history/offline-scope-reconciliation-20261017.md).

## Behavior and implementation

`CompositeEngineeringWorkflowStore` retains exact request/report bytes and a
canonical receipt beneath `composite-engineering-reviews`. A public handle
binds the record ID and receipt digest. Every reopen, page and download checks
the intent, receipt and artifact hashes, independently reopens each original
dependency through the existing evaluator, and compares the rebuilt report.
Self-consistent replacement hashes cannot authorize an invented result.

Publication flushes intent, request and report, rechecks dependencies, then
flushes a final receipt. Failed or interrupted files remain discoverable.
The store bounds bytes, records and filesystem entries, including abandoned
content. An existing exact handle remains readable when unrelated content
exhausts the global write quota. No implicit resume, retry, repair or deletion
is added.

The Host routes use this store directly without a native bridge. The strict
[workflow schema](../../contracts/composite-engineering-workflow.v1.schema.json)
and [reference](../reference/composite-engineering-review.md) define retained
receipts, tool arguments, all ten page sections, explicit oversized-row
omissions and exact byte transfers. The catalog now contains 227 tools,
56 mutation kinds and 301 features: 131 implemented, 160 partial and
10 not implemented. All five new features remain partial/unverified;
native and complete-impact flags remain false.

## Evidence

The retained local evidence root is `artifacts/composite-workflow-01/`.
The final focused managed run (`focused-02`) passes 44 evaluator and
177 workflow checks. Four owned child processes are actually terminated after
flushed intent, request, report and receipt writes. Their observed exit codes
and remaining files distinguish incomplete publication from a committed
record whose reply was lost. An independent Python verifier checks the exact
remaining file sets, receipts, lengths and hashes. These are process-exit
tests, not power-loss or real disk-full tests.

The actual MCP harness imports exact saved review, AML and component inputs
through public tools; no prebuilt composite store is copied into the Host.
It checks restart after upload release, exact request whitespace/Unicode,
all paged arrays, three download artifacts, changed original AML/component
input/report refusal, public incomplete-record discovery and oversized
metadata download. Its first attempt (`mcp-01`) failed because the harness
used the wrong existing AML argument name; correcting it to `xmlUploadRef`
made the next run pass. The failure remains retained.

Two snapshots contain the same 2,189 source files, based on
`94333cc6843fcdc2d295f8a8bc964a4cd34b7351` plus this uncommitted increment:

| Source manifest | SHA-256 |
| --- | --- |
| `aggregate-source-manifest.json` | `9e77372d5d40eb458ccb769377fd46e70f72eae99b3fb2c349206e94248e60ea` |
| `package-source-manifest.json` | `a7c2c3f145277ece09dae7ae623f11c527b1cbcf08be158b6c66c7e92bc5d853` |

The complete aggregate passes all 44 managed suites (242 general checks),
1,489 Python tests with one skip, the Explorer model/client unit tests and all
configured Host workflows. All 31 package/relocation stages pass. Both runs
pass 44 evaluator, 177 retention/recovery and 49 actual MCP checks. Each managed
fixture records four actual publisher interruptions; each Host harness records
two clean lifetimes with no native workers.

The final package contains 1,537 files. Its manifest SHA-256 is
`fa67c15b35f2280621eee6075cc9acef35da57d1531bcbd51d6710e554a227c0`. Runtime checks total
644 in the aggregate and 645 in the package;
the evidence verifier checks the fixed scenario set and reconciles variable
poll/file-count checks against actual retained files.

`verification.json` and `record-validation.py` record exact source manifests,
results, post-freeze documentation changes and preserved historical evidence.
Production code, schemas, tests and configuration are unchanged after the
freeze; subsequent edits only finalize documentation. No final-source browser
rerun or native qualification is claimed. `integration.json` records the pushed
commit, exact-head hosted CI results and final Git/worktree state. Hosted CI
execution remains a separate gate from these local results.

## Remaining work

C02 still needs automatic supported saved/component consumer expansion and
typed telegram/encoder semantics. C03 diagnostic/runtime handoff and the
remaining original workstreams stay open. Native exchange/rebind, preservation,
compile/reopen/recovery, physical communication/motion, source authenticity
and live freshness are not certified. No actual browser rerun was needed for
this non-UI increment. Real disk-full, power-loss durability and sustained
maximum-size performance remain untested.

The source binding update preserves all 106 unavailable historical evidence
inputs with their original metadata and hashes; absence is not a pass.
The immutable 44-workstream reconciliation files remain unchanged.
