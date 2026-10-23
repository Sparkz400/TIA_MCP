using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Core.VersionControl;

public sealed record VciLanguageText(string Culture, string Text);
public sealed record VciBlockCommentOracle(string BlockName, string BlockKind, int Number,
    IReadOnlyList<VciLanguageText> Comments, string EvidenceSha256);
public sealed record VciCommentPreservationReport(string NativeFormat, string BlockName, string OracleSha256,
    IReadOnlyList<VciFileEvidence> Files, IReadOnlyList<VciLanguageText> ObservedComments,
    IReadOnlyList<string> MissingFiles, IReadOnlyList<string> Issues, bool RequestedCommentsPreserved,
    bool NativeImportQualified = false, string Scope = "exactBlockCommentCulturesOnly");

/// <summary>Reviews retained bytes only. It cannot recover omitted languages or invent absent native companions.</summary>
public static class VciBlockCommentPreservation
{
    public static VciCommentPreservationReport Inspect(string nativeFormat, VciBlockCommentOracle oracle,
        string primaryPath, IReadOnlyList<VciExpectedFile> requiredFiles, IReadOnlyList<VciFileInput> files)
    {
        if (oracle is null || files is null || files.Count > 20000 || files.Any(x => x is null) ||
            files.Sum(x => (long)x.Bytes.Length) > 16777216)
            throw new BridgeException("VciLimitExceeded", "An oracle and a bounded retained file inventory are required.");
        files = files.Select(x => x with { Bytes = x.Bytes.ToArray() }).ToArray();
        VciGuard.Text(nativeFormat, "nativeFormat"); VciGuard.Text(oracle.BlockName, "blockName");
        VciGuard.Hash(oracle.EvidenceSha256); VciGuard.Path(primaryPath, 255);
        if (oracle.BlockKind is not ("FC" or "FB" or "OB" or "GlobalDB" or "InstanceDB") || oracle.Number < 0 ||
            oracle.Comments is null || oracle.Comments.Count is < 1 or > 256 || oracle.Comments.Any(x => x is null) ||
            oracle.Comments.Select(x => x.Culture).Distinct(StringComparer.Ordinal).Count() != oracle.Comments.Count)
            throw new BridgeException("VciInvalidCommentOracle", "An exact block identity and unique independently retained comment cultures are required.");
        foreach (var comment in oracle.Comments)
        {
            VciGuard.Text(comment.Culture, "culture");
            if (comment.Text is null || comment.Text.Length > 1048576)
                throw new BridgeException("VciInvalidCommentOracle", "Oracle comment text exceeds the supported bound.");
        }
        if (requiredFiles is null || requiredFiles.Count is < 1 or > 20000 ||
            requiredFiles.Any(x => x is null) || !requiredFiles.Any(x => x.RelativePath == primaryPath) ||
            requiredFiles.Select(x => x.RelativePath.Normalize(NormalizationForm.FormC)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requiredFiles.Count)
            throw new BridgeException("VciInvalidProfile", "The primary file and complete unique required companion inventory are required.");
        foreach (var file in requiredFiles)
        {
            VciGuard.Path(file.RelativePath, 255);
            if (file.Role is not ("source" or "resources" or "script" or "metadata" or "document" or "libraryVersion" or "auxiliary"))
                throw new BridgeException("VciInvalidProfile", "Every retained file requires an explicit known role.");
        }
        var inventory = VciManifest.Capture("comment-preservation", new("retained-byte-review", oracle.EvidenceSha256), [], files, true,
            new(MaxTotalBytes: 16777216));
        var missing = requiredFiles.Where(x => !inventory.Files.Any(f => f.RelativePath == x.RelativePath)).Select(x => x.RelativePath).ToArray();
        var issues = missing.Select(x => "missingCompanion:" + x).ToList();
        issues.AddRange(inventory.Files.Where(x => !requiredFiles.Any(f => f.RelativePath == x.RelativePath))
            .Select(x => "unreviewedCompanion:" + x.RelativePath));
        var observed = new List<VciLanguageText>();
        var primary = files.SingleOrDefault(x => x.RelativePath == primaryPath);
        if (nativeFormat == "SimaticSD" && !requiredFiles.Any(x => x.Role == "resources" && x.RelativePath.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase)))
            issues.Add("simaticSdResourceRequirementMissing");
        if (primary is not null && nativeFormat == "SimaticML")
        {
            using var bytes = new MemoryStream(primary.Bytes.ToArray());
            using var reader = XmlReader.Create(bytes, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16777216 });
            XDocument document;
            try { document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
            catch (XmlException error) { throw new BridgeException("VciInvalidNativeDocument", "Retained SimaticML is not a bounded safe XML document.", error); }
            if (document.Root?.Name != "Document")
                throw new BridgeException("VciInvalidNativeDocument", "A native SimaticML Document root is required.");
            var blocks = document.Root?.Elements().Where(x => x.Name.NamespaceName.Length == 0 && x.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).ToArray() ?? [];
            if (blocks.Length != 1) throw new BridgeException("VciInvalidNativeDocument", "Exactly one native block document is required.");
            var block = blocks[0];
            if (block.Elements("AttributeList").Count() != 1 || block.Elements("ObjectList").Count() != 1)
                throw new BridgeException("VciInvalidNativeDocument", "Exactly one block attribute and object composition is required.");
            var attributes = block.Element("AttributeList")!;
            if (attributes.Elements("Name").Count() != 1 || attributes.Elements("Number").Count() != 1 ||
                attributes.Element("Name")!.HasElements || attributes.Element("Number")!.HasElements)
                throw new BridgeException("VciInvalidNativeDocument", "The block identity must be unambiguous scalar data.");
            if (block.Name.LocalName != "SW.Blocks." + oracle.BlockKind || attributes?.Element("Name")?.Value != oracle.BlockName ||
                attributes.Element("Number")?.Value != oracle.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new BridgeException("VciNativeIdentityMismatch", "SimaticML block kind/name/number differs from the independent oracle.");
            var comments = block.Element("ObjectList")?.Elements("MultilingualText").Where(x => (string?)x.Attribute("CompositionName") == "Comment").ToArray() ?? [];
            if (comments.Length != 1) issues.Add("blockCommentCompositionMissingOrAmbiguous");
            else
            {
                if (comments[0].Elements("ObjectList").Count() != 1 ||
                    comments[0].Element("ObjectList")!.Elements().Any(x => x.Name != "MultilingualTextItem"))
                    throw new BridgeException("VciInvalidNativeDocument", "Unknown or ambiguous block comment item composition.");
                foreach (var item in comments[0].Element("ObjectList")?.Elements("MultilingualTextItem") ?? [])
                {
                    if (item.Elements("AttributeList").Count() != 1)
                        throw new BridgeException("VciInvalidNativeDocument", "The comment item attributes must be unambiguous.");
                    var values = item.Element("AttributeList");
                    var culture = values?.Element("Culture"); var text = values?.Element("Text");
                    if (culture is null || text is null || culture.HasElements || text.HasElements ||
                        values!.Elements("Culture").Count() != 1 || values.Elements("Text").Count() != 1)
                        throw new BridgeException("VciInvalidNativeDocument", "Unknown or incomplete block comment text representation.");
                    VciGuard.Text(culture.Value, "culture"); observed.Add(new(culture.Value, text.Value));
                    if (observed.Count > 256) throw new BridgeException("VciLimitExceeded", "Native comment culture inventory exceeds the supported bound.");
                }
                if (observed.Select(x => x.Culture).Distinct(StringComparer.Ordinal).Count() != observed.Count)
                    throw new BridgeException("VciInvalidNativeDocument", "Duplicate comment cultures cannot establish language preservation.");
                foreach (var expected in oracle.Comments)
                    if (!observed.Any(x => x == expected)) issues.Add("commentMissingOrChanged:" + expected.Culture);
                foreach (var extra in observed.Where(x => !oracle.Comments.Any(c => c.Culture == x.Culture)))
                    issues.Add("unreviewedCommentCulture:" + extra.Culture);
            }
        }
        else issues.Add("blockCommentLanguageGrammarUnqualified:" + nativeFormat);
        return new(nativeFormat, oracle.BlockName, oracle.EvidenceSha256, inventory.Files, observed.AsReadOnly(),
            Array.AsReadOnly(missing), issues.AsReadOnly(), issues.Count == 0);
    }
}
