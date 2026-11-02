# Explorer performance: warm/cold, memory and payloads — 1 November 2026

This record fills the missing warm/cold timings, memory and payload sizes next to the existing
1k/10k/50k tree and 500-node graph measurements ([qualification record](project-explorer-qualification.md#performance-measured-separately),
[1.0 checklist §8](../history/1.0-release-checklist-20261013.md#8-close-explorerreview-acceptance)). It measures the
cached model and the browser viewer on synthetic fixtures only: no TIA traversal, no MCP transport and no desktop
host. Native capture timings stay in the qualification record.

## Reproduce

From the repository root, after `npm ci` (Playwright 1.64.1) and with Chrome installed. Both scripts write
fresh JSON and exit non-zero when a target is missed.

```powershell
node ui/project-explorer/tests/benchmark.cjs artifacts/explorer-perf-20261101/cache-performance.json
node ui/project-explorer/tests/browser-performance.mjs artifacts/explorer-perf-20261101/browser-performance.json
```

[`benchmark.cjs`](../../ui/project-explorer/tests/benchmark.cjs) builds the detached index in Node and now also
records the first (cold) search and child query after indexing, the byte size of a 200-row search and child page,
and the process's peak resident set. [`browser-performance.mjs`](../../ui/project-explorer/tests/browser-performance.mjs)
loads each fixture into the packed viewer in headless Chrome: cold is a new browser context, warm a second load in
the same page; it samples the Chrome process tree about every 50 ms.

## Conditions

PCNico: AMD Ryzen 7 9800X3D (16 logical processors), 64 GB, Windows 11 build 26300, Node 24.22.1, Chrome
157.0.8103.26 headless. Run at 22:25 UTC with no TIA Portal process; other agents' idle .NET build servers and one
OPC UA test process were running. Source: main at `1fd8d4bc` plus the benchmark change in this PR
(`artifacts/explorer-perf-20261101/source.diff`). The 8 October numbers came from another machine
(Intel Core Ultra 7 155H); compare them with care.

## Results

Payloads and the cached model (Node):

| | 1k | 10k | 50k |
| --- | --- | --- | --- |
| Snapshot JSON | 520,633 B | 2,276,087 B | 10,158,087 B |
| Search page (200 rows) / child page (200 rows) | 64,655 / 38,441 B | 64,655 / 38,442 B | 64,655 / 38,443 B |
| Index build (cold) | 3.7 ms | 17.2 ms | 70.5 ms |
| First search / first child page (cold) | 0.5 / 0.1 ms | 0.5 / 0.0 ms | 1.8 / 0.0 ms |
| Search / child p95, 50 warm queries | 0.2 / 0.0 ms | 0.4 / 0.0 ms | 1.8 / 0.0 ms |
| Peak resident set of the Node process (cumulative) | 53.3 MB | 77.7 MB | 171.3 MB |

Viewer (headless Chrome):

| | 1k | 10k | 50k |
| --- | --- | --- | --- |
| First viewport, cold / warm | 36.5 / 8.9 ms | 43.0 / 16.1 ms | 83.0 / 60.3 ms |
| First cached search (cold) / search p95 of 100 (warm) | 0.2 / 0.1 ms | 1.3 / 0.5 ms | 4.1 / 2.3 ms |
| Rendered tree rows, cold / warm / scrolled | 27 / 26 / 21 | 27 / 26 / 21 | 27 / 26 / 26 |
| JS heap used after cold / warm load | 7.9 / 7.1 MB | 23.2 / 28.4 MB | 102.0 / 126.1 MB |
| Chrome process tree, largest sample: working set / private | 517.6 / 319.7 MB | 567.8 / 359.4 MB | 794.6 / 597.1 MB |
| Data delivery into the page (Playwright, not transport) | 140 ms | 553 ms | 2,272 ms |

The 500-node / 1,000-edge graph: SVG export 341,305 bytes, built in 0.9–1.9 ms in Node; rendered in the viewer in
55.5 / 55.5 ms (1k page), 63.9 / 46.2 ms (10k) and 55.5 / 55.6 ms (50k), cold / warm.

Every target holds: cached queries p95 below 200 ms, first viewport below 1 second, the graph below 2 seconds and
fewer than 70 rendered rows. Over the whole browser run the largest process-tree sample was 1,271.7 MB working set
(71 samples, 9 processes); working sets count shared pages more than once, so it is not a certified peak.

## Artifacts

`artifacts/explorer-perf-20261101/` (local, ignored): `cache-performance.json`, `browser-performance.json`,
`browser-performance-memory-samples.json`, `browser-performance.log`, the packed viewer, `source-commit.txt` and
`source.diff`.

## Limits

- Synthetic fixtures; a native 50,000-object project would also pay TIA traversal, which the
  [qualification record](project-explorer-qualification.md) measures separately (2,070 nodes in 10.2 s).
- Rendering inside a desktop MCP Apps host (VS Code) is not timed here. The roundtrip passed
  ([record](explorer-desktop-20261101.md)); timing it needs the desk, because the view renders only in the
  interactive editor.
- MCP stdio and SSH/VM transport of pages and exports are not measured; the VM stays open (B07).
- One run on one machine; the repeatability gate for 50k file imports is the
  [portable performance acceptance](../reference/project-explorer-performance.md).
