# Project explorer

The explorer is a read-only browser and a structured `project-model.v1` API over the same exact objects. It uses the existing offline inspection, analysis and artifact transfer routes. It does not open a network engineering service or expose a second change workflow. Existing `project_index` requests and response semantics are unchanged.

## Run it

Open `ui/project-explorer/index.html` in a current browser and choose **Open snapshot**. A snapshot can also be packaged into a single HTML file, with no network or runtime dependencies:

```powershell
node ui/project-explorer/pack.mjs artifacts/project-explorer-native-live.snapshot.json artifacts/explorer.html
```

The completed local delivery is `artifacts/project-explorer/native-viewer.html` (real native PLC/hardware details and LAD/FBD previews) and `artifacts/project-explorer/hmi-native-viewer.html` (real Classic Comfort screen details; the provider reports native screen rendering unsupported). These are historical snapshots, with their capture time and freshness shown. The exact final worker HTML is `artifacts/project-explorer-final-native-complete.html`. The frozen runnable MCP package is `artifacts/releases/tia-mcp-v21_20261008_125230_8b2f8ce9` with adjacent `.zip` and `.mcp.json`; its hashes and native checks are recorded in [qualification](../verification/project-explorer-qualification.md).

For live inspection, start the normal MCP host in the authorized engineering user's session, explicitly attach to the intended TIA process, and invoke:

```json
{"name":"project_model_capture","arguments":{"request":{"projectName":"Exact open project name","maxVisited":50000,"maxDepth":64,"maxBytes":33554432,"lifetimeSeconds":900}}}
```

The response supplies the project copy, host, session generation, snapshot identity, native capture interval, roots and scoped coverage. Use its `snapshotId` for children/search, and the exact returned reference for details and relationships. Export a portable viewer through the same host:

```json
{"name":"project_model_export","arguments":{"request":{"snapshotId":"returned snapshot ID","format":"html"}}}
```

The artifact manifest contains a host-owned path, byte length and SHA-256. Use `artifact_read` to transfer it from a VM in bounded chunks; client files are not remote project paths. `format: "json"` exports the underlying document. Browser graph exports provide SVG or PNG plus a JSON manifest containing timestamp, filters, coverage and evidence.

Clients advertising the MCP Apps extension and `text/html;profile=mcp-app` receive `ui://tia-project-explorer/view.html`. The resource has no network domains and the browser negotiates host capabilities before calling tools. Clients without negotiated support retain structured results and HTML export. Protocol tests verify both cases; that is separate from qualification of a particular desktop product. The implementation follows the [MCP Apps specification](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx).

### Desktop MCP Apps host acceptance (manual, open)

VS Code 1.140.0 has passed [desktop rendering and retained-snapshot interaction checks](../verification/explorer-desktop-20261025.md), including resize negotiation and historical-selection refusal. Full production Host/native acceptance remains open. Register the engineering Host as a stdio server in that product, then record each step with its product name and version:

