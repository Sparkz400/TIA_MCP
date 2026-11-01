# Changelog

Notable changes to TIA MCP, in the style of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 any release may change the
tool surface. The product version is set once in [`Directory.Build.props`](Directory.Build.props);
the feature catalog keeps its own data contract version. Dated detail and evidence are in the
[status log](docs/history/status-log.md); [STATUS](docs/STATUS.md) has the current counts, and the
[tool reference](docs/reference/tools.md) gives each tool's support class.

## [Unreleased]

Changes since the repository went public on 23 October 2026, up to feature catalog `0.4.92`.

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
- **Online device identification:** `device_online_browse` and `device_identify` are qualified on an
  owned PLCSIM instance over TCP/IP: they equal pinned `Modules.Browse` and I&M0 reads of the CPU, its
  PROFINET interface and both ports. The operator trusts the instance's web certificate for the run, and
  the session token comes from an `Api.Login` passed as a `TIA_INVENTORY_<target>` record
  (`local.device_inventory.online.20261101`).
- **HMI:** Unified text-list authoring and generation whose candidates import exactly through V21;
  Unified and Classic HMI read and authoring routes.
- **Add-In companion:** a TIA Add-In that hands a project-tree selection to an enrolled Host.
- **Explorer:** technology, software-unit, library and port providers. The desktop roundtrip passed in
  VS Code 1.144.0 through MCP Apps: an exact selection answered by `project_model_details`, refusal
  after `tia_detach` and the artifact fallback (`local.explorer.desktop.20261101`).
- **HMI compile:** `change.hmi_compile` compiles fixtures an operator configured in the editor (start
  screen, Unified transfer password): TP700 Comfort and MTP700 Unified Comfort, two targets each, with
  zero errors and warnings, a fresh-Host reopen and checkpoint restore (`local.hmi.compile.20261101`).
- **Project upgrade:** `project_upgrade` upgrades a V14–V20 project folder (`.ap14`–`.ap20`,
  `.ap15_1`) or archive (`.zap14`–`.zap20`) to V21 on an owned copy; the source is never opened,
  its hash is checked before and after, and V13 and older are refused before any native call
  (`local.project_upgrade.20261031`). A half-written upgrade is held without being opened, and Host
  loss after the native reply reconciles `verifiedApplied`
  (`local.project_upgrade.interruption.20261101`, 16/16).
- **OPC UA client:** `runtime_ua_call` calls a method component of an object in the PLC namespace
  (on an S7-1500, a user-program server method) on an owned PLCSIM instance behind the
  `runtime_ua_write` gate, never retried; `runtime_ua_conditions` takes an Alarms and Conditions
  snapshot with ConditionRefresh; `runtime_ua_history_probe` records the server's `HistoryRead`
  answer, which the product refuses (`UaHistoryReadUnsupported`); `opcua_server_settings_set` takes
  `alarmsAndConditions` (`local.runtime.plcsim-ua-services.20261031`).
- **Unified text-list import:** change kind `hmi_text_list_import` imports a retained
  `hmi_text_list_author` or `hmi_text_list_generate_set` candidate into a Unified Comfort or Unified
  PC HMI through `change_prepare`, `change_apply` and `change_restore`. The Host refuses content V21
  would change, also for older records; apply checkpoints, compiles, imports and requires the
  candidate lists to read back exactly and the other lists, the rest of the HMI and the compiler
  errors and warnings to stay unchanged before it saves (`local.hmi.text-list-import.route.20261031`).
- **VCI remote delivery:** `vci_push` publishes a committed or fetched revision to the one remote set
  by `--git-remote`, with compare-and-swap and no forced update; `vci_fetch` fast-forwards a revision
  to the remote branch after validating its tree; change kind `vci_conflict_resolve` resolves a
  two-sided VCI conflict from a reviewed three-way comparison (take native, remote or a reviewed
  merged document) with checkpoint, compile and exact readback (`local.vci_git.20261031`,
  `local.hostonly.vci.git.20261031`). `vci_workspace_inspect` reports each mapped object's
  `contentSha256`.
- **Hardware topology:** change kinds `network_mrp_set` (media redundancy role and MRP domain of a
  PROFINET interface, shown by `hardware_inspect` `network.mrp`) and `hardware_shared_device_access`
  (an IO-device module's access: one PLC of the station's I/O systems, or `OutsideOfProject` with an
  optional `externalIoControllers` count); `network_io_assign` takes `sharedDevice` with `sharedSubmodules`, and
  `hardware_replace` takes `acceptAddressChange` with `acknowledgedTags`
  (`local.hardware_changes.topology.20261101`, `local.hardware_changes.topology_restores.20261101`).
