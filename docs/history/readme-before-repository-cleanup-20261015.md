# README snapshot before repository cleanup — 15 October 2026

This preserves the previous README and its dated verification history. Follow the
[root README](../../README.md) for setup and [current status](../STATUS.md) for
the latest scope. Links into `artifacts/` refer to local evidence that is not
distributed in Git. Commands below retain their original repository-root context.

---

# TIA MCP — V21 engineering and simulation prototype (v0.4)

[Documentation](../README.md) · [Current status](../STATUS.md) · [Remaining TODO](../TODO.md)

A Windows MCP server for inspecting, tracing and changing offline TIA Portal V21 projects, with a separate PLCSIM Advanced V8 runtime adapter. The .NET 10 MCP host talks to isolated .NET Framework 4.8 workers through current-user-only named pipes. Siemens assemblies resolve from the installed products and are never redistributed.

The [9 October continuation](../history/continuation-20261009.md) records the new persistent review tools, simulator lifecycle and timing fixes, HMI catalog correction, pinned package and exact current qualification scope. Existing VM proof is accepted for this continuation.

The v0.4 increment adds persistent project capture, comparison, search and health reports, plus durable simulator creation-status lookup. The existing support contract, target capability discovery, shared identities and conformance projects remain available. The feature catalog distinguishes callable operations, partial implementations and explicit backlog entries; a catalog entry does not establish support on every target. See the [engineering coverage expansion](../reference/engineering-coverage.md) for reusable edits and their qualification limits.

See [historical v0.3 evidence](../history/generalization-verification.md), [verification history](../history/verification.md), [engineering workflows](../guides/engineering-workflows.md), [simulation](../guides/simulation.md), and [deployment](../guides/deployment.md). The larger product direction remains in the [implementation plan](../plans/overview.md).

[Selected engineering-gap qualification](../verification/engineering-gap-qualification.md) records the safety fingerprint correction and exact native compilation, checkpoint recovery, and saved-project reopen checks, with retained exports and binary identities.

The [reusable block-testing executor](../guides/block-testing.md) prepares isolated FC/FB test programs and connects sequences/assertions to the existing simulator tools. Run `python scripts/run_block_tests.py` to generate the nine-family corpus; `--engineering-only --worker <executable>` imports, compiles, exports and checks save/reopen without PLCSIM. `--live --worker <executable> --sim-worker <executable>` adds runtime qualification. Native runs record the selected binary hashes. Individual DUT calls use a request/acknowledgment handshake, with explicit limits on timing and scan synchronization. The protected runtime baseline passes; the nine-family corpus remains unqualified. See the [runtime-proof TODO](../plans/runtime-proof-todo.md) for recorded failures and remaining work.

| Area | Tools and supported scope |
| --- | --- |
| Feature contracts and identities | `feature_catalog` queries versioned contracts without starting TIA; `object_resolve` selects an exact native target; `target_capabilities` observes that target's services and metadata |
| Sessions/projects | `tia_discover`, `tia_attach`, `tia_start`, `tia_session_status`, `tia_detach`, `project_open`, `project_retrieve`, `tia_create_scratch`, `tia_capabilities` |
| Inspection | `project_inventory`, `software_inspect`, `block_read`, `block_interface`, `type_read`, `tag_table_read`, `hardware_inspect`, `technology_read`, `safety_inspect` |
| Project explorer | `project_model` and eight named query/export tools; shared exact references, bounded native hierarchy/relationships, a virtualized read-only viewer, native HMI details and typed previews. [Run and coverage](../guides/project-explorer.md), [qualification](../verification/project-explorer-qualification.md). |
| Persistent reviews | `project_snapshot_capture`, `project_snapshot_compare`, `project_snapshot_search`, `project_health_report`; saved native exports, explicit coverage, direct dependency impacts and offline search. [Usage and limits](../guides/project-reviews.md). |
| HMI integration | `hmi_capabilities`, `hmi_inspect`, `hmi_references`; exact HMI v2 identities and shared explorer providers. See [profile-specific scope and qualification](../guides/hmi-integration.md). |
| Tracing | `xref_query`, `call_graph`, `symbol_trace`, `change_impact`: native references, static callers/readers/writers, declared instances and configured I/O associations with unresolved coverage |
| Recoverable engineering | `change_prepare`, `change_apply`, `change_status`, `change_restore`, `engineering_read`, `fixture_sources`, `hardware_catalog_search` |
| Simulation | `sim_status`, `sim_create`, `sim_creation_status`, `sim_select`, `simulation_download`, `simulation_provision_scratch_credential`, `sim_state`, `sim_tags`, `sim_read`, `sim_write`, `sim_test`, `sim_release` |
| Artifact transfer | `artifact_read`: bounded base64 chunks and SHA-256 for exports/reports on the local or remote TIA machine |

