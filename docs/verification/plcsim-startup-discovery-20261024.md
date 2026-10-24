# Owned empty-CPU discovery — 24 October 2026

## Result

The owned PLCSIM Advanced V8 CPU started successfully in STOP and reported
`192.168.0.1` with `CommunicationInterface: Softbus`. TIA V21's explicit
`PN/IE / PLCSIM (1)` route returned only the same all-zero placeholder in every
one of 61 snapshots across 62,348 ms. Increasing the production ten-second wait
alone is therefore not supported as a repair. No credential, project download,
RUN command, address assignment or network configuration was attempted.

The CPU was powered OFF and its registration was confirmed absent. The project
and Portal connection closed, and the worker exited with code 0. The owned
file-storage helper remained alive after the coordinator's 45-second terminal
deadline. Exact retained-handle cleanup terminated that helper; all ten members
of the original process job are now terminal. The overall coordinator result is
**failed**, with `normalLifecyclePassed: false`. This is discovery evidence,
not a successful provisioning, requested-stop, recovery or runtime qualification.

## Retained evidence

All paths are under this checkout's ignored `artifacts/` directory.

- `p24-startup02/native/result.json`: complete discovery samples and owned-CPU
  cleanup. CPU catalog target: `OrderNumber:6ES7 511-1AL03-0AB0/V4.1`.
- `p24-startup02/request.json`: exact worker and coordinator fingerprints.
  The native observation used the isolated `n24q` Release worker.
- `p24-startup02/window-intent.json`, `editor-ready.json`, `process-*-start.json`:
  held native dispatch/store locks and original birth/image-checked process handles.
- `p24-startup02/owned-job-terminal.json`: original helper signaled, ten total
  processes, zero active processes; no kill-on-close or breakaway configuration.
- `p24-startup02/process-exits.json`, `result.json`,
  `process-cleanup-command-01.json`: actual exits and explicit forced cleanup,
  with no promotion of the original attempt to a normal lifecycle pass.
- `p24-startup02/project-file-access-before-cleanup.json`: all eight existing
  project files opened successfully for exclusive read after project/Portal close
  but before helper termination. This proves those file opens, not native reopen,
  named-lock release or full project preservation.

The first extension attempt, `p24-startup`, failed before project creation because
Siemens rejected the `DirectoryInfo` returned by `Directory.CreateDirectory` as
relative. Creating the directory and passing a new absolute `DirectoryInfo`, as
the existing scratch-project implementation does, fixed the probe. That failed
attempt and its forced helper cleanup remain retained. The probe also no longer
reads the simulator initialization property from `finally` when its main path
never reached simulator initialization; that read started Runtime Manager in
the first failed attempt.

The experimental coordinator reused the HMI process monitor. Its
`explicitNativeProcessDisposeInvoked` field is not evidence of a call to
`TiaPortalProcess.Dispose`: this probe disposes only its own `TiaPortal`
connection. The final source names the observation
`ownedPortalConnectionDisposed` to distinguish it. Forced cleanup, failed normal
lifecycle and actual original-handle exits remain explicit in the raw receipt.

## Diagnostic implementation and checks

`--plcsim-discovery-probe <directory>` keeps the original OFF-only behavior.
The new explicit `--plcsim-startup-discovery-probe <directory>` additionally
starts one empty owned CPU, records 61 one-second-spaced snapshots and powers it
off. It requires zero preexisting registrations and existing Softbus mode,
checks exact registration identity and operating state around every observation,
rejects trial/Test Suite startup warnings, and never selects a discovered device.
Only its own instance is eligible for cleanup. Existing output is refused.
Startup intent and returned Portal identity support independent lifetime capture.

Do not launch this mode concurrently with other native work. The retained
coordinator scripts in `todo-resume-20261024/` demonstrate the existing shared
guard and contained-process workflow; they are evidence for these exact attempts,
not general qualification runners. Future callers should pass
`explicit_native_dispose=False` to the reused monitor because no SDK process
descriptor is disposed by this probe.

The final `n24r` worker builds with zero warnings/errors and passes the compiled
worker lifecycle guard. Both diagnostic modes refuse a nonempty output directory
before writing startup intent and preserve the existing sentinel. All 30 API
binding tests pass. Only the two changed source fingerprints were refreshed;
historical evidence rows remain intact. Binding validation permits explicitly
reported unavailable historical evidence (106 paths) in this public checkout and makes no
release-acceptance claim. Logs are in `todo-resume-20261024/`.

