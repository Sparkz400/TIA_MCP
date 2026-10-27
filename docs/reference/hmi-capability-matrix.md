# HMI capability evidence — Siemens V21

Observed locally on 8 October 2026. Four states are deliberately independent: **public API available**, **bridge implemented**, **native fixture exercised**, and **currently selected target accessible**. A DLL, installed license manager, or catalog row establishes none of the other states. Runtime/deployment verification is false throughout.

The installed `Siemens.Engineering.WinCC` and `Siemens.Engineering.WinCCUnified` assembly/XML identities and hashes are retained in `artifacts/hmi-api-v21-20261008.json`. Native Classic exports identify TIA Portal V21 Update 2, WinCC Basic/Comfort/Advanced V21 Update 2, and WinCC Unified V21 Update 2. The independently seeded target catalog evidence is in `artifacts/hmi-native-20261008-d/native-report.json`; both families' software objects were actually obtained. This proves accessible engineering operations in this environment, not a transferable license entitlement.

## Exact native target profiles

| Profile | Independently selected catalog identity | Runtime version | Current fixture availability |
| --- | --- | --- | --- |
| `classic-comfort` | `OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0`, TP700 Comfort | 17.0.0.0 | Native software accessible; independent archive and reopen/recovery readback retained |
| `classic-rt-advanced` | `OrderNumber:6AV2 104-0xxxx-xxxx/17.0.0.0`, WinCC RT Advanced | 17.0.0.0 | Native software accessible; independent archive and reopen/recovery readback retained |
| `classic-basic` | `OrderNumber:6AV2 123-2GB03-0AX0/17.0.0.0`, KTP700 Basic PN | 17.0.0.0 | Native software accessible; independent archive and reopen/recovery readback retained |
| `classic-rt-professional` | Exact catalog filter `WinCC RT Professional` | Not observed | No installed catalog matches; no native target fixture and no inherited qualification |
| `unified-comfort` | `OrderNumber:6AV2 128-3GB06-0AXx/21.0.1.0`, MTP700 Unified Comfort | 21.0.1.0 | Native software accessible; independent archive and reopen/recovery readback retained |
| `unified-pc` | `OrderNumber:6AV2 155-xxxxx-xxxx/21.0.1.0`, WinCC Unified PC RT | 21.0.1.0 | Native software accessible; independent archive and reopen/recovery readback retained |

Catalog identifiers above are native observations, including Siemens placeholder characters. They are not guessed order numbers. Fixture runtime version 17 is not the installed engineering assembly version 21.

These rows describe successful or blocked fixture creation, not the target currently attached to a different worker. `hmi_capabilities` probes the live selected target separately and leaves licensing unprobed until a relevant native access succeeds or fails. Native runtime visibility, protection and missing engineering options can still make an installed-family target inaccessible. An `unknown` profile remains unknown when native catalog metadata cannot prove a family/device profile.

## API and bridge surfaces

| Surface | Classic WinCC public V21 API | WinCC Unified public V21 API | Bridge and preservation boundary |
| --- | --- | --- | --- |
| Device/software discovery | `SoftwareContainer.Software` → `HmiTarget` | `SoftwareContainer.Software` → `HmiSoftware` | Optional adapters, exact HMI identities without a PLC owner; null/error remains a diagnostic |
| Connections | `HmiTarget.Connections`; native export/import | `HmiSoftware.Connections`; typed properties/create | Bounded inspection; integrated HW connection native endpoints can establish exact PLC ownership |
| Tags/tables | `TagFolder`/`TagTables`/`Tags`; native export/import | `Tags` and `TagTables.Tags`; typed creation/properties | Unified flat/table views are aliases only with matching table scope and native attributes |
| Screens | `ScreenFolder`, templates, popups, slide-ins; native export/import | `Screens`; typed creation/properties | General inspection enumerates all six Classic screen roles with distinct canonical paths and per-scope coverage, plus Unified screens. Added Classic roles have offline traversal/model/schema checks and SDK compilation; their native inventory, item parsing and ordinary source-export acceptance remain unverified. Special Classic exports also participate in separate preservation capture. No complete Unified screen export is assumed. |
| Screen items | Items are obtained from retained actual native screen XML | Typed `ScreenItems`, properties, dynamics, event metadata | Geometry is exposed; original XML/native source remains evidence; no project script execution |
| Alarms | No public `HmiTarget` alarm composition observed | Typed discrete/analog alarm collections | Classic explicitly unsupported; Unified read coverage is separately bounded |
| Scripts | `VBScriptFolder` and export/import | Native script modules/export plus screen/item script/event/dynamic source | Modules are inventoried; supported source is read on demand. There is no JavaScript AST, static-navigation or script behavior parser. Unknown tag/navigation expressions remain unresolved. |
| Resources | Text/graphic lists and exports | Text/graphic lists and exposed properties | The supported subset is text lists and graphic lists. Standalone images, fonts, global resource repositories and unknown types are excluded. Empty inventories qualify only empty fixture enumeration. |
| Faceplates | Native screen XML may contain instance/type references | Faceplate containers expose type/interface metadata | Library lifecycle remains in the library subsystem; placement/binding requires explicit handoff and qualification |
| Compile | Classic `ICompilable` on `HmiTarget` | `ICompilable` on owning `Device` (software itself is not a service provider) | Native compile results and pre-existing errors retained per fixture |

