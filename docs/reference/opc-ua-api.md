# OPC UA API audit and offline review

Status: isolated implementation handoff, 2026-10-14, with [offline dependency analysis](opc-ua-dependencies.md) added on 2026-10-16. The user confirmed that the existing coordinator remains active. These modules have no dispatcher registration, project mutations or native/runtime support claim. The complete [expansion plan](../plans/expansion-plans/opc-ua-engineering.md) remains the scope/qualification authority.

## Implemented entry points

`scripts/Inspect-OpcUaApi.ps1` reads the installed public DLL metadata with `MetadataLoadContext`; it has no execution-load fallback. `scripts/probe_opcua_api.py` inventories public XML members, optional product availability and installed XSDs, hashes the files, and can merge a matching accessor report. Reports refuse an existing destination. Neither starts or attaches to TIA. The installation audit found 129 relevant STEP 7 XML entries, 151 Base entries, 44 Unified entries and 3 Test Suite entries; all four public assemblies are present. These counts are an inventory, not supported feature counts. The 21 installed XSD files contain no UA NodeSet schema.

```powershell
pwsh -NoProfile -File scripts/Inspect-OpcUaApi.ps1 -ReportPath artifacts/my-opcua-accessors.json
python -B scripts/probe_opcua_api.py --accessor-report artifacts/my-opcua-accessors.json --report artifacts/my-opcua-api.json
pwsh -NoProfile -File scripts/Test-OpcUaOffline.ps1
```

`src/TiaMcp.OpcUa` is a separate pure .NET 10 library referencing the existing Core error contracts. This keeps the active coordinator's Core/Host/worker project files unchanged. `OpcUaModelReview.Inspect(byte[])` and `Compare(byte[],byte[],JsonObject?)` return bounded JSON reports. `OpcUaNodeIdentity.Parse` resolves local namespace indexes and aliases to URI-based identities. A standalone CLI provides a usable offline workflow pending host integration:

```powershell
dotnet run --project src/TiaMcp.OpcUa.Cli -- inspect fixtures/opcua/machine-v1.xml artifacts/machine-review.json
dotnet run --project src/TiaMcp.OpcUa.Cli -- compare fixtures/opcua/machine-v1.xml fixtures/opcua/machine-v2.xml artifacts/machine-impact.json fixtures/opcua/consumers-v1.json
```

Each output path must be new. The isolated test script is preferred during concurrent work: it freezes only the relevant inputs and builds into a unique artifact tree, without touching shared bin/obj outputs. CLI path access is local command-line authority; a future MCP wrapper must use committed upload references and host-owned artifact storage instead of exposing arbitrary paths.

The CLI also provides `dependencies` and `compare-set` for explicitly supplied collections. These resolve selected cross-file edges, report missing/ambiguous providers, and explain transitive client impact candidates using a v2 set-bound consumer manifest. See [commands, report semantics and independent tests](opc-ua-dependencies.md). The single-file review limitations below continue to apply to `inspect` and `compare`.

## API differences to preserve at integration

Installed references live under `C:/Program Files/Siemens/Automation/Portal V21/PublicAPI/V21/net48`. Full signatures, summaries and readable/writable properties are in the generated manifest. Installed metadata and Siemens documentation establish candidate routes, not target behavior.

| Public owner/type | Candidate operation | Important boundary |
| --- | --- | --- |
| `PlcSoftware.GetService<SW.OpcUa.OpcUaProvider>()`, `CommunicationGroup.ServerInterfaceGroup` | Enumerate `ServerInterfaces`, `SimaticInterfaces`, `ReferenceNamespaces`, `AccessControl` | Null/missing/inaccessible service differs from an empty collection. |
| `ServerInterfaceComposition.Create(string)`; `ServerInterface.Import/Export(FileInfo)`, `Delete()` | Custom interface lifecycle, writable Author/Enabled and comment items | Installed Name has no setter. No typed per-node CRUD. |
| `SimaticInterfaceComposition.Create(string)`; `SimaticInterface.Export(FileInfo)`, `Delete()` | SIMATIC lifecycle, writable Name/Author/Enabled/UseStringNodeIds | No import. Installed Name setter conflicts with older documentation; rename needs native evidence. |
| `ReferenceNamespaceComposition.Create(string,FileInfo)`; `ReferenceNamespace.Import/Export`, `Delete()` | Namespace lifecycle, Name/Author/Enabled/GenerateNodes/GeneratedInterfaceName/UseStringNodeIds | `GenerateNodes` is a property. No invented generation method or options overload. Timing remains unknown. |
| `AccessControl.RoleMappings`, `NamespaceAccessRestrictions` | Role mapping Create/AddStandardOpcUaRole, role setters/Delete; restriction booleans and `NamespacePermission.SetPermission(string,bool)` | AddStandardOpcUaRole returns void. No typed namespace-restriction/permission collection create/delete. Permission names/defaults/effective access require qualification. |
| `HW.Utilities.OpcUaExportProvider.Export(DeviceItem,FileInfo)` | Separate offline PLC address-space export, utility found by `OPCUAExportProvider` | Not a custom interface export/import substitute; PLC must be offline. |
| `HW.OpcUaSecurityPolicies` | Candidate dynamic UA submodule attribute `OpcUaSecurityPolicies` | Server must be active. Enum zero `None` differs from the named no-security policy flag. No writes enabled by this audit. |
| `HW.Features.OpcUaUserManagement` | Legacy users Create with SecureString, SetPassword, Delete | Server/password-auth prerequisites; do not extrapolate to newer UMAC or reuse scratch full-access roles. |
| `Security.LocalCertificateManager`, `LocalCertificateStore`, certificate templates/composition | Local certificate generation/import/export/delete, SAN/usage/validity; candidate `OpcUaServerCertificate` assignment | Private-key export safety, cryptographic adequacy, trust and assignment types unqualified. No discovered general trust/CRL route. |
| `HW.Features.CertificateManagementConfiguration` | Usage/expiry settings and supported-service groups (including installed OpcUaClient entry) | FW4.1 client prerequisite and derived ApplicationUri effects need exact native profile. Runtime GDS stays outside this module. |
| Unified `HmiSoftware.RuntimeSettings.OpcUaServerRuntimeSettings`; `HmiOpcUaAlarmTypeComposition` | Optional server settings/policy/limits/Validate and alarm types | Separate optional product and HMI owner; no PLC interface API substitution. |

