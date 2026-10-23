using System;
using System.Collections;
using System.Collections.Generic;
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

// A manually admitted qualification collector, not a Host dispatch implementation.
// The coordinator alone runs this executable under the sequential native admission.
internal static class Program
{
    private const string Api = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
    private static string Output;

    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, evt) => {
            string name = new AssemblyName(evt.Name).Name;
            string path = Path.Combine(Api, name + ".dll");
            return name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args).ToString(Formatting.Indented)); return 0; }
        catch (Exception e) { var failed = Failure(e); if (Output != null) Save("failure.json", failed); Console.WriteLine(failed); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        if (args.Length != 1) throw new InvalidOperationException("Expected exact admission JSON path.");
        var admission = JObject.Parse(File.ReadAllText(args[0]));
        string mode = admission.Value<string>("mode");
        if (mode != "inspect" && mode != "definition-import" && mode != "definition-create" && mode != "style-run")
            throw new InvalidOperationException("Unknown mode.");
        if (admission.Value<string>("binarySha256") != Hash(Assembly.GetExecutingAssembly().Location) ||
            admission.Value<string>("testSuiteApiSha256") != Hash(Path.Combine(Api, "Siemens.Engineering.TestSuite.dll")) ||
            admission.Value<string>("baseApiSha256") != Hash(Path.Combine(Api, "Siemens.Engineering.Base.dll")))
            throw new InvalidOperationException("Binary/API hashes differ from admission.");
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot"));
        string output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        Inside(root, output);
        if (Directory.Exists(output)) throw new InvalidOperationException("Fresh output required; no replay.");
        Directory.CreateDirectory(output); Output = output;
        foreach (string file in new[] { Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location + ".config", typeof(JObject).Assembly.Location })
            if (File.Exists(file)) File.Copy(file, Path.Combine(Output, Path.GetFileName(file)));
        foreach (JObject source in (JArray)admission["sourceFiles"])
        {
            string sourcePath = source.Value<string>("path");
            if (Hash(sourcePath) != source.Value<string>("sha256")) throw new InvalidOperationException("Source hash changed.");
            File.Copy(sourcePath, Path.Combine(Output, "source-" + Path.GetFileName(sourcePath)));
        }
        Save("admission.json", admission);
        var process = TiaPortal.GetProcesses().Single(p => p.Id == admission.Value<int>("processId"));
        using (var portal = process.Attach())
        using (portal.ExclusiveAccess("TIA MCP: admitted Test Suite definition qualification"))
        {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, admission.Value<string>("projectPath"), StringComparison.OrdinalIgnoreCase));
            var result = new JObject { ["mode"] = mode, ["processId"] = process.Id, ["projectPath"] = project.Path.FullName,
                ["startedAtUtc"] = DateTime.UtcNow, ["beforeModified"] = project.IsModified,
                ["servicePresence"] = "unknown", ["licenseAvailability"] = "unknown", ["executionAvailability"] = "notAttempted",
                ["automaticReplay"] = false, ["applicationExecutionInvoked"] = false, ["systemExecutionInvoked"] = false };
            var suite = project.GetService<TestSuiteService>();
            result["servicePresence"] = suite == null ? "absent" : "observed";
            if (suite != null)
            {
                result["metadata"] = Inspect(suite);
                if (mode != "inspect")
                {
                    if (admission.Value<bool?>("ownedScratch") != true || admission.Value<string>("creationTool") != "tia_create_scratch" ||
                        !project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || project.IsModified)
                        throw new InvalidOperationException("Mutation requires the exact saved owned scratch project.");
                    Inside(root, project.Path.FullName);
                    Save("checkpoint-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false });
                    project.Archive(new DirectoryInfo(Output), "before-testsuite.zap21", ProjectArchivationMode.Compressed);
                    result["checkpoint"] = Artifact(Path.Combine(Output, "before-testsuite.zap21")); Save("checkpoint.json", result);
                    Save("dispatch-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["mode"] = mode, ["automaticReplay"] = false });
                    try
                    {
                        if (mode == "definition-import") result["definitions"] = Import(suite, admission);
                        else if (mode == "definition-create") result["definitions"] = Create(suite, admission);
                        else result["execution"] = ExecuteStyle(suite, admission);
                        // A successful import/export proves that operation only; do not infer a general license.
                        result["licenseAvailability"] = "operationCompletedWithoutLicenseException";
                        if (mode == "style-run") result["executionAvailability"] = "styleCallReturned";
                        project.Save(); result["saved"] = true;
                    }
                    catch (Exception e)
                    {
                        result["operationFailure"] = Failure(e);
                        if (IsLicenseFailure(e)) result["licenseAvailability"] = "licenseNotFound";
                        if (mode == "style-run") result["executionAvailability"] = "failed";
                        // A failed call may have changed the project. Do not retry, save or delete implicitly.
                    }
                }
            }
            result["afterModified"] = project.IsModified;
            result["finishedAtUtc"] = DateTime.UtcNow; result["completed"] = true;
            Save("result.json", result); return result;
        }
    }

    private static JObject Inspect(TestSuiteService suite)
    {
        var groups = new JArray();
        foreach (var group in new[] { (IEngineeringObject)suite.StyleGuideGroup, suite.ApplicationTestGroup, suite.SystemTestGroup })
        {
            var compositions = new JArray();
            foreach (var info in group.GetCompositionInfos())
            {
                var composition = group.GetComposition(info.Name) as IEngineeringComposition;
                if (composition == null) { compositions.Add(new JObject { ["name"] = info.Name, ["state"] = "singleton" }); continue; }
                var creations = new JArray(composition.GetCreationInfos().Select(c => new JObject {
                    ["type"] = c.Type.FullName, ["parameters"] = new JArray(c.ParameterInfos.Select(p => new JObject {
                        ["name"] = p.Name, ["mandatory"] = p.IsMandatory, ["types"] = new JArray(p.SupportedTypes.Select(t => t.FullName)) })) }));
                var objects = new JArray(); int count = 0;
                foreach (IEngineeringObject item in (IEnumerable)composition)
                {
                    if (++count > 100) throw new InvalidOperationException("Definition bound exceeded.");
                    objects.Add(new JObject { ["type"] = item.GetType().FullName, ["name"] = Convert.ToString(item.GetAttribute("Name")) });
                }
                compositions.Add(new JObject { ["name"] = info.Name, ["creationInfos"] = creations, ["definitions"] = objects });
            }
            groups.Add(new JObject { ["type"] = group.GetType().FullName, ["compositions"] = compositions,
                ["services"] = new JArray(((IEngineeringServiceProvider)group).GetServiceInfos().Select(s => s.Type.FullName)) });
        }
        return new JObject { ["groups"] = groups };
    }

    private static JArray Import(TestSuiteService suite, JObject admission)
    {
        string family = admission.Value<string>("family");
        string source = Path.GetFullPath(admission.Value<string>("definitionPath"));
        if (Hash(source) != admission.Value<string>("definitionSha256")) throw new InvalidOperationException("Definition hash changed.");
        File.Copy(source, Path.Combine(Output, "import-source" + Path.GetExtension(source)));
        IEnumerable definitions;
        if (family == "style") definitions = suite.StyleGuideGroup.RuleSets.LoadFromFile(new FileInfo(source), ImportOptions.None, RSLoadOptions.None);
        else if (family == "application") definitions = suite.ApplicationTestGroup.TestCases.LoadFromFile(new FileInfo(source), ImportOptions.None, Siemens.Engineering.TestSuite.ApplicationTest.TCLoadOptions.None);
        else if (family == "system") definitions = suite.SystemTestGroup.SystemTestCases.LoadFromFile(new FileInfo(source), ImportOptions.None, Siemens.Engineering.TestSuite.SystemTest.TCLoadOptions.None);
        else throw new InvalidOperationException("Unsupported family.");
        var exports = new JArray(); int count = 0;
        foreach (object definition in definitions)
        {
            if (++count > 100) throw new InvalidOperationException("Imported definition count exceeded.");
            exports.Add(Export(definition, count));
        }
        if (count == 0) throw new InvalidOperationException("Native import returned no definitions.");
        return exports;
    }

    private static JArray Create(TestSuiteService suite, JObject admission)
    {
        string family = admission.Value<string>("family");
        IEngineeringComposition composition = family == "style" ? (IEngineeringComposition)suite.StyleGuideGroup.RuleSets :
            family == "application" ? (IEngineeringComposition)suite.ApplicationTestGroup.TestCases :
            family == "system" ? (IEngineeringComposition)suite.SystemTestGroup.SystemTestCases : throw new InvalidOperationException("Unsupported family.");
        var info = composition.GetCreationInfos().Single(c => c.Type.FullName == admission.Value<string>("creationType"));
        var supplied = (JObject)admission["creationParameters"];
        var parameters = new Dictionary<string, object>();
        foreach (var p in info.ParameterInfos)
        {
            if (supplied[p.Name] == null) { if (p.IsMandatory) throw new InvalidOperationException("Missing creation parameter."); continue; }
            if (!p.SupportedTypes.Contains(typeof(string)) || supplied[p.Name].Type != JTokenType.String)
                throw new InvalidOperationException("Collector only supports native-advertised string creation parameters.");
            parameters.Add(p.Name, supplied.Value<string>(p.Name));
        }
        if (parameters.Count != supplied.Count) throw new InvalidOperationException("Unknown creation parameter.");
        return new JArray(Export(composition.Create(info.Type, parameters), 1));
    }

    private static JObject Export(object definition, int index)
    {
        string extension = definition is RuleSet ? ".xml" : definition is TestCase ? ".tat" : definition is SystemTestCase ? ".tst" : throw new InvalidOperationException("Unqualified definition type.");
        string path = Path.Combine(Output, "native-definition-" + index + extension);
        bool saved;
        if (definition is RuleSet rules) saved = rules.SaveToFile(new FileInfo(path));
        else if (definition is TestCase test) saved = test.SaveToFile(new FileInfo(path));
        else saved = ((SystemTestCase)definition).SaveToFile(new FileInfo(path));
        if (!saved || !File.Exists(path)) throw new InvalidOperationException("Native definition export did not succeed.");
        return new JObject { ["name"] = Convert.ToString(((IEngineeringObject)definition).GetAttribute("Name")), ["type"] = definition.GetType().FullName, ["artifact"] = Artifact(path), ["origin"] = "nativeSaveToFile" };
    }

    private static JObject ExecuteStyle(TestSuiteService suite, JObject admission)
    {
        var rules = suite.StyleGuideGroup.RuleSets.Find(admission.Value<string>("ruleSetName")) ?? throw new InvalidOperationException("Exact selected rule set absent.");
        var exported = Export(rules, 1);
        if (exported["artifact"].Value<string>("sha256") != admission.Value<string>("definitionSha256")) throw new InvalidOperationException("Rule-set definition changed.");
        // Scope must already be explicitly reviewed and observed. This collector never changes scope.
        if (admission.Value<bool?>("scopeReviewed") != true) throw new InvalidOperationException("Reviewed style scope required.");
        var executor = suite.StyleGuideGroup.GetService<RuleSetExecutor>() ?? throw new InvalidOperationException("Style executor absent.");
        var native = executor.Run(rules); int count = 0;
        var raw = new JObject { ["state"] = native.State.ToString(), ["errorCount"] = native.ErrorCount,
            ["warningCount"] = native.WarningCount, ["messages"] = Messages(native.Messages, 0, ref count) };
        Save("native-results.json", raw);
        return new JObject { ["definition"] = exported, ["rawResult"] = Artifact(Path.Combine(Output, "native-results.json")), ["nativeState"] = native.State.ToString(), ["messages"] = count };
    }

    private static JArray Messages(IEnumerable<TestResultsMessage> messages, int depth, ref int count)
    {
        if (depth > 128) throw new InvalidOperationException("Result depth exceeded.");
        var result = new JArray();
        foreach (var item in messages)
        {
            if (++count > 100000) throw new InvalidOperationException("Result message bound exceeded.");
            result.Add(new JObject { ["state"] = item.State.ToString(), ["path"] = item.Path, ["description"] = item.Description,
                ["dateTime"] = item.DateTime.ToString("o"), ["errorCount"] = item.ErrorCount, ["warningCount"] = item.WarningCount,
                ["messages"] = Messages(item.Messages, depth + 1, ref count) });
        }
        return result;
    }
    private static bool IsLicenseFailure(Exception e) => e is LicenseNotFoundException || e.InnerException != null && IsLicenseFailure(e.InnerException);
    private static JObject Failure(Exception e) => new JObject { ["type"] = e.GetType().FullName, ["message"] = e.Message, ["inner"] = e.InnerException == null ? null : Failure(e.InnerException), ["automaticReplay"] = false };
    private static void Inside(string root, string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path leaves admitted artifact root."); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string path) => new JObject { ["path"] = path, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length };
    private static void Save(string name, JObject data) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Formatting.Indented)); using (var file = new FileStream(Path.Combine(Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } }
}
