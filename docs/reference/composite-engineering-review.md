# Combined engineering review: retained C02 workflow

`CompositeEngineeringReview.Build(artifactsRoot, requestBytes)` now evaluates
explicit associations between retained AutomationML, saved model, connection,
drive and technology records. Five offline MCP tools retain and revalidate
these combined reviews. Native rebind is not implemented.

The [v1 request](../../contracts/composite-engineering-request.v1.schema.json)
and [v1 result](../../contracts/composite-engineering-review.v1.schema.json)
preserve the original declared mapping evaluation. The
[v2 request](../../contracts/composite-engineering-request.v2.schema.json)
uses the same fields with an explicit `composite-engineering-request.v2`
version; its [v2 result](../../contracts/composite-engineering-review.v2.schema.json)
adds bounded saved/component consumer context. The explicit
[v3 request](../../contracts/composite-engineering-request.v3.schema.json) and
[v3 result](../../contracts/composite-engineering-review.v3.schema.json) add typed
telegram/address/encoder selectors and original-reference constraints. These schemas
define exact fields. Existing component records are reopened through the same
integrity/rebuild checks used by their public paging workflows. Original AML
bytes are rechecked and parsed. No caller-supplied report bypasses those checks.

Each `before` and `after` context supplies one `reviewId`, one original AML
`automationMlRef`, and exact `connections`, `drives` and `technology` record
arrays. Every component in a phase must name that phase's saved review.
Different or incomplete host/project-path declarations require
`allowDifferentProjectCopy: true`; that admission does not verify project
identity or freshness. The two phases may use the same review for a proposal.
Each context also retains the component report's schema, observation/proposal
role, summary counts, declared coverage and limitations. Existing read-only,
unknown-access, unresolved-reference or incomplete-coverage findings remain
visible alongside the combined associations.

## Exact selectors and mappings

Every selector includes its phase and its exact retained reference. Supported
kinds are:

- `amlObject`, `amlAttribute`, `amlLink`: original AML reference and report path.
- `model`: review ID, node ID and object path; virtual rows do not resolve.
- `connectionEndpoint`: component record, supplied connection ID and
  `local`/`partner` endpoint.
- `driveParameter`: component record, exact name, number and array index.
- `technologyRelationship`: component record and exact relationship ID.
- V3 `driveTelegram`: drive record and exact telegram `id`.
- V3 `driveTelegramAddress`: drive record, telegram `id` and exact `addressId`.
- V3 `technologyEncoder`: technology record and exact encoder observation `id`.

An AML path selects a position in those original bytes. Duplicate IDs remain
ambiguous at every affected path. A missing row remains unresolved; it does not
prove a deletion. Drive identities/value domains and TO identities are retained
with their selected evidence. Disconnected TO declarations remain distinct.

Mappings include an ID, phase, purpose, reason, source and target. Their current
supported pairs are deliberately explicit:

| Purpose | Source | Target | Result meaning |
| --- | --- | --- | --- |
| `hardware` | Instance AML InternalElement or ExternalInterface | Saved hardware row | Declared association only |
| `communication` | AML link with resolved document-local endpoints | Supplied connection endpoint with captured ancestry | Declared association only |
| `parameter` | AML attribute | Exact supplied drive parameter | Declared association only; no conversion or range/access approval |
| `dependent` | Model, connection endpoint, drive parameter or TO relationship | One of those four kinds | Explicit directed dependency claim |
| V3 `telegram` | AML attribute or instance InternalElement/ExternalInterface | Exact drive telegram or address | Explicit declaration; no inferred unit conversion, wiring or layout |
| V3 `encoder` | Exact drive telegram | Startdrive encoder observation | Original record, phase and telegram ID must match; domain and documented number contradictions remain explicit |
| V3 `encoder` | Saved model object | Module, split-module, channel, address-PLC or DB-member encoder observation | Original target node ID and path must match; channel/member eligibility remains unverified |

V1/v2 keep telegram/encoder purposes unsupported. V3 also allows explicit
`dependent` edges involving the three new kinds and the four existing component
kinds. A dependency claim does not establish compatible profiles or wiring.

V3 mapping rows retain `constraints`. An exact encoder reference with a different
value domain or a known TO_ExternalEncoder number contradiction yields
`constraintMismatch` and adds no primary impact edge. Unknown number/profile
eligibility remains explicit; even 81/83 cannot certify CPU, firmware, provider,
layout or motion support. Disconnected, unknown, contradictory and unresolved
encoder selections cannot create accepted mapping edges. Selecting a supplied
telegram/address row alone does not certify its declared geometry or PLC ownership.

