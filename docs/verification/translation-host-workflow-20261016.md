# Offline translation Host implementation — 16 October 2026

Implemented the bounded captured-workbook → gap inspection → unapproved proposals → complete review → explicit exact-digest approval → preserving candidate → reopen workflow. The ten public tools are `translation_capture`, `translation_inspect`, `translation_prepare`, `translation_review_page`, `translation_approve`, `translation_candidate`, `translation_reconcile`, `translation_reopen`, `translation_list` and `translation_artifact_read`.

Implementation and usage are described in [the workflow reference](../reference/translation-host-workflow.md). Native import remains disabled. No TIA process was started or attached, no native worker was launched, no PLCSIM API was initialized, and no existing native reservation, frozen binary, qualification artifact or historical receipt was modified by this lane. Actual MCP verification supplied deliberately nonexistent worker paths and used isolated artifact storage; the native operation directory stayed empty. Existing uncommitted watch/Test Suite/library/lifecycle work was preserved.

## Final verification

All outputs are new under `artifacts/translation-host-20261016-offline-01/`.

| Check | Exact final result | Retained result |
| --- | --- | --- |
| Final Host/test build | Success, 0 warnings, 0 errors | `host-build-final-05.txt` |
| Dedicated Host-to-library suite | **59/59 passed**, including 10 genuine workbook cases | `host-tests-final-03.txt`, `host-tests-final-03/verification.json` |
| Actual production MCP workflow | All 10 tool schemas registered; complete workflow, exact approval refusal/binding, real Host restart, candidate retrieval and fresh unapproved reconciliation passed; Host exit 0 | `mcp-final-03/verification.json` |
| Existing managed translation suite | **72/72 passed** | `managed-tests.txt` |
| Existing Python decoder/request/lifetime tests | **61/61 passed** normally | `retained-decoder/python-normal.txt` |
| Hardened populated verifier with `python -O` | **43/43 passed** | `retained-decoder/python-optimized.txt` |
| Genuine retained category packet, independent ZIP/XML verifier | 14 recorded requests: **10 exports, 28 rows, 4 refusals** verified | `native-category-verified/verification.json` |
| Separate managed cross-check of that packet | **10 workbooks / 28 rows / 7 comparisons** verified | `native-category-managed.json` |
| Retained decoder regression oracle | All **10 genuine workbooks / 28 rows** identical to the retained earlier decoder; all **15 historical adversarial cases** retain their expected disposition | `retained-decoder/verification.json` |
| Category conclusions | **Five mismatches and two empty inconclusive controls remain** | Same retained decoder receipt |
| Contract/catalog coverage | **120 MCP tools, 55 mutation kinds, 193 feature entries**; references valid | `contracts-final.txt` |

The Host suite covers committed-input admission and path refusal, release of source uploads, paging/cursor binding, every-change approval, proposal/candidate tampering, multilingual Unicode and independent XML preservation, missing/empty/whitespace distinctions, stale keys and source/target hashes, profile restrictions, cross-process restart, concurrent publication, concurrent text conflicts, fresh-current reconciliation, byte/count amplification limits, and publication interruptions at intent, workbook, manifest, handoff, pre-receipt and completed-but-response-lost boundaries. Incomplete capture/review/approval/reconciliation records remain discoverable and unusable. Existing candidate bytes are never overwritten.

Earlier failed attempts remain retained. `host-tests-02.txt` was 53/55 because two new tests assumed a whitespace-only fixture cell and a nonexistent `docProps/core.xml`; `host-tests-03.txt` was 54/55 because the whitespace setup used an absent native key. The fixture setup now uses the existing `K-004` and shared-string part, and final verification is 59/59 after four additional resource, paging and retention-boundary tests. `host-tests-01.txt` records an attempted test invocation before its build had finished, so no suite ran in that attempt. A combined optimized discovery attempt correctly hit the older category verifier's intentional `-O` refusal; the supported optimized populated suite separately passed 43/43. These receipts were not rewritten as successes.

## Changed files owned by this lane

