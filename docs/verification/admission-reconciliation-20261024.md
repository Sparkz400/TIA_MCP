# Interruption and trusted admission reconciliation — 24 October 2026

`engineering_admission_reconcile` releases an unresolved shared mutation admission
only on fresh native evidence. Otherwise the reservation stays held and the tool
returns why. It never replays, saves, powers or downloads. Provisioning admissions
are delegated to `simulation_provision_reconcile`
([provisioning](provision-reconciliation-20261024.md)).

## Rule

An admission is reconcilable when it is `outcomeUnknown`, or `dispatchIntent` left
by a Host that died. The verifier runs in an empty worker-owned TIA instance
(`tia_start`). The admission is released only when all of these hold:

- the admitted TIA process is no longer running, so the dispatch cannot still be in
  progress;
- the verification ran in a different Portal process and invoked no mutation;
- every admitted resource has exactly one fresh row that proves it `unchanged` or
  `verifiedApplied`.

Each resource is then reconciled with its own evidence file under
`artifacts/admission-reconciliation/<admission-id>/`. Any unproven row keeps the
whole admission held and its reasons are returned. A damaged journal, report or
receipt holds every row with that reason instead of failing the call.

Since PR #21, a structured failure that a live worker returns for `change_apply` or
`change_restore` ends the admission as `returned`, so the journal's own recovery state
drives `change_restore`. Only transport loss, timeouts, cancellation, unstructured
errors, and every `simulation_download` and provisioning failure become
`outcomeUnknown` and need this reconciliation.

## Evidence per operation

Project fingerprints are the location-independent `EngineeringFingerprint` (policy 2)
plus the project library revision. Hardware kinds add their configured hardware.

| Operation | Resource | Proof |
| --- | --- | --- |
| `change_apply` | project | `unchanged`: equals a fresh retrieve of the hash-verified checkpoint archive and was last saved before that archive was written (since 27 October; this also releases unsaved HMI, attribute and technology kinds). `verifiedApplied`: journal `applied` and the fresh fingerprint equals the journal's `afterFingerprint`; HMI, attribute and technology kinds cannot recompute it and stay held. [Coverage](interruption-coverage-20261027.md). |
| `change_apply`/`change_restore` | change journal | readable, bound to its directory, every recorded archive matches its hash, no unrecorded archive |
| | prepared input | not written since the dispatch intent |
| `change_restore` | original project | fresh fingerprint still equals the journal's `afterFingerprint` |
| | checkpoint archive | recorded SHA-256 |
| | recovery destination | absent, without content, or equal to a fresh retrieve of the checkpoint |
| `simulation_download` | storage | no claim, card or report (`unchanged`); or a report completed after the admission whose card manifest and program revision match (`verifiedApplied`) |
| | project | `Project.LastModified` precedes the admission (the route never saves) |
| `scratch_create` | destination | absent or empty; or a saved project with its station and the four fixture blocks |
| `project_retrieve` | archive | its hash recomputes the admitted observation |
| | destination | absent, empty or equal to a fresh retrieve of the archive |
| `library_open` | project library | the project's identity recomputes the admitted observation |
| | source library | its hash recomputes the admitted observation |
| | working copy | absent or empty; or a completed open receipt whose snapshot equals the source |
| library `change_apply` | each project / global library | project fingerprint or global-library revision equals its own retrieved checkpoint; resources without a checkpoint must not have been written since admission. New outputs stay held. |
| library `change_restore` | originals | not saved / not written since admission (recovery never writes them) |
| | archives, destinations | as for engineering restore, per checkpoint; library destinations compare library revisions |

## Native qualification

`scripts/qualify_interruption.py` uses one owned scratch project and artifact root per
scenario. It terminates either the exact worker and its owned TIA (handle and image
path verified), or the exact Host, at a journal-observed boundary. It then calls
`engineering_admission_reconcile` once from a fresh Host and fresh owned TIA. It
checks that the change journal is byte-identical before and after reconciliation and
that no mutation was dispatched again.

