# Library HMI adapters (V21)

The library subsystem has separate typed Classic WinCC and Unified adapters. Their presence is not a claim that M6/M7 in `library-management-plan.md` are accepted. Engineering, persistence, preservation, recovery, visual and runtime evidence are independent gates. PublicAPI assembly availability does not establish engineering product licensing or compatibility with a particular target.

## Available implementation routes

Use `library_query` with `action: hmi_capabilities`, `hmi_targets`, or `hmi_inventory`. Target identities are exact returned `hmiPath` values, scoped to an open project. Select `family: classic` or `family: unified`; family conversion is rejected. Inventory is bounded and declares omissions. Truncated inventory cannot authorize an HMI mutation.

`library_instantiate` / `library_integrate` payloads select a source type/version or master copy through the common native library contracts and include an exact `target`. Source/version dependencies must pass the common library planner. The enclosing executor checkpoints the project, compares the prepared HMI target fingerprint, applies the action once, compiles the selected HMI, and records recovery-required failures. Runtime and visual verification remain false until separate evidence proves them.

| Family | Action / target | Native route | Boundary |
| --- | --- | --- | --- |
| Classic | `hmiAction: instantiate`, `composition: screens` | `ScreenComposition.CreateFrom(ScreenLibraryTypeVersion)` | Committed version, collision rejection and exact native library-link readback. |
| Classic | `hmiAction: instantiate`, `composition: scripts` | `VBScriptComposition.CreateFrom(VBScriptLibraryTypeVersion)` | Preserves a native script; never executes it. |
| Classic | Single-root master copy into `screens`, `screenTemplates`, `screenPopups`, `tagTables`, `tags`, `scripts` | Each corresponding typed `CreateFrom(MasterCopy)` | Exact native `ContentType` and one root required. Root compositions only; tags require an explicit existing root `tagTable`. No broad reflected create endpoint. |
| Unified | `faceplate_create` | `ScreenItems.Create<HmiFaceplateContainer>`, `ContainedType`, rectangle properties | Exact committed version must already be in the project library. `integration.mode: instantiateOnly` is required because the new interface is unavailable before creation. Binding is a subsequent prepared operation. Exact native version and rectangle readback are mandatory; mismatch is a changed project needing recovery. |
| Unified | `faceplate_bind` | Named `Interface.Find(property).Value` | Existing exact-version container; explicit mapping for all native properties; existing tags/connections; native interface and tag data types must be readable and match. `IntegrationTypeUnavailable` blocks metadata that cannot establish compatibility. No index-based property assignment or guessed connection. |
| Unified | `tag_create` | `TagTable.Tags.Create`, typed `DataType` | Explicit existing table; new internal primitive tag only. External PLC/UDT binding is a separate unsupported integration route until its native mapping is verified. |
| Both, vendor eligible | Edit, discard, release, default, compare, export/publish | Common `LibraryTypeVersion` lifecycle and library operations | Native eligibility and project-library restrictions apply. These do not establish an authoring roundtrip for every HMI type category. |

The Classic adapter does not expose faceplate container objects. Siemens documents screen XML import/export, but imports can normalize sizes and interface values and fail for missing type/event/interface references. A preserving Classic faceplate XML editor requires independently exported native fixtures and readback/preservation tests before it can be advertised. It is currently returned as unsupported, rather than substituting a generic XML rewrite for a native versioned operation.

Unified's installed public API does not expose `CreateFrom(MasterCopy)` on screens or tags and does not define the Classic library-specific version subclasses. No such route is inferred. Screen creation/container creation is a distinct operation from native library instantiation. `ContainedType` uses Siemens' versioned reference form; native `LibraryTypeInstanceInfo` must prove that the resulting use has the requested GUID. If the target does not expose that service, the change cannot report exact-version success.

Unified `HmiSoftware` also does not implement `IEngineeringServiceProvider`, which native qualification confirmed. Its compilation route is the owning `Device` (hardware and software), not a cast to the Classic service interface and not `DeviceItem` (hardware only). Inventory and previews expose this exact broader compilation scope and preflight checks that the service exists. [Siemens compilation scopes](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-projects-and-project-data/compiling-a-project)

Other unqualified categories include nested faceplate placement, event/script rewriting, HMI-owned UDT authoring and PLC-UDT conversion, dynamic SVGs, styles, palettes, graphics/text resources and cross-device PLC communication. Native API availability for one category does not enable its neighbours.

## Optional products

Runtime prerequisites are the installed V21 `Siemens.Engineering.WinCC.dll` and/or `Siemens.Engineering.WinCCUnified.dll`, the corresponding licensed engineering product and target device/runtime version. Vendor DLLs are non-copying references and never redistributed. The worker resolves them from its configured PublicAPI directory only. No optional HMI type is stored in session state; family-specific method bodies use `NoInlining` and are entered after the module check. PLC-only code paths must continue to work when either family is absent.

`scripts/verify_library_optional_hmi.ps1 -Worker <worker.exe>` verifies worker construction, `bridge_info`, `library_capabilities` and disposal while actively refusing every WinCC/WinCCUnified assembly resolution. It also checks that no alternate resolver loaded an HMI module. This metadata/startup isolation test passed against the full compiled worker on 8 October 2026; it does not invoke native TIA or claim an end-to-end PLC engineering run with products uninstalled.

Profiles require separate evidence for Classic Comfort Panel, RT Advanced, Basic and RT Professional, plus Unified Comfort and Unified PC. A fixture passes only its recorded exact device/runtime configuration. Missing products, access or fixture data are blocked outcomes, never passing skips.

## Qualification tools and required fixtures

