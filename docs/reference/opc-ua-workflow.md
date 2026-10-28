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

## Native engineering read and export

Two worker-backed tools read the OPC UA configuration of one PLC CPU in the attached TIA V21 project; S7-1500 is the target profile. Select the CPU by `hardwarePath` or `plcPath` from `project_inventory` (both are accepted when they name the same CPU). They change nothing in the project and never connect to a UA server.

`opcua_engineering_inspect` returns:

- `serverSettings`: CPU and sub-item attributes whose native names contain `OpcUa`, with the same sensitive-name exclusion and value states as `hardware_inspect`. Settings exposed under other names are not reported.
- `serverInterfaces`, `simaticInterfaces` and `referenceNamespaces`: name, enabled, author, timestamps and, where Openness exposes them, the NodeId and node-generation flags.
- `namespaceAccessRestrictions` and `roleMappings`, each role with its per-namespace `browse`, `read`, `write`, `call`, `receiveEvents` and `readRolePermissions`.

`serverInterfaceGroup: "notExposed"` means the PLC software did not expose the OPC UA provider; it does not prove the CPU has no server. `maxItems` (1–2000, default 500) bounds sub-items, attributes and rows together; `completeness.truncated` and `warnings` report what was not read. Comments, legacy OPC UA users, certificates and client interfaces are not read.

`opcua_engineering_export` writes native XML for one `category`:

| `category` | V21 API | `name` |
| --- | --- | --- |
| `server` | `Project.HwUtilities.Find("OPCUAExportProvider")` → `OpcUaExportProvider.Export(DeviceItem, FileInfo)`: whole address space of the CPU | not accepted |
| `serverInterface` | `ServerInterface.Export(FileInfo)` | optional exact name |
| `simaticInterface` | `SimaticInterface.Export(FileInfo)` | optional exact name |
| `referenceNamespace` | `ReferenceNamespace.Export(FileInfo)` | optional exact name |

At most `maxItems` (1–32, default 32) files are written as new files under `<artifacts>/exports/opcua`, each with path, byte count and SHA-256. The Host reads every file back through the artifact boundary, checks its hash and registers it as an ordinary model record, the same record `opcua_inspect` creates from an upload. Each `files[]` row then carries either `record` (with its `recordRef`) or `registrationError`, for example `OpcUaCapacity` or `OpcUaInvalidXml`; the exported file stays in place either way. Pass the record handles to `opcua_compare`, `opcua_dependencies` and the other tools above, and export the reference namespaces too when a dependency set needs them. A failed item export, such as an empty SIMATIC interface, is listed in `warnings` and the other files are kept.

Neither tool is natively qualified: the whole-server utility identifier and the XML dialect of each export come from Siemens documentation. A model record holds the exported bytes, not which CPU or interface they came from; keep the export response for that.

## Verification and native boundary

Run `scripts/Test-OpcUaOffline.ps1` for the parser/dependency CLI package. The combined `scripts/Test-Offline.ps1` runner includes the workflow harness and actual MCP transport checks; Python dependencies are declared in `scripts/requirements-test.txt`. The standalone workflow commands are:

```powershell
dotnet run --project tests/TiaMcp.OpcUa.Workflow.Tests -c Release
python -B -m unittest discover -s scripts -p test_opcua_workflow_contract.py -v
python -B scripts/test_opcua_host_mcp.py --host <built-Host.exe> --output <new-output-directory>
```

The MCP harness explicitly validates every UA success/error response against the workflow JSON Schema and launches the Host twice with nonexistent worker paths. See the [delivery receipt](../verification/opc-ua-host-delivery-20261016.md).

## Prepared native engineering changes

Six change kinds use the normal `change_prepare` → `change_apply` → `change_status`/`change_restore` workflow. They work offline only, on one PLC selected by an exact top-level `plcPath`; `scopePath` is refused. Apply requires `checkpointSave=true` and `compile=true`. They use only the public V21 `Siemens.Engineering.SW.OpcUa` API: `OpcUaProvider.CommunicationGroup.ServerInterfaceGroup` with `ServerInterfaces`, `SimaticInterfaces`, `ReferenceNamespaces` and `AccessControl.RoleMappings`. The worker looks for the provider on the PLC software first, then on the CPU device items that own it. If neither has it, the change is refused (an S7-1500 with OPC UA is required).

