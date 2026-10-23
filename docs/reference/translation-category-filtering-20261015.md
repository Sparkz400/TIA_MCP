# Translation category filtering investigation

**16 October continuation:** A genuinely populated Main control confirmed leakage: hardware/alarm requests returned three block-comment rows. Restricted profiles remain disabled. A separate [whole-project bilingual no-op/edit import packet](../verification/translation-native-import-20261016.md) passed without relying on category/object filtering. The earlier findings below remain retained.

The retained V21 single-category request does **not** demonstrate category intersection semantics. The exact request selected `HwcnCommentTextCategoryData` and the PLC object. Its workbook contains one hardware-comment row and four `<BlockCommentCategoryData>` rows: all five keys and source/target values equal the all-category PLC export. Block selection remains a separate positive object-selection observation. Native translation imports remain disabled.

The [independent investigation receipt](../../artifacts/production-watch-20261015/translations/category-investigation-01.json) verifies the original workbook hashes, independently reads every native worksheet, checks the frozen helper's single-category selection, and pins the installed Base XML and help ZIP. It performs no native call. The installed `TIAPortalOpennessenUS.zip` export topic `37231359755/85568841355.htm` documents only the three-argument overload. The installed public Base XML documents the five-argument overload and stable category identifiers, but gives no category/object interaction or empty/null rules. The current [Siemens export topic](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/import/export-of-project-data/project-texts/export-of-project-texts) likewise describes the older overload; the [V21 release notes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness) announce device-specific category filtering. These sources do not explain the observed result. It remains unresolved whether the cause is object-type behavior, category interpretation, or a product defect.

`TranslationCategoryScope.Compare(allCategoriesForSameObjects, selectedCategoriesForSameObjects, requestedCategoryIds, categoryBindings)` adds a conservative file-only comparison. The explicit identifier-to-export-label bindings retain exact observed spelling; identifiers are never fabricated from labels and angle brackets are never stripped. The narrow captured bilingual layout requires complete worksheet-row coverage and reads actual A-column category and C-column key cells. Removing unwanted rows from the profile or relabelling their categories cannot hide them. Source/target values, view paths and substitutions must equal the baseline records; unexpected, missing, changed and outside-category keys are reported separately. Different project identities, capture receipts, source cultures, unknown labels or unsupported layouts are refused.

`matchesRequestedCategoryIntersection` applies only to these supplied captured workbooks. `restrictiveFilteringDemonstrated` additionally requires both selected and excluded baseline rows, so an all-category equality or empty baseline does not become a restrictive-filter pass. Both observations are file comparisons: the API explicitly reports that it has not verified equal native object selections or current project freshness. A caller must independently bind native requests to the same selected owners and capture receipt. Every report keeps `nativeQualified`, `nativeExecuted` and `nativeImportAllowed` false, including a positive synthetic control. No report authorizes native import.

The [14-check receipt](../../artifacts/production-watch-20261015/translations/category-scope-checks-01.json) covers the genuine leakage, all-category equality, explicit category input validation, exact label matching, profile omission/relabel attacks, foreign/stale capture refusal, missing/changed records and distinctly labelled synthetic positive controls. The ordinary translation suite also passes 72/72 checks. Reproduce with new output paths:

```powershell
dotnet build tests/TiaMcp.Translations.Tests/TiaMcp.Translations.Tests.csproj --artifacts-path artifacts/production-watch-20261015/translations/build
dotnet artifacts/production-watch-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll
dotnet artifacts/production-watch-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --category-scope artifacts/expansion-foundation-20261015/translations/native-read-02/cases.json <new-check-receipt.json>
python scripts/inspect_translation_category_evidence.py <new-investigation-receipt.json>
```

The separately compiled [native helper](../../scripts/native/ExpansionEvidence/TranslationCategoryProbe.cs) was subsequently executed through the coordinator's sequential admission queue. It has no standalone entry point and is not run by the managed tests. The `translations-category-read` dispatch uses the admitted helper's existing exclusive scope, passing its exact `project`, `plc`, resolved PLC `owner` and fresh `Output`; that run retains frozen source and binaries. The fixture was already saved with `de-DE` reference, active `en-GB`, and the exact fixture block `TiaMcp_IsEnabled`. The helper never activates languages, imports, saves, opens a new Portal, or closes a Portal.

| Native probe group | Exact selection and resolving purpose |
| --- | --- |
| PLC controls | Same PLC object, all categories versus hardware-comment, block-comment and alarm-class categories. Determine whether categories alter results at all, including a category with zero expected PLC rows. |
| Category collection boundaries | Same PLC object, empty and null category arguments. Retain actual exceptions/partial output; assign no default meaning. |
| Block controls | Same seeded block, all/hardware-comment/block-comment. Test exclusion of a block by a disjoint category separately from ancestor selection. |
| Device-item controls | Same resolved PLC hardware owner, all/hardware-comment/block-comment. Distinguish hardware owner behavior from `PlcSoftware` selection. |
| Object collection boundaries | All categories with empty and null object arguments. Retain failures; do not interpret either as all objects. |

