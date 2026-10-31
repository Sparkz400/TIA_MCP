# Block move feasibility — 31 October 2026

`block.move` ("Move blocks between supported groups and scopes", D07/X11) asks for an identity-preserving
move of a PLC block, type or tag table between block groups or into another software unit. The
[API roadmap](../plans/expansion-plans/api-coverage-and-roadmap.md) kept it open until a deciding public-API
check. This record is that check. It is an offline metadata check; TIA Portal was not started.

**Result:** V21 boundary. The public V21 Openness API has no member that moves a PLC block, type or tag
table; the only move members in the whole API move hardware device items. One non-mutating native
confirmation remains (see [Remaining check](#remaining-check)).

## Method

- Inputs: the V21 PublicAPI directory (`Siemens.Engineering.*.dll` and their `.xml` documentation, 15
  assemblies: Base, Step7, Safety, SafetyValidation, Startdrive, TestSuite, TeamcenterGateway, WinCC,
  WinCC.Extension, WinCCUnified and the five AddIn assemblies).
- Member names were searched in every documentation file for `Move`, `Relocat`, `Reparent`, `Transfer`,
  `Reassign`, `Migrate`, `Cut`, `Paste`, `Detach`, `Attach`, `SetParent`, `ChangeParent` and `ChangeGroup`
  (excluding `Remove`). Public members and accessors of the PLC object types were listed by reflection
  over the DLL metadata (`System.Reflection.MetadataLoadContext`, the approach of
  [`Inspect-PublicApi.ps1`](../../scripts/Inspect-PublicApi.ps1); no Siemens code was loaded).

## Move candidates in the whole API

| Member | Verdict |
| --- | --- |
| `Siemens.Engineering.HW.HardwareObject.CanPlugMove(DeviceItem deviceItem, Int32 positionNumber) : Boolean` | Moves a device item to another slot. Hardware only. |
| `Siemens.Engineering.HW.HardwareObject.PlugMove(DeviceItem deviceItem, Int32 positionNumber) : DeviceItem` | Same. The Openness guide documents it as "Moving device items into another slot". |

Every other hit is a different concept (`Cax.TransferResult*`, `Library.*TransferResult*`,
`HW.TransferArea*`, `HmiLinearMovementPart`, `TiaPortalProcess.Attach`), none of which touches PLC blocks.

## Complete PLC-side surface

No `Move`, `MoveTo`, `Relocate`, `Reparent`, `Cut` or `Paste` exists on any of these types.

| Type | Public members that change structure or location |
| --- | --- |
| `SW.Blocks.PlcBlock` | `Delete()`, `Export(FileInfo, ExportOptions)`, `Export(FileInfo, ExportOptions, DocumentInfoOptions)`, `ExportAsDocuments(DirectoryInfo, String)`, `ShowInEditor()`. `IEngineeringObject Parent { get; }` is read-only; `Name`, `Number` and `Namespace` are settable. No GUID or identifier property. |
| `SW.Blocks.PlcBlockGroup` | `Blocks { get; }`, `Groups { get; }`, `Parent { get; }`, `Name { get; }`. No methods of its own. |
| `SW.Blocks.PlcBlockUserGroup` | `Delete()`, `Name { get; set; }` (rename), `Parent { get; }` read-only. |
| `SW.Blocks.PlcBlockComposition` | `CreateFB(String, Boolean, Int32, ProgrammingLanguage)`, `CreateInstanceDB(String, Boolean, Int32, String)`, `CreateFrom(MasterCopy)`, `CreateFrom(MasterCopy, MasterCopyMode)`, `CreateFrom(CodeBlockLibraryTypeVersion)`, `CreateFrom(CodeBlockLibraryTypeVersion, UpdatePathsMode)`, `Import(FileInfo, ImportOptions)`, `Import(FileInfo, ImportOptions, SWImportOptions)`, `ImportFromDocuments(DirectoryInfo, String, ImportDocumentOptions)`, `Find`, `Contains`, `IndexOf`, `Any`. |
| `SW.Blocks.PlcBlockUserGroupComposition` | `Create(String)`, `CreateFrom(MasterCopy[, MasterCopyMode])`, `Find`, `Contains`, `IndexOf`. |
| `SW.Types.PlcTypeComposition` / `PlcTypeUserGroupComposition` / `PlcTypeUserGroup` | `CreateFrom` (master copy, library type version), `Import(FileInfo, ImportOptions[, SWImportOptions])`, `ImportFromDocuments`, `Create(String)` for groups, `Delete()`, `Name { get; set; }`. |
| `SW.Tags.PlcTagTableComposition` / `PlcTagTableUserGroupComposition` / `PlcTagTableUserGroup` | `Create(String)`, `CreateFrom(MasterCopy[, MasterCopyMode])`, `Import(FileInfo, ImportOptions)`, `Delete()`, `Name { get; set; }`. |
| `SW.Units.PlcUnitComposition`, `PlcUnit`, `PlcUnitBase` | `Create(String)`, `CreateFrom(MasterCopy[, MasterCopyMode])`, `PlcUnit.Delete()`; `BlockGroup`, `TypeGroup`, `TagTableGroup`, `ExternalSourceGroup`, `Parent`, `Relations` are get-only; `Name`, `Author`, `NamespacePreset` settable. |
| `SW.Units.PlcUnitRelationComposition` | `Create(String, UnitRelationType)`; `PlcUnitRelation.Delete()`. Relations express publish dependencies, not ownership. |
| `SW.ExternalSources.PlcExternalSource` | `GenerateBlocksFromSource(PlcBlockUserGroup, GenerateBlockOption)` creates new blocks in a chosen user group from a source file. A creation route, not a move. |

The only cross-group operations are create-from-something (master copy, library type, SimaticML,
external source, documents). Each makes a new object. Export, delete and import elsewhere is the same
thing: the [roadmap](../plans/expansion-plans/api-coverage-and-roadmap.md) already records that export and
import recreation is not a move, and a move would have to preserve instance DB links, cross-references and
creation data that a delete-and-import cannot keep.

## Siemens documentation

The installed V21 Openness guide documents creating, renaming and deleting block groups and creating
objects from master copies, but no page for moving a block or a group. The TIA Portal information system
describes the move only for the editor, as a drag-and-drop gesture ("Drag-and-drop the block to the new
position", `ProgBlocksenUS`, page "Moving blocks"). That gesture has no Openness counterpart, and a
software unit has no user-visible block groups to move into (see the 30 October unit record in the TODO).

## Remaining check

The typed API is complete evidence for the public surface, but `IEngineeringObject.GetInvocationInfos()`
can list actions that are not in the documentation (the Test Suite `CopyScope` action is one). One
non-mutating native call settles it:

1. Open an owned scratch project that already has a block `FC` in a user group and a software unit (the
   [`qualify_software_unit_changes.py`](../../scripts/qualify_software_unit_changes.py) scratch).
2. Call `target_capabilities` with `kind` `block`, `type`, `tagTable` and `softwareUnit` on one object
   each; nothing is invoked ([capability discovery](../reference/capabilities.md)).
3. Assert that no `invocations[].name` matches `Move|Relocate|Reparent|Transfer|Cut|Paste`.

If any name matches, the identity probe follows: create `FB_Callee` with its instance DB and an `FC` that
calls it, record `Number`, `Name`, `CreationDate`, every `GetAttribute` value from `GetAttributeInfos`, the
instance DB's `InstanceOfName`, the cross-reference rows of the callee and the SimaticML export hash
(group path removed), invoke the action into a second user group, and compare every field. Identity is
preserved only if `CreationDate`, the instance DB link and the cross-reference rows are unchanged. Without a
match, the boundary above is final.

## Disposition

`block.move` stays `not_implemented` and moves from "deferred, feasibility unresolved" to "V21
boundary: no public move; recreation is not identity-preserving". Users move blocks in the TIA Portal
editor; recreation through `block_read` and the `source_upsert` change remains available and is a copy, not a move.
