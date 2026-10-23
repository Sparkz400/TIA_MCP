# Project index store tests and measured scale

`tests/TiaMcp.Deployment.Tests` is an isolated .NET 10 console test project without external package dependencies. It links the production `ProjectIndexStore.cs` helper directly. It does not build or run the MCP host, native workers, TIA or PLCSIM.

From the repository root:

```powershell
dotnet run --project tests/TiaMcp.Deployment.Tests/TiaMcp.Deployment.Tests.csproj -c Release -- artifacts/project-index-store-tests.json
```

The process returns zero only when every assertion passes. It writes a JSON report containing individual results, measured timings, allocation observations, environment information and SHA-256 evidence for the helper source and executed test assembly. Running outside the repository root omits source-file evidence if that relative source path is unavailable.

## Coverage

The suite has 26 cases, including four measured scale runs:

- Immutable captured rows/warnings, isolation from the source arrays, and rejection of mutation through `IList<T>`.
- Exact canonical identities and filters with duplicate names across PLC/software-unit namespaces, case-distinct paths, deep groups, German names, Japanese/CJK text and emoji.
- Coherent ordinal traversal with no missing or duplicate identities, variable page sizes, empty pages, and repeated cursors after query-cache turnover.
- Cursor rejection after changing filters, snapshot or worker lifetime; tampered offsets, digest, signature, malformed Base64 and oversized envelopes.
- Clock-controlled expiration at the exact deadline, oldest-snapshot eviction, explicit invalidation, fresh recapture and idempotent release.
- Invalid rows, duplicate paths, selector/page/lifetime limits, 50,001-row rejection and preservation of an earlier snapshot after capture failure.
- UTF-8 byte accounting at the exact 64 KiB row boundary, the 32 MiB snapshot limit including identity metadata, and the 512 KiB page-payload boundary. A 19-row fixture containing 64 KiB payloads traverses as 8/8/3 rows without gaps.

Query-cache tests use observable requery behavior. They do not expose or mutate the store's private cache dictionary.

## Recorded local measurements

On 8 October 2026, the Release test build completed with zero warnings/errors and all 26 cases passed under .NET 10.0.12, Windows build 26300, x64. The following single-run observations are recorded in `artifacts/project-index-store-tests.json`:

| Generated rows | Accounted snapshot bytes | Capture | Complete traversal | Pages |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 37,860 | 0.06 ms | 0.05 ms | 1 |
| 1,000 | 378,600 | 0.41 ms | 0.26 ms | 5 |
| 10,000 | 3,786,000 | 6.36 ms | 2.11 ms | 50 |
| 50,000 | 18,930,000 | 69.08 ms | 11.05 ms | 250 |

The scale fixtures distribute entries across four PLC paths and seven unit paths, repeat names deliberately, and include Unicode and nested groups. Each run independently verifies the returned identities against the generated source and checks the filtered result count. Source fixture generation is outside the capture timer. Full traversal starts after a first-page query has populated the query view; the report includes the first-page time separately.

These numbers measure the pure cache helper. They do **not** measure TIA project traversal, native serialization, MCP transport, a real 50,000-object engineering project or VM performance. They establish bounded helper behavior and test evidence at the stated sizes; native capture must be qualified separately.

The byte limits count stored row payload/identity data, not all managed-object overhead. The 512 KiB page cap counts row JSON payload and excludes the MCP envelope. Reported allocations are process-wide allocations during capture/traversal rather than retained memory. Working set includes earlier cases. Single measurements, JIT compilation and warm caches affect timings; these values are not performance guarantees or an application memory ceiling.
