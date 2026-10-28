# Lost evidence re-runs — 28 October 2026

84 raw reports of 74 curated evidence records were gone before the evidence store existed
([list](../../evidence/raw-reports-lost.json)), and 140 of the 359 verified features cited only
those records. Overnight on 28 October, 40 new records re-ran 44 of the 74, and now only 3
verified features rest solely on lost reports. Each new record is named
`<original stem>.rerun.20261028`, keeps its raw reports in the main checkout's evidence store, and
replaces the lost record in the features that cited it; the lost summaries stay as history.

## How the runs were made

- **Host-only (21 records):** `scripts/Test-Offline.ps1 -Suite Managed -Configuration Release`
  from a clean detached worktree of `e1b0a42` passed every managed suite and Host workflow. The
  first attempt from `2d5aa7c` failed in `TiaMcp.Tests`, because `FeatureCatalogTests` named two
  catalog records retired in catalog 0.4.68; `3bbc32e` fixed the test.
- **Native (18 records):** one run at a time from a frozen Debug build of `2d5aa7c`
  (`D:\wp\b65`, `git archive` plus `source-manifest.json`), each runner started from the frozen
  copy. Each record was curated from a checkout at that commit, so it names the commit its
  binaries came from.
- **Safety import (1 record):** `safety_import_xml` now compiles every device; the safety runner
  ran from a frozen build of `2d74ebb` (`D:\wp\b66`). See
  [safety changes](safety-changes-20261028.md#f-block-import-compiles-every-device).

| New record | Replaces | Commit | Reports | Recorded steps | Features citing it |
| --- | --- | --- | --- | --- | --- |
| [`local.access.qualify.rerun.20261028`](../../evidence/local.access.qualify.rerun.20261028.json) | `local.access.qualify.20261028` | `2d5aa7c` | 1 | 215 | 8 |
| [`local.cpu_memory_bytes.rerun.20261028`](../../evidence/local.cpu_memory_bytes.rerun.20261028.json) | `local.cpu_memory_bytes.20261026` | `2d5aa7c` | 1 | 51 | 1 |
| [`local.hostonly.access-review.rerun.20261028`](../../evidence/local.hostonly.access-review.rerun.20261028.json) | `local.hostonly.access-review.20261028` | `e1b0a42` | 1 | 42 | 2 |
| [`local.hostonly.automationml.rerun.20261028`](../../evidence/local.hostonly.automationml.rerun.20261028.json) | `local.hostonly.automationml.20261027` | `e1b0a42` | 1 | 69 | 6 |
| [`local.hostonly.composite-engineering.rerun.20261028`](../../evidence/local.hostonly.composite-engineering.rerun.20261028.json) | `local.hostonly.composite-engineering.20261028` | `e1b0a42` | 3 | 179 | 5 |
| [`local.hostonly.connection-review.rerun.20261028`](../../evidence/local.hostonly.connection-review.rerun.20261028.json) | `local.hostonly.connection-review.20261028` | `e1b0a42` | 1 | 33 | 2 |
| [`local.hostonly.db-snapshot-review.rerun.20261028`](../../evidence/local.hostonly.db-snapshot-review.rerun.20261028.json) | `local.hostonly.db-snapshot-review.20261028` | `e1b0a42` | 1 | 30 | 2 |
| [`local.hostonly.diagnostic-experiments.rerun.20261028`](../../evidence/local.hostonly.diagnostic-experiments.rerun.20261028.json) | `local.hostonly.diagnostic-experiments.20261027` | `e1b0a42` | 2 | 135 | 4 |
| [`local.hostonly.diagnostic-ua.rerun.20261028`](../../evidence/local.hostonly.diagnostic-ua.rerun.20261028.json) | `local.hostonly.diagnostic-ua.20261027` | `e1b0a42` | 1 | 2104 | 4 |
| [`local.hostonly.diagnostics.rerun.20261028`](../../evidence/local.hostonly.diagnostics.rerun.20261028.json) | `local.hostonly.diagnostics.20261027` | `e1b0a42` | 1 | 123 | 15 |
| [`local.hostonly.drive-profile-review.rerun.20261028`](../../evidence/local.hostonly.drive-profile-review.rerun.20261028.json) | `local.hostonly.drive-profile-review.20261028` | `e1b0a42` | 2 | 79 | 2 |
| [`local.hostonly.graph.rerun.20261028`](../../evidence/local.hostonly.graph.rerun.20261028.json) | `local.hostonly.graph.20261027` | `e1b0a42` | 1 | 43 | 6 |
| [`local.hostonly.hmi-component-review.rerun.20261028`](../../evidence/local.hostonly.hmi-component-review.rerun.20261028.json) | `local.hostonly.hmi-component-review.20261028` | `e1b0a42` | 1 | 1102 | 2 |
| [`local.hostonly.opcua.rerun.20261028`](../../evidence/local.hostonly.opcua.rerun.20261028.json) | `local.hostonly.opcua.20261027` | `e1b0a42` | 1 | 53 | 8 |
| [`local.hostonly.public-certificate-review.rerun.20261028`](../../evidence/local.hostonly.public-certificate-review.rerun.20261028.json) | `local.hostonly.public-certificate-review.20261028` | `e1b0a42` | 1 | 68 | 2 |
| [`local.hostonly.runtime-handoff.rerun.20261028`](../../evidence/local.hostonly.runtime-handoff.rerun.20261028.json) | `local.hostonly.runtime-handoff.20261028` | `e1b0a42` | 1 | 110 | 6 |
| [`local.hostonly.safety-evidence-review.rerun.20261028`](../../evidence/local.hostonly.safety-evidence-review.rerun.20261028.json) | `local.hostonly.safety-evidence-review.20261028` | `e1b0a42` | 1 | 87 | 2 |
| [`local.hostonly.software-unit-review.rerun.20261028`](../../evidence/local.hostonly.software-unit-review.rerun.20261028.json) | `local.hostonly.software-unit-review.20261028` | `e1b0a42` | 1 | 34 | 2 |
| [`local.hostonly.technology-profile-review.rerun.20261028`](../../evidence/local.hostonly.technology-profile-review.rerun.20261028.json) | `local.hostonly.technology-profile-review.20261028` | `e1b0a42` | 1 | 193 | 2 |
| [`local.hostonly.testsuite-result.rerun.20261028`](../../evidence/local.hostonly.testsuite-result.rerun.20261028.json) | `local.hostonly.testsuite-result.20261028` | `e1b0a42` | 1 | 55 | 2 |
| [`local.hostonly.translation.rerun.20261028`](../../evidence/local.hostonly.translation.rerun.20261028.json) | `local.hostonly.translation.20261027` | `e1b0a42` | 1 | 6 | 10 |
| [`local.hostonly.vci.rerun.20261028`](../../evidence/local.hostonly.vci.rerun.20261028.json) | `local.hostonly.vci.20261027` | `e1b0a42` | 1 | 72 | 12 |
| [`local.hostonly.web-application-review.rerun.20261028`](../../evidence/local.hostonly.web-application-review.rerun.20261028.json) | `local.hostonly.web-application-review.20261028` | `e1b0a42` | 1 | 77 | 2 |
| [`local.library_compatibility.rerun.20261028`](../../evidence/local.library_compatibility.rerun.20261028.json) | `local.library_compatibility.20261027` | `2d5aa7c` | 1 | 41 | 2 |
| [`local.library_dependencies.rerun.20261028`](../../evidence/local.library_dependencies.rerun.20261028.json) | `local.library_dependencies.20261024`, `local.library_dependencies.20261025` | `2d5aa7c` | 1 | 82 | 16 |
| [`local.library_formats.rerun.20261028`](../../evidence/local.library_formats.rerun.20261028.json) | `local.library_formats.20261024`, `local.library_formats.20261025` | `2d5aa7c` | 1 | 17 | 5 |
| [`local.library_integrate.combined_apply.rerun.20261028`](../../evidence/local.library_integrate.combined_apply.rerun.20261028.json) | `local.library_integrate.combined_apply.20261028` | `2d5aa7c` | 1 | 5 | 2 |
| [`local.library_interruption.rerun.20261028`](../../evidence/local.library_interruption.rerun.20261028.json) | `local.library_interruption.20261024`, `local.library_interruption.20261025` | `2d5aa7c` | 1 | 55 | 4 |
| [`local.library_lifecycle.rerun.20261028`](../../evidence/local.library_lifecycle.rerun.20261028.json) | `local.library_lifecycle.20261024` | `2d5aa7c` | 1 | 82 | 15 |
| [`local.library_project_state.rerun.20261028`](../../evidence/local.library_project_state.rerun.20261028.json) | `local.library_project_state.20261024` | `2d5aa7c` | 1 | 18 | 0 |
| [`local.library_releases.rerun.20261028`](../../evidence/local.library_releases.rerun.20261028.json) | `local.library_releases.20261024`, `local.library_releases.20261026` | `2d5aa7c` | 1 | 105 | 20 |
| [`local.milestones.rerun.20261028`](../../evidence/local.milestones.rerun.20261028.json) | `local.milestones.reopen.20261028`, `local.milestones.network.20261028` | `2d5aa7c` | 1 | 206 | 7 |
| [`local.plc_component.existing_caller.rerun.20261028`](../../evidence/local.plc_component.existing_caller.rerun.20261028.json) | `local.plc_component.existing_caller.20261025` | `2d5aa7c` | 1 | 0 | 1 |
| [`local.plc_component.install.rerun.20261028`](../../evidence/local.plc_component.install.rerun.20261028.json) | `local.plc_component.install.20261024` | `2d5aa7c` | 1 | 0 | 1 |
| [`local.plc_component.runtime.rerun.20261028`](../../evidence/local.plc_component.runtime.rerun.20261028.json) | `local.plc_component.runtime.20261024` | `2d5aa7c` | 1 | 0 | 1 |
| [`local.plc_object_mutations.rerun.20261028`](../../evidence/local.plc_object_mutations.rerun.20261028.json) | `local.plc_object_mutations.20261028` | `2d5aa7c` | 1 | 369 | 24 |
| [`local.remote-io.full-route.rerun.20261028`](../../evidence/local.remote-io.full-route.rerun.20261028.json) | `local.remote-io.full-route.20261024` | `2d5aa7c` | 1 | 196 | 5 |
| [`local.remote-io.lifecycle.rerun.20261028`](../../evidence/local.remote-io.lifecycle.rerun.20261028.json) | `local.remote-io.lifecycle.20261024` | `2d5aa7c` | 1 | 194 | 4 |
| [`local.safety_import.device-compile.20261028`](../../evidence/local.safety_import.device-compile.20261028.json) | `local.safety_import.20261028` | `2d74ebb` | 1 | 66 | 1 |
| [`local.sim.state.rerun.20261028`](../../evidence/local.sim.state.rerun.20261028.json) | `local.sim.state.20261024` | `2d5aa7c` | 1 | 244 | 3 |

"Recorded steps" counts the steps a report lists; `component_runtime.py` logs events instead,
so its record shows 0.

## Findings

- **Native path limit.** The library harness caps the scratch path at 143 characters, and TIA
  refused to archive a 167-character checkpoint path (the first combined-apply attempt). Runs
  whose artifacts sat under the main checkout's `artifacts/` failed for that reason, so native
  runs use `D:\wp\rr`; curation still copies every report into the main store.
- **Component runner.** `qualify_plc_component.py` requires `<frozen source>/artifacts` to exist,
  and its `--output` is a directory holding `result.json`.
- **Access run.** As in the original, the reopen comparison differs only on
  `WebserverApplicationName` and `WebserverHTMLDirectory`, the attributes of
  `webserver_user_pages_generate`, which stays unqualified (214/215 steps).
- **Library scopes.** Both attempts failed at the same point: the runner reopens its project
  while the earlier session still holds TIA's project lock ("already opened by user"), which TIA
  releases only after about two minutes. The original passed on 25 October.
- **Library units.** The run hung for about 15 minutes in its `LibraryUnitSeeder` step with no
  seeder process and no visible dialog, and was stopped. The unit-relation and protected-object
  runs use the same seeder and were not started.

## Still resting on lost reports

Only `hmi_inspect`, `hmi_references` and `hmi_capabilities` rest solely on a lost record
(`local.hmi.bridge.20261025`); re-running it needs `HmiSeeder.exe qualify` fixtures per HMI
profile followed by `scripts/qualify_hmi.py`. The other 23 lost records still cited are listed
in the [TODO](../TODO.md#lost-evidence-still-to-re-run); every feature citing them also cites
retained evidence.

Not attempted: library upgrades, vendor packages and DMC prerequisites (their inputs are no
longer on this PC), the typed LAD/FBD captures and runs, the engineering interruption records,
the explorer provider runs and the wave-kinds probe.
