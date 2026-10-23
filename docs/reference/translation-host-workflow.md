# Durable offline translation workflow through Host tools

The production Host exposes ten offline translation tools backed by `TranslationWorkflowStore`. They reuse the bounded workbook reader, inspection, review, preserving overlay and three-way reconciliation library. These tools have no native bridge dependency. Original workbook profiles remain `synthetic` or `unqualified-explicit`; no native import profile is enabled.

The separate [disabled durable import flow](translation-native-import.md) now binds completed candidates to the shared journal and exercises interruption/recovery offline. It adds no native MCP tool or production admission. The concrete Siemens session and native qualification remain pending.

## Complete caller workflow

1. Upload the captured XLSX, its explicit `WorkbookProfile` JSON and exact `EngineeringObjectReference` JSON using `artifact_upload_begin`, `artifact_upload_chunk` and `artifact_upload_commit`. Pass the three returned `upload:` handles to `translation_capture`. The Host retains independent copies. Releasing the original uploads does not invalidate the retained capture. Project/capture provenance is supplied by the caller and is never presented as a fresh native observation.
2. Select that capture's `recordRef` and call `translation_inspect`. Pages show exact native keys and cultures, source/target states and SHA-256 values, locations and findings. Follow the returned cursor. Missing, present-empty, whitespace-only and source-equal text remain distinct; findings do not automatically authorize changes.
3. Create and commit a [proposal batch](../../contracts/translation-proposals.v1.schema.json). Each edit specifies its exact native key, culture, `keep`/`set`/`clear`, value and expected source/target state hashes. `approved` must be false and `reviewer` null. `options` can supply the existing explicit source-language mode and glossary contract. Call `translation_prepare(captureRef, proposalsArtifactRef)`.
4. Review every change and issue using `translation_review_page`: sections are `edits`, `issues` and `completeness`. The immutable complete `review.json` is available through `translation_artifact_read`. Approval is an explicit caller attestation of reviewing the entire diff; page reads themselves do not grant consent or prove a human reviewed it. Project strings remain untrusted data.
5. Call `translation_approve(reviewRef, exactReviewSha256, reviewer, approveEveryChange: true)` only after that explicit review. The digest is `metadata.reviewSha256` from the unapproved review. Blocking state/profile/preservation issues prevent approval. Original proposals and the displayed review remain unapproved immutable evidence. A separate approval receipt binds the caller's exact digest, reviewer, approved proposals and the library's approved-review digest. The receipt's `recordSha256` is the `approvalSha256` for the next call. These digests provide integrity, not authentication or signatures.
6. Call `translation_candidate(reviewRef, exactReviewSha256, approvalSha256)`. It verifies retained inputs, recomputes the review, derives consent only from the stored explicit approval, and invokes `TranslationReview.BuildCandidate`. It returns immutable handles for `candidate.xlsx`, `candidate-manifest.json` and `native-handoff.json`. The manifest embeds the complete approved library review and its digest; the publication metadata links it to the original caller-approved digest. No native import occurs.
7. Use `translation_reopen(recordRef)` and `translation_list(kind)` after restarting the Host. Reopening a review rechecks its capture, profile, identity, proposals and complete report and shows related approval/candidate states. Use `translation_artifact_read(artifactRef, offset, count)` to retrieve original inputs and completed output artifacts without a host filesystem path.

Minimal proposal batch (replace the example hashes with the exact inspection values):

```json
{
  "edits": [{
    "nativeKey": "exact-captured-key",
    "culture": "de-DE",
    "kind": "set",
    "value": "Kühlung aktiviert",
    "expectedSourceSha256": "<source-state-sha256>",
    "expectedTargetSha256": "<target-state-sha256>",
    "approved": false,
    "reviewer": null,
    "rationale": "Reviewed terminology"
  }],
  "options": {"updateSourceLanguage": false, "glossaryId": null, "glossary": null}
}
```

`translation_capture` reads already captured bytes. It is not a native export operation. Tools accept committed upload references and opaque `translation:<kind>:<id>[:<artifact-name>]` handles; there is no caller-supplied filesystem path resolver. The project path inside the retained identity is provenance and a future exact destination selector, never an input-file read path.

## Reconciliation