Each of the 14 requests is recorded before native dispatch with category IDs, argument kind, object selection/type/count and languages. Returned artifact hashes or exceptions and partial artifact hashes are recorded after it. A change in `project.IsModified` stops further probes. The enclosing coordinator must still bind exact ownership, PID, current admission and source/binary identity and reconcile an interrupted helper without replay. A successful API return is only an export observation. The comparison oracle must inspect all sheets and independently compare request groups; a full or disjoint-category union is not asserted in advance.

The [executed 14-case packet](../../artifacts/production-watch-20261015/translations/native-category-01/result.json) identifies owned scratch PID **46936**, CPU **1511-1 PN / V4.1**, and the exact project path. All calls were sequential; before/after `IsModified` remained false, with no save, online connection or import. Project-file hashes remained unavailable due to native file locks. The [independent ZIP/XML receipt](../../artifacts/production-watch-20261015/translations/native-category-read-02/verification.json) checks the frozen binary against admission, request ordering and scope, all ten workbook hashes, complete worksheets and all **28 records**. A [separate managed read/scope receipt](../../artifacts/production-watch-20261015/translations/native-category-read-02/managed-verification.json) verifies all ten profiles and seven comparisons against that independent oracle.

| Executed request | Expected intersection / actual rows | Finding |
| --- | --- | --- |
| PLC, all categories | 4 / 4 | One hardware-comment and three block-comment rows. |
| PLC, hardware comments only | 1 / 4 | Three outside-category rows retained. |
| PLC, block comments only | 3 / 4 | Hardware-comment row retained. |
| PLC, alarm-class category only | 0 / 4 | All four rows outside the requested category. |
| DeviceItem, all/hardware/block categories | 4 / 4, 1 / 4, 3 / 4 | All three outputs have the same keys and values as the PLC baseline. Changing from `PlcSoftware` to its hardware owner does not resolve the leakage. |
| Exact block, all/hardware/block categories | 0 / 0 in each | Empty block selection supplies no positive filtering control and proves no category exclusion. |
| Empty/null category collections | Both refused | Empty requires one or more categories; null is rejected. Neither means all categories. |
| Empty/null object collections | Both refused | Relevant text-bearing objects are required; null is rejected. Neither means all objects. No partial XLSX was emitted by these four refusals. |

The seeded `MilestoneWatch` row comment is exported as `<BlockCommentCategoryData>` with exact source `Freigabestatus {0}` and target `Enable status {0}`. The Dutch comment is not selected by this bilingual export. All `System Texts` worksheets remain header-only on this fixture. An empty system sheet or selected block is an observed empty result, not a product-wide unsupported claim. Source text beginning with `=` remains a shared-string cell, not a formula. The new capture-specific maps retain unknown token grammar and disable shared-string conversion, clear and missing-cell creation; managed edit-refusal checks remain negative.

Reproduce the new file-only checks using a fresh output directory:

```powershell
python scripts/verify_translation_category_probe.py artifacts/production-watch-20261015/translations/native-category-01 <new-output-directory>
dotnet artifacts/production-watch-20261015/translations/native-probe-check-build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --native-category-probe <new-output-directory>/managed-cases.json <new-output-directory>/managed-verification.json
```

TR-U01 now has explicit null/empty refusals and independently verified PLC/DeviceItem leakage. Restrictive category semantics remain unresolved; the next explanatory probe needs a populated block/category contrast or vendor clarification, not an assumption that a successful call applied the filter. Native imports remain blocked because category restriction, workbook write/empty-cell semantics, native no-op roundtrip and the import/readback/reopen/recovery lifecycle are unqualified. HMI/category breadth, token grammars and untouched-language preservation also remain required. No existing release obligation is removed by this read-only packet.

## Recovery-expansion scope hardening

The [new 23-check receipt](../../artifacts/recovery-expansion-20261015/translations/category-scope-checks-01.json) closes gaps in the file comparator. Both captured worksheets must remain declared, visible and in the observed `User Texts` / `System Texts` order with their exact relationship targets. Empty sheets still bind the target language; all sheets and both workbooks must agree. A row moved between user and system text is a changed row even if its key, category and text are unchanged. Removing, renaming, hiding or duplicating a sheet cannot turn an incomplete export into a category pass. This remains intentionally limited to the genuine bilingual layout; it does not broaden the general workbook reader's support claims.

The independent native-packet verifier now also checks the complete category inventory in each all-category baseline, exact category argument kind, object name/type/count, admitted PLC name, request start/end ordering within the enclosing capture interval, read-only flags and import refusal flags. Thirteen Python checks challenge these receipt fields, including an optimization-mode refusal so `python -O` cannot remove the evidence assertions. These are checks of retained request records, not proof of current native object identity or project freshness. The [new independent receipt](../../artifacts/recovery-expansion-20261015/translations/native-category-read-01/verification.json) and [managed cross-check](../../artifacts/recovery-expansion-20261015/translations/native-category-read-01/managed-verification.json) still find ten genuine workbooks, 28 rows and seven scope comparisons, with category leakage unchanged. The ordinary suite passes 72/72. No native process was invoked in this lane.

