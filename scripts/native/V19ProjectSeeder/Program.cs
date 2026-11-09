using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;

/// <summary>
/// Owned TIA V19 fixtures for the read-only V19 adapter and V19-to-V21 project_upgrade (X12, X01).
///   create &lt;runDirectory&gt;   new worker-owned V19 instance: one CPU 1511-1 PN project with a tag table, PLC data
///                              types, FC/FB/global DB/instance DB/OB and a software unit with its own type, FB, DB and
///                              tag table, all from source; compile, save, archive (.zap19), close. Prints one JSON line.
///   hold &lt;project.ap19&gt; &lt;releaseFile&gt;   opens the project in a new V19 instance and keeps it open (TIA's project lock)
///                              until releaseFile exists or 10 minutes pass; never saves. Prints a JSON line when holding.
/// Every path must lie under the repository's artifacts folder.
/// </summary>
internal static class Program
{
    internal const string ProjectName = "TiaMcpV19Fixture";
    private static readonly string Api = InstalledApiDirectory();

    private static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\19.0\PublicAPI\19.0.0.0"))
        {
            var path = key?.GetValue("Siemens.Engineering") as string;
            return string.IsNullOrWhiteSpace(path) ? @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19" : Path.GetDirectoryName(path);
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, evt) =>
        {
            string name = new AssemblyName(evt.Name).Name;
            if (name != "Siemens.Engineering" && !name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(Api, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { return Run(args); }
        catch (Exception ex)
        {
            Emit(new JObject { ["completed"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.Message });
            return 1;
        }
    }

    private static void Emit(JObject line) { Console.WriteLine(line.ToString(Formatting.None)); Console.Out.Flush(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        string artifacts = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts"));
        if (args.Length == 2 && args[0] == "create") { Emit(Create(Guard(artifacts, args[1]))); return 0; }
        if (args.Length == 3 && args[0] == "hold") return Hold(Guard(artifacts, args[1]), Guard(artifacts, args[2]));
        throw new ArgumentException("Usage: V19ProjectSeeder.exe create <runDirectory> | hold <project.ap19> <releaseFile>");
    }

    private static string Guard(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture paths must lie under the repository's artifacts folder.");
        return full;
    }

    private static JObject Create(string runDirectory)
    {
        if (Directory.Exists(runDirectory) || File.Exists(runDirectory)) throw new InvalidOperationException("The run directory must not exist yet.");
        if (Path.Combine(runDirectory, ProjectName, ProjectName + ".ap19").Length > 200) throw new InvalidOperationException("Choose a shorter run directory.");
        Directory.CreateDirectory(runDirectory);
        var sources = Directory.CreateDirectory(Path.Combine(runDirectory, "sources")).FullName;
        var result = new JObject { ["completed"] = false, ["stages"] = new JArray() };
        var stages = (JArray)result["stages"];
        using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            result["portalProcessId"] = portal.GetCurrentProcess().Id;
            result["apiAssemblyVersion"] = typeof(TiaPortal).Assembly.GetName().Version.ToString();
            var project = portal.Projects.Create(new DirectoryInfo(runDirectory), ProjectName);
            stages.Add("project_created");
            var cpu = portal.HardwareCatalog.Find("1511-1 PN")
                .Where(entry => string.Equals(entry.TypeName, "CPU 1511-1 PN", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => Version.TryParse((entry.Version ?? "").TrimStart('V', 'v'), out var v) ? v : new Version(0, 0))
                .ThenBy(entry => entry.ArticleNumber, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException("The V19 catalog has no CPU 1511-1 PN.");
            result["cpu"] = new JObject { ["typeIdentifier"] = cpu.TypeIdentifier, ["articleNumber"] = cpu.ArticleNumber, ["version"] = cpu.Version };
            project.Devices.CreateWithItem(cpu.TypeIdentifier, "PLC_1", "S71500_Station");
            stages.Add("device_created");
            var plc = Plcs(project).Single();

            var tags = plc.TagTableGroup.TagTables.Create("TiaMcp_Tags");
            tags.Tags.Create("Start", "Bool", "%I0.0");
            tags.Tags.Create("Motor", "Bool", "%Q0.0");
            tags.Tags.Create("Speed", "Int", "%MW10");
            tags.UserConstants.Create("MaxSpeed", "Int", "1500");
            stages.Add("tag_table_created");

            Generate(plc.ExternalSourceGroup, sources, "TiaMcp_Types.udt", MotorType);
            Generate(plc.ExternalSourceGroup, sources, "TiaMcp_Program.scl", ProgramSource);
            stages.Add("plc_sources_generated");

            var provider = plc.GetService<PlcUnitProvider>() ?? throw new InvalidOperationException("The V19 CPU exposes no software units.");
            var unit = provider.UnitGroup.Units.Create("TiaMcp_Conveyor");
            unit.TagTableGroup.TagTables.Create("TiaMcp_ConveyorTags").Tags.Create("BeltRun", "Bool", "%M20.0");
            Generate(unit.ExternalSourceGroup, sources, "TiaMcp_BeltTypes.udt", BeltType);
            Generate(unit.ExternalSourceGroup, sources, "TiaMcp_Belt.scl", BeltSource);
            stages.Add("software_unit_generated");

            var compilation = plc.GetService<ICompilable>().Compile();
            result["compile"] = new JObject { ["state"] = compilation.State.ToString(), ["errorCount"] = compilation.ErrorCount, ["warningCount"] = compilation.WarningCount,
                ["messages"] = new JArray(Messages(compilation.Messages).Take(200)) };
            if (compilation.ErrorCount != 0) throw new InvalidOperationException("The V19 fixture does not compile: " + result["compile"].ToString(Formatting.None));
            stages.Add("software_compiled");

            result["inventory"] = Inventory(plc);
            project.Save();
            stages.Add("project_saved");
            var archive = new FileInfo(Path.Combine(runDirectory, ProjectName + ".zap19"));
            project.Archive(archive.Directory, archive.Name, ProjectArchivationMode.Compressed);
            archive.Refresh();
            if (!archive.Exists) throw new InvalidOperationException("TIA did not write the archive.");
            stages.Add("project_archived");
            result["project"] = new JObject { ["name"] = project.Name, ["path"] = project.Path.FullName };
            result["archive"] = new JObject { ["path"] = archive.FullName, ["bytes"] = archive.Length, ["sha256"] = Sha256(archive.FullName) };
            project.Close();
            stages.Add("project_closed");
        }
        result["completed"] = true;
        return result;
    }

    private static int Hold(string projectPath, string releaseFile)
    {
        if (!string.Equals(Path.GetExtension(projectPath), ".ap19", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
            throw new InvalidOperationException("hold needs an existing .ap19 project.");
        using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
        {
            var project = portal.Projects.Open(new FileInfo(projectPath));
            Emit(new JObject { ["holding"] = true, ["portalProcessId"] = portal.GetCurrentProcess().Id, ["project"] = project.Path.FullName });
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (!File.Exists(releaseFile) && DateTime.UtcNow < deadline) Thread.Sleep(500);
            bool released = File.Exists(releaseFile);
            project.Close();
            Emit(new JObject { ["holding"] = false, ["released"] = released, ["saved"] = false });
            return released ? 0 : 2;
        }
    }

    private static void Generate(PlcExternalSourceSystemGroup group, string directory, string fileName, string text)
    {
        string unit = (group.Parent as PlcUnit)?.Name;
        string path = Path.Combine(directory, (unit == null ? "" : unit + "_") + fileName);
        File.WriteAllText(path, text, new UTF8Encoding(true));
        var source = group.ExternalSources.CreateFromFile(Path.GetFileNameWithoutExtension(path), path);
        source.GenerateBlocksFromSource();
    }

    private static IEnumerable<PlcSoftware> Plcs(Project project) =>
        project.Devices.SelectMany(device => Items(device.DeviceItems))
            .Select(item => item.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(software => software != null);

    private static IEnumerable<DeviceItem> Items(DeviceItemComposition items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Items(item.DeviceItems)) yield return child;
        }
    }

    private static IEnumerable<JObject> Messages(IEnumerable<CompilerResultMessage> messages, int depth = 0)
    {
        foreach (var message in messages)
        {
            yield return new JObject { ["path"] = message.Path, ["state"] = message.State.ToString(), ["description"] = message.Description, ["depth"] = depth };
            foreach (var child in Messages(message.Messages, depth + 1)) yield return child;
        }
    }

    // What the fixture holds by name; the qualifier compares the adapter's and the upgraded project's inventories against it.
    private static JObject Inventory(PlcSoftware plc)
    {
        var scopes = new JArray { Scope("plc", plc.Name, plc.BlockGroup, plc.TypeGroup, plc.TagTableGroup) };
        foreach (var unit in plc.GetService<PlcUnitProvider>().UnitGroup.Units) scopes.Add(Scope("softwareUnit", unit.Name, unit.BlockGroup, unit.TypeGroup, unit.TagTableGroup));
        return new JObject { ["plc"] = plc.Name, ["scopes"] = scopes };
    }

    private static JObject Scope(string kind, string name, PlcBlockGroup blocks, PlcTypeGroup types, PlcTagTableGroup tables) => new JObject
    {
        ["kind"] = kind, ["name"] = name,
        ["blocks"] = new JArray(blocks.Blocks.Select(b => new JObject { ["name"] = b.Name, ["number"] = b.Number, ["kind"] = b.GetType().Name,
            ["programmingLanguage"] = b.ProgrammingLanguage.ToString(), ["isConsistent"] = b.IsConsistent }).OrderBy(b => (string)b["name"], StringComparer.Ordinal)),
        ["types"] = new JArray(types.Types.Select(t => (string)t.Name).OrderBy(n => n, StringComparer.Ordinal)),
        ["tagTables"] = new JArray(tables.TagTables.Select(t => new JObject { ["name"] = t.Name, ["tags"] = t.Tags.Count, ["constants"] = t.UserConstants.Count })
            .OrderBy(t => (string)t["name"], StringComparer.Ordinal)),
    };

    private static string Sha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private const string MotorType = "TYPE \"TiaMcp_Motor\"\r\nVERSION : 0.1\r\n   STRUCT\r\n      Running : Bool;\r\n      Fault : Bool;\r\n      Speed : Int;\r\n   END_STRUCT;\r\nEND_TYPE\r\n";

    private const string ProgramSource =
        "FUNCTION \"TiaMcp_IsEnabled\" : Bool\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n   Enable : Bool;\r\nEND_VAR\r\nBEGIN\r\n   #TiaMcp_IsEnabled := #Enable;\r\nEND_FUNCTION\r\n\r\n" +
        "FUNCTION_BLOCK \"TiaMcp_MotorCtrl\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n   Start : Bool;\r\n   Stop : Bool;\r\n   Setpoint : Int;\r\nEND_VAR\r\nVAR_OUTPUT\r\n   Motor : \"TiaMcp_Motor\";\r\nEND_VAR\r\nBEGIN\r\n" +
        "   IF \"TiaMcp_IsEnabled\"(Enable := #Start) AND NOT #Stop THEN\r\n      #Motor.Running := TRUE;\r\n   ELSIF #Stop THEN\r\n      #Motor.Running := FALSE;\r\n   END_IF;\r\n   #Motor.Speed := #Setpoint;\r\nEND_FUNCTION_BLOCK\r\n\r\n" +
        "DATA_BLOCK \"TiaMcp_Data\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\n   VAR\r\n      Conveyor : \"TiaMcp_Motor\";\r\n      Setpoint : Int := 1200;\r\n   END_VAR\r\nBEGIN\r\nEND_DATA_BLOCK\r\n\r\n" +
        "DATA_BLOCK \"TiaMcp_MotorCtrl_DB\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\n\"TiaMcp_MotorCtrl\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n\r\n" +
        "ORGANIZATION_BLOCK \"Main\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nBEGIN\r\n   \"TiaMcp_MotorCtrl_DB\"(Start := \"Start\", Stop := FALSE, Setpoint := \"TiaMcp_Data\".Setpoint);\r\n" +
        "   \"Motor\" := \"TiaMcp_MotorCtrl_DB\".Motor.Running;\r\nEND_ORGANIZATION_BLOCK\r\n";

    private const string BeltType = "TYPE \"TiaMcp_BeltState\"\r\nVERSION : 0.1\r\n   STRUCT\r\n      Moving : Bool;\r\n      Position : DInt;\r\n   END_STRUCT;\r\nEND_TYPE\r\n";

    private const string BeltSource =
        "FUNCTION_BLOCK \"TiaMcp_Belt\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n   Run : Bool;\r\nEND_VAR\r\nVAR\r\n   State : \"TiaMcp_BeltState\";\r\nEND_VAR\r\nBEGIN\r\n" +
        "   #State.Moving := #Run;\r\n   IF #Run THEN\r\n      #State.Position := #State.Position + 1;\r\n   END_IF;\r\nEND_FUNCTION_BLOCK\r\n\r\n" +
        "DATA_BLOCK \"TiaMcp_Belt_DB\"\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\n\"TiaMcp_Belt\"\r\nBEGIN\r\nEND_DATA_BLOCK\r\n";
}
