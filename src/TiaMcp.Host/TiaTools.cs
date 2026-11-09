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
    [Description("Search the installed TIA hardware catalog in an attached session. Returns exact type identifiers for subsequent scratch creation; an entry with a captured module capability profile names it in profile (contracts/hardware-catalog-profiles.v1.json#typeIdentifier). Does not scan the network.")]
    public Task<CallToolResult> Catalog(string filter, CancellationToken cancellationToken, int maxItems = 100)
        => Call("catalog_query", new { filter, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "tia_detach", Destructive = false, OpenWorld = false)]
    [Description("Release the worker's TIA connection. Does not close an existing user-owned TIA process. A worker-owned scratch instance may be disposed.")]
    public Task<CallToolResult> Detach(CancellationToken cancellationToken) => Call("detach", null, cancellationToken);

    [McpServerTool(Name = "project_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the attached offline project: PLCs, blocks and hardware. Returns bounded results and explicit completeness. Does not connect to a PLC. Filters for large projects: groupPath (block group names from the top, e.g. \"Station 3000/Downholder\", or a group path from software_inspect) and namePrefix (case-insensitive block name prefix) select blocks; includeHardware=false omits device rows, which otherwise also count. With a filter only matching rows count against maxItems.")]
    public Task<CallToolResult> Inventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200, string? groupPath = null, string? namePrefix = null, bool includeHardware = true)
        => Call("project_inventory", new { projectName, maxItems = Limit(maxItems), groupPath, namePrefix, includeHardware }, cancellationToken);

    [McpServerTool(Name = "project_properties", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the open project's metadata (author, comment, copyright, family, version, times, size, modified/primary state), used products, history entries, language settings (supported, active, reference, editing) and text categories. Each list is bounded by maxItems with explicit truncation. Change attributes and languages with change_prepare kinds project_attributes_set and project_languages_set.")]
    public Task<CallToolResult> ProjectProperties(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200)
        => Call("project_properties", new { projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "block_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export an offline block as original Siemens XML and inspect its metadata. Use qualified paths from project_inventory when names are ambiguous. Optional format: simaticSd exports native SIMATIC SD documents; generatedSource generates its SCL/STL/DB external source file without dependencies and needs a writable offline project, because any external source the generation leaves is deleted. Writes uniquely named, SHA-256 hashed local artifacts only; no project changes. render=text adds each network as readable text (networks[], text): LAD/FBD contacts and boxes as Boolean expressions, coils, instructions and calls with pin bindings; optional culture picks title/comment language. Inline xml is returned up to 64 KiB and never with render=text, unless includeXml=true (up to 512 KiB); the artifact always holds the full export.")]
    public async Task<CallToolResult> BlockRead(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, string? scopePath = null, string? format = null, string? render = null, string? culture = null, bool includeXml = false)
    {
        RequireBlock(blockPath, blockName);
        if (render is not (null or "text")) throw new McpException("render accepts only text.");
        var result = await Call("block_read", new { projectName, plcPath, blockPath, blockName, scopePath, format, render, culture,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken);
        return includeXml ? result : WithoutLargeXml(result, render == "text");
    }

    /// <summary>Inline XML is a large, hard-to-read duplicate of the export artifact; keep it only when small and no text rendering was asked for.</summary>
    internal const int InlineXmlLimit = 64 * 1024;
    private static CallToolResult WithoutLargeXml(CallToolResult result, bool rendered)
    {
        if (result.IsError == true || result.StructuredContent is not JsonElement content || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("xml", out var xml) || xml.ValueKind != JsonValueKind.String) return result;
        int characters = xml.GetString()!.Length;
        if (!rendered && characters <= InlineXmlLimit) return result;
        var node = System.Text.Json.Nodes.JsonNode.Parse(content.GetRawText())!.AsObject();
        node.Remove("xml");
        node["xmlOmitted"] = new System.Text.Json.Nodes.JsonObject { ["characters"] = characters,
            ["reason"] = rendered ? "render=text" : "larger than 64 KiB", ["read"] = "artifact.path with artifact_read, or block_read includeXml=true" };
        return Result(node.ToJsonString(BridgeProtocol.Json), false);
    }

    [McpServerTool(Name = "xref_query", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read native TIA cross-references for an offline block, including call/read/write locations where supplied by Siemens. Member references may have text only; unresolved coverage is reported. memberPath (relative to the block, e.g. Real[210], Real[200..220] or Station.Downholder.Enable) returns only locations of those members or elements, each with memberMatch: exact (member or constant index in range), indirect (variable index), wholeArray (array used without index) or container (containing structure or the whole block used); only matches take maxItems, and complete=true with no exact rows means no constant-index use.")]
    public Task<CallToolResult> CrossReferences(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, int maxItems = 200, string? scopePath = null, string? memberPath = null)
    {
        RequireBlock(blockPath, blockName);
        return Call("xref_query", new { projectName, plcPath, blockPath, blockName, scopePath, memberPath, maxItems = Limit(maxItems) }, cancellationToken);
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
