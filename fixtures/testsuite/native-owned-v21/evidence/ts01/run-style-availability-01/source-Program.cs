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
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.TestSuite;
using Siemens.Engineering.TestSuite.StyleGuide;

// Availability only. Exact requested scope remains unverified; no coverage or qualification claim.
// Public Siemens API only. No application/system executor, simulator, or endpoint invocation.
internal static class Program
{
    private const string Api = @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
    private static string Output;
    [STAThread] private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, evt) => {
            string name = new AssemblyName(evt.Name).Name, path = Path.Combine(Api, name + ".dll");
            return name.StartsWith("Siemens.Engineering", StringComparison.Ordinal) && File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try { Console.WriteLine(Run(args)); return 0; }
        catch (Exception e) { var failure = Failure(e); if (Output != null) Save("failure.json", failure); Console.WriteLine(failure); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static JObject Run(string[] args)
    {
        if (args.Length != 1) throw new InvalidOperationException("One exact admission JSON path required.");
        var admission = JObject.Parse(File.ReadAllText(args[0]));
        if (admission.Value<string>("mode") != "run-style-availability") throw new InvalidOperationException("Only run-style-availability is supported.");
        if (admission.Value<string>("binarySha256") != Hash(Assembly.GetExecutingAssembly().Location)) throw new InvalidOperationException("Binary hash changed.");
        foreach (string part in new[] { "Base", "Step7", "TestSuite" })
            if (admission["apiSha256"].Value<string>(part) != Hash(Path.Combine(Api, "Siemens.Engineering." + part + ".dll"))) throw new InvalidOperationException("API hash changed.");
        if (admission.Value<bool?>("isolatedScratch") != true || admission.Value<bool?>("allProjectObjectsOwned") != true ||
            admission.Value<bool?>("availabilityOnlyScopeUnverifiedAccepted") != true)
            throw new InvalidOperationException("Explicit isolated-owned-scratch and unverified-scope availability admission required.");
        string root = Path.GetFullPath(admission.Value<string>("artifactRoot")), output = Path.GetFullPath(admission.Value<string>("outputDirectory"));
        Inside(root, output);
        if (Directory.Exists(output)) throw new InvalidOperationException("Fresh output required; automatic replay forbidden.");
        var ownership = (JObject)admission["ownershipEvidence"];
        if (ownership == null || Hash(ownership.Value<string>("path")) != ownership.Value<string>("sha256"))
            throw new InvalidOperationException("Exact scratch-creation ownership receipt required.");
        Directory.CreateDirectory(output); Output = output;
        File.Copy(ownership.Value<string>("path"), Path.Combine(Output, "ownership-receipt.json"));
        foreach (string file in new[] { Assembly.GetExecutingAssembly().Location, Assembly.GetExecutingAssembly().Location + ".config", typeof(JObject).Assembly.Location })
            if (File.Exists(file)) File.Copy(file, Path.Combine(Output, Path.GetFileName(file)));
        foreach (JObject source in (JArray)admission["sourceFiles"])
        {
            string path = source.Value<string>("path");
            if (Hash(path) != source.Value<string>("sha256")) throw new InvalidOperationException("Source hash changed.");
            File.Copy(path, Path.Combine(Output, "source-" + Path.GetFileName(path)));
        }
        Save("admission.json", admission);
        var process = TiaPortal.GetProcesses().Single(p => p.Id == admission.Value<int>("processId"));
        using (var portal = process.Attach())
        using (portal.ExclusiveAccess("TIA MCP: isolated scratch style execution availability only"))
        {
            var project = portal.Projects.Single(p => string.Equals(p.Path.FullName, admission.Value<string>("projectPath"), StringComparison.OrdinalIgnoreCase));
            Inside(root, project.Path.FullName);
            if (!project.Name.StartsWith("TiaMcpScratch_", StringComparison.Ordinal) || admission.Value<bool?>("ownedScratch") != true ||
                admission.Value<string>("creationTool") != "tia_create_scratch" || project.IsModified)
                throw new InvalidOperationException("Exact saved owned scratch required.");
            if (admission.Value<string>("plcName") != "ScratchPLC" || admission.Value<string>("blockName") != "TiaMcp_IsEnabled" ||
                admission.Value<string>("ruleSetName") != "TiaMcp_CasingAvailability")
                throw new InvalidOperationException("Only the explicit style availability fixture is supported.");
            var devices = Devices(project).ToArray();
            var plcs = devices.SelectMany(d => Items(d.DeviceItems)).Select(i => i.GetService<SoftwareContainer>()?.Software as PlcSoftware).Where(p => p != null).ToArray();
            if (devices.Length != 1 || plcs.Length != 1 || plcs[0].Name != "ScratchPLC") throw new InvalidOperationException("Exact isolated one-device PLC owner mismatch.");
            var plc = plcs[0];
            var inventory = Inventory(plc);
            var block = plc.BlockGroup.Blocks.Find("TiaMcp_IsEnabled") ?? throw new InvalidOperationException("Exact root block absent.");
            var suite = project.GetService<TestSuiteService>() ?? throw new InvalidOperationException("Test Suite service absent.");
            var rules = suite.StyleGuideGroup.RuleSets.Find(admission.Value<string>("ruleSetName")) ?? throw new InvalidOperationException("Exact rule set absent.");
            if (suite.StyleGuideGroup.RuleSets.Count != 1) throw new InvalidOperationException("Exactly one owned style rule set required.");
            var result = new JObject { ["mode"] = "run-style-availability", ["processId"] = process.Id, ["projectPath"] = project.Path.FullName,
                ["plcName"] = plc.Name, ["blockName"] = block.Name, ["blockType"] = block.GetType().FullName, ["ruleSetName"] = rules.Name,
                ["startedAtUtc"] = DateTime.UtcNow, ["beforeModified"] = project.IsModified, ["automaticReplay"] = false,
                ["styleExecutionInvoked"] = false, ["applicationExecutionInvoked"] = false, ["systemExecutionInvoked"] = false,
                ["licenseAvailability"] = "unknown", ["executionAvailability"] = "notAttempted", ["effectiveScope"] = "unverified",
                ["qualification"] = false, ["nativeQualified"] = false, ["testsPassed"] = false, ["coverageClaim"] = false,
                ["scopeLimit"] = "SetScope success records requested scope only; execution availability is not exact-scope or test-coverage proof.",
                ["inventory"] = inventory, ["ownershipReceipt"] = Artifact(Path.Combine(Output, "ownership-receipt.json")) };
            string definition = Path.Combine(Output, "definition-before.xml");
            if (!rules.SaveToFile(new FileInfo(definition))) throw new InvalidOperationException("Native definition export failed.");
            result["definitionBefore"] = Artifact(definition);
            if (Hash(definition) != admission.Value<string>("definitionSha256")) throw new InvalidOperationException("Immutable native definition hash changed.");
            block.Export(new FileInfo(Path.Combine(Output, "target-block-before.xml")), ExportOptions.WithDefaults);
            result["blockBefore"] = Artifact(Path.Combine(Output, "target-block-before.xml"));
            result["nativeMetadata"] = Metadata(rules); result["scopeBefore"] = ObserveScope(rules, block); Save("before.json", result);
            string stage = "checkpoint";
            try
            {
                Save("checkpoint-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false });
                project.Archive(new DirectoryInfo(Output), "before-availability.zap21", ProjectArchivationMode.Compressed);
                result["checkpoint"] = Artifact(Path.Combine(Output, "before-availability.zap21")); Save("checkpoint.json", result);
                rules = suite.StyleGuideGroup.RuleSets.Find(admission.Value<string>("ruleSetName")) ?? throw new InvalidOperationException("Rule set disappeared.");
                block = plc.BlockGroup.Blocks.Find("TiaMcp_IsEnabled") ?? throw new InvalidOperationException("Block disappeared.");
                string recheck = Path.Combine(Output, "definition-before-binding.xml");
                if (!rules.SaveToFile(new FileInfo(recheck)) || Hash(recheck) != admission.Value<string>("definitionSha256")) throw new InvalidOperationException("Definition changed before scope binding.");
                Inventory(plc);
                Save("scope-binding-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false,
                    ["nativeRoute"] = "RuleSet.SetScope(UpdateOptions.Override,IEnumerable<IEngineeringObject>)",
                    ["requestedScope"] = new JArray("ScratchPLC/Program blocks/TiaMcp_IsEnabled"), ["definition"] = Artifact(recheck),
                    ["effectiveScope"] = "unverified", ["qualification"] = false });
                stage = "scopeBindingDispatched";
                bool setScope = rules.SetScope(UpdateOptions.Override, new IEngineeringObject[] { block });
                result["setScopeReturned"] = setScope; result["scopeAfterBinding"] = ObserveScope(rules, block);
                Save("scope-binding-return.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["setScopeReturned"] = setScope,
                    ["scopeObservation"] = result["scopeAfterBinding"].DeepClone(), ["effectiveScope"] = "unverified", ["qualification"] = false });
                if (!setScope) throw new InvalidOperationException("SetScope did not return true; executor not invoked.");
                rules = suite.StyleGuideGroup.RuleSets.Find(admission.Value<string>("ruleSetName")) ?? throw new InvalidOperationException("Rule set disappeared after scope binding.");
                block = plc.BlockGroup.Blocks.Find("TiaMcp_IsEnabled") ?? throw new InvalidOperationException("Block disappeared after scope binding.");
                Inventory(plc);
                string dispatchDefinition = Path.Combine(Output, "definition-before-execution.xml");
                if (!rules.SaveToFile(new FileInfo(dispatchDefinition)) || Hash(dispatchDefinition) != admission.Value<string>("definitionSha256"))
                    throw new InvalidOperationException("Definition bytes changed after scope binding; new review required before any executor call.");
                var executor = suite.StyleGuideGroup.GetService<RuleSetExecutor>() ?? throw new InvalidOperationException("Style executor absent.");
                result["scopeAtExecution"] = ObserveScope(rules, block);
                Save("execution-intent.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["automaticReplay"] = false,
                    ["nativeRoute"] = "RuleSetExecutor.Run(RuleSet)", ["definition"] = Artifact(dispatchDefinition),
                    ["requestedScope"] = new JArray("ScratchPLC/Program blocks/TiaMcp_IsEnabled"), ["setScopeReturned"] = true,
                    ["scopeObservation"] = result["scopeAtExecution"].DeepClone(), ["effectiveScope"] = "unverified", ["qualification"] = false,
                    ["executionPurpose"] = "Operation-specific style license and execution availability in an entirely owned isolated scratch." });
                stage = "styleExecutionDispatched"; result["styleExecutionInvoked"] = true;
                var native = executor.Run(rules); stage = "styleExecutionReturned";
                // Persist that the call returned before reading diagnostic collections or later readback.
                Save("execution-return.json", new JObject { ["atUtc"] = DateTime.UtcNow, ["callReturned"] = true,
                    ["effectiveScope"] = "unverified", ["qualification"] = false, ["testsPassed"] = false });
                result["licenseAvailability"] = "acceptedForObservedStyleExecution"; result["executionAvailability"] = "styleCallReturned";
                int count = 0;
                var raw = new JObject { ["state"] = native.State.ToString(), ["errorCount"] = native.ErrorCount, ["warningCount"] = native.WarningCount,
                    ["messages"] = Messages(native.Messages, 0, ref count), ["effectiveScope"] = "unverified", ["qualification"] = false,
                    ["testsPassed"] = false, ["coverageClaim"] = false };
                Save("native-results.json", raw); result["rawResult"] = Artifact(Path.Combine(Output, "native-results.json"));
                stage = "readback";
                result["scopeAfter"] = ObserveScope(rules, block);
                string after = Path.Combine(Output, "definition-after.xml");
                if (!rules.SaveToFile(new FileInfo(after))) throw new InvalidOperationException("Native definition readback failed.");
                result["definitionAfter"] = Artifact(after); result["definitionUnchanged"] = Hash(after) == admission.Value<string>("definitionSha256");
                block.Export(new FileInfo(Path.Combine(Output, "target-block-after.xml")), ExportOptions.WithDefaults);
                result["blockAfter"] = Artifact(Path.Combine(Output, "target-block-after.xml"));
                result["blockExportBytesUnchanged"] = result["blockBefore"].Value<string>("sha256") == result["blockAfter"].Value<string>("sha256");
                project.Save(); result["saved"] = true; stage = "complete";
            }
            catch (Exception e)
            {
                result["operationFailure"] = Failure(e); result["failureStage"] = stage;
                if (IsLicenseFailure(e)) result["licenseFailureAtStage"] = stage;
                if (stage == "styleExecutionDispatched")
                {
                    result["executionAvailability"] = IsLicenseFailure(e) ? "licenseRejected" : "failedOrUnknown";
                    if (IsLicenseFailure(e)) result["licenseAvailability"] = "licenseNotFoundForObservedStyleExecution";
                }
                // Post-return failures do not erase the immutable observed execution-return receipt.
            }
            result["afterModified"] = project.IsModified; result["completed"] = true; result["finishedAtUtc"] = DateTime.UtcNow;
            result["lastStage"] = stage; Save("result.json", result); return result;
        }
    }

    private static JObject Inventory(PlcSoftware plc)
    {
        var expected = new[] { "Main", "TiaMcp_EnableGate", "TiaMcp_EnableGate_DB", "TiaMcp_IsEnabled" };
        var actual = plc.BlockGroup.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (plc.BlockGroup.Groups.Count != 0 || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Exact four-block root fixture without user block groups required for isolated availability probe.");
        return new JObject { ["rootBlocks"] = new JArray(actual), ["userBlockGroups"] = plc.BlockGroup.Groups.Count,
            ["allProjectObjectsOwned"] = "explicitAdmissionAndScratchCreationReceipt", ["coverageClaim"] = false };
    }

    private static JObject Metadata(IEngineeringObject rules) => new JObject {
        ["attributes"] = new JArray(rules.GetAttributeInfos().Select(a => new JObject { ["name"] = a.Name, ["access"] = a.AccessMode.ToString(), ["types"] = new JArray(a.SupportedTypes.Select(t => t.FullName)) })),
        ["compositions"] = new JArray(rules.GetCompositionInfos().Select(c => c.Name)),
        ["invocations"] = new JArray(rules.GetInvocationInfos().Select(i => new JObject { ["name"] = i.Name, ["type"] = i.Type.FullName,
            ["parameters"] = new JArray(i.ParameterInfos.Select(p => new JObject { ["name"] = p.Name, ["type"] = p.Type.FullName })) })) };

    private static JObject ObserveScope(IEngineeringObject rules, PlcBlock expected)
    {
        var observations = new JArray();
        // No guessed attributes or invocation names are invoked. Only actually advertised public read routes are candidates.
        foreach (var attribute in rules.GetAttributeInfos().Where(a => (a.Name == "Scope" || a.Name == "ExecutionScope") && a.AccessMode.ToString().Contains("Read")))
            Observe(observations, "attribute:" + attribute.Name, () => rules.GetAttribute(attribute.Name), expected);
        foreach (var composition in rules.GetCompositionInfos().Where(c => c.Name == "Scope" || c.Name == "ExecutionScope"))
            Observe(observations, "composition:" + composition.Name, () => rules.GetComposition(composition.Name), expected);
        foreach (var invocation in rules.GetInvocationInfos().Where(i => i.Name == "GetScope" && i.ParameterInfos.Count == 0))
            Observe(observations, "invocation:GetScope", () => rules.Invoke("GetScope", null), expected);
        bool verified = observations.Count > 0 && observations.All(o => o.Value<bool?>("exactSingleBlock") == true);
        return new JObject { ["exactSingleBlockVerified"] = verified, ["routes"] = observations,
            ["state"] = verified ? "observed" : observations.Count == 0 ? "publicScopeReadbackUnavailable" : "unverifiedOrMismatched",
            ["setScopeReturnIsNotReadback"] = true, ["expected"] = "ScratchPLC/Program blocks/TiaMcp_IsEnabled" };
    }

    private static void Observe(JArray result, string route, Func<object> read, PlcBlock expected)
    {
        var observation = new JObject { ["route"] = route };
        try
        {
            object value = read(); observation["valueType"] = value?.GetType().FullName;
            var objects = new List<IEngineeringObject>(); bool nativeObjectsOnly = true;
            if (value is IEngineeringObject single) objects.Add(single);
            else if (value is IEnumerable sequence && !(value is string))
            {
                int count = 0;
                foreach (object item in sequence) { if (++count > 1000) throw new InvalidOperationException("Scope bound exceeded."); if (item is IEngineeringObject native) objects.Add(native); else nativeObjectsOnly = false; }
            }
            else nativeObjectsOnly = false;
            observation["objects"] = new JArray(objects.Select(o => new JObject { ["type"] = o.GetType().FullName,
                ["isExpectedNativeBlock"] = o.Equals(expected) }));
            if (value is string text) observation["unqualifiedText"] = text.Length > 8192 ? text.Substring(0, 8192) : text;
            observation["exactSingleBlock"] = nativeObjectsOnly && objects.Count == 1 && objects[0].Equals(expected);
        }
        catch (Exception e) { observation["failure"] = Failure(e); observation["exactSingleBlock"] = false; }
        result.Add(observation);
    }
    private static IEnumerable<Device> Devices(Project project) => project.Devices.Concat(GroupDevices(project.DeviceGroups)).Concat(project.UngroupedDevicesGroup.Devices);
    private static IEnumerable<Device> GroupDevices(DeviceUserGroupComposition groups) { foreach (var group in groups) { foreach (var d in group.Devices) yield return d; foreach (var d in GroupDevices(group.Groups)) yield return d; } }
    private static IEnumerable<DeviceItem> Items(DeviceItemComposition items) { foreach (var item in items) { yield return item; foreach (var child in Items(item.DeviceItems)) yield return child; } }
    private static JArray Messages(IEnumerable<TestResultsMessage> messages, int depth, ref int count)
    {
        if (depth > 128) throw new InvalidOperationException("Result depth exceeded."); var result = new JArray();
        foreach (var item in messages) { if (++count > 100000) throw new InvalidOperationException("Result count exceeded."); result.Add(new JObject { ["state"] = item.State.ToString(),
            ["path"] = item.Path, ["description"] = item.Description, ["dateTime"] = item.DateTime.ToString("o"), ["errorCount"] = item.ErrorCount, ["warningCount"] = item.WarningCount,
            ["messages"] = Messages(item.Messages, depth + 1, ref count) }); } return result;
    }
    private static bool IsLicenseFailure(Exception e) => e is LicenseNotFoundException || e.InnerException != null && IsLicenseFailure(e.InnerException);
    private static JObject Failure(Exception e) => new JObject { ["type"] = e.GetType().FullName, ["message"] = e.Message, ["inner"] = e.InnerException == null ? null : Failure(e.InnerException), ["automaticReplay"] = false };
    private static void Inside(string root, string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Path leaves admitted artifact root."); }
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static JObject Artifact(string path) => new JObject { ["path"] = path, ["sha256"] = Hash(path), ["bytes"] = new FileInfo(path).Length };
    private static void Save(string name, JObject data) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data.ToString(Formatting.Indented)); using (var file = new FileStream(Path.Combine(Output, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); } }
}
