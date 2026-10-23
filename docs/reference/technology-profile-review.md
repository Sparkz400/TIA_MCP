# Supplied technology-object profile review

Use `technology_profile_review(reviewId, uploadRef)` with a committed UTF-8 JSON
upload and an exact saved `technologyObject` selection. v1 requests retain profile
JSON; v2 also retains independently uploaded source files, v3 adds consumer context,
and v4 adds typed supplied encoder connections. Use
`technology_profile_report(recordId, section, offset, count)` to reopen the report.
Neither tool needs or launches Siemens products. They do not execute proposals.

## Workflow

1. Import a saved project review with `project_snapshot_import`, or use its existing
   review ID. Find the exact technology object path in that saved model.
2. Create a `technology-profile-review-request.v1` or `.v2` packet using the
   [strict contract](../../contracts/technology-profile-review.v1.schema.json).
   Supply `reviewId`, `technologyPath`, `afterKind` (`observation` or `proposal`),
   and complete `before`/`after` objects. Upload through `artifact_upload_begin`,
   `artifact_upload_chunk`, `artifact_upload_commit`.
3. Call `technology_profile_review`. The returned receipt identifies immutable
   original input and report artifacts. Repeating the same call is idempotent.
4. Page `observations`, `relationships`, `changes`, `context` or `sources` through
   `technology_profile_report`; its default `summary` returns the original receipt.
   Download complete artifacts through `artifact_read`, including oversized rows.
   Releasing the original upload does not remove retained review evidence.

Each phase contains `identity`, `valueDomain` (`offline`/`online`), explicit Boolean
`parametersComplete` and `relationshipsComplete`, plus `parameters` and
`relationships` arrays. Before/after identity declarations must match exactly.
The identity requires `plcPath`, nullable `orderNumber`, `firmware`, `technologyType`,
`technologyVersion`, an `options` string array, and nullable `unknownReason`. Missing
profile fields require a reason. Captured TO owner/type/version metadata, where
present, must agree. This detects contradictions; it cannot verify CPU compatibility,
product licenses or current native identity. Proposals require offline domains.

A parameter supplies exact `name`, nullable `nativeType`, `access` (`readOnly`,
`readWrite`, `unknown`), `value`, and an opaque `metadata` object. Value forms:

```json
{"kind":"scalar","text":"9007199254740993"}
{"kind":"opaque","text":"original composite/array representation"}
{"kind":"unavailable","reason":"Value was not captured"}
```

The first two examples preserve text exactly; no number, Boolean, enum, unit, array
or native-type conversion is performed. Original native types/access declarations,
limits and unknown metadata remain inspectable. A proposed changed value contradicts
before `readOnly` even when after metadata claims `readWrite`; unknown access stays
unknown. Supplied writable access never yields an approved mutation.

A relationship requires exact `id`, `kind` (`hardware`, `coupling`, `mapping`,
`dependency`, `child`, `cam`, `interpreter`), `sourcePath`, nullable `targetPath`,
nullable `targetMember`, `state`, nullable `reason`, and opaque `metadata`:

- `declared` requires a target path.
- `disconnected` requires null target and member.
- `unknown` requires a reason and never means disconnected.

Paths resolve only to unique nonvirtual objects in the selected saved review.
Missing paths remain unresolved; no display-name, case-folding or cross-project
matching occurs. A captured target does not verify its DB member, hardware channel,
telegram, address unit, direction, service, supported TO version or firmware.
Cycles are reported over the supplied declared relationships; they do not establish
that a native relationship is invalid. Relationship IDs, not array order or endpoint
resemblance, determine before/after matching. Preserve specialized native fields in
metadata rather than asking this tool to guess their semantics.

`changes` reports exact changed fields and `onlyBefore`/`onlyAfter` rows. Missing
rows do not prove addition or deletion, even when supplied coverage is marked complete.
`context` retains the selected saved node, up to 128 ancestors and 1,024 direct saved
edges. Missing/cyclic ancestry and truncated edge context are explicit. Graph edge
selection requires the exact node ID and object path together. Context links to the
existing saved graph; it neither edits that graph nor claims complete downstream impact.

## Typed supplied encoder observations (v4)

Use `technology-profile-review-request.v4` with all v3 fields and an `encoders`
object containing `before` and `after`. Each phase has exactly `complete` (Boolean)
and `observations` (0–256 rows). This is a supplied-observation format, not a
Siemens export or an executable connection command. The existing tools retain,
reopen and page it; no additional tool or native adapter is required.

Every row supplies `id`, `provider`, `interfaceName`, `state`, `route`,
`sensorIndex`, `connectOption`, `target` and `unknownReason`. IDs are exact ordinal
strings local to the phase; provider/interface labels remain supplied text.
States are `connected`, `disconnected` and `unknown`. Sensor index is null or
0–2. Connect option is null, `Default` or `AllowAllModules`; the last value does
not certify eligibility. Missing sensor/option values, unknown state/route,
incomplete connected target capture or unknown target fields require a reason.

