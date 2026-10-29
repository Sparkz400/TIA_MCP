# Supplied component-to-HMI install/update review

The offline Host accepts exact saved PLC component and HMI observations plus
independent proposed output files. It reports contradictions and missing evidence
without starting TIA, executing scripts, generating screens or changing a project.
This is one portable part of C01, not a qualified component installer or SiVArc engine.

For authored plain Unified language resources, use
[text-list authoring](unified-text-list-authoring.md) to download the paired YAML
candidates, then supply both as explicitly selected proposed files below. The
authoring record does not establish destination ownership, consumer completeness
or native import/preservation acceptance.
The v2 authoring request can select this review's exact output and captured Bool
binding to generate one list, guard its previous candidate pair against current
manual edits, and retain selected peer files unchanged.

## Public workflow

1. Import the original saved reviews with `project_snapshot_import`. Keep the
   returned review IDs for the before component, after component and HMI destination.
2. Upload each baseline, independently captured current and proposed output file
   with `artifact_upload_begin`, chunk and commit. Companion scripts/resources are
   opaque bytes; include each file separately. Give each a role-independent ID,
   single display filename, exact byte count, SHA-256 and committed upload reference.
3. Upload a strict `hmi-component-review-request.v1` through `.v7` request, then call
   `hmi_component_review(uploadRef)`. The [schema](../../contracts/hmi-component-review.v1.schema.json)
   defines every mandatory field; unknown fields and duplicate JSON keys refuse.
4. Read `hmi_component_review_page(recordId, section, offset, count)`, with section
   `summary`, `components`, `outputs`, `bindings` or `files`; v2 also offers
   `consumerPaths` and `consumerCoverage`; v3 adds `effectChecks`; v4 adds
   `derivedEffects` and `expectationCoverage`; v5 adds `libraryAssociations` and
   `libraryCoverage`; v6 adds `dependencyAssociations`, `dependencyCoverage` and
   `dependencyIdentities`. Download the retained
   exact input, full report and each original file through `artifact_read`.
5. Committed uploads may be released after review succeeds. Retained review reads
   revalidate the original request, independent files and exact saved project reviews
   after Host restart. Tampering and rehashed invented results refuse.

The managed [fixture producer](../../tests/TiaMcp.HmiComponents.Tests/Program.cs)
and actual [MCP workflow](../../scripts/test_hmi_component_host.py) contain complete
synthetic install/update examples. The aggregate runner emits `hmi-components/mcp-fixture.json`.
Saved observations and dependency relationships in these examples are synthetic.
Library cases retain original supported FB exports, but their association with
synthetic observations is not native qualification.

## Exact selections and interpretation

`beforeComponent` and `afterComponent` select a review ID and 1..64 captured PLC
block/type paths each. `install` requires no before component; `update` requires one.
`componentMatches` explicitly pairs selected before/after object paths with reasons.
Equal names and paths never imply a match. Each matched pair recomputes document,
interface and logic signatures from retained XML. Unmatched/protected/unavailable
objects remain unresolved. Interface changes are visible even for objects with no
supplied HMI member mapping. Signatures are textual evidence, not native semantics.

`destination` selects the exact saved HMI software path and Classic/Unified family.
Existing outputs must identify a captured object, direct parent, name and kind;
ancestry establishes membership rather than string prefixes. `outputs` supports
`hmiTag`, `hmiScreen`, `hmiScreenItem`, `hmiScript` and `hmiResource` review labels.
A faceplate instance can be represented by a captured screen item; that label does
not establish a native faceplate API or eligible library type.

Each output declares `create`, `update`, `remove` or `retain`, its claimed ownership
(`generated`, `manual`, `unknown`), baseline/current/proposed file ID sets, separate
completeness flags and a reason. File sets compare exact names, hashes and lengths.
Changed current bytes flag possible manual edits or export variation. Updating or
removing manual/unknown content is a conflict. Missing baseline/current/proposed
content remains a gap even when a completeness flag says true. Captured and proposed
case-variant sibling collisions are reported. New outputs always need fresh native
identity, live name-availability and creation-support checks.