Both families have inspection/reference bridge routes. Native qualification is narrower than these implementations: see the operation-specific reports in [qualification](../verification/hmi-qualification.md). RT Professional has no local native qualification. Rich alarms/scripts/resources/faceplates and protected projects need nonempty independent native fixtures before claiming their full content coverage.

The completed d report establishes the five exact native targets above. `hmi-bridge-20261008-root-g.json` passes bounded read-only inventory, exact screen-item/scalar comparisons, duplicate scopes, stale/wrong-family controls and actual explorer integration for all five profiles. Completed f adds native integrated connections for Classic Comfort and Unified Comfort, with Unified scalar PLC tag and DB-member configurations. Completed g adds nonempty Unified alarms and screen event/global script source. Historical f/g Unified PC link authoring failed because its new PC station had no network interface; that fixture error is retained and is not an external license limitation. Fresh PC fixture h adds a catalog-observed, native-compatible IE General module, creates both exact integrated links and linked scalar/DB tags, and completes the native lifecycle. Final-package bridge comparison of that corrected fixture remains pending at the requested checkpoint.

Immutable root-i observations plus supplemental `root-i-reassessment-b` qualify Unified Comfort's direct item-to-tag-to-connection-to-exact PLC tag/DB member chain, and both Unified profiles' script/dynamic/broken observations. Missing-tag native getter errors remain explicit incomplete coverage. Root-i Classic launch failed native authorization, while subsequent Unified launches succeeded. These fixtures contain real compiler failures. The f Classic ordered save/reopen and archive comparison failed because screen enumeration order changed; all eight scoped paired screen exports remain equal apart from the exporter timestamp. Original gates remain unchanged. Historical root-e comparison failures and root-h startup timeout remain retained. See qualification for remaining acceptance gates.

Unified screen inventories defer behavior capture. An exact screen `objectPath`, selected screen items, or `includeScreenOwner:true` on an items request reads exposed dynamics, event handlers and property event handlers. Native script source/global text, hashes, async state and trigger scalar properties are retained without execution. Per-composition coverage is explicit. Direct tag dynamics require observed non-indirect, non-formula metadata; formula and indirect references stay dynamic. Missing scalar metadata on a broken item remains a diagnostic without invalidating an otherwise completely enumerated candidate scope.

HMI-to-PLC relationships require exact native endpoint ownership from V21 `CommunicationManagement` or native cross-reference object equality. Unified configured scalar symbols then resolve only inside that proven PLC scope, with type checks; DB members require a retained native declaration export with exact component paths. Duplicate PLC tag names, matching addresses, textual partner names and script strings alone do not prove ownership. Arrays, multiplexing, opaque UDTs and unknown Classic XML bindings remain unresolved. See [the evidence rules](hmi-relationship-evidence.md).

## Engineering operations

Every profile currently fails closed for production HMI mutation. Allowlisted bodies exist for `scalar_property_set`, Classic whole-object `classic_xml_import`, tag creation, item properties, placement, explicit bindings, and library placement handoff, but existence is not enabled support. The exact diagnostics are in [HMI engineering](../guides/hmi-engineering.md).

| Operation | Classic bridge body | Unified bridge body | Production availability for every exact profile above |
| --- | --- | --- | --- |
| `scalar_property_set` | Standalone screen `BackColor` through preserving native XML import; library type instances rejected | Existing item `Enabled`, `Visible` or `TabIndex` | Unavailable: precise effect alone does not establish complete preservation/dependencies, compile, persistence, recovery and visual acceptance |
| `classic_xml_import` | Whole-object native export imported with `Override` through the exact object's owning composition; its re-export must equal the supplied document and every other HMI export must stay unchanged; library-linked screens/scripts rejected | Unavailable: no Unified screen export can verify a preserving import | Unavailable: import dependency closure, whole-HMI preservation, compile, persistence, recovery and visual acceptance are unqualified |
| `tag_create` | Unavailable: preserving XML tag-creation adapter remains required | Guarded primitive internal tag creation in an exact native table | Unavailable: exact-profile export/readback dependency closure and unrelated-resource preservation are unqualified |
| `external_tag_create` | Unavailable: preserving XML binding grammar for Classic PLC tags is not established | Guarded symbolic PLC tag on an existing integrated connection, with the native endpoint owner and exact root PLC tag/type proven before writing | Unavailable: tag-table dependency closure, unrelated-resource preservation, compile, persistence, recovery and PLC communication are unqualified; the [prepare-only probe](../verification/hmi-external-tag-create-20261026.md) passed on Unified Comfort |
| `item_properties_set` | Unavailable: typed screen-item mutation route absent | Guarded allowlisted item properties | Unavailable: complete Unified screen readback/export scope is not established |
| `item_place` | Unavailable: preserving XML item-placement adapter remains required | Guarded existing top-level faceplate rectangle within exact screen bounds | Unavailable: complete content/dependency and coordinate-system qualification is required |
| `existing_binding_set` | Unavailable: preserving XML binding adapter remains required | Guarded existing faceplate interface, exact tag identity/type and expected old value | Unavailable: complete preservation, exact binding dependencies and native acceptance are required |
| `explicit_binding_set` | Unavailable: preserving XML binding adapter remains required | Guarded existing faceplate interface with explicit same-HMI tag identity/type | Unavailable: complete preservation, exact binding dependencies and native acceptance are required |