The final source differs from the native `n24q` observation only in its explanatory
comment and connection-disposal field name. The native attempt was not repeated
for that metadata correction. No full offline aggregate was repeated here.

A final scan while holding the shared dispatch lock validates both native tickets
as terminal; neither remains unresolved (`native-guard-after-probes.json`). No
Siemens/worker process from these runs remains. Only the primary `main` checkout
exists; no task branch or worktree needs removal. Native outputs and binaries are
retained as evidence. Automatic approval review rejected removal of the disposable
`artifacts/ctx7-cache` and `artifacts/p24-output-guard` folders with "blocked by
policy"; both are retained rather than bypassing that rejection.

## Next investigation

### Visible Portal comparison and diagnostic mode admission

`artifacts/p24-ui/` records the explicit visible probe using the `n24w` worker.
It creates its own project and OFF CPU; three pre-runtime and three initialized
runtime observations reproduce the absent-route to zero-address-placeholder
transition through both providers. It then fails during the owned-OFF
observation with Siemens `NonRecoverableException`, before power-on. The owned
registration is removed in cleanup. The visible project was inspected through
Computer Use, including switching from portal view to project view, but no
successful UI accessible-device comparison was obtained. The generic exception
does not identify its cause or establish that UI interaction caused it.

The worker exits 1; the main Portal, its SearchReplace background Portal and
their crash detectors exit `0xFFFFFFFF`. The diagnostic-report child exits zero,
but its command-line-referenced stack and undo files are no longer present at
inspection. The storage helper exits naturally with code zero about five minutes
after the worker. All ten captured handles and all twelve original job members
are terminal; no forced termination was used. The overall attempt remains failed
for the native exception, abnormal exits and missed normal 45-second deadline.
The exact receipt is `p24-ui/result.json`; natural drain does not promote it to
normal lifecycle acceptance or prove project reopen.

`--plcsim-ui-discovery-probe <directory>` is the explicit diagnostic UI variant;
the existing two modes remain headless. It has the same singleton ownership,
Softbus, output-preservation and cleanup checks. Its UI/native behavior remains
unqualified after this failed attempt. The mode is not used by ordinary MCP
startup and does not change production provisioning or target selection.

Review exposed a separate argument-admission defect: multiple diagnostic flags
could overwrite the output path while retaining the startup flag, and a probe
could silently override a bridge pipe or qualification mode. The final parser
refuses these combinations before loading the Siemens API or creating output.
`scripts/verify_worker_probe_arguments.py` reproduces the prior parser failure
using a deliberately absent API directory, then passes 33 ordered/duplicate/mixed
cases against `n24x`. The check is included in `scripts/Test.ps1`. The final
worker builds without warnings/errors and passes the compiled lifecycle guard;
the test runner parses successfully. These checks do not start TIA.

The visible native observation used `n24w`, before this argument-only correction.
Its request and coordinator remain pinned under `p24-ui/`; logs and the failing
negative control are retained in `artifacts/todo-resume-20261024/`.

### Provider comparison and natural helper exit

The subsequent `artifacts/p24-provider/` probe uses the `n24s` Release worker
and compares `OnlineProvider.Configuration` with the project's
`StationUploadProvider.Configuration`. Both return only the same placeholder in
all 61 paired observations over 63,322 ms while the sole owned CPU is in STOP
at `192.168.0.1`. Both configured-address collections are empty; only the PLC
provider exposes target interface `1 X1`. Changing the discovery provider alone
does not resolve this PC's missing endpoint. No station upload is performed.

The native observation and owned CPU cleanup pass. The storage helper exits
naturally with code zero about five minutes after the worker, just beyond the
coordinator's explicit 300-second diagnostic deadline. All eight captured process
handles exit zero and the job has ten total members, zero active. No forced
cleanup occurs in this comparison. The overall attempt remains **failed** for
the missed deadline; this single observation is not a guaranteed shutdown bound
or proof of native reopen. It does not relax the normal 45-second lifecycle gate.
The monitor correctly records `explicitNativeProcessDisposeInvoked: false`.

The comparison worker builds without warnings/errors and passes its compiled
lifecycle guard. Process enumeration now disposes each temporary process wrapper.
Its configuration comparison uses the documented public read-only discovery
collections; the production route and exact-target guard remain unchanged.

