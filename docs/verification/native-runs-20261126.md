# Native runs of 26 November 2026

One owned-scratch session on the maintainer's second workstation (LT-PRG2024-03; TIA Portal V21 Update 3, German
installation; PLCSIM Advanced V8), driven by the runners added the same day. No user project was opened or changed.
The curated evidence was produced on Debug builds of `339ebb2d` (Host, engineering and simulator worker) with a
clean `src/` and `scripts/` tree; the earlier attempts listed below ran on Debug builds of `956776f4` plus the
changes that `339ebb2d` then committed.

## Build and environment

- A Release engineering bundle was packaged from `956776f4` (`sourceTreeDirty: false`). Its worker is not in Siemens'
  Openness AllowList: `tia_start` failed with `AccessDenied` ("Security error. The operation has timed out.") because
  nobody answered TIA's access prompt. Registering a release bundle needs an elevated shell
  (`scripts/Register-TiaOpennessApplication.ps1`), so the TIA runs used Debug builds, which
  `scripts/Register-DevOpennessWorker.ps1` keeps in the AllowList on every build. `sim_prune` also passed once on the
  Release bundle, which needs only the simulator worker.
- `project_retrieve` refuses an artifacts root whose retrieval directory exceeds 80 characters
  (`RecoveryPathTooLong`); the runs use `artifacts/n26/...`.
- The PLCSIM virtual adapter (`Ethernet 3`) had only the link-local address `169.254.117.125/16` (the 29 October TCP/IP
  runs had `192.168.0.10/24` configured by the operator); several other adapters carry tentative link-local addresses.

## Curated evidence

| Evidence | Rows bound | What ran |
| --- | --- | --- |
| [`local.platform.lifecycle.20261126`](../../evidence/local.platform.lifecycle.20261126.json) | `project_save`, `project_close`, `project_archive`, `project_open_copy` (X01), `simulation_provision_status`, `simulation_provision_recover` (L03) | `scripts/qualify_platform_lifecycle.py` |
| [`local.plcsim.prune.20261126`](../../evidence/local.plcsim.prune.20261126.json) | `sim_prune` (L03) | `scripts/qualify_sim_prune.py` |
| [`local.plcsim.network-mode.20261126`](../../evidence/local.plcsim.network-mode.20261126.json) | `sim_network_mode_set` (L03) | `scripts/qualify_sim_network_mode.py` |
| [`local.block-import.readback.20261126`](../../evidence/local.block-import.readback.20261126.json) | `change.block_import_xml` (content readback added) | `scripts/probe_block_import_readback.py` |

Catalog `0.5.1` binds them; the matrix has 63 open cells (85 before).

## Findings

- **`Project.Archive` writes into the open project's folder.** Although the project stays unmodified and nothing is
  saved, one run saw the `.ap21`, `XRef.db`, `ProjectInfo.txt`, `System/PEData.*`, `IM/HMI/DeviceFolders.dat`,
  `IM/SPL/options.json` and `AdditionalFiles/PLCM` change, another only the `.info` file. The 14 November note that the
  folder hash stayed unchanged does not hold in general; the runner records the written files and compares content
  (inventory, nothing unsaved). The read-only open and `project_open_copy` `closeAndCopyFolder` left the original
  folder byte-identical. Archiving a read-only (secondary) project is refused by TIA (`ReadOnlyProject`).
- **A never-powered PLCSIM instance cannot be archived** (`sim_storage`: `ArchiveStorageNotCreated`, runtime error
  -37); powering it on and off once first works.
- **`sim_network_mode_set`:** Softbus to `TCPIPSingleAdapter` took 29 ms with an immediate readback and `tcpIpReady`;
  no Runtime Manager restart was needed. Standard Windows user only.
- **`block_import_xml` round trips:** TIA's re-export of its own export differs only in `DocumentInfo/Created` for an
  SCL FC, an optimized DB and LAD/FBD FBs; a supplied document additionally lacks the empty Title/Comment
  `MultilingualText` objects TIA adds. With those ignored (and whitespace in `ObjectList` containers, formatting and
  ID numbering, as the structured-edit comparison already ignored) every supplied block compared equal, which is the
  rule the new readback uses.
