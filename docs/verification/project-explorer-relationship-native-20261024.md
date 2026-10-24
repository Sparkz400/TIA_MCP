# Native explorer relationship scope and performance — 24 October 2026

Evidence: [`evidence/local.explorer.relationship-scope.20261024.json`](../../evidence/local.explorer.relationship-scope.20261024.json)
(source `53a3807`, clean; raw report `explorer-01.json`).

`scripts/qualify_project_explorer.py` (default profile) ran once on a new owned TIA V21
scratch project (73 captured nodes) and passed. It adds three checks to the earlier
native explorer run, against the [declared providers and budgets](../reference/project-explorer-relationship-scope.md).

## Completeness within the declared bounds

- **Exact authored edges.** The depth-1 graph from `TiaMcp_EnableGate` must equal the
  edges authored from the fixed scratch SCL source, with nothing missing or added:
  - seven resolved edges: calls OB→FB and FB→FC; the DB's `instanceOf`/`usesType`/`dependsOn`
    to the FB; `dependsOn` OB→FB and FB→FC;
  - eight unresolved FB member edges: reads/writes of `Enable`/`Active` in local and
    instance-qualified form, plus their `dependsOn`.

  Member names never resolve to project objects.
- **Every retained edge within budget.** A depth-5, 500-node, 1,000-edge traversal
  returns all 21 retained native edges:
  - `relationshipScope.status` is `complete` with no stop reasons;
  - native `scopeComplete` is true;
  - every endpoint is owned by one of the four fixture blocks;
  - `projectCompleteness` stays `notEstablished`.
- **Visible truncation.** A 3-edge budget returns 3 edges with `truncated: true` and
  stop reasons `depth` and `edges`.

The native provider also reports `instanceOf` and `dependsOn` from `Main` to the instance
DB for the instance call, and the FC's own reads/writes of `Enable` and its return value.
The oracle accepts these resolved fixture-internal edges at full depth; it does not
require them.

## Native performance (single owned session)

| Measurement | Result |
| --- | --- |
| Hierarchy capture, cold / warm (73 nodes) | 187 ms / 136 ms |
| Details (block, hardware) | 255 ms, 372 ms |
| Relationships (four queries, fresh native read each) | 149–541 ms |
| Cached search over MCP, 25 calls | median 5.9 ms, p95 16.0 ms |
| Relationship payloads | 27.6–99.6 KB (limit 512 KiB) |
| Snapshot JSON / HTML export | 134,840 / 286,877 bytes |
| Resident memory: TIA Portal / Host / worker | 2.62 GB / 127 MB / 98 MB; unchanged by warm recapture |

These numbers add native cold/warm, memory and payload observations to the existing
1k/10k/50k tree and 500-node graph measurements in the
[qualification record](project-explorer-qualification.md), which remain unchanged.

## Reconciliation against the plan's targets

The [explorer plan](../plans/project-explorer-plan.md) set the targets below. Each
result points to its existing record. No earlier miss is rewritten.

| Target | Measured | Result |
| --- | --- | --- |
| Cached search/child p95 < 200 ms | Native MCP: 16.0 ms (73 nodes, today); 47.5 / 62.0 / 79.6 ms (93 / 405 / 2,070 nodes); detached 1k/10k/50k cache 72.1 / 32.4 / 51.9 ms ([record](project-explorer-qualification.md)) | Met uncontended. An earlier run under concurrent native load stays retained as a miss at 237.1 ms. |
| First viewport < 1 s after data arrival | Portable 50k-node/50k-edge import: 80 of 80 imports below 1,000 ms, maximum 427.7 ms ([record](project-explorer-performance-20261021.md)) | Met. The earlier 1,004.1 and 1,121.3 ms misses used another harness and stay retained. |
| 500-node graph usable < 2 s | 85.9–301.9 ms cold/warm at 1k/10k/50k ([record](project-explorer-performance-20261021.md), [record](project-explorer-qualification.md)) | Met |
| Separate native capture, detail, transport and rendering measurements | Capture 187 ms / 73 nodes (warm 136 ms), 1,004 ms / 93, 3,855 ms / 405, 10,237 ms / 2,070 nodes; details 255–372 ms; relationships 149–541 ms | Measured locally. The VM column stays open, because the VMware guest is a user blocker. |
| Warm/cold, memory and payload evidence | Native cold/warm and Portal/Host/worker memory (this record); 50k detached test-process peak 637.8 MB; sampled browser process tree up to 923 MB; payloads below 512 KiB | Recorded |

**Capture budgets.** Native capture costs roughly 5–11 ms per node (about 2.5 ms per
node at 73 nodes). The existing ceiling of 50,000 visits would therefore take several
minutes, and capture runs sequentially and does not stop an in-flight Siemens call.
The declared budgets remain unchanged: capture `maxVisited` up to 50,000, and graph
queries up to depth 5, 500 nodes, 1,000 edges and 512 KiB.

For projects with more than a few thousand objects, capture a narrower `plcPath` or
`scopePath`, or a lower `maxVisited`. A bounded capture reports partial coverage
explicitly.

## Not covered

- Larger generated programs, a second PLC, HMI or library relationships and positive
  configured-I/O edges were not part of this run.
- Protected and indirect references were not exercised either.
- Whole-project relationship completeness is never claimed.
- The timings come from one machine and are not a contention-independent guarantee.
- Desktop MCP Apps host acceptance stays open and needs a host product; see the
  [manual procedure](../guides/project-explorer.md#desktop-mcp-apps-host-acceptance-manual-open).
