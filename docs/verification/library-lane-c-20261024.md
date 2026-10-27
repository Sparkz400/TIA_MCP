# Library version, dependency, lifecycle, compatibility and vendor qualification — 24 October 2026

Lane C of the [parallel completion plan](../plans/parallel-completion-20261024.md). Every run used the real MCP stdio Host,
owned headless TIA V21 sessions and owned scratch projects, under the machine-wide native lane lock. Raw reports stay under
`D:\lcq`; the committed summaries are in `evidence/`.

## Evidence

| Evidence | Runner | Build | Result |
| --- | --- | --- | --- |
| `local.library_releases.20261024` | `qualify_library_release_changes.py` | 4687b82 | 75/75 steps |
| `local.library_dependencies.20261024` | `qualify_library_dependencies.py` | 4687b82 | 82/82 steps |
| `local.library_lifecycle.20261024` | `qualify_library_lifecycle.py` | 4687b82 | 82/82 steps |
| `local.library_formats.20261024` | `qualify_library_formats.py` | 4687b82 | 17/17 steps |
| `local.library_project_state.20261024` | `qualify_library_project_state.py` | 4bb2e8f | 18/18 steps |
| `local.library_upgrades.20261024` | `qualify_library_upgrades.py` | 48d4e38 | 37/37 steps |
| `local.library_interruption.20261024` | `qualify_library_interruption.py` | 48d4e38 | 26/26 steps |
| `local.library_vendor.20261024` | `qualify_library_vendor.py` | 48d4e38 | 22/22 steps |

`scripts/validate_library_qualification.py` validated every retained plan, change result and original-byte lock receipt of
these runs against the library schemas (258 observations, 83 lock receipts, no violation after declaring the new preview fields).

## What passed

- **Releases.** One FB type carries 1.0.0, an implementation-changing 1.1.0 (negated output) and a breaking 2.0.0 (output
  renamed). Released exports match independently authored expectations. A consumer updates 1.0.0 → 1.1.0 with unrelated
  native exports unchanged. The 2.0.0 update fails compilation at the stale caller `Main` ("The formal parameter 'Active' is
  invalid") and is not saved; after reconciling its admission, the checkpoint restores and the unsaved original reopens, both at
  exactly 1.1.0. Behavior is asserted from native exports, not executed.
- **Dependencies.** Nested UDT (member and array) ← FB ← FB (multi-instance) closure matches an authored graph before and after
  an update removes a reference. Publishing the root carries the exact closure. An installed exact dependency is reused. An
  exact-version update is a no-op. Refused before mutation: non-default older version, removed reference stranding a use
  (`LibraryUpdateCleanupRisk`), downgrade below a newer project default, same-name different-GUID and same-name unlinked
  dependencies.
- **Lifecycle.** Discard, malformed/mixed/read-only policy flags, DoNotUse publication block, folder create/rename/delete with
  conflicts, publication into an existing library, master copies, deletion constraints, a fresh consumer without block-number
  collision, and restore of a library-only checkpoint.
- **Formats and sources.** Read-only archives and directories give writable owned copies and stay unchanged; locked, newer
  (`.zal22`), corrupt and lone-marker sources are refused; mixed packages classify correctly and both complete libraries open.
- **Project state.** An inherited inconsistent block refuses library preparation (`FingerprintUnavailable`) before any write.
- **Upgrades.** DMC PLC archives V14 SP1 (S7-300), V15, V16, V17 and V18 upgrade into V21, read back complete and reopen with
  identical identities; HMI-bearing archives upgrade with unavailable elements listed; companion and read-only directories upgrade.
- **Interruption.** Worker killed while compiling a multi-resource change (project plus global library) and while archiving a
  checkpoint: fresh-host `outcome_unknown`, replay refused, admission reconciled in a fresh session, both resources restored or
  (without a completed checkpoint) restore refused and the saved original reopened unchanged.

## Native findings changed in code

- TIA refuses source generation over an edited version's test instance. `library_edit_version` now also accepts
  `documentDirectory`/`documentName` and creates the next version with `LibraryTypeVersionComposition.CreateFromDocuments`.
- `SetForUpdate` is read-only on PLC data types; setting both policy flags half-applied `doNotUse`. Unwritable flags are refused.
- Siemens' version comparison covers metadata only. Reuse requires identical metadata (default flag and uncompared original
  library excluded) and no longer claims a content comparison.
- V21 upgrades complete and save `<name>_V21`, then the return throws "Could not load type 'Dummy'". The saved library is opened
  only when its conversion log records success; elements of absent products are listed as `unavailable`.
- `File.Copy` kept the read-only attribute on working copies; owned working copies are now writable.
- A failed change leaves its shared admission `outcomeUnknown`; restore needs `engineering_admission_reconcile` from a fresh session.
- Several upgrades in one session exceed TIA's 500,000-instance limit; the runner uses one session per upgrade.

