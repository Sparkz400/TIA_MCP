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
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.WatchAndForceTables;

// This collector always creates its own Portal. It never attaches to a user's process.
// UI edits and UI Save are performed by the coordinator. No force, connection, download,
// import, generic Create, field setter or project Save operation exists in this executable.
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

    private static string Output;
    private static int NativePid;

    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, evt) => {
            string name = new AssemblyName(evt.Name).Name, path = Path.Combine(Api, name + ".dll");
            return name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.Indented)); return 0; }
        catch (Exception e) { var failure = Failure(e); if (Output != null) Save("failure.json", failure); Console.WriteLine(failure); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        if (args.Length != 1) throw new InvalidOperationException("One exact admission JSON is required.");
        var admission = JObject.Parse(File.ReadAllText(args[0]));
        string mode = admission.Value<string>("mode");
        if (mode != "author-ui" && mode != "capture" && mode != "metadata") throw new InvalidOperationException("Only author-ui, fresh-process capture and read-only metadata modes are supported.");
        if (admission.Value<bool?>("ownedScratch") != true || admission.Value<bool?>("allProjectObjectsOwned") != true ||
            admission.Value<bool?>("sequentialNativeAdmission") != true || admission.Value<bool?>("checkpointRetrievalGatePassed") != true)
            throw new InvalidOperationException("Explicit sequential owned-scratch admission after the recovery gate is required.");
        string projectPath = Path.GetFullPath(admission.Value<string>("projectPath"));
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot"));
        string output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        Inside(root, projectPath); Inside(root, output);
        if (Directory.Exists(output)) throw new InvalidOperationException("Fresh output is required; automatic replay is forbidden.");
        if (!Path.GetFileNameWithoutExtension(projectPath).StartsWith("TiaMcpScratch_", StringComparison.Ordinal))
            throw new InvalidOperationException("Only an explicitly owned TiaMcpScratch project is supported.");
        if (Hash(projectPath) != admission.Value<string>("projectFileSha256")) throw new InvalidOperationException("Reviewed saved project file changed before launch.");
        if (admission.Value<string>("binarySha256") != Hash(Assembly.GetExecutingAssembly().Location)) throw new InvalidOperationException("Binary hash changed.");
        foreach (string part in new[] { "Base", "Step7" })
            if (admission["apiSha256"].Value<string>(part) != Hash(Path.Combine(Api, "Siemens.Engineering." + part + ".dll"))) throw new InvalidOperationException("API hash changed.");
        foreach (string key in new[] { "ownershipEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence", "reviewedCheckpoint", "oracle" }) CheckArtifact((JObject)admission[key]);
        var ownership = JObject.Parse(File.ReadAllText(admission["ownershipEvidence"].Value<string>("path")));
        string ownedPath = ownership["project"]?.Value<string>("path") ?? ownership.Value<string>("projectPath");
        if (ownership.Value<bool?>("completed") != true || !string.Equals(ownedPath, projectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Completed owned-project creation receipt is required.");
        var oracle = JObject.Parse(File.ReadAllText(admission["oracle"].Value<string>("path")));
        if (mode == "metadata")
        {
            if (oracle.Value<string>("profile") != "force-entries-metadata-v1" || !string.Equals(oracle.Value<string>("projectPath"), projectPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Exact read-only metadata oracle required.");
        }
        else ValidateOracle(oracle, projectPath);
        var sources = admission["sourceFiles"] as JArray;
        if (sources == null || sources.Count != 3) throw new InvalidOperationException("Exact three native helper source artifacts required.");
        foreach (JObject source in sources) CheckArtifact(source);
        Directory.CreateDirectory(output); Output = output;
        Save("admission.json", admission); Save("reviewed-oracle.json", oracle);
        File.Copy(admission["oracle"].Value<string>("path"), Path.Combine(Output, "reviewed-oracle-original.json"));
        foreach (string key in new[] { "ownershipEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence" })
            File.Copy(admission[key].Value<string>("path"), Path.Combine(Output, key + ".json"));
        File.Copy(admission["reviewedCheckpoint"].Value<string>("path"), Path.Combine(Output, "reviewed-before.zap21"));
        foreach (JObject source in sources) File.Copy(source.Value<string>("path"), Path.Combine(Output, "source-" + Path.GetFileName(source.Value<string>("path"))));
        foreach (string file in new[] { Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location + ".config", typeof(JObject).Assembly.Location })
            if (File.Exists(file)) File.Copy(file, Path.Combine(Output, Path.GetFileName(file)));
        Save("launch-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["mode"] = mode, ["projectPath"] = projectPath, ["automaticReplay"] = false });
        var result = new JObject { ["mode"] = mode, ["projectPath"] = projectPath, ["ownsNativeProcess"] = true,
            ["nativeAuthoringInvoked"] = false, ["nativeSaveInvoked"] = false, ["onlineOperationInvoked"] = false,
            ["forceOperationInvoked"] = false, ["automaticReplay"] = false, ["nativeQualified"] = false,
            ["reviewedOracleSha256"] = admission["oracle"].Value<string>("sha256"), ["restorationVerified"] = false };
        TiaPortal portal = null; Project project = null;
        try
        {
            portal = new TiaPortal(mode == "author-ui" ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface);
            var native = portal.GetCurrentProcess(); NativePid = native.Id;
            result["processId"] = NativePid; result["nativeMode"] = native.Mode.ToString();
            result["nativeProcessStartTimeUtc"] = Process.GetProcessById(NativePid).StartTime.ToUniversalTime().ToString("o");
            Save("launch-return.json", result);
            project = portal.Projects.Open(new FileInfo(projectPath));
            if (project.IsModified || project.Path.FullName != projectPath) throw new InvalidOperationException("Exact saved project required after fresh open.");
            var devices = Devices(project).ToArray();
            var items = devices.SelectMany(d => Items(d.DeviceItems, d.Name, 0)).ToArray();
            if (items.Length > 2000) throw new InvalidOperationException("Hardware inventory bound exceeded.");
            var plcs = items.Select(i => i.Item2.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).ToArray();
            if (plcs.Length != 1 || plcs[0].Name != oracle.Value<string>("plcName")) throw new InvalidOperationException("Exact one-PLC owned fixture required.");
            var plc = plcs[0];
            var units = plc.GetService<PlcUnitProvider>();
            if (units != null && (units.UnitGroup.Units.Count != 0 || units.UnitGroup.SafetyUnits.Count != 0)) throw new InvalidOperationException("Software-unit fixtures require a separate complete preservation profile.");
            var forces = WatchGroups(plc.WatchAndForceTableGroup, "root", 0).SelectMany(g => g.Item2.ForceTables).ToArray();
            if (forces.Length != 1 || forces[0].Name != oracle.Value<string>("forceTableName")) throw new InvalidOperationException("Exact one fixed force table required.");
            var force = forces[0];
            using (portal.ExclusiveAccess("TIA MCP: owned force fixture baseline capture"))
            {
                if (mode != "metadata") VerifyTagHardwareOracle(project, plc, items, oracle);
                var before = Capture(project, plc, force, items, "before");
                Save("before.json", before); result["before"] = Artifact("before.json");
            }
            if (mode == "author-ui")
            {
                Save("ui-open-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["forceTableName"] = force.Name, ["processId"] = NativePid });
                force.ShowInEditor();
                Save("ui-ready.json", new JObject { ["processId"] = NativePid, ["projectPath"] = projectPath,
                    ["forceTableName"] = force.Name, ["commandPath"] = Path.Combine(Output, "coordinator-command.json"),
                    ["requiredCommand"] = "capture-close", ["instruction"] = "Coordinator enters reviewed offline rows and saves through UI, with all force selections unchecked; then writes the exact command and oracle hash." });
                int minutes = admission.Value<int?>("uiTimeoutMinutes") ?? 30;
                if (minutes < 1 || minutes > 60) throw new InvalidOperationException("UI wait must be bounded to 1-60 minutes.");
                DateTime deadline = DateTime.UtcNow.AddMinutes(minutes);
                string commandFile = Path.Combine(Output, "coordinator-command.json");
                while (!File.Exists(commandFile) && DateTime.UtcNow < deadline) Thread.Sleep(250);
                if (!File.Exists(commandFile)) throw new TimeoutException("No coordinator capture command; no automatic save or replay.");
                var command = JObject.Parse(File.ReadAllText(commandFile));
                if (command.Value<string>("command") != "capture-close" || command.Value<string>("oracleSha256") != admission["oracle"].Value<string>("sha256"))
                    throw new InvalidOperationException("Only an exact oracle-bound capture-close command is accepted.");
            }
            using (portal.ExclusiveAccess("TIA MCP: owned force fixture saved readback capture"))
            {
                if (project.IsModified) throw new InvalidOperationException("Coordinator must save the authored fixture through UI before capture; helper never saves.");
                var afterItems = Devices(project).SelectMany(d => Items(d.DeviceItems, d.Name, 0)).ToArray();
                if (afterItems.Length > 2000) throw new InvalidOperationException("Hardware inventory bound exceeded.");
                if (mode != "metadata") VerifyTagHardwareOracle(project, plc, afterItems, oracle);
                var after = Capture(project, plc, force, afterItems, "after");
                Save("after.json", after); result["after"] = Artifact("after.json");
                var before = JObject.Parse(File.ReadAllText(Path.Combine(Output, "before.json")));
                bool peersUnchanged = JToken.DeepEquals(before["peers"], after["peers"]);
                result["peerFingerprintsUnchanged"] = peersUnchanged;
                result["populatedSignalRows"] = ((JArray)after["force"]["rows"]).Count(r => r.Value<string>("kind") == "signal" &&
                    (!string.IsNullOrWhiteSpace(r.Value<string>("name")) || !string.IsNullOrWhiteSpace(r.Value<string>("address"))) && r.Value<string>("displayFormat") != "Undef");
                result["oracleMatched"] = mode != "metadata" && JToken.DeepEquals(after["force"]["oracleRows"], oracle["rows"]);
                result["metadataOnly"] = mode == "metadata";
                result["projectModified"] = project.IsModified;
                Save("capture-return.json", result);
                if (!peersUnchanged || project.IsModified || (mode != "metadata" && (force.IsConsistent != true || result.Value<int>("populatedSignalRows") != 2 || result.Value<bool>("oracleMatched") != true)))
                    throw new InvalidOperationException("Force capture failed peers/population/independent-oracle checks; genuine captured bytes are retained and no replay is performed.");
            }
            project.Close(); project = null;
            Save("project-close-return.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["processId"] = NativePid });
            result["completed"] = true;
        }
        finally
        {
            // Explicitly owned native instance only. Never terminate or attach to any other TIA process.
            if (portal != null)
            {
                try { portal.GetCurrentProcess().Dispose(); }
                catch (Exception e) { Save("owned-dispose-failure.json", Failure(e)); }
                try { portal.Dispose(); }
                catch (Exception e) { Save("portal-dispose-failure.json", Failure(e)); }
            }
            bool exited = NativePid == 0;
            if (NativePid != 0)
            {
                try { using (var process = Process.GetProcessById(NativePid)) exited = process.WaitForExit(15000); }
                catch (ArgumentException) { exited = true; }
            }
            Save("cleanup.json", new JObject { ["nativeProcessId"] = NativePid, ["nativeProcessExited"] = exited,
                ["helperProcessId"] = Process.GetCurrentProcess().Id, ["helperExitNeedsCoordinatorProof"] = true,
                ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false });
            result["nativeProcessExited"] = exited;
        }
        Save("result.json", result);
        if (!result.Value<bool>("nativeProcessExited")) throw new InvalidOperationException("Owned native process remains; exact coordinator cleanup is required.");
        return result;
    }

    private static void ValidateOracle(JObject oracle, string projectPath)
    {
        if (oracle.Value<string>("reviewStatus") != "reviewed") throw new InvalidOperationException("A concrete independently reviewed oracle is required; templates are not admission.");
        if ((oracle.Value<string>("profile") != "owned-peripheral-bool-force-v1" && oracle.Value<string>("profile") != "owned-peripheral-direct-bool-force-v1") ||
            !string.Equals(oracle.Value<string>("projectPath"), projectPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Exact independent oracle profile/project required.");
        var rows = oracle["rows"] as JArray;
        if (rows == null || rows.Count != 3 || rows[0].Value<string>("kind") != "signal" || rows[1].Value<string>("kind") != "comment" || rows[2].Value<string>("kind") != "signal")
            throw new InvalidOperationException("Reviewed signal/comment/signal row order required.");
        foreach (JObject row in rows.Where(r => r.Value<string>("kind") == "signal"))
            if (row.Value<bool?>("forceIntention") != false || row.Value<string>("displayFormat") != "Bool" ||
                string.IsNullOrWhiteSpace(row.Value<string>("name")) && string.IsNullOrWhiteSpace(row.Value<string>("address")))
                throw new InvalidOperationException("Two nonblank Bool signal rows with forceIntention=false are required.");
        if (rows[0].Value<string>("forceValue") != "FALSE" || rows[2].Value<string>("forceValue") != "TRUE") throw new InvalidOperationException("Exact reviewed FALSE/TRUE stored values required.");
        bool direct = oracle.Value<string>("profile") == "owned-peripheral-direct-bool-force-v1";
        if (((JArray)oracle[direct ? "signals" : "tags"])?.Count != 2 || ((JArray)oracle["activeLanguages"])?.Count < 1 || ((JArray)oracle["activeLanguages"])?.Count > 3)
            throw new InvalidOperationException("Two reviewed native I/O bindings and all one-to-three existing active cultures are required.");
    }

    private static void VerifyTagHardwareOracle(Project project, PlcSoftware plc, Tuple<string, DeviceItem>[] items, JObject oracle)
    {
        var active = project.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (!active.SequenceEqual(((JArray)oracle["activeLanguages"]).Values<string>().OrderBy(n => n, StringComparer.Ordinal))) throw new InvalidOperationException("Active cultures differ from oracle; helper never adds cultures.");
        var tags = TagGroups(plc.TagTableGroup, "root", 0).SelectMany(g => g.Item2.TagTables.SelectMany(t => t.Tags)).ToArray();
        bool direct = oracle.Value<string>("profile") == "owned-peripheral-direct-bool-force-v1";
        foreach (JObject expected in (JArray)oracle[direct ? "signals" : "tags"])
        {
            var hardware = items.Single(i => i.Item1 == expected.Value<string>("hardwarePath")).Item2;
            int start = expected.Value<int>("rangeStartAddress"), length = expected.Value<int>("rangeLengthBits"), byteOffset = expected.Value<int>("byteOffset"), bitOffset = expected.Value<int>("bitOffset");
            string io = expected.Value<string>("ioType");
            string logical = "%" + (io == "Input" ? "I" : "Q") + byteOffset.ToString(CultureInfo.InvariantCulture) + "." + bitOffset.ToString(CultureInfo.InvariantCulture);
            if (!direct)
            {
                var tag = tags.Single(t => t.Name == expected.Value<string>("name"));
                if (tag.DataTypeName != "Bool" || tag.IsSafety || tag.LogicalAddress != expected.Value<string>("logicalAddress") || tag.LogicalAddress != logical)
                    throw new InvalidOperationException("Actual non-safety Bool declaration differs from the exact reviewed I/Q byte and bit.");
            }
            int ordinal = expected.Value<int>("forceOrdinal");
            if ((io == "Input" && ordinal != 0) || (io == "Output" && ordinal != 2) ||
                oracle["rows"][ordinal].Value<string>("name") != expected.Value<string>("forceName") ||
                oracle["rows"][ordinal].Value<string>("address") != expected.Value<string>("forceAddress"))
                throw new InvalidOperationException("Independent oracle must bind each exact force row to its reviewed native tag/address mapping.");
            if (io != "Input" && io != "Output" || bitOffset < 0 || bitOffset > 7 || start < 0 || length <= 0 || byteOffset < start || ((long)byteOffset - start) * 8 + bitOffset >= length)
                throw new InvalidOperationException("Reviewed BOOL address does not fit an explicit native peripheral range.");
            var range = hardware.Addresses.Single(a => a.IoType.ToString() == io && a.StartAddress == start && a.Length == length);
            if (!range.AddressControllers.Any() || range.AddressControllers.Any(c => !OwnedBy(c, plc))) throw new InvalidOperationException("Configured I/O range has missing or foreign PLC ownership.");
            if (direct && expected.Value<string>("forceAddress") != logical + ":P")
                throw new InvalidOperationException("Direct-address profile requires the explicit reviewed %I/%Q BOOL address with :P suffix; another native spelling needs a separate reviewed profile.");
        }
    }

    private static JObject Capture(Project project, PlcSoftware plc, PlcForceTable force, Tuple<string, DeviceItem>[] items, string stage)
    {
        if (project.IsModified) throw new InvalidOperationException("Saved offline fixture required for evidence capture.");
        foreach (var item in items)
        {
            var provider = item.Item2.GetService<OnlineProvider>();
            if (provider != null && provider.State != OnlineState.Offline) throw new InvalidOperationException("Read-only offline-state guard refused a connected/unknown target; no disconnect is performed.");
        }
        var peers = new JObject(); var artifacts = new JArray(); int number = 0;
        Action<string, Action<FileInfo>> export = (identity, write) => {
            string file = stage + "-peer-" + (++number) + ".xml"; write(new FileInfo(Path.Combine(Output, file)));
            peers[identity] = XmlHash(Path.Combine(Output, file)); artifacts.Add(new JObject { ["identity"] = identity, ["artifact"] = Artifact(file) });
        };
        foreach (var group in WatchGroups(plc.WatchAndForceTableGroup, "root", 0))
        {
            peers["watch-group:" + group.Item1] = group.Item2.Name;
            foreach (var watch in group.Item2.WatchTables) export("watch:" + group.Item1 + "/" + watch.Name, f => watch.Export(f, ExportOptions.WithDefaults));
        }
        foreach (var group in BlockGroups(plc.BlockGroup, "root", 0)) foreach (var block in group.Item2.Blocks) export("block:" + group.Item1 + "/" + block.Name, f => block.Export(f, ExportOptions.WithDefaults));
        foreach (var group in TypeGroups(plc.TypeGroup, "root", 0)) foreach (var type in group.Item2.Types) export("type:" + group.Item1 + "/" + type.Name, f => type.Export(f, ExportOptions.WithDefaults));
        foreach (var group in TagGroups(plc.TagTableGroup, "root", 0)) foreach (var table in group.Item2.TagTables) export("tags:" + group.Item1 + "/" + table.Name, f => table.Export(f, ExportOptions.WithDefaults));
        if (number > 1000) throw new InvalidOperationException("Preservation export bound exceeded.");
        peers["hardware"] = new JArray(items.Select(i => new JObject { ["path"] = i.Item1, ["name"] = i.Item2.Name, ["typeIdentifier"] = i.Item2.TypeIdentifier,
            ["positionNumber"] = i.Item2.PositionNumber, ["addresses"] = new JArray(i.Item2.Addresses.Select(a => new JObject { ["ioType"] = a.IoType.ToString(), ["startAddress"] = a.StartAddress, ["lengthBits"] = a.Length,
                ["ownerCount"] = a.AddressControllers.Count, ["allOwnersMatchPlc"] = a.AddressControllers.All(c => OwnedBy(c, plc)) })) }));
        peers["languages"] = new JObject { ["active"] = new JArray(project.LanguageSettings.ActiveLanguages.Select(l => l.Culture.Name)), ["editing"] = project.LanguageSettings.EditingLanguage.Culture.Name, ["reference"] = project.LanguageSettings.ReferenceLanguage.Culture.Name };
        peers["access"] = new JArray(items.Select(i => {
            var manager = i.Item2.GetService<WatchAndForceTableAccessManager>();
            return new JObject { ["owner"] = i.Item1, ["available"] = manager != null,
                ["watch"] = manager == null ? null : new JArray(manager.WatchtableAccessRules.Select(r => new JObject { ["name"] = r.WatchTable.Name, ["access"] = r.Access.ToString() })),
                ["force"] = manager == null ? null : new JArray(manager.ForcetableAccessRules.Select(r => new JObject { ["name"] = r.ForceTable.Name, ["access"] = r.Access.ToString() })) };
        }));
        if (force.Entries.Count > 200) throw new InvalidOperationException("Force row bound exceeded.");
        var rows = new JArray(); var oracleRows = new JArray();
        foreach (var entry in force.Entries)
        {
            var attributes = Attributes(entry); var comments = new JObject();
            foreach (JObject attr in attributes.Where(a => a.Value<string>("name") == "Comment"))
                foreach (JObject text in (JArray)attr["value"]) comments[text.Value<string>("culture")] = text.Value<string>("text");
            JObject row;
            if (entry is PlcForceTableEntry signal)
            {
                row = new JObject { ["kind"] = "signal", ["name"] = signal.Name, ["address"] = signal.Address, ["displayFormat"] = signal.DisplayFormat.ToString(),
                    ["forceValue"] = signal.ForceValue, ["forceIntention"] = signal.ForceIntention, ["monitorTrigger"] = signal.MonitorTrigger.ToString(), ["comments"] = comments };
                if (signal.ForceIntention) { Save(stage + "-refused-force-intention.json", row); throw new InvalidOperationException("ForceIntention=true is forbidden; no repair or force operation is performed."); }
            }
            else row = new JObject { ["kind"] = "comment", ["comments"] = comments };
            oracleRows.Add(row.DeepClone()); row["runtimeType"] = entry.GetType().FullName; row["attributes"] = attributes; rows.Add(row);
        }
        string forceFile = stage + "-force.xml"; force.Export(new FileInfo(Path.Combine(Output, forceFile)), ExportOptions.WithDefaults);
        var observed = new JObject { ["name"] = force.Name, ["isConsistent"] = force.IsConsistent, ["rows"] = rows, ["oracleRows"] = oracleRows,
            ["metadata"] = ForceEntriesMetadata.Capture(force), ["artifact"] = Artifact(forceFile), ["semanticSha256"] = XmlHash(Path.Combine(Output, forceFile)) };
        return new JObject { ["stage"] = stage, ["atUtc"] = DateTime.UtcNow, ["processId"] = NativePid, ["projectPath"] = project.Path.FullName,
            ["force"] = observed, ["peers"] = peers, ["peerArtifacts"] = artifacts, ["projectModified"] = project.IsModified,
            ["preservationLimit"] = "Exported root/user block, type, tag and watch groups plus configured hardware/address, cultures and exposed table access. System blocks, vendor-unexposed state and software units are not qualified." };
    }

    private static JArray Attributes(IEngineeringObject obj)
    {
        var infos = obj.GetAttributeInfos(); if (infos.Count > 128) throw new InvalidOperationException("Attribute metadata bound exceeded.");
        return new JArray(infos.Select(info => {
            var item = new JObject { ["name"] = info.Name, ["access"] = info.AccessMode.ToString(), ["supportedTypes"] = new JArray(info.SupportedTypes.Select(t => t.FullName)) };
            if ((info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
            {
                object value = obj.GetAttribute(info.Name);
                if (value is MultilingualText text) { if (text.Items.Count > 32) throw new InvalidOperationException("Culture bound exceeded."); item["value"] = new JArray(text.Items.Select(t => new JObject { ["culture"] = t.Language.Culture.Name, ["text"] = t.Text })); }
                else if (value == null) item["value"] = JValue.CreateNull();
                else if (value is string || value is bool || value.GetType().IsPrimitive || value.GetType().IsEnum) item["value"] = JToken.FromObject(value.GetType().IsEnum ? value.ToString() : value);
                else item["value"] = new JObject { ["nativeType"] = value.GetType().FullName, ["notScalar"] = true };
            }
            return item;
        }));
    }
    private static bool OwnedBy(IEngineeringObject obj, PlcSoftware plc)
    {
        for (int depth = 0; obj != null && depth < 64; depth++, obj = obj.Parent)
            if (obj is DeviceItem device && device.GetService<SoftwareContainer>()?.Software is PlcSoftware owner) return owner.Equals(plc);
        return false;
    }
    private static IEnumerable<Device> Devices(Project project) => project.Devices.Concat(project.UngroupedDevicesGroup.Devices);
    private static IEnumerable<Tuple<string, DeviceItem>> Items(IEnumerable<DeviceItem> source, string prefix, int depth)
    { if (depth > 20) throw new InvalidOperationException("Hardware depth bound exceeded."); foreach (var item in source) { string path = prefix + "/" + item.Name; yield return Tuple.Create(path, item); foreach (var child in Items(item.DeviceItems, path, depth + 1)) yield return child; } }
    private static IEnumerable<Tuple<string, PlcWatchAndForceTableGroup>> WatchGroups(PlcWatchAndForceTableGroup root, string path, int depth)
    { if (depth > 12 || root.Groups.Count > 100) throw new InvalidOperationException("Watch group bound."); yield return Tuple.Create(path, root); foreach (var group in root.Groups) foreach (var child in WatchGroups(group, path + "/" + group.Name, depth + 1)) yield return child; }
    private static IEnumerable<Tuple<string, PlcBlockGroup>> BlockGroups(PlcBlockGroup root, string path, int depth)
    { if (depth > 12 || root.Groups.Count > 100) throw new InvalidOperationException("Block group bound."); yield return Tuple.Create(path, root); foreach (var group in root.Groups) foreach (var child in BlockGroups(group, path + "/" + group.Name, depth + 1)) yield return child; }
    private static IEnumerable<Tuple<string, PlcTypeGroup>> TypeGroups(PlcTypeGroup root, string path, int depth)
    { if (depth > 12 || root.Groups.Count > 100) throw new InvalidOperationException("Type group bound."); yield return Tuple.Create(path, root); foreach (var group in root.Groups) foreach (var child in TypeGroups(group, path + "/" + group.Name, depth + 1)) yield return child; }
    private static IEnumerable<Tuple<string, PlcTagTableGroup>> TagGroups(PlcTagTableGroup root, string path, int depth)
    { if (depth > 12 || root.Groups.Count > 100) throw new InvalidOperationException("Tag group bound."); yield return Tuple.Create(path, root); foreach (var group in root.Groups) foreach (var child in TagGroups(group, path + "/" + group.Name, depth + 1)) yield return child; }
    private static string XmlHash(string path)
    {
        // Latin-1 is used only as a reversible byte transport for the ASCII timestamp mask.
        // Do not normalize BOM, line endings, indentation, unknown payload or non-ASCII text.
        var transport = Encoding.GetEncoding(28591);
        string raw = transport.GetString(File.ReadAllBytes(path)); var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var timestamp = document.Root?.Element("DocumentInfo")?.Element("Created");
        if (timestamp == null) throw new InvalidOperationException("Expected native export timestamp is absent.");
        var pattern = new Regex(@"(<DocumentInfo>\s*<Created>)[^<]*(</Created>)", RegexOptions.CultureInvariant);
        if (pattern.Matches(raw).Count != 1) throw new InvalidOperationException("Only the one observed DocumentInfo/Created timestamp may be masked.");
        return HashBytes(transport.GetBytes(pattern.Replace(raw, "$1__NATIVE_EXPORT_TIME__$2", 1)));
    }
    private static void CheckArtifact(JObject artifact) { if (artifact == null || Hash(artifact.Value<string>("path")) != artifact.Value<string>("sha256")) throw new InvalidOperationException("Admitted artifact hash changed."); }
    private static void Inside(string root, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path leaves admitted owned artifact root.");
        for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse points are not admitted.");
    }
    private static JObject Failure(Exception e) => new JObject { ["type"] = e.GetType().FullName, ["message"] = e.Message, ["nativeProcessId"] = NativePid, ["inner"] = e.InnerException == null ? null : Failure(e.InnerException), ["automaticReplay"] = false };
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static string HashBytes(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string relative) { string path = Path.Combine(Output, relative); return new JObject { ["path"] = relative, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length }; }
    private static void Save(string name, JObject data) { byte[] bytes = Encoding.UTF8.GetBytes(data.ToString(Formatting.Indented)); using (var file = new FileStream(Path.Combine(Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } }
}
