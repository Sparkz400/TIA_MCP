# HMI compiler scope, normal lifecycle and lock release — 1 November 2026

**Status:** passed on the three installed profiles that have a production HMI write: TP700 Comfort
(`hmi_classic_import`), MTP700 Unified Comfort and WinCC Unified PC RT (`hmi_external_tag_create`).
Build hbf1 (`2431ccf9`), `scripts/qualify_hmi_gate_route.py`, which now gates the three properties below
on every run. Evidence: [`local.hmi.lifecycle.20261101`](../../evidence/local.hmi.lifecycle.20261101.json).

| Profile and fixture | Kind | Checks |
| --- | --- | --- |
| TP700 Comfort, desk-configured pair | `hmi_classic_import` | 72/72 |
| MTP700 Unified Comfort, desk-configured pair | `hmi_external_tag_create` | 49/49 |
| WinCC Unified PC RT, seeded baseline (`artifacts/q/hps`) | `hmi_external_tag_create` | 49/49 |

Each run is the full route of [1 November](hmi-gate-route-20261101.md) (refusals, applies, replay,
restore, fresh reopen) plus these gates:

- **Compiler scope.** For every apply, the compile after the checkpoint and the compile after the
  write each name exactly one target: the target HMI's own scope. Classic compiles its `HmiTarget`
  (`hmiSoftware`), Unified its owning device (`owningDeviceHardwareAndSoftware`, since Unified
  `HmiSoftware` has no compiler service). The other HMI and both PLCs are never in scope.
- **Normal lifecycle.** Each of the two Host lifetimes (the qualification Host and the production Host
  of the reopen) ends by closing its stdin: the Host exits 0 without being killed, its stderr reaches
  EOF, every `bridgeWorkerExit` receipt shows the worker's exit verified with code 0 and no forced
  termination, and the owned TIA process exits. That process is watched through a `SYNCHRONIZE` handle
  captured right after `tia_start` (`OwnedTiaProcessWatcher`), never reopened by PID. Measured: workers
  exit 1.8–2.8 s after EOF, the TIA process within 0.2 s after the Host.
- **Independent lock release.** After the first TIA process has exited, a second TIA process (a
  different PID, in a new Host) opens the saved original at the first attempt, read-write. No retry for
  "already opened" is made, so a lock left by the first instance fails the run.

## Complete content preservation: the V21 boundary

What the public V21 API exposes is captured and compared on every HMI write; the rest cannot be
observed:

- **Classic:** every screen of every role, tag table, connection, cycle, VB script and text/graphic
  list is exported and compared. `HmiTarget` has no public alarm composition, so Classic HMI alarms are
  not compared (the standing Classic export note).
- **Unified:** tag tables are exported and every node of the public object model is hashed (screens,
  items, dynamizations, event handlers with hashed scripts, parts, translations, tags, alarms, logs,
  audit, runtime settings, groups, lists, script modules). V21 has no `Screen.Export`, so a payload
  without a public getter is not compared (the two standing Unified notes).

Closing this gap needs a Siemens API (a Classic alarm composition, a Unified screen export); it is
recorded as a V21 boundary for the maintainer's disposition, not as a pass.

## Limits

The three profiles and fixtures above only. WinCC RT Advanced and KTP700 Basic PN have seeded fixtures
on this PC but no production HMI write yet; WinCC RT Professional is a recorded V21 boundary
([record](hmi-rt-professional-20261025.md#v21-boundary--1-november-2026)). Runtime, visual behavior
and PLC communication are not covered.
