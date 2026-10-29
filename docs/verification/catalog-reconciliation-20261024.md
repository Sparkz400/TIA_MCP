# Feature catalog reconciliation — 24 October 2026

The feature catalog, the catalog the Host embeds, the registered MCP tools,
the prepared mutation kinds and the capability documentation now agree with the
current source and with the evidence this repository actually retains. Catalog
version `0.4.2` claims no `verified` qualification: none of the cited evidence
records has its artifacts in the repository. This is an offline file and source
reconciliation. No TIA Portal, PLCSIM or native worker was started, and no
qualification result was promoted or rerun.

## What was compared

- `contracts/feature-catalog.v1.json` (336 features, 45 evidence records, 10
  acceptance criteria).
- The embedded catalog. `src/TiaMcp.Core/TiaMcp.Core.csproj` embeds the same
  file as `TiaMcp.FeatureCatalog.v1.json`, so source and embedded content agree
  by construction. `FeatureCatalog.LoadBundled` validates it at Host start, and
  `scripts/verify_feature_catalog.py` and the package smoke compare the served
  rows with the external contract.
- All 262 `McpServerTool` registrations in `src/TiaMcp.Host` and all 56 prepared
  mutation kinds against catalog bindings (`scripts/test_contracts.py`). No
  missing, extra or duplicate binding was found.
- Every evidence artifact path, checked against `git ls-files`, this worktree,
  and the ignored `artifacts/` folders of the public and original checkouts on
  this PC. Only `local.sim.startup.20261024` has its artifacts on this machine,
  in the public checkout's ignored `artifacts/p24-simstate04/`. No record has its
  artifacts in the repository. The bindings registry keeps each artifact's
  originally observed SHA-256, so lineage remains, but the evidence does not.
- Catalog wording, the capability dimension table in
  [target capability discovery](../reference/capabilities.md) and the current
  status pages.

## Mismatches fixed

| Mismatch | Change |
| --- | --- |
| 36 features claimed `verified` from evidence whose artifacts are not retained in the repository: `tia_discover`, `tia_capabilities`, `tia_attach`, `tia_session_status`, `tia_start`, `tia_detach`, `project_open`, `project_retrieve`, `project_inventory`, `block_read`, `block_interface`, `type_read`, `tag_table_read`, `xref_query`, `call_graph`, `symbol_trace`, `change_impact`, `hardware_catalog_search`, `fixture_sources`, `simulation_download`, `sim_status`, `sim_create`, `sim_release`, `artifact_read`, `feature_catalog`, `deployment.vm_qualification`, `artifact_upload_status`, `artifact_upload_release`, `library_package_inspect`, `library_package_status`, `library_package_release`, `project_model_release`, `sim_creation_status`, `project_snapshot_compare`, `project_snapshot_search`, `project_health_report`. | Now `unverified`. Their evidence IDs stay cited as history, and one availability reason states why. Evidence records, their outcomes and limitations are unchanged. |
| `local.library_intake.20261008` and `local.catalog.v03` are host-only offline runs (`nativeCodeExecuted: false`, absent native workers), yet they qualified six features alongside native records. | Covered by the change above: offline validation no longer appears as qualification. |
| `hmi_capabilities` reported availability `available` while unverified, against the dimension rule that `available` needs all required evidence. Its reason repeated an implementation limitation. | Now `conditional`, with a reason that names the per-call conditions. |
| `runtime.physical_plc` described its OPC UA client as "qualified against local servers only" without any evidence ID. | Reworded to "exercised against local servers only; no catalog qualification evidence is recorded". |

`catalogVersion` moves from `0.4.1` to `0.4.2`. The bindings registry was
regenerated for the new catalog hash; its `evidenceInputs` section is unchanged.

## New check

`claim_errors` in `scripts/test_contracts.py` now fails the contract check when a
`verified` feature cites an evidence artifact that is missing or inside the
ignored `artifacts/` or `output/` trees, or when `available` is claimed without
`verified`. Applied to the previous catalog it reports all 37 rows above. Unit
tests cover retained, ignored and missing artifacts, historical evidence under
`unverified`, and the bundled catalog. The managed catalog validation test now
sets `verified` explicitly for its verified-only corruption cases, because the
bundled catalog no longer contains one.

## Checks

- `python -B scripts/test_contracts.py`: passes (262 tools, 56 mutation kinds,
  336 features).
- `python -B scripts/api_coverage_bindings.py validate contracts/api-coverage/implementation-bindings.v1.json --allow-missing-evidence`:
  valid; every catalog evidence artifact is reported unavailable in this worktree.
- `python -B -m unittest discover -s scripts -p "test_*.py"`: 1,967 tests pass,
  one existing skip.
- `dotnet run --project tests/TiaMcp.Tests -c Debug`: 247/247 passed, with no TIA API
  calls.

## What stays open

- Requalification. A feature returns to `verified` only when its evidence
  artifacts are retained in the repository (or an approved evidence store the
  check can read) with exact binary and profile lineage. The accepted VM proof
  behind `deployment.vm_qualification` is in the same position; its acceptance
  is unchanged, but its artifacts are not here.
- Six evidence records are cited by no feature (`vm.offline.licensed.20261008`,
  `vm.deployment.20261008`, `local.layers.calls.20261008`,
  `local.layers.encoder-dependencies.20261008`,
  `local.layers.safety-comment.20261008`,
  `local.layers.safety-fingerprint-fix.20261008`). They remain as historical
  outcomes.
- Many implementation limitations still describe scoped historical native passes
  in prose. They are statements under `unverified`, not qualification, and were
  left as written.
- The dated "Public surface" row in [STATUS](../STATUS.md) (95 tools, 168
  features, 15 October) is historical and was not rewritten. Use the actual tool
  list and `counts` in the bindings registry for current numbers.
- The 1.0 checklist's declared per-cell support matrix
  ([release checklist](../history/1.0-release-checklist-20261013.md)) is still
  unwritten. This reconciliation covers the catalog and the capability reference only.
- PLCSIM provisioning tools may change in a parallel lane. This change touched
  only existing catalog entries: the 36 listed above, `hmi_capabilities` and
  `runtime.physical_plc`.