Call `translation_reconcile(baselineCaptureRef, editedWorkbookArtifactRef, currentCaptureRef)`. The edited workbook can be a committed upload or the exact completed candidate handle. An uploaded workbook explicitly declares the selected baseline as its provenance; unrelated-part and unedited-cell preservation are independently checked by the existing reconciliation implementation. A Host candidate must belong to that exact baseline capture. The current capture must identify the same exact project and TIA version and use the same profile; a newer snapshot/content observation is permitted only here.

The retained report exposes `SourceTextConflict`, `TargetTextConflict`, already-applied cells and safe proposals. Conflicts are never converted into proposals. `translation_review_page` supports `rows` and `proposals` for reconciliation records. The `proposals.json` handle can be passed directly to `translation_prepare` with the exact `currentCaptureRef`; it is refused against the old baseline. All reconciled proposals are unapproved. Nonconflicting proposals can be reviewed independently while the conflict report remains retained and its conflict count is visible in the new review summary. Resolving conflicts requires explicit new proposals and a new review. No old approval is copied. Subsequent candidates start from current and preserve its unrelated translations.

## Bounds, integrity and interruption

- Original XLSX inputs retain the library's 100 MiB compressed/default OPC/XML bounds. Profile, identity and externally submitted proposal JSON uploads are limited to 16 MiB. A review has at most 10,000 proposals. Complete review and reconciliation reports are preflighted against a conservative 32 MiB worst-case JSON budget before allocation; reconciliation also refuses more than 10,000 changed targets. Mapped keys/source/target text is bounded to 64 Mi UTF-16 code units, counting repeated shared-string references separately. Retained JSON artifacts are limited to 64 MiB. No input is silently truncated.
- Inspection and review pages accept 1–1,000 entries and a 192 KiB serialized row budget. One oversized entry is returned as an explicit omitted-entry marker, with its complete artifact handle and index. For inspection, the original workbook and explicit profile contain the exact cell; for review, the complete report contains the entire change. Read exact content before approval. A page can contain fewer rows than requested because of its byte budget. Inspection cursors bind workbook/profile/project hashes; review cursors bind record hash and section.
- Artifact transfer returns at most 65,536 bytes per call. Listing accepts 1–100 records with a 192 KiB row budget. List offsets are for discovery ordered by immutable ID; relist after concurrent additions. Review/inspection cursors remain stable for their immutable input.
- Retention is bounded to 10,000 records and 8 GiB, including incomplete publications. Dependency ancestry is capped at 64 levels and checked before publication, so a completed record remains reopenable. At that ancestry bound, start a new review from the current capture with an explicitly supplied proposal batch. Administrative archiving is required at the storage ceiling; there is no automatic deletion or retention-expiry policy in this slice. Complete reports and workbook models are still read into memory; these byte limits do not claim a measured production resident-memory envelope.
- A file lock serializes translation publications across Host processes. Contending mutations return `TranslationStoreBusy`. It is local offline storage coordination, not a native project reservation. Readers verify immutable hashes and do not acquire native resources.
- Every publication uses new files, flushed staged writes and a completion receipt published last. The [receipt contract](../../contracts/translation-workflow.v1.schema.json) binds artifacts and dependencies. Record SHA-256 hashes the canonical receipt with `recordSha256` set to the empty string. Approval and candidate IDs match their review ID, making retries discoverable without another candidate dispatch.
- Publication failures return the exact record handle and `incomplete` or completed-but-response-lost state. `translation_list` distinguishes complete, incomplete and integrity-failed records. An intent alone, a pending file or even all output files without a receipt is never success. There is one publication attempt per review/approval/candidate ID. An incomplete candidate stays retained and is never overwritten; prepare and explicitly approve a new review for another attempt. A completed retry verifies and returns the existing record. Reopening a review exposes its incomplete related candidate/approval.
- Missing or modified retained artifacts invalidate dependent records. Filesystem links/junctions are refused. Host-owned retention is an integrity boundary against accidental replacement, not protection against an administrator able to rewrite all files and hashes.

The original library continues enforcing placeholders, explicit length limits, source-mode restrictions, clear/missing-cell permissions, unknown grammars, rich text and opaque metadata. Unapproved reviews now run these checks before reporting missing approval, so profile errors are visible without fabricating consent. Unrelated parts, unselected cells and other cultures retain the existing preservation checks.

## Disabled native handoff and smallest qualification packet