- **Capability profiles:** 13 module capability profiles in
  `contracts/hardware-catalog-profiles.v1.json`, linked from `hardware_catalog_search` and checked by
  attribute writes; technology version profiles in `contracts/technology-version-profiles.v1.json`
  (45 cells on three CPUs) that `to_create` admits or refuses by (`local.technology.version_profiles.20261031`).
- **Restricted translation profile:** qualification captures accept `categoryIds`, and the Host
  keeps exactly the requested categories of V21's leaking category export by row identity; a
  one-PLC `BlockCommentCategoryData` admission contract becomes final only after
  `build_translation_admission.py --finalize` adds the default-mode interruption packet
  (`local.translation.restricted.qualification.20261031`, `local.translation.restricted.native.20261031`).
- **PLC component shapes:** combined `library_integrate` apply also installs a LAD FC caller, an FB
  caller, an earlier placement in an existing FBD FC and reuse of an installed FB, each as an exact
  single-feature shape (`local.library_integrate.candidate_shapes.rerun.20261031`); combinations
  stay plan-only. `library_instantiate` sets an explicit `namespace` on a master copy of a block or
  PLC data type (`local.library_units.namespace.rerun.20261031`); a UDT that the instantiation of a
  namespaced FB creates alongside it keeps its type's empty namespace and compiles
  (`local.library_units.namespace.dependency.20261101`). An FB caller with a multi-instance
  and a reused component also verifies (`local.library_integrate.candidate_shapes.multi.20261101`);
  a caller FB with dependents (its own instance DB, embedding parents) is refused at
  `mutation_begin` with `DependencyProfileUnavailable` before any admission record
  (`local.library_integrate.candidate_shapes.dependents.rerun.20261101`).
