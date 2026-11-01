# Feasibility checks — 31 October 2026

Offline checks that decide open TODO items, made from the installed V21 PublicAPI metadata, the installed
Siemens help (`D:/Arbeit/Siemens/Portal V21/Help/en-US`) and the PLCSIM Advanced V8 manual. No TIA Portal or
PLCSIM process was started. The checks that ended in a V21 boundary have their own records:
[block move](block-move-feasibility-20261031.md) and [OPC UA HistoryRead](opcua-history-read-feasibility-20261031.md).
This note holds the routes that exist.

| Item | Verdict | What decides the next step |
| --- | --- | --- |
| CFC (X06) | Route documented by Siemens, not verifiable here: product and assembly absent | Install of SIMATIC STEP 7 CFC (B04), then one create/export/import/compile probe |
| DCC (X06) | Route documented by Siemens, not verifiable here: product and assembly absent | Install of SINAMICS DCC and a drive (B04, D10) |
| OPC UA method `Call` | Implementable; PLCSIM probe needed | New client tool plus a server-method FB in the PLCSIM runner |
| OPC UA Alarms & Conditions | Implementable; PLCSIM probe needed | A&C settings pin plus a program alarm in the PLCSIM runner |
| OPC UA `HistoryRead` | V21 boundary | [record](opcua-history-read-feasibility-20261031.md) |

## CFC and DCC (X06)

### What V21 exposes without the optional products

- `Siemens.Engineering.SW.Blocks.ProgrammingLanguage` has the members `CFC` ("Optional language CFC"),
  `SFC` ("Optional language SFC") and `FCP` ("Function Chart Plus language (CFC)")
  (`Siemens.Engineering.Step7.xml`). `PlcBlock.ProgrammingLanguage` is get-only.
- No CFC, chart, FCP, SFC or DCC type exists in any of the 15 installed Siemens.Engineering assemblies
  (reflection over the DLL metadata with name patterns `cfc|chart|fcp|dcc|sfc`; only the enum members
  above match). The PublicAPI `Schemas` folder has 21 XSDs (`SW.PlcBlocks.LADFBD_v5`, `SCL_v4`, `STL_v5`,
  `Graph_v6` and others) and none for CFC.
- The creation call is language-generic: `PlcBlockComposition.CreateFB(String name, Boolean isAutoNumbered,
  Int32 number, ProgrammingLanguage programmingLanguage)` declares `LicenseNotFoundException`. The
  generic `Export(FileInfo, ExportOptions)` and `Import(FileInfo, ImportOptions)` are the same for every
  block.

### What Siemens documents

- The V21 Openness readme (`ReadmeTIAPortalOpennessenUS.zip`, `82367461771/192402443403.htm`) lists modular
  assemblies per product: `Siemens.Engineering.CFC.dll` for "SIMATIC STEP 7 CFC" and
  `Siemens.Engineering.Dcc.dll` for "SINAMICS DCC". Neither DLL is in the PublicAPI directory; the install
  config and the Bin folder hold no CFC product (a satellite resource DLL named
  `Siemens.MC.Drives.Acx.BusinessLogic.Dcc.resources.dll` is the only DCC trace).
- The Openness guide (`TIAPortalOpennessenUS.zip`) links "CFC Charts (Export/Import)" from "Importing/
  exporting data of a PLC device" to a help package `ImExCFCOpennessenUS`, and "Functions for DCC" to
  `DccOpennessenUS`. Neither package is installed, so the CFC SimaticML schema and the DCC object model
  cannot be read here.

### Verdicts

- **CFC: not a boundary.** Siemens documents CFC chart export and import, and the language is a first-class
  `ProgrammingLanguage`. Profile work needs the product (blocker B04). Nothing offline can define a CFC
  model, because the SimaticML schema ships with the product's help.
- **DCC: not a boundary, doubly blocked.** It needs `Siemens.Engineering.Dcc.dll`, a drive that holds a DCC
  chart, and the Startdrive drive-object access the D10 TODO row is still waiting for.

### Probe once CFC is installed

No Host change kind creates a block in a chosen language (the GRAPH runner,
[`qualify_graph_changes.py`](../../scripts/qualify_graph_changes.py), starts from an authored seed through
`block_import_xml`), so the first probe is a console probe under `scripts/native/` in the style of
`UnitSeeder`, on an owned scratch with a CPU 1511-1 PN:

1. `scripts/Inspect-PublicApi.ps1` over the PublicAPI folder; record `Siemens.Engineering.CFC.dll` types,
   members and the new XSD (expect a `SW.PlcBlocks.Cfc*` schema).
2. Create `CFC_Probe` with `CreateFB(name, true, 0, ProgrammingLanguage.CFC)` in the CPU's block group;
   assert `ProgrammingLanguage == CFC`, then compile and save.
3. `PlcBlock.Export` the empty chart; assert the root namespace is the CFC SimaticML schema and the file
   validates against the new XSD.
