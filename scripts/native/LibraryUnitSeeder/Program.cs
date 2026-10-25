using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Units;

/// <summary>
/// Adds one empty software unit to a worker-owned scratch project for the library scope qualifier.
/// Unlike UnitSeeder, the guard root is the authorization file's own directory, so short artifact roots work.
/// </summary>
internal static class Program
{
    private const string UnitName = "TiaMcp_LibUnit";
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
        if (args.Length != 1) throw new InvalidOperationException("Usage: LibraryUnitSeeder.exe <authorization.json>");
        string authorizationPath = Path.GetFullPath(args[0]);
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
                if (provider.UnitGroup.Units.Find(UnitName) != null) throw new InvalidOperationException("The test unit already exists; do not overwrite fixtures.");
                var unit = provider.UnitGroup.Units.Create(UnitName);
                unit.NamespacePreset = UnitName;
                var compile = plcs[0].GetService<ICompilable>()?.Compile() ?? throw new InvalidOperationException("No PLC compiler service.");
                project.Save();
                return new JObject { ["completed"] = compile.ErrorCount == 0, ["projectPath"] = project.Path.FullName, ["processId"] = pid,
                    ["unit"] = unit.Name, ["namespacePreset"] = unit.NamespacePreset, ["compileErrors"] = compile.ErrorCount, ["compileWarnings"] = compile.WarningCount };
            }
        }
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
