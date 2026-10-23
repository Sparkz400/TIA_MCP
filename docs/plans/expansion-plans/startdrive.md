# D10 — Startdrive and drive engineering

Audit: 17 October 2026. Every feasible capability remains a 1.0 obligation under
the [release ledger](../release-obligations-20261015.md). No Siemens product,
Startdrive SDK, product license or drive bench is available on this workstation.
Documentation, retained member inventories, portable implementation and actual
target qualification are separate evidence levels.

## Source audit and reusable foundations

`src/TiaMcp.Openness.V21` has no Startdrive/DriveParameter/SDR adapter. The catalog's
`drives.engineering` obligation is still unimplemented natively. Existing hardware,
TO profiles, exact project-model references, prepared changes and engineering
jobs are foundations, not qualification for drive-specific effects.

`OpennessSession.ProjectModel.cs::CaptureModelItems` retains exact hardware paths,
position/classification and built-in/plugged state; it does not capture drive
objects, firmware, parameters, BICO wiring or telegrams. Consequently selecting
a saved hardware node cannot validate supplied drive metadata. The existing
ordinary-drive trace route remains not established; parameter access is not a
recorder. See [trace feasibility](bug-driven-trace-feasibility.md).

The retained [14 October inventory](api-coverage-inventory-20261014.json) names
969 Startdrive XML members / 64 types, with 330 heuristic semantic candidates.
It is historical declaration evidence, not current installed visibility, setters,
ownership or product access. The full member scan includes upload/download
callback types, SafetyAcceptanceTestReport.CreateProtocol and RAM-to-ROM, but did
not establish a generic parameter-file export route. Infrastructure classifications
must not be treated as proof of absence. Public assembly metadata remains needed.

The first portable increment uses `Core/Drives/DriveProfileReview.cs`,
`DriveProfileWorkflowStore.cs` and `Host/DriveProfileTools.cs` to compare explicitly
supplied observations and offline proposals. It reuses saved-review/artifact
upload, integrity, storage and MCP patterns. [Workflow](../../reference/drive-profile-review.md).
It does not parse an invented Siemens file format or create native evidence.

There is one primary checkout; no concurrent source owner or parallel agent work
was found. Shared registration/catalog/registry/bindings and aggregate/package
scripts must remain coordinated with the other workstreams.

## Documentation and capability matrix

Context7's official V20 index returned generic/failsafe/DriveController and
unrelated CSV routes for three bounded queries. Those snippets were not accepted
as Startdrive API or format proof. Official V21 reference/example pages below were
read directly. Examples contain positional selectors and occasional syntax/case
errors; installed public metadata must settle exact declarations before adapters
are compiled. Their example auto-acceptance of trust or confirmations is not the
intended authorization policy.