`bindings` explicitly associate an output property/port with before/after member
selectors (object path and exact component-array member path), a declared HMI type
or null, and a reason. Literal exported FB/FC/DB/UDT interface members are read from
retained XML. Nested inline members are supported; aliases, UDT expansion, runtime
storage, instance substitution and conversions are not inferred. Removed mappings,
changed type text and declared HMI type mismatches are reported. Missing/protected/
ambiguous declarations or unobserved property metadata remain gaps.

Neither a zero-conflict summary nor equal bytes/type text establishes safe native
regeneration. Every report keeps native execution/qualification, source authenticity,
whole-project preservation, complete component-member coverage and complete consumer
coverage false. Per-output ownership, file association and completeness are supplied
claims, not verified facts. Unlisted outputs and unrelated project contents are not
assessed. The summary counts supplied conflicts/gaps and unresolved component rows
separately; these counts are not an overall readiness score.

## Saved consumer context (v2)

The v2 request adds one mandatory object:

```json
"impactLimits": {
  "impactDepth": 3,
  "maxImpactPaths": 100,
  "maxImpactEdgeVisits": 5000
}
```

Limits apply independently to each saved scope: `beforeComponent`, `afterComponent`
and `destination`. Installation has no before scope. Component roots are the exact
selected block/type paths. Destination roots are existing outputs with matching
captured HMI identity and ancestry; new, virtual, foreign or unverified output
identities appear in `excludedOutputIds`. No proposed object identity is invented.

`consumerPaths` contains the scope, exact review ID, selected root path, consumer
path, distance, ordered object paths and every original supporting edge. Incoming
static relationships can reveal consumers omitted from supplied member/output
mappings. The shared saved-review traversal follows supported `nativeConfirmed` or
`parsedSupported` observations with exact captured endpoint IDs/paths. It preserves
distinct diamond paths and terminates cycles. Paths are review candidates, not
observed effects of the proposed installation or regeneration.

`consumerCoverage` retains each scope's roots, exclusions, supported relationship
kinds, limits and truncation reasons. It always reports incomplete coverage. Edges
are never joined between before, after or destination snapshots, even when names
and paths match. Supplied bindings never become captured edges. Unsupported and
unresolved observations remain exclusions; no paths does not establish no impact.
These are incoming consumer observations, not a complete outgoing library dependency
closure or verified instance/member/runtime mapping.

Depth is 1..5, returned paths 1..200 and visited edges 1..20,000 per scope. The shared
traversal also caps retained path evidence at 1 MiB per scope before projection into
consumer rows; its recorded `pathBytes` measures that conservative intermediate
representation. Up to three scopes return at most 600 paths. Depth, path, edge-visit
and byte limits remain visible in the retained report and summary. Oversized original
edge evidence stays in its saved review even when it cannot fit a returned path.

V1 requests and retained records keep their original v1 results. Request/report/
record versions must agree. Reading a v2-only page from a v1 record refuses explicitly;
no historical consumer analysis is fabricated or silently written into that record.

## Bounds and storage

Requests: 4 MiB, depth 32, 256 outputs, 1,024 bindings, 64 component matches,
32 file declarations, 16 MiB per file and 64 MiB total including repeated roles.
Member paths: 1..16 components. Files use unique IDs and each phase has unique
case-insensitive filenames. Names are portable single display names, never paths.

Immutable `hmi-component-reviews/` storage retains exact `.input`, `.source` and
`.json` record bytes. The directory has a 512 MiB retention limit including
incomplete files; source files can remain after a later publication failure and
are never silently deleted or repaired. Reports are limited to 16 MiB. Pages allow
1..200 rows within 128 KiB. An oversized row becomes an explicit download marker;
the full report retains its bytes. Review reads are available when capacity is full.

## Remaining full C01 scope

