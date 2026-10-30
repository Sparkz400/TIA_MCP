using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>
    /// TS-Q02: two owned application test cases on the scratch's Main → TiaMcp_EnableGate(Enable := TRUE), externally
    /// managed on the owned PLCSIM instance. Pass asserts Active = TRUE, Fail deliberately asserts FALSE.
    /// </summary>
    public static class ApplicationQualificationFixture
    {
        public const string Profile = "v21.owned-plcsim.application.qualification-v1";
        public const string Pass = "TiaMcp_Q02_Pass", Fail = "TiaMcp_Q02_Fail";
        public static readonly string[] Names = { Fail, Pass };

        /// <summary>Pure qualification-mode admission: a refusal code, or null for the exact owned fixture on an owned instance.</summary>
        public static string Refusal(JObject input, string scratchRoot)
        {
            var application = input?["application"] as JObject;
            var definitions = input?["run"]?["definitions"] as JArray;
            if (input?.Value<string>("executionProfile") != Profile || application == null ||
                application.Value<string>("executionMode") != "externallyManaged" || application.Value<string>("route") != "testCase" ||
                string.IsNullOrWhiteSpace(application.Value<string>("simulatorTicket")) || definitions == null || definitions.Count != 1 ||
                definitions[0].Value<string>("family") != "application" || !Names.Contains(definitions[0].Value<string>("name"), StringComparer.Ordinal) ||
                definitions[0].Value<string>("id") != definitions[0].Value<string>("name"))
                return "TestSuiteProfileUnqualified";
            string path = input["operation"]?["plan"]?["target"]?.Value<string>("projectFilePath");
            string prefix = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path == null || !Path.IsPathRooted(path) || !Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? "ProjectIsolationRequired" : null;
        }

        /// <summary>
        /// Complete application scope: the target controller (GetScope) and the instance and mode the case's own native export
        /// persists. An unset, foreign or system-managed instance does not match.
        /// </summary>
        public static bool ScopeMatches(string getScope, string exportText, string plcName, string instanceName)
        {
            string Property(string name)
            {
                var found = Regex.Matches(exportText ?? "", "^[ \\t]*" + name + "[ \\t]*:[ \\t]*\"([^\"\\r\\n]*)\"[ \\t]*\\r?$", RegexOptions.Multiline);
                return found.Count == 1 ? found[0].Groups[1].Value : null;
            }
            string mode = Property("TEST_EXECUTION_MODE");
            return !string.IsNullOrEmpty(plcName) && !string.IsNullOrEmpty(instanceName) && getScope == plcName && Property("SCOPE") == plcName &&
                Property("PLCSIM_INSTANCE_NAME") == instanceName && mode != null && mode.StartsWith("ExternallyManaged", StringComparison.Ordinal);
        }

        internal static string Text(string name, string plcName) =>
            "TEST_CASE \"" + name + "\"\r\n\r\nPROPERTY\r\nAUTHOR : \"TIA MCP\"\r\nVERSION : \"0.1\"\r\n" +
            "COMMENT : \"TS-Q02 owned qualification fixture\"\r\nSCOPE : \"" + plcName + "\"\r\nPLCSIM_INSTANCE_NAME : \"\"\r\n" +
            "TEST_EXECUTION_MODE : \"SystemManagedPlcSimAdvInstance\"\r\nEND_PROPERTY\r\n\r\nSTEP: \"Enable gate output\"\r\nRUN(Cycles:=1);\r\n" +
            "ASSERT.Equal(\"TiaMcp_EnableGate_DB\".Active," + (name == Pass ? "true" : "false") + ");\r\nEND_STEP\r\n\r\n\r\nEND_TEST_CASE\r\n";

        /// <summary>Strict import of both cases, then the target the Run will write again: idempotent, so preservation holds.</summary>
        public static void Prepare(Project project, PlcSoftware plc, string instanceName, string directory)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            var group = suite.ApplicationTestGroup;
            if (suite.StyleGuideGroup.RuleSets.Count != 0 || group.TestCases.Count != 0 || group.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 0)
                throw new InvalidOperationException("Fresh empty Test Suite groups required.");
            StyleUiEvidence.Save(Path.Combine(directory, "application-fixture-intent.json"), new JObject {
                ["projectPath"] = project.Path.FullName, ["cases"] = new JArray(Names), ["instanceName"] = instanceName,
                ["executionMode"] = "ExternallyManagedPLCSIMInstance", ["automaticReplay"] = false, ["executorInvoked"] = false });
            foreach (string name in Names)
            {
                string file = Path.Combine(directory, name + "-input.tat");
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                { var bytes = Encoding.ASCII.GetBytes(Text(name, plc.Name)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                group.TestCases.LoadFromFile(new FileInfo(file), ImportOptions.None, TCLoadOptions.None);
                (group.TestCases.Find(name) ?? throw new InvalidOperationException("Imported fixture case is absent."))
                    .SetScope(plc, instanceName, ExecutionMode.ExternallyManagedPLCSIMInstance);
            }
            Validate(project, plc);
        }

        public static void Validate(Project project, PlcSoftware plc)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            var group = suite.ApplicationTestGroup;
            if (plc.Name != "ScratchPLC" || plc.BlockGroup.Groups.Count != 0 ||
                !plc.BlockGroup.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(StyleQualificationFixture.BlockNames) ||
                suite.StyleGuideGroup.RuleSets.Count != 0 || group.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 0 ||
                !group.TestCases.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(Names) || group.GetService<TestCaseExecutor>() == null)
                throw new InvalidOperationException("Exact four-block/two-case application qualification fixture required.");
        }

        public static string GetScope(Project project, string name) =>
            (project.GetService<TestSuiteService>().ApplicationTestGroup.TestCases.Find(name) ?? throw new InvalidOperationException("Fixture case is absent.")).GetScope();

        public static void Export(Project project, string name, string path)
        {
            var test = project.GetService<TestSuiteService>().ApplicationTestGroup.TestCases.Find(name) ?? throw new InvalidOperationException("Fixture case is absent.");
            if (File.Exists(path) || !test.SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Fresh definition export required.");
        }
    }
}
