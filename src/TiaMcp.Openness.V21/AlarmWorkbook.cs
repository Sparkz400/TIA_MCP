#nullable disable
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Siemens-free, layout-agnostic reader for native alarm XLSX exports. Cells keep
    // their exported text by column; the Siemens column layout is not interpreted.
    internal static class AlarmWorkbook
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        internal const int MaxCellCharacters = 4096;

        // take() is charged once per row; a refused row stops that sheet and marks it truncated.
        internal static JArray Read(string path, Func<bool> take)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Workbook exceeds 16 MiB.");
            using (var zip = ZipFile.OpenRead(path))
            {
                Func<string, XElement> part = name =>
                {
                    var entry = zip.GetEntry(name);
                    if (entry == null) return null;
                    using (var stream = entry.Open())
                    using (var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32L * 1024 * 1024 }))
                        return XElement.Load(xml);
                };
                var strings = (part("xl/sharedStrings.xml")?.Elements(S + "si") ?? Enumerable.Empty<XElement>()).Select(Text).ToArray();
                var targets = (part("xl/_rels/workbook.xml.rels")?.Elements() ?? Enumerable.Empty<XElement>())
                    .ToDictionary(e => (string)e.Attribute("Id") ?? "", e => (string)e.Attribute("Target") ?? "", StringComparer.Ordinal);
                var sheets = new JArray();
                foreach (var sheet in part("xl/workbook.xml")?.Element(S + "sheets")?.Elements(S + "sheet") ?? throw new InvalidDataException("Workbook has no sheet list."))
                {
                    if (!targets.TryGetValue((string)sheet.Attribute(R + "id") ?? "", out string target)) throw new InvalidDataException("Worksheet relationship is missing.");
                    var data = part(target.StartsWith("/", StringComparison.Ordinal) ? target.Substring(1) : "xl/" + target)?.Element(S + "sheetData")
                        ?? throw new InvalidDataException("Worksheet data is missing.");
                    var rows = new JArray();
                    var info = new JObject { ["name"] = (string)sheet.Attribute("name"), ["rows"] = rows, ["rowsTruncated"] = false, ["cellTextTruncated"] = false };
                    sheets.Add(info);
                    foreach (var row in data.Elements(S + "row"))
                    {
                        if (!take()) { info["rowsTruncated"] = true; break; }
                        var cells = new JObject();
                        int position = 0;
                        foreach (var cell in row.Elements(S + "c"))
                        {
                            position++;
                            string type = (string)cell.Attribute("t"), value = (string)cell.Element(S + "v");
                            string text = type == "s" ? strings[int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)] : type == "inlineStr" ? Text(cell.Element(S + "is")) : value;
                            if (text != null && text.Length > MaxCellCharacters) { text = text.Substring(0, MaxCellCharacters); info["cellTextTruncated"] = true; }
                            string address = (string)cell.Attribute("r");
                            cells[address == null ? "#" + position : new string(address.TakeWhile(char.IsLetter).ToArray())] = text;
                        }
                        rows.Add(new JObject { ["row"] = (int?)row.Attribute("r"), ["cells"] = cells });
                    }
                }
                return sheets;
            }
        }

        private static string Text(XElement item) => item == null ? null :
            string.Concat(item.Elements(S + "t").Concat(item.Elements(S + "r").Elements(S + "t")).Select(t => t.Value));
    }
}
