# Host-only qualification and offline obligation sweep — 27 October 2026

This sweep went through the [44-workstream ledger](../plans/release-obligations-20261015.md)
and the [release matrix](../plans/release-matrix.v1.json) for work that needs no TIA.
A matrix cell binds only when its whole feature is `verified`. Before this change,
every verified feature cited native evidence, apart from two Host-only precedents
(`local.catalog.20261024`, `local.library_intake.20261024`). The cheapest closable
cells were therefore Host-only features whose actual-Host MCP runners existed but
had never been curated as evidence.

No TIA Portal, Openness worker, PLCSIM or device was started. The runs used the
Release Host built from commit `c849391` (`src/` is unchanged through `ff5a755`)
on `PCNico`, with absent engineering and simulator worker paths.

## Closed

Catalog `0.4.28` verifies 65 features with eight new records. Each record has a
curated summary under [`evidence/`](../../evidence/README.md):

| Record | Runner | Features (open cells closed) |
| --- | --- | --- |
| `local.hostonly.vci.20261027` | `test_vci_host_mcp.py` (72 checks, real Git) | 12 `vci_*` (48, D01) |
| `local.hostonly.graph.20261027` | `test_graph_host_mcp.py` (43) | 6 `graph_*` (24, X06) |
| `local.hostonly.automationml.20261027` | `test_automationml_host_mcp.py` (69) | 6 `automationml_*` (24, D05) |
| `local.hostonly.opcua.20261027` | `test_opcua_host_mcp.py` (53) | 8 offline `opcua_*` (32, D04) |
| `local.hostonly.translation.20261027` | `test_translation_host_mcp.py` | 10 offline `translation_*` (50, D03) |
| `local.hostonly.diagnostics.20261027` | `qualify_trace_diagnostics_offline.py` (123 steps; 47/47 sample groups) | `diagnostic_plan`, `diagnostic_watch_plan`, `diagnostic_replan`, `diagnostic_report_export`, six `trace_*`, `project_snapshot_import`, four `artifact_upload_*` (begin/chunk/commit/abort) |
| `local.hostonly.diagnostic-experiments.20261027` | `qualify_diagnostic_experiments_offline.py` (57) + `qualify_repeated_diagnostic_experiments_offline.py` (78) | `diagnostic_experiment_plan/review/compare_runs/report_export` |
| `local.hostonly.diagnostic-ua.20261027` | `qualify_diagnostic_ua_binding.py` (2,102 checks, encrypted loopback UA fixture server) | `diagnostic_ua_binding_plan/observe/compare/report_export` |

The matrix moves from 169 bound / 1,379 open to **416 bound / 1,132 open** cells.
D01, D04, D05 and X06 have no open cells left, but that does not close those
obligations: their native scope has no catalog rows yet (see each obligation's
`executionNotes` in the [JSON ledger](../plans/release-obligations-20261015.json)).
Every record's limitations say that no native product is qualified.

Supporting changes:

- `scripts/curate_evidence.py` reads per-Host tool receipts (`hostExits`, `cleanups`),
  `checks` names, string steps and `status: passed`, so these runners curate unchanged.
- `scripts/test_contracts.py` `claim_errors` rejects a feature verified only by
  `.hostonly.` records when it carries `AC-native`, `AC-recovery`, `AC-runtime` or a
  simulator criterion. Host-only evidence therefore cannot close native gates. For
  example, `runtime_ua_*` passed in the UA-diagnostic run but stays unverified.

Reproduce (fresh output directories; no Portal processes are started):

```powershell
dotnet build src/TiaMcp.Host/TiaMcp.Host.csproj -c Release
dotnet run --project tests/TiaMcp.Diagnostics.Tests -c Release -- <samples.json>
dotnet build tests/TiaMcp.OpcUa.Runtime.Tests/TiaMcp.OpcUa.Runtime.Tests.csproj -c Release
python -B scripts/test_vci_host_mcp.py --host <Host.exe> --git-executable <git.exe> --output <dir>
python -B scripts/qualify_diagnostic_ua_binding.py --host <Host.exe> --server <Runtime.Tests.dll> --samples <samples.json> --output <dir>
python -B scripts/curate_evidence.py --id <record> --report <dir>/verification.json --binary <Host dir> --scope ... --limitation ...
```

## Prepared for native runs

Nothing in this change needs a native run to stand. For the promoted families, the
open native scope already has runners or is still unplanned:

- `translation_native_*` / `qualification_translation_*`: `scripts/qualify_translation_import.py`
  is ready; the category-leakage blocker must be resolved first.
- `runtime_ua_*`: the loopback suites (`scripts/qualify_opcua_runtime.py`) are ready.
  Closing AC-recovery needs a separate decision: either a PLCSIM Advanced/physical
  UA server run or explicit acceptance of loopback-server recovery evidence.
- Native VCI, UA engineering, AML exchange and GRAPH import have no catalog rows yet.

## Remaining offline-reachable work, ranked by cells per effort

| Rank | Work | Open cells | What it needs |
| --- | --- | --- | --- |
| 1 | Per-kind review of committed native summaries: many X11/X10/L02 change kinds call the generic `change_*` tools in passed native runs, but none is cited | 0 (reviewed 28 October) | [Review](native-summary-review-20261028.md): the kinds appear only as seeds, prerequisites or smoke steps; no row closes. It lists the next native run per kind |
| 2 | ~~One native runner for X11 PLC object mutations~~ done 28 October: `scripts/qualify_plc_object_mutations.py` verified 22 kinds (`local.plc_object_mutations.20261028`, [native slot](wave10-native-slot-20261027.md)) | 0 (132 bound) | — |
| 3 | `runtime_ua_*`, `runtime_web_*`, `runtime_capture_*` | 68 + 24 + 24 | Scope decision above; the runners exist |
| 4 | ~~Review-family criteria~~ done 28 October: 40 Host-only review features lost `AC-native`/`AC-recovery` and are verified by their Host runners ([record](review-criteria-and-watch-20261028.md)) | 0 (200 bound, 80 removed) | — |
| 5 | `watch_table_*` / `qualification_watch_*`: 9 verified 28 October ([record](review-criteria-and-watch-20261028.md)) | 81 → 24 | Authoring needs an Update 3 re-pin; create/rename/delete and group features have no native runner |
| 6 | `diagnostic_experiment_acquire_runtime`, `diagnostic_runtime_acquisition_export` | 6 | Genuine runtime capture input |
| 7 | Test Suite, libraries, typed, HMI, hardware | rest | Native product, license or fixture prerequisites (B01–B05) |
