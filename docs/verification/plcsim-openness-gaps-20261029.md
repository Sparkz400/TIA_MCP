# PLCSIM and engineering gaps — 29 October 2026

A review on 29 October found seven capabilities that TIA MCP neither shipped, planned nor recorded
as deferred. Under the [release rule](../plans/release-obligations-20261015.md) each one either ships
or gets an evidence-backed V21 disposition. This record holds the native results; the routes are
documented in [simulation](../guides/simulation.md) and [engineering workflows](../guides/engineering-workflows.md).

| Gap | Feature | Disposition | Evidence |
| --- | --- | --- | --- |
| 1. Download a changed program to an existing owned PLCSIM instance | `change.simulation_download_online` | Implemented; native run pending | [PLCSIM run](#plcsim-run) |
| 2. Virtual time and time scaling | `sim_time`, `sim_test` `virtualTime` | Implemented; native run pending | [PLCSIM run](#plcsim-run) |
| 3. Interrupt and fault injection | `sim_event` | Implemented; native run pending. OB40 cannot be configured through V21 Openness (below) | [PLCSIM run](#plcsim-run) |
| 4. Memory reset, storage archive/retrieve | `sim_state` `memoryReset`, `sim_storage` | Implemented; native run pending | [PLCSIM run](#plcsim-run) |
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
  attribute through V21 Openness, so its rising-edge hardware interrupt and the event's OB40 assignment
  cannot be configured by this route.
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
  values are refused before any native call.
- **HMI compilation.** A TP700 Comfort and an MTP700 Unified Basic created through `hardware_add` compile
  on their exact targets, and TIA's errors return as `CompileFailed` ("A start screen has not been
  configured"; the Unified panel also reports its unset runtime password); both restore from their
  checkpoints. A compilable HMI needs screens, which are behind the closed HMI preservation gate, so a
  successful `hmi_compile` is not qualified.

## PLCSIM run

Pending: `scripts/qualify_plcsim_gaps.py` on one owned PLCSIM Advanced V8 instance (frozen
`artifacts/gs2`), held while the native queue served the TCP/IP real-server window.
