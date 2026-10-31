# OPC UA method calls, conditions and the HistoryRead boundary — 31 October 2026

Follows the [feasibility note](feasibility-checks-20261031.md) and the
[HistoryRead record](opcua-history-read-feasibility-20261031.md). Offline only: nothing here ran
against TIA Portal or PLCSIM.

## What exists

- `runtime_ua_call` (destructive): the owned-PLCSIM gate of `runtime_ua_write`, then one Call of a
  Method that browse finds as a `HasComponent` child of an object, both in the PLC namespace
  `http://www.siemens.com/simatic-s7-opcua`. Inputs are typed against the method's
  `InputArguments`; refusals before dispatch keep the session. A lost response leaves the session
  `remoteSessionUnknown` and is never retried.
- `runtime_ua_conditions` (read-only): temporary subscription on the Server object, ConditionRefresh,
  bounded Publish, DeleteSubscriptions. Acknowledge, Confirm and AddComment are left out: they are
  writes, and the S7-1500's client-acknowledgment option has no attribute in the
  [server-on capture](../../fixtures/opcua/server-settings-v21-1511-v41.json).
- `runtime_ua_history_probe` (read-only): HistoryServerCapabilities, the node's history access
  attributes and one bounded HistoryReadRaw; status only, product code `UaHistoryReadUnsupported`.
- `opcua_server_settings_set` field `alarmsAndConditions` (`OpcUaAlarmsAndConditionsEnable`, on the
  CPU's `OPC UA` sub-item, only while the server is on), with the rules of the other fields.

Reference: [opcua-runtime.md](../reference/opcua-runtime.md#server-methods-conditions-and-history).

## Offline evidence

`TiaMcp.OpcUa.Runtime.Tests --methods-only --output <fresh>`: 37 checks against the encrypted
fixture server, which now also hosts a synthetic `"ServerMethod_DB".Method` (InInt/InBool to
OutInt/OutBool), limit methods (not executable; DateTime argument; Int16 with an array rank) and one
AlarmConditionState.
Covered: a call executes the method exactly once with exact typed outputs; a standard Server-object
method, a method of another object, an object as method, wrong count, wrong encoding, out-of-range
Int16, a non-executable method, an unsupported argument type, an array-rank argument, InputArguments
listed twice and a server without the PLC namespace are refused with no Call service sent and the
session kept; a namespace table whose index 0 is not the standard namespace ends the session; the method's own Bad result is `methodFailed`; a cancelled call leaves the session
`remoteSessionUnknown`, the identical call is refused and the server executed it once; the history
probe returns the fixture's `BadHistoryOperationUnsupported` (`0x80720000`) and no values; idle
refresh shows only RefreshStart/End, an active unacknowledged alarm comes back with all ten fields,
an inactive acknowledged one is no longer retained; a Bad ConditionRefresh result is reported; a Bad
subscription status ends the window; a cancelled snapshot deletes its subscription before the session
closes (server audit order). The full suite passed (1,155 checks).
`TiaMcp.Tests` (293) covers the new setting field and that `runtime_ua_call` checks the binding
before its session call.

## Native run still needed

`qualify_runtime_plcsim_tcpip.py --mode ua-services` (one Host, about 15 minutes): owned scratch with
FB `ServerMethod` (the V21 help's OPC_UA_ServerMethodPre/Post V1.1 example), FB `AlarmGate` with a
`Program_Alarm` on `RealServer.AlarmTrigger`, Alarms and Conditions on; then the PLC namespace's method set (bounded walk over its Object nodes, so
the run shows whether system methods sit beside the user program's), browse, call, three
refusals, the method's Bad result, the PLCSIM call counter, the history probe and three
ConditionRefresh snapshots around a `sim_write` of the trigger. The oracle accepts a run in which
PLCSIM raises no condition only as a recorded observation (`alarmCondition.raised: false`).