4. Delete the block, `Import` the export into the same group, and compare: name, number, language, no
   compile error. Export again and assert the second export equals the first.
5. Refusals to record: import into a CPU without the CFC licence (`LicenseNotFoundException`), a chart name
   conflict, and a malformed chart.

If steps 2 to 4 pass, CFC follows the GRAPH pattern: the retained export becomes the seed for a
`qualify_graph_changes.py`-style runner (`block_import_xml`, compile, save, fresh-Portal reopen).

The DCC probe starts the same way (inventory the assembly, then create and export an empty chart on a
drive); it cannot be specified further until `DccOpennessenUS` and the assembly are readable.

## OPC UA client services

### Client stack

[`TiaMcp.OpcUa.Runtime`](../../src/TiaMcp.OpcUa.Runtime/TiaMcp.OpcUa.Runtime.csproj) uses
`OPCFoundation.NetStandard.Opc.Ua.Client` 1.5.378.182 on net10.0. The library supports all three services; the
repo uses none of them yet (no `CallAsync`, `HistoryReadAsync`, `ConditionRefresh` or condition field in
`src/`):

| Service | Library member (metadata of 1.5.378) |
| --- | --- |
| Method call | `Opc.Ua.Client.SessionClientExtensions.CallAsync(ISessionClient session, NodeId objectId, NodeId methodId, CancellationToken ct, Object[] args) : Task<IList<object>>`; raw form `ISessionClientMethods.CallAsync(RequestHeader, CallMethodRequestCollection, CancellationToken) : Task<CallResponse>` |
| Condition refresh | `Opc.Ua.Client.Subscription.ConditionRefreshAsync(CancellationToken) : Task<bool>` and `ConditionRefresh2Async(UInt32 monitoredItemId, CancellationToken) : Task<bool>` |
| History | `ISessionClientMethods.HistoryReadAsync(...)` with `Opc.Ua.ReadRawModifiedDetails` |

The repo's event subscription ([`OpcUaRuntimeClient.Events.cs`](../../src/TiaMcp.OpcUa.Runtime/OpcUaRuntimeClient.Events.cs))
selects the eight `BaseEventType` fields in `OpcUaEventContracts.Fields` (EventId, EventType, SourceNode,
SourceName, Time, ReceiveTime, Message, Severity) with a severity filter. Alarm conditions need more select
clauses (`ConditionId`, `AckedState/Id`, `ActiveState/Id`, `Retain`, `EnabledState/Id`) and the
`ConditionRefresh` call after the subscription exists; the decoding in `DecodeEventField` rejects any
other field type (`UaEventFieldTypeInvalid`).

### Method call

**Server side.** The S7-1500 server supports "Method Server Facet" (profile table,
`HWCConf34enUS.zip`, `19342675723/171492274059.htm`). A method is user-program code: an FB that calls
`OPC_UA_ServerMethodPre` and `OPC_UA_ServerMethodPost` (S7-1500 firmware V2.5 and higher; version V1.1 of the
instructions needs cyclic calls, `ProgTecCom2MenUS.zip`, `16546228235/177244357003.htm`), with
`UAMethod_InParameters` and `UAMethod_OutParameters` in `VAR DB_SPECIFIC`. Documented limits: one call per
instance at a time, 20 to 100 methods by CPU, `0xB080_B000` TooManyMethods, 2,097,152 bytes
(`112614416267.htm`), and a fixed SIMATIC to OPC UA type mapping (BOOL to Boolean, INT to Int16, DINT to
Int32, REAL to Float, WSTRING to String, and so on). PLCSIM Advanced V8 simulates firmware V2.9 and higher
and offers the OPC UA server on the TCP/IP adapters (product overview and chapter 8.1), the setup that
[`runtime-real-server-20261029.md`](runtime-real-server-20261029.md) already uses.

**Openness.** Nothing configures a method. `OpcUaServer` (Boolean) and the licence
`OpcUaPurchasedLicense` (CPU, UInt64, 1 = `SimaticOpcUaS71500Small`) from
[`server-settings-v21-1511-v41.json`](../../fixtures/opcua/server-settings-v21-1511-v41.json) switch the
server on, and the generated runtime user needs the `OPCUAServerAccess` right. With firmware V4.0 and
higher a server interface can also restrict a role's "Call" permission per node; the probe has to record
whether the default role may call a method without a server interface.

**Verdict:** implementable. `runtime_ua_method_call` would wrap `CallAsync` with the repo's existing
guards (pinned encrypted session, bounded results, typed arguments only from the server's own
`InputArguments` declaration).

**Native probe** (extend [`qualify_runtime_plcsim_tcpip.py`](../../scripts/qualify_runtime_plcsim_tcpip.py),
`normal` mode, after the `runtime_ua_browse RealServer` stage; one PLCSIM run):

