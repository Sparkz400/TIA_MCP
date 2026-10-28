# Release matrix audit — 27 October 2026

Offline audit of the open cells in the frozen V21 release matrix
([`release-matrix.v1.json`](../plans/release-matrix.v1.json)). No TIA Portal,
PLCSIM or worker was started, and no evidence was created or rerun.

A matrix cell is bound only when its feature is `verified`, so a row closes as a
whole. A row was closed only when every one of these holds:

- each cited record is `passed`, retained under `evidence/` and has a completed
  summary whose passed tools include all of the row's MCP tools (`claim_errors`);
- the run targets the row's operation and its gates map to every acceptance
  criterion of the row, for the recorded profile;
- no plan, TODO line or record limitation holds the row open on a criterion.

Rows that pass these checks were all verified before 24 October. They became
unverified when their source changed or their artifacts were not retained, and
the 24 October reruns that cover them again never re-bound them. Unretained
citations were dropped (the records stay in the catalog as history), and the
catalog moved to `0.4.27`. Open cells: 1,379 → 1,345. Rows: 41 shipped, 18
limited, 270 unqualified.

## Closed rows

| Row | Cells | Evidence | Basis |
| --- | --- | --- | --- |
| `artifact_upload_begin` | 3 | `local.library_intake.20261024` | Host-only chunked upload with host restart, byte-identical retry. Same run already verifies `artifact_upload_status`/`release`. |
| `artifact_upload_chunk` | 3 | `local.library_intake.20261024` | As above. |
| `artifact_upload_commit` | 3 | `local.library_intake.20261024` | Incomplete commit rejected, complete hash and repeated commit. |
| `library_package_stage` | 3 | `local.library_intake.20261024`, `local.library_vendor.20261025`, `local.library_upgrades.20261024` | The same three records verify `library_package_inspect`. Stage still cited the unretained 8 October record. |
| `project_snapshot_capture` | 3 | `local.reviews.20261024` | The before/after captures feed the compare/search/health rows that this run already verifies. |
| `change.hardware_plug` | 6 | `local.remote-io.full-route.20261024`, `local.remote-io.lifecycle.20261024` | DI and AI creation, each with two-device compile, strict configured preservation and save, then fresh reopen. Recovery comes from the route's whole-project checkpoint, as for the verified `change.hardware_move`. |
| `change.hardware_address_set` | 6 | same two records | Input start-address change with compile, preservation, fresh reopen and its own checkpoint recovery. |
| `sim_select` | 4 | `local.sim.creation.20261024`, `local.sim.state.20261024` | Refused to a second Host until the creator exits, adoption after verified exit, OFF confirmed through a fresh interface. |
| `sim_test` | 3 | `local.sim.state.20261024`, `local.sim.provision.20261024` | Stimulus scenario asserted on an owned CPU in RUN, in both runs. |

The hardware and simulator rows have a new first limitation that names the
qualified profile. The four upload/stage rows lose the 17 October "historical
profile" limitation, because current evidence now qualifies them, as for
`artifact_upload_status`.

`change.hardware_plug` is the one judgement call. The run recovers the address
change and the disconnect, not a failed plug. It closes on the same
whole-project checkpoint basis that catalog `0.4.9` accepted for
`change.hardware_move` in the same run.

## Retained evidence that closes nothing

- `local.explorer.native-providers.20261026` has no catalog record. Its runs list
  no passed tools (the runner records no tool calls), so even with a record it
  cannot verify a row. Recurate it with tool calls, or rerun with recording,
  before citing it from `project_model_relationships`.
- `local.hmi.external-tag-prepare.20261026` is prepare-only and outside the HMI
  acceptance gates.
- `local.lifecycle.file-storage.20261024`, `local.runtime.card-and-lock.20261024`
  and `local.runtime.requested-stop.20261024` are runner lifecycle evidence with
  no matching catalog row.
- `local.library_project_state.20261024` and `local.library_vendor.20261024` are
  cited by no row. They belong to the library rows held below.

## Closest to closable, by owner

Each item names the native run it still needs. Owners without an item have no
retained summary for any of their rows, so each of their rows needs its first
native qualification.

- **X10 Hardware and remote I/O.** `change.hardware_add` and
  `change.network_configure` run in the remote-I/O and explorer-provider
  fixtures only as prerequisites (remote I/O adds them with `compile=false`). They need `qualify_remote_io.py`
  gates that compile, preserve, reopen and recover them.
  `change.hardware_delete` and `change.hardware_attributes` need the same gates.
  `hardware_inspect` is used as the oracle in those runs and still needs an
  independent comparison of its own. Closed on 28 October for the six hardware kinds by
  [their own runner](engineering-kinds-20261028.md).
- **L03 Runtime.** `simulation_provision_scratch_credential` is held on
  AC-preserve: project preservation after provisioning is not independently
  verified. It needs a provisioning run that compares project fingerprints
  before and after.
- **L04 Explorer.** `project_model_capture`, `_children`, `_search`, `_details`,
  `_verify`, `_export` and `project_index` pass the small default fixture
  (`local.explorer.20261024`, `local.explorer.relationship-scope.20261024`). The
  E1 acceptance in the [explorer plan](../plans/project-explorer-plan.md) needs a
  native `qualify_project_explorer.py` run with two PLCs, software units,
  nested/empty groups, namespace collisions and budget exhaustion.
  `project_model_relationships` also needs `qualify_project_explorer_relationships.py
  --project <fixture> --require technologyConnections softwareUnitRelations`.
  Closed on 28 October: [explorer E1](explorer-e1-20261028.md); `project_index` by
  [read contracts](read-contracts-20261028.md).
- **X02/L02 Libraries.** `library_list`, `library_query`, `library_close`,
  `library_open` and the `change.library_*` rows already cite retained, passing
  runs. They stay open for protected objects, software-unit relations and the
  scale run in progress
  ([library TODO](../plans/library-management-todo.md)).
  `artifact_upload_abort` needs only a Host-only intake run that calls abort;
  no TIA is required.
- **X11/L01 Typed.** `change.graphical_typed_edit` cites 18 passing records. It
  stays open for candidate lineage, unrelated-content preservation, persistence
  and recovery ([typed TODO](../plans/typed-lad-fbd-todo.md)).
- **X01/F03 Platform and recovery.** `tia_create_scratch`, `change_prepare`,
  `change_apply`, `change_status`, `change_restore`, `software_inspect` and
  `engineering_read` answer in almost every run. Catalog `0.4.4` deliberately did
  not promote them from incidental use. They need a run that asserts each tool's
  own identity and completeness contract. `target_capabilities` and
  `object_resolve` have no current run. `engineering_operation_status` needs the
  watch-table Bool ModifyValue lifecycle run. Closed on 28 October except
  `engineering_operation_status`: [read contracts](read-contracts-20261028.md).
- **D13 Technology and D14 Safety.** `technology_read`, `change.to_*`,
  `safety_inspect` and `change.safety_import_xml` cite only unretained
  8 October records. They need fresh fixture runs with technology objects and a
  safety CPU. `technology_read` and the six `change.to_*` kinds closed on 28 October
  ([record](engineering-kinds-20261028.md)); safety stays open.
