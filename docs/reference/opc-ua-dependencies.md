# Offline OPC UA dependency analysis

`OpcUaDependencyAnalysis` resolves selected dependencies across explicitly supplied NodeSet files and follows those dependencies to identify potential client impacts. It uses the existing parser and embedded, hash-checked NodeSet XSD. It requires .NET 10, local files and no Siemens installation. It performs no dependency downloads, directory discovery, TIA operations or endpoint connections. The [offline Host MCP workflow](opc-ua-workflow.md) now exposes this analyzer through uploaded artifacts, immutable handles and bounded report pages.

## CLI

Run from the repository root. Output paths must not already exist; create their parent directory first.

```powershell
# Resolve this exact collection of files.
dotnet run --project src/TiaMcp.OpcUa.Cli -- dependencies artifacts/device-dependencies.json fixtures/opcua/dependencies/device.xml fixtures/opcua/dependencies/types-v1.xml

# Compare two exact collections and identify declared client impacts.
dotnet run --project src/TiaMcp.OpcUa.Cli -- compare-set artifacts/device-impacts.json --before fixtures/opcua/dependencies/device.xml --before fixtures/opcua/dependencies/types-v1.xml --after fixtures/opcua/dependencies/device.xml --after fixtures/opcua/dependencies/types-v2.xml --consumers fixtures/opcua/dependencies/consumers-v2.json

# Independent managed and CLI checks plus the existing OPC UA regression suite.
pwsh -NoProfile -File scripts/Test-OpcUaOffline.ps1
```

`dependencies report.json file.xml [file.xml ...]` analyzes a set. `compare-set report.json` takes repeated `--before file.xml` and `--after file.xml` options, and an optional `--consumers file.json`. Each side requires at least one file. Supply unchanged dependency files on both sides. Omitting a file from the after set means it is absent from that snapshot; it does not mean reuse its before version. The original `inspect` and single-file `compare` commands remain available.

| Exit | Meaning |
| --- | --- |
| 0 | Report written; selected dependencies resolve. Comparison changes may still require review. |
| 1 | Invalid input, exceeded capacity or file I/O failure. |
| 2 | Invalid command usage. |
| 3 | Report written with missing or ambiguous dependencies. |

The library entry points are `Analyze(IReadOnlyList<OpcUaNodeSetSource>)` and `Compare(beforeSources, afterSources, JsonObject? consumerManifest)`. Each `OpcUaNodeSetSource` contains a unique provenance ID and source bytes. Library reports carry diagnostics rather than throwing for missing or ambiguous definitions. Invalid XML, identities, manifests and capacity overruns throw structured `BridgeException` errors. The CLI uses absolute paths as provenance IDs; the Host wrapper uses its immutable model handles and enforces its own storage policy.

## Resolution and report scope

Node identities use namespace **URIs**, identifier kinds and normalized identifiers. Namespace indexes and aliases are resolved independently within each input. Files may use different indexes for the same URI. A namespace-table entry alone does not supply any node or model definition.

The analyzer follows these explicit edges:

- Variable and VariableType `DataType`, including the schema default.
- Each `Reference` target and its `ReferenceType`.
- `ParentNodeId` and `MethodDeclarationId`.
- DataType `Definition/Field/@DataType`, including the schema default.
- Node `RolePermissions/RolePermission` identities.

Reference targets are followed from the node declaring the reference, including references with `IsForward="false"`. The analyzer does not synthesize inverse references or interpret every reference type's semantics. An indirect impact is therefore a conservative review candidate, and absence of a path does not establish compatibility.

Each edge reports its source file, source/target node keys, kind, resolution and candidate provider IDs. Multiple definitions of the same node are ambiguous even when their bytes match. No provider wins by argument order, filename, version or recency. Duplicate definitions are reported even without incoming references. The standard namespace receives the same explicit-file treatment: its nodes are never assumed present.

