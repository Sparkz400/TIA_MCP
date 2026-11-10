using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Host;

/// <summary>plc_io_map: configured I/O address ranges next to the PLC tags addressed into them. Pure: works on compact
/// project_inventory and software_inspect results, so the offline tests cover it without a worker.</summary>
internal static partial class IoMap
{
    [GeneratedRegex(@"^%([IQ])([BWD]?)(\d+)(?:\.([0-7]))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Address();

    internal sealed record Tag(string Name, string Table, string DataType, string LogicalAddress, char Area, int Byte, int? Bit, int Bytes);

    /// <summary>An I or Q tag with its first byte and width; null for memory, unaddressed and unparsable tags.</summary>
    internal static Tag? Parse(string name, string table, string? dataType, string? logicalAddress)
    {
        if (logicalAddress is null || Address().Match(logicalAddress) is not { Success: true } match) return null;
        int start = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        int? bit = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : null;
        // The width is the address prefix when present; a structured tag at a bit address covers an unknown span, counted as its first byte.
        int bytes = match.Groups[2].Value switch { "B" => 1, "W" => 2, "D" => 4, _ => 1 };
        return new Tag(name, table, dataType ?? "", logicalAddress, match.Groups[1].Value[0], start, bit, bytes);
    }

    internal static IEnumerable<Tag> Tags(JsonElement software) =>
        from table in software.GetProperty("tagTables").EnumerateArray()
        from tag in table.GetProperty("tags").EnumerateArray()
        let parsed = Parse(tag.GetProperty("name").GetString()!, table.GetProperty("name").GetString()!,
            tag.TryGetProperty("dataType", out var type) ? type.GetString() : null,
            tag.TryGetProperty("logicalAddress", out var address) ? address.GetString() : null)
        where parsed is not null
        select parsed;

    private sealed record Range(char Area, int Start, int End, string Module, string? Type);

    internal static JsonObject Build(JsonElement inventory, JsonElement software)
    {
        var ranges = new List<Range>();
        foreach (var item in inventory.GetProperty("hardware").EnumerateArray())
            if (item.TryGetProperty("addresses", out var addresses))
                foreach (var address in addresses.EnumerateArray())
                {
                    string io = address.GetProperty("io").GetString()!;
                    if (io is not ("Input" or "Output")) continue;
                    int start = address.GetProperty("start").GetInt32(), bytes = Math.Max(1, address.GetProperty("bytes").GetInt32());
                    ranges.Add(new Range(io == "Input" ? 'I' : 'Q', start, start + bytes - 1, item.GetProperty("path").GetString()!,
                        item.TryGetProperty("typeIdentifier", out var type) ? type.GetString() : null));
                }
        ranges = [.. ranges.OrderBy(range => range.Area).ThenBy(range => range.Start).ThenBy(range => range.Module, StringComparer.Ordinal)];
        var tags = Tags(software).OrderBy(tag => tag.Area).ThenBy(tag => tag.Byte).ThenBy(tag => tag.Bit ?? -1).ThenBy(tag => tag.Name, StringComparer.Ordinal).ToArray();
        var rows = new JsonArray();
        var placed = new HashSet<Tag>();
        var empty = new JsonArray();
        foreach (var range in ranges)
        {
            var inside = tags.Where(tag => tag.Area == range.Area && tag.Byte >= range.Start && tag.Byte <= range.End).ToArray();
            foreach (var tag in inside) placed.Add(tag);
            var row = new JsonObject { ["range"] = "%" + range.Area + range.Start + ".." + range.End, ["module"] = range.Module };
            if (range.Type is not null) row["typeIdentifier"] = range.Type;
            row["tagCount"] = inside.Length;
            row["tags"] = new JsonArray([.. inside.Select(tag => (JsonNode)(tag.LogicalAddress + " " + tag.Name))]);
            rows.Add(row);
            if (inside.Length == 0) empty.Add(new JsonObject { ["range"] = row["range"]!.GetValue<string>(), ["module"] = range.Module });
        }
        var shared = tags.GroupBy(tag => tag.LogicalAddress, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => (JsonNode)new JsonObject { ["address"] = group.Key, ["tags"] = new JsonArray([.. group.Select(tag => (JsonNode)tag.Name)]) });
        var completeness = inventory.GetProperty("completeness");
        bool tagsCut = software.TryGetProperty("truncation", out _) || software.GetProperty("completeness").GetProperty("truncated").GetBoolean();
        bool hardwareCut = completeness.GetProperty("truncated").GetBoolean();
        int plcs = inventory.GetProperty("plcs").GetArrayLength();
        return new JsonObject
        {
            ["project"] = JsonNode.Parse(inventory.GetProperty("project").GetRawText()),
            ["plcPath"] = software.TryGetProperty("plcPath", out var plc) ? plc.GetString() : null,
            ["summary"] = new JsonObject { ["ranges"] = ranges.Count, ["ioTags"] = tags.Length, ["rangesWithoutTags"] = empty.Count,
                ["tagsWithoutListedHardware"] = tags.Length - placed.Count },
            ["ranges"] = rows,
            ["rangesWithoutTags"] = empty,
            ["tagsWithoutListedHardware"] = new JsonArray([.. tags.Where(tag => !placed.Contains(tag))
                .Select(tag => (JsonNode)new JsonObject { ["name"] = tag.Name, ["address"] = tag.LogicalAddress, ["dataType"] = tag.DataType, ["table"] = tag.Table })]),
            ["sharedAddresses"] = new JsonArray([.. shared]),
            ["completeness"] = new JsonObject
            {
                ["complete"] = !tagsCut && !hardwareCut && plcs <= 1, ["hardwareTruncated"] = hardwareCut, ["tagsTruncated"] = tagsCut, ["plcsInProject"] = plcs,
                ["scope"] = "Offline configuration only. A tag is placed by its first byte; a structured tag's full span is not evaluated. "
                    + "Address ranges that the hardware inventory does not expose, such as drive telegrams configured in Startdrive, appear as tags without listed hardware. "
                    + "With several PLCs the address ranges of all of them are listed together; a range without tags may belong to another PLC. "
                    + "A range without tags can still be used through its hardware identifier or absolute addressing; plc_tag_usage reports absolute accesses."
            }
        };
    }
}
