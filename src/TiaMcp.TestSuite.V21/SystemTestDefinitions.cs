using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.SystemTest;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>
    /// System-test definition inventory/export and strict import. No SetScope or execution.
    /// NoInlining keeps optional-product types out of worker methods when Test Suite is absent.
    /// </summary>
    public static class SystemTestDefinitions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JObject Inventory(Project project, int maxItems, string exportDirectory)
        {
            var suite = project.GetService<TestSuiteService>();
            var result = new JObject { ["servicePresent"] = suite != null, ["productAssembly"] = typeof(TestSuiteService).Assembly.Location };
            if (suite == null) return result;
            var group = suite.SystemTestGroup;
            int caseCount = group.SystemTestCases.Count;
            var cases = new JArray();
            foreach (var test in group.SystemTestCases.Take(maxItems))
            {
                var row = new JObject { ["name"] = test.Name };
                try { row["scope"] = Scope(test); } catch (Exception e) { row["scopeFailure"] = e.GetType().Name + ": " + e.Message; }
                if (exportDirectory != null)
                {
                    try { row["export"] = Export(test, Path.Combine(exportDirectory, "case-" + (cases.Count + 1) + ".tst")); }
                    catch (Exception e) { row["exportFailure"] = e.GetType().Name + ": " + e.Message; }
                }
                cases.Add(row);
            }
            result["group"] = new JObject { ["testCaseCount"] = caseCount };
            result["testCases"] = cases;
            result["truncated"] = cases.Count < caseCount;
            return result;
        }

        /// <summary>Names among the requested cases that already exist in the project.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string[] Existing(Project project, string[] caseNames)
        {
            var group = Group(project);
            return caseNames.Where(n => group.SystemTestCases.Find(n) != null).ToArray();
        }

        /// <summary>Strict native import: collisions and invalid objects refuse. Returns what the call reported.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JObject Import(Project project, string path)
        {
            var returned = Group(project).SystemTestCases.LoadFromFile(new FileInfo(path), ImportOptions.None, TCLoadOptions.None).Cast<object>().ToArray();
            return new JObject {
                ["testCases"] = new JArray(returned.OfType<SystemTestCase>().Select(t => t.Name)),
                ["unexpected"] = new JArray(returned.Where(o => !(o is SystemTestCase)).Select(o => o?.GetType().FullName))
            };
        }

        internal static SystemTestSystemGroup Group(Project project) =>
            (project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("TargetPrerequisiteMissing: Test Suite service is absent; product/license are not inferred.")).SystemTestGroup;

        internal static JObject Scope(SystemTestCase test) => new JObject { ["serverAddress"] = Convert.ToString(test.OPCUAServerAddress),
            ["serverInterface"] = Convert.ToString(test.OPCUAServerInterfaceType), ["interfaceFolderPath"] = Convert.ToString(test.OPCUAServerInterfaceFolderPath) };

        internal static JObject Export(SystemTestCase test, string path)
        {
            if (File.Exists(path) || !test.SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Native system test case export was refused.");
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024) throw new InvalidOperationException("DefinitionLimit: system test case export exceeds 16 MiB.");
            using (var sha = SHA256.Create()) using (var input = info.OpenRead())
                return new JObject { ["path"] = info.FullName, ["bytes"] = info.Length,
                    ["sha256"] = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() };
        }
    }
}
