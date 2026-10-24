# FB release-change fixture

Two original SCL documents for later generations of the `TiaMcpLibraryGate` type from `../type-lifecycle`:

- `TiaMcpLibraryGate.implementation.scl` (release 1.1.0) keeps the interface and negates the output: an implementation-only change.
- `TiaMcpLibraryGate.breaking.scl` (release 2.0.0) renames output `Active` to `Ready`: a breaking interface change for the `Main` caller in `ConsumerReferences.scl`.

`scripts/qualify_library_release_changes.py --run-native` authors 1.0.0 from `../type-lifecycle`. Each later generation is created from documents (`library_edit_version` with `documentDirectory`): the runner exports the previous version, keeps its `.libinfo` and replaces the document with the authored SCL. TIA V21 refuses replacing an edited version's test instance by source generation. Each generation is released into a separate global-library archive. A fresh consumer takes 1.0.0, updates to 1.1.0 (compile clean, unrelated content preserved), then attempts 2.0.0. The breaking update must fail compilation at `Main` without saving. Its shared admission is reconciled in a fresh session, the checkpoint is restored into a new location and the unsaved original is reopened, both still linked to exactly 1.1.0.

Each release is checked against independently authored expectations of its native export: the output member names and whether the SCL body negates. No CPU or PLCSIM execution is involved.
