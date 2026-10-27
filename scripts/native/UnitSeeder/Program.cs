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
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using Siemens.Engineering.SW.Units;

internal static class Program
{
    private static string Api = InstalledApiDirectory();

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
        catch (Exception ex) { Console.WriteLine(new JObject { ["completed"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message }.ToString(Formatting.None)); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args)
    {
        if (args.Length != 2 || (args[0] != "seed" && args[0] != "mutate-other-unit" && args[0] != "add-alternate-namespace" && args[0] != "explorer-relations"))
            throw new InvalidOperationException("Usage: UnitSeeder.exe seed|mutate-other-unit|add-alternate-namespace|explorer-relations <authorization.json>");
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts"));
        string authorizationPath = GuardPath(root, args[1]);
        var authorization = JObject.Parse(File.ReadAllText(authorizationPath));
        if (authorization.Value<bool?>("ownsPortalProcess") != true || authorization.Value<string>("creationTool") != "tia_create_scratch")
            throw new InvalidOperationException("Only a freshly recorded worker-owned tia_create_scratch fixture is eligible.");
        string projectPath = GuardPath(root, authorization.Value<string>("projectPath"));
        if (!Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || Path.GetExtension(projectPath) != ".ap21")
            throw new InvalidOperationException("The target must be a task-created TiaMcpScratch_ .ap21 fixture under repository artifacts.");
        int pid = authorization.Value<int>("processId");
        // Never dispose a TiaPortalProcess: doing so closes the process itself.
        var process = TiaPortal.GetProcesses().SingleOrDefault(candidate => candidate.Id == pid)
            ?? throw new InvalidOperationException("The explicitly authorized process is not running.");
        using (var portal = process.Attach())
        {
            if (portal.Projects.Count != 1) throw new InvalidOperationException("Fixture attachment must contain exactly one project.");
            var project = portal.Projects[0];
            if (!string.Equals(project.Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The attached project does not match the authorized exact path.");
            var plcs = Devices(project).SelectMany(device => Items(device.DeviceItems)).Select(item => item.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(plc => plc != null).ToArray();
            if (plcs.Length != 1) throw new InvalidOperationException("The fixture must contain one PLC.");
            var plc = plcs[0];
            var provider = plc.GetService<PlcUnitProvider>();
            if (provider == null) return new JObject { ["completed"] = false, ["unsupported"] = true, ["reason"] = "Selected CPU exposes no PlcUnitProvider." };
            using (portal.ExclusiveAccess("TIA MCP test fixture: isolated software-unit qualification"))
            {
                if (args[0] == "seed")
                {
                    foreach (string unitName in new[] { "TiaMcp_UnitA", "TiaMcp_UnitB" })
                    {
                        if (provider.UnitGroup.Units.Find(unitName) != null) throw new InvalidOperationException("The test unit already exists; do not overwrite fixtures.");
                        var unit = provider.UnitGroup.Units.Create(unitName);
                        unit.NamespacePreset = unitName;
                        unit.BlockGroup.Groups.Create("Helpers");
                        unit.TypeGroup.Groups.Create("Data");
                        string sourcePath = Path.Combine(Path.GetDirectoryName(authorizationPath), unitName + ".scl");
                        string temporaryName = "TiaMcp_Seed_" + unitName;
                        File.WriteAllText(sourcePath, "FUNCTION \"" + temporaryName + "\" : Bool\nVAR_INPUT\n Enable : Bool;\nEND_VAR\nBEGIN\n #" + temporaryName + " := #Enable;\nEND_FUNCTION\n", new UTF8Encoding(true));
                        unit.ExternalSourceGroup.ExternalSources.CreateFromFile("Fixture_" + unitName, sourcePath).GenerateBlocksFromSource();
                        var block = unit.BlockGroup.Blocks.Find(temporaryName);
                        block.Namespace = unitName;
                        block.Name = "TiaMcp_IsEnabled";
                        unit.ExternalSourceGroup.GenerateSource(new[] { block }, new FileInfo(Path.Combine(Path.GetDirectoryName(authorizationPath), unitName + "-native.scl")), GenerateOptions.None);
                    }
                }
                else if (args[0] == "explorer-relations")
                {
                    // Neither unit relations nor TO hardware connections have a prepared change kind.
                    foreach (string unitName in new[] { "ExplorerUnitA", "ExplorerUnitB" })
                        if (provider.UnitGroup.Units.Find(unitName) != null) throw new InvalidOperationException("The explorer fixture unit already exists; do not overwrite fixtures.");
                    provider.UnitGroup.Units.Create("ExplorerUnitB");
                    provider.UnitGroup.Units.Create("ExplorerUnitA").Relations.Create("ExplorerUnitB", UnitRelationType.SoftwareUnit);
                    string moduleName = authorization.Value<string>("moduleName");
                    var module = Devices(project).SelectMany(device => Items(device.DeviceItems)).Single(item => item.Name == moduleName);
                    var channel = Items(new[] { module }).SelectMany(item => item.Channels)
                        .Single(candidate => candidate.Number == 0 && candidate.Type == ChannelType.Technology && candidate.IoType == ChannelIoType.Complex);
                    // V21 exposes OperatingMode as UInt64 beside a public symbolic enum; resolve that exact enum, never a number.
                    object before = channel.GetAttribute("OperatingMode");
                    object mode = Enum.Parse(before.GetType().IsEnum ? before.GetType() : typeof(DeviceItem).Assembly.GetType("Siemens.Engineering.HW.OperatingMode", true), "MotionControl");
                    channel.SetAttribute("OperatingMode", before.GetType().IsEnum ? mode : Convert.ChangeType(mode, before.GetType()));
                    if (plc.TechnologicalObjectGroup.TechnologicalObjects.Find("ExplorerEncoder") != null) throw new InvalidOperationException("The explorer encoder fixture already exists.");
                    var encoder = plc.TechnologicalObjectGroup.TechnologicalObjects.Create("ExplorerEncoder", "TO_ExternalEncoder", new Version("10.0"));
                    var sensor = encoder.GetService<EncoderHardwareConnectionProvider>().SensorInterface;
                    sensor.Connect(channel);
                    if (!sensor.IsConnected) throw new InvalidOperationException("The encoder sensor interface did not connect to the module channel.");
                }
                else if (args[0] == "add-alternate-namespace")
                {
                    var unit = provider.UnitGroup.Units.Find("TiaMcp_UnitA") ?? throw new InvalidOperationException("Seeded unit A is absent.");
                    if (unit.BlockGroup.Blocks.Any(item => item.Name == "TiaMcp_IsEnabled" && item.Namespace == "TiaMcp_UnitA.Alternate"))
                        throw new InvalidOperationException("The alternate-namespace fixture already exists.");
                    string sourcePath = Path.Combine(Path.GetDirectoryName(authorizationPath), "alternate.scl");
                    File.WriteAllText(sourcePath, "FUNCTION \"TiaMcp_AlternateSeed\" : Bool\nVAR_INPUT\n Enable : Bool;\nEND_VAR\nBEGIN\n #TiaMcp_AlternateSeed := #Enable;\nEND_FUNCTION\n", new UTF8Encoding(true));
                    unit.ExternalSourceGroup.ExternalSources.CreateFromFile("Fixture_Alternate", sourcePath).GenerateBlocksFromSource();
                    var block = unit.BlockGroup.Blocks.Find("TiaMcp_AlternateSeed");
                    block.Namespace = "TiaMcp_UnitA.Alternate";
                    block.Name = "TiaMcp_IsEnabled";
                }
                else
                {
                    var unit = provider.UnitGroup.Units.Find("TiaMcp_UnitB") ?? throw new InvalidOperationException("Seeded unit B is absent.");
                    var block = unit.BlockGroup.Blocks.SingleOrDefault(item => item.Name == "TiaMcp_IsEnabled") ?? throw new InvalidOperationException("Unit B fixture block is absent.");
                    block.Name = "TiaMcp_IsEnabled_BChanged";
                }
                var compile = plc.GetService<ICompilable>()?.Compile() ?? throw new InvalidOperationException("No PLC compiler service.");
                project.Save();
                return new JObject { ["completed"] = compile.ErrorCount == 0, ["mode"] = args[0], ["projectPath"] = project.Path.FullName,
                    ["processId"] = pid, ["compileErrors"] = compile.ErrorCount, ["compileWarnings"] = compile.WarningCount,
                    ["compileMessages"] = new JArray(Messages(compile.Messages).Take(100)),
                    ["units"] = new JArray(provider.UnitGroup.Units.Select(unit => new JObject { ["name"] = unit.Name, ["namespacePreset"] = unit.NamespacePreset,
                        ["blocks"] = new JArray(unit.BlockGroup.Blocks.Select(block => new JObject { ["name"] = block.Name, ["namespace"] = block.Namespace })),
                        ["relations"] = new JArray(unit.Relations.Select(relation => new JObject { ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = relation.RelatedObject })) })) };
            }
        }
    }

    private static string GuardPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("An absolute fixture path is required.");
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture paths must stay under this repository's artifacts directory.");
        string cursor = full;
        while (!string.IsNullOrEmpty(cursor) && cursor.Length >= root.Length)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse-point fixture paths are not accepted.");
            cursor = Path.GetDirectoryName(cursor);
        }
        return full;
    }

    private static IEnumerable<Device> Devices(Project project)
    {
        foreach (var device in project.Devices) yield return device;
        foreach (var device in project.UngroupedDevicesGroup.Devices) yield return device;
    }
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items)
    {
        foreach (var item in items) { yield return item; foreach (var nested in Items(item.DeviceItems)) yield return nested; }
    }
    private static IEnumerable<string> Messages(IEnumerable<CompilerResultMessage> messages)
    {
        foreach (var message in messages) { yield return message.Description; foreach (string nested in Messages(message.Messages)) yield return nested; }
    }
}
