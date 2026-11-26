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
