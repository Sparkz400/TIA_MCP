# Translation integration and disabled import — 16 October 2026

**Subsequent completion:** [Concrete Siemens binding and the narrow native packet](translation-native-import-20261016.md) are now implemented and verified through a separate fresh-fixture admission. The account below preserves the earlier offline-only delivery and its genuine `wt02` refusal. Production import remains disabled.

The offline translation Host project is included in the solution, `Test.ps1` and `Test-Offline.ps1`. Both runners also cover the new durable import suite. The offline aggregate invokes the actual stdio Host translation workflow with deliberately nonexistent native workers. Existing watch, Test Suite, VCI, Host registration, project references and catalog entries were retained; no native qualification was promoted.

`TranslationImportWorkflow` implements the disabled shared-journal flow described in [the reference](../reference/translation-native-import.md): exact approved candidate binding, complete fresh export comparison before checkpoint and dispatch, one import, retained result/log/readback, peer/language preservation, applicable compile, save, fresh reopen, distinct-destination restoration and explicit reconciliation. Production qualification remains code-owned and closed. The trusted session seam is tested with synthetic adapters; a concrete Siemens import session and actual native import packet are still outstanding.

All new evidence is under `artifacts/translation-integration-20261016-01/`. Earlier failed test output is retained. `import-tests-01` failed during synthetic fixture construction because the test used an XML name expression for a relationship URL. It made no native call. After correcting the relationship strings, `import-tests-02` passed all 78 original checks. Additional final checks distinguish lost compile/save/reopen/cleanup responses from returned failures, reject existing recovery directories and refuse source/clear/empty-target writes.

## Verification

- Initial combined managed run (`artifacts/translation-integration-20261016-01/combined-managed/result.json`): 19 suites plus actual offline MCP passed before adapter additions.
- Combined adapter run (`artifacts/translation-integration-20261016-01/combined-final/result.json`): 21 suites and actual offline MCP passed, including 86 import checks, before the final retained-peer hardening.
- Final combined managed run (`artifacts/translation-integration-20261016-01/combined-retained-peers/result.json`): **21 managed suites and actual offline MCP passed**, including **49 Host workflow checks and 88 import checks**, after requiring original peer-export artifacts.
- Final import interruption receipt (`artifacts/translation-integration-20261016-01/combined-retained-peers/translation-import/verification.json`): synthetic no-op/edit, checkpoint/dispatch/return/readback/save/reopen/cleanup interruptions, actual managed child exit after flushed intent, restart, exact-digest/revision checks, source/target/key/scope/peer/identity drift, competing ownership, candidate/archive/peer-byte tampering, lost restoration response, absence and recovery preservation.
- Translation Python checks (`artifacts/translation-integration-20261016-01/translation-python-final.log`): **89 tests passed**, including retained category evidence and coordinator lifetime/resource admission regressions.
- Optimized populated-category verifier (`artifacts/translation-integration-20261016-01/populated-optimized.log`): **43 tests passed** with `python -O`.
- Full solution build (`artifacts/translation-integration-20261016-01/solution-build.log`) and final retained-peer rebuild (`artifacts/translation-integration-20261016-01/solution-peer-build.log`): **zero warnings and errors**. Current catalog audit (`artifacts/translation-integration-20261016-01/contracts-final.log`): **124 tools, 55 mutation kinds, 197 features**. Concurrent lane additions are included in this later catalog count; earlier counts remain in their original receipts.

## Coordinator refusal before native startup

The exact-controller wrapper's obsolete lifecycle04 hash was replaced with the collector frozen and qualified in `production-host-20261016/controller-02` / `startup-02`: `dff10491a60f497c7f0b96eac0d19f966390b0ea1269b5fb9d4b9f9c47a8c891`. Tests refuse obsolete or changed bytes. Historical controller packages were not modified.

The package/source/API/recovery preflight passed. A held resource-window check found an unreconciled original watch dispatch on the exact source project:

| Field | Retained value |
| --- | --- |
| Store | `D:\TIAW\wt02\engineering-operations` |
| Operation | `8dc3fe356740412d8de54f8e5d0f6f2f` |
| Revision/state | `4` / `dispatchIntent` |
| Idempotency key | `first-watch-normal` |
| Plan | `78a8540080777be03f98cbeb48d7555ea1eb2d00e01a4eaf679b95e6cbd09f0d` |

The resource-window close receipt (`artifacts/translation-integration-20261016-01/category-resource-check-01-close.json`) proves both lock handles closed, no journal modification and no replay. The wrapper now checks this during preflight, before starting a Host, and still reacquires the stores during the native helper window. The final preflight (`artifacts/translation-integration-20261016-01/category-preflight-final.json`) retains the refusal.

No TIA process, native worker, helper, PLC or simulator was started or attached by this work. Preexisting TIA/Host processes were only inventoried. No old reservation was adopted or cleared. The populated category-control gap remains unresolved; consequently the no-op/edit native import packet was not dispatched. The next packet request (`artifacts/translation-integration-20261016-01/native-packet-request.json`) preserves the required ordering and exact blocker without treating preparation as admission.