1. **Negotiated resource loading.** `tools/list` exposes `_meta.ui.resourceUri` only after the product advertises MCP Apps; the product loads `ui://tia-project-explorer/view.html` with no network requests (check the product's developer console).
2. **Exact selection roundtrip.** After `project_model_capture`, select a block in the embedded tree, send the selection, and confirm the agent's next `project_model_details` call carries all six reference fields unchanged and matches an independent `block_read`.
3. **Stale and disconnected states.** Run `tia_detach`, then reuse the selection: the view shows the stale/disconnected state and the call returns `SessionMismatch`, `StaleSelection` or `SnapshotInvalidated`, never another object.
4. **Artifact fallback.** In the same product with MCP Apps disabled (or in a product without it), the same capture returns structured results and `project_model_export` HTML that opens offline.

Keep screenshots and the product's MCP log with the record. Browser protocol fixtures (`browser-native.mjs`) do not substitute for this check.

## Tools and selection

Every operation is available through `project_model` with `request.action`; additive named tools expose capture, children, search, details, relationships, verify, release and export. There is no mutation action. Any future edit entry point must call the existing `change_prepare`/`change_apply` workflow and its content freshness/recovery checks.

| Action | Request | Result and bound |
| --- | --- | --- |
| capture | projectName, optional plcPath/scopePath; maxVisited, maxDepth, maxBytes, lifetimeSeconds | Header and roots. At most 50,000 visits/nodes, depth 64, 32 MiB accounted JSON; two retained snapshots; 30–900 seconds. |
| children | snapshotId, parentId (null for roots), maxItems, cursor | Deterministic child partition. At most 200 rows and 512 KiB per page. |
| search | snapshotId, text, kind, plcPath, scopePath, maxItems, cursor | Cached matches with ancestors and exact references. Cursor binds snapshot and original filters. |
| details | reference; optional maxItems/facet options | Native metadata verification followed by separately timestamped details, content evidence and preview provider responses. |
| relationships | reference, kinds, direction, depth, maxNodes, maxEdges | Default depth 1 / 100 nodes / 200 edges; ceilings 5 / 500 / 1,000, with bounded payload and truncation. |
| verify | snapshotId; optional reference | Recaptures the original bounded metadata scope; cannot certify logic or project-wide content. |
| release | snapshotId | Drops cached data and cursors. |
| export | snapshotId, format: json or html | Hashed host artifact; observations retain their individual timestamps and coverage. |

An agent selection has exactly these context fields:

```json
{
  "schemaVersion": "project-model.v1",
  "snapshotId": "returned snapshot",
  "sessionGeneration": "returned worker generation",
  "projectPath": "exact project-copy path on the TIA host",
  "nodeId": "returned snapshot node",
  "objectPath": "exact case-sensitive canonical path"
}
```

These are scoped paths, not permanent Siemens GUIDs. Rename, deletion, ambiguity, expiry, reconnect or observed metadata changes require recapture and explicit selection repair. There is no fallback to display names. Virtual folders have no object path and cannot be sent as native selections. Offline copying preserves historical context; sending through a live host requires verification.

The viewer supports a virtualized tree, text/type/scope filtering, breadcrumbs, keyboard navigation, details, hardware metadata, coverage, previews and bounded relationship expansion. Arrow keys navigate/expand; Enter selects; Home/End move within the current tree. Refresh recaptures and clears the selected object. Loading and transport failures are explicit; closing a view does not claim to cancel an in-flight Siemens operation.

## Coverage and freshness

| View | Provider | Boundary |
| --- | --- | --- |
| Project/device/item hierarchy | Native V21 engineering compositions | Includes native empty groups. Exposed composition is not proof of physical wiring or rack/network topology. |
| PLC blocks/types/tables/tags/constants | Native typed collections and canonical identities | Software-unit scope, native groups and namespace display folders remain distinct. Protected/unavailable objects are reported. |
| Block/type/interface and hardware details | Existing inspection routes | Offline declarations, never live PLC values. A failed service is not an empty object. |
| Calls/instances/reads/writes/dependencies | Existing bounded offline analysis | Every edge retains native/export provenance. Unknown member endpoints and uncertain aliases remain unresolved. |
| Configured I/O | Existing address/ownership analyzer | Address overlap without verified controller ownership does not become a confirmed hardware binding. |
| Subnets, IO systems and their members | Native project subnets plus the existing configured-network inspection | `connectedSubnet`, `ioSystemController` and `ioSystemDevice` need an exact captured endpoint and native owner. Selecting a subnet or IO system scans all bounded network interfaces (`maxNetworkVisited`, default 20,000, ceiling 50,000). Not cables or ports. |
| Technology-object hardware connections | Existing `technology_read` reference renderer | `technologyConnection` resolves only references with an exact hardware path; tags and blocks stay unresolved. |
| Software-unit relations | Captured `PlcUnitBase.Relations` | Native relations are names; `unitRelation` resolves only a unique unit or PLC-root DB name in a complete capture. |
| Watch/force tables and external sources | Native PLC collections | Names and groups only; no live monitoring, forcing state or source text. |
| PLC alarm text lists | Native `PlcAlarmTextlistGroup` per PLC/software unit | `alarmTextList` nodes with ID, range and comments under a virtual folder; coverage is `partial`. Entries and texts come from `plc_alarm_inspect`. No relationships. |
| Libraries | Unsupported in the tree | Reported as `unsupported` coverage; libraries use the library tools. |
| HMI hierarchy/details/relationships | HMI workstream provider | Family/section-specific coverage and optional services; screen items load on demand. No duplicated HMI adapter. |
| Typed LAD/FBD preview | Typed workstream provider contract | Separate instruction graph and source hash; unsupported/opaque content remains explicit. No duplicated instruction compiler. |
| Physical ports/cables/topology | Unsupported | No invented topology derived from display ancestry. |

`metadataRevision` hashes only the scoped exposed metadata. It excludes PLC logic, detailed settings and HMI/graphical content. Detail exports and provider attachments have their own content revisions. Observations captured at different times carry a mixed-capture warning; exported metadata and detail timestamps are retained. External edits are detected only by explicit native verification of the covered metadata. Expired snapshots remain useful historical artifacts but cannot establish a current live selection.

## Validation and release evidence

Run the model and host checks independently of Siemens:

```powershell
dotnet run --project tests/TiaMcp.ProjectModel.Tests -c Release
python -B tests/ProjectModelContracts.py
python -B scripts/test_project_explorer_host.py
npm --prefix ui/project-explorer test
npm --prefix ui/project-explorer run test:browser
npm --prefix ui/project-explorer run benchmark
```

The repository root also exposes `npm run test:explorer` and
`npm run test:explorer:browser`. Install the locked Node dependencies with
`npm ci` and the browser with `npx playwright install chromium`. The browser
runner resolves the project-local Playwright package and prefers its installed
Chromium; `EXPLORER_BROWSER` can select another explicit executable. It writes
new run directories beneath `artifacts/project-explorer/runs/` and
`output/playwright/project-explorer/`. Optional `--output` and `--screenshots`
arguments must name fresh directories. The generated `e2e/example.spec.js`
tests Playwright's documentation site and is separate from these explorer checks.

The schema test requires `jsonschema>=4.18`; its optional workspace test-only installation is `artifacts/project-model-schema-deps`. Browser test dependency setup and recorded browser version are documented in the UI package. Neither dependency is required by the delivered viewer.

`scripts/qualify_project_explorer.py` starts a new worker-owned scratch instance, creates independent expected empty groups through prepared changes, enumerates every child page, checks the legacy index, inspects an exact block separately, queries relationships, verifies metadata, transfers exports and rejects stale/session-loss and mutation requests. `--seeder` enables the existing independent native software-unit fixture; `--two-plcs --count 250` adds a second PLC and a measured medium fixture. `--config` tests the actual authenticated local/SSH launch configuration without fallback. Never run native qualification under a different user's session or weaken pipe permissions to make a sandbox pass.

`--typed-previews` prepares the fixed native LAD/FBD fixtures and validates the preview against the original XML and SHA-256. `scripts/qualify_project_explorer_io.py` independently creates the exact configured-I/O fixture through prepared changes, then verifies controller ownership in both graph directions. Its `--verify-report` mode reopens the saved fixture without mutations. `scripts/recheck_project_explorer.py --fixture-report <completed-report> --host <release-host> --worker <release-worker> --report <new-report>` checks a final release against a previously qualified saved PLC fixture, including real graph directions and actual parent-depth bounds, without regenerating, compiling or saving the project.

`scripts/qualify_project_explorer_relationships.py` qualifies the configured-network, technology-connection and unit-relation providers and the new collections against independent `hardware_inspect`, `technology_read` and `software_inspect` reads. It creates its own subnet/IO-system fixture through prepared changes, plugs a TM PosInput 2 module and lets the registered `scripts/native/UnitSeeder` add two related software units and a connected external-encoder TO; `--verify-report` reopens it read-only and `--project` audits another saved project read-only. See the [provider record](../verification/explorer-relationship-providers-20261026.md).

Every relationship response includes `providerScope`: the providers and edge kinds declared for the selected node kind, or `unsupported` with a reason.

`scripts/qualify_project_explorer_tags.py` qualifies a native scale fixture with 2,000 independently authored Byte tags imported through one prepared table change. It validates all names/types/addresses from full native XML, all search/child pages, exact details/relationships, metadata freshness, exports and lifecycle boundaries. `--verify-report` continues read-only against the exact already-saved fixture. The completed 2,070-node result is separate from the earlier function-heavy generation failure and its preserved recovery evidence. `scripts/verify_project_explorer_hmi.py` reopens the exact HMI fixture read-only, checking owner/layer/rectangle data and original XML provenance through the common provider.

For focused cold/warm browser and sampled process-tree memory measurements, run `node ui/project-explorer/tests/browser-performance.mjs`. The report separates Playwright data delivery from rendering and cached queries; it does not measure native capture.

`node ui/project-explorer/tests/browser-native.mjs` is an opt-in native end-to-end check against the exact saved owned fixture and final release named in its arguments/defaults. It starts a new owned TIA session, opens that fixture read-only, drives the actual resource HTML through a headless MCP App parent, verifies browser selection against the native worker and independent `block_read`, then detaches. Its Playwright binding relays only the viewer's read-only model/artifact tools to the authenticated stdio host; it opens no HTTP engineering listener. This test host does not certify a particular desktop MCP Apps product.

Evidence is recorded separately for pure cache, browser, native and transport checks. The [qualification record](../verification/project-explorer-qualification.md) records actual results and outstanding gates. Synthetic 50,000-node measurements are not measurements of a real 50,000-object TIA traversal.

Release bundles include all viewer assets and schemas in their hashed manifests; missing resources fail bundle verification. Relocation tests load resources through the relocated MCP host and open a detached exported HTML file.


## Technology children

The native capture source now places `OutputCams` and `MeasuringInputs` service
children beneath their technology owner, preserving `ownerPath`, `composition` and
an exact owner-relative object path. The live viewer can request a technology
object's first child page; it no longer requires the children to be cached before
showing an expand control. Same-named children in different compositions retain
separate details and selection handoffs. Existing snapshots still load.

[Verification](../verification/technology-children-20261017.md) includes actual
browser protocol-fixture tests and screenshots. This is not a new Siemens capture
or live engineering-host qualification. Other service-owned content remains outside
this extension; warnings and capture bounds must still be reviewed.

## PLC library-instance observations

Captured PLC blocks and types can carry `metadata.libraryInstance` with an
observed type/version GUID pair. `linked`, `noService`, `notApplicable` and
`unavailable` distinguish the supported service observations; only `linked`
contains GUIDs. The `plcLibraryInstances` coverage remains partial. Saved reviews
retain the original record without authenticating imported data or proving
library exports, dependency closure, runtime instance identity or safe updates.
Older snapshots simply lack this observation. See the [implementation and native
testing gates](../verification/plc-library-association-20261018.md).

Linked observations can also retain `dependencies`, with an ordered
`requiredVersionGuids` list when its status is `observed`. An empty observed list
means enumeration completed with no entries; `unavailable` has no list and gives
a reason. Missing dependency fields in older snapshots remain missing. Repeated
GUIDs and self references are retained as observed, without inferring a complete
dependency graph or matching them to objects by name.

Each list is limited to 1,000 entries. One shared budget allows 20,000 dependency
collection/iterator read attempts across a capture; exhaustion prevents further
dependency reads and discards any incomplete list. The snapshot exposes this
limit and usage, with partial `plcLibraryDependencies` coverage. A known root
library identity remains available even when its dependencies cannot be read.
See the [dependency capture checks and native gates](../verification/plc-library-dependency-observations-20261018.md).
