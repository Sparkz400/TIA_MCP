# Offline OPC UA MCP workflow

The production Host accepts uploaded NodeSet files for offline inspection, comparison, explicit dependency analysis and consumer-impact review. These eight tools work with nonexistent engineering and simulator workers. They never start TIA, discover dependencies on disk, download a namespace URI, connect to a UA endpoint or import a model.

The [workflow schema](../../contracts/opc-ua-workflow.v1.schema.json) defines request and response shapes. Existing [single-file consumer](../../contracts/opc-ua-consumers.v1.schema.json) and [model-set consumer](../../contracts/opc-ua-consumers.v2.schema.json) schemas remain separate. The implementation reuses the pinned NodeSet parser and [dependency analyzer](opc-ua-dependencies.md); its report semantics and unsupported dependencies are unchanged.

## Upload and inspect

1. Begin an upload with `artifact_upload_begin`: display `fileName`, exact `expectedBytes` and lowercase `sha256`.
2. Send sequential base64 chunks using `artifact_upload_chunk` and the returned `uploadId`, respecting `maxChunkBytes`. Exact-byte retry is supported.
3. Call `artifact_upload_commit`; use its `artifactRef`, for example `upload:<id>`. An incomplete upload cannot be inspected.
4. Call `opcua_inspect` with `{"nodeSetUploadRef":"upload:<id>"}`. This retains the exact original bytes and a complete inspection report in independent immutable storage.

The response includes a `recordRef` in the form `opcua:<publication-id>:<receipt-sha256>`, the source hash under `summary.sourceSha256`, and `artifacts` entries for `original.xml` and `report.json`. Handles are opaque: pass them back exactly. A path, filename, URL or upload handle cannot substitute for a retained model handle.

Large arrays in `summary` become section descriptors such as `{"section":"nodes","rowCount":4}`. Read them with:

```json
{"recordRef":"<model recordRef>","section":"nodes","pageSize":100}
```

Send that object to `opcua_report_page`. If `nextCursor` is non-null, repeat the same record and section with that exact cursor. Inspect `diagnostics`, `models` and `limitations` as well as `nodes`. The report represents selected fields and hashes of opaque content; the original NodeSet remains the source artifact. Each node also carries `annotationsSha256` (null when absent), binding XML comments and processing instructions to their positions inside that node. Annotations outside nodes, including prolog/epilog and model/alias/namespace metadata, contribute to `envelopeSha256`. Annotation changes require review and node changes participate in declared consumer impacts; they do not establish a runtime behavior change. Position hashing is conservative: formatting or reordering around annotations can require review. Inputs are bounded before DOM allocation by 16 MiB, depth 64 and 500,000 XML reader events, including annotations and opaque elements. [Validation](../verification/opc-ua-preservation-20261016.md).

## Compare models and review declared consumers

Inspect the before and after files separately, then call:

```json
{"beforeRef":"<before model recordRef>","afterRef":"<after model recordRef>","consumerManifestUploadRef":"upload:<manifest id>"}
```

`opcua_compare` accepts an optional uploaded `opcua-consumers.v1` manifest. Its `modelSha256` must equal the before model's `summary.sourceSha256`. Each consumer declares exact expanded node keys from that model. A manifest bound to different bytes is rejected as `OpcUaStaleConsumers`.

The comparison retains the supplied manifest byte for byte, exposes `changes` and `clientImpacts` sections, and preserves `unknownConsumers=true` and `compatibilityCertified=false`. Omitting the manifest produces an empty consumer-impact section; it does not establish the absence of consumers. The sample files under [fixtures/opcua](../../fixtures/opcua/README.md) contain three independently expected breaking changes and three affected declared consumers.

## Explicit dependency sets

Inspect every selected NodeSet, including any standard or companion files needed for review. Call `opcua_dependencies` with `{"modelRefs":["<model 1>","<model 2>"]}`. Source IDs in the report are the retained model handles. No dependency is selected implicitly.

Read `sources`, `nodes`, `dependencies`, `requiredModels`, `diagnostics` and `limitations`. `summary.modelSetSha256` binds the exact multiset of source bytes, independent of handle names or input order. Repeated handles are refused; separate captures containing duplicate definitions remain ambiguous, even if their bytes are identical.

Create before and after sets independently, supplying unchanged dependencies on both sides. Then call:

```json
{"beforeSetRef":"<before dependencies recordRef>","afterSetRef":"<after dependencies recordRef>","consumerManifestUploadRef":"upload:<v2 manifest id>"}
```

`opcua_dependency_compare` accepts the optional `opcua-consumers.v2` manifest, whose `modelSetSha256` must match the before set. Its `clientImpacts` section contains direct changes, conservative indirect changes with actual dependency paths, and dependency gaps. Nested sections such as `before.dependencies` and `after.diagnostics` are pageable. One shortest path is retained per affected node/consumer, preferring the before snapshot.

