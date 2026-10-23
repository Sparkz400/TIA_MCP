using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>A read-only resume position bound to a captured workbook, profile and project observation.</summary>
public sealed record TranslationInspectionCursor(string WorkbookSha256, string ProfileSha256, string ProjectSha256, int Offset);

public static class TranslationInspection
{
    /// <summary>Ordinal key/culture pages. A cursor is an integrity-bound position, not an authorization token.</summary>
    public static JsonObject Page(TranslationWorkbook workbook, int pageSize = 100, TranslationInspectionCursor? cursor = null)
    {
        Require(pageSize is > 0 and <= 1000, "TranslationPageLimit", "Page size must be between 1 and 1000.");
        string projectHash = HashJson(workbook.Project);
        int offset = cursor?.Offset ?? 0;
        if (cursor is not null)
            Require(cursor.WorkbookSha256 == workbook.WorkbookSha256 && cursor.ProfileSha256 == workbook.ProfileSha256 &&
                cursor.ProjectSha256 == projectHash, "StaleTranslationCursor", "Resume cursor belongs to a different capture, profile or project observation.");
        Require(offset >= 0 && offset <= workbook.OrderedTargets.Length, "InvalidTranslationCursor", "Resume offset is outside the captured mapped targets.");
        var rows = new JsonArray();
        int end = offset + Math.Min(pageSize, workbook.OrderedTargets.Length - offset);
        for (int i = offset; i < end; ++i)
        {
            var (row, target) = workbook.OrderedTargets[i];
            var source = workbook.ReadText(row.SourceCell);
            var state = workbook.ReadText(target.Location);
            var findings = new JsonArray();
            if (state.State != "text") findings.Add(state.State);
            if (state.State == "text" && string.IsNullOrWhiteSpace(state.Text)) findings.Add("whitespace-only");
            if (state.State == "text" && state.Text == source.Text) findings.Add("same-as-source");
            if (state.RichText) findings.Add("rich-text-edit-unsupported");
            if (row.TokenGrammar == "unknown") findings.Add("placeholder-grammar-unqualified");
            if (target.MaxUtf16Length is int max && state.Text?.Length > max) findings.Add("length-limit-exceeded");
            var result = new JsonObject { ["nativeKey"] = row.NativeKey, ["culture"] = target.Culture, ["category"] = row.Category,
                ["location"] = Node(target.Location), ["source"] = Node(source), ["target"] = Node(state),
                ["sourceSha256"] = source.Sha256, ["targetSha256"] = state.Sha256, ["findings"] = findings };
            if (row.AlarmTextPolicy is not null) result["alarmInheritance"] = TranslationReview.AlarmInheritance(workbook, row);
            rows.Add(result);
        }
        var report = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "translation-inspection-page",
            ["workbookSha256"] = workbook.WorkbookSha256, ["profileSha256"] = workbook.ProfileSha256,
            ["projectSha256"] = projectHash, ["offset"] = offset, ["totalMappedTargets"] = workbook.OrderedTargets.Length,
            ["scope"] = "explicit-profile-rows-only", ["rows"] = rows,
            ["nextCursor"] = end == workbook.OrderedTargets.Length ? null : Node(new TranslationInspectionCursor(workbook.WorkbookSha256, workbook.ProfileSha256, projectHash, end)),
            ["nativeQualified"] = false, ["nativeExecuted"] = false, ["nativeImportAllowed"] = false };
        report["pageSha256"] = CanonicalJson.Sha256(report);
        return report;
    }
}
