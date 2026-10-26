# Project explorer implementation plan

**Release scope update — 15 October 2026:** The user requires every feasible capability before 1.0. Read the [current release ledger](release-obligations-20261015.md) before scheduling work. Older wording that permits optional expansions, selected-only release scope or a reduced 1.0 is superseded. Missing prerequisites and unresolved feasibility remain release blockers; recorded passed/failed evidence and native/UI scheduling boundaries remain intact.

Status: E0–E4 implementation delivered and locally qualified within the recorded fixture scope, 8 October 2026. Remote and desktop-product host acceptance remain pending. Initial native target: TIA Portal V21. The additive API and viewer are documented in [project explorer](../guides/project-explorer.md); [qualification evidence](../verification/project-explorer-qualification.md) distinguishes passed gates from pending acceptance. The requirements below remain the acceptance plan; blocked remote acceptance is not a completed release gate.

Build one queryable project model for agents and a read-only visual explorer for people. The first delivery covers exposed PLC software and hardware inventory. HMI coverage arrives through the separate [HMI integration plan](hmi-integration-plan.md); graphical network previews arrive through the [typed LAD/FBD builder plan](typed-lad-fbd-builders-plan.md). The shared contract milestone belongs to these three streams, not a fourth project.

## Relationship providers and collection scope — 26 October 2026

Implemented offline, **not natively qualified** ([record](../verification/explorer-relationship-providers-20261026.md)):

| Area | Implemented | Still unqualified or out of scope |
| --- | --- | --- |
| Configured network | Subnet and IO-system nodes; `connectedSubnet`, `ioSystemController`, `ioSystemDevice` edges from hardware network interfaces | Native fixture run; cables, ports and physical topology stay unsupported |
| Technology objects | `technologyConnection` edges to exact hardware paths from `technology_read` | Native run on a fixture with connected technology objects; tag/block targets stay unresolved |
| Software units | `unitRelation` edges from captured unit relations; unique typed names in a complete capture resolve | Native run on a unit fixture with relations |
| PLC collections | Watch/force tables and groups, external sources and groups | Native capture of populated tables/sources |
| Scope | Every node kind declares its providers or an `unsupported` reason (`providerScope`) | PLC alarm text lists and project/global libraries are reported unsupported, not captured |

The native step is `scripts/qualify_project_explorer_relationships.py`. It must run only when the TIA machine is free.

## Intended experience

A user selects a project and sees a searchable tree of devices, PLCs, software units, groups, blocks, data types, tags and technology objects. Selecting an item opens its details and supported relationships. Breadcrumbs and exact object references let the user tell the agent precisely which object to inspect or change. The agent can answer the same questions through structured tools without reading screenshots.

The interface always shows the project copy, connection host, capture time and coverage. An HMI device can be visible while its internals are unsupported. A device tree is not proof of a complete rack or network topology. Missing, inaccessible, truncated and unsupported data must remain distinguishable from an empty collection.

## Current foundation and actual gaps

| Existing component | Reuse | New work |
| --- | --- | --- |
| `ProjectIndexTools.cs`, `OpennessSession.Index.cs`, `ProjectIndexStore.cs` | Bounded snapshots, search, authenticated cursors, expiration, invalidation | Explicit hierarchy, empty groups, paged child queries and provider coverage. |
| `ObjectIdentity.cs`, `object_resolve` | Qualified project/PLC/scope paths | Snapshot-bound references, parent relationships and stale-selection rules. Paths are not persistent GUIDs. |
| `project_inventory`, `software_inspect`, `hardware_inspect`, `technology_read` | Existing metadata and selected details | Normalize detail responses and classify unavailable fields. |
| `call_graph`, `symbol_trace`, `change_impact` | Bounded static relationships with uncertainty | Common edge schema, filtering, evidence links and graph display. |
| `ArtifactTools` and `ArtifactStore` | Hashed, bounded exports from the TIA host | Portable explorer snapshots and selected-view exports. |

The integrated viewer and additive project model now enumerate native parent groups and empty groups independently of legacy index rows. Inferring ancestors from paths can only provide virtual navigation placeholders. Detailed hardware connectivity, HMI internals and library version relationships remain bounded by their respective provider evidence.

## Shared data contract

