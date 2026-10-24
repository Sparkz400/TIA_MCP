# Runtime admission continuation — 24 October 2026

The user resumed the full TODO autonomously. This increment fixes failure reporting
and test-runner admission; it does not qualify native provisioning or release any
historical unresolved operation.

## Reproduced failures and changes

- A failed provisioning call with complete worker-reported OFF/card-restoration/
  interface-release cleanup incorrectly allowed the next runtime case to start.
  The Host nevertheless retained an `outcomeUnknown` project/storage admission.
  Both a backend regression and a two-case session regression reproduced this.
  Failed provisioning now always retains an uncertain outcome and stops later
  cases, while still attempting exact owned unregistration, TIA detach and process
  cleanup. Original error and cleanup facts remain available.
- Failed admitted native calls did not expose their reservation ID. Bridge errors
  now retain their original code, message and details and add a Host-issued ID for
  the existing read-only `engineering_operation_status` tool. Object details keep
  their shape; scalar details are retained separately. Status reads do not invoke
  native work, replay the operation or release the reservation.

## Executed verification

- `python -B -m unittest discover -s scripts -p 'test_block_test_*.py'`:
  178 tests ran successfully with one existing skip. This includes the two-case regression,
  exact cleanup, stopped admission and retained error evidence.
- The full `TiaMcp.Engineering.Tests` runner passed, including 47 shared admission
  checks, 131 recovery-boundary checks and existing watch recovery/authoring tests.
  New checks resolve the returned ID through the actual dispatcher, retain native
  object/scalar/null details, refuse competing acquisition and verify no replay.
- The original failing engineering regression is retained in
  `artifacts/todo-resume-20261024/engineering-before.log`; passing logs are
  `engineering-after.log` and `block-tests.log` in that directory.

These tests use managed/fake native adapters. No Siemens process or PLC was started.

The full `Test-Offline.ps1 -Suite All -OutputDirectory artifacts/b24` aggregate
subsequently passed: 1,931 Python tests (one existing skip), 45 managed suites,
25 Explorer model/client checks, relationship checks and all configured actual
offline Host workflows. `artifacts/b24/result.json` and the retained console log
record the terminal pass. Source bindings were regenerated for the changed Core
file while preserving every historical evidence row and qualification boundary.
The earlier `artifacts/a24` attempt failed because the shell selected a Python
environment without `jsonschema`; the successful rerun selected the existing
project test environment. Both attempts remain retained.

## Native prerequisite observation

`Test-TiaMcpEnvironment.ps1 -RequireSimulation` retained
`artifacts/todo-resume-20261024/environment.json`. PLCSIM Advanced V8 API/runtime
metadata and the current token's Openness/PlcSimUsers authorization were present.
The configured `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48`
directory lacked the required Base, Step7 and Safety assemblies. Further inspection
found the process environment's `TiaApiDirectory` override pointing at
`D:\Arbeit\Siemens\Portal V21\PublicAPI\V21\net48`. Passing that exact override to
the doctor produced `environment-explicit.json`: all required prerequisites are
present, including Base/Step7/Safety assembly versions 21.0.0.0. The first report
was a default-path observation, not proof that TIA was unavailable.

The current Release V21 worker, simulator worker and engineering-capable Host then
built into isolated `artifacts/n24` outputs with zero warnings/errors. The compiled
worker lifecycle guard passed without loading Siemens assemblies. The subsequent
actual read-only MCP smoke test (`native-discovery.json`, `native-discovery.log`)
loaded the V21 worker, returned capabilities and an empty Portal-process list,
and exited with Host code zero. No Portal was started or project attached.
Licensing, requested stop, recovery and the full runtime corpus remain unqualified.

## Next reproduced bugs

`artifacts/todo-resume-20261024/next-runtime-evidence-findings.json` records two
additional false acceptances against the actual Python backend/verifier: a download
with Boolean `instanceId: false` passes as integer zero, and a one-byte card file
passes a manifest with Boolean `bytes: true` and its matching manifest hash.
The reproductions use a fake simulator and a temporary local file; no PLC was
contacted. These require strict integer validation and regression checks in the
next increment. The default-path environment diagnostic also needs to honor the
installed-location selection used by the native worker.

## Backlog corrections and next work

The reviewed placeholder exclusion is already in public `main` through `54f0237`;
public snapshot publication and green hosted CI are recorded in the existing
publication review. Their stale open TODO entries were removed. Historical
failures, paused checkpoints and exact prior binary evidence remain unchanged.

Trusted provisioning reconciliation still needs independent project and simulator
storage evidence; the helper's own cleanup report is insufficient. Exact CPU
discovery, intermittent card-move denial and all other unfinished workstreams
remain open. Offline checks do not establish native or release acceptance.