`RequiredModel` declarations resolve against supplied `Models/Model` declarations by URI. Missing and multiple providers produce diagnostics. Requested version/publication metadata and provider metadata are retained, but **version compatibility and model-dependency cycles are not evaluated**. The distinction is intentional: `Version` is human-readable metadata, while `ModelVersion` and `PublicationDate` have separate comparison rules in [OPC UA Part 6, Annex F](https://reference.opcfoundation.org/specs/OPC-10000-6/annex-f). This implementation does not choose a version on the caller's behalf.

`selectedDependenciesResolved` means only that all reviewed edges and required-model URIs resolve uniquely and no duplicate definitions were found. `dependencyClosureComplete`, `requiredModelVersionsEvaluated` and `compatibilityCertified` remain false. Values (including method arguments), vendor extensions, inherited permissions, obsolete Definition `BaseType`, Siemens mappings and runtime behavior are outside dependency traversal. The existing parser still hashes opaque content for change review.

## Client impact evidence

Set comparisons require the [v2 consumer manifest](../../contracts/opc-ua-consumers.v2.schema.json) when client results are wanted. Each consumer declares node keys that uniquely resolve in the before set. The manifest binds the entire set's `modelSetSha256`, including dependency file bytes. The [single-file v1 manifest](../../contracts/opc-ua-consumers.v1.schema.json) remains specific to the original `compare` command.

Obtain `modelSetSha256` from a `dependencies` report and retain the exact input files. The digest is SHA-256 over UTF-8 text consisting of `opcua-model-set.v1` plus LF, followed by the ordinal-sorted lowercase SHA-256 digests of all input files, each followed by LF. Duplicates are retained in this multiset. Argument order and source paths are excluded; any changed file bytes or added/removed file changes the binding. Duplicate CLI paths are rejected, while separate files defining the same nodes produce ambiguity diagnostics.

Comparison reuses the existing node-field change rules. Identities ambiguous in either snapshot are listed in `comparisonSkippedNodeKeys` and excluded from change inference; ambiguity never becomes a fabricated addition or removal. Artifact hashes, model metadata and envelope hashes accompany the results, so metadata-only or opaque envelope changes remain visible even when no node field changes. Unchanged bytes do not imply a complete model.

For each consumer, `directChanges` identifies changes to declared keys. `indirectChanges` identifies changed nodes reachable through resolved edges, with a `consumedNodeKey`, `graphSnapshot` and an ordered edge `path`. Breadth-first traversal terminates on cycles. It chooses one shortest path in the before snapshot when one exists, otherwise one in the after snapshot. The two graphs are traversed independently: combining them could invent a path that exists in neither. Removed dependencies retain before evidence; new paths use after evidence. Missing or ambiguous edges stop traversal and appear in `dependencyGaps`. Required-model issues remain in the snapshot-wide diagnostics.

Statuses are `affected` for direct changes, `potentiallyAffected` for indirect changes, and `noKnownImpact` otherwise. These statuses do not certify a client or endpoint; unknown consumers and opaque dependencies remain possible. Without a manifest, `clientImpacts` is empty.

The sample has exactly one breaking node-field change, the BrowseName of Reading. SensorClient is potentially affected via `Sensor → Telemetry → Reading`; TypeClient is directly affected; UnrelatedClient has no known impact. The sample models are authored envelope fixtures, not complete companion models or deployable Siemens exports.

## Bounds and validation

Each snapshot accepts 1–32 files, at most 64 MiB total, 100,000 total node definitions and 200,000 selected edges. Existing per-file bounds still apply: 16 MiB, XML depth 64, 500,000 XML reader events (including annotations and opaque content), 20,000 nodes and 100,000 edges. Unresolved-row limits apply **after** cross-file resolution. Required-model rows are capped at 200,000 and diagnostics at 10,000 per snapshot. Reports, including CLI formatting, are capped at 32 MiB. No results are silently truncated.

Consumer manifests are limited to 1 MiB, 1,000 consumers and 20,000 total declared keys. Traversal is capped at 2,000,000 node/edge visits across all consumers and both snapshots, 50,000 impact/gap rows, and 200,000 materialized path steps. Exceeding a bound fails explicitly and requires a smaller review scope.

`tests/TiaMcp.OpcUa.Dependencies.Tests` is an executable harness separate from the original parser tests. Its expectations use authored node keys, paths and independently calculated hashes. The isolated runner snapshots relevant inputs, builds in a unique artifact tree, runs both harnesses and invokes the actual CLI for success, usage, invalid input, unresolved dependencies and output-preservation cases. To run only the library harness, use `dotnet run --project tests/TiaMcp.OpcUa.Dependencies.Tests`; pass the built CLI DLL path after `--` to also run subprocess checks.
