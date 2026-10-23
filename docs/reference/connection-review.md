# Supplied connection review

This offline workflow compares supplied connection observations or a proposed
after-state against one exact saved project review. It works without Siemens
products. It does not discover live connections, validate native properties,
check resource capacity or execute connection changes.

## Workflow

1. Import a supplied `project-review.v1` with `artifact_upload_*` and
   `project_snapshot_import`, or select an existing exact `reviewId`.
2. Prepare `connection-review-request.v1` JSON using
   `contracts/connection-review.v1.schema.json`. Supply `reviewId`, `afterKind`
   (`observation` or `proposal`), `before`, `after` and explicit `matches`.
3. Upload it with `artifact_upload_begin`, `artifact_upload_chunk` and
   `artifact_upload_commit`. Call `connection_review(reviewId, uploadRef)`.
4. Retain the returned `recordId`, original `input` and `reportArtifact` references.
   Download either artifact using its `path` with `artifact_read`.
5. Use `connection_review_page(recordId, section, offset, count)` for `summary`,
   `observations`, `comparisons`, `consumers` or `matches`. Reopening revalidates
   input and saved review hashes and independently rederives the report. Upload
   release and Host restart do not remove retained inputs or reports.

The managed `TiaMcp.Connections.Tests` suite emits `mcp-fixture.json` with an
executable synthetic request and saved review. The actual Host harness imports
that review, performs the workflow, downloads exact artifacts and restarts the
Host. The fixture includes PLC-to-PLC property changes, PLC-to-HMI partner changes,
declared consumers and deliberately unmatched program connections. It is not a
native capture or communication test.

## Input and interpretation

Both snapshots contain explicit `connectionsComplete` and `consumersComplete`
Boolean attestations, `connections` and `consumers`. Neither completeness flag can
prove whole-project inventory, runtime-generated connections or missing consumers.

A connection contains `id`, `origin`, `protocol`, `local`, `partner` and
`properties`. The ID is a supplier-defined exact key within that phase, not a
certified native object identity. Origins are `engineering`, `program` or
`hmiSoftware`; an explicit match cannot substitute one origin for another.
Protocol text is retained exactly. S7, TCP, UDP, ISO, ISO-on-TCP, FDL, PtP and HMI
are marked as advertised families; that flag establishes no target availability.
Other nonempty protocol text remains visible without invented interpretation.

Each endpoint has nullable `ownerPath`, `interfacePath` and `reason`. Missing paths
require a reason. An absent, virtual, non-hardware or case-mismatched path stays
unresolved. When both exact hardware nodes exist, the review follows captured
`parentId` links to check their ancestry; it never infers ownership from a path
prefix, name, address or array order. Missing/cyclic ancestry cannot prove the
relationship. `capturedHardwareAncestry` only describes this saved model chain.
It does not prove a native communication endpoint or that the item exposes an
interface service: both `nativeOwnershipVerified` and `interfaceCapabilityVerified`
remain false.

`properties` is an opaque JSON object. Unknown vendor properties, nested content,
nulls, multiline strings and Unicode remain in the original and report. Properties
are compared structurally, including numeric values without double-precision
rounding. JSON number spelling and whitespace are retained in the original input;
the difference report does not treat equivalent numeric spelling as a native edit.
Missing versus null properties differ. `changedFields` uses escaped property names
after `properties/`; a nested change identifies its complete top-level property.
No property name is treated as a writable native setter.

`matches` contains explicit `beforeId`/`afterId` pairs, each used at most once.
There is no automatic match, even for equal IDs, names, ports or addresses. Mapped
content becomes `changed` or `unchanged`; other rows become `unmatchedBefore` or
`unmatchedAfter`. Unmatched observations do not prove creation, deletion or unused
resources. Renaming a supplier key through an explicit match alone is not a
connection-property change.

A consumer has nullable `objectPath`, nullable `connectionId` and a required
supplied `reason`. Its report separately identifies whether the object is captured,
whether the connection key is supplied in that phase, and that connection's
comparison status. This is a declared association, not native dependency proof.
Unknown references remain unresolved. A nonnull consumer connection ID missing
from an asserted complete connection list is a contradiction and refuses.

Every observation has `resourceAssessment: notAssessed`. Equal ports or local IDs
do not become generic conflicts, and no CPU capacity table is fabricated.
Native property/resource profiles, compatibility, compilation and communication
require separate evidence. `nativeExecutable`, `nativeQualified`,
`sourceAuthenticityVerified`, `liveFreshnessVerified` and
`wholeProjectAnalysisComplete` always remain false.

## Bounds and integrity

Input is 1 byte..4 MiB strict UTF-8 JSON with depth at most 32. Duplicate fields
and malformed Unicode refuse, including inside opaque properties. Selectors are
nonempty, control-free text; opaque values may contain valid control characters.
Each phase allows at most 512 connections and 2,048 consumers, with 512 explicit
matches. Missing or unknown structural fields refuse; extension properties belong
inside `properties`.

Reports are capped at 16 MiB. Retention is capped at 512 MiB, including incomplete
publication files. Existing immutable artifacts are never replaced. Pages contain
1..200 indexed rows within 128 KiB. An oversized row is explicitly marked omitted
with its index; the downloadable report retains it in full. Pagination uses the
exact retained record, never an implicit latest report.

See the [D08 implementation plan](../plans/expansion-plans/communication-connections.md)
for documented protocol scope, native provider gaps, resource profiles, lifecycle,
consumer integration and qualification. This workflow implements supplied evidence
review; it does not complete the connection-engineering domain.