`scripts/qualify_library_hmi.ps1` compiles `fixtures/libraries/NativeHmiProbe.cs` against the installed V21 PublicAPI and runs an owned isolated TIA process. It does not attach to existing processes or interact with simulators. Supply a fresh output directory; evidence and native outputs are never overwritten. Each native stage is written before invocation.

For catalog discovery:

```powershell
./scripts/qualify_library_hmi.ps1 -Profile fixtures/libraries/hmi-catalog-probe.json -OutputDirectory artifacts/my-hmi-catalog-run
```

For an exact target, supply a JSON profile with `family` (`classic` or `unified`) and a `typeIdentifier` returned by this host's catalog. The probe verifies that identifier before device creation. Classic creates two owned devices, imports the explicitly supplied `screenXml` fixture through the public SimaticML import API, exports the native result, authors a master copy and copies it into the second device, then compiles. Unified creates a screen and an internal Boolean tag and compiles. Both save, close, reopen, verify native readback and archive their owned project. The minimal empty Classic screen fixture is authored public SimaticML; it is not a faceplate or private-format template.

On 8 October 2026, the isolated Classic TP700 Comfort `OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0` foundation probe passed on TIA V21 Update 2: public screen import, canonical export, native master-copy authoring/consumption, zero-error/zero-warning compilation, fresh-reopen readback and archive. Evidence is retained under `artifacts/library-hmi-classic-20261008-b`. Source, consumer and reopened screen exports compare identically under `scripts/verify_library_hmi_screen.py`, which preserves native IDs, bindings, script text and all screen attributes, excluding only document export metadata and interelement indentation. This evidence qualifies the native empty-screen foundation probe, not all worker adapter paths or full Classic faceplate support.

The corresponding Unified MTP700 Comfort `OrderNumber:6AV2 128-3GB06-0AXx/21.0.0.0` probe created the native screen and internal tag and successfully compiled hardware, but software compilation failed with two Runtime settings errors: the unset encrypted-transfer password violated complexity requirements and its confirmation did not match. Complete diagnostics are retained under `artifacts/library-hmi-unified-20261008-e/qualification.json`. The fixture is failed, and persistence/archive stages were not run; this is not Unified qualification. A fresh Unified fixture must have its encrypted-transfer credentials explicitly configured before the compilation gate. Siemens enables encrypted transfer by default; the probe does not disable it to obtain a passing result. [Siemens secure communication prerequisites](https://docs.tia.siemens.cloud/r/en-us/v21/compiling-and-loading-rt-unified/basics-rt-unified/secure-communication-rt-unified)

`scripts/library_hmi_evidence.py` independently checks required fixture stages. Catalog-only reports, incomplete native stages, missing fresh-reopen readback and compiler errors cannot pass. Even a successful foundation probe keeps full faceplate, recovery, visual and runtime gates false. Its regression and screen-preservation checks run with `python scripts/test_library_hmi_evidence.py`.

These are foundation probes, not full release acceptance. Full acceptance additionally needs:

- Authored released faceplate and HMI UDT version pairs, an incompatible interface version and explicit expected native GUIDs.
- Complete independent expected screen/resource/binding manifests, including unrelated content, multilingual text and event behavior.
- Combined PLC/HMI connection mappings with exact connection partner and symbolic tag path, tested on matching runtimes.
- Fresh-consumer publication/retrieval, exact-version update, save/reopen readback and checkpoint recovery comparisons.
- Visual screenshots and explicit assertions for layout, events, scripts, navigation, dynamic values and PLC communication.

Pure library mapping checks run with `dotnet run --project tests/TiaMcp.LibraryHmi.Tests`. They validate family, name, placement, interface completeness, native version, data type/capacity and localized internal-tag default rules. They do not start Siemens or qualify a native route. Newly-created Unified internal tags can expose a localized `Connection` sentinel (observed `<Interne Variable>` on this German engineering installation); internal-tag validation preserves that native default and verifies that no configured connection or PLC tag was attached, instead of assuming an empty string.

The production creation adapter now uses that rule through the shared creation
boundary. Previously, only the native foundation probe and pure rule handled the
localized label; the production adapter still rejected every nonempty connection.
The repaired adapter records the original and returned label, PLC tag and native
connection lookup result. Its 18 boundary cases pass on both .NET 10 and Framework,
including failure after creation without retries. The complete V21 worker builds,
and the repaired optional-module probe passes metadata calls with HMI assembly
loading refused. These checks do not complete native workflow qualification.
See [repair and evidence](../verification/unified-internal-tag-20261023.md).

## Siemens references

- [Classic screen import with a faceplate](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/importing-a-screen-with-a-faceplate-instance)
- [Unified faceplate container creation](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/faceplatecontainer.create)
- [Unified contained type property](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/accessing-contained-type-properties)
- [Unified UDT assignment](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/screens/screenitems/controls/faceplate-container/assigning-udt-to-faceplate-container)

The installed V21 XML and DLL signatures were used to compile the actual adapters; documentation examples alone are not implementation evidence.

## Library-link observation and import guards

Shared HMI readback now validates service ownership with native equality and uses
one observed type/version pair for both comparison and reporting. `linkStatus`
distinguishes `linked`, `noService` and `unavailable`; unknown reads retain an error
and null identities. `nativeLinkVerified` requires the exact requested type/version
pair; it does not prove content, dependencies or runtime behavior. Both Classic
preserving-import phases allow only confirmed absence of the service, require
library handoff for a known link, and refuse unavailable observations.

The helper passes portable and actual Framework tests with synthetic providers.
These are not native SDK, caller, optional-module loading or TIA acceptance.
See the [repair evidence](../verification/hmi-link-readback-20261018.md).
