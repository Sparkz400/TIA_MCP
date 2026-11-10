using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Host;

/// <summary>plc_tag_usage: which exported blocks read and write each PLC tag. Pure: reads native block XML documents
/// (LAD/FBD FlgNet and SCL StructuredText) and never calls a worker. Static evidence only: an operand is matched by the first
/// component of its symbol, so indirect access (PEEK/POKE, pointers, hardware identifiers) and unexported blocks are not seen.</summary>
internal static class TagUsageScan
{
    internal sealed record Use(string Block, int Network, bool Write, string Operand);

    private static readonly HashSet<string> WritingParts = new(StringComparer.Ordinal) { "Coil", "SCoil", "RCoil", "Sr", "Rs" };
    private static readonly HashSet<string> AssignmentTokens = new(StringComparer.Ordinal) { ":=", "+=", "-=", "*=", "/=", "?=" };

    internal static XDocument Load(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    /// <summary>Every global-variable and absolute-address operand of one exported block, with its network number and direction.</summary>
    internal static List<Use> Uses(XDocument document)
    {
        var uses = new List<Use>();
        var block = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
        string name = block?.Element("AttributeList")?.Element("Name")?.Value ?? "?";
        int number = 0;
        foreach (var unit in document.Descendants("SW.Blocks.CompileUnit"))
        {
            number++;
            var source = unit.Element("AttributeList")?.Element("NetworkSource");
            foreach (var network in source?.Elements() ?? [])
                if (network.Name.LocalName == "FlgNet") Graphical(network, name, number, uses);
                else if (network.Name.LocalName == "StructuredText") Structured(network, name, number, uses);
        }
        return uses;
    }

    private static string? Operand(XElement access)
    {
        string? scope = (string?)access.Attribute("Scope");
        var children = access.Elements().ToArray();
        var address = children.FirstOrDefault(child => child.Name.LocalName == "Address")
            ?? children.FirstOrDefault(child => child.Name.LocalName == "Symbol")?.Elements().FirstOrDefault(child => child.Name.LocalName == "Address");
        if (address is not null && Absolute(address) is { } absolute) return absolute;
        if (scope != "GlobalVariable") return null;
        return (string?)children.FirstOrDefault(child => child.Name.LocalName == "Symbol")?.Elements().FirstOrDefault(child => child.Name.LocalName == "Component")?.Attribute("Name");
    }

    private static string? Absolute(XElement address)
    {
        string? area = (string?)address.Attribute("Area") switch { "Input" => "I", "Output" => "Q", _ => null };
        if (area is null || !int.TryParse((string?)address.Attribute("BitOffset"), NumberStyles.None, CultureInfo.InvariantCulture, out int bits)) return null;
        string type = (string?)address.Attribute("Type") ?? "Bool";
        return type == "Bool" ? $"%{area}{bits / 8}.{bits % 8}" : $"%{area}{(type == "Byte" ? "B" : type is "Word" or "Int" ? "W" : "D")}{bits / 8}";
    }

    // A wire lists its source first: an operand that leads its wire feeds a pin, one that follows is driven by it.
    private static void Graphical(XElement network, string block, int number, List<Use> uses)
    {
        var operands = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in network.Descendants())
            if (element.Name.LocalName == "Access" && (string?)element.Attribute("UId") is { } access && Operand(element) is { } operand) operands[access] = operand;
            else if (element.Name.LocalName == "Part" && (string?)element.Attribute("UId") is { } part) parts[part] = (string?)element.Attribute("Name") ?? "";
        foreach (var wire in network.Descendants().Where(element => element.Name.LocalName == "Wire"))
        {
            var ends = wire.Elements().ToArray();
            for (int index = 0; index < ends.Length; index++)
            {
                if (ends[index].Name.LocalName != "IdentCon" || (string?)ends[index].Attribute("UId") is not { } id || !operands.TryGetValue(id, out var operand)) continue;
                foreach (var pin in ends.Where(end => end.Name.LocalName == "NameCon"))
                {
                    bool coil = parts.TryGetValue((string?)pin.Attribute("UId") ?? "", out var part) && WritingParts.Contains(part) && (string?)pin.Attribute("Name") == "operand";
                    uses.Add(new Use(block, number, coil || index > 0, operand));
                }
            }
        }
    }

