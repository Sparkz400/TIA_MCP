# Completion recheck — 16 October 2026

This supersedes the live-session assumptions and counts in the [first audit](completion-audit-20261016.md). The user confirmed the other sessions have finished. The new snapshot captured **1,690 files at 10:15:54 Berlin / 08:15:54 UTC**, with no source changes during copying. Code also remained unchanged during this recheck. Both staged and unstaged work were included; HEAD remains `557c5e4`, so a checkout of HEAD alone would omit the delivered work.

The repository builds, and GRAPH's package is complete for its declared profile. **The combined validation is still red and the full release is still open.** Use the updated [completion assignments](completion-assignments-20261016.md) and [44-workstream recheck ledger](completion-recheck-20261016.json). No TIA session, simulator or physical device was started by this audit.

## What finished, and what remains

| Area | Rechecked result | Remaining completion work |
| --- | --- | --- |
| GRAPH document package | **51/51** checks pass: 49 ordinary checks plus two installed Siemens schema checks. The missing fixtures and cross-sequence-link failure from the earlier in-flight copy are closed. Four exact native profiles have retained import/compile/unchanged-export re-import/fresh-process reopen/archive-recovery evidence. | Integrate its portable suite into the declared runner and document delivery. Host/explorer consumption, editing and runtime are separate increments. CFC, DCC and interpreter support remain open. Do not assign someone to recreate or requalify the completed package. |
| Translation | The completed whole-project de-DE/en-GB no-op and Main OB1 English-comment packet remains valid for its recorded package-03/package-06 profiles. Readback, preservation, compile, fresh reopen, restoration, stale/replay refusal, six owned exits and zero pending resources are retained. Actual offline MCP workflow passes again. | Production import remains disabled. Finish exact profile admission and the remaining interruption/drift/negative cases. Category-restricted imports remain blocked by demonstrated leakage; broader target shapes need separate evidence. |
| Test Suite | New assisted qualification tools, current UI-evidence transport, casing-result interpretation and recovery boundary code are present. **442** managed, **96** worker and **22** actual Host checks pass. | The latest native clean attempt, `D:/TIAW/ts1602`, failed during preparation: **“VCI native identity ancestry exceeds its bound.”** All three owned processes exited normally. Diagnose the bounded identity traversal before the next trial; then qualify fresh complete scope/license observation, execution, stop, preservation/reopen/recovery and failure cases. No production profile is enabled. |
| Watch authoring | The earlier exact one-row Bool creation and separately frozen production Bool/recovery evidence still stand. No new watch implementation delta was found. | Exact admission, collision/stale negatives and later existing-table/add/remove/reorder/comment/type profiles remain open. Populated force capture remains separate. |
| Other roadmap families | No new closure evidence was found for their previous outstanding obligations. | Continue the existing 44-workstream ledger; the full feasible 1.0 scope has not been reduced. |

GRAPH's four positive profiles are populated conditions, cross-sequence jumps, simultaneous branches/actions, and alternative branches/actions. The empty-condition native export/re-import failure and duplicate-number rejection remain explicit negative evidence. The package does not certify arbitrary GRAPH input, embedded-language behavior or runtime timing. See [native fixture evidence](../../tests/TiaMcp.Graph.Tests/fixtures/native-v21/README.md).

The Test Suite packet is newer than the documentation's revision-05 handoff. Revision-07 built an assisted qualification candidate, and `ts1602` then failed before reaching execution. The failure originates in `QualificationVciCapture` / `QualificationNativeIdentity`, reused by `TsCapture`; the report does not establish why native parent traversal exceeded its bound. Preserve the bound and complete identity requirement while diagnosing the actual native chain. The retained physical-Escape UI stop also remains in force; this audit did not resume UI work. The assisted observation code is implementation, not proof of an automatic scope reader or an accepted production provider.

## Validation from the new snapshot

| Check | Result |
| --- | --- |
| Release solution build | Pass, zero errors; one `CS0649` warning for the default-disabled Test Suite qualification field |
| GRAPH, including optional installed XSD checks | **51/51 pass** |
| Python discovery | **1,164 run, seven errors, one skipped** |
| Aggregate `Test-Offline.ps1` | Fails at catalog validation before subsequent aggregate stages |
| Actual default Host `tools/list` | **121 tools**, normal exit, zero workers |
| Test Suite managed / worker / actual Host | **442 / 96 / 22 pass** |
| Watch and component worker boundaries | **31 / 12 pass**, no native session |
| Translation Host managed | **49/49 pass** |
| Translation import managed | **95/95 pass**, synthetic callbacks only |
| Translation actual offline MCP workflow | Pass, including committed uploads, approval/candidate, actual Host restart and reconciliation; import remains disabled |

