# Package pagination allocation repair — 23 October 2026

Package inspection now retains at most one parsed manifest whose original encoding
is no larger than 8 MiB. Every request still opens the current file, enforces the
64 MiB read limit, and compares every byte before reusing its parsed contents.
Timestamps and file size alone never authorize reuse. Package state and identity
are checked on each request. Larger manifests use the original uncached streaming
deserializer. The retained entry is replaced on a miss, with access serialized
across stores in the process.

Only inspection uses this cache. Native candidate resolution, staged-file hash
verification, retention and release continue through their existing paths. Pages
copy their output array and candidate note arrays so callers cannot modify future
responses. Inspection no longer allocates an array of all section items to return
one page.

## Measured result

The baseline and repaired production code read the same retained 5,411,415-byte
manifest from the preceding real package-store scale run. Each request used a new
`PackageStore`, as the MCP tools do. This measurement creates a metadata-only
fixture; it does not restage the package or claim native validity.

| Reading all 10,000 candidates across 50 pages | Baseline | Repaired |
| --- | ---: | ---: |
| Total request time | 2,012.96 ms | 334.25 ms |
| Cumulative managed allocation | 699,238,424 B | 21,342,480 B |
| Process peak working set, including fixture setup and output retention | 141,705,216 B | 102,965,248 B |

All returned page content is byte-identical after replacing only the differing
fixture manifest location with one fixed marker. The input hash is identical.
These are single observed runs on this host, not timing guarantees. Allocation
is cumulative, not resident memory; the cache deliberately retains one parsed
manifest and its raw bytes. The source-byte cap does not represent a precise
managed-object heap cap.

## Checks and limits

All 91 managed library checks pass, including 16 new compatibility and freshness
cases. They cover changed same-length bytes with restored timestamps, changes in
the first/middle/final comparison chunks, malformed/trailing JSON, missing files,
failed package state, altered identity, candidate-note and page-array mutation,
distinct locations sharing one copied package ID, current page bounds, release,
UTF-8 BOMs, uncached manifests above 8 MiB and refusal above 64 MiB. The existing
staged companion drift case warms inspection before checking native-use refusal.

The raw reports, page outputs, original production source and test logs are retained
under `artifacts/package-pagination-20261023/`. The earlier 10,000-source staging,
retention and quota results remain separate in
[the scale report](library-scale-20261023.md). No native dispatch, qualification
promotion or installation follows from this performance change.
