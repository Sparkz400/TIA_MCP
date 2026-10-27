using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>
    /// Application-test definition inventory/export and strict import. No SetScope or execution.
    /// NoInlining keeps optional-product types out of worker methods when Test Suite is absent.
    /// </summary>
    public static class ApplicationTestDefinitions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JObject Inventory(Project project, int maxItems, string exportDirectory)
        {
            var suite = project.GetService<TestSuiteService>();
            var result = new JObject { ["servicePresent"] = suite != null, ["productAssembly"] = typeof(TestSuiteService).Assembly.Location,
                ["setExportRoute"] = "none: V21 Openness exposes no ApplicationTestSet.SaveToFile; sets are listed by name only." };
            if (suite == null) return result;
            var group = suite.ApplicationTestGroup;
            int caseCount = group.TestCases.Count, setCount = group.ApplicationTestSets.Count;
            var cases = new JArray(); var sets = new JArray();
            foreach (var test in group.TestCases.Take(maxItems))
            {
                var row = new JObject { ["name"] = test.Name };
                try { row["scope"] = test.GetScope(); } catch (Exception e) { row["scopeFailure"] = e.GetType().Name + ": " + e.Message; }
                if (exportDirectory != null)
                {
                    try { row["export"] = Export(test, Path.Combine(exportDirectory, "case-" + (cases.Count + 1) + ".tat")); }
                    catch (Exception e) { row["exportFailure"] = e.GetType().Name + ": " + e.Message; }
                }
                cases.Add(row);
            }
            foreach (var set in group.ApplicationTestSets.Take(maxItems)) sets.Add(new JObject { ["name"] = set.Name });
            result["group"] = new JObject { ["testCaseCount"] = caseCount, ["testSetCount"] = setCount };
            result["testCases"] = cases; result["testSets"] = sets;
            result["truncated"] = cases.Count < caseCount || sets.Count < setCount;
            return result;
        }

        /// <summary>Names among the requested cases/sets that already exist in the project.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string[] Existing(Project project, string[] caseNames, string[] setNames)
        {
            var group = Group(project);
            return caseNames.Where(n => group.TestCases.Find(n) != null).Concat(setNames.Where(n => group.ApplicationTestSets.Find(n) != null)).ToArray();
        }

        /// <summary>Strict native import: collisions and invalid objects refuse. Returns what the call reported.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static JObject Import(Project project, string path)
        {
            var group = Group(project);
            var file = new FileInfo(path);
            var returned = string.Equals(file.Extension, ".tat", StringComparison.OrdinalIgnoreCase)
                ? group.TestCases.LoadFromFile(file, ImportOptions.None, TCLoadOptions.None).Cast<object>().ToArray()
                : group.LoadFromFile(file, ImportOptions.None, TSLoadOptions.None).Cast<object>().ToArray();
            return new JObject {
                ["testCases"] = new JArray(returned.OfType<TestCase>().Select(t => t.Name)),
                ["testSets"] = new JArray(returned.OfType<ApplicationTestSet>().Select(s => s.Name)),
                ["unexpected"] = new JArray(returned.Where(o => !(o is TestCase) && !(o is ApplicationTestSet)).Select(o => o?.GetType().FullName))
            };
        }

        internal static ApplicationTestSystemGroup Group(Project project) =>
            (project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("TargetPrerequisiteMissing: Test Suite service is absent; product/license are not inferred.")).ApplicationTestGroup;

        internal static JObject Export(TestCase test, string path)
        {
            if (File.Exists(path) || !test.SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Native test case export was refused.");
            var info = new FileInfo(path);
            if (info.Length > 16 * 1024 * 1024) throw new InvalidOperationException("DefinitionLimit: test case export exceeds 16 MiB.");
            using (var sha = SHA256.Create()) using (var input = info.OpenRead())
                return new JObject { ["path"] = info.FullName, ["bytes"] = info.Length,
                    ["sha256"] = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() };
        }
    }
}
