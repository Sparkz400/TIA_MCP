# OPC UA polling capture qualification - 21 October 2026

Bounded read-only UA polling capture is delivered through five tools in the existing UA Host registration. The implementation reuses the actual RuntimeCaptureStore writer, immutable segment chain, atomic head publication, quotas, page/export code and completion reservation cleanup. An optional UA context is omitted from all previous Web plans. UA records occupy a separate bounded store and carry explicit UA status/page/export schemas.

The [workflow](../reference/opcua-runtime-capture.md) specifies exact node selection, decoded value/status/quality/time retention, sequential polling and skipped-slot gaps. Stop, duration expiry, Host shutdown and errors retain distinct terminal reasons; no automatic reconnect or replay occurs. Host disposal finishes acquisition before session cleanup. A new Host reopens only the original committed prefix; remote session cleanup remains unknown until separately established.

## Focused final evidence

| Qualification | Final result | Receipt |
| --- | --- | --- |
| Actual encrypted loopback SDK server, managed UA suite | 454 checks; includes capture extrema, quality, missing times, unsupported forms, gaps, bounded stop, actual publication collision, graceful disposal, tamper refusal and rehashed promoted-flag refusal | `artifacts/ua-capture-managed-04/result.json` |
| Actual Host/DPAPI UA capture, complete JSON/CSV transfer through artifact_read, original-process crash and restart | 160 checks; 12 independent altered-evidence controls; original bytes unchanged, no reopen/repeated-start traffic, actual synthetic-server expiration of the crashed session | `artifacts/ua-capture-host-05/result.json` |
| Existing unchanged UA Host session harness | 65 checks; normal protected credentials, retirement, lifecycle and original process cleanup still pass | `artifacts/ua-session-host-02/result.json` |
| Actual prior Web capture producer records | 62 checks across two Host lifetimes; exact status/page/export output and original retained bytes remain unchanged | `artifacts/ua-web-compatibility-03/verification.json` |
| Existing PhysicalRuntime managed regression | 350 checks total, including 43 completion-failure checks and 71 pressure checks | `artifacts/ua-capture-physical-final.json`; no native or authenticated session execution |

Final Host and managed builds have zero warnings and errors. The Host capture receipt binds loaded binary/source hashes before and after execution and records the original owned Host/server process handles and exit results. Its intentional Host crash is explicitly marked; the later Host and fixture server exit normally. A crash exit is not described as clean logout. The lane summary binds each original receipt SHA256 in `artifacts/ua-capture-lane-final.json`.

## Retained development outcomes

The first Host capture harness reached its no-replay/reopen checks but then tried to hash the exclusive owner.lock while the new Host held it. That failed report remains under `ua-capture-host-01`; the corrected evidence comparison excludes the non-evidence lock file. Managed `ua-capture-managed-02` failed when completion of a prior intentionally cancelled server read contaminated the next session's global no-replay audit. The fixture now exposes its active-read count, and the test waits for the original in-flight request's actual completion before the next scenario. The original failed report/audit is retained. These are harness corrections, not suppressed failures or production retries.

Review also corrected cancellation terminal-reason precedence, preserving durationLimit when a read crosses the capture deadline, and required exact normalized UA plan comparison so rehashed computed qualification flags cannot be silently accepted. Final controls cover both changes. Independent review also reproduced acceptance of impossible decoded scalar/encoding pairs; the final validator now enforces canonical integer ranges, finite roundtrip float/double tokens, UTC O-format scalar dates, D-format GUIDs and base64. Ten altered scalar cases are rejected. The reviewer's unchanged original nondecimal-Int64 and Boolean-as-Int64 probe rejects both mutations against the repaired Core. Declared custom or abstract DataType identity remains exact evidence without an invented subtype equivalence.

## Remaining boundaries

This is polling acquisition only. Monitored items, revised subscription settings, Publish/Republish sequence and overflow semantics, reconnect/transfer, and subscription capture remain separate unimplemented contracts requiring dedicated qualification. Independent online program/address/instance association and diagnostic behavioral assertions remain unqualified. No native TIA dispatch, physical endpoint, disk-full action, system-wide pressure test or production write was performed.

The UA backend has its own four-worker limit and 512 MiB capture-store quota, alongside the separate existing Web backend. These are per-backend bounds, not a combined Host budget. The new tool surface does not admit a caller-supplied UA plan through the existing Web start tool. Combined frozen aggregate, package/relocation, catalog reconciliation and release acceptance belong to the coordinator.
