# Web runtime profile verification — 17 October 2026

Baseline: `e14614c68e466f233c00010a839f2a952d73e464`. This increment extends the actual owned Web session flow with advertised API version, explicit quantity-field profile, authentication-mode selection and session identity/policy validation. New statuses use v2; v1 records remain readable unchanged. Full RT-02, RT-03, D16 and the 44-workstream goal remain open.

## Behavior and focused evidence

Host builds pass with zero warnings/errors. Final focused `mcp-03/verification.json` passes 395 checks using actual loopback TLS, current-user DPAPI and Host processes. Checks include malformed discovery, unsupported authentication mode, documented Anonymous exception, explicit static/disabled/UMC modes, wrong returned user/mode, missing identity, changed/denied/secret-echoed session observations, login policy, pre-dispatch request bounds, ownership after reply loss, idle/shutdown cleanup and actual restart. New data requests stop after failed identity revalidation; cleanup remains limited to this Host's token. Polling-dependent check counts can vary between runs.

The actual v1 compatibility fixture is copied byte-for-byte from the previous increment's `mcp-07` closed-session record, SHA-256 `ec0a8309144fa84745af5769b003223745a0180806ca844f02a5edbbe445eedd`. Provenance is retained in `artifacts/runtime-profile-01/legacy-fixture.json`. A separately authored connected-v1 fixture tests restart/unknown-state handling. Neither is a physical-controller transcript.

## Frozen aggregate and final package

All receipts below are under `artifacts/runtime-profile-01`. The initial frozen source manifest (`source-manifest.json`, SHA-256 `c331657723b404225a54223de0686b14ba3fad2684a5de32ff28c6f2fad0e24c`) passes all 43 managed suites, 1,459 Python tests with one unavailable-native-observation skip, 24 Explorer model/client checks and every configured MCP workflow. Its runtime flow passes 389 checks. Terminal receipt: `source/artifacts/aggregate/result.json`; console output is retained beside it.

A subsequent review identified oversized numeric API-version retention. The negative control `mcp-version-negative` fails on the earlier binary with `RuntimeRecordLimit`; the corrected client refuses a numeric representation longer than 64 characters before login. `snapshot-comparison.json` proves this is the only subsequent production change; the harness, bindings and documentation also changed. This is aggregate evidence plus an independently reproduced regression and final package checks, not an exact-final-source rerun of every aggregate suite.

The corrected source (`source-02-manifest.json`, SHA-256 `8d2ed899ef3e7fc8414511021082b9eacbefa5ed8dfb00ba396f34b19c21efb1`) passes all 26 portable package stages, including 397 runtime checks after relocation. Terminal receipts are `source-02/artifacts/package-checks/result.json` and its `runtime-session-mcp/verification.json`. The final bundle has 1,469 files and manifest SHA-256 `e76e61b2785d7595109f6333c37b99eb99c0fcb43e6ff65fae9d011400924fba`.

The first package attempt failed in the runtime harness because a loopback transport request failed during discovery, before login. The retained record reports `notConnected` / `RuntimeTransportFailure`; it is not a policy-parser failure. An unchanged-binary rerun (`package-runtime-recheck`) passes 387 checks. The original failure is retained and its underlying transport cause remains unconfirmed; no automatic request/login retries were added. The later full corrected package passes independently.

## Coverage and limits

Contract/binding validation passes at 210 tools, 56 mutation kinds and 284 catalog entries. All 106 unavailable historical evidence entries remain identical to baseline; none is promoted. Final documentation-only changes after the corrected snapshot are recorded in `final-source-comparison.json`. Integration and exact hosted-CI outcomes are retained in `integration.json` after push. Prior hosted runs were refused before steps for account billing/spending limits; local passes do not establish hosted execution.

Current manual contracts and the Siemens-owned quantity model are linked in the [workflow](../reference/physical-runtime-sessions.md). Context7 returned unrelated libraries; official Siemens sources were inspected directly. The contradictory quantity example without `max` remains unsupported rather than silently aliased. API version is not mapped to firmware or qualification. No live controller, TIA, PLCSIM, browser rendering or full native engineering-workstation `Test.ps1` suite was run for this increment. Remaining RT-02 device/profile/stress coverage, RT-03 capture/reopen/export, UA, diagnostics, program association and gated controls remain open.