- **Regression in 0.9.0:** every successful `project_snapshot_capture` failed with `ProjectReviewFailed` ("System.Int32
  cannot be converted to System.Int64") while adding `remoteAccess`, since `da5e374a` (20 November). Fixed in
  `5e5538f3`.
- **Card-staging access denial recurred.** Two provisioning attempts could not move the original card back after
  power-off: `IOException` 0x80070005 for the whole 15 s retry budget (3 and 22 attempts), Windows Restart Manager
  naming no holder, Defender real-time protection off. One was the user-authorized trusted attempt
  (`SimulationProvisionCardRestoreFailed`), one a refused-TLS attempt, whose `simulation_provision_recover` then
  stopped at the reconciliation ("Memory card cannot contain a reparse point"; no reparse point was found afterwards
  and `SIMATIC_MC` was absent). Other attempts the same day restored the card at the first move. Heavy concurrent
  builds and test runs were active during the failures.
- **TCP/IP web run blocked by the environment.** `qualify_runtime_plcsim_tcpip.py --mode web-services` hardcoded the
  English item name `PROFINET interface_1` (German installations name it differently; fixed by finding the CPU's one
  networked interface — other runners still hardcode it). With the CPU at `169.254.117.10` (the adapter's own
  link-local subnet), the setup reached provisioning, where `GoOnline` failed ("Changing to online mode failed")
  before any TLS or login callback. The 29 October runs had the adapter on a static `192.168.0.10/24`; restoring that
  is an operator step (adapter settings, elevated). Softbus was restored and read back afterwards. The three D12 rows
  stay open.

## Not run

- The UMC global-user and `UmacLoginRejected` legs of `project_open` (no UMC endpoint here), the administrator leg of
  `sim_network_mode_set`, the elevated UA disk-full test and the WinCC Unified alarm tools (the user skipped the
  elevated steps and the Runtime certificate renewal).
- A 2-5 minute `runtime_plcsim_bind` with a live UA session: there is no honest way to make the bind take that long.

## Download pre-check abort

`scripts/probe_download_abort.py` on Debug builds of `d85a6444` (owned scratch CPU 1511-1 PN, owned PLCSIM instance
provisioned with the user's first-use trust, RUN, `Kept := 7`), report `artifacts/da/da_da578614/report.json` (not
curated: the run did not complete). Case `first`: after a layout change of a standard-access DB,
`simulation_download_online` (`changes`, `keepActualValues`, `allowStop: false`) received `DataBlockReinitialization`
("The data blocks will be re-initialized with their start values.") as its first pre-download configuration and
refused it from the callback. PLCSIM showed nothing loaded: `Kept` 7, `ProgramVersion` 1, RUN, `Cycles` advancing,
no software-configuration or operating-state event in the watch around the download. The online compare before the
download listed `AbortDb` and `Main` as different. After the refusal the change reported `EngineeringError`
("Access to a disposed object of type 'Siemens.Engineering.Project'"), and every later Openness call, including the
online compare and `tia_detach`, failed with "TIA Portal has either been disposed or stopped running"; no Portal
process was left and Windows logged no application error. The `later` and `control` cases could not run. One
observation, to be repeated before any conclusion about the Portal's fate.

The repeat (`artifacts/da3`) showed the same: nothing loaded, the Portal disposed, `EngineeringError`. TIA documents
`DataBlockReinitializationSelections.NoAction`, but setting it is refused ("The configuration provided is invalid",
`EngineeringTargetInvocationException`), and that Siemens exception ended the download without loading while the
Portal stayed usable (`artifacts/da4`: the online compare after the abort ran; the control case, `reinitialize` with
`allowStop`, loaded `Added` and `ProgramVersion` 2 and the compare became identical, so the compare detects a load).
The worker now records its typed refusal and declines a refused `DataBlockReinitialization` through that setter
(`artifacts/da5`, case `later`: `SimulationDownloadReinitializationRequired`, nothing loaded, compare ran; the probe's
rule that counted the later `ConsistentBlocksDownload` as missing was corrected, since TIA asks it after the refused
question). The `first` case of that run hit the card-staging access denial during provisioning, so no run of all three
cases is complete and nothing is curated.

## HMI content fingerprint

`scripts/qualify_hmi_content_fingerprint.py` on HmiSeeder positive baselines (`HmiSeeder qualify ... positive`; two
HMIs per profile) of unified-pc, unified-comfort, classic-comfort, classic-rt-advanced and classic-basic, each
retrieved into an owned copy. The first run found that `hmi_content_fingerprint` accepted any kind beginning with
`hmi_` and fingerprinted `hmi_not_a_kind`; fixed in `3a7eb100` (`HmiEngineeringRules.IsFingerprintKind`). The rerun on
that build passed on all five profiles and is curated as
[`local.hmi.content-fingerprint.20261126`](../../evidence/local.hmi.content-fingerprint.20261126.json) (catalog
`0.5.2`): a usable, repeatable hash (`complete` false with the kinds' standing notes, `scopeAccepted` true), a prepare
bound to it, a new hash after the applied change (a Unified screen, a Classic `BackColor`), `StaleRevision` naming the
new hash for a prepare bound to the old one, and `InvalidRequest` for an unknown kind. The hash was identical for
`HMI_RT_1` and `HMI_RT_2` and for `hmi_screen_create` and `hmi_external_tag_create`, so it covers the family's HMIs of
the project rather than one target.

The decline was then verified on a complete run of all three cases (`artifacts/da6`, Debug builds of `d1570160`):
`first` and `later` were both refused at `DataBlockReinitialization` with `SimulationDownloadReinitializationRequired`,
nothing was loaded and the online compare ran; the control loaded the change and the compare became identical. TIA
asks `DataBlockReinitialization` before `ConsistentBlocksDownload`, so this change set cannot provoke an abort at a
later callback; the probe therefore reports `inconclusive` and the run is not curated.

## Card-staging access denial

`scripts/probe_card_lock.py` (10 cycles, owned instances, `artifacts/n26/cl1`): after power-off, renaming the card
folder away succeeded at once every time, and renaming it straight back was refused with `WinError 5` for about
100 ms in all 10 cycles; a plain folder with 20 files in the artifacts folder was refused for about 10 ms on most
renames. The artifacts folder is not in the Windows Search index. The PC runs Sophos Intercept X with on-access file
scanning (`SophosFileScanner`); Windows Defender real-time protection is off. A scanner's file-system filter is not a
Restart Manager holder, which matches the "no holder" refusals. The card move now retries for up to 120 s
(`d1570160`); the next trusted provisioning (`artifacts/n26/p7`) completed with every card move at the first attempt.

## V19 requalification

The V19 worker, which had stopped compiling when the batched capture reads made the shared traversal use
`BatchedAttributeReads` (linked by PR 330), was rebuilt against the V19 SDK and registered in V19's own Openness
whitelist by its Debug build. `scripts/qualify_tia_v19.py --run-native` passed all 53 stages, the first V19 run of the
batched reads, and is curated as
[`local.tia.v19-adapter.rebuild.20261126`](../../evidence/local.tia.v19-adapter.rebuild.20261126.json) (catalog `0.5.3`).

## Follow-up checks

`scripts/qualify_followup_checks.py` covers the native checks still open from 14 to 25 November in 16 independent
stages on owned scratch projects and instances. Its runs found three defects, fixed the same day:

- a `simulation_download` refused before its claim (`ticketState` `reusable`) kept its admission `outcomeUnknown`, so
  every later download and `project_save` was refused with `ResourceBusy` (`34308389`);
- a download whose password question the worker refuses from TIA's callback leaves the Portal disposed, and
  `tia_detach` then failed, so `tia_start` stayed refused with `AlreadyAttached` (`ed8a84e3`; the decline itself stays
  open);
- the runner's own stage order and fixtures (the timed apply, the test DB, the spent-ticket retry, which the Host
  refuses with `ResourceBusy` because the spent download stays held).

The complete run (`artifacts/n26/fu5`) passed all 16 stages and is curated as
[`local.followups.native.20261126`](../../evidence/local.followups.native.20261126.json) (catalog `0.5.4`); among
them, `QueueTimeout` refused a second call after 5 s during a 47 s apply, the stale sweep removed only the aged
prepared change, and a CPU 1518-4 PN/DP added to the scratch reported `plc_loadable_generate` available.

## Hardware smoke on a German installation

`scripts/qualify_hardware_changes.py` now resolves TIA-generated items (interfaces, ports, racks, the CPU display)
through `scripts/hardware_paths.py` and the default tag table through `scripts/plc_tag_tables.py` instead of their
English names. Its first rerun reported the default tag table's row as an unexpected change: since 10 November tag
table rows carry `tagCount` and `constantCount`, which a plugged module's system constant changes. The shared effect
check now tolerates exactly that. `--slice delete-rule` (14 steps) and `--slice drive` (52 steps) then passed and are
curated as [`local.hardware_changes.delete-rule.rerun.20261126`](../../evidence/local.hardware_changes.delete-rule.rerun.20261126.json)
and [`local.hardware_changes.drive.rerun.20261126`](../../evidence/local.hardware_changes.drive.rerun.20261126.json).
