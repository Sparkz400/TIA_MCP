# AutomationML offline workflow verification - 16 October 2026

Base: `c2a6bb2daa92989b6d41ba8f67d2093103e6b78c`. That inventory-export
commit passed hosted runs `38940028093` (Explorer) and `38940028137` (offline).
This increment adds six offline document tools. Native exchange remains closed.

## Exact source and executed checks

| Source snapshot | Executed result |
| --- | --- |
| `artifacts/automationml-workflow-01/source`, tree `71df6055206f3211ebcdf197ac50cf4bf6b37054` | Initial full aggregate and portable package pass; predates the opaque-scalar and endpoint-composite corrections. |
| `artifacts/automationml-workflow-02/source`, tree `8f561156f76d39bdc8dafb68c4c5c8d077f521fd` | Full aggregate: 30 managed suites, 1,280 Python tests with one unavailable-evidence skip, 24 Explorer model/client checks and all actual MCP workflows. Includes 75 AutomationML managed and 65 MCP checks plus portable package/relocation. Contains the opaque-scalar correction. |
| `artifacts/automationml-workflow-03/source`, tree `3a3edd26d07d94daf09f13c8008bd1fa7e3cafd0` | Final endpoint-composite correction: zero-warning/error Host build, 77 AutomationML parser/comparison/store checks, 69 actual MCP checks, 1,280 Python tests with one unavailable-evidence skip, source bindings/contracts and the complete portable package/relocation workflow pass. |

The complete 30-suite aggregate was not rerun locally after the final endpoint
correction. That delta changes only the AutomationML parser, its managed/MCP
regressions, generated bindings and documentation. Final-source targeted and
packaged checks cover the delta. Hosted CI runs the complete aggregate on the
pushed commit. No result above is presented as a different source's result.

Final source manifest: `artifacts/automationml-workflow-03/source-manifest.json`,
1,818 files, SHA-256
`a31523b430ca0b615a8ade0f821e63127ce5f32f639defdf838429a5b7c4202f`.
Implementation/contract/harness/fixture bytes match that frozen source. Only
result documentation changes after validation.

Baseline-02 aggregate receipt:
`artifacts/automationml-workflow-02/source/artifacts/checks/result.json`, SHA-256
`394e73f89096dd27ce660b467396076c11e63f36d03bc5ee018644a45c674246`.
Its source-manifest SHA-256 is
`cc009410bfe8750d85ae3bad55db1d2cdfba59ee6e355ab9627a2e0ad3fdc621`.

Final artifact paths below are relative to the final frozen source:

| Receipt | SHA-256 |
| --- | --- |
| `artifacts/python-checks/result.json` | `416b9bec04b4ca190e22c3da0292c409b63b8fb3cb33668fd8eb53be508d0a85` |
| `artifacts/automationml-targets/verification.json` | `c2622ca78adebd6b64b3b1b0e70538660426eba84659c9076ec8069d4cc77207` |
| `artifacts/automationml-mcp/verification.json` | `aaabb8e4f8253f28b3bf3b12c6070b5f8c8fdd856c244c2b4c16fb23791e660b` |
| `artifacts/package-checks/result.json` | `6699c5cf522bb0232a662a64281c315e953a950601ea3fe77495ec1447375592` |
| `artifacts/package-checks/automationml-mcp/verification.json` | `b3e3a643b0488e4503594bd34dea04e415fbf5a2101cf8c4819e3ddf4a1b58fb` |

## Package delivery

Final bundle: `artifacts/releases/tia-mcp-offline_20261016_180144_c973dfec` under the final source.
Its ZIP SHA-256 is `0b1596ad1549fa4d83c940ea9aa02653585a1cc11cc8f3fa3175c03a7e32ae02`.
Bundle manifest SHA-256: `7d92447d452fdadfb90d4e133cb35b2eb9c7016b7062498257f581ea547ff7d4`.
The original and Unicode-path relocated copies pass strict checks, exact ZIP
comparison and actual GRAPH, AutomationML, OPC UA, VCI, translation and diagnostic
workflows. Both schema source files and MIT notices remain in the bundle.

The AutomationML packaged workflow passes 69 checks. All three owned Host exits
are zero and unforced with no native workers started; the development Host flow
has the same clean-lifetime result. Hash consistency is not publisher signing,
clean-machine installation or desktop MCP client acceptance. Earlier bundles
under snapshots 01/02 predate the final endpoint correction.

## Coverage and reproduced failures

Seven upstream examples and both XSDs retain exact pinned-source bytes and
licenses; three Python tests check provenance, tool contracts and the independent
oracle's ability to detect missing declarations. Python independently compares
object/link inventories against the actual Host's public-fixture reports.
Contract validation covers 181 declared tools, 55 mutation kinds and 254 features.

Managed/MCP cases include separate 2.15/3.0 endpoint forms, schema-invalid
inspection diagnostics, missing/duplicate IDs, Attribute/device ID collisions,
duplicate interface names, external references/schema locations, opaque vendor
payloads, root-external annotations, changed identities, nested address values,
parallel links, conservative connected/hierarchical impact, 10,000-device input,
DTD/malformed/over-limit XML, exact transfer, upload release, restart, tampering,
comparison lineage, bounded/oversized rows and incomplete publications.

Two additional review findings were reproduced before their fixes:

- `artifacts/automationml-dev/opaque-before.log`: schema-valid vendor XML inside
  Attribute Value was flattened into scalar text. Final values retain an unknown
  scalar (`null`) and diagnostic for nested/annotated/attributed content; original
  bytes remain exact. Plain text/CDATA, absent and empty values stay distinct.
- `artifacts/automationml-dev/colon-before.log`: a colon in an interface name
  prevented 2.15 link resolution. Final matching indexes the full declared
  ID/name composite; multiple possible matches and owner-ID collisions remain
  ambiguous. Both positive and ambiguous cases pass.

Development also had two compiler errors corrected before runtime validation.
Upstream CRLF/trailing whitespace is deliberately unchanged. Authored-file
whitespace checks exclude these pinned third-party/fixture paths; provenance
checks verify their exact bytes.

## Remaining gates

No TIA, Openness SDK, PLCSIM, device, native import/export, compile, save/reopen,
catalog compatibility, whole-device preservation, physical wiring or runtime
check was executed. Public fixtures establish format behavior only; synthetic
names do not establish Siemens semantics. `nativeQualified`,
`importabilityCertified` and `impactScopeComplete` remain false.

See the [user workflow](../reference/automationml-workflow.md),
[D05/C02 implementation and native gates](../plans/expansion-plans/automationml-cax.md)
and [full offline queue](../plans/offline-execution-20261016.md). The broad goal
and those obligations remain open. No task branch or additional worktree was
created; unique validation evidence is retained under `artifacts`.
