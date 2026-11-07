# Cancelled engineering calls: native qualification (7 November 2026)

Evidence: `local.bridge.cancellation.20261107`
([summary](../../evidence/local.bridge.cancellation.20261107.json)), issue
[#217](https://github.com/Sparkz400/TIA_MCP/issues/217).

## Before

A cancelled or timed-out bridge request that had reached the worker faulted the
Host: every later call, `change_status` included, failed with `BridgeFaulted`
until the Host was restarted. The worker kept going and often finished, so the
reported state ("interrupted") and the project disagreed. This matches the
4 November session.

## Change

The bridge keeps the worker connection and the pending read, which is never
cancelled. Later calls are refused with `PreviousOperationRunning` and send
nothing. Once the response arrives it is recorded (`bridgeLateResponse` on the
Host's stderr) and the session continues. The admission dispatcher's lease release
(`mutation_end`) is held while the request runs and delivered right after its
response. A lost connection still faults the bridge. Nothing is replayed.

## Run

`scripts/qualify_cancellation.py` in one owned scratch (TIA Portal V21 Update 3,
CPU 1511-1 PN):

| Step | Result |
| --- | --- |
| `change_apply` of a `tag_create`, cancelled with `notifications/cancelled` once the journal showed native work | Apply continued in TIA |
| `change_status` while it ran | `PreviousOperationRunning`, nothing sent |
| `change_status` afterwards | `applied`, one `mutation_applied`, project saved |
| Host log | Late `mutation_dispatch` response; held `mutation_end` delivered |
| Same idempotency key | `ResourceBusy` naming the `outcomeUnknown` admission and `engineering_admission_reconcile` |
| Fresh owned TIA, `engineering_admission_reconcile` | Released without replay |
| Reopen and a further `tag_create` | Applied; the first tag exists once |

## Not covered

One cancellation point. Cancellation before dispatch, during compile or save, and
a worker crash during the interrupted request are covered by managed tests only.
The cancelled dispatch's admission still needs the existing fresh-instance
reconciliation.
