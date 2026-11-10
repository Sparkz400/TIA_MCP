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
    [Description("Inspect the attached offline project: PLCs, blocks and hardware. Returns bounded results and explicit completeness. Does not connect to a PLC. Filters for large projects: groupPath (block group names from the top, e.g. \"Station 3000/Downholder\", or a group path from software_inspect) and namePrefix (case-insensitive block name prefix) select blocks; includeHardware=false omits device rows, which otherwise also count. With a filter only matching rows count against maxItems. includeBlocks=false lists hardware and PLCs only. compact=true returns short rows: hardware as path (relative to the project), name, typeIdentifier and I/O ranges (io, start, bytes), leaving out items that have neither a type identifier nor I/O addresses; blocks with paths relative to their PLC and without default-valued flags.")]
    public Task<CallToolResult> Inventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200, string? groupPath = null, string? namePrefix = null, bool includeHardware = true,
        bool includeBlocks = true, bool compact = false)
        => Call("project_inventory", new { projectName, maxItems = Limit(maxItems), groupPath, namePrefix, includeHardware, includeBlocks = includeBlocks ? (bool?)null : false, compact = compact ? true : (bool?)null }, cancellationToken);

    [McpServerTool(Name = "project_properties", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the open project's metadata (author, comment, copyright, family, version, times, size, modified/primary state), used products, history entries, language settings (supported, active, reference, editing) and text categories. Each list is bounded by maxItems with explicit truncation. Change attributes and languages with change_prepare kinds project_attributes_set and project_languages_set.")]
    public Task<CallToolResult> ProjectProperties(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200)
        => Call("project_properties", new { projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "block_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export an offline block as original Siemens XML and inspect its metadata. Use qualified paths from project_inventory when names are ambiguous. Optional format: simaticSd exports native SIMATIC SD documents; generatedSource generates its SCL/STL/DB external source file without dependencies and needs a writable offline project, because any external source the generation leaves is deleted. Writes uniquely named, SHA-256 hashed local artifacts only; no project changes. render=text adds each network as readable text (networks[].text): LAD/FBD contacts and boxes as Boolean expressions, coils, instructions and calls with pin bindings, SCL networks as their source text; a network over 16384 characters is cut in the response and complete in textArtifact, a UTF-8 file written next to the XML that holds the whole block in one piece. A block without networks (a DB) is rendered as its declared interface instead (interface.text: sections, members, data types, start values, comments). A failed rendering, or a block with neither networks nor interface members, reports textUnavailable and keeps the export; no empty text file is written. Optional culture picks title/comment language. Inline xml is returned up to 64 KiB and never with render=text, unless includeXml=true (up to 512 KiB); the artifact always holds the full export.")]
    public async Task<CallToolResult> BlockRead(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, string? scopePath = null, string? format = null, string? render = null, string? culture = null, bool includeXml = false)
    {
        RequireBlock(blockPath, blockName);
        if (render is not (null or "text")) throw new McpException("render accepts only text.");
        var result = await Call("block_read", new { projectName, plcPath, blockPath, blockName, scopePath, format, render, culture,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports") }, cancellationToken);
        return includeXml ? result : WithoutLargeXml(result, render == "text");
    }

    internal const int ExportBatchLimit = 500;
    private static readonly string[] BlockKinds = ["FB", "FC", "OB", "GlobalDB", "InstanceDB"];

    [McpServerTool(Name = "block_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export many offline blocks in one call, for reading or searching a program on disk. Selects blocks like project_inventory: groupPath (block group names from the top, e.g. \"04_Stations\") and namePrefix (case-insensitive); kinds is a comma-separated list of FB, FC, OB, GlobalDB, InstanceDB (default FB,FC,OB). Each block is exported as unchanged native XML and, like block_read render=text, as a complete UTF-8 text file (LAD/FBD as statements, SCL as source, a DB as its declared interface); a block without a text form has no text file and reports textUnavailable. Returns one compact row per block with both file names in directory and no block content, and writes the same rows to index. Know-how protected blocks are listed as skipped; a block that fails is listed with its error and does not stop the others. At most maxBlocks (1-500) per call in path order; continue with nextOffset. Writes local artifacts only; no project changes.")]
    public async Task<CallToolResult> BlockExport(CancellationToken cancellationToken, string? groupPath = null, string? namePrefix = null, string? kinds = null,
        string? plcPath = null, string? projectName = null, string? culture = null, int offset = 0, int maxBlocks = 200)
    {
        if (maxBlocks is < 1 or > ExportBatchLimit) throw new McpException("maxBlocks must be between 1 and 500.");
        if (offset < 0) throw new McpException("offset must not be negative.");
        var wanted = (kinds ?? "FB,FC,OB").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (wanted.Length == 0 || wanted.Any(kind => !BlockKinds.Contains(kind, StringComparer.Ordinal)))
            throw new McpException("kinds accepts a comma-separated list of: " + string.Join(", ", BlockKinds) + ".");
        string directory = Path.Combine(options.ArtifactsDirectory, "exports");
        try
        {
            var inventory = await bridge.CallAsync("project_inventory", new { projectName, maxItems = 2000, groupPath, namePrefix, includeHardware = false }, cancellationToken);
            return Result(BlockExportIndex.Build(inventory, wanted, plcPath, offset, maxBlocks, directory,
                await ExportRows(BlockExportIndex.Select(inventory, wanted, plcPath, offset, maxBlocks), culture, directory, cancellationToken)).ToJsonString(BridgeProtocol.Json), false);
        }
        catch (BridgeException error) { return Failure(error); }
    }

    private async Task<List<System.Text.Json.Nodes.JsonObject>> ExportRows(IReadOnlyList<JsonElement> blocks, string? culture, string directory, CancellationToken cancellationToken)
    {
        var rows = new List<System.Text.Json.Nodes.JsonObject>();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.GetProperty("isKnowHowProtected").GetBoolean()) { rows.Add(BlockExportIndex.Row(block, null, "ProtectedObject", "Know-how protected; not exported.")); continue; }
            try
            {
                rows.Add(BlockExportIndex.Row(block, await bridge.CallAsync("block_read", new { blockPath = block.GetProperty("path").GetString(), render = "text", culture,
                    artifactDirectory = directory }, cancellationToken), null, null));
            }
            // One unreadable block must not cost the rest of the batch.
            catch (BridgeException error) { rows.Add(BlockExportIndex.Row(block, null, error.Code, error.Message)); }
        }
        return rows;
    }

    [McpServerTool(Name = "plc_io_map", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Map the configured I/O address ranges of the offline project to the PLC tags addressed into them. Returns every input and output range with its module and the tags that start inside it, the ranges without any tag, the I/Q tags that lie in no listed range, and addresses shared by several tags. Offline configuration only: a range without tags can still be used through its hardware identifier or absolute addressing, and ranges the hardware inventory does not expose (for example drive telegrams configured in Startdrive) show up as tags without listed hardware. plcPath selects the PLC whose tags are read. No project changes.")]
    public async Task<CallToolResult> PlcIoMap(CancellationToken cancellationToken, string? plcPath = null, string? projectName = null)
    {
        try
        {
            var inventory = await bridge.CallAsync("project_inventory", new { projectName, maxItems = 2000, includeBlocks = false, compact = true }, cancellationToken);
            var software = await bridge.CallAsync("software_inspect", new { projectName, plcPath, maxItems = 2000, include = "tags", compact = true }, cancellationToken);
            return Result(IoMap.Build(inventory, software).ToJsonString(BridgeProtocol.Json), false);
        }
        catch (BridgeException error) { return Failure(error); }
    }

    [McpServerTool(Name = "plc_tag_usage", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report which exported blocks read and write each I/Q PLC tag, to answer whether an input or output is used. index is the index file of a block_export run (several, comma-separated, for a paged export); its XML files are scanned on disk, LAD/FBD and SCL networks alike, and the tags are read from the offline project. Each tag lists readers and writers as block and network number; unusedOnly=true returns only tags no scanned block references, namePrefix narrows the tags. absoluteAccesses lists direct %I/%Q operands that bypass the tags. Static evidence over the exported blocks only: know-how protected or skipped blocks, technology-object and HMI use, hardware-identifier access and PEEK/POKE are not seen, so an unused tag is unused in the scanned code, not proven unused. No project changes.")]
    public async Task<CallToolResult> PlcTagUsage(string index, CancellationToken cancellationToken, string? plcPath = null, string? projectName = null, bool unusedOnly = false, string? namePrefix = null)
    {
        string root = Path.GetFullPath(options.ArtifactsDirectory) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var skipped = new List<string>();
        foreach (string entry in index.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.GetFullPath(entry);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new McpException("index must name block_export index files below the Host artifact directory.");
            var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as System.Text.Json.Nodes.JsonObject;
            if (document?["blocks"] is not System.Text.Json.Nodes.JsonArray rows || document["directory"]?.GetValue<string>() is not { } directory)
                throw new McpException("index is not a block_export index file.");
            foreach (var row in rows)
                if (row?["xml"]?.GetValue<string>() is { } xml && Path.GetFullPath(Path.Combine(directory, xml)) is var file && file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file)) files.Add(file);
                else skipped.Add(row?["name"]?.GetValue<string>() ?? "?");
        }
        if (files.Count == 0) throw new McpException("The index lists no exported block files.");
        try
        {
            var software = await bridge.CallAsync("software_inspect", new { projectName, plcPath, maxItems = 2000, include = "tags", compact = true }, cancellationToken);
            var uses = new List<TagUsageScan.Use>();
            foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                uses.AddRange(TagUsageScan.Uses(TagUsageScan.Load(file)));
            }
            var tags = IoMap.Tags(software).Where(tag => namePrefix is null || tag.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
            var report = TagUsageScan.Report(tags, uses, unusedOnly);
            bool tagsCut = software.TryGetProperty("truncation", out _) || software.GetProperty("completeness").GetProperty("truncated").GetBoolean();
            report["blocksScanned"] = files.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            report["blocksNotScanned"] = new System.Text.Json.Nodes.JsonArray([.. skipped.Select(name => (System.Text.Json.Nodes.JsonNode)name)]);
            report["completeness"] = new System.Text.Json.Nodes.JsonObject { ["complete"] = false, ["tagsTruncated"] = tagsCut,
                ["scope"] = "Static references in the exported block files of the given index. Blocks not in the index, skipped blocks, technology objects, HMI, hardware-identifier access and indirect addressing are not covered; the files are not checked against the current project state." };
            return Result(report.ToJsonString(BridgeProtocol.Json), false);
        }
        catch (BridgeException error) { return Failure(error); }
        catch (System.Xml.XmlException error) { throw new McpException("An exported block file is not readable XML: " + error.Message); }
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
