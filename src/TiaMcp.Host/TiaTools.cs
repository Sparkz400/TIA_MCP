using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcp.Core;

namespace TiaMcp.Host;

[McpServerToolType]
public sealed partial class TiaTools(IBridgeClient bridge, HostOptions options)
{
    [McpServerTool(Name = "tia_discover", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List running TIA Portal V21 instances. Does not start TIA or connect to a PLC.")]
    public Task<CallToolResult> Discover(CancellationToken cancellationToken) => Call("discover", null, cancellationToken);

    [McpServerTool(Name = "tia_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report V21 bridge installation, implemented operations and capability limits. Installation evidence does not verify licenses or project-specific support.")]
    public Task<CallToolResult> Capabilities(CancellationToken cancellationToken) => Call("bridge_info", null, cancellationToken);

    [McpServerTool(Name = "tia_attach", Destructive = false, OpenWorld = false)]
    [Description("Attach to an explicitly selected running V21 process. Siemens may display its own Openness access dialog. Does not change the project or connect online.")]
    public Task<CallToolResult> Attach([Description("Process ID returned by tia_discover.")] int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0) throw new McpException("processId must be positive.");
        return Call("attach", new { processId }, cancellationToken);
    }

    [McpServerTool(Name = "tia_session_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the worker's current TIA attachment and open project identities.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) => Call("session_status", null, cancellationToken);

    [McpServerTool(Name = "tia_start", Destructive = false, OpenWorld = false)]
    [Description("Explicitly start a separate V21 instance without its graphical editor, owned by this worker, for scratch-project work. Does not open existing projects or connect to any PLC. Must be detached first.")]
    public Task<CallToolResult> Start(CancellationToken cancellationToken) => Call("start", null, cancellationToken);

    [McpServerTool(Name = "hardware_catalog_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search the installed TIA hardware catalog in an attached session. Returns exact type identifiers for subsequent scratch creation; does not scan the network.")]
    public Task<CallToolResult> Catalog(string filter, CancellationToken cancellationToken, int maxItems = 100)
        => Call("catalog_query", new { filter, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "tia_detach", Destructive = false, OpenWorld = false)]
    [Description("Release the worker's TIA connection. Does not close an existing user-owned TIA process. A worker-owned scratch instance may be disposed.")]
    public Task<CallToolResult> Detach(CancellationToken cancellationToken) => Call("detach", null, cancellationToken);

    [McpServerTool(Name = "project_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the attached offline project: PLCs, blocks and hardware. Returns bounded results and explicit completeness. Does not connect to a PLC.")]
    public Task<CallToolResult> Inventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200)
        => Call("project_inventory", new { projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "project_properties", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the open project's metadata (author, comment, copyright, family, version, times, size, modified/primary state), used products, history entries, language settings (supported, active, reference, editing) and text categories. Each list is bounded by maxItems with explicit truncation. Change attributes and languages with change_prepare kinds project_attributes_set and project_languages_set.")]
    public Task<CallToolResult> ProjectProperties(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200)
        => Call("project_properties", new { projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "block_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export an offline block as original Siemens XML and inspect its metadata. Use qualified paths from project_inventory when names are ambiguous. Writes a uniquely named local artifact only; no project changes.")]
    public Task<CallToolResult> BlockRead(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, string? scopePath = null)
    {
        RequireBlock(blockPath, blockName);
        return Call("block_read", new { projectName, plcPath, blockPath, blockName, scopePath,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken);
    }

    [McpServerTool(Name = "xref_query", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read native TIA cross-references for an offline block, including call/read/write locations where supplied by Siemens. Member references may have text only; unresolved coverage is reported.")]
    public Task<CallToolResult> CrossReferences(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, int maxItems = 200, string? scopePath = null)
    {
        RequireBlock(blockPath, blockName);
        return Call("xref_query", new { projectName, plcPath, blockPath, blockName, scopePath, maxItems = Limit(maxItems) }, cancellationToken);
    }

    [McpServerTool(Name = "tia_create_scratch", Destructive = false, OpenWorld = false)]
    [Description("Explicit feasibility action: create a uniquely named scratch project in an empty worker-owned instance started with tia_start. Adds sample SCL blocks, compiles, exports and saves only this scratch project. Omit CPU identifier to select a catalog-listed standard CPU 1511-1 PN; the exact selection is returned. Never edits an existing project or downloads to a PLC. May take several minutes.")]
    public Task<CallToolResult> CreateScratch(CancellationToken cancellationToken, string? cpuTypeIdentifier = null)
        => Call("scratch_create", new { artifactDirectory = Path.Combine(options.ArtifactsDirectory, "scratch"), cpuTypeIdentifier }, cancellationToken);

    private async Task<CallToolResult> Call(string operation, object? arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bridge.CallAsync(operation, arguments, cancellationToken);
            return Result(result.GetRawText(), false);
        }
        catch (BridgeException error) { return Failure(error); }
    }

    private static CallToolResult Failure(BridgeException error) => Result(JsonSerializer.Serialize(new { error = new { code = error.Code, message = error.Message,
        details = (error as BridgeOperationException)?.Details } }, BridgeProtocol.Json), true);

    private static CallToolResult Result(string json, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = json }],
        StructuredContent = JsonSerializer.Deserialize<JsonElement>(json)
    };

    private static int Limit(int value) => value is >= 1 and <= 2000 ? value : throw new McpException("maxItems must be between 1 and 2000.");
    private static void RequireBlock(string? path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name))
            throw new McpException("Provide blockPath from inventory, or an unambiguous blockName.");
    }
}
