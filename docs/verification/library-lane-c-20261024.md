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
are outside current MCP routes, so complete DMC PLC compilation is not claimed. HMI behavior is left to lane D.

## Not covered

Multiple PLCs and software units, namespaces, protected/know-how objects, runtime behavior, a V20 library, kills during native
mutation or save, and PAL native import.
