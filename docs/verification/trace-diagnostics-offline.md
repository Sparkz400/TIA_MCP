# Offline diagnostic increment — 14 October 2026

Historical first-increment receipt. See the [expanded implementation and final offline verification](trace-diagnostics-expanded-offline-20261014.md) for current scope; the counts and parser limitations below describe the original validated build.

Implemented scope and usage: [trace diagnostics](../guides/trace-diagnostics.md). Route decisions and unimplemented plan packages: [TD-01 annex](../plans/expansion-plans/bug-driven-trace-feasibility.md).

Validation used isolated build output under `artifacts/trace-diagnostics-validation-20261014`. The existing active Host/native worker output was not rebuilt or replaced. No TIA/native test, PLC connection, deployment or session interruption occurred.

| Check | Result | Scope |
| --- | --- | --- |
| Release Host build | Passed, zero warnings/errors | New Host/Core code and embedded catalog compile |
| `TiaMcp.Diagnostics.Tests` | 10/10 groups passed | Authored two-conveyor mappings, exact member/type evidence, ambiguity/protection/call context, unavailable storage, manual preparation, normalized synthetic event, negative predictions, quality, bounds and immutable artifacts |
| Existing `TiaMcp.Tests` | 144/144 passed | Existing pure managed Core/bridge regression suite; fake workers only, no TIA API calls |
| `scripts/test_contracts.py` | Passed | 84 public tools, 55 mutation kinds, 157 feature entries, schema JSON and catalog/evidence references |
| Draft 2020-12 validation | 8 actual generated request/output samples passed | Six diagnostic artifact shapes plus two request shapes; local registry, jsonschema 4.26.0 installed only in the isolated validation artifact directory |
| Actual stdio MCP Host | Passed | All five tools discoverable/read-only, offline route report, exact Conv2 storage, manual spec, native route refusal without dispatch, normalized event/report; both workers deliberately absent |

The [MCP receipt](../../artifacts/trace-diagnostics-validation-20261014/host-smoke/result.json) records the tested Host hash, artifact IDs and clean exit. The [schema receipt](../../artifacts/trace-diagnostics-validation-20261014/schema-result.json) lists validated contracts. The [conveyor fixture](../../fixtures/trace-diagnostics/README.md) explicitly separates synthetic native-shaped edges and samples from native observations. Event expectations were independently authored: three condition samples and four transitions. No screenshot or API success is treated as physical acceptance.

Negative checks include ambiguous/unselected names, duplicate names and unsupported namespaces, unavailable/protected parent source, missing root call evidence, unsupported future XML namespaces, repeated calls retained, endpoint-ID mismatch, wrong CPU storage, TEMP/indirect/array/IN_OUT members, unknown hypotheses, stale review, unavailable native route, unsupported trigger/requirements, invalid units/types/ranges/timestamps/lineage, polling claims, gaps/missing values, no reproduced fault, contradicted prediction and tampering. Existing unrelated artifact preservation is an offline file check only. Trace slot exhaustion, stale online program detection, native settings readback, native preservation/reopen, real injected events, physical timing/one-scan detection and interruption/recovery remain **unqualified**.

Reproduce the managed suite with an optional JSON sample output argument, build the Host with the same fresh `--artifacts-path`, then run `scripts/qualify_trace_diagnostics_offline.py --host <isolated Host.exe> --output <fresh directory> --samples <sample JSON>`. The script forces absent engineering and simulator workers. It has no native mode.

The feature catalog keeps the five tools `partial` and `unverified` for native end-to-end qualification. This evidence supports only the explicit offline scope above; it does not close TD-07/08 or native TD-09/11 acceptance, and does not expand existing 1.0 commitments.