Inspection includes exposed software/safety units, generated system groups, tags/constants, UDTs, DB/interface members, hardware attributes/channels, technology parameters/connections and safety metadata. Native XML is retained. Original exports do not imply full semantic understanding; indirect calls, pointers, inaccessible objects and missing services remain explicit gaps. Offline DB start values and simulated current values have separate origins.

Use `feature_catalog` for the bridge's implementation contract and recorded fixture evidence, then `object_resolve` and `target_capabilities` for the current target. API availability, bridge implementation, observed qualification and target blockers are separate facts. Native attribute write access never authorizes a generic write. See [capability discovery](../reference/capabilities.md), [object identities and scope resolution](../reference/object-model.md), and the [machine-readable contracts](../../contracts/README.md).

The change workflow supports standard SCL/STL block sources, SimaticML import, block/UDT rename and dependency-checked deletion, individual tag/constant edits, user groups, targeted LAD/FBD native network patches/insertion and FC/FB argument edits. The [typed LAD/FBD builders](../guides/typed-lad-fbd-builders.md) add versioned language-specific graphs, exact scalar resolution, native instruction profiles, explicit instance/dependent declaration effects and read-only network data through the same workflow. Their five native/runtime qualification gates remain separate and target-specific. Hardware routes include catalog-validated creation, eligible module deletion, selected attributes and I/O address conflict checks. TO creation can configure initial parameters before compilation. Dedicated safety XML import remains subject to native eligibility. See the workflow document for exact payloads and limits. Protection boundaries and native language are preserved; unrestricted graphical generation and arbitrary vendor/safety writes remain outside the implemented scope.

Changes follow prepare → preview/hash → checkpoint/archive → apply → compile → verify/journal. Applying requires explicit `checkpointSave:true`, which saves the current in-memory project before Siemens can archive it. A change is not a transaction across all Siemens calls. Partial failures retain the recovery archive and journal; writes are never automatically replayed. Recovery retrieves an archive into a new directory in a worker-owned TIA instance. Read persisted journals after a restart before choosing further action.

The simulation download uses an offline memory-card image for an independently verified, adapter-owned PLCSIM instance. No physical PLC download, force, runtime write or CPU control route is exposed. Standard PLCSIM V21 is detected but is not the implemented public runtime API; the runtime adapter uses Advanced V8. A compatible CPU/program and usable Siemens license are required.

The generated corpus has passed **14 live engineering scenarios and 73 steps** on TIA V21 / CPU 1511-1 PN V4.0, covering all four standard languages, nested UDT/array declarations, calls, multi-instances, native IEC timers/counters and bounded graphical preservation. That engineering run did not execute its expected runtime vectors. The installer-required Windows restart is confirmed at 11:08:12 Europe/Berlin on 8 October 2026. A fresh empty CPU again reached STOP with `PowerOn: OK`; the unchanged protected scratch program still returned `WarningPasswordForProtectionError`. The opt-in scratch credential provisioning route is implemented. Attempts thirteen and fourteen rejected `certificate not matching` before opening the credential, with successful Offline/OFF cleanup and unchanged protected-card restoration. Fresh CPUs presented different certificate fingerprints. After explicit user authorization for trust scoped to each owned empty CPU, attempt fifteen passed credential provisioning, unchanged card restoration, protected RUN, typed read/write/readback and all eight baseline assertions, with successful cleanup. **Attempt seventeen reconfirmed all baseline gates** on the recorded isolated build; its [evidence snapshot](../../artifacts/runtime-proof-20261008/evidence-snapshot-20261008T113602982412Z.json) and [binary manifest](../../artifacts/runtime-proof-20261008/provisioned-17-binaries.json) identify that proof. The nine-family runtime corpus remains unqualified: project locks, provisioning/restoration/isolation failures, native timeouts, a disconnected engineering worker, a report-persistence failure and one elapsed-time assertion failure are recorded. See the [runtime-proof TODO](../plans/runtime-proof-todo.md). Earlier attempt eleven's crash phase remains unknown. See [post-restart evidence](../guides/simulation.md#post-restart-check-and-credential-investigation--8-october-2026). Existing VM offline-engineering, SSH and recovery proof is accepted for this continuation; repeating the VM run is not a delivery gate. See [deployment reliability evidence](../verification/deployment-reliability-status.md). Parsed interfaces now retain sparse individual array-element start values alongside original native XML; symbolic bounds and unresolved element paths remain explicit. These limits remain visible in the catalog and evidence ledger.

## Build and validate

Use the normal Windows engineering user with TIA V21 Openness, PLCSIM Advanced V8, .NET 10 SDK and .NET Framework 4.8 reference assemblies installed:

```powershell
.\scripts\Probe-Environment.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1 -SkipBuild
```

