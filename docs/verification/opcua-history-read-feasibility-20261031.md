# OPC UA HistoryRead feasibility — 31 October 2026

The TODO lists `HistoryRead` among the missing OPC UA client services of the `runtime_ua_*` tools
([capability gaps](../TODO.md#capability-gaps-found-on-29-october)). This record decides whether the
servers those tools talk to (the OPC UA server of an S7-1500 CPU, simulated by PLCSIM Advanced V8) can
answer it. It is an offline check of the installed Siemens documentation and of the pinned client
library; no TIA Portal or PLCSIM process was started.

**Result:** V21 boundary for the S7-1500 and PLCSIM Advanced V8 server. The client side is not the
limit. Method `Call` and Alarms & Conditions are separate rows of the
[combined note](feasibility-checks-20261031.md).

## Client stack

[`TiaMcp.OpcUa.Runtime`](../../src/TiaMcp.OpcUa.Runtime/TiaMcp.OpcUa.Runtime.csproj) uses
`OPCFoundation.NetStandard.Opc.Ua.Client` 1.5.378.182 (net10.0). Its metadata declares the service, so a
HistoryRead tool could be built:

- `Opc.Ua.ISessionClientMethods.HistoryReadAsync(RequestHeader requestHeader, ExtensionObject historyReadDetails, TimestampsToReturn timestampsToReturn, Boolean releaseContinuationPoints, HistoryReadValueIdCollection nodesToRead, CancellationToken ct) : Task<HistoryReadResponse>` (implemented by `Opc.Ua.SessionClient`).
- `Opc.Ua.ReadRawModifiedDetails` (the raw-history request body) in `Opc.Ua.Core` 1.5.378.

The runtime tools use none of it today.

## Server evidence

| Source (installed V21 help, `D:/Arbeit/Siemens/Portal V21/Help/en-US`) | Finding |
| --- | --- |
| `HWCConf34enUS.zip`, `19342675723/171492274059.htm`, "Supported OPC UA profiles and Conformance Classes" | The S7-1500 server "supports the Embedded 2017 UA Server Profiles" plus these facets: Method Server, Standard Event Subscription Server, A & C Base Condition Server, A & C Acknowledgeable Alarm Server, Standard DataChange Subscription 2017 Server, ComplexType 2017 Server, User Token Anonymous and User Name Password, Global Certificate Management Server, DI BaseDevice Server. No Historical Access facet is listed; the fail-safe and R/H additions are `SafetyProviderServer` and `Redundancy Transparent` only. |
| same page, S7-1200 G2 server section | The same Embedded 2017 list without Alarms & Conditions, again without any historical facet. |
| `HWCSimNetPCApplenUS.zip`, `64983839115/124356255115.htm`, "Access to historical data (Historical Access)" | The only "Historical Access" pages in the V21 hardware help belong to the SIMATIC NET PC OPC server (PC station), not to a CPU. |
| `OPCWCCUenUS.zip`, `102805073803/102852309899.htm` and `OPCWCCPenUS.zip`, `21218280203/23669363851.htm` | `HistoryRead` is documented for the WinCC Unified and WinCC Professional runtime servers, which serve logged HMI tags. Those are different servers than the PLC's. |
| `C:/Program Files (x86)/Siemens/Automation/PLCSIMADV/documents/en-US/s7-plcsim_advanced_function_manual_en-US.pdf` (V8.0, 11/2025), chapter 8.1 | The simulated OPC UA server exists only on the TCP/IP adapters ("The OPC UA server functionality is not available" on Softbus). No historical data is mentioned. |
| `fixtures/opcua/server-settings-v21-1511-v41.json` (`serverOn.exposedOnlyWhileOn.opcua`, 22 attributes) | No attribute configures history. The OPC UA node names this fixture pins are `OpcUaServer`, `OpcUaPort`, `OpcUaAlarmsAndConditionsEnable` and the diagnostics and limits family; Openness has nothing to switch on for historical access. |

The PLC keeps data logs, but the S7-1500 OPC UA server does not expose them as history: the profile table
is the server's own statement of what it implements, and a certified server must implement every
Conformance Unit of a profile it claims.

## Disposition

`HistoryRead` against the PLC (real or PLCSIM) is a V21 boundary, to be recorded in the TODO beside the
OPC UA client row. It is not a missing tool: a tool would only ever return `BadHistoryOperationUnsupported`
or an equivalent refusal. History for logged HMI tags is a different feature on a different server (the
WinCC Unified runtime, installed here) and would be a new row, not this one.

## Confirmation to capture

The refusal text is the one piece of native evidence not yet recorded. It costs two existing tool calls in a
PLCSIM run that already exists ([`qualify_runtime_plcsim_tcpip.py`](../../scripts/qualify_runtime_plcsim_tcpip.py)
`normal` mode, after the `runtime_ua_browse RealServer` stage), so capture it with the next slot rather than
as its own run: `runtime_ua_browse` the node `Server > ServerCapabilities > HistoryServerCapabilities`
(namespace 0, numeric `11192`) and record whether it exists; if it does, `runtime_ua_read` its
`AccessHistoryDataCapability` child and assert `false`. `runtime_ua_read` reads `Value`, `DataType`,
`ValueRank` and `ArrayDimensions` only, so the `AccessLevel` attribute (whose `0x04` bit marks a
historizing variable) cannot be read without new client code; this note argues against building that.

## Native confirmation (31 October 2026)

Captured by `runtime_ua_history_probe` in the `ua-services` run `rs_013a5a7a` of
[`qualify_runtime_plcsim_tcpip.py`](../../scripts/qualify_runtime_plcsim_tcpip.py) on build `bf6615a`, against
the OPC UA server of an owned PLCSIM Advanced V8 CPU 1511-1 PN V4.1 in TCP/IP mode (`local.runtime.plcsim-ua-services.20261031`,
[record](opcua-call-conditions-20261031.md)):

| Probe | Answer |
| --- | --- |
| `HistoryReadRaw` on `"RealServer"."Marker"` (last hour, one value) | the whole service refused: `BadServiceUnsupported` (`0x800b0000`); no values |
| `AccessHistoryDataCapability` (`i=11193`), `AccessHistoryEventsCapability` (`i=11242`) | absent: `BadNodeIdUnknown` (`0x80340000`); the `HistoryServerCapabilities` object does not exist |
| `AccessLevel` / `UserAccessLevel` / `Historizing` of the node | `3` / `3` (CurrentRead and CurrentWrite, no `0x04` HistoryRead bit) / `false` |

The boundary holds: the server does not implement the service at all. The product keeps refusing
`HistoryRead` (`UaHistoryReadUnsupported`); `runtime_ua_history_probe` is the recorded proof, not a
history route.
