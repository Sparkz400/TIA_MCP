using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>An observed association, never inferred by trimming brackets or translating labels.</summary>
public sealed record TranslationCategoryBinding(string Identifier, string ExportLabel);

/// <summary>File-only comparison of the narrow, observed V21 bilingual layout. It grants no import permission.</summary>
public static class TranslationCategoryScope
{
    public static JsonObject Compare(TranslationWorkbook allCategoriesForSameObjects,
        TranslationWorkbook selectedCategoriesForSameObjects, string[] requestedCategoryIds,
        TranslationCategoryBinding[] categoryBindings)
    {
        var baseline = allCategoriesForSameObjects;
        var selected = selectedCategoriesForSameObjects;
        EngineeringIdentityGuard.EnsureSameObject(baseline.Project, selected.Project);
        Require(baseline.Project.Snapshot.SourceSha256 == selected.Project.Snapshot.SourceSha256 &&
            baseline.Project.Snapshot.TiaVersion == selected.Project.Snapshot.TiaVersion &&
            baseline.Project.ContentSha256 == selected.Project.ContentSha256,
            "TranslationScopeCaptureMismatch", "Comparisons require a common capture receipt and project identity.");
        Require(baseline.Mapping.SourceCulture == selected.Mapping.SourceCulture,
            "TranslationScopeCaptureMismatch", "Source languages differ.");
        Require(requestedCategoryIds is { Length: > 0 and <= 256 } && categoryBindings is { Length: > 0 and <= 256 },
            "InvalidTranslationCategoryScope", "A bounded explicit category selection and label map are required.");
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in categoryBindings)
            Require(binding is not null && !string.IsNullOrWhiteSpace(binding.Identifier) && binding.Identifier.Length <= 1024 &&
                !string.IsNullOrWhiteSpace(binding.ExportLabel) && binding.ExportLabel.Length <= 1024 &&
                bindings.TryAdd(binding.Identifier, binding.ExportLabel) && labels.Add(binding.ExportLabel),
                "InvalidTranslationCategoryScope", "Category identifiers and exact observed labels must be unique.");
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in requestedCategoryIds)
            Require(id is not null && bindings.ContainsKey(id) && requested.Add(id),
                "InvalidTranslationCategoryScope", "Unknown or duplicate category identifier.");
        var allowedLabels = requested.Select(id => bindings[id]).ToHashSet(StringComparer.Ordinal);
        var beforeLayout = ReadCompleteObservedLayout(baseline, labels);
        var afterLayout = ReadCompleteObservedLayout(selected, labels);
        Require(beforeLayout.TargetCulture == afterLayout.TargetCulture,
            "TranslationScopeCaptureMismatch", "Target languages differ, including in empty worksheets.");
        var before = beforeLayout.Rows;
        var after = afterLayout.Rows;
        var expected = before.Where(p => allowedLabels.Contains(p.Value.Category)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var unexpected = after.Keys.Except(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = expected.Keys.Except(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changed = after.Where(p => before.TryGetValue(p.Key, out var old) && old != p.Value).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var outside = after.Where(p => !allowedLabels.Contains(p.Value.Category)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        bool matches = unexpected.Length == 0 && missing.Length == 0 && changed.Length == 0 && outside.Length == 0;
        var report = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "translation-category-scope-comparison",
            ["baselineWorkbookSha256"] = baseline.WorkbookSha256, ["selectedWorkbookSha256"] = selected.WorkbookSha256,
            ["baselineProfileSha256"] = baseline.ProfileSha256, ["selectedProfileSha256"] = selected.ProfileSha256,
            ["baselineProject"] = Node(baseline.Project), ["selectedProject"] = Node(selected.Project),
            ["requestedCategoryIds"] = Node(requested.Order(StringComparer.Ordinal).ToArray()),
            ["categoryBindings"] = Node(categoryBindings.OrderBy(b => b.Identifier, StringComparer.Ordinal).ToArray()),
            ["baselineRows"] = before.Count, ["expectedSelectedRows"] = expected.Count, ["observedSelectedRows"] = after.Count,
            ["sourceCulture"] = baseline.Mapping.SourceCulture, ["targetCulture"] = beforeLayout.TargetCulture,
            ["completeWorksheetNames"] = Node(new[] { "User Texts", "System Texts" }),
            ["unexpectedKeys"] = Node(unexpected), ["missingKeys"] = Node(missing), ["changedKeys"] = Node(changed),
            ["outOfCategoryKeys"] = Node(outside), ["matchesRequestedCategoryIntersection"] = matches,
            ["restrictiveFilteringDemonstrated"] = matches && expected.Count > 0 && expected.Count < before.Count,
            ["scope"] = "complete-observed-bilingual-worksheet-layout-only",
            ["sameNativeObjectSelectionVerified"] = false, ["projectFreshnessVerified"] = false,
            ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false
        };
        report["comparisonSha256"] = CanonicalJson.Sha256(report);
        return report;
    }

    private sealed record ObservedRow(string Worksheet, string Category, string ViewPath, string Substitutions,
        string SourceCulture, string SourceSha256, string TargetCulture, string TargetSha256);
    private sealed record ObservedLayout(string TargetCulture, Dictionary<string, ObservedRow> Rows);

    // Complete native layout, including both empty sheets and every metadata/text
    // column. A caller's partial map cannot conceal peer rows. ZIP timestamps and
    // shared-string indexes are storage details; unsupported layouts fail closed.
    internal static string NativeContentSha256(TranslationWorkbook workbook) =>
        HashJson(ReadCompleteObservedLayout(workbook,
            workbook.Mapping.Rows.Select(r => r.Category).ToHashSet(StringComparer.Ordinal)));

    internal static string NativeTargetCulture(TranslationWorkbook workbook) =>
        ReadCompleteObservedLayout(workbook, workbook.Mapping.Rows.Select(r => r.Category).ToHashSet(StringComparer.Ordinal)).TargetCulture;

    private static ObservedLayout ReadCompleteObservedLayout(TranslationWorkbook workbook, HashSet<string> labels)
    {
        var mapped = workbook.Mapping.Rows.ToDictionary(r => r.NativeKey, StringComparer.Ordinal);
        var observed = new Dictionary<string, ObservedRow>(StringComparer.Ordinal);
        var sheet = BoundedOpcPackage.Sheet;
        // Empty sheets are scope evidence too. Do not accept a map that hides a
        // removed, renamed, orphaned or differently localized worksheet.
        var declaredSheets = workbook.Package.Xml["xl/workbook.xml"].Root!.Element(sheet + "sheets")?.Elements(sheet + "sheet").ToArray();
        string[] names = ["User Texts", "System Texts"];
        Require(declaredSheets is { Length: 2 } && declaredSheets.Select(s => (string?)s.Attribute("name")).SequenceEqual(names),
            "UnsupportedTranslationScopeLayout", "Exactly the observed User Texts and System Texts worksheets are required.");
        var worksheetParts = workbook.Package.Xml.Where(p => p.Value.Root?.Name == sheet + "worksheet").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Require(worksheetParts.Count == 2, "UnsupportedTranslationScopeLayout", "Undeclared or missing worksheet content is refused.");
        var links = workbook.Package.Xml["xl/_rels/workbook.xml.rels"].Root!.Elements(BoundedOpcPackage.Relations + "Relationship");
        var relationNamespace = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        string? workbookTargetCulture = null;
        for (int sheetIndex = 0; sheetIndex < names.Length; sheetIndex++)
        {
            var declared = declaredSheets[sheetIndex];
            string? state = (string?)declared.Attribute("state");
            Require(state is null or "visible", "UnsupportedTranslationScopeLayout", "Hidden worksheets require separate scope qualification.");
            var link = links.Single(l => (string?)l.Attribute("Id") == (string?)declared.Attribute(relationNamespace + "id"));
            string partName = $"xl/worksheets/sheet{sheetIndex + 1}.xml";
            string? relationshipTarget = (string?)link.Attribute("Target");
            Require((relationshipTarget == "/" + partName || relationshipTarget == partName[3..]) && worksheetParts.ContainsKey(partName),
                "UnsupportedTranslationScopeLayout", "Worksheet relationships differ from the observed layout.");
            var part = new KeyValuePair<string, System.Xml.Linq.XDocument>(partName, worksheetParts[partName]);
            var sheetData = part.Value.Root!.Elements(sheet + "sheetData").ToArray();
            Require(sheetData.Length == 1 && sheetData[0].Elements().All(e => e.Name == sheet + "row"),
                "UnsupportedTranslationScopeLayout", "Exactly one complete worksheet data element is required.");
            var rows = sheetData[0].Elements(sheet + "row").ToArray();
            Require(rows is { Length: > 0 } && (string?)rows[0].Attribute("r") == "1",
                "UnsupportedTranslationScopeLayout", "Every sheet must have the observed row-one headers.");
            string[] headers = ["Category", "ViewPath", "Internal ID", "Substitutions", workbook.Mapping.SourceCulture + "*"];
            for (int i = 0; i < headers.Length; i++)
                Require(Plain(workbook, part.Key, ((char)('A' + i)) + "1") == headers[i],
                    "UnsupportedTranslationScopeLayout", "Bilingual native header differs from the observed layout.");
            // Target columns follow the source until ref="0"; the five-argument overload may declare several.
            var targets = new List<string>();
            for (string culture; (culture = Plain(workbook, part.Key, TranslationWorkbook.ColumnName(6 + targets.Count) + "1")) != "ref=\"0\"";)
            {
                try { Require(System.Globalization.CultureInfo.GetCultureInfo(culture).Name == culture && culture.Length > 0,
                    "UnsupportedTranslationScopeLayout", "Target header must name an exact canonical culture."); }
                catch (System.Globalization.CultureNotFoundException) { throw new TiaMcp.Core.BridgeException("UnsupportedTranslationScopeLayout", "Unknown target culture header."); }
                Require(culture != workbook.Mapping.SourceCulture && !targets.Contains(culture) && targets.Count < 64,
                    "UnsupportedTranslationScopeLayout", "Distinct target cultures other than the source are required.");
                targets.Add(culture);
            }
            // One target keeps the observed bilingual values, so existing content hashes are unchanged.
            string targetCulture = string.Join(",", targets);
            Require(targets.Count > 0 && rows[0].Elements(sheet + "c").Count() == 21 + targets.Count,
                "UnsupportedTranslationScopeLayout", "Target culture and the observed reference headers are required.");
            Require(workbookTargetCulture is null || workbookTargetCulture == targetCulture,
                "UnsupportedTranslationScopeLayout", "Every worksheet must declare the same target culture, including empty sheets.");
            workbookTargetCulture = targetCulture;
            for (int i = 0; i < 16; i++)
                Require(Plain(workbook, part.Key, TranslationWorkbook.ColumnName(6 + targets.Count + i) + "1") == $"ref=\"{i}\"",
                    "UnsupportedTranslationScopeLayout", "Reference headers differ from the observed layout.");
            foreach (var row in rows.Skip(1))
            {
                string number = (string)row.Attribute("r")!;
                var targetCells = targets.Select((_, i) => new CellLocation(part.Key, TranslationWorkbook.ColumnName(6 + i) + number)).ToArray();
                Require(row.Elements(sheet + "c").Select(c => (string?)c.Attribute("r")).Order(StringComparer.Ordinal)
                    .SequenceEqual(Enumerable.Range(1, 5 + targets.Count).Select(c => TranslationWorkbook.ColumnName(c) + number).Order(StringComparer.Ordinal)),
                    "UnsupportedTranslationScopeLayout", "Every data row must contain exactly the observed metadata, source and target cells.");
                string key = Plain(workbook, part.Key, "C" + number), category = Plain(workbook, part.Key, "A" + number);
                Require(mapped.TryGetValue(key, out var map) && map.Category == category && labels.Contains(category) &&
                    map.KeyCell == new CellLocation(part.Key, "C" + number) && map.SourceCell == new CellLocation(part.Key, "E" + number) &&
                    map.Targets.Select(t => t.Culture).SequenceEqual(targets) && map.Targets.Select(t => t.Location).SequenceEqual(targetCells),
                    "TranslationScopeMapMismatch", "Every actual row and category must match the complete explicit map; omitted or relabelled rows are refused.");
                var source = workbook.ReadText(new(part.Key, "E" + number));
                var values = targetCells.Select(workbook.ReadText).ToArray();
                Require(!source.RichText && values.All(v => !v.RichText), "UnsupportedTranslationScopeLayout", "Rich text requires a separate scope profile.");
                Require(observed.TryAdd(key, new(names[sheetIndex], category, Plain(workbook, part.Key, "B" + number), Plain(workbook, part.Key, "D" + number),
                    workbook.Mapping.SourceCulture, source.Sha256, targetCulture, string.Join(",", values.Select(v => v.Sha256)))),
                    "TranslationScopeMapMismatch", "Duplicate native key in observed workbook.");
            }
        }
        Require(observed.Count == mapped.Count, "TranslationScopeMapMismatch", "Profile contains non-observed rows.");
        return new(workbookTargetCulture!, observed);
    }

    private static string Plain(TranslationWorkbook workbook, string part, string cell)
    {
        var value = workbook.ReadText(new(part, cell));
        Require(!value.RichText && value.State is "text" or "empty", "UnsupportedTranslationScopeLayout", "Plain present header and metadata cells are required.");
        return value.Text!;
    }
}
