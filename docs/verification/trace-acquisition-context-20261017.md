# Trace analysis and comparison acquisition context — 17 October 2026

## Problem and change

Two measurements could name the same saved review but declare different
`programIdentity` values. Comparison accepted them without the explicit changed
source option. Its exported report also omitted the original clock labels,
program labels, acquisition modes and time units. Referencing the measurement
IDs alone made these differences easy to miss when reading the report.

The shared `TraceComparison.Compare` now requires `allowChangedSource:true` and
an explicit reason when either saved review IDs or declared program identities
differ. `sourceChanged` retains its previous review-ID meaning;
`programIdentityChanged` reports the separate measurement-label comparison.
The report retains `beforeAcquisition` and `afterAcquisition`, including original
`mode`, `clock`, `timeUnit`, `programIdentity` and `identityVerified:false`.
`clockSynchronizationVerified:false` is unconditional: caller-selected event
anchors establish neither synchronized clocks nor simultaneous events.

The single-measurement analysis report had the same missing-context problem,
reproduced in [its regression output](../../artifacts/trace-analysis-before.log).
Both reports now use `TraceMeasurements.AcquisitionContext`; analysis retains
`acquisitionContext` and `clockSynchronizationVerified:false`. The original
analysis mode/time-unit fields remain compatible. Its v1 schema also preserves
legacy reports while requiring paired current context/verification fields.

Exact sample alignment/statistics, explicit changed-storage mapping, units,
quality, no-resampling and `bugFixProven:false` behavior remain in place.
Supplied `programMatchVerified` claims are retained as supplied quality evidence;
they do not authenticate either declared program identity.

The v1 schema accepts historical reports without the new context. If any new
context field is present, both complete contexts and both flags are required.
The schema refuses true identity/synchronization verification claims. Historical
artifacts are not rewritten; rerun comparison to create a current report.
Host help, the feature catalog and the user guide describe the stronger check.

## Verification

[Before-fix regression output](../../artifacts/trace-acquisition-before-02.log)
reproduces acceptance of different declared programs and missing report context.
Two new managed groups cover absent/false/true changed-source admission,
unchanged review identity, both exact contexts, different clocks/modes/time
units, identical aligned statistics, unchanged uncertainty and saved reporting.
All 33 managed diagnostic groups pass after the repair.

The [actual MCP workflow](../../artifacts/trace-acquisition-mcp-02/result.json)
passes 61 steps across five clean Host lifetimes with deliberately absent native
workers. It includes changed-program refusal/admission, schema refusal of
incomplete or authenticated contexts, legacy-schema compatibility, exact report
identity after upload release/restart, and hash-checked paged report download. The retained analysis report contains
the same original acquisition context after restart.

Final frozen Git tree: `03b80c5a37e50ffade86700a2eee690acdee63e8` (2042 files).
[Source manifest](../../artifacts/trace-acquisition-02/source-manifest.json) SHA-256:
`7e07cefae6a54b15f09a1578a1cc2bd1c3ae9de4bc54e740c70a0563b824df3f`.
Source ZIP SHA-256: `9dcb8563e8ad8cd814163f1f749b94155b1fb7324dbb28c6fe36c96ba99a1f10`.

The [aggregate](../../artifacts/trace-acquisition-02/source/artifacts/checks/result.json)
passes 41 managed suites, 1,455 Python tests (one unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all offline MCP workflows.
The general executable passes 226 checks; diagnostics passes 33 groups.
The frozen [diagnostic MCP workflow](../../artifacts/trace-acquisition-02/source/artifacts/checks/diagnostic-mcp/result.json)
passes 61 steps across five clean Host lifetimes.
Run: 2026-10-17T16:02:55.3153602Z to 2026-10-17T16:17:54.4970001Z.
Aggregate receipt SHA-256: `49575f0d519dfd0bd6be169bbc10707f76331e9fa4ed527e786c6ed11d9a7af7`.
[Console evidence](../../artifacts/trace-acquisition-02/aggregate-console.log).

All 24 [package stages](../../artifacts/trace-acquisition-02/source/artifacts/package-checks/result.json)
pass, including exact ZIP/manifest verification and relocated MCP workflows.
Bundle `tia-mcp-offline_20261017_160325_6154f42e` contains 1444 manifest files.
Package receipt SHA-256: `55c484d9bb2d9e7dd1ab5bd36612aa5c6ae4025c6b29fbf0f34abbdbfedfdddd`.
Manifest SHA-256: `05a4184b33a666815bfd0ab6e18d006f188f7de336a6c2de034fb88818682c90`.
ZIP SHA-256: `412d8ad753d4865a3262bff36937ccbef6c91adfd9a0a83776a57e51dcdbfa76`.

The [source comparison](../../artifacts/trace-acquisition-02/final-source-comparison.json)
verifies unchanged tested implementation; only five completion documents differ.
All 106 unavailable historical evidence binding rows remain unchanged. The
[integration receipt](../../artifacts/trace-acquisition-02/integration.json)
records Git/CI and cleanup state. Earlier `trace-acquisition-01` checks cover
their own comparison-only source; final evidence uses revision 02 with the shared
analysis/comparison formatter.

## Remaining scope

These are comparisons of supplied evidence. No live program identity, clock
synchronization, source authenticity, acquisition, Siemens decoding, native
trace creation or fix/causality claim has been qualified. Browser checks are not
repeated for this Core/Host/report change. Continue the full C04/D17 workflow
and the 44-workstream queue, including independently qualified native routes.

The project-preservation inspection from the preceding increment remains open:
existing inventory/model routes expose selected fields and hierarchy, not full
project settings; original external-source byte export is unestablished. See
the retained [scope inspection](../../artifacts/encoder-hardware-01/next-scope.json).
All 106 missing historical evidence binding records remain unchanged.
