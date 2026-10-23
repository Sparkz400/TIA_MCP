# Persistent project reviews

`project_snapshot_capture` saves the native hierarchy, selected standard block XML and bounded direct relationship observations in the host's `artifacts/project-reviews` directory. The returned `reviewId` is the SHA-256 of the entire saved document. `project_snapshot_compare`, `project_snapshot_search` and `project_health_report` use that document without starting TIA and remain usable after a host restart.

## Use saved reviews without Siemens installed

Upload the exact `project-review.v1` JSON returned by an earlier capture using
`artifact_upload_begin`, `artifact_upload_chunk` and `artifact_upload_commit`.
Call `project_snapshot_import` with `{ "uploadRef": "upload:<committed-id>" }`.
This imports a review document into the local artifact store; it does not open or
import a TIA project. It requires no Siemens worker, SDK, license or connection.

The returned `reviewId` is the SHA-256 of the original bytes. Import retains those
bytes independently, so releasing the upload or restarting the Host does not lose
the review. Repeat import of the same bytes returns the same ID; an existing
corrupted destination is refused rather than overwritten. Use `artifact_read`
with the returned artifact path to download the exact saved document. Source
artifact paths embedded in the review remain data and are never opened.

Intake accepts UTF-8 JSON without a BOM: 64 MiB per review, 50,000 nodes and edges,
200 selected block/type XML documents combined, 16 MiB per XML document and 40 MiB total XML.
It rejects duplicate JSON keys, ambiguous object/node identities, foreign document
paths, malformed relevant metadata, mismatched resolved relationship endpoints,
incorrect XML hashes or derived signatures, DTDs/entities and XML nesting beyond
128 levels. Unknown supplied fields are retained. These are structural and
consumer-specific checks, not full Siemens project-model/schema validation.
Publication shares a lock with local capture, uses a complete temporary file and
retains a 1 GiB store limit including interrupted temporary files.

Use the returned ID with search, compare, health and `diagnostic_plan`. Every Host
review response includes `sourceAuthenticityVerified:false` and a source evidence
policy; diagnostic plans and reports retain the same warning. Native-origin labels
and reported observations inside the original file are supplied claims. Hashes
prove byte identity, not authenticity, fresh project state or native qualification.
No saved review can certify complete project content through this route.

Supplied type/UDT documents use the same `documents` entries as block exports:
an exact `objectPath` whose captured node has `kind: "type"`, `status: "exported"`,
`xml`, `utf8Sha256` and the existing `signatures` object. Virtual nodes, other
node kinds and duplicate document paths are refused. `status: "unavailable"`
retains an explicit missing-content observation. No native UDT schema, member
type compatibility or alarm configuration is certified by structural XML intake.

For measurements, `trace_read` accepts exactly one existing hashed `artifact` or
committed `uploadRef`. Uploaded JSON/CSV bytes are copied into diagnostic retention
before parsing (8 MiB input, existing 256 MiB store limit), and remain available
after upload release. Invalid inputs may retain their raw bytes for inspection;
they do not produce a valid measurement or grant native qualification. Existing
explicit CSV mapping, exact review/trace IDs and acquisition-quality rules apply.
The [diagnostic guide](trace-diagnostics.md) explains signal selection and analysis;
[the intake verification record](../verification/review-import-20261016.md) records
executed portable checks separately from native work.

## Capture and compare

Call `project_snapshot_capture` with a `request` object. It accepts `projectName`, `plcPath`, `maxVisited`, `maxBlocks` (0–200, default 25), `blockPaths` (unique exact native paths) and `maxRelationshipRoots` (0–25, default 5). Relationship queries cover the first selected blocks in exact-path order, with at most 500 nodes and 1,000 edges per root. Protected/safety or unsupported block languages are recorded as unavailable. Excluded blocks remain omitted, never implicitly equal.

Capture checks native metadata again after the exports and refuses a changed metadata revision. Sequential exports are not an atomic content snapshot. Coverage and separate observation times remain in the result. Limits are 16 MiB per XML document, 40 MiB of selected export input, 64 MiB per saved review and 1 GiB for the review store. Archive selected artifacts manually before exceeding the store limit.

Call `project_snapshot_compare` with `beforeId`, `afterId` and optional `allowDifferentProjectCopy` (default false). Cross-project comparison requires explicit opt-in and matches exact paths relative to each project root; it does not guess renames. Added/removed paths are changes to the captured scope, not proof of project creation/deletion. The report distinguishes metadata, interface, network logic and full-document changes. It retains direct observed incoming dependencies and adds bounded dependency paths with explicitly incomplete impact coverage.

XML comparison ignores only top-level `/Document/DocumentInfo/Created`. Whitespace, IDs, namespaces, comments and unknown payload remain significant. An equal captured subset does not establish whole-project equivalence. Changed native identifiers can therefore produce a document change even if logic signatures match.

Type/UDT XML uses the same interface/network/full-document comparison as blocks.
`contentComparedTypes` and `contentUnknownTypes` count matched type paths with
both exports or with missing/unavailable content respectively; existing block
counters retain their meaning. A block-to-type kind transition belongs to both
categories. Paths absent from one model remain scope additions/removals, outside
these matched-path counters. Opaque supervision payload changes remain visible
as `documentChanged`, even when interface and network signatures are unchanged.

The native `project_snapshot_capture` selection still exports standard blocks
only. Supplied type evidence is accepted through `project_snapshot_import`;
native type capture and alarm relationship extraction remain separate work.

## Explain dependency impacts

