#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // tag_table_import_xml's row form: {tagTableName, tags:[{name, dataType, logicalAddress, comment?}]} becomes the SimaticML the
    // XML form takes, so both share one import, readback and restore path. Rows are merged into the table's own native export, so
    // members the rows do not name stay as exported. The hardware cross-check is advisory: it never refuses an import.
    internal static class TagTableRowRules
    {
        internal const int MaxRows = 2000;

        internal sealed class Row
        {
            internal string Name, DataType, LogicalAddress, Comment;
        }

        // International (I/Q/M) and German (E/A) mnemonics. A native export writes the international form with %, and the import's
        // readback compares text, so every row address is normalized to it.
        private static readonly Regex Address = new Regex(@"^%?(?<area>[IQMEA])(?:(?<byte>\d{1,6})\.(?<bit>[0-7])|(?<size>[BWDL])(?<start>\d{1,6}))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Elementary types by width in bits, in the spelling a native export uses. Other type names (PLC data types) pass unchanged.
        private static readonly Dictionary<string, (string Name, int Bits)> Elementary = new[]
        {
            ("Bool", 1), ("Byte", 8), ("Char", 8), ("SInt", 8), ("USInt", 8),
            ("Word", 16), ("Int", 16), ("UInt", 16), ("WChar", 16), ("Date", 16), ("S5Time", 16),
            ("DWord", 32), ("DInt", 32), ("UDInt", 32), ("Real", 32), ("Time", 32), ("Time_Of_Day", 32),
            ("LWord", 64), ("LInt", 64), ("ULInt", 64), ("LReal", 64), ("LTime", 64), ("LTime_Of_Day", 64), ("Date_And_LTime", 64)
        }.ToDictionary(type => type.Item1, type => (type.Item1, type.Item2), StringComparer.OrdinalIgnoreCase);

        internal static string NormalizeAddress(string text)
        {
            var match = Address.Match(text?.Trim() ?? "");
            if (!match.Success) return null;
            char area = char.ToUpperInvariant(match.Groups["area"].Value[0]);
            if (area == 'E') area = 'I';
            if (area == 'A') area = 'Q';
            return match.Groups["byte"].Success
                ? "%" + area + int.Parse(match.Groups["byte"].Value, CultureInfo.InvariantCulture) + "." + match.Groups["bit"].Value
                : "%" + area + char.ToUpperInvariant(match.Groups["size"].Value[0]) + int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>The address width in bits of a normalized absolute address: 1 for a bit, else 8, 16, 32 or 64.</summary>
        private static int AddressBits(string normalized) =>
            normalized.Contains(".") ? 1 : "BWDL".IndexOf(normalized[2]) switch { 0 => 8, 1 => 16, 2 => 32, _ => 64 };

        internal static Row[] Rows(JToken tags)
        {
            if (!(tags is JArray array) || array.Count < 1 || array.Count > MaxRows)
                throw new BridgeException("InvalidRequest", "tags must be an array of 1–" + MaxRows + " rows {name, dataType, logicalAddress, comment?}.");
            var rows = new List<Row>();
            foreach (var item in array)
            {
                if (!(item is JObject row) || row.Properties().Any(p => p.Name != "name" && p.Name != "dataType" && p.Name != "logicalAddress" && p.Name != "comment"))
                    throw new BridgeException("InvalidRequest", "Every tag row is an object with only name, dataType, logicalAddress and optional comment.");
                string name = Text(row, "name", 128, false), type = Text(row, "dataType", 128, false), address = Text(row, "logicalAddress", 32, false);
                string normalized = NormalizeAddress(address)
                    ?? throw new BridgeException("InvalidRequest", "Tag " + name + ": logicalAddress " + address + " is not an absolute I, Q or M bit, byte, word, double-word or long-word address.");
                rows.Add(new Row { Name = name, DataType = Elementary.TryGetValue(type, out var known) ? known.Name : type, LogicalAddress = normalized, Comment = Text(row, "comment", 1024, true) });
            }
            if (rows.Select(row => row.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
                throw new BridgeException("InvalidRequest", "Tag row names must be unique (case-insensitive).");
            return rows.ToArray();
        }

        private static string Text(JObject row, string name, int max, bool optional)
        {
            var token = row[name];
            if (token == null || token.Type == JTokenType.Null) { if (optional) return null; throw new BridgeException("InvalidRequest", "Every tag row needs " + name + "."); }
            string value = token.Type == JTokenType.String ? ((string)token).Trim() : null;
            if (string.IsNullOrEmpty(value) || value.Length > max || value.Any(char.IsControl))
                throw new BridgeException("InvalidRequest", "Tag row " + name + " must be a nonempty string of at most " + max + " characters without control characters.");
            return value;
        }

        /// <summary>The import document: the table's own export with the rows merged in, or a new table holding only the rows.
        /// A row naming an existing tag sets its data type, address and (when given) comment; any other member stays as exported.</summary>
        internal static XDocument Document(string tableName, XDocument existing, Row[] rows, string commentCulture, out int added, out int updated)
        {
            added = updated = 0;
            if (rows.Any(row => row.Comment != null) && string.IsNullOrWhiteSpace(commentCulture))
                throw new BridgeException("InvalidRequest", "Tag comments need commentCulture (for example en-US), an active project language.");
            var document = existing != null ? new XDocument(existing)
                : new XDocument(new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")),
                    new XElement("SW.Tags.PlcTagTable", new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", tableName)))));
            var table = document.Root?.Elements("SW.Tags.PlcTagTable").SingleOrDefault()
                ?? throw new BridgeException("InvalidSimaticML", "The exported tag table is not a single SW.Tags.PlcTagTable document.");
            var objects = table.Element("ObjectList");
            if (objects == null) table.Add(objects = new XElement("ObjectList"));
            int nextId = document.Descendants().Attributes("ID").Select(id => int.TryParse(id.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : 0)
                .DefaultIfEmpty(0).Max() + 1;
            string NewId() => (nextId++).ToString("X", CultureInfo.InvariantCulture);
            var members = objects.Elements().Where(member => member.Name != "MultilingualText")
                .ToDictionary(member => (string)member.Element("AttributeList")?.Element("Name") ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (members.TryGetValue(row.Name, out var tag))
                {
                    if (tag.Name != "SW.Tags.PlcTag") throw new BridgeException("TagScopeMismatch", "A tag row names an existing constant of the table: " + row.Name);
                    var attributes = tag.Element("AttributeList");
                    attributes.SetElementValue("DataTypeName", row.DataType);
                    attributes.SetElementValue("LogicalAddress", row.LogicalAddress);
                    updated++;
                }
                else
                {
                    objects.Add(tag = new XElement("SW.Tags.PlcTag", new XAttribute("ID", NewId()), new XAttribute("CompositionName", "Tags"),
                        new XElement("AttributeList", new XElement("DataTypeName", row.DataType), new XElement("LogicalAddress", row.LogicalAddress), new XElement("Name", row.Name))));
                    added++;
                }
                if (row.Comment == null) continue;
                var tagObjects = tag.Element("ObjectList");
                if (tagObjects == null) tag.Add(tagObjects = new XElement("ObjectList"));
                var comment = tagObjects.Elements("MultilingualText").FirstOrDefault(text => (string)text.Attribute("CompositionName") == "Comment");
                if (comment == null) tagObjects.Add(comment = new XElement("MultilingualText", new XAttribute("ID", NewId()), new XAttribute("CompositionName", "Comment")));
                var items = comment.Element("ObjectList");
                if (items == null) comment.Add(items = new XElement("ObjectList"));
                var item = items.Elements("MultilingualTextItem").FirstOrDefault(entry => (string)entry.Element("AttributeList")?.Element("Culture") == commentCulture);
                if (item != null) item.Element("AttributeList").SetElementValue("Text", row.Comment);
                else items.Add(new XElement("MultilingualTextItem", new XAttribute("ID", NewId()), new XAttribute("CompositionName", "Items"),
                    new XElement("AttributeList", new XElement("Culture", commentCulture), new XElement("Text", row.Comment))));
            }
            return document;
        }

        /// <summary>Compares every imported tag on an absolute I/O address with the configured I/O address ranges (ioType, start byte,
        /// length in bits, hardware path). Advisory: findings and an unreadable hardware configuration are reported, never thrown.</summary>
        internal static JObject CrossCheck(XDocument document, Func<IEnumerable<(string IoType, int StartByte, int LengthBits, string Path)>> configuredRanges)
        {
            const int maxFindings = 100;
            var findings = new JArray();
            int total = 0, ioTags = 0;
            void Report(string tag, string address, string finding, string detail)
            {
                total++;
                if (findings.Count < maxFindings) findings.Add(new JObject { ["tag"] = tag, ["logicalAddress"] = address, ["finding"] = finding, ["detail"] = detail });
            }
            var tags = document.Descendants("SW.Tags.PlcTag").Select(tag => tag.Element("AttributeList")).Where(attributes => attributes != null)
                .Select(attributes => (Name: (string)attributes.Element("Name"), Type: (string)attributes.Element("DataTypeName"), Address: (string)attributes.Element("LogicalAddress"))).ToArray();
            (string IoType, int StartByte, int LengthBits, string Path)[] ranges = null;
            string unavailable = null;
            foreach (var tag in tags)
            {
                string normalized = NormalizeAddress(tag.Address);
                if (normalized != null && tag.Type != null && Elementary.TryGetValue(tag.Type.Trim('"'), out var type) && type.Bits != AddressBits(normalized))
                    Report(tag.Name, tag.Address, "dataTypeWidthMismatch", type.Name + " is " + type.Bits + " bit wide; the address is " + AddressBits(normalized) + " bit wide.");
                var io = TagAddressRules.Io(tag.Address);
                if (io == null) continue;
                ioTags++;
                if (unavailable != null) continue;
                if (ranges == null)
                    try { ranges = configuredRanges().Where(range => range.LengthBits > 0).ToArray(); }
                    catch (Exception error) when (!(error is OutOfMemoryException)) { unavailable = error.Message; continue; }
                var (ioType, start, length) = io.Value;
                var hits = ranges.Where(range => string.Equals(range.IoType, ioType, StringComparison.Ordinal) &&
                    start < range.StartByte * 8 + range.LengthBits && range.StartByte * 8 < start + length).ToArray();
                if (hits.Length == 0) { Report(tag.Name, tag.Address, "noConfiguredAddress", "No configured " + ioType.ToLowerInvariant() + " address range of this PLC contains the address."); continue; }
                bool covered = Enumerable.Range(start, length).All(bit => hits.Any(range => bit >= range.StartByte * 8 && bit < range.StartByte * 8 + range.LengthBits));
                if (!covered) Report(tag.Name, tag.Address, "partlyConfigured", "Only part of the address lies in a configured range: " + string.Join(", ", hits.Select(range => range.Path).Distinct(StringComparer.Ordinal)));
            }
            return new JObject
            {
                ["status"] = unavailable != null ? "unavailable" : total > 0 ? "findings" : "passed",
                ["blocking"] = false,
                ["tagCount"] = tags.Length, ["ioTagCount"] = ioTags, ["configuredRangeCount"] = ranges?.Length,
                ["findingCount"] = total, ["findingsTruncated"] = total > findings.Count, ["findings"] = findings,
                ["unavailableReason"] = unavailable,
                ["scope"] = "Absolute I and Q tag addresses against the I/O address ranges of configured hardware this PLC owns or may own, and elementary data type width against address width. It does not show that a signal is wired to that address."
            };
        }

        /// <summary>The preview warning for a cross-check that did not pass, or null.</summary>
        internal static string Warning(JObject crossCheck) => crossCheck.Value<string>("status") switch
        {
            "findings" => "Hardware cross-check: " + crossCheck.Value<int>("findingCount") + " finding(s) on the imported tags (preview.hardwareCrossCheck.findings). The check is advisory and does not block the import; review the findings before change_apply.",
            "unavailable" => "Hardware cross-check could not read the hardware configuration (" + crossCheck.Value<string>("unavailableReason") + "). The import is not blocked; I/O addresses were not compared with configured modules.",
            _ => null
        };
    }
}