| File | Integration purpose |
| --- | --- |
| `src/TiaMcp.Translations/TranslationRetention.cs` | Immutable handle-based storage, hash verification, cross-process publication lock, receipt-last publication, bounded retrieval/discovery and quotas. |
| `src/TiaMcp.Translations/TranslationWorkflowStore.cs` | Capture, inspection, durable reviews, explicit approval, preserving candidates, reconciliation and disabled native handoff. |
| `src/TiaMcp.Translations/TranslationTestAccess.cs` | Internal test-only publication interruption seam visibility. No production fault-injection tool. |
| `src/TiaMcp.Translations/TranslationReview.cs` | Move approval checking after existing profile/text checks so unapproved reviews expose blockers without invented consent. All prior uncommitted preservation changes retained. |
| `src/TiaMcp.Host/TranslationTools.cs` | Dedicated public offline MCP tool class. No `IBridgeClient` dependency. |
| `src/TiaMcp.Host/Program.cs` | One additive `.WithTools<TranslationTools>()` registration. Other lanes' native/watch registration retained. |
| `src/TiaMcp.Host/TiaMcp.Host.csproj` | One additive reference to the existing translation library. |
| `tests/TiaMcp.TranslationHost.Tests/TiaMcp.TranslationHost.Tests.csproj` | Isolated managed Host test executable. |
| `tests/TiaMcp.TranslationHost.Tests/Program.cs` | 59 exercised Host workflow checks, including retained genuine inputs. |
| `scripts/test_translation_host_mcp.py` | Production stdio MCP smoke/regression with absent workers and isolated storage. |
| `contracts/translation-workflow.v1.schema.json` | Durable receipt, artifact/record handles and review cursor contract. |
| `contracts/translation-proposals.v1.schema.json` | Bounded unapproved proposal-batch contract. |
| `contracts/feature-catalog.v1.json` | Append only ten translation tool entries; qualification stays unverified. Existing rows preserved. |
| `docs/reference/translation-host-workflow.md` | Tool sequence, approval semantics, bounds, interruption/reconciliation behavior and future native adapter requirements. |
| `docs/reference/translation-review.md` | Update introductory Host availability statement; preserve existing library/evidence documentation. |
| `docs/verification/translation-host-workflow-20261016.md` | This integration handoff. |

No solution-wide build, native lifecycle runner, watch implementation, Test Suite implementation, global `Test.ps1` or native dispatch contract was changed. The new test project can be built directly; the integration owner may add it to the shared aggregate test runner when merging lanes. Its Host project reference already ensures the production translation assembly is built and copied with the Host. Source snapshots and a lane-only patch against the pre-existing dirty versions are retained in the new output directory for conflict resolution. Preserve the other sessions' changes when integrating shared `Program.cs`, `.csproj` and catalog files.

Reproduce using fresh directories:

```powershell
dotnet build tests/TiaMcp.TranslationHost.Tests/TiaMcp.TranslationHost.Tests.csproj --artifacts-path <new-host-build>
dotnet <new-host-build>/bin/TiaMcp.TranslationHost.Tests/debug/TiaMcp.TranslationHost.Tests.dll <new-test-output> artifacts/production-watch-20261015/translations/native-category-read-02/managed-cases.json
python scripts/test_translation_host_mcp.py --host <new-host-build>/bin/TiaMcp.Host/debug/TiaMcp.Host.exe --output <new-mcp-output>
dotnet build tests/TiaMcp.Translations.Tests/TiaMcp.Translations.Tests.csproj --artifacts-path <new-managed-build>
dotnet <new-managed-build>/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll
python scripts/test_verify_translation_populated_category_probe.py
python -O scripts/test_verify_translation_populated_category_probe.py
python scripts/test_verify_translation_category_probe.py
python scripts/test_translation_exact_lifetime.py
python scripts/verify_translation_category_probe.py artifacts/production-watch-20261015/translations/native-category-01 <new-decoder-output>
dotnet <new-managed-build>/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --native-category-probe <new-decoder-output>/managed-cases.json <new-managed-receipt.json>
python scripts/test_contracts.py
```

The original retained `validate_offline.py` was copied into the new `retained-decoder/` output with only its repository/output/oracle locations adjusted. It reuses all fifteen historical variants and the frozen earlier decoder as a comparison oracle; neither old script nor old receipt was modified.

## Native prerequisites and smallest proposed packet

Native import is still unavailable. Each candidate's `native-handoff.json` records the exact destination, capture/profile/review/approval/candidate hashes and gates, while all native qualification/execution/freshness/import flags remain false. It is not an admitted durable operation plan.

The smallest next import qualification packet is one coordinator-admitted disposable bilingual PLC fixture with a third-language and nontext preservation sentinel, a pinned plain-text layout/profile, native no-op roundtrip, and one nonempty target edit. Use the shared durable adapter for exact destination observation, fresh complete export comparison, resource admission/native exclusive access, verified archive checkpoint, exactly one persisted import dispatch, retained raw result/log, independent complete readback, unchanged-peer preservation, applicable compile/save and fresh reopen. Restore into a new owned destination and verify recovered reopen. Include stale source/target/foreign-destination refusal and pre-dispatch/in-dispatch/post-return interruption cases proving no replay. Clear, missing-cell, source-language, rich text and HMI breadth remain separate qualification obligations.

Resolve scope semantics separately with the already prepared populated Main OB1 all-category/category-contrast packet or vendor clarification. The recorded five mismatches, two empty inconclusive controls and all existing native prerequisites remain. These offline results grant no native qualification and enable no import profile.
