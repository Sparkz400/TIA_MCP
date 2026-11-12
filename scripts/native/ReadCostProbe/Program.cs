using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

// Measures what one Openness read costs: the same block and tag attributes read one property at a time
// (as the project-model capture does) and in one GetAttributes call. Starts its own Portal without UI and
// creates its own project under artifacts/read-cost-probe; it never attaches to another Portal.
internal static class Program
{
    private static readonly string Api = InstalledApiDirectory();
    private static readonly string[] BlockAttributes = { "Name", "Number", "ProgrammingLanguage", "IsConsistent", "IsKnowHowProtected", "Namespace", "ModifiedDate" };
    private static readonly string[] TagAttributes = { "Name", "DataTypeName", "LogicalAddress", "ExternalAccessible", "ExternalVisible", "ExternalWritable" };
    private const string Cpu = "OrderNumber:6ES7 511-1AL03-0AB0/V4.0";

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
        try { Console.WriteLine(Run(args).ToString(Formatting.Indented)); return 0; }
        catch (Exception ex) { Console.WriteLine(new JObject { ["completed"] = false, ["error"] = ex.GetType().Name, ["message"] = ex.ToString() }.ToString(Formatting.Indented)); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args) => args.Length > 0 && args[0] == "open" ? RunCaptureSequence(args) : RunSeeded(args);

