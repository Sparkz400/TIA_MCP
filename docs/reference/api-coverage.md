# Offline API coverage registry

The additive [source-bound implementation registry](api-coverage-bindings.md) now records exact Host/worker/mutation anchors and evidence dependencies. See the [latest integrated results](../verification/expansion-implementation-wave-20261015.md) for current source validation; the initial snapshots below remain historical.

This is the first implementation slice of **AC-01 / F01**, not completion of the coverage roadmap. It inventories the installed public SDK without executing Siemens code, records a small reviewed semantic seed, retains current catalog claims separately and exposes unresolved coverage. Every feasible capability remains required before 1.0.

## Inputs and outputs

- [Inspect-PublicApi.ps1](../../scripts/Inspect-PublicApi.ps1) uses `MetadataLoadContext`, never an execution-load fallback, to inspect exported types and declared public members. It records exact XML documentation IDs, accessors, inheritance, generic constraints, enum values, assembly/source hashes and explicit errors. The [helper and fixture test](../../scripts/public_api_metadata/) are isolated from production projects.
- [api_coverage.py](../../scripts/api_coverage.py) uses only Python's standard library. It reconciles that report against installed XML and hashes every DLL/XML/XSD plus shipped EXE/PDF. It enumerates XSD declarations/enumerations and import/include/redefine dependencies; external or escaping dependencies are recorded without fetching them.
- The reviewed [seed registry](../../contracts/api-coverage/registry.v1.json) has D01–D17 and X01–X13 rows, 13 initial operation groups comprising 24 exact overloads, existing feature-to-obligation mappings, and an explicit manual-review backlog for every domain. These operations came from the four parallel planning investigations; their implementation, qualification and target availability remain separate and unfulfilled.
- The [snapshot schema](../../contracts/api-coverage-snapshot.v1.schema.json) and [seed schema](../../contracts/api-coverage-registry.v1.schema.json) describe the first-slice format. The Python validator checks cross-record invariants and semantic hashes; it is not a general JSON Schema engine.

Each symbol has `semantic`, `infrastructure` or `untriaged` classification. Infrastructure recognition is deliberately limited to exact declaring interfaces. An `IEngineeringObject` parameter does **not** hide VCI, filtered translation export or style-guide scope methods. Public accessor methods and XML documentation for nonpublic explicit interface implementations are retained as discrepancies; absence on one side is not a universal unsupported finding.

Current source mapping records exact MCP registration locations and literal bridge-call/switch-case candidates. **Handler, schema and profile mappings still require review**; lexical candidates are labeled accordingly. The existing feature catalog is copied as historical scoped claims and is never edited or promoted by the collector. No support percentage is produced.

## Run without native execution

Use a fresh output name each time. All output files are create-new; existing receipts are never overwritten.

```powershell
pwsh -NoProfile -File scripts/Inspect-PublicApi.ps1 -ReportPath artifacts/my-coverage-run/metadata.json
python -B scripts/api_coverage.py collect --metadata artifacts/my-coverage-run/metadata.json --output artifacts/my-coverage-run/snapshot.json
python -B scripts/api_coverage.py validate artifacts/my-coverage-run/snapshot.json --check-inputs
python -B scripts/api_coverage.py diff artifacts/previous-run/snapshot.json artifacts/my-coverage-run/snapshot.json --output artifacts/my-coverage-run/proposed-impact.json
python -B -m unittest discover -s scripts -p test_api_coverage.py -v
pwsh -NoProfile -File scripts/public_api_metadata/Test-PublicApiMetadata.ps1 -OutputDirectory artifacts/my-metadata-fixture
```

No command above starts TIA, probes a target, acquires PLCSIM, changes the catalog or runs selected native tests. The diff is a **proposal**: until dependency mappings are reviewed, any semantic input change conservatively selects all current and removed feature/profile records for review. An unchanged semantic snapshot selects none. Timestamp-only differences do not change its semantic hash.

The collector bounds file count, file size, total bytes, XML nodes and XML depth; rejects DTD/entity declarations and duplicate keys/IDs; refuses path escapes; rejects stale or incomplete metadata reports; and rehashes inputs before publication. `validate --check-inputs` also detects added/deleted source/API inputs. A mismatch requires a new snapshot, not editing an old receipt.

## Remaining AC-01 work

1. Review all untriaged symbols and XML/DLL discrepancies. Every method needs an operation or an evidence-backed infrastructure/exclusion decision; blanket namespace assignment is insufficient.
2. Enumerate the complete official manual leaf inventory, dynamic attributes/services, schema residuals and non-V21/other-SDK scope. The current 30 manual residual rows explicitly mean **not fully enumerated**.
3. Replace lexical handler candidates with reviewed end-to-end tool/change → dispatch → implementation → schema → evidence/profile bindings for all current operations, including the 55 mutation kinds. Add operation/profile dependency edges and precise change impact selection.
4. Expand per-domain semantic rows and profile/availability requirements; generate human-reviewable catalog/documentation proposals. The current diff reports impacts, not an automatically applicable catalog patch.
5. Qualify bounded native metadata probes separately. Pure registry tests do not settle licenses, service availability, mutation effects, preservation, recovery, runtime or release acceptance.

See the [current wave results](../verification/ac01-planning-wave-20261015.md), [owner handoffs](../plans/active-handoffs-20261015.md) and [full coverage roadmap](../plans/expansion-plans/api-coverage-and-roadmap.md).
