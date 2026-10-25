# Read-only UA event capture — 25 October 2026

## Scope and boundary

The existing bounded subscription now accepts an explicit event notifier and
minimum severity, alone or beside selected signals. It retains eight standard
BaseEventType fields, exact event IDs, localized messages, original clocks,
null/status field outcomes, queue overflow, duplicates and recovery evidence.
Exact JSON/CSV and escaped HTML/Markdown timelines reuse the original immutable
capture chain. Prebound diagnostic reports and portable Explorer expose the
retained mixed evidence without promoting a behavioral verdict.

No new dependency, mutation tool, condition refresh or active-alarm inventory was
added. Native TIA, physical-server behavior, running-program association, clock
synchronization and complete delivery remain unqualified.

## Checks

Evidence root: `artifacts/opcua-events-20261025` in the primary checkout.

- `events-null-final/result.json`: 34 focused managed checks passed against an actual
  encrypted independent loopback server. Covers severity filtering, exact IDs,
  localized hostile content, separate clocks, mixed signals, malformed handles /
  types / field counts / mandatory IDs, unavailable fields, denied monitoring,
  non-notifier rejection, actual server queue overflow, duplicate envelopes,
  repeated IDs, lost-response same-session recovery and original Republish.
- Rehashed retained-history controls refuse invented overflow, changed event item
  association, missing identity, invalid severity encoding and reordered fields.
- `managed-1/result.json`: 927 managed UA checks passed; the final focused rerun
  also verifies queue overflow bypassing a severity-1000 filter.
- `host-2/result.json`: 493 actual Host/loopback checks passed.
- `diagnostics-host-2/result.json`: 1,049 diagnostic Host checks passed;
  `browser-final/result.json`: 159 browser/model checks passed, with the event timeline
  visually inspected. All 46 diagnostic managed groups, 26 Explorer model/client
  tests, 13 subscription schema tests, three observation schema tests and the
  generated-asset packaging check passed. Source bindings validate with historical
  unavailable native evidence still reported.
- The existing aggregate Host qualification now includes real protected Windows
  credentials, exact artifact downloads, event filter inspection, secret-echo
  refusal and a second Host reading original event bytes without acquisition
  replay. The secret-echo fixture intentionally invalidates its session; the
  later credential-retirement test starts a fresh session.
- Diagnostic qualification retains repeated events beside original bound signals,
  revalidates original bytes, exports inert reports and reopens them in another
  Host. Browser checks exercise actual supplied reports at desktop/mobile widths.

Reproduce through `Test-OfflinePackage.ps1` (full mode), which includes the managed
UA suite, actual Host subscription and diagnostic workflows, independent schema /
service checks and browser checks against relocated package binaries. The focused
suite is available as `TiaMcp.OpcUa.Runtime.Tests --events-only --output <fresh>`.

## Preserved investigation

`events-2` retained an incorrect test expectation that CSV would contain literal
angle brackets; its complete JSON cell correctly escaped them. `host-1` retained
a test-order failure after the deliberate secret echo invalidated the session;
secret rejection and cleanup worked, and the retirement test needed a new owned
session. `diagnostics-host-1` similarly put a healthy event scenario after a
session-invalidating denial; that scenario now precedes the denial. `browser-1`
used the stale generated viewer, so the checked-in portable reader was rebuilt.
`browser-2` passed behavioral checks but its event screenshot was blank after a
mobile scroll; `browser-3` adds an actual viewport check and a verified visible
capture. The npm shell could not resolve Node; the same 26 tests passed using the
available Node executable directly. These failures were not reclassified as
passing evidence.

## Protocol evidence

The implementation uses the existing pinned OPC Foundation .NET Standard
1.5.378.182 SDK and its actual server fixture. The official
[EventFilter specification](https://reference.opcfoundation.org/specs/OPC-10000-4/7.22.3)
defines select-clause order, null/status fields, mandatory event identity and
receive time, and overflow bypassing the filter. The
[BaseEventType specification](https://reference.opcfoundation.org/specs/OPC-10000-5/6.4.2)
defines the selected standard fields. Context7 CLI was unavailable on this
machine; official specification and pinned SDK source were consulted directly.

The first full package run was intentionally stopped before completion when
source review identified localized null message text and explicit-null event
selection boundaries. Its process identity and terminal descendants are retained
in `package-source-review-interruption.json`; it is not a passing package run.
Final source checks explicitly preserve null localized text and normalize an
explicit `events: null` to the existing signal-only request.

The first PR run failed all Windows jobs during SDK installation, before build:
`setup-dotnet` resolved the global `latestFeature` policy to 10.0.403 and its SDK
URL returned HTTP 409. CI now installs explicit SDK 10.0.402, the version used by
the local build and listed in Microsoft's current
[10.0 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
The exact published Windows SDK URL was independently reachable. The local SDK
selection policy and application sources are unchanged. This workflow-only repair
requires new checks on the resulting PR head; the earlier installer failures are
retained as failures.

After the .NET 10 installer succeeded, managed CI encountered the same unavailable
floating redirect for .NET 8.0.427. Its auxiliary SDK is now pinned to the
published 8.0.426; official release metadata and its downloadable Windows archive
were also checked. All validation jobs remain enabled.

A subsequent managed shard failed in the unchanged UA session MCP qualification,
whose detailed `result.json` was missing from the CI upload patterns. The same
qualification passed locally (124 checks, `session-host/result.json`). CI now
retains MCP `result.json` beside `verification.json` so a repeated failure has its
original diagnostic evidence. No cause is inferred from that unobserved detail;
the failed run is preserved under `ci-managed-0`.
