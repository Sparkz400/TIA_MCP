using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.StyleGuide;

namespace TiaMcp.TestSuite.V21
{
    // Fixture setup and public readback only. This class contains no executor invocation.
    public static class StyleScopeEditorProbe
    {
        public const string RuleName = "TiaMcp_CasingAvailability";
        public static JObject Observe(Project project, PlcSoftware plc, string directory, bool prepare)
        {
            var names = plc.BlockGroup.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (plc.Name != "ScratchPLC" || plc.BlockGroup.Groups.Count != 0 ||
                !names.SequenceEqual(new[] { "Main", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcp_IsEnabled" }))
                throw new InvalidOperationException("Exact four-block root scratch fixture required.");
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (suite.ApplicationTestGroup.TestCases.Count != 0 || suite.ApplicationTestGroup.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 0)
                throw new InvalidOperationException("Only the isolated empty Test Suite fixture is admitted.");
            if (prepare)
            {
                if (suite.StyleGuideGroup.RuleSets.Count != 0) throw new InvalidOperationException("No existing style definitions may be changed.");
                Save(Path.Combine(directory, "setup-intent.json"), new JObject { ["action"] = "importPackagedRuleAndSetRootProgramBlockScope", ["projectPath"] = project.Path.FullName, ["executorInvoked"] = false });
                string path = Path.Combine(directory, "fixture.xml");
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("TestSuiteScopeFixture.xml"))
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { input.CopyTo(output); output.Flush(true); }
                suite.StyleGuideGroup.RuleSets.LoadFromFile(new FileInfo(path), ImportOptions.None, RSLoadOptions.None);
                var rule = suite.StyleGuideGroup.RuleSets.Find(RuleName) ?? throw new InvalidOperationException("Fixture import absent.");
                bool returned = rule.SetScope(UpdateOptions.Override, new IEngineeringObject[] { plc.BlockGroup });
                Save(Path.Combine(directory, "set-scope-return.json"), new JObject { ["returned"] = returned, ["completeEffectiveScope"] = false });
                if (!returned) throw new InvalidOperationException("Scope binding refused.");
                project.Save();
            }
            if (suite.StyleGuideGroup.RuleSets.Count != 1 || suite.StyleGuideGroup.RuleSets.Find(RuleName) == null)
                throw new InvalidOperationException("Exact sole fixture rule required.");
            var result = StylePrerequisiteProbe.Read(project, new[] { RuleName }, directory);
            result["inventory"] = new JArray(plc.BlockGroup.Blocks.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => new JObject {
                ["name"] = b.Name, ["type"] = b.GetType().FullName, ["number"] = b.Number, ["programmingLanguage"] = b.ProgrammingLanguage.ToString() }));
            result["requestedScope"] = "ScratchPLC/Program blocks";
            result["styleExecutionInvoked"] = false;
            Save(Path.Combine(directory, "observation.json"), result);
            return result;
        }
        public static void Show(Project project) { project.GetService<TestSuiteService>().StyleGuideGroup.RuleSets.Find(RuleName).ShowInEditor(); }
        private static void Save(string path, JObject value)
        { var bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.Indented)); using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } }
    }
}
