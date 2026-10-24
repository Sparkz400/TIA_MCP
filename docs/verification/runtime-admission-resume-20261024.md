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

## Runtime scalar follow-up

`artifacts/todo-resume-20261024/next-runtime-evidence-findings.json` records two
additional false acceptances against the actual Python backend/verifier: a download
with Boolean `instanceId: false` passes as integer zero, and a one-byte card file
passes a manifest with Boolean `bytes: true` and its matching manifest hash.
The reproductions use a fake simulator and a temporary local file; no PLC was
contacted. The follow-up fixes both false acceptances, and extends the checks to
zero-valued floating-point IDs/error counts and numeric completion flags. Both
runtime entry points call the same download validator. The standalone CLI now
checks download identity in both provisioning and ordinary startup modes, and
refuses malformed creation identities before download or identity-based cleanup.
Provisioning comparison requires integer identities on both sides. Empty files
with integer byte count zero remain valid.

The initial scalar regression recorded 25 failing subcases; the additional
creation/provisioning identity regression recorded seven. The repaired code passes
all 182 block-test tests with one existing skip (`runtime-final.log`). These are
focused follow-up checks after the full aggregate at `b090df8`; that aggregate was
not rerun for the Python-only follow-up. Logs `scalars-before.log`,
`identity-before.log` and `runtime-final.log` retain the failures and final result.
All 100 additional safety-qualification/lifecycle and typed-graphical/dependency/
resolution caller tests pass (`runtime-callers.log`).
The default-path environment diagnostic was still open at this checkpoint;
the follow-up below repairs its installed-location selection.

## Installed API path follow-up

The diagnostic now follows the native worker's V21 installation lookup: an explicit
`-TiaApiDirectory` wins without reading registration, otherwise the Siemens Base
assembly registration in the 64-bit registry selects its parent directory. A missing
registration uses the setup default; a denied lookup is unavailable, not absence.
The 29 diagnostic tests pass under PowerShell 7 and Windows PowerShell 5.1
(`environment-path-tests.json` and `environment-path-tests-ps51.json` in the same
evidence directory). Tests cover explicit/registered/default precedence, refusal to
fall back after denied access, and supplied-evidence isolation from the machine probe.

The fresh `environment-path-probe.json` resolves
`D:\Arbeit\Siemens\Portal V21\PublicAPI\V21\net48` without an override.
`environment-default-fixed.json` reports all required simulation-profile prerequisite
metadata present. No Siemens worker, project or CPU was started by these checks.

## Durable native failure identity

After Host restart, the admission previously retained the exception type but lost
the native error code and original report identity. `failure.json` now also retains
`nativeErrorCode` and `nativeDetailsSha256`. The fingerprint is SHA-256 of the
existing `CanonicalJson` UTF-8 encoding of `{"details": <original native details>}`:
object keys sort ordinally, arrays retain order and numeric representations are
preserved. Absent details have a null fingerprint; explicit JSON null has the
fingerprint of `{"details":null}`. Host-added `mutationAdmission` is excluded.
The record stores neither the native error message nor raw details or tickets.

The regression fails against the previous implementation at the missing retained
error code (`admission-failure-before.log`). The fixed Release engineering suite
passes, including 64 admission checks (`admission-failure-final.log` under
`artifacts/todo-resume-20261024/`). Object, string, explicit null and absent details
are covered, with fingerprints independently calculated from known JSON bytes,
reopened durable records, no raw diagnostic retention and continued resource
conflict refusal. The build has zero warnings/errors. Current source bindings
validate with all historical evidence rows preserved; 106 historical inputs remain
unavailable in this checkout and release acceptance remains false
(`admission-failure-bindings.json`). No full aggregate or native run was repeated.

This supplies a report identity for later independent comparison; it does not
establish that cleanup succeeded. Provisioning still needs a complete before-state
baseline and fresh independent project/storage observations before reconciliation.
Existing watch/component captures cover bounded fixtures, not a generic whole
project. No old reservation is released and no native operation is replayed.

## Generated-card evidence admission

The native provisioning preflight previously converted JSON completion and instance
ID values to Boolean/integer types and accepted the report without checking its
compilation or download diagnostics. The card producer also accepted a zero-error
download summary with truncated diagnostics. Both Python runtime entry points
checked the error count but not diagnostic completeness.

The producer now uses the existing strict diagnostic evaluator. Provisioning
requires an actual Boolean completion, integer instance identity, exact owned
name, and complete successful compilation and download diagnostics before card
staging or CPU startup. The runtime backend and standalone validator share the
same Python result checks as the compiler validator. Unknown/Error states,
truncation, missing arrays and nested Error messages refuse. Complete Success,
Information and warning-only results remain eligible. Typed qualification records
also pin the newly consumed diagnostic helper. No diagnostic is rewritten and no
historical report is promoted.

The original native preflight predicate, extracted unchanged into the shared
helper, fails both new regression groups (`download-evidence-before.log`). The
Python regression records 36 failing subcases before the repair
(`download-runtime-before.log`): incomplete reports reach card verification instead
of stopping before startup. After repair, all 14 managed evidence groups pass on
both .NET 10 and .NET Framework 4.8; all 184 block-test tests pass with one existing
skip, and all 14 compiler-evidence tests pass. The V21 worker builds with zero
warnings/errors and its compiled lifecycle guard passes. Logs are retained under
`artifacts/todo-resume-20261024/` with `download-` prefixes.

The broader Python regression also passes all 1,944 tests with one existing skip
(`download-python-all.log`). This is the Python suite, not a repeat of the complete
managed/Host/package aggregate. All 77 typed-graphical caller tests also pass after
the recorded-helper update (`download-typed-callers.log`).

Context7 lookup failed because its executable was unavailable. The installed V21
`Siemens.Engineering.Base.xml`, members `DownloadResultState.Success`,
`.Information`, `.Warning` and `.Error`, confirms the result states used here.
The initial attempt to override the portable test project's framework failed at
restore; a separate retained framework test project then built and ran the exact
same production helpers and assertions successfully. Neither helper run loads
Siemens or starts a PLC.

The refreshed implementation bindings validate, preserving all historical evidence
rows; 106 historical inputs remain unavailable locally and release acceptance is
false (`download-bindings.json`). This fixes a prerequisite evidence boundary, not
trusted provisioning reconciliation. Complete before-state project/storage capture,
independent after-state verification, discovery and native lifecycle remain open.

## Remaining work

The reviewed placeholder exclusion is already in public `main` through `54f0237`;
public snapshot publication and green hosted CI are recorded in the existing
publication review. Their stale open TODO entries were removed. Historical
failures, paused checkpoints and exact prior binary evidence remain unchanged.

Trusted provisioning reconciliation still needs independent project and simulator
storage evidence; the helper's own cleanup report is insufficient. Exact CPU
discovery, intermittent card-move denial and all other unfinished workstreams
remain open. Offline checks do not establish native or release acceptance.
