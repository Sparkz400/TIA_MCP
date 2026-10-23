# Project explorer

A read-only browser for `project-model.v1`. It uses the exact snapshot/session/project/node/object references exposed by the agent tools. The browser has no engineering HTTP endpoint and makes no network requests. Mutation tools are excluded from the client and host allowlists; future edits must enter the existing prepared-change workflow.

CI runs `npm run test:explorer` and `npm run test:explorer:browser` against this
viewer on Chromium, retaining each run's reports and screenshots. The separate
Playwright installation example is not a product acceptance test. The Windows
job runs `scripts/Test-Offline.ps1`; native engineering and actual desktop MCP
Apps acceptance remain separate gates.

## Open a real snapshot

Call `project_model` with `{"request":{"action":"capture","projectName":"<open project>","maxVisited":50000}}`, then use `details` and `relationships` on exact references to capture the desired observations. Export with `{"request":{"action":"export","snapshotId":"<id>","format":"html"}}`. Transfer the returned host-owned artifact using `artifact_read`, then open the HTML in a browser. No TIA installation is required on the viewing computer.

Alternatively, open `ui/project-explorer/index.html` directly and select an exported JSON snapshot, or package one:

```powershell
node ui/project-explorer/pack.mjs artifacts/project-explorer-native-live.snapshot.json artifacts/project-explorer/viewer.html
```

`pack.mjs --empty viewer.html` creates a portable reader without project data. The generated `.manifest.json` records SHA-256 and size for the complete artifact and each source asset. No build step or runtime dependency is required. Host export uses the same `index.template.html`, CSS, model, client, and renderer.

## Interaction

- Search names and exact paths; filter object kind and software scope. Identical names in different PLCs or software units retain different references.
- Arrow keys navigate; right/left expand/collapse; Home/End jump; Enter selects. Ctrl/Cmd+F focuses project search; Escape clears the search text.
- The tree virtualizes visible rows and uses bounded lazy child pages through a live transport. Native empty groups are separate from virtual display folders.
- Details show exposed metadata and captured native interface declarations. Native artifact paths retain their engineering-host ownership; use artifact transfer to read them elsewhere.
- Relationships distinguish calls, instances, reads, writes, type dependencies, general dependencies, configured I/O and HMI bindings. Expansion is bounded to five levels, 500 nodes and 1,000 edges. Every displayed relationship has an evidence action. Unresolved endpoints remain explicit.
- Copy reference works offline. Send selection is available only through a negotiated MCP App host and performs exact reference verification first. A rename, deletion, different session or metadata mismatch requires a new selection; the viewer never repairs a selection by name.
- JSON exports retain the snapshot and export manifest. Connected exports transfer the host artifact in 64 KiB chunks using the existing `artifact_read` tool, verify every chunk's offsets/length/hash, then verify the reassembled SHA-256 before downloading. The transfer rejects files above 32 MiB and never retries automatically. SVG embeds a JSON manifest in `<metadata>`; PNG embeds the manifest in a UTF-8 `iTXt` chunk. Manifests include capture/export timestamps, filters, coverage, exact selection, limits and relationship evidence.
- Refresh requests a new bounded native capture when connected. Offline refresh opens a replacement file. Loading another snapshot clears the previous selection.

## Freshness and coverage

`metadataRevision.scope` must be `exposedMetadata` and `includesContent` must be `false`. Native detail observations and relationship evidence retain their separate capture times and content hashes. Offline browsing never verifies current content. Expired snapshots remain historical artifacts, but cannot authorize live selection. Coverage distinguishes complete, partial/truncated, unsupported, unavailable, protected and failed states. Device containment is engineering hierarchy, never inferred physical topology.

The HMI and typed LAD/FBD integrations consume `project-model-provider.v1` envelopes and `hmiScreen` / `ladFbdNetwork` structured attachments. Their owning providers supply content, coverage, provenance and revisions. The viewer never executes HMI scripts or treats raw native XML as a typed preview. Provider-unavailable acceptance is tested; live native integration depends on the owning workstream's installed provider and qualified target.

## Transport and MCP Apps

`ProjectExplorerClient.ReadOnlyClient` wraps an injected `invoke(action, args)` transport. `OfflineTransport` uses the same data model. `mcpAppTransport(app)` accepts an initialized official SDK `App`; it requires negotiated `serverTools` capability. A small bundled protocol adapter also implements the `2026-01-26` MCP Apps handshake for self-contained resources. It validates parent window, pins the responding origin, correlates request IDs, has timeouts, rejects unknown requests, and only calls the host's `project_model` tool with its actual `arguments.request` envelope.

