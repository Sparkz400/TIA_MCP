# Retained polling observation analysis — 18 October 2026

Base: `d4bde2a23c85362f3182088fccd0e858a42b5c48`. Evidence root: `artifacts/runtime-analysis-01`.

`runtime_capture_analyze` connects an explicitly associated immutable capture prefix to its exact retained diagnostic case. It reuses existing exact numeric/Boolean predicates, streams committed records, and retains per-read timing, failures, gaps, uncaptured signals and bounded examples. The report counts observed cycle patterns; all behavioral verdicts remain unresolved. Program/address identity, atomicity, synchronization, source authenticity, native qualification and causation remain unverified. Existing JSON/Markdown/HTML report export presents the new report without analysis or network access.

## Executed focused checks

- Host Release build: zero warnings/errors.
- Diagnostic managed suite: 37/37 groups. New checks cover empty pinned prefixes, wrong/missing units, duplicate selection and decreasing-cycle refusal, missing reads/provider failures, raw values, type/range/underflow rejection, UTC reversal retention, exact UInt64 maximum, case removal, source-independent exports, forbidden verdict promotion and deterministic report IDs. A 12,002-read capture verifies streaming beyond 10,000 reads, all 6,001 cycles, 6,000 counted changes, 200 retained changes and 20 examples per hypothesis.
- Physical runtime managed checks: 206 passed; no native execution.
- Actual Host/MCP, disposable loopback TLS, current-user DPAPI and restart: 644 checks passed in `mcp-01`. Analysis and refusals made no network calls; exact case removal prevents reanalysis while retained report exports remain available after restart.
- Capture contract suite: five tests pass, including actual report fixture validation and rejection of promoted claims, omitted per-read timing and supported verdicts.
- Actual HTML/Markdown browser checks: four passed in `report-browser-02`, at 1440×1000 and 390×844; exact JSON/hash, keyboard disclosure, inert hostile text, no page overflow, requests, dialogs or page errors. Both screenshots visually inspected; mobile tables scroll horizontally. Initial `report-browser-01` failed because a test expected capitalized “Sequential reads” while the actual paragraph uses lowercase. The expectation was corrected; product rendering was unchanged.

## Aggregate and integration

Both frozen source copies contain the same 2,110-file map. Their manifest SHA-256 values differ because each records its destination:

- Aggregate: `86a3b5ca4feeec7c9ff1a7959d9edc0942a47c70b465d1876ca4a62e106936db`.
- Package: `9e66ad2c4cf3649817189323d745c3e6f9737a0074e117ce152642826ac9443d`.

All 26 relocated-package stages passed, including 643 runtime MCP checks, 37 diagnostic groups, report export and final strict bundle verification. The bundle contains 1,487 files; manifest SHA-256: `e80d6a3a0998b199b657a859a5ccdf3c688ae8630e6a479c3c7bdd67d7d7d683`. Its original and relocated bytes match. Only documentation was edited after the frozen source copies.

The full frozen aggregate passed all 43 managed suites, 1,464 Python tests (one unavailable-native-evidence skip), 24 Explorer model/client tests and all MCP workflows, including 644 runtime checks. Both runner processes exited successfully. `verification.json` records the results and the documentation-only changes after the snapshot; `integration.json` records the subsequent commit/push and exact hosted-CI results. Hosted CI is not counted as a pass here.

Historical evidence binding entries remain identical to the base commit; all 106 absent inputs stay absent and unqualified. Catalog/bindings now cover 216 tools, 56 mutations and 290 features.

## Limits and remaining work

No Siemens product, SDK, PLC, native trace or physical network was used. No passing/failing behavioral assertion is admitted by sequential polling. Exact units and source mappings remain caller declarations. Raw/nonrepresentable values remain unknown. Reports are bounded by the existing 8 MiB artifact and 256 MiB diagnostic retention limits. Captures retain their separate 64 MiB/100,000-read bounds.

RT-03 remains partial: broader deterministic scheduling/fake-clock, OS disk-full/access-control and extreme-load coverage remain. RT-07 independent identity/association and assertion admission, other runtime packages and the full 44-workstream scope remain open. Historical missing evidence is not regenerated or promoted.
