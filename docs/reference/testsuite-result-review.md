# Supplied Test Suite result review without Siemens

`testsuite_result_import` and `testsuite_result_review` review supplied evidence in
the portable Host. They do not start a worker, execute tests or establish native
qualification. The input is this bridge's captured-result format, not a decoder
for arbitrary Siemens logs, `.tat`, `.tst` or `.TTST` files.

## Workflow

1. Save or import a project review using the existing saved-review workflow.
   Keep its `reviewId` and the original source observation identities.
2. Prepare a UTF-8 JSON packet conforming to
   [testsuite-supplied-results.v1](../../contracts/testsuite-supplied-results.v1.schema.json).
   It contains `schemaVersion`, an existing validated `plan`, recursive captured
   `results`, and independently supplied `scope` observations. Original native
   states, counters, paths, timestamps and failure classifications must be retained.
3. Upload those exact bytes through `artifact_upload_begin`,
   `artifact_upload_chunk` (`data` is base64), and `artifact_upload_commit`.
4. Call `testsuite_result_import(reviewId, uploadRef)`. The returned references
   identify the immutable receipt, byte-exact original, derived JSON report and
   JUnit XML. They survive upload release and Host restart.
5. Call `testsuite_result_review(relativePath, sha256, section, offset, count)`
   using the **receipt** reference. Sections are `summary`, `results`, `scope`,
   `findings` and `scopeBindings`; use the returned `nextOffset` until null.
6. Download original/report/JUnit through `artifact_read` using each returned
   reference. The existing content-addressed blob store uses `.json` filenames
   for all three; the `junit` reference contains UTF-8 XML despite that extension.

Every review recomputes the report and JUnit from the original packet and exact
saved review, then compares retained bytes. Hash-valid but substituted derived
reports are refused. Review files and original inputs cannot change silently.

## Source association and diagnostic use

The result report follows
[testsuite-result-report.v1](../../contracts/testsuite-result-report.v1.schema.json).
Its result rows retain `captureIndex`, native root state and separate error/warning
counts, so a root counter remains visible even without message children. Missing
captures have null raw provenance. Finding rows preserve diagnostic text, native
path, state and individual counters without adding parent/child counts together.

| Binding | Meaning |
| --- | --- |
| `exactCapturedSource` | Captured project, PLC, scope and source observation match the saved object. Required-scope checks also require exact object/content identity. |
| `differentSourceObservation` | A selector exists, but its source observation differs. A candidate node can aid navigation; the finding remains unresolved. |
| `differentProjectPlcOrScope` | The object belongs to a different captured context. It is neither resolved nor offered as a candidate. |
| `differentObjectOrContent` | Required-scope identity/content differs, including known versus unknown content hashes. |
| `objectAbsentFromReview` | No concrete saved object exists for the supplied location, or the location is absent. |

Association verifies structural consistency of supplied captures, not the truth
or freshness of their source. `tiaVersionOrigin` is `suppliedUnverified`; TIA
version labels are not independently checked. Do not rewrite historical native
snapshot identities to make a later review match. Cross-observation results must
remain unresolved candidates until independent association evidence exists.

An exact finding can now be selected in `diagnostic_plan` with
`testSuiteFinding:{relativePath,sha256,index}`. The path/hash identify the retained
**receipt**, and index is the zero-based report finding index, not a page offset
or result capture index. The planner rechecks original/report/JUnit and saved
review, then retains their references and the full finding as `testSuiteEvidence`.
It selects that finding's exact captured block; an explicitly different block,
non-block object, unresolved association or different review refuses. The complete
finding is retained even if display paging required a download marker.

