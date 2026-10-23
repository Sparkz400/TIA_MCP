# Diagnostic report exports — 17 October 2026

## Change and limits

`diagnostic_report_export(reportId, format)` presents retained diagnostic analysis
and before/after comparison reports as exact JSON, Markdown or standalone HTML.
It uses the existing hash-bound diagnostic store and paged `artifact_read`
download. JSON preserves the original bytes and ID. Readable exports contain
result tables, acquisition/quality context, source lineage, explicit evidence
limits and the complete original JSON. Missing legacy acquisition context is
shown as missing. The receipt explicitly records no native qualification,
source authentication or repeated analysis. No Siemens software is invoked.

Malformed report claims and IDs, missing/tampered sources and cached output,
invalid formats and output above 8 MiB refuse. Oversized readable output can
still be obtained as exact JSON. Existing shared retention/link/lock checks
remain in effect. Exporting reports does not prove a cause or fixed fault.

HTML escapes source content and includes a restrictive content security policy.
Markdown escapes syntax and encodes URL punctuation to avoid GFM autolinks.
A browser regression caught unintended links in escaped source text; the final
renderer keeps supplied URLs, email addresses and markup literal. No new
production dependency is required. Browser qualification uses installed
Playwright and Marked as development tools.

## Verification

The managed regression group covers 76 checks across analysis and comparison,
three formats, exact bytes/hashes, hostile Unicode, unknown numeric evidence,
legacy contexts, tampering, invalid claims and capacity refusal. All 34 diagnostic
groups pass. The public MCP workflow covers nine exports, exact paged download,
receipt schema refusals and stable receipts across restart: 65 steps, five clean
Host lifetimes. Synthetic supplied reports establish portable behavior only.

`node scripts/qualify_diagnostic_report_browser.mjs <MCP result.json> <fresh output>`
checks the actual exported artifacts at 1440x1000 and 390x844, including keyboard
source disclosure, internal table scrolling, complete exact source JSON,
HTML/Markdown injection, GFM rendering and zero remote requests or dialogs.
Screenshots are inspected separately from assertions.

Final frozen Git tree: `38cddd98495d8869ef18693587b94a557340fab5` (2047 files).
[Source manifest](../../artifacts/diagnostic-export-01/source-manifest.json) SHA-256:
`c615b6e25a59e0d391d7f9aa93b22ff67f4438046ed570d77a00a4650591e3cf`.
Source ZIP SHA-256: `c0842320344f9ad1b8adbbe8ff99aefa2350bd771c15d41924ffdf5ced273b2b`.

The [initial aggregate](../../artifacts/diagnostic-export-01/source/artifacts/checks/result.json)
passed 1,455 Python tests (one unavailable-evidence skip), 24 Explorer checks,
contracts/source bindings and 28 managed suites, then stopped at one existing
manifest-publication test: expected zero retry delays, observed one. This failed
receipt is retained, not relabeled as a pass. The unchanged executable then
passed all 226 general checks. An isolated diagnostic probe repeated all ten
manifest drift/interruption cases 100 times against the same Core assembly with
no recurrence ([log](../../artifacts/diagnostic-export-01/manifest-probe.log)).
The cause is unconfirmed; no production or test behavior was changed to mask it.

The fresh [managed/MCP recheck](../../artifacts/diagnostic-export-01/source/artifacts/managed-recheck/result.json)
passes all 41 managed suites and offline MCP workflows against the same frozen
source. Together these receipts cover the full offline scope; the first all-in-one
run remains failed.
The general executable passes 226 checks; diagnostics passes 34 groups.
The frozen [diagnostic MCP workflow](../../artifacts/diagnostic-export-01/source/artifacts/managed-recheck/diagnostic-mcp/result.json)
passes 65 steps across five clean Host lifetimes.
Run: 2026-10-17T16:51:41.2701070Z to 2026-10-17T17:01:09.7379720Z.
Initial aggregate receipt SHA-256: `44e6524cb2433f2cf48af682add1099d4524b6104b815b73822feaf28425fdf6`.
Managed recheck receipt SHA-256: `4c4aff882c65a58bb9471246a7d2585dcd21bb6b8a3c4ea51a62cc2cb7743fea`.
[Initial console](../../artifacts/diagnostic-export-01/aggregate-console.log),
[managed recheck console](../../artifacts/diagnostic-export-01/managed-recheck-console.log).

All 24 [package stages](../../artifacts/diagnostic-export-01/source/artifacts/package-checks/result.json)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_163757_2e8e3cbe` contains 1447 manifest files.
Package receipt SHA-256: `14eb506eaeec0d73ec97f610286b73c99db2ec699cd09c10109cfd0bbd219e3d`.
Manifest SHA-256: `6677f93895bdb586e8526e6d99c1ec1baceb3454ea8ecb294aa731ba404f0205`.
ZIP SHA-256: `35c9c438557b4fee90130be487f60c2353919bd9baefc0d2af942af8fbfe8ca3`.

The [source comparison](../../artifacts/diagnostic-export-01/final-source-comparison.json)
verifies unchanged tested implementation; only five completion documents differ.
All 106 unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/diagnostic-export-01/integration.json)
records Git/CI and cleanup state. The [browser receipt](../../artifacts/diagnostic-export-01/browser/result.json)
passes 10 checks against the frozen relocated package's actual MCP exports: six HTML
viewport renders and three GFM renders, plus zero page errors, remote requests
or dialogs. The final mobile and desktop screenshots were inspected. Browser
checks establish only those recorded sizes and interactions, not physical
assistive-technology or native product acceptance.


## Remaining scope

This fulfills the report-format increment in TD-09, not the full D17/C04 or
44-workstream goal. Native acquisition, authenticated program/clock/source
identity, complete diagnostic orchestration and Siemens acceptance remain open.
Historical missing evidence is not replaced by synthetic output. GitHub hosted
CI must be reported for the exact integrated commit; local passes are separate.
