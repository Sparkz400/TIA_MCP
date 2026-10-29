# Library update scopes and instance namespaces — 29 October 2026

On 29 October the maintainer allowed library updates beyond `entireSelectedPlc`
([TODO decision](../TODO.md), [library TODO](../plans/library-management-todo.md#p1--plc-integration-and-version-workflows)).
This page records which scopes V21 supports natively, what TIA MCP implements and the native runs.

## What V21 offers

Metadata inventory of the V21 PublicAPI (`scripts/Inspect-PublicApi.ps1`, `D:\Arbeit\Siemens\Portal V21\PublicAPI\V21\net48`,
15 assemblies, report SHA-256 `d9699061bf49178f5002fd299d481a28ebfc37a3ce6551929b5a75d6e2f49ff8`) and the shipped XML documentation:

| Question | V21 API | Consequence |
| --- | --- | --- |
| Narrower update scope | `IUpdateProjectScope` is implemented only by `PlcSoftware`, `HmiTarget` and `HmiSoftware`. No block group, software unit (`PlcUnit`) or instance implements it. | An update always covers every use in one PLC. Instance, block-group and software-unit scopes are a V21 boundary; they stay refused as `NativeScopeTooBroad`. |
| Per-instance version | `LibraryTypeInstanceInfo.LibraryTypeVersion` is read-only; no member assigns a version to an instance. A PLC holds one instance of a type ([25 October](library-lane-c-20261024.md#software-units-and-namespaces--25-october-2026)). | Per-instance assignment does not exist; within a PLC it equals per-PLC assignment. |
| Which version an update applies | `LibraryType.UpdateProject(scope, DeleteUnusedVersionsMode, StructureConflictResolutionMode, ForceUpdateMode)` updates "with the latest content", i.e. the source library's default version. | A specific release is assigned by making it the source default first (`library_set_default`). |
| Forced downgrade | `ForceUpdateMode.ForceSetAnyUpdatedVersionAsDefault`: the source default becomes the target default "irrespective of version number" (UI "force update" checked). `SetOnlyHigherUpdatedVersionAsDefault` is the unchecked default; `NoDefaultVersionChange` is for project-library-to-project updates. | Implemented as explicit `forceDefault: true`. |
| Folder selection | `ILibrary.UpdateProject(IEnumerable<ILibraryTypeOrFolderSelection>, IEnumerable<IUpdateProjectScope>, …)` takes library type folders and several PLCs at once. | Not implemented: a library folder only expands to its types, which an explicit selection of up to 100 types already covers with exact pins; several PLCs are updated as one prepared change each, each with its own cleanup proof. |
| Namespace of an instance | `PlcBlock.Namespace` and `PlcType.Namespace` are writable; `LibraryType.Namespace` is read-only; `PlcUnitBase.NamespacePreset` is writable. | See [instance namespaces](#instance-namespaces). |

## Forced per-PLC assignment

`library_update_project` with `updateScope: "entireSelectedPlc"` and `forceDefault: true`:

- The selected version must still be the source default. `forceDefault` waives two refusals for the selected roots only:
  a higher committed release in the source and a newer project default. Dependency types keep the non-forcing rules,
  so a forced update never moves a dependency's project default down.
- The preview's `updateImpact.types[]` carry `projectDefaultBefore`, `forceDefault` and `defaultEffect`
  (`preserveDefault`, `promoteIfHigher`, `forcedDowngrade`, `forcedDefault`); the update effect carries `forceDefault`.
  A forced update that would change only the project default is not a no-op.
- The native call uses `ForceSetAnyUpdatedVersionAsDefault`. Every readback phase (checkpoint, native action, compile,
  save) additionally requires unchanged per-version use counts in all other PLCs of the project (`updateBaseline.peerPlcUses`)
  and, when forced, the selected version as project default.
- Without `forceDefault` nothing changed: the selected default must be the highest committed source release and not
  below the project default.

## Instance namespaces

TIA does not apply a software unit's namespace preset to library instances (25 October). A probe on build 542149b
(`LibraryUnitSeeder namespace`, raw report `artifacts/q/lxu/un_ccd8c136/qualification.json`, run stopped at the probe's
deliberately opposite expectation) set `PlcBlock.Namespace = "TiaMcp_LibUnit"` on the FB instance in the unit:
TIA accepted it, kept the library link to the same version GUID, left the type's namespace empty and compiled without
errors. The namespace is therefore settable through Openness and not a V21 boundary.

`library_instantiate` accepts `namespace` for a software-unit `scopePath`: dot-separated identifiers, type versions only.
The preview shows `instanceNamespace` (namespace, unit preset, each root's type namespace). Apply sets the namespace on
each created root right after `CreateFrom` and requires the namespace and the exact version link before compile and save.
Created dependencies keep their type namespace; reused dependencies are untouched. A namespace in the PLC root is refused
(`TargetIncompatible`), a malformed one as `InvalidRequest`.

## Native runs

Both runs used build 49fc206 frozen at `artifacts/lx2` (one owned TIA Portal V21 at a time, owned scratch projects, no PLCSIM).

[`local.library_scopes.forced.20261029`](../../evidence/local.library_scopes.forced.20261029.json)
(`scripts/qualify_library_scopes.py`, raw report `artifacts/q/xs2/sq_a196a1d7`, 89/89 steps):

- Two PLCs of the scratch CPU type use FB and UDT 1.0.0 behind an OB each; non-forced updates bring both to FB 1.1.0.
- A pinned source archive holds FB 1.0.0 and 1.1.0 with default 1.0.0. Refused without any native change: the update
  without `forceDefault` (`VersionSelectionUnsupported`), a non-boolean `forceDefault` (`InvalidRequest`), the
  non-default 1.1.0 even when forced (`VersionSelectionUnsupported`) and `updateScope` `selectedInstances`,
  `selectedFolder` and `softwareUnit` (`NativeScopeTooBroad`).
- With `forceDefault: true` the preview declares `forcedDowngrade` from project default 1.1.0, one use moving
  1.1.0 → 1.0.0 and the first PLC's FB 1.1.0 and UDT 1.0.0 uses as peer baseline. All four readback phases verify.
- Only the second PLC moves to FB 1.0.0; the first keeps 1.1.0 and every native block export; the UDT stays 1.0.0 in
  both; FB 1.0.0 becomes the project default.
- The checkpoint restores the pre-update project (both PLCs at 1.1.0, default 1.1.0) into a fresh owned TIA; a fresh
  reopen of the saved project shows the forced state.

[`local.library_units.namespace.20261029`](../../evidence/local.library_units.namespace.20261029.json)
(`scripts/qualify_library_units.py`, raw report `artifacts/q/xu2/un_306b3cd4`, 57/57 steps):

- The UDT instantiated into unit `TiaMcp_LibUnit` (preset `TiaMcp_LibUnit`) without a namespace keeps its type's empty
  namespace.
- Refused without any native change: a namespace in the PLC root (`TargetIncompatible`) and `1 bad` (`InvalidRequest`).
- With `namespace: "TiaMcp_LibUnit"` the FB is created under `…/units/TiaMcp_LibUnit/blocks/namespaces/TiaMcp_LibUnit/`
  with its exact 1.0.0 link; the UDT stays outside the namespace.
- The one-instance-per-device and cleanup refusals of 25 October still hold.
- The checkpoint restores the project with the UDT only; a fresh reopen shows the namespaced FB and the UDT.

## Not covered

Forced dependency defaults (refused by design), forced updates of HMI targets, several PLCs in one native call,
library folder selections, namespaces on created dependencies or master copies, a namespaced FB's later update
(the unit use has no OB-rooted reference here), mixed CPU families and runtime behavior.
