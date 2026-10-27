using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.ApplicationTest;
using Siemens.Engineering.TestSuite.StyleGuide;

namespace TiaMcp.TestSuite.V21
{
    /// <summary>
    /// Optional in-process boundary for the already owning V21 worker. It never starts/attaches TIA.
    /// No production profile is accepted. Do not add a profile on the strength of a managed test or
    /// the retained availability-only receipt. Independent scope and lifecycle qualification are missing.
    /// </summary>
    public sealed class StylePublicApi
    {
        private readonly Project project;
        private readonly string profile;
        private readonly Action qualification;
        private readonly bool independentlyObservedScope;
        private bool invoked;
        public StylePublicApi(Project ownedProject, string executionProfile)
        { project = ownedProject ?? throw new ArgumentNullException(nameof(ownedProject)); profile = executionProfile; }
        internal StylePublicApi(Project ownedProject, string executionProfile, Action qualification, bool independentlyObservedScope)
            : this(ownedProject, executionProfile)
        { this.qualification = qualification ?? throw new ArgumentNullException(nameof(qualification)); this.independentlyObservedScope = independentlyObservedScope; }

        private void RequireQualification()
        {
            if (qualification != null) { qualification(); return; }
            // Keep this native-side guard as well as the Host guard. There is no request override.
            throw new InvalidOperationException("TestSuiteProfileUnqualified: " + profile + "; TS-Q01 effective-scope and lifecycle qualification required.");
        }

        public byte[] ExportExactDefinition(string name, string outputPath)
        {
            RequireQualification();
            return Export(Resolve(name), outputPath);
        }

