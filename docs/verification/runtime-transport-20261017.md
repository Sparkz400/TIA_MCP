# Runtime transport reuse regression — 17 October 2026

Baseline: `a2b967637c5e9729dc1f353e25b2199aeaf13de0`. This change fixes the previously intermittent loopback `RuntimeTransportFailure` caused by reusing a closing HTTP connection. It does not qualify a physical controller or close RT-02/03/D16 or the full 44-workstream goal.

## Cause and correction

The request's `Connection: close` header did not enforce local pool lifetime. A discovery-only probe reproduced `ResponseEnded` without credentials, sessions or capture workers. The retained .NET event trace shows connection 1509 receiving the next request without a new connection; the server accepted 1,510 connections and received 1,510 requests, but never received that next request. Separate reproductions failed after 1,254, 1,359 and 1,559 received requests. The server recorded no handler/accept errors.

The client now uses `SocketsHttpHandler` with `PooledConnectionLifetime = TimeSpan.Zero`, preserving the existing per-request close header, deadline, HTTP version, disabled redirect/proxy/cookie/decompression behavior and one-request concurrency. The TLS callback still requires the explicit leaf SHA-256 pin, hostname, validity and server EKU. A disposable public-certificate copy adapts the callback type. No retry, authentication fallback or trust bypass was added.

Microsoft documents connection lifetime on the [handler property](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.pooledconnectionlifetime?view=net-10.0). The [runtime connection implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs) also distinguishes received close headers from connection reuse. Those sources explain the mechanism; the local trace and regression establish the behavior of the installed .NET 10.0.13 runtime. Context7 was queried first; the primary API/source references supplement its general pooling guidance.

## Focused evidence

All artifacts are under `artifacts/runtime-transport-01`. `diagnosis.json` records the original failure and corrected binary hash. The `probe*` sources and outputs are retained there; these are diagnostics, not a new product API.

The deterministic `KeepAliveHandler` fixture deliberately keeps an HTTP/1.1 connection open despite the client's close header. `mcp-reuse-negative` fails on the previous binary because multiple discovery/login/read/logout RPCs use the same connection. The fixture's assertion is part of `scripts/test_runtime_session_host.py`, so the existing offline/package runners execute it automatically.

The corrected `mcp-01` run passes 523 checks, including the new connection-identity assertion and the existing TLS, certificate, credential, policy, capture, restart and cleanup checks. `fixed-stress` passes 2,000 discovery cycles: 8,000 requests on 8,000 distinct .NET connection IDs, zero reuse and zero server errors. No production diagnostic logging remains. All tests use loopback TLS and synthetic credentials; no Siemens product or physical controller was used.

## Final checks and remaining scope

The frozen source has 2,096 files; manifest SHA-256 `b14c625378be5373de9f5128a1105be66ffd88c53901199b1876300dc3cb45d8`. All 26 portable-package stages pass, including 523 runtime checks after relocation. Terminal receipt: `source/artifacts/package-checks/result.json`. Bundle manifest SHA-256: `322c7934a17aca132874bf1f2155fb02735f975ef3c1992c6f5ed1801b80bf2a`.

The focused managed protocol/capture suite passes 202 checks (the concurrency observation count varies), and its independent Python schema/value/time cross-check passes. Catalog/source bindings validate at 215 tools, 56 mutation kinds and 289 features; all 106 unavailable historical evidence inputs remain unchanged. The full unrelated aggregate and browser suites were not rerun for this transport-only correction. Final documentation changes after the package snapshot are recorded in `final-source-comparison.json`; no production code changed. Exact push/hosted-CI outcomes are recorded in `integration.json`. Historical failed runs remain preserved; this fix does not rewrite their outcomes. Physical CPU/firmware and certificate-lifecycle qualification, remaining RT-03 review association/storage faults/load/diagnostic assertions, and the other open workstreams remain required. Hosted CI still requires the account billing/spending issue to be resolved; exact integration outcomes are recorded separately.
