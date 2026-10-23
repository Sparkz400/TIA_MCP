using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

/// <summary>File-only gate for the exact retained V21 de-DE/en-GB export layout.</summary>
internal static class TranslationCategoryWorkbook
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";
    internal sealed class Row
    {
        internal string Key, Category, ViewPath, Substitutions, Source, Target, Sheet, Part;
        internal bool Same(Row other) => other != null && Key == other.Key && Category == other.Category &&
            ViewPath == other.ViewPath && Substitutions == other.Substitutions && Source == other.Source &&
            Target == other.Target && Sheet == other.Sheet && Part == other.Part;
    }

    internal static Dictionary<string, Row> Read(string path)
    {
        Require(new FileInfo(path).Length <= 8 * 1024 * 1024, "Workbook exceeds 8 MiB.");
        using (var zip = ZipFile.OpenRead(path))
        {
            string[] expectedParts = { "xl/workbook.xml", "_rels/.rels", "xl/styles.xml", "xl/_rels/workbook.xml.rels",
                "docProps/custom.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml", "xl/sharedStrings.xml", "[Content_Types].xml" };
            Require(zip.Entries.Count == expectedParts.Length && zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() == expectedParts.Length &&
                expectedParts.All(n => zip.GetEntry(n) != null) && zip.Entries.Sum(e => e.Length) <= 32 * 1024 * 1024, "Unrecognized or oversized workbook parts.");
            Func<string, XElement> read = name =>
            {
                using (var stream = zip.GetEntry(name).Open())
                using (var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                    return XElement.Load(xml);
            };
            var shared = read("xl/sharedStrings.xml");
            Require(shared.Name == S + "sst" && shared.Elements().All(e => e.Name == S + "si"), "Unrecognized shared strings.");
            var strings = shared.Elements().Select(si =>
            {
                Require(si.Elements().Count() == 1 && si.Element(S + "t") != null, "Rich shared string is outside this captured layout.");
                return si.Element(S + "t").Value;
            }).ToArray();
            var relationships = read("xl/_rels/workbook.xml.rels").Elements().ToArray();
            Require(relationships.All(e => e.Name == P + "Relationship") && relationships.Select(e => (string)e.Attribute("Id")).Distinct().Count() == relationships.Length,
                "Duplicate or unrecognized workbook relationships.");
            var links = relationships.ToDictionary(e => (string)e.Attribute("Id"), e => e);
            var workbook = read("xl/workbook.xml");
            Require(workbook.Name == S + "workbook" && workbook.Elements().Count(e => e.Name == S + "sheets") == 1, "Unrecognized workbook.");
            var sheets = workbook.Element(S + "sheets").Elements().ToArray();
            Require(sheets.Length == 2, "Both captured worksheets are required.");
            var result = new Dictionary<string, Row>(StringComparer.Ordinal);
            for (int sheetIndex = 0; sheetIndex < sheets.Length; sheetIndex++)
            {
                var sheet = sheets[sheetIndex];
                string name = sheetIndex == 0 ? "User Texts" : "System Texts";
                string part = "xl/worksheets/sheet" + (sheetIndex + 1) + ".xml";
                Require(sheet.Name == S + "sheet" && (string)sheet.Attribute("name") == name &&
                    ((string)sheet.Attribute("state") == null || (string)sheet.Attribute("state") == "visible"), "Renamed, hidden or reordered worksheet.");
                XElement link;
                Require(links.TryGetValue((string)sheet.Attribute(R + "id") ?? "", out link) &&
                    (string)link.Attribute("Target") == "/" + part && (string)link.Attribute("TargetMode") == null &&
                    (string)link.Attribute("Type") == R.NamespaceName + "/worksheet", "Worksheet target differs from the captured layout.");
                var root = read(part);
                Require(root.Name == S + "worksheet" && root.Elements(S + "sheetData").Count() == 1, "Missing or duplicate sheet data.");
                var rows = root.Element(S + "sheetData").Elements().ToArray();
                Require(rows.Length >= 1 && rows.Length <= 10001 && rows.All(e => e.Name == S + "row"), "Invalid worksheet rows.");
                for (int index = 0; index < rows.Length; index++)
                {
                    string number = (index + 1).ToString(CultureInfo.InvariantCulture);
                    Require((string)rows[index].Attribute("r") == number, "Missing, repeated or reordered worksheet row.");
                    var cells = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var cell in rows[index].Elements())
                    {
                        int stringIndex;
                        Require(cell.Name == S + "c" && (string)cell.Attribute("t") == "s" && cell.Elements().Count() == 1 && cell.Element(S + "v") != null &&
                            int.TryParse(cell.Element(S + "v").Value, NumberStyles.None, CultureInfo.InvariantCulture, out stringIndex) && stringIndex >= 0 && stringIndex < strings.Length,
                            "Only complete shared-string cells are accepted.");
                        string address = (string)cell.Attribute("r");
                        Require(address != null && !cells.ContainsKey(address), "Duplicate cell address.");
                        cells.Add(address, strings[int.Parse(cell.Element(S + "v").Value, CultureInfo.InvariantCulture)]);
                    }
                    int width = index == 0 ? 22 : 6;
                    Require(cells.Count == width && Enumerable.Range(0, width).All(i => cells.ContainsKey(((char)('A' + i)).ToString() + number)), "Incomplete worksheet row.");
                    if (index == 0)
                    {
                        string[] headers = new[] { "Category", "ViewPath", "Internal ID", "Substitutions", "de-DE*", "en-GB" }
                            .Concat(Enumerable.Range(0, 16).Select(i => "ref=\"" + i + "\"")).ToArray();
                        Require(Enumerable.Range(0, 22).All(i => cells[((char)('A' + i)).ToString() + number] == headers[i]), "Captured languages or headers differ.");
                        continue;
                    }
                    string key = cells["C" + number];
                    Require(key.Length > 0 && !result.ContainsKey(key), "Missing or duplicate native key.");
                    result.Add(key, new Row { Key = key, Category = cells["A" + number], ViewPath = cells["B" + number], Substitutions = cells["D" + number],
                        Source = cells["E" + number], Target = cells["F" + number], Sheet = name, Part = part });
                }
            }
            return result;
        }
    }

    internal static bool Equal(Dictionary<string, Row> left, Dictionary<string, Row> right) => left.Count == right.Count &&
        left.All(item => right.ContainsKey(item.Key) && item.Value.Same(right[item.Key]));

    internal static bool PopulatedMainControl(Dictionary<string, Row> plc, Dictionary<string, Row> main, string projectName, string plcName)
    {
        string prefix = projectName + "\\" + plcName + "\\Programmbausteine\\Main\\";
        return main.Count > 0 && main.All(item => plc.ContainsKey(item.Key) && item.Value.Same(plc[item.Key]) &&
            item.Value.ViewPath.StartsWith(prefix, StringComparison.Ordinal)) &&
            main.Values.Any(row => row.ViewPath == prefix + "Remanence" && row.Category == "<BlockCommentCategoryData>" && row.Source == "=True, if remanent data are available") &&
            main.Values.Any(row => row.ViewPath == prefix + "Initial_Call" && row.Category == "<BlockCommentCategoryData>" && row.Source == "Initial call of this OB");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
