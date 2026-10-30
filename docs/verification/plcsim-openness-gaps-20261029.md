# PLCSIM and engineering gaps — 29 October 2026

A review on 29 October found seven capabilities that TIA MCP neither shipped, planned nor recorded
as deferred. Under the [release rule](../plans/release-obligations-20261015.md) each one either ships
or gets an evidence-backed V21 disposition. This record holds the native results; the routes are
documented in [simulation](../guides/simulation.md) and [engineering workflows](../guides/engineering-workflows.md).

| Gap | Feature | Disposition | Evidence |
| --- | --- | --- | --- |
| 1. Download a changed program to an existing owned PLCSIM instance | `change.simulation_download_online` | Verified | `local.plcsim_gaps.20261029`, `local.plcsim_gaps.recovery.20261029` |
| 2. Virtual time and time scaling | `sim_time`, `sim_test` `virtualTime` | Verified | `local.plcsim_gaps.20261029` |
| 3. Interrupt and fault injection | `sim_event` | Verified for OB82, OB83 and OB86; OB40 verified on 30 October with a channel hardware interrupt | `local.plcsim_gaps.20261029`, `local.plcsim_gaps.ob40.20261030` |
| 4. Memory reset, storage archive/retrieve | `sim_state` `memoryReset`, `sim_storage` | Verified | `local.plcsim_gaps.20261029` |
| 5. Typed CPU parameter edits | `change.hardware_cpu_parameters` | Verified; the protection level is a recorded boundary | `local.engineering_gaps.20261029` |
| 6. Compile a device, the project or an HMI target | `change.device_compile`, `change.project_compile`, `change.hmi_compile` | Device and project compile verified; HMI compile unverified (no compilable HMI fixture through public routes) | `local.engineering_gaps.20261029` |
| 7. Tag table create, rename, delete | `change.tag_table_create`, `change.tag_table_rename`, `change.tag_table_delete` | Verified | `local.engineering_gaps.20261029` |

## How the runs were made

Each evidence run used a Debug build of one commit, frozen with `git archive` into the main
checkout's ignored `artifacts/` tree with a `source-manifest.json` (`artifacts/gx4` for commit
`ec90975`), and was curated from a worktree at that commit. Run output is under `artifacts/q/`.
Two read-only probes on an owned scratch project came first: they found the native CPU attribute
names and validated the PLCSIM fixture (interrupt OB XML, hardware IDs, the ET 200SP station).

## Discovery (probes, not evidence)

- The scratch CPU 1511-1 PN V4.1 declares `CycleMaximumCycleTime`, `CycleMinimumCycleTime`,
  `CycleEnableMinimumCycleTime`, `CycleCommunicationLoad`, `StartupActionAfterPowerOn`,
  `StartupComparisonPresetToActualModule`, `StartupConfigurationTimeout`, `TimeSynchronizationNtpV2`,
  `TimeOfDay*` and `PlcAccessControlConfiguration` (V21 enum `Disabled`, `EnabledWithAccessControl`,
  `EnabledWithAccessControlViaAccessLevel` and the central-user variants). NTP server addresses are not
  declared on the CPU or its interface.
