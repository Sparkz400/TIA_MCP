# OPC UA annotation preservation — 16 October 2026

This D04/L05/F02 increment repairs offline NodeSet comparison and bounds XML
allocation. It adds no tool, dependency, vendor dialect or native admission.

## Reproduction and repair

The XML reader discarded comments and processing instructions before inspection.
The selected-field projection consequently reported no reviewed changes when
only these annotations changed, including inside vendor extensions. Declared
consumers could receive `noChangesInReviewedFields` for those changed inputs.

The reader now retains annotations. Each reviewed node exposes a nullable
`annotationsSha256`, hashing annotation content, processing-instruction targets
and positions throughout its subtree. Node-level annotation differences require
review and participate in direct and transitive declared-consumer impacts.
Annotations outside nodes contribute to the envelope hash, including prolog,
epilog, namespace/alias/model metadata and root extensions. Existing opaque
element/text hashes and normalized namespace/alias identities remain separate.
Annotation position hashing is deliberately conservative: formatting/reordering
around annotations may require review; this is not runtime-change certification.

The streaming preflight also enforces 500,000 XML reader events before DOM
allocation, including comments, instructions, text and element events. Existing
16 MiB, depth-64, node, edge and report bounds remain. Unknown content is neither
silently truncated nor discarded to make it fit.

## Development evidence

Receipts are under `artifacts/opcua-preservation-dev`:

- `before.log`: original library fails five new groups (32/37 pass), covering
  node/envelope annotations, positions, consumer effects and the new event bound.
- `mcp-before/verification.json`: the previous frozen Host reproduces the public
  comparison omission; it exits normally without native workers.
- `final-library04.log`: 38/38 library groups pass. Cases cover direct node content,
  references, localized text, values, definitions, vendor extensions, namespace
  and alias content, model containers, root/prolog/epilog, annotation movement,
  instruction targets, UTF-8 BOM/UTF-16 LE/BE and immutable source bytes. Comment,
  instruction and opaque-element floods refuse before DOM allocation.
- `text-boundary-before.log`: review caught an intermediate false breaking-change
  classification for text split by an annotation (37/38 pass). Element/text hashing
  now joins those text segments while retaining mixed-content order; the final
  library check passes without treating unchanged method-argument text as changed.
- `offset-before.log`: a follow-up adversary moves an annotation within unchanged
  text while keeping its node index. The intermediate hash misses it (37/38 pass).
  Final annotation identities also include the preceding text offset.
- `dependencies.log`: 28/28 checks pass, including actual CLI subprocesses and an
  annotation change propagated through an independently expected dependency path.
- `mcp-after/verification.json`: 52 actual Host checks pass, including exact
  annotated downloads, paged changes/consumer impacts and restart retention.
  Receipts now bind the harness and Host/Core/OPC UA DLL hashes, alongside the
  apphost and schema. Initialization keeps ownership visible to failure cleanup.

All new XML variants are authored adversaries. They do not establish Siemens
mapping support, genuine product compatibility or effective server behavior.

## Frozen validation

Both frozen snapshots contain 1,790 tracked files and use base commit
`332693126b00de589abc4f0a703f09ecb78bf872`.

- `artifacts/opcua-annotations-01`: tree
  `6d09cbefa64c4aa1c412bf2b3fd050bd4b9b8844`; source manifest SHA-256
  `b07cce7f3d28e859dee0ea636c5a2ecdfdc23d2f9afbb5bddbeaa19bc3a10197`.
  Its isolated OPC UA runner and full aggregate pass: 29 managed suites,
  1,273 Python tests with one unavailable-native-evidence skip, 24 Explorer
  model/client checks and all portable MCP workflows. Aggregate receipt
  `source/artifacts/checks/result.json` SHA-256:
  `2292c9aeed1e3fd4a74df59e0968772caaa57b66c96aef7804db1231328881af`.
  This snapshot predates the final text-offset correction and must not be used
  as evidence for that correction.
- `artifacts/opcua-annotations-02`: final tree
  `bce8a51b544bea3795ed35c82ab145f856e92f9d`; source manifest SHA-256
  `e4e9e20789d04c0eea3aa80dc5812a7c229ad13de343e25388768981604d00a8`.
  All 1,273 Python tests pass (one unavailable-native-evidence skip), along with
  contracts, source bindings and the evidence inventory. The isolated OPC UA
  runner passes 38 parser and 28 dependency/CLI checks; the Host build passes,
  all 24 workflow checks pass, and 53 actual MCP checks pass across two clean
  Host lifetimes without native workers.

Final snapshot receipts:

- `source/artifacts/python-checks/result.json` SHA-256
  `386e047fb8d57f62a02e95a1bb5eb7b2d3c5d6e51c357d813f1f28e834796b0b`.
- `source/artifacts/final-checks/result.json` SHA-256
  `5a3ca68918b7ace67a045a22f357a2090a5d05e8be53880f74ab16a0bb35f8aa`.
- `source/artifacts/final-checks/mcp/verification.json` SHA-256
  `aabc7fb13b33faf51814b7d7df842c57a02327c9b9bd308cee539ae1c6359563`.
- `source/artifacts/opcua-offline/cb79257c8a86445b952519f28f9409d9/result.json`
  SHA-256 `5533b077bb08ac6ed804604150fac75b0a2f260ab9739e118e290d5e68cc6d68`.

The snapshots differ only in the annotation-offset implementation, parser/MCP
regressions, generated source bindings and this verification document. Final
non-documentation files must match snapshot 02; later edits are documentation only.
The catalog remains 175 declared tools, 55 mutation kinds and 248 features. Six
historical evidence inputs match; 106 remain unavailable and unqualified.

OPC UA commit `6bf024e` passes all hosted jobs: Python, managed, portable package
and isolated OPC UA in `38933806994`, Explorer in `38933807041`. No new
portable ZIP, browser UI or clean destination installation was tested locally
for this increment.

## Remaining gates

Native provider discovery, model/mapping import/export, product-specific schema
compatibility, permissions, certificates, endpoints and runtime consumers still
need their actual products and targets. Native qualification remains false. The
full offline queue and release obligations remain active.
