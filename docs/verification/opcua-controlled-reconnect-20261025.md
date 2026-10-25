# Controlled OPC UA acquisition reconnect

Polling and subscriptions can opt into one to three attempts to reactivate their
original live session after a transport interruption or timeout. Omission retains
the existing stop-on-error behavior. Recovery retains the original capture,
duration and storage limits, certificate pin, protected credential admission,
session ownership and uncertain remote-state handling. It cannot create another
session, transfer a subscription or resume after Host restart.

See the [runtime workflow](../reference/opcua-runtime.md#controlled-acquisition-reconnect),
[polling evidence](../reference/opcua-runtime-capture.md#optional-transport-recovery)
and [subscription evidence](../reference/opcua-runtime-subscriptions.md#recovery-evidence).

## Local validation

The encrypted loopback managed suite passed 896 checks. Recovery coverage includes:

- An opaque loopback TCP relay drops the original active connection during a
  read. The client reactivates the same session, retains the failed read and
  downtime, continues values and confirms explicit cleanup.
- A real fixture-server restart preserves its certificate and endpoint but loses
  its sessions. The client cannot replace the original session or invent resumed
  values; unresolved ownership remains visible.
- Disabled recovery, exhausted attempts, changed namespace/server-start identity,
  expired sessions, authentication denial, retired credentials and cancellation
  during backoff refuse further acquisition.
- Lost Publish responses are recovered through the existing subscription's
  Republish service before normal Publish resumes. Original values, sequences,
  acknowledgements, gaps and unavailable messages remain separate evidence.
  An unavailable drain probe consumes the existing Republish budget without
  inventing an additional missing sequence.
- Rehashed retained-record controls reject invented attempt numbers, data after
  failed recovery and Publish before the recovery drain has completed.

The actual-Host harnesses in `qualify_opcua_capture.py` and
`qualify_opcua_subscription.py` exercise protected Windows login, recovery events,
exact JSON/CSV downloads, request bounds and unchanged retained evidence across a
second Host lifetime. They audit that recovery performs ActivateSession without
CreateSession, subscription transfer, value writes or method calls. The full
portable-package check runs these harnesses against its relocated Host and retains
source/binary hashes and process-settlement receipts.

The focused Python contract tests passed 18 tests, including matching recovery
event definitions, permitted request bounds, default compatibility and rejection
of unsupported control options. Builds passed without warnings or errors.

Local receipts are preserved under
`D:/Entwicklung/TIA_MCP-public/artifacts/opcua-reconnect-20261025`.
Intermediate failed checks remain separate from final passing receipts.

## Limits

This validates software behavior against a synthetic encrypted loopback server.
It does not qualify a physical CPU, Siemens runtime configuration, license,
production network, server retransmission capacity or complete sampling during an
outage. A successful reconnect can still have unknown remote data loss. Native,
physical-device and release acceptance remain separate gates.
