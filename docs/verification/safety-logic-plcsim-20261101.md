# Changed F-logic in PLCSIM Advanced V8: native runs of 1 November 2026

`scripts/probe_safety_logic.py` (feature `safety.logic_changes`) ran in owned scratch projects. Each run used a frozen Debug
build under `artifacts/w2bs*`, ran with PLCSIM Advanced V8 on Softbus, and was curated from a clean worktree at its build
commit:

| Evidence | Build | Result |
| --- | --- | --- |
| `local.project_simulation.multi_plc.20261101` | `d681ece1` (w2bs4) | 24/24 steps; V21 refused the probe's F-FBD import (absolute operand under the wrong scope) |
| `local.safety.logic_plcsim_boundary.20261101` | `a7740570` (w2bs5) | 25/25 steps; the boundary below |

Earlier runs on the way: sl1/sl2 (`project_simulation_enable` compiled only one PLC, fixed in 4c87ae29), sl3 (a tag on
the F-DI address became a safety tag, fixed by addressing the channel directly and by the prepare-time refusal in
b50472b1/a7740570), sl5 (relative `--sim-worker` path).

## Fixture

- The scratch holds the standard CPU 1511-1 PN V4.1 and an added CPU 1511F-1 PN `6ES7 511-1FL03-0AB0/V4.1`, created
  with `configureScratchSecurity` and `failsafeRuntimeAccess`. Its generated scratch user holds Full access including
  fail-safe; this exists in the owned scratch only.
- An ET 200SP station with two F-DI 8x24VDC HF sits on the F-CPU's IO system. The F-DI's inputs start at byte 0.
- `project_simulation_enable` was applied without plcPath and compiled all three devices with zero errors.
- The safety program has no password, and nothing logged in.

## What passed

- `safety_import_xml` re-imported `Main_Safety_RTG1` with two F-FBD networks: `%M100.0 := %I0.0` (F-DI channel 0, by
  absolute address under `Scope="Address"`) and `%M100.1 := TRUE`. The apply took a checkpoint, compiled every device
  including the safety program with zero errors, and saved. The F-runtime group still names `FOB_RTG1` and
  `Main_Safety_RTG1`.
- `sim_create` created an owned PLCSIM instance of `ECPUType` `CPU1511F` (license OK, Off).
- `simulation_download` compiled the F-PLC ("The safety program is already consistent", zero errors). TIA requested no
  download configuration callback.

## Boundary

V21's offline `DownloadProvider.Download(DirectoryInfo, callback)`, which generates the PLCSIM virtual memory card,
refused the F-CPU:

> Error when calling method 'Download' of type 'Siemens.Engineering.Download.DownloadProvider'. An error has occured during download: 'Load safety program to device'
(`Siemens.Engineering.EngineeringTargetInvocationException`)

No memory card was written. The owned instance was released (unregistered) and its storage kept. The changed F-logic
therefore compiles, but it cannot be loaded into PLCSIM through the memory-card route. F-CPU RUN, F-logic behavior, F-I/O
injection and passivation were not reached.

## Proposed disposition (for the maintainer)

`safety.logic_changes`: the offline memory-card route is a V21 boundary for F-CPUs; Openness cannot load a safety program
this way (`local.safety.logic_plcsim_boundary.20261101`). The only other route is an online download to an owned PLCSIM
instance (`change.simulation_download_online`), which is untested for an F-CPU. It would have to answer the safety
download prompts without a password, and the worker refuses unknown download configurations today.

Choose one:

- (a) Close the row as `unavailable_target`, with this record as the evidence.
- (b) Keep it open for one feasibility probe of the online route on an F-CPU. A refusal or an unhandled safety prompt
  there would close it the same way.
