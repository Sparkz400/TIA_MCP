using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness.V21
{
    // Read-only project-text export. The Host compares workbooks; a filtered export is evidence, never scope.
    internal sealed partial class OpennessSession
    {
        private JObject ProjectTextExport(JObject args)
        {
            var project = SelectProject(args);
            var settings = project.LanguageSettings;
            string sourceName = args.Value<string>("sourceCulture");
            string[] targetNames = ProjectTextList(args, "targetCultures", 32), ownerPaths = ProjectTextList(args, "owners", 64), categoryIds = ProjectTextList(args, "categoryIds", 256);
            if (targetNames.Length == 0 || targetNames.Contains(sourceName)) throw new BridgeException("InvalidRequest", "One or more target cultures other than the source culture are required.");
            // The filtered overload refuses null or empty object lists natively, so a whole-project filter does not exist.
            if (categoryIds.Length != 0 && ownerPaths.Length == 0) throw new BridgeException("CategoryFilterRequiresOwners", "Category filtering requires explicit owners.");
            var source = ProjectTextLanguage(settings, sourceName);
            var targets = targetNames.Select(name => ProjectTextLanguage(settings, name)).ToArray();
            var all = project.TextCategories.Take(257).ToArray();
            if (all.Length > 256) throw new BridgeException("TraversalLimit", "Project text category inventory exceeds 256.");
            var selected = categoryIds.Select(id => project.TextCategories.Find(id) ?? throw new BridgeException("CategoryUnavailable", "Unknown text category: " + id)).ToArray();
            var owners = ProjectTextOwners(project, ownerPaths);
            string directory = TranslationDirectory(args, "export");
            bool modifiedBefore = project.IsModified;
            var workbooks = new JArray();
            Action<string, string, Action<FileInfo>, JToken> export = (role, overload, call, categories) =>
            {
                var file = new FileInfo(Path.Combine(directory, role.Replace(':', '-') + ".xlsx"));
                call(file);
                workbooks.Add(new JObject { ["role"] = role, ["overload"] = overload, ["categoryIds"] = categories, ["artifact"] = FileArtifact(file) });
            };
            using (_portal.ExclusiveAccess("TIA MCP: read-only project text export"))
            {
                if (owners.Length == 0)
                    foreach (var target in targets)
                        export("full:" + target.Culture.Name, "ExportProjectTexts(FileInfo,CultureInfo,CultureInfo)",
                            file => project.ExportProjectTexts(file, source.Culture, target.Culture), null);
                else
                {
                    export("full", "ExportProjectTexts(FileInfo,Language,IEnumerable<Language>,IEnumerable<TextCategory>,IEnumerable<IEngineeringObject>)",
                        file => project.ExportProjectTexts(file, source, targets, all, owners), new JArray(all.Select(c => c.Identifier)));
                    if (selected.Length != 0)
                        export("filtered", "ExportProjectTexts(FileInfo,Language,IEnumerable<Language>,IEnumerable<TextCategory>,IEnumerable<IEngineeringObject>)",
                            file => project.ExportProjectTexts(file, source, targets, selected, owners), new JArray(categoryIds));
                }
            }
            var result = new JObject
            {
                ["projectPath"] = project.Path.FullName, ["projectName"] = project.Name, ["mode"] = owners.Length == 0 ? "wholeProject" : "owners",
                ["sourceCulture"] = sourceName, ["targetCultures"] = new JArray(targetNames), ["owners"] = new JArray(ownerPaths),
                ["requestedCategoryIds"] = new JArray(categoryIds),
                ["categories"] = new JArray(all.Select(c => new JObject { ["identifier"] = c.Identifier, ["name"] = c.Name })),
                ["workbooks"] = workbooks, ["directory"] = directory, ["modifiedBefore"] = modifiedBefore, ["modifiedAfter"] = project.IsModified,
                ["readOnly"] = true, ["nativeImportExecuted"] = false
            };
            TranslationWrite(Path.Combine(directory, "export.json"), result);
            return result;
        }

        private static string[] ProjectTextList(JObject args, string name, int maximum)
        {
            var values = (args[name] as JArray)?.Select(x => x.Type == JTokenType.String ? x.Value<string>() : null).ToArray() ?? new string[0];
            if (values.Length > maximum || values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new BridgeException("InvalidRequest", name + " requires at most " + maximum + " distinct nonempty values.");
            return values;
        }

        private static Language ProjectTextLanguage(LanguageSettings settings, string name) =>
            settings.ActiveLanguages.FirstOrDefault(language => language.Culture.Name == name) ??
            throw new BridgeException("LanguageInactive", "Culture is not an active project language: " + name);

        // Exact paths as project_inventory reports them: hardware items, PLC software (/plc/) and HMI software (/hmi/).
        private static IEngineeringObject[] ProjectTextOwners(Project project, string[] paths)
        {
            if (paths.Length == 0) return new IEngineeringObject[0];
            var budget = new ScanBudget(50000);
            var found = new Dictionary<string, IEngineeringObject>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            Action<string, IEngineeringObject> add = (path, value) => { if (!found.ContainsKey(path)) found.Add(path, value); else ambiguous.Add(path); };
            foreach (var entry in DeviceItems(project, budget))
            {
                add(entry.Path, entry.Item);
                var software = entry.Item.GetService<SoftwareContainer>()?.Software;
                if (software == null) continue;
                string type = software.GetType().FullName;
                add(entry.Path + (software is PlcSoftware ? "/plc/" : type == "Siemens.Engineering.Hmi.HmiTarget" || type == "Siemens.Engineering.HmiUnified.HmiSoftware" ? "/hmi/" : "/software/") + Segment(software.Name), software);
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Owner lookup reached its traversal limit; completeness cannot be established.");
            return paths.Select(path => ambiguous.Contains(path) ? throw new BridgeException("AmbiguousTarget", "More than one owner has this path: " + path)
                : found.TryGetValue(path, out var owner) ? owner : throw new BridgeException("OwnerNotFound", "No exact hardware, PLC or HMI owner has this path: " + path)).ToArray();
        }
    }
}
