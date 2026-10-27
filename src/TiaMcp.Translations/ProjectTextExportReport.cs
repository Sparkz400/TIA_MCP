using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>Reads worker project-text exports. A filtered export is evidence only, never an import scope.</summary>
public static class ProjectTextExportReport
{
    public static JsonObject Build(string root, JsonElement exported, int maxItems)
    {
        string raw = exported.GetRawText(), receipt = Sha256(Encoding.UTF8.GetBytes(raw));
        var value = JsonNode.Parse(raw)!.AsObject();
        // All workbooks come from one exclusive worker call, so they share one capture identity.
        var project = new EngineeringObjectReference(new(value["projectPath"]!.GetValue<string>(), null, null,
            "/project/" + value["projectName"]!.GetValue<string>(), "project"), new(receipt, receipt), receipt);
        var opened = new Dictionary<string, (TranslationWorkbook Workbook, string Directory)>(StringComparer.Ordinal);
        foreach (var item in value["workbooks"]!.AsArray())
        {
            try
            {
                byte[] bytes = TranslationWorkerAdapter.ReadArtifact(root, item!["artifact"]!);
                var profile = TranslationWorkerAdapter.NativeProfile(bytes, "tia21-project-text-export-v1");
                var workbook = TranslationWorkbook.Open(bytes, profile, project, Sha256(bytes));
                item["rows"] = profile.Rows.Length;
                item["categoryRows"] = Node(profile.Rows.GroupBy(r => r.Category, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()));
                opened.Add(item["role"]!.GetValue<string>(), (workbook, Path.GetDirectoryName(Path.GetFullPath(item["artifact"]!["path"]!.GetValue<string>()))!));
            }
            catch (Exception error) when (error is BridgeException or InvalidOperationException or KeyNotFoundException) { item!["readError"] = Error(error); }
        }
        if (value["workbooks"]!.AsArray().Any(w => w!["role"]!.GetValue<string>() == "filtered")) value["comparison"] = Compare(value, opened, maxItems);
        value["filteredExportIsEvidenceOnly"] = true; value["nativeImportAllowed"] = false;
        return value;
    }

    private static JsonObject Compare(JsonObject value, Dictionary<string, (TranslationWorkbook Workbook, string Directory)> opened, int maxItems)
    {
        if (!opened.TryGetValue("full", out var full) || !opened.TryGetValue("filtered", out var filtered))
            return new() { ["status"] = "unavailable", ["reason"] = "The full or filtered workbook could not be read; see readError." };
        // Every observed V21 export labels category rows "<Identifier>"; any other label makes Compare refuse rather than guess.
        var bindings = value["categories"]!.AsArray().Select(c => c!["identifier"]!.GetValue<string>()).Select(id => new TranslationCategoryBinding(id, "<" + id + ">")).ToArray();
        JsonObject report;
        try { report = TranslationCategoryScope.Compare(full.Workbook, filtered.Workbook, value["requestedCategoryIds"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray(), bindings); }
        catch (BridgeException error) { return new() { ["status"] = "unavailable", ["error"] = Error(error) }; }
        byte[] bytes = Encoding.UTF8.GetBytes(report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        string path = Path.Combine(full.Directory, "comparison.json");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
        JsonObject Keys(string name)
        {
            var keys = report[name]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
            return new() { ["count"] = keys.Length, ["keys"] = Node(keys.Take(maxItems).ToArray()), ["truncated"] = keys.Length > maxItems };
        }
        return new()
        {
            ["status"] = "compared", ["fullRows"] = report["baselineRows"]!.DeepClone(),
            ["expectedFilteredRows"] = report["expectedSelectedRows"]!.DeepClone(), ["filteredRows"] = report["observedSelectedRows"]!.DeepClone(),
            // Leaked: filtered rows the requested categories do not explain. Dropped: requested-category rows the filter omitted.
            ["leaked"] = Keys("unexpectedKeys"), ["dropped"] = Keys("missingKeys"), ["changed"] = Keys("changedKeys"),
            ["matchesRequestedCategories"] = report["matchesRequestedCategoryIntersection"]!.DeepClone(),
            ["artifact"] = new JsonObject { ["path"] = path, ["sha256"] = Sha256(bytes) }
        };
    }

    private static JsonObject Error(Exception error) => new()
    { ["code"] = (error as BridgeException)?.Code ?? "UnsupportedTranslationScopeLayout", ["message"] = error.Message };
}