## Vendor distributions

Downloads with receipts (URL, size, SHA-256, licence) are under `D:\lcq\vendor`. PAL (MIT, © 2020 Michael Gledhill) is a
TIA V16 VCI export of 55 SimaticML files; V21 refuses its Engineering version (V16), so a native import needs an intermediate
V17–V20 installation. DMC Siemens Open Library 19.0 (MIT, © DMC, Inc.) upgrades all five archives; one dependency-free data
type instantiates with an exact link. DMC prerequisites (CPU system/clock memory bytes, "Open Library" tag-table master copy)
were outside MCP routes on 24 October, so complete DMC PLC compilation is not claimed; both are covered since 25/26 October ([CPU memory bytes](cpu-memory-bytes-20261026.md)). HMI behavior is left to lane D.

## Not covered

Multiple PLCs and software units, namespaces, protected/know-how objects, runtime behavior, a V20 library, kills during native
mutation or save, and PAL native import. (Protected objects and unit relations since covered: [27 October](#protected-objects-and-related-software-units--27-october-2026).)

## Follow-up slice — 25 October 2026

Build 041b0d8 (frozen `D:\lcq\b8`):

| Evidence | Runner | Result |
| --- | --- | --- |
| `local.library_scopes.20261025` | `qualify_library_scopes.py` | 63/63 steps |
| `local.library_interruption.20261025` | `qualify_library_interruption.py` | 55/55 steps |
| `local.library_dmc_prerequisites.20261025` | `qualify_library_dmc_prerequisites.py` | 7/7 steps |
| `local.library_formats.20261025` | `qualify_library_formats.py` | 17/17 steps |
| `local.library_vendor.20261025` | `qualify_library_vendor.py` | 22/22 steps |

- **Two PLCs.** A second PLC (added with scratch security, since a new S7-1500 needs its confidential-data password before it compiles) consumes the same types. An update scoped to one PLC leaves the other at its version with every native block export unchanged.
- **Interruption at four points.** Kills during the native mutation and during compile restore both resources. A kill during the project save, after the global library was already saved, is held by reconciliation ("The global library differs from its pre-transaction checkpoint") and restore stays blocked for a human decision. The checkpoint-archive kill reopens the untouched original.
- **DMC tag table.** `library_instantiate` accepts `destinationComposition=tagTables`; DMC's "Open Library" tag-table master copy instantiates once and a duplicate is refused.
- **Locked sources.** The library adapter refuses a locked source as `SourceLocked`; for `library_open` the shared admission scope hashes the source first, so the refusal surfaces there as an `IOException` naming the locked file.
- **Inventory checks.** `project_inventory` lists blocks only; refusal and recovery checks now also compare PLC data types and tag tables through `software_inspect`. The vendor and formats runs were repeated under this check.

Still not covered: software units and namespaces (unit creation needs a native fixture tool whose Openness AllowList key requires an elevated `Register-DevOpennessWorker.ps1 -Grant -AllNativeTools`), protected objects, runtime behavior, a V20 library, PAL native import and DMC CPU clock memory (since closed: [CPU memory bytes](cpu-memory-bytes-20261026.md)).

## Software units and namespaces — 25 October 2026

After the user's elevated Openness grant, `scripts/native/LibraryUnitSeeder` seeds one software unit (`TiaMcp_LibUnit`, namespace preset `TiaMcp_LibUnit`). `local.library_units.20261025` (`qualify_library_units.py`, build a74211b, 49/49 steps):

- Library FB and UDT instantiate into the unit once. TIA does not apply the unit's namespace preset to library instances.
- TIA allows one instance of a library type per device. Instantiating the same type in the PLC root, or another unit, previously failed natively after the checkpoint; the preview now refuses it (`LibraryConflict`).
- A PLC-wide update is refused while a unit use has no OB-rooted reference; no version is copied.
- The unit use survives a fresh reopen.

`local.library_dependencies.20261025` repeats the dependency run on this build to confirm same-scope reuse is unaffected.

## Release behavior on PLCSIM — 26 October 2026

`local.library_releases.20261026` (`qualify_library_release_changes.py` at bc974fc, build da24990 frozen at `D:\lrc\b9`, raw
report `D:\lrc\a\rc_e57871ae`, 105/105 steps) extends the release run above with behavior executed on fresh owned PLCSIM
Advanced V8 CPU1511 instances. One instance per release is downloaded from the saved scratch.

- **Oracles.** The runner's `INSTANCE` and `BEHAVIOR` tables are authored from the fixture SCL, not read back from the
  builder. A runtime probe (`fixtures/libraries/release-changes/ReleaseProbe.scl`: global DB, second instance DB, own
  program-cycle OB) writes `Enable`. `sim_test` asserts the probe output, the probe instance output, Main's instance output
  and Main's UDT field.
- **1.0.0 → 1.1.0 (implementation only).** 1.0.0 copies the input. After the update to exactly 1.1.0, both instance DBs keep
  `Input: Enable`, `Output: Active`, the unrelated native exports are unchanged, and the inverted truth table passes.
- **2.0.0 (breaking).** The update fails compilation at exactly `Main (OB1)` and `TiaMcpReleaseProbe_Cycle (OB123)`
  ("The formal parameter 'Active' is invalid.") and nowhere else. It is not saved; the checkpoint restores to a new location
  and the unsaved original reopens, both at exactly 1.1.0 with unrelated content unchanged.
- **Adapted callers.** A fresh consumer at exactly 2.0.0 with the same two callers adapted to `Ready` compiles clean. Its
  instance DBs expose `Ready` and no `Active`, and the 2.0.0 truth table passes.
- All 67 retained library observations (21 original-byte lock receipts) validate against the library schemas.

Native findings:

- A consumer download refuses library blocks compiled without simulation support ("'TiaMcpLibraryGate [FB2]' cannot be
  simulated … use a library with simulation support"). The consumer's project setting cannot reach non-editable library
  blocks, so simulation support must be enabled in the authoring project before the types are created.
- On this build a compile-failed library update returns its shared admission. The `outcomeUnknown` plus reconciliation
  seen in lane C no longer occurs, and the runner reconciles only an unresolved admission.

Not covered: instance data retention across update and re-download (the FB is stateless), caller migration within the
failing update (project-library updates require `compile=true`), software units, protected/F types and physical CPUs.
(Protected FB types and related units since covered: [27 October](#protected-objects-and-related-software-units--27-october-2026).)

## Protected objects and related software units — 27 October 2026

Build 51d1d9e (frozen `D:\lpr\b1`, package `tia-mcp-v21_20261027_065357_c5960a43`), owned scratch projects, no runtime.
`scripts/native/LibraryUnitSeeder` gains three fixture modes: `protect` (know-how protection with a random password
that is never stored), `related` (two units with one `SoftwareUnit` relation) and `publish` (sets a unit object's
`Access` attribute to `Published`).

| Evidence | Runner | Result |
| --- | --- | --- |
| [`local.library_protected.20261027`](../../evidence/local.library_protected.20261027.json) | `qualify_library_protected.py` | 60/60 steps, 41 observations valid |
| [`local.library_unit_relations.20261027`](../../evidence/local.library_unit_relations.20261027.json) | `qualify_library_unit_relations.py` | 58/58 steps, 49 observations valid |

**Protected objects.**

- **Create.** A protected plain FB becomes a global-library master copy. An FB type's in-work test instance is protected and released as 1.0.0.
  Worker fix: the in-work content pin previously refused protected test instances, because Openness cannot export them. It now uses the block's native fingerprints, the same values the project fingerprint already uses for protected user blocks.
- **Copy.** `library_publish` copies the protected type into the global library. A metadata-only edit and release of the still-protected test instance gives 1.1.0, published as the new default.
- **Instantiate.** In a fresh consumer the master copy and FB 1.0.0 instantiate and stay protected, and Main calls both.
- **Update.** The PLC-wide update to exactly 1.1.0 applies. The cleanup guard reads cross-references of every block, protected ones included. A fresh reopen keeps both blocks protected.
- **Exact refusals.**
  - `block_read` refuses every protected block with `ProtectedObject`.
  - Openness refuses `ExportAsDocuments` of a protected type version ("The block is know-how-protected and cannot be exported"). The worker returns this as `EngineeringError`. As a result, `library_edit_version` from documents (a content change) is unavailable for protected versions.
  - V21 Openness has no protection service for PLC data types, so protected UDTs cannot be produced.

**Related software units.** `TiaMcp_LibConsumer` has a `SoftwareUnit` relation to `TiaMcp_LibProvider`.

- FB and UDT 1.0.0 instantiate into the provider.
- Instantiating either one in the related consumer is refused before any write (`LibraryConflict`). The one-instance-per-device rule holds across relations.
- TIA accepts `Published` on the typed provider instances. A consumer-unit cycle OB, instance DB and UDT DB then call them through the relation and compile.
- The PLC-wide update to exactly FB 1.1.0 applies. The cleanup guard proves both provider uses through native cross-unit edges from the consumer OB: OB → instance DB → FB, and UDT DB → UDT. The UDT stays at 1.0.0 and every path is unchanged.
- A fresh reopen keeps the relation, FB 1.1.0 and the placement.

Not covered: `NonUnitDB`/`TODB` relations, namespaces on library instances, safety units, protected UDTs, content
changes of protected versions, MCP routes for units/relations/protection, and runtime behavior.