    private static bool Trivia(XElement element) => element.Name.LocalName is "Blank" or "NewLine" or "LineComment" or "Comment";

    private static void Structured(XElement network, string block, int number, List<Use> uses)
    {
        foreach (var access in network.Descendants().Where(element => element.Name.LocalName == "Access"))
        {
            if (Operand(access) is not { } operand) continue;
            string? after = (string?)access.ElementsAfterSelf().FirstOrDefault(element => !Trivia(element))?.Attribute("Text");
            string? before = (string?)access.ElementsBeforeSelf().LastOrDefault(element => !Trivia(element))?.Attribute("Text");
            // Assigned when ':=' follows it as a statement target, or when it receives a call output ('=>').
            bool target = after is not null && AssignmentTokens.Contains(after) && access.Parent?.Name.LocalName != "Parameter";
            bool output = before == "=>";
            uses.Add(new Use(block, number, target || output, operand));
            // A compound assignment also reads its target.
            if (target && after != ":=") uses.Add(new Use(block, number, false, operand));
        }
    }

    /// <summary>The report: one row per I/Q tag with its reading and writing blocks, plus absolute accesses that bypass the tags.</summary>
    internal static JsonObject Report(IReadOnlyList<IoMap.Tag> tags, IReadOnlyList<Use> uses, bool unusedOnly)
    {
        var byOperand = uses.ToLookup(use => use.Operand, StringComparer.Ordinal);
        var rows = new JsonArray();
        int unused = 0, readOnly = 0, writeOnly = 0;
        foreach (var tag in tags.OrderBy(tag => tag.Area).ThenBy(tag => tag.Byte).ThenBy(tag => tag.Bit ?? -1).ThenBy(tag => tag.Name, StringComparer.Ordinal))
        {
            var found = byOperand[tag.Name].ToArray();
            string[] Blocks(bool write) => [.. found.Where(use => use.Write == write).Select(use => use.Block + " N" + use.Network).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            string[] readers = Blocks(false), writers = Blocks(true);
            if (found.Length == 0) unused++;
            else if (writers.Length == 0) readOnly++;
            else if (readers.Length == 0) writeOnly++;
            if (unusedOnly && found.Length > 0) continue;
            var row = new JsonObject { ["address"] = tag.LogicalAddress, ["name"] = tag.Name };
            if (readers.Length > 0) row["readers"] = new JsonArray([.. readers.Select(reader => (JsonNode)reader)]);
            if (writers.Length > 0) row["writers"] = new JsonArray([.. writers.Select(writer => (JsonNode)writer)]);
            // An input written or an output written from several blocks is worth a look, not necessarily wrong.
            if (tag.Area == 'I' && writers.Length > 0) row["note"] = "input written by the program";
            else if (tag.Area == 'Q' && found.Where(use => use.Write).Select(use => use.Block).Distinct(StringComparer.Ordinal).Count() > 1) row["note"] = "output written by several blocks";
            rows.Add(row);
        }
        var absolute = uses.Where(use => use.Operand.StartsWith('%')).GroupBy(use => use.Operand, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (JsonNode)new JsonObject { ["address"] = group.Key,
                ["blocks"] = new JsonArray([.. group.Select(use => use.Block + " N" + use.Network + (use.Write ? " (write)" : " (read)")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(text => (JsonNode)text)]) });
        return new JsonObject
        {
            ["summary"] = new JsonObject { ["ioTags"] = tags.Count, ["unused"] = unused, ["readOnly"] = readOnly, ["writeOnly"] = writeOnly, ["readAndWritten"] = tags.Count - unused - readOnly - writeOnly },
            ["tags"] = rows,
            ["absoluteAccesses"] = new JsonArray([.. absolute])
        };
    }
}