| Route | Exact target shape |
| --- | --- |
| `module` | `{nodeId, objectPath}` |
| `splitModules` | `{inputModule, outputModule}`, each an exact saved selector |
| `channel` | `{module, number, type, direction}`; module is an exact selector, other fields may be null with a reason |
| `addresses` | `{plc, input, output}`; PLC is an exact selector, each address is `{value, unit}` |
| `dbMember` | `{block, member}`; block is an exact selector, member is exact supplied text |
| `startdriveTelegram` | `{recordId, phase, telegramId}` selecting an existing drive record and its exact original row |
| `unknown` | null target and an explicit reason |

A disconnected row must have a null target. A known route with an uncaptured
target may remain connected/unknown with a reason; it does not become disconnected.
Connected plus sensor index zero, or disconnected plus a positive sensor index,
is retained as a declaration contradiction. It is not silently corrected.

Saved selectors require the path and node ID to resolve to the same unique,
nonvirtual row of the appropriate kind in the declared PLC. Missing, ambiguous,
mismatched, virtual, wrong-kind, different-PLC and unknown-scope outcomes remain
explicit. A captured module does not verify a channel, and a captured block does
not verify a DB member. Member paths are not parsed or rewritten.

Address values are nullable nonnegative Int32 values. Units are independently
`bit`, `byte` or `unknown` for each direction. Only explicit bit/byte values
normalize to Int64 bit addresses; the largest result is 17,179,869,176. Unknown
units stay unknown, including readback units that the source did not establish.
Equal addresses never identify an object or prove physical wiring.

A telegram reference must name the observation's phase and the same exact saved
review. Intake and every reopen use the shared verified drive reader, including
its original input. An old untyped drive record yields `unsupportedVersion`;
a missing exact telegram ID remains missing. Original telegram observations,
their drive comparison, drive identity/domain and captured hardware resolution
are retained. V21's documented 81/83 number check is reported only within a
declared `TO_ExternalEncoder` profile; it does not validate CPU family, firmware,
version, license, geometry, route availability or motion.

`encoderConnections`, `encoderChanges` and `encoderCoverage` are new page
sections; v1–v3 records explicitly refuse them. Original array indices and rows
remain intact. Exact IDs determine changes, independent of array order. The
required before/after selector phase alone does not manufacture a connection
change; underlying telegram changes remain separately visible in target evidence.
Supplied consumer declarations retain their v3 parameter/relationship subjects;
encoder rows are not inserted as native graph edges or inferred consumers.

V4 permits at most eight distinct retained drive dependencies and 64 MiB of
their original record/input bytes per evaluation. Shared dependency reads also
charge the enclosing composite review's byte budget. Encoder output is checked
incrementally against the existing 16 MiB report limit; the final full report
is checked again before publication. The existing source-file, storage and page
limits remain. A proposal may change at most 500 parameter, relationship and
encoder records in total. Oversized page rows remain downloadable in full.

