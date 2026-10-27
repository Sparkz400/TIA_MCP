# Native summary review — 28 October 2026

Offline review of rank 1 in the [host-only sweep](host-only-qualification-20261027.md#remaining-offline-reachable-work-ranked-by-cells-per-effort):
X11, X10 and L02 change kinds that the generic `change_*` tools prepare in passed, retained native runs
under [`evidence/`](../../evidence/README.md). No TIA Portal, PLCSIM, Host or worker was started, and no
evidence was created or rerun.

The rules are the ones of the [27 October audit](release-matrix-audit-20261027.md). A row closes as a whole,
and only when:

- each cited record is `passed`, retained under `evidence/` and has a completed summary whose passed tools
  include all of the row's MCP tools (`claim_errors`);
- the run targets the row's operation and its gates map to every acceptance criterion of the row, for the
  recorded profile;
- no plan, TODO line or record limitation holds the row open on a criterion.

Method: for each of the 54 unqualified `change.*` rows owned by X11, X10 or L02 (326 open cells), the kinds a
run prepares were taken from its summary scope, its runner and, where the raw report is still on this PC
(matched by the SHA-256 in the summary), the `kind` and apply `stages` of each prepared change. Raw reports
found: `milestones.json`, `hardware.json`, `reopen.json`, `explorer.json`, `reviews.json`,
`provision2.json`, the library import-scale report and the typed curation reports.

**Result: no row closes.** The catalog stays at `0.4.44`; open cells 1,652 → 1,652. Outside the typed and
library runs, these kinds appear only as fixture seeds, prerequisites or smoke steps. None of those runs both
reopens the changed state and recovers the change, and only the remote-I/O, typed and library-interruption
runs pass `change_status`.

## Closed rows

None.

## Reviewed rows that stay open

| Rows | Cells | Retained runs that prepare them | Missing |
| --- | --- | --- | --- |
| `change.graphical_operand_set` | 6 | `local.milestones.20261024`, `local.sim.state.20261024` (same engineering phase): LAD and FBD edits with preview, zero-error compile, `native_preservation_verified`, structured-edit readback, save and restore from the change's own checkpoint | AC-native: neither run reopens the changed project. Tools: neither passes `change_status`. The catalog also cites `local.typed.resolution.20261025`, which prepares only `graphical_typed_edit`. |
| `change.source_upsert` | 6 | Milestones (SCL/STL fixtures, idempotent replay), `local.reviews.20261024`, `local.typed.resolution.20261025` (competing DB: addition-only XML, own-checkpoint restore, recovered reopen), `local.engineering.boundaries.20261024` (apply-cancel, stale previews), `local.engineering.interruption-matrix.20261027` (route representative, `project_retrieve` recovery), `local.cpu_memory_bytes.20261026` (consumer FC, reopen) | Tools: no run that targets it passes `change_status`. No single run combines own preservation, reopen of the applied change and its recovery; the gates are spread over four fixtures and runners. The SIMATIC SD route has only reference captures ("no changed-state recovery"). |
| `change.block_import_xml` | 6 | Milestones (LAD/FBD fixtures), typed seeds (`qualify_typed_graphical.py`, `qualify_typed_resolution.py`), `local.typed.route-capture.20261025` and the IEC/scoped captures (compile, save, existing XML unchanged, fresh reopen) | AC-recovery: the capture records say "no changed-state recovery"; typed runs restore the typed edit, not the import. Tools: the captures pass no `change_status`/`change_restore` (two pass no `change_*` tool). |
| `change.type_source_upsert`, `change.plc_type_import_xml`, `change.tag_table_import_xml` | 18 | `local.hardware.20261024` (UDT and tag-table XML roundtrips, compile, save), `local.reopen.20261024` (roundtrips on the reopened project), milestones UDT fixture, typed-resolution seeds | AC-preserve: no preservation stage or readback. AC-recovery: no restore or retrieval. Tools: no `change_status`/`change_restore` in hardware or reopen. |
| `change.block_rename` | 6 | Milestones: compile, save, own-checkpoint restore, recovered block XML read back | AC-preserve: no preservation stage. AC-native: no reopen. Tools: no `change_status`. |
| `change.block_delete` | 6 | Milestones: unused fixture deleted with compile and save; a repeated delete is refused (`BlockNotFound`) | AC-preserve, AC-recovery, reopen; no `change_status`. |
| `change.tag_set` | 6 | `local.typed.resolution.20261025` stale-tag-address case: deliberate address change, address-only XML check, own-checkpoint restore, recovered reopen | AC-native: the changed state is not reopened. Tools: no `change_status`. Fixture-side use only; rank 2 (an X11 object-mutation runner) covers it. |
| `change.group_create` | 6 | `local.explorer.20261024`: six block groups as explorer fixtures | AC-preserve, AC-recovery, reopen; no `change_status`/`change_restore`. |
| `change.plc_compile` | 6 | `local.sim.provision.20261024`, `local.sim.state.20261024`: generated-state save after download | AC-recovery and AC-preserve (the row's own limitation); no `change_status`/`change_restore`. |
| `change.graphical_typed_edit` | 8 | 28 retained typed records | Held by the [typed TODO](../plans/typed-lad-fbd-todo.md) and the main TODO typed line: candidate lineage, unrelated-content preservation, persistence and recovery for the inactive profiles. |
| `change.block_number_set`, `block_attributes_set`, `graphical_network_patch`, `graphical_network_insert`, `call_parameter_set`, `call_connection_set`, `type_rename`, `type_delete`, `tag_create`, `tag_delete`, `constant_create`, `constant_set`, `constant_delete`, `group_rename`, `group_delete` | 90 | None. Typed insertion runs use `graphical_typed_edit`; their old citations (`local.layers.*.20261008`) are not retained | First native qualification (rank 2). |
| `change.hardware_add`, `change.network_configure` | 12 | `local.remote-io.*` (prerequisites with `compile=false`, `save=false`, no configured-preservation check), `local.cpu_memory_bytes.20261026` (CPU 1516-3 PN/DP added; preservation checked only for the memory-byte kind, later changes restored), milestones (offline network address, compile with a warning, save) | AC-preserve, AC-native and AC-recovery for the change itself. Same finding as the audit. |
| `change.hardware_delete`, `change.hardware_attributes` | 12 | `local.hardware.20261024`: central DI/DO/AI/AO deletions and attribute changes compile and save without `configured_hardware_preservation_verified` or a readback; `local.reopen.20261024` checks UDTs and tag tables only | AC-preserve, AC-recovery, reopen; no `change_status`/`change_restore`. |
| `change.hardware_port_connect`, `hardware_port_disconnect`, `hardware_replace` | 18 | None | First native qualification. |
| `change.library_create_type`, `library_create_master_copy`, `library_instantiate`, `library_update_project` | 24 | Tool-complete with `local.library_interruption.20261024/20261025` plus releases, dependencies, lifecycle, scopes, units, protected and unit-relation runs | AC-preserve: [library TODO](../plans/library-management-todo.md) "Complete independent post-operation preservation/readback … for every recipe" and the row limitation "Save/reopen, preservation and runtime claims require independent evidence". Instantiate: namespaces on library instances are open (TODO; `local.library_unit_relations.20261027`). Master copy: the group source is "not natively qualified". Two of these rows still cite the unretained `local.library_plc_master.20261008`. |
| `change.library_create`, `archive`, `copy_elements`, `edit_version`, `release_version`, `discard_version`, `set_default`, `type_policy`, `organize`, `delete`, `publish` | 66 | Releases, dependencies, lifecycle, protected, unit-relation and import-scale runs | Tools: no cited record passes `change_status` (no interruption run covers them). The same AC-preserve TODO line applies. Edit/discard, defaults, policy flags, copy/publication, folders and deletion stay listed in the library TODO. |
| `change.library_upgrade` | 6 | `local.library_upgrades.20261024`, `local.library_vendor.20261025`, `local.library_formats.20261025`, `local.library_compatibility.20261027` | Tools: no `change_restore`/`change_status`. V20 upgrades are open (TODO). |
| `change.library_integrate`, `library_cleanup`, `library_harmonize`, `library_save` | 24 | None (combined `library_integrate` apply stays disabled) | First native qualification; integrate and cleanup are listed in the library TODO. |

## Cheapest next runs

- **Milestones family (`graphical_operand_set`, `block_rename`, `source_upsert`, `block_import_xml`,
  `type_source_upsert`, `block_delete`, `network_configure`).** `validate_milestones.py` already prepares,
  compiles, saves and (for two kinds) restores them. It would close the most cells if it also called
  `change_status` after each apply, required the preservation stage for every kind, and reopened the changed
  project in a fresh Portal before each restore. `graphical_operand_set` needs only the reopen and
  `change_status`.
- **Hardware smoke (`hardware_delete`, `hardware_attributes`).** Make `smoke_hardware_to.py` require
  `configured_hardware_preservation_verified` for these kinds, restore one change from its checkpoint and
  reopen, or move them into `qualify_remote_io.py`, as the audit proposed.
- **Libraries.** These rows close only after the library TODO preservation/readback line is closed and the
  catalog limitation on save/reopen and preservation is narrowed. Twelve of them also need `change_status`
  (`library_upgrade` also `change_restore`) in a run that targets them.
