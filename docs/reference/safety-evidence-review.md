# Supplied safety evidence review

This workflow runs without Siemens products. It compares two supplied
`safety_inspect` JSON observations for one exact PLC in a saved project review.
It retains opaque supporting artifacts, including supplied reports, without
interpreting their proprietary formats. It neither modifies nor certifies a
safety program. The [full D14 plan](../plans/expansion-plans/safety-validation-and-fio.md)
keeps native capture, F-I/O, activation-test management and specialist acceptance open.

## Use through MCP

1. Import an existing saved project review using `artifact_upload_begin`,
   `artifact_upload_chunk`, `artifact_upload_commit`, then `project_snapshot_import`.
   Select the exact captured PLC object path from that review.
2. Upload original before/after `safety_inspect` JSON responses through the same
   upload tools. `safety_validation_inspect` responses are accepted too; their
   F-parameter and activation-test sections compare as other reported content. Preserve original bytes; do not round signatures through a
   client that stores all JSON numbers as floating point.
3. Call `safety_evidence_review` with `reviewId`, `plcPath`, `uploadRef` (before),
   and `comparisonUploadRef` (after). Optionally supply `sources`, an array of
   `{uploadRef, phase, purpose}` objects. `phase` is `before`, `after` or `context`;
   `purpose` is a nonblank label up to 256 characters. Each exact phase/purpose/
   original-filename combination must be unique. Roles are supplied declarations.
4. Keep the returned `recordId` and `reportArtifact`. Use `safety_evidence_report`
   with `section` of `summary` (default), `observations`, `signatures`, `issues` or
   `changes`. Page using `offset`, `count` (1..200), and `nextOffset` until null.
   Oversized rows explicitly say `omitted`; download the complete report with
   `artifact_read`. Receipts also expose exact original and supporting references.
5. Uploads may be released after a successful review. The retained originals,
   supporting files and saved review are revalidated on every reopen, including
   after Host restart. Changed bytes, noncanonical references and a changed
   derived report refuse; successful reopening never replays a native operation.

## Evidence semantics and bounds

Both inputs must select the same project file and exact captured, nonvirtual PLC.
If a block is selected, both observations must name that same block under the PLC.
Duplicate JSON fields or row identities, malformed UTF-8, incorrect scalar types,
negative/fractional/overflow signatures and absent scope fields refuse.

Program and block signatures are exact unsigned 64-bit values. The `signatures`
section supplies `valueDecimal` strings, and signature differences supply
`beforeSignatureDecimal` and `afterSignatureDecimal`, for clients that cannot
represent all UInt64 values. Original JSON numeric values remain in the artifact.
Zero signatures, absent signature lists, unavailable selected-block services and
reported gaps/warnings/truncation become explicit issues. Required product-specific
signature sets are not inferred from these supplied observations.

Runtime groups and settings are compared by exact name; signature rows by exact
type. Unknown fields and nested array order remain significant. Other top-level
reported content is compared separately. Equal observations are not proof that a
native program is unchanged, valid or safe. A missing row is only a difference
between observations, not proof of a native deletion.

Each observation is at most 4 MiB and 32 JSON levels, with at most 128 signatures
per scope, 4096 runtime groups, 4096 settings and 4096 warnings. At most 16
supporting files are accepted, each 1 byte..16 MiB, 64 MiB total including repeated
roles. Reports are bounded to 16 MiB and pages to 128 KiB. Dedicated retention is
bounded to 512 MiB including incomplete files; capacity failure does not publish
a successful record. Previously published content-addressed artifacts may remain.

Every receipt/report explicitly keeps native execution/qualification, source
authenticity, live freshness, required signature profile coverage, whole-program
analysis and safety approval false. Opaque supporting files are downloadable bytes,
not verified links to live equipment or parsed Siemens reports. Never use this
comparison as authorization to download, operate equipment or approve safety logic.

Contract: [safety-evidence-review.v1](../../contracts/safety-evidence-review.v1.schema.json).
Validation: [exact evidence](../verification/safety-evidence-review-20261017.md).
