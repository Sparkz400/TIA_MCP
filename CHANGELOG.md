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

- `project_archive` writes the saved state of an open project to a new `.zap21` archive under the Host's artifacts; the project is never saved or changed (catalog `0.4.181`, unverified).

- **Renaming a safety tag:** a single `tag_set` with `safetyAcknowledged: true` and `attributes` holding only `Name` renames a safety tag. Address, data type, deletion and every item of a batch stay excluded, and the safety offline program must be writable. The preview holds `safetyBefore` and a warning, the renamed tag is read back as a safety tag on its address, and the applied change records `safetyAfter`. Nothing validates or accepts the safety program. Qualified natively on an owned scratch F-CPU for a tag no safety block uses (`local.safety.tag-rename.20261111`) and for a Bool tag that an F-FBD network of the main safety block reads (`local.safety.tag-rename-used.20261111`): the block followed the rename and the offline signatures were unchanged in both runs; other uses of a safety tag are not exercised ([renaming a safety tag](docs/guides/engineering-workflows.md#renaming-a-safety-tag)).
- **Classic HMI fixtures without the editor:** a newly created Classic HMI has no start screen and Openness has no setter for it. A native probe of 11 November 2026 shows that a screen created from a master copy in an HMI without screens becomes its start screen on WinCC RT Advanced and TP700 Comfort (17.0.0.0): the HMI then compiles without errors, also after save, archive and reopen. `HmiSeeder.exe startscreen-probe` runs the probe and archives the fixture; `compile-experiment` replays save, archive, compile and re-import steps on a copy of it. See the [record](docs/verification/classic-start-screen-master-copy-20261111.md).
- **Evidence for batches in namespaces and on an F-CPU (catalog 0.4.178):** three native runs of 11 November 2026 on one build are recorded. `local.change-batch.items-2.20261111`: `items` on `block_number_set`, `group_create` and `group_rename`, and block and data type batches in a software unit that holds one name in a namespace and in none (129/129 steps). `local.safety.tag-batch.20261111`: a batch that renames or deletes a real safety tag, or moves or creates a tag inside a PROFIsafe telegram, is refused as a whole and no tag changes (21/21). `local.safety.fio-routes.20261111`: the F-I/O tag routes on an F-CPU with F-DI modules, including a `tag_set` and a `tag_create` batch, on a German TIA installation (96/96); `scripts/qualify_fio_tag_routes.py` stopped there before, because it created its VCI workspace in `en-US`.
- **Items on block numbers and groups:** `block_number_set`, `group_create` and `group_rename` take `items` like the other batched kinds, each with a cross-item rule of its own: two items may not take one number for blocks of one kind (`BlockNumberCollision`), two items may not give one name to groups under one parent, and a `group_rename` item may not name a group inside a group that another item renames ([items on other kinds](docs/guides/engineering-workflows.md#items-on-other-kinds)).
- **Evidence for item batches and fingerprint reuse (catalog 0.4.177):** three native runs of 11 November 2026 in an owned scratch CPU are recorded: the `tag_set` batch (`local.tag-set.batch.20261111`), `items` on the ten further kinds including a software unit and a batch that fails midway and is restored (`local.change-batch.items.20261111`), and the reused fingerprint read behind `--enable-fingerprint-reuse` (`local.fingerprint.reuse.20261111`).
- **Group preflight and clearer failures for item batches:** a batch of the simple single-object kinds now prepares every item a second time at apply, without backups, before the first item is written, like the `tag_set` batch. A native TIA failure at an item is reported with the item's index and how many items were already written, instead of the bare native message.
- **Items on more change kinds:** `constant_set`, `tag_create`, `constant_create`, `tag_delete`, `constant_delete`, `tag_table_create`, `tag_table_rename`, `block_rename`, `block_attributes_set` and `type_rename` take `items` (1 to 100 payloads of that kind in one software scope) and apply them under one fingerprint check, checkpoint archive, compile and save. Each item keeps its kind's own checks; two items may not edit the same object or produce the same name, and a refused item refuses the whole change ([items on other kinds](docs/guides/engineering-workflows.md#items-on-other-kinds)).
- **Fingerprint reuse between changes:** the worker keeps its last project-wide fingerprint read and reuses it while TIA reports the project as saved with the same `LastModified`, for the same change kind and target blocks, across the prepare, admission and apply steps of prepared changes only. A change that follows another of the same kind reads the project once instead of five times; fingerprint values are unchanged. `fingerprintContent` in the change view and journal counts full reads and reuses, and `tia_session_status` reports `isModified` and `lastModified` per project. The save-state marker is checked natively for worker edits in an owned scratch (`scripts/qualify_fingerprint_reuse.py`); edits made by hand in the TIA user interface are not verified yet ([fingerprint reuse](docs/guides/engineering-workflows.md#fingerprint-reuse-between-changes)).
- **Revealed compiler warnings:** TIA's incremental software compile reports a warning only for an object it recompiles, so an old warning of a recompiled object looked new. For a change that names its own blocks, data types or tag tables, `compile.baselineComparison` now lists warnings of other objects as `revealed` instead of `introduced`; they no longer clear `verified` or fail a tag-table import. New errors and warnings of the change's own objects are judged as before ([compile verification](docs/guides/engineering-workflows.md#compile-verification)).
- **Data blocks as text:** `block_read` with `render: "text"` and `block_export` render a block without networks as its declared interface (sections, members, data types, start values, comments) in `interface.text` and the text file. Not natively qualified yet.
- **Many tag edits in one change:** `tag_set` takes `items: [{tagTableName or tagTablePath, memberName, attributes}]` (1 to 500 edits in one software scope) and applies them under one fingerprint check, one checkpoint archive, one baseline and post-change compile and one save, instead of that whole sequence per tag. `preview.items` lists every edit, a refusal on any item (safety tag, F-I/O address, name collision) refuses the whole change before a write, and every item is read back after the writes and again after save ([many tag edits](docs/guides/engineering-workflows.md#many-tag-edits-in-one-change)). Change journals also time the three fingerprint runs in `phaseTimings`. Run once natively in an owned scratch (`scripts/qualify_tag_batch.py`); not recorded in the evidence store yet.
- **I/O address map:** `plc_io_map` lists every configured input and output range with its module and the PLC tags addressed into it, the ranges without any tag, the I/Q tags that lie in no listed range and addresses shared by several tags. Not natively qualified yet.
- **Readers and writers of I/O tags:** `plc_tag_usage` scans the block files of a `block_export` index (LAD/FBD and SCL) and reports, per I/Q tag, the blocks and networks that read and write it, the tags no scanned block references, and absolute `%I`/`%Q` accesses. Static evidence over the exported blocks only. Not natively qualified yet.
- **Compact inventory and hardware reads:** `project_inventory` takes `includeBlocks: false` and `compact: true` (short hardware rows with I/O ranges, block paths relative to their PLC); `hardware_inspect` takes `compact: true` for identity, address ranges and channels without the attribute, network and GSD reads that exhausted `maxItems` after a few dozen modules.
- **Batch block export:** `block_export` exports every block selected by `groupPath`, `namePrefix` and `kinds` in one call, each as unchanged native XML and as a complete text file, and returns one compact row per block plus an index file. Know-how protected and unexportable blocks are listed as skipped; `maxBlocks` and `offset` page large programs. Not natively qualified yet.
- **SCL networks as text:** `block_read` with `render: "text"` now renders SCL (StructuredText) networks as their source text, and writes the complete text of every network to `textArtifact` next to the XML; the response still cuts a network after 16,384 characters.
- **Focused, compact tag reads:** `software_inspect` takes `include` (`blocks`, `tags`, `constants`, `types`, `technology`) so `maxItems` is not spent on sections that are not needed, and `compact: true` for tags as name, data type and address. Each tag table reports `tagCount`, `constantCount`, `tagsTruncated` and `constantsTruncated`, and `truncation` names the first place the item bound cut the result.
- **Tag tables from rows, with a hardware cross-check:** `tag_table_import_xml` takes `tagTableName` and `tags: [{name, dataType, logicalAddress, comment?}]` instead of SimaticML, merging the rows into the table's own export (or a new table) and importing through the existing checks. Every tag-table import preview now carries `hardwareCrossCheck`, which compares imported I/O addresses with configured module address ranges and data type width with address width; it is advisory and never blocks the import. Exercised natively in an owned V21 scratch by `scripts/qualify_tag_rows.py` on 10 November 2026, without a retained evidence record ([tag rows](docs/guides/engineering-workflows.md#tag-rows-and-the-hardware-cross-check)).
- **Classic screen edits that clear the last HMI error:** when the baseline compile aborted with errors and the edited screens compile without any, warnings that V21's aborted run never reached are accepted if they lie outside the edited screens, and listed in `compile.unmaskedWarnings`; new errors or findings inside the edited screens still fail ([compiler diagnostics](docs/guides/hmi-engineering.md#compiler-diagnostics-and-completion)).
- **Large source and XML inputs by reference:** `change_prepare` kinds `source_upsert` and `block_import_xml` take `sourceRef`/`xmlRef` instead of inline `source`/`xml`, and the Classic HMI kinds `hmi_classic_import` and `hmi_classic_screen_edit` take `xmlRef` instead of `xml` ([large Classic exports](docs/guides/hmi-engineering.md#large-classic-exports-by-reference)). Each reference is a committed `artifact_upload_*` ref or a file under the Host artifact directory (at most 2 MiB UTF-8). The Host inlines the exact text and records `payload.inputReference` with its SHA-256, so the preview hash covers it like inline text ([large inputs](docs/guides/engineering-workflows.md#large-inputs-and-summary-results)).
- **Classic tag-table imports beside graphic lists:** `hmi_classic_import` of a Classic tag table no longer refuses because the HMI has graphic lists. Its preservation capture leaves out only graphic lists, whose export stays blocked, under scope policy `classic-tag-table-import-without-graphic-lists-v1`, and reports that exclusion as coverage. Like the bounded screen edit, it accepts compiler findings the new tags resolve; a new or worsened finding still fails. Screen imports keep the whole-HMI capture ([containment](docs/guides/hmi-engineering.md#temporary-classic-graphic-list-export-containment)).
- **Member-level cross references:** `xref_query` takes `memberPath` (for example `Real[210]`, `Real[200..220]` or `Station.Downholder.Enable`) and returns only locations of those members or elements, each marked `exact`, `indirect`, `wholeArray` or `container`. Only matches count against `maxItems`, so a global DB with thousands of references no longer truncates before the wanted element ([filters](docs/guides/engineering-workflows.md#large-projects-filters-and-member-cross-references)).
- **Inventory filters:** `project_inventory` and `software_inspect` take `groupPath` (group names from the top, such as `Station 3000/Downholder`, or a full group path) and `namePrefix`; `project_inventory` also takes `includeHardware: false`. Only matches count against `maxItems` ([filters](docs/guides/engineering-workflows.md#large-projects-filters-and-member-cross-references)).
- **Readable LAD/FBD networks:** `block_read` with `render: "text"` returns each network as text: contacts, O/A/X boxes and comparators as Boolean expressions; coils, instructions and calls as statements with their pin bindings ([reading networks](docs/guides/engineering-workflows.md#reading-networks-as-text)).
- **Predicted UId renumbering for network patches (opt-in):** `graphical_network_patch` with `renumberUids: true` applies V21's renumbering rule at prepare time, so a patch that adds or removes nodes, such as a series contact, can read back exactly. The rule is a fixed point of every native export in the typed-graphical corpus but is not natively qualified for patches; the exact readback check still decides ([LAD/FBD editing](docs/guides/engineering-workflows.md#ladfbd-editing)).
- **Read-only TIA Portal V19 adapter:** Host option `--tia-version 19` runs the new `TiaMcp.Openness.V19` worker (source build; not in release bundles) and advertises only the base tools and twelve read tools: discover, attach, start, status, detach, `project_open` of `.ap19`, `project_inventory`, `software_inspect` and native-XML `block_read`, `type_read` and `tag_table_read`, with the V21 Host's object paths. `V19ProjectSeeder` builds owned V19 fixtures; a V19 fixture's folder and archive upgraded into V21, compiled and matched their V19 exports, and a source held open in V19 is refused with `SourceLocked` ([record](docs/verification/tia-v19-adapter-20261109.md), [reference](docs/reference/tia-v19-adapter.md)). TIA V19 approves Openness applications under its own `19.0\Whitelist` key, where V19 builds now register.
- **1,000-node library branching tree:** a tree of 1,000 FC types was released, its complete closure queried in a project and a global library, published and instantiated with a clean compile ([record](docs/verification/library-native-scale-20261027.md#1000-node-branching-tree-9-november-2026)).
- **Five more OB event classes for components:** a PLC component installs into a status, update, profile, pull or plug of modules or rack or station failure OB (OB55, 56, 57, 83, 86), as FBD or LAD, after an earlier network and with reuse in every combination; 40 more shapes passed natively and run in production ([record](docs/verification/component-ob-event-classes-20261110.md)).
- **OB callers in every combination:** a PLC component installs into a startup, cyclic-interrupt, time-of-day, time-delay, hardware or diagnostic error interrupt OB with LAD, an earlier placement and reuse in every combination; 24 more shapes passed natively and run in production ([record](docs/verification/component-ob-event-classes-20261108.md#fourth-run)).
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

- **Faster project-model and index capture:** `project_model_capture` and `project_index` read each PLC block's and tag's attributes in one `GetAttributes` call instead of one native call per property, and no longer read a block's name and namespace several times. On a warm Portal, a 2,582-node project was captured in 2.1 s instead of 5.9 s, and a 1,290-node one in 1.9 s instead of 3.6 s. The metadata revisions of both tools are unchanged (`local.project-model.batched-reads.20261112`, three owned projects). A native type whose batch read fails falls back to the per-property reads. Hardware, data type, technology and HMI nodes are unchanged, and the first capture after opening a project is still dominated by TIA loading objects ([record](docs/verification/batched-capture-reads-20261112.md)).
- **PROFIsafe drive tags as Bool bits:** `safety_drive_tags` now takes `bits: [{ word: "status" or "control", bit: 0..15, name }]` and creates one Bool safety tag per named bit of the S_ZSW1/S_STW1 word of PROFIsafe telegram 30 (word bit 0..7 in byte n+1, 8..15 in byte n). The earlier `tags` payload created Word tags, which the F-compiler refuses as F-I/O accesses of the safety program; it is now refused with `InvalidRequest`, and a bit an existing tag covers (including such a Word tag) is refused with `AddressInUse`. The route gives no bit a meaning. Natively qualified on an owned scratch F-CPU: an F-FBD network of the main safety block reads a created status bit and writes a created control bit, and the safety program compiles (`local.safety.drive-tags-bool.20261112`, [table](docs/guides/engineering-workflows.md#supported-payloads)).

- **Fingerprint reuse is opt-in:** the reuse of the project-wide change-fingerprint read is now off unless the Host is started with `--enable-fingerprint-reuse`. It relies on TIA's save state, which is verified for edits the worker makes but not for edits made by hand in the TIA user interface.
- **OPC UA session timeout:** the runtime client requests a 600 s session timeout instead of 60 s, so an idle session is not ended by the server during a 2-5 minute `runtime_plcsim_bind`. The server may revise it down; the client still sends no keep-alive, and a session orphaned by a Host crash can live up to the revised timeout ([OPC UA runtime](docs/reference/opcua-runtime.md)).
- **`block_read` text once:** with `render: "text"` the response no longer repeats the rendered text of all networks in a top-level `text` field; each network's text is in `networks[].text` and the whole block in one piece is the `textArtifact` file.
- **`software_inspect` group paths:** `softwareScopes[].groups` no longer counts against `maxItems`; it has its own bound of the same size (`groupsTruncated`, `groupTruncation`) and is left out when `include` is given.
- **Summary change results:** `change_prepare`, `change_apply` and `change_status` return a summary by default: state, `changeId`/`previewHash`, stages, failures, warnings and effects, compile error/warning counts with introduced/resolved findings and error messages, and artifact paths. Payload strings over 1 KiB appear as length and SHA-256. `includeDetails: true` returns the full view; the journal keeps everything. `block_read` returns inline `xml` only up to 64 KiB and never with `render: "text"` unless `includeXml: true`; the artifact always holds the full export ([summary results](docs/guides/engineering-workflows.md#large-inputs-and-summary-results)).
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

- **Clearer dependency refusal on an F-CPU:** when TIA refuses to export a block during the delete dependency scan, `DependencyCoverageIncomplete` now names the block's programming language, know-how protection and consistency. On a PLC with a safety program this is the generated `FOB_RTG1` (a know-how protected SCL OB), whose native cross-references name no references either, so `tag_delete`, `constant_delete`, `type_delete` and `block_delete` stay refused there by decision (`local.safety.drive-tags-bool.20261112`).

- **Classic screen edit when the HMI compiler cannot run:** on 11 November 2026 the compile after a `hmi_classic_screen_edit` import and the rollback compile both ended with `Cannot compile. Cannot access the project path.`, and the change reported "Rollback compiler diagnostics do not match the successful baseline". Both compiles now report a compiler that could not run (`compile.compilerUnavailable`, `hmiEvidence.rollbackCompilerUnavailable`); after such a rollback compile the worker still compares every regular screen and states that the open project holds the original screens unsaved, and whether the saved project is the checkpoint or, after the apply's own save, the edit. The change stays `recovery_required`. `hmiEvidence.baselineCompileSkippedGeneration` records a baseline that V21 answered from its last compile. The cause of the compiler failure is not identified and it is still detected only after the import; see the [record](docs/verification/classic-screen-compile-path-failure-20261111.md).
- **Technology hardware references beside same-named siblings:** `technology_read` left every hardware and channel reference of a project unresolved (`hardwareMembershipUnavailable`) when any two sibling hardware items shared a name, for example both bus adapters of a PN/PN coupler. Only a reference to such an item, or to an item below it, is now unresolved, with reason `hardwarePathAmbiguous`; `hardwareMembershipStatus` is `completeWithRepeatedPaths` and `hardwareRepeatedPaths` counts the paths. Covered by managed tests; not natively qualified.
- **F-I/O refusal of a `tag_set` batch names its item:** a `tag_set` batch refused because one item's address lies in an F-I/O range reported `SafetyBoundary` without saying which item. The message now starts with `items[n]:` and the details carry `index` and `memberName`, like every other batch refusal.
- **Decision log that cannot be written:** a read-only `intent-decisions.jsonl`, or a directory in its place, raised `UnauthorizedAccessException` after a change was applied, which the apply did not catch. It is now recorded as `intent-log-write-failure` like other log write failures and the change stays `applied`.
- **Replay of a failed apply:** `change_apply` repeated with the same idempotency key after a change ended `not_applied` or `rolled_back` was refused with `RecoveryRequired` by mutation admission; it now returns the recorded outcome, as it already did for `applied` ([record](docs/verification/native-audit-20261110.md)).

- **`readbackVerified` on applied changes:** the progress milestone stayed `notReached` on applied changes whose readback had run. An attribute write such as `tag_set` now records `attribute_readback_verified`, stages ending in `_verified_after_save` (`source_upsert`, `certificate_create`, `instance_db_create`) count, a compile-only change reports `skipped`, and an applied kind that checks inside its mutation without a stage reports `notRecorded`.
- **`software_inspect` with many groups:** on a project with more user groups than `maxItems`, group paths used up the item budget, so a read such as `include: "constants"` with `namePrefix` returned no tag tables.
- **Empty text file for data blocks:** `block_read` with `render: "text"` and `block_export` wrote a 0-byte text file for a block without networks. A block with no text form now gets no file and a `textUnavailable` reason.
- **`technology_read` hardware membership error without a reason:** when the walk of the project's hardware failed, `referenceCoverage` said `readError` and nothing else, and every hardware reference repeated the failing walk. The coverage now keeps the exception type, error code and bounded message in `hardwareMembershipError`, and the walk runs once per read. Why the walk failed on the project that showed this is not known.
- **Object arguments sent as JSON text:** tools with a `request` object (for example `project_snapshot_capture`, `project_index`, `project_model_*`, `library_*`) advertised an unconstrained schema, so some clients sent the object as a JSON string and the call failed with "The node must be of type 'JsonObject'". These arguments are now declared as objects, and an object or array that still arrives as a JSON string is parsed.
- **Text rendering of large blocks:** `block_read` with `render: "text"` failed with an XML character-limit error on blocks whose export exceeds 4 MiB, although the export had been written. Exports are now read without that bound, and a rendering failure reports `textUnavailable` instead of failing the read.
- **Classic screen rollback stuck on renamed triggers:** a failed `hmi_classic_screen_edit` batch restored and read back every screen, then stayed `recovery_required` because the rollback compile named each screen's generated event trigger after the screen's current name (`trigger 3024_Setup_…, event 'Loaded'`) instead of the old copied name. Compile comparisons now ignore only that generated trigger label; the owning object, event, state, the rest of the message and every count must still match.
- **Region titles refused SCL sources:** `source_upsert` refused an unmodified TIA-generated SCL source with `SourceScopeMismatch` because words in `REGION` titles such as `Sensor Configuration` or `Function calls` were read as declaration keywords. A `REGION` that starts its line now takes the rest of the line as its title, and a `#`-prefixed local identifier such as `#Type` is never a keyword; real nested, configuration and global-tag declarations are still refused ([source workflow](docs/guides/engineering-workflows.md)).
- **Large library publishes and compiles:** publishing a 1,000-version closure completed in TIA and then failed its next journal write at 8 MiB, leaving the change in `applying`. Library journals are now compact with a 32 MiB bound, and prepare refuses an oversized journal with `LibraryLimitExceeded` before any native change. A clean 1,000-block compile was refused because its success rows exceeded the 500-row diagnostic budget; surplus success rows are now counted in `omittedSuccessRows` ([record](docs/verification/library-native-scale-20261027.md#1000-node-branching-tree-9-november-2026)).
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