`selectedDependenciesResolved=true` says only that the selected explicit edges and required-model URIs resolve uniquely. `dependencyClosureComplete`, `requiredModelVersionsEvaluated` and `compatibilityCertified` remain false. Values, method arguments, vendor extensions, inherited permissions and obsolete Definition `BaseType` remain opaque. URI resolution does not establish version compatibility; unknown consumers remain possible.

## Retention, restart and downloads

| Tool | Accepted inputs | Result |
| --- | --- | --- |
| `opcua_inspect` | Committed NodeSet upload handle | Retained model and inspection summary |
| `opcua_compare` | Two model handles; optional v1 manifest upload | Retained comparison and direct consumer impacts |
| `opcua_dependencies` | 1–32 distinct model handles | Retained explicit dependency set |
| `opcua_dependency_compare` | Two dependency-set handles; optional v2 manifest upload | Retained set comparison and conservative consumer impacts |
| `opcua_report_page` | Record, section, optional size and exact cursor | Bounded rows and continuation cursor |
| `opcua_reopen` | Record handle | Verified summary and artifact handles after restart |
| `opcua_list` | Optional offset and count | Discovery of complete, incomplete and integrity-failed publications |
| `opcua_artifact_read` | Artifact handle, byte offset and count | Base64 bytes, exact SHA-256 and continuation offset |

`opcua_artifact_read` downloads the exact original XML, supplied consumer JSON or complete report, at most 65,536 bytes per call. Append decoded `data` at `offset`, continue at `nextOffset` until `complete=true`, then verify the full file against `sha256`. A read at the exact end returns an empty completed chunk. Nothing in this tool writes a caller-selected output path.

Every read, reopen and comparison verifies the retained receipt, intent manifest, artifacts and transitive inputs. The receipt hash is part of the record handle. Changed bytes fail closed. Captures keep independent copies, so an upload can be explicitly released through `artifact_upload_release` after successful inspection.

Publication writes a complete intent manifest, then immutable artifacts, then the completion receipt. All files use create-new semantics. Repeated inspection or comparison creates another record; it never updates an existing output. An interrupted publication remains discoverable and cannot be read as complete. `OpcUaPublicationInterrupted` includes the intended record handle when available. Use `opcua_list` after response loss, then `opcua_reopen`; retrying a creation request creates a new publication. A crash before the intent is complete can leave only a `publicationId` for administrative review.

Discovery sorts publication IDs and accepts an offset. Concurrent additions can change offsets; relist from the beginning when reconciling concurrent work. Complete record handles remain stable. There is no automatic cleanup or overwrite tool for retained UA records. Storage maintenance is an explicit administrative action outside this workflow.

## Bounds and failures

| Boundary | Limit |
| --- | --- |
| NodeSet | 1 byte–16 MiB, XML depth 64, 20,000 nodes |
| Explicit set | 1–32 files, 64 MiB, 100,000 nodes and 200,000 selected dependencies |
| Consumer manifest | 1 MiB, 1,000 consumers, 20,000 aggregate declared keys |
| Complete report | 32 MiB |
| Report page | 1–500 rows; 128 KiB serialized row budget before MCP content duplication |
| Artifact read | 1–65,536 requested bytes |
| Discovery | 1–100 records per page |
| Retention | 1,000 records and 2 GiB, including incomplete publications |

Oversized rows become explicit `omitted` entries carrying their row index, byte count, hash and complete report handle. They still advance the cursor. Download the full report to review those rows; an omitted row does not count as reviewed or absent. Report arrays are never silently truncated in retained artifacts. Aggregate response metadata is bounded separately.

Errors distinguish invalid handles/sections, stale cursors/manifests, capacity limits, integrity failure, incomplete publication and a busy store. No error grants native admission. Symlinks and junctions in storage paths are refused. XML DTD/entities and network resolvers remain disabled.

## Verification and native boundary

Run `scripts/Test-OpcUaOffline.ps1` for the parser/dependency CLI package. The combined `scripts/Test-Offline.ps1` runner includes the workflow harness and actual MCP transport checks; Python dependencies are declared in `scripts/requirements-test.txt`. The standalone workflow commands are:

```powershell
dotnet run --project tests/TiaMcp.OpcUa.Workflow.Tests -c Release
python -B -m unittest discover -s scripts -p test_opcua_workflow_contract.py -v
python -B scripts/test_opcua_host_mcp.py --host <built-Host.exe> --output <new-output-directory>
```

The MCP harness explicitly validates every UA success/error response against the workflow JSON Schema and launches the Host twice with nonexistent worker paths. See the [delivery receipt](../verification/opc-ua-host-delivery-20261016.md).

The next native increment is the [UA-03/UA-Q00 eligibility/export packet](../verification/opc-ua-native-eligibility-packet-20261016.md). Target eligibility, Siemens mapping dialects, native engineering, security configuration, runtime endpoints and full compatibility are separate unqualified gates. None is admitted by parser, schema or offline MCP tests.
