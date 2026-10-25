using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.VersionControl;

// Experimental collector only. No synchronize, block import, language write, online or runtime route exists.
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

    private static string output;
    private static int sequence;
    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.Indented)); return 0; }
        catch (Exception e)
        {
            var failure = new JObject { ["completed"] = false, ["automaticReplay"] = false,
                ["type"] = e.GetType().FullName, ["message"] = e.Message, ["inner"] = e.InnerException?.Message };
            if (output != null) Save("failure.json", failure);
            Console.WriteLine(failure.ToString(Formatting.Indented)); return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        Need(args.Length == 2 && (args[0] == "export" || args[0] == "snapshot"), "Expected export|snapshot and admission JSON.");
        var admission = JObject.Parse(File.ReadAllText(args[1]));
        Need(admission.Value<string>("mode") == args[0] && admission.Value<string>("binarySha256") == Hash(Assembly.GetExecutingAssembly().Location), "Admission binary/mode mismatch.");
        Need(admission.Value<bool?>("ownedScratch") == true && admission.Value<bool?>("sequentialNativeAdmission") == true &&
            admission.Value<bool?>("checkpointRetrievalFoundationPassed") == true, "Current owned scratch, exclusive admission and recovery foundation are required.");
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot"));
        string requestedOutput = Path.GetFullPath(admission.Value<string>("outputDirectory")); Guard(root, requestedOutput);
        string projectPath = Path.GetFullPath(admission.Value<string>("projectPath")); Guard(root, projectPath);
        Need(!Directory.Exists(requestedOutput) && !File.Exists(requestedOutput), "Fresh output required: existing intent must be reconciled, never replayed.");
        var sources = (JArray)admission["sourceFiles"];
        Need(sources != null && sources.Count >= 2 && sources.Count <= 8, "Reviewed source inventory required.");
        foreach (JObject source in sources) CheckArtifact(source);
        var checkpoint = (JObject)admission["checkpoint"]; CheckArtifact(checkpoint); Guard(root, checkpoint.Value<string>("path"));
        CheckArtifact((JObject)admission["hostBinary"]); CheckArtifact((JObject)admission["workerBinary"]);
        CheckArtifact((JObject)admission["recoveryFoundationEvidence"]);
        var oracleFile = (JObject)admission["oracle"]; CheckArtifact(oracleFile);
        var oracle = JObject.Parse(File.ReadAllText(oracleFile.Value<string>("path")));
        Need(oracle.Value<string>("projectPath") == projectPath && oracle.Value<string>("plcName") == admission.Value<string>("plcName"), "Current independent oracle binding differs.");
        Need((oracle["blocks"] as JArray)?.Count == 2, "Exactly two reviewed oracle blocks required.");
        int pid = admission.Value<int>("processId");
        using (var process = Process.GetProcessById(pid))
            Need(process.StartTime.ToUniversalTime().ToString("O") == admission.Value<string>("processStartedAtUtc"), "Native PID creation-time mismatch.");
        Directory.CreateDirectory(requestedOutput); output = requestedOutput;
        Save("admission.json", admission);
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(output, "collector.exe"));
        File.Copy(typeof(JObject).Assembly.Location, Path.Combine(output, "Newtonsoft.Json.dll"));
        foreach (JObject source in sources) File.Copy(source.Value<string>("path"), Path.Combine(output, "source-" + Path.GetFileName(source.Value<string>("path"))));
        Save("oracle.json", oracle);
        Event("attachIntent", new JObject { ["processId"] = pid, ["projectPath"] = projectPath });
        var native = TiaPortal.GetProcesses().Single(p => p.Id == pid);
        using (var portal = native.Attach())
        using (portal.ExclusiveAccess("TIA MCP: bounded VCI preservation discrimination"))
        {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase));
            Need(project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) && !project.IsModified, "Exact saved owned scratch required.");
            var plcs = Devices(project).SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).ToArray();
            Need(plcs.Length == 1 && plcs[0].Name == admission.Value<string>("plcName"), "Exact single PLC fixture required.");
            var plc = plcs[0];
            var before = Snapshot(portal, project, plc, "before"); Save("before.json", before);
            RequireOracle(before, oracle);
            var result = new JObject { ["mode"] = args[0], ["processId"] = pid, ["projectPath"] = projectPath,
                ["plcName"] = plc.Name, ["checkpoint"] = checkpoint.DeepClone(), ["oracle"] = oracleFile.DeepClone(),
                ["nativeImportQualified"] = false, ["wholeProjectPreservationQualified"] = false,
                ["scope"] = "boundedBlockXmlCommentsLanguagesVciMappings", ["automaticReplay"] = false };
            if (args[0] == "export")
            {
                result["profiles"] = Export(project, plc, oracle, admission.Value<string>("workspacePrefix"));
                Event("saveIntent", new JObject()); project.Save(); Event("saveReturned", new JObject());
                var after = Snapshot(portal, project, plc, "after"); Save("after.json", after);
                RequireOracle(after, oracle);
                result["blockAndLanguagePeersUnchanged"] = SamePeers(before, after);
                result["preexistingMappingsUnchanged"] = ExistingMappingsUnchanged(before, after);
                Need(result.Value<bool>("blockAndLanguagePeersUnchanged") && result.Value<bool>("preexistingMappingsUnchanged"), "Export changed preexisting bounded peers.");
            }
            Need(!project.IsModified, "Saved/read-only final state is modified.");
            result["completed"] = true; result["saved"] = args[0] == "export";
            result["freshReopenVerified"] = false; result["checkpointRestorationVerified"] = false;
            Save("result.json", result); return result;
        }
    }

    private static JArray Export(Project project, PlcSoftware plc, JObject oracle, string prefix)
    {
        Need(!string.IsNullOrWhiteSpace(prefix) && prefix.Length <= 40 && prefix.All(c => char.IsLetterOrDigit(c) || c == '_'), "Unique bounded workspace prefix required.");
        var service = project.GetService<VersionControlInterface>() ?? throw new InvalidOperationException("VCI service absent.");
        Need(!WorkspaceGroups(service.WorkspaceGroup, 0).SelectMany(g => g.Workspaces).Any(w => w.Name.StartsWith(prefix, StringComparison.Ordinal)), "Workspace prefix already exists; reconcile before replay.");
        var results = new JArray(); int n = 0;
        foreach (JObject expected in (JArray)oracle["blocks"])
        {
            string name = expected.Value<string>("name"); var block = plc.BlockGroup.Blocks.Find(name);
            Need(block != null, "Selected block must be in exact root group.");
            foreach (string culture in new[] { "de-DE", "en-GB" })
            foreach (string format in new[] { "SCL", "SimaticML", "SimaticSD" })
            {
                string directory = Path.Combine(output, "workspaces", "profile-" + (++n).ToString("00")); Directory.CreateDirectory(directory);
                string workspaceName = prefix + "_" + n.ToString("00");
                var row = new JObject { ["index"] = n, ["block"] = name, ["culture"] = culture, ["format"] = format, ["workspace"] = workspaceName, ["directory"] = directory };
                results.Add(row); Event("workspaceCreateIntent", row);
                var workspace = service.WorkspaceGroup.Workspaces.Create(workspaceName, new DirectoryInfo(directory), CultureInfo.GetCultureInfo(culture));
                Event("workspaceCreated", new JObject { ["name"] = workspace.Name, ["culture"] = workspace.WorkspaceLanguage.Name });
                var supported = workspace.GetSupportedFileFormats(block).ToArray(); row["supportedFormats"] = new JArray(supported);
                Need(supported.Length <= 8 && supported.Contains(format, StringComparer.Ordinal), "Required native format unavailable.");
                Event("exportIntent", row);
                try
                {
                    var mapped = workspace.ExportObject(block, new DirectoryInfo(directory), "fixture", format);
                    row["mapping"] = Mapping(mapped); row["workspaceLanguage"] = workspace.WorkspaceLanguage.Name;
                    Need(mapped.EngineeringObject.Equals(block), "Export mapping did not reacquire the exact selected native object.");
                    Need(workspace.WorkspaceLanguage.Name == culture, "Native workspace language differs.");
                    row["state"] = "exported";
                }
                catch (Exception e) { row["state"] = "failed"; row["type"] = e.GetType().FullName; row["message"] = e.Message; }
                row["files"] = Inventory(directory); Event("exportReturned", row);
            }
            string direct = Path.Combine(output, "direct-sd", name); Directory.CreateDirectory(direct);
            var directRow = new JObject { ["block"] = name, ["format"] = "SimaticSD", ["route"] = "PlcBlock.ExportAsDocuments", ["directory"] = direct };
            results.Add(directRow); Event("directExportIntent", directRow);
            try
            {
                var exported = block.ExportAsDocuments(new DirectoryInfo(direct), "fixture");
                directRow["nativeResult"] = PublicResult(exported, 0); directRow["state"] = "returned";
            }
            catch (Exception e) { directRow["state"] = "failed"; directRow["type"] = e.GetType().FullName; directRow["message"] = e.Message; }
            directRow["files"] = Inventory(direct); Event("directExportReturned", directRow);
        }
        return results;
    }

    private static JObject Snapshot(TiaPortal portal, Project project, PlcSoftware plc, string label)
    {
        string directory = Path.Combine(output, label); Directory.CreateDirectory(directory);
        var blocks = new JArray(); int index = 0;
        foreach (var group in BlockGroups(plc.BlockGroup, 0))
        foreach (var block in group.Blocks.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            Need(++index <= 32, "Block peer bound exceeded.");
            string path = Path.Combine(directory, "block-" + index.ToString("00") + ".xml"); block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            var doc = ReadXml(path); var node = doc.Root.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            blocks.Add(new JObject { ["identity"] = Identity(block), ["name"] = block.Name, ["number"] = block.Number, ["kind"] = node.Name.LocalName.Substring("SW.Blocks.".Length),
                ["comments"] = Texts(block.Comment), ["xml"] = Artifact(path),
                ["blockXmlSha256"] = HashBytes(Encoding.UTF8.GetBytes(node.ToString(SaveOptions.DisableFormatting))) });
        }
        var languages = project.LanguageSettings;
        var vci = project.GetService<VersionControlInterface>() ?? throw new InvalidOperationException("VCI service absent.");
        var workspaces = new JArray();
        foreach (var group in WorkspaceGroups(vci.WorkspaceGroup, 0))
        foreach (var workspace in group.Workspaces.OrderBy(w => w.Name, StringComparer.Ordinal))
        {
            Need(workspaces.Count < 64 && workspace.MappedObjects.Count <= 128, "Mapping peer bound exceeded.");
            workspaces.Add(new JObject { ["identity"] = Identity(workspace), ["name"] = workspace.Name, ["root"] = workspace.RootPath.FullName,
                ["culture"] = workspace.WorkspaceLanguage.Name, ["mappings"] = new JArray(workspace.MappedObjects.Select(Mapping)) });
        }
        var settingsFolder = portal.SettingsFolders.Find("VCI") ?? throw new InvalidOperationException("VCI settings folder absent.");
        var languageOption = settingsFolder.Settings.Find("ProjectLanguageImportOptions") ?? throw new InvalidOperationException("VCI language import setting absent.");
        return new JObject { ["projectPath"] = project.Path.FullName, ["plcName"] = plc.Name, ["blocks"] = blocks,
            ["projectLanguageImportOptions"] = Convert.ToString(languageOption.Value, CultureInfo.InvariantCulture),
            ["languages"] = new JObject { ["active"] = new JArray(languages.ActiveLanguages.Select(l => l.Culture.Name).OrderBy(x => x, StringComparer.Ordinal)),
                ["reference"] = languages.ReferenceLanguage.Culture.Name, ["editing"] = languages.EditingLanguage.Culture.Name }, ["workspaces"] = workspaces };
    }

    private static void RequireOracle(JObject snapshot, JObject oracle)
    {
        foreach (JObject expected in (JArray)oracle["blocks"])
        {
            var actual = ((JArray)snapshot["blocks"]).OfType<JObject>().Single(b => b.Value<string>("name") == expected.Value<string>("name"));
            Need(actual.Value<string>("kind") == expected.Value<string>("kind") && actual.Value<int>("number") == expected.Value<int>("number"), "Oracle block identity mismatch.");
            var comments = (JArray)expected["comments"];
            Need(comments != null && comments.Count == 2 && comments.Values<JObject>().Select(c => c.Value<string>("culture")).OrderBy(x => x).SequenceEqual(new[] { "de-DE", "en-GB" }), "Exact bilingual independent oracle required.");
            foreach (JObject comment in comments)
                Need(((JArray)actual["comments"]).Count(c => JToken.DeepEquals(c, comment)) == 1 && !string.IsNullOrEmpty(comment.Value<string>("text")), "Independent comment oracle mismatch or empty text.");
        }
    }
    private static JArray Texts(MultilingualText text) => new JArray(text.Items.Select(i => new JObject { ["culture"] = i.Language.Culture.Name, ["text"] = i.Text }).OrderBy(x => x.Value<string>("culture"), StringComparer.Ordinal));
    private static JObject Mapping(MappedObject mapped) => new JObject { ["identity"] = Identity(mapped.EngineeringObject), ["format"] = mapped.FileFormat,
        ["directory"] = mapped.DirectoryPath.FullName, ["fileName"] = mapped.FileNameWithoutExtension, ["compareState"] = mapped.GetStatus().CompareState.ToString() };
    private static JArray Identity(IEngineeringObject value)
    {
        var result = new JArray(); int depth = 0;
        for (var current = value; current != null; current = current.Parent)
        {
            Need(++depth <= 32, "Native identity depth exceeded.");
            var entry = new JObject { ["type"] = current.GetType().FullName };
            foreach (var info in current.GetAttributeInfos().Where(i => i.Name == "Name" || i.Name == "Number")) entry[info.Name] = Convert.ToString(current.GetAttribute(info.Name), CultureInfo.InvariantCulture);
            result.AddFirst(entry);
        }
        return result;
    }
    private static bool SamePeers(JObject before, JObject after)
    {
        Func<JObject, JArray> peers = s => new JArray(((JArray)s["blocks"]).Select(b => new JObject { ["identity"] = b["identity"].DeepClone(), ["comments"] = b["comments"].DeepClone(), ["xml"] = b["blockXmlSha256"] }));
        return JToken.DeepEquals(peers(before), peers(after)) && JToken.DeepEquals(before["languages"], after["languages"]) &&
            JToken.DeepEquals(before["projectLanguageImportOptions"], after["projectLanguageImportOptions"]);
    }
    private static bool ExistingMappingsUnchanged(JObject before, JObject after) => ((JArray)before["workspaces"]).All(w => ((JArray)after["workspaces"]).Count(a => JToken.DeepEquals(w, a)) == 1);
    private static IEnumerable<PlcBlockGroup> BlockGroups(PlcBlockGroup group, int depth)
    { Need(depth <= 8 && group.Groups.Count <= 16, "Block group bound exceeded."); yield return group; foreach (var child in group.Groups) foreach (var nested in BlockGroups(child, depth + 1)) yield return nested; }
    private static IEnumerable<WorkspaceGroup> WorkspaceGroups(WorkspaceGroup group, int depth)
    { Need(depth <= 8 && group.Groups.Count <= 16, "Workspace group bound exceeded."); yield return group; foreach (var child in group.Groups) foreach (var nested in WorkspaceGroups(child, depth + 1)) yield return nested; }
    private static IEnumerable<Device> Devices(Project project)
    { Need(project.DeviceGroups.Count == 0, "Nested device groups are outside this bounded fixture."); return project.Devices.Concat(project.UngroupedDevicesGroup.Devices); }
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items, int depth = 0)
    { Need(depth <= 16, "Device nesting bound exceeded."); foreach (var item in items) { yield return item; foreach (var nested in Items(item.DeviceItems, depth + 1)) yield return nested; } }
    private static JToken PublicResult(object value, int depth)
    {
        Need(depth <= 8, "Document result nesting bound exceeded.");
        if (value == null) return JValue.CreateNull();
        if (value is string || value.GetType().IsPrimitive || value.GetType().IsEnum) return new JValue(Convert.ToString(value, CultureInfo.InvariantCulture));
        if (value is FileSystemInfo file) return new JValue(file.FullName);
        if (value is IEnumerable list)
        { var result = new JArray(); foreach (var item in list) { Need(result.Count < 256, "Document result collection bound exceeded."); result.Add(PublicResult(item, depth + 1)); } return result; }
        var properties = new JObject { ["runtimeType"] = value.GetType().FullName };
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.Name != "Parent" && p.GetIndexParameters().Length == 0).Take(32))
            properties[property.Name] = PublicResult(property.GetValue(value), depth + 1);
        return properties;
    }
    private static XDocument ReadXml(string path)
    { using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16777216 })) return XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
    private static JArray Inventory(string directory)
    {
        var paths = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Need(paths.Length <= 128 && paths.Sum(p => new FileInfo(p).Length) <= 16777216, "Package inventory exceeds bounds.");
        return new JArray(paths.Select(p => { Guard(directory, p); var row = Artifact(p); row["relativePath"] = p.Substring(directory.Length + 1).Replace('\\', '/'); return row; }));
    }
    private static void Guard(string root, string path)
    {
        path = Path.GetFullPath(path); root = Path.GetFullPath(root);
        Need(path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Path escapes admitted artifact root.");
        for (string p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            Need(!(File.Exists(p) || Directory.Exists(p)) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "Reparse-point path refused.");
    }
    private static void CheckArtifact(JObject artifact)
    { Need(artifact != null && File.Exists(artifact.Value<string>("path")) && artifact.Value<string>("sha256") == Hash(artifact.Value<string>("path")), "Reviewed artifact bytes mismatch."); }
    private static JObject Artifact(string path) => new JObject { ["path"] = Path.GetFullPath(path), ["bytes"] = new FileInfo(path).Length, ["sha256"] = Hash(path) };
    private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
    private static string HashBytes(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static void Need(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Event(string stage, JObject data) => Save("event-" + (++sequence).ToString("000") + "-" + stage + ".json", new JObject { ["stage"] = stage, ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false, ["data"] = data.DeepClone() });
    private static void Save(string name, JObject value)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(value.ToString(Formatting.Indented));
        using (var stream = new FileStream(Path.Combine(output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
    }
}
