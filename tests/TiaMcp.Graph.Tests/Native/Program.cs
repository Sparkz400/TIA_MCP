using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

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

    private static string output;
    private static string stage = "initialization";
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(Api, name + ".dll");
            return name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.None)); return 0; }
        catch (Exception e)
        {
            var result = new JObject { ["completed"] = false, ["stage"] = stage, ["error"] = e.GetType().FullName, ["message"] = e.ToString() };
            if (output != null && Directory.Exists(output)) Save("failure.json", result);
            Console.Error.WriteLine(result.ToString(Formatting.None)); return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JObject Run(string[] args)
    {
        if (args.Length < 2 || args.Length > 4 || (args.Length == 4 && args[3] != "--probe-actions")) throw new ArgumentException("Expected authored seed XML, NEW output directory under this runner's bin, optional owned pre-import archive, and optional --probe-actions.");
        string seed = Path.GetFullPath(args[0]);
        string candidate = Path.GetFullPath(args[1]);
        string bin = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        if (!candidate.StartsWith(bin.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Directory.Exists(candidate))
            throw new InvalidOperationException("A new directory beneath this runner's bin is required.");
        if (Path.Combine(candidate, "project", "GraphScratch", "GraphScratch.ap21").Length > 143)
            throw new InvalidOperationException("Scratch project path exceeds TIA's 143 character limit.");
        if (!File.Exists(seed)) throw new FileNotFoundException("Seed absent.", seed);
        string snapshot = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
        JObject sourceBaseline = null;
        if (snapshot != null)
        {
            if (!snapshot.StartsWith(bin.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(snapshot) != "before-import.zap21")
                throw new InvalidOperationException("Only this runner's owned, pre-import checkpoint is eligible.");
            sourceBaseline = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(snapshot), "baseline.json")));
            var ownership = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(snapshot), "project-ownership.json")));
            if (sourceBaseline["checkpoint"].Value<string>("sha256") != Hash(snapshot)
                || ownership.Value<string>("projectPath") != sourceBaseline.Value<string>("projectPath"))
                throw new InvalidOperationException("Owned checkpoint receipt does not match.");
        }
        output = candidate;
        Directory.CreateDirectory(output);
        File.Copy(seed, Path.Combine(output, "seed.xml"));
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(output, "TiaMcp.Graph.Native.exe"));
        Save("intent.json", new JObject { ["operation"] = "New owned TIA process and new GRAPH scratch project",
            ["seed"] = Artifact(Path.Combine(output, "seed.xml")), ["binary"] = Artifact(Assembly.GetExecutingAssembly().Location),
            ["attachExistingProcess"] = false, ["onlineAccess"] = false, ["replayOnFailure"] = false, ["startedAtUtc"] = DateTime.UtcNow,
            ["ownedSourceCheckpoint"] = snapshot == null ? null : Artifact(snapshot),
            ["nativeApi"] = new JArray(new[] { "Base", "Step7" }.Select(n => Artifact(Path.Combine(Api, "Siemens.Engineering." + n + ".dll")))) });
        string projectPath;
        using (var portal = CreatePortal("create"))
        {
            stage = "create-project";
            string projectDirectory = Path.Combine(output, "project");
            Directory.CreateDirectory(projectDirectory);
            var project = snapshot == null ? portal.Projects.Create(new DirectoryInfo(projectDirectory), "GraphScratch")
                : portal.Projects.Retrieve(new FileInfo(snapshot), new DirectoryInfo(projectDirectory));
            foreach (string culture in new[] { "en-US", "de-DE" })
            {
                var language = project.LanguageSettings.Languages.Find(CultureInfo.GetCultureInfo(culture));
                if (!project.LanguageSettings.ActiveLanguages.Contains(language)) project.LanguageSettings.ActiveLanguages.Add(language);
            }
            projectPath = project.Path.FullName;
            Save("project-ownership.json", new JObject { ["projectPath"] = projectPath, ["processId"] = portal.GetCurrentProcess().Id,
                ["creationMethod"] = snapshot == null ? "TiaPortal.Projects.Create" : "TiaPortal.Projects.Retrieve of owned pre-import checkpoint into new directory",
                ["createdAtUtc"] = DateTime.UtcNow });
            stage = "create-device";
            string cpuTypeIdentifier;
            if (snapshot == null)
            {
                var entries = portal.HardwareCatalog.Find("1511-1 PN").Where(e => e.TypeName == "CPU 1511-1 PN").ToArray();
                Save("cpu-catalog.json", new JObject { ["entries"] = new JArray(entries.Select(e => new JObject {
                    ["articleNumber"] = e.ArticleNumber, ["typeIdentifier"] = e.TypeIdentifier, ["version"] = e.Version })) });
                var entry = entries.OrderByDescending(e => VersionNumber(e.Version)).ThenBy(e => e.ArticleNumber).First();
                cpuTypeIdentifier = entry.TypeIdentifier;
                project.Devices.CreateWithItem(entry.TypeIdentifier, "GraphPLC", "GraphStation");
            }
            else cpuTypeIdentifier = sourceBaseline.Value<string>("cpuTypeIdentifier");
            var plc = GetPlc(project);
            if (plc.BlockGroup.Blocks.Find("GraphQualification") != null) throw new InvalidOperationException("Pre-import scratch must not contain the fixture block.");
            project.Save();
            project.Archive(new DirectoryInfo(output), "before-import.zap21", ProjectArchivationMode.Compressed);
            Save("baseline.json", new JObject { ["cpuTypeIdentifier"] = cpuTypeIdentifier, ["plcName"] = plc.Name,
                ["projectPath"] = projectPath, ["checkpoint"] = Artifact(Path.Combine(output, "before-import.zap21")) });
            if (args.Length == 4) return ProbeActions(plc, project);
            stage = "import-authored-seed";
            plc.BlockGroup.Blocks.Import(new FileInfo(Path.Combine(output, "seed.xml")), ImportOptions.None);
            Compile(plc, "imported");
            project.Save();
            Export(plc, "baseline");
            project.Archive(new DirectoryInfo(output), "qualified-baseline.zap21", ProjectArchivationMode.Compressed);
            stage = "no-op-import";
            plc.BlockGroup.Blocks.Import(new FileInfo(Path.Combine(output, "baseline.xml")), ImportOptions.Override);
            Compile(plc, "roundtrip");
            project.Save();
            Export(plc, "roundtrip");
            project.Close();
        }
        using (var portal = CreatePortal("reopen"))
        {
            stage = "fresh-process-reopen";
            var project = portal.Projects.Open(new FileInfo(projectPath));
            var plc = GetPlc(project);
            Compile(plc, "reopened");
            Export(plc, "reopened");
            project.Close();
        }
        using (var portal = CreatePortal("recovery"))
        {
            stage = "retrieve-checkpoint";
            string recoveryDirectory = Path.Combine(output, "recovered");
            Directory.CreateDirectory(recoveryDirectory);
            var project = portal.Projects.Retrieve(new FileInfo(Path.Combine(output, "qualified-baseline.zap21")),
                new DirectoryInfo(recoveryDirectory));
            Save("recovered-project.json", new JObject { ["projectPath"] = project.Path.FullName, ["processId"] = portal.GetCurrentProcess().Id });
            var plc = GetPlc(project);
            Compile(plc, "recovered");
            Export(plc, "recovered");
            project.Close();
        }
        var result = new JObject { ["completed"] = true, ["projectPath"] = projectPath, ["finishedAtUtc"] = DateTime.UtcNow,
            ["exports"] = new JArray(new[] { "baseline", "roundtrip", "reopened", "recovered" }.Select(n => Artifact(Path.Combine(output, n + ".xml")))),
            ["scope"] = "Native import, software compile, no-op roundtrip, fresh-process reopen and archive recovery; no online or runtime behavior claim." };
        Save("result.json", result);
        return result;
    }
    private static TiaPortal CreatePortal(string phase)
    {
        stage = "start-owned-process-" + phase;
        var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
        try
        {
            Save("process-" + phase + ".json", new JObject { ["processId"] = portal.GetCurrentProcess().Id,
                ["ownership"] = "Created directly by this runner with new TiaPortal; never attached.", ["startedAtUtc"] = DateTime.UtcNow });
            return portal;
        }
        catch { portal.Dispose(); throw; }
    }
    private static JObject ProbeActions(PlcSoftware plc, Project project)
    {
        var results = new JArray();
        // Independent blocks in a newly retrieved owned scratch. Never replay a failed import.
        foreach (string form in new[] { "assignment", "empty-prefix", "space-prefix", "space-both", "literal", "literal-end", "token-literal", "suffix-empty" })
        {
            var xml = XDocument.Load(Path.Combine(output, "seed.xml"));
            xml.Root.Element("SW.Blocks.FB").Element("AttributeList").Element("Name").Value = "GraphProbe_" + form.Replace('-', '_');
            XNamespace ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6";
            foreach (var action in xml.Descendants(ns + "Action"))
            {
                string symbol = action.Descendants(ns + "Component").SingleOrDefault()?.Attribute("Name")?.Value
                    ?? action.Element(ns + "Token")?.Attribute("Text")?.Value.TrimStart('#');
                if (string.IsNullOrEmpty(symbol)) throw new InvalidOperationException("Action probes require the authored simple-operand fixture.");
                var access = new XElement(ns + "Access", new XAttribute("Scope", "LocalVariable"),
                    new XElement(ns + "Symbol", new XElement(ns + "Component", new XAttribute("Name", symbol))));
                action.RemoveNodes();
                action.SetAttributeValue("Event", "");
                action.SetAttributeValue("Qualifier", "N");
                action.SetAttributeValue("Interlock", "false");
                Func<string, XElement> token = text => new XElement(ns + "Token", new XAttribute("Text", text));
                switch (form)
                {
                    case "assignment":
                        action.Add(access, token(":="), new XElement(ns + "Access", new XAttribute("Scope", "LiteralConstant"),
                            new XElement(ns + "Constant", new XElement(ns + "ConstantType", "Bool"), new XElement(ns + "ConstantValue", "true"))), token(";")); break;
                    case "empty-prefix": action.Add(token(""), access); break;
                    case "space-prefix": action.Add(token(" "), access); break;
                    case "space-both": action.Add(token(" "), access, token(" ")); break;
                    case "literal": action.Add(token("#" + symbol)); break;
                    case "literal-end": action.Add(token("#" + symbol + ";")); break;
                    case "token-literal": action.Add(token(""), token("#" + symbol), token("")); break;
                    default: action.Add(access, token("")); break;
                }
            }
            string path = Path.Combine(output, "probe-" + form + ".xml");
            xml.Save(path);
            var row = new JObject { ["form"] = form, ["seed"] = Artifact(path) };
            results.Add(row);
            try
            {
                stage = "probe-import-" + form;
                var block = plc.BlockGroup.Blocks.Import(new FileInfo(path), ImportOptions.None).Single();
                var compiled = block.GetService<ICompilable>().Compile();
                row["errors"] = compiled.ErrorCount; row["warnings"] = compiled.WarningCount; row["messages"] = Messages(compiled.Messages);
                if (compiled.ErrorCount == 0)
                {
                    string export = Path.Combine(output, "probe-" + form + "-native.xml");
                    block.Export(new FileInfo(export), ExportOptions.WithDefaults); row["nativeExport"] = Artifact(export);
                }
            }
            catch (Exception error) { row["error"] = error.ToString(); }
            Save("probe-" + form + ".json", row);
        }
        project.Save();
        var result = new JObject { ["probeCompleted"] = true, ["nativeQualified"] = false, ["results"] = results };
        Save("probe-results.json", result); project.Close(); return result;
    }
    private static Version VersionNumber(string value)
    { Version version; return Version.TryParse(value.TrimStart('V', 'v'), out version) ? version : new Version(0, 0); }
    private static PlcSoftware GetPlc(Project project)
    {
        return project.Devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware)
            .Where(p => p != null && p.Name == "GraphPLC").Single();
    }
    private static void Compile(PlcSoftware plc, string phase)
    {
        stage = "compile-" + phase;
        var result = plc.GetService<ICompilable>().Compile();
        Save("compile-" + phase + ".json", new JObject { ["state"] = result.State.ToString(), ["errors"] = result.ErrorCount,
            ["warnings"] = result.WarningCount, ["messages"] = Messages(result.Messages) });
        if (result.ErrorCount != 0) throw new InvalidOperationException("Native software compilation failed; see compile receipt.");
    }
    private static void Export(PlcSoftware plc, string phase)
    {
        stage = "export-" + phase;
        var block = plc.BlockGroup.Blocks.Find("GraphQualification");
        if (block == null || block.ProgrammingLanguage != ProgrammingLanguage.GRAPH) throw new InvalidOperationException("Expected GRAPH block absent.");
        block.Export(new FileInfo(Path.Combine(output, phase + ".xml")), ExportOptions.WithDefaults);
    }
    private static IEnumerable<DeviceItem> Items(IEnumerable<DeviceItem> items)
    { foreach (var item in items) { yield return item; foreach (var child in Items(item.DeviceItems)) yield return child; } }
    private static JArray Messages(IEnumerable<CompilerResultMessage> messages) => new JArray(messages.Select(m => new JObject
    { ["state"] = m.State.ToString(), ["description"] = m.Description, ["path"] = m.Path, ["messages"] = Messages(m.Messages) }));
    private static void Save(string name, JObject result)
    {
        using (var file = new StreamWriter(new FileStream(Path.Combine(output, name), FileMode.CreateNew))) file.Write(result.ToString(Formatting.Indented));
        Console.WriteLine("Recorded " + name);
    }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string path) => new JObject { ["path"] = path, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length };
}
