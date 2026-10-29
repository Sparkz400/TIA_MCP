# Live monitoring and forcing — route research and 1.0 plan

Maintainer decision, 29 October 2026: live monitoring and active forcing are 1.0 scope for D02. Until then the release ledger said they "stay out of scope" without a decision. This page records which public routes exist, what is built, and what stays impossible.

## Research: public routes (30 October 2026)

| Route | Monitoring | Forcing | Evidence |
| --- | --- | --- | --- |
| TIA Portal Openness V21 (`PublicAPI/V21/net48/*.xml`) | None. `Siemens.Engineering.SW.WatchAndForceTables` holds engineering data only: table `Export`/`Import`/`Find`/`Create`/`Delete`/`ShowInEditor`, row properties without setters (`Address`, `Name`, `DisplayFormat`, `ModifyValue`, `ModifyIntention`, `ModifyTrigger`, `ForceValue`, `ForceIntention`, `MonitorTrigger`). | None. No method in any V21 assembly monitors, modifies or forces online. `OnlineProvider` offers only `GoOnline`, `GoOffline`, `State`, `Configuration` and the PLC master secret. `HW.ForcedValues` (Activate/Deactivate) is a hardware-configuration enum, and `CrossReference.Access.Force` a cross-reference category; neither starts a force job. | XML member search for `Force`, `Monitor`, `Online` across all 15 V21 assemblies |
| PLCSIM Advanced V8 Runtime API (`SimulationRuntimeApi.h`, `.Runtime.Api.x64.dll`) | Typed symbolic reads (`Read*`, `ReadSignals`) and the tag list (`UpdateTagList`, `TagInfos`: name, area, offset, `DataType`, `PrimitiveDataType`). | None. `IInstance` has no force member. Writes (`Write*`, `WriteSignals`) set the process image or memory once; the scan may overwrite them. That is an assignment. The only force artefact is `ELEDType.Force`, delivered through `OnLedChanged` events: the simulator reports a force LED that TIA Portal set, it cannot create one. | Header lines 305, 1725–1908, 2583–2743; .NET reflection of `IInstance`; Function Manual V8.0 §8.17 (a watch table changing an input is not read by PLCSIM) |
| S7-1500 Web API | `PlcProgram.Read`/`Browse` (already in `runtime_web_*`) | None. The method set the repository uses and audits (`Api.*`, `PlcProgram.Read/Write/Browse`, `Plc.*` operating mode/time/backup, `Alarms.*`, `DiagnosticBuffer.*`, `Modules.*`, `Project.*`, `Syslog.*`) has no force method and no force permission. `PlcProgram.Write` is an assignment. | [physical-runtime-contracts](../reference/physical-runtime-contracts.md); [RT-M03](expansion-plans/physical-plc-runtime.md) |
| OPC UA (S7-1500 server, `runtime_ua_*`) | Read and subscriptions (already in `runtime_ua_*`) | None. OPC UA defines no force service; the server's `Write` is an assignment. | [physical-runtime plan RT-M03](expansion-plans/physical-plc-runtime.md) |

Conclusion: forcing exists only in the TIA Portal user interface (force table, online). No public, automatable route exists on V21, PLCSIM Advanced V8, the Web API or OPC UA. Repeated writes, table imports, trigger enums or access rules are not forcing; the repository never relabels an assignment as a force ("Assignment is not forcing").

Monitoring has no Openness route either, but an honest composition exists: read the table offline (`watch_table_read`, verified) and read its symbols on the owned simulator over the verified PLCSIM routes (`sim_tags`, `sim_read`).

## Built for 1.0: `watch_table_monitor`

- Reads the exact watch or force table as `watch_table_read` does (TIA must be offline for that read).
- Checks ownership first (`sim_identity`: instance ID, name and creation receipt), then resolves each row of the page by symbol against the instance's own tag list. Both sides are compared without TIA's quotes; the tag list's own spelling is what is read.
- Reads the resolved symbols 1–20 times, 100–5000 ms apart, with `sim_read`. Rows per page: 1–64.
- Reports comment rows, absolute-address-only rows, missing or ambiguous symbols and types without a typed PLCSIM read per row, and does not read them.
- Read only: no modify, no force, no physical PLC. Stored `ModifyValue`/`ForceValue`/`ForceIntention` are returned as configuration. `programMatchVerified` is always false.
- Code: `src/TiaMcp.Core/WatchTables/WatchTableMonitor.cs`, tool `WatchMonitorTools` in `src/TiaMcp.Host/WatchTableTools.cs`. Offline checks: `tests/TiaMcp.WatchTables.Tests/MonitorChecks.cs`. Native runner: `scripts/qualify_watch_monitor.py`.

Not built (no caller yet): absolute-address reads (PLCSIM area reads), Web API or OPC UA monitoring of a table, `MonitorTrigger` points, scan-consistent sampling (`ReadSignals` lists or the cycle-synchronous capture mode).

## Forcing disposition

Forcing stays unimplemented, and the reason is that no route exists, not that the work is unfinished. The catalog should record it as a limitation of `watch_table_monitor` and `watch_table_force_document_edit` with the evidence above. Reopen only if Siemens publishes a force API (Openness, PLCSIM Runtime API or Web API).