The official V21 [general encoder routes](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-encoders)
and [Startdrive encoder route](https://docs.tia.siemens.cloud/r/en-us/v21/technology-objects/s7-1500-motion-control/connecting-encoders-configured-by-startdrive)
establish distinct connection forms. Context7 indexed only the older V20
collection during this implementation; the V21 task pages were checked directly.
Native signatures, source capture, complete target geometry and installed-product
behavior still require SDK/product qualification. Composite telegram/encoder
selectors and complete C02 cross-domain constraints remain open.

## Bounds and retention

Inputs: 4 MiB, JSON depth 32, 4,096 parameters and 1,024 relationships per phase,
64 declared options. A proposal permits at most 500 changed parameter/relationship
records. Observations retain larger changes within collection/byte bounds. Duplicate
JSON fields, parameter names, relationship IDs, malformed Unicode, unknown top-level
fields, contradictory states and mismatched saved selections refuse before publication.

Reports: 16 MiB; retained workflow directory: 512 MiB including incomplete files.
Pages: 1–200 rows, at most 128 KiB. Oversized individual rows are explicitly omitted
from pages while remaining intact in downloadable artifacts. The context section is
one row, so a large context may require artifact download. Every reopen rehashes the
original input, reads the exact saved source and recomputes the report. Changing a
report and merely naming it with a new hash cannot grant qualification or alter evidence.

All native-execution, qualification, compatibility, source-authenticity, freshness
and whole-object-completeness flags remain false. This is supplied evidence review,
not native discovery, connection configuration, compile, tuning or motion validation.
Genuine metadata/exports, whole native XML preservation, specialized service semantics,
change admission, save/reopen/recovery and bench acceptance remain in the
[full D13 plan](../plans/expansion-plans/motion-and-technology-objects.md).

## Independently retained source files (v2)

Start from the same profile packet, set `schemaVersion` to
`technology-profile-review-request.v2`, and add a `sources` array. Upload each source
file separately through the existing artifact-upload tools before submitting the
profile packet. Each source declaration has exactly these fields:

| Field | Meaning |
| --- | --- |
| `label` | Unique exact label, up to 256 characters; Unicode normalization is not inferred. |
| `purpose` | Explicit supplied purpose, up to 1,024 characters; it is not a certification. |
| `phase` | `before`, `after` or `context`. |
| `fileName` | Exact display filename supplied when that source upload began; no path. |
| `uploadRef` | The `upload:...` reference returned by committing that source upload. |
| `sha256` | Exact lowercase SHA-256 of its original bytes. |
| `bytes` | Exact integer byte count. |

Use 1–16 sources, 1 byte–16 MiB each, at most 64 MiB in declared total. Reusing the
same bytes under multiple purposes still counts each declaration toward that total.
The input packet retains its separate 4 MiB limit. All declarations and uploaded
bytes are checked before any source is copied into owned retention. Each filename,
digest and byte count must match its committed upload. Binary, UTF-16, BOM and line
endings remain exact; filenames/extensions do not make their formats trusted.

The immutable v2 report has a `sources` table with label, purpose, phase, filename,
byte count and an owned artifact reference. The `sources` page section returns those
rows; download the bytes with `artifact_read`. Equal bytes can share physical storage
while retaining distinct declared labels/purposes/phases. No source is parsed,
executed, converted, or automatically associated with a native TO by its content.

After successful intake, all original source uploads and the input upload may be
released. Reopen with the returned record ID: the Host rehashes its owned source
copies and recomputes the manifest from retained input. Corruption, unavailable
owned bytes, mismatched lengths or forged report associations refuse. Intake does
not silently repair corrupted retained files from still-available uploads. An
explicit restoration of the original bytes can make the same record readable again.

Intake retries require the committed uploads; record reopening does not. A capacity
or I/O failure during publication may leave owned partial files, which count against
the same 512 MiB retention cap. It cannot produce a successful record. An explicit
retry revalidates inputs and existing copies; there is no automatic replay or cleanup
that could erase evidence. Ordinary v1 records retain their original representation,
hash and receipt; querying their `sources` section returns an empty table.

Source authenticity, native format/schema conformance, TO identity, executable
membership and complete consumer/impact coverage remain unverified. The v3 extension below supplies the consumer handoff; native qualification remains open.

## Declared consumers and saved impact context (v3)

Use `technology-profile-review-request.v3` with the same profile fields, a `sources`
array (0–16, with the existing per-file/aggregate limits), `consumers` and
`impactLimits`. `consumers` contains exactly `before` and `after` arrays, each with
0–256 declarations. An empty list makes no completeness claim. Each row has:

| Field | Meaning |
| --- | --- |
| `id` | Exact consumer ID, unique within its phase, at most 256 characters. |
| `subjectKind` | `parameter` or `relationship`. |
| `subjectKey` | Exact parameter name or relationship ID in that phase, at most 256 characters. |
| `objectPath`, `nodeId` | Both exact saved identities, or both null for an explained unknown. |
| `member` | Supplied member text, at most 1,024 characters, or null; never resolved by name. |
| `state` | `declared` requires an object identity; `unknown` requires a reason. |
| `reason` | Explicit explanation, at most 1,024 characters, or null. |

Unknown/extra fields and duplicate IDs refuse. Composed/decomposed Unicode, case,
member names and phase associations are never normalized. A subject missing from
that phase is retained as `missingDeclaration`, without guessing an association.
Object resolution distinguishes `capturedObject`, `missing`, `ambiguous`,
`identityMismatch`, `outOfScope`, `scopeUnknown` and `notDeclared`. Both the path and
node ID must select the same unique nonvirtual object with the declared PLC owner.
Other PLCs and virtual nodes are out of this handoff's scope; absent PLC ownership
is unknown. A captured identity does not verify a native consumer link or member.

`consumerChanges` matches only exact IDs across phases and retains changed fields,
`onlyBefore` and `onlyAfter`; absence does not prove native creation/removal.
The `consumers` table retains every original row and its separate resolution.

Supply these integer `impactLimits`: `impactDepth` 1–5, `maxImpactPaths` 1–200,
`maxImpactEdgeVisits` 1–20,000. Returned paths also have a fixed 1 MiB aggregate cap.
The existing saved-review traversal follows incoming simple dependency paths,
retaining distinct diamond paths and stopping cycles. Roots are the selected TO
and captured same-PLC consumers with declared state and a present subject in that
phase. Both phases use the **same saved review**, not two native captures. Roots
are labelled `suppliedContextRoot`: they are context candidates, not proven changes
or runtime effects. Repeated consumer associations share a traversal root.

Only supported, resolved captured edges with exact endpoints are followed. Supplied
consumer/relationship declarations are never inserted as captured graph edges.
Ambiguous, mismatched, virtual and out-of-scope objects are excluded. `impactTraversal`
records limits, truncation reasons, visited edges, returned paths, excluded-edge
counts, excluded-node count and policy. The exact original saved review remains the
source for excluded evidence. No paths does not establish no impact; completeness
and freshness remain unverified even when no traversal bound is reached.

Page `consumers`, `consumerChanges`, `impactPaths` or `impactTraversal` with the existing
report tool. Traversal metadata is one row; oversized pages use the existing explicit
omission/download behavior. The v3 record recomputes all consumers and graph context
from retained input and the exact saved review on every reopen, and rehashes any owned
source bundle. Rehashed report forgeries still refuse. Existing v1/v2 records keep
identical bytes and receipts and return empty tables for these new sections.
