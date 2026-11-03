using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.SystemTest;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>Candidate TS-Q03 fixture. Import/scope/result grammar require native qualification before promotion.</summary>
    public static class SystemQualificationFixture
    {
        public const string Profile = "v21.owned-plcsim.system.qualification-v1";
        public const string Pass = "TiaMcp_Q03_Pass", Fail = "TiaMcp_Q03_Fail";
        public static readonly string[] Names = { Fail, Pass };

        public static bool Endpoint(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme == "opc.tcp" && uri.HostNameType == UriHostNameType.IPv4 && uri.Port == 4840 &&
            uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && address == uri.AbsoluteUri;

        public static string Refusal(JObject input, string scratchRoot)
        {
            var binding = input?["application"] as JObject;
            var definitions = input?["run"]?["definitions"] as JArray;
            var dispatch = input?["run"]?["dispatchDefinitionIds"] as JArray;
            var excluded = input?["run"]?["excludedDefinitionIds"] as JArray;
            if (input?.Value<string>("executionProfile") != Profile || binding == null ||
                binding.Value<string>("executionMode") != "endpoint" || binding.Value<string>("route") != "systemTestCase" ||
                string.IsNullOrWhiteSpace(binding.Value<string>("simulatorTicket")) || !Endpoint(binding.Value<string>("serverAddress")) ||
                input["run"]?.Value<string>("executionMode") != "endpoint" || definitions == null || definitions.Count != 1 ||
                definitions[0].Value<string>("family") != "system" || definitions[0].Value<string>("profileId") != Profile ||
                !Names.Contains(definitions[0].Value<string>("name"), StringComparer.Ordinal) ||
                definitions[0].Value<string>("id") != definitions[0].Value<string>("name") ||
                dispatch == null || dispatch.Count != 1 || dispatch.Value<string>(0) != definitions[0].Value<string>("id") ||
                excluded == null || excluded.Count != 0)
                return "TestSuiteProfileUnqualified";
            string path = input["operation"]?["plan"]?["target"]?.Value<string>("projectFilePath");
            string prefix = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path == null || !Path.IsPathRooted(path) || !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? "ProjectIsolationRequired" : null;
        }

        public static bool ScopeMatches(JObject scope, string exported, string address)
        {
            string Property(string name)
            {
                var found = Regex.Matches(exported ?? "", "^[ \\t]*" + name + "[ \\t]*:[ \\t]*\"([^\"\\r\\n]*)\"[ \\t]*\\r?$", RegexOptions.Multiline);
                return found.Count == 1 ? found[0].Groups[1].Value : null;
            }
            return Endpoint(address) && scope?.Value<string>("serverAddress") == address &&
                scope.Value<string>("serverInterface") == "StandardSIMATIC" && string.IsNullOrEmpty(scope.Value<string>("interfaceFolderPath")) &&
                Property("SERVER_ADDRESS") == address && Property("SELECTED_SERVER_INTERFACE") == "Standard_SIMATIC" && Property("FOLDER_PATH") == "";
        }

        internal static string Text(string name, string address)
        {
            if (!Names.Contains(name, StringComparer.Ordinal) || !Endpoint(address)) throw new ArgumentException("Exact fixture and canonical simulator endpoint required.");
            return "TEST_CASE \"" + name + "\"\r\n\r\nPROPERTY\r\nAUTHOR : \"TIA MCP\"\r\nVERSION : \"0.1\"\r\n" +
                "COMMENT : \"TS-Q03 owned qualification fixture\"\r\nSERVER_ADDRESS : \"" + address + "\"\r\n" +
                "FOLDER_PATH : \"\"\r\nSELECTED_SERVER_INTERFACE : \"Standard_SIMATIC\"\r\nEND_PROPERTY\r\n\r\n" +
                "VAR\r\ngate : TiaMcp_EnableGate_DB.Active;\r\nEND_VAR\r\n" +
                "STEP: \"Read enable gate output\"\r\nwait(time:=t#10ms);\r\nASSERT.Equal(gate," +
                (name == Pass ? "true" : "false") + ");\r\nEND_STEP\r\n\r\nEND_TEST_CASE\r\n";
        }

        public static void Prepare(Project project, PlcSoftware plc, string address, string directory, Action verifyOwnedEndpoint)
        {
            if (verifyOwnedEndpoint == null) throw new ArgumentNullException(nameof(verifyOwnedEndpoint));
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (suite.StyleGuideGroup.RuleSets.Count != 0 || suite.ApplicationTestGroup.TestCases.Count != 0 ||
                suite.ApplicationTestGroup.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 0)
                throw new InvalidOperationException("Fresh empty Test Suite groups required.");
            StyleUiEvidence.Save(Path.Combine(directory, "system-fixture-intent.json"), new JObject {
                ["projectPath"] = project.Path.FullName, ["cases"] = new JArray(Names), ["serverAddress"] = address,
                ["automaticReplay"] = false, ["executorInvoked"] = false });
            foreach (string name in Names)
            {
                string file = Path.Combine(directory, name + "-input.tst");
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                { var bytes = Encoding.ASCII.GetBytes(Text(name, address)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                var imported = SystemTestDefinitions.Import(project, file);
                if (!imported["testCases"].Values<string>().SequenceEqual(new[] { name }) || ((JArray)imported["unexpected"]).Count != 0)
                    throw new InvalidOperationException("Imported fixture names differ.");
                verifyOwnedEndpoint();
                suite.SystemTestGroup.SystemTestCases.Find(name).SetScope(address, ServerInterfaces.StandardSIMATIC);
            }
            Validate(project, plc);
        }

        public static void Validate(Project project, PlcSoftware plc)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (plc.Name != "ScratchPLC" || plc.BlockGroup.Groups.Count != 0 ||
                !plc.BlockGroup.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(StyleQualificationFixture.BlockNames) ||
                suite.StyleGuideGroup.RuleSets.Count != 0 || suite.ApplicationTestGroup.TestCases.Count != 0 ||
                suite.ApplicationTestGroup.ApplicationTestSets.Count != 0 ||
                !suite.SystemTestGroup.SystemTestCases.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(Names) ||
                suite.SystemTestGroup.GetService<SystemTestCaseExecutor>() == null)
                throw new InvalidOperationException("Exact four-block/two-case system qualification fixture required.");
        }

        public static JObject Scope(Project project, string name) => SystemTestDefinitions.Scope(SystemTestDefinitions.Group(project).SystemTestCases.Find(name));
        public static void Export(Project project, string name, string path) => SystemTestDefinitions.Export(SystemTestDefinitions.Group(project).SystemTestCases.Find(name), path);
    }
}
