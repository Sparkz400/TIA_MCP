# Source-bound API coverage

[The binding registry](../../contracts/api-coverage/implementation-bindings.v1.json) links MCP registrations, mutation kinds and catalog features to source observations; its `counts` field records the current population. It supplements the [API inventory](../../scripts/api_coverage.py); it does not change its snapshot, native qualification or release decisions.

The [collector](../../scripts/api_coverage_bindings.py) records exact Host method spans and hashes, literal bridge operations, unique Host helper delegation, family-specific worker `Execute` routes and defined handler locations. Engineering and library prepare/apply branches are separate. HMI's direct early branch is retained. Grouped switch labels share their implementation. Local method-name matches may include multiple receivers/overloads and remain explicitly partial.

Source parsing uses balanced tokens, ignores comments and string bodies, and supports regular, verbatim, interpolated and raw strings. It is a bounded structural scanner, not a complete C# parser: preprocessor branches, overload resolution, extension methods, reflection, property access and interpolated expressions do not establish complete call-graph semantics. Exact entrypoint and dispatcher anchors are useful evidence even while transitive dependencies need review. Every feature retains gaps; none is labeled fully reviewed.

Schema bindings require a literal version identifier inside an anchored method and a matching existing contract. They identify a source reference, not schema conformance or comprehensive coverage of that feature. Empty schema mappings explicitly remain unresolved. Evidence IDs and profile hashes come from the catalog; artifact files are hashed when available, and missing/directory/bounded-read states remain explicit. This package does not turn an existing catalog qualification result into a new result.

```powershell
python -B scripts/test_api_coverage_bindings.py
python -B scripts/api_coverage_bindings.py validate contracts/api-coverage/implementation-bindings.v1.json
python -B scripts/api_coverage_bindings.py impact contracts/api-coverage/implementation-bindings.v1.json --path src/TiaMcp.Openness.V21/OpennessSession.Changes.cs
python -B scripts/api_coverage_bindings.py impact contracts/api-coverage/implementation-bindings.v1.json --evidence local.offline.v02
python -B scripts/api_coverage_bindings.py collect --out artifacts/coverage-next.json
python -B scripts/api_coverage_bindings.py diff contracts/api-coverage/implementation-bindings.v1.json artifacts/coverage-next.json
```

Collection writes a new file exclusively. Validation checks duplicate/missing tools, mutation-feature reconciliation, source/schema/profile references, prohibited qualification promotion, current source hashes and exact regenerated binding content. Source changes invalidate the captured bindings, including changes to the scanner/tests. Evidence artifact changes also invalidate the observation. Regenerating a registry records a new observation; it is not semantic review or release acceptance.

Source inputs must use LF, matching this repository's Git checkout policy. The
collector rejects CRLF source bytes rather than recording hashes that Git would
change during publication. JSON generators should write with `newline="\n"` or
use the existing `api_coverage.write_new` helper. This rule applies to source
inputs only; byte-sensitive fixtures and historical evidence retain their exact
original encoding and line endings. Do not normalize evidence to pass validation.

Impact reports distinguish directly bound features from conservative affected features. Evidence/profile changes map to their actual catalog references. A source/schema/unknown path change conservatively affects every feature because the transitive call graph is partial. Diff includes removed feature IDs, changed artifact/profile dependencies and newly added features. This fallback is intentional and prevents a narrower-looking map from hiding unreviewed dependencies.

The [contract](../../contracts/api-coverage-bindings.v1.schema.json), [independent tests](../../scripts/test_api_coverage_bindings.py) and current report (`artifacts/expansion-implementation-20261015/coverage/current-report.json`) retain this boundary. All feasible unreviewed routes, schema mappings and native qualifications remain 1.0 obligations.
