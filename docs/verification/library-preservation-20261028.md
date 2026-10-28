# Library preservation, persistence and recovery per change kind — 28 October 2026

`scripts/qualify_library_preservation.py` (offline checks: `scripts/test_qualify_library_preservation.py`) at
1de880d on build ce021da (frozen `D:\wp\b14`), owned TIA Portal V21 scratch projects, no runtime. Raw report
`D:\lp\a\lp_9e3f2652\qualification.json`.

| Evidence | Result |
| --- | --- |
| [`local.library_preservation.20261028`](../../evidence/local.library_preservation.20261028.json) | 477/477 steps: 95 independent snapshots, 53 typed refusals, 19 checkpoint restores, 0 snapshot mismatches; 197 library observations (64 lock receipts) valid |

## Fixture

- Scratch CPU 1511-1 PN V4.1 (PLC 1) plus a second CPU added with scratch security (PLC 2), one owned user global library.
- The [dependency fixture](../../fixtures/libraries/dependencies/README.md): UDT Inner ← UDT Outer (member and `Array[0..2]`) ←
  FB Child ← FB Parent (Child multi-instance). An unrelated UDT `TiaMcpLibSpare` and, in phase B, a UDT `TiaMcpLibExtra`.
- Upgrade input: `Open Library V19 PLC.zal19` from DMC Siemens Open Library V19 (MIT, `Siemens-Open-Library-V19.zip`,
  SHA-256 `1a7289c1…bfacb`; archive SHA-256 `153c3654…d439`).

## What every change had to pass

- **Snapshot diff.** Around every change the runner takes an independent snapshot through read tools only: every block,
  PLC data type and tag-table XML of both PLCs (native payload hash), every project and global library type, version,
  folder and master copy, and the native document export of every committed project-library version. The new
  snapshot must differ from the previous one in exactly the intended keys.
- **Identity and preview.** On a kind's first use, a stale source revision (`PreviewStale`), an unregistered destination
  library (`LibraryIdentityMismatch`), a changed upgrade hash (`PackageChanged`) or an invalid name (`InvalidRequest`)
  is refused with no change. A wrong `previewHash` is refused (`PreviewMismatch`), the change stays `prepared` and nothing changes.
- **Persistence.** A fresh Portal reopens the saved project and global library: the snapshot equals the last verified
  state. The upgraded library and the global-library archive reopen equal to their recorded rows.
- **Recovery.** From the reopened state each kind applies once more and restores its checkpoint into new locations
  (project checkpoints in an empty owned Portal). The recovered snapshot equals the pre-change snapshot.
  `library_create` and `library_upgrade` write only new outputs: `change_restore` refuses (`CheckpointInvalid`) and the
  originals are unchanged.

## Covered operations

| Kind | Phase A (exact diff) | Phase B restore |
| --- | --- | --- |
| `library_create_type` | Inner, Outer, Child, Parent, Spare | Extra |
| `library_release_version` | 1.0.0 of each, Parent 1.1.0–1.3.0 | Extra 1.0.0 |
| `library_edit_version` | Parent test instance; Parent from documents ×3 | Extra test instance |
| `library_discard_version` | Parent edit (committed version refused) | Extra edit |
| `library_set_default` | Parent back to 1.0.0 | Extra back to 1.0.0 |
| `library_type_policy` | Inner `doNotUse` on/off; malformed, mixed and read-only flags refused | Inner `doNotUse` |
| `library_organize` | folder create/rename/delete; duplicate and root rename refused | folder create |
| `library_delete` | unused Parent 1.1.0; default version refused | unused Extra 1.0.0 |
| `library_cleanup` | unused Parent 1.0.0 (`PreserveDefaultVersionOfUnusedTypes`); used-only selection refused (`NoEffect`) | unused Extra 1.0.0 |
| `library_create` / `library_publish` / `library_archive` / `library_save` | global library, Parent closure, Spare, Parent 1.2.0/1.3.0, archive, clean save | create (no checkpoint), Extra, archive, save |
| `library_instantiate` | Parent closure and Spare into PLC 2 | master copy into PLC 2 |
| `library_update_project` | PLC 2 to Parent 1.2.0 after reachability refusals | PLC 2 to Parent 1.3.0 |
| `library_create_master_copy` / `library_copy_elements` | two blocks into the global library, one copied into the project library; duplicate refused | instance DB master; copy back |
| `library_harmonize` | Parent renamed back in PLC 2 (no flag refused) | Child |
| `library_upgrade` | DMC V19 PLC, source bytes unchanged | refusal without checkpoint |

