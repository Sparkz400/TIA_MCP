using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

internal static class PreservationChecks
{
    internal static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action run) => tests.Add(("VCI preservation " + name, run));
        var oracle = new VciBlockCommentOracle("Motor", "FC", 1, [new("de-DE", "Deutsch {0}"), new("en-GB", "English {0}")], new('a', 64));
        string Xml(params VciLanguageText[] comments)
        {
            var items = comments.Select(c => new XElement("MultilingualTextItem", new XElement("AttributeList",
                new XElement("Culture", c.Culture), new XElement("Text", c.Text))));
            var text = new XElement("MultilingualText", new XAttribute("CompositionName", "Comment"), new XElement("ObjectList", items));
            var block = new XElement("SW.Blocks.FC", new XAttribute("ID", "0"),
                new XElement("AttributeList", new XElement("Name", "Motor"), new XElement("Number", "1")), new XElement("ObjectList", text));
            return new XDocument(new XElement("Document", block)).ToString();
        }
        VciCommentPreservationReport Review(string xml, VciBlockCommentOracle? expected = null) =>
            VciBlockCommentPreservation.Inspect("SimaticML", expected ?? oracle, "fixture.xml", [new("fixture.xml", "document")],
                [new("fixture.xml", Encoding.UTF8.GetBytes(xml))]);
        Test("exact multilingual comment coverage is scoped and never native import acceptance", () =>
        {
            var r = Review(Xml(oracle.Comments.ToArray()));
            True(r.RequestedCommentsPreserved); True(!r.NativeImportQualified); Equal("exactBlockCommentCulturesOnly", r.Scope);
        });
        Test("lost secondary language cannot become passing native green", () =>
        {
            var r = Review(Xml(oracle.Comments[0])); True(!r.RequestedCommentsPreserved); True(r.Issues.Contains("commentMissingOrChanged:en-GB"));
        });
        Test("changed placeholders and empty translated text fail exact comparison", () =>
        {
            True(!Review(Xml(oracle.Comments[0], new("en-GB", "English {1}"))).RequestedCommentsPreserved);
            True(!Review(Xml(oracle.Comments[0], new("en-GB", ""))).RequestedCommentsPreserved);
        });
        Test("unknown extra cultures require explicit review", () => True(!Review(Xml([.. oracle.Comments, new("nl-NL", "Extra")])).RequestedCommentsPreserved));
        Test("duplicate native cultures are ambiguous", () => Reject("VciInvalidNativeDocument", () => Review(Xml([.. oracle.Comments, oracle.Comments[0]]))));
        Test("wrong native kind name or number refuses apparent text equality", () =>
        {
            foreach (string text in new[] { Xml(oracle.Comments.ToArray()).Replace("SW.Blocks.FC", "SW.Blocks.FB"),
                Xml(oracle.Comments.ToArray()).Replace("Motor", "Other"), Xml(oracle.Comments.ToArray()).Replace("<Number>1", "<Number>2") })
                Reject("VciNativeIdentityMismatch", () => Review(text));
        });
        Test("external entity XML cannot read local or network input", () => Reject("VciInvalidNativeDocument", () => Review("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///C:/secret'>]><Document>&x;</Document>")));
        Test("rich XML text representation remains unqualified", () => Reject("VciInvalidNativeDocument", () => Review(Xml(oracle.Comments.ToArray()).Replace("Deutsch {0}", "<Run>Deutsch {0}</Run>"))));
        Test("missing resources and unknown auxiliary bytes remain explicit", () =>
        {
            var r = VciBlockCommentPreservation.Inspect("SimaticSD", oracle, "fixture.s7dcl",
                [new("fixture.s7dcl", "source"), new("fixture.s7res", "resources"), new("extra.bin", "auxiliary")],
                [new("fixture.s7dcl", Encoding.UTF8.GetBytes("FUNCTION Motor : Bool")), new("extra.bin", new byte[] { 0, 255 })]);
            True(!r.RequestedCommentsPreserved); True(r.MissingFiles.Contains("fixture.s7res")); Equal(2, r.Files.Count);
        });
        Test("SD cannot evade resource requirement by declaring a different package", () =>
        {
            var r = VciBlockCommentPreservation.Inspect("SimaticSD", oracle, "fixture.s7dcl", [new("fixture.s7dcl", "document")], [new("fixture.s7dcl", new byte[] { 65 })]);
            True(r.Issues.Contains("simaticSdResourceRequirementMissing")); True(!r.RequestedCommentsPreserved);
        });
        Test("SCL does not acquire language qualification from matching tokens", () =>
        {
            var r = VciBlockCommentPreservation.Inspect("SCL", oracle, "fixture.scl", [new("fixture.scl", "source")],
                [new("fixture.scl", Encoding.UTF8.GetBytes("Deutsch {0} English {0}"))]);
            True(!r.RequestedCommentsPreserved); True(r.Issues.Contains("blockCommentLanguageGrammarUnqualified:SCL"));
        });
        Test("an unreviewed companion cannot hide behind exact comments", () =>
        {
            var r = VciBlockCommentPreservation.Inspect("SimaticML", oracle, "fixture.xml", [new("fixture.xml", "document")],
                [new("fixture.xml", Encoding.UTF8.GetBytes(Xml(oracle.Comments.ToArray()))), new("opaque.bin", new byte[] { 1 })]);
            True(!r.RequestedCommentsPreserved); True(r.Issues.Contains("unreviewedCompanion:opaque.bin")); Equal(2, r.Files.Count);
        });
        Test("native identity duplicate fields and disguised root are rejected", () =>
        {
            string valid = Xml(oracle.Comments.ToArray());
            foreach (string input in new[] { valid.Replace("<Name>Motor</Name>", "<Name>Motor</Name><Name>Other</Name>"),
                valid.Replace("<Number>1</Number>", "<Number>1</Number><Number>2</Number>"),
                valid.Replace("Document", "Envelope"), valid.Replace("<Name>Motor</Name>", "<Name><Text>Motor</Text></Name>") })
                Reject("VciInvalidNativeDocument", () => Review(input));
        });
        Test("ambiguous duplicate compositions and text fields are rejected", () =>
        {
            foreach (string parent in new[] { "SW.Blocks.FC", "MultilingualText", "MultilingualTextItem" })
            {
                var doc = XDocument.Parse(Xml(oracle.Comments.ToArray()));
                var target = doc.Descendants(parent).First();
                string child = parent == "MultilingualText" ? "ObjectList" : "AttributeList";
                target.Add(new XElement(target.Element(child)!));
                Reject("VciInvalidNativeDocument", () => Review(doc.ToString()));
            }
            var duplicateText = XDocument.Parse(Xml(oracle.Comments.ToArray()));
            duplicateText.Descendants("Text").First().AddAfterSelf(new XElement("Text", "Other"));
            Reject("VciInvalidNativeDocument", () => Review(duplicateText.ToString()));
            var hiddenItem = XDocument.Parse(Xml(oracle.Comments.ToArray()));
            hiddenItem.Descendants("MultilingualText").First().Element("ObjectList")!.Add(new XElement("UnknownText", "hidden"));
            Reject("VciInvalidNativeDocument", () => Review(hiddenItem.ToString()));
        });
        RetainedPackageChecks.Register(tests);
    }

    internal static void ReviewRetained(string nativeRoot, string output)
    {
        string seedPath = Path.Combine(nativeRoot, "fixture-seed-01", "result.json");
        var seed = JsonNode.Parse(File.ReadAllText(seedPath))!;
        string seedHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(seedPath)));
        string resultPath = Path.Combine(nativeRoot, "vci-03", "result.json");
        var native = JsonNode.Parse(File.ReadAllText(resultPath))!;
        var reports = new List<VciCommentPreservationReport>();
        foreach (var row in native["vci"]!["profiles"]!.AsArray())
        {
            string block = (string)row!["block"]!;
            var seedRow = seed["fixtureOracle"]!.AsArray().SingleOrDefault(x => (string)x!["block"]! == block);
            if (seedRow is null) continue; // No independently authored comment oracle exists for OB/DB.
            string format = (string)row["format"]!;
            string directory = Path.GetFullPath((string)row["mappedDirectory"]!);
            string expectedParent = Path.GetFullPath(Path.Combine(nativeRoot, "vci-03", "workspaces"));
            Equal(expectedParent, Path.GetDirectoryName(directory)!);
            var files = new List<VciFileInput>();
            foreach (var artifact in row["artifacts"]!.AsArray())
            {
                string path = Path.GetFullPath((string)artifact!["path"]!);
                True(path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                byte[] bytes = File.ReadAllBytes(path);
                Equal((string)artifact["sha256"]!, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Equal((long)artifact["bytes"]!, bytes.LongLength);
                files.Add(new(Path.GetRelativePath(directory, path).Replace('\\', '/'), bytes));
            }
            var actual = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p).Replace('\\', '/')).Order().ToArray();
            True(actual.SequenceEqual(files.Select(f => f.RelativePath).Order()));
            string primary = files.Single(f => !f.RelativePath.StartsWith(".vci/")).RelativePath;
            var required = files.Select(f => new VciExpectedFile(f.RelativePath, f.RelativePath == primary ? "document" : "auxiliary")).ToList();
            if (format == "SimaticSD") required.Add(new("fixture.s7res", "resources"));
            var oracle = new VciBlockCommentOracle(block, block == "TiaMcp_IsEnabled" ? "FC" : "FB", 1,
                [new("de-DE", (string)seedRow["de-DE"]!), new("en-GB", (string)seedRow["en-GB"]!)], seedHash);
            var report = VciBlockCommentPreservation.Inspect(format, oracle, primary, required, files);
            Equal(format == "SimaticML", report.RequestedCommentsPreserved); True(!report.NativeImportQualified); reports.Add(report);
        }
        Equal(6, reports.Count);
        File.WriteAllText(output, JsonSerializer.Serialize(new { retainedNativeResultSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(resultPath))),
            nativeCalls = 0, scope = "twoSeededBlocksThreeFormats", reports, nativeImportEnabled = false }, BridgeProtocol.Json));
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
