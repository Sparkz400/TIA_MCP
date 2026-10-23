# UA observation reports — 22 October 2026

This local increment adds `diagnostic_ua_observe` and
`diagnostic_ua_report_export`, complete retained polling/subscription evidence,
readable exports and the portable Explorer view. No Siemens product, native
worker, physical device, publishing or merge is involved.

## Focused evidence

Evidence root: `artifacts/overnight-20261022/observations/`.

- `build-02.log`: Host builds with zero warnings/errors.
- `diagnostics-01.log`: all 46 diagnostic groups pass; the initial 34 report checks
  cover pinned-prefix replay, quality/timestamp gaps, exact UInt64, host errors,
  exports, tamper rejection and unbound compatibility. `diagnostics-02.log`
  additionally covers nullable UInt64: 35 report checks and all 46 groups pass.
- `mcp-04/result.json`: 902 checks pass using the actual Host and an owned encrypted
  loopback server. Polling/subscription reports preserve bad and uncertain
  quality, unsupported arrays and missing timestamps. Separate subscription
  cases retain duplicate envelopes, repaired gaps, overflow, unavailable
  retransmission and partial monitored-item creation failure. Independent server
  audit equality proves report generation/export/restart perform no UA service.
- `prior-replay-01/result.json`: 112 checks pass against separate copies of actual
  previous-increment producer files. Both bound modes reopen without a server;
  every original previous-producer file remains byte-identical.
- `output/playwright/ua-observations-20261022-01/result.json`: 126 browser/model
  checks pass over all seven actual reports, including mobile/desktop layouts,
  exact downloads, inert hostile evidence in HTML/Markdown, derived-summary and
  byte tamper rejection, and a pending UA import not replacing a newer Web import.
  Desktop/mobile screenshots were visually inspected. The final run at
  `output/playwright/ua-observations-20261022-final/result.json` passes 128 checks,
  adding empty-prefix and nullable UInt64 checks with stricter nested-claim
  validation. All runs record zero remote requests, browser errors or dialogs.
- Six focused binding/observation Python contract tests pass. Contract coverage
  reports 261 MCP tools, 56 mutation kinds and 335 feature entries.
- `explorer-unit-02.log`: 29 Explorer JS tests pass; two fixture-dependent tests
  are skipped, as reported by the harness.

The first live qualification mistakenly invoked the Host DLL directly under
Windows and failed before Host initialization; reruns use its application host
EXE. The next run detected an overly generic expected tamper error in the harness;
the backend correctly refused with its capture-specific integrity code. Both
failed logs and cleanup receipts remain retained. The first Explorer unit run
also correctly detected a stale generated bundle; the bundle was regenerated.

## Scope and remaining verification

Reports are original-evidence consistency checks with explicit limits of 10,000
rows, 3 MiB original bytes and 8 MiB serialized output. They do not establish
source authenticity, online program/address/type equivalence, synchronized
samples, causal diagnosis or a fixed fault. Every hypothesis remains unresolved.

The full offline aggregate passes all 45 managed suites, all 1,652 Python tests,
31 Explorer JavaScript checks and every Host workflow. Its UA diagnostic workflow
passes 926 checks. `aggregate-01/result.json` records final status `passed`.
The broad baseline package run passes all 53 stages, including 922 UA diagnostic
checks; `package-01/result.json` records final status `passed`. The baseline bundle
is `artifacts/releases/tia-mcp-offline_20261022_200816_ca8d47fc`, manifest SHA-256
`5fb4479de0eea8e3dc478b3c494c38af14963543c2d4ce48c336b0b60319a6fc`.

## Final viewer boundary and relocated checks

Review found that a zero-segment imported report could invent initial progress.
The viewer now derives the exact empty-prefix progress from the original plan.
`output/playwright/ua-observations-20261022-empty-prefix/result.json` passes 129
checks, including the new refusal, and `explorer-empty-prefix.log` passes all 25
model/client tests. This is a viewer-only change; Host source is unchanged.

The complete 2,799-file baseline source is retained at
`artifacts/deployment-build/339e899fc4e5496f86d3c9009a5050ba/`. The final viewer
source is at `artifacts/deployment-build/22bdf58f9c624d078eab0a976f3f12ed/`.
`ui-source-delta.json` records exactly four changed files: viewer model, bundled
HTML/manifest and browser harness. It confirms no managed source changed.

The final local bundle is
`artifacts/releases/tia-mcp-offline_20261022_202035_fd49bf79`, manifest SHA-256
`fe8360ca2acd7c01c494d488bea923ced3857ba2d2b798b3423593db531f5cce`.
Its separately extracted copy passes strict verification of 2,063 files.
`final-package-mcp/result.json` passes 912 actual Host/loopback checks, including
14 missing-binding report/export refusals. Both original Host lifetimes exit
normally with no workers; independent server cleanup confirms zero sessions.

The exact extracted viewer, model and harness pass 129 checks at
`output/playwright/ua-observations-20261022-packaged/result.json`.
`packaged-browser-inputs.json` verifies their bytes match the final source.
No browser errors, remote requests or dialogs occur. These focused final-bundle
results remain distinct from the completed 53-stage broad baseline package run;
native and physical qualification remain unverified.