`library_save` also persists the partial state of a failed change. In a separate scratch, run first, two CPUs share
one IP address on one subnet (the second assignment applied without compile and save), so every block stays
consistent and the project compile fails after a master copy is added to the global library (`CompileFailed`,
`recovery_required`). An unrelated `recoveryChangeId` is refused (`RecoveryRequired`). Saving with the failed change's
ID persists exactly the added master copy, and restoring that save retrieves the failed change's checkpoint, equal to
the library before it.

**Update reachability.** Before the PLC 2 update, preparation is refused (`LibraryUpdateCleanupRisk`) with no change
while no OB reaches any library object, while Child, Outer and Inner are reachable only through the updated Parent, and
while the unrelated spare type is unreferenced. With a cycle OB referencing all of them, the update applies and changes
only PLC 2's Parent.

## Native findings

- Releasing a higher version makes it the default, so an explicit default change is only an effect toward an older version.
- A type's native `status` follows instance/default relations (`Consistent`, `NonDefaultVersionInstantiation` while a
  PLC uses a non-default version). The runner lets a type's own rows change with any change of that type; every other
  key stays exact.
- A released block instance exports without `HeaderAuthor`/`HeaderFamily`/`HeaderName`/`HeaderVersion`; a PLC data type
  instance exports unchanged.
- Renaming a typed FB instance (allowed natively) changes every export that names it: the instance DB's
  `InstanceOfName`, the calling OB's call info and a parent FB's multi-instance member.
- A version export's `.libint` holds byte hashes of the exported documents, which carry an export timestamp. Documents
  compare by native payload and `.libint` without those hash lines.
- A new CPU's `Main` is LAD; SCL sources must not replace it (`LanguageConversionNotSupported`), so PLC 2 gets its own cycle OB.
- DMC's PLC archive contains SiVArc master copies of absent HMI products, listed as unavailable after the upgrade.
- A CPU added without compile leaves an inconsistent `Main`, and every change then refuses (`FingerprintUnavailable`)
  before mutating; an engineering change without compile may not save.
- The plan schema did not declare the `cleanup` and `harmonize` previews of these kinds' first native run; both are
  now declared in `contracts/library-plan.v1.schema.json`.

Runs 1–5 at earlier runner commits stopped on these findings (raw reports `lp_8f6600a3`, `lp_4bd412e3`, `lp_f205bd6f`,
`lp_7f1fb0fc`, `lp_10cabb25` under `D:\lp\a`). Runs 4 and 5 passed everything except the failed-change setup. Only
the final run is evidence.

## Catalog

Catalog `0.4.50` verifies the 19 change rows above, `change.library_integrate` (its PLC route from
`local.library_integrate.combined_apply.20261028`) and the umbrella row `library.types`, each with its qualified profile
as first limitation: 123 matrix cells bound. The historical 8 October record `local.library_plc_master.20261008`
has no retained summary and is no longer cited by these rows.

## Not covered

- `library_cleanup` with `DeleteUnusedTypes` or folder selections; `harmonizePaths` where library folder and PLC group differ
  (V21 cannot move a type into a folder or a block into a group).
- Isolated cycles: UDT nesting and FB multi-instances cannot be cyclic in TIA, and call cycles between plain blocks hold no library objects.
- Divergent content under an identical version GUID: not constructible through the public API.
- `library_integrate` (qualified by `local.library_integrate.combined_apply.20261028`), HMI types, software units and
  protected types (their own runs), interface-changing releases and runtime behavior (`local.library_releases.20261026`).