| Workflow | Verified public route / source | Prerequisites, current state and open work |
| --- | --- | --- |
| Drive/CU/DO inventory | DeviceItem `DriveObjectContainer` → DriveObjects; `DriveObject.Parameters`, Telegrams [DriveObject](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/driveobject) | MC.Drives in Startdrive assembly; exact eligible DeviceItem, family/order/firmware/options. Native capture absent. Reference table omits ReadParameters while official examples use it; verify metadata. No `[0]` or name-only target selection. |
| Offline parameters and bits | `DriveParameter` Name, Number, ArrayIndex, ArrayLength, Bits, Value, Unit, MinValue/MaxValue, EnumValueList [parameter reference](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/driveparameter?contentId=zZPPnm4NYgnCTcC7kOrmCw), [read/write example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-parameters?contentId=gsh3DvaCMe6XhZxa3~bhkA) | Not every parameter is exposed. Bounds depend on current unit; Value is object-valued. Arrayless parameters use -1. Bits is a collection of available named bits, not bit-number indexing. Supplied comparison implemented; typed access and native writes remain unqualified. |
| Read-only parameter access | `ReadParameters.Find`, ReadDriveParameter on offline and online objects [read example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-out-parameter-values-and-attributes?contentId=8qzSI46WtOOkyl1FCa~ZWw) | Separate route and availability metadata. Do not infer setters from a readable Value, a misleading table heading or a p/r prefix. Pin exact types/accessors and native error cases. |
| BICO | Value may be a DriveParameter reference, scalar or unavailable [BICO example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-bico-parameters?contentId=IuN8oMgvtQY8_W8LCugRuQ) | Sources may be read-only; a null sink value can mean inaccessible/invalid source. Preserve exact source owner plus full parameter name and unavailable reason. Supplied-reference navigation is implemented; native compatibility and dependent wiring effects are not. |
| Drive/component creation | `Devices.CreateWithItem` with family-specific TypeIdentifier; `Device.PlugNew` [drive creation](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/creating-a-drive?contentId=WquywGO8JmOfKeFUuNGu_g), [component creation](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/creating-a-drive-component?contentId=8kBwbMXhPoSaWsDM7J~E4g) | Validate catalog, order/firmware/additional family token, position and complete resulting inventory. Public family lists/examples are not an installed catalog. No creation adapter or acceptance here. |
| Telegrams / process data | TelegramNumber, Type, Addresses, PKW, hardware identifiers; CanChangeTelegram, GetSize/GetSizeInBytes, CanChangeSize/ChangeSize [Telegram](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/telegram?contentId=1BdKwwY3Ro6_h38bnSGzaw) | Explicit direction, size unit and keepOriginalAddress policy. Main telegram enlargement rules and free telegram 999 require actual target/profile evidence. Separate PKW/safety/torque channels and resource impacts. No native telegram edit implemented. |
| TO/axis/encoder connection | V21 SDR connection providers/interfaces and `Connect(Telegram, ...)` [TO connection](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/connecting-technology-objects-with-hardware-modules-via-telegram-objects?contentId=GYIG1xfucCDd6MKMux~3sw) | Coordinate D13 and shared identities. Do not reuse pre-V21 signatures or copy misspelled provider names from examples. Exact TO type/version, actor/encoder interface and resulting dependencies need metadata and native readback. Broad AllowAllModules is not a default. |
| DRIVE-CLiQ wiring | `NetworkPort.ConnectToPort` / DisconnectFromPort [wiring example](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/editing-drive-cliq-connections?contentId=7TwErTSz1f6qXTsEcAEtdA) | Documented example has fixed module positions; never generalize them. Resolve exact port ownership, allowed topology, auto-created edges and full affected-device preservation. Reuse D08 topology work where compatible. |
| Motor/encoder datasets | DFI/OnlineDFI HardwareProjection, GetCurrent/ProjectMotorConfiguration, GetCurrent/ProjectEncoderConfiguration, SetMotorType/SetEncoder [projection](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/hardwareprojection?contentId=NyWBNrnpP0J4wWuNJ9o7Xg) | G120 requires a Power Module and supports offline/online configuration; S120 route is offline. SetMotorType/SetEncoder are G120-specific. Dataset and encoder indices are explicit; null/unsupported is not empty success. Typed ConfigurationEntry values and all dependent effects require fixtures. |
| Commissioning / activation | DFI Commissioning.SetSimoGearMlfb; DriveObjectActivation state/query/change [commissioning](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/commissioning?contentId=G5Qw_gwCX1T7ARA5r~81Pg), [activation](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/driveobjectactivation?contentId=OQLdt31J7MLxs5rQ7yRO~g) | Family-specific functions and offline/online actions need individual profiles. Querying activation differs from Activate/Deactivate/DeactivateAndNotPresent. A false action result is not success. Motion-producing functions require a separate controlled bench admission. |
| Technology Extensions | Portal TechnologyExtensionInstallationProvider, package install/identifier/uninstall; per-DO extension activation [extensions](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/using-technology-extensions?contentId=aDo6a1WbtYCzXdhTqQUq4g) | `.tec` installation affects the engineering installation; activation affects eligible targets. Verify package integrity, installed identity, entitlement, in-use dependencies and recovery. Never infer supported trace from a TRCDATA example. No portable `.tec` parser or installer is implemented. |
| Online parameter reads/writes | `OnlineDriveObjectContainer` / OnlineDriveObject.Parameters [online access](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/reading-and-writing-parameters-online?contentId=QaYbrzsvR8FZE6Pj4CJeHw) | Require explicit target/interface/session, role/security and firmware. Read, upload into offline engineering, write and download are different capabilities. No online adapter or automatic connection here. |
| Download and upload | DownloadProvider plus Startdrive confirmation/password/selection callbacks [download](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/download?contentId=3rXcaHLqiaXw3ENtyP4IaQ) | FW6.1 security setup introduces project UMAC and TLS prerequisites. Do not auto-select the first interface, trust certificates or confirm destructive choices. Retained upload callback declarations alone do not establish an eligible drive upload owner/operation; that route remains a feasibility gate. |
| Durable drive parameterization | OnlineDFI DriveDomainFunctions.PerformRAMtoROMCopyAllDriveObject [persistence](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/code-examples/saving-the-parameterization?contentId=fctL7fDAt3xTg_TfvFL3xA) | This is device-wide online persistence, not a file export. CU ownership/family, online state and all affected DOs must be explicit. Null/unsupported service and power loss need separate outcomes/recovery. Never trigger as a side effect of comparison or upload. |
| Access/security | project UMAC rights [introduction](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/introduction); FW6.1 UmacConfiguration and DriveDataEncryption [UMAC](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/umacconfiguration-from-sinamics-fw-v6.1?contentId=mEuFMvHjKIHZXW03t8npdQ), [DDE](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-startdrive/references/drivedataencryption-from-sinamics-fw-v6.1?contentId=QLB~zWRjohOGkWM7iup6rQ) | Security activation/deactivation is a distinct change; encrypted data uses SecureString credentials. Do not weaken protection to make another operation succeed. Rights, certificates and secrets need exact policy, redaction and recovery. |
| Safety and advanced functions | Retained members: SafetyAcceptanceTestProvider/Report.CreateProtocol, SafetyCommissioning, TestFunction, FunctionInUse and checksum/telegram families | Public names alone are insufficient to implement effects or certify safety. Pin official member descriptions, installed metadata and product/firmware/license applicability. Report generation, safety activation tests and actual safety acceptance are different obligations; coordinate D14/D18 and specialist qualification. |

