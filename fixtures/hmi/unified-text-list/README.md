# Bilingual Unified text-list fixture

This input follows Siemens' V21 [paired text-list/TextLibrary YAML example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/text-lists/export-of-text-lists). The names, two scalar values and bilingual text are synthetic. The paired files are intentionally kept together; importing only the configuration does not establish translated text.

Use `HmiSeeder.exe qualify <new-output> unified-comfort positive textlists` to attempt native qualification. The files are copied into each newly owned HMI's evidence directory, hashed, imported through the documented collection import, and independently re-exported. A successful native return alone is not accepted: the named nonempty list and both languages' text must appear in the native evidence. Lifecycle stages retain separate exports. Until the native run and bridge comparison complete, these are documented input fixtures, not native-accepted output or a production preservation contract.

For this option the newly created disposable project activates `en-US` and `de-DE` using the public `LanguageSettings.ActiveLanguages` association before HMI creation/import. The before/after native language inventory is retained; existing projects are never accepted by the seeder.

Native run `artifacts/p16h/native-resources-01` accepted the paired import on both Unified Comfort owners, read back `QualificationStates`, and exported both entries with all four English/German strings through reopen and archive retrieval. This qualifies the documented input and native resource observation only. HMI compiler summaries remain contradictory to Error-state password diagnostics, and selected-resource bridge source comparison remains pending. No production resource mutation is enabled.

The example's unsupported formatted-text case remains excluded. Graphic assets, global script-module paired files and a library type/version with faceplate interface definitions still require independently valid templates; these files do not substitute for them.