- The central DI 16x24VDC HF module (6ES7 521-1BH00) exposes no channels and no hardware-interrupt
  attribute through V21 Openness. The probe looked only at the module item; its channels sit on the
  submodule (see [OB40 on 30 October](#ob40-on-30-october)).
- Interrupt OBs import through `block_import_xml` with their number, `SecondaryType` and an empty
  `<Namespace />`; SCL external sources cannot set an OB's event class. `Version` and `Timer` are SCL
  keywords and cannot name DB members.
- Hardware identifiers come from the PLC's system constants (`Local~GapDI`, `GapHead~IODevice`), as decimal strings.

## Engineering run

`scripts/qualify_engineering_gaps.py`, attempt 4 of four, passed 80 of 80 steps
(`local.engineering_gaps.20261029`). Attempts 1–3 stopped on findings that the routes now handle:

1. V21 refuses `StartupActionAfterPowerOn = 2` in its setter (the change failed into recovery); coded
   values are now tried in order and every refusal is recorded. V21 accepted 1.
2. A time zone without daylight saving (22 → 23) makes TIA switch `TimeOfDayActivateDaylightSavingTime`
   off; configured-hardware preservation caught the collateral change. Daylight saving now changes first,
   and the documented rule is to request both together.
3. Turning the minimum cycle time off makes V21 undeclare `CycleMinimumCycleTime`. The route now models
   it like the memory byte addresses: disabling drops the value, a value for a disabled flag is refused,
   and enabling needs an explicit value with the flag written first. The same attempt reopened the
   original project path after a checkpoint restore; the runner now reopens the session's current project.

Attempt 4 covers: the tag-table lifecycle with six typed refusals and a checkpoint restore; device and
project compilation without any mutation stage; nine reviewed CPU parameters plus
`TimeSynchronizationNtpV2 = 1`, each device-compiled with preservation and readback, eight typed
refusals, a fresh-Portal reopen, a checkpoint restore and the original values back in one combined change.

Recorded boundaries:

- **Protection level.** `PlcAccessControlConfiguration = EnabledWithAccessControlViaAccessLevel` fails
  compilation: "If access control via access level is enabled and a higher access level … than full access
  (no protection) is set, the password for full access (no protection) must be set." This route never
  accepts passwords (as for block protection, no Host-to-worker credential route exists), so changing the
  protection level stays out of reach; the change is restored from its checkpoint. `Disabled` and numeric
  values are refused before any native call. Update 30 October: the credential route now carries the
  full-access password, but V21 refuses the level while UMAC runtime access is enabled, as on every owned
  scratch CPU ([record](cpu-protection-20261030.md)).
- **HMI compilation.** A TP700 Comfort and an MTP700 Unified Basic created through `hardware_add` compile
  on their exact targets, and TIA's errors return as `CompileFailed` ("A start screen has not been
  configured"; the Unified panel also reports its unset runtime password); both restore from their
  checkpoints. A compilable HMI needs screens, which are behind the closed HMI preservation gate, so a
  successful `hmi_compile` is not qualified.

## PLCSIM run

`scripts/qualify_plcsim_gaps.py` on one owned PLCSIM Advanced V8 CPU1511 instance on Softbus, attempt 2
of two, passed 73 of 73 steps (`local.plcsim_gaps.20261029`, frozen `e38adf2`). Attempt 1 (`e75ae0c`)
completed its first online download and then read `NotUpToDate` (-47): after a program change PLCSIM
needs its tag list refreshed with `sim_tags`. The guide now says so and the runner refreshes.

The fixture: a scratch CPU 1511-1 PN V4.1 with a central DI 16x24VDC HF, an ET 200SP IM 155-6 PN ST on
the CPU's IO system, `GapDb` (one counter per interrupt OB, an IEC TON, a DTL), `Main` and the interrupt
OBs 40/82/83/86, then card download, credential provisioning, power on and RUN as in the D09 runner.

- **Online download.** A `Main` change downloads with `softwareScope: "changes"` and `keepActualValues`
  in RUN; TIA asks only `ConsistentBlocksDownload`, the runtime user logs in once, and a counter set to 7
  survives. A `GapDb` layout change with `keepActualValues` answers TIA's
  `DataBlockReinitializationOrKeepActualValues` with `KeepActualValues` and keeps the 7. A further layout
  change with `reinitialize` and `allowStop` answers `StopPlcAndReinitialize`, resets the counter and
  returns to RUN (TIA asked for no separate stop). `plc_online_compare` afterwards reports
  `FolderContentsIdentical`. Refusals: no ticket (`PhysicalPlcRefused`), `reinitialize` without
  `allowStop`, and `compile`/`save` set on apply.
- **Virtual time.** A 5 s TON scenario passed on the wall clock (5.97 s) and twice with `virtualTime`
  (13 and 14 `StartProcessing` spans, 5.90 s virtual time, previous mode restored). At scale factor 1 a
  virtual-time scenario takes wall-clock time; it is repeatable, and the speed-up comes from
  `sim_time` `scaleFactor`: at 10 the timer finished within 1.25 s of wall time. Setting
  `systemTimeUtc` to 2031-06-15 made `RD_SYS_T` read year 2031.
- **Events.** `diagnosticAlarm` on the DI module raised OB82 once, `pullOrPlug` pull and plug raised
  OB83 twice, and `rackOrStationFault` fault and return on the ET 200SP IO device raised OB86 twice; each
  changed only its own counter. A malformed event is refused before the runtime call.
- **Memory reset and storage.** `memoryReset` restarted the instance with start values and the
  online-downloaded program. `sim_storage` archived the powered-off card (791 737 bytes, receipt with
  SHA-256), refused a wrong hash, retrieved it with an unchanged ownership marker, and the retrieved card
  ran the online-downloaded program.

Boundaries:

- **OB40 (superseded on 30 October).** With no hardware interrupt configured, `GetConfiguredProcessEvents`
  lists nothing and PLCSIM answers `ProcessEvent` with `NotSupportedByModule` (-55); OB40 does not run.
  `statusEvent` on the central module is answered `NotSupportedByModule` too (OB55 serves distributed modules).

### Recovery top-up

The passed run never called `change_status` or `change_restore` on a download. A top-up run of the
same runner (`ad7096c`, 74 of 74 steps, `local.plcsim_gaps.recovery.20261029`) repeated every step and
then, after the reinitialized download: `change_status` kept the applied state and its checkpoint
archive; `change_restore` retrieved the checkpoint into a new directory while the CPU kept the
downloaded program (ProgramVersion 2, reinitialized counter 0); a download prepared from the recovered
copy refused with `SimulationTargetMismatch`, because that copy did not produce the instance's card; and
`plc_online_compare` of the recovered project reported `FolderContentsIdentical` (its checkpoint was
taken after the layout change and before its download).

## OB40 on 30 October

Siemens' *TIA Portal Openness Hardware parameters* manual (V21) lists, for DI 16x24VDC HF V1.0, per
channel `HardwareInterruptRisingEdgeActive` (Boolean), `HardwareInterruptRisingEdgeInterrupt` (an OB link)
and `...Priority`. The channels sit on the submodule (`.../items/GapDI/items/GapDI`), not on the module.
`change.hardware_channel_attributes` now takes the flag and the OB link. The link names a hardware interrupt
OB of the one PLC in the module's own device. Anything else is refused typed: a diagnostic OB or a
non-string value with `InvalidRequest`, a missing block with `BlockNotFound`.

- **Attempt 1** (`2e90783`, frozen in `artifacts/ob1`) stopped at preparation. V21 declares
  `HardwareInterruptRisingEdgeInterrupt` only while `HardwareInterruptRisingEdgeActive` is true, so the
  route refused with `AttributeNotWritable`. Enabling the flag now requires the OB link in the same request.
  Apply writes the flag first, then checks the link's access and sets it. Disabling must omit the link,
  and preservation expects V21 to undeclare it.
- **Attempt 2** (`807a189`, `artifacts/ob2`) passed 91 of 91 steps (`local.plcsim_gaps.ob40.20261030`).
  Channel 0 was enabled and linked to `GapHardwareInterrupt` (OB40), device-compiled, with
  configured-hardware preservation verified. After download PLCSIM listed one configured process event
  (`Rising edge0`, the submodule's hardware ID, channel 0, `RisingEdge`). `sim_event` `hardwareInterrupt`
  on it raised OB40 exactly once and changed no other counter. A rising edge written to a PLC tag on the
  input (`%I0.0`) with `sim_write` did not raise OB40: PLCSIM raises hardware interrupts through its
  process-event API, not through input writes. On channel 1 the runner enabled and linked the interrupt,
  refused disabling with a link, and disabled it. `hardware_inspect` then showed the link undeclared again.
  The disabling change was restored from its checkpoint (`configured_hardware_recovery_verified`), and
  the channel matched its enabled state. The rest of the PLCSIM gaps run passed again, unchanged.