No general drive parameter CSV/XML import/export format was established by this
audit. The portable observation schema is explicitly our own. Genuine supported
files can be added only after documented format, encoding, identity and preservation
contracts are known. Unresolved routes remain open rather than invented or removed.

## Architecture and contracts

Delivered offline tools: `drive_profile_review` and `drive_profile_review_page`;
contract `contracts/drive-profile-review.v1.schema.json`. They retain exact supplied
JSON, bind one saved hardware selection, preserve full named parameters, scalar
text/reference/unavailable values, explicit offline/online domains, parent chains
and opaque unit/limit metadata. Proposed offline changes expose contradictions in
supplied read-only evidence but never produce an admission token. Complete flags,
matching names/versions and supplied access cannot certify native state.

Future native tools below are proposals, not registered endpoints:
`drive_observe`, `drive_parameters_page`, `drive_profile_capture_start`,
`drive_online_read_start`, `drive_upload_start`. Use existing prepared-change/job
APIs for supported offline edits and separate qualified operation kinds for
download, online writes, RAM-to-ROM, activation, security and motion functions.
No native object crosses the worker pipe. Load Startdrive lazily or in an optional
worker if product/assembly isolation requires it; absence is an explicit capability
result and must not break ordinary Host operation.

Native identity must bind project/session epoch, exact device/CU/DO paths and
public IDs where exposed, DO number/type, order/firmware/options, parameter set or
dataset, complete parameter name, numeric/index metadata and explicit named bit.
Include unit/value runtime type, current enum/range/access evidence, unresolved
references, source/program/target observation identity and coverage/truncation.
Never use collection order, display names, a p/r prefix or a guessed bit parser
as native identity or writeability proof. Metadata getters can fail independently;
successful peers and precise failures must remain in the capture.

Prepared offline changes need expected original values/units/constraints, exact
profile applicability, intended edits and dependency closure. Reobserve before
dispatch and refuse changed firmware, dataset, unit, access, topology or source.
Numeric conversions and enum/range checks require pinned CLR/native type and
unit contracts; until then retain text/metadata and say not assessed. Templates
need versioned source lineage and explicit target mappings, not silent cross-family
or cross-firmware copying. A supplied proposal is not a qualified template.

Separate authorities: offline engineering edit; online read; upload into offline
engineering; online write; download; activation/function call; durable persistence;
security change; safety/motion. Capture-to-file and observation cannot escalate
to another category. Credential handles are transient; no passwords/tokens enter
profiles, artifact hashes, logs or templates. Certificate change and unhandled
confirmation require explicit refusal/action, not default trust.

Use project and physical-target reservations with bounded jobs and immutable
results. Preserve full native before state and checkpoints before any engineering
change; CU-wide operations reserve and record every affected DO. Cancellation or
lost response after dispatch is unknown-outcome and requires reobservation, never
automatic replay. Preserve partial upload/download and failure evidence. Power
loss is a runtime/bench scenario; an offline fake process cannot certify recovery.

## Dependency-ordered work packages

Effort is focused engineering days with prerequisites available, excluding product
procurement, owner scheduling and bench duration. These are ranges, not promises.