| Kind | Payload | Native call and readback |
| --- | --- | --- |
| `opcua_server_interface_import` | `interfaceName`, `mode` (`create`/`replace`), absolute `path`, `expectedSha256` | `ServerInterfaces.Create(name)` when creating, then `ServerInterface.Import(file)`. Readback gives name, `Enabled`, modification time, a native `Export` artifact and `exportMatchesInput`. |
| `opcua_server_interface_delete` | `interfaceName` | Backup `Export`, then `Delete()`, then a check that the interface is gone. |
| `opcua_server_interface_enable` | `interfaceName`, `enabled` | Sets `ServerInterface.Enabled` and reads it back. |
| `opcua_reference_namespace_import` | `namespaceName`, `mode`, `path`, `expectedSha256` | `ReferenceNamespaces.Create(name, file)` or `ReferenceNamespace.Import(file)`. Readback gives `Enabled`, `GenerateNodes`, `GeneratedInterfaceName`, `UseStringNodeIds` and an export. |
| `opcua_reference_namespace_delete` | `namespaceName` | Backup `Export`, then `Delete()`. The preview names any generated interface that may disappear with the namespace. |
| `opcua_role_mapping_set` | `action: "create"`, `roleName`, `definedInNamespace`, optional `projectRole`, `namespacePermissions`; or `action: "remove"`, `roleName` | `RoleMappings.Create(name, namespace)`, `SetProjectRole`, then `NamespacePermission.SetPermission` for each listed namespace, all read back. Remove uses `RoleMapping.Delete()`. |
| `opcua_server_settings_set` | Each optional and each `{ expected, value }`: `serverEnabled` (bool), `runtimeLicense` (`NoLicense`, `SimaticOpcUaS71500Small`, `SimaticOpcUaS71500Medium`, `SimaticOpcUaS71500Large`), `port` (1..65535), `guestAccess`, `autoAcceptClientCertificates` (bool); outer `plcPath` | `SetAttribute("OpcUaServer")` on the CPU's `OPC UA` sub-item and `SetAttribute("OpcUaPurchasedLicense")` (UInt64 0..3) on the CPU, then both read back. Names are pinned from a [native capture](../../fixtures/opcua/server-settings-v21-1511-v41.json). `port`, `guestAccess` and `autoAcceptClientCertificates` have no pinned attribute and are refused with `UnsupportedCapability`. A stale `expected` is `ExpectedValueMismatch`. Device compile required. |

Each `namespacePermissions` entry needs `namespaceUri` and explicit booleans for `Browse`, `Read`, `Write`, `Call`, `ReceiveEvents` and `ReadRolePermissions`. Namespaces you do not list keep their native defaults, which the readback reports. Payload fields outside each kind's list are refused. This means passwords, users, certificates and trust settings are never accepted. Standard-role insertion (`AddStandardOpcUaRole`), role rename and editing an existing mapping are not offered.

V21 refuses to compile an active OPC UA server unless at least one user holds the `OPCUAServerAccess` device function right ("OPC UA server access"). Grant it first with `umac_role_set` (`action: "grant"`, `deviceRights: [{ device, identifier: "OPCUAServerAccess" }]`) to a custom role of an active project user; a refused grant lists the CPU's available device rights in its details. Guest access is the anonymous UMAC user holding that right, which `umac_role_set` never changes. `runtimeLicense` records the purchased license in the project; it does not verify or activate an entitlement.

Import files are copied into the change directory and bound to `expectedSha256` at prepare and again before apply. Use the bytes you reviewed with `opcua_inspect`; change payloads cannot resolve Host OPC UA record handles. Interface and namespace selection is case-insensitive. SIMATIC interfaces, and interfaces whose name matches a reference namespace's `GeneratedInterfaceName`, are refused as protected or generated objects.

The V21 API exposes no mapping-status property. The readback export and the PLC compile diagnostics of the change are the only evidence of mapping validity. External OPC UA clients are never discovered.

For these kinds the change fingerprint also includes a snapshot of every PLC's server interface group: user and SIMATIC interface names, enabled state and native modification time, reference namespaces with generation settings, and role mappings with their namespace permissions. Shared-mutation interruption reconciliation uses the same snapshot when it compares the project with its checkpoint and the recorded outcome. Interface and namespace content is covered only through native modification times. None of these kinds is natively qualified yet.

The next native increment is the [UA-03/UA-Q00 eligibility/export packet](../verification/opc-ua-native-eligibility-packet-20261016.md). Target eligibility, Siemens mapping dialects, native engineering, security configuration, runtime endpoints and full compatibility are separate unqualified gates. None is admitted by parser, schema or offline MCP tests.
