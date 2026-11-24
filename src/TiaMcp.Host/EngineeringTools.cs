using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

public sealed partial class TiaTools
{
    [McpServerTool(Name = "simulation_download", Destructive = true, OpenWorld = false)]
    [Description("Generate a memory-card image into a fresh, verified owned PLCSIM instance using its one-use downloadTicket from sim_create. Validates the local simulator identity and OFF state. No network interface or physical PLC download is exposed.")]
    public Task<CallToolResult> SimulationDownload(string downloadTicket, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null,
        string? credentialMetadataPath = null, string? credentialLineageJournalPath = null, string[]? credentialLineageJournalPaths = null)
        => Call("simulation_download", new { downloadTicket, projectName, plcPath, credentialMetadataPath, credentialLineageJournalPath, credentialLineageJournalPaths }, cancellationToken);

    [McpServerTool(Name = "simulation_provision_scratch_credential", Destructive = true, OpenWorld = false)]
    [Description("Provision a generated scratch CPU configuration credential to its ticket-bound owned PLCSIM. Requires a saved scratch project (project_save first), card/DPAPI evidence, isolated Softbus and a verified PLCSIM link. Creates a recovery archive; saves no edits. May start the CPU; returns it OFF. trustOwnedEmptyCpuCertificate needs explicit user authorization per attempt; otherwise no TLS trust. No raw password, adapter or address; protection stays enabled. Does not prove RUN.")]
    public Task<CallToolResult> SimulationProvisionScratchCredential(string downloadTicket, string credentialMetadataPath, CancellationToken cancellationToken,
        string? projectName = null, string? plcPath = null, string? credentialLineageJournalPath = null, string[]? credentialLineageJournalPaths = null,
        bool trustOwnedEmptyCpuCertificate = false)
        => Call("simulation_provision_scratch_credential", new { downloadTicket, projectName, plcPath, credentialMetadataPath, credentialLineageJournalPath, credentialLineageJournalPaths, trustOwnedEmptyCpuCertificate }, cancellationToken);

    [McpServerTool(Name = "simulation_provision_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Release a failed credential-provisioning admission (outcomeUnknown) only on fresh evidence: in an empty worker-owned TIA instance started with tia_start, the scratch project's fingerprint must equal its retrieved pre-provision archive, the memory card manifest must equal the original, and any discovery observer project must be empty. Otherwise the reservation stays held and the reasons are returned. Never powers, connects, provisions, saves or replays.")]
    public Task<CallToolResult> SimulationProvisionReconcile(string admissionId, CancellationToken cancellationToken)
        => Call("simulation_provision_reconcile", new { admissionId }, cancellationToken);

    [McpServerTool(Name = "simulation_discovery_baseline", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read-only PN/IE / PLCSIM discovery for the worker-owned scratch PLC, before sim_create, with no PLCSIM Advanced instance registered. Requires Softbus, unchanged. Records nothing or one all-zero Runtime Manager entry that the next simulation_provision_scratch_credential may exclude for five minutes; never accepts other devices. Side effects: may start the PLCSIM Runtime Manager for the worker's lifetime and loosens the next provisioning isolation check.")]
    public Task<CallToolResult> SimulationDiscoveryBaseline(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null)
        => Call("simulation_discovery_baseline", new { projectName, plcPath }, cancellationToken);

    [McpServerTool(Name = "plc_online_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Compare the PLC's offline software with its ticket-bound owned PLCSIM Advanced instance (sim_create downloadTicket) in a worker-owned TIA. The instance must be in STOP or RUN and the only registered Softbus simulator. Returns a bounded compare tree and fingerprints (optional expectedFingerprintSha256). Refuses TLS trust and passwords; runtimeCredentialMetadataPath answers the one login. Always goes offline; never powers, writes, downloads or saves; never a physical PLC.")]
    public Task<CallToolResult> PlcOnlineCompare(string downloadTicket, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null,
        int maxItems = 500, string? expectedFingerprintSha256 = null, string? runtimeCredentialMetadataPath = null)
        => Call("plc_online_compare", new { downloadTicket, projectName, plcPath, maxItems = Limit(maxItems), expectedFingerprintSha256, runtimeCredentialMetadataPath,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "online") }, cancellationToken);

    [McpServerTool(Name = "plc_station_upload", Destructive = false, OpenWorld = false)]
    [Description("Upload the station from a ticket-bound owned PLCSIM Advanced instance into a new worker-owned project, in an empty TIA started with tia_start (guards as plc_online_compare). Saves the new project even when partial and returns its path. Never opens or changes an existing project, never powers or writes the instance. Passwords and TLS trust refused by default; trustOwnedSimulationCertificate needs explicit user authorization. runtimeCredentialMetadataPath answers the login.")]
    public Task<CallToolResult> PlcStationUpload(string downloadTicket, CancellationToken cancellationToken, string? runtimeCredentialMetadataPath = null,
        bool trustOwnedSimulationCertificate = false)
        => Call("plc_station_upload", new { downloadTicket, runtimeCredentialMetadataPath, trustOwnedSimulationCertificate, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "uploads") }, cancellationToken);

