using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW.Features;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private readonly bool _testSuiteQualification = false;
        private const string TestSuiteQualificationProfile = "v21.owned-root-codeblocks.ui-reviewed.qualification-v1";
#if TESTSUITE_QUALIFICATION
        private JObject _testSuiteUiProof;
        private string _testSuiteUiProofPath;
        private string _testSuiteQualificationRoot;
        private bool _testSuiteFixtureAttempted;
        private string _testSuiteFaultOperation, _testSuiteFault;
        private JObject TestSuiteQualificationFault(JObject args)
        {
            string fault = args.Value<string>("fault"), operation = args.Value<string>("operationId");
            if (!_testSuiteQualification || _testSuiteSession != null || _testSuiteFault != null || string.IsNullOrWhiteSpace(operation) ||
                !(fault == "beforeRun" || fault == "afterRun" || fault == "afterRestore"))
                throw new BridgeException("InvalidRequest", "One explicit qualification-only response-loss boundary is required.");
            _testSuiteFaultOperation = operation; _testSuiteFault = fault;
            return new JObject { ["operationId"] = operation, ["fault"] = fault, ["armed"] = true };
        }
        private void TsFault(string operation, string phase, string directory)
        {
            if (_testSuiteFault != phase || _testSuiteFaultOperation != operation) return;
            _testSuiteFault = null;
            TsSave(directory, "injected-" + phase + ".json", new JObject { ["operationId"] = operation, ["phase"] = phase,
                ["responseLossInjected"] = true, ["processCrashClaimed"] = false, ["automaticReplay"] = false });
            throw new BridgeException("QualificationResponseLost", "Explicit qualification response-loss boundary: " + phase);
        }

        private JObject TestSuiteQualificationPrepare(JObject args)
        {
            if (!_testSuiteQualification) throw new BridgeException("TestSuiteProfileUnqualified", "An isolated qualification build and session are required.");
            EnsureAttached();
            if (!_ownsPortal || _portal.Projects.Count != 1 || _scratchProject == null || _scratchProject.IsModified ||
                _scratchProject.Path.FullName != args.Value<string>("projectPath"))
                throw new BridgeException("ProjectIsolationRequired", "One exact saved worker-created scratch is required.");
            string root = Path.GetFullPath(args.Value<string>("artifactRoot") ?? "");
            if (!ProjectPathIsAbsolute(root) || !Path.GetFullPath(_scratchProject.Path.FullName).StartsWith(
                Path.Combine(root, "scratch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectIsolationRequired", "The fixture must belong to this qualification artifact root.");
            _testSuiteQualificationRoot = root;
            string fixture = args.Value<string>("fixture");
            if (fixture == "applicationPass" || fixture == "applicationFail") return TestSuiteQualificationApplicationPrepare(args, root, fixture == "applicationPass");
            string ruleName = fixture == "clean" ? StyleQualificationFixture.Clean : fixture == "violating" ? StyleQualificationFixture.Violating :
                throw new BridgeException("InvalidRequest", "Choose the exact clean, violating, applicationPass or applicationFail packaged fixture.");
            string directory = TsDirectory(root, "prepare");
            var selector = TsSelector(_scratchProject.Path.FullName, directory);
            using (_portal.ExclusiveAccess("TIA MCP: prepare owned Test Suite qualification fixture"))
            {
                if (!_testSuiteFixtureAttempted)
                {
                    _testSuiteFixtureAttempted = true;
                    StyleQualificationFixture.Prepare(_scratchProject, SelectPlc(selector).Software, directory);
                    var compile = ChangeCompilation(SelectPlc(selector).Software.GetService<ICompilable>().Compile(), SelectPlc(selector).Path);
                    TsSave(directory, "initial-compile.json", compile);
                    if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Initial fixture compilation failed.");
                    _scratchProject.Save();
                }
            }
            JObject manifest = TsCapture(_scratchProject, selector, directory);
            TsObserveUi(_scratchProject, selector, root, ruleName, manifest, "prepare", requireLicense: true);
            using (_portal.ExclusiveAccess("TIA MCP: bind current Test Suite observations"))
            {
                var fresh = TsCapture(_scratchProject, selector, TsDirectory(root, "prepare-recheck"));
                TsEqual(manifest, fresh); TsRequireUi(_scratchProject, ruleName, fresh);
                var target = TsTarget(_scratchProject, selector, fresh);
                var probes = TsProbes(root, directory, target, "project:" + _scratchProject.Path.FullName.ToUpperInvariant());
                var definition = TsDefinition(_scratchProject, selector, ruleName, fresh, target, directory);
                return new JObject { ["target"] = target, ["definition"] = definition, ["probeEvidence"] = probes,
                    ["executionProfile"] = TestSuiteQualificationProfile, ["processId"] = _processId,
                    ["uiEvidence"] = TsArtifact(root, _testSuiteUiProofPath), ["nativeExecuted"] = false };
            }
        }

        // TS-Q02: owned scratch and externally managed owned PLCSIM only; the ticket is verified again at observe and before Run.
        private JObject TestSuiteQualificationApplicationPrepare(JObject args, string root, bool pass)
        {
            var simulator = TestSuiteOwnedSimulator(args.Value<string>("simulatorTicket"));
            string instance = simulator.Value<string>("instanceName"), name = pass ? ApplicationQualificationFixture.Pass : ApplicationQualificationFixture.Fail;
            string directory = TsDirectory(root, "prepare");
            var selector = TsSelector(_scratchProject.Path.FullName, directory);
            using (_portal.ExclusiveAccess("TIA MCP: prepare owned Test Suite application qualification fixture"))
            {
                if (!_testSuiteFixtureAttempted)
                {
                    _testSuiteFixtureAttempted = true;
                    ApplicationQualificationFixture.Prepare(_scratchProject, SelectPlc(selector).Software, instance, directory);
                    var compile = ChangeCompilation(SelectPlc(selector).Software.GetService<ICompilable>().Compile(), SelectPlc(selector).Path);
                    TsSave(directory, "initial-compile.json", compile);
                    if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Initial fixture compilation failed.");
                    _scratchProject.Save();
                }
                var manifest = TsCapture(_scratchProject, selector, directory, true);
                var target = TsTarget(_scratchProject, selector, manifest, ApplicationQualificationFixture.Profile);
                var probes = TsProbes(root, directory, target, "project:" + _scratchProject.Path.FullName.ToUpperInvariant(), TsApplicationProbes);
                var definition = TsApplicationDefinition(_scratchProject, selector, name, instance, manifest, target, directory);
                return new JObject { ["target"] = target, ["definition"] = definition, ["probeEvidence"] = probes,
                    ["executionProfile"] = ApplicationQualificationFixture.Profile, ["processId"] = _processId, ["simulator"] = simulator, ["nativeExecuted"] = false };
            }
        }
        private static readonly string[] TsStyleProbes = { "testsuite.product", "testsuite.license", "testsuite.styleService", "testsuite.scope" };
        private static readonly string[] TsApplicationProbes = { "testsuite.product", "testsuite.license", "testsuite.applicationService", "testsuite.scope", "simulator.ownedInstance" };

        /// <summary>
        /// The case's export and GetScope must name the owned controller, instance and external mode; the required scope is
        /// that controller and instance. Product, service and executor presence come from fixture validation; the license from
        /// this session's SaveToFile, which V21 documents to throw LicenseNotFoundException. Execution acceptance needs the Run.
        /// </summary>
        private JObject TsApplicationDefinition(Project project, JObject selector, string name, string instance, JObject manifest, JObject target, string directory)
        {
            var plc = SelectPlc(selector); string source = target["reference"]["snapshot"].Value<string>("sourceSha256");
            string file = Path.Combine(directory, "selected-definition.tat"); ApplicationQualificationFixture.Export(project, name, file);
            if (ChangeFileHash(file) != manifest["definitions"].Value<string>("application/" + name)) throw new BridgeException("StaleContent", "Definition changed during capture.");
            string scope = ApplicationQualificationFixture.GetScope(project, name);
            TsSave(directory, "scope-readback.json", new JObject { ["case"] = name, ["getScope"] = scope, ["plc"] = plc.Software.Name, ["instanceName"] = instance,
                ["exportSha256"] = ChangeFileHash(file), ["licenseObservation"] = "TestCase.SaveToFile returned in this session; execution acceptance only by the returned Run" });
            if (!ApplicationQualificationFixture.ScopeMatches(scope, File.ReadAllText(file), plc.Software.Name, instance))
                throw new BridgeException("TestSuiteScopeIncomplete", "The case's native scope readback does not name the owned controller, instance and externally managed mode.");
            var required = new JArray(TsReference(project, plc.Path, plc.Path, "plcSoftware", source, source),
                TsReference(project, plc.Path, plc.Path + "/plcsim/" + Segment(instance), "plcsimInstance", source, ChangeHash(instance + "|externallyManaged")));
            return new JObject { ["id"] = name, ["name"] = name, ["reference"] = TsReference(project, plc.Path, plc.Path + "/testsuite/application/" + name, "applicationTest", source, ChangeFileHash(file)),
                ["requiredScope"] = required, ["originalArtifact"] = TsArtifact(_testSuiteQualificationRoot, file) };
        }

        private void TsRequireQualification(JObject input)
        {
            if (input["application"] is JObject)
            {
                string scratchRoot = Path.Combine(Path.GetFullPath(input.Value<string>("artifactRoot") ?? ""), "scratch");
                string refusal = _testSuiteQualification ? ApplicationQualificationFixture.Refusal(input, scratchRoot) : "TestSuiteProfileUnqualified";
                if (refusal != null) throw new BridgeException(refusal, refusal == "ProjectIsolationRequired" ? "Qualification cannot adopt an external project."
                    : "Only the exact owned application fixture case, externally managed on an owned PLCSIM instance, is admitted.");
                _testSuiteQualificationRoot = Path.GetFullPath(input.Value<string>("artifactRoot"));
                return;
            }
            if (!_testSuiteQualification || input.Value<string>("executionProfile") != TestSuiteQualificationProfile ||
                !(input["run"]?["definitions"] is JArray definitions) || definitions.Count != 1 ||
                !StyleQualificationFixture.Names.Contains(definitions[0].Value<string>("name"), StringComparer.Ordinal))
                throw new BridgeException("TestSuiteProfileUnqualified", "Only the independently observed single-rule owned qualification fixture is admitted.");
            string root = Path.GetFullPath(input.Value<string>("artifactRoot") ?? "");
            string path = input["operation"]?["plan"]?["target"]?.Value<string>("projectFilePath");
            if (path == null || !ProjectPathIsAbsolute(path) || !path.StartsWith(Path.Combine(root, "scratch") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ProjectIsolationRequired", "Qualification cannot adopt an external project.");
            _testSuiteQualificationRoot = root;
        }

        private static JObject TsSelector(string projectPath, string directory) => new JObject {
            ["projectPath"] = projectPath, ["tableName"] = "__TSQ01_absent_control__", ["allowAbsent"] = true,
            ["group"] = "root", ["artifactDirectory"] = directory };
        private static string TsDirectory(string root, string phase)
        { string path = Path.Combine(root, "testsuite-qualification", phase + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
        private static void TsSave(string directory, string name, JObject value) => StyleUiEvidence.Save(Path.Combine(directory, name), value);
        private static void TsEqual(JToken expected, JToken actual)
        { if (expected == null || !JToken.DeepEquals(expected, actual)) throw new BridgeException("PreservationFailed", "Independently recaptured complete bounded fixture changed."); }
        private static JObject TsArtifact(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("InvalidArtifact", "Evidence must remain under the qualification root.");
            return new JObject { ["relativePath"] = path.Substring(prefix.Length).Replace('\\', '/'), ["sha256"] = ChangeFileHash(path) };
        }

        private JObject TsCapture(Project project, JObject selector, string directory, bool application = false)
        {
            EnsureProjectOffline(project);
            var plc = SelectPlc(selector);
            var budget = new ScanBudget(10000);
            var owners = DeviceItems(project, budget).Select(d => d.Item.GetService<SoftwareContainer>()?.Software).Where(s => s != null).ToArray();
            if (budget.Truncated || owners.Length != 1 || !owners[0].Equals(plc.Software))
                throw new BridgeException("TestSuiteProfileUnqualified", "Exactly one PLC software owner is required.");
            if (application) ApplicationQualificationFixture.Validate(project, plc.Software); else StyleQualificationFixture.Validate(project, plc.Software);
            var scopes = SoftwareScopes(plc, budget).ToArray();
            if (budget.Truncated || scopes.Length != 1)
                throw new BridgeException("TestSuiteProfileUnqualified", "Software-unit payloads are outside this exact root-only qualification fixture.");
            if (project.ProjectLibrary.TypeFolder.Types.Count != 0 || project.ProjectLibrary.TypeFolder.Folders.Count != 0 ||
                project.ProjectLibrary.MasterCopyFolder.MasterCopies.Count != 0 || project.ProjectLibrary.MasterCopyFolder.Folders.Count != 0)
                throw new BridgeException("TestSuiteProfileUnqualified", "This exact fixture requires an independently observed empty project library.");
            var vci = QualificationVciCapture(project);
            if (((JArray)vci["workspaces"]).Count != 0 || ((JArray)vci["groups"]).Count != 1)
                throw new BridgeException("TestSuiteProfileUnqualified", "This exact fixture requires no VCI workspaces or user groups.");
            var snapshot = WatchSnapshot(selector);
            string definitionsDirectory = Path.Combine(directory, "definitions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(definitionsDirectory);
            var manifest = new JObject {
                ["profile"] = application ? ApplicationQualificationFixture.Profile : TestSuiteQualificationProfile,
                ["peers"] = snapshot["peers"].DeepClone(),
                ["tables"] = new JArray(((JArray)snapshot["tables"]).Select(t => new JObject { ["kind"] = t["kind"], ["group"] = t["group"], ["name"] = t["name"], ["sha256"] = t["semanticSha256"] })),
                ["definitions"] = StylePrerequisiteProbe.CaptureDefinitions(project, definitionsDirectory),
                ["projectLibrary"] = new JObject { ["types"] = 0, ["typeFolders"] = 0, ["masterCopies"] = 0, ["masterCopyFolders"] = 0 },
                ["vci"] = vci,
                ["inventory"] = new JArray(plc.Software.BlockGroup.Blocks.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => new JObject {
                    ["name"] = b.Name, ["type"] = b.GetType().FullName, ["number"] = b.Number, ["language"] = b.ProgrammingLanguage.ToString() })) };
            if (project.IsModified) throw new BridgeException("StaleContent", "Qualification requires a saved unmodified project.");
            TsSave(directory, "manifest-" + Guid.NewGuid().ToString("N") + ".json", manifest);
            return manifest;
        }

        private JObject TsObserveUi(Project project, JObject selector, string root, string rule, JObject manifest, string phase, bool requireLicense)
        {
            if (project.IsModified) throw new BridgeException("StaleContent", "Saved state required before UI scope observation.");
            StyleQualificationFixture.Show(project, rule);
            string directory = TsDirectory(root, "ui-" + phase);
            var challenge = new JObject { ["nonce"] = Guid.NewGuid().ToString("N"), ["issuedUtc"] = DateTime.UtcNow.ToString("o"),
                ["phase"] = phase, ["projectPath"] = project.Path.FullName, ["processId"] = _processId, ["sessionEpoch"] = _testSuiteEpoch,
                ["workerSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location), ["ruleName"] = rule,
                ["definitionSha256"] = manifest["definitions"]["style/" + rule].DeepClone(),
                ["manifestSha256"] = ChangeHash(manifest.ToString(Formatting.None)), ["requireLicenseCheck"] = requireLicense,
                ["selectedProgramBlocks"] = new JArray(StyleQualificationFixture.BlockNames),
                ["excludedBranches"] = new JArray("Software Units", "PLC-Variablen", "PLC-Datentypen") };
            TsSave(directory, "challenge.json", challenge);
            string response = Path.Combine(directory, "observation.json");
            var deadline = DateTime.UtcNow.AddMinutes(12);
            while (!File.Exists(response))
            {
                if (DateTime.UtcNow >= deadline) throw new BridgeException("TestSuiteScopeIncomplete", "Independent UI observation was not supplied before the bounded wait expired.");
                Thread.Sleep(200);
            }
            var observed = StyleUiEvidence.Validate(challenge, StyleUiEvidence.Read(response), directory, DateTime.UtcNow);
            if (project.IsModified) throw new BridgeException("StaleContent", "Project changed during independent UI observation.");
            var after = TsCapture(project, selector, TsDirectory(root, "ui-after")); TsEqual(manifest, after);
            _testSuiteUiProof = observed; _testSuiteUiProofPath = response;
            return observed;
        }
        private void TsRequireUi(Project project, string rule, JObject manifest)
        {
            var challenge = _testSuiteUiProof?["challenge"] as JObject;
            if (challenge == null || challenge.Value<string>("projectPath") != project.Path.FullName ||
                challenge.Value<string>("sessionEpoch") != _testSuiteEpoch || challenge.Value<int?>("processId") != _processId ||
                challenge.Value<string>("ruleName") != rule || challenge.Value<string>("manifestSha256") != ChangeHash(manifest.ToString(Formatting.None)))
                throw new BridgeException("TestSuiteScopeIncomplete", "Fresh independent scope evidence for this exact worker/project/definition is required.");
            StyleUiEvidence.Validate(challenge, StyleUiEvidence.Read(_testSuiteUiProofPath), Path.GetDirectoryName(_testSuiteUiProofPath), DateTime.UtcNow);
        }
        private JObject TsTarget(Project project, JObject selector, JObject manifest, string profile = TestSuiteQualificationProfile)
        {
            var plc = SelectPlc(selector); string source = ChangeHash(manifest.ToString(Formatting.None));
            return new JObject {
                ["reference"] = TsReference(project, plc.Path, plc.Path, "plcSoftware", source, source),
                ["projectFilePath"] = project.Path.FullName, ["sessionEpoch"] = _testSuiteEpoch,
                ["adapterSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location),
                ["profileSha256"] = ChangeHash(profile + "|" + ChangeFileHash(typeof(StylePublicApi).Assembly.Location)),
                ["hardwareIdentity"] = ChangeHash(manifest["peers"]["hardware"].ToString(Formatting.None)),
                ["nativeIdentity"] = "tia-process:" + _processId, ["offline"] = true, ["probes"] = new JArray() };
        }
        private JObject TsReference(Project project, string plcPath, string objectPath, string kind, string source, string content) => new JObject {
            ["object"] = new JObject { ["projectPath"] = project.Path.FullName, ["plcPath"] = plcPath, ["scopePath"] = plcPath, ["objectPath"] = objectPath, ["kind"] = kind },
            // Immutable content snapshot identity survives a proven identical reopen. The distinct
            // live native epoch is independently bound in target observations and reopen receipts.
            ["snapshot"] = new JObject { ["observationId"] = source, ["sourceSha256"] = source, ["tiaVersion"] = 21 }, ["contentSha256"] = content };
        private JArray TsProbes(string root, string directory, JObject target, string resourceKey, string[] names = null)
        {
            var references = new JArray(); var probes = new JArray();
            foreach (string name in names ?? TsStyleProbes)
            {
                var receipt = new JObject { ["name"] = name, ["state"] = "observed", ["projectResourceKey"] = resourceKey,
                    ["sessionEpoch"] = target["sessionEpoch"], ["adapterSha256"] = target["adapterSha256"], ["profileSha256"] = target["profileSha256"],
                    ["sourceSha256"] = target["reference"]["snapshot"]["sourceSha256"], ["contentSha256"] = target["reference"]["contentSha256"],
                    ["hardwareIdentity"] = target["hardwareIdentity"], ["nativeIdentity"] = target["nativeIdentity"] };
                string file = Path.Combine(directory, name + ".json"); StyleUiEvidence.Save(file, receipt); var artifact = TsArtifact(root, file);
                references.Add(artifact); probes.Add(new JObject { ["name"] = name, ["state"] = "observed", ["evidenceSha256"] = artifact["sha256"] });
            }
            target["probes"] = probes; return references;
        }
        private JObject TsDefinition(Project project, JObject selector, string rule, JObject manifest, JObject target, string directory)
        {
            var plc = SelectPlc(selector); string source = target["reference"]["snapshot"].Value<string>("sourceSha256");
            string file = Path.Combine(directory, "selected-definition.xml"); StyleQualificationFixture.Export(project, rule, file);
            if (ChangeFileHash(file) != manifest["definitions"].Value<string>("style/" + rule)) throw new BridgeException("StaleContent", "Definition changed during capture.");
            var scope = new JArray(plc.Software.BlockGroup.Blocks.Where(b => b.Name != "TiaMcp_EnableGate_DB").OrderBy(b => b.Name, StringComparer.Ordinal).Select(b => {
                string path = plc.Path + "/blocks/" + Segment(b.Name);
                return TsReference(project, plc.Path, path, "block", source, manifest["peers"].Value<string>("block:" + path)); }));
            return new JObject { ["id"] = rule, ["name"] = rule, ["reference"] = TsReference(project, plc.Path, plc.Path + "/testsuite/style/" + rule, "ruleSet", source, ChangeFileHash(file)),
                ["requiredScope"] = scope, ["originalArtifact"] = TsArtifact(_testSuiteQualificationRoot, file) };
        }
#endif
    }
}
