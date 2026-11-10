using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Host;

/// <summary>Selection and result rows of block_export. Pure: works on project_inventory and block_read results, so the
/// offline tests cover it without a worker.</summary>
internal static class BlockExportIndex
{
    private static JsonElement[] Matching(JsonElement inventory, IReadOnlyCollection<string> kinds, string? plcPath) =>
        [.. inventory.GetProperty("blocks").EnumerateArray()
            .Where(block => kinds.Contains(block.GetProperty("kind").GetString()!, StringComparer.Ordinal)
                && (plcPath is null || block.GetProperty("plcPath").GetString() == plcPath))
            .OrderBy(block => block.GetProperty("path").GetString(), StringComparer.Ordinal)];

    /// <summary>The blocks of this page, in path order.</summary>
    internal static IReadOnlyList<JsonElement> Select(JsonElement inventory, IReadOnlyCollection<string> kinds, string? plcPath, int offset, int maxBlocks) =>
        [.. Matching(inventory, kinds, plcPath).Skip(offset).Take(maxBlocks)];

    /// <summary>One block: identity, the two file names inside the export directory, or why it has none.</summary>
    internal static JsonObject Row(JsonElement block, JsonElement? read, string? errorCode, string? errorMessage)
    {
        string plc = block.GetProperty("plcPath").GetString()!, path = block.GetProperty("path").GetString()!;
        var row = new JsonObject
        {
            ["name"] = block.GetProperty("name").GetString(), ["kind"] = block.GetProperty("kind").GetString(),
            ["number"] = block.GetProperty("number").GetInt32(), ["language"] = block.GetProperty("programmingLanguage").GetString(),
            // Relative to plcPath + "/blocks/": a full path per row would dominate the response.
            ["path"] = path.StartsWith(plc + "/blocks/", StringComparison.Ordinal) ? path[(plc.Length + "/blocks/".Length)..] : path,
        };
        if (errorCode is not null) { row["skipped"] = new JsonObject { ["code"] = errorCode, ["message"] = errorMessage }; return row; }
        var result = read!.Value;
        row["xml"] = Path.GetFileName(result.GetProperty("artifact").GetProperty("path").GetString());
        row["bytes"] = result.GetProperty("artifact").GetProperty("bytes").GetInt64();
        if (result.TryGetProperty("textArtifact", out var text)) row["text"] = Path.GetFileName(text.GetProperty("path").GetString());
        if (result.TryGetProperty("networks", out var networks)) row["networks"] = networks.GetArrayLength();
        if (result.TryGetProperty("textUnavailable", out var unavailable)) row["textUnavailable"] = unavailable.GetProperty("reason").GetString();
        return row;
    }

    /// <summary>The response, also written to an index file so a later step can read it without repeating the call.</summary>
    internal static JsonObject Build(JsonElement inventory, IReadOnlyCollection<string> kinds, string? plcPath, int offset, int maxBlocks, string directory,
        List<JsonObject> rows)
    {
        int matched = Matching(inventory, kinds, plcPath).Length;
        var completeness = inventory.GetProperty("completeness");
        bool listed = !completeness.GetProperty("truncated").GetBoolean();
        var result = new JsonObject
        {
            ["project"] = JsonNode.Parse(inventory.GetProperty("project").GetRawText()),
            ["plcs"] = JsonNode.Parse(inventory.GetProperty("plcs").GetRawText()),
            ["directory"] = directory,
            ["kinds"] = new JsonArray([.. kinds.Select(kind => (JsonNode)kind)]),
            ["matchedBlocks"] = matched, ["offset"] = offset,
            ["exported"] = rows.Count(row => row["skipped"] is null), ["skipped"] = rows.Count(row => row["skipped"] is not null),
            ["nextOffset"] = offset + rows.Count < matched ? offset + rows.Count : null,
            ["blocks"] = new JsonArray([.. rows]),
            ["completeness"] = new JsonObject
            {
                // False when the inventory walk itself was cut: blocks beyond its bound are not in matchedBlocks.
                ["inventoryComplete"] = listed, ["complete"] = listed && offset + rows.Count >= matched,
                ["scope"] = "Native XML and rendered text of the selected blocks; paths are relative to plcPath + \"/blocks/\". No project changes, no revision guarantee across the batch."
            }
        };
        Directory.CreateDirectory(directory);
        string index = Path.Combine(directory, "block-export-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(index, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        result["index"] = index;
        return result;
    }
}