    [McpServerTool(Name = "project_open_copy", Destructive = false, OpenWorld = false)]
    [Description("Swap the one open project of a worker-owned instance for a read-write copy of its saved state under the host's copies artifacts, and leave the copy open. A read-write project is archived, closed and retrieved; a read-only one (which TIA cannot archive) is closed and its saved folder copied. The original files are never written. Use it after opening a real project with readOnly:true when a tool needs a writable project (block exports in project_snapshot_capture, which does this itself with fromTemporaryCopy). Unsaved edits are refused.")]
    public Task<CallToolResult> ProjectOpenCopy(CancellationToken cancellationToken, string? projectName = null)
        => Call("project_open_copy", new { projectName, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "copies") }, cancellationToken);

    [McpServerTool(Name = "project_close", Destructive = true, OpenWorld = false)]
    [Description("Close one open project in a worker-owned instance (tia_start) without saving, so another project can be opened in the same Portal. Refused in a session attached to a user-owned TIA. Unsaved edits are refused (UnsavedProject) unless discardUnsaved is true; they are then lost. Never saves.")]
    public Task<CallToolResult> ProjectClose(CancellationToken cancellationToken, string? projectName = null, bool discardUnsaved = false)
        => Call("project_close", new { projectName, discardUnsaved }, cancellationToken);

    [McpServerTool(Name = "project_save", Destructive = false, OpenWorld = false)]
    [Description("Save one open project in a worker-owned instance (tia_start), e.g. after simulation_download, before simulation_provision_scratch_credential. Refused for a user-owned TIA (OwnedSessionRequired), read-only project (ReadOnlyProject), unresolved change (RecoveryRequired) or held mutation admission (ResourceBusy; reconcile first). No recovery archive: checkpointed edits go through change_prepare/change_apply. Without unsaved edits nothing is written (saved:false).")]
    public Task<CallToolResult> ProjectSave(CancellationToken cancellationToken, string? projectName = null)
        => Call("project_save", new { projectName }, cancellationToken);

    [McpServerTool(Name = "project_open", Destructive = false, OpenWorld = false)]
    [Description("Open an exact existing .ap21 project in an empty worker-owned instance: tia_start first, project_close any open project. Does not upgrade, save or download; edit only copies or test projects. readOnly=true refuses exports and mutations. ProjectLocked: open readOnly, use project_open_copy or close it there. UMAC projects need umacUserName, umacUserType (project or global) and a stored umacCredential; no raw passwords, login never retried. Not qualified when protected.")]
    public Task<CallToolResult> ProjectOpen(string path, CancellationToken cancellationToken, bool readOnly = false,
        [Description("UMAC user name for a protected project; requires umacCredential.")] string? umacUserName = null,
        [Description("project (default) or global.")] string umacUserType = "project",
        [Description("protected-credential-reference.v1 object holding that user's password.")] JsonElement? umacCredential = null,
        [Description("Credential vault: default or a 32-character lowercase hex name.")] string? vaultName = null)
    {
        bool login = umacCredential is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };
        if (login != (umacUserName is not null)) throw new McpException("umacUserName and umacCredential go together.");
        if (!login) return Call("project_open", new { path, readOnly }, cancellationToken);
        if (options.TiaVersion == 19) throw new McpException("UMAC project login is V21-only; the V19 adapter opens unprotected projects only.");
        if (umacUserType is not ("project" or "global")) throw new McpException("umacUserType must be project or global.");
        return Call("project_open", new { path, readOnly, umac = new { userName = umacUserName, userType = umacUserType, vaultName, reference = umacCredential } }, cancellationToken);
    }

    [McpServerTool(Name = "project_archive", Destructive = false, OpenWorld = false)]
    [Description("Archive the saved state of an open project as a new .zap21 file under the host's archives artifacts (Project.Archive). Works in a worker-owned or attached session. The project is never saved, closed or changed; unsaved edits are refused (UnsavedProject). mode: compressed (default) or discardRestorableDataAndCompressed (smaller; TIA regenerates the discarded data). Returns the archive path, size and SHA-256; restore it with project_retrieve.")]
    public Task<CallToolResult> ProjectArchive(CancellationToken cancellationToken, string? projectName = null, string mode = "compressed")
        => Call("project_archive", new { projectName, mode, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "archives") }, cancellationToken);

    [McpServerTool(Name = "project_copy", Destructive = false, OpenWorld = false)]
    [Description("Copy the saved state of an open project into a new unique directory under the host's copies artifacts, in a worker-owned instance started with tia_start: archive, retrieve as a secondary project, verify name and location, close the copy. The source stays open and is never saved or changed; unsaved source edits are refused. Returns the copy's path and identity plus the archive hash; open it with project_open in a fresh instance.")]
    public Task<CallToolResult> ProjectCopy(CancellationToken cancellationToken, string? projectName = null)
        => Call("project_copy", new { projectName, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "copies") }, cancellationToken);

    [McpServerTool(Name = "plc_compare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Offline compare of one PLC's software with exactly one target: another PLC in the same project (targetPlcPath) or a library (libraryId from library_list/library_open). Returns the bounded native compare tree (left/right names, state, detail, children) with explicit truncation. No online compare; states are not runtime equivalence.")]
    public Task<CallToolResult> PlcCompare(CancellationToken cancellationToken, string? plcPath = null, string? targetPlcPath = null, string? libraryId = null,
        string? projectName = null, int maxItems = 500)
        => Call("plc_compare", new { projectName, plcPath, targetPlcPath, libraryId, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "project_upgrade", Destructive = false, OpenWorld = false)]
    [Description("Upgrade an exact V14-V20 project (.ap14-.ap20, .ap15_1) or archive (.zap14-.zap20) with its required SHA-256 to V21, in an empty worker-owned TIA (tia_start). The source is never opened or written: only a copy under the host's upgrades artifacts is upgraded and left open; sourceUnchanged is verified. V13 and older or V21+ refused (ProjectVersionUnsupported). Returns conversion logs. Nothing compiled or saved beyond TIA's upgrade. UMAC projects unsupported.")]
    public Task<CallToolResult> ProjectUpgrade(string path, string expectedSha256, CancellationToken cancellationToken)
        => Call("project_upgrade", new { path, expectedSha256, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "upgrades") }, cancellationToken);

    [McpServerTool(Name = "project_retrieve", Destructive = false, OpenWorld = false)]
    [Description("Retrieve an exact .zap21 archive with a required SHA-256 into a new unique directory in an empty worker-owned TIA instance. Supports explicit checkpoint recovery after a restart. Never overwrites an existing project.")]
    public Task<CallToolResult> ProjectRetrieve(string archivePath, string expectedSha256, CancellationToken cancellationToken)
        => Call("project_retrieve", new { archivePath, expectedSha256, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "recovery") }, cancellationToken);

    [McpServerTool(Name = "software_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect PLC tag tables, tags/constants, PLC data types, software units and their blocks, bounded, reporting missing or unsupported scope. Filters: groupPath (group names from the top, or a path from groups[]) and namePrefix (case-insensitive); then only matches count against maxItems. include (comma-separated: blocks, tags, constants, types, technology) reads only those sections; compact=true returns tags as name, dataType, logicalAddress.")]
    public Task<CallToolResult> SoftwareInspect(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null, string? groupPath = null, string? namePrefix = null,
        string? include = null, bool compact = false)
        => Call("software_inspect", new { projectName, plcPath, scopePath, maxItems = Limit(maxItems), groupPath, namePrefix, include, compact = compact ? true : (bool?)null }, cancellationToken);

    [McpServerTool(Name = "type_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export and inspect a PLC data type (UDT), preserving native XML and normalized member declarations. Select exact typeName or typePath from software_inspect. Optional format: simaticSd exports native SIMATIC SD documents; generatedSource generates its .udt external source file and needs a writable offline project, because any external source the generation leaves is deleted; both return SHA-256 hashed artifacts instead of parsed members.")]
    public Task<CallToolResult> TypeRead(CancellationToken cancellationToken, string? typeName = null, string? typePath = null, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null, string? format = null)
        => Call("type_read", new { typeName, typePath, projectName, plcPath, scopePath, format, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "plc_document_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List native PLC documents (PlcDocument below PLC data type groups) of a PLC or exact software scope, bounded by maxItems with explicit truncation. exportPaths exports those exact document paths as SIMATIC SD files with SHA-256 into the artifact directory. No project changes.")]
    public Task<CallToolResult> PlcDocumentInventory(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? scopePath = null, int maxItems = 500, string[]? exportPaths = null)
        => Call("plc_document_inventory", new { projectName, plcPath, scopePath, maxItems = Limit(maxItems), exportPaths, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "plc_loadable_generate", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Generate a native loadable file for exact non-unit blockPaths or exact software unitPaths with an explicit target (plc or plcSim) into the artifact directory with SHA-256. Siemens documents loadables only for CPU 1518 and its subtypes from firmware V2.8; other CPUs refuse with UnsupportedCapability, and target_capabilities reports the plc_loadable_generate service per PLC. Refuses inconsistent objects instead of compiling. Never downloads, connects to a PLC/PLCSIM or changes runtime state.")]
    public Task<CallToolResult> PlcLoadableGenerate(string target, CancellationToken cancellationToken, string[]? blockPaths = null, string[]? unitPaths = null, string? projectName = null, string? plcPath = null)
        => Call("plc_loadable_generate", new { target, blockPaths, unitPaths, projectName, plcPath, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "tag_table_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a selected tag table as unchanged native XML including constants and multilingual comments. Select tagTableName or tagTablePath from software_inspect.")]
    public Task<CallToolResult> TagTableRead(CancellationToken cancellationToken, string? tagTableName = null, string? tagTablePath = null, string? projectName = null, string? plcPath = null, string? scopePath = null)
        => Call("tag_table_read", new { tagTableName, tagTablePath, projectName, plcPath, scopePath, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "block_interface", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect block/DB interface members and declared start values from native XML. These are offline declarations, not runtime values.")]
    public Task<CallToolResult> BlockInterface(CancellationToken cancellationToken, string? blockName = null, string? blockPath = null, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null)
        => Call("block_interface", new { blockName, blockPath, projectName, plcPath, scopePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "hardware_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect configured hardware attributes, addresses, exposed channels, subnet memberships, native I/O-controller/connector associations, port interconnections and MRP instances (domain, role, ring ports) with exact hardware owners or explicit unresolved coverage, plus GSD identities. Bounded offline observations; no physical wiring inference, network scan or PLC connection. compact=true returns only identity, I/O address ranges and channels per item, without the attribute, network and GSD reads that use up maxItems on a large station.")]
    public Task<CallToolResult> HardwareInspect(CancellationToken cancellationToken, string? hardwarePath = null, string? projectName = null, int maxItems = 500, bool compact = false)
        => Call("hardware_inspect", new { hardwarePath, projectName, maxItems = Limit(maxItems), compact = compact ? true : (bool?)null }, cancellationToken);

    [McpServerTool(Name = "connection_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List configured communication connections (S7, TCP, UDP, ISO, ISO-on-TCP, FDL, PtP, HMI) from native CommunicationManagement of all hardware or one exact hardwarePath. Each row has type, name, ID, typed properties and local/partner target and interface paths resolved by native object equality. reviewSnapshot is a connection_review before/after snapshot; maxItems bounds rows and truncation is explicit. Offline configuration only; no consumers, resource checks or communication.")]
    public Task<CallToolResult> ConnectionInspect(CancellationToken cancellationToken, string? hardwarePath = null, string? projectName = null, int maxItems = 500)
        => Call("connection_inspect", new { hardwarePath, projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "hardware_gsd_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export the GSD file of an I-device through the native GsdExportProvider of the exact hardwarePath (hardware_inspect reports gsdExportAvailable). Writes the files Siemens produces into a fresh artifact directory and returns each with its SHA-256. Does not change the project or validate the GSD content.")]
    public Task<CallToolResult> HardwareGsdExport(string hardwarePath, string designation, CancellationToken cancellationToken, string? description = null, string? projectName = null)
        => Call("hardware_gsd_export", new { hardwarePath, designation, description, projectName, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "technology_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect offline technology objects and exposed parameters. Select technologyName or technologyPath to inspect a specific object; absent selectors enumerate the PLC's objects.")]
    public Task<CallToolResult> TechnologyRead(CancellationToken cancellationToken, string? technologyName = null, string? technologyPath = null, string? projectName = null, string? plcPath = null, int maxItems = 500, string? parameterPrefix = null, bool exportXml = false)
        => Call("technology_read", new { technologyName, technologyPath, projectName, plcPath, parameterPrefix, exportXml, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "plc_alarm_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read offline PLC alarm configuration for one PLC and its software units: user/system alarm text lists (name, ID, range, comment per culture) and the native exports of user text-list entries, alarm instance texts, global ProDiag supervisions, project alarm classes and ProDiag settings. Exports are retained with SHA-256; worksheet rows are bounded by maxItems. A missing service, option or empty list is reported per part with the native message. scopePath narrows to one software unit.")]
    public Task<CallToolResult> PlcAlarmInspect(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? scopePath = null, int maxItems = 500)
        => Call("plc_alarm_inspect", new { projectName, plcPath, scopePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "drive_observe", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read offline SINAMICS drive objects through Startdrive Openness for one exact drive hardwarePath or all drives, as drive-profile-review v2 snapshots usable as before/after in drive_profile_review. maxItems bounds all rows; parameters page with parameterOffset/nextParameterOffset; parameterNumbers filters exact numbers. Motor/encoder entries only for listed motorDataSets/encoderNumbers. Without Startdrive: UnsupportedCapability. No online access, upload, download or write.")]
    public Task<CallToolResult> DriveObserve(CancellationToken cancellationToken, string? hardwarePath = null, string? projectName = null, int maxItems = 500,
        int parameterOffset = 0, int[]? parameterNumbers = null, int[]? motorDataSets = null, int[]? encoderNumbers = null)
        => Call("drive_observe", new { hardwarePath, projectName, parameterOffset, parameterNumbers, motorDataSets, encoderNumbers, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "call_graph", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Build a bounded offline call graph using native cross-references and optional exported call-site evidence. Unresolved, indirect and protected coverage is explicit. This is static evidence, not a running call stack.")]
    public Task<CallToolResult> CallGraph(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null,
        int maxItems = 500, bool includeXmlEvidence = true, int maxXmlBlocks = 10, string? blockName = null, string? blockPath = null, string? scopePath = null)
        => Call("call_graph", new { projectName, plcPath, blockName, blockPath, scopePath, maxItems = Limit(maxItems), includeXmlEvidence, maxXmlBlocks, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "instance_lookup", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Find where one exact FB or FC is stored and called in one PLC, from native cross-references and exported SimaticML: instance DBs, multi-instances and arrays, call sites with actual operands, and instance DB references. STL/GRAPH are an unqualified scan; unsupported languages listed. maxItems bounds rows, maxXmlBlocks exports, maxDepth nesting. A row's diagnosticInstanceSelector can be passed to diagnostic_plan as instance. Static evidence only; no runtime call context.")]
    public Task<CallToolResult> InstanceLookup(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? blockPath = null, string? blockName = null,
        string? scopePath = null, int maxItems = 500, int maxDepth = 8, int maxXmlBlocks = 20)
        => Call("instance_lookup", new { projectName, plcPath, blockPath, blockName, scopePath, maxItems = Limit(maxItems), maxDepth, maxXmlBlocks, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "symbol_trace", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Trace an exact offline native symbol/name/address/path to references, readers/writers and configured I/O ranges. Reports unresolved aliases and incomplete propagation.")]
    public Task<CallToolResult> SymbolTrace(string symbol, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, int maxItems = 500,
        string? blockName = null, string? blockPath = null, bool includeXmlEvidence = false, int maxXmlBlocks = 10, string? scopePath = null)
        => Call("symbol_trace", new { symbol, projectName, plcPath, blockName, blockPath, scopePath, maxItems = Limit(maxItems), includeXmlEvidence, maxXmlBlocks, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "change_impact", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect static dependents of an exact symbol or qualified block path before editing. Bounded native evidence; indirect/runtime dependencies cannot be proven complete.")]
    public Task<CallToolResult> ChangeImpact(string symbol, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, int maxItems = 500,
        string? blockName = null, string? blockPath = null, int maxDepth = 32, bool includeXmlEvidence = false, int maxXmlBlocks = 10, string? scopePath = null)
        => Call("change_impact", new { symbol, projectName, plcPath, blockName, blockPath, scopePath, maxDepth, maxItems = Limit(maxItems), includeXmlEvidence, maxXmlBlocks, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "engineering_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read engineering target metadata and supported change operations. Pass all fields in selectors. scope=graphical with an exact blockPath returns a versioned read-only LAD/FBD graph, opaque coverage and native XML artifact; optional networkId, maxItems, maxGraphBytes and includeNativeXml bound the result. No mutation occurs.")]
    public Task<CallToolResult> EngineeringRead(JsonElement selectors, CancellationToken cancellationToken)
    {
        var arguments = System.Text.Json.Nodes.JsonNode.Parse(ObjectArguments(selectors).GetRawText())!.AsObject();
        arguments["artifactDirectory"] = ExportDirectory;
        return Call("engineering_read", arguments, cancellationToken);
    }

    [McpServerTool(Name = "fixture_sources", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Return built-in source/XML examples for dedicated isolated language fixtures. Does not import them or alter a project.")]
    public Task<CallToolResult> FixtureSources(CancellationToken cancellationToken, string language = "SCL", string? blockName = null)
        => Call("fixture_sources", new { language, blockName, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "fixtures") }, cancellationToken);

    [McpServerTool(Name = "change_prepare", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Prepare an offline engineering change (kind, payload, exact project/PLC targets); writes preview artifacts only, project unchanged. Schema: tia://change-payloads/{kind}. Returns changeId and previewHash for change_apply. Optional intent is recorded, never evaluated; machine safety not verified. RecoveryRequired while another change is unresolved: inspect change_status. Batch kinds take {items:[...]}; one refused item refuses all. Large inputs via sourceRef/xmlRef (2 MiB).")]
    public async Task<CallToolResult> ChangePrepare(string kind, JsonElement payload, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? scopePath = null, JsonElement? intent = null, bool includeDetails = false,
        [Description(DetailDescription)] string? detail = null)
        => Compacted(await ChangePrepareCore(kind, payload, cancellationToken, projectName, plcPath, scopePath, intent), Level(detail, includeDetails));

    private async Task<CallToolResult> ChangePrepareCore(string kind, JsonElement payload, CancellationToken cancellationToken, string? projectName, string? plcPath, string? scopePath, JsonElement? intent)
    {
        object request;
        // #220: confirmed intent is passed to the worker, which validates it and binds it to the preview hash.
        object? intentArgument = intent is { ValueKind: JsonValueKind.Object } supplied ? ObjectArguments(supplied) : null;
        // The AML is resolved at the Host trust boundary; its refusals are typed like every other preparation refusal.
        try { request = kind == "cax_import" ? CaxImportPayload(payload) : ObjectArguments(payload); }
        catch (BridgeException error) when (kind == "cax_import") { return Failure(error); }
        // Large source/XML by reference: the Host reads the committed upload or artifact file and inlines its exact text.
        // Later payload handlers (the Classic screen artifact route) read the resolved payload.
        if (kind is "source_upsert" or "block_import_xml" or "hmi_classic_import" or "hmi_classic_screen_edit")
            try { request = payload = TiaMcp.Core.Engineering.ChangeInputReference.Resolve(kind, payload, options.ArtifactsDirectory, new ArtifactUploadStore(options.ArtifactsDirectory).ResolveCommitted); }
            catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException)
            { return Failure(error as BridgeException ?? new BridgeException("ArtifactUnavailable", error.Message)); }
        if (kind == "hmi_classic_screen_edit")
            try { request = ClassicScreenArtifactPayload(payload); }
            catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            { return Failure(error as BridgeException ?? new BridgeException("HmiCandidateUnavailable", error.Message)); }
        // TiaMcp.Graph targets net10 and cannot load in the net48 worker, so GRAPH documents are validated here before native preparation.
        if (kind == "graph_import")
            try { request = new GraphWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits).ImportPayload(payload); }
            catch (BridgeException error) { return Failure(error); }
        // The worker cannot read Host review records, so the reviewed DB identity and snapshot hash are bound here.
        if (kind == "db_snapshot_load_actual")
            try { request = new DbSnapshotWorkflowStore(options.ArtifactsDirectory).LoadPayload(payload); }
            catch (Exception error) when (error is BridgeException or IOException or JsonException or InvalidOperationException)
            { return Failure(error as BridgeException ?? new BridgeException("InvalidDbSnapshot", error.Message)); }
        // The worker cannot read Host VCI records, so the reviewed three-way row and its exact package bytes are bound here.
        if (kind == "vci_conflict_resolve")
            try
            {
                if (options.GitExecutable is null) throw new BridgeException("GitNotConfigured", "Start the Host with --git-executable to read the reviewed VCI comparison.");
                request = new TiaMcp.Core.VersionControl.VciWorkspaceStore(options.ArtifactsDirectory, options.GitExecutable, options.LibraryLimits).ConflictResolutionPayload(payload);
            }
            catch (Exception error) when (error is BridgeException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
            { return Failure(error as BridgeException ?? new BridgeException("VciWorkflowFailed", "The reviewed VCI comparison could not be read.")); }
        // The worker cannot read review records; the Host reopens the text-list record and hands over the exact candidate pair.
        if (kind == "hmi_text_list_import")
            try { request = new HmiComponentWorkflowStore(options.ArtifactsDirectory).TextListImportPayload(payload); }
            catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { return Failure(error as BridgeException ?? new BridgeException("HmiTextListCandidateUnavailable", error.Message)); }
        // The worker cannot read the upload store; only exact public DER of the committed upload is handed over.
        if (kind == "certificate_import")
            try { request = TiaMcp.Core.Security.PublicCertificateReview.ImportPayload(payload, new ArtifactUploadStore(options.ArtifactsDirectory).ResolveCommitted); }
            catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException)
            { return Failure(error as BridgeException ?? new BridgeException("InvalidCertificateImport", "The certificate upload could not be read.")); }
        // safetyAcknowledged is a client-supplied flag, so it gates nothing: only the launch option admits a safety write
        // (and the discovery build, which is itself a launch option, for safety_fparameter_set).
        if (IsSafetyWrite(kind, payload) && !options.EnableSafetyChanges && !(kind == "safety_fparameter_set" && options.SafetyFParameterDiscovery))
            return Failure(new BridgeException("SafetyChangesDisabled", kind + " writes the safety program and is refused by this Host: safety changes are off unless the Host is launched with --enable-safety-changes. Nothing was prepared or changed."));
        // Only the launch option widens safety_fparameter_set beyond the qualified allowlist; no client argument can.
        if (kind == "safety_fparameter_set" && options.SafetyFParameterDiscovery)
            return await Call("change_prepare", new { kind, payload = request, projectName, plcPath, scopePath, intent = intentArgument, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "changes"), fParameterDiscovery = true, pruneStalePreparations = true }, cancellationToken);
        return await Call("change_prepare", new { kind, payload = request, projectName, plcPath, scopePath, intent = intentArgument, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "changes"), pruneStalePreparations = true }, cancellationToken);
    }

    private static bool IsSafetyWrite(string kind, JsonElement payload) => kind is "safety_import_xml" or "safety_fparameter_set" or "safety_drive_tags"
        // The one generic safety edit: a tag_set that renames a safety tag (it is asked for with safetyAcknowledged).
        || kind == "tag_set" && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("safetyAcknowledged", out _);

    [McpServerTool(Name = "change_apply", Destructive = true, OpenWorld = false)]
    [Description("Apply a prepared offline change exactly once with its previewHash and a unique idempotencyKey. checkpointSave explicitly saves current edits before the recovery archive. A failed hmi_classic_screen_edit attempts one verified in-place restore (rolled_back, not_applied or recovery_required). Never retries the candidate, downloads or changes runtime values. After a lost response inspect change_status. Returns a summary unless includeDetails=true.")]
    public async Task<CallToolResult> ChangeApply(string changeId, string previewHash, string idempotencyKey, bool checkpointSave, CancellationToken cancellationToken, bool compile = true, bool save = true, bool includeDetails = false,
        [Description(DetailDescription)] string? detail = null)
        => Compacted(await Call("change_apply", new { changeId, previewHash, idempotencyKey, checkpointSave, compile, save }, cancellationToken), Level(detail, includeDetails));

    private const string DetailDescription = "summary (default), standard (the full view with HMI preservation artifact maps reduced to a count and hash) or full (everything, as includeDetails=true).";

    // A bad level is a client error before any native work.
    private static string Level(string? detail, bool includeDetails)
    {
        try { return TiaMcp.Core.Engineering.ChangeResultCompaction.DetailLevel(detail, includeDetails); }
        catch (ArgumentException error) { throw new McpException(error.Message); }
    }

    // Summary by default: the full view echoes whole sources/XML and every compiler message. A failure's change view is summarized too.
    private static CallToolResult Compacted(CallToolResult result, string level)
    {
        if (level == "full" || result.StructuredContent is not JsonElement content || content.ValueKind != JsonValueKind.Object) return result;
        var node = System.Text.Json.Nodes.JsonNode.Parse(content.GetRawText())!.AsObject();
        if (result.IsError == true)
        {
            if (node["error"]?["details"] is not System.Text.Json.Nodes.JsonObject details) return result;
            node["error"]!["details"] = TiaMcp.Core.Engineering.ChangeResultCompaction.AtLevel(details.DeepClone(), level);
        }
        else node = TiaMcp.Core.Engineering.ChangeResultCompaction.AtLevel(node, level)!.AsObject();
        return Result(node.ToJsonString(BridgeProtocol.Json), result.IsError == true);
    }

    [McpServerTool(Name = "change_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the journal and known outcome of a prepared/applied change. Do this after a partial failure; never automatically retry a write. Returns the same summary as change_prepare/change_apply; includeDetails=true (or detail=full) returns the full view with the complete preview, payload, compiler messages and readback evidence; detail=standard returns it with HMI preservation artifact maps reduced to a count and hash.")]
    public async Task<CallToolResult> ChangeStatus(string changeId, CancellationToken cancellationToken, bool includeDetails = false, bool progressOnly = false,
        [Description(DetailDescription)] string? detail = null)
    {
        if (progressOnly) return ChangeProgress(changeId);
        string level = Level(detail, includeDetails);
        return Compacted(await Call("change_status", new { changeId, includeDetails = level != "summary", artifactDirectory = Path.Combine(options.ArtifactsDirectory, "changes") }, cancellationToken), level);
    }

    // A poll must not re-parse a journal of up to 64 MiB just for its state: the result is kept per journal file and reused while its
    // length and write time are unchanged (the worker replaces the journal atomically at each stage). The worker could drop this read
    // by writing the state into progress.json on every transition; it does so only for hmi_classic_screen_edit today.
    // Like git's racy-clean rule, a result is reused only when it was read well after the file's write time: two writes of equal
    // length within one timestamp tick (not_applied, then rolled_back) would otherwise return the first state forever.
    private static readonly Dictionary<string, (long Length, long Ticks, long ReadTicks, string? State, bool Finished)> JournalStates = new(StringComparer.Ordinal);
    private static readonly long JournalRacyTicks = TimeSpan.FromSeconds(2).Ticks;

    private static (string? State, bool Finished) JournalState(string journal, string changeId)
    {
        var info = new FileInfo(journal);
        if (!info.Exists || info.Length > 64 * 1024 * 1024) return (null, false);
        lock (JournalStates)
        {
            if (JournalStates.TryGetValue(journal, out var cached) && cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks
                && cached.ReadTicks - cached.Ticks >= JournalRacyTicks)
                return (cached.State, cached.Finished);
            long readTicks = DateTime.UtcNow.Ticks;
            var recorded = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!.AsObject();
            if (recorded["changeId"]?.GetValue<string>() != changeId) throw new IOException("Journal belongs to a different change.");
            var state = recorded["state"]?.GetValue<string>();
            string? terminal = state is "applied" or "rolled_back" or "not_applied" or "recovery_required" ? state : null;
            bool finished = terminal is not null && (terminal != "recovery_required" || (recorded["stages"] is System.Text.Json.Nodes.JsonArray stages
                && stages.Any(s => s?.GetValue<string>() == "change_finished_with_failure")));
            if (JournalStates.Count >= 256) JournalStates.Clear();
            JournalStates[journal] = (info.Length, info.LastWriteTimeUtc.Ticks, readTicks, terminal, finished);
            return (terminal, finished);
        }
    }

    private CallToolResult ChangeProgress(string changeId)
    {
        try
        {
            if (!Guid.TryParseExact(changeId, "N", out _)) throw new ArgumentException("progressOnly requires a 32-character changeId.");
            string path = Path.Combine(options.ArtifactsDirectory, "changes", "change-" + changeId, "progress.json");
            if (!File.Exists(path)) return Result(JsonSerializer.Serialize(new { changeId, progressAvailable = false, outcomeVerified = false, automaticRetryAllowed = false }), false);
            if (new FileInfo(path).Length > 1024 * 1024) throw new IOException("Progress receipt exceeds 1 MiB.");
            var data = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (data["changeId"]?.GetValue<string>() != changeId) throw new IOException("Progress receipt belongs to a different change.");
            if (JournalState(Path.Combine(Path.GetDirectoryName(path)!, "journal.json"), changeId) is (string state, bool finished))
            {
                data["state"] = state;
                data["stateSource"] = "journal";
                data["operationFinished"] = finished;
            }
            data["progressAvailable"] = true; data["outcomeVerified"] = false; data["automaticRetryAllowed"] = false;
            // #219: equal fingerprints mean nothing new to report; elapsed time of the running phase is recomputed per poll.
            var phases = (data["phases"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
            data["phaseFingerprint"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                (data["state"]?.ToString() ?? "") + "|" + string.Join(",", phases.Select(p => p["phase"] + ":" + p["state"]))))).ToLowerInvariant()[..16];
            var running = phases.LastOrDefault(p => p["state"]?.ToString() == "running");
            data["currentPhase"] = running?["phase"]?.ToString();
            if (running?["startedAtUtc"]?.ToString() is string started && DateTimeOffset.TryParse(started, out var since))
                data["currentPhaseElapsedMilliseconds"] = (long)(DateTimeOffset.UtcNow - since).TotalMilliseconds;
            data["note"] = "Progress is diagnostic only. Use normal change_status for the durable outcome; never replay a pending write.";
            return Result(data.ToJsonString(), false);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Failure(new BridgeException("ChangeProgressUnavailable", error.Message)); }
    }

    [McpServerTool(Name = "change_restore", Destructive = true, OpenWorld = false)]
    [Description("Recover a change checkpoint into a new project directory in a worker-owned TIA instance. Preserves the current state first; never overwrites the original archive or closes a user-owned TIA instance.")]
    public Task<CallToolResult> ChangeRestore(string changeId, string previewHash, CancellationToken cancellationToken)
        => Call("change_restore", new { changeId, previewHash, artifactDirectory = Path.Combine(options.ArtifactsDirectory,
            changeId.StartsWith("lib-", StringComparison.Ordinal) ? "changes" : "recovery") }, cancellationToken);

    private string ExportDirectory => Path.Combine(options.ArtifactsDirectory, "exports");

    [McpServerTool(Name = "safety_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect exposed offline safety administration, runtime groups and signatures. Optional block selector requests a per-block signature. Does not unlock, accept or change a safety program.")]
    public Task<CallToolResult> SafetyInspect(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? blockName = null, string? blockPath = null, int maxItems = 500, string? scopePath = null)
        => Call("safety_inspect", new { projectName, plcPath, blockName, blockPath, scopePath, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "access_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read offline project UMAC users, UMC users/groups and roles with assigned engineering/device function rights, plus each CPU's access level, access-control/UMC settings, confidential-configuration state and legacy web users with permissions. Never reads passwords, hashes, secrets or private keys. Each observation's identity and snapshot can be passed to access_review. Optional exact hardwarePath narrows CPU rows; bounded by maxItems.")]
    public Task<CallToolResult> AccessInspect(CancellationToken cancellationToken, string? projectName = null, string? hardwarePath = null, int maxItems = 500)
        => Call("access_inspect", new { projectName, hardwarePath, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "certificate_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read offline CPU local certificate stores as public certificates (subject, issuer, validity, SHA-1/SHA-256 fingerprints, public DER), dynamic certificate-management services, certificate/syslog-named CPU attributes and project syslog servers. Private keys are never exported. Each certificate observation can be passed to public_certificate_review. Bounded by maxItems.")]
    public Task<CallToolResult> CertificateInspect(CancellationToken cancellationToken, string? projectName = null, string? hardwarePath = null, int maxItems = 500)
        => Call("certificate_inspect", new { projectName, hardwarePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "web_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read offline CPU web server configuration: system web pages flag, default/user web application configurations and web-named CPU attributes. Each CPU observation can be passed to web_application_review; archives are not read. Never generates blocks or deploys. Bounded by maxItems.")]
    public Task<CallToolResult> WebInspect(CancellationToken cancellationToken, string? projectName = null, string? hardwarePath = null, int maxItems = 500)
        => Call("web_inspect", new { projectName, hardwarePath, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "vci_workspace_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the open project's native VCI workspace groups and workspaces: root path, language, library policy and mapped objects with native compare/child mapping state, supported file formats and contentSha256 (SHA-256 of the object's SimaticML export without document info; the native-side hash vci_conflict_resolve rechecks). Bounded by maxItems with explicit truncation. No workspace creation, mapping edit, workspace export or synchronization.")]
    public Task<CallToolResult> VciWorkspaceInspect(CancellationToken cancellationToken, string? projectName = null, int maxItems = 500)
        => Call("vci_workspace_inspect", new { projectName, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "safety_validation_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect one safety PLC like safety_inspect, plus Failsafe_* F-parameters of the CPU and F-I/O modules and Safety Validation Assistant activation tests, groups, safety functions, conditions and recorded test states. exportArtifacts writes activation-test SimaticML/XLSX reports and the safety printout PDF with SHA-256. The result is accepted by safety_evidence_review. Does not log in, execute activation tests, reset results, change F-parameters or approve the safety program.")]
    public Task<CallToolResult> SafetyValidationInspect(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? blockName = null, string? blockPath = null, int maxItems = 2000, string? scopePath = null, bool exportArtifacts = false)
        => Call("safety_validation_inspect", new { projectName, plcPath, blockName, blockPath, scopePath, maxItems = Limit(maxItems), exportArtifacts, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "safety_printout", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Print the native safety printout of one safety PLC to a file in the export directory with SHA-256. printer is MicrosoftPrintToPdf or MicrosoftXpsDocumentWriter (Windows file printers only; refuses when that driver is not installed and never prints to a physical printer), documentLayout a TIA layout such as DocuInfo_ISO_A4_Portrait or DocuInfo_Simple_A4_Portrait_Small_Footer, option All or Compact. activationTestReports also writes each Safety Validation Assistant activation test's XLSX report and SimaticML export. Does not log in, change, compile, validate or approve the safety program.")]
    public Task<CallToolResult> SafetyPrintout(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string printer = "MicrosoftPrintToPdf", string documentLayout = "DocuInfo_ISO_A4_Portrait", string option = "All", bool activationTestReports = false, int maxItems = 2000)
        => Call("safety_printout", new { projectName, plcPath, printer, documentLayout, option, activationTestReports, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);
    private static JsonElement ObjectArguments(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw new McpException("An object is required.");
}
