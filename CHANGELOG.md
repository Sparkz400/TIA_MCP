# Changelog

Notable changes to TIA MCP, in the style of [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow [Semantic Versioning](https://semver.org/); before 1.0 any release may change the
tool surface; from 1.0 the [contract freeze policy](docs/plans/contract-freeze-policy.md) says what
may change in a major, minor or patch release. The product version is set once in [`Directory.Build.props`](Directory.Build.props);
the feature catalog keeps its own data contract version. Dated detail and evidence are in the
[status log](docs/history/status-log.md); [STATUS](docs/STATUS.md) has the current counts, and the
[tool reference](docs/reference/tools.md) gives each tool's support class.

## [Unreleased]

Changes since the repository went public on 23 October 2026, up to feature catalog `0.4.133`.

### Added

- **Drive motor projection:** change kind `drive_motor_configure` writes a motor's nameplate into one drive data set through Startdrive's `HardwareProjection`, with exact expected values, readback and checkpoint restore; qualified natively on a SINAMICS G220 ([record](docs/verification/drive-motor-configure-20261108.md)).
- **Time-synchronised PLCSIM captures:** `runtime_capture`'s simulator source takes `TimespanSynchronized_C`, `_P` and `_CP` with `spanMilliseconds`; each sample processes exactly that CPU time before reading at the next sync point ([record](docs/verification/plcsim-timespan-capture-20261108.md)).
- **Hardware download to an owned simulator:** `simulation_download_online` takes `hardware: true` (with `softwareScope: all` and `allowStop: true`) to download the hardware configuration too; a `sim_observe` watch reported the resulting hardware-configuration event natively ([record](docs/verification/plcsim-hardware-download-20261108.md)).
- **Motion TOs on drive telegrams:** `to_connect` takes a SINAMICS drive's telegram (`hardwarePath`, drive-object handle, `telegramType`) through Startdrive's `Connect(Telegram)`, which reaches the G120C telegram that plain Openness does not expose; a real speed axis actor passed natively, and `SafetyTelegram` is refused with `SafetyBoundary` ([record](docs/verification/drive-telegram-connect-20261108.md)).
- **Offline runtime extensions:** bounded `syslog` reads through `runtime_web_diagnostics` (natively qualified on PLCSIM TCP/IP on 8 November; the user needs the `WebReadSyslog` device right, [record](docs/verification/plcsim-syslog-20261108.md)), and explicit SingleStep_C/P/CP/CT/CPT capture mode selection. Existing ownership, request bounds and restore behavior remain; the capture modes are not natively qualified ([record](docs/verification/offline-backlog-20261103.md)).
- **Contract review inventory:** the actual-Host surface runner retains a hashed discovery candidate with tool schemas, annotations, prompts, resources and binary hashes. The [contract freeze policy](docs/plans/contract-freeze-policy.md) was accepted on 8 November and takes effect at 1.0.
- **Contract freeze tooling:** the frozen surface baseline `contracts/mcp-surface.v1.json` with `scripts/mcp_surface.py` (cut, and a check that lists breaking changes and fails on them from 1.0), the generated [error code registry](docs/reference/error-codes.md) with stable and detail classes, a **1.0 contract** column in the [tool reference](docs/reference/tools.md), the catalog's optional `deprecation` field, and a surface gate in `release.yml`.
- **Typed LAD/FBD coverage:** CPU 1513-1 PN V4.1 base families (102 engineering cases), and 120 wave 9 software-unit/namespace profiles on CPU 1511 V4.1 (124 engineering cases), with retained exact-build evidence and explicit runtime limits.
- **Classic import on more panels:** `hmi_classic_import` is qualified and admitted on WinCC RT Advanced and KTP700 Basic PN as well ([record](docs/verification/hmi-classic-profiles-20261101.md)).
- **Unified event handlers:** change kind `hmi_event_handler_create` (Unified script event handlers of screens and screen items (the script stored with CRLF line ends, never executed)) past the HMI preservation gate; qualified natively and admitted on Unified Comfort and Unified PC ([record](docs/verification/hmi-unified-authoring-20261102.md)).
- **Unified tag dynamizations:** change kind `hmi_dynamization_create` (Unified tag dynamizations (ProcessValue bound to a PLC tag, a global DB member or an internal tag)) past the HMI preservation gate; qualified natively and admitted on Unified Comfort and Unified PC ([record](docs/verification/hmi-unified-authoring-20261102.md)).
- **Unified screens and items:** change kind `hmi_screen_create` creates a Unified screen (sized like an existing one) or an
  allowlisted screen item past the HMI preservation gate; qualified natively and admitted on Unified Comfort and Unified PC
  ([record](docs/verification/hmi-unified-authoring-20261102.md)).
- **HMI lifecycle gates:** the HMI gate route checks that every compile names only the target HMI's own scope, that each Host
  lifetime ends normally (Host, worker and owned TIA exits verified) and that a new TIA process then opens the saved project
  read-write at once; passed on TP700 Comfort, MTP700 Unified Comfort and WinCC Unified PC RT
  ([record](docs/verification/hmi-lifecycle-20261101.md)).
- **Versioning and releases:** product version `0.9.0` for every assembly, the npm metadata,
  `serverInfo.version` and release bundles; a tag-triggered workflow drafts a GitHub release with
  the verified offline bundle ([releasing](docs/guides/releasing.md)).
- **MCP surface:** `--toolsets` tool groups, a title on every tool, four workflow prompts
  (`safe_change`, `project_health`, `block_test`, `diagnose_fault`), progress heartbeats, resource
  templates (artifacts, saved reviews, feature catalog), completions, client logging and output
  schemas for 85 tools ([MCP surface](docs/reference/mcp-surface.md)).
- **Engineering changes** through the prepare/apply/restore workflow: hardware families and CPU
  parameters, device and project compile, tag tables, software units (rename and create from a unit
  master copy), motion connections, SINAMICS drive parameters and telegrams (by container index when
  Startdrive cannot read the drive object number), PLC documents, project copy
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
- **Test Suite:** application test runs on an owned PLCSIM instance (TS-Q02 qualification mode), and
  style-rule runs with operator scope and license observations (TS-Q01 qualification mode). A job
  queued by `testsuite_style_job_prepare` from a public application review ran in TS-Q02.
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
  after `tia_detach` and the artifact fallback (`local.explorer.desktop.20261101`). Warm/cold
  timings, memory and payload sizes for the 1k/10k/50k trees and the 500-node graph; the cache
  benchmark also records cold first queries, page bytes and peak resident set
  ([record](docs/verification/explorer-performance-20261101.md)).
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
- **HMI writes past the preservation gate:** change kinds `hmi_classic_import` (Classic
  `classic_xml_import` of a screen or tag table, and the screen `BackColor`) on TP700 Comfort and
  `hmi_external_tag_create` (PLC-backed Unified tags: root tags and scalar global DB members) on
  Unified Comfort and Unified PC take the `hmi_engineering` payload of their operation. They accept
  their family's standing capture notes, compile after the checkpoint and require the compiler
  problems unchanged, and passed route and interruption runs (`local.hmi.classic-import.route.20261101`,
  `local.hmi.classic-import.interruption.20261101`, `local.hmi.external-tag.route.20261101`,
  `local.hmi.external-tag.interruption.20261101`); `--hmi-change-qualification <profile>` runs them
  on a qualification Host.
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
- **Capability profiles:** 13 module capability profiles and the operator-installed I-device GSD
  head's versionless profile in `contracts/hardware-catalog-profiles.v1.json`, linked from
  `hardware_catalog_search` and checked by attribute writes; technology version profiles in `contracts/technology-version-profiles.v1.json`
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
- **Hardware refusals natively:** the hardware runner's `--slice refusals` runs the MRP port-link
  refusal both ways, the PLC-to-PLC shared-access refusal, channel and versionless capability-profile
  refusals, the `sharedDevice` disconnect, an address change that moves the start address and a
  second owned scratch whose CPU takes the module the first project set `OutsideOfProject`
  (`local.hardware_changes.refusals.20261102`, [record](docs/verification/hardware-refusals-20261102.md)).

### Changed

- From 1.0 on, `scripts/release_matrix.py --release-gate` also refuses while any release obligation in
  the ledger is not accepted, so a set `releaseReady` flag cannot bypass the per-obligation acceptance.
- `serverInfo.version` reports the product version; the catalog version moved to
  `capabilities.experimental["tiaMcp/featureCatalog"].version`.
- Bundles from `scripts/Package.ps1` carry the product version in their name and `manifest.json`.
- Combined PLC library apply is on by default for the natively verified recipe shapes;
  `--enable-library-integrate-apply` is still accepted and changes nothing. Since 2 November these
  are 23 shapes: every combination of LAD/FBD FC and FB callers, earlier placement, reuse and the
  reused-FB multi-instance (`local.library_integrate.candidate_shapes.combinations.20261102`), and a
  multi-instance caller FB with its own instance DB, which gains the member through the newly captured
  component effect (`local.typed.component-effect-capture.20261102`,
  `local.library_integrate.candidate_shapes.dependent_db.20261102`); an embedding parent is still
  refused before admission with `DependencyProfileUnavailable`.
- Production Hosts dispatch the qualified force-table document edits.
- `simulation_provision_reconcile` also requires the project's save time before its pre-provision
  archive (and compares the project library), the storage outside the card against its pre-attempt
  inventory by metadata, and a write-ahead stream that never opened or armed the credential; an
  observer that was never created is unchanged. All five retained provisioning admissions on this
  PC were released (`local.sim.reconcile.release.20261102`); `reconcile_provision.py` tracks every
  process exactly and proves the project lock released.
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
  created there became a safety tag and was refused only after the create. `vci_synchronize`
  WorkspaceToProject and a project-ward `vci_conflict_resolve` of a tag table apply the same rule to
  the incoming package, and `library_instantiate` refuses a tag-table master copy while the PLC owns
  any F-I/O range (V21 cannot read a master copy's addresses before the create); before, these
  ended in checkpoint recovery. Rechecked natively on an F-CPU with ET 200SP F-DI, except the
  `vci_conflict_resolve` route (`local.safety.fio-routes.20261101`).
- Combined `library_integrate` apply refuses before any claim when a block number the import would
  take is already used (`ComponentBlockNumberOccupied`) or a step checkpoint or recovery path would
  exceed TIA's 143 characters (`ComponentPathTooLong`); its steps run in a short workspace
  `changes/c/<8 hex>`, and a reused FB may differ from its source only in the version comment, which
  the preview records.
- `to_create` checks the CPU, type and version against the technology version profiles: a recorded
  refusal refuses with its native text (`TechnologyVersionRefused`), and an unprofiled pair needs
  `unprofiledAcknowledged` (`TechnologyVersionUnprofiled` otherwise). `change_apply` refuses a PLC's
  first motion object with `compile=false` (`CompileRequired`, nothing written): its generated
  MC_Servo and MC_Interpolator stay inconsistent without a compile, and the change failed after the
  create with `FingerprintUnavailable` and could not be restored in place. Rechecked natively, with
  the technology version probe's compiled motion cells (`local.technology.first-motion.20261101`).
- `safety.logic_changes` and `block.move` are closed as `unavailable_target`: V21's offline
  `DownloadProvider` refuses the PLCSIM card and the online route refuses at prepare without a card
  from this PLC; V21 has no block move API. The release matrix counts an unavailable API with an
  unsupported or blocked target as `unavailable_target`.
- `SimulationTargetMismatch` tells an owned instance with no successful card that it has none.
- `drive_observe` no longer drops a drive object whose `DriveObjectNumber` Startdrive cannot read (a
  G120C PN V4.7): such a drive's objects are addressed by their container position
  (`driveObjectIndex`, `driveId` `DO@<index>`) with a warning. `drive_parameter_set` and
  `drive_telegram_set` take exactly one of `driveObjectNumber` or `driveObjectIndex`, refuse the
  other with `DriveObjectHandleMismatch`, and add the drive object's identity to the change fingerprint.
- The Unified HMI preservation capture also covers the complete text-list export, and OPC UA sessions
  also check that namespace 0 is the OPC UA namespace.
- **Canonical JSON:** the Host and the Openness worker hash with one form, RFC 8785 (JCS), pinned by
  [shared vectors](fixtures/canonical-json/vectors.v1.json) that both runtimes' tests reproduce byte for
  byte; integer literals beyond 2^53 keep their exact digits, and a lone surrogate is refused
  (`InvalidRequest`). Numbers now hash by value (`1.0` as `1`, `1e3` as `1000`), and both runtimes
  read number literals with one exact conversion (the worker's .NET Framework parser misrounds some and never sees them);
  review diffs still report `1` to `1.0`. Non-ASCII text and `+ < > & '` stay literal. **Upgrade:**
  journals, records, captures, completed reviews, approvals and candidates, Test Suite jobs, VCI
  manifests and revisions, capture plans and saved reviews written by earlier builds keep verifying in
  the form they were written with, and the worker accepts its own earlier hashes at reconcile and when
  a fresh Host resumes a component window; external resource keys and credential bindings keep their
  earlier hashes. A new approval, candidate or import from a translation review prepared before the
  upgrade, and a commit from an earlier VCI preview, are refused with `CanonicalFormChanged` and are
  prepared again
  ([hashes stored before RFC 8785](docs/reference/engineering-capture-identities.md#hashes-stored-before-rfc-8785)).

### Fixed

- **Library upgrades hit TIA's path limit:** `library_upgrade` failed with `NativeLibraryError` for 18 of 22 DMC vendor archives because TIA refuses an upgraded library folder over 143 characters. The upgrade folders are shorter now, and prepare refuses a library that still would not fit with `LibraryPathTooLong`, before any copy ([record](docs/verification/library-vendor-workload-20261108.md)).
- **Component installs into OBs of other event classes:** combined apply admitted any existing OB caller, though its evidence covered program-cycle OBs only. Another event class now stays plan-only until verified; startup and cyclic-interrupt OBs passed natively and run in production ([record](docs/verification/component-ob-event-classes-20261108.md)).
- **VCI interruption check:** accept exact verified-callback reconciliation when Git finishes verification before the interrupted Host exits; earlier callbacks still require recovery.
- A request number beyond the double range (such as `1.8e308`) broke the worker connection: the
  worker refused it before reading the request id, so the Host reported `ProtocolMismatch` and
  refused every later call until it was restarted. The worker now refuses such a request with
  `InvalidRequest` and the connection stays usable (rechecked natively, `local.component.canonical-hash.20261102`).
- No `sharedDevice` disconnect could compile: V21 fails a submodule left assigned to the leaving
  PLC. The disconnect now names that PLC's items in `sharedSubmodules`, and they go to the one
  remaining PLC in the same change (`local.hardware_changes.refusals.20261102`).
- `hardware_catalog_search` never named a versionless (`GSD:`, `System:`) profile, because it
  compared the entry's catalog version, a GSD file name, with the profile's firmware; it now matches
  the type.
- A Test Suite system-test endpoint also needs a TCP/IP instance: a Softbus instance keeps its
  configured controller address and the PLCSIM adapter can still route to it with nothing
  listening, so it now refuses with `PlcsimEndpointUnreachable`. No build dispatches system tests
  yet, so this is checked offline only.
- `drive_telegram_set` refused every prepare with `ExpectedValueMismatch`: telegram rows carried a
  string-typed null that never equals the null of the echoed `expectedTelegrams`; they now carry real
  JSON nulls (`local.drives.index.20261101`).
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
  - Rechecked natively on 2 November ([record](docs/verification/audit-paths-20261102.md)): the
    delete and rename compile rule for all four kinds, `change_prepare`'s `RecoveryRequired` after a
    failed apply, generated-source reads with `projectModifiedBefore`/`After`, and translation reopen
    keeping scratch status for the owned fixture and never granting it to an admitted project. A fresh
    worker resuming a closed translation fixture is refused its capture; no adoption route is built.
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
    - `sim_write`, `sim_write_signals`, `sim_test` step writes and `diagnostic_plcsim_write` refuse a
      symbol whose area in PLCSIM's tag list is the output area, as `sim_area_write` refuses outputs
      (`InvalidRequest`, the whole request, before anything is written); inputs, markers, data blocks,
      timers and counters stay writable. Rechecked natively for `sim_write`, `sim_write_signals` and
      `sim_test` (`local.sim.output-refusal.20261101`), and for `diagnostic_plcsim_write` and the
      member of a structured output tag (`local.sim.output-refusal-2.20261101`).
    - A component-window step whose request had emoji, certain control characters or a number such
      as `1e3` was refused: the Host and the worker hashed it differently. Both now use RFC 8785
      (rechecked natively, `local.component.canonical-hash.20261101`).
- Open audit items, 1 November ([record](docs/verification/audit-rechecks-20261101.md#second-round-the-open-items)):
  - `software_unit_create_from` brings a unit's tag tables from a master copy unread and had no F-I/O
    rule; like `library_instantiate` of a tag-table master copy it now refuses with `SafetyBoundary`
    while the PLC owns, or may own, any F-I/O range (`local.safety.fio-routes-2.20261101`).
  - `library_instantiate` of a tag-table master copy reported applied when TIA had created a tag whose
    name the PLC already used under a new name (`McProbe(1)`). Its readback now checks the created
    tags' names and fails such a change with `MutationVerificationFailed`, so the checkpoint recovers
    it; `software_unit_create_from`, where TIA renamed a copy's unit tag the same way, refuses such
    tags with the renamed blocks and types (`local.library.tag-collision.20261101`).
- The translation admission scripts compared the Host's RFC 8785 key and row hashes (PR 184) with
  the earlier form: `qualify_translation_import.py --same-instance` stopped before its first case,
  and `build_translation_admission.py` would have written proofs the Host loader refuses. They accept
  either form; the restricted pipeline ran natively end to end again
  (`local.translation.restricted.qualification.20261102`, `local.translation.restricted.native.20261102`).
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
    deadline; the runners' store scanners and watch verifiers accept the Host's RFC 8785 journal
    hashes beside the earlier form (they refused every record a Host since PR 184 writes).

### Known limitations

- Pre-1.0. Use it only with offline projects, copies, scratch projects and simulators you own,
  never with production PLCs or plants.
- 463 of 481 catalog features are verified; 22 release-matrix cells are open and 6 deferred
  ([STATUS](docs/STATUS.md), [TODO](docs/TODO.md)).
- V21 boundaries: no public API forces a value, an OPC UA reference-namespace import crashes TIA, V21 refuses the CPU protection
  level while UMAC runtime access is enabled (its password has a credential route), the PLC's OPC UA
  server has no `HistoryRead`, no public member moves a PLC block, Openness cannot install a GSD
  file, a safety program cannot be loaded into PLCSIM (the offline memory-card download is
  refused; the online route needs a card from the PLC), V21 Openness neither creates nor
  exposes WinCC RT Professional, and V21 Update 3 cannot re-check a Test Suite style rule's scope at
  execution time, so production style execution is unavailable (only the TS-Q01 qualification mode runs).
- Test Suite system tests have no qualification mode: they need PLCSIM's TCP/IP mode and, since V21
  takes no OPC UA credential, a guest-access seed made at the desk
  ([record](docs/verification/testsuite-system-blocker-20261101.md)).
- `project_upgrade` cannot upgrade projects with device types V21 dropped (for example a TP700
  Comfort 15.0 panel); V15.1 and V17–V20 sources and UMAC-protected or locked sources are not
  exercised, since only V21 is installed here and V21 cannot create older projects. Restricted
  translation profiles cover one PLC's block comments only, and the default-mode run that finalizes
  such an entry imports its one admitted edit, so a final entry cannot import on its own scratch.
- A local Git remote with more than 100,000 entries is refused (`GitLimitExceeded`) until it is
  repacked.
- `library_instantiate` and `software_unit_create_from` recognize a master-copy tag TIA renamed by
  its `<name>(<n>)` form beside an existing `<name>`, so a tag that is itself named that way fails
  the change too.
- Release assets are the offline bundle only; the engineering bundle with native workers is built
  on a TIA V21 workstation. Bundle manifests are hash lists, not publisher signatures.