Startdrive encoder selections also require their original drive record in the
same phase context; an indirect dependency cannot bypass that declaration.
V3 consumer context retains explicit encoder target-to-owner links separately
from saved graph edges. Telegram links require their original drive record in
that phase's context; domain/number contradictions and unresolved owner/target
scope cannot create eligible links. New mapped selectors seed exact owner,
hardware and address-PLC context. Existing shared depth, visit and byte bounds
still apply. Component summaries retain original encoder coverage and typed
policies. Actual prior-producer v1/v2 records are tested without regenerating
their stored results.

Other combinations remain unsupported. Missing/ambiguous/mismatched selections
remain unresolved. Duplicate mapping IDs, duplicate edges and self-edges refuse.
Conflicting many-to-one assignments remain visible in coverage gaps; dependency
edges do not establish aliases or identity equality.

Separate `correspondences` name before/after selectors of the same kind and
declare `continuity` or `replacement` with a reason. A selector may appear only
once on each side. Changed drive/TO profiles cannot become continuity. An
explicit replacement retains both profiles without certifying migration.

## Impact and bounds

The existing AML comparison seeds declared impact. The evaluator follows only
accepted explicit mapping edges, including mappings of attributes/links owned
by an impacted AML object. It reports destinations, unmapped impacts, cycles,
exhausted traversal and original-document review requirements. It does not yet
expand consumers inside that primary impact array. The permanent
`declaredMappingGraphOnly` gap and `wholeImpactAnalysisComplete: false` make
that remaining work explicit.

## Version 2 consumer context

V2 adds a separate `consumerContexts` entry for each phase. Its `rootBindings`
trace every seed to an impacted exact selector and AML impact path. Model
selectors seed their saved object; drive parameters seed their selected
hardware context; connection endpoints seed their declared owner/interface;
TO relationships seed their declared source/target within the stated PLC.
These are context roots, not proof that a parameter change affects every
object associated with its hardware.

`associations` retains the original source index, record reference, evidence,
endpoint resolutions, eligibility and whether a link was traversed:

| Kind | Candidate expansion |
| --- | --- |
| `capturedHardwareChild` | Exact saved hardware parent ID to hardware child; excludes ambiguous/virtual identities. |
| `connectionPeerContext` | Declared local/partner owner or interface to its peer, after captured ancestry checks. Explicit cross-PLC connection context is allowed. |
| `connectionConsumer` | An exact declared connection endpoint to its captured supplied consumer. Missing connections or objects remain unresolved. |
| `technologyRelationship` | Declared target to source within the profile's exact PLC. Disconnected, unknown, missing and foreign-PLC endpoints remain visible without traversal. |
| `technologyConsumer` | A supplied parameter's selected TO or a supplied relationship's source to its exact declared consumer; subject existence, node ID/path and PLC must agree. |

`savedLinks` remains separate and contains original saved dependency edges;
supplied associations never become captured graph evidence. The existing
supported-kind/resolution/origin checks apply. Expansion alternates supplied
context with incoming saved dependencies until no new candidate remains or a
bound stops it. `candidates` identifies exact review objects, with no native
effect or member-compatibility claim. Names, member strings and equal values
never create aliases. Before/after phases and project copies never join.

The shared 10,000 edge-visit budget includes primary declared impact and both
consumer phases. Candidate depth starts at the primary traversal's retained
declared witness depth; the 128-hop allowance is not reset. That witness is not
a shortest-path certificate. Lower known context depths take precedence over
deeper pending roots. Each phase admits at most 10,000 associations, 4,096
candidates and 2,048 saved links. Each saved traversal batch also retains the
existing 1 MiB path-byte limit. All copied evidence is charged incrementally
against the same 32 MiB combined report limit.

Coverage retains truncation reasons, omitted association/candidate encounters,
excluded nodes/edges and older TO records without consumer declarations.
No roots is distinct from inspecting saved edges and finding no supported
paths. Missing declarations, unsupported evidence, scoped exclusions and bounds
keep `complete`, `nativeImpactVerified` and member compatibility false.

