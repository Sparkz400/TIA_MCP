# Supplied TO profile and relationship review - 17 October 2026

Two offline tools now retain and compare supplied technology-object observations:
`technology_profile_review` and `technology_profile_report`. The
[workflow](../reference/technology-profile-review.md) documents exact saved selection,
strict input/output schemas, parameter and relationship matching, proposal contradictions,
unknown endpoint/member states, supplied cycles, bounded graph context and retained artifacts.
This is the first executable TO-02 workflow, not completion of every TO-02 or D13 obligation.

## Implemented and tested scope

The Core comparer binds an exact nonvirtual saved technology object and rejects
contradictions with its captured owner/type/version. Supplied CPU/profile declarations
remain unverified. Before/after profiles must agree; online observations cannot become
proposals. Exact parameter names and relationship IDs control matching. Scalar text,
opaque values and metadata remain intact without native conversions or implicit tolerances.
Before read-only/unknown access cannot be promoted by proposed after metadata.

Declared relationships expose captured versus unresolved endpoints, unverified member
selection, explicit disconnected/unknown states and supplied graph cycles. Those cycles
do not establish native invalidity. Saved context retains the exact selection, bounded
ancestors and direct edges with explicit incomplete traversal. Both node ID and object
path must match an endpoint; no name-only or ordinal matching occurs.

The store uses the existing committed-upload, saved-review, immutable publication and
artifact-read patterns. It retains original input bytes, revalidates them and the saved
source on every reopen, recomputes the report, and refuses corruption or rehashed forged
reports. Pages stay within 128 KiB and explicitly omit oversized rows; complete artifacts
remain available. Released uploads and Host restart do not invalidate owned evidence.
No native worker is installed or launched.

## Development checks and corrections

Final focused [managed checks](../../artifacts/technology-tests-03/verification.json)
pass 81 assertions, covering exact Unicode selection and distinct composed/decomposed
names, profile/owner mismatches, supplied graph cycles, missing/ambiguous evidence,
read-only and unknown access, exact large scalar/opaque numeric data, collection/proposal/
byte bounds, malformed Unicode/depth, graph ancestry, invalid endpoints, retained-byte
integrity, restart, rehashed forgery, oversized pages and retention capacity.

The [actual MCP run](../../artifacts/technology-mcp-04/verification.json) passes 44
checks across two clean Host lifetimes. It validates strict request/record/receipt/page
schemas; imports a saved review; uploads exact UTF-8 originals; checks immutable reports,
all paged sections and unknown/cycle/access results; releases input uploads; restarts;
then verifies original bytes, corruptions and restored originals. Every refusal preserves
all existing workflow files. Both native worker paths deliberately name absent files.

The first managed fixture used string graph endpoints, which the actual saved-review
import correctly rejected in artifacts/technology-mcp-01.log. The fixture was corrected
to the existing structured endpoint/evidence contract, and the comparer now selects
edges by both node ID and object path. A dedicated mismatch case prevents binding by ID
alone. Initial contract checking also found missing new feature-obligation mappings;
they now explicitly retain D13/F02/L05 obligations. Failed development runs remain
available and are not acceptance evidence.

## Final frozen source

Source: artifacts/technology-01/source, tree
414070463001f6db5df3df3ff11b614006fd08d4; 1,946 tracked files.
[Source manifest](../../artifacts/technology-01/source-manifest.json) SHA-256:
8008420bbe3184b2b799832341fcc6d6ad46f7c536ecc175200613416d1028e7.
Archive SHA-256:
574c96449297db20c9e02fccb9a5349cae2f841b6627aadc5fe4b2fda7a34136.

Base f0572232cec738b3c222c83901196c40179afdea passes all hosted jobs: Explorer
38998317034 and Python/.NET/OPC UA/package 38998316966. This confirms the prior
Git timeout fixture repair on the hosted runner. New-source acceptance is separate.

The [frozen aggregate](../../artifacts/technology-01/source/artifacts/checks/result.json)
passes 39 managed suites, 1,295 Python tests (one existing unavailable-evidence skip),
24 Explorer checks, contracts/source bindings and all actual MCP workflows. This
includes 81 TO checks and 44 TO MCP checks. Run: 2026-10-17T03:29:37.9986714Z to
2026-10-17T03:39:38.7621776Z; receipt SHA-256:
96b17f9855f56fea9422601d92bd70a2a405028ad2d64cfdc1197320c7bff9b8.

All 22 [package stages](../../artifacts/technology-01/source/artifacts/package-checks/result.json)
pass, including the new TO workflow after Unicode relocation: 81 managed checks and
44 actual MCP checks across two clean Host lifetimes. Receipt SHA-256:
3c2b85025a457130d18cc928f3d6cb16715a2d7b22af1458677273015edc683b.
Bundle tia-mcp-offline_20261017_032959_72ab075f has 1,382 manifest files;
manifest SHA-256: 52d5fa3498e546c8bf1573ada944fb1e2364c96abd4bb9c9491ac9ffa164c9d8.
ZIP SHA-256: d4d1832cf0f999ec008f5ce7439396503d90d1e81172b9312d6bc4053ce93415.
These hashes establish retained integrity, not publisher authentication or native acceptance.

The [final source comparison](../../artifacts/technology-01/final-source-comparison.json)
permits only completion-documentation updates to status, TODO, queue, D13 remaining
acceptance and this record. Production source, tests, contracts and scripts remain
identical to the frozen source. The [integration receipt](../../artifacts/technology-01/integration.json)
records the pushed commit, exact hosted CI state and cleanup. New-commit CI acceptance
is separate from the local checks above.

## Remaining acceptance

TO-02 still requires independently retained source-artifact bundles and broader
supplied consumer/impact handoff. The current input can describe provenance in opaque
metadata, but does not independently retrieve or authenticate those external artifacts.
The direct saved graph context is not a complete transitive-impact proof. These are
remaining obligations, not a reduced definition of the full TO-02 package.

TO-03–12 still cover genuine SDK/service capture, exact CPU/version eligibility,
lifecycle/scalar and connection/coupling operations, specialized cam/MCL/mapping formats,
full XML/generated-peer preservation, save/reopen/recovery and native/bench qualification.
No source-authenticity, live-freshness, whole-object, motion or release claim is made.
All 106 unavailable historical evidence bindings/hashes remain preserved. The static
catalog has 199 tools, 55 mutation kinds and 272 features; new entries remain partially
implemented and unqualified. The full 44-workstream goal remains active.
