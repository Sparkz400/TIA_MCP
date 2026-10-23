# OPC UA offline implementation evidence — 2026-10-14

This is offline source/metadata evidence only. No TIA process was started or attached, native test run, PLC contacted, simulator window acquired, or deployment performed by this work. The user confirmed that the existing coordinator remains active. Shared host/worker dispatch, feature catalog, solution and central test runners were not edited by this implementation.

## Frozen validation

- Run: `artifacts/opcua-offline/be5f86568da442ccb4c628e874b0c1f5/result.json`, status `passed` for its explicit offline checks.
- Source manifest (`artifacts/opcua-offline/be5f86568da442ccb4c628e874b0c1f5/source-manifest.json`) SHA-256: `333ecf95c388c10f5e60fcc4d18343f29cb6ec28e50662d862a9ebf75ec27611`. It includes the Core dependency, new module/CLI/tests, profile/consumer contracts, fixtures, scripts, pinned schema and build configuration. Live input hashes still matched immediately after verification.
- SDK `.NET 10.0.401`; Python `3.13.15`. Managed and CLI builds: zero warnings/errors. Output binary hashes are in `result.json`; the run preserves the exact copied source tree.
- Managed tests (`artifacts/opcua-offline/be5f86568da442ccb4c628e874b0c1f5/managed.log`): **31/31 passed**. Identity normalization/collision, namespace reindex, datatype/rank changes, stale client identity, unprovided references, access and vendor extension effects, opaque QName binding and whitespace preservation, XML/XSD/depth/capacity refusal, and v1/v1.1/v2 oracles are covered.
- Python tests (`artifacts/opcua-offline/be5f86568da442ccb4c628e874b0c1f5/python.log`): **9/9 passed**. Optional API absence, accessor hashes, metadata-only declaration, malformed/DTD/oversize XML, schema inventory, fail-closed profiles and exact consumer fixture hash are covered.
- CLI inspection, independent v2 impact expectation and existing-output preservation checks passed. Breaking-change report (`artifacts/opcua-offline/be5f86568da442ccb4c628e874b0c1f5/machine-breaking-impact.json`): three old namespace identities removed; Historian-A, Dashboard-B and MES-C each affected; `compatibilityCertified: false`.

Earlier local/snapshot runs are development history only. The final run above includes the opaque namespace-context preservation fix and all 40 checks. `checks.nativeAcceptance` is `notRun` and `nativeQualification` is false. Skipped/unexecuted work is not counted among these passes.

## Installation metadata

The XML/schema/signature manifest (`artifacts/opcua-engineering-02/api-manifest.json`) has SHA-256 `bdea9ae62df87933c53fb53242ddca1eff58b616b76b079671f1af3b4216d3d3`. Its metadata-only accessor evidence (`artifacts/opcua-engineering-02/accessors.json`) has SHA-256 `5e1f0d1ce4b4acfbc1e817e18ff2230751527144d86a0ee85b4d06f69ae1e63e`. DLL hashes must match before accessor evidence is merged. These files record public V21 installation facts, never service availability or native support on a target.

The accessor audit independently confirms custom Name read-only/import present, SIMATIC Name writable/import absent and reference Name writable/import present. The metadata inspection uses only `MetadataLoadContext`, with no execution-load fallback. Public API, schema and documentation file hashes are retained. The exact inspector and Python source used are included in the frozen source manifest.

## Shared workspace regression boundary

One read-only `python -B scripts/test_contracts.py` run failed with:

```text
Public tool missing feature contract:
trace_read, trace_analyze, trace_prepare, diagnostic_plan, trace_capabilities
```

Those registrations are in `src/TiaMcp.Host/DiagnosticTools.cs` (observed SHA-256 `a3f5b1f1da85d1a2cc741f6c7efd3f46d6399a062b613d0656ccfff126bc516e`). No OPC UA tools are registered in Host. The active coordinator owns reconciling that concurrent integration; this report does not classify the shared check as passed or repair another session's files.

The catalog SHA-256 remained `2d17641027afb9535c5b04a74e072de2e68fe0eb90ec7da0b5f3e13d32dde7ac`. Shared `OpennessSession.Changes.cs`, `.Capabilities.cs`, and `.ObjectModel.cs` also matched the planning audit hashes. The isolated OPC UA run is not a full repository/release regression claim.

## Incomplete scope

The new library and CLI provide a complete **offline envelope/identity review and client impact workflow**. They do not provide native interface engineering. MCP registration, exact PLC mapping/dependency closure, target probing, native discovery/import/export, all prepared mutations, generation, roles/security/certificates, save/reopen/recovery and physical protocol acceptance remain pending. The [API guide and coordinator handoff](../reference/opc-ua-api.md) lists the integration files and the smallest native feasibility packet. The [expansion plan](../plans/expansion-plans/opc-ua-engineering.md) remains open; no 1.0 promise is downgraded or accepted VM evidence rerun requested.