Every candidate includes an exact destination identity and a disabled handoff. It deliberately does not create an `EngineeringOperationPlan`, native observation, reservation or admission. A future trusted adapter must integrate with the existing `EngineeringOperationStore` and `NativeTargetObservation` contracts:

1. Under fresh coordinator admission and native exclusive access, resolve the exact original destination, active/reference cultures, target build and full preservation scope. Acquire a fresh native export and compare native keys, source/target/peer cultures, profile, owners and text/settings fingerprints. Any drift requires reconciliation, a new review and explicit approval.
2. Bind all immutable input hashes and full resource effects, retain the checkpoint intent and verified archive, then recheck freshness. Persist a dispatch intent before exactly one import. No timeout, reconnect or missing receipt may trigger automatic replay.
3. Retain the raw native result, complete log and independent re-export/readback. Verify edited cells and all unrelated cultures/resources, applicable compile/save, and fresh reopen. Treat warnings, partial effects and unknown log grammar explicitly.
4. Retain original failures. Reconcile interruptions and ownership through the shared durable adapter. Restore only to a new owned destination, verify preservation and recovered reopen, and require new capture provenance before subsequent translation work.

The smallest proposed native packet is one admitted disposable bilingual PLC fixture with an unrelated third-language/nontext sentinel, one original exact destination, a no-op roundtrip and one nonempty target edit under a narrowly pinned plain-text profile. It must retain an independent expected-key/value oracle, full before/after native exports, unchanged-peer/native-object comparisons, checkpoint, one dispatch receipt, complete log, fresh reopen, restore into a new owned destination and recovered reopen. Add stale-source/target/foreign-destination refusals and controlled pre-dispatch, in-dispatch and post-return interruptions with no replay. Clear, missing-cell, source-language and rich-text mutations require separate positive qualification before enabling those native operations. HMI breadth remains separate.

The retained five category-filter mismatches and two empty inconclusive controls remain unresolved. A populated exact Main OB1 all-category control versus requested category exports, or vendor clarification, is the smallest separate explanatory scope packet; an empty control remains inconclusive. Do not infer category intersections from native call success. These offline Host tests neither rerun that packet nor enable native import. See [category filtering evidence](translation-category-filtering-20261015.md), [profiles](project-text-profiles.md), and the full [translation plan](../plans/expansion-plans/project-translations.md).

## Offline verification

Worksheet capture requires one direct `sheetData` container. Reading and editing
use those same rows; accepted numeric row spellings such as `0003` keep their
original XML attributes. Row-like elements in opaque extensions are preserved
and never become edit destinations. Mapped text and shared-string indices with
nested XML elements are refused instead of flattened into apparent values.

Comments or processing instructions inside a selected inline/shared string or
shared-string index remain readable and survive a no-op, but block an edit that
would discard them. An explicit approval cannot bypass that preservation issue.
Cell-level annotations that the overlay retains can still accompany a text edit.
Reconciliation applies the same checks to baseline, edited and current content.
Its independent comparison also retains non-text nodes in selected cells and
refuses extra attributes/annotations on newly created cells; selecting a text
change does not authorize arbitrary cell metadata changes.
UTF-8 BOM and UTF-16 little/big-endian worksheet inputs are tested for exact no-op
bytes and correct edited values; changed worksheet XML may be reserialized as
UTF-8, while unrelated package parts remain byte-identical. These are synthetic
format checks, not new Siemens/vendor compatibility qualification. See the
[preservation audit](../verification/translation-preservation-20261016.md).

Build `tests/TiaMcp.TranslationHost.Tests/TiaMcp.TranslationHost.Tests.csproj` with a new `--artifacts-path`. Run its DLL with a new output directory and optionally a retained `managed-cases.json` path. The harness calls real Host tool methods, starts one fresh managed verification process for restart retrieval, and never starts a native worker. Run the existing translation suite and Python adversarial verifiers as well. `scripts/test_translation_host_mcp.py` exercises actual MCP registration and a full capture/review/approve/candidate/reopen workflow using deliberately nonexistent native and simulator worker paths and a new isolated artifact root.

For explicit supplied PLC alarm text mappings, the optional
[inheritance policy](translation-review.md#supplied-plc-alarm-instance-text-inheritance)
adds before/after language consistency to `translation_review_page` with
`section: "alarmInheritance"`. All existing approval, preservation and restart
rules apply. The policy never enables a native alarm import.