        /// <summary>
        /// One single-rule call only. Owning worker must hold ExclusiveAccess across exact reacquisition,
        /// archive/manifest checkpoint and flushed EngineeringOperationStore dispatch intent. No SetScope
        /// or import is performed. A failed response/enumeration is not permission to use another instance.
        /// </summary>
        public byte[] RunOnce(string name, string definitionSha256, string definitionId, JObject definitionReference,
            string verificationExportPath, string executionReturnPath, string rawResultPath)
        {
            RequireQualification();
            if (invoked) throw new InvalidOperationException("RecoveryRequired: native dispatch cannot be replayed.");
            var rule = Resolve(name);
            var bytes = Export(rule, verificationExportPath);
            if (Hash(bytes) != definitionSha256) throw new InvalidOperationException("StaleContent: exact definition changed.");
            var suite = project.GetService<TestSuiteService>();
            var executor = suite.StyleGuideGroup.GetService<RuleSetExecutor>();
            if (executor == null) throw new InvalidOperationException("TargetPrerequisiteMissing: style executor is absent.");
            invoked = true;
            TestResults native = executor.Run(rule); // Documented public V21 single-rule overload; exactly one invocation.
            FlushNew(executionReturnPath, Encoding.UTF8.GetBytes(new JObject {
                ["callReturned"] = true, ["atUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["definitionId"] = definitionId, ["definitionSha256"] = definitionSha256,
                ["rawResultPath"] = rawResultPath,
                ["automaticReplay"] = false }.ToString(Formatting.None)));
            int count = 0; long textBytes = 0;
            var result = new JObject {
                ["definitionId"] = definitionId, ["definitionReference"] = definitionReference.DeepClone(),
                ["state"] = native.State.ToString(), ["errorCount"] = native.ErrorCount, ["warningCount"] = native.WarningCount,
                ["dispatched"] = true,
                // Public Run/SetScope returns do not prove independently observed effective scope.
                ["scopeVerified"] = independentlyObservedScope, ["failureKind"] = null
            };
            return StyleRawCapture.Retain(rawResultPath, result, messages => Messages(native.Messages, messages, 1, ref count, ref textBytes));
        }

        /// <summary>
        /// One application Run (case, case list, set, set list or whole group) on an owned PLCSIM instance the
        /// worker verified by ticket. Every reviewed case must re-export to its original bytes; sets have no
        /// export route and resolve by exact name only. SetScope persists the target on each reviewed case, so
        /// the owning worker checkpoints before this call. No import, retry or cancellation route.
        /// </summary>
        public byte[] RunApplicationOnce(PlcSoftware software, string instanceName, string executionMode, string route,
            JArray definitions, string[] dispatchIds, string verificationDirectory, string executionReturnPath, string rawResultPath)
        {
            RequireQualification();
            if (invoked) throw new InvalidOperationException("RecoveryRequired: native dispatch cannot be replayed.");
            if (software == null || string.IsNullOrWhiteSpace(instanceName) || dispatchIds == null || dispatchIds.Length < 1)
                throw new ArgumentException("Exact PLC software, owned instance name and dispatch units required.");
            var mode = executionMode == "systemManaged" ? ExecutionMode.SystemManagedPLCSIMInstance
                : executionMode == "externallyManaged" ? ExecutionMode.ExternallyManagedPLCSIMInstance
                : throw new ArgumentException("Exact application execution mode required.");
            var group = ApplicationTestDefinitions.Group(project);
            var casesById = new Dictionary<string, TestCase>(StringComparer.Ordinal);
            var setsById = new Dictionary<string, ApplicationTestSet>(StringComparer.Ordinal);
            Directory.CreateDirectory(verificationDirectory);
            foreach (var definition in definitions)
            {
                string id = definition.Value<string>("id"), name = definition.Value<string>("name");
                if (definition.Value<string>("family") == "applicationSet")
                {
                    setsById.Add(id, group.ApplicationTestSets.Find(name) ?? throw new InvalidOperationException("StaleContent: exact test set is absent."));
                    continue;
                }
                var test = group.TestCases.Find(name) ?? throw new InvalidOperationException("StaleContent: exact test case is absent.");
                var export = ApplicationTestDefinitions.Export(test, Path.Combine(verificationDirectory, "case-" + (casesById.Count + 1) + ".tat"));
                if (export.Value<string>("sha256") != definition.Value<string>("originalSha256")) throw new InvalidOperationException("StaleContent: exact test case definition changed.");
                casesById.Add(id, test);
            }
            var cases = dispatchIds.Where(casesById.ContainsKey).Select(id => casesById[id]).ToList();
            var sets = dispatchIds.Where(setsById.ContainsKey).Select(id => setsById[id]).ToList();
            if (cases.Count + sets.Count != dispatchIds.Length || cases.Count != 0 && sets.Count != 0)
                throw new InvalidOperationException("StaleContent: dispatch units must resolve to reviewed cases or sets, never both.");
            if (route == "group" && group.TestCases.Count != cases.Count) throw new InvalidOperationException("StaleContent: the project group holds unreviewed test cases.");
            var executor = group.GetService<TestCaseExecutor>() ?? throw new InvalidOperationException("TargetPrerequisiteMissing: application executor is absent.");
            invoked = true; // Before SetScope: a partially written scope is never replayed either.
            var scopes = new JArray();
            foreach (var test in casesById.Values)
            {
                test.SetScope(software, instanceName, mode);
                var row = new JObject { ["name"] = test.Name }; scopes.Add(row);
                try { row["scope"] = test.GetScope(); } catch (Exception e) { row["scopeFailure"] = e.GetType().Name + ": " + e.Message; }
            }
            TestResults native;
            switch (route)
            {
                case "testCase": native = executor.Run(cases.Single()); break;
                case "testCaseList": native = executor.Run(cases); break;
                case "testSet": native = executor.Run(sets.Single()); break;
                case "testSetList": native = executor.Run(sets); break;
                case "group": native = executor.Run(group); break;
                default: throw new InvalidOperationException("Unknown application Run route.");
            }
            FlushNew(executionReturnPath, Encoding.UTF8.GetBytes(new JObject {
                ["callReturned"] = true, ["atUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["route"] = route, ["executionMode"] = executionMode, ["instanceName"] = instanceName,
                ["dispatchDefinitionIds"] = new JArray(dispatchIds), ["scopeReadback"] = scopes, ["setContentVerified"] = false,
                ["rawResultPath"] = rawResultPath, ["automaticReplay"] = false }.ToString(Formatting.None)));
            int count = 0; long textBytes = 0;
            var result = new JObject {
                ["definitionId"] = dispatchIds[0], ["definitionReference"] = definitions.First(d => d.Value<string>("id") == dispatchIds[0])["reference"].DeepClone(),
                ["state"] = native.State.ToString(), ["errorCount"] = native.ErrorCount, ["warningCount"] = native.WarningCount,
                ["dispatched"] = true,
                // GetScope names only the target controller; executed objects are not independently observed.
                ["scopeVerified"] = false, ["failureKind"] = null
            };
            return StyleRawCapture.Retain(rawResultPath, result, messages => Messages(native.Messages, messages, 1, ref count, ref textBytes));
        }

        private RuleSet Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Exact bounded rule name required.");
            var suite = project.GetService<TestSuiteService>();
            if (suite == null) throw new InvalidOperationException("TargetPrerequisiteMissing: Test Suite service is absent; product/license are not inferred.");
            return suite.StyleGuideGroup.RuleSets.Find(name) ?? throw new InvalidOperationException("StaleContent: exact rule set is absent.");
        }
        private static byte[] Export(RuleSet rule, string path)
        {
            if (File.Exists(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Fresh absolute export path required.");
            if (!rule.SaveToFile(new FileInfo(path))) throw new InvalidOperationException("Native definition export was refused.");
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("DefinitionLimit");
            return File.ReadAllBytes(path);
        }
        private static void Messages(IEnumerable<TestResultsMessage> messages, JArray result, int depth, ref int count, ref long textBytes)
        {
            foreach (var message in messages)
            {
                if (depth > 32 || ++count > 100000) throw new InvalidOperationException("TestResultLimit: native message depth/count exceeded.");
                textBytes += Encoding.UTF8.GetByteCount(message.Description ?? "") + Encoding.UTF8.GetByteCount(message.Path ?? "");
                if (textBytes > 32 * 1024 * 1024) throw new InvalidOperationException("TestResultLimit: native text exceeds 32 MiB.");
                var children = new JArray();
                result.Add(new JObject {
                    ["state"] = message.State.ToString(), ["description"] = message.Description, ["path"] = message.Path,
                    ["nativeTimestamp"] = message.DateTime.ToString("o", CultureInfo.InvariantCulture),
                    ["nativeTimestampKind"] = message.DateTime.Kind == DateTimeKind.Unspecified ? "Unknown" : message.DateTime.Kind.ToString(),
                    ["errorCount"] = message.ErrorCount, ["warningCount"] = message.WarningCount,
                    ["messages"] = children
                });
                Messages(message.Messages, children, depth + 1, ref count, ref textBytes);
            }
        }
        private static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void FlushNew(string path, byte[] bytes)
        { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); } }
    }
}