| Scenario | Interruption | Outcome | Basis |
| --- | --- | --- | --- |
| apply while applying | worker + TIA | released | project equals retrieved checkpoint (unsaved mutation lost) |
| apply while checkpointing | worker + TIA | held | no complete checkpoint archive |
| apply while applying | Host (transport loss) | released | `dispatchIntent`; worker finished and saved; fresh fingerprint equals `afterFingerprint` |
| restore while preserving | worker + TIA | released | original unchanged, archive unchanged, destination without content |
| restore while retrieving | worker + TIA | held | partial destination project cannot be opened |
| project retrieve | worker + TIA | released | destination equals the archive |
| scratch create | worker + TIA | held | partial project cannot be opened |
| library master copy apply (project + global library) | worker + TIA | released | both equal their own retrieved checkpoints |
| library restore, two checkpoints | worker + TIA | released | 7 resources proven: originals not written, archives unchanged, destinations without content |
| held `simulation_download` `63974f72…` (`artifacts/f2`) | earlier worker crash | released | report completed after admission, card manifest matches; project saved 13:27:15Z, admitted 13:28:13Z |

Evidence: `evidence/local.engineering.interruption.20261024.json` (first batch,
outcome failed because of the qualifier fault below) and
`evidence/local.engineering.interruption.rerun.20261024.json` (the two affected
scenarios after their fixes).

Findings from the native runs:

- TIA returned `Project.LastModified` with `DateTimeKind.Utc` in every case. An
  unspecified kind would be read conservatively and could only hold.
- In the first restore run, the qualifier's plain journal reads blocked the worker's
  `File.Replace`, and the restore failed before it journaled `restoring`. The
  qualifier now reads with full sharing. Any external reader that opens a change
  journal without delete sharing can still fail a running mutation this way.
- The first library apply run held because the global-library checkpoint
  (`…\created\LaneELib\LaneELib.al21`) sits below its admitted directory and was not
  matched. It now is.

## Boundaries, cancellation and completed recovery

A second batch covered the remaining §9 boundaries
(`evidence/local.engineering.boundaries.20261024.json`, passed).

| Scenario | Outcome |
| --- | --- |
| MCP cancellation while applying | The next call got `BridgeFaulted`, and the worker finished its call and exited with its TIA. Reconciliation from a fresh Host **released** it: project `verifiedApplied`. |
| Concurrent change: a second Host attached to the same TIA applies between prepare and apply | `StaleRevision` before any admission; the preview stays prepared and unclaimed |
| Stale preview in the same Host after another apply | `StaleRevision`; nothing claimed or admitted |
| Owned TIA gone while idle, then apply | Refused (`EngineeringError`: TIA disposed) before any admission; a fresh session shows the project unchanged |
| `library_open` killed after its receipt | **Held**: source unchanged, working copy partial |
| Completed two-checkpoint library recovery, then fresh reopen | The recovered project keeps its fixture block. The recovered library has no master copies (its pre-change checkpoint), while the original keeps the applied `TiaMcp_IsEnabled`. |

Journal replacement now also retries sharing and lock violations, bounded at about
1.5 s (PR #28). An external reader without delete sharing no longer fails a running
mutation.

## Limits

- Interruption means process termination of the worker and its owned TIA, or of the
  Host. Power loss, disk faults and a remote worker's network transport are not
  exercised. Each scenario ran once; where a kill landed inside a phase depends on
  timing and is recorded per case.
- Refusal reasons beyond those met natively, such as a process still running, a
  foreign scope or a rewritten effect, are covered by `AdmissionReconciliationChecks`.
- Fingerprints exclude generated payload bytes and unexposed vendor data. Library
  revisions cover structure, type versions and master copies, not unexposed object
  document content.
- Protected (know-how) content is not covered natively: no protected fixture exists,
  and creating one needs a protection password and new fixture code.
- Fresh reopen after recovery compares fixture blocks and library master copies, not
  full object content.
