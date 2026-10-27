# HMI integration: operating contract

The V21 worker provides optional Classic WinCC and WinCC Unified adapters, `hmi_inspect`, `hmi_references`, `hmi_capabilities`, HMI v2 object resolution, and an actual provider for the shared project explorer. The families and exact runtime profiles are evaluated separately. Installed APIs, implemented code, native fixture qualification, and current target availability are separate response fields.

See [qualification and remaining gates](../verification/hmi-qualification.md), [native capability matrix](../reference/hmi-capability-matrix.md), [engineering and preservation](hmi-engineering.md), and the [explorer handoff](../reference/hmi-explorer-handoff.md). A passed metadata or scalar fixture does not close the full H0–H5 acceptance criteria.

## Inspection and selection

Start with `hmi_capabilities` and an empty request for optional installation evidence. Use `hmi_inspect` with `request.section = "devices"` on an attached project. Every accessible HMI software target returns its actual device/software path, family, observed catalog identifier/runtime version, and project-copy/session-bound identity. Unknown or inaccessible software remains visible with diagnostic evidence.

Pass that returned identity as `request.target` with one of `connections`, `tags`, `screens`, `alarms`, `scripts`, or `resources`; Unified targets also accept the opt-in `model` section described below. Use `items` plus the exact `screenPath` returned by screen inspection for screen children. Native names may repeat in valid separate scopes; preserve the returned paths. HMI identities never contain a fabricated PLC owner.

Example request shape (values must come from the current worker):

```json
{"request":{"schemaVersion":1,"section":"tags","target":{"hmiPath":"<returned exact path>","family":"classic"},"limit":100,"maxVisited":5000,"maxBytes":262144}}
```

Inspection returns per-section `complete`, `truncated`, `unsupported`, `unavailable` or `failed` coverage, measured native visits, diagnostics, and `nextOffset` where another bounded page exists. Pass the returned `metadataRevision` as `expectedMetadataRevision` when continuing a live traversal. This is a metadata consistency check, not a content fingerprint. Cached explorer pages instead use the shared authenticated snapshot cursors.

For a Unified target, the opt-in `model` section walks the whole public V21 `HmiSoftware` object model: screens and nested screen items, dynamizations, event and property-event handlers, faceplate interface entries, tag tables/groups, tags and members, thresholds, logging tags, system tags, connections, alarm classes, discrete/analog alarms, audit classes, OPC UA alarm types, alarm/data logs, audit trails, script modules, text/graphic/system text lists, plant object tags and runtime settings. It reads typed public properties plus `GetAttributeInfos`/`GetAttribute` and `GetCompositionInfos`/`GetComposition`. Each composition member is one `hmiModelNode` row at its `hmi_inspect` path (for example `.../screens/Main/items/Button_1/dynamizations/Visible`). Typed attributes are returned with object parts and multilingual texts inline, and with a `contentSha256` identical to that node's preservation-manifest entry. Script code appears only as SHA-256 and length; attribute names matching the worker's sensitive pattern (password, credential, private key, secret, token) are returned as `<excluded>`. `modelRoots` (for example `["Screens","AlarmClasses"]`) restricts the walk, and `objectPath` selects a subtree. `maxVisited`, the 8 MiB retained-row bound and normal paging bound the result; truncation and failed getters are reported in `coverage` and `diagnostics`. The walk only observes what the public API exposes. It is not a native export, is not natively qualified, and Classic targets report the section as unsupported.

```json
{"request":{"schemaVersion":1,"section":"model","target":{"hmiPath":"<returned exact path>","family":"unified"},"modelRoots":["Screens"],"maxVisited":5000,"limit":200}}
```

Source export is opt-in with `includeSource:true` and an exact `objectPath`. The host injects its configured artifact directory. Publicly supported native exports retain their original bytes and SHA-256; unavailable export routes report their limitation. Selected Classic screen-item inspection retains native XML, including unknown payloads. Export-local IDs have only the documented artifact lifetime. Script text is inspected as data; project scripts are never executed to extract relationships or produce previews.

## Relationships and explorer