Reproduce with a fresh evidence output path:

```powershell
python scripts/test_verify_translation_category_probe.py
dotnet build tests/TiaMcp.Translations.Tests/TiaMcp.Translations.Tests.csproj --artifacts-path artifacts/recovery-expansion-20261015/translations/build
dotnet artifacts/recovery-expansion-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll
dotnet artifacts/recovery-expansion-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --category-scope artifacts/expansion-foundation-20261015/translations/native-read-02/cases.json <new-check-receipt.json>
python scripts/verify_translation_category_probe.py artifacts/production-watch-20261015/translations/native-category-01 <new-output-directory>
dotnet artifacts/recovery-expansion-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --native-category-probe <new-output-directory>/managed-cases.json <new-managed-receipt.json>
```

The next sequential native request can avoid another empty selected block without seeding new text: the retained PLC baseline contains two `Main` OB interface-comment rows (`Remanence` and `Initial_Call`) with genuine source text. Resolve that exact OB in the admitted saved fixture, record its path/type/number and a fresh all-category PLC baseline, then export the exact OB with all categories, block comments only and the disjoint hardware-comment category. Run all-category controls before and after the restricted exports and require unchanged complete records. Only a populated selected-OB all-category control makes its category exclusion result meaningful. If the fresh control is empty, stop this branch as inconclusive instead of interpreting it as a pass. A separately admitted read-only contrast using the populated `MilestoneWatch` comment can follow if that exact watch-table object is supported by `objectsToExport`; record a refusal without fallback if unsupported.

The coordinator must acquire the exact project's shared resource admission and native exclusive scope, reconcile pending operations first, pin sources/binary/API identities, and retain dispatch intents before each call. No import, save, language activation or automatic replay belongs in this probe. A successful read-only scope probe still cannot enable imports: workbook write semantics and import/readback/unchanged-peer/fresh-reopen/restoration qualification remain separate obligations.

## Prepared populated Main control

The [separate bounded helper](../../scripts/native/ExpansionEvidence/TranslationPopulatedCategoryProbe.cs) implements the next read-only request. It resolves only `plc.BlockGroup.Blocks.Find("Main")` as OB number 1 and records its selection path, runtime type, parent name/type and number. It requires the already saved `de-DE` reference / active `en-GB` fixture. The coordinator must dispatch `translations-populated-category-read` under a fresh admission after the startup/interruption gate; this preparation does not admit a native run.

The seven-call maximum is `plc-all-before`, `main-all-before`, `main-comments`, `main-hardware`, `main-alarms`, `main-all-after`, `plc-all-after`, in that order. The [file-only workbook gate](../../scripts/native/ExpansionEvidence/TranslationCategoryWorkbook.cs) decodes both complete visible worksheets after each returned export. Before any restricted call, the exact Main selection must contain the genuine `Remanence` and `Initial_Call` source rows, all its rows must belong to the exact project's/PLC's Main path, and every complete record must equal its fresh PLC baseline record. An empty, foreign or changed control returns an explicit inconclusive two-export packet. An unsupported workbook or native exception stops without fallback or replay. The last two controls compare all records, including sheets, categories, view paths, substitutions, source and target; drift is refused.

The [independent verifier](../../scripts/verify_translation_populated_category_probe.py) validates exact request order and intervals, binary/source hashes against admission, all-category inventories, Main OB1 identity, both worksheet layouts, every workbook hash and complete records. It records category leakage or disjoint-category exclusion as observed; it does not require a filtering pass. Empty Main controls remain inconclusive. Every report/profile keeps imports and native qualification disabled. Its checks remain active under Python optimization. Same-object scope is bound to retained request records; this is not a live project-freshness proof.

The admission must include `sourceSha256`, keyed by frozen source basename, as well as the existing `sourceFiles`. Required files are `Program.cs`, `TranslationPopulatedCategoryProbe.cs`, `TranslationCategoryWorkbook.cs`, `ExpansionEvidence.csproj` and `LibraryJournalStore.cs`. Root must freeze the final dispatch source, build, and hash the admitted binary after integration. Invoke the admitted helper and then file-only verification using fresh output paths:

```powershell
& <frozen-ExpansionEvidence.exe> translations-populated-category-read <fresh-admission.json>
python scripts/verify_translation_populated_category_probe.py <native-capture-directory> <fresh-verification-directory>
dotnet <translation-test-build>/TiaMcp.Translations.Tests.dll --native-category-probe <fresh-verification-directory>/managed-cases.json <fresh-managed-verification.json>
```

Preparation checks are retained under [startup-unblock translations](../../artifacts/startup-unblock-20261015/translations/): 24 managed file-only gate checks and 30 Python request/scope checks, with an isolated net48 helper build. The managed gate assembly has no Siemens assembly reference. Synthetic Main subsets and adversarial workbook variants are explicitly test-only; they are not native populated captures. Both independent decoders also read the ten retained genuine workbooks (28 rows), preserving the prior unresolved category leakage. No native call was made while preparing this helper, and no failing import profile was enabled.
