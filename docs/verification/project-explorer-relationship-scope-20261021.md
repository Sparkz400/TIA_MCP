# Explorer relationship scope qualification — 2026-10-21

This batch qualifies detached saved-model behavior, provider retention accounting and the portable browser flow. It does not qualify new native TIA behavior or the ChatGPT desktop host. Native dispatch was not attempted; the existing native dispatch guard remains applicable.

## Changes

The saved reader now distinguishes its 50,000-edge input limit from its 1,000-edge graph response limit, matching the existing v1 schema/C# snapshot capacity. Offline depth bounds are visible; exact endpoints and duplicate edge identities are validated. Both graph engines expose retained-edge traversal scope, explicit stop reasons and the absence of whole-project completeness proof. Saved native provider coverage is partial when retention is only a filtered/bounded subset. Provider scopes appear in the relationship panel and image-export manifests. Older valid v1 models remain readable.

The [scope reference](../reference/project-explorer-relationship-scope.md) defines providers, kinds, acquisition/retention/traversal budgets and excluded semantics.

## Focused checks

- C# `TiaMcp.ProjectModel.Tests`: **13/13** groups, including the independent relationship oracle, retention/export/reopen, duplicate/endpoint refusal, depth/node/edge/byte limits, seed preservation, complete serialized byte accounting, and existing compatibility checks.
- JavaScript model/client/scope tests: **29/29**, **zero skipped**, including the actual C# store export. Source observations remain unchanged by traversal; cycles, distinct parallel evidence, self-loops, unresolved source/target endpoints, disconnected edges and direction/kind filters match independently specified expected edge IDs. A 50,001-edge input is refused.
- Draft 2020-12 model/provider contracts: **9/9**, including the additive relationship-scope contract and refusal of a project-completeness claim.
- Chromium 156.0.8078.4: **39 functional checks**, zero page errors and network requests. The browser imports the C# export through the file picker, checks depth truncation and expansion, provider partial scope and SVG provenance. Scale rendering uses the actual saved-model query, not a manually assembled graph.
- `git diff --check`: passed.

The C# linked test project compiles `ProjectModelStore` and the detached provider components. Native `OpennessSession.ProjectModel.cs` adapter changes are source changes pending the aggregate supported build/native qualification; no native runtime claim follows from the detached tests.

## Measurements

Windows 10.0.26300, Intel Core Ultra 7 155H, Node v24.20.0; native/desktop qualification flags false. Thirty warm graph queries per disconnected scale fixture. Every expected selected component contains exactly 500 nodes, 1,000 distinct edge records and two unresolved observations. A second fixture joins the additional edges to the selected root and must report edge truncation at 10k/50k scale.

| Saved nodes / edges | Saved bytes | Parse, validate and index | Warm scoped graph p95 | Large connected index | Browser first viewport | Browser graph render |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 / 1,000 | 478,661 | 7.12 ms | 11.53 ms | 3.35 ms | 34.4 ms | 133.9 ms |
| 10,000 / 10,000 | 5,057,007 | 60.25 ms | 10.65 ms | 36.23 ms | 271.5 ms | 143.9 ms |
| 50,000 / 50,000 | 25,696,009 | 263.60 ms | 9.03 ms | 160.98 ms | 1,121.3 ms | 119.3 ms |

All saved documents remain below 32 MiB. Query payloads are 382,428–382,431 bytes, below 512 KiB. Tree DOM stays at 21–26 rows. The sampled Node process RSS at 50k/50k was 401,186,816 bytes while holding the input and both disconnected/connected indexes; this is not an isolated browser or single-index memory estimate. Chromium's exposed heap samples were coarse and are retained in the report without treating them as reliable per-fixture memory deltas.

**The final 50k/50k first-viewport result is 1,121.3 ms and misses the existing 1,000 ms target.** The earlier unoptimized run measured 1,004.1 ms and is preserved in `browser-scale/browser-results.json`. One bounded optimization removed per-edge temporary Sets/arrays during adjacency indexing and skipped sorting singleton adjacency lists; it preserves self-loop deduplication and edge ordering. The repeat after that code change still misses the target. Both recorded `passesViewportTarget` values are false; functional checks passed, and the 2,000 ms graph-render target passed. The performance target remains open. These synthetic timings do not qualify native capture latency or real-project relationship completeness.

## Evidence and reproduction

Evidence from this isolated branch lives under `D:/03_Entwicklung/TIA_MCP_w5_explorer`:

- `artifacts/relationship-model-final.log`
- `artifacts/relationship-js-final.log`
- `artifacts/relationship-contract-final.log`
- `artifacts/relationship-scope-final/store-export.json`
- `artifacts/relationship-scope-final/relationship-performance.json`
- `artifacts/relationship-scope-final/browser-optimized/browser-results.json`
- `artifacts/relationship-scope-final/browser-scale/browser-results.json` (original missed viewport measurement)
- `output/playwright/relationship-scope-optimized/relationship-scope-saved-model.png` (visually inspected)

Run from repository root, using fresh browser output paths. The export environment variable is required for the cross-language check; without it that one test reports an explicit skip.

```powershell
$env:TIA_RELATIONSHIP_EVIDENCE = Join-Path (Get-Location) 'artifacts/relationship-scope-run'
dotnet run --project tests/TiaMcp.ProjectModel.Tests --artifacts-path artifacts/build-explorer
node --test ui/project-explorer/tests/model.test.cjs ui/project-explorer/tests/client.test.cjs ui/project-explorer/tests/relationship-scope.test.cjs
python tests/ProjectModelContracts.py
node ui/project-explorer/tests/relationship-benchmark.cjs artifacts/relationship-scope-run/performance.json
node ui/project-explorer/tests/browser.mjs --output artifacts/relationship-scope-run/browser --screenshots output/playwright/relationship-scope-run
```

`TIA_EXPLORER_MODEL` can point the scope tests and benchmark at an absolute packaged `model.js`; their default is the adjacent source implementation. The full browser harness packages source UI assets; package qualification must compare their source hashes to the shipped bundle, or separately run the exact packaged UI. The coordinator owns the final combined validation and package evidence. No native collection/retention limits were raised.