These rows mirror server-side `HmiEngineeringRules.Gate`: Classic scalar, Classic XML import and the Unified operations report `guarded_body`, other Classic operations and Unified `classic_xml_import` report `unavailable`, and every production operation returns `available:false` with `HmiPreservationUnavailable`. Caller flags, native setter availability and fixture trials cannot override that gate. A Classic native tag import experiment is not a qualified creation adapter. Library type/version lifecycle, transfer, publication and updates remain in the library subsystem; the HMI destination/placement/binding handoff is only a validated prerequisite.

Classic standalone screen `BackColor` was independently changed by native `ScreenComposition.Import(..., Override)`, precisely re-exported, compared, and restored. It cannot be set through the screen's exposed attribute interface: native `GetAttribute("BackColor")` rejects it. The native export contains `BackColor`; the self-described attribute list only exposes read-only `Name`. Only `Document/DocumentInfo/Created` changed on repeated unchanged exports and is normalized in evidence comparison. IDs, bindings, events, translations and unknown payload are retained.

This selected-screen trial does **not** establish whole-HMI preservation or authorize the production mutation. Classic hidden alarm/runtime/type content and library provenance remain gaps; Siemens states that exporting a screen type instance omits type information, and importing it detaches that instance. Unified lacks a complete preserving screen export/readback route for every relevant event/dynamic/resource/type dependency. A scalar property alone does not overcome these preservation gates.

## Vendor evidence

The discovery began with the installed V21 public assemblies/XML documentation. Relevant primary vendor references:

Installed documentation root: `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48`. SHA-256 values read from that installation:

| File | SHA-256 |
| --- | --- |
| `Siemens.Engineering.Base.xml` | `0bf27f4ab7918c811a7dfde7f7c294b3133169b5f791e507278bfd27182cea43` |
| `Siemens.Engineering.WinCC.xml` | `2d1c7e1816b43ab10ead3036d37e007d15540ddbddc6a1a239635f71e43e7615` |
| `Siemens.Engineering.WinCCUnified.xml` | `894afc507742b3498a3be4273e6818b05677103ecdd4687af1c6d1caa4a5f73b` |
| `Siemens.Engineering.WinCC.Extension.xml` | `1b7eb100a6900509ceb548bb35c72e81087cf5ab0af1aa850bf04db991f45307` |

The Classic and Unified DLL hashes in the API probe are respectively `b6558405bd196b0dffcb64258482312b98ea3f7f6867c2e7fed7f060c5c82bd7` and `937852a43fb15a2b87e79fc403e4ac1467f389d1c0b528536e3ad244ef1bd0ff`. Optional resolution validates the expected family assembly name, major version 21, neutral culture and Siemens public-key identity anchored in the loaded Base assembly. Hash evidence identifies the inspected binaries; it does not establish licensing or qualification.

- [Unified device license access](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/checking-license-to-access-unified-device): license failure and inaccessible software must be reported separately.
- [Classic screen import](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-screens-to-an-hmi-device): screen structure, properties, target dimensions and library instance caveats.
- [Classic import/export structure](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/data-structure-for-import/export/structure-of-the-data-for-importing/exporting): typed objects, composition names, IDs and links.
- [Classic faceplate screen import](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-a-screen-with-a-faceplate-instance): instance dependencies belong to explicit library integration.
- [Unified faceplate UDT assignment](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/assigning-udt-to-faceplate-container): qualified type/interface mapping is required.
- [V21 Openness changes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/what-s-new-in-tia-portal-openness): public communication management includes HMI connections.
- [Unified tag properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/tags/accessing-tag-properties) and [connection properties](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/connections/accessing-connection-properties): configured foreign keys and read-only endpoint text are distinct evidence.
- [Unified external-tag addressing](https://docs.tia.siemens.cloud/r/en-us/v21/configuring-tags-rt-unified/basics-rt-unified/addressing-external-tags-rt-unified): symbolic access is restricted to integrated connections.
- [Unified tag-dynamization formula](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/dynamization/accessing-tag-dynamization-formula): formulas may reference multiple tags and operators and are not direct static bindings.

Public references document API semantics, not the pass status of this bridge. No Siemens assemblies are copied into fixture or release packages.
