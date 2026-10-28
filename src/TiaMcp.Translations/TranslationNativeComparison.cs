using System.Text;
using System.Xml;
using System.Xml.Linq;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

// Narrow qualification profile. Original bytes always remain retained. Nothing in
// this comparer enables native dispatch or treats arbitrary XML differences as text.
internal static class TranslationNativeComparison
{
    // ExportProjectTexts labels view paths in the TIA UI language; the German and English labels were observed natively.
    internal static bool MainCommentView(string viewPath, string projectFilePath)
    {
        string[] path = viewPath.Split('\\');
        return path.Length == 5 && path[0] == Path.GetFileNameWithoutExtension(projectFilePath) &&
            path[2] is "Programmbausteine" or "Program blocks" && path[3] == "Main" && path[4] is "Bausteinkommentar" or "Block comment";
    }

    internal static byte[] Peer(byte[] bytes, string profile, TranslationImportCandidate candidate)
    {
        if (profile == "exact") return bytes;
        Require(profile == "tia21-simaticml-translation-v1", "TranslationPeerProfileUnqualified", "Unknown peer comparison profile.");
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        Require(document.Root?.Name == "Document", "TranslationPeerProfileUnqualified", "Expected complete SimaticML document.");
        var created = document.Root!.Elements("DocumentInfo").Elements("Created").ToArray();
        Require(created.Length == 1 && !created[0].HasElements, "TranslationPeerProfileUnqualified", "Exact native creation metadata required.");
        created[0].Value = "";
        // The first profile admits Main OB1's block comment only. Match its full
        // captured view path and exact XML composition; equal text elsewhere is
        // never authority to normalize a different native object or field. Only the
        // approved cell's own culture leaf (target, or source in source-text mode) is normalized.
        var map = candidate.Baseline.Profile;
        foreach (var row in map.Rows)
        foreach (var target in row.Targets.Prepend(new TranslationCell(map.SourceCulture, row.SourceCell)))
        {
            var before = candidate.Baseline.ReadText(target.Location); var after = candidate.Candidate.ReadText(target.Location);
            if (before == after) continue;
            string view = candidate.Baseline.ReadText(new(row.SourceCell.Part, "B" + row.SourceCell.Cell[1..])).Text ?? "";
            Require(row.Category == "<BlockCommentCategoryData>" && MainCommentView(view, candidate.Baseline.Project.Object.ProjectPath),
                "TranslationPeerProfileUnqualified", "Only the captured Main OB1 block-comment target is admitted by this native peer profile.");
            var blocks = document.Root.Elements("SW.Blocks.OB").Where(b => b.Element("AttributeList")?.Element("Name")?.Value == "Main").ToArray();
            if (blocks.Length == 0) continue;
            Require(blocks.Length == 1 && blocks[0].Element("AttributeList")?.Element("Number")?.Value == "1",
                "TranslationPeerProfileUnqualified", "Exact Main OB1 XML identity required.");
            var nodes = blocks[0].Elements("ObjectList").Elements("MultilingualText").Where(m => (string?)m.Attribute("CompositionName") == "Comment")
                .Elements("ObjectList").Elements("MultilingualTextItem").Elements("AttributeList")
                .Where(a => a.Element("Culture")?.Value == target.Culture).Elements("Text").ToArray();
            Require(nodes.Length == 1 && !nodes[0].HasElements, "TranslationPeerProfileUnqualified", "Exact plain comment leaf required.");
            if (nodes[0].Value == before.Text || nodes[0].Value == after.Text)
                nodes[0].Value = "__approved_translation_" + Sha256(Encoding.UTF8.GetBytes(before.Text! + "\0" + after.Text!)) + "__";
        }
        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
    }
}
