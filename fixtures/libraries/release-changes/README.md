# FB release-change fixture

Two original SCL documents for later generations of the `TiaMcpLibraryGate` type from `../type-lifecycle`:

- `TiaMcpLibraryGate.implementation.scl` (release 1.1.0) keeps the interface and negates the output: an implementation-only change.
- `TiaMcpLibraryGate.breaking.scl` (release 2.0.0) renames output `Active` to `Ready`: a breaking interface change for the `Main` caller in `ConsumerReferences.scl`.
- `ReleaseProbe.scl` is the runtime probe: a global DB `TiaMcpReleaseProbe` (`Enable`, `Output`), a second instance DB `TiaMcpReleaseProbe_Gate` and its own program-cycle OB calling the FB with the written input.
- `ReleaseProbe.adapted.scl` and `ConsumerReferences.adapted.scl` are the same two callers adapted to the renamed output `Ready`.

`scripts/qualify_library_release_changes.py --run-native` authors 1.0.0 from `../type-lifecycle` in a project with simulation support, since consumers cannot add simulation support to non-editable library blocks. Each later generation is created from documents (`library_edit_version` with `documentDirectory`): the runner exports the previous version, keeps its `.libinfo` and replaces the document with the authored SCL. TIA V21 refuses replacing an edited version's test instance by source generation. Each generation is released into a separate global-library archive. A fresh consumer takes 1.0.0, updates to 1.1.0 (compile clean, unrelated content preserved), then attempts 2.0.0. The breaking update must fail compilation at `Main` without saving. Its shared admission is reconciled in a fresh session only if it is still unresolved (current builds return it), the checkpoint is restored into a new location and the unsaved original is reopened, both still linked to exactly 1.1.0.

Each release is checked against independently authored expectations of its native export (output member names, whether the SCL body negates) and of both instance DBs (top-level interface members). Both stale callers (`Main`, `TiaMcpReleaseProbe_Cycle`) must report the removed output `Active`, and no unrelated block may. A fresh third consumer instantiates exactly 2.0.0 with the adapted callers, which must compile clean.

Behavior runs on a fresh owned PLCSIM Advanced V8 instance per release (1.0.0, 1.1.0 and the adapted 2.0.0), downloaded from the saved scratch. `sim_test` writes the probe input and asserts the probe output, the probe instance output, Main's instance output and Main's UDT field against an authored truth table (`BEHAVIOR` in the runner): 1.0.0 copies `Enable`, 1.1.0 and 2.0.0 negate it. The other release's output member must be absent from the simulator tag list. Pass `--sim-worker` with the packaged simulator worker.
