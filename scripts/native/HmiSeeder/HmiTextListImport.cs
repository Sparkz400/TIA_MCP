using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

// Imports Host-authored Unified text-list candidate pairs into a new owned fixture through the
// documented HmiTextListComposition.Import/Export. The runner compares the recorded exports;
// this mode only records native results. No existing project, script execution or download.
internal static partial class Program
{
    private static int ImportTextLists(string outputPath, string candidatesPath)
    {
        string candidates = Path.GetFullPath(candidatesPath);
        string manifestPath = Path.Combine(candidates, "manifest.json");
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));
        var main = CandidatePairs(candidates, manifest["main"]);
        var probes = CandidatePairs(candidates, manifest["probes"]);
        Output = Path.GetFullPath(outputPath);
        if (Directory.Exists(Output) || File.Exists(Output)) throw new InvalidOperationException("Choose a new output directory; existing evidence is never overwritten.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(Output)); parent != null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Output ancestry cannot contain reparse points.");
        Directory.CreateDirectory(Output);
        TextListFixture = true; PositiveFixture = true; CurrentProfile = "unified-comfort";
        Report = new JObject { ["schemaVersion"] = 1, ["mode"] = "textlist-import", ["completed"] = false, ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["scope"] = "Host-authored Unified text-list candidates imported into a newly created owned MTP700 Unified Comfort fixture",
            ["seederSha256"] = Hash(System.Reflection.Assembly.GetExecutingAssembly().Location), ["candidatesManifestSha256"] = Hash(manifestPath),
            ["assembly"] = typeof(TiaPortal).Assembly.FullName, ["runtimeVerified"] = false, ["scriptsExecuted"] = false,
            ["imports"] = new JArray(), ["probes"] = new JArray(), ["portals"] = new JArray(), ["cleanup"] = "pending" };
        Persist();
        string projectPath = null, checkpoint = Path.Combine(Output, "checkpoint.zap21"), final = Path.Combine(Output, "final.zap21");
        OwnedPortal("authoring", portal =>
        {
            var project = portal.Projects.Create(new DirectoryInfo(Path.Combine(Output, "p")), "TiaMcpTl_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                projectPath = project.Path.FullName; Report["projectPath"] = projectPath;
                Report["languages"] = ConfigureResourceLanguages(project);
                var selected = portal.HardwareCatalog.Find("MTP700 Unified Comfort").Where(item => item.TypeName == "MTP700 Unified Comfort")
                    .OrderByDescending(item => item.Version).ThenBy(item => item.TypeIdentifier, StringComparer.Ordinal).First();
                Report["selectedTarget"] = new JObject { ["typeIdentifier"] = selected.TypeIdentifier, ["version"] = selected.Version?.ToString(), ["typeName"] = selected.TypeName };
                var device = project.Devices.CreateWithItem(selected.TypeIdentifier, null, "HmiDevice_A");
                var sw = Items(device.DeviceItems).Select(item => item.GetService<SoftwareContainer>()?.Software)
                    .Single(item => item != null && item.GetType().FullName.StartsWith("Siemens.Engineering.HmiUnified.", StringComparison.Ordinal));
                // Screens, a tag, start screen, an alarm, a screen event script and the documented bilingual peer list.
                Report["seed"] = Seed(sw, Output); Persist();
                Report["baseline"] = Inspect(sw, Output, "baseline");
                Report["baselineRepeat"] = Inspect(sw, Output, "repeat");
                Report["compileBaseline"] = Compile(new[] { sw }); Persist();
                project.Save();
                project.Archive(new DirectoryInfo(Output), "checkpoint.zap21", ProjectArchivationMode.Compressed);
                Report["checkpoint"] = new JObject { ["path"] = checkpoint, ["sha256"] = Hash(checkpoint) }; Persist();
                for (int index = 0; index < main.Length; index++) { ((JArray)Report["imports"]).Add(ImportPair(sw, main[index], "i" + index)); Persist(); }
                // No-change roundtrip: the whole native export goes back in through the same import.
                var last = (JObject)((JArray)Report["imports"]).Last["export"];
                string exported = Path.GetDirectoryName(last["export"][0].Value<string>("path"));
                var files = Directory.GetFiles(exported).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
                Report["roundtrip"] = ImportPair(sw, new JObject { ["label"] = "roundtrip", ["directory"] = exported,
                    ["configuration"] = files.Single(name => name.EndsWith(".hmi.yml", StringComparison.Ordinal) && !name.EndsWith(".TextLibrary.hmi.yml", StringComparison.Ordinal)),
                    ["files"] = new JArray(files.Select(name => new JObject { ["name"] = name, ["sha256"] = Hash(Path.Combine(exported, name)) })) }, "rt");
                Report["after"] = Inspect(sw, Output, "after");
                Report["compileAfter"] = Compile(new[] { sw }); Persist();
                project.Save();
                project.Archive(new DirectoryInfo(Output), "final.zap21", ProjectArchivationMode.Compressed);
                Report["final"] = new JObject { ["path"] = final, ["sha256"] = Hash(final) }; Persist();
            }
            finally { project.Close(); }
        });
        // A fresh Portal process reads the saved project, the final archive and the pre-import checkpoint.
        OwnedPortal("verification", portal =>
        {
            Report["reopened"] = Staged(portal.Projects.Open(new FileInfo(projectPath)), "reopened", true);
            Report["retrieved"] = Staged(Retrieve(portal, final, "r1"), "retrieved", false);
            var restored = Retrieve(portal, checkpoint, "r2");
            try
            {
                var sw = FindSoftware(restored).Single();
                Report["restored"] = Inspect(sw, Output, "restored");
                // Boundary probes run on the restored copy, which is closed unsaved and retrieved again below.
                for (int index = 0; index < probes.Length; index++) { ((JArray)Report["probes"]).Add(ImportPair(sw, probes[index], "p" + index)); Persist(); }
            }
            finally { restored.Close(); }
            Report["restoredAgain"] = Staged(Retrieve(portal, checkpoint, "r3"), "restored-again", false);
            Report["checkpointUnchanged"] = Hash(checkpoint) == Report["checkpoint"].Value<string>("sha256");
        });
        Report["cleanup"] = "owned-portals-exited";
        Report["completed"] = ((JArray)Report["portals"]).All(row => row.Value<bool?>("exitVerified") == true);
        Persist();
        Console.WriteLine(new JObject { ["report"] = Path.Combine(Output, "native-report.json"), ["completed"] = Report["completed"] }.ToString(Newtonsoft.Json.Formatting.None));
        return Report.Value<bool>("completed") ? 0 : 1;
    }

    private static JObject[] CandidatePairs(string root, JToken rows)
    {
        if (!(rows is JArray list) || list.Count > 64) throw new InvalidDataException("Candidate manifest lists at most 64 pairs.");
        return list.Select(token =>
        {
            var row = (JObject)token.DeepClone();
            string directory = Path.GetFullPath(Path.Combine(root, row.Value<string>("directory")));
            if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Candidate directory escapes the manifest root.");
            var files = (JArray)row["files"];
            if (files.Count != 2 || !files.Any(f => f.Value<string>("name") == row.Value<string>("configuration"))) throw new InvalidDataException("A candidate is one configuration file and its TextLibrary file.");
            foreach (var file in files)
            {
                string path = Path.Combine(directory, file.Value<string>("name"));
                if (Path.GetDirectoryName(path) != directory || Hash(path) != file.Value<string>("sha256")) throw new InvalidDataException("Candidate file differs from the manifest: " + path);
            }
            row["directory"] = directory;
            return row;
        }).ToArray();
    }

    private static JObject ImportPair(Software sw, JObject pair, string stage)
    {
        var result = new JObject { ["label"] = pair["label"], ["configuration"] = pair["configuration"], ["inputs"] = new JArray() };
        try
        {
            // Copied into the owned output so the import reads exactly the hashed bytes and the evidence keeps them.
            string input = Path.Combine(Output, "in-" + stage); Directory.CreateDirectory(input);
            foreach (var file in pair["files"])
            {
                string name = file.Value<string>("name"), destination = Path.Combine(input, name);
                File.Copy(Path.Combine(pair.Value<string>("directory"), name), destination);
                if (Hash(destination) != file.Value<string>("sha256")) throw new InvalidDataException("Copied candidate differs: " + name);
                ((JArray)result["inputs"]).Add(new JObject { ["path"] = destination, ["sha256"] = file["sha256"] });
            }
            object composition = Get(sw, "HmiTextLists");
            var import = composition.GetType().GetMethod("Import", new[] { typeof(DirectoryInfo), typeof(string) });
            var started = Stopwatch.StartNew();
            try { result["accepted"] = (bool)import.Invoke(composition, new object[] { new DirectoryInfo(input), pair.Value<string>("configuration") }); }
            catch (Exception ex) { result["accepted"] = false; result["error"] = ErrorChain(ex); }
            result["elapsedMs"] = started.ElapsedMilliseconds;
            result["listNames"] = new JArray(((IEnumerable)composition).Cast<IEngineeringObject>().Select(item => (string)item.GetAttribute("Name")));
        }
        catch (Exception ex) { result["accepted"] = false; result["setupError"] = ErrorChain(ex); }
        result["export"] = ExportTextLists(sw, Output, stage);
        return result;
    }

    private static JObject Staged(Project project, string stage, bool compile)
    {
        try
        {
            var sw = FindSoftware(project).Single();
            var result = Inspect(sw, Output, stage);
            if (compile) result["compile"] = Compile(new[] { sw });
            result["projectPath"] = project.Path.FullName;
            Persist();
            return result;
        }
        finally { project.Close(); }
    }

    private static Project Retrieve(TiaPortal portal, string archive, string directory)
    {
        string target = Path.Combine(Output, directory); Directory.CreateDirectory(target);
        return portal.Projects.Retrieve(new FileInfo(archive), new DirectoryInfo(target));
    }

    // One owned Portal at a time; the exact process handle must exit after disposal.
    private static void OwnedPortal(string name, Action<TiaPortal> body)
    {
        var row = new JObject { ["name"] = name }; ((JArray)Report["portals"]).Add(row);
        Process process = null; Exception failure = null;
        try
        {
            using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                process = Process.GetProcessById(portal.GetCurrentProcess().Id);
                row["processId"] = process.Id; row["startedUtc"] = process.StartTime.ToUniversalTime().ToString("O"); Persist();
                RecordProcesses(portal, name + ":started");
                try { body(portal); }
                catch (Exception ex) { failure = ex; row["error"] = ErrorChain(ex); }
                portal.GetCurrentProcess().Dispose();
            }
        }
        finally
        {
            if (process != null) { row["exitVerified"] = process.WaitForExit(120000); process.Dispose(); }
            RecordProcesses(null, name + ":disposed");
            Persist();
        }
        if (failure != null) throw new InvalidOperationException("Owned portal stage failed: " + name, failure);
    }

    private static JObject ErrorChain(Exception ex)
    {
        var result = Error(ex); var inner = new JArray();
        for (var current = ex.InnerException; current != null && inner.Count < 8; current = current.InnerException)
            inner.Add(new JObject { ["type"] = current.GetType().FullName, ["message"] = current.Message });
        if (inner.Count > 0) result["inner"] = inner;
        return result;
    }
}
