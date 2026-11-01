# Changed F-logic in PLCSIM Advanced V8: native runs of 1 November 2026

`scripts/probe_safety_logic.py` (feature `safety.logic_changes`) ran in owned scratch projects. Each run used a frozen Debug
build under `artifacts/w2bs*`, ran with PLCSIM Advanced V8 on Softbus, and was curated from a clean worktree at its build
commit:

| Evidence | Build | Result |
| --- | --- | --- |
| `local.project_simulation.multi_plc.20261101` | `d681ece1` (w2bs4) | 24/24 steps; V21 refused the probe's F-FBD import (absolute operand under the wrong scope) |
| `local.safety.logic_plcsim_boundary.20261101` | `a7740570` (w2bs5) | 25/25 steps; the boundary below |
| `local.safety.logic_online_route.20261101` | `42287512` (w2bs6) | 26/26 steps; the online route, below |

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

## Online route

The controller ruled one bounded probe of the online PLCSIM route before closing the row. Run sl7 repeated the fixture, and
V21 refused the memory card again. The probe then prepared `change.simulation_download_online` to the same owned instance
(`softwareScope` all, `dataBlockValues` reinitialize, `allowStop` true), which refused at prepare:

> SimulationTargetMismatch: The owned instance's generated memory card came from another project or PLC; only that PLC is downloaded to it online.

The online route downloads only to an instance whose generated memory card came from exactly this PLC. Its owned-instance
identity, credential provisioning and TLS trust all build on that card. For an F-CPU, V21 refuses the card, so the online
route cannot start. The refusal text names a different project or PLC, but here no successful card exists at all; the
message could say so.

Behind that gate, V21 Openness exposes a safety download configuration,
`Siemens.Engineering.Safety.Download.Configurations.SafetyProgram` ("Load safety program to device"), with the single
selection `ConsistentDownload`. An online download of the F-PLC would raise it. The worker answers only its reviewed
configurations and refuses this one (`UnsupportedDownloadConfiguration`), as the project's policy refuses safety
confirmations. No safety prompt was answered, and no password or login was used.

## Disposition

`safety.logic_changes`: **unavailable_target** (maintainer decision, 1 November 2026; ledger entry in
[release-obligations-20261015.json](../plans/release-obligations-20261015.json) `dispositions`). Changed F-logic imports
and compiles with the safety program, and PLCSIM Advanced V8 creates an owned CPU1511F instance. But no route loads a safety program into it within the project's policy:

- V21's offline `DownloadProvider` memory-card route refuses: "Load safety program to device"
  (`local.safety.logic_plcsim_boundary.20261101`).
- The online route needs a successful card from this PLC (`SimulationTargetMismatch`,
  `local.safety.logic_online_route.20261101`). The worker now says so: an instance without a successful card is refused
  as having none, not as holding another PLC's card.
- Behind both stands V21's `SafetyProgram` download confirmation, which the policy does not answer.

Proposed TODO text: "[x] `safety.logic_changes` (D14): V21 boundary on 1 November (records
`local.safety.logic_plcsim_boundary.20261101`, `local.safety.logic_online_route.20261101`,
[record](verification/safety-logic-plcsim-20261101.md)). Changed F-logic imports and compiles; V21 cannot load a safety
program into a PLCSIM memory card, the online route requires that card, and V21's SafetyProgram download confirmation is
outside the project's policy. Closed as unavailable_target."
