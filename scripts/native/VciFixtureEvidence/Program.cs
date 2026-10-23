using System;
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
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.WatchAndForceTables;
using Siemens.Engineering.VersionControl;

// Isolated qualification fixture. No import, runtime, online, force or watch mutation route.
internal static class Program
{
    private static string output;
    private static int eventId;
    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            string name = new AssemblyName(e.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            string path = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48", name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args)); return 0; }
        catch (Exception e) {
            var failure = new JObject { ["completed"] = false, ["automaticReplay"] = false, ["outcome"] = "unknownRequiresReview", ["error"] = e.ToString() };
            if (output != null) Save("failure.json", failure);
            Console.WriteLine(failure); return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        Need(args.Length == 2 && (args[0] == "seed" || args[0] == "inventory" || args[0] == "checkpoint"), "Expected seed|inventory|checkpoint admission.json.");
        var a = JObject.Parse(File.ReadAllText(args[1]));
        Need(a.Value<string>("mode") == args[0] && a.Value<string>("binarySha256") == Hash(Assembly.GetExecutingAssembly().Location), "Admission binary/mode mismatch.");
        Need(a.Value<bool?>("ownedScratch") == true && a.Value<bool?>("sequentialNativeAdmission") == true && a.Value<bool?>("checkpointRetrievalFoundationPassed") == true && a.Value<bool?>("automaticReplay") == false, "Owned sequential recovery-gated scratch required.");
        string root = Path.GetFullPath(a.Value<string>("artifactRoot")), projectPath = Path.GetFullPath(a.Value<string>("projectPath")), destination = Path.GetFullPath(a.Value<string>("outputDirectory"));
        Guard(root, projectPath); Guard(root, destination);
        Need(!Directory.Exists(destination) && !File.Exists(destination), "Fresh output required; unknown intent must not be replayed.");
        foreach (string name in new[] { "hostBinary", "workerBinary", "recoveryFoundationEvidence", "scratchReceipt", "storeWindowIntent" }) Check((JObject)a[name]);
        var sources = (JArray)a["sourceFiles"]; Need(sources != null && sources.Count == 2, "Exact reviewed two-source inventory required.");
        foreach (JObject source in sources) Check(source);
        var window = JObject.Parse(File.ReadAllText(a["storeWindowIntent"].Value<string>("path")));
        Need(window.Value<string>("projectPath") == projectPath && window.Value<bool?>("sameHandlesHeldThroughDispatch") == true && window.Value<bool?>("automaticReplay") == false, "Held store scope differs.");
        Need(window.Value<string>("mode") == (args[0] == "seed" ? "isolatedQualificationMutation" : "readOnlyProbe"), "Mutation must not be labeled read-only.");
        JArray expected = null;
        if (args[0] == "seed") {
        Check((JObject)a["reviewedInput"]);
        var reviewed = JObject.Parse(File.ReadAllText(a["reviewedInput"].Value<string>("path")));
        expected = (JArray)reviewed["blocks"];
        Need(expected != null && expected.Count == 2 && reviewed.Value<string>("kind") == "reviewed-vci-bilingual-fixture-input", "Reviewed exact fixture input required.");
        Need(expected.OfType<JObject>().Select(b => b.Value<string>("name")).OrderBy(n => n).SequenceEqual(new[] { "TiaMcp_EnableGate", "TiaMcp_IsEnabled" }), "Only exact two fixture block names admitted.");
        foreach (JObject block in expected) {
            Need(block.Value<int>("number") == 1 && block.Value<string>("kind") == (block.Value<string>("name") == "TiaMcp_IsEnabled" ? "FC" : "FB"), "Reviewed block identity differs.");
            var texts = (JArray)block["comments"];
            Need(texts != null && texts.Count == 2 && texts.Values<JObject>().Select(c => c.Value<string>("culture")).OrderBy(c => c).SequenceEqual(new[] { "de-DE", "en-GB" }) && texts.All(c => !string.IsNullOrWhiteSpace(c.Value<string>("text")) && c.Value<string>("text").Length <= 256), "Exact bounded nonempty bilingual text input required.");
        }
        }
        var scratch = JObject.Parse(File.ReadAllText(a["scratchReceipt"].Value<string>("path")));
        Need(scratch.Value<bool?>("completed") == true && scratch["compile"].Value<int>("errorCount") == 0, "Successful native scratch provenance required.");
        if (args[0] == "seed") Need(scratch["project"].Value<string>("path") == projectPath && scratch.Value<int>("processId") == a.Value<int>("processId"), "Seed must target this worker's just-created scratch.");
        if (args[0] == "checkpoint") {
            Check((JObject)a["sessionOwnershipReceipt"]);
            var owned = JObject.Parse(File.ReadAllText(a["sessionOwnershipReceipt"].Value<string>("path")));
            Need(scratch["project"].Value<string>("path") == projectPath && owned.Value<bool?>("ownsPortalProcess") == true && owned.Value<int>("processId") == a.Value<int>("processId") &&
                ((JArray)owned["projects"]).Count == 1 && owned["projects"][0].Value<string>("path") == projectPath, "Checkpoint requires exact original owned scratch and current worker ownership receipt.");
        }
        int pid = a.Value<int>("processId");
        using (var p = Process.GetProcessById(pid)) Need(p.StartTime.ToUniversalTime().ToString("O") == a.Value<string>("processStartedAtUtc"), "Exact native PID/birth mismatch.");
        Directory.CreateDirectory(destination); output = destination; Save("admission.json", a);
        if (args[0] == "seed") File.Copy(a["reviewedInput"].Value<string>("path"), Path.Combine(output, "reviewed-input.json"));
        Event("attachIntent", new JObject { ["processId"] = pid });
        using (var portal = TiaPortal.GetProcesses().Single(p => p.Id == pid).Attach())
        using (portal.ExclusiveAccess("TIA MCP: admitted isolated bilingual VCI fixture")) {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase));
            Need(project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) && !project.IsModified, "Exact saved scratch required.");
            Need(project.DeviceGroups.Count == 0, "Nested device groups outside fixture scope.");
            var devices = project.Devices.Concat(project.UngroupedDevicesGroup.Devices).ToArray();
            var owners = devices.SelectMany(d => Items(d.DeviceItems)).ToArray();
            var plcs = owners.Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).ToArray();
            Need(plcs.Length == 1 && plcs[0].Name == a.Value<string>("plcName"), "Exact single PLC required.");
            var plc = plcs[0];
            var units = plc.GetService<PlcUnitProvider>();
            Need(units == null || units.UnitGroup.Units.Count == 0 && units.UnitGroup.SafetyUnits.Count == 0, "Software/safety units outside root fixture scope.");
            Need(plc.TechnologicalObjectGroup.TechnologicalObjects.Count == 0, "Technology objects outside fixture scope.");
            var before = Snapshot(portal, project, plc, owners, "before"); Save("before.json", before);
            var result = new JObject { ["mode"] = args[0], ["projectPath"] = projectPath, ["processId"] = pid, ["plcName"] = plc.Name, ["automaticReplay"] = false, ["nativeImportEnabled"] = false, ["watchForceMutationInvoked"] = false };
            if (args[0] == "checkpoint") {
                result["checkpoint"] = Archive(project, "current-saved");
                var after = Snapshot(portal, project, plc, owners, "after"); Save("after.json", after);
                Need(JToken.DeepEquals(StableSnapshot(before), StableSnapshot(after)), "Checkpoint altered native inventory.");
                result["nativeInventoryUnchanged"] = true; result["saveInvoked"] = false; result["languageOrCommentSetterInvoked"] = false;
            }
            if (args[0] == "seed") {
                Need(((JArray)before["languages"]["active"]).Values<string>().All(c => c == "de-DE" || c == "en-GB"), "Fresh two-culture fixture only; refuse any third culture.");
                foreach (JObject wanted in expected) {
                    var selected = plc.BlockGroup.Blocks.Find(wanted.Value<string>("name"));
                    Need(selected != null && selected.Number == wanted.Value<int>("number") && selected.GetType().Name == wanted.Value<string>("kind"), "Selected native block identity differs before mutation.");
                }
                result["preseedCheckpoint"] = Archive(project, "preseed"); Save("preseed-checkpoint.json", (JObject)result["preseedCheckpoint"]);
                Event("seedDispatchIntent", new JObject { ["checkpoint"] = result["preseedCheckpoint"].DeepClone(), ["reviewedInput"] = a["reviewedInput"].DeepClone(), ["effects"] = "Activate en-GB if absent; set two exact FC/FB comments in de-DE/en-GB; save once" });
                var en = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo("en-GB"));
                Need(project.LanguageSettings.ActiveLanguages.Any(l => l.Culture.Name == "de-DE"), "German baseline must already be active.");
                if (!project.LanguageSettings.ActiveLanguages.Contains(en)) project.LanguageSettings.ActiveLanguages.Add(en);
                foreach (JObject wanted in expected) {
                    var block = plc.BlockGroup.Blocks.Find(wanted.Value<string>("name"));
                    Need(block != null && block.Number == wanted.Value<int>("number") && block.GetType().Name == wanted.Value<string>("kind"), "Selected native block identity differs.");
                    foreach (JObject text in (JArray)wanted["comments"]) {
                        var language = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo(text.Value<string>("culture")));
                        block.Comment.Items.Find(language).Text = text.Value<string>("text");
                    }
                }
                Event("saveIntent", new JObject()); project.Save(); Event("saveReturned", new JObject());
                var after = Snapshot(portal, project, plc, owners, "after"); Save("after.json", after);
                foreach (JObject wanted in expected) {
                    var block = ((JArray)after["blocks"]).OfType<JObject>().Single(b => b.Value<string>("name") == wanted.Value<string>("name"));
                    Need(JToken.DeepEquals(block["comments"], new JArray(((JArray)wanted["comments"]).OrderBy(c => c.Value<string>("culture"), StringComparer.Ordinal))), "Direct exact bilingual readback differs.");
                }
                result["postseedCheckpoint"] = Archive(project, "postseed"); Save("postseed-checkpoint.json", (JObject)result["postseedCheckpoint"]);
                Event("seedReturned", result);
            }
            Need(!project.IsModified, "Final native project must be saved/unmodified.");
            result["completed"] = true; result["saved"] = args[0] == "seed"; result["readOnly"] = args[0] != "seed"; Save("result.json", result); return result;
        }
    }

    private static JObject Archive(Project p, string role) {
        string name = role + ".zap21"; Event(role + "ArchiveIntent", new JObject { ["path"] = Path.Combine(output, name) });
        p.Archive(new DirectoryInfo(output), name, ProjectArchivationMode.Compressed);
        var result = Artifact(Path.Combine(output, name)); result["role"] = role; Event(role + "ArchiveReturned", result); return result;
    }
    private static JObject StableSnapshot(JObject snapshot) {
        var result = (JObject)snapshot.DeepClone();
        foreach(string key in new[] { "blocks", "tags", "types", "watchForceTables" }) foreach(JObject row in (JArray)result[key]) {
            string path = row["artifact"].Value<string>("path");
            using(var reader=XmlReader.Create(path,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null, MaxCharactersInDocument=16777216 })) {
                var doc=XDocument.Load(reader,LoadOptions.PreserveWhitespace);
                Need(doc.Root.Name.LocalName=="Document", "Unknown native XML root.");
                var content=doc.Root.Elements().Where(e=>e.Name.LocalName!="Engineering" && e.Name.LocalName!="DocumentInfo").ToArray();
                Need(content.Length==1 && content[0].Name.LocalName.StartsWith("SW.",StringComparison.Ordinal),"Unknown native XML engineering shape.");
                row["artifact"]=content[0].ToString(SaveOptions.DisableFormatting);
            }
        }
        return result;
    }
    private static JObject Snapshot(TiaPortal portal, Project p, PlcSoftware plc, DeviceItem[] owners, string label) {
        string dir = Path.Combine(output, label); Directory.CreateDirectory(dir);
        var result = new JObject { ["projectPath"] = p.Path.FullName, ["plcName"] = plc.Name, ["languages"] = new JObject { ["active"] = new JArray(p.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name).OrderBy(x => x, StringComparer.Ordinal)), ["reference"] = p.LanguageSettings.ReferenceLanguage.Culture.Name, ["editing"] = p.LanguageSettings.EditingLanguage.Culture.Name } };
        var blocks = new JArray(); result["blocks"] = blocks;
        foreach (var g in BlockGroups(plc.BlockGroup, "root", 0)) foreach (var b in g.Item1.Blocks.OrderBy(b => b.Name, StringComparer.Ordinal)) {
            Need(blocks.Count < 32, "Block bound exceeded."); var row = Export(dir, "block", g.Item2, b.Name, blocks.Count, f => b.Export(f, ExportOptions.WithDefaults));
            row["number"] = b.Number; row["comments"] = new JArray(b.Comment.Items.Select(t => new JObject { ["culture"] = t.Language.Culture.Name, ["text"] = t.Text }).OrderBy(t => t.Value<string>("culture"), StringComparer.Ordinal)); blocks.Add(row);
        }
        var tags = new JArray(); result["tags"] = tags;
        foreach (var g in TagGroups(plc.TagTableGroup, "root", 0)) foreach (var t in g.Item1.TagTables.OrderBy(t => t.Name, StringComparer.Ordinal)) { Need(tags.Count < 32, "Tag table bound exceeded."); tags.Add(Export(dir, "tags", g.Item2, t.Name, tags.Count, f => t.Export(f, ExportOptions.WithDefaults))); }
        var types = new JArray(); result["types"] = types;
        foreach (var g in TypeGroups(plc.TypeGroup, "root", 0)) foreach (var t in g.Item1.Types.OrderBy(t => t.Name, StringComparer.Ordinal)) { Need(types.Count < 32, "Type bound exceeded."); types.Add(Export(dir, "type", g.Item2, t.Name, types.Count, f => t.Export(f, ExportOptions.WithDefaults))); }
        var groups = new JArray(); var tables = new JArray(); result["watchGroups"] = groups; result["watchForceTables"] = tables;
        foreach (var g in WatchGroups(plc.WatchAndForceTableGroup, "root", 0)) {
            groups.Add(g.Item2);
            foreach (var t in g.Item1.WatchTables.OrderBy(t => t.Name, StringComparer.Ordinal)) { Need(tables.Count < 32 && t.Entries.Count <= 200, "Watch bound exceeded."); var row = Export(dir, "watch", g.Item2, t.Name, tables.Count, f => t.Export(f, ExportOptions.WithDefaults)); row["rows"] = t.Entries.Count; tables.Add(row); }
            foreach (var t in g.Item1.ForceTables.OrderBy(t => t.Name, StringComparer.Ordinal)) { Need(tables.Count < 32 && t.Entries.Count <= 200, "Force bound exceeded."); var row = Export(dir, "force", g.Item2, t.Name, tables.Count, f => t.Export(f, ExportOptions.WithDefaults)); row["rows"] = t.Entries.Count; tables.Add(row); }
        }
        var access = new JArray(); result["watchForceAccess"] = access;
        foreach (var owner in owners) {
            var manager = owner.GetService<WatchAndForceTableAccessManager>();
            access.Add(new JObject { ["owner"] = OwnerPath(owner), ["available"] = manager != null,
                ["watch"] = manager == null ? null : new JArray(manager.WatchtableAccessRules.Select(r => new JObject { ["table"] = r.WatchTable.Name, ["access"] = r.Access.ToString() })),
                ["force"] = manager == null ? null : new JArray(manager.ForcetableAccessRules.Select(r => new JObject { ["table"] = r.ForceTable.Name, ["access"] = r.Access.ToString() })) });
        }
        var vci = p.GetService<VersionControlInterface>() ?? throw new InvalidOperationException("VCI service absent.");
        var workspaces = new JArray();
        foreach (var group in WorkspaceGroups(vci.WorkspaceGroup, 0)) foreach (var workspace in group.Workspaces.OrderBy(w => w.Name, StringComparer.Ordinal)) {
            Need(workspaces.Count < 64 && workspace.MappedObjects.Count <= 128, "VCI mapping bound exceeded.");
            workspaces.Add(new JObject { ["identity"] = Identity(workspace), ["name"] = workspace.Name, ["root"] = workspace.RootPath.FullName,
                ["culture"] = workspace.WorkspaceLanguage.Name, ["mappings"] = new JArray(workspace.MappedObjects.Select(m => new JObject {
                    ["identity"] = Identity(m.EngineeringObject), ["format"] = m.FileFormat, ["directory"] = m.DirectoryPath.FullName,
                    ["fileName"] = m.FileNameWithoutExtension, ["compareState"] = m.GetStatus().CompareState.ToString() })) });
        }
        var settings = portal.SettingsFolders.Find("VCI") ?? throw new InvalidOperationException("VCI settings absent.");
        var languageOption = settings.Settings.Find("ProjectLanguageImportOptions") ?? throw new InvalidOperationException("VCI language setting absent.");
        result["vci"] = new JObject { ["projectLanguageImportOptions"] = Convert.ToString(languageOption.Value, CultureInfo.InvariantCulture), ["workspaces"] = workspaces };
        result["completeWithinBounds"] = true; return result;
    }
    private static JArray Identity(IEngineeringObject value) {
        var result = new JArray(); int depth = 0;
        for(var current=value; current!=null; current=current.Parent) {
            Need(++depth<=32,"Native identity depth exceeded."); var entry=new JObject { ["type"]=current.GetType().FullName };
            foreach(var info in current.GetAttributeInfos().Where(i=>i.Name=="Name" || i.Name=="Number")) entry[info.Name]=Convert.ToString(current.GetAttribute(info.Name),CultureInfo.InvariantCulture);
            result.AddFirst(entry);
        }
        return result;
    }
    private static IEnumerable<WorkspaceGroup> WorkspaceGroups(WorkspaceGroup group,int depth) {
        Need(depth<=8 && group.Groups.Count<=16,"Workspace group bound exceeded."); yield return group;
        foreach(var child in group.Groups) foreach(var nested in WorkspaceGroups(child,depth+1)) yield return nested;
    }
    private static string OwnerPath(DeviceItem item) { var names = new List<string>(); for (IEngineeringObject o = item; o != null && !(o is Project); o = o.Parent) { Need(names.Count < 32, "Owner nesting bound."); var n = o.GetAttributeInfos().SingleOrDefault(a => a.Name == "Name"); if (n != null) names.Insert(0, Uri.EscapeDataString(Convert.ToString(o.GetAttribute("Name"), CultureInfo.InvariantCulture))); } return string.Join("/", names); }
    private static JObject Export(string dir, string kind, string group, string name, int n, Action<FileInfo> export) { string path = Path.Combine(dir, kind + "-" + n.ToString("00") + ".xml"); export(new FileInfo(path)); return new JObject { ["kind"] = kind, ["group"] = group, ["name"] = name, ["artifact"] = Artifact(path) }; }
    private static IEnumerable<Tuple<PlcBlockGroup,string>> BlockGroups(PlcBlockGroup g, string p, int d) { Need(d <= 8 && g.Groups.Count <= 16, "Block group bound."); yield return Tuple.Create(g,p); foreach(var c in g.Groups) foreach(var n in BlockGroups(c,p+"/"+Uri.EscapeDataString(c.Name),d+1)) yield return n; }
    private static IEnumerable<Tuple<PlcTagTableGroup,string>> TagGroups(PlcTagTableGroup g, string p, int d) { Need(d <= 8 && g.Groups.Count <= 16, "Tag group bound."); yield return Tuple.Create(g,p); foreach(var c in g.Groups) foreach(var n in TagGroups(c,p+"/"+Uri.EscapeDataString(c.Name),d+1)) yield return n; }
    private static IEnumerable<Tuple<PlcTypeGroup,string>> TypeGroups(PlcTypeGroup g, string p, int d) { Need(d <= 8 && g.Groups.Count <= 16, "Type group bound."); yield return Tuple.Create(g,p); foreach(var c in g.Groups) foreach(var n in TypeGroups(c,p+"/"+Uri.EscapeDataString(c.Name),d+1)) yield return n; }
    private static IEnumerable<Tuple<PlcWatchAndForceTableGroup,string>> WatchGroups(PlcWatchAndForceTableGroup g, string p, int d) { Need(d <= 8 && g.Groups.Count <= 16, "Watch group bound."); yield return Tuple.Create(g,p); foreach(var c in g.Groups) foreach(var n in WatchGroups(c,p+"/"+Uri.EscapeDataString(c.Name),d+1)) yield return n; }
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items, int d=0) { Need(d<=16,"Device depth bound."); foreach(var item in items) { yield return item; foreach(var n in Items(item.DeviceItems,d+1)) yield return n; } }
    private static void Guard(string root, string path) { Need(Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"Artifact root escape."); for(string p=path; !string.IsNullOrEmpty(p); p=Path.GetDirectoryName(p)) Need(!(File.Exists(p)||Directory.Exists(p)) || (File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Reparse path refused."); }
    private static void Check(JObject file) { Need(file!=null && File.Exists(file.Value<string>("path")) && Hash(file.Value<string>("path"))==file.Value<string>("sha256"),"Admitted file hash differs."); }
    private static string Hash(string p) { using(var f=File.OpenRead(p)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(f)).Replace("-","").ToLowerInvariant(); }
    private static JObject Artifact(string p) => new JObject { ["path"]=Path.GetFullPath(p), ["sha256"]=Hash(p), ["bytes"]=new FileInfo(p).Length };
    private static void Need(bool value,string reason) { if(!value) throw new InvalidOperationException(reason); }
    private static void Event(string name,JObject data) => Save("event-"+(++eventId).ToString("000")+"-"+name+".json",new JObject { ["stage"]=name, ["atUtc"]=DateTime.UtcNow, ["data"]=data.DeepClone(), ["automaticReplay"]=false });
    private static void Save(string name,JObject value) { byte[] bytes=new UTF8Encoding(false).GetBytes(value.ToString()); using(var stream=new FileStream(Path.Combine(output,name),FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough)) { stream.Write(bytes,0,bytes.Length); stream.Flush(true); } }
}
