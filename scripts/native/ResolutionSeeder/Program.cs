using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;

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

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) =>
        {
            string name = new AssemblyName(evt.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.None)); return 0; }
        catch (Exception error)
        {
            Console.WriteLine(new JObject { ["completed"] = false, ["error"] = error.GetType().FullName,
                ["message"] = error.Message }.ToString(Formatting.None));
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args)
    {
        if (args.Length != 2) throw new InvalidOperationException("Usage: ResolutionSeeder.exe <artifact-root> <authorization.json>");
        string root = Path.GetFullPath(args[0]);
        if (!Path.IsPathRooted(args[0]) || Path.GetPathRoot(root).TrimEnd('\\') == root.TrimEnd('\\'))
            throw new InvalidOperationException("An explicit bounded absolute artifact root is required.");
        string authorizationPath = Guard(root, args[1]);
        var authorization = JObject.Parse(File.ReadAllText(authorizationPath));
        string operation = authorization.Value<string>("operation");
        if (authorization.Value<bool?>("ownsPortalProcess") != true || authorization.Value<string>("creationTool") != "tia_create_scratch"
            || operation != "duplicate-tag-diagnostic" && operation != "duplicate-db-diagnostic")
            throw new InvalidOperationException("Only an exact owned scratch duplicate-tag or duplicate-DB diagnostic is authorized.");
        string projectPath = Guard(root, authorization.Value<string>("projectPath"));
        if (!Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || Path.GetExtension(projectPath) != ".ap21")
            throw new InvalidOperationException("Only a task-created TiaMcpScratch_ .ap21 may be used.");
        int pid = authorization.Value<int>("processId");
        var process = TiaPortal.GetProcesses().SingleOrDefault(row => row.Id == pid)
            ?? throw new InvalidOperationException("The exact authorized owned process is absent.");
        using (var portal = process.Attach())
        {
            if (portal.Projects.Count != 1 || !string.Equals(portal.Projects[0].Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native attachment does not match the exact scratch receipt.");
            var project = portal.Projects[0];
            var devices = project.Devices.Concat(project.UngroupedDevicesGroup.Devices);
            var plcs = devices.SelectMany(device => Items(device.DeviceItems))
                .Select(item => item.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(item => item != null).ToArray();
            if (plcs.Length != 1) throw new InvalidOperationException("The owned scratch must contain exactly one PLC.");
            var plc = plcs[0];
            if (operation == "duplicate-db-diagnostic") return DuplicateDb(portal, project, plc, projectPath, pid, Path.GetDirectoryName(authorizationPath));
            var tables = plc.TagTableGroup.TagTables;
            var original = tables.Find("Tgp_ResolutionTags") ?? throw new InvalidOperationException("Observed baseline fixture table absent.");
            var ready = original.Tags.Find("Ready") ?? throw new InvalidOperationException("Observed baseline Ready tag absent.");
            if (ready.DataTypeName != "Bool" || ready.LogicalAddress != "%M100.0" || tables.Find("Tgp_ResolutionDuplicate") != null)
                throw new InvalidOperationException("Baseline tag identity/address/type differs or duplicate fixture already exists.");
            using (portal.ExclusiveAccess("TIA MCP owned native duplicate-tag diagnostic"))
            {
                var result = new JObject { ["completed"] = false, ["projectPath"] = projectPath, ["processId"] = pid,
                    ["duplicateCreated"] = false, ["mutationReplayed"] = false };
                var duplicate = tables.Create("Tgp_ResolutionDuplicate");
                result["tableCreated"] = true;
                try
                {
                    duplicate.Tags.Create("Ready", "Bool", "%M101.0");
                    result["duplicateCreated"] = true;
                }
                catch (EngineeringException error)
                {
                    result["nativeRejection"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message };
                }
                var compile = plc.GetService<ICompilable>()?.Compile() ?? throw new InvalidOperationException("No compiler service.");
                result["compileErrors"] = compile.ErrorCount;
                result["compileWarnings"] = compile.WarningCount;
                result["compileMessages"] = new JArray(Messages(compile.Messages).Take(100));
                project.Save();
                result["completed"] = true;
                return result;
            }
        }
    }

    // A same-qualified-name competitor for the root DB: Tgp_ResolutionOne in unit B without a namespace.
    private static JObject DuplicateDb(TiaPortal portal, Project project, PlcSoftware plc, string projectPath, int pid, string directory)
    {
        const string name = "Tgp_ResolutionOne", preset = "TiaMcp_UnitB";
        var root = plc.BlockGroup.Blocks.Find(name) as GlobalDB;
        var unit = plc.GetService<PlcUnitProvider>()?.UnitGroup.Units.Find(preset);
        Func<PlcBlock[]> competitors = () => unit.BlockGroup.Blocks.Where(block => block.Name == name || block.Name == name + "_Renamed").ToArray();
        if (root == null || !string.IsNullOrEmpty(root.Namespace) || unit == null || unit.NamespacePreset != preset || competitors().Length != 0)
            throw new InvalidOperationException("Observed root DB, seeded unit B preset or competitor absence differs.");
        Func<string, string> db = dbName => "DATA_BLOCK \"" + dbName + "\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\nVAR\n Flag : Bool := FALSE;\n RetainedMarker : Int := 91;\nEND_VAR\nBEGIN\nEND_DATA_BLOCK\n";
        using (portal.ExclusiveAccess("TIA MCP owned native duplicate-DB diagnostic"))
        {
            var attempts = new JArray();
            var result = new JObject { ["completed"] = false, ["projectPath"] = projectPath, ["processId"] = pid,
                ["duplicateCreated"] = false, ["mutationReplayed"] = false, ["attempts"] = attempts };
            // Every way to reach the root DB's qualified name: generate it, under an empty preset, by namespace edit, by rename.
            foreach (string route in new[] { "source", "unitPreset", "namespaceEdit", "rename" })
            {
                var attempt = new JObject { ["route"] = route };
                attempts.Add(attempt);
                try
                {
                    if (route == "unitPreset") { unit.NamespacePreset = ""; attempt["presetCleared"] = unit.NamespacePreset ?? ""; }
                    string sourcePath = Path.Combine(directory, "competing-db-" + route + ".scl");
                    File.WriteAllText(sourcePath, route == "namespaceEdit" ? "NAMESPACE " + preset + "\n" + db(name) + "END_NAMESPACE\n" : db(route == "rename" ? name + "_Renamed" : name), new UTF8Encoding(true));
                    var source = unit.ExternalSourceGroup.ExternalSources.CreateFromFile("Tgp_ResolutionCompetitor", sourcePath);
                    try { source.GenerateBlocksFromSource(); } finally { source.Delete(); }
                    var generated = competitors();
                    attempt["generated"] = new JArray(generated.Select(block => (block.Namespace ?? "") + "|" + block.Name));
                    foreach (var block in generated)
                        if (route == "namespaceEdit") block.Namespace = ""; else if (route == "rename") block.Name = name;
                }
                catch (EngineeringException error) { result["nativeRejection"] = attempt["nativeRejection"] = new JObject { ["type"] = error.GetType().FullName, ["message"] = error.Message }; }
                finally { if (route == "unitPreset") unit.NamespacePreset = preset; }
                var created = competitors();
                attempt["observed"] = new JArray(created.Select(block => (block.Namespace ?? "") + "|" + block.Name));
                if (created.Length == 1 && created[0].Name == name && string.IsNullOrEmpty(created[0].Namespace)) { result["duplicateCreated"] = true; break; }
                // No partial competitor survives into the next route or the observed project.
                foreach (var block in created) block.Delete();
                attempt["partialDeleted"] = created.Length;
            }
            result["unitNamespacePreset"] = unit.NamespacePreset;
            var compile = plc.GetService<ICompilable>()?.Compile() ?? throw new InvalidOperationException("No compiler service.");
            result["compileErrors"] = compile.ErrorCount;
            result["compileWarnings"] = compile.WarningCount;
            result["compileMessages"] = new JArray(Messages(compile.Messages).Take(100));
            project.Save();
            result["completed"] = true;
            return result;
        }
    }

    private static string Guard(string root, string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute owned paths are required.");
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the explicit artifact root.");
        for (string cursor = full; !string.IsNullOrEmpty(cursor) && cursor.Length >= root.Length; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse-point artifact paths are forbidden.");
        return full;
    }
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items)
    { foreach (var item in items) { yield return item; foreach (var nested in Items(item.DeviceItems)) yield return nested; } }
    private static IEnumerable<string> Messages(IEnumerable<CompilerResultMessage> messages)
    { foreach (var message in messages) { yield return message.Description; foreach (string nested in Messages(message.Messages)) yield return nested; } }
}
