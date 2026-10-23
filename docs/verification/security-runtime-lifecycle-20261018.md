# Bound runtime lifecycle — 18 October 2026

Status: full offline aggregate/package verification passed. Integration is recorded
in `artifacts/security-runtime-lifecycle-01/integration.json` when pushed.
Base: `0c22c4dcd5c43b057cc384f5daa21d92c4d47067`. No Siemens/native execution.
The full 44-workstream goal remains active.

The existing local TLS/DPAPI/MCP harness now exercises retained handoffs during
read-response loss, an actual Host kill while a read is held, active-credential
retirement, credential replacement, same-endpoint server-certificate rotation
and account replacement. Shared setup retains the original synthetic target,
program and session observations through the existing public tools.

The required observations are an explicit missing result without stale-value
substitution or request replay; the exact committed prefix after a Host crash;
no adoption of an old session; no RPC after credential retirement; no HTTP
request reaching a peer with a changed pin; and refusal of an old handoff
before new capture intent. Fresh contexts may perform separately authorized
reads, while all target/program/rights/assertion authority flags remain false.
Migration comparisons retain both sides and require fresh evidence. Restoring
the old certificate must not resume a previously ended capture.

Evidence is retained under `artifacts/security-runtime-lifecycle-01/`.
The first focused run uses the exact previously verified packaged Host so a
newly exposed production failure cannot be hidden by rebuilding the baseline.
The final evidence below covers both production repairs and the complete expanded harness.

Focused run 04 passes 1,444 checks with 627 RPCs and zero TLS resumptions.
Run 05 adds retained RPC ranges and harness-source hashes and passes 1,443
checks. Poll/status counts vary with scheduling; both execute the same seven
fixed lifecycle cases. The independent checker passes all seven capture chains,
three original migration comparisons and seven negative controls. Its initial
run incorrectly compared an omitted caller `representation` to the stored
explicit `simple` default; `independent-05-recheck.log` records the correction
without changing any retained fixture or production behavior.

## Reproduced transport classification defect

`runtime-mcp-01/` runs against the exact previous package and fails the new
lost-read-response assertion. The retained segment has one read attempt, a null
result and `RuntimeCaptureReadFailed`; server telemetry records one read RPC.
No value or retry was invented, but the transport cause was lost. The shared
HTTP exchange handled `HttpRequestException` while response-stream failures
could escape as `IOException`. It now maps both to the existing sanitized
`RuntimeTransportFailure` code. This also reaches login, logout, discovery,
permission and session checks through the same exchange method.

The [Microsoft HttpIOException reference](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpioexception?view=net-10.0)
confirms that HTTP response-read errors derive from `IOException`.
Run 02 passes that regression and the bound in-flight crash and credential
retirement/replacement cases, then exposes the separate TLS failure below.

## Reproduced certificate-rotation gap

Runs 02 and 03 continue successful reads after the server loads a replacement
certificate at the same origin. Run 03 independently observes the new leaf pin
in a fresh TLS 1.3 handshake, while the Host's subsequent RPCs report TLS
session reuse. Zero HTTP connection-pool lifetime alone did not force a new
certificate exchange. The shared runtime client now disables TLS resumption;
the existing pin/name/validity/EKU checks and request limits remain intact.
The harness requires every RPC to report a full handshake and requires the
rotated peer to receive no HTTP request from the old pinned session.

The [Microsoft AllowTlsResume reference](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.allowtlsresume?view=net-10.0)
documents the setting and its enabled default. Runs 04 and 05 pass with both
production repairs. Full handshakes add overhead; device-specific timing and
load acceptance remain native gates, and polling still makes no scan claim.

These are protocol and local process/storage observations. They do not verify
real CPU/program identity, native migration, scoped device permissions, client
certificate deployment, Siemens SDK behavior or rollback of runtime mutations.
Broader resource-pressure/profile work and the rest of C01/C04/D16 and the
44-workstream queue remain open. Hosted CI has a separate account billing gate.

## Final frozen verification

Both final source snapshots contain 2338 identical files. The
aggregate passes 44 managed suites, 242 general checks, 1,510 Python tests
(one existing skip), 25 Explorer model/client checks and all Host workflows.
All 38 package/relocation stages pass. The 1,670-file bundle manifest is
`14be18e4a0eadbf9c42bc65073a8ad1a039c2ed00b15cb80ad988d63c81f4dc6`.

The aggregate runtime harness passes 1442 checks over
643 RPCs; the exact packaged Host passes
1464 checks over 647 RPCs.
Every recorded RPC uses a full TLS handshake. Both runs pass the independent
seven-capture/three-migration original-input audit and seven mutation controls,
plus 62 actual prior-producer capture compatibility checks. Counts of repeated
status polls vary; required lifecycle cases are fixed.

Both isolated CI-upload selections retain twelve bound captures, twenty
original handoff records and all required public dependencies. The independent
lifecycle verifier succeeds using only the selected copy. No TLS private keys
are selected. The current package's five Explorer assets match the prior
browser-tested assets; no new browser run or native UI acceptance is claimed.

Source bindings retain all 112 original historical evidence entries; 106 are
still unavailable. Original scope reconciliation files remain byte-identical.
Only final documentation differs from the frozen sources. Exact manifests,
binary hashes, source hashes and retained-evidence results are in
`verification.json`. The full goal is not complete.
