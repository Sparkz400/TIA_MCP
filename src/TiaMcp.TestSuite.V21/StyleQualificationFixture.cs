using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.StyleGuide;

namespace TiaMcp.TestSuite.V21
{
    internal static class StyleQualificationFixture
    {
        internal const string Clean = "TiaMcp_Q01_Clean", Violating = "TiaMcp_Q01_Violating";
        internal static readonly string[] Names = { Clean, Violating };
        internal static readonly string[] BlockNames = { "Main", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcp_IsEnabled" };
        internal static void Prepare(Project project, PlcSoftware plc, string directory)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (suite.StyleGuideGroup.RuleSets.Count != 0) throw new InvalidOperationException("Fresh empty style folder required.");
            StyleUiEvidence.Save(Path.Combine(directory, "fixture-intent.json"), new Newtonsoft.Json.Linq.JObject {
                ["projectPath"] = project.Path.FullName, ["rules"] = new Newtonsoft.Json.Linq.JArray(Names),
                ["scopeAssignment"] = "Override: root program-block group", ["automaticReplay"] = false,
                ["scopeProvenByAssignment"] = false, ["executorInvoked"] = false });
            foreach (string name in Names)
            {
                XDocument definition;
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("TestSuiteScopeFixture.xml")) definition = XDocument.Load(source);
                definition.Root.Element("RuleSet").SetAttributeValue("Name", name);
                if (name == Clean) definition.Descendants("RuleAttribute").Single(e => (string)e.Attribute("Name") == "ViolationCondition").SetAttributeValue("Value", "IsSet");
                string file = Path.Combine(directory, name + "-input.xml");
                using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write)) { definition.Save(stream); stream.Flush(true); }
                suite.StyleGuideGroup.RuleSets.LoadFromFile(new FileInfo(file), ImportOptions.None, RSLoadOptions.None);
                if (!suite.StyleGuideGroup.RuleSets.Find(name).SetScope(UpdateOptions.Override, new IEngineeringObject[] { plc.BlockGroup }))
                    throw new InvalidOperationException("Native scope assignment refused; this return is not scope proof.");
            }
            Validate(project, plc);
        }
        internal static void Validate(Project project, PlcSoftware plc)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (plc.Name != "ScratchPLC" || plc.BlockGroup.Groups.Count != 0 ||
                !plc.BlockGroup.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(BlockNames) ||
                !suite.StyleGuideGroup.RuleSets.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(Names) ||
                suite.ApplicationTestGroup.TestCases.Count != 0 || suite.ApplicationTestGroup.ApplicationTestSets.Count != 0 || suite.SystemTestGroup.SystemTestCases.Count != 0 ||
                suite.StyleGuideGroup.GetService<RuleSetExecutor>() == null)
                throw new InvalidOperationException("Exact four-block/two-rule offline qualification fixture required.");
            var expectedTypes = new[] { "OB", "FB", "InstanceDB", "FC" };
            var expectedLanguages = new[] { "SCL", "SCL", "DB", "SCL" };
            var blocks = plc.BlockGroup.Blocks.OrderBy(b => b.Name, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < blocks.Length; i++)
                if (blocks[i].GetType().Name != expectedTypes[i] || blocks[i].Number != 1 || blocks[i].ProgrammingLanguage.ToString() != expectedLanguages[i])
                    throw new InvalidOperationException("Exact block kind, number and language required for selector applicability.");
        }
        internal static void Show(Project project, string name) => project.GetService<TestSuiteService>().StyleGuideGroup.RuleSets.Find(name).ShowInEditor();
        internal static void Export(Project project, string name, string path)
        { if (File.Exists(path) || !project.GetService<TestSuiteService>().StyleGuideGroup.RuleSets.Find(name).SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Fresh definition export required."); }
    }
}
