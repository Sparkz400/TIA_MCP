# Catalog requalification with committed evidence — 24 October 2026

Catalog `0.4.4` verifies 35 of the 36 features demoted in `0.4.2` again. Each
one now cites a new evidence record whose curated summary is committed under
[`evidence/`](../../evidence/README.md), and the contract check confirms that
every bound MCP tool answered without error in a completed run. The runs used
TIA Portal V21 and the local simulator on this PC (`PCNico`), with the Debug
Host and workers built from this branch.

`deployment.vm_qualification` stays `unverified`: its proof needs the VMware
guest, and no VMware Workstation is installed on this PC.

`simulation_download` first failed here: a `validate_milestones.py` run powers on
the downloaded protected program without provisioning its runtime credential, and
`powerOn` timed out (PLCSIM -11). It is now verified by the end-to-end
provisioning run (`local.sim.provision.20261024`), which became possible once
provisioning used a fresh discovery observer
([integration](plcsim-startup-discovery-20261024.md#production-integration)).

## Evidence format

`scripts/curate_evidence.py` writes `evidence/<evidence-id>.json`
(`tia-mcp.evidence-summary.v1`) from the local raw reports of one run: the source
commit and whether `src/` or `scripts/` were dirty, a hash over each binary tree
that ran, each raw report's SHA-256 and size, completion, step names, and the MCP
tools that answered with and without error. Raw reports, native exports and
project files stay in the ignored `artifacts/` tree. They can contain local
paths, process IDs and project content; the hashes tie the summary to them.

`claim_errors` in `scripts/test_contracts.py` now also requires, for each
`verified` feature, a passed summary for every cited record and every bound tool
in that summary's passed tools. The shared smoke client (and the reliability
client) record tool calls, and the runners below copy them into their reports.

## Runs

| Evidence record | Runner | Features verified |
| --- | --- | --- |
| `local.smoke.20261024` | `smoke_mcp.py --scratch` | `tia_discover`, `tia_capabilities`, `tia_start`, `tia_detach`, `project_inventory`, `xref_query`, `block_read` |
| `local.milestones.20261024` | `validate_milestones.py --skip-simulation` | `block_interface`, `call_graph`, `change_impact`, `symbol_trace`, `fixture_sources`, `artifact_read`, `type_read`, `block_read` |
| `local.hardware.20261024` | `smoke_hardware_to.py --skip-technology` | `tag_table_read`, `hardware_catalog_search`, `type_read` |
| `local.reopen.20261024` | `smoke_hardware_to.py --project` (saved hardware scratch) | `project_open` |
| `local.reliability.20261024` | `qualify_reliability.py --live` (all eleven fake-worker modes) | `tia_attach`, `tia_session_status`, `project_retrieve` |
| `local.reviews.20261024` | `qualify_project_reviews.py` | `project_snapshot_compare`, `project_snapshot_search`, `project_health_report` |
| `local.explorer.20261024` | `qualify_project_explorer.py` | `project_model_release` |
| `local.sim.creation.20261024` | `qualify_simulator_creation.py` | `sim_status`, `sim_create`, `sim_release`, `sim_creation_status` |
| `local.library_intake.20261024` | `qualify_library_intake.py` (Host only) | `artifact_upload_status`, `artifact_upload_release`, `library_package_inspect`, `library_package_status`, `library_package_release` |
| `local.catalog.20261024` | `verify_feature_catalog.py` (Host only) | `feature_catalog` |
| `local.sim.provision.20261024` | `validate_simulation.py --provision-runtime-credential` | `simulation_download` |

Each record's scope and limitations are in the catalog and in its summary. All
runs used new repository scratch projects or the saved scratch from an earlier
run; none is evidence for customer projects, other CPUs, protection states or
large-scale completeness. Library intake and the catalog are Host-only
features, so their runs involve no Siemens worker; their limitations say so.
The earlier evidence records stay in the catalog as history, uncited.

## Fixes found on the way

- `HostOptions.FindRoot` looked for `docs/PLAN.md`, which this checkout does not
  have, so a source-built Host resolved its workers and artifacts under `bin/`:
  `WorkerNotFound` without `--worker`, and `ProjectPathTooLong` for scratch
  projects. It now looks for `TiaMcp.slnx`; a library test covers it.
- `validate_milestones.py` and `smoke_hardware_to.py` accept `--worker` and
  `--artifacts`. `qualify_project_reviews.py` adds its own `rv_<id>` folder, so its
  artifact root must stay short (`artifacts/v` here).
- The `qualify_reliability.py` test-double worker predated the Host's
  `mutation_begin`/`mutation_dispatch`/`mutation_end` admission, so ten of its
  eleven failure-boundary modes failed. It now returns a bounded workspace scope
  and runs the admitted write on dispatch; the harness counts dispatches as
  native writes. All eleven pass, alongside the native part.

## What stays open

- `deployment.vm_qualification`: needs the VMware guest.
- One `outcomeUnknown` provisioning admission from a failed attempt is still held
  (`artifacts/engineering-admissions/9547a9a83cfc42b388fbcab53c1204cb`); trusted
  reconciliation does not exist yet.
- Native evidence applies to the recorded binary tree hashes. The Host embeds
  the catalog, so the Host built after this catalog change has a different hash
  from the one that ran; later binaries need their own runs.
- Features outside the 36 that these runs also exercised (for example
  `change_prepare`, `software_inspect`, `project_model_capture`) were not promoted.
