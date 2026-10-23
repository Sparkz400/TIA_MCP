# Native explorer extraction and evidence

The native reader is `src/TiaMcp.Openness.V21/OpennessSession.ProjectModel.cs`. It runs on the existing V21 worker STA and retains detached JSON only. The `project_model` route is read-only and shares the existing authenticated MCP/worker transport. It does not start an HTTP engineering API.

## Hierarchy sources

| View | Native source | Identity and limits |
| --- | --- | --- |
| Project/device groups | `Project.DeviceGroups`, `DeviceUserGroup.Groups`, `DeviceUserGroup.Devices` | Groups are enumerated even when empty. Existing `/projects/.../groups/.../devices/...` paths remain unchanged. |
| Devices and hardware items | `Project.Devices`, `UngroupedDevicesGroup.Devices`, `Device.DeviceItems`, `DeviceItem.DeviceItems` | Explicit device ancestry is retained. Position numbers/classifications are offline native metadata; neither proves electrical or network topology. |
| PLC/software units | `SoftwareContainer.Software`, `PlcUnitProvider.UnitGroup` | The actual `PlcUnitSystemGroup` is a native node at the additive `/units` container path, including when empty. `Units` and `SafetyUnits` both have that actual parent; existing member paths are preserved. Missing `PlcUnitProvider` is reported as unsupported service coverage. |
| Block/type/tag/technology groups | `PlcBlockGroup.Groups`, `PlcBlockSystemGroup.SystemBlockGroups`, recursive `PlcSystemBlockGroup.Groups`, `PlcTypeGroup.Groups`, `PlcTypeSystemGroup.SystemTypeGroups`, `PlcTagTableGroup.Groups`, `TechnologicalInstanceDBGroup.Groups` | Enumerates real groups separately from members, preserving empty groups. Groups consume the same native-visit, depth, node and byte budgets. |
| Namespace navigation | Native block/type `Namespace` property | Display folder is explicitly virtual with no engineering `objectPath`; child paths retain the existing exact escaped namespace segments. |
| HMI | Registered HMI workstream hooks | Family adapters own native extraction. Common model coverage carries each section's unavailable, partial or unsupported state; screen content remains a separate observation. |

Installed native API evidence: `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.Step7.xml` documents `PlcUnitProvider.UnitGroup` (member at line 20234), `PlcUnitSystemGroup` (line 20519), and its `Parent`, `SafetyUnits`, `Units`, `Name` properties (lines 20632–20654). `PlcSystemTypeGroup` exposes `Types` and `Name`, without a nested `Groups` composition (lines 18567–18584); the reader does not invent nested system type groups. These are installed V21 API observations, separate from target qualification.

## Exact reads and relationship evidence

An exact selection contains schema version, snapshot ID, worker/session generation, project copy path, node ID and canonical object path. Fresh details and relationships first validate all reference fields and recapture the same bounded metadata scope. A mismatch invalidates the snapshot and requires explicit reselection. The reader never falls back to a display name. Paths are not durable native GUIDs; an unchanged metadata observation is not proof of a complete project/content revision.

Details reuse `BlockInterface`, `TypeRead`, `TagTableRead`, `HardwareInspect`, and `TechnologyRead`. Native XML exports retain their own SHA-256 hashes. Content observations carry their own timestamp and revision scope, and portable exports retain their mixed-capture warning. Large details are written as hashed artifacts rather than exceeding the inline node budget.

Relationship extraction reuses `ReadNativeAnalysis`, its directional native access classification, `OfflineEvidence.ResolveInstanceTarget`, and `MatchHardwareAddresses`. Calls, reads, writes, instance/type uses, dependencies and configured I/O remain distinct edge kinds. Exact engineering proxies may be mapped to canonical tag/constant paths only during native analysis; no proxy enters a cached snapshot. Unmapped native members stay explicit unresolved endpoints with original evidence. Configured I/O overlaps without selected-controller ownership remain unresolved candidates.

A hardware item can also be the seed of a reverse configured-I/O query. It compares captured I/O-address tag declarations against freshly inspected native address ranges on that exact hardware item, grouped by exact PLC identity. Controller ownership is checked by the existing analyzer, never inferred from equal addresses or device ancestry. Defaults cap candidate tags at 200 and PLCs at 10 (ceilings 2000 and 32); native range traversal is bounded per PLC, and the common graph still enforces 500 nodes/1000 edges/512 KiB. Missing captured tag declarations or exhausted candidates are partial coverage, not evidence that hardware is unused.

Native traversal and separately observed content timing are reported as `nativeCaptureMilliseconds`, `nativeDetailMilliseconds`, and `nativeAnalysisMilliseconds`. Cached children/search timing measures a different operation. Bounds are not a claim that an in-flight Siemens API call was cancelled or that cross-provider capture is atomic.

## Qualification records

`scripts/qualify_project_explorer.py` creates a fresh owned scratch project, prepares its fixture groups through the existing change workflow, and checks hierarchy partitions, exact inspection, relationships, portable transfer, invalid selections, legacy-index compatibility and release/session behavior. It records actual worker hashes and native/cached timing separately. The first live scratch run is recorded in `artifacts/project-explorer-native-live.json`; later qualification records supersede its capability/count evidence when additional native containers or fixtures are added. Synthetic cache/browser benchmarks do not qualify native project-size latency, HMI families, physical topology or runtime PLC behavior.

The independently authored positive tag/module binding and both configured-I/O query directions are recorded in [configured I/O qualification](../verification/project-explorer-io-qualification.md). Its saved offline fixture can be reopened with the read-only final-package verifier; address overlap alone never qualifies a resolved association.
