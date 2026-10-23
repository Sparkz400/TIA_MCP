# Managed library scale measurements — 23 October 2026

The component planner now indexes native types, version-edge targets and existing
object names instead of repeatedly scanning their arrays. Type/version pairing,
case-insensitive name collisions, exact dependency closure and refusal gates remain
enforced. No apply or native admission gate changed.

The retained baseline and optimized runs used the same generated inputs on this
Windows host with .NET 10.0.13. All 102 serialized plans are byte-for-byte identical.
These are individual observed timings, not cross-machine performance guarantees.

| Planner input | Baseline time | Indexed time | Baseline maximum allocation per plan | Indexed maximum allocation per plan |
| --- | ---: | ---: | ---: | ---: |
| 10,000 objects, 1 version, 1 plan | 11.05 ms | 8.06 ms | 845,592 B | 1,519,152 B |
| 10,000 objects, 1,000 versions, 1 plan | 1,594.26 ms | 30.58 ms | 101,689,616 B | 6,429,048 B |
| 10,000 objects, 10 versions, 100 separate plans | 1,439.28 ms total | 524.34 ms total | 665,720 B | 1,567,312 B |

The name index adds allocation for small dependency sets; it removes repeated
inventory scans for larger ones. Reported allocation is cumulative managed
allocation, not resident memory. Peak working set is recorded separately per
process and includes setup and preceding cases. Serialized sizes are retained for
every plan. Inputs remain unchanged and the 1,001-version case refuses.

The real managed reachability implementation traversed a generated 1,000-node
chain within 10,000 known objects in 8.82 ms, allocating 4,029,632 bytes. Its exact
1,000-path result serializes to 21,001 bytes. Incomplete/truncated evidence,
an isolated cycle used as a required reference, 50,001 objects and 200,001 edges
all refuse.

The real package store staged 10,000 generated source candidates across three ZIP
levels in 22.23 seconds. The input was 24,020 compressed bytes and extraction
accounted for 11,927,971 bytes across 10,003 files. The retained manifest is
5,411,415 bytes. This is a high-entry-count, compressible fixture; it does not
exercise the maximum byte limit with a multi-gigabyte distribution.

All 10,000 candidates were read uniquely across 50 pages in 2.06 seconds, with
699,688,600 bytes cumulatively allocated. Each page currently reparses the complete
manifest; reducing that cost while preserving freshness checks remains open.
Retention and refusal of release took 33.75 ms; explicit unretain/release took
4.29 seconds. Source bytes stayed unchanged. Page-size, retained-release,
package-count, stored-byte, nesting-depth and entry-count refusals passed. Release
freed the package-count quota for a new package. Failed intake state is retained
and independently audited; no partial inventory became ready.

Evidence is under `artifacts/library-scale-20261023/`, pinned by `verification.json`:

- `planner-baseline-01/` and `planner-optimized-01/`: raw timing/allocation samples,
  input hashes and all exact plan outputs; the original planner source is retained.
- `managed-01/`: graph result, generated archives, retained package manifest and
  package refusal state. The successfully released large staging directory is
  intentionally absent; its manifest and original input remain.
- `planner-tests.log`: 268 passing component checks, including five added closure,
  malformed-identity and collision regressions.
- `library-tests.log`: 75 passing package/reachability checks.
- The isolated V21 .NET Framework 4.8 worker snapshot
  `artifacts/hmi-worker-build/c34d15d15018455296a8536ebced5b6c/` built successfully
  with zero warnings/errors. It was not launched.
- Contract and current source-binding checks pass without qualification promotion.

Native library enumeration, native checkpoint cost, realistic vendor packages,
combined 100-component apply and native runtime qualification remain open. The
100-plan measurement executes separate existing single-component plans. The full
aggregate and portable package were not rebuilt for this increment; the earlier
frozen release excludes it. The current native dispatch ticket remains unresolved.