Exact source links and target/version caveats are retained in the expansion plan's I1–I5/D1–D17 source inventory. The machine-readable [candidate profiles](../../contracts/opc-ua-profiles.v1.json) deliberately keep `targetAvailable: null`, `nativeQualified: false` and `formatProfile: null`. No configuration route is enabled by that file. The only named dynamic attribute candidates are the two verified documentation names above; activation, authentication, port/limits and trust names are not guessed.

## Offline contract and limitations

The general [OPC Foundation NodeSet XSD](https://github.com/OPCFoundation/UA-Nodeset/blob/4194ae486d36166d6997de9946d361ec2ce4a4fe/Schema/UANodeSet.xsd) is pinned, licensed and embedded with a runtime hash check. Its declared version is 1.05.04. This validates the XML envelope, not Siemens extensions or native target compatibility. DTDs, entity declarations and external schema resolution are disabled. Input is limited to 16 MiB, depth 64, 20,000 nodes, 100,000 dependencies and 10,000 unresolved rows; reports are capped at 32 MiB. Exceeding a bound fails explicitly rather than returning a falsely complete subset.

The review handles all eight standard NodeSet node classes, explicit namespace tables, aliases, numeric/string/GUID/opaque NodeIds, BrowseNames, normalized datatype identities, array rank/dimensions and references. Duplicate canonical identities, namespace collisions and missing indexes fail. Standard-model and other unresolved references remain explicit diagnostics. General XSD success is never labeled full model validation.

Comparison identifies removed/added nodes and changes in BrowseName, class, datatype, rank and dimensions. Type-definition and method-argument value changes are conservatively breaking. Other attribute, reference, access, value, localized metadata and extension changes require review. Opaque XML content is hashed with expanded names, order and whitespace preserved. Namespace indexes inside opaque values/definitions are not semantically normalized, so a namespace reindex may conservatively require review there. There is no automatic old/new node pairing, complete dependency closure, inherited-access calculation or PLC declaration mapping. Raw input hashes remain in every report; keep the original bytes because reports cannot reconstruct/import the model.

The [consumer manifest schema](../../contracts/opc-ua-consumers.v1.schema.json) requires exact before-model hash and declared node keys. Known direct effects are reported per client; other impacts remain unknown and unknown consumers are always possible. A clean review never certifies external-client compatibility or a physical endpoint identity. Runtime browsing, sessions, subscriptions, calls and values are outside this code.

## Coordinator handoff and remaining packages

1. Done: the library, CLI and tests are in `TiaMcp.slnx`.
2. Done: the Host references the library, and [`OpcUaTools.cs`](../../src/TiaMcp.Host/OpcUaTools.cs) registers the review as `opcua_inspect` and `opcua_compare` (the proposed names `opcua_model_validate`/`opcua_model_compare` were not used); both are in the feature catalog. The review must still be described as partial envelope/identity validation. Refuse arbitrary file paths, verify hashes after capture, and return bounded artifact handles rather than 32 MiB inline responses. Keep original uploads and reports immutable. Shared dispatcher/catalog changes are not performed in this handoff.
3. Complete UA-03 exact native discovery/export using the public provider/category APIs, existing project/PLC identities and current admission owner. Bind native evidence to frozen worker/host source and binary, exact CPU/order number/firmware, project/fixture/model/profile hashes and cleanup receipt.
4. Admit UA-Q00's smallest owned fixture packet: configured secure P1500-40 copy, category-specific export/import acceptance, mapping extension grammar, generation timing, SIMATIC rename, exact policy/auth owner descriptors and independent peer preservation. Missing fixture, privilege, service or license is blocked/unsupported, never passed. No VM rerun is requested; accepted recovery evidence stays within its original scope.
5. Only after the dialect/target results, finish declaration mapping/dependency closure and prepared mutations through the existing checkpoint/fingerprint/compile/readback/reopen/journal recovery pipeline. Security provisioning, certificate lifecycle, native acceptance and runtime handoff remain separate required packages from the plan.

UA-01 is substantially implemented but its target-specific attribute/format/fixture decisions remain open. UA-02 and UA-04 have an offline foundation; they are not complete. UA-03 through UA-09 and every UA-Q native/protocol gate remain unexecuted. No 1.0 support promise or acceptance checkbox is changed by this handoff.
