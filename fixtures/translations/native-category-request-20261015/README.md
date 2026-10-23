# Retained native category request evidence

`result.json` and `admission.json` are exact byte copies from the genuine TIA V21 `native-category-01` capture on 15 October 2026. `provenance.json` records their original repository-relative locations, byte lengths and SHA-256 hashes. No values, identifiers, timestamps, paths or native outcomes were rewritten. The repository's `fixtures/** -text` attribute preserves these bytes across checkout.

These two small receipts make `scripts/test_verify_translation_category_probe.py` independent of ignored local `artifacts/` directories. The test verifies their retained bytes before making adversarial changes to in-memory copies. The genuine matrix contains 14 sequential requests: 10 returned exports and four refusals. The translation section embedded in `result.json` supplies the request list; copying the separate progress receipt would duplicate it.

This fixture tests retained category/object selection and response metadata. It does not contain the original workbooks or helper executable and cannot independently verify exported workbook contents, filtering or live identity. Full native evidence remains at `artifacts/production-watch-20261015/translations/native-category-01`. Native imports remain disabled; this portable fixture establishes no additional native qualification.

Run from any working directory:

```powershell
python <repository>/scripts/test_verify_translation_category_probe.py
```