`observedImpactPaths` explains why an object is a candidate for review after a captured change. For example, an interface change to `Motor` can return `Main → Conveyor → Motor`, with the original edge objects, source locations, evidence origins, observation times and content-revision qualifications for both steps. These are static dependency paths; they do not prove runtime effects, interlock behavior or a compile failure.

Paths are evaluated separately in the before and after snapshots. Each row contains `reviewSide`, `reviewId`, exact `changedObjectPath` and `affectedObjectPath`, relative `changedPath`, the original `change`/`changedFields`, `distance`, `objectPaths` in source-to-changed-object order, and corresponding unmodified `edges`. Before-side paths remain available when an object leaves the captured scope; that event remains `removedFromCapturedScope`, not a deletion verdict. Different project copies keep their own exact paths, and edges from different snapshots are never joined.

Supplied alarm evidence can express `HMI → alarm → supervision → member → UDT`
through `hmiBinding`, `dependsOn`, `reads` and `usesType` edges. Set `impactDepth: 4`
to include this whole chain; the default depth of three reports truncation.
A changed UDT interface then identifies declared consumers for review. The tool
does not infer member paths from XML, manufacture alarm links, decide whether a
type change is legal in TIA, or prove a runtime effect. Every edge and endpoint
must already be present in each supplied snapshot; unknown links remain gaps.

Traversal follows incoming `calls`, `dependsOn`, `hmiBinding`, `instanceOf`, `libraryVersion`, `reads`, `usesType` and `writes` edges only. Edge evidence must be `resolved` and either `nativeConfirmed` or `parsedSupported`; both endpoint paths and node IDs must match captured nodes. Optional endpoint resolution cannot contradict the edge. Containment, other relationship kinds, unresolved/inferred evidence and missing/mismatched endpoints are excluded and counted separately for each snapshot in `impactTraversal.excludedEdges`. No names or unresolved endpoint hints are used to invent a path.

The compare tool accepts these additional bounds:

| Parameter | Default | Range |
| --- | ---: | ---: |
| `impactDepth` | 3 | 1–5 edges |
| `maxImpactPaths` | 100 | 1–200 paths across both snapshots |
| `maxImpactEdgeVisits` | 5,000 | 1–20,000 adjacency inspections across both snapshots |

The serialized path array also has a fixed 1 MiB limit; evidence is omitted as a whole path when it cannot fit, never shortened. `impactTraversal` reports the effective limits, returned path count, inspected-edge count, serialized path bytes, `truncated`, and specific `truncationReasons`. Depth is reported as a truncation only when another non-cyclic supported path can extend beyond it. Paths are breadth first for each changed object, in exact relative-path order, before snapshot then after snapshot. Distinct paths through a diamond remain visible; a path never repeats an object. The shared bounds can therefore consume the budget before later changed objects or the after snapshot are reached. Increase the relevant bound or compare narrower saved captures when needed.

`impactTraversal.complete` and `impactCoverageComplete` always remain false because captures contain only bounded relationship observations. `truncated: false` means this traversal hit none of its limits, not that the project has complete relationship coverage. No returned paths does not establish no impact. Impact paths cover the entire comparison independently of the `changes` page; `offset`/`maxItems` do not paginate them. The existing `directObservedImpacts` and `impactTruncated` retain their original after-snapshot direct-observation semantics and 200-row bound; use `impactTraversal.truncated` for the new path analysis.

## Search and health

`project_snapshot_search` accepts `reviewId` and a literal, case-insensitive `query` of 1–256 characters. It searches exact object paths/names, XML text and the native `Text`, `Name` and `Value` attributes used by code tokens and declarations. Results contain exact object paths and excerpts. The text index is not a source line number. Results describe the saved snapshot, not subsequent live edits.

`project_health_report` accepts `reviewId`. It reports native inconsistency flags, protected/unavailable objects, unavailable selected source and unresolved relationship endpoints. An unresolved endpoint is a coverage finding, not proof of a broken reference. This report does not compile, establish that an object is unused, validate safety or inspect physical wiring.

All three offline tools support `offset` and `maxItems` (1–200). Search retains at most 10,000 observed hits and reports truncation. Review IDs reject traversal, artifact reads reject symlinks/junctions, and every read verifies the saved content hash. XML DTDs and external entities are prohibited. Project text is never executed.

## Qualification

Five independent core test groups cover persisted reads, tampering, exact project identity, incomplete exports, meaningful XML differences, dependency impacts, token/comment search, health findings, bounds and unsafe XML. The content/impact group also runs `ProjectReviewImpactTests`: changed-interface chains, cycles, diamonds, exact evidence, deterministic ordering, separate before/after graphs, partial scope absence, cross-copy paths, excluded edges, all traversal limits and the serialized byte bound. These are offline fixtures and do not extend native qualification. `scripts/qualify_project_reviews.py` exercises capture before/after a compiled source change in a fresh owned scratch project and repeats the offline tools after TIA/host shutdown. Native status must be taken from that run's retained report and exact binary hashes; implemented tools alone are not native proof.

The 9 October native run (`artifacts/continuation-20261009/reviews-native-01.json`) passed on frozen build `59fb741484b34684a8e12fdf760777af`: both source captures, comment search, logic/document comparison and health reporting completed; the exact owned TIA process exited; comparison/search/health produced persisted results in a new host without launching a native worker. The full regression gate passed with 125/125 core checks and 27 simulator checks (log (`artifacts/continuation-20261009/review-full-regression-05.log`)). These results qualify the recorded fixture and build, not arbitrary project contents.