| ID | Inputs / concrete boundary | Completion evidence / remaining work | Estimate |
| --- | --- | --- | --- |
| SD-01 | This audit, retained member inventory; public Startdrive metadata/schema inventory | Documentation/source audit delivered. Pin current public types/accessors/owners, supported family/catalog/license matrix, upload route, safety/report metadata and genuine file formats. No native support inferred from declarations. | 2–5 + access wait |
| SD-02 | Supplied observation contract; Core/Host/artifact store/tests | First portable profile comparison implemented. Exact source retention, named bits, BICO unknowns, access contradictions, immutable paging/restart/tamper and package delivery. Genuine observations remain needed for compatibility. | portable increment; 2–4 genuine profiles |
| SD-03 | SD-01, saved model extension, optional native adapter and Core DTOs | Complete bounded parameter/metadata/DO/telegram capture with independent getter failures and partial coverage. Integrate Explorer details and diagnostic references. Verify exact native readback and secret-free records. | 4–7 |
| SD-04 | SD-03, qualified type/unit/access constraints and fixtures | Offline parameter/BICO edits and versioned templates: expected-before conflicts, exact target mapping, dependent effects, compile where supported, full preservation/save/reopen/checkpoint recovery. Metadata presence is insufficient. | 5–9 |
| SD-05 | SD-03 + D08/D13; hardware/telegram/TO profiles | One complete drive → telegram → axis/encoder workflow, including addresses/directions/units, DRIVE-CLiQ, dataset configuration and unchanged-peer effects. Extend each family explicitly; no blanket support from one fixture. | 6–12 |
| SD-06 | SD-01, exact target/session model and D09 shared transport policy | Online read first, then independently established upload route. Authentication, TLS, stale identity, cancellation and partial outcomes tested with fake transport and real targets separately. | 4–8 |
| SD-07 | SD-04/06 plus bench admission and recovery | Separate online writes/download/RAM-to-ROM/activation/function/TEC/security routes, per family/firmware/role and whole-effect scope. Confirm false/unknown outcomes and no replay after interruption. | 7–14 + bench |
| SD-08 | SD-01 + D14/D18 specialist prerequisites | Supported safety reports/checksums/telegrams/commissioning/test functions each get independent contracts and qualification. Ordinary parameter tests never certify safety or motion behavior. | 4–10 + specialist windows |
| SD-09 | All source/binary/profile/fixture identities and UI/native receipts | Complete release matrix, version compatibility, bounded scale, install/absence/recovery, owner workflow acceptance and unchanged original evidence. No current portable test satisfies native/runtime gates. | 4–8 + acceptance |

Phases: discovery (SD-01), first useful offline review (SD-02), native read/capture
then preserved offline engineering (SD-03/04/05), separate online and advanced
expansion (SD-06/07/08), release acceptance (SD-09). All feasible rows remain
required; unavailable prerequisites or unsupported public routes stay visible.

## Independent qualification matrix

Bind source, worker/Host binaries, SDK metadata, target model/firmware/options,
license/role, fixture project and profile hashes. Reuse existing accepted evidence
only within exact scope; do not schedule redundant historic VM campaigns.

1. Two same-name drives and multiple DOs/datasets: exact selection, renamed/moved
   hardware, stale session and changed firmware must not rebind. Validate against
   native inventory/readback independently of the adapter's own manifest.
2. Scalar, enum, arrayless/indexed and sparse bit parameters; readonly, inaccessible,
   missing and unsupported types; culture/Unicode/precision; current-unit bounds.
   Verify named bits, actual exposed access and dependent parameter changes.
3. BICO same-DO, other-DO and other-device sources, constants, invalid/unavailable
   sources and changed source identity. Retain exact relations and unknowns;
   UI/source name text alone cannot prove wiring or compatible signal type.
4. Telegram main/PKW/safety/torque channels, direction/size unit, address retention,
   auto-created consumers, free telegram compatibility and TO actor/encoder links.
   Confirm native resource/topology effects and unrelated-device preservation.
5. Motor/encoder datasets, absent Power Module and unsupported family services;
   compile where available, save/reopen, independent configuration readback and
   complete intended-delta check. Compilation does not prove motion behavior.
6. Offline mutation interruption before/after first checkpoint, lost response,
   corrupt receipts and partial cleanup. Fresh observation must distinguish
   unchanged/completed/partial/unknown state without repeating an action.
7. Online authorization denial, TLS change, wrong interface, target replacement,
   transfer loss and power failure. Track read/write/upload/download/persistence
   effects separately, including every CU-wide affected DO and retained originals.
8. TEC install/in-use removal, UMAC/DDE changes, unsupported roles and protected
   settings. No automatic uninstallation, weakened security, plaintext credentials
   or fabricated success. Safety and motion need independent specialist/bench
   evidence and physical acceptance, not fake callbacks or report creation alone.
9. Maximum partial inventories, bounded pages, original artifacts after restart,
   hostile input, package relocation, genuine formats and actual MCP-host UX.
   The stdio harness is not desktop visual acceptance.

## Open decisions and next executable work

Native API accessors/owners, drive upload semantics, genuine typed parameter
observations, supported parameter-file formats, safety report applicability and
product/firmware entitlement remain unresolved. SDK presence alone would still
not settle licensed target availability. No impossible route is declared beyond
the bounded evidence; no undocumented file generator or private project parser
is a substitute.

Finish SD-02 integrated verification and delivery, then extend only contracts
supported by genuine metadata/captures. Parallel independent offline domains may
continue while these prerequisites remain unavailable. SD-03–09 and the broad
`drives.engineering` obligation remain open. Nothing in this increment performs
native creation, upload/download, writes, activation, persistence, security or motion.