Add a versioned `project-model.v1` contract and fixtures before building UI-specific response shapes. Preserve the existing `project_index` behavior; introduce additive capabilities or a separately versioned model endpoint. Initial schema fields are proposals to validate in implementation:

- Snapshot: `schemaVersion`, `snapshotId`, worker/session generation, project-copy identity, capture timestamps, scoped `metadataRevision`, requested limits, measured usage and provider coverage.
- Node: snapshot-local `nodeId`, `parentId`, kind, display name, owning device/PLC/software scope, `origin`, protection/access flags and detail availability. Native nodes carry their exact canonical `objectPath`; virtual folders carry only presentation identity and cannot become mutation targets.
- Edge: source and destination references, relationship kind, evidence origin, confidence/resolution state, source artifact/location, capture time and applicable content revision. Preserve unresolved endpoints as explicit records.
- Coverage: status per provider and scope, visited/returned counts, truncation reason, warnings and continuation availability. Never make a whole-project completeness claim from the current metadata index.

Native groups and virtual presentation folders need different origins. Enumerate native groups independently to preserve empty groups; synthesize only necessary structural ancestors with `origin: virtual`. Preserve case-sensitive canonical identities, namespace segments, Unicode and duplicate display names across PLCs and units.

Containment, calls, instance uses, reads, writes, declared type uses and configured I/O associations are distinct edge kinds. A text mention is not a proven dependency; an address overlap with uncertain controller ownership is not a confirmed hardware binding. Carry existing unresolved evidence through unchanged.

Metadata and content have separate freshness. The current `metadataRevision` does not certify PLC logic, HMI screens or detailed hardware settings. Fetch details with their own source hash or applicable revision and timestamp; do not silently combine mismatched captures into a supposedly consistent graph. Where atomic cross-provider capture is unavailable, expose the capture interval and mixed-snapshot warning.

## Proposed tool surface and operating rules

The following additive APIs are implemented, also exposed as versioned operations on the shared `project_model` endpoint. See the API document for the exact request envelopes and bounds.

| Proposed operation | Inputs and result |
| --- | --- |
| `project_model_capture` | Explicit project/scope and budgets; returns snapshot, roots and coverage. |
| `project_model_children` | Snapshot, parent reference and authenticated cursor; bounded deterministic child page. |
| `project_model_search` | Snapshot, text/kind/scope filters; bounded hits with ancestor references. |
| `project_model_details` | Exact reference and requested detail facets; revision-labelled details or typed unavailable state. |
| `project_model_relationships` | Exact reference, edge kinds, direction, depth and limits; bounded graph plus unresolved records. |
| `project_model_export` | Selected snapshot/view and format; artifact manifest, hash and coverage. |

Retain explicit verify/release operations and existing terminal-timeout semantics. Expired snapshots require recapture; never replay an operation with unknown worker outcome. UI pagination reads cached pages lazily; native traversal remains bounded on the worker STA. Lazy rendering alone does not make an initial native capture incremental.

Reuse current limits initially: at most 50,000 visited objects/rows, depth 64, 32 MiB accounted snapshot data and 200 rows/512 KiB row payload per page. Groups consume the same budgets. Propose graph defaults of depth 1, 100 nodes and 200 edges, with hard ceilings of depth 5, 500 nodes and 1,000 edges; return explicit truncation and narrow-scope suggestions.

Selection sent to the agent contains project copy, session generation, snapshot ID and exact path. Re-resolve before fresh reads or proposed edits. A renamed, deleted or ambiguous object requires selection repair; never substitute a same-named object. Later edits use existing prepare/apply/recovery workflows with their own target freshness checks, not the explorer's metadata hash.

## Delivery milestones and executable work packages

### E0 — Freeze the shared model and capability boundaries

Add schemas under `contracts/`, pure fixtures, provider interfaces and feature-catalog entries. Agree with the HMI and builder streams on references, coverage, revisions and preview attachments. Agree with library management on future type/version identities without implementing its lifecycle here.

Acceptance: contract validation covers native/virtual parents, unsupported providers, unresolved edges and metadata/content distinctions; old `project_index` fixtures remain compatible. Publish a coverage table showing exactly which provider supplies each view.

### E1 — Implement hierarchy and structured queries