Separately, the [actual MCP empty-CPU lifecycle](simulator-startup-20261024.md)
passes without starting TIA. That success does not resolve TIA discovery or the
storage-helper lifecycle above.

Investigate Siemens discovery semantics and the local Softbus route independently
of a timeout increase. Keep exact target matching and uncertain-outcome admission
in place. Independently qualify normal project/worker/file-storage shutdown and
native reopen before another full runtime batch.

Siemens documents Softbus as the default local simulation route, separate from
TCP/IP adapter configuration: [Local communication](https://docs.tia.siemens.cloud/r/simatic_s7_1500_et_200mp_manual_collection_enus_21/comprehensive-information/cpu-function-manuals/s7-plcsim-advanced/communication-paths/local-communication).
The installed V21 Base XML reference confirms `GetAccessibleDevices` returns
accessible-device descriptors but supplies no convergence guarantee. Context7's
CLI failed to launch in three attempts; no Context7 documentation was retrieved.

### UI search refresh comparison

`artifacts/p24-ui2/` uses the unchanged `n24al` worker in its explicit visible
diagnostic mode, with a new project, sole owned empty CPU and retained process
job. Unlike the earlier visible attempt, no UI input occurs before the
coordinator confirms the owned CPU is in STOP. The coordinator pins its sources
and every adjacent worker file. No credential, program download, RUN command,
network configuration or uncertain-admission release is involved.

The read-only UI sequence opens Project view, expands **Online access / PLCSIM
[PN/IE]**, then double-clicks **Update accessible devices**. The captured TIA
information pane records the search start at `2026-10-24 04:50:57 +02:00`.
`ui-accessible-device.png` shows the resulting `S7-1500 CPU:192.168.0.1` node.
The separate `ui-observation.json` timestamp is the later evidence-save time,
not the action time.

Both API providers produce 52 placeholder samples, two empty samples during
the search, then seven endpoint samples. The first endpoint appears at
`02:51:00.5785102Z` for the PLC provider and `02:51:00.8757137Z` for the project
provider. The PLC provider's first endpoint lacks a device-series value; its
next sample includes `S7-1500 CPU`. All endpoint samples carry address
`192.168.0.1` and MAC `FF-FF-C0-A8-00-01`; the exact owned instance remains STOP
with that address and Softbus throughout the 61-sample observation. The
independent `discovery-assessment.json` validates those counts and binds the
native report and UI evidence by hash. OFF and unregister then succeed, and
both routes disappear in all three post-unregister samples.

This establishes a usable local Softbus route in this run and a discovery
transition following the scoped UI refresh. It suggests cached discovery data;
it does not establish that repeated `GetAccessibleDevices()` calls themselves
perform a new search, guarantee freshness, or qualify headless provisioning.
No production target-selection guard or timeout was relaxed.

The installed V8 manual (`A5E37039512-AK`, 11/2025, page 23) explicitly includes
CPU 1511-1 PN firmware through V4.1. It therefore does not justify downgrading
the selected V4.1 CPU to address this failure. Pages 41 and 59 describe the local
Softbus route. Source hash and selected page extracts are retained in
`artifacts/todo-resume-20261024/plcsim-installed-manual-review.json`; the public
[Siemens manual](https://support.industry.siemens.com/cs/attachments/109997788/s7-plcsim_advanced_function_manual_en-US_en-US.pdf)
identifies the same edition.

A fresh metadata-only inventory of the installed public V21 API covers 15
assemblies, 2,455 types and 32,286 members. The concrete
`ConfigurationPcInterface` exposes `GetAccessibleDevices()` without a refresh
parameter; no separate discovery refresh member was found in the inventory.
`discovery-public-api-metadata.json` and its log are retained beside the manual
review. This does not rule out runtime self-description or another documented
workflow. The next investigation is a supported refresh operation, including
its isolation and freshness evidence, rather than another unchanged timeout
probe.

The official V21 [accessible-device API documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-configuration-accessible-devices)
describes a snapshot from one selected interface and the four returned fields;
its example supplies no explicit refresh argument. This describes the API
contract, not a proven explanation of the observed UI/API transition.

The comparison's worker exits zero after closing the project and disconnecting.
Its visible Portal remains open, so the exact original UI window is closed with
Alt+F4 after the UI confirms `Project closed`. The storage helper subsequently
exits naturally. All 13 job members are terminal and all 11 retained handles
signal; one background crash detector exits `0xFFFFFFFF`, while the other ten
retained exits are zero. The overall coordinator therefore remains failed for
the normal deadline overrun and nonzero exit. Source/worker hashes are unchanged;
no OS process termination is requested. UI close intent/return receipts and the
closed-project screenshot remain alongside the original result.

The visible diagnostic had used attachment disposal without the existing
`OwnedQualificationPortal` process owner. It now reuses that owner exclusively
for the new visible Portal it constructs. Headless diagnostics retain their
previous connection lifetime; discovery and user-attached Portal descriptors
remain disposal-free. The result records completion of explicit owned SDK
process disposal separately from connection disposal. The `n24am` worker builds
without warnings/errors, passes the compiled lifecycle boundary guard and all
33 diagnostic argument-refusal checks. Source bindings validate while preserving
all 117 historical evidence rows, including 106 unavailable local inputs.

`artifacts/p24-ui3/` exercises that rebuilt worker with no UI input. All 61
paired samples over 63,037 ms retain the placeholder while the exact empty CPU
is STOP at `192.168.0.1`. OFF, unregister, project close and explicit owned SDK
process disposal complete successfully. Both Portal processes exit without a
UI close action. `probe-assessment.json` checks the full observation sequence
and preserves the native-report hash. This confirms the visible diagnostic's
closure fix and provides a no-search comparison; it does not qualify automatic
discovery. The remaining file-storage helper again exceeds the normal deadline.

The rebuilt attempt's final job has 13 total members and zero active members;
all ten retained handles signal and close. Nine retained exit codes are zero;
the background Portal crash detector returns `0xFFFFFFFF` again. The storage
helper exits naturally about five minutes after the worker. This remains failed
lifecycle evidence, even though explicit owned SDK closure fixes the lingering
visible Portal. No OS termination command or UI input is used in this run.
Source and binary pins remain unchanged. The final locked registry scan validates
all 14 local dispatch tickets as terminal. The independent
`discovery-ui-lifecycle-assessment.json` records both attempts' exit codes and
failed lifecycle outcomes; `discovery-ui-native-guard.json` retains the scan.

Only the primary `main` checkout exists; no task branch or unused worktree was
created. Unique native reports, UI captures, worker builds and manual/API evidence
are retained. No full offline aggregate was rerun for this diagnostic lifetime
change, and no runtime or release acceptance is inferred.

### Delayed first enumeration and runtime metadata

The explicit `--plcsim-late-discovery-probe <new-directory>` mode tests whether
pre-start connection-configuration discovery affects later observations. It
creates the same isolated scratch project and sole owned empty CPU, but skips
all three pre-start discovery phases. Each skipped phase and the top-level
`preStartDiscoverySkipped` field explicitly record the missing isolation proof;
this diagnostic cannot authorize provisioning. Normal production provisioning
and its pre-start isolation checks are unchanged. The first connection-
configuration enumeration occurs only after the exact owned CPU reaches STOP.

`artifacts/p24-late/` uses the `n24an` worker headlessly, with no UI action.
Both providers return the exact endpoint in every one of 61 paired samples over
65,033 ms; the first pair completes at 1,722 ms. Every sample reports address
`192.168.0.1`, MAC `FF-FF-C0-A8-00-01`, series `S7-1500 CPU`, and the matching
owned instance's address and Softbus interface. The independent
`independent-assessment.json` checks the entire sequence, skipped phases and
initial empty registration list, and binds the native report by hash. This
supports a startup-order/cache hypothesis; it does not make omission of the
production isolation check an acceptable remedy.

After the samples, public runtime self-description reads action names, return
types and attribute descriptors on each provider, its connection configuration,
PN/IE mode and PLCSIM interface. All eight metadata observations are bounded and
untruncated. Both PC interfaces expose only `GetAccessibleDevices` as an action;
both modes expose none, and both configurations expose the two
`ApplyConfiguration` overloads. No separate refresh action is advertised by
these targets. No described action is invoked by the metadata reader, and no
attribute value is written. Siemens documents this read-only descriptor route
under [self-description](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/self-description-support-for-attributes-navigators-actions-and-services).
Context7 again fails to launch; no Context7 answer was substituted for native
metadata or the official reference.

OFF, unregister and connection disposal succeed. All ten job members are
terminal; all eight retained handles exit zero and close. The storage helper
again misses the normal deadline, so the overall coordinator remains failed.
Source and worker-dependency pins remain unchanged. This establishes a headless
endpoint observation in this specific ordering, not provisioning, full runtime,
normal shutdown or preservation acceptance.

The final diagnostic also records a first lookup through `DownloadProvider`
after the 61 startup samples, then three follow-up observations through the
original two providers. This lets the next comparison retain its pre-start
baseline while testing a previously unqueried configuration. It performs no
download, connection apply, credential submission or PLC write. The final
`n24ao` worker builds cleanly, passes the compiled lifecycle guard and all 48
ordered/mixed/duplicate diagnostic-mode refusal checks. The previous worker
fails the newly added mode's negative control before Siemens loading. Source
bindings validate with all historical evidence rows preserved.

The fresh `artifacts/p24-fresh-provider/` comparison retains all pre-start
observations. All 61 startup pairs over 63,346 ms, the first DownloadProvider
lookup, and three subsequent original-provider pairs return only the
placeholder. Its independent assessment checks all 129 device snapshots and
pins the native report. No UI input, download or connection apply occurs.
A previously unqueried provider therefore does not refresh the warm discovery
cache in this experiment.

OFF, unregister and connection disposal complete. All ten job members are
terminal and all eight retained handles exit zero and close without forced
termination. Source and worker pins remain unchanged. The normal deadline
still fails because the storage helper exits late. The final guard assessment
was read under the registry lock after the coordinator exited; these cleanup
results do not release any earlier uncertain provisioning reservation.

## Offline route selection and project reload

The explicit `--plcsim-configured-discovery-probe` mode retains the pre-start
observations and the 61 paired startup samples. After the fresh-provider
comparison, it selects only the sole named target interface exposed by the
scratch CPU's PN/IE / PLCSIM (1) route. It requires that exact owned simulator
to remain the sole registered instance in STOP on Softbus, and requires the
scratch connection to remain offline before and after configuration. It never
calls GoOnline or downloads, supplies credentials, or changes network settings.

Siemens documents `ApplyConfiguration` as selecting connection parameters before
the separate online step; it overwrites the session's previous connection
selection. This is confined to the new scratch project. The installed V21 SDK
also exposes the target-interface overload. Context7's launcher failed again,
so the reference used was Siemens' official [connection parameters documentation](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/functions-for-accessing-plc-service/accessing-parameters-of-an-online-connection).

`artifacts/p24-configured/`, using worker `n24ap`, applies the exact enumerated
`1 X1` target successfully while remaining Offline. All three subsequent paired
observations still return the placeholder. Its independent assessment checks
159 device observations, including twelve absent-route observations before API
initialization and after unregister. The first audit assertion incorrectly
expected a placeholder in those absent-route phases; the assessment records
that correction. The native result itself is unchanged.

OFF, unregister and connection disposal succeed. All ten process-job members
are terminal; all eight retained handles exit zero and close without automatic
termination. The normal storage-helper deadline remains failed. Source and
worker pins are unchanged, and a final locked registry scan finds seventeen
terminal job tickets. Earlier uncertain provisioning reservations remain held.

The final `n24aq` diagnostic adds a distinct observation after saving, closing
and reopening the same scratch project. It checks the saved path, single-device
identity, unmodified state, fresh providers and offline connection before
sampling again. Both workers build without warnings or errors and pass the
compiled lifecycle guard and all 65 mixed/duplicate-mode refusal checks. The
previous worker fails the new-mode negative control without Siemens loading.
Source bindings validate with all 117 historical evidence rows retained;
106 unavailable historical inputs remain explicit.

`artifacts/p24-reload/` uses that final worker. The route is again applied
successfully while offline. Saving, closing and reopening the exact scratch
project all return successfully, with the same path, one named device and no
unsaved changes. All three paired samples after reopening still contain only
the placeholder. The independent assessment checks all 165 device observations
and the saved/closed/reopened receipts, and binds the unchanged native report.
The owned CPU is subsequently powered OFF and unregistered.

These experiments rule out route selection and project reload as sufficient
refresh mechanisms in the observed Portal session. They do not qualify full
project preservation, production provisioning or runtime behavior. A fresh
Portal-session comparison retaining the pre-start isolation evidence remains
the next useful lifetime experiment; no production check has been removed.

The reload comparison's original process job subsequently drains naturally:
ten total members, zero active members, and all eight retained handles closed
with exit code zero. No automatic termination occurs. Source and worker pins
match, and the final locked registry scan finds eighteen terminal tickets.
Its only process-proof error is the exceeded original terminal deadline; late
exit is cleanup evidence only. Both comparison fixtures and their binaries are
retained as unique evidence. No extra branch or worktree was created.

## Fresh Portal process comparison

The explicit `--plcsim-fresh-portal-discovery-probe` extends the configured
comparison with a second newly created headless Portal process. The original
Portal remains alive, but closes the scratch project before the second opens
that exact saved path. Both sessions require one matching device, saved state,
offline providers and the same sole owned STOP/Softbus simulator. The second
session observes three paired samples, closes its project and disposes its
connection. The original session then reopens the same project and records
three further paired samples before powering OFF and unregistering the CPU.
This compares two Portal processes in one Openness worker; it does not compare
two independent worker processes or retire every original helper first.

Before the second session may open the project, its reported process ID and
fresh nonce must be acknowledged by the coordinator. The coordinator requires
a distinct live Portal process, direct ancestry from the original owned worker,
verified birth/image, original retained handle and membership in the existing
non-breakaway job. It publishes the acknowledgement by rename after a durable
write. Missing second-process capture leaves the process-tree proof incomplete.
The original editor identity is never replaced in the lifecycle tracker.

The `n24ar` worker builds without warnings/errors and passes the compiled
lifecycle guard plus all 84 argument-refusal checks. Nine independent checks
exercise the new coordinator capture predicate, including nonce, ancestry,
birth, duplicate identity, already-exited process and incomplete-capture refusal.
The previous worker fails the new-mode negative control without loading Siemens.
Siemens documents creating multiple Portal instances through Openness in the
official [V21 connection reference](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/connecting-to-the-tia-portal).
Context7's launcher remains unavailable; the official reference and installed
SDK were used directly.

`artifacts/p24-fresh-portal/` succeeds in observing the exact endpoint in all
three paired samples in the fresh Portal: address `192.168.0.1`, MAC
`FF-FF-C0-A8-00-01`, series `S7-1500 CPU`. All three paired samples back in the
original Portal still show the placeholder. The independent assessment checks
177 device observations, including six exact endpoint observations and twelve
absent-route observations, plus the nonce/handle acknowledgement. Pre-start
discovery was retained, and the owned CPU was powered OFF and unregistered.
This strongly supports Portal-process-local discovery state in this sequence.

All twelve job members subsequently exit naturally. Ten retained handles,
including both separately captured Portal processes, exit zero and close.
The storage-helper deadline remains failed. All coordinator source and worker
pins match, and the final locked registry scan finds nineteen terminal tickets.

### Independent empty discovery project

The next variant first creates an empty project in the second Portal and reads
its StationUploadProvider while the original scratch project remains open.
It then performs the saved-project comparison above. The extra project path is
declared in the coordinator's resource scope before native dispatch. This tests
an observer route that would not require closing the production scratch project.

The first `n24as` attempt in `artifacts/p24-empty-discovery/` fails before that
observation: Siemens rejects the result of `Directory.CreateDirectory` passed
directly to `Projects.Create` as a relative `TargetDirectory`. Its owned CPU is
powered OFF and unregistered during failure cleanup; no discovery-only result
is inferred from this failed attempt.

An exact .NET Framework probe reproduces the argument distinction:
`Directory.CreateDirectory(absolutePath).FullName` is absolute, but its
`ToString()` is only the final directory name. An explicit
`new DirectoryInfo(absolutePath)` retains the full path in both properties.
The initial PowerShell runtime comparison did not reproduce this distinction;
the Framework executable does (`artifacts/todo-resume-20261024/directoryinfo-shape.log`).
The diagnostic now creates the directory separately and passes an explicitly
constructed absolute DirectoryInfo, matching the established project-creation
code. No other direct SDK call with this pattern was found in the worker.
The final `n24at` worker builds cleanly and passes the compiled lifecycle guard
and all 84 argument checks.

The failed `n24as` process job is terminal: twelve total members, ten retained
handles, nine zero exits and the expected worker exit code 1. Both exact Portal
handles are captured and closed; no process is forcibly terminated. The normal
deadline and nonzero worker outcome remain failed. A locked final scan finds
twenty terminal job tickets; no uncertain provisioning reservation is released.

The fresh `artifacts/p24-empty-discovery-fixed/` run with `n24at` confirms the
path repair and the independent observer route. Its empty project's first three
StationUploadProvider snapshots all show the exact `192.168.0.1` endpoint while
the original saved scratch project remains open. The empty project has no
configured addresses or target interfaces. After that project closes, opening
the original saved project in the second Portal also returns the endpoint in
all three paired samples. The original Portal remains stale in all three paired
follow-up samples. The independent assessment checks all 180 device observations,
including nine exact endpoint observations, the original-open assertion and
the second-process handle receipt. OFF, unregister and both Portal connection
disposals return successfully.

This proves an automatic diagnostic discovery route that preserves the
pre-start observations and does not need to close the working project for the
first fresh read. It does not yet qualify production credential provisioning.
Integration must declare the extra discovery project before shared mutation
dispatch, retain its process/lifecycle evidence, preserve the original
project/PLC/Runtime Manager baseline binding and five-minute lifetime, and use
fresh endpoint observations at the existing post-start and credential-boundary
identity checks. The actual connection and authentication must remain bound to
the original scratch PLC and sole PLCSIM route; the observer must never select
an alternative network interface or remove the current ambiguity refusals.

The corrected run's process job is now terminal: twelve total members, ten
retained zero-exit handles (including both Portal processes), no forced
termination, and unchanged source/worker pins. The final locked registry scan
finds twenty-one terminal tickets. Its original storage-helper deadline still
fails; successful late cleanup does not qualify normal shutdown. All three
comparison roots and worker builds remain retained as unique evidence. The
final source bindings validate with all 117 historical rows preserved and
106 unavailable historical inputs still explicit. No extra worktree or branch
was created, and no production qualification profile was promoted.

## Original Portal connection while its discovery is stale

The integration above assumes the original Portal can still connect over its
sole PLCSIM route although its own discovery shows only the placeholder. The
`--plcsim-stale-connect-probe` mode tests exactly that. It runs the
independent-empty-project flow, then closes the discovery project and disposes
the second Portal without the saved-project comparison, so the original scratch
project stays open throughout. In the original Portal it re-applies the sole
target interface, attaches an `OnlineLegitimation` handler that answers any TLS
challenge `NonTrusted` and supplies nothing for any other challenge, calls
`GoOnline` once, always calls `GoOffline` if needed and removes the handler.
The probe has no credential at all.

Worker `n24au` (`TiaMcp.Openness.V21.exe` SHA-256
`0c71eac49dd2742b9984af4a4757a184023ba6dbe75f8f9a8ece0e31c6d5704c`) builds with
no warnings or errors, passes the compiled lifecycle guard and all 105
argument-refusal checks; `n24at` fails the new-mode check as negative control.
The run in `artifacts/p24-stale-connect/` (worker result SHA-256
`6ac39cff9530b393310c169f482da411f9fc6afffee5e153088442a2d1e74bf7`, coordinator
result `0502fc6c3a29453f9dace1ae26fd24a24f814564830fe43ae376d364639a79f5`) used
`artifacts/todo-resume-20261024/run_stale_connect_probe.py`, derived from the
fixed empty-discovery runner.

- The fresh Portal's empty project showed `192.168.0.1` / `S7-1500 CPU` in all
  three samples while the original project stayed open; the second Portal's
  handle receipt was verified.
- Immediately before and after the attempt, the original Portal's discovery
  showed only the all-zero placeholder.
- `ApplyConfiguration("1 X1")` returned true with the connection Offline.
  `GoOnline` raised exactly one `TlsVerificationConfiguration` callback (initial
  selection `NonVerified`), was answered `NonTrusted`, and failed after 997 ms
  with "Connection to device cannot be established. The device is not trusted.
  Please check the certificate." The state stayed Offline and the handler was
  removed.
- The CPU stayed STOP around every step, was powered OFF and unregistered, and
  both Portal connections were disposed; worker exit 0.

So the original Portal's route reaches the owned CPU's TLS handshake despite its
stale discovery: the failure is the deliberate trust refusal, not a route or
discovery error. This supports the planned integration (fresh observer for
identity, original route for connection). It does not qualify provisioning:
trust, credential submission and every identity check remain to be integrated.

The process job is terminal (12 members, 0 active, none limit-terminated). All
ten retained handles exited 0 without forced termination; the coordinator again
records only "Exact terminal deadline exceeded; late exit is cleanup only" for
the file-storage helper, so `normalLifecyclePassed` stays false. The guard
ticket has its terminal receipt, and source pins and worker files are unchanged.
