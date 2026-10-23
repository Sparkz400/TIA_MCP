# Translation workbook fixtures

The `synthetic-bilingual/` files are authored **synthetic OPC/XLSX fixtures**, not Siemens exports. They exercise an explicit cell map and the offline preservation/review contract. They do not establish native worksheet names, keys, category identifiers, empty-cell semantics or import acceptance.

`synthetic-bilingual/` contains independent package-part inputs, `profile.json` and `expected.json`. The test program packages these parts with BCL ZIP APIs, then tests the production parser and writer against the authored oracle. English/German texts include missing/empty cells, exact literal placeholders, Unicode, CR/LF/tab, shared text referenced by two cells, rich text, formula-looking literal text and an unrelated French sentinel. Unknown XML and custom metadata must survive.

Run the new console test project using an isolated `--artifacts-path`; pass `--demo <new-directory>` to save an original workbook, approved candidate and manifest after all tests pass. The output remains an offline candidate; `nativeImportAllowed` is always false.

[native-tia21-read-20261015](native-tia21-read-20261015/README.md) separately retains exact read maps and provenance for genuine native exports, including the later frozen-helper block/category-selection cases. Their original workbooks stay in the native evidence directory. These maps establish narrow read compatibility and retain native-write/import gates.

[native-category-request-20261015](native-category-request-20261015/README.md) retains exact, hash-bound admission/result receipts for the genuine 14-case category request matrix. The pure Python adversarial request checks use these portable fixtures without depending on local `artifacts/` directories. Workbook contents and native filtering/import qualification remain outside this receipt fixture's scope.
