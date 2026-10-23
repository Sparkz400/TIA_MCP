# Repeatable portable Explorer imports — 21 October 2026

The actual saved-file workflow now has repeatable acceptance evidence: **80
50k-node/50k-edge imports across two 20-pair runs all finish below 1,000 ms**.
The final run exercises the unchanged, actual relocated wave5 package. This
closes the portable file-import repeatability item under the recorded conditions;
native collection completeness and actual desktop MCP Apps rendering remain open.
Production viewer source and shipped assets are unchanged.

## Acceptance fixed before implementation

At least ten fresh-browser-process cold imports and ten same-page warm imports,
every first viewport strictly below 1,000 ms, 1–69 rendered tree rows, independent
tree/search/reference/relationship checks, meaningful refusal controls, no page
errors or HTTP(S) requests. The [reference](../reference/project-explorer-performance.md)
defines the actual file-change-to-two-frames interval, cache conditions and command.
No percentile substitutes for the every-import threshold; no outliers were dropped.

## Profile the actual workflow first

Unmodified production code imported the 24,393,005-byte file in **376.6, 307.7,
287.4 ms**, including file reading, JSON parsing and two frames. Validation and
indexing took 137.4–151.3 ms. The legacy harness injected the entire object through
Playwright's evaluation binding; three paired diagnostic runs measured **742.8,
767.5, 1,002.3 ms**, with essentially equal 143.4–148.3 ms validation/index time.
CPU profiles retain automation reconstruction, garbage collection and frame-delay
observations. Attribution of the extra delay to automation work is an inference;
these profiles do not isolate every scheduler/GC contribution. Profiler runs are
diagnostics, separate from unprofiled acceptance.

The new gate and the ordinary browser suite's scale samples use the user-facing
file input, with no large dataset passed through `evaluate`. This repairs the
measurement path; it is not a claimed production speedup. The original 1,002.3 ms
diagnostic miss, wave5's 1,004.1/1,121.3 ms misses and its 725.2 ms final observation
remain retained under their original methods.

## Final evidence

Windows 10.0.26300, Intel Core Ultra 7 155H, 22 logical CPUs, Node v24.20.0,
Chromium 156.0.8078.4, headless 1440×1000 light theme. Each pair creates a new
browser process/context/page, then imports the same file twice. OS disk caching
and unrelated desktop workloads are uncontrolled. Repository workers paused
heavy builds and native dispatch for the measurement windows. During the final
window, the coordinator performed light text/schema reads and a 3.5-second Git
checkout into a separate review worktree; this was not a completely idle system.

| Run | Imports | Minimum | Median | p95 | Maximum | Misses |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Current-source portable bundle | 20 cold + 20 warm | 237.2 ms | 283.6 ms | 297.5 ms | 427.7 ms | 0 |
| Actual relocated wave5 reader, final harness | 20 cold + 20 warm | 235.0 ms | 280.3 ms | 312.5 ms | 424.6 ms | 0 |

Each run also passes 20 independent exact-inventory/reference/relationship checks
and eight negative-control groups. Expected first tree IDs, last-object canonical
reference, 1,000 explicit edge IDs, original edge records, 500 node IDs, two
unresolved observations and edge truncation are independently specified. Missing
edges, altered endpoints and false completeness fail the oracle. The target
comparator rejects the exact 1,000 ms boundary and missing/invalid observations.
Malformed JSON, duplicate edge IDs, changed endpoints and a file above 32 MiB
refuse while preserving the active model and selection. No page errors or remote
requests occurred.

Coordinator review requested per-cold/warm snapshot identity and file-size
assertions plus after-run source/viewer hashes. The original passing source run
and its exact harness are retained. The final relocated run includes those
stronger assertions; all source/viewer hashes match before and after. Six viewer
assets (`index.html`, template, style, model, client, renderer) match the current
source, original wave5 release and actual relocated directory, both before and
after qualification. The embedded bundle manifest is also verified. Relocated
`index.html` SHA-256 is
`b273ae0254a45f5f21f1f92a7e3413277eddb28ff4e39d2da736aaa2bf11b4b5`.
No unchanged production binaries were rebuilt merely for this test-only change.

The broader final regression passes **31 JS checks, zero skips**, including the
actual wave5 C# saved exports, and **39 browser checks**, zero page/network errors.
The standard browser scale samples are 66.7/176.7/351.0 ms at 1k/10k/50k nodes
and edges; graph rendering is 85.9/71.4/106.8 ms. The relationship-scope screenshot
was visually inspected. `git diff --check` passes. A one-pair development smoke
retains `passed:false` because the acceptance minimum is ten pairs, despite its
functional checks passing. No failed attempt is relabeled.

## Retention and reproduction

All paths below are in the isolated worktree
`D:/03_Entwicklung/TIA_MCP_w6_explorer`; source base is `5cd92a5` on
`work/wave6-explorer`.

- `artifacts/explorer-initial-profile/`: original scripts, file-import/resident
  results and six raw Chromium CPU profiles, including the 1,002.3 ms miss.
- `artifacts/explorer-import-development-01/result.json`: one-pair development
  result, intentionally insufficient for acceptance.
- `artifacts/explorer-import-acceptance-01/result.json` and `source/`: original
  20-pair source-bundle run and pre-review harness.
- `artifacts/explorer-import-packaged-final-01/result.json`: final 20-pair actual
  relocated reader run, exact source/input/viewer hashes and all individual times.
  `conditions-supplement.json` retains the coordinator's disk-I/O overlap report
  without modifying the completed result.
- `artifacts/explorer-package-parity.json` and `explorer-package-parity-after.json`:
  original/relocated/current six-asset parity and bundle-manifest checks.
- `artifacts/explorer-js-final.log`, `artifacts/explorer-browser-final-01/`,
  `output/playwright/explorer-final-01/`: final regression and screenshots.
- `artifacts/explorer-retention-receipt.json`: hashes binding the retained evidence.

The final test command uses the `relocated` path from the parity receipt plus
`/index.html` as `--viewer`, `--trials 20`, a fresh `--output` directory and the
explicit recorded `--conditions`. The main reference gives the portable default.
This narrow milestone does not close native/provider collection, real project
completeness, desktop MCP Apps host acceptance or the full release.

## Coordinator integration

The lane commit `6b22608` was independently reviewed and integrated as `2b017ca`. All 87 lane source/evidence files were hash-verified and copied under `D:/03_Entwicklung/TIA_MCP/artifacts/goal-wave6-20261021/explorer/`; the original report and test bytes remain retained there. Current documentation fixes five text-encoding sequences separately. Catalog coverage and exact source bindings pass on the integration checkout. The diff contains only tests and documentation; the final wave5 production binaries, full aggregate and package qualification remain applicable. Focused browser/unit/refusal and exact relocated-viewer checks qualify this test-only change without rebuilding unchanged binaries. The source binding validator reports unavailable ignored native evidence in this clean worktree explicitly; those records remain in the original main workspace.