NuGet uses repository-local `.packages` and nuget.org. `TiaApiDirectory` and `SimulatorApiDirectory` are MSBuild properties for non-default vendor installations. `Build.ps1` serializes MSBuild and checks compiled IL for the destructive Siemens process-disposal mistake.

```powershell
# Discovery only; no project writes.
python .\scripts\smoke_mcp.py

# Creates and changes a separate test project and owned simulation instance.
python .\scripts\validate_milestones.py --cpu-type-identifier 'OrderNumber:6ES7 511-1AL03-0AB0/V4.0'

# Additional isolated hardware/TO and native safety fixtures.
python .\scripts\smoke_hardware_to.py
python .\scripts\smoke_safety_direct.py --help

# Deterministic fixtures and dry-run validation; neither command starts TIA.
python .\scripts\generate_conformance.py
python .\scripts\run_conformance.py

# Explicit native engineering qualification in a fresh owned scratch project.
python .\scripts\run_conformance.py --live --phase engineering

# Build a portable framework-dependent bundle with a file hash manifest.
.\scripts\Package.ps1
```

Reports, native exports, checkpoints and scratch projects stay under ignored `artifacts/`. They may contain project source and identifiers. Live tests use separate scratch projects; the user's engineering project is not their mutation target.

`Test.ps1` includes feature-contract validation, the Python conformance and runtime-harness suites, C# bridge/model/capability checks, **27 simulator validation tests**, deployment checks and the compiled process-lifetime guard. Pure checks do not qualify live behavior. The [fixture guide](../guides/conformance-fixtures.md) documents independent expected vectors, exact manifest snapshots, subset/variant generation, failure recovery and the distinction between dry-run, engineering and runtime results.

The V4.0 CPU selection above matches the installed Advanced V8 firmware support. Offline hardware/TO fixtures also passed on V4.1. Use `validate_simulation.py --help` to resume simulation on an existing saved scratch program with its explicit protected-credential metadata and recovery journals. Its `--host`, `--worker` and `--sim-worker` flags select an isolated build; `--provision-runtime-credential` opts into the generated-credential provisioning route. The block runner accepts that provisioning flag only with `--live`. The additional `--trust-owned-empty-cpu-certificate` flag requires provisioning and explicit first-use trust authorization; it defaults false and pins only within each attempt. `validate_recovery.py` verifies archive recovery after a host restart; `smoke_package.py` verifies a generated bundle's manifest and actual launcher.

## Launch

[Library management](../guides/library-management.md) adds resumable ZIP/native-library upload, bounded package inspection, project/global library browsing, exact native version selection, prepared imports and resource recovery. See [Classic/Unified support](../reference/library-hmi-support.md) for the separate HMI routes and qualification limits. Host storage limits can be configured with `--library-limits-file`.

The [read-only project explorer](../guides/project-explorer.md) provides a searchable virtualized hierarchy, details, evidence-backed relationships, exact agent selections and portable HTML/JSON exports. Open `ui/project-explorer/index.html` to load a snapshot, or use `project_model_capture` and `project_model_export` through the MCP host. Negotiated MCP Apps clients receive the same viewer as a sandboxed UI resource. See [qualification and remaining gates](../verification/project-explorer-qualification.md).

For development, configure an MCP stdio client with:

```text
Command: D:\03_Entwicklung\TIA_MCP\src\TiaMcp.Host\bin\Debug\net10.0-windows\TiaMcp.Host.exe
Arguments: --timeout-seconds 300
```

Use the actual checkout location. Optional arguments are `--worker`, `--sim-worker`, `--api-dir`, `--sim-api-dir`, `--artifacts`, `--timeout-seconds` and `--diagnose`. `--diagnose` checks engineering worker metadata/discovery and exits; omit it for MCP. All logs go to stderr; stdout is reserved for protocol messages.

For a package, use its `scripts/Start-TiaMcp.ps1` launcher and generated external `<bundle>.mcp.json` configuration beside the immutable bundle. For a Windows VM or another PC, deploy the package beside TIA there and generate an authenticated SSH stdio configuration. Project/artifact paths belong to that machine. See [deployment instructions](../guides/deployment.md); the existing VM proof is accepted for this continuation, as requested.

The restricted execution sandbox can reject Windows named-pipe or Siemens authorization even when group SIDs exist. Run workers in the authorized engineering user's ordinary session; do not weaken pipe ACLs. An interrupted bridge call may still be running in Siemens software; the session faults and refuses more calls until the host is restarted.

`TiaPortal.Dispose()` releases an Openness connection; **`TiaPortalProcess.Dispose()` closes TIA itself**. Raw process proxies are confined to `PortalProcesses`, whose compiled-code guard rejects disposal and escaping descriptors. Never wrap discovered process proxies in `using`. Detaching from a user-owned process releases only this worker's attachment.
