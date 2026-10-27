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
    [Description("Provision an existing generated scratch CPU configuration credential to its ticket-bound owned local PLCSIM instance. Requires a saved scratch project, matching generated-card evidence, DPAPI credential metadata, isolated Softbus and an explicitly verified PLCSIM connection. Creates a full project recovery archive in owned storage before card staging or startup; does not automatically save unsaved edits. May start the virtual CPU for provisioning and returns it OFF. Optional trustOwnedEmptyCpuCertificate requires explicit user authorization for first-use TLS trust on this freshly created owned empty CPU; its certificate is pinned within the attempt. Default rejects TLS trust. Accepts no raw password, network adapter or target address; protection stays enabled. This does not prove RUN or program execution.")]
    public Task<CallToolResult> SimulationProvisionScratchCredential(string downloadTicket, string credentialMetadataPath, CancellationToken cancellationToken,
        string? projectName = null, string? plcPath = null, string? credentialLineageJournalPath = null, string[]? credentialLineageJournalPaths = null,
        bool trustOwnedEmptyCpuCertificate = false)
        => Call("simulation_provision_scratch_credential", new { downloadTicket, projectName, plcPath, credentialMetadataPath, credentialLineageJournalPath, credentialLineageJournalPaths, trustOwnedEmptyCpuCertificate }, cancellationToken);

    [McpServerTool(Name = "simulation_provision_reconcile", Destructive = false, OpenWorld = false)]
    [Description("Release a failed credential-provisioning admission (outcomeUnknown) only on fresh evidence: in an empty worker-owned TIA instance started with tia_start, the scratch project's fingerprint must equal its retrieved pre-provision archive, the memory card manifest must equal the original, and any discovery observer project must be empty. Otherwise the reservation stays held and the reasons are returned. Never powers, connects, provisions, saves or replays.")]
    public Task<CallToolResult> SimulationProvisionReconcile(string admissionId, CancellationToken cancellationToken)
        => Call("simulation_provision_reconcile", new { admissionId }, cancellationToken);

    [McpServerTool(Name = "simulation_discovery_baseline", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read-only PN/IE / PLCSIM discovery for the worker-owned scratch PLC, before sim_create, while no PLCSIM Advanced instance is registered. Requires Softbus and does not change it. Records either nothing or exactly one all-zero Runtime Manager entry; the next simulation_provision_scratch_credential in this worker, same TIA session, project and PLC, may exclude only that single exact entry, at each of its discovery checks only while the baseline is under five minutes old and the same Runtime Manager process runs. A new baseline call first discards any earlier one. Never accepts any other device. Side effects: initializes the PLCSIM Advanced API, which may start the Runtime Manager and keeps it for the worker's lifetime, and stores that one baseline, which loosens the next provisioning isolation check as described.")]
    public Task<CallToolResult> SimulationDiscoveryBaseline(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null)
        => Call("simulation_discovery_baseline", new { projectName, plcPath }, cancellationToken);

    [McpServerTool(Name = "project_open", Destructive = false, OpenWorld = false)]
    [Description("Open an exact existing .ap21 project on the TIA host in an empty worker-owned instance started with tia_start. Does not upgrade, save or download it. Use a copied/test project for validation edits. If project login is required, prompt for the target's local username and password through TIA authentication; this tool accepts no raw credentials. Follow the server authentication instructions.")]
    public Task<CallToolResult> ProjectOpen(string path, CancellationToken cancellationToken)
        => Call("project_open", new { path }, cancellationToken);

    [McpServerTool(Name = "project_retrieve", Destructive = false, OpenWorld = false)]
    [Description("Retrieve an exact .zap21 archive with a required SHA-256 into a new unique directory in an empty worker-owned TIA instance. Supports explicit checkpoint recovery after a restart. Never overwrites an existing project.")]
    public Task<CallToolResult> ProjectRetrieve(string archivePath, string expectedSha256, CancellationToken cancellationToken)
        => Call("project_retrieve", new { archivePath, expectedSha256, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "recovery") }, cancellationToken);

    [McpServerTool(Name = "software_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect PLC tag tables, tags/constants, PLC data types, software units and their blocks. Bounded offline results report missing or unsupported scope.")]
    public Task<CallToolResult> SoftwareInspect(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null)
        => Call("software_inspect", new { projectName, plcPath, scopePath, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "type_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export and inspect a PLC data type (UDT), preserving native XML and normalized member declarations. Select exact typeName or typePath from software_inspect.")]
    public Task<CallToolResult> TypeRead(CancellationToken cancellationToken, string? typeName = null, string? typePath = null, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null)
        => Call("type_read", new { typeName, typePath, projectName, plcPath, scopePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "tag_table_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export a selected tag table as unchanged native XML including constants and multilingual comments. Select tagTableName or tagTablePath from software_inspect.")]
    public Task<CallToolResult> TagTableRead(CancellationToken cancellationToken, string? tagTableName = null, string? tagTablePath = null, string? projectName = null, string? plcPath = null, string? scopePath = null)
        => Call("tag_table_read", new { tagTableName, tagTablePath, projectName, plcPath, scopePath, artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "block_interface", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect block/DB interface members and declared start values from native XML. These are offline declarations, not runtime values.")]
    public Task<CallToolResult> BlockInterface(CancellationToken cancellationToken, string? blockName = null, string? blockPath = null, string? projectName = null, string? plcPath = null, int maxItems = 500, string? scopePath = null)
        => Call("block_interface", new { blockName, blockPath, projectName, plcPath, scopePath, maxItems = Limit(maxItems), artifactDirectory = ExportDirectory }, cancellationToken);

    [McpServerTool(Name = "hardware_inspect", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect configured hardware attributes, addresses, exposed channels, subnet memberships, native I/O-controller/connector associations and port interconnections with exact hardware owners or explicit unresolved coverage, plus GSD identities. Bounded offline observations; no physical wiring inference, network scan or PLC connection.")]
    public Task<CallToolResult> HardwareInspect(CancellationToken cancellationToken, string? hardwarePath = null, string? projectName = null, int maxItems = 500)
        => Call("hardware_inspect", new { hardwarePath, projectName, maxItems = Limit(maxItems) }, cancellationToken);

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

    [McpServerTool(Name = "call_graph", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Build a bounded offline call graph using native cross-references and optional exported call-site evidence. Unresolved, indirect and protected coverage is explicit. This is static evidence, not a running call stack.")]
    public Task<CallToolResult> CallGraph(CancellationToken cancellationToken, string? projectName = null, string? plcPath = null,
        int maxItems = 500, bool includeXmlEvidence = true, int maxXmlBlocks = 10, string? blockName = null, string? blockPath = null, string? scopePath = null)
        => Call("call_graph", new { projectName, plcPath, blockName, blockPath, scopePath, maxItems = Limit(maxItems), includeXmlEvidence, maxXmlBlocks, artifactDirectory = ExportDirectory }, cancellationToken);

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
    [Description("Prepare an offline engineering change: kind and payload, exact project/PLC targets, preview, artifact hashes and conflict checks. Returns changeId/previewHash for change_apply. Source/XML must explicitly describe intended objects. Does not change the project; writes preview artifacts.")]
    public async Task<CallToolResult> ChangePrepare(string kind, JsonElement payload, CancellationToken cancellationToken, string? projectName = null, string? plcPath = null, string? scopePath = null)
    {
        object request = kind == "cax_import" ? CaxImportPayload(payload) : ObjectArguments(payload);
        // TiaMcp.Graph targets net10 and cannot load in the net48 worker, so GRAPH documents are validated here before native preparation.
        if (kind == "graph_import")
            try { request = new GraphWorkflowStore(options.ArtifactsDirectory, options.LibraryLimits).ImportPayload(payload); }
            catch (BridgeException error) { return Failure(error); }
        return await Call("change_prepare", new { kind, payload = request, projectName, plcPath, scopePath, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "changes") }, cancellationToken);
    }

    [McpServerTool(Name = "change_apply", Destructive = true, OpenWorld = false)]
    [Description("Apply a prepared offline change exactly once using its previewHash and unique idempotencyKey. checkpointSave explicitly saves the current in-memory project so Siemens can archive a recovery checkpoint. Returns compile diagnostics, verification and partial-failure journal; never downloads or changes runtime values.")]
    public Task<CallToolResult> ChangeApply(string changeId, string previewHash, string idempotencyKey, bool checkpointSave, CancellationToken cancellationToken, bool compile = true, bool save = true)
        => Call("change_apply", new { changeId, previewHash, idempotencyKey, checkpointSave, compile, save }, cancellationToken);

    [McpServerTool(Name = "change_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the journal and known outcome of a prepared/applied change. Do this after a partial failure; never automatically retry a write.")]
    public Task<CallToolResult> ChangeStatus(string changeId, CancellationToken cancellationToken)
        => Call("change_status", new { changeId, artifactDirectory = Path.Combine(options.ArtifactsDirectory, "changes") }, cancellationToken);

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
    private static JsonElement ObjectArguments(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw new McpException("An object is required.");
}
