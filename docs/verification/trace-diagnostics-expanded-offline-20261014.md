# Expanded offline trace diagnostics — 14 October 2026

Subsequent feasibility correction: the [SIMATIC AX audit](../plans/expansion-plans/bug-driven-trace-simatic-ax-feasibility.md) established a documented external PLC-job CLI, separate from V21 Openness. It remains unavailable in this bridge and unqualified; documented TIA trigger restrictions and unresolved readback/persistence prevent full native fulfillment. The counts below describe the expanded build before the additive nanosecond/capability correction; its separate receipt is [AX audit validation](../../artifacts/trace-diagnostics-ax-audit-20261014/result.json).

The [diagnostic guide](../guides/trace-diagnostics.md) describes the implemented contract. This supersedes the first increment's current-scope description, while preserving its [historical evidence](trace-diagnostics-offline.md). **The complete native workflow is not finished or qualified.** The user explicitly kept native UI work paused on 14 October; this work did not start/attach TIA, run native tests, connect to a PLC, deploy, or interrupt another session.

## Delivered scope

Eight additive Host tools provide saved-review discovery, exact captured instance resolution, conservative automatic signal proposals, manual trace specification, supplied-readback comparison, JSON/explicit CSV import, hypothesis analysis, capture comparison and instance revalidation. The original five tools remain; new tools are `trace_compare`, `diagnostic_replan` and `trace_check_readback`.

The Core implementation is isolated in `src/TiaMcp.Core/Diagnostics`. New modules are `DiagnosticCodeReader`, `DiagnosticReportContext`, `DiagnosticSignalSelector`, `DiagnosticReplanner`, `TraceCsvReader`, `TraceComparison`, `TraceReadback` and `TraceNumber`. Existing diagnostic modules retain bounded storage, freshness, quality and capability checks. Host registration is in `src/TiaMcp.Host/DiagnosticTools.cs`; no engineering/simulator dispatcher or native worker adapter was added.

Exact fully qualified type names now resolve within the same captured CPU/scope. Simple SCL/FlgNet actual bindings and IN_OUT substitution retain declaration/call evidence. Automatic selection follows conservative dependency supersets and captured competing writers; it does not claim complete program semantics or autonomous equipment association. Tags retain captured node/type/access evidence. Coverage gaps and truncation survive into the manual specification.

CSV import requires an explicit dialect/column/unit/clock mapping; negative pre-trigger times survive. Numeric conversion rejects overflow, underflow and decimal precision loss. Comparison requires explicit anchors, matching units/types and explicit changed-instance mapping; no resampling or fix proof is produced. Replanning rechecks every mapped declaration and records source lineage. Supplied readback checks settings, identities, signals and resource observations without pretending to probe or authenticate a live target.

## Verification and identities

All outputs are isolated under `artifacts/trace-diagnostics-full-20261014`. Active deployment/native binaries were not replaced.

| Check | Result | Demonstrated scope |
| --- | --- | --- |
| Final Release Host build | Passed; zero warnings/errors | Additive Host/Core compilation |
| Final diagnostic suite | 17/17 groups passed | Independent conveyor mapping/event oracles, ambiguity/protection/call context, qualified names, dependency/actual/IN_OUT/writer selection, tag evidence, budgets, unavailable storage, lineage, negative captures, quality, CSV, comparison/replan, readback mismatches and numeric precision |
| Full offline runner | Passed all 13 allowlisted managed suites, Python tests and explorer tests | Existing managed/fake-worker regression coverage; includes 144/144 Core tests. No native calls. Subsequent additive coverage propagation and publication locking were checked by the final diagnostic suite and MCP smoke. |
| Feature contract check | Passed | 87 MCP tools, 55 mutation kinds, 160 feature entries; no invented native change kinds |
| JSON Schema | 16 generated artifact/request samples passed Draft 2020-12 | Local registry, isolated jsonschema 4.26.0; automatic requests/results, CSV, replan, comparison and readback included |
| Final actual stdio MCP Host | Passed, clean exit | Eight tools discoverable/read-only, exact Conv2 selection, automatic actual/dependency signals, native-route refusal, manual preparation, synthetic event assertions, JSON/CSV equivalence, explicit comparison/replan and exhausted-slot readback mismatch |

