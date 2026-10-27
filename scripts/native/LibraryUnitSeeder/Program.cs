using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;

/// <summary>
/// Library scope fixtures in a worker-owned scratch project: one empty software unit (default), two related
/// units ("related"), published unit objects ("publish") or know-how protection of one block ("protect").
/// Unlike UnitSeeder, the guard root is the authorization file's own directory, so short artifact roots work.
/// </summary>
internal static class Program
{
    private const string UnitName = "TiaMcp_LibUnit", ProviderUnit = "TiaMcp_LibProvider", ConsumerUnit = "TiaMcp_LibConsumer";
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
        string mode = args.Length == 1 ? "unit" : args.Length > 1 ? args[0] : null;
        if (!new[] { "unit", "related", "publish", "protect" }.Contains(mode) || mode == "unit" && args.Length != 1
            || mode == "related" && args.Length != 2 || mode == "protect" && args.Length != 3 || mode == "publish" && args.Length < 4)
            throw new InvalidOperationException("Usage: LibraryUnitSeeder.exe <authorization.json> | related <authorization.json> | publish <authorization.json> <unit> <object>... | protect <authorization.json> <block>");
        string authorizationPath = Path.GetFullPath(args[mode == "unit" ? 0 : 1]);
        string root = Path.GetDirectoryName(authorizationPath);
        var authorization = JObject.Parse(File.ReadAllText(authorizationPath));
        if (authorization.Value<bool?>("ownsPortalProcess") != true || authorization.Value<string>("creationTool") != "tia_create_scratch")
            throw new InvalidOperationException("Only a freshly recorded worker-owned tia_create_scratch fixture is eligible.");
        string projectPath = Path.GetFullPath(authorization.Value<string>("projectPath"));
        if (!projectPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || Path.GetExtension(projectPath) != ".ap21")
            throw new InvalidOperationException("The target must be a task-created TiaMcpScratch_ .ap21 fixture below the authorization directory.");
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
            var provider = plcs[0].GetService<PlcUnitProvider>();
            if (provider == null) return new JObject { ["completed"] = false, ["unsupported"] = true, ["reason"] = "Selected CPU exposes no PlcUnitProvider." };
            using (portal.ExclusiveAccess("TIA MCP test fixture: library software-unit scope"))
            {
                var result = new JObject { ["mode"] = mode, ["projectPath"] = project.Path.FullName, ["processId"] = pid };
                if (mode == "unit")
                {
                    var unit = CreateUnit(provider, UnitName);
                    result["unit"] = unit.Name; result["namespacePreset"] = unit.NamespacePreset;
                }
                else if (mode == "related")
                {
                    CreateUnit(provider, ProviderUnit);
                    var consumer = CreateUnit(provider, ConsumerUnit);
                    var relation = consumer.Relations.Create(ProviderUnit, UnitRelationType.SoftwareUnit);
                    result["units"] = new JArray(ProviderUnit, ConsumerUnit);
                    result["relation"] = new JObject { ["unit"] = consumer.Name, ["relatedObject"] = Convert.ToString(relation.RelatedObject), ["relationType"] = relation.RelationType.ToString() };
                }
                else if (mode == "publish")
                {
                    var unit = provider.UnitGroup.Units.Find(args[2]) ?? throw new InvalidOperationException("The named unit is absent.");
                    var published = new JArray();
                    foreach (string name in args.Skip(3))
                    {
                        var item = (IEngineeringObject)Blocks(unit.BlockGroup).SingleOrDefault(x => x.Name == name) ?? unit.TypeGroup.Types.Find(name)
                            ?? throw new InvalidOperationException("Unit object not found: " + name);
                        // The public API documents UnitAccessType but not the attribute that carries it.
                        var attributes = item.GetAttributeInfos().Select(x => x.Name).Where(x => Readable(item, x) is UnitAccessType).ToArray();
                        if (attributes.Length != 1) throw new InvalidOperationException("Expected one UnitAccessType attribute on " + name + ": " + string.Join(", ", attributes));
                        object before = item.GetAttribute(attributes[0]);
                        item.SetAttribute(attributes[0], UnitAccessType.Published);
                        published.Add(new JObject { ["name"] = name, ["attribute"] = attributes[0], ["before"] = before.ToString(), ["after"] = item.GetAttribute(attributes[0]).ToString() });
                    }
                    result["published"] = published;
                }
                else
                {
                    var blocks = Blocks(plcs[0].BlockGroup).Concat(provider.UnitGroup.Units.SelectMany(x => Blocks(x.BlockGroup))).Where(x => x.Name == args[2]).ToArray();
                    if (blocks.Length != 1) throw new InvalidOperationException("Expected exactly one block named " + args[2]);
                    var protection = blocks[0].GetService<PlcBlockProtectionProvider>() ?? throw new InvalidOperationException("The block exposes no know-how protection service.");
                    // The password is random and never stored: the scratch fixture stays protected for good.
                    byte[] random = new byte[24];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(random);
                    using (var password = new SecureString())
                    {
                        foreach (char character in "Tm9!" + Convert.ToBase64String(random)) password.AppendChar(character);
                        password.MakeReadOnly();
                        protection.Protect(password);
                    }
                    result["block"] = blocks[0].Name; result["isKnowHowProtected"] = blocks[0].IsKnowHowProtected; result["passwordRetained"] = false;
                }
                var compile = plcs[0].GetService<ICompilable>()?.Compile() ?? throw new InvalidOperationException("No PLC compiler service.");
                project.Save();
                result["completed"] = compile.ErrorCount == 0; result["compileErrors"] = compile.ErrorCount; result["compileWarnings"] = compile.WarningCount;
                return result;
            }
        }
    }

    private static PlcUnit CreateUnit(PlcUnitProvider provider, string name)
    {
        if (provider.UnitGroup.Units.Find(name) != null) throw new InvalidOperationException("The test unit already exists; do not overwrite fixtures.");
        var unit = provider.UnitGroup.Units.Create(name);
        unit.NamespacePreset = name;
        return unit;
    }

    private static object Readable(IEngineeringObject item, string name)
    {
        try { return item.GetAttribute(name); } catch (EngineeringException) { return null; }
    }

    private static IEnumerable<PlcBlock> Blocks(PlcBlockGroup group)
    {
        foreach (var block in group.Blocks) yield return block;
        foreach (var nested in group.Groups) foreach (var block in Blocks(nested)) yield return block;
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
}
