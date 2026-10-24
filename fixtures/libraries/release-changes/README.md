# FB release-change fixture

Two original SCL replacements for the `TiaMcpLibraryGate` test instance in `../type-lifecycle`:

- `TiaMcpLibraryGate.implementation.scl` (release 1.1.0) keeps the interface and negates the output: an implementation-only change.
- `TiaMcpLibraryGate.breaking.scl` (release 2.0.0) renames output `Active` to `Ready`: a breaking interface change for the `Main` caller in `ConsumerReferences.scl`.

`scripts/qualify_library_release_changes.py --run-native` authors 1.0.0 from `../type-lifecycle`, then edits the FB version, replaces its test instance through `source_upsert` and releases each generation into separate global-library archives. A fresh consumer takes 1.0.0, updates to 1.1.0 (compile clean, unrelated content preserved), then attempts 2.0.0. The breaking update must fail compilation at `Main` without saving; the checkpoint is restored into a new location and the unsaved original is reopened, both still linked to exactly 1.1.0.

Each release is checked against independently authored expectations of its native export: the output member names and whether the SCL body negates. No CPU or PLCSIM execution is involved.
