# Native requested stop on PCNICO — 23 October 2026

Both runs used the Debug Host, the AllowListed Debug V21 worker (registry API path)
and, for the live run, the Debug PLCSIM Advanced V8 worker. Each held the empty native
dispatch guard registry lock for its whole lifetime; no ticket was created.

## Engineering-only requested stop — passed

`artifacts/block-tests/stop-engineering-20261023-01`,
`--engineering-only --fixture runtime-boolean --fixture runtime-arithmetic`.
The stop file was created at 17:00:10 UTC while `runtime-boolean` was being qualified.

- `runtime-boolean` completed engineering qualification: import, compile, save,
  reopen and persistence comparison of all three blocks; `engineeringVerified: true`.
- The request was observed at 17:01:50 UTC at the next suite boundary;
  `runtime-arithmetic` was not started. Index: `admission: stopped_by_request`,
  runner exit 2 (blocked), as designed.
- Host exit 0, worker exit 0 (`processExitVerified`, not forced), both owned TIA
  lifetimes `tiaProcessExit.processExitVerified: true`.
- Not established: `projectLockReleaseVerified` is false throughout; this runner does
  not observe project-lock release independently.

## Live requested stop — not reached

`artifacts/block-tests/stop-live-20261023-01`, `--live --provision-runtime-credential
--trust-owned-empty-cpu-certificate --fixture runtime-boolean` (user-authorized first-use
trust for owned scratch CPUs only).

- Engineering passed. Case `truth-0` created owned PLCSIM Advanced instance 0
  (CPU1511, Softbus, OFF) and downloaded the program.
- `simulation_provision_scratch_credential` refused with
  `SimulationProvisionIsolationRequired`: in 33 snapshots over 29.9 s, explicit
  PN/IE / PLCSIM (1) discovery returned one `Accessible device` at
  `00-00-00-00-00-00` (no name, no series). No credential was opened or submitted and
  the CPU was never started. Report:
  `artifacts/simulation/TiaMcp_367193c2dc13417cb8048d4eb8963ae9/simulation-provision-49a8d738a35749ce97360c24161b4d0e.json`.
- Cleanup powered off and released the instance and deleted the owned registration;
  the runner then stopped with `stopped_due_to_uncertain_runtime` (exit 1) because
  this provisioning outcome is classed as unreconciled. The stop request created after
  the case was therefore not what ended the run. `remaining-cases.json` lists the
  seven unattempted cases.
- No TIA, worker or simulator process remained afterwards.

This is the same zero-address entry recorded on the VM, where it was intermittent and
appeared only after many cases. Here it appears before the first owned startup, so it is
reproducible on this PC.

## Discovery probe — the entry comes with the Runtime Manager

The worker's diagnostic mode `--plcsim-discovery-probe <dir>`
(`src/TiaMcp.Openness.V21/PlcsimDiscoveryProbe.cs`) creates its own scratch project
with a CPU 1511-1 PN (`6ES7 511-1AL03-0AB0/V4.1`) and takes three PN/IE discovery
snapshots, one second apart, in each state. It never touches existing instances,
projects or the network mode. It ran under the guard lock without an Openness prompt:
`artifacts/plcsim-discovery-probe-20261023-02/result.json` (SHA-256
`972141d7d73574355bce4c6139d59edf4eb4723a13668ea9d023b563241d3d80`). An earlier attempt (`-01`)
failed before any discovery because `Projects.Create` rejected the `DirectoryInfo` it
was given; it is retained.

| State | Simulator processes | PN/IE interfaces | PLCSIM (1) devices |
|---|---|---|---|
| A: nothing touched | none (only `sim9sync`, SRM services) | Realtek NIC, PLCSIM Virtual Ethernet Adapter, Wintun | route absent |
| B: PLCSIM Advanced API initialized, 0 registered instances | + `Siemens.Simatic.Simulation.Runtime.Manager` | `PLCSIM (1)` | one `Accessible device`, `00-00-00-00-00-00` |
| C: one owned instance (ID 0, OFF, Softbus) | same | `PLCSIM (1)` | same single entry |
| D: after unregistering it | same | `PLCSIM (1)` | same single entry |

Network mode was Softbus and runtime version `0x00080000`. The probe's self-check
confirmed its instance was no longer registered; the Runtime Manager was shut down at
the end.

Conclusion: on this PC the zero-address entry appears as soon as the PLCSIM Advanced
Runtime Manager provides the Softbus route, with no instance registered. It is neither
the owned CPU nor S7-PLCSIM V21, PLCSIM V5.4 or the virtual Ethernet adapter, which do
not create the route on their own (state A). Every provisioning run initializes the
Runtime Manager first, so the current guard can never pass here. The guard is unchanged;
accepting this exact entry would be a design change that needs independent review.