The earlier full 21-suite managed run, browser, OPC UA, real-Git and other boundary results remain historical results for their recorded snapshot. This recheck rebuilt the solution and reran affected suites; it does not relabel the earlier entire matrix as executed on today's later snapshot. All current commands, outputs and hashes are in the new receipt (`artifacts/deep-dive-20261016-02/test-results.json`).

## Concrete blockers for the integration owner

1. **15 qualification declarations lack catalog contracts**, up from ten: four assisted Test Suite tools and translation's closed-fixture resume route were added. There are now **139 source declarations**, while the normal Host still exposes **121**. Registration is conditional; reconcile the catalog and validator without accidentally enabling qualification routes.
2. **23 feature-to-obligation mappings are missing**, unchanged. **21 recorded source inputs are stale**, up from 17. Refresh bindings only after the intended source handoffs are integrated. Exact names and paths are in the structural audit (`artifacts/deep-dive-20261016-02/structural-audit.json`).
3. **The same seven Python errors remain:** five component source-reuse fixtures bypass initialization and lack `store_root`/`lifetime`; force-capture tests import an ignored historical helper; the Test Suite schema module cannot import undeclared `jsonschema`. Its schema checks also require explicit invocation because importing its `main()` is not execution. Declare `referencing` and the lifecycle scripts' `psutil` dependency as appropriate.
4. **Seven suites remain outside both aggregate runners and the solution:** ComponentWindow, Graph, HmiRepairAdmission, OpcUa, OpcUa.Dependencies, TranslationCategoryProbe and WatchReindex. Classify them by portable versus workstation/fixture-bound requirements; OPC UA already has a standalone runner. WatchReindex's hardcoded native fixture paths need a deliberate runner contract.
5. **Documentation and source checkpoint need consolidation.** Current STATUS/TODO paragraphs still mix older counts, resolved startup blockers and earlier handoffs with later evidence. Large staged and unstaged changes remain uncommitted. Record the final intended source and test matrix; staging alone is not a release checkpoint.

## Work to run in parallel now

| Slot | Assignment | Concrete finish line |
| --- | --- | --- |
| 1 | **Integration closure** | Clean-copy aggregate is green; catalog/mappings/bindings match; portable suites and prerequisites are wired; docs and the source checkpoint agree. Own shared registration, project/solution files and central CI. |
| 2 | **OPC UA offline MCP delivery** | Existing passing parser/dependency CLI becomes a documented Host artifact workflow with actual stdio tests and CI. Own UA code and a separate tool file; hand central registration changes to slot 1. This takes the finished GRAPH slot. |
| 3 | **Translation production closure** | Reuse the passing native slice; complete exact admission and remaining native failure/negative gates. Deliver a bounded supported workflow and explicit unsupported profiles. Own translation files and tests. |
| 4 | **Watch exact-profile closure** | Complete the one-row creation profile's admission and negative cases, then its next bounded native packet. Own watch files and tests. |

These are proposed assignments, not newly launched sessions. Offline work can overlap. Translation and watch native trials share **one serialized TIA queue**; PLCSIM remains exclusive. Separate tool files do not make simultaneous edits to shared Host/worker registration safe: slot 1 integrates those changes.

Backfill a finished slot with VCI's preserving export-to-revision workflow or Test Suite's concrete identity/provider blocker. GRAPH Host/explorer delivery is another bounded follow-on using the finished document library. Then close selected typed/component/HMI/inventory prerequisites. Keep the remaining 17 runtime diagnostics and final complete 65-case corpus in the existing exclusive sequence. Avoid turning all remaining planning prompts into simultaneous implementation sessions.

“Finished” still requires the promised user workflow, exact source/profile evidence, applicable negative/preservation/reopen/recovery tests, reproducible validation, matching contracts/docs and no unresolved owned operation. A completed session or successful build alone does not close its whole roadmap family.
