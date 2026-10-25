using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.VersionControl;

internal static class Program
{
    private static readonly string Api = InstalledApiDirectory();

    // Siemens registers the installed V21 API location; C:\Program Files is only the setup default.
    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }

    private static string Output;
    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, evt) =>
        {
            string name = new AssemblyName(evt.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.Indented)); return 0; }
        catch (Exception e)
        {
            var failure = new JObject { ["completed"] = false, ["type"] = e.GetType().FullName, ["message"] = e.Message, ["inner"] = e.InnerException?.Message };
            if (Output != null && Directory.Exists(Output)) Save("failure.json", failure);
            Console.WriteLine(failure.ToString(Formatting.Indented)); return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        if (args.Length != 2) throw new InvalidOperationException("Expected mode and exact admission JSON.");
        var admission = JObject.Parse(File.ReadAllText(args[1]));
        if (admission.Value<string>("mode") != args[0] || admission.Value<string>("binarySha256") != Hash(Assembly.GetExecutingAssembly().Location))
            throw new InvalidOperationException("Admission mode/binary mismatch.");
        Output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        string artifactRoot = Path.GetFullPath(admission.Value<string>("artifactRoot"));
        Guard(artifactRoot, Output);
        if (Directory.Exists(Output)) throw new InvalidOperationException("A fresh evidence directory is required.");
        Directory.CreateDirectory(Output);
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(Output, "ExpansionEvidence.exe"));
        string selfConfig = Assembly.GetExecutingAssembly().Location + ".config";
        if (File.Exists(selfConfig)) File.Copy(selfConfig, Path.Combine(Output, "ExpansionEvidence.exe.config"));
        File.Copy(typeof(JObject).Assembly.Location, Path.Combine(Output, "Newtonsoft.Json.dll"));
        foreach (string source in ((JArray)admission["sourceFiles"] ?? new JArray()).Values<string>())
            File.Copy(source, Path.Combine(Output, "source-" + Path.GetFileName(source)));
        Save("admission.json", admission);
        Save("intent.json", new JObject { ["state"] = "dispatchIntent", ["mode"] = args[0], ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false });
        var process = TiaPortal.GetProcesses().Single(p => p.Id == admission.Value<int>("processId"));
        using (var portal = process.Attach())
        {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, admission.Value<string>("projectPath"), StringComparison.OrdinalIgnoreCase));
            var plcs = Devices(project).SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).ToArray();
            if (args[0] == "owners-read")
            {
                var owners = new JObject { ["projectPath"] = project.Path.FullName, ["processId"] = process.Id, ["plcs"] = new JArray(plcs.Select(p => p.Name)), ["completed"] = true, ["readOnly"] = true };
                Save("result.json", owners); return owners;
            }
            if (plcs.Length != 1) throw new InvalidOperationException("Exact one-PLC fixture required; no owner fallback.");
            var plc = plcs[0];
            if (plc.Name != admission.Value<string>("plcName")) throw new InvalidOperationException("PLC identity mismatch.");
            var owner = Devices(project).SelectMany(d => Items(d.DeviceItems)).Single(i => i.GetService<SoftwareContainer>()?.Software is PlcSoftware);
            var hardware = new JObject { ["ownerName"] = owner.Name };
            foreach (string field in new[] { "TypeIdentifier", "ArticleNumber", "FirmwareVersion", "TypeName", "Version" })
            {
                try { hardware[field] = Value(owner.GetAttribute(field)); }
                catch (Exception e) { hardware[field + "Observation"] = e.GetType().Name + ": " + e.Message; }
            }
            var result = new JObject { ["projectPath"] = project.Path.FullName, ["plcName"] = plc.Name, ["processId"] = process.Id,
                ["hardwareOwner"] = hardware,
                ["mode"] = args[0], ["startedAtUtc"] = DateTime.UtcNow, ["beforeModified"] = project.IsModified,
                ["projectFileBefore"] = ProjectFileEvidence(project.Path.FullName), ["api"] = new JArray(new[] { "Base", "Step7", "TestSuite" }.Select(n => Artifact(Path.Combine(Api, "Siemens.Engineering." + n + ".dll")))) };
            using (portal.ExclusiveAccess("TIA MCP: bounded offline expansion evidence"))
            {
                if (args[0] == "watch-read") result["watch"] = Watch(plc, admission);
                else if (args[0] == "fixture-seed")
                {
                    if (admission.Value<bool?>("ownedScratch") != true || admission.Value<string>("creationTool") != "tia_create_scratch" ||
                        !project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || project.IsModified)
                        throw new InvalidOperationException("Only the saved, owned scratch fixture may be seeded.");
                    Guard(artifactRoot, project.Path.FullName);
                    project.Archive(new DirectoryInfo(Output), "before-seed.zap21", ProjectArchivationMode.Compressed);
                    result["checkpoint"] = Artifact(Path.Combine(Output, "before-seed.zap21"));
                    Save("checkpoint.json", result);
                    var german = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("de-DE"));
                    var english = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("en-GB"));
                    if (!project.LanguageSettings.ActiveLanguages.Contains(english)) project.LanguageSettings.ActiveLanguages.Add(english);
                    var oracle = new JArray();
                    foreach (string name in new[] { "TiaMcp_IsEnabled", "TiaMcp_EnableGate" })
                    {
                        var block = plc.BlockGroup.Blocks.Find(name);
                        block.Comment.Items.Find(german).Text = "Prueftext " + name + " {0}";
                        block.Comment.Items.Find(english).Text = "Verification text " + name + " {0}";
                        oracle.Add(new JObject { ["block"] = name, ["de-DE"] = "Prueftext " + name + " {0}", ["en-GB"] = "Verification text " + name + " {0}" });
                    }
                    project.Save(); result["fixtureOracle"] = oracle;
                }
                else if (args[0] == "translations-read") result["translations"] = Translations(project, plc);
                else if (args[0] == "translations-category-read") result["translations"] = TranslationCategoryProbe.Run(project, plc, owner, Output);
                else if (args[0] == "translations-populated-category-read") result["translations"] = TranslationPopulatedCategoryProbe.Run(project, plc, owner, Output);
                else if (args[0] == "testsuite-read") result["testSuite"] = TestSuite(project);
                else if (args[0] == "vci-read") result["vci"] = Vci(project);
                else if (args[0] == "vci-export")
                {
                    if (admission.Value<bool?>("ownedScratch") != true || admission.Value<string>("creationTool") != "tia_create_scratch" ||
                        !project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || project.IsModified)
                        throw new InvalidOperationException("VCI fixture export requires the exact saved owned scratch.");
                    Guard(artifactRoot, project.Path.FullName);
                    project.Archive(new DirectoryInfo(Output), "before-vci.zap21", ProjectArchivationMode.Compressed);
                    result["checkpoint"] = Artifact(Path.Combine(Output, "before-vci.zap21")); Save("checkpoint.json", result);
                    result["vci"] = VciExport(project, plc);
                    project.Save();
                }
                else throw new InvalidOperationException("Unknown read-only evidence mode.");
            }
            result["afterModified"] = project.IsModified;
            result["projectFileAfter"] = ProjectFileEvidence(project.Path.FullName);
            bool writes = args[0] == "fixture-seed" || args[0] == "vci-export";
            result["readOnly"] = !writes; result["saved"] = writes; result["onlineConnected"] = false;
            result["completed"] = true; result["finishedAtUtc"] = DateTime.UtcNow;
            Save("result.json", result); return result;
        }
    }

    private static JObject Watch(PlcSoftware plc, JObject admission)
    {
        var group = plc.WatchAndForceTableGroup;
        var groups = Groups(group).ToArray();
        var inventory = new JArray(); var exports = new JArray();
        var requested = ((JArray)admission["tableNames"]).Values<string>().ToArray();
        if (requested.Length > 8 || requested.Distinct().Count() != requested.Length) throw new InvalidOperationException("Invalid table request bound.");
        int matched = 0;
        foreach (var g in groups)
        {
            foreach (var watch in g.WatchTables)
            {
                inventory.Add(new JObject { ["kind"] = "watch", ["group"] = g.Name, ["name"] = watch.Name, ["rows"] = watch.Entries.Count });
                if (!requested.Contains(watch.Name, StringComparer.Ordinal)) continue;
                matched++;
                if (watch.Entries.Count > 200) throw new InvalidOperationException("Watch row bound exceeded.");
                string file = Path.Combine(Output, "watch-" + matched + ".xml");
                watch.Export(new FileInfo(file), ExportOptions.WithDefaults);
                exports.Add(new JObject { ["kind"] = "watch", ["group"] = g.Name, ["name"] = watch.Name, ["isConsistent"] = watch.IsConsistent,
                    ["attributes"] = Attributes(watch), ["entries"] = new JArray(watch.Entries.Select(Entry)), ["artifact"] = Artifact(file), ["exportOptions"] = "WithDefaults" });
            }
            foreach (var force in g.ForceTables)
            {
                inventory.Add(new JObject { ["kind"] = "force", ["group"] = g.Name, ["name"] = force.Name, ["rows"] = force.Entries.Count });
                if (!requested.Contains(force.Name, StringComparer.Ordinal)) continue;
                matched++;
                if (force.Entries.Count > 200) throw new InvalidOperationException("Force row bound exceeded.");
                string file = Path.Combine(Output, "force-" + matched + ".xml");
                force.Export(new FileInfo(file), ExportOptions.WithDefaults);
                exports.Add(new JObject { ["kind"] = "force", ["group"] = g.Name, ["name"] = force.Name, ["isConsistent"] = force.IsConsistent,
                    ["attributes"] = Attributes(force), ["entries"] = new JArray(force.Entries.Select(Entry)), ["artifact"] = Artifact(file), ["exportOptions"] = "WithDefaults" });
            }
        }
        return new JObject { ["inventory"] = inventory, ["exports"] = exports, ["requested"] = new JArray(requested), ["matched"] = matched,
            ["allRequestedFound"] = requested.All(n => exports.Any(e => e.Value<string>("name") == n)), ["groupAttributes"] = Attributes(group) };
    }

    private static JObject Entry(PlcTableCommentEntry entry)
    {
        var result = new JObject { ["runtimeType"] = entry.GetType().FullName, ["attributes"] = Attributes(entry) };
        foreach (string name in new[] { "Name", "Address", "DisplayFormat", "ModifyIntention", "ModifyTrigger", "ModifyValue", "MonitorTrigger", "ForceIntention", "ForceValue" })
        {
            var property = entry.GetType().GetProperty(name);
            if (property != null) result[name] = Value(property.GetValue(entry));
        }
        return result;
    }

    private static JArray Attributes(IEngineeringObject value)
    {
        var result = new JArray();
        foreach (var info in value.GetAttributeInfos().Take(128))
        {
            var item = new JObject { ["name"] = info.Name, ["access"] = info.AccessMode.ToString() };
            try { item["value"] = Value(value.GetAttribute(info.Name)); }
            catch (Exception e) { item["readFailure"] = e.GetType().Name + ": " + e.Message; }
            result.Add(item);
        }
        return result;
    }

    private static JToken Value(object value)
    {
        if (value == null) return JValue.CreateNull();
        if (value is MultilingualText text) return new JArray(text.Items.Take(32).Select(i => new JObject { ["culture"] = i.Language.Culture.Name, ["text"] = i.Text }));
        return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static JObject Translations(Project project, PlcSoftware plc)
    {
        var settings = project.LanguageSettings;
        var result = new JObject { ["activeLanguages"] = new JArray(settings.ActiveLanguages.Select(l => l.Culture.Name)),
            ["referenceLanguage"] = settings.ReferenceLanguage.Culture.Name, ["editingLanguage"] = settings.EditingLanguage.Culture.Name,
            ["categories"] = new JArray(project.TextCategories.Select(c => new JObject { ["id"] = c.Identifier, ["name"] = c.Name })) };
        var source = settings.ReferenceLanguage;
        var target = settings.ActiveLanguages.FirstOrDefault(l => l.Culture.Name != source.Culture.Name);
        if (target == null) { result["state"] = "blocked"; result["reason"] = "No distinct active target language; read-only capture does not activate one."; return result; }
        var exports = new JArray();
        foreach (string mode in new[] { "legacy", "filtered-all", "filtered-plc", "filtered-block", "filtered-category" })
        {
            string file = Path.Combine(Output, mode + ".xlsx");
            try
            {
                if (mode == "legacy") project.ExportProjectTexts(new FileInfo(file), source.Culture, target.Culture);
                else project.ExportProjectTexts(new FileInfo(file), source, new[] { target },
                    mode == "filtered-category" ? new[] { project.TextCategories.Find("HwcnCommentTextCategoryData") } : project.TextCategories.ToArray(),
                    mode == "filtered-all" ? new IEngineeringObject[0] : mode == "filtered-block" ? new IEngineeringObject[] { plc.BlockGroup.Blocks.Find("TiaMcp_IsEnabled") } : new IEngineeringObject[] { plc });
                exports.Add(new JObject { ["mode"] = mode, ["source"] = source.Culture.Name, ["target"] = target.Culture.Name, ["artifact"] = Artifact(file) });
            }
            catch (Exception e) { exports.Add(new JObject { ["mode"] = mode, ["failure"] = e.GetType().FullName, ["message"] = e.Message }); }
        }
        result["exports"] = exports; return result;
    }

    private static JObject TestSuite(Project project)
    {
        var service = project.GetService<TestSuiteService>();
        if (service == null) return new JObject { ["state"] = "absent", ["license"] = "unknown" };
        var result = new JObject { ["state"] = "observed", ["license"] = "unknown", ["definitions"] = new JArray() };
        var definitions = (JArray)result["definitions"];
        foreach (var pair in new[] { Tuple.Create("style", (IEnumerable)service.StyleGuideGroup.RuleSets),
            Tuple.Create("application", (IEnumerable)service.ApplicationTestGroup.TestCases), Tuple.Create("application-set", (IEnumerable)service.ApplicationTestGroup.ApplicationTestSets),
            Tuple.Create("system", (IEnumerable)service.SystemTestGroup.SystemTestCases) })
        {
            int count = 0;
            foreach (object definition in pair.Item2)
            {
                if (++count > 100) throw new InvalidOperationException("Definition bound exceeded.");
                definitions.Add(new JObject { ["family"] = pair.Item1, ["name"] = definition.GetType().GetProperty("Name").GetValue(definition).ToString(),
                    ["type"] = definition.GetType().FullName,
                    ["exportMethods"] = new JArray(definition.GetType().GetMethods().Where(m => m.Name == "Export" || m.Name == "SaveToFile").Select(m => m.ToString())) });
            }
        }
        return result;
    }

    private static JObject VciExport(Project project, PlcSoftware plc)
    {
        var service = project.GetService<VersionControlInterface>() ?? throw new InvalidOperationException("VCI service absent.");
        string root = Path.Combine(Output, "workspaces"); Directory.CreateDirectory(root);
        string prefix = "Evidence_" + Path.GetFileName(Output).Replace('-', '_');
        var probe = service.WorkspaceGroup.Workspaces.Create(prefix + "_Formats", new DirectoryInfo(root));
        var results = new JArray(); int index = 0;
        foreach (string name in new[] { "TiaMcp_IsEnabled", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "Main" })
        {
            var block = plc.BlockGroup.Blocks.Find(name);
            var formats = probe.GetSupportedFileFormats(block).ToArray();
            if (formats.Length > 8) throw new InvalidOperationException("VCI format bound exceeded.");
            foreach (string format in formats)
            {
                var item = new JObject { ["block"] = name, ["format"] = format };
                string folder = Path.Combine(root, "profile-" + (++index)); Directory.CreateDirectory(folder);
                var workspace = service.WorkspaceGroup.Workspaces.Create(prefix + "_Profile_" + index, new DirectoryInfo(folder));
                string previous = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = folder;
                    var mapped = workspace.ExportObject(block, new DirectoryInfo(folder), "fixture", format);
                    var status = mapped.GetStatus();
                    item["state"] = "exported"; item["nativeCompareState"] = status.CompareState.ToString();
                    item["mappedFormat"] = mapped.FileFormat; item["mappedDirectory"] = mapped.DirectoryPath.ToString();
                    var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                    if (files.Length > 32) throw new InvalidOperationException("VCI companion bound exceeded.");
                    item["artifacts"] = new JArray(files.Select(Artifact));
                }
                catch (Exception e) { item["state"] = "failed"; item["failure"] = e.GetType().FullName; item["message"] = e.Message; }
                finally { Environment.CurrentDirectory = previous; }
                results.Add(item); Save("vci-progress.json", new JObject { ["profiles"] = results });
            }
        }
        return new JObject { ["profiles"] = results, ["nativeSynchronizationInvoked"] = false, ["scope"] = "Four authored scratch blocks; formats reported by native Workspace.GetSupportedFileFormats" };
    }

    private static JObject Vci(Project project)
    {
        var service = project.GetService<VersionControlInterface>();
        if (service == null) return new JObject { ["state"] = "absent" };
        return new JObject { ["state"] = "observed", ["workspaces"] = new JArray(service.WorkspaceGroup.Workspaces.Take(20).Select(w => new JObject {
            ["name"] = w.Name, ["rootPath"] = w.RootPath.ToString(), ["mappings"] = new JArray(w.MappedObjects.Take(100).Select(m => new JObject {
                ["fileFormat"] = m.FileFormat, ["fileName"] = m.FileNameWithoutExtension, ["directory"] = m.DirectoryPath.ToString(), ["status"] = m.GetStatus().CompareState.ToString() })) })) };
    }

    private static IEnumerable<PlcWatchAndForceTableGroup> Groups(PlcWatchAndForceTableGroup root, int depth = 0)
    {
        if (depth > 8 || root.Groups.Count > 50 || root.WatchTables.Count > 100) throw new InvalidOperationException("Watch group bound exceeded.");
        yield return root;
        foreach (var g in root.Groups) foreach (var nested in Groups(g, depth + 1)) yield return nested;
    }
    private static IEnumerable<Device> Devices(Project p) => p.Devices.Concat(p.UngroupedDevicesGroup.Devices);
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items)
    { foreach (var item in items) { yield return item; foreach (var child in Items(item.DeviceItems)) yield return child; } }
    private static void Guard(string root, string path)
    {
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Evidence must remain under artifacts.");
        for (string p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("No reparse points.");
    }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string path) => new JObject { ["path"] = path, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length };
    private static JObject ProjectFileEvidence(string path)
    {
        try { return Artifact(path); }
        catch (IOException e) { return new JObject { ["path"] = path, ["hashState"] = "unavailable-native-file-lock", ["reason"] = e.Message }; }
    }
    private static void Save(string name, JObject value) => TiaMcp.Openness.V21.LibraryJournalStore.Write(Path.Combine(Output, name), value);
}
