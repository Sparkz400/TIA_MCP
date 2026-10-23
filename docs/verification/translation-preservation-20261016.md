# Translation document preservation audit — 16 October 2026

This L05/D03 increment audits the existing supplied-workbook path. It introduces
no vendor profile, native mutation admission, fabricated Siemens API or new tool.

## Reproduced defects

The worksheet reader accepted numeric row spellings such as `0002`, but the
overlay indexed raw attribute strings and looked up canonical `2`. An accepted
workbook could therefore fail candidate creation with a missing dictionary key.
The overlay also collected every descendant `row`, including row-like elements
inside opaque extensions, causing duplicate keys or selecting an unrelated node.
The reader admitted multiple or nested-only `sheetData` containers.

Selected text containers were checked through their child elements and attributes.
XML comments/instructions were overlooked, so rewriting the text could discard
them. Shared-string index attributes could also disappear. Nested elements inside mapped text or a shared-string index were flattened
through `XElement.Value`, concealing unsupported content as an apparent value.

`artifacts/translation-preservation-dev/before.log` reproduces six failing groups
with the old implementation (72/78 pass). `mcp-before/verification.json` uses the
previous frozen Host and independently reproduces `TranslationWorkflowFailed`
from the public candidate call; its single Host exits normally without workers.

## Repair and compatibility matrix

One shared row selector requires exactly one direct worksheet data container.
Both the reader and overlay use it, indexing validated numbers invariantly while
preserving the original XML attributes. Opaque extensions remain outside the edit
index. Mapped text/index elements cannot contain nested XML. The existing edit
admission path checks all nodes that would be removed by an overlay, so it also
protects approval and three-way reconciliation.

A subsequent independent-verifier audit found that selecting a changed cell
removed the entire cell from the final XML comparison. Existing attributes were
checked, but annotations were omitted and a previously missing cell had no
attribute comparison. External edited workbooks could therefore remove/add cell
annotations or introduce new-cell metadata without a preservation refusal. The
verifier now compares all nodes outside the text containers and permits only the
address/type attributes on a newly created cell.

| Case | Expected result |
| --- | --- |
| Leading-zero and signed/whitespace numeric row attributes already accepted by reading | Candidate succeeds; original row attribute remains unchanged. |
| An opaque extension containing a duplicate-looking row | Extension remains unchanged and cannot become an edit destination. |
| Duplicate direct or nested-only worksheet data containers | Structured `UnsupportedWorkbookProfile` refusal during capture. |
| Comments/instructions inside selected text containers or shared index | Exact no-op remains available; changed review/approval is blocked. |
| Nested elements inside mapped text or shared index | Structured capture refusal; no flattened value is admitted. |
| Cell-level comments/instructions retained by the overlay | Text edit succeeds and those annotations survive. |
| Reconciliation removing/introducing annotated text | Same preservation refusal before proposals can be admitted. |
| Reconciliation changing non-text nodes of selected cells or metadata on new cells | Independent `TranslationPreservationFailure`, without masking the whole selected cell. |
| UTF-8 BOM, UTF-16 LE and UTF-16 BE worksheet input | Exact original ZIP for no-op, correct edited values, byte-identical unrelated parts. |

`after.log` passes the six repaired groups plus all previous checks (78 total).
`matrix.log` passes all 81 checks after adding encoding, retained cell annotation
and reconciliation cases. `mcp-after/verification.json` passes four workflow stages
across two clean Host lifetimes, retaining hashes of Host/Core/Translations and
the harness. Actual calls cover malformed capture refusal, visible review issues,
approval refusal, exact candidate retrieval/restart and fresh reconciliation.

`reconciliation-before02.log` reproduces the independent-verifier gaps (81/83
pass), and `mcp-reconciliation-before/verification.json` reproduces the accepted
new-cell metadata through the actual Host. The earlier `reconciliation-before.log`
had an overly broad XML-reserialization fixture; the corrected fixture preserves
unrelated worksheet bytes and first checks the valid candidate control. That earlier
partial failure is retained but is not the new-cell defect proof.
`index-attribute-before.log` separately reproduces shared-index attribute loss.
`final-library.log` passes all 83 checks. `mcp-final/verification.json` passes five
stages and two clean Host lifetimes, including explicit external-workbook metadata
refusal. These development checks are followed by frozen-source validation below.

