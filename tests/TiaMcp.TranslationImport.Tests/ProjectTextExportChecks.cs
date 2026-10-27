using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Translations;
using static TiaMcp.Translations.TranslationJson;

// Host-side project_text_export reading: header cultures, multi-target layout and key comparison. Synthetic workbooks only.
internal static class ProjectTextExportChecks
{
    internal static void Register(Action<string, Func<Task>> test, string output)
    {
        static void True(bool value, string message) { if (!value) throw new Exception(message); }
        test("native profile reads header cultures and keeps the bilingual profile unchanged", () => {
            var bilingual = TranslationWorkerAdapter.NativeProfile(Book(["de-DE", "en-GB"], [Values("K-1", "<BlockCommentCategoryData>", 1)]));
            True(bilingual.Id == "tia21-whole-project-bilingual-qualification-v1" && bilingual.SourceCulture == "de-DE" &&
                bilingual.Rows.Single().Targets.Single() is { Culture: "en-GB", Location.Cell: "F2" }, "Bilingual profile changed");
            var multi = TranslationWorkerAdapter.NativeProfile(Book(["en-US", "de-DE", "fr-FR"], [Values("K-1", "<BlockCommentCategoryData>", 2)]));
            True(multi.SourceCulture == "en-US" && multi.Rows[0].Targets.Select(t => t.Culture + "@" + t.Location.Cell).SequenceEqual(["de-DE@F2", "fr-FR@G2"]), "Multi-target columns misread");
            return Task.CompletedTask;
        });
        test("filtered multi-target export is compared by key with bounded leaked and dropped rows", () => {
            string root = Path.Combine(output, "project-text-" + Guid.NewGuid().ToString("N")), directory = Path.Combine(root, "translation-native", "export-1");
            Directory.CreateDirectory(directory);
            string[] cultures = ["en-US", "de-DE", "fr-FR"];
            string[][] full = [Values("K-1", "<BlockCommentCategoryData>", 2), Values("K-2", "<HwcnCommentTextCategoryData>", 2),
                Values("K-3", "<BlockCommentCategoryData>", 2), Values("K-4", "<HwcnCommentTextCategoryData>", 2)];
            JsonObject Workbook(string role, string[][] rows)
            {
                byte[] bytes = Book(cultures, rows); string path = Path.Combine(directory, role + ".xlsx"); File.WriteAllBytes(path, bytes);
                return new() { ["role"] = role, ["artifact"] = new JsonObject { ["path"] = path, ["sha256"] = Sha256(bytes) } };
            }
            var worker = new JsonObject { ["projectPath"] = Path.Combine(root, "Fixture.ap21"), ["projectName"] = "Fixture",
                ["requestedCategoryIds"] = new JsonArray("BlockCommentCategoryData"),
                ["categories"] = new JsonArray(new JsonObject { ["identifier"] = "BlockCommentCategoryData" }, new JsonObject { ["identifier"] = "HwcnCommentTextCategoryData" }),
                ["workbooks"] = new JsonArray(Workbook("full", full), Workbook("filtered", [full[0], full[1], full[3]])) };
            var report = ProjectTextExportReport.Build(root, JsonDocument.Parse(worker.ToJsonString()).RootElement, 1);
            var comparison = report["comparison"]!;
            string[] Keys(string name) => comparison[name]!["keys"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
            True(comparison["status"]!.GetValue<string>() == "compared" && comparison["leaked"]!["count"]!.GetValue<int>() == 2 &&
                comparison["leaked"]!["truncated"]!.GetValue<bool>() && Keys("leaked").SequenceEqual(["K-2"]) && Keys("dropped").SequenceEqual(["K-3"]) &&
                comparison["changed"]!["count"]!.GetValue<int>() == 0 && !comparison["matchesRequestedCategories"]!.GetValue<bool>(), "Key comparison differs");
            string retained = comparison["artifact"]!["path"]!.GetValue<string>();
            True(Sha256(File.ReadAllBytes(retained)) == comparison["artifact"]!["sha256"]!.GetValue<string>() &&
                report["workbooks"]![0]!["rows"]!.GetValue<int>() == 4 && !report["nativeImportAllowed"]!.GetValue<bool>(), "Retained comparison or row summary differs");
            return Task.CompletedTask;
        });
    }

    private static string[] Values(string key, string category, int targets) =>
        new[] { category, "Fixture\\PLC\\" + key, key, "", "Source " + key }.Concat(Enumerable.Range(1, targets).Select(i => "Target " + i + " " + key)).ToArray();

    // Shared-string layout as observed in V21 exports: User/System Texts, culture headers, then ref="0".."15".
    private static byte[] Book(string[] cultures, string[][] rows)
    {
        XNamespace s = Fixture.S, r = "http://schemas.openxmlformats.org/package/2006/relationships", d = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var strings = new List<string>();
        XElement Cell(string address, string text)
        {
            int index = strings.IndexOf(text); if (index < 0) { index = strings.Count; strings.Add(text); }
            return new(s + "c", new XAttribute("r", address), new XAttribute("t", "s"), new XElement(s + "v", index));
        }
        XElement Row(int number, IEnumerable<string> values) => new(s + "row", new XAttribute("r", number), values.Select((v, i) => Cell(TranslationWorkbook.ColumnName(i + 1) + number, v)));
        var header = new[] { "Category", "ViewPath", "Internal ID", "Substitutions", cultures[0] + "*" }.Concat(cultures.Skip(1)).Concat(Enumerable.Range(0, 16).Select(n => "ref=\"" + n + "\"")).ToArray();
        var parts = new Dictionary<string, byte[]>();
        void Part(string name, XElement xml) => parts.Add(name, Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)));
        Part("xl/worksheets/sheet1.xml", new(s + "worksheet", new XElement(s + "sheetData", Row(1, header), rows.Select((v, i) => Row(i + 2, v)))));
        Part("xl/worksheets/sheet2.xml", new(s + "worksheet", new XElement(s + "sheetData", Row(1, header))));
        Part("xl/sharedStrings.xml", new(s + "sst", strings.Select(text => new XElement(s + "si", new XElement(s + "t", text)))));
        Part("xl/workbook.xml", new(s + "workbook", new XAttribute(XNamespace.Xmlns + "r", d), new XElement(s + "sheets",
            new[] { "User Texts", "System Texts" }.Select((n, i) => new XElement(s + "sheet", new XAttribute("name", n), new XAttribute("sheetId", i + 1), new XAttribute(d + "id", "rId" + (i + 1)))))));
        Part("xl/_rels/workbook.xml.rels", new(r + "Relationships", Enumerable.Range(1, 2).Select(i => new XElement(r + "Relationship",
            new XAttribute("Id", "rId" + i), new XAttribute("Type", d.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet" + i + ".xml")))));
        Part("_rels/.rels", new(r + "Relationships", new XElement(r + "Relationship", new XAttribute("Id", "root"), new XAttribute("Type", d.NamespaceName + "/officeDocument"), new XAttribute("Target", "xl/workbook.xml"))));
        XNamespace content = "http://schemas.openxmlformats.org/package/2006/content-types";
        Part("[Content_Types].xml", new(content + "Types", new XElement(content + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(content + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml"))));
        return Fixture.Pack(parts);
    }
}
