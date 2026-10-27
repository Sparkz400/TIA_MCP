# Offline GRAPH Host and Explorer workflow

This workflow needs the .NET Host and supplied GRAPH XML, with no Siemens product.
It uses the existing `graph-v6-structure.v2` reader. An inspection is a structural
document review, not full schema validation, native rendering, compile/import
permission or runtime qualification. Unknown content and reader diagnostics stay
visible. The original bytes are retained without XML normalization.

## MCP flow

1. Upload XML using `artifact_upload_begin`, `artifact_upload_chunk` and
   `artifact_upload_commit`. The source must be 1 byte through 8 MiB.
2. Call `graph_inspect` with that committed `xmlUploadRef`. Retain the returned
   `recordRef`, artifact names and source/report hashes. The document owns copies
   of `original.xml`, `report.json` and self-contained `viewer.html`; the upload
   can now be released.
3. Call `graph_report_page` with the exact handle and an array pointer such as
   `/diagnostics`, `/unsupported`, `/graphs/0/sequences/0/steps`,
   `/graphs/0/sequences/0/transitions`, `/graphs/0/sequences/0/branches`,
   `/graphs/0/sequences/0/connections` or
   `/graphs/0/sequences/0/steps/1/actions`. Follow `nextOffset` until null.
   Indices are zero based. Scalar fields are available in the full report.
4. Download a named artifact with `graph_artifact_read`, retaining the same handle
   and name while following byte offsets. Independently check the assembled byte
   count and SHA-256. `receipt.json` is also transferable; its digest is embedded
   in the handle.
5. Inspect another source and call `graph_compare` with `beforeRef` and `afterRef`.
   Its `/changes`, `/beforeDiagnostics` and `/afterDiagnostics` arrays are pageable.
   Matching ambiguity, changed opaque XML and diagnostic differences stay explicit;
   no difference result certifies behavioral equivalence.
6. After restart, use `graph_reopen` or `graph_list`. Reopen and transfer verify
   receipts, artifacts and both exact comparison inputs. Incomplete and damaged
   publications remain discoverable; they are not silently repaired or overwritten.

The [JSON Schema](../../contracts/graph-workflow.v1.schema.json) defines all six
offline tool contracts. Tool calls accept opaque handles and fixed artifact names, never
arbitrary host paths. External XML entities and DTD resolution are rejected.
The standalone HTML escapes source and uses a no-script content security policy.

## Native export and import

These steps need the V21 worker and an attached offline project. They are compiled
against the V21 PublicAPI only and are **not natively qualified**.

- `graph_export` takes an exact `blockPath` (or unambiguous `blockName`) and reuses
  the `block_read` export (`PlcBlock.Export`, `ExportOptions.WithDefaults`). Blocks
  whose `ProgrammingLanguage` is not `GRAPH` are refused after the export. The
  unchanged SimaticML stays under the Host `exports` directory, and its exact bytes
  are retained as a document record bound to the exporter's SHA-256. The result
  carries the block, the export artifact and the record summary, so the record works
  with every `graph_*` tool above. Each export uses one record of the bounded store.
- The prepared change kind `graph_import` imports one GRAPH v6 document with
  `change_prepare`/`change_apply`. The payload is `graphRef` (a retained document)
  or `path` (a file inside the Host artifact directory), plus
  `expectedBlockNames` naming exactly that one block. `TiaMcp.Graph` targets .NET 10
  and cannot load in the .NET Framework 4.8 worker, so the Host validates the
  document first. It refuses reader errors, unsupported grammar outside the opaque
  envelope and embedded-logic boundaries, more than one chart, and empty
  `Interlock`/`Supervision` modules without `FlgNet` (`EmptyNativeCondition`). TIA V21
  exports that shape but rejects it on import; see the
  [native evidence](../../tests/TiaMcp.Graph.Tests/fixtures/native-v21/README.md).
- The worker then uses the `block_import_xml` route with its scope, number,
  protection, language-conversion and checkpoint checks, and accepts only one GRAPH
  block. Apply requires `compile=true`. After compilation the block is re-exported,
  and this readback must equal the imported document apart from `DocumentInfo` and
  formatting before the project is saved; otherwise the change is
  `recovery_required`. Import documents exported with defaults, such as
  `graph_export` output, because missing default attributes fail that comparison.
- Review the declared change before applying: `graph_export` the current block, then
  `graph_compare` it with the document to import. The GRAPH comparer runs in the
  Host and is not part of the worker's apply gate. Typed GRAPH edits are not provided.

## Explorer

Open an existing portable project snapshot, select an exact native block, then
choose **Previews → Attach supplied GRAPH report** and select `report.json`.
This is a local, caller-supplied historical association: the viewer does not prove
that the XML belongs to the selected native block. No native project is changed.

The viewer checks the report profile, bounds, false qualification flags and the
retained XML byte count/hash. It does not independently reparse the XML to verify
the supplied report's interpretation or authenticate its author. The panel keeps
this distinction visible, including after export/reopen.

Navigate sequences and select step, transition and branch records. Their retained
records include actions, conditions, interlocks, supervisions and source fragments.
Chart pre/post operations, alarms, complete selected-sequence connections,
diagnostics and opaque content are expandable. Diagrams are schematic document
connections, with no inferred execution order or native coordinates. Chart-scoped
duplicate steps/transitions, missing targets and cross-sequence destinations are
not drawn as resolved local edges; branch identities remain sequence scoped.

**Download original XML** preserves exact source bytes. **Download GRAPH report**
serializes the supplied report; JSON whitespace may differ from the original file.
**Export JSON** creates a derived snapshot with the attachment pinned to its exact
snapshot/session/project/object reference. The original capture remains unchanged.
Loading another capture clears unsaved local associations. Changing selection
while a file import is pending rejects the import instead of remapping by name.

## Limits and remaining work

- Host: 128 records, 2 GiB total including incomplete publications; 96 MiB per
  generated report/HTML; 64 KiB transfer chunks; 1–200 rows and 128 KiB per page.
  Oversized individual rows become explicit omission records with the complete
  content available through artifact transfer. Discovery lists 1–50 records per
  page by record ID; relist after additions. No automatic retention deletion.
- Explorer: 12 MiB report input, 8 MiB retained source, 2,000 sequences and 100,000
  inventory rows; diagrams show at most 500 nodes and 1,000 connections. Export
  remains subject to the existing 32 MiB snapshot limit. Larger valid Host reports
  must be reviewed through paged results or standalone HTML.
- Transfers recheck retained inputs on each call. This favors integrity; maximum
  size transfer throughput and memory are not performance-qualified.
- `graph_export` and `graph_import` are implemented but not natively qualified.
  Typed GRAPH editing, schema acceptance, native layout and runtime remain
  separate unexecuted gates on this workstation.
  CFC, DCC and other language products are separate X06 work, not GRAPH support.

See [validation evidence](../verification/graph-host-workflow-20261016.md).