    // Opens a project this probe saved earlier and replays the per-object native calls of the project-model capture
    // (OpennessSession.ProjectModel.cs CaptureModelBlock/CaptureModelTags, OpennessTraversal.BlockInfo,
    // PlcLibraryInstanceCapture.Read), first cold, then warm next to batched and local-only variants.
    private static JObject RunCaptureSequence(string[] args)
    {
        if (args.Length < 2) throw new InvalidOperationException("Usage: ReadCostProbe.exe open <project.ap21 under artifacts/read-cost-probe> [rounds]");
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts/read-cost-probe")) + Path.DirectorySeparatorChar;
        string projectPath = Path.GetFullPath(args[1]);
        if (!projectPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(projectPath) != ".ap21")
            throw new InvalidOperationException("Only a project this probe created under artifacts/read-cost-probe is opened.");
        int rounds = args.Length > 2 ? int.Parse(args[2]) : 5;
        var report = new JObject { ["probe"] = "ReadCostProbe", ["mode"] = "captureSequence", ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["project"] = projectPath, ["rounds"] = rounds, ["machine"] = Environment.MachineName };
        var total = Stopwatch.StartNew();
        Process process = null;
        try
        {
            var sw = Stopwatch.StartNew();
            using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                process = Process.GetProcessById(portal.GetCurrentProcess().Id);
                report["portalProcessId"] = process.Id;
                report["portalStartMs"] = sw.ElapsedMilliseconds;
                sw.Restart();
                var project = portal.Projects.Open(new FileInfo(projectPath));
                report["projectOpenMs"] = sw.ElapsedMilliseconds;
                using (portal.ExclusiveAccess("ReadCostProbe: timing reads on its own project"))
                {
                    sw.Restart();
                    var plc = Devices(project).SelectMany(d => Items(d.DeviceItems)).Select(item => item.GetService<SoftwareContainer>()?.Software as PlcSoftware).Single(item => item != null);
                    report["findPlcMs"] = sw.ElapsedMilliseconds;
                    var local = new LocalCost();
                    // Cold: the first pass after open, in capture order (blocks, then tags).
                    sw.Restart(); CaptureBlocks(plc, local); double coldBlocks = sw.Elapsed.TotalMilliseconds;
                    sw.Restart(); CaptureTags(plc, local); double coldTags = sw.Elapsed.TotalMilliseconds;
                    report["cold"] = new JObject { ["captureSequenceBlocksMs"] = Math.Round(coldBlocks, 1), ["captureSequenceTagsMs"] = Math.Round(coldTags, 1),
                        ["blocks"] = plc.BlockGroup.Blocks.Count, ["tags"] = Tags(plc).Count() };
                    var blockCount = plc.BlockGroup.Blocks.Count; var tagCount = Tags(plc).Count();
                    report["blocks"] = Measure(rounds, blockCount, new Dictionary<string, Action>
                    {
                        ["enumerateOnly"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(b); },
                        ["perProperty"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(BlockIndividually(b)); },
                        ["getAttributes"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(b.GetAttributes(BlockAttributes)); },
                        ["captureSequence"] = () => CaptureBlocks(plc, new LocalCost()),
                        ["captureSequenceNoLocal"] = () => CaptureBlocks(plc, null),
                        ["captureBatched"] = () => CaptureBlocksBatched(plc, new LocalCost()),
                        ["libraryServiceOnly"] = () => { foreach (var b in plc.BlockGroup.Blocks) if (b is FB || b is FC) GC.KeepAlive(((IEngineeringServiceProvider)b).GetService<Siemens.Engineering.Library.Types.LibraryTypeInstanceInfo>()); },
                    }, BlockAttributes.Length);
                    report["tags"] = Measure(rounds, tagCount, new Dictionary<string, Action>
                    {
                        ["enumerateOnly"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(t); },
                        ["perProperty"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(TagIndividually(t)); },
                        ["getAttributes"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(t.GetAttributes(TagAttributes)); },
                        ["captureSequence"] = () => CaptureTags(plc, new LocalCost()),
                        ["captureSequenceNoLocal"] = () => CaptureTags(plc, null),
                        ["captureBatched"] = () => CaptureTagsBatched(plc, new LocalCost()),
                    }, TagAttributes.Length);
                    var localOnly = new LocalCost();
                    var swLocal = Stopwatch.StartNew();
                    for (int i = 0; i < blockCount + tagCount; i++) localOnly.Add("/projects/ReadCost/devices/ProbeStation/items/ProbePLC/plc/ProbePLC/tags/Probe_0/tags/T_" + i.ToString("D5"), "tag", "T_" + i,
                        new JObject { ["dataType"] = "Int", ["logicalAddress"] = "%MW" + i, ["isSafety"] = false, ["externalAccessible"] = true, ["externalVisible"] = true, ["externalWritable"] = true, ["valueSemantics"] = "Offline declaration; no live PLC value" });
                    report["localNodeWorkMsPerNode"] = Math.Round(swLocal.Elapsed.TotalMilliseconds / (blockCount + tagCount), 4);
                }
                portal.GetCurrentProcess().Dispose();
            }
            report["completed"] = true;
        }
        finally
        {
            if (process != null) { report["portalExitVerified"] = process.WaitForExit(120000); process.Dispose(); }
            report["totalMs"] = total.ElapsedMilliseconds;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(projectPath))), "capture-sequence-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + ".json"),
                report.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
        return report;
    }

    private static readonly string[] CaptureBlockAttributes = { "Name", "Namespace", "Number", "ProgrammingLanguage", "IsConsistent", "IsKnowHowProtected", "ModifiedDate" };
    private static readonly string[] CaptureTagAttributes = { "Name", "DataTypeName", "LogicalAddress", "IsSafety", "ExternalAccessible", "ExternalVisible", "ExternalWritable" };

    // The calls CaptureModelBlock makes today, in order, including the repeated Name and Namespace reads.
    private static void CaptureBlocks(PlcSoftware plc, LocalCost local)
    {
        const string group = "/projects/ReadCost/devices/ProbeStation/items/ProbePLC/plc/ProbePLC/blocks";
        foreach (var block in plc.BlockGroup.Blocks)
        {
            string path = group + "/" + Uri.EscapeDataString(block.Name) + (string.IsNullOrEmpty(block.Namespace) ? "" : "@" + block.Namespace); // BlockObjectPath
            string ns = block.Namespace;                                                                               // NamespaceParent
            var metadata = new JObject { ["number"] = block.Number, ["kind"] = block.GetType().Name, ["programmingLanguage"] = block.ProgrammingLanguage.ToString(),
                ["isConsistent"] = block.IsConsistent, ["isKnowHowProtected"] = block.IsKnowHowProtected, ["name"] = block.Name };       // BlockInfo
            metadata["namespace"] = block.Namespace; metadata["modified"] = block.ModifiedDate.ToString("O");
            if (block is InstanceDB instance) metadata["instanceOfName"] = instance.InstanceOfName;
            metadata["libraryInstance"] = LibraryStatus(block);
            string name = block.Name;
            GC.KeepAlive(ns);
            local?.Add(path, "block", name, metadata);
        }
    }

    private static void CaptureBlocksBatched(PlcSoftware plc, LocalCost local)
    {
        const string group = "/projects/ReadCost/devices/ProbeStation/items/ProbePLC/plc/ProbePLC/blocks";
        foreach (var block in plc.BlockGroup.Blocks)
        {
            var v = block.GetAttributes(CaptureBlockAttributes);
            string name = (string)v[0], ns = (string)v[1];
            var metadata = new JObject { ["number"] = (int)v[2], ["kind"] = block.GetType().Name, ["programmingLanguage"] = v[3].ToString(),
                ["isConsistent"] = (bool)v[4], ["isKnowHowProtected"] = (bool)v[5], ["namespace"] = ns, ["modified"] = ((DateTime)v[6]).ToString("O") };
            if (block is InstanceDB instance) metadata["instanceOfName"] = instance.InstanceOfName;
            metadata["libraryInstance"] = LibraryStatus(block);
            local?.Add(group + "/" + Uri.EscapeDataString(name) + (string.IsNullOrEmpty(ns) ? "" : "@" + ns), "block", name, metadata);
        }
    }

    private static string LibraryStatus(PlcBlock block)
    {
        if (!(block is FB) && !(block is FC)) return "notApplicable";
        var info = ((IEngineeringServiceProvider)block).GetService<Siemens.Engineering.Library.Types.LibraryTypeInstanceInfo>();
        return info == null ? "noService" : "present";
    }

    // The calls CaptureModelTags makes per tag: Name for the path and again for the node, then six properties.
    private static void CaptureTags(PlcSoftware plc, LocalCost local)
    {
        foreach (var table in plc.TagTableGroup.TagTables)
        {
            string tablePath = "/projects/ReadCost/devices/ProbeStation/items/ProbePLC/plc/ProbePLC/tags/" + Uri.EscapeDataString(table.Name);
            foreach (var tag in table.Tags)
            {
                string path = tablePath + "/tags/" + Uri.EscapeDataString(tag.Name);
                string name = tag.Name;
                var metadata = new JObject { ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety,
                    ["externalAccessible"] = tag.ExternalAccessible, ["externalVisible"] = tag.ExternalVisible, ["externalWritable"] = tag.ExternalWritable,
                    ["valueSemantics"] = "Offline declaration; no live PLC value" };
                local?.Add(path, "tag", name, metadata);
            }
        }
    }

    private static void CaptureTagsBatched(PlcSoftware plc, LocalCost local)
    {
        foreach (var table in plc.TagTableGroup.TagTables)
        {
            string tablePath = "/projects/ReadCost/devices/ProbeStation/items/ProbePLC/plc/ProbePLC/tags/" + Uri.EscapeDataString(table.Name);
            foreach (var tag in table.Tags)
            {
                var v = tag.GetAttributes(CaptureTagAttributes);
                string name = (string)v[0];
                var metadata = new JObject { ["dataType"] = (string)v[1], ["logicalAddress"] = (string)v[2], ["isSafety"] = (bool)v[3],
                    ["externalAccessible"] = (bool)v[4], ["externalVisible"] = (bool)v[5], ["externalWritable"] = (bool)v[6],
                    ["valueSemantics"] = "Offline declaration; no live PLC value" };
                local?.Add(tablePath + "/tags/" + Uri.EscapeDataString(name), "tag", name, metadata);
            }
        }
    }

    // The worker-local part of ModelCapture.Add: SHA-256 node id, the node object and its serialized size.
    private sealed class LocalCost
    {
        private readonly JArray _nodes = new JArray();
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
        internal void Add(string path, string kind, string name, JObject metadata)
        {
            string id;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                id = "n-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path + "\n" + kind))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
            if (!_ids.Add(id)) return;
            var node = new JObject { ["nodeId"] = id, ["parentId"] = "p", ["kind"] = kind, ["name"] = name, ["origin"] = "native", ["plcPath"] = "plc", ["scopePath"] = "plc",
                ["access"] = new JObject { ["state"] = "readable" }, ["detailAvailability"] = "available", ["metadata"] = metadata, ["objectPath"] = path };
            GC.KeepAlive(Encoding.UTF8.GetByteCount(node.ToString(Formatting.None)));
            _nodes.Add(node);
        }
    }

    private static IEnumerable<Device> Devices(Project project) => project.Devices.Concat(project.UngroupedDevicesGroup.Devices);

    private static JObject RunSeeded(string[] args)
    {
        int blockCount = args.Length > 0 ? int.Parse(args[0]) : 500;
        int tagCount = args.Length > 1 ? int.Parse(args[1]) : 2000;
        int rounds = args.Length > 2 ? int.Parse(args[2]) : 5;
        if (blockCount < 10 || blockCount > 5000 || tagCount < 10 || tagCount > 10000 || rounds < 1 || rounds > 20)
            throw new InvalidOperationException("Usage: ReadCostProbe.exe [blocks 10..5000] [tags 10..10000] [rounds 1..20]");
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../../artifacts/read-cost-probe"));
        string runDir = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
        Directory.CreateDirectory(runDir);
        var report = new JObject { ["probe"] = "ReadCostProbe", ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["runDirectory"] = runDir,
            ["cpu"] = Cpu, ["blocksRequested"] = blockCount, ["tagsRequested"] = tagCount, ["rounds"] = rounds, ["machine"] = Environment.MachineName };
        var total = Stopwatch.StartNew();
        Process process = null;
        try
        {
            var sw = Stopwatch.StartNew();
            using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                process = Process.GetProcessById(portal.GetCurrentProcess().Id);
                report["portalProcessId"] = process.Id;
                report["portalStartMs"] = sw.ElapsedMilliseconds;
                sw.Restart();
                var project = portal.Projects.Create(new DirectoryInfo(Path.Combine(runDir, "p")), "ReadCost");
                var device = project.Devices.CreateWithItem(Cpu, "ProbePLC", "ProbeStation");
                var plc = Items(device.DeviceItems).Select(item => item.GetService<SoftwareContainer>()?.Software as PlcSoftware).Single(item => item != null);
                Seed(plc, blockCount, tagCount, runDir);
                project.Save();
                report["seedMs"] = sw.ElapsedMilliseconds;

                using (portal.ExclusiveAccess("ReadCostProbe: timing reads on its own project"))
                {
                    var blocks = plc.BlockGroup.Blocks.ToList();
                    var tags = plc.TagTableGroup.TagTables.SelectMany(table => table.Tags).ToList();
                    report["blocksSeen"] = blocks.Count;
                    report["tagsSeen"] = tags.Count;
                    report["blockAttributeInfos"] = AttributeCheck(blocks[0], BlockAttributes);
                    report["tagAttributeInfos"] = AttributeCheck(tags[0], TagAttributes);
                    report["blocks"] = Measure(rounds, blocks.Count, new Dictionary<string, Action>
                    {
                        ["enumerateOnly"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(b); },
                        ["oneProperty"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(b.Name); },
                        ["perProperty"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(BlockIndividually(b)); },
                        ["getAttributes"] = () => { foreach (var b in plc.BlockGroup.Blocks) GC.KeepAlive(b.GetAttributes(BlockAttributes)); },
                    }, BlockAttributes.Length);
                    report["tags"] = Measure(rounds, tags.Count, new Dictionary<string, Action>
                    {
                        ["enumerateOnly"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(t); },
                        ["oneProperty"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(t.Name); },
                        ["perProperty"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(TagIndividually(t)); },
                        ["getAttributes"] = () => { foreach (var t in Tags(plc)) GC.KeepAlive(t.GetAttributes(TagAttributes)); },
                    }, TagAttributes.Length);
                    report["valuesEqual"] = new JObject
                    {
                        ["blocks"] = blocks.All(b => Same(BlockIndividually(b), b.GetAttributes(BlockAttributes))),
                        ["tags"] = tags.All(t => Same(TagIndividually(t), t.GetAttributes(TagAttributes))),
                    };
                }
                portal.GetCurrentProcess().Dispose();
            }
            report["completed"] = true;
        }
        finally
        {
            if (process != null) { report["portalExitVerified"] = process.WaitForExit(120000); process.Dispose(); }
            report["totalMs"] = total.ElapsedMilliseconds;
            File.WriteAllText(Path.Combine(runDir, "report.json"), report.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
        return report;
    }

    private static IEnumerable<PlcTag> Tags(PlcSoftware plc) => plc.TagTableGroup.TagTables.SelectMany(table => table.Tags);

    private static object[] BlockIndividually(PlcBlock b) =>
        new object[] { b.Name, b.Number, b.ProgrammingLanguage, b.IsConsistent, b.IsKnowHowProtected, b.Namespace, b.ModifiedDate };

    private static object[] TagIndividually(PlcTag t) =>
        new object[] { t.Name, t.DataTypeName, t.LogicalAddress, t.ExternalAccessible, t.ExternalVisible, t.ExternalWritable };

    private static bool Same(object[] individual, IList<object> batched) =>
        individual.Length == batched.Count && individual.Zip(batched, (a, b) => Equals(a, b)).All(x => x);

    private static JObject AttributeCheck(IEngineeringObject item, string[] wanted)
    {
        var names = new HashSet<string>(item.GetAttributeInfos().Select(info => info.Name), StringComparer.Ordinal);
        return new JObject { ["kind"] = item.GetType().Name, ["missing"] = new JArray(wanted.Where(name => !names.Contains(name))) };
    }

    // Warm-up pass per mode, then rounds with the mode order rotated each round; median per mode.
    private static JObject Measure(int rounds, int objects, Dictionary<string, Action> modes, int attributeCount)
    {
        foreach (var mode in modes.Values) mode();
        var samples = modes.Keys.ToDictionary(key => key, key => new List<double>());
        var order = modes.Keys.ToList();
        for (int round = 0; round < rounds; round++)
        {
            foreach (string key in order)
            {
                var sw = Stopwatch.StartNew();
                modes[key]();
                samples[key].Add(sw.Elapsed.TotalMilliseconds);
            }
            order.Add(order[0]); order.RemoveAt(0);
        }
        var result = new JObject { ["objects"] = objects, ["attributes"] = attributeCount };
        foreach (var pair in samples)
        {
            double median = Median(pair.Value);
            result[pair.Key] = new JObject { ["medianMs"] = Math.Round(median, 1), ["msPerObject"] = Math.Round(median / objects, 3),
                ["samplesMs"] = new JArray(pair.Value.Select(v => Math.Round(v, 1))) };
        }
        if (samples.ContainsKey("captureSequence"))
            result["captureBatchedSpeedup"] = Math.Round(Median(samples["captureSequence"]) / Median(samples["captureBatched"]), 2);
        double per = Median(samples["perProperty"]), batched = Median(samples["getAttributes"]), enumerate = Median(samples["enumerateOnly"]);
        result["batchedSpeedup"] = Math.Round(per / batched, 2);
        result["msPerPropertyCall"] = Math.Round((per - enumerate) / objects / attributeCount, 3);
        return result;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static void Seed(PlcSoftware plc, int blockCount, int tagCount, string runDir)
    {
        const int chunk = 100;
        for (int start = 1; start <= blockCount; start += chunk)
        {
            var scl = new StringBuilder();
            for (int i = start; i < Math.Min(start + chunk, blockCount + 1); i++)
            {
                if (i % 2 == 1)
                    scl.Append("FUNCTION \"F_").Append(i.ToString("D4")).Append("\" : Int\nVAR_INPUT\n a : Int;\nEND_VAR\nBEGIN\n #F_")
                       .Append(i.ToString("D4")).Append(" := #a + 1;\nEND_FUNCTION\n\n");
                else
                    scl.Append("DATA_BLOCK \"D_").Append(i.ToString("D4")).Append("\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\nNON_RETAIN\nVAR\n v : Int;\nEND_VAR\nBEGIN\nEND_DATA_BLOCK\n\n");
            }
            string path = Path.Combine(runDir, "seed_" + start.ToString("D4") + ".scl");
            File.WriteAllText(path, scl.ToString(), new UTF8Encoding(true));
            plc.ExternalSourceGroup.ExternalSources.CreateFromFile("Seed_" + start.ToString("D4"), path).GenerateBlocksFromSource();
        }
        // Shapes the flat seed lacks: an FB with an instance DB, and a software unit whose blocks carry a namespace.
        string fbPath = Path.Combine(runDir, "seed_fb.scl");
        File.WriteAllText(fbPath, "FUNCTION_BLOCK \"FB_Probe\"\nVAR_INPUT\n a : Int;\nEND_VAR\nVAR\n s : Int;\nEND_VAR\nBEGIN\n #s := #a;\nEND_FUNCTION_BLOCK\n\n" +
            "DATA_BLOCK \"IDB_Probe\" \"FB_Probe\"\nBEGIN\nEND_DATA_BLOCK\n", new UTF8Encoding(true));
        plc.ExternalSourceGroup.ExternalSources.CreateFromFile("Seed_FB", fbPath).GenerateBlocksFromSource();
        var units = plc.GetService<Siemens.Engineering.SW.Units.PlcUnitProvider>();
        if (units != null)
        {
            var unit = units.UnitGroup.Units.Create("ProbeUnit");
            unit.NamespacePreset = "ProbeUnit";
            string unitPath = Path.Combine(runDir, "seed_unit.scl");
            File.WriteAllText(unitPath, "FUNCTION \"U_Seed\" : Bool\nVAR_INPUT\n e : Bool;\nEND_VAR\nBEGIN\n #U_Seed := #e;\nEND_FUNCTION\n\n" +
                "FUNCTION_BLOCK \"U_FbSeed\"\nVAR\n s : Int;\nEND_VAR\nBEGIN\n #s := #s + 1;\nEND_FUNCTION_BLOCK\n", new UTF8Encoding(true));
            unit.ExternalSourceGroup.ExternalSources.CreateFromFile("Seed_Unit", unitPath).GenerateBlocksFromSource();
            foreach (var block in unit.BlockGroup.Blocks.ToList()) block.Namespace = "ProbeUnit.Inner";
        }
        const int perTable = 1000;
        for (int t = 0; t * perTable < tagCount; t++)
        {
            var table = plc.TagTableGroup.TagTables.Create("Probe_" + t);
            for (int i = t * perTable; i < Math.Min((t + 1) * perTable, tagCount); i++)
                table.Tags.Create("T_" + i.ToString("D5"), "Int", "%MW" + (i * 2));
        }
    }

    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items)
    {
        foreach (var item in items) { yield return item; foreach (var nested in Items(item.DeviceItems)) yield return nested; }
    }
}
