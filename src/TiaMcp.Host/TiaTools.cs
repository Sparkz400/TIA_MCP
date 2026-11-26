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
    [Description("List running TIA Portal V21 instances. Each process lists attachedSessions: the other Openness clients attached to it (processId, processName, processPath, processRunning, isActive = in the middle of a call, attachedAtUtc, utilizationSeconds, accessLevel, trustAuthority). Does not start TIA or connect to a PLC.")]
    public Task<CallToolResult> Discover(CancellationToken cancellationToken) => Call("discover", null, cancellationToken);

    [McpServerTool(Name = "tia_capabilities", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report V21 bridge installation, implemented operations and capability limits. Installation evidence does not verify licenses or project-specific support.")]
    public Task<CallToolResult> Capabilities(CancellationToken cancellationToken) => Call("bridge_info", null, cancellationToken);

    [McpServerTool(Name = "tia_attach", Destructive = false, OpenWorld = false)]
    [Description("Attach to an explicitly selected running V21 process; Siemens may show its own Openness access dialog. Does not change the project or connect online. Lists attachedSessions, the Portal's other Openness clients. waitForIdleSeconds (0..60) checks first: if another client is still in a call after that, the attach is refused with OpennessClientBusy (busyClients) and nothing is attached. Never disconnects another client or ends its process.")]
    public Task<CallToolResult> Attach([Description("Process ID returned by tia_discover.")] int processId, CancellationToken cancellationToken,
        [Description("Seconds (0..60) to wait for other Openness clients of this Portal to finish their call before attaching; omit to attach without the check.")] int? waitForIdleSeconds = null)
    {
        if (processId <= 0) throw new McpException("processId must be positive.");
        if (waitForIdleSeconds is < 0 or > 60) throw new McpException("waitForIdleSeconds must be from 0 to 60.");
        // The wait runs inside one bridge call, so it must end well before the Host's call timeout to refuse cleanly.
        if (waitForIdleSeconds is { } wait && wait > options.TimeoutSeconds - 15)
            throw new McpException($"waitForIdleSeconds must be at most {Math.Max(0, options.TimeoutSeconds - 15)} with this Host's --timeout-seconds {options.TimeoutSeconds}.");
        if (waitForIdleSeconds is not null && options.TiaVersion == 19) throw new McpException("waitForIdleSeconds is V21-only; the V19 adapter attaches without the check.");
        return Call("attach", new { processId, waitForIdleSeconds }, cancellationToken);
    }

    [McpServerTool(Name = "tia_session_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the worker's current TIA attachment and open project identities, and portals: every TIA Portal process visible on this machine (processId, mode, the project it has open, thisWorker, ownedByThisWorker). One Portal opens a project writable at a time, so portals names the lock owner when several Hosts or a user's TIA share the machine; other Hosts use readOnly or project_open_copy.")]
    public Task<CallToolResult> Status(CancellationToken cancellationToken) => Call("session_status", null, cancellationToken);

    [McpServerTool(Name = "tia_start", Destructive = false, OpenWorld = false)]
    [Description("Explicitly start a separate V21 instance without its graphical editor, owned by this worker, for scratch-project work. Does not open existing projects or connect to any PLC. Must be detached first.")]
    public Task<CallToolResult> Start(CancellationToken cancellationToken) => Call("start", null, cancellationToken);

    [McpServerTool(Name = "hardware_catalog_search", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Search the installed TIA hardware catalog in an attached session. Returns exact type identifiers for subsequent scratch creation; an entry with a captured module capability profile names it in profile (contracts/hardware-catalog-profiles.v1.json#typeIdentifier). Does not scan the network.")]
    public Task<CallToolResult> Catalog(string filter, CancellationToken cancellationToken, int maxItems = 100)
        => Call("catalog_query", new { filter, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "tia_detach", Destructive = false, OpenWorld = false)]
    [Description("Release the worker's TIA connection. A user-owned TIA instance is only detached, never closed. A worker-owned instance (tia_start) has its projects closed without saving (unsaved edits discarded, listed in projectsClosed) and the call waits up to waitForExitSeconds (0..300, default 30) for exit. terminateOwnedIfRunning:true ends that exact owned process only. PLCSIM instances stay registered (plcsimRegistrations; release with sim_release before simulation_discovery_baseline).")]
    public Task<CallToolResult> Detach(CancellationToken cancellationToken, int waitForExitSeconds = 30, bool terminateOwnedIfRunning = false)
        => Call("detach", new { waitForExitSeconds, terminateOwnedIfRunning }, cancellationToken);

    [McpServerTool(Name = "project_inventory", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Inspect the attached offline project: PLCs, blocks and hardware, bounded; completeness.complete is always false, completeness.scopeComplete says whether scope was fully listed. Does not connect to a PLC. Filters: groupPath (block group names from the top) and namePrefix (case-insensitive); then only matches count against maxItems. includeHardware=false omits devices; includeBlocks=false lists hardware and PLCs only; compact=true returns short rows.")]
    public Task<CallToolResult> Inventory(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200, string? groupPath = null, string? namePrefix = null, bool includeHardware = true,
        bool includeBlocks = true, bool compact = false)
        => Call("project_inventory", new { projectName, maxItems = Limit(maxItems), groupPath, namePrefix, includeHardware, includeBlocks = includeBlocks ? (bool?)null : false, compact = compact ? true : (bool?)null }, cancellationToken);

    [McpServerTool(Name = "project_properties", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the open project's metadata (author, comment, copyright, family, version, times, size, modified/primary state), used products, history entries, language settings (supported, active, reference, editing) and text categories. Each list is bounded by maxItems with explicit truncation. Change attributes and languages with change_prepare kinds project_attributes_set and project_languages_set.")]
    public Task<CallToolResult> ProjectProperties(CancellationToken cancellationToken, string? projectName = null, int maxItems = 200)
        => Call("project_properties", new { projectName, maxItems = Limit(maxItems) }, cancellationToken);

    [McpServerTool(Name = "block_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export an offline block as original Siemens XML and inspect its metadata; no project changes. Use qualified paths from project_inventory when names are ambiguous. format: simaticSd or generatedSource (needs a writable offline project). render=text adds readable network text (complete in textArtifact). culture picks the comment language. Writes hashed artifacts; inline xml up to 64 KiB (never with render=text) unless includeXml=true (512 KiB).")]
    public async Task<CallToolResult> BlockRead(CancellationToken cancellationToken, string? blockPath = null, string? blockName = null,
        string? plcPath = null, string? projectName = null, string? scopePath = null, string? format = null, string? render = null, string? culture = null, bool includeXml = false)
    {
        RequireBlock(blockPath, blockName);
        if (render is not (null or "text")) throw new McpException("render accepts only text.");
        ExportRetention.Prune(Path.Combine(options.ArtifactsDirectory, "exports"));
        var result = await Call("block_read", new
        {
            projectName,
            plcPath,
            blockPath,
            blockName,
            scopePath,
            format,
            render,
            culture,
            artifactDirectory = Path.Combine(options.ArtifactsDirectory, "exports")
        }, cancellationToken);
        return includeXml ? result : WithoutLargeXml(result, render == "text");
    }

    internal const int ExportBatchLimit = 500;
    private static readonly string[] BlockKinds = ["FB", "FC", "OB", "GlobalDB", "InstanceDB"];

    [McpServerTool(Name = "block_export", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export many offline blocks in one call as unchanged native XML plus a full UTF-8 text file each (as block_read render=text), to read or search a program on disk. Select by groupPath, namePrefix and kinds (comma-separated FB, FC, OB, GlobalDB, InstanceDB; default FB,FC,OB). Returns one row per block with file names, no content, also written to index. Protected blocks skipped, failures per block. maxBlocks 1-500; continue with nextOffset. No project changes.")]
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
            var inventory = await ExportSelection(projectName, groupPath, namePrefix, offset, cancellationToken);
            return Result(BlockExportIndex.Build(inventory, wanted, plcPath, offset, maxBlocks, directory,
                await ExportRows(BlockExportIndex.Select(inventory, wanted, plcPath, offset, maxBlocks), projectName, culture, directory, cancellationToken)).ToJsonString(BridgeProtocol.Json), false);
        }
        catch (BridgeException error) { return Failure(error); }
    }

    // The selection of a paged run: the first page reads the inventory, the following pages (offset > 0) reuse it for a short time instead of
    // walking the project again. It is a snapshot, like the rest of a run: a block that changed meanwhile is a skipped row, not a wrong file.
    private static readonly object SelectionLock = new();
    private static (IBridgeClient Owner, string Key, DateTime At, JsonElement Inventory)? _selection;
    internal static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(10);

    private async Task<JsonElement> ExportSelection(string? projectName, string? groupPath, string? namePrefix, int offset, CancellationToken cancellationToken)
    {
        string key = JsonSerializer.Serialize(new { projectName, groupPath, namePrefix });
        lock (SelectionLock)
            if (offset > 0 && _selection is { } cached && ReferenceEquals(cached.Owner, bridge) && cached.Key == key && DateTime.UtcNow - cached.At < SelectionLifetime) return cached.Inventory;
        var inventory = await PagedInventory(projectName, groupPath, namePrefix, cancellationToken);
        lock (SelectionLock) _selection = (bridge, key, DateTime.UtcNow, inventory);
        return inventory;
    }

    internal const int InventoryPageItems = 2000, MaxInventoryPages = 100;

    // project_inventory returns at most 2000 blocks per call. Further pages skip the blocks already read (blockOffset, in walk order), so every
    // match is gathered before the selection is sorted; completeness.truncated stays true only when the walk or the page cap cut the listing.
    private async Task<JsonElement> PagedInventory(string? projectName, string? groupPath, string? namePrefix, CancellationToken cancellationToken)
    {
        var first = await bridge.CallAsync("project_inventory", new { projectName, maxItems = InventoryPageItems, groupPath, namePrefix, includeHardware = false }, cancellationToken);
        if (!first.GetProperty("completeness").GetProperty("truncated").GetBoolean()) return first;
        var merged = System.Text.Json.Nodes.JsonNode.Parse(first.GetRawText())!.AsObject();
        var blocks = merged["blocks"]!.AsArray();
        var seen = blocks.Select(block => block!["path"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var page = first;
        bool complete = false;
        for (int pages = 1; pages < MaxInventoryPages; pages++)
        {
            // Only a page cut by maxItems has more to read; a walk cut (walkTruncated) cannot be continued by skipping.
            if (page.TryGetProperty("filter", out var filter) && filter.TryGetProperty("walkTruncated", out var walkCut) && walkCut.GetBoolean()) break;
            page = await bridge.CallAsync("project_inventory", new { projectName, maxItems = InventoryPageItems, groupPath, namePrefix, includeHardware = false, blockOffset = blocks.Count }, cancellationToken);
            foreach (var block in page.GetProperty("blocks").EnumerateArray())
                if (seen.Add(block.GetProperty("path").GetString()!)) blocks.Add(System.Text.Json.Nodes.JsonNode.Parse(block.GetRawText()));
            if (!page.GetProperty("completeness").GetProperty("truncated").GetBoolean()) { complete = true; break; }
        }
        merged["completeness"]!["truncated"] = !complete;
        merged["completeness"]!["scopeComplete"] = complete && page.GetProperty("completeness").GetProperty("scopeComplete").GetBoolean();
        return JsonDocument.Parse(merged.ToJsonString()).RootElement;
    }

    // Blocks per worker call: the worker selects the PLC once and finds each block by its path; one call stays far below the call timeout.
    internal const int ExportChunk = 20;

    private async Task<List<System.Text.Json.Nodes.JsonObject>> ExportRows(IReadOnlyList<JsonElement> blocks, string? projectName, string? culture, string directory, CancellationToken cancellationToken)
    {
        // Rows stay in the order of the selection; the blocks to export are gathered per PLC, in calls of at most ExportChunk paths.
        var rows = new System.Text.Json.Nodes.JsonObject?[blocks.Count];
        var byPlc = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].GetProperty("isKnowHowProtected").GetBoolean()) rows[i] = BlockExportIndex.Row(blocks[i], null, "ProtectedObject", "Know-how protected; not exported.");
            else
            {
                string plc = blocks[i].GetProperty("plcPath").GetString()!;
                if (!byPlc.TryGetValue(plc, out var indexes)) byPlc[plc] = indexes = [];
                indexes.Add(i);
            }
        }
        foreach (var (plcPath, indexes) in byPlc)
            foreach (var chunk in indexes.Chunk(ExportChunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var response = await bridge.CallAsync("block_read", new
                    {
                        projectName,
                        plcPath,
                        blockPaths = chunk.Select(i => blocks[i].GetProperty("path").GetString()).ToArray(),
                        render = "text",
                        culture,
                        includeXml = false,
                        artifactDirectory = directory
                    }, cancellationToken);
                    var entries = response.GetProperty("results").EnumerateArray().ToDictionary(entry => entry.GetProperty("path").GetString()!, StringComparer.Ordinal);
                    foreach (int i in chunk)
                    {
                        if (!entries.TryGetValue(blocks[i].GetProperty("path").GetString()!, out var entry)) throw new BridgeException("InvalidResponse", "The worker answered a block export without an entry for every block.");
                        rows[i] = entry.GetProperty("ok").GetBoolean() ? BlockExportIndex.Row(blocks[i], entry, null, null)
                            : BlockExportIndex.Row(blocks[i], null, entry.GetProperty("error").GetProperty("code").GetString(), entry.GetProperty("error").GetProperty("message").GetString());
                    }
                }
                // A refused call (a completed error) is each block's skipped row. A lost transport, timeout, faulted bridge or a call still
                // running is not: it ends the batch, so no block is reported skipped, the next offset stays put and the run is never "complete".
                catch (BridgeOperationException error) { foreach (int i in chunk) rows[i] = BlockExportIndex.Row(blocks[i], null, error.Code, error.Message); }
            }
        return [.. rows.Select(row => row!)];
    }

    [McpServerTool(Name = "plc_io_map", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Map the configured I/O address ranges of the offline project to the PLC tags addressed into them: ranges with module and tags, ranges without tags, I/Q tags in no listed range, shared addresses. plcPath selects the PLC. Offline configuration only: an untagged range may still be used via hardware identifier or absolute addressing, and ranges missing from the hardware inventory (e.g. Startdrive telegrams) show as unlisted tags. No project changes.")]
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
    [Description("Report which exported blocks read and write each I/Q PLC tag. index: index file(s) of a block_export run, comma-separated; XML scanned on disk, tags read from the offline project. unusedOnly=true returns only unreferenced tags; namePrefix narrows; absoluteAccesses lists direct %I/%Q operands. Static evidence only: protected/skipped blocks, TO/HMI use, hardware-identifier access and PEEK/POKE are unseen, so unused is not proven unused. No project changes.")]
    public async Task<CallToolResult> PlcTagUsage(string index, CancellationToken cancellationToken, string? plcPath = null, string? projectName = null, bool unusedOnly = false, string? namePrefix = null)
    {
        List<string> files, skipped;
        try { (files, skipped) = TagUsageScan.IndexFiles(options.ArtifactsDirectory, index); }
        catch (BridgeException error) { return Failure(error); }
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
            report["completeness"] = new System.Text.Json.Nodes.JsonObject
            {
                ["complete"] = false,
                ["tagsTruncated"] = tagsCut,
                ["scope"] = "Static references in the exported block files of the given index. Blocks not in the index, skipped blocks, technology objects, HMI, hardware-identifier access and indirect addressing are not covered; the files are not checked against the current project state."
            };
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
        node["xmlOmitted"] = new System.Text.Json.Nodes.JsonObject
        {
            ["characters"] = characters,
            ["reason"] = rendered ? "render=text" : "larger than 64 KiB",
            ["read"] = "artifact.path with artifact_read, or block_read includeXml=true"
        };
        return Result(node.ToJsonString(BridgeProtocol.Json), false);
    }

    [McpServerTool(Name = "xref_query", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read native TIA cross-references for an offline block, including call/read/write locations where Siemens supplies them; unresolved coverage is reported. memberPath (relative to the block, e.g. Real[210], Real[200..220] or Station.Downholder.Enable) limits results to those members, each with memberMatch exact, indirect, wholeArray or container; only matches take maxItems. complete=true with no exact rows means no constant-index use.")]
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
            return ToolResult.Of(await bridge.CallAsync(operation, arguments, cancellationToken));
        }
        catch (BridgeException error) { return Failure(error); }
    }

    private static CallToolResult Failure(BridgeException error) => ToolResult.Failure(error, "BridgeError");

    private static CallToolResult Result(string json, bool isError) => ToolResult.FromJson(json, isError);

    private static int Limit(int value) => value is >= 1 and <= 2000 ? value : throw new McpException("maxItems must be between 1 and 2000.");
    private static void RequireBlock(string? path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(name))
            throw new McpException("Provide blockPath from inventory, or an unambiguous blockName.");
    }
}
