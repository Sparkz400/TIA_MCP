# Bounded library dependency capture — 18 October 2026

Status: full offline verification passed. Exact pushed commit and hosted-CI state
are recorded in `artifacts/library-dependency-capture-01/integration.json` after push.
Base: `8cf4d79e7e2c39ca7461e633679f96b137ae67fd`. No Siemens SDK build,
native acquisition, mutation or new qualification is claimed.

The C01 source audit found that PLC integration bounded its first dependency
enumeration but populated exported version rows by enumerating again without the
same bound. Copy/publish expansion, project-update impact and captured-storage
readback also enumerated dependency collections without a per-list bound.
The existing read-only `LibraryDependencyClosure` already has bounded enumeration
and incomplete-result behavior; that query contract remains unchanged.

One shared capture helper now reads a native dependency collection once, takes at
most 1,001 entries to enforce the existing 1,000-entry limit, and freezes each
observed version GUID. Missing collections, null entries, empty GUIDs and overflow
refuse with `DependencyUnresolved`. Getter/iterator failures propagate rather than
becoming successful empty or partial observations. Enumerators are disposed.
Order and repeated entries are retained; existing closure owners still handle
cycles, duplicate versions, type conflicts and their total node/version bounds.

PLC integration stores the captured GUID arrays during traversal and uses those
same observations in its exported version rows. A second bounded capture after
export must match the original ordered list or preparation refuses with
`DependencyConflict`; it cannot silently replace the first observation. Ordering
changes conservatively require a fresh plan until native ordering is qualified.
Copy/publish, update-impact and
captured-storage callers reuse the bounded reader. This is not an atomic native
snapshot: changes between separate native reads/exports, blocking native getters,
freshness and complete project preservation still require their existing gates
and native acceptance. No mutation or automatic retry was added.

## Focused evidence

The PLC integration suite passes 207 checks, including 15 new capture cases:
empty and exact-limit collections, one-item overflow, disposal, retained object
and GUID identity after source changes, duplicate observations, missing identity,
native getter/iterator/GUID failures, stable rereads and changed/reordered export-time
membership. The production helper is linked into
the suite with a synthetic Siemens-shaped dependency provider.

Three isolated mutation controls fail against the final helper: removing the
bound produces 206 passes/one failure; replacing retained GUIDs with getter
rereads and disabling freshness comparison each produce 205 passes/two failures.
These controls test the helper contract; they are not executions of the original
native worker. Earlier 12-case controls remain separately retained.

The same final helper and cases also pass 15 checks as an actual x64 .NET Framework
executable, using the existing Roslyn/Framework fixture approach and the actual
worker `BridgeException` source. Its result records CLR `4.0.30319.42000` and the
loaded Framework/Newtonsoft paths. No Siemens assembly is loaded.

Exact evidence is under `artifacts/library-dependency-capture-01/`. Full aggregate, package verification and source-binding checks pass within the
scope below; exact integration is recorded separately.
The changed native callers have only source-level integration review here.

## Still open

Compile and exercise every changed caller against the Siemens SDK and real
library collections, including overflow, failures, stale prepared state and
project preservation. Continue C01 exact original export/type/version association,
complete dependency and consumer mapping, and generation/regeneration. The
[full offline queue](../history/offline-execution-20261016.md) and all original
44-workstream obligations remain open; this repair does not close C01.

## Full frozen verification

Both frozen snapshots contain 2376 identical source files. The
aggregate passes 44 managed suites, including 207 PLC integration checks,
242 general checks, 25 HMI model checks and 19 HMI preservation checks. All
1,510 Python tests pass with one existing skip, along with 25 Explorer
model/client checks and all Host workflows. All 38 package/relocation stages
pass. The 1705-file package manifest is
`45529b88e4bf9c71c74fe5c2aaf6ae88ef0c345b5b04b513206591a9c3419ef6`.

Both runs retain 384 HMI MCP checks, four independently checked expected-effect
records and 416 compatibility checks over nine prior records. Five packaged
Explorer assets match prior browser-tested assets; no new browser run is claimed.
The 15-case Framework capture fixture and three expected-failure mutation
controls retain exact source/binary hashes. Native adapter integration remains a
source review: neither this helper fixture nor the portable package compiles or
executes the Siemens worker.

All 112 historical evidence entries are unchanged, including 106 unavailable
inputs. Original scope reconciliation files are byte-identical. Only final docs
differ from the frozen source. Full C01 and the remaining 44-workstream scope
remain open.
