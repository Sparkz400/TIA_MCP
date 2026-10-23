using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

/// <summary>Only callable inside an admitted, exclusive, serialized native evidence run.</summary>
internal static class TranslationCategoryProbe
{
    internal static JObject Run(Project project, PlcSoftware plc, DeviceItem owner, string output)
    {
        if (project.IsModified) throw new InvalidOperationException("Category probe requires an already saved fixture.");
        var source = project.LanguageSettings.ReferenceLanguage;
        var target = project.LanguageSettings.ActiveLanguages.Single(l => l.Culture.Name == "en-GB");
        if (source.Culture.Name != "de-DE") throw new InvalidOperationException("Exact de-DE/en-GB fixture required.");
        var block = plc.BlockGroup.Blocks.Find("TiaMcp_IsEnabled") ?? throw new InvalidOperationException("Exact seeded block required.");
        var all = project.TextCategories.ToArray();
        if (all.Length == 0 || all.Length > 64) throw new InvalidOperationException("Category inventory bound exceeded.");
        Func<string, TextCategory[]> one = id => new[] { all.Single(c => c.Identifier == id) };
        var hardware = one("HwcnCommentTextCategoryData");
        var comments = one("BlockCommentCategoryData");
        var alarms = one("AlarmClassDisplayNameCategory");
        var exports = new JArray();
        var result = new JObject
        {
            ["kind"] = "translation-category-filter-probe", ["readOnly"] = true,
            ["nativeImportExecuted"] = false, ["nativeImportAllowed"] = false,
            ["sourceCulture"] = source.Culture.Name, ["targetCulture"] = target.Culture.Name,
            ["categories"] = new JArray(all.Select(c => new JObject { ["identifier"] = c.Identifier, ["localizedName"] = c.Name })),
            ["exports"] = exports, ["automaticReplay"] = false
        };
        Action<string, TextCategory[], IEngineeringObject[], string> probe = (name, categories, objects, selection) =>
        {
            if (project.IsModified) throw new InvalidOperationException("Project changed before next read-only probe; stopped.");
            string file = Path.Combine(output, name + ".xlsx");
            if (File.Exists(file)) throw new InvalidOperationException("Fresh output file required.");
            var request = new JObject
            {
                ["mode"] = name, ["categoryArgument"] = categories == null ? "null" : categories.Length == 0 ? "empty" : "explicit",
                ["categoryIds"] = categories == null ? null : new JArray(categories.Select(c => c.Identifier)),
                ["objectSelection"] = selection, ["objectArgument"] = objects == null ? "null" : objects.Length == 0 ? "empty" : "explicit",
                ["objectCount"] = objects == null ? (int?)null : objects.Length,
                ["objectRuntimeTypes"] = objects == null ? null : new JArray(objects.Select(o => o.GetType().FullName)),
                ["sourceCulture"] = source.Culture.Name, ["targetCulture"] = target.Culture.Name,
                ["state"] = "dispatchIntent", ["automaticReplay"] = false, ["startedAtUtc"] = DateTime.UtcNow
            };
            exports.Add(request); Save(output, result);
            try
            {
                project.ExportProjectTexts(new FileInfo(file), source, new[] { target }, categories, objects);
                request["state"] = "returned"; request["artifact"] = Artifact(file);
            }
            catch (Exception error)
            {
                request["state"] = "failed"; request["failureType"] = error.GetType().FullName;
                request["message"] = error.Message; request["innerMessage"] = error.InnerException?.Message;
                if (File.Exists(file)) request["partialArtifact"] = Artifact(file);
            }
            request["afterModified"] = project.IsModified;
            request["finishedAtUtc"] = DateTime.UtcNow; Save(output, result);
            if (project.IsModified) throw new InvalidOperationException("Read-only category probe changed modified state; further probes stopped.");
        };
        var plcObjects = new IEngineeringObject[] { plc };
        var blockObjects = new IEngineeringObject[] { block };
        var ownerObjects = new IEngineeringObject[] { owner };
        probe("plc-all", all, plcObjects, "plc:" + plc.Name);
        probe("plc-hardware", hardware, plcObjects, "plc:" + plc.Name);
        probe("plc-comments", comments, plcObjects, "plc:" + plc.Name);
        probe("plc-alarms", alarms, plcObjects, "plc:" + plc.Name);
        probe("plc-empty-categories", new TextCategory[0], plcObjects, "plc:" + plc.Name);
        probe("plc-null-categories", null, plcObjects, "plc:" + plc.Name);
        probe("block-all", all, blockObjects, "block:" + block.Name);
        probe("block-hardware", hardware, blockObjects, "block:" + block.Name);
        probe("block-comments", comments, blockObjects, "block:" + block.Name);
        probe("owner-all", all, ownerObjects, "deviceItem:" + owner.Name);
        probe("owner-hardware", hardware, ownerObjects, "deviceItem:" + owner.Name);
        probe("owner-comments", comments, ownerObjects, "deviceItem:" + owner.Name);
        probe("empty-objects", all, new IEngineeringObject[0], "empty");
        probe("null-objects", all, null, "null");
        result["completed"] = true; Save(output, result); return result;
    }

    private static JObject Artifact(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return new JObject { ["path"] = path, ["bytes"] = stream.Length,
                ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
    }
    private static void Save(string output, JObject result) =>
        TiaMcp.Openness.V21.LibraryJournalStore.Write(Path.Combine(output, "category-probe-progress.json"), result);
}
