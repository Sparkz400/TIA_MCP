using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

// Qualification only: accepts the receipt of a newly owned scratch. It never
// attaches to a user project and cannot select a different PLC or source body.
internal static class Program
{
    static readonly string Api = InstalledApiDirectory();
    // Siemens registers the installed V21 API location; C:\Program Files is only the setup default.
    static string InstalledApiDirectory()
    {
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var key = hklm.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48"))
        {
            var basePath = key?.GetValue("Siemens.Engineering.Base") as string;
            return string.IsNullOrWhiteSpace(basePath) ? @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48" : Path.GetDirectoryName(basePath);
        }
    }
    [STAThread] static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string n = new AssemblyName(e.Name).Name, file = Path.Combine(Api, n + ".dll");
            return n.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { Run(args); return 0; } catch (Exception e) { Console.WriteLine(e); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Run(string[] args)
    {
        if (args.Length != 2 || !new[] { "seed", "seed-lad", "drift", "consume", "safety-telegram", "safety-bit-tag" }.Contains(args[0])) throw new Exception("Expected seed|seed-lad|drift|consume|safety-telegram|safety-bit-tag authorization.json");
        string receipt = Path.GetFullPath(args[1]); var auth = JObject.Parse(File.ReadAllText(receipt));
        string projectPath = Path.GetFullPath(auth.Value<string>("projectPath"));
        if (auth.Value<bool?>("ownsPortalProcess") != true || auth.Value<string>("creationTool") != "tia_create_scratch"
            || !projectPath.StartsWith(Path.GetDirectoryName(receipt) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal)) throw new Exception("Exact owned scratch receipt required");
        using (var portal = TiaPortal.GetProcesses().Single(p => p.Id == auth.Value<int>("processId")).Attach())
        using (portal.ExclusiveAccess("Owned tag-import regression fixture"))
        {
            if (portal.Projects.Count != 1 || portal.Projects[0].Path.FullName != projectPath) throw new Exception("Wrong scratch project");
            var project = portal.Projects[0];
            if (args[0] == "safety-telegram") { SafetyTelegram(project, auth.Value<string>("driveDevice"), auth.Value<int?>("safetyTelegram") ?? 30); return; }
            if (args[0] == "safety-bit-tag") { SafetyBitTag(project, auth.Value<string>("plcName"), auth.Value<string>("tagTable"), auth.Value<string>("tagName"), auth.Value<string>("tagAddress")); return; }
            var plc = project.Devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Single(p => p != null);
            if (args[0] == "drift")
            {
                // A toggle, so a later call can return the unrelated table to the state a change recorded.
                var untouched = plc.TagTableGroup.TagTables.Find("Other").Tags.Find("Untouched");
                untouched.LogicalAddress = untouched.LogicalAddress == "%M51.1" ? "%M51.0" : "%M51.1";
                Console.WriteLine("Moved one unrelated scratch tag to " + untouched.LogicalAddress + " for stale-state verification."); return;
            }
            if (args[0] == "consume") { Consume(plc, Path.GetDirectoryName(receipt)); return; }
            if (args[0] == "seed-lad")
            {
                // Graphical recovery probe: a LAD block whose XML names a global tag that does not exist. The XML lies next to the
                // receipt (written by the runner); it is imported as is and must end up inconsistent.
                string xml = Path.Combine(Path.GetDirectoryName(receipt), "broken-lad.xml");
                plc.BlockGroup.Blocks.Import(new FileInfo(xml), ImportOptions.Override);
                var lad = plc.BlockGroup.Blocks.Find(auth.Value<string>("ladBlockName")) ?? throw new Exception("Imported LAD block missing");
                try { Compile(lad); } catch (EngineeringException) { }
                if (lad.IsConsistent) throw new Exception("Fixture did not create the intended inconsistent LAD block");
                Console.WriteLine("Seeded an inconsistent LAD block " + lad.Name + " (" + lad.ProgrammingLanguage + ")."); return;
            }
            var signals = plc.TagTableGroup.TagTables.Create("Signals"); signals.Tags.Create("Existing", "Bool", "%M50.0");
            plc.TagTableGroup.TagTables.Create("Other").Tags.Create("Untouched", "Bool", "%M51.0");
            string file = Path.Combine(Path.GetDirectoryName(receipt), "broken.scl");
            File.WriteAllText(file, "FUNCTION \"TagScopeBroken\" : Bool\nVERSION : 0.1\nBEGIN\n #TagScopeBroken := \"UnresolvedFlag\";\nEND_FUNCTION\n");
            var source = plc.ExternalSourceGroup.ExternalSources.CreateFromFile("TagScopeBroken", file);
            try { source.GenerateBlocksFromSource(); } catch (EngineeringException) { }
            var broken = plc.BlockGroup.Blocks.Find("TagScopeBroken");
            if (broken == null || broken.IsConsistent) throw new Exception("Fixture did not create the intended inconsistent block");
            Console.WriteLine("Seeded two tables and an inconsistent scratch block.");
        }
    }
    // The negative case needs a block that reads "Added": TIA accepts an unused tag with
    // any type/address. Prove natively that Bool compiles and Int fails, then restore.
    static void Consume(PlcSoftware plc, string dir)
    {
        var tag = plc.TagTableGroup.TagTables.Find("Signals").Tags.Find("Added") ?? throw new Exception("Added tag missing");
        string type = tag.DataTypeName, address = tag.LogicalAddress;
        if (type != "Bool") throw new Exception("Added must still be Bool before the consumer exists");
        string file = Path.Combine(dir, "consumer.scl");
        File.WriteAllText(file, "FUNCTION \"TagScopeConsumer\" : Bool\nVERSION : 0.1\nBEGIN\n IF \"Added\" THEN #TagScopeConsumer := TRUE; ELSE #TagScopeConsumer := FALSE; END_IF;\nEND_FUNCTION\n");
        plc.ExternalSourceGroup.ExternalSources.CreateFromFile("TagScopeConsumer", file).GenerateBlocksFromSource();
        var consumer = plc.BlockGroup.Blocks.Find("TagScopeConsumer") ?? throw new Exception("Consumer block missing");
        var ok = Compile(consumer);
        if (ok.State == CompilerResultState.Error || !consumer.IsConsistent) throw new Exception("Consumer does not compile against Bool Added: " + Describe(ok));
        tag.DataTypeName = "Int"; tag.LogicalAddress = "%MW60";
        var bad = Compile(consumer);
        tag.DataTypeName = type; tag.LogicalAddress = address;
        var restored = Compile(consumer);
        if (bad.State != CompilerResultState.Error) throw new Exception("Int Added did not produce the intended consumer diagnostic: " + Describe(bad));
        if (restored.State == CompilerResultState.Error || !consumer.IsConsistent) throw new Exception("Consumer did not recover after restore: " + Describe(restored));
        Console.WriteLine("Consumer compiles against Bool Added; Int Added fails: " + Describe(bad));
    }
    static CompilerResult Compile(PlcBlock block) => (block.GetService<ICompilable>() ?? throw new Exception("Block exposes no compiler")).Compile();
    static string Describe(CompilerResult r) => r.State + " " + string.Join(" | ", Flatten(r.Messages).Where(m => m.State == CompilerResultState.Error).Select(m => m.Path + ": " + m.Description));
    static IEnumerable<CompilerResultMessage> Flatten(CompilerResultMessageComposition ms) { foreach (var m in ms) { yield return m; foreach (var c in Flatten(m.Messages)) yield return c; } }
    // #212 fixture: Startdrive Openness inserts the drive's PROFIsafe telegram; MCP's drive_telegram_set refuses safety telegrams.
    [MethodImpl(MethodImplOptions.NoInlining)] static void SafetyTelegram(Project project, string deviceName, int number)
    {
        var device = project.Devices.Single(d => d.Name == deviceName);
        var drive = Items(device.DeviceItems).Select(i => i.GetService<Siemens.Engineering.MC.Drives.DriveObjectContainer>()).First(c => c != null).DriveObjects[0];
        if (!drive.Telegrams.CanInsertSafetyTelegram(number)) throw new Exception("Startdrive refuses safety telegram " + number + " on " + deviceName);
        drive.Telegrams.InsertSafetyTelegram(number);
        Console.WriteLine(string.Join("; ", drive.Telegrams.Select(t => t.Type + " " + t.TelegramNumber)));
    }
    // Safety-tag rename fixture: a Bool tag on one bit of the PROFIsafe telegram, which TIA makes a safety tag that an F-block can
    // read. MCP's tag_create refuses every address inside an F-I/O range, so the fixture creates it.
    [MethodImpl(MethodImplOptions.NoInlining)] static void SafetyBitTag(Project project, string plcName, string tableName, string tagName, string address)
    {
        var plc = project.Devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Single(p => p != null && p.Name == plcName);
        var table = plc.TagTableGroup.TagTables.Find(tableName) ?? throw new Exception("Tag table missing: " + tableName);
        var tag = table.Tags.Create(tagName, "Bool", address);
        if (!tag.IsSafety) throw new Exception(address + " did not become a safety tag");
        Console.WriteLine("Created safety tag " + tag.Name + " at " + tag.LogicalAddress);
    }
    static IEnumerable<DeviceItem> Items(DeviceItemComposition items) { foreach (var i in items) { yield return i; foreach (var c in Items(i.DeviceItems)) yield return c; } }
}