- **GSD:** the I-device GSD export reran (`local.gsd_transfer_areas.20261101`). With the GSDML
  installed by an operator, the hardware runner's `--slice gsd` adds the device, puts it on the subnet and
  assigns it with compile and checkpoint restore (`local.hardware_changes.gsd.20261101`).

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
- New Host options `--git-remote <repo>` (the one bare remote of `vci_push`/`vci_fetch`; needs
  `--git-executable`) and `--git-allow-network-remote` (lets it be a credential-free `https://` URL);
  the qualification-only `--hmi-text-list-qualification <profile>` and, in builds made with
  `-p:SafetyFParameterDiscovery=true` only, `--safety-fparameter-discovery`
  ([Host options](docs/guides/deployment.md#host-options)).
- `safety_fparameter_set` writes only the F-parameters of the allowlist derived from a discovery run
  (77 over seven ET 200SP and ET 200MP F-module types, `local.safety.fparameter_discovery.20261031`);
  any other name is refused with `UnsupportedAttribute` and the module type, and written
  F-parameters join configured-hardware preservation.
- `tag_create`, `tag_set` (`LogicalAddress`) and `tag_table_import_xml` refuse an absolute input or
  output address in an F-I/O range at prepare with `SafetyBoundary` and write nothing; before, a tag
  created there became a safety tag and was refused only after the create.
- Combined `library_integrate` apply refuses before any claim when a block number the import would
  take is already used (`ComponentBlockNumberOccupied`) or a step checkpoint or recovery path would
  exceed TIA's 143 characters (`ComponentPathTooLong`); its steps run in a short workspace
  `changes/c/<8 hex>`, and a reused FB may differ from its source only in the version comment, which
  the preview records.
- `to_create` checks the CPU, type and version against the technology version profiles: a recorded
  refusal refuses with its native text (`TechnologyVersionRefused`), and an unprofiled pair needs
  `unprofiledAcknowledged` (`TechnologyVersionUnprofiled` otherwise).
- `safety.logic_changes` and `block.move` are closed as `unavailable_target`: V21's offline
  `DownloadProvider` refuses the PLCSIM card and the online route refuses at prepare without a card
  from this PLC; V21 has no block move API. The release matrix counts an unavailable API with an
  unsupported or blocked target as `unavailable_target`.
- `SimulationTargetMismatch` tells an owned instance with no successful card that it has none.
- The Unified HMI preservation capture also covers the complete text-list export, and OPC UA sessions
  also check that namespace 0 is the OPC UA namespace.

### Fixed

- Defects found in native runs across text-list import (V21 silently altered some content; those
  inputs are now refused), DB snapshots, OPC UA role mappings, TCP/IP provisioning, the Add-In
  sandbox, project translations and the read tools; each is recorded in the status log.
- Code audit of 31 October ([status log](docs/history/status-log.md#31-october-2026)):
  - `sim_write`, `sim_state` and `sim_test` with writes need an owned instance; `sim_state` and
    `sim_release` without `deleteOwned` refuse while a capture holds the instance, and a release
    retries a failed capture-mode restore.
  - Runtime writes, RUN/STOP and Test Suite system tests need Windows to route the endpoint through
    the PLCSIM virtual adapter (`RuntimeTargetRouteUnverified`, `RuntimeTargetNotOwnedPlcsim`). A Web
    or OPC UA session also needs that route from when it connected, so one opened to a physical PLC
    stays refused after the PLCSIM adapter becomes the best route.
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
  - Smaller findings, fixed on 1 November (offline):
    - Web API passwords are checked without a managed string copy, and the login body is cleared
      after the exchange.
    - A `File.Replace` that reports 0x498 finishes the rename instead of losing both journal versions.
    - Operation, admission and component-window revisions and VCI callback journals are renamed
      write-through; VCI callbacks are no longer written in place.
    - A crash during a new vault's first write no longer locks the vault; consumers refuse an
      unknown vault name (`CredentialUnavailable`) instead of creating its folder.
    - A logged-in Web or OPC UA session holds the vault lock only for the credential check, so a slow
      PLC no longer makes a block-protection apply report `CredentialStoreBusy`.
    - `sim_observe` event times are UTC; `watch_table_monitor` deletes the export its read leaves.
    - Scans past 1,000 records archive finished ones in every store, component windows included, so
      a store over 10,000 records shrinks instead of refusing every admission. Hosts older than this
      change refuse new admissions and operations in an artifact root whose component-window store
      has an `.archive` folder.
  - Follow-ups, fixed on 1 November (offline):
    - A Test Suite fresh reopen keeps scratch status only for the project that held it; a
      watch-owned project no longer gains it.
    - Admission reconcile of a combined apply's step workspace no longer holds on the temporary
      file a crashed journal write leaves; it lists it in the evidence and never reads it.
    - The text-list Host-loss sweep now fails when a run leaves a temporary file behind.
- Native runs of 31 October and 1 November ([status log](docs/history/status-log.md#1-november-2026)):
  - `project_simulation_enable` compiles every device: in a project with more than one PLC it
    refused with `AmbiguousTarget`, or with a `plcPath` left the other PLCs uncompiled so the
    post-apply fingerprint failed (`local.project_simulation.multi_plc.20261101`).
  - Combined apply of a verified shape no longer imports and saves the component FB before failing
    at typed preparation when the captured profile's FB number is taken in the target, which left the
    change `recovery_required`; it is refused before any claim.
  - `network_io_assign` accepts the start address the assigned PLC gives a range that had none (an
    I-device GSD's transfer areas); it refused the assignment with `MutationVerificationFailed`.
  - `device_online_browse` and `device_identify` send a sized HTTP/1.1 body with plain
    `application/json`, as the runtime Web API client does; PLCSIM answered their chunked
    `application/json; charset=utf-8` request with HTTP 500.
  - Runner and diagnostics fixes: the Explorer desktop gate reads the MCP 2026-07-28
    `server/discover` handshake and accepts `ModelInvalidated` after `tia_detach`; the GSD slice puts the
    device on the subnet without an address (its GSD sets `IpProtocolSelection` `OtherPath`);
    `ExactClient` runners accept the `tools/list` prefetch without params again (broken since
    `28d50f91`); `qualify_plcsim_signals.py` takes the discovery baseline before `sim_create`; card-move
    rows carry their UTC time; the retained-tree runners give the FileStorage helper its 360 s exit
    deadline.

### Known limitations

- Pre-1.0. Use it only with offline projects, copies, scratch projects and simulators you own,
  never with production PLCs or plants.
- 451 of 479 catalog features are verified; 108 release-matrix cells are open and 6 deferred
  ([STATUS](docs/STATUS.md), [TODO](docs/TODO.md)).
- V21 boundaries: no public API forces a value, Startdrive cannot read the number of a drive added
  through Openness, an OPC UA reference-namespace import crashes TIA, V21 refuses the CPU protection
  level while UMAC runtime access is enabled (its password has a credential route), the PLC's OPC UA
  server has no `HistoryRead`, no public member moves a PLC block, Openness cannot install a GSD
  file, and a safety program cannot be loaded into PLCSIM (the offline memory-card download is
  refused; the online route needs a card from the PLC).
- `project_upgrade` cannot upgrade projects with device types V21 dropped (for example a TP700
  Comfort 15.0 panel); restricted translation profiles cover one PLC's block comments only.
- Release assets are the offline bundle only; the engineering bundle with native workers is built
  on a TIA V21 workstation. Bundle manifests are hash lists, not publisher signatures.
