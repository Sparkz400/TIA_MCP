using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;
using Siemens.Engineering.TestSuite.StyleGuide;
using Siemens.Engineering.TestSuite.SystemTest;

// Read-only native collector. The integration owner alone admits its sequential invocation.
// There is deliberately no import, SetScope, save, Run, simulator or endpoint operation.
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
        if (args.Length != 1) throw new InvalidOperationException("One exact admission JSON path is required.");
        var admission = JObject.Parse(File.ReadAllText(args[0]));
        if (admission.Value<string>("mode") != "inspect-system-scope" || admission.Value<bool?>("ownedScratch") != true ||
            admission.Value<bool?>("allProjectObjectsOwned") != true || admission.Value<bool?>("sequentialNativeAdmission") != true ||
            admission.Value<bool?>("checkpointRetrievalGatePassed") != true || admission.Value<string>("systemCaseName") != "SingleStep")
            throw new InvalidOperationException("Only the explicitly admitted owned SingleStep read-only profile is supported.");
        if (admission.Value<string>("binarySha256") != Hash(Assembly.GetExecutingAssembly().Location)) throw new InvalidOperationException("Binary hash changed.");
        foreach (string part in new[] { "Base", "TestSuite" })
            if (admission["apiSha256"].Value<string>(part) != Hash(Path.Combine(Api, "Siemens.Engineering." + part + ".dll")))
                throw new InvalidOperationException("API hash changed.");
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot"));
        string output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        string projectPath = Path.GetFullPath(admission.Value<string>("projectPath"));
        Inside(root, projectPath); Inside(root, output);
        if (Directory.Exists(output)) throw new InvalidOperationException("A fresh output directory is required; automatic replay is forbidden.");
        foreach (string key in new[] { "ownershipEvidence", "openObservationEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence", "reviewedSource" })
            CheckArtifact((JObject)admission[key]);
        var ownership = JObject.Parse(File.ReadAllText(admission["ownershipEvidence"].Value<string>("path")));
        if (ownership.Value<bool?>("completed") != true || !string.Equals(ownership["project"]?.Value<string>("path"), projectPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The retained creation receipt does not identify this completed owned project.");
        if (admission["reviewedSource"].Value<string>("sha256") != "7c139e29b965e7769e32b8cf14be55b2f0ef6f4a83febd1ca78cb10e0c77bcee")
            throw new InvalidOperationException("Only the unchanged reviewed genuine vendor-source profile is supported.");
        var sources = admission["sourceFiles"] as JArray;
        if (sources == null || sources.Count != 2 || sources.Any(s => s is not JObject)) throw new InvalidOperationException("Exact two helper source artifacts are required.");
        foreach (JObject source in sources) CheckArtifact(source);
        var nativeProcess = Process.GetProcessById(admission.Value<int>("processId"));
        string started = nativeProcess.StartTime.ToUniversalTime().ToString("o");
        if (started != admission.Value<string>("nativeProcessStartTimeUtc")) throw new InvalidOperationException("Exact process start identity changed.");
        Directory.CreateDirectory(output); Output = output;
        Save("admission.json", admission);
        foreach (string key in new[] { "ownershipEvidence", "openObservationEvidence", "resourceAdmissionEvidence", "checkpointRetrievalEvidence", "reviewedSource" })
            File.Copy(admission[key].Value<string>("path"), Path.Combine(Output, key == "reviewedSource" ? "reviewed-source.tst" : key + ".json"));
        foreach (JObject source in sources) File.Copy(source.Value<string>("path"), Path.Combine(Output, "source-" + Path.GetFileName(source.Value<string>("path"))));
        foreach (string file in new[] { Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location + ".config", typeof(JObject).Assembly.Location })
            if (File.Exists(file)) File.Copy(file, Path.Combine(Output, Path.GetFileName(file)));

        var process = TiaPortal.GetProcesses().Single(p => p.Id == admission.Value<int>("processId"));
        using (var portal = process.Attach())
        using (portal.ExclusiveAccess("TIA MCP: read-only retained System Test scope preservation evidence"))
        {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, projectPath, StringComparison.OrdinalIgnoreCase));
            if (!project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || project.IsModified)
                throw new InvalidOperationException("Exact saved owned scratch is required.");
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service is absent.");
            ExactInventory(suite);
            var selected = suite.SystemTestGroup.SystemTestCases.Find("SingleStep");
            var result = new JObject {
                ["mode"] = "inspect-system-scope", ["processId"] = process.Id, ["nativeProcessStartTimeUtc"] = started,
                ["projectPath"] = project.Path.FullName, ["projectOpenEvidenceSha256"] = admission["openObservationEvidence"].Value<string>("sha256"),
                ["startedAtUtc"] = DateTime.UtcNow, ["beforeModified"] = project.IsModified,
                ["automaticReplay"] = false, ["nativeMutationInvoked"] = false, ["applicationExecutionInvoked"] = false,
                ["systemExecutionInvoked"] = false, ["styleExecutionInvoked"] = false, ["endpointConnectionInvoked"] = false,
                ["nativeQualified"] = false, ["testsPassed"] = false, ["effectiveStyleScopeVerified"] = false,
                ["reviewedSource"] = Artifact("reviewed-source.tst"), ["preservationScope"] = "All three exact Test Suite definition exports and selected system typed scope only; project-wide peer preservation is not claimed."
            };
            // Flush typed values before the first SaveToFile call. Null is retained and never silently replaced by empty text.
            Save("typed-before.json", ReadScope(selected)); result["typedBefore"] = Artifact("typed-before.json");
            result["definitionsBefore"] = ExportDefinitions(suite, "before");
            Save("typed-after-first-export.json", ReadScope(selected)); result["typedAfterFirstExport"] = Artifact("typed-after-first-export.json");
            result["definitionsAfter"] = ExportDefinitions(suite, "after");
            Save("typed-after.json", ReadScope(selected)); result["typedAfter"] = Artifact("typed-after.json");
            ExactInventory(suite);
            result["afterModified"] = project.IsModified;
            result["completed"] = true; result["finishedAtUtc"] = DateTime.UtcNow;
            Save("result.json", result);
            if (project.IsModified) throw new InvalidOperationException("The read-only probe left the project modified; coordinator reconciliation is required.");
            return result;
        }
    }

    private static void ExactInventory(TestSuiteService suite)
    {
        if (suite.StyleGuideGroup.RuleSets.Count != 1 || suite.StyleGuideGroup.RuleSets[0].Name != "TiaMcp_CasingAvailability" ||
            suite.ApplicationTestGroup.TestCases.Count != 1 || suite.ApplicationTestGroup.TestCases[0].Name != "TiaMcp_ScratchSingleStep" ||
            suite.ApplicationTestGroup.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 1 ||
            suite.SystemTestGroup.SystemTestCases[0].Name != "SingleStep")
            throw new InvalidOperationException("Only the exact retained three-definition fixture with no application test sets is supported.");
    }

    private static JObject ReadScope(SystemTestCase selected)
    {
        string address = selected.OPCUAServerAddress;
        DirectoryInfo folder = selected.OPCUAServerInterfaceFolderPath;
        var type = selected.OPCUAServerInterfaceType;
        var engineering = (IEngineeringObject)selected;
        return new JObject { ["observedAtUtc"] = DateTime.UtcNow, ["name"] = selected.Name,
            ["serverAddress"] = address, ["folderPath"] = folder == null ? null : folder.FullName,
            ["folderIsNull"] = folder == null, ["serverInterface"] = type.ToString(),
            ["attributes"] = new JArray(engineering.GetAttributeInfos().Select(a => new JObject { ["name"] = a.Name, ["access"] = a.AccessMode.ToString() })),
            ["compositions"] = new JArray(engineering.GetCompositionInfos().Select(c => c.Name)),
            ["invocations"] = new JArray(engineering.GetInvocationInfos().Select(i => i.Name)) };
    }

    private static JArray ExportDefinitions(TestSuiteService suite, string stage)
    {
        var result = new JArray();
        Export(suite.StyleGuideGroup.RuleSets[0], stage, "style", ".xml", result);
        Export(suite.ApplicationTestGroup.TestCases[0], stage, "application", ".tat", result);
        Export(suite.SystemTestGroup.SystemTestCases[0], stage, "system", ".tst", result);
        return result;
    }
    private static void Export(object definition, string stage, string family, string extension, JArray result)
    {
        string name = stage + "-" + family + extension;
        var file = new FileInfo(Path.Combine(Output, name));
        bool saved = definition is RuleSet rules ? rules.SaveToFile(file) : definition is TestCase test ? test.SaveToFile(file) : ((SystemTestCase)definition).SaveToFile(file);
        if (!saved || !file.Exists) throw new InvalidOperationException("Native SaveToFile did not return the expected artifact.");
        result.Add(new JObject { ["family"] = family, ["name"] = Convert.ToString(((IEngineeringObject)definition).GetAttribute("Name")), ["artifact"] = Artifact(name) });
    }
    private static void CheckArtifact(JObject artifact)
    {
        if (artifact == null || Hash(artifact.Value<string>("path")) != artifact.Value<string>("sha256")) throw new InvalidOperationException("An admitted retained artifact hash changed.");
    }
    private static JObject Failure(Exception e) => new JObject { ["type"] = e.GetType().FullName, ["message"] = e.Message,
        ["inner"] = e.InnerException == null ? null : Failure(e.InnerException), ["automaticReplay"] = false };
    private static void Inside(string root, string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path leaves the admitted artifact root."); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string relative) { string path = Path.Combine(Output, relative); return new JObject { ["path"] = relative, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length }; }
    private static void Save(string name, JObject data) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Formatting.Indented)); using (var file = new FileStream(Path.Combine(Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } }
}