`hmi_references` takes the exact target, optionally an exact `screenPath` and `includeAlarms:true`. Every relationship retains its native property/artifact, configured spelling, evidence category, resolution state and candidates. Known object endpoints may be resolved; dynamic expressions, unsupported XML shapes, unresolved members, inaccessible remote PLCs and incomplete mappings remain unresolved. Equal names or addresses alone never establish a PLC binding. Inspection cannot authorize automatic rewiring.

The [relationship evidence contract](../reference/hmi-relationship-evidence.md) specifies the V21 native connection endpoint proof. A supported Unified scalar symbol is interpreted only after that proof, with complete candidate enumeration and declaration type checks. A DB member locator retains its owning block, section, exact member components and native export hash. When that declaration or an export-local screen item is absent from the explorer's metadata tree, the relationship retains the exact evidence without inventing a captured endpoint. Uncaptured observations share graph scope, direction, kind, edge and byte limits.

`project_model` composes these adapters into the existing shared snapshot store. HMI screens, tables, tags, connections and available other sections appear under their actual hardware/software target. Path-derived display folders are marked virtual. HMI screen children are loaded through selected-screen details; they are not claimed as a complete initial traversal. Provider visits, retained rows, bytes and response graph sizes share bounded limits with the explorer.

The tree, details and relationships use the same exact references as agent queries. A native detail/reference observation is labelled separately from the metadata snapshot; exported viewers retain both coverage and freshness limitations. The explorer's HTML export works offline, escapes project text, and exposes no engineering mutation endpoint. It does not execute HMI scripts or claim a native-rendered screen preview.

## Engineering and library integration

The allowlist is exposed through `change_prepare` kind `hmi_engineering`, using the existing apply/status/restore workflow. Classic standalone-screen `BackColor`, Classic whole-object XML import (`classic_xml_import`) and Unified internal tag creation, selected item properties, existing faceplate placement and explicit interface binding have family-specific guarded bodies. The server rejects a request before project mutation if complete preservation, exact dependencies or the operation's native qualification are missing. There is no client flag to bypass a blocked gate.

`contracts/hmi-engineering.v1.schema.json` defines operation payloads. Scalar types, rectangles, names and explicit binding ownership are validated before dispatch; a metadata revision cannot replace the required native content fingerprint. Raw exports, the narrow qualified export-time normalization, baseline/after manifests, native compiler diagnostics and recovery evidence remain in the existing change journal. Unknown write outcomes are not replayed.

The library subsystem still owns library types, exact versions, release, transfer, publication and updates. Its HMI action must pass the [explicit handoff](../../contracts/hmi-library-handoff.v1.schema.json), including the resolved native source and exact destination, dependency closure, rectangle and typed bindings. The HMI adapter verifies the handoff and preservation gate; it does not create a parallel type lifecycle.

## Build and qualification

`Build-HmiWorkerSnapshot.ps1` creates an immutable worker source/binary pair for repeated native qualification while other workstreams edit the shared checkout. It is a qualification utility; the normal `New-IsolatedBuild.ps1` and `Package.ps1` create the complete portable release. Vendor assemblies resolve from the configured Siemens installation and are excluded from releases.

Run all three `TiaMcp.Hmi.*.Tests` suites plus `scripts/test_hmi_qualification.py` and the optional-startup evidence tests for pure checks. Run `scripts/probe_hmi_api.py` for installed public API evidence, the independent `scripts/native/HmiSeeder` for new owned fixtures, and `scripts/qualify_hmi.py` against explicit host/worker binaries and a hashed fixture report. The harness retains failed attempts and distinguishes passed, failed, blocked and unverified gates. Native screen appearance, changed-state persistence/recovery, interrupted writes, exact PLC mapping and SSH/VM qualification require their own evidence; local API availability cannot substitute for them.

`python -B tests/HmiContracts.py --evidence <bridge-report.json>` validates Draft 2020-12 schemas and actual recorded responses using the explorer's local `artifacts/project-model-schema-deps` dependency directory (`jsonschema>=4.18`). These are contract checks, separate from native acceptance. See [optional startup controls](../verification/hmi-optional-startup.md) for the actual host/worker absence and incompatible-metadata runs.

No physical HMI/PLC download or runtime deployment is provided by this workstream.
