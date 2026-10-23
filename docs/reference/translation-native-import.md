# Exact admitted durable translation import

`TranslationImportWorkflow` binds a completed offline candidate to `EngineeringOperationStore`. `TranslationProductionImportAdapter` requires an exact operator-reviewed installation registry before acquiring resources or opening a native lease. Missing admission returns `TranslationImportProfileUnqualified`. The six `translation_native_*` tools expose capture, prepare, apply, status, recover and reconcile; the existing ten translation tools remain offline. No registry is installed by this source change, and no MCP argument grants admission.

The concrete owning-worker Siemens binding is implemented in `OpennessSession.TranslationImport.cs`. `TranslationWorkerAdapter` connects its retained native observations to the durable workflow. Identical binaries expose qualification tools only with the explicit local `--translation-qualification` startup argument; default sessions require the fixed registry and refuse fixture-only actions. The synthetic interruption tests remain distinct from actual native evidence. See [installation admission](translation-production-admission.md) for exact dependency/key binding and the separate operator receipt-review requirement.

## Binding and freshness

Preparation verifies the candidate's complete receipt ancestry, exact caller approval, approved proposals, rebuilt workbook and manifest. The plan names one exact project resource, its checkpoint strategy, four immutable inputs and all required evidence gates. A trusted native observation must agree with the captured project/content/profile and complete original workbook. Caller-supplied offline capture provenance never becomes a live observation merely by reopening it.

The initial shape is a no-op or nonempty plain-text replacement in an existing nonempty target. Source edits, clear, missing/empty target writes and rich-text changes remain refused. The complete observed V21 bilingual layout requires both visible `User Texts` and `System Texts` sheets, their headers and language declarations, every A:F row, exact native keys, category, view path, substitutions, source and target state. An omitted profile row, changed source/target, hidden sheet or different empty-sheet culture cannot disappear from comparison. Logical comparison permits ZIP timestamp and shared-string index changes; the candidate itself still uses the existing preserving overlay and immutable exact-byte hashes. This is a narrow layout comparison, not qualification of arbitrary XLSX parts or native formats.

The adapter must independently supply and retain the complete active-language, object-selection, category and unaffected-peer inventories, including untouched languages. Each peer hash must bind its retained original export bytes, reverified on status, preparation and execution; unsupported hash claims are refused. Original peer artifacts total at most 100 MiB per observation. Qualified native capture must still establish the inventory's completeness. Every observation receipt binds its actual workbook bytes, target, scope and peers.

Peer artifact equality defaults to exact bytes. The narrow `tia21-simaticml-translation-v1` qualification comparer retains the original XML and normalizes only `DocumentInfo/Created` and the exact approved English Main OB1 block-comment leaf. It binds the captured native view path and XML object/composition identity. The same text in another composition, another language, code, interfaces and other engineering content remains compared. Unknown profiles are refused.

## Execution and recovery

1. Validate the exact plan digest/revision and production qualification; acquire the shared project reservation and the operation's controller lock.
2. Open native exclusive access and compare a fresh complete export against the reviewed baseline.
3. Persist checkpoint intent, retain the archive and its operation/project/session/content receipt, then persist the checkpoint reference.
4. Export and compare again under the same exclusive scope; revalidate retained candidate/approval/archive bytes; persist dispatch intent.
5. Invoke import once with the exact candidate. Retain raw result before decoding, retain the complete log, and verify both against operation, project and candidate.
6. Retain complete readback before validation; require unchanged peers and approved text. Retain applicable compile evidence, save, a fresh native reopen and cleanup. Only then record success.

Cancellation before dispatch prevents import. Cancellation after dispatch does not cancel or retry the native call. A lost import, compile, save, reopen or cleanup reply keeps the outcome unknown and the resource reserved. A returned but invalid result remains a failed operation requiring recovery. Status/restart never resume work. Original retained raw results and logs are not replaced by recovery outcomes.

Recovery requires the original retained archive and a distinct absent project directory. The journal reserves that destination before one restoration call. The session must recheck absence immediately before retrieval and must never overwrite a destination. A returned restore receipt alone releases neither resource.

Read-only reconciliation observes the original project through a fresh reopen and independently observes/reopens the restored copy. Original texts must equal either the complete baseline or complete candidate, with unchanged peers and scope. The recovered copy must equal the original baseline. A lost restore response is resolved by inspecting the already reserved destination, never by retrieving again. An exact independently proved absent destination can be reconciled as absent; partial or changed copies remain unresolved. Cleanup must finish before either reservation is released.

The shared [operation schema](../../contracts/engineering-operation.v1.schema.json) describes the journal. Typed observation, checkpoint, result, step and absence receipts are in [TranslationImportAdapter.cs](../../src/TiaMcp.Translations/TranslationImportAdapter.cs). Content-addressed artifacts are create-new, flushed, hash-verified and bounded to 100 MiB each. Native reconciliation is distinct from the offline `translation_reconcile` proposal workflow.

## Qualification boundary

The sequential coordinator freezes source and binary packages, holds declared foreign-store locks through exact owned process termination, and admits only its own disposable fixture. The populated Main category control demonstrated leakage: hardware/alarm category requests returned three block-comment rows. Restricted category/object profiles therefore remain disabled. The narrow import packet uses the legacy **whole-project bilingual export** and compares every exported row; it does not rely on category filtering.

The original source-bound category controller correctly refused old `wt02` watch operation `8dc3fe356740412d8de54f8e5d0f6f2f` at revision 4, `dispatchIntent`. That journal remains untouched. The subsequent separately admitted fresh-fixture controller neither adopts that source nor releases its reservation.

Qualification-only continuation can inspect a previously closed owned fixture after verifying the original three-process retained-handle terminal proof and original worker hash. A pre-checkpoint failure can reconcile unchanged content only with the exact prepared/acquired/outcomeUnknown history, no checkpoint or dispatch, and fresh complete native evidence. An already reconciled no-op with verified checkpoint recovery can be inspected with a newer explicitly recorded worker without rewriting the terminal journal or repeating import/restoration. The ordinary Apply and Reconcile paths remain pinned to their original worker.

See [native qualification and retained continuations](../verification/translation-native-import-20261016.md) for the completed narrow packet, and [earlier offline integration](../verification/translation-import-20261016.md) for the historical source-bound refusal.

The [exact production admission proposal](translation-production-admission.md) binds complete instance content and deployed binaries and lists the remaining same-fixture proof. The [failure-case follow-on](../verification/translation-admission-20261016.md) adds individually admitted interruption/lost-response packets without replaying the accepted trials. Qualification-only Apply can retain one supported response-loss injection or a pre-dispatch interruption; normal production has neither those tools nor an import-enabling flag.
