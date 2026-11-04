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
using Siemens.Engineering.SW;

// Qualification only: accepts the receipt of a newly owned scratch. It never
// attaches to a user project and cannot select a different PLC or source body.
internal static class Program
{
    static readonly string Api = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
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
        if (args.Length != 2 || !new[] { "seed", "drift" }.Contains(args[0])) throw new Exception("Expected seed|drift authorization.json");
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
            var plc = project.Devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Single(p => p != null);
            if (args[0] == "drift")
            {
                plc.TagTableGroup.TagTables.Find("Other").Tags.Find("Untouched").LogicalAddress = "%M51.1";
                Console.WriteLine("Drifted one unrelated scratch tag for stale-preview verification."); return;
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
    static IEnumerable<DeviceItem> Items(DeviceItemComposition items) { foreach (var i in items) { yield return i; foreach (var c in Items(i.DeviceItems)) yield return c; } }
}
