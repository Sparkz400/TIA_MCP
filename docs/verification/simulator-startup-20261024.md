# Fresh empty simulator startup — 24 October 2026

The actual MCP route passes one fresh CPU1511 Softbus lifecycle on this PC:
empty registry, owned creation in OFF, `powerOn` returning `OK` and STOP,
`powerOff` returning normally and OFF, exact unregistration, and a fresh empty
registry observation. Host and simulator worker exit normally. All nine captured
original process handles exit with code zero, and the original non-breakaway job
has nine total members and zero active members. No process was forcibly stopped.

This qualifies only that empty-CPU sequence on the retained binaries. No TIA
Portal was observed, no program was downloaded, and no RUN, program I/O,
requested stop, project-lock release or native project reopen was tested.
Brokered/alternate-parent processes and individual ephemeral-process capture
are outside the job proof. The 65-case runtime suite remains open.

## Exact evidence

The final run is `artifacts/p24-simstate04/`:

- `request.json` pins the Host/simulator package files, four caller scripts and
  coordinator. The package verifier confirms their hashes and byte counts still
  match after execution.
- `startup.json` records every MCP request/result, exact instance identity,
  creation receipt, cleanup and independent Host worker-exit receipt. Protected
  ticket material is redacted.
- `result.json`, `process-exits.json` and `owned-job-terminal.json` retain the
  coordinator's normal terminal proof and exit code zero.
- The native run uses `artifacts/n24/bin/TiaMcp.Host/release/` and
  `artifacts/n24/bin/TiaMcp.Simulator.V8/release/`. Its exact hashes, rather than
  later source or catalog changes, define the binary scope.

`contracts/feature-catalog.v1.json` retains historical
`local.sim.startup.v02` as failed and adds `local.sim.startup.20261024` for this
specific passed profile. `sim_state` remains **unverified** overall, with
conditional availability. The earlier blanket loader-error reason is no longer
a description of current empty-CPU startup. Rebuilt catalog checks are separate
from this native binary qualification.

## Validator repairs

The startup validator now refuses unavailable/nonempty preflight, malformed or
foreign instance identity, wrong state/action/result and unproven cleanup or
process exit. Instance ID zero is valid. Both runtime runners reuse the same
owned-identity validation. Cleanup errors cannot skip Host closure, an existing
report/workspace is preserved, and intermediate reports cannot publish success
before cleanup and shutdown finish.

The managed power-off API returns void, so its MCP `runtimeCode` must be null;
fresh power-on must return `OK`. This matches the existing adapter and installed
V8 API manual, PowerOff .NET table 9-49.

Seven regression tests include fault subcases for identity, preflight, state,
action, completion code, cleanup, output preservation, early publication and
shutdown. The early-publication assertion failed before its repair. All 182
block-test checks pass with one existing skip. Contract coverage passes for
262 tools, 56 mutation kinds and 336 feature entries. The managed general suite
passes 245 checks; the rebuilt Host's catalog is also checked through actual
MCP with deliberately absent native workers. No full offline aggregate is
claimed for these focused changes.

## Earlier attempts remain retained

- `p24-simstate`: startup/cleanup passed, but the experimental coordinator
  compared a `{path, sha256}` record against a `{path, sha256, bytes}` record.
  Its overall result remains failed. Read-only rechecking found matching hashes.
- `p24-simstate02`: input verification passed, but an incorrect validator check
  expected `OK` from the void power-off API. Cleanup succeeded; the validator
  and enclosing process proof correctly retained failure. No replay was used
  to alter that attempt.
- `p24-simstate03`: passed before the additional early-publication repair.
  The final-source run above replaces it for current validator evidence.

Run coordinators and check logs are retained in
`artifacts/todo-resume-20261024/`. Existing failures are not overwritten.
