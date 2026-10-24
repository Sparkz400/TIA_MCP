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
