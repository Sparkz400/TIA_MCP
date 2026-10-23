# First contained native startup on PCNICO — 23 October 2026

The user confirmed that the earlier native runs, including unresolved ticket
`090f8915a47c401eb70201c452af2585`, happened on another machine or VM, and asked
to treat this PC as a clean start. That ticket and its evidence are not on this PC;
nothing here admits, clears or reconciles it.

## Setup

- TIA Portal V21 is installed under `D:\Arbeit\Siemens\Portal V21`, not the default
  `C:\Program Files` location. The user is in `Siemens TIA Openness`.
- The Debug worker `src/TiaMcp.Openness.V21/bin/Debug/net48/TiaMcp.Openness.V21.exe`
  is registered in the Openness AllowList by the build hook (commit `7d86eaa`); the
  Entry hash matched the executable before the runs.
- The native dispatch guard registry `%LOCALAPPDATA%\TiaMcp\native-dispatch-guard`
  was created empty. Its `dispatch.lock` was held for each run, and the scan found
  no tickets. No ticket was created: the exact startup harness produces process-tree
  exit proof, not the owned-job receipt `native_dispatch_guard.complete` requires.
- Host rebuilt in Debug from `7d86eaa` with zero warnings/errors.

## Attempt 01 — worker never connected (held, stopped)

`artifacts/first-native-startup-20261023`. The worker exited with code 1 in
under 1 ms ("Installed TIA V21 Openness Base assembly not found") because the
harness did not pass `--api-dir`. The exact tree never saw a worker and entered its
designed startup-inventory hold (`startup-inventory-blocked.json`, SHA-256
`4796f367039c125bfba18cf29eceaf289d84b7d18b941dceb05203aa843df255`). No TIA
process was started. The coordinator was stopped after the owned Host (PID 31796)
was confirmed to have no children; the Host then exited and no tickets remained.
`qualify_exact_worker_startup.py` now accepts an optional `--api-dir` passed to the Host.

## Attempt 02 — Openness connection, Portal self-terminated

`artifacts/first-native-startup-20261023-02`, `result.json` SHA-256
`8c497a0cc9fa5679c791003d3ea77f5cf7ae32f73d978b3614ef9a96aeb40851`.

- `tia_discover` passed; the exact tree captured Host 30708, worker 6772 and the
  owned `Siemens.Automation.Portal.exe` 4012 with verified ancestry.
- No Openness access prompt or `EngineeringSecurityException` occurred.
- `tia_start` failed after 15.8 s: `EngineeringError` "Connection to TiaPortal failed"
  (`Siemens.Engineering.NonRecoverableException`). Portal 4012 exited with code
  `0xFFFFFFFF`; Siemens diagnostics reported the process at 18:35:27 local time.
- Siemens' installed-software report then failed with: product
  `Siemens.Simatic.Sivarc:21.00.02.01` integrates into non-existing product
  `Siemens.Simatic.Hmi.Basic:21.00.02.01`. The installation lists WinCC Basic ES
  at V21.0 Upd3.
- Root cause, from Siemens' crash report bundle
  `C:\ProgramData\Siemens\Automation\Portal V21\Diagnostics\2100.0.121.1\caab8f28-2804-4647-b9f7-96b473a968ca`:
  Portal fails while opening its startup library (`OpenAndHoldLib` → `OpenProject` →
  `CheckCanOpenMainPersistence` → `ReleaseManagement.GetCompatibilityInfo` →
  `ProductVersionMemory.ResolveReferences`). The `MetaException` above surfaces as
  `InvalidOperationException` "Opening persistence System threw an exception other
  that the expected TiaPersistenceOpenFailedException". Installed SiVArc is
  `V21.0 + Upd2 Hotfix1` (21.00.0201) while WinCC Basic, Professional and Unified ES are
  Upd3 (21.00.0300).
- The user's manual UI starts at 18:43 and 18:44 crash with the identical stack
  (report `5576b31e-4ef8-4eb3-aec3-e135d8f27ba8`). This is an installation defect,
  not an Openness, AllowList or worker defect.
- All three original handles proved normal exit without forced termination; Host
  exit 0, worker exit 0. The harness reports `completed: false`.

No project was opened or created and no PLC or PLCSIM instance was touched.

## Attempt 03 — passed after SiVArc removal

The user uninstalled SiVArc (it is no longer registered) and confirmed that the
interactive Portal starts. Attempt 02 was then repeated unchanged:
`artifacts/first-native-startup-20261023-03`, `result.json` SHA-256
`a5ea8333aa026163a3f7c33fc8c1c79d4d9360f1dacc71e7c916ffc9a6f872e5`,
`startup-verification.json` SHA-256
`4c81389c98d7688b2b0137250fc58f674d02806f477a80956ff7b523e3ea03d6`.

- `tia_discover` → `tia_start` (9.2 s, owned Portal 33892, no projects, no Openness
  prompt) → `tia_session_status` → `tia_detach` (4.5 s) → `tia_discover`; the
  pre-existing instance list was unchanged.
- Host 32076, worker 15256 and Portal 33892 each exited with code 0, with
  original-handle, birth and image proof and no forced termination.
  `exactOriginalStartupTreeVerified: true`, `completed: true`.
- The guard registry still holds only `dispatch.lock` and no TIA process remains.

This qualifies one empty owned headless start/status/detach on this PC. It does not
qualify project operations, persistence, recovery or any other TODO item, and the
result still needs independent review.

## Attempt 04 — registry API path, no `--api-dir`

The worker now defaults to the directory of the `Siemens.Engineering.Base` value under
`HKLM\SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48` and falls back
to the old `C:\Program Files` path only when that value is absent. The rebuilt Debug
worker was re-registered by the build hook (hash verified). The same empty
start/status/detach then passed without `--api-dir`:
`artifacts/first-native-startup-20261023-04`, `result.json` SHA-256
`6176045dfecb15bb4967b759f9514d4c5cd8e1ba60f1223476c47ea456454612`; Host, worker and
Portal exited 0 without forced termination, `exactOriginalStartupTreeVerified: true`.

## Open

- The startup harness holds forever when the worker exits before capture; that
  is by design (explicit reconciliation) but needs an operator to notice.