Receipts: [full offline runner](../../artifacts/trace-diagnostics-full-20261014/regression/result.json), [final MCP](../../artifacts/trace-diagnostics-full-20261014/host-smoke-verified/result.json), [final schemas](../../artifacts/trace-diagnostics-full-20261014/schema-result-final.json), [source/binary inventory](../../artifacts/trace-diagnostics-full-20261014/result.json). The MCP receipt hashes both managed Host/Core binaries in addition to the executable stub. Its engineering and simulator worker paths deliberately do not exist. Synthetic proof artifacts never become native qualification.

The authored event has three matching condition samples and four transitions. Separate expected mappings select Conv2, not Conv1. Readback mutation tests independently alter instance, sampling reduction, type, missing signal, program-match assertion and free-slot count and require six differences. These results establish offline comparison behavior only; screenshots, API success or supplied attestations cannot replace native acceptance.

## Remaining work and disposition

| Package | Current disposition | Remaining gate |
| --- | --- | --- |
| TD-01 | Bounded public API/schema research decision recorded | A documented general trace owner/service or officially supported schema/import route has not been established. No route implies no speculative adapter. |
| TD-02…05 | Offline contracts, fixture, ranking, captured instances and bounded signal selection implemented | Exact equipment association still requires project evidence/caller selection. General language semantics, indirect/array/cross-unit aliases and full automatic minimality remain outside the supported parser subset. Every actual target needs observability validation. |
| TD-06 | Manual specification and offline supplied-readback checker implemented | Human configuration/readback remains necessary; this is partial native fulfillment. UI assistance remains paused. |
| TD-07/08 | Blocked for general native automation; no adapter/jobs claimed | Supported route, owned target, current native admission, independent configure/reopen/preserve, transfer/arm/record/retrieve and interruption reconciliation. No transport competing with prompt 16. |
| TD-09 | JSON/explicit CSV import and sample-level hypothesis analysis implemented | Real native export/dialect, exact program identity, genuine injected-event acquisition and timing acceptance remain unqualified. No binary decoder without supported format evidence. |
| TD-10 | Eight role templates, repeated captures, explicit comparison/reuse, bounded retention and JSON reports implemented | General parser expansion and ordinary/long-term/project/multi-device/drive family qualification remain separate work. No Safety Validation or Startdrive extrapolation. |
| TD-11 | Offline build/contracts/regressions/MCP evidence complete for this subset | Native/runtime/preservation/recovery/compatibility and release packaging acceptance remain open. No expansion of existing 1.0 commitments. |

Recommended next executable package is the remaining **TD-01 public-route clarification**, read-only under the current pause. An authoritative exact API owner/method or supported editable schema/import contract would permit designing the native adapter without inventing capabilities. Sending a vendor inquiry is not authorized by this task and was not done. If no such route exists, native automation cannot proceed while UI work stays paused. Later native qualification must use current owner/admission decisions, an isolated two-signal fixture and independent readback/known-waveform evidence; old process IDs and queue receipts are not ownership proof.

## Reproduction

Run `scripts/Test-Offline.ps1 -OutputDirectory <fresh repository artifact directory>` for the managed/fake-worker regression profile. For the diagnostic Host proof, run `dotnet run --project tests/TiaMcp.Diagnostics.Tests --configuration Release --artifacts-path <isolated build directory> -- <samples.json>`, build `src/TiaMcp.Host` with that artifact directory, then run `scripts/qualify_trace_diagnostics_offline.py --host <isolated Host.exe> --output <fresh directory> --samples <samples.json>`. The smoke script has no native mode.
