# Offline AutomationML / CAx document review

This workflow needs the managed Host and supplied XML, with no Siemens product.
It inspects declared CAEX structure, compares retained documents and reports
possible effects through declared hierarchy, links and class references.
These document tools neither export a TIA project nor import or rewrite an AML file.
The separate native [`cax_export` tool and `cax_import` change kind](#native-cax-export-and-import)
need TIA Portal V21 and feed the same record store.

## User workflow

1. Upload a file using `artifact_upload_begin`, `artifact_upload_chunk` and
   `artifact_upload_commit`. Call `automationml_inspect` with its `xmlUploadRef`.
2. Retain the returned `recordRef`. The document owns exact `original.aml` bytes
   and `report.json`; the original upload may now be released. The source suffix
   does not change its encoding, BOM, comments, processing instructions or bytes.
3. Page `/objects`, `/attributes`, `/references`, `/links`, `/opaqueContent` and
   `/diagnostics` with `automationml_report_page`. Follow `nextOffset` until null.
   Object paths and parent paths identify positions in this exact file. Attribute
   rows retain nested paths, owner, ID, name, type, unit, values and defaults.
   Plain text/CDATA is a scalar; attributed or annotated/nested XML produces
   `null` plus an opaque-value diagnostic. An absent value is `[]`, while an
   explicitly empty value is `[""]`.
4. Inspect another file, then call `automationml_compare` with exact `beforeRef`
   and `afterRef` handles. Page `/changes`, `/beforeImpacts`, `/afterImpacts`,
   `/beforeDiagnostics` and `/afterDiagnostics`.
5. Download `original.aml`, `report.json` or `receipt.json` with
   `automationml_artifact_read`. Follow byte `nextOffset` until `complete`, then
   independently check full length and SHA-256. Oversized report rows explicitly
   refer you to this complete download.
6. After restarting the Host, use `automationml_reopen` or `automationml_list`.
   Every read verifies the receipt, retained artifacts and comparison inputs.
   Incomplete and integrity-failed publications remain visible in the inventory.

The [six tool contracts](../../contracts/automationml-workflow.v1.schema.json)
accept upload/record handles and fixed artifact names, not arbitrary file paths.
Comparison inputs must be documents; comparisons cannot be chained as documents.

## Native CAx export and import

Both routes use Siemens `CaxProvider` from the attached TIA Portal V21 project.
They are compiled against the V21 PublicAPI only and are not natively qualified.

`cax_export` exports the open project, or one exact device (`hardwarePath`, the
device path from `device_inventory`), into a new `cax-<id>.aml` under the Host
exports artifacts. The V19+ `TransferResult` overloads write no log file, so the
worker retains the transfer result (state, error/warning counts and nested
messages, up to 50,000) as `cax-<id>.transfer-result.json`. The response returns
both SHA-256 values, up to `maxItems` messages with explicit truncation, and the
`automationml` summary of a new document record. Use its `recordRef` with
`automationml_report_page` and `automationml_compare`, or as the `cax_import`
`amlRef`. Publication keeps the 8 MiB inspection bound; a publication error is
reported beside the still valid export. The export reads the in-memory project
and does not save it; `projectModifiedBefore`/`projectModifiedAfter` report
unsaved edits. An error state or a missing AML file refuses with the retained
transfer result. Siemens excludes drives, most HMI devices and listed
Scalance/system device types from CAx exchange.

`cax_import` is a prepared change kind (`change_prepare`, then `change_apply`).
Its payload names `importOption`, spelled as Siemens names `CaxImportOptions`,
and either `amlRef` or a `path` inside the Host artifact directory plus
`expectedSha256`. The Host verifies the record or the artifact-root boundary.
The worker copies the file (at most 64 MiB) into the change directory and pins
its hash; apply verifies it again. Apply uses the normal whole-project checkpoint,
requires `compile=true` and compiles the device project.

Siemens states that "CAx Import APIs cannot be used within exclusive access".
Apply therefore releases its admitted mutation lease only around the native
import. The kind is limited to a worker-owned TIA instance and the Host's
admitted `change_apply` route, so no interactive edit can interleave.

| `importOption` | Siemens behavior for name-conflicting devices | Configured-hardware check |
| --- | --- | --- |
| `MoveToParkingLot` | Keeps project devices; imports the AML versions into a parking-lot folder | Existing rows unchanged; only additions |
| `RetainTiaDevice` | Keeps project devices; skips those AML devices | Existing rows unchanged; only additions |
| `OverwriteTiaDevice` | Replaces project devices with the AML versions | None possible; the difference is recorded for review |

The change readbacks hold the native transfer result
(`cax-import.transfer-result.json`), the configured-hardware manifest after the
import (the prechange manifest is a backup) and `hardware-difference.json` with
added, removed and changed hardware paths. An error state fails into
`recovery_required`. `change_restore` verifies the restored hardware manifest
against the prechange one. Software, HMI and unexposed vendor data changed by the
import are covered only by the generic engineering fingerprint, and no mapping
between AML IDs and native objects is established.

## Supported inspection profiles

| Profile | Root namespace | Link endpoints |
| --- | --- | --- |
| CAEX 2.15 | None | Exact InternalElement ID plus `:` plus ExternalInterface name |
| CAEX 3.0 | `http://www.dke.de/CAEX` | Exact ExternalInterface ID |

The public XSDs are embedded from pinned AutomationML repositories. See
[source hashes and provenance](../../third-party/automationml/sources.json).
`SchemaValid` in the managed model (`schemaValid` in JSON) refers only to that
XSD check. A supported, well-formed but schema-invalid document is retained with
validation diagnostics. Unsupported root/version/namespace and malformed or
over-limit XML refuse before publication. Schema locations and external files
are never fetched. The Siemens V21 manual specifies CAEX 2.15; our independent
3.0 reader does not imply that TIA imports 3.0. See the
[Siemens AML format documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-hardware-data/aml-file-format?contentId=XPbYagciSgxdewCGyWfnuA).

Objects separate instance hierarchy from class/library definitions. Unique IDs
are compared exactly; absent IDs are not inferred from names or positions.
Classes/libraries may match through unique exact class paths. Duplicate IDs,
including IDs on Attributes, and duplicate sibling interface names remain
diagnosed. External aliases, missing/ambiguous class targets and unsupported
endpoint syntax remain unresolved. A 2.15 endpoint matches the full declared
ID/name composite, including colons inside names; multiple possible matches
remain ambiguous. Direct, explicitly declared links are
retained, including parallel links; they are not evidence of physical wiring.

The reader stops at opaque extension payloads, including `AdditionalInformation`.
Familiar element names inside those payloads do not become devices. Opaque
subtrees are identified by path and hash; full content remains in the original.
Original bytes also retain root-external annotations and text positions.
No inheritance, mirrors, companion files or vendor-specific semantics are
expanded. Address/type/tag strings are observations, not validated Siemens
addresses, catalog selections or native object associations.

## Comparison and impact limits

`changes` reports added, removed, changed, ambiguous and unmatched declarations.
Subtree hashes conservatively include descendants, annotations and formatting.
A changed ID appears as removal/addition; it does not silently match by name.
Moving an object between identifiable parents also requires review. Cross-profile
comparison makes no reliable-match claim. Byte-identical inputs have no changes.

Before/after impact rows traverse declared containment in both directions,
resolved links in both directions, and class-to-consumer references. This is
deliberately conservative: a hierarchy can bring every sibling into the review.
It is not an electrical, runtime or Siemens import dependency calculation.
An opaque-only change may have no named impact rows. **Every byte change sets
`wholeDocumentReviewRequired=true`**, even when `changes` is empty.
`nativeQualified`, `importabilityCertified` and `impactScopeComplete` remain
false. `matchingReliable` concerns only extracted document keys, not native IDs
or coverage of all semantics. Missing prerequisites never become a safe-import
decision.

## Bounds and retention

Input: 1 byte..8 MiB, XML depth 64, 250,000 reader events, 50,000 elements,
128 attributes per element, 8,192 characters per XML attribute, 16,384 characters
per constructed class path, and at most 2,048 diagnostics. Exceeding any bound
refuses rather than truncating the inspection. DTDs and entity resolution refuse.
Reports are limited to 64 MiB. Pages contain 1..200 rows within 128 KiB; oversized
rows are marked omitted and remain in the downloadable report. Downloads return
at most 64 KiB. Storage holds at most 128 records and 2 GiB, including incomplete
publications; list pages contain 1..50 rows. Storage inventory offsets are for
discovery, so relist after additions.

Records publish intent first and the receipt last. No existing publication is
overwritten. Receipts bind exact artifact hashes and comparison inputs. The
workflow neither resumes native work nor repairs damaged retained files.

See [verification](../verification/automationml-workflow-20261016.md) and the
[D05 implementation/native qualification plan](../plans/expansion-plans/automationml-cax.md).