The [44-workstream queue](../history/offline-execution-20261016.md#all-44-workstreams)
and [release ledger](../plans/release-obligations-20261015.md) remain authoritative.
This increment does not close C01, L02 or X07/X08. Remaining obligations include:

- Verified library GUID/version identity, full PLC/HMI dependency closure, genuine
  versioned export fixtures, and complete component/consumer inventories.
- Authored generation rules, eligible screen/faceplate templates, instance/runtime
  mapping, layout/placement and operation-specific library handoff; SiVArc D15
  exposure must be researched and qualified separately.
- Initial install and changed-interface regeneration with independently established
  generated ownership and preserving every manual/unrelated file, resource, event,
  script, binding, language and dependency, including removed/renamed interfaces.
- Native family/device/version/license eligibility, exact target freshness,
  checkpoint/journal/recovery, compile, saved reopen, first-checkpoint failure and
  response-loss behavior; shared job ownership and cancellation remain required.
- Actual HMI visuals/runtime, PLC instance behavior, bilingual content and full
  project preservation across initial install, update and recovery. Supplied files
  cannot substitute for these acceptance gates.

Portable continuation can improve supplied-document association, richer declared
consumer coverage, review presentation and preservation fixtures without inventing
native APIs. Genuine export compatibility and all native behaviors need external
evidence or an engineering workstation with the appropriate Siemens products.

## Independent supplied output effects (v3)

V3 keeps all v2 fields and requires `observations`, at most 256 rows. Each row
contains exactly `outputId`, `files` (0..32 existing upload file IDs) and Boolean
`complete`. Select each reviewed output at most once. Missing observations are
reported as incomplete; an unknown output ID refuses rather than inventing scope.
Observed files share the existing 32-file/64 MiB total request limit and immutable
retention. Names are unique within each set, including case variants.

`effectChecks` compares expected files according to the reviewed action:

| Action | Expected set | Observation |
| --- | --- | --- |
| Create/update | Independently supplied `proposed` files | Actual supplied post-operation files |
| Retain | Independently captured `current` files | Files after the operation, including manual peer content |
| Remove | Empty set | Explicit complete empty set is needed to match the declared removal |

Comparison uses case-sensitive filenames, verified SHA-256 and byte length.
File order does not matter; byte changes do. Scripts, XML, YAML, images and other
content remain opaque and are never executed or normalized. A timestamp/export
variation therefore remains a byte mismatch requiring review. No Siemens format
or semantic equivalence is inferred.

Each file reports `match`, `different`, `missing`, `unexpected`, `unobserved` or
`unresolvedExpected`. A missing file is a definite mismatch only when the caller
declares the observed set complete. A file absent from an incomplete expected set
is unresolved. Definite differences remain mismatches even if other coverage is
incomplete; the same row retains its gaps. Output status is `match`, `mismatch`
or `incomplete`; the summary exposes the corresponding three effect counts.
These counts are separate from the original proposal conflict/gap counts.

A match means only that the supplied file sets match the supplied intent. Native
execution, object/file association, completeness, provenance and ownership remain
unverified. Selecting manual outputs as `retain` allows their supplied bytes to be
checked, but does not establish that all project peers were captured. Whole-project
preservation, actual native output collection and semantic/format-specific expected
effects remain open. User-supplied expected and observed files can be identical;
that is not independent proof that an operation happened.

Retained v1/v2 records remain byte-compatible and reject the new page section.
V3 rereads original observations and recomputes every effect after upload release
and Host restart. Forged report status/qualification and changed sources refuse.
See [verification](../verification/hmi-output-effects-20261017.md).

## Derived expected effects (v4)

V4 retains the v3 byte comparison and adds `expectations` (1..256 rules).
It addresses a distinct failure: supplied proposed and observed files can agree
while both contain the same unintended change. Each rule derives an expected
document from an original current file, then checks proposed and observed files
independently against that expectation.

The established profile is `classic-screen-backcolor.v1`, reusing the existing
HMI preservation implementation. This profile covers a Classic screen background
property; it is not a general HMI or SiVArc generation engine. Each rule has
exactly these fields:

```json
{
  "outputId": "screen",
  "sourceFileId": "screen-current",
  "profile": "classic-screen-backcolor.v1",
  "ownerId": "0",
  "expectedValue": "204, 204, 204",
  "replacementValue": "64, 128, 192"
}
```

Select an `update` output of kind `hmiScreen` under a Classic destination and a
file ID from that output's current set. The original XML must have one direct
`Hmi.Screen.Screen` under `Document`, with the exact export-local ID and selected
output name. The shared scalar rule checks the original value, unique plain-text
`BackColor` and canonical RGB replacement. Unknown rules, stale values, ambiguous
owners, duplicate output/filename rules and proposed files substituted as current
sources refuse. Export-local IDs and matching names are consistency checks, not
native identity or proof of file association.

`derivedEffects` retains the original rule/source descriptor and separate
`proposal`/`observation` results. It selects the same exact filename from each
phase and reports `match`, `different`, `missing`, `unobserved` or
`invalidDocument`. Missing files are definite only when that phase is declared
complete. Invalid candidate XML is reported without exposing parser diagnostics;
invalid source XML cannot establish an expectation. Integrity or resource-limit
failures refuse the review. Native effect, association and whole-project
preservation flags remain false.

Comparison preserves the XML declaration, whitespace, comments, processing
instructions, namespaces, unknown content, IDs, resources, events, bindings and
unselected screen items. No timestamp normalization is applied. XML parsing can
equate lexical spellings such as quote style; the original bytes remain available
and v3's exact-byte results remain separate. Files must be UTF-8, with an optional
BOM, and a compatible encoding declaration. DTDs refuse. Parsing is bounded to
64 levels and 200,000 reader events per document. Comparison examines at most
64 MiB including repeated source/proposed/observed roles, in addition to the
existing input and retention limits. No script or embedded content executes.

`expectationCoverage` lists derived and unassessed filenames for every output.
An unchanged companion script remains in the byte comparison and is explicitly
outside this profile's derived coverage. No selection establishes complete
output, consumer or project coverage, ownership, library dependency identity,
native compatibility, successful generation or runtime behavior.

V1-v3 records remain unchanged. V4 reopens by re-reading original retained files
and recomputing expectations, so a rehashed invented result cannot grant a pass.
See the [implementation and verification record](../verification/hmi-derived-effects-20261018.md).

## Original library-export association (v5)

`hmi-component-review-request.v5` retains the v4 fields and adds a required
`libraryAssociations` array with 1..128 rows. Each row explicitly selects:

- `scope`: `beforeComponent` or `afterComponent`.
- `objectPath`: an exact path selected in that component's saved review.
- `fileId`: an independently uploaded original file from `files`.
- `profile`: `v21-full-fb-library-version.v1`.
- `reason`: why this particular export and saved object are being compared.

Each scope/path can have one association. The current profile requires a captured
FB and the supported full V21 committed FB library-version export shape. Default
exports without GUIDs, other engineering versions, missing/ambiguous identities,
and unsupported library content formats refuse. Names and XML-local IDs cannot
substitute for the exact saved path and type/version GUID observations.

V5 permits an empty `expectations` array for association-only reviews. When
expected effects are included, all XML roles share the same 64 MiB examined-byte
budget; each document retains the UTF-8, DTD, depth and event restrictions above.

`libraryAssociations` reports `identifierAgreement` as `match`, `different` or
`unavailable`, retains the original file descriptor and captured observation, and
identifies the exact saved review. `libraryCoverage` lists every selected component
path and whether it has an association. No service, unsupported, missing or
inconsistent saved observations do not become a match. The summary separately
counts associations, mismatches, unavailable observations and unassociated paths.

Identifier agreement does not prove authenticity, native identity, matching
component content, complete dependencies/consumers, runtime storage or safe
generation. Both original exports can identify the same version and still have
different bytes. The original files remain available for separate preservation
review. All existing qualification flags stay false.

Both sections are available through `hmi_component_review_page`. Reopening
recomputes comparisons from retained original files and exact saved reviews.
V1-v4 records retain their original results and refuse the new sections. See the
[verification and remaining work](../verification/hmi-library-association-20261018.md).

## Explicit dependency associations (v6)

V6 retains the preceding fields and adds required `dependencyAssociations`
(0..4096 rows). `libraryAssociations` may be empty in v6, so a coverage-only
review can expose unavailable evidence without inventing an export. V5 still
requires at least one library association. Each dependency selection has exactly:

```json
{
  "scope": "afterComponent",
  "objectPath": "<exact selected source path>",
  "dependencyIndex": 0,
  "targetObjectPath": "<exact selected target path>",
  "reason": "Associate this captured direct dependency with this selected target."
}
```

Both paths must be explicitly selected in the same before/after component scope.
The zero-based index must exist in the source's original ordered observation.
The owner requires a linked `plc-library-instance-observation.v1` from
`LibraryTypeInstanceInfo.LibraryTypeVersion` with a nonempty type/version GUID
pair. Dependencies require `plc-library-dependencies.v1`, source
`LibraryTypeVersion.Dependencies`, status `observed`, and a valid
`requiredVersionGuids` array. GUID text must have the exact 36-character form;
case is canonicalized, whitespace is not trimmed. Missing/malformed owners or
lists remain unavailable. Only a valid observed empty array means zero entries.

Each result compares the required version with the target's observed version,
the selected original export's version and the target/export type-version pair.
It reports `match`, `different`, `conflicting` or `unavailable`. A definite
difference takes precedence; the separate `identityConflict` flag still exposes
competing type GUIDs for a version within the same scope. No export means no
complete match. Multiple instances of one version remain distinct selected paths;
there is no automatic name, version-only or cross-review join.

`dependencyCoverage` includes every selected object, observed/unavailable status,
reason, nullable required count, selected/unassociated indices, repeated GUID
positions and self-reference indices. Repeated original entries are preserved;
mapping one position does not cover another. Selecting the same scope/path/index
twice refuses. `allObservedIndicesMatched` concerns only that supplied list and
can be true for a valid empty list. It never establishes native dependency closure.

`dependencyIdentities` retains original selected-object/export observations and
competing type GUIDs grouped by version within each scope. `declaredCycle` marks
edges in cycles composed only of explicit matching mappings in that scope. An
unmapped, different, conflicting or unavailable edge cannot complete such a cycle.
No cross-phase graph or complete native dependency graph is inferred.

At most 64 paths per component scope, 1000 entries per observed list, 20000 total
observed references and 4096 mappings are accepted. The existing 4 MiB request,
16 MiB report, 512 MiB retained-store and bounded paging limits also apply. All
original exports retain the shared XML budgets. These comparison limits are
separate from the capture provider's iterator-read budget.

Reopening recomputes all three sections from retained original files and exact
saved reviews after upload release and Host restart. Historical v1-v5 records
retain their bytes/results and refuse v6 sections. Native identity, complete
dependency/consumer coverage, matching instance content, source authenticity and
safe generation remain unverified. See [verification and remaining work](../verification/hmi-dependency-associations-20261018.md).

## Dependency impacts and expected outputs (v7)

V7 uses the same required request fields as v6. It adds `dependencyImpacts` and
`dependencyImpactCoverage` report/page sections. Each impact row references one
original dependency association, retaining its exact scope, review, component
owner, dependency target and agreement status, including contradictions and
unavailable observations.

The owner and target select their saved incoming consumer paths in that same
review and scope. Their paths and the observed consumers then select explicit
`beforeMember` or `afterMember` bindings. Member selectors must still belong to
the explicitly selected component block/type set; other consumers remain
unmapped. Bindings never create native graph edges or join different reviews.
Multiple paths and bindings remain visible; each output appears once per impact.

Output rows reference the existing output, independent supplied-file effect check,
expectation coverage and derived-effect indices. A matching file check does not
prove an intended native effect; readers must inspect the referenced expectations,
binding gaps and original dependency status. `unmappedObjectPaths` exposes paths
without supplied bindings. All native-impact and complete-coverage flags remain
false, including when every supplied mapping agrees.

Rows form a deterministic prefix capped at 1 MiB of compact serialized JSON.
Coverage records the byte count, included count, truncation and every omitted
association index. Original dependency and traversal coverage remain available;
this byte bound cannot promote a partial traversal to complete coverage. Existing
request/report/store limits continue to apply.

Reopen recomputes these references from retained original requests, files and
reviews. V1-v6 records keep their original bytes/results and refuse both new
sections. See [verification](../verification/hmi-dependency-impacts-20261019.md).
