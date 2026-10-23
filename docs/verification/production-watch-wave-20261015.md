# Production watch workflow — 15 October 2026

The first scoped watch milestone is implemented through the production Host dispatcher and V21 worker: prepare → independently reviewed exact change → readback and peer preservation → saved fresh reopen → archive retrieval → fresh recovered reopen → original/restored reconciliation. This is limited qualification of one owned offline Bool `ModifyValue` edit, not completion of AC-02/AC-03, WT-03–05 or the release ledger.

## Implementation

- `WatchTableDocument` preserves genuine V21 exports, including three existing comment cultures and opaque/native content. No-op retains exact bytes. Existing field/comment edits, deletion, permutation and captured-row cloning are bounded and reject ambiguous or unsafe grammar. Force exports remain capture-only.
- Eight Host tools connect native observation, shared durable admission, exclusive native ownership, checkpoint intent/archive, dispatch intent, one-use import, saved readback, fresh-instance verification and explicit recovery. Binary, installed API profile, hardware, physical project and session identities are checked at their appropriate boundaries.
- Resource ownership persists after process loss. Recovery reserves a new destination and appends immutable retrieval/original/restored proofs. An evidence-only reconciliation path handles lost checkpoint/retrieval receipts and explicitly retained partial destinations without replay or deletion.
- Legacy mutation routes are blocked while the shared store has pending owners. Complete resource-specific legacy migration, cross-host admission and every interruption/cancellation path remain open.

See [workflow boundaries](../reference/watch-table-workflow.md) and [journal semantics](../reference/engineering-operation-journal.md).

## Normal native milestone

`D:\TIAW\wt04` completed on frozen `native/binary-03` through the real production dispatcher. Operation `72fbb25f3f634368a2b0a39530f3669b` binds reviewed digest `ebfc90b497fe653dd49c82653f7890c9f73589845e20f344ea40a2007485cf17`.

The independent [nine-stage verification](../../artifacts/production-watch-20261015/document-review/wt04-workflow-verification-02.json) checks the complete journal chain, approved delta, typed/native XML readback, stable baseline, archive identity, distinct fresh processes, original/restored observations and final `reconciled` state. Strict native comparisons exclude only `DocumentInfo.Created` and the approved literal; they do not discard meaningful whitespace, comments, native IDs or the failed blank-peer case. Seven exported peers remain equal across five later phases. All six owned native PIDs exited; [cleanup receipt](../../artifacts/production-watch-20261015/document-review/wt04-cleanup-review-01.json).

The exact change is `"TiaMcp_EnableGate_DB".Active`, `DisplayFormat=Bool`, `ModifyValue FALSE → TRUE`. German, British English and Dutch comments, the populated `UnchangedPeer`, empty native force set, exported PLC blocks/types/tags, configured hardware, languages and exposed access assignments remain preserved. The manifest is bounded to those exposed providers; it does not prove every hidden/vendor/product payload. This is stored offline configuration, with no online modification, forcing, download or runtime execution.

Seven byte-identical owned exports are packaged with hashes and relative provenance in [native-owned-v21](../../fixtures/watch-tables/native-owned-v21/README.md). Customer-origin captures remain separate. The helper derives seed grammar from a genuine capture but replaces customer selectors and authors owned content; subsequent fixture files are genuine native exports.

## Actual interruption and explicit recovery

`D:\TIAW\wt06`, frozen worker `native/binary-04` (SHA-256 `f14c8e07ba5529c36637d779ff5fc891f4d92be8cf55fe9ff52dd72489613ce7`), deliberately terminates the worker after import and save but before its response. Worker PID 53840 exited with code 73. The Host records `outcomeUnknown` at revision 5 and retains resource ownership; no successful readback is invented. The marker binds the exact saved original and owned native PID 38200, which also exited.

Operation `3f96ca5ade7a44ef810edf903540904b` binds reviewed digest `f96d2179d16384a1d96c05ca03b311de80355af360ec9ad61f174cc1d78501e7`. A fresh recovery runner explicitly attempts the same apply and receives `RecoveryRequired` before native dispatch. It then retrieves the checkpoint, reopens that destination in another fresh instance, and separately opens the saved original. The recovered target is `FALSE`, the original is `TRUE`, all peers match, and the final journal is `reconciled` with exactly one dispatch. The recovery worker PID 23368 exited with code 0.

The independent [eight-stage interruption verification](../../artifacts/production-watch-20261015/document-review/wt06-workflow-verification-02.json) passes. Its first audit remains retained: the reviewer receipt used `inputSha256` while the verifier expected `reviewedInputSha256`; both refer to the same exact approved input hash. The verifier now accepts either spelling and rejects disagreement when both appear. The original approval, journal and native artifacts were not edited. The `binary-05` recovery runner adds only explicit replay-refusal evidence and uses the same frozen `binary-04` worker. Its Core DLL was rebuilt; [provenance](../../artifacts/production-watch-20261015/native/recovery-runner-provenance-01.json) verifies all 53 Core/catalog source hashes unchanged and retains both distinct compiled hashes. Actual interruption qualification currently covers this after-save/before-response boundary; other checkpoint/import/save/retrieval loss points remain open.