Requests are limited to 4 MiB, depth 32, 64 component references across both
phases, 4,096 mappings and 4,096 correspondences. Duplicate JSON fields and
unknown fields refuse. Reopened original requests, records, AML data/reports,
technology source files and materialized saved reviews count against a 128 MiB
resolved-data budget. Repeated phase references are counted again.
Report construction is bounded to 32 MiB, including unresolved selections;
traversal stops at 128 hops or 10,000 edge visits across the report.

All results retain `nativeExecutable: false`, `nativeQualified: false`,
`sourceAuthenticityVerified: false`, `liveFreshnessVerified: false` and
`wholeImpactAnalysisComplete: false`. Hashes and record relationships establish
retained consistency, not authentic native evidence or permission to mutate.

## Retained MCP workflow

The [workflow schema](../../contracts/composite-engineering-workflow.v1.schema.json)
defines all five tool inputs, successful responses, errors and downloaded receipts.

1. Import the exact saved reviews and retain AML, connection, drive and TO
   inputs through their existing workflows. Keep those records available.
2. Upload the combined request through `artifact_upload_begin/chunk/commit`.
3. Call `composite_engineering_review(uploadRef)`. Save its `recordRef`, which
   binds a unique record ID and the exact receipt SHA-256. Every call creates
   a new record; use discovery after a lost reply before submitting again.
4. `composite_engineering_reopen(recordRef)` verifies intent, receipt, request,
   report and all original dependencies, then rebuilds the report for comparison.
   Request upload release and Host restart do not invalidate retained records.
5. Use `composite_engineering_page` for `selections`, `mappings`,
   `correspondences`, `impacts`, `coverageGaps`, `beforeComponents`,
   `afterComponents`, `amlChanges`, `beforeAmlImpacts` or `afterAmlImpacts`.
   Offset is nonnegative; count is 1..200. Pages stay within 128 KiB and
   explicitly mark oversized rows as omitted.
6. `composite_engineering_artifact_read` downloads exact `request.json`,
   `report.json` or `receipt.json`, with complete size/digest and base64 chunks
   of at most 65,536 bytes. Verify contiguous offsets and the final digest.

V2 additionally exposes `consumerContexts`, `consumerCoverage`, and the
`beforeConsumerRoots`/`afterConsumerRoots`,
`beforeConsumerAssociations`/`afterConsumerAssociations`,
`beforeConsumerSavedLinks`/`afterConsumerSavedLinks`, and
`beforeConsumerCandidates`/`afterConsumerCandidates` arrays. A v1 handle
refuses these new sections. Reusing its original references in a new v2
request creates a separate review; it never rewrites the old receipt.
The actual prior-producer fixture and MCP compatibility tests preserve v1
summaries, pages and all original artifact bytes through restart.

Every page and download revalidates the complete retained input chain. This
prioritizes consistency; large records may be expensive to reopen repeatedly.
Hashes prove retained consistency, not producer authentication or live identity.
Copying a composite directory alone is insufficient: its dependencies are needed.

`composite_engineering_list(offset=0,count=20)` discovers complete, incomplete
and verification-failed records. It never resumes an interrupted publication.
Records are ordered by record ID; relist after additions. A completed record
whose reply was lost can be recovered through its returned handle. Malformed
or changed dependencies remain explicit failures.

Publication writes and flushes intent, original request and report, rechecks
inputs, then writes and flushes the final receipt. Interrupted partial files
are retained. Storage admits at most 128 records, 1,024 filesystem entries and
512 MiB, counting abandoned/incomplete content. New writes and discovery refuse
an over-capacity store; an existing exact handle can still reopen independently.
No automatic deletion or repair is performed. Actual process interruption is
tested; power-loss durability and real disk-full behavior are not certified.

The [drive v2 workflow](drive-profile-review.md) now retains typed supplied
telegram observations, explicit address units and arithmetic comparisons.
Existing composite v1/v2 readers preserve its source summary and parameter
selectors with their original semantics. The [technology v4 workflow](technology-profile-review.md#typed-supplied-encoder-observations-v4)
now retains typed encoder routes and exact drive telegram dependencies. Existing
composite readers retain its summary/relationship selectors, revalidate those
indirect dependencies and charge their bytes. V3 adds the explicit selectors
and declared-reference checks described above; native profile qualification
and complete cross-domain effects remain unverified.

The remaining full [C02/C03 contract](../plans/expansion-plans/composite-identity-contracts.md)
still requires C03 security/runtime handoff and full native C02 orchestration.
Complete native consumer coverage, exchange, rebind,
preservation, compile/reopen/recovery and physical communication/motion
acceptance remain separate gates.
