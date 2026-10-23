# Encoder qualification process lifetime — 17 October 2026

The legacy encoder harness could publish `completed: true` after false detach
receipts and an unclean or forcibly stopped Host. An injected reproduction retained
three false detach receipts and Host exit code -1 while reporting completion:
[original probe](../../artifacts/encoder-cleanup-before.log).

The producer and read-only saved-copy verifier now reuse the existing
`ComponentOwnedScratch` exact lifetime controller. Each reopen waits for the
original Host, worker and native process handles to prove clean terminal exit.
The shared controller also refuses an explicit forced-Host-stop receipt before
another session starts. Completion is published only after the final cleanup and
independent receipt checks; discovery alone remains incomplete qualification.

The seeder uses the existing original CreateProcess handle controller. Timeout,
cancellation, partial construction and identity-capture failure retain the same
process until terminal exit. They cannot authorize replay, recovery commands or
early parent teardown. Raw output and unique authorization files remain retained.
Unknown constructor outcomes without an exact handle stay held; they do not
silently become proof that no process exists.

The verifier requires hash-bound exact source lifecycle receipts, rather than
opening a potentially reused PID. Its independent auditor checks matching startup
and terminal identities, worker birth receipts, all original handles and helper
lifetimes. The known same-user/same-host project-lock rejection may be retried
within the existing bounded policy only after an immediate exact empty owned
session observation. Failed attempts remain in the audit; unknown failures refuse.
Legacy XML-only audits remain available with `exactLifecycleVerified: false`;
legacy reports cannot authorize a current native companion session.

## Portable regression evidence

Sixteen new regression methods exercise actual controller orchestration with fake
Host/worker/native processes and helper handles. They cover successful three-session
qualification, final/intermediate cleanup failures, forced shutdown, false detach,
missing worker receipts, lost start/apply replies, helper deadline/construction/
capture/output failures, immutable reports, explicit empty configuration, discovery,
tampered lifecycle receipts, two-session read-only verification, exact known lock
retries and independent rejection of altered owner/empty-session/failure evidence.

[Focused output](../../artifacts/encoder-lifecycle-tests-06.log).
Existing component, encoder XML/parameter and shared preservation checks remain in
the aggregate. The optimized-mode regression runs these checks under both `-O`
and `-OO` so cleanup and preservation requirements cannot disappear.

Frozen Git tree: `c42e8a3ffbaed1dc595fee06f0d375dbdfb6f92e` (1981 files).
[Source manifest](../../artifacts/encoder-life-01/source-manifest.json) SHA-256:
`c8c5caadfb1f1e86ced62b116df1a6f963ddf663c339b3dadb1f53c788cf3e7c`.
Source ZIP SHA-256: `8673937aaa9a6318ab88c3e2d6b7c1bb4c4389a2dbdbe64c648a990b9ad73e22`.

The [aggregate](../../artifacts/encoder-life-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,334 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The unchanged general executable passes all 202 checks. The Python aggregate
includes 55 focused regression cases under each of `-O` and `-OO` through its
optimization subprocess test; child cases are not added to the parent test count.
Run: 2026-10-17T08:12:41.9680254Z to 2026-10-17T08:23:07.0665761Z.
Receipt SHA-256: `2826c7a41c95181acd9fb8d10f80f690f6742d0c5c4a6ccc90c19fe4e713148a`.
[Console evidence](../../artifacts/encoder-life-01/aggregate-console.log).

All 22 [package stages](../../artifacts/encoder-life-01/source/artifacts/package-checks/result.json)
pass, including relocation. Bundle `tia-mcp-offline_20261017_081306_4b4f9a66` contains 1399
manifest files. Receipt SHA-256: `c68e094c10bce9cd0e109a8cac3d0f3c139f3abd68c3800933571375f77a1437`.
Manifest SHA-256: `2a6f675f2054132a69a748c433c42357e7ab58f31301a38ef3a1989f66746b3d`.
ZIP SHA-256: `8afde3c30fbc6d792eb02767ba1911d5053d868a4ad7f0fee9f84b4c07d82a6b`.
This offline package excludes native workers; it does not compile the Siemens SDK.
Hashes establish retained byte integrity, not publisher or native authenticity.

The [source comparison](../../artifacts/encoder-life-01/final-source-comparison.json)
allows only completion-documentation changes after testing and verifies all 106
unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/encoder-life-01/integration.json) records the
pushed commit, hosted CI results and cleanup.

## Boundaries and remaining work

No Siemens executable, SDK, TIA project, simulator or PLC ran. The new orchestration
tests deliberately stub endpoint correspondence to isolate process lifecycle; they
do not prove native hardware relationships or equivalence across recovered copies.
Compile-completeness and recovery-copy endpoint correspondence remain open, as do
native qualification, remaining TO-03–12 work and the full offline execution queue.
No historical native evidence or unavailable binding hash is rewritten.

This repair does not enable a production mutation profile. Genuine SDK build,
native helper/controller behavior, complete preservation and hardware/runtime
acceptance still need the appropriate Siemens environment.
