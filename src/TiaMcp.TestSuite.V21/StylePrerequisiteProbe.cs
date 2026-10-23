using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.StyleGuide;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>Read-only public metadata/export observations. No execution, scope mutation or inferred licensing.</summary>
    public static class StylePrerequisiteProbe
    {
        public static JObject CaptureDefinitions(Project project, string directory)
        {
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            if (suite.ApplicationTestGroup.ApplicationTestSets.Count != 0)
                throw new InvalidOperationException("Test Suite set preservation has no qualified export route.");
            var result = new JObject(); int count = 0;
            void Export(string key, string extension, Func<FileInfo, bool> save)
            {
                if (++count > 1000 || result[key] != null) throw new InvalidOperationException("Definition inventory exceeds bounds or has duplicate identities.");
                string path = Path.Combine(directory, "definition-" + count + extension);
                if (File.Exists(path) || !save(new FileInfo(path)) || new FileInfo(path).Length > 16 * 1024 * 1024)
                    throw new InvalidOperationException("Native definition capture incomplete.");
                using (var sha = SHA256.Create()) using (var input = File.OpenRead(path))
                    result[key] = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            }
            foreach (var rule in suite.StyleGuideGroup.RuleSets.OrderBy(r => r.Name, StringComparer.Ordinal)) Export("style/" + rule.Name, ".xml", rule.SaveToFile);
            foreach (var test in suite.ApplicationTestGroup.TestCases.OrderBy(r => r.Name, StringComparer.Ordinal)) Export("application/" + test.Name, ".tat", test.SaveToFile);
            foreach (var test in suite.SystemTestGroup.SystemTestCases.OrderBy(r => r.Name, StringComparer.Ordinal)) Export("system/" + test.Name, ".tst", test.SaveToFile);
            return result;
        }
        public static JObject Read(Project project, string[] ruleNames, string directory)
        {
            if (ruleNames == null || ruleNames.Length < 1 || ruleNames.Length > 32 || ruleNames.Distinct(StringComparer.Ordinal).Count() != ruleNames.Length)
                throw new ArgumentException("One to 32 exact unique existing rule names required.");
            var suite = project.GetService<TestSuiteService>();
            var result = new JObject {
                ["projectPath"] = project.Path.FullName, ["projectModified"] = project.IsModified,
                ["productAssembly"] = typeof(TestSuiteService).Assembly.Location,
                ["servicePresent"] = suite != null, ["executionLicense"] = "unknown",
                ["completeEffectiveScope"] = false, ["nativeExecutionAuthorized"] = false,
                ["scopeReason"] = "No qualified complete effective-object readback or exhaustive proof profile. SetScope and diagnostic findings cannot establish coverage.",
                ["rules"] = new JArray()
            };
            if (suite == null) return result;
            result["styleExecutorPresent"] = suite.StyleGuideGroup.GetService<RuleSetExecutor>() != null;
            foreach (string name in ruleNames)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Bounded exact rule name required.");
                var rule = suite.StyleGuideGroup.RuleSets.Find(name);
                var row = new JObject { ["name"] = name, ["present"] = rule != null, ["effectiveScopeComplete"] = false };
                ((JArray)result["rules"]).Add(row);
                if (rule == null) continue;
                var native = (IEngineeringObject)rule;
                row["attributes"] = new JArray(native.GetAttributeInfos().Select(a => new JObject { ["name"] = a.Name, ["access"] = a.AccessMode.ToString() }));
                row["compositions"] = new JArray(native.GetCompositionInfos().Select(c => c.Name));
                row["invocations"] = new JArray(native.GetInvocationInfos().Select(i => new JObject { ["name"] = i.Name, ["parameters"] = i.ParameterInfos.Count }));
                var routes = new JArray(); row["scopeRoutes"] = routes;
                foreach (var a in native.GetAttributeInfos().Where(a => (a.Name == "Scope" || a.Name == "ExecutionScope") && (a.AccessMode & EngineeringAttributeAccessMode.Read) != 0))
                    Capture(routes, "attribute:" + a.Name, () => native.GetAttribute(a.Name));
                foreach (var c in native.GetCompositionInfos().Where(c => c.Name == "Scope" || c.Name == "ExecutionScope"))
                    Capture(routes, "composition:" + c.Name, () => native.GetComposition(c.Name));
                foreach (var i in native.GetInvocationInfos().Where(i => i.Name == "GetScope" && i.ParameterInfos.Count == 0))
                    Capture(routes, "invocation:GetScope", () => native.Invoke("GetScope", null));
                string path = Path.Combine(directory, "rule-" + ((JArray)result["rules"]).Count + ".xml");
                if (File.Exists(path) || !rule.SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Fresh native rule export required.");
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("Native rule export exceeds bound.");
                row["definitionPath"] = path;
            }
            // Even a returned container is a selector candidate, not proof of all evaluated/ignored objects.
            // It needs its own reviewed semantics and clean/out-of-scope oracles before promotion.
            return result;
        }

        private static void Capture(JArray routes, string route, Func<object> read)
        {
            var row = new JObject { ["route"] = route, ["completeEffectiveScope"] = false }; routes.Add(row);
            try
            {
                var value = read(); row["valueType"] = value?.GetType().FullName;
                var objects = new JArray(); row["objects"] = objects;
                if (value is IEngineeringObject single) objects.Add(Identity(single));
                else if (value is IEnumerable items && !(value is string))
                    foreach (var item in items)
                    {
                        if (objects.Count >= 1000) throw new InvalidOperationException("Scope enumeration exceeds bound.");
                        objects.Add(item is IEngineeringObject obj ? Identity(obj) : new JObject { ["unsupportedValueType"] = item?.GetType().FullName });
                    }
                else row["unsupportedValue"] = true;
            }
            catch (Exception e) { row["failureType"] = e.GetType().FullName; row["failure"] = e.Message; }
        }
        private static JArray Identity(IEngineeringObject obj)
        {
            var identity = new JArray();
            for (var item = obj; item != null; item = item.Parent)
            {
                if (identity.Count >= 64) throw new InvalidOperationException("Native identity exceeds bound.");
                var row = new JObject { ["type"] = item.GetType().FullName };
                foreach (var info in item.GetAttributeInfos().Where(a => a.Name == "Name" && (a.AccessMode & EngineeringAttributeAccessMode.Read) != 0))
                    row["name"] = Convert.ToString(item.GetAttribute(info.Name));
                identity.AddFirst(row);
            }
            return identity;
        }
    }
}
