# Offline diagnostic watch delivery — 16 October 2026

This D02/D17/C04 increment connects the existing pure watch planner to immutable
diagnostic cases through `diagnostic_watch_plan`. It does not complete the full
watch, force, diagnostic or composite release obligations. No Siemens product
is installed or started.

## Implementation boundary

The Host adapter reads the existing case and exact saved review, checks the
source inventory and builds references using actual captured model kinds,
PLC/scope paths and known document hashes. The full review hash binds the
snapshot. The TIA version is an explicit caller assertion, never inferred from
a file extension or silently defaulted. External storage requires independent
allowances/reasons. The existing planner still enforces exact instance/CPU
boundaries, signal evidence and row limits. Unknown fields, malformed budgets,
unused allowances and incomplete selections fail before publication.

The existing diagnostic artifact store persists an envelope around the unchanged
hashed planner output. The response is a bounded summary; `artifact_read` exports
the whole artifact. Identical requests retain identical IDs across Host restarts.
Unresolved signals and row omissions remain available with their reasons. Source
authenticity, native compatibility, native XML generation, live addresses and
native apply eligibility are never claimed.

## Development checks

- `artifacts/diagnostic-watch-dev/diagnostics02.log`: 23 diagnostic groups pass.
  New checks use the independent 11-signal conveyor oracle, assert deterministic
  artifact/canonical plan hashes, row-budget omissions, genuine saved tag kinds
  and scopes, unresolved source, source/case tamper, malformed requests, missing
  or duplicate external allowances and absent instance selection.
- `artifacts/diagnostic-watch-dev/mcp01/result.json`: actual Host upload, diagnosis,
  watch planning/download and trace/measurement workflow passes through three
  Host lifetimes with missing native-worker paths. It independently validates
  envelope, nested planner/reference, request and summary schemas; repeated
  planning after restart returns the same artifact ID. Missing external storage,
  row omissions and altered source are checked at the public tool boundary.
- Two Python request/summary contract checks pass; existing three intake/shutdown
  harness checks still pass. Contracts cover 175 tools, 55 mutation kinds and
  248 feature entries without promoting native qualification.

The first development test attempt modified a signal used in a Boolean prediction
to unavailable storage and failed during diagnostic prediction validation, before
watch planning. The corrected fixture uses a signal outside that prediction.
Both logs remain retained. An initial MCP command used system Python without
jsonschema; the successful run uses the existing repository `.venv`. No package
or dependency was added. In-memory schema references follow the current
[jsonschema referencing documentation](https://github.com/python-jsonschema/jsonschema/blob/main/docs/referencing.rst),
retrieved through Context7; validation resolves only explicitly registered local
schemas.

Follow-up finding: an unavailable declaration referenced by a Boolean hypothesis
currently produces a misleading numeric precision error in
`TraceMeasurements.Conditions`. The failed development check reproduces this
pre-existing diagnostic validation issue. This increment tests watch handling of
unavailable signals outside a prediction; it does not claim to repair that error. The subsequent [prediction validation repair](prediction-validation-20261016.md) now rejects both Boolean and numeric conditions on unknown/unsupported types explicitly.

## Aggregate verification

The final aggregate passes from `artifacts/diagnostic-watch-01/source`:

- Tested Git tree: `72189c551425fb6f33085c55057d3dbe113db021` (1,780 files).
- Source manifest SHA-256: `01093972b88bf6ac7f1e962e8cb766b9137c852133877e1a61611fe273e1f728`.
- Aggregate result SHA-256: `6def0376508510317cf9e79855298ac550adf1df0b781e1b22bccae741bf1c58`.
- Diagnostic MCP result SHA-256: `6cc2b63f6bf6e3f2d4a922429acc4f9dc2416acabb7e017066fb7a7a70ad1b0a`.
- All 29 managed suites, 1,263 Python tests (one unavailable-native-evidence skip),
  24 Explorer model/client tests and every actual stdio workflow pass. Diagnostic
  checks include 23 managed groups and 21 MCP steps across three Host lifetimes.
  The default Host lists 155 tools; the catalog includes 175 declared tools,
  including tools behind explicit gates, and 248 feature entries.
- Retained historical evidence inventory remains six exact matches and 106 missing
  files. No missing evidence was reconstructed or promoted to qualification.

Only documentation changed after that tested source tree. Browser UI tests were
not rerun locally because no UI source changed. No Siemens SDK build, native
engineering, simulation, acquisition, runtime or release acceptance was executed.
Previous commit `3e03200` passes all hosted checks: Python/managed/isolated OPC UA
in `38914212672` and Explorer in `38914212661`.
