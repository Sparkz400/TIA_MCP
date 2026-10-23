# Native dispatch quarantine and process containment — 2026-10-20

The fixture coordinator now acquires a shared per-user native dispatch lock
before its store locks. Its durable registry is
`%LOCALAPPDATA%\TiaMcp\native-dispatch-guard`, shared across source snapshots.
An unresolved attempt blocks a subsequent window even when it names a different
store or its previous coordinator is gone. This governs the repository's
`StoreExclusiveWindow` callers, not arbitrary TIA clients or production adapters.
File-only preparation remains available.

The original failed `artifacts/hmic20c` attempt is registered with an immutable
reference to `coordinator-reconciliation-final.json`.
`artifacts/native-guard-wave2-20261020/machine-guard/quarantine-verification.json`
records a real new-window refusal before native dispatch. The original report,
configured Classic archive and failure status remain unchanged. There is no
automatic quarantine clearance or retry. PID absence and releasing an old
store lock do not resolve its uncaptured reporter lifetime.

A read-only check for retained process-event evidence also failed to supply that
lifetime: the Security log requires elevated access, and no Sysmon Operational
log is installed. `process-event-log-access.json` in the same evidence directory
retains those results. No logging configuration, privilege or process was changed.

Future fixture attempts use `CreateProcessW` with
`PROC_THREAD_ATTRIBUTE_JOB_LIST` and a restricted standard-handle inheritance
list. Job assignment occurs before execution. No fallback launches outside the
job. The coordinator keeps the original job and helper process handles; both
breakaway flags, kill-on-close and resource-termination limits remain unset.
The main editor still requires its exact retained birth/image/ancestry proof.
Observed descendants outside the job refuse admission.

A raced ephemeral observation in a newly contained attempt does not assert an
individual lifetime. Release requires every retained handle to signal, the
original helper handle to signal and original job accounting to reach zero.
The immutable terminal receipt binds the exact job name and dispatch intent.
Existing incomplete captures cannot be upgraded using a new job.

This covers ordinary inherited `CreateProcess` descendants. WMI/brokered
creation and explicit alternate parents are outside the guarantee. Siemens
compatibility with this containment has not been exercised because the old
native quarantine is still unresolved. No native qualification is promoted.

Validation:

- 33 fixture coordinator tests, 27 existing store/recovery tests and 12 new
  quarantine controls passed.
- `artifacts/native-guard-wave2-20261020/jobs-05/verification.json`: 22 actual
  Windows process checks passed, including 24 short-lived child interpreters,
  an exited parent with a live child, refusal to close a live job, refusal of
  breakaway, exclusion of the unrelated coordinator and an atomic launch
  failure with no helper and refusal to retry that same job.
- Each expected test interpreter independently queried membership in the
  original job. Raw accounting is retained separately; it is not treated as
  a unique PID count. Reused PIDs are distinguished by creation time.
- Earlier `jobs-01` through `jobs-03` failures remain retained: the harness
  initially assumed one accounting entry per interpreter and inherited output
  capture. The installed Python launcher adds a process, and explicit stream
  inheritance was needed for child observations. No failed run was relabeled.

The independent review additionally required explicit containment-from-creation
on releasable intents. Legacy intents without that field, or carrying incomplete
old lifetime evidence, refuse even a valid same-name new job terminal receipt.
Existing translation coordinators that lock more than sixteen stores share the
exact still-held guard owner across their batches; the owner cannot release
while a dependent batch remains held.

Windows behavior follows Microsoft's [job object documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects),
[process creation attributes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
and [job accounting definition](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_accounting_information).
The atomic creation rationale is described in
[Microsoft's process/job example](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812/).