## Retained failed attempts

- `wt01` refused fixture ownership before import because managed wrapper reference equality was unsuitable. Exact recorded scratch-path ownership replaced that check.
- `wt02` applied/read back the literal, then failed fresh reopen because TIA discarded a wholly blank peer comment row. The mismatch remains a failure. A populated peer and a fresh-reopened baseline are now prerequisites; the comparator was not weakened.
- `wt03` failed native startup with a security timeout. `wt05` exceeded the bridge startup deadline before creating a project or dispatching a change; its exact worker later exited. Both immutable failures remain retained. No registry/security settings were changed, and neither startup failure was treated as mutation recovery or replay.

## Parallel lanes

VCI now connects all four controlled Git mutations to the shared durable journal with binding/reacquisition and unknown-outcome handling. **108 checks** pass, including real Git and separate processes. Native profile evidence supports bilingual comments only for the observed SimaticML profile; SCL language loss and SD comment/companion loss keep those profiles blocked. See [Git journal](../reference/vci-git-runner.md) and [native profile findings](../reference/vci-native-profiles-20261015.md).

The sequential translation probe executed 14 read-only cases. PLC/DeviceItem category selections produced the same four rows even when the requested alarms category should select none; three block-local exports were empty and do not prove positive category support. Null/empty selections failed explicitly. Project modified state remained false. Native imports remain disabled; [category findings](../reference/translation-category-filtering-20261015.md).

Test Suite obtained genuine V21 `SaveToFile` definitions for style, application and system tests through strict imports. The final isolated `ts02` scratch retained all three definitions and saved state. The application definition uses the actual owned PLC/Bool symbol; the system definition retains the vendor example's external references and was not executed. The definition artifacts and provenance are packaged under [native-owned-v21](../../fixtures/testsuite/native-owned-v21/README.md).

One offline style executor call returned after documented `SetScope(Override, plc.BlockGroup)` succeeded. This establishes license acceptance and execution availability for that observed style operation. Its three errors name the intentionally non-camel-case `Main`, `TiaMcp_IsEnabled` and `TiaMcp_EnableGate` blocks. The native metadata exposes no scope-readback route, so effective scope, full coverage and test-pass qualification remain unverified. Application/system execution and their license acceptance remain untested. The system export emptied the vendor input's `FOLDER_PATH`; this observed round-trip change remains a preservation gap. The earlier individual-FC scope call was rejected before execution; the original application example was rejected for its absent `PLC1` scope and left modified state, which was inspected and explicitly discarded on owned-scratch close. Both failures remain retained. See [availability evidence](../reference/testsuite-native-availability-20261015.md).

## Validation and remaining obligations

The combined offline run passed all **18 managed suites**, Python contracts/tests and **21 explorer model/client checks**; [receipt](../../artifacts/production-watch-20261015/combined-01/result.json). The later complete Python pass ran **771 tests**; [receipt](../../artifacts/production-watch-20261015/final-python-01/result.json). Focused totals include 165 engineering checks, 28 watch groups, 108 VCI checks, 72 translation checks plus 14 category checks, and 149 Test Suite checks. The final independent workflow oracle passes **25 tests**, including the subsequent review-field alias/conflict cases. Host/worker builds pass with zero warnings/errors. These managed/native results are distinct evidence classes.

Source bindings cover **95 MCP tools, 55 mutation kinds and 168 feature entries**. The eight new tools remain partial/unverified at broad catalog level. File binding does not supply missing semantic/manual/schema qualification.

All **22 actual native journal revisions** validate against the operation JSON Schema; [receipt](../../artifacts/production-watch-20261015/native-journal-schema-01.json). The native queue is closed and exact owned workers/instances exited; [final cleanup](../../artifacts/production-watch-20261015/final-cleanup-01.json). Other existing TIA sessions were left untouched. Raw native trial roots remain under `D:\TIAW` to meet TIA path-length limits; immutable audit receipts reference their exact artifacts and frozen binaries.

Existing typed, component, HMI and runtime obligations remain required. Typed capture remains 38 inputs/45 blocks with candidates inactive; remote-I/O native07 passed nine scoped gates but channels/full route remain open; component07 remains failed; HMI compile/archive/production-write gates remain open; runtime remains 48/65 cases, 317/358 steps and 371/478 assertions. No accepted historical result was rerun or promoted by this wave. No installed package was replaced.