1. Add to the program source an FB `ServerMethod` (optimized access) with the Pre/Post pair, one input
   `Struct { InInt : Int; InBool : Bool }`, one output `Struct { OutInt : Int; OutBool : Bool }`, whose
   logic sets `OutInt := InInt + 1` and `OutBool := NOT InBool`; call it from `Main` through its instance DB.
2. `runtime_ua_browse` from the instance DB and from the Objects folder; record where the method node
   appears and its `InputArguments` and `OutputArguments` property values.
3. `runtime_ua_method_call` with `InInt = 41`, `InBool = true`: assert status Good, `OutInt == 42`,
   `OutBool == false`.
4. Wrong argument count or type: assert `BadArgumentsMissing` or `BadInvalidArgument` (record the exact code),
   and that the instance is callable again afterwards.
5. A session of a role without `OPCUAServerAccess` and the generated user with the method's permissions
   removed (if the default role allows the call): assert the refusal and record its text.
6. Read the program-side effect through the PLCSIM API (a `Calls : DInt` counter in the same DB increments by
   exactly the number of successful calls); this is the independent oracle.

### Alarms and Conditions

**Server side.** The S7-1500 server lists "A & C Base Condition Server Facet" and "A & C Acknowledgeable Alarm
Server Facet"; the S7-1200 G2 server does not. Activation (`138927675915.htm`): firmware V2.9 or higher,
an OPC UA runtime licence, "Central alarm management in the PLC" enabled, and "Enable Alarms and
Conditions on the OPC UA server"; optionally "Allow message acknowledgment by OPC UA client". The methods are
`Acknowledge` (`i=9111`), `ConditionRefresh` and `AddComment` (`139713068043.htm`); events arrive through the
"Server" node (`133030788875.htm`). Both are inside PLCSIM's firmware range (V2.9 and higher) and CPU 1511-1 PN V4.1
is the repo's scratch CPU.

**Openness.** `OpcUaAlarmsAndConditionsEnable` (Boolean, on the CPU's `OPC UA` device item) is captured
with default `false` and is exposed only while the server is on; `CentralAlarmManagement` (Boolean on the
CPU, in the "TIA Portal Openness Hardware parameters" PDF of the PublicAPI folder) is the other switch. The attribute behind
"Allow message acknowledgment by OPC UA client" is not in the 22 captured while-on attributes; it
may appear only after A&C is enabled. [`OpcUaServerSettings.cs`](../../src/TiaMcp.Core/OpcUaServerSettings.cs)
pins four settings today (server, port, auto-accept, licence), so `opcua_server_settings_set` needs a
fifth pin for the A&C switch (and the CPU parameter route for central alarm management).

**Verdict:** implementable on the client side (library members above). Whether PLCSIM Advanced simulates
the alarm conditions of its virtual CPU is not stated in its manual; the probe decides it. If it does not,
A&C stays implemented and qualified against the repo's fixture server only, recorded as a PLCSIM
limitation, with the physical-PLC row (B05) as the other route.

**Native probe** (same runner, after the method stage; one PLCSIM run):

1. `opcua_server_settings_set` with the new A&C pin and `CentralAlarmManagement` true; compile, save,
   download. Dump the OPC UA item's attributes again and record the acknowledgment attribute name.
2. Add to the program a `Program_Alarm` instance driven by `RealServer.AlarmTrigger : Bool` (a DB flag the
   PLCSIM API writes).
3. `runtime_ua_subscription_start` with `events.notifier` = Server (`ns=0;i=2253`) and the condition select
   clauses; then `runtime_ua_condition_refresh`. Assert that the refresh delivers a `RefreshStartEvent` and a
   `RefreshEndEvent` and no active condition for the idle program.
4. Set `AlarmTrigger` true through PLCSIM. Assert one condition event with `ActiveState/Id == true`,
   `AckedState/Id == false`, `Retain == true`, and `Message`, `Severity` and `SourceName` matching the
   configured alarm.
5. `runtime_ua_method_call` `Acknowledge` (`i=9111`) with the event's `EventId` and `ConditionId`; assert a
   second event with `AckedState/Id == true`. Then set `AlarmTrigger` false; assert `ActiveState/Id == false`
   and, once acknowledged, `Retain == false`.
6. Reconnect and refresh again; assert the condition set equals the PLCSIM-side alarm state. Calling
   `Acknowledge` with the acknowledgment option off must be refused (record the status).

### Proposed TODO text

Replace the OPC UA client box in "Capability gaps found on 29 October" with:

> - [ ] **OPC UA client** (X13): `HistoryRead` is a V21 boundary: the S7-1500 server's profile has no
>   historical-access facet ([record](opcua-history-read-feasibility-20261031.md)). Method
>   `Call` and Alarms & Conditions are implementable (the pinned client library has `CallAsync` and
>   `ConditionRefresh`; the server has the Method and A&C facets from firmware V2.5 and V2.9) and wait for the
>   `runtime_ua_*` tools plus one PLCSIM probe each ([note](feasibility-checks-20261031.md)).