Supply the diagnostic report/hypotheses normally. The native finding is an
unverified observation, not proof of a runtime cause. Select an instance and
justified signals explicitly before watch/trace planning. Existing watch,
measurement-analysis and comparison tools consume the immutable diagnostic case;
see [the integrated workflow](../guides/trace-diagnostics.md#supplied-test-suite-findings).
`diagnostic_replan` retains historical evidence through `previousPlanId`; it does
not rebase or carry old findings as current evidence. Supply a new finding selector
explicitly if needed; the new review/block must match it independently.

`project_health_report` can aggregate explicitly selected receipts as described
below. Broader alarm/context composition and D06 native providers remain open.
A style warning is not a causal diagnosis or native watch/trace execution.

## Selected evidence in project health

Pass `testSuiteResults:[{relativePath,sha256}]` to `project_health_report`, using
retained result-review **receipt** references for the same `reviewId`. Selection
is explicit: omitting the parameter preserves the metadata-only view, and an
empty array reports zero selected evidence with a false coverage gate.

The separate `testSuite` section follows
[project-health-testsuite.v1](../../contracts/project-health-testsuite.v1.schema.json).
It contains at most 32 distinct packets, their original/report/JUnit/receipt
references, supplied-coverage and source-binding summaries, and per-packet outcome
counts. Distinct attempts remain separate even when definition IDs repeat. A later
passing packet cannot replace an earlier selected failing/inconclusive attempt.
Duplicate receipt hashes or the same original packet under aliased receipts refuse.
Receipts from another saved review also refuse; matching object names are insufficient.

`resultCount` counts planned execution units per packet, including missing/not-run
units. `findingCount` counts finding-message rows per packet, with resolved and
unresolved counts. Clean zero-count Success messages are omitted from findings;
the original tree remains retained. Neither count is a unique-defect or Siemens-assertion total. Native
parent/child error/warning counters are never summed. A native root error without
message children remains visible as an `executionError` outcome even when the
message finding count is zero.

`allSelectedSuppliedEvidencePassed` requires a nonempty selection, validated result
and independent scope coverage, and exact saved-review scope binding for every
selected packet. It says nothing about unselected tests or overall project health;
`wholeProjectHealthComplete`, `nativeQualified`, source authenticity and live
freshness remain false. Passing selected tests do not remove metadata findings.

`offset`/`maxItems` still page metadata health findings only. Test Suite summaries
are complete within a separate 128 KiB section budget; an oversized selection
refuses rather than truncating. Use each summary's receipt with
`testsuite_result_review` for detailed pages or `diagnostic_plan.testSuiteFinding`
for a deliberately selected finding. Original artifacts remain downloadable.
Every health call re-derives and verifies the selected reports/JUnit and originals.

Metadata relationship coverage now uses the model's edge evidence, unresolved
details and exact endpoint identities, rather than assuming resolution lives on
endpoints. Partial/inferred/conflicting evidence remains a coverage gap. Supported
resolved containment of a known virtual presentation group is not a broken native
reference; calls to such a group remain unresolved. A virtual node with an object
path also cannot become a native endpoint or supply an observed impact path.

[Verification](../verification/testsuite-health-20261016.md).

## Coverage, JUnit and limits

`suppliedRequiredCoveragePassed` combines validated results and independent scope;
`savedReviewScopeBound` separately states whether required scope binds to this
review. JUnit has one execution-unit case per planned result plus an explicitly
named `TiaMcp.OfflineEvidence` case. That additional case fails if supplied
coverage or review binding is incomplete. It is an offline evidence check, not
an invented Siemens assertion. A successful raw result with missing independent
scope therefore cannot produce an entirely green JUnit report.

The packet and expanded report each have an independent 64 MiB limit. Existing
semantic limits include 1,000 results, 100,000 messages, depth 32 and 32 MiB of
message text. Combining maximum selector lengths and many messages can exceed
the expanded-report limit even when the input fits; import then refuses before
publishing result artifacts. Pages accept 1..200 rows and fit within 128 KiB of
structured tool data. An oversized row produces an indexed download marker and
advances the cursor; the complete row remains in the downloadable report. MCP
transport envelopes are outside that structured-data budget.

`sourceAuthenticityVerified`, `liveFreshnessVerified`, `nativeExecuted`,
`nativeQualified`, `nativeExecutionAuthorized` and `wholeProjectHealthComplete`
remain false. Synthetic fixtures prove this workflow's tested behavior only.
See the [verification record](../verification/testsuite-result-review-20261016.md)
and [full offline queue](../history/offline-execution-20261016.md).
