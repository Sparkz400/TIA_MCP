# Changelog

Notable changes to TIA MCP, in the style of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 any release may change the
tool surface. The product version is set once in [`Directory.Build.props`](Directory.Build.props);
the feature catalog keeps its own data contract version. Dated detail and evidence are in the
[status log](docs/history/status-log.md); [STATUS](docs/STATUS.md) has the current counts, and the
[tool reference](docs/reference/tools.md) gives each tool's support class.

## [Unreleased]

Changes since the repository went public on 23 October 2026, up to feature catalog `0.4.86`.

### Added

- **Versioning and releases:** product version `0.9.0` for every assembly, the npm metadata,
  `serverInfo.version` and release bundles; a tag-triggered workflow drafts a GitHub release with
  the verified offline bundle ([releasing](docs/guides/releasing.md)).
- **MCP surface:** `--toolsets` tool groups, a title on every tool, four workflow prompts
  (`safe_change`, `project_health`, `block_test`, `diagnose_fault`), progress heartbeats, resource
  templates (artifacts, saved reviews, feature catalog), completions, client logging and output
  schemas for 85 tools ([MCP surface](docs/reference/mcp-surface.md)).
- **Engineering changes** through the prepare/apply/restore workflow: hardware families and CPU
  parameters, device and project compile, tag tables, software units (rename and create from a unit
  master copy), motion connections, PLC documents, project copy
  and compare, connections, alarms, GRAPH, CAx import and export, OPC UA server interfaces and role
  mappings, web server and certificates, safety F-parameters and F-block import, block protection
  and the CPU protection level through a credential route, watch and force tables, library maintenance and combined PLC library
  integration, and further typed LAD/FBD families including cross-unit calls.
- **Simulation and online:** online download to the owned PLCSIM instance, batch signal reads and
  writes, byte and bit area access and bounded state observation (`sim_read_signals`,
  `sim_write_signals`, `sim_area_read`, `sim_area_write`, `sim_observe`), virtual time, event
  injection (OB40, OB82, OB83, OB86), memory reset and storage archives, DB snapshots,
  `plc_online_compare` and `plc_station_upload`, runtime OPC UA and Web API against PLCSIM over
  TCP/IP, and read-only live monitoring of watch and force tables (`watch_table_monitor`).
- **Test Suite:** application test runs on an owned PLCSIM instance (TS-Q02 qualification mode).
- **HMI:** Unified text-list authoring and generation whose candidates import exactly through V21;
  Unified and Classic HMI read and authoring routes.
- **Add-In companion:** a TIA Add-In that hands a project-tree selection to an enrolled Host.
- **Explorer:** technology, software-unit, library and port providers.

### Changed

- `serverInfo.version` reports the product version; the catalog version moved to
  `capabilities.experimental["tiaMcp/featureCatalog"].version`.
- Bundles from `scripts/Package.ps1` carry the product version in their name and `manifest.json`.
- Combined PLC library apply is on by default for the natively verified recipe shapes;
  `--enable-library-integrate-apply` is still accepted and changes nothing.
- Production Hosts dispatch the qualified force-table document edits.
- `plc_station_upload` can trust an owned PLCSIM instance's TLS (`trustOwnedSimulationCertificate`,
  off by default); `safety_import_xml` compiles every device; the device compile lists devices
  without a compile service as not compiled instead of refusing.

### Fixed

- Defects found in native runs across text-list import (V21 silently altered some content; those
  inputs are now refused), DB snapshots, OPC UA role mappings, TCP/IP provisioning, the Add-In
  sandbox, project translations and the read tools; each is recorded in the status log.
- Code audit of 31 October ([status log](docs/history/status-log.md#31-october-2026)):
  - `sim_write`, `sim_state` and `sim_test` with writes need an owned instance; `sim_state` and
    `sim_release` without `deleteOwned` refuse while a capture holds the instance, and a release
    retries a failed capture-mode restore.
  - Runtime writes, RUN/STOP and Test Suite system tests need Windows to route the endpoint through
    the PLCSIM virtual adapter (`RuntimeTargetRouteUnverified`, `RuntimeTargetNotOwnedPlcsim`).
  - Cancelling a `sim_*` call no longer breaks the simulator bridge; waits that cannot end inside
    `--timeout-seconds` are refused.
  - `library_package_stage` `hostPath` accepts only library archives and library folders.
  - `translation_native_capture` uploads workbooks over 64 KiB in chunks.
  - `db_snapshot_create` detects an unchanged snapshot by content; translation reopen no longer
    grants scratch rights; generated-source reads take exclusive access.
  - Change prepare and apply refuse while another change on the same project needs recovery;
    `block_delete`, `block_rename`, `to_delete` and `hardware_delete` need `compile`.
  - A missing or renamed old project path no longer blocks every change; store locks wait up to
    5 s; finished records archive past 1,000 (Hosts older than this change refuse new admissions
    on a store with an `.archive` folder); a clock step back no longer fails a dispatch.
  - Release tooling: blocked rows need a disposition, verified features cannot rest on lost raw
    reports only, the Explorer relationship checks run in CI, and a 1.x release tag is gated.

### Known limitations

- Pre-1.0. Use it only with offline projects, copies, scratch projects and simulators you own,
  never with production PLCs or plants.
- 437 of 469 catalog features are verified; 108 release-matrix cells are open
  ([STATUS](docs/STATUS.md), [TODO](docs/TODO.md)).
- V21 boundaries: no public API forces a value, Startdrive cannot read the number of a drive added
  through Openness, an OPC UA reference-namespace import crashes TIA, and V21 refuses the CPU protection
  level while UMAC runtime access is enabled (its password has a credential route).
- Release assets are the offline bundle only; the engineering bundle with native workers is built
  on a TIA V21 workstation. Bundle manifests are hash lists, not publisher signatures.
