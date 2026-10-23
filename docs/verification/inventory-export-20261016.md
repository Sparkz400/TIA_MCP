# Inventory export fidelity — 16 October 2026

This D16/L05/F02 increment repairs two reproduced export defects and exercises
both file readers and the installed Excel application. It does not perform TIA
capture, CPU identification or any Siemens operation.

## Scope and standards

Raw XML text was insufficient for Excel strings: literal `_xHHHH_` sequences
could be interpreted as escapes, carriage returns could change through XML
normalization, and XML-prohibited control characters threw while writing.
The exporter now escapes the literal underscore prefix, CR and prohibited XML
characters, preserving LF/tab text and valid Unicode. The regex matches only the
underscore to handle overlapping escape-looking sequences. The existing text
cell policy and 32,767-character pre-encoding limit remain unchanged.

Microsoft's [SpreadsheetML Text reference](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.spreadsheet.text?view=openxml-3.0.1)
identifies `t` as ST_Xstring. Its [Office interpretation of ST_Xstring](https://learn.microsoft.com/en-us/openspecs/office_standards/ms-oi29500/d34ae755-c53f-4a44-a363-c6dd3ee018a4)
specifies escaping CR and literal escape-prefix underscores, while keeping LF
and tabs literal in element text. Context7 resolved the Open XML docs but did not
supply the exact escaping rule; the linked primary references supplied it.

A failed export also left its partial workbook behind. Error cleanup now removes
only files whose `CreateNew` succeeded in that call. Existing files are retained;
cleanup attempts both data and metadata files. Deletion errors remain errors.
This does not guarantee publication/cleanup across process loss or disk failure.

The independent Python CSV/XLSX comparison decodes Xstrings once and checks the
exact documented CSV apostrophe policy; it no longer accepts an arbitrary added
apostrophe. Four new reader tests cover escapes, real mismatches, formula-policy
mismatches and row-count errors. No new dependency, MCP tool or export column was added.

## Development evidence

`artifacts/inventory-export-dev/before.log` reproduces literal-text corruption.
`cleanup-before.log` reproduces the leftover partial export after text repair.
`final-scale.log` exposes a .NET `TextFieldParser` limitation on a string
containing all U+0000–U+001F characters together. Raw-byte inspection and Python's
CSV reader retain every character, so that case now checks exact retained file
text rather than relying on a normalizing reader. XLSX tests still decode and
compare every source character.

The focused suite passes 30,336 assertions, including source-string round trips,
all control characters, maximum ASCII/escaped text lengths, capture warnings,
late-sheet refusal cleanup, Host refusal and streamed order/identity checks for
all 30,000 rows in both formats. Small-workbook checks retain SHA-256 receipts.

## Frozen and Excel verification

Final source: `artifacts/inventory-export-01/source`, staged tree
`4c5f5ae2672befb3adaaf71802ead45a737bb24a`, base commit
`d2b57ad602e80a81e0e0739be8678e42fbad9e0c`, 1,794 tracked files. Source manifest
SHA-256: `4320bbdd0c20894cffacd93804477a9221d98907af5038f2db3de48f31e7776d`.
All final non-documentation files match this snapshot; later changes are docs only.

The frozen aggregate passes 29 managed suites, 1,277 Python tests (one unavailable
native-evidence skip), 24 Explorer model/client checks and all portable MCP
workflows. Inventory passes 30,336 assertions. The aggregate console is retained
in `source/artifacts/aggregate-console.log`; its result receipt is
`source/artifacts/checks/result.json`, SHA-256 `16a8f7c7a341db53a52fdf825c60b4d5423fb19b3694829bce30fa426f4a4f57`.

The independent Python reader verifies all cells in six actual CSV/XLSX pairs
(3, 14, 2, 2, 8 and 30,000 data rows), including exact formula-prefix policy and
single-pass Xstring decoding. Receipt: `source/artifacts/checks/inventory-export-crosscheck.json`,
SHA-256 `4653fa19a5ef53095bda813818ada9f367930179f0c229803bf17c434012465a`.

**Installed Excel 16.0, build 20430:** the final harness checks 30,022 generated
inventory name cells (14 special-text cases, eight control cases and 30,000 scale
rows) plus capture-warning cells. Names stay text, including formula-like input;
all compared values and source workbook hashes remain exact. The run uses a
separate process while a controlled unsaved workbook is already open. That
existing instance's content, workbook count, unsaved flag and visibility remain
unchanged. Both owned processes exit normally; no forced stop is needed.

Application receipt beneath `source/artifacts/checks/temp/`:
`tia-inventory-tests-b5bc1b33b2504c57a9afef05d323dd74/excel-verification-b77978dc2eb84dceb71775612c8d7407.json`,
SHA-256 `ff94d90d5ea95a83622d49141c7098ca6b3b69925eb2ff601282703853cfee83`.
Existing-instance receipt in that same directory: `excel-existing-instance-0ede6a45505f424b9fa684eab3cfafe0.json`,
SHA-256 `2a89156d1804058087011dfa3235d973952cfb112f64f200eac59774ab4b67c5`. The wrapper is retained as
`source/artifacts/checks/excel-existing-wrapper.ps1`; its hash matches the receipt.

A deliberate expected-value mismatch is correctly refused at row 2, with unchanged
workbook bytes and clean owned-process exit. Its fixture copies the development
workbook; the final frozen harness performs the negative check. Receipt:
`source/artifacts/excel-negative/excel-verification-73254a0692eb4295959cab99643798cf.json`,
SHA-256 `380fad9ce34d8bcd00fd69791f571bce96c1a6f4ed3a79e06b628b36b7d646ce`. This expected failure is evidence that the application
check detects changed text, not a passing source-data comparison.

The catalog remains 175 declared tools, 55 mutation kinds and 248 features; six
historical inputs match and 106 remain missing. Inventory response commit
`d2b57ad` is fully green in hosted CI (`38936287994`, Explorer `38936287930`).
This export increment needs its own hosted run after push. No new portable ZIP,
clean destination installation or desktop MCP client was tested locally here.

The application harness uses Microsoft's documented [read-only Workbooks.Open](https://learn.microsoft.com/en-us/office/vba/api/excel.workbooks.open),
no link updates, forced macro disabling and a separately identified Excel process.
Only generated fixture workbooks are opened. It checks name values, formulas, warnings,
source hashes and process cleanup; it does not certify screen layout or printing.

## Remaining gates

D16/L05/F02 remain open for other vendor/format/freshness/provenance coverage.
Excel validation is specific to the tested installed build and generated corpus;
CSV import choices and other spreadsheet applications remain separate. Siemens
SDK compilation, native collection, actual module identities and hardware
acceptance remain unavailable. Native Siemens qualification remains false.