Extend native traversal in a dedicated `OpennessSession.ProjectModel.cs` and reuse bounded storage patterns. Add host/core routing and `ProjectModelTools.cs`. Enumerate actual groups, preserve empty containers, add child lookup and search breadcrumbs, and keep native proxies out of the cache.

Acceptance: independent expected-tree fixtures match roots, ancestry and all paged children without duplicates or omissions. Cover two PLCs, software units, nested/empty groups, namespace collisions, unsupported software containers and budget exhaustion. Native scratch evidence verifies the supported subset against explicit expected objects.

### E2 — Ship the read-only tree and detail viewer

Create a separate `ui/project-explorer/` package with a transport-independent data client. Provide a virtualized tree, search/filter controls, breadcrumbs, detail tabs, keyboard navigation, loading/error states, refresh and an explicit send-selection action. Display offline values as declarations, not live PLC values.

Deliver a self-contained artifact viewer with embedded snapshot JSON and no network requirement. Then add an MCP App adapter where host capability negotiation permits it; retain structured responses and artifact fallback. MCP Apps describes UI resources, sandboxed views and negotiated support in its [official overview](https://apps.extensions.modelcontextprotocol.io/api/documents/overview.html). Host compatibility must be tested rather than assumed.

Acceptance: representative desktop widths, light/dark themes and keyboard operation pass UI checks. Selecting identical names in different scopes produces different exact references. Stale, partial, protected and disconnected states are visible; no UI action mutates the TIA project.

### E3 — Add useful relationship and hardware views

Adapt existing analysis results into bounded call, reader/writer and dependency views. Start from a selected object, support one-step expansion and link every edge to evidence. Provide module hierarchy and exposed address/channel details first. Add configured subnet, interface, port or rack-position views only after separate extraction fixtures establish those relationships.

Acceptance: known calls and readers/writers match native/export evidence; unresolved aliases remain visible; cyclic graphs terminate under limits. Missing physical layout or port connectivity produces an unsupported state, not an invented cable or rack position. HMI bindings plug in only when the HMI adapter supplies resolved evidence.

### E4 — Integrate, qualify and package

Add HMI node/detail/relationship providers from the HMI stream and supported LAD/FBD preview attachments from the builder stream. Keep the PLC/hardware MVP independently releasable. Export JSON snapshots and selected graph SVG/PNG artifacts with timestamps, filters and coverage manifests; escape project text in HTML/SVG.

Package UI assets with hashes in release bundles. Test the same reader against local Windows and SSH/VM transport: TIA remains on its host, paths retain host ownership, and files move through artifact transfer. The browser never opens a raw unauthenticated engineering API. Any later local preview service needs loopback binding, per-session authentication, origin checks and a read-only allowlist.

Acceptance: release relocation tests find all UI assets; unsupported hosts receive usable artifacts; reconnect invalidates old session references; no network call is needed to view an exported snapshot. Trace one selected object from UI through exact reference resolution and independent tool inspection.

## Verification, performance and release gate

Run contract and pure model tests without Siemens, then browser component/integration tests against deterministic recorded responses. Include adversarial labels, escaped paths, stale cursors, renamed/deleted nodes, partial captures, protected blocks, empty projects, delayed responses and session loss. Regression checks must prove no project-mutating route is reachable through the viewer adapter.

Benchmark synthetic 1,000/10,000/50,000-node trees and maximum graphs on a documented reference machine. Initial UI targets: cached search/child response p95 below 200 ms, first viewport below 1 second after data arrival and a bounded 500-node graph usable within 2 seconds. Record warm/cold runs, peak memory, bytes, browser and hardware; these are proposed acceptance targets, not existing measurements.

Measure native capture, detail extraction, transport and rendering separately. Record local and VM results on small/medium/large real fixtures with exact object counts and installed versions. Use those measurements to set scoped capture budgets before release; pure cache timings cannot certify native capture latency. Maintain responsive loading/cancellation UX without claiming cancellation has stopped an in-flight Siemens call.

Release when the PLC/hardware tree, exact selection, supported details, bounded relationships, freshness display, export fallback and local/VM packaging pass their gates. Explicitly publish remaining HMI, hardware topology and graphical-preview limitations. The largest risks are expensive native traversals, misleading inferred relationships, stale content presented as current and scope growth into an engineering editor; the staged providers and evidence contract address these directly.
