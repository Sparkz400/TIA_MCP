# X13 feasibility: other Siemens interfaces

Feasibility record for the TODO box [X13 other Siemens interfaces](../TODO.md#work-outside-the-matrix)
(ledger obligation X13, roadmap package AC-24). It gives one row per public SDK or workflow
found on this PC, including the residuals of the two closed 29 October gaps (PLCSIM V8 and the
OPC UA client), and a proposed order for the feasible rows. Taken on 1 November 2026 against
`main` at catalog `0.4.90`.

**Method.** Read-only: the Siemens installation folders, the HKLM uninstall entries
(`DisplayName`, `DisplayVersion`), file version metadata, the XML documentation shipped next to the
public assemblies and the manuals and help installed with the products, plus `src/`,
`docs/reference/` and the catalog's `api` sections. Nothing was decompiled. No TIA Portal or PLCSIM
process was started and no setting was changed. A file on disk is not a route: a row is feasible
only where public documentation describes the interface.

**Classes.**

- **Feasible:** a documented public route and every product it needs are on this PC.
  *Implemented* rows are already in the catalog; *now* rows can be built and qualified with
  what is here (owned PLCSIM, owned scratch projects), a probe deciding the details.
- **Blocked:** a named prerequisite is missing: a blocker of the
  [release ledger](release-obligations-20261015.md#blockers-and-smallest-resolving-actions)
  (B03 HMI targets, B04 optional products and licences, B05 physical targets, B06 trace route,
  B10 external services, B11 SDK and version inventory), another machine or a scope decision.
- **Boundary:** no public route exists, or a recorded decision or policy excludes the route.

**Path shorthands.** `TIA/` = `D:/Arbeit/Siemens/Portal V21/`, `HELP/` = `TIA/Help/en-US/`,
`API/` = `TIA/PublicAPI/V21/net48/`, `ADV/` = `C:/Program Files (x86)/Siemens/Automation/PLCSIMADV/documents/en-US/`,
`UNI/` = `D:/Arbeit/Siemens/WinCCUnified/Documentation/English/`. "Uninstall" means the HKLM
uninstall entry with that display name and version.

## Summary

34 rows: **11 feasible** (4 implemented, 7 now), **17 blocked**, **6 boundary**. X13 itself owns
the rows marked X13 in the owner column; the other rows belong to obligations that already track
them and are listed so the table covers every interface on this PC.

The two 29 October gaps are closed: the PLCSIM V8 signal, area and observation tools passed
natively 75/75 (`local.plcsim_signals.20261030`), and the OPC UA client calls methods, takes
condition snapshots and probes `HistoryRead` (`local.runtime.plcsim-ua-services.20261031`).
Their residuals are rows 2–7 and 10–14.

## Simulation

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | PLCSIM Advanced API, integrated surface | PLCSIM Advanced V8.0 Upd1; `Siemens.Simatic.Simulation.Runtime.Api.x64.dll` 8.0.0.1 (file 80.0.299.0) | Yes: Programming Manual V8.0, 11/2025, A5E53246567-AE, and `SimulationRuntimeApi.h` | Nothing: instances, power and state, tags, I/O by address and name, signals and areas, virtual time and scaling, OB40/82/83/86 events, storage, memory reset, `SingleStep_C` capture and `sim_observe` are in the catalog | Feasible, implemented (verified) | L03 | `ADV/s7-plcsim_advanced_function_manual_API_en-US.pdf`; `src/TiaMcp.Simulator.V8/`; catalog `sim_*`; [PLCSIM gaps](../verification/plcsim-openness-gaps-20261029.md) |
| 2 | PLCSIM acyclic data records | Same API | Yes: §9.11.2 `ReadRecordDone`/`WriteRecordDone` (p. 192) and the `OnDataRecordRead`/`OnDataRecordWrite` events | Answers a user program's `RDREC`/`WRREC` (SFB 52/53) as the simulated I/O module would; no tool uses it today | Feasible now | X13 | API manual Table 9-192; no use in `src/` |
| 3 | PLCSIM status, profile and update interrupts | Same API | Yes: `StatusEvent` (OB55), `ProfileEvent` (OB57, p. 200), `UpdateEvent` (OB56, p. 201) | `sim_event` has no profile or update type; its status event was answered `NotSupportedByModule` on a central module (OB55 serves distributed modules), so a distributed-module case is open | Feasible now | X13 | API manual Table 9-193; `SimulatorRuntimeControl.cs` event switch; catalog `sim_event` limitations |
| 4 | PLCSIM cycle-control modes and partner synchronisation | Same API | Yes: §9.7 cycle control (p. 174), §9.12.1.1 synchronise simulation partner (p. 204) | The repo uses `SingleStep_C` and `TimespanSynchronized_C`; `SingleStep_P`, `_CP`, `_CT`, `_CPT`, `_Bus` and the other time-synchronised modes step per process image partition or bus cycle | Feasible now | X13 | API manual §9.7; `SimulatorSession.cs` |
| 5 | PLCSIM configuration-change events | Same API | Yes: `OnSoftwareConfigurationChanged` (p. 216), `OnIPAddressChanged` (p. 218), `OnHardwareConfigChanged` (p. 222) | `sim_observe` reports only operating-state and LED changes; a download or IP change inside a window is not reported | Feasible now | X13 | API manual §9.12.1; `SimulatorSession.Signals.cs` |
| 6 | PLCSIM remote runtime manager | Same API | Yes: §8.1 `OpenPort`, `RemoteConnect`, `RunAutodiscover` (p. 68); §10 `IRemoteRuntimeManager` (p. 234) | Drives PLCSIM on another Windows host. The manual warns that an open port accepts any runtime manager on the network (TLS with temporary self-signed certificates, no client authentication) | Blocked: a second Windows host (the VMware guest, L05) and a decision on opening a network port | X13 | API manual §8.1 note |
| 7 | ODK 1500S applications in PLCSIM | ODK 1500S not installed; PLCSIM ships `Siemens.Simatic.PlcSim.Vplc1500.ODKClient.x64.exe` | Yes: function manual §6.2 (p. 69): CPU 1518 ODK/MFP, libraries from ODK1500S V2.5 and later | C/C++ functions in a simulated CPU 1518 ODK | Blocked: B04 (ODK 1500S product and licence) | X13 | `ADV/s7-plcsim_advanced_function_manual_en-US.pdf` §6.2; no ODK uninstall entry |
| 8 | Standard S7-PLCSIM V21 | Uninstall "SIMATIC S7-PLCSIM V21"; `S7PLCSIMV21.exe` 2100.0.91.11777 | No public automation interface: its help lists the gRPC port only as internal "vPLC API communication", and instances started through the Advanced API appear in it as "External" | Nothing PLCSIM Advanced does not already give | Boundary | X13 | `C:/Program Files/Siemens/Automation/PLCSIM_V21/help/en-US/` (ports topic `150861973259`); [simulation guide](../guides/simulation.md) |
| 9 | S7-PLCSIM V5.4 with S7ProSim (COM) | Uninstall "SIMATIC S7-PLCSIM V5.4 + SP8 + Upd3" | Yes: S7ProSim V5.4 Operating Manual, 07/2011, A5E00992430-02 | Simulation of S7-300/400 CPUs, a family no TIA MCP obligation engineers | Blocked: scope decision (S7-300/400 is outside every obligation) | X13 | `C:/Program Files (x86)/Siemens/PLCSIM/S7MANUAL/S7WSI/s7wspscb.pdf`; [device inventory](../reference/device-inventory.md) |

## OPC UA

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 10 | OPC UA client to the PLC server | OPC Foundation `Opc.Ua.Client` 1.5.378.182 (NuGet, pinned) | Yes: OPC UA specification and the S7-1500 server help | Nothing: connect, browse, read, write, method call, condition snapshot, subscriptions and capture are in the catalog | Feasible, implemented (verified) | D16 | `src/TiaMcp.OpcUa.Runtime/TiaMcp.OpcUa.Runtime.csproj`; catalog `runtime_ua_*`; [method calls and conditions](../verification/opcua-call-conditions-20261031.md) |
| 11 | OPC UA `HistoryRead` on the PLC server | STEP 7 V21 Upd3 | Server profile has no historical-access facet | — | Boundary (V21, decision of 31 October) | D16 | [HistoryRead record](../verification/opcua-history-read-feasibility-20261031.md) |
| 12 | OPC UA `Acknowledge`, `Confirm`, `AddComment` | Same | Yes: S7-1500 A&C methods | Acknowledging PLC alarms from the client | Boundary: the "Allow message acknowledgment by OPC UA client" option has no V21 Openness attribute, so an owned scratch cannot enable it, and the methods are writes | D16 | [method calls and conditions](../verification/opcua-call-conditions-20261031.md); [feasibility checks](../verification/feasibility-checks-20261031.md#alarms-and-conditions) |
| 13 | OPC UA discovery (Local Discovery Server) | Uninstall "OPC UA Local Discovery Server 1.04" 1.04.413.498 | Yes: OPC UA Part 12 | `FindServers`/`FindServersOnNetwork` instead of one explicit endpoint | Boundary: the runtime plan allows one explicit endpoint and no network discovery | D16 | [physical runtime plan](expansion-plans/physical-plc-runtime.md) row RT-C01 |
| 14 | SiOME (OPC UA modelling editor) | SiOME 3.2.1 | No: a GUI tool with no documented automation interface; its Openness DLLs are private | Its output, NodeSet XML, is already read by `opcua_*` and imported by `change.opcua_server_interface_import` | Boundary | D04 | `D:/Arbeit/Siemens/Siome/` (no manual or API reference); catalog `opcua_*` |

## CPU web server

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 15 | S7-1500 Web API (JSON-RPC), integrated methods | Firmware of the owned TCP/IP PLCSIM instance | Yes: the S7-1500 web server function manual (online collection; no copy installed) | Nothing for the 22 methods the sources call: `Api.*` session methods, `PlcProgram.Browse/Read/Write`, `Plc.ReadOperatingMode`, `RequestChangeOperatingMode`, `ReadModeSelectorState`, `ReadSystemTime`, `ReadTimeSettings`, `DiagnosticBuffer.Browse`, `Alarms.Browse`, `Project.ReadLanguages`/`ReadInformation`, and `Modules.Browse`/`ReadIdentificationMaintenance` for the X10 device tools (see below) | Feasible, implemented (`runtime_web_*` verified) | D16 | catalog `runtime_web_*`; [real-server run](../verification/runtime-real-server-20261029.md) |
| 16 | Web API methods not used yet | Same | Yes: listed in the [physical runtime plan](expansion-plans/physical-plc-runtime.md) (RT-C12, RT-M04–M06) with manual links | `Syslog.Browse`, `Modules.DownloadServiceData`, `PlcProgram.DownloadProfilingData`, `Plc.CreateBackup`/`RestoreBackup`, `Alarms.Acknowledge`, file, data-log, clock and web-application methods | Feasible now for each method the owned TCP/IP PLCSIM lists in `Api.Browse`; a method it does not list falls to B05 | D16, D12 | [physical runtime plan](expansion-plans/physical-plc-runtime.md); `PhysicalRuntime` sources in `src/TiaMcp.Core/` |

The two X10 rows `device_online_browse` and `device_identify` also use the Web API; they wait on
the CPU's web certificate being trusted in the Windows store, a setting the user applies
([TODO](../TODO.md#open-matrix-rows-by-owner)).

## HMI runtimes

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 17 | WinCC Unified PC Runtime GraphQL and Open Pipe | Uninstall "SIMATIC WinCC Unified PC V21 - Unified Runtime V7.0"; `WCCILgraphQLServer.exe` 08.00.00.00 | Yes: WinCC Unified GraphQL System Manual 11/2025; Open Pipe Operating Manual | Runtime tags (read, write, subscribe), active and logged alarms (acknowledge, reset, shelve, enable), login, redundancy state | Blocked: B03. Both need a Unified PC runtime project running on this PC; no route in the repo loads one (V21's download configurations name HMI options such as `OverwriteHmiData`, but only PLC downloads use `DownloadProvider` today), and the runtime licence state is unobserved | X13, X08 | `UNI/GQLWCCUenUS.pdf` §2.1–2.2; `UNI/WinCCRTUOpPenUS.pdf` |
| 18 | WinCC Unified Runtime API (ODK, C# and C++) | Not installed: `Siemens.Runtime.HmiUnified.Interfaces.dll` and the ODK `include`/`lib` folders come from `Openness_SDK.zip` on the installation medium | Yes: WinCC Unified Runtime API System Manual 11/2025; Installation of SDK manual | In-process tag, alarm, connection, plant-model and logging access | Blocked: B04 (SDK not on this PC) and B03 as row 17 | X13, X08 | `UNI/APIWCCUenUS.pdf` §2; `UNI/WinCCRTUSDKenUS.pdf` §2 |
| 19 | WinCC Unified engineering Openness | `API/Siemens.Engineering.WinCCUnified.dll` 2100.300.4.1 | Yes: `HELP/OpennessWCCUenUS.zip` | Screens and other Unified writes past the text lists | Blocked: B03 (preservation gate; the text-list route passed it) | X08 | [HMI plan](hmi-remaining-work.md); [TODO](../TODO.md#engineering-workstreams) |
| 20 | WinCC Runtime Professional interfaces | Uninstall "SIMATIC WinCC Runtime Professional V21.0", WinCC OPC Server V3.9 SP14, WinCC OPC-UA Server V2.0 SP1, SIMATIC Logon V2.0 Upd2 | Yes: `HELP/ODKWCCPenUS.zip` (WinCC Professional ODK), `HELP/RTScriptingWCCPenUS.zip` | ODK, OPC DA/UA servers and runtime scripting of an RT Professional station | Blocked: B03. Openness creates the PC station without its RT Professional application item, so no fixture exists | X07 | [RT Professional attempt](../verification/hmi-rt-professional-20261025.md) |
| 21 | WinCC Runtime Advanced and its simulator | Uninstall "SIMATIC WinCC Runtime Advanced V17.0 UPD9" and "WinCC Runtime Advanced Simulator 21.00" | No runtime SDK reference found on this PC | Runtime access to Classic panels | Blocked: B11 (no public runtime interface established) and B03 (no running runtime project) | X07 | Uninstall entries; no manual under `D:/Arbeit/Siemens/WinCC RT Advanced/` |

## Engineering add-ons and optional products

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 22 | Test Suite Openness | Uninstall "Test Suite Advanced V21.0 + Upd1"; `API/Siemens.Engineering.TestSuite.dll` 21.0.0.0 | Yes: `HELP/TestSuiteOLH_enUS.zip` and the assembly XML (application, system and style-guide namespaces) | Style-guide jobs ran in the TS-Q01 qualification mode on 1 November; production style execution has no qualified profile (no scope read route), and system tests have no execution route yet | Feasible now (application tests implemented and verified) | D06 | [application tests](../verification/testsuite-application-20261030.md); [Test Suite native](../verification/testsuite-native-20261028.md); [TS-Q01](../verification/testsuite-style-q01-20261101.md) |
| 23 | Startdrive Openness | Uninstall "SINAMICS Startdrive ... V21.0 + Upd1"; `API/Siemens.Engineering.Startdrive.dll` 2100.100.3900.1 | Yes: `HELP/SdrOpennessenUS.zip` | Drive parameters, telegrams, online drive objects | Blocked: Startdrive cannot read `DriveObjectNumber` of a drive added through Openness (D10), and online drive access needs a physical drive (B05) | D10 | [drives guide](../guides/drives.md); [TODO](../TODO.md#open-matrix-rows-by-owner) |
| 24 | TIA Add-In API, selection companion | `API/Siemens.Engineering.AddIn.Base.dll`, `.Step7.dll` 2100.0.121.1; `Siemens.Engineering.AddIn.Publisher.exe` | Yes: assembly XML | Nothing for project-tree selections | Feasible, implemented (verified, `local.addin.companion.20261029`) | X04 | `src/TiaMcp.AddIn.V21/`; catalog `addin_companion` |
| 25 | Add-In safety compile workflow | `API/Siemens.Engineering.AddIn.Safety.dll` 2100.0.121.1 | Yes: assembly XML, `SafetyCompileWorkflowAddIn`, `SafetyCompileContext` | A hook that runs when the operator compiles the safety program; the companion refuses workflow hooks today | Feasible now (qualification needs an operator at the desk) | X04, D14 | `API/Siemens.Engineering.AddIn.Safety.xml`; catalog `addin_companion` limitations |
| 26 | Teamcenter Gateway Openness | Uninstall "TcGateway SetupPackage V21.0 Upd3"; `API/Siemens.Engineering.TeamcenterGateway.dll` 2100.0.121.1 | Yes: assembly XML | Search, download, checkout and checkin against Teamcenter | Blocked: B10 (no nonproduction endpoint or account) | X09 | [Teamcenter plan](expansion-plans/teamcenter-gateway.md) |
| 27 | TIA Project Server | Uninstall "TIA Project Server V2.2" | Yes: multiuser members of the V21 Openness API | Server, session and lock inventory; local session commit | Blocked: B10. A server is installed here, but no server is configured and no nonproduction account is supplied | X05 | [Project Server plan](expansion-plans/multiuser-project-server.md) |
| 28 | SIMATIC AX library import | Not installed; TIA help documents the import | Yes: `HELP/ProgAXenUS.zip` ("Import SIMATIC AX libraries into TIA Portal") | AX libraries as global TIA libraries; the AX trace route | Blocked: B04 and B06 (licensed AX package) | D17, X13 | [AX feasibility](expansion-plans/bug-driven-trace-simatic-ax-feasibility.md) |
| 29 | SiVArc, CFC and DCC Openness | Not installed: no `Siemens.Engineering.Sivarc`, `.CFC` or `.Dcc` assembly under `API/` | Yes for CFC and DCC (Siemens online documentation) | Generation rules, CFC charts, DCC charts | Blocked: B04 | D15, X06 | [feasibility checks](../verification/feasibility-checks-20261031.md#cfc-and-dcc-x06) |
| 30 | Openness of other TIA versions | Only V21 is installed (no other "Totally Integrated Automation Portal" uninstall entry) | Per version | V14–V20 engineering without upgrade | Blocked: B11 | X12 | Uninstall entries |

## Device, fleet and administration tools

| # | SDK or workflow | Version on this PC | Public and documented | Beyond TIA MCP | Feasibility | Owner | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 31 | SIMATIC Automation Tool API | SAT V5.0 SP4; `SIMATICAutomationToolAPI.dll` 5.0.4 (.NET 8) | Public, but not documented here: the installed user guide names the API only in its event-log messages, which say it is usable only through the separate SAT SDK and its licence and that the installation alone does not provide the API objects | Network scan, identify, IP and PROFINET name, firmware and program update, backup and restore, memory and factory reset, service data, data logs, F-address assignment | Blocked: B04 (SAT SDK and API licence) and B05 (physical devices; whether a PLCSIM instance answers a SAT scan is not documented) | X13 | `D:/Arbeit/Siemens/SIMATIC Automation Tool/SAT_UserGuide_V5_0_SP4/en-US/` topic `120672622987` (event log messages) |
| 32 | S7-PCT (IO-Link port configuration) | S7-PCT V3.5 SP4 Upd1; `Siemens.Simatic.Pct.ConsoleApplicationLoader.exe` 305.401.801.1 | Partly: the readme mentions command-line use; no command-line reference is installed | IO-Link master and device parameters | Blocked: B11 (no public command-line reference here) and B05 (IO-Link master and devices) | X10, X13 | `D:/Arbeit/Siemens/S7-PCT/Information/English/` readme topic `179119814283` |
| 33 | UMC (UMX command line, REST API) | User Management Component V2.15 SP3 | Yes: UMC System Manual (UMX reference §6.2) | Users, groups and account policies of a UMC domain | Blocked: B10 (no owned nonproduction UMC domain) | X03 | `D:/Arbeit/Siemens/UserManagement/Documentation/UMC_SystemManual_enUS.pdf` |
| 34 | TIA Administrator and Automation License Manager | TIA Administrator V3.2 (ALM, software management, UMAC plugins); ALM V6.2 SP5 | No public SDK found for TIA Administrator; ALM has a manual but its task is licence administration | Licence transfer, software installation, central user administration | Boundary: machine administration outside the product's scope; licence state is reported per product where an API offers it (`sim_status`) | — | Uninstall entries; `D:/Arbeit/Siemens/Documentation/Manuals/English/` |

Not separate rows: `Siemens.Engineering.AddIn.Permissions` and `.Utilities` (process-start helpers
inside the Add-In sandbox, no capability of their own), `Siemens.Engineering.WinCC.Extension`
(four HMI value types used by the WinCC assembly), and the V21 core assemblies
(`Base`, `Step7`, `Safety`, `SafetyValidation`, `WinCC`), which X01–X11 and D01–D17 track.

## Proposed order of the feasible-now rows

Each line: what to build, then how to qualify it. All PLCSIM rows reuse the owned-instance gate,
so a foreign or released instance is refused before any API call.

1. **Row 3, status, profile and update interrupts.** Add `profileEvent` and `updateEvent` to
   `sim_event` next to `statusEvent`, in `SimulationRuntimeControl` and the worker's event switch.
   Qualify like OB40: an ET 200SP IO device on the owned instance, OB55/OB56/OB57 counters read
   back with `sim_read`, and a module that does not support the event recorded with its refusal.
2. **Row 5, configuration-change events.** Subscribe the three events in `sim_observe`'s bounded
   window. Qualify with a `simulation_download_online` and an IP change inside one window,
   asserting one event each and none in an idle window.
3. **Row 2, acyclic data records.** An owned-only `sim_record` tool (or `sim_event` type) that
   answers a pending `RDREC`/`WRREC` with a bounded payload and status. Qualify with a scratch FB
   calling `RDREC` and `WRREC` on a configured module: data and status round trip, a
   deliberate error status, and a request left unanswered until its timeout.
4. **Row 16, unused Web API read methods.** Add `Syslog.Browse`, service-data and profiling-data
   downloads to `runtime_web_*`, offered only when `Api.Browse` lists them. Qualify in the
   real-server runner on the owned TCP/IP instance; record each method PLCSIM does not list as
   a B05 row. The state-changing methods (`Plc.CreateBackup`, `Alarms.Acknowledge`, setters)
   follow later behind the write gate, each with its own D16 contract.
5. **Row 4, further cycle-control modes.** Let `cycleSynchronous` capture choose
   `SingleStep_CP`, `_CT` or `_CPT` and the time-synchronised variants. Qualify with a step
   count per mode on the owned instance and the previous mode restored on stop, failure and Host
   loss.
6. **Row 25, safety compile workflow.** A second hook in the Add-In companion that hands the
   safety compile result and collective signature to the enrolled Host as evidence, never
   changing the compile. Qualify in a desk session: the operator compiles an F-CPU of an owned
   scratch, and the Host record matches `safety_inspect`.
7. **Row 22, Test Suite residuals.** Nothing to build for X13: TS-Q01 ran on 1 November; the remaining
   style rows need a production profile or more desk runs, and system tests need an execution route under D06.

## Open points for the maintainer

- **S7-300/400 (row 9):** is classic simulation through S7ProSim in scope at all? No obligation
  engineers S7-300/400 today, so the row stays blocked until that is decided.
- **PLCSIM remote (row 6):** may a qualification run open a PLCSIM port on a trusted network, and
  is the VMware guest the second host?
- **Unified runtime (rows 17–18):** a running Unified PC runtime project is the prerequisite. A
  desk-configured fixture, as for `change.hmi_compile`, would let GraphQL and Open Pipe be probed
  without the ODK SDK.
- **Local Project Server (row 27):** a TIA Project Server V2.2 is installed. Configuring it as the
  nonproduction X05 endpoint is a system change the user would make.
