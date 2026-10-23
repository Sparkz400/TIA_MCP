using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

/// <summary>At most seven read-only exports, only inside root's admitted exclusive native scope.</summary>
internal static class TranslationPopulatedCategoryProbe
{
    internal static JObject Run(Project project, PlcSoftware plc, DeviceItem owner, string output)
    {
        if (project.IsModified) throw new InvalidOperationException("Populated category probe requires an already saved fixture.");
        var source = project.LanguageSettings.ReferenceLanguage;
        var target = project.LanguageSettings.ActiveLanguages.Single(l => l.Culture.Name == "en-GB");
        if (source.Culture.Name != "de-DE") throw new InvalidOperationException("Exact de-DE/en-GB fixture required.");
        var main = plc.BlockGroup.Blocks.Find("Main") as OB;
        if (main == null || main.Number != 1) throw new InvalidOperationException("Exact root Main OB1 required; no alternate object fallback.");
        var categories = project.TextCategories.ToArray();
        if (categories.Length == 0 || categories.Length > 64 || categories.Select(c => c.Identifier).Distinct().Count() != categories.Length)
            throw new InvalidOperationException("Invalid category inventory.");
        Func<string, TextCategory[]> one = identifier => new[] { categories.Single(c => c.Identifier == identifier) };
        var comments = one("BlockCommentCategoryData"); var hardware = one("HwcnCommentTextCategoryData"); var alarms = one("AlarmClassDisplayNameCategory");
        var requests = new JArray();
        var result = new JObject
        {
            ["kind"] = "translation-populated-category-probe", ["readOnly"] = true, ["automaticReplay"] = false,
            ["nativeImportExecuted"] = false, ["nativeImportAllowed"] = false, ["nativeQualified"] = false,
            ["sourceCulture"] = source.Culture.Name, ["targetCulture"] = target.Culture.Name,
            ["projectName"] = project.Name, ["plcName"] = plc.Name, ["hardwareOwnerName"] = owner.Name,
            ["selectedBlock"] = new JObject { ["name"] = main.Name, ["number"] = main.Number, ["runtimeType"] = main.GetType().FullName,
                ["selectionPath"] = "plc:" + plc.Name + "/BlockGroup/Blocks/Main", ["parentName"] = plc.BlockGroup.Name, ["parentRuntimeType"] = main.Parent.GetType().FullName },
            ["categories"] = new JArray(categories.Select(c => new JObject { ["identifier"] = c.Identifier, ["localizedName"] = c.Name })),
            ["maximumExportCalls"] = 7, ["exports"] = requests, ["completed"] = false
        };
        Func<string, TextCategory[], IEngineeringObject, string, Dictionary<string, TranslationCategoryWorkbook.Row>> export = (mode, selected, instance, selection) =>
        {
            if (project.IsModified) throw new InvalidOperationException("Project changed before read-only export; stopped.");
            if (requests.Count >= 7) throw new InvalidOperationException("Export-call bound exceeded.");
            string path = Path.Combine(output, mode + ".xlsx");
            if (File.Exists(path)) throw new InvalidOperationException("Fresh export path required; no replay.");
            var request = new JObject { ["mode"] = mode, ["categoryArgument"] = "explicit", ["categoryIds"] = new JArray(selected.Select(c => c.Identifier)),
                ["objectArgument"] = "explicit", ["objectCount"] = 1, ["objectSelection"] = selection,
                ["objectRuntimeTypes"] = new JArray(instance.GetType().FullName), ["sourceCulture"] = source.Culture.Name, ["targetCulture"] = target.Culture.Name,
                ["beforeModified"] = false, ["automaticReplay"] = false, ["state"] = "dispatchIntent", ["startedAtUtc"] = DateTime.UtcNow };
            requests.Add(request); Save(output, result);
            try
            {
                project.ExportProjectTexts(new FileInfo(path), source, new[] { target }, selected, new[] { instance });
                request["state"] = "returned"; request["artifact"] = Artifact(path);
            }
            catch (Exception error)
            {
                request["state"] = "failed"; request["failureType"] = error.GetType().FullName; request["message"] = error.Message;
                request["innerMessage"] = error.InnerException?.Message;
                if (File.Exists(path)) request["partialArtifact"] = Artifact(path);
                request["afterModified"] = project.IsModified; request["finishedAtUtc"] = DateTime.UtcNow;
                result["outcome"] = "stopped-after-native-failure"; Save(output, result); throw;
            }
            request["afterModified"] = project.IsModified; request["finishedAtUtc"] = DateTime.UtcNow; Save(output, result);
            if (project.IsModified) throw new InvalidOperationException("Read-only export changed modified state; stopped.");
            try
            {
                var records = TranslationCategoryWorkbook.Read(path); request["decodedRows"] = records.Count; Save(output, result); return records;
            }
            catch (Exception error)
            {
                request["decodeFailureType"] = error.GetType().FullName; request["decodeFailure"] = error.Message;
                result["outcome"] = "stopped-after-unrecognized-workbook"; Save(output, result); throw;
            }
        };
        string plcSelection = "plc:" + plc.Name, mainSelection = "block:Main:OB:1";
        var plcBefore = export("plc-all-before", categories, plc, plcSelection);
        var mainBefore = export("main-all-before", categories, main, mainSelection);
        bool populated = TranslationCategoryWorkbook.PopulatedMainControl(plcBefore, mainBefore, project.Name, plc.Name);
        result["populatedMainControlPassed"] = populated;
        if (!populated)
        {
            result["completed"] = true; result["outcome"] = "inconclusive-main-control-not-populated-or-not-preserved";
            result["restrictedExportsSkipped"] = true; Save(output, result); return result;
        }
        Save(output, result);
        export("main-comments", comments, main, mainSelection);
        export("main-hardware", hardware, main, mainSelection);
        export("main-alarms", alarms, main, mainSelection);
        var mainAfter = export("main-all-after", categories, main, mainSelection);
        var plcAfter = export("plc-all-after", categories, plc, plcSelection);
        result["mainBaselineUnchanged"] = TranslationCategoryWorkbook.Equal(mainBefore, mainAfter);
        result["plcBaselineUnchanged"] = TranslationCategoryWorkbook.Equal(plcBefore, plcAfter);
        result["completed"] = true;
        result["outcome"] = result.Value<bool>("mainBaselineUnchanged") && result.Value<bool>("plcBaselineUnchanged") ? "exports-completed-awaiting-independent-comparison" : "baseline-drift-refused";
        Save(output, result); return result;
    }

    private static JObject Artifact(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return new JObject { ["path"] = path, ["bytes"] = stream.Length, ["sha256"] = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
    }
    private static void Save(string output, JObject result) => TiaMcp.Openness.V21.LibraryJournalStore.Write(Path.Combine(output, "populated-category-probe-progress.json"), result);
}