The protocol is based on the [official MCP Apps specification](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx) and [App API](https://apps.extensions.modelcontextprotocol.io/api/classes/app.App.html). Host support is required at both the server capability-negotiation and iframe handshake layers. A deterministic browser host verifies the complete bridge, native-shaped envelopes, child paging, search, verified selection, bounded artifact transfer and rejection of corrupted bytes. `artifact_read` is a separate fixed helper, not an arbitrary tool dispatch route. This does **not** claim that a particular desktop chat host has successfully rendered the App resource. Unsupported hosts receive the same portable HTML/JSON artifact.

## Verification

```powershell
cd ui/project-explorer
npm test
node tests/benchmark.cjs ../../artifacts/project-explorer/cache-performance.json
node tests/browser.mjs --snapshot ../../artifacts/project-explorer-native-live.snapshot.json
```

Browser tests first resolve the project-local `playwright` package and prefer its installed Chromium. The repository root provides `npm run test:explorer` and `npm run test:explorer:browser`; install locked dependencies with `npm ci` and browsers with `npx playwright install chromium`. `PLAYWRIGHT_MODULE` supplies a fallback package and `EXPLORER_BROWSER` selects an explicit executable. The browser receives an explicit workspace download directory so Windows sandbox temp-directory restrictions do not cancel exports.

Browser test artifacts go to a new directory beneath `artifacts/project-explorer/runs/`; screenshots go to a matching new directory beneath `output/playwright/project-explorer/`. `--output` and `--screenshots` may select other fresh directories; existing destinations are rejected. `browser-results.json` records exact Chromium version, viewport/render timings, DOM row counts and native snapshot identity. The separate benchmark's `cache-performance.json` records CPU, OS, runtime, bytes, sampled process memory, cold indexing and warm query p95 for 1k/10k/50k objects. Browser heap readings are samples, not a certified process peak. Native capture and inspection latency is measured separately by `scripts/qualify_project_explorer.py`; cached timings do not establish native capture performance.

The browser suite covers escaped hostile labels, Unicode, repeated names, native empty groups, virtual folders, protected/partial/stale states, keyboard navigation, offline exports, provider attachments, delayed results, session loss, no-network viewing, real MCP argument shapes, 1440/1024/760px layouts, light/dark themes, and virtualized large trees and bounded graphs. Native snapshots are additional inputs; synthetic fixtures alone do not qualify native integration.

`tests/browser-native.mjs` is an opt-in end-to-end acceptance harness for an already qualified, saved scratch project. From the repository root, run `node ui/project-explorer/tests/browser-native.mjs --release <immutable-release-directory> --fixture-report <completed-native-recheck.json>` in an authorized current-user engineering context. It starts a new owned TIA instance, opens the exact saved fixture, reads the App HTML from the actual MCP `resources/read` response, and relays browser requests to the real stdio host through a bounded read-only Playwright binding. It opens no HTTP listener. The trace compares browser details, native verification and the sent six-field selection with an independent native `block_read`, checks incoming/outgoing call directions and verified artifact transfer, rejects a previous-session selection, then detaches without saving. Results are written to `artifacts/project-explorer/browser-native-results.json`; this acceptance relay does not certify a particular desktop MCP Apps product. The recorded `.ap21` before/after hash covers that file only, never the complete project revision.

## Supplied GRAPH reports

Select a block, open Previews and attach a `graph_inspect` report. The viewer verifies
retained XML bytes, labels the object association as caller supplied, and supports
sequence/item navigation plus exact source download. Export JSON to retain the
association in a derived snapshot. [Workflow and limits](../../docs/reference/graph-host-workflow.md).

## Repeatable file-import performance

The [portable performance gate](../../docs/reference/project-explorer-performance.md)
requires ten cold/warm 50k-node/50k-edge import pairs, every viewport below 1000 ms,
with independent relationship and refusal checks. Run
`node tests/browser-import-performance.mjs --output ../../artifacts/explorer-import-run --conditions "Recorded competing workload conditions"`.
Timing includes actual file read/parse/validation/indexing and two frames; it
does not inject the dataset through a browser automation binding. Native capture
and actual desktop MCP Apps acceptance remain separate.
