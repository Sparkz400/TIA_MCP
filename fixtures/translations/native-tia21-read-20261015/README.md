# Captured V21 project-text read profiles

These profiles map the original [legacy export](../../../artifacts/expansion-foundation-20261015/native/translations-02/legacy.xlsx) and [PLC-filtered export](../../../artifacts/expansion-foundation-20261015/native/translations-02/filtered-plc.xlsx) from the admitted scratch project. [Provenance](provenance.json) pins workbook and native result hashes. Original workbook bytes are retained, not reconstructed from these maps.

`legacy.profile.json` maps nine user-text rows; `filtered-plc.profile.json` maps five. Both use exact C-column opaque keys, E-column German source and F-column British English target, with exact exported category labels. The source header's asterisk is observed metadata; the canonical source culture is `de-DE`. Key and source/target cell locations differ between captures, so use only the profile paired with the exact workbook hash. Equal visible text never substitutes for a native key.

Each profile remains `unqualified-explicit` with unknown token grammar and disabled conversion/clear/create flags. This enables inspection and prevents changed offline candidates. No native import or write profile has been qualified. `System Texts` has only headers in both exports and therefore contributes no mapped rows; this is an empty observation on this fixture, not a product-wide unsupported claim.

The [independent native verification receipt](../../../artifacts/expansion-foundation-20261015/native/translation-verification-01.json) records full package metadata, scope comparison and exact matches against separately seeded block comments. The [managed read receipt](../../../artifacts/expansion-foundation-20261015/translations/native-read-01/managed-read-check.json) independently checks the production reader against the ZIP/XML decoder and confirms that changed candidates are blocked. Its harness can be repeated with a new output path:

```powershell
dotnet artifacts/expansion-foundation-20261015/translations/build/bin/TiaMcp.Translations.Tests/debug/TiaMcp.Translations.Tests.dll --native-read artifacts/expansion-foundation-20261015/translations/native-read-01/cases.json <new-read-receipt.json>
```

This command reads retained files only. It does not attach to TIA, call native export/import, modify a workbook or establish current native project freshness.

The later [capture-03 provenance](capture-03/provenance.json) pins four additional workbooks produced by the retained frozen native helper. Its `legacy`, `filtered-plc`, `filtered-block` and `filtered-category` profiles map 9, 5, 1 and 5 rows respectively. [Verification 02](../../../artifacts/expansion-foundation-20261015/native/translation-verification-02.json) and the [second managed read receipt](../../../artifacts/expansion-foundation-20261015/translations/native-read-02/managed-read-check.json) verify all twenty records without changing the earlier packet.

Single-block selection returns only `TiaMcp_IsEnabled`'s exact seeded comment. Selecting only `HwcnCommentTextCategoryData` with the PLC still returns all five PLC rows, including four block-comment rows. This observed result does not qualify restrictive category filtering. The category/object combination semantics remain open; all four profiles retain the same read-only permissions. To repeat the managed check, use `native-read-02/cases.json` and a new output path in the command above.