## Aggregate evidence

Validation uses an initial complete aggregate and a final targeted delta. Both
are immutable staged Git archives; the distinction matters because the second
independent-verifier defect was found while the first run was executing.

- `artifacts/translation-preservation-01`: tree
  `ed76403cb00d7b143ce7165701e67f849343680a`, 1,787 files. All 29 managed suites,
  1,266 Python tests (one unavailable-native-evidence skip), 24 Explorer checks
  and all actual MCP workflows pass. Its portable package also passes, but this
  tree predates the final reconciliation and shared-index attribute fixes.
- `artifacts/translation-preservation-02`: final tree
  `f843661e60286d27ec3cf0f6d317ce81e50134d5`, 1,787 files. Python/contracts/source
  bindings pass all 1,266 tests (one skip). The affected managed suites pass all
  83 library, 49 Host and 160 synthetic import/recovery checks. The final actual
  translation MCP workflow passes five stages/two clean Host lifetimes, and all
  41 actual Host admission checks pass.
- The final rebuilt portable package passes all eight stages: 133 deployment
  guards, exact ZIP/hash/notice checks, original and Unicode-relocated launch,
  local Git/restart, and GRAPH/OPC UA/VCI/translation/diagnostic workflows. Its
  expanded translation workflow exercises the final refusals on the relocated
  binary. Strict final verification confirms no bundle writes.

The source differences between these trees are exactly `TranslationWorkbook.cs`,
`TranslationReview.cs`, their library tests, the translation MCP harness, generated
source bindings and the two translation documentation files. The final delta
rechecks every affected translation consumer and the full packaged document
workflows. No claim is made that the first 29-suite aggregate used the later
verifier bytes. Only documentation changes follow the final tested tree.

Exact retained digests:

- First source manifest: `244988e2cc02191abc32af77003140b065c0b608cdd3303024beb6c77f369356`.
- Full aggregate result: `cc75de873ad2e571e967a061a92089a6ee7430db8cb86dbcfb83bc34436060f1`.
- Final source manifest: `964c716b24cc3d7497419b3c871642010a3de7662bcac74dbbb8baff0067ef85`.
- Final Python result: `18d7fe652f997bd6635b4dddc17a5bffdd758042dc01307874c6acb30865984e`.
- Final translation suite result: `8aff31f4116566a2c67b29270dcc31e2ce9d8bbdeec0830ed33725d682340af8`.
- Final translation MCP result: `81c52171c525c068a05959c5853a822a03be6b9fd8c5cadd307360d455b8bdce`.
- Final package result: `1448afd4fe173016403adc73e47bbf0e439b07be7212ff671e56e664c0d87eec`.

The final bundle is `source/artifacts/releases/tia-mcp-offline_20261016_153735_cf9f87d6`
beneath `artifacts/translation-preservation-02`. It contains 1293 files;
manifest SHA-256 `ec31dbe5a464f74db05ba5417bd82fe2a5d2745d4dac0a204a91717431525ce8`,
ZIP SHA-256 `7a05110e30bd56a5d93674be3fa4f7a85d59984c75cb2c05f4d26ab58d20a8dc`.

The translation repair commit `7142f5e` fully passes hosted package, Python,
managed and OPC UA checks (`38926062639`) and Explorer (`38926062735`). Source metadata remains
175 declared tools / 55 mutation kinds / 248 features, with six matching retained
files and 106 unavailable historical files; no qualification is promoted.

## Remaining boundaries

All new cases are authored synthetic document variants. No original workbook
receipt was replaced or reclassified. Genuine missing vendor exports, native
import/save/reopen/recovery, restricted category behavior and full source/HMI
compatibility still need their own evidence. Library and Host tests do not prove
that Siemens accepts the generated candidate. The full L05/D03 scope remains open.
