# Supplied DB snapshot XML review

This offline workflow inspects one supplied InterfaceSnapshot XML export or
compares two. It works without Siemens products. It retains the original bytes,
including encoding, and associates them with one explicitly selected DB in a
saved review. That association is supplied by the caller: XML block names do not
prove the originating CPU or program. Supplied XML uses no network connection or
worker; [native capture](#native-capture-and-owned-simulation-value-loads) uses the worker.

1. Import a saved `project-review.v1` with `project_snapshot_import`, or reuse an
   existing review. Select its exact nonvirtual block `objectPath`; captured
   `metadata.kind` must be GlobalDB, InstanceDB or ArrayDB, with a `plcPath`.
2. Transfer XML through `artifact_upload_begin/chunk/commit`.
3. Call `db_snapshot_review(reviewId, objectPath, uploadRef,
   comparisonUploadRef?)`. Omit the last argument for inspection. Export names
   must match the selected captured name exactly. Same-name DBs elsewhere are
   never selected implicitly. Both comparison inputs use this one selection;
   cross-project/program mapping is not implemented.
4. Keep `recordId`, `input`, optional `comparisonInput` and `reportArtifact`.
   `artifact_read` downloads exact retained artifacts with their SHA-256.
5. `db_snapshot_review_page(recordId, section, offset, count)` revalidates the
   saved review, original inputs and rederived report, including after Host
   restart and upload release. Sections are `summary`, `beforeValues`,
   `afterValues`, `changes`. A single inspection has no after values.

The output contract is [db-snapshot-review.v1.schema.json](../../contracts/db-snapshot-review.v1.schema.json).
Inputs use the documented `Document/SW.Blocks.InterfaceSnapshot/AttributeList`
shape with Name, Snapshot/SnapshotValues/Value and optional SnapshotDate and
StructureModified. Value requires exact Path and Type attributes and scalar text.
The unqualified container and the documented Snapshot/v1 namespace are supported.
Unknown structural namespaces, ambiguous fields, duplicate paths, nested value
markup and unknown value elements refuse. DTDs and external entities are disabled.
This is a bounded document-shape reader; the vendor XSD and genuine V21 captures
are unavailable and `xsdValidated` is always false.

Path/type/text differences use ordinal equality, preserving array indices,
nested paths, unknown vendor types and large numeric strings. `TRUE` and `true`
are different text. XML-standard decoding and newline/attribute normalization
apply to extracted values; originals remain byte-exact. No scalar interpretation,
start-value conversion or runtime equality is inferred. Missing rows mean only
absence from that supplied XML. Opaque envelope content, annotations and extra
attributes stay in the original artifact; they are not semantically interpreted.
`originalBytesEqual` distinguishes any byte change from equal extracted values.

Dates remain unvalidated text. `structureTimestampComparison` reports only
same/different text, unknown, or not compared. Even equal dates cannot establish
compatible structures; a future or older date cannot establish current data.
All results keep nativeExecutable, nativeQualified, sourceAuthenticityVerified,
liveFreshnessVerified, programIdentityVerified and structureCompatibilityVerified
false. These reports cannot authorize native changes or online value writes.

Bounds: 4 MiB and 20,000 values per XML, XML depth 32 and 200,000 reader events;
16 MiB retained report; 512 MiB store including incomplete publications;
1..200 rows and 128 KiB per page. A value too large for a page is explicitly
omitted there and remains downloadable in the complete report and original.
Failure during publication may retain an input without a final receipt; it never
replays native work. Unavailable/corrupted inputs or saved source prevent reopen.

## Native capture and owned-simulation value loads

`db_snapshot_capture(reviewId, objectPath, projectName?)` replaces steps 2–3 for
the open project: the worker exports the snapshot stored on that exact DB with
`InterfaceSnapshot.Export` (read-only, no online access) and the Host reviews it as
above. The record and receipt add `inputSource`
(`nativeInterfaceSnapshotExport` with the export's project path, PLC path and DB
path), which must match the saved review's DB and `model.project.projectPath`;
the report's `association` becomes `nativeExportOfSelectedDB`. All verification
flags stay false: stored snapshot values are not current CPU values. A DB without
a stored snapshot refuses with `DbSnapshotUnavailable`.

Two prepared change kinds work only against a ticket-bound owned PLCSIM instance,
online through the `plc_online_compare` route; without a `downloadTicket` they
refuse with `PhysicalPlcRefused`. `db_snapshot_create` stores the instance's
actual values as the DB snapshot (a checkpointed project change).
`db_snapshot_load_actual` needs the `recordId` of a capture and
`acknowledgeActualValueWrite: true`; it writes the stored snapshot as actual
values only while the DB's stored snapshot still hashes equal to the reviewed
capture. Supplied-upload records cannot authorize a load. Payloads and limits are
in the [kind table](../guides/engineering-workflows.md).

The [D09 plan](../plans/expansion-plans/online-compare-snapshots-upload.md) covers
the remaining CPU/program identity, comparison, capture, upload and mutation work.
This review does not complete those obligations.
