using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
        private StyleWorkerSession _testSuiteSession;
        private string _testSuiteEpoch = Guid.NewGuid().ToString("N");

        // Internal qualification read route on the already owning worker. No import, SetScope or Run.
        private JObject TestSuitePrerequisites(JObject args)
        {
            EnsureAttached();
            var project = SelectProject(args);
            if (!_ownsPortal || _portal.Projects.Count != 1 || !WatchOwnsScratch(project) || project.IsModified ||
                project.Path.FullName != args.Value<string>("projectPath"))
                throw new BridgeException("ProjectIsolationRequired", "Read-only Test Suite prerequisites require the exact saved worker-owned scratch.");
            EnsureProjectOffline(project);
            string output = Path.Combine(ArtifactDirectory(args), "ts-prerequisites-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            JObject observation;
            using (_portal.ExclusiveAccess("TIA MCP: read-only Test Suite prerequisites"))
            {
                try { observation = ReadTestSuitePrerequisites(project, args, output); }
                catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is TypeLoadException)
                {
                    observation = new JObject { ["optionalAdapterAvailable"] = false, ["failureType"] = error.GetType().FullName,
                        ["failure"] = error.Message, ["executionLicense"] = "unknown", ["completeEffectiveScope"] = false };
                }
                observation["projectPath"] = project.Path.FullName; observation["sessionEpoch"] = _testSuiteEpoch;
                observation["processId"] = _processId; observation["workerSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location);
                observation["projectModifiedAfter"] = project.IsModified;
                observation["nativeExecutionAuthorized"] = false; observation["checkpointInvoked"] = false; observation["executorInvoked"] = false;
                string path = Path.Combine(output, "prerequisites.json");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                { var bytes = Encoding.UTF8.GetBytes(observation.ToString(Formatting.None)); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                observation["receipt"] = FileArtifact(new FileInfo(path));
            }
            return observation;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static JObject ReadTestSuitePrerequisites(Project project, JObject args, string output)
        {
            var names = args["ruleNames"] as JArray ?? throw new BridgeException("InvalidRequest", "Exact existing ruleNames are required.");
            return StylePrerequisiteProbe.Read(project, names.Values<string>().ToArray(), output);
        }

        private static void RequireTestSuiteQualification()
        {
            throw new BridgeException("TestSuiteProfileUnqualified", "No native Test Suite profile is qualified. Complete effective scope, current prerequisites and TS-Q01 lifecycle receipts are required.");
        }

        // Keep the gate outside optional-product code so absent TestSuite assemblies do not break worker startup
        // or alter the fail-closed response. No flag or request field bypasses this gate.
        private JObject TestSuiteBegin(JObject args)
        {
#if TESTSUITE_QUALIFICATION
            if (_testSuiteQualification) { TsRequireQualification(args); return TestSuiteBeginQualified(args); }
#endif
            RequireTestSuiteQualification();
            return TestSuiteBeginQualified(args);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private JObject TestSuiteBeginQualified(JObject args)
        {
            if (_testSuiteSession != null || _watchLease != null || _sharedMutationLease != null)
                throw new BridgeException("ResourceBusy", "An existing owning-worker native lease cannot be replaced.");
            _testSuiteSession = new StyleWorkerSession(new NativeStyleBoundary(this), args);
            return _testSuiteSession.Opened();
        }
        private JObject TestSuiteStage(string stage, JObject args)
        {
            if (_testSuiteSession == null) throw new BridgeException("InvalidLease", "No owning-worker Test Suite lease exists.");
            // The wire uses an object envelope for all worker replies, including checkpoint arrays.
            return new JObject { ["value"] = _testSuiteSession.Execute(stage, args) };
        }
        private JObject TestSuiteEnd(JObject args)
        {
            if (_testSuiteSession == null) throw new BridgeException("InvalidLease", "No owning-worker Test Suite lease exists.");
            var result = _testSuiteSession.End(args.Value<string>("leaseToken"), args.Value<string>("operationId"));
#if TESTSUITE_QUALIFICATION
            if (_testSuiteQualification && _scratchProject != null)
            {
                _scratchProject.Close(); _scratchProject = null;
                if (_portal.Projects.Count != 0) throw new BridgeException("NativeCleanupUnverified", "Qualification scratch did not close after session end.");
            }
#endif
            _testSuiteSession = null; return result;
        }
        private void TestSuiteRelease()
        {
            // Do not clear a failed release and make a second dispatch appear admissible.
            if (_testSuiteSession == null) return;
            _testSuiteSession.Dispose(); _testSuiteSession = null;
        }

        private sealed partial class NativeStyleBoundary : IStyleWorkerBoundary
        {
            private readonly OpennessSession owner;
            private JObject context, selector, baseline;
            private string root, directory;
            private Project project;
            private StylePublicApi api;
            internal NativeStyleBoundary(OpennessSession owner) { this.owner = owner; }
            public void RequireQualification(JObject input)
            {
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) { owner.TsRequireQualification(input); return; }
#endif
                RequireTestSuiteQualification();
            }

            public IDisposable Acquire(JObject input)
            {
                context = (JObject)input.DeepClone(); owner.EnsureAttached();
                if (!owner._ownsPortal) throw new BridgeException("ProjectIsolationRequired", "Test Suite requires the owning worker's headless native instance.");
                root = Path.GetFullPath(context.Value<string>("artifactRoot"));
                if (!ProjectPathIsAbsolute(root)) throw new BridgeException("InvalidArtifact", "Absolute artifact root required.");
                var target = (JObject)context["operation"]["plan"]["target"];
                selector = new JObject { ["projectPath"] = target.Value<string>("projectFilePath"),
                    ["plcPath"] = target["reference"]["object"]["plcPath"].DeepClone(),
                    ["tableName"] = "__TSQ01_absent_control__", ["allowAbsent"] = true, ["group"] = "root" };
                string state = context["operation"].Value<string>("state");
                if (state == "acquired")
                {
                    project = owner.SelectProject(selector);
                    if (owner._portal.Projects.Count != 1 || !owner.WatchOwnsScratch(project) || project.IsModified ||
                        project.Path.FullName != selector.Value<string>("projectPath"))
                        throw new BridgeException("ProjectIsolationRequired", "One exact saved owned scratch is required.");
                    owner.EnsureProjectOffline(project);
                    if (target.Value<string>("sessionEpoch") != owner._testSuiteEpoch)
                        throw new BridgeException("StaleNativeTarget", "Test Suite session epoch changed.");
                    api = new StylePublicApi(project, context.Value<string>("executionProfile"));
#if TESTSUITE_QUALIFICATION
                    if (owner._testSuiteQualification) api = new StylePublicApi(project, context.Value<string>("executionProfile"),
                        () => { owner.TsRequireQualification(context); if (!Application) owner.TsRequireUi(project, Rule, Capture()); }, true);
#endif
                }
                else if (owner._portal.Projects.Count != 0)
                    throw new BridgeException("ProjectIsolationRequired", "Recovery/reconciliation require an empty owned instance.");
                var lease = owner._portal.ExclusiveAccess("TIA MCP: durable Test Suite session");
                try
                {
                    directory = Path.Combine(root, "testsuite-worker", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory); selector["artifactDirectory"] = directory;
#if TESTSUITE_QUALIFICATION
                    if (owner._testSuiteQualification) { qualificationLease = new QualificationLease(owner, lease); return qualificationLease; }
#endif
                    return lease;
                }
                catch { lease.Dispose(); throw; }
            }

            public JObject Observe()
            {
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) return ObserveQualification();
#endif
                string output = NewDirectory("observation");
                var definitions = (JArray)context["run"]["definitions"];
                var application = context["application"] as JObject;
                bool system = application?.Value<string>("executionMode") == "endpoint";
                var receipt = application == null ? StylePrerequisiteProbe.Read(project, definitions.Select(d => d.Value<string>("name")).ToArray(), output)
                    : system ? SystemTestDefinitions.Inventory(project, 1000, output) : ApplicationTestDefinitions.Inventory(project, 1000, output);
                if (application != null)
                {
                    receipt["simulator"] = system ? owner.TestSuiteOwnedEndpoint(application.Value<string>("simulatorTicket"), application.Value<string>("serverAddress"))
                        : owner.TestSuiteOwnedSimulator(application.Value<string>("simulatorTicket"));
                    receipt["completeEffectiveScope"] = false; // Scope readback names only the target controller or endpoint.
                }
                receipt["sessionEpoch"] = owner._testSuiteEpoch;
                receipt["processId"] = owner._processId;
                receipt["workerSha256"] = ChangeFileHash(typeof(OpennessSession).Assembly.Location);
                receipt["adapterSha256"] = ChangeFileHash(typeof(StylePublicApi).Assembly.Location);
                Save(Path.Combine(output, "prerequisites.json"), receipt);
                // These actual observations intentionally cannot become a successful typed admission receipt.
                // A current non-mutating execution-license provider and complete scope provider remain unresolved.
                if (!receipt.Value<bool>("completeEffectiveScope"))
                    throw new BridgeException("TestSuiteScopeIncomplete", "Retained public scope observations do not prove complete effective scope. No checkpoint or Run is admitted.");
                throw new BridgeException("TargetPrerequisiteMissing", "Current execution-license observation provider is unresolved; service/DLL presence is insufficient.");
            }

            public JArray Checkpoint()
            {
                baseline = Capture();
                var preservation = Save(Path.Combine(directory, "preservation.json"), baseline);
                string archive = Path.Combine(directory, "checkpoint.zap21");
                project.Archive(new DirectoryInfo(directory), Path.GetFileName(archive), ProjectArchivationMode.Compressed);
                // The record holds one checkpoint per planned resource, so one manifest pins the archive and its baseline.
                return new JArray(Save(Path.Combine(directory, "checkpoint.json"), new JObject { ["archive"] = Artifact(archive), ["preservation"] = preservation }));
            }
            private JToken Checkpointed(JToken operation, string name)
            {
                var checkpoints = (JArray)operation["checkpoints"];
                if (checkpoints.Count != 1) throw new BridgeException("CheckpointRequired", "One retained checkpoint manifest is required.");
                return StyleUiEvidence.Read(Resolve(checkpoints[0]))[name] ?? throw new BridgeException("ArtifactChanged", "The checkpoint manifest lacks its " + name + ".");
            }
            public JObject Run()
            {
                var definitions = (JArray)context["run"]["definitions"];
                var application = context["application"] as JObject;
                if (application == null && definitions.Count != 1) throw new BridgeException("TestSuiteProfileUnqualified", "Only a separately reviewed single-rule dispatch is implemented.");
                string raw = Path.Combine(directory, "raw-results.json");
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) owner.TsFault(context["operation"].Value<string>("id"), "beforeRun", directory);
#endif
                if (application?.Value<string>("executionMode") == "endpoint")
                {
                    // Reverify the ticket-owned instance and its controller address immediately before SetScope and the one Run.
                    string address = application.Value<string>("serverAddress");
                    owner.TestSuiteOwnedEndpoint(application.Value<string>("simulatorTicket"), address);
                    api.RunSystemOnce(address, application.Value<string>("route"), definitions, context["run"]["dispatchDefinitionIds"].Values<string>().ToArray(),
                        Path.Combine(directory, "dispatch-definitions"), Path.Combine(directory, "execution-return.json"), raw);
                }
                else if (application != null)
                {
                    // Reverify the ticket-owned instance immediately before SetScope and the one Run.
                    var simulator = owner.TestSuiteOwnedSimulator(application.Value<string>("simulatorTicket"));
                    api.RunApplicationOnce(owner.SelectPlc(selector).Software, simulator.Value<string>("instanceName"), application.Value<string>("executionMode"),
                        application.Value<string>("route"), definitions, context["run"]["dispatchDefinitionIds"].Values<string>().ToArray(),
                        Path.Combine(directory, "dispatch-definitions"), Path.Combine(directory, "execution-return.json"), raw);
                }
                else
                {
                    var definition = definitions[0];
                    api.RunOnce(definition.Value<string>("name"), definition.Value<string>("originalSha256"), definition.Value<string>("id"),
                        (JObject)definition["reference"], Path.Combine(directory, "dispatch-definition.xml"), Path.Combine(directory, "execution-return.json"), raw);
                }
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) owner.TsFault(context["operation"].Value<string>("id"), "afterRun", directory);
#endif
                return new JObject { ["rawResults"] = Artifact(raw) };
            }
            public JObject Verify()
            {
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) return VerifyQualification();
#endif
                var compile = ChangeCompilation(owner.SelectPlc(selector).Software.GetService<ICompilable>().Compile(), selector.Value<string>("plcPath"));
                var compileArtifact = Save(Path.Combine(directory, "compile.json"), compile);
                if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Complete successful compilation required.");
                var after = Capture(); Equal(baseline, after);
                var preservation = Save(Path.Combine(directory, "preservation-after.json"), after);
                project.Save(); string path = project.Path.FullName, beforeEpoch = owner._testSuiteEpoch;
                project.Close(); project = owner._portal.Projects.Open(new FileInfo(path));
                owner._scratchProject = project; owner._testSuiteEpoch = Guid.NewGuid().ToString("N");
                owner._watchEpoch = Guid.NewGuid().ToString("N"); owner._projectIndexes.Invalidate("testsuiteFreshReopen");
                var reopened = Capture(); Equal(baseline, reopened);
                Save(Path.Combine(directory, "fresh-reopen.json"), new JObject { ["beforeEpoch"] = beforeEpoch,
                    ["afterEpoch"] = owner._testSuiteEpoch, ["projectPath"] = path, ["manifest"] = reopened,
                    ["preservationArtifact"] = preservation, ["compileArtifact"] = compileArtifact });
                // The retained lifecycle material is never a fabricated passing scope/cleanup result.
                // Qualification must supply independent scope and exact native cleanup dispositions.
                throw new BridgeException("VerificationIncomplete", "Fresh reopen material retained; complete scope and qualified cleanup evidence remain unresolved.");
            }
            public JObject Restore(JArray destinations)
            {
                string archivePath = Resolve(Checkpointed(context["operation"], "archive")), destination = destinations[0].Value<string>("recoveryLocation");
                if (!ProjectPathIsAbsolute(destination) || File.Exists(destination) || Directory.Exists(destination) || owner._portal.Projects.Count != 0)
                    throw new BridgeException("InvalidRecovery", "An exact absent reserved destination and empty owned instance are required.");
                Directory.CreateDirectory(destination);
                project = owner._portal.Projects.Retrieve(new FileInfo(archivePath), new DirectoryInfo(destination));
                var returned = Save(Path.Combine(directory, "restore-return.json"), new JObject { ["projectPath"] = project.Path.FullName,
                    ["destination"] = destination, ["processId"] = owner._processId, ["automaticReplay"] = false,
                    ["restorationVerified"] = false });
                project.Close(); project = null;
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) owner.TsFault(context["operation"].Value<string>("id"), "afterRestore", directory);
#endif
                return returned;
            }
            public JObject Reconcile(JObject operation)
            {
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) return ReconcileQualification(operation);
#endif
                // Obtain independent observations of each admitted location. A successful Retrieve or a
                // matching original alone cannot produce dispositions for a recovered copy.
                var captures = new JArray();
                var paths = new[] { selector.Value<string>("projectPath") }.Concat(((JArray)operation["recoveryDestinations"])
                    .Select(d => d.Value<string>("recoveryLocation"))).ToArray();
                foreach (string path in paths)
                {
                    if (!ProjectPathIsAbsolute(path)) throw new BridgeException("InvalidRecovery", "Exact absolute reconciliation location required.");
                    if (File.Exists(path) && Path.GetExtension(path).Equals(".ap21", StringComparison.OrdinalIgnoreCase))
                    {
                        project = owner._portal.Projects.Open(new FileInfo(path)); owner._testSuiteEpoch = Guid.NewGuid().ToString("N");
                        selector["projectPath"] = project.Path.FullName;
                        try { captures.Add(new JObject { ["location"] = path, ["sessionEpoch"] = owner._testSuiteEpoch, ["manifest"] = Capture() }); }
                        finally { project.Close(); }
                    }
                    else captures.Add(new JObject { ["location"] = path, ["exists"] = File.Exists(path) || Directory.Exists(path), ["disposition"] = "unverified" });
                }
                Save(Path.Combine(directory, "reconciliation-observations.json"), new JObject { ["locations"] = captures });
                throw new BridgeException("ReconciliationIncomplete", "Destination project resolution and full preservation/cleanup dispositions must be qualified; retained observations do not release reservations.");
            }
            private JObject Capture()
            {
#if TESTSUITE_QUALIFICATION
                if (owner._testSuiteQualification) return owner.TsCapture(project, selector, NewDirectory("capture"), Application);
#endif
                var plc = owner.SelectPlc(selector);
                if (project == null || plc.Project.Path.FullName != project.Path.FullName) throw new BridgeException("IdentityMismatch", "Exact preservation project required.");
                owner.EnsureProjectOffline(project);
                var budget = new ScanBudget(10000);
                var softwareOwners = DeviceItems(project, budget).Select(d => d.Item.GetService<SoftwareContainer>()?.Software).Where(s => s != null).ToArray();
                if (softwareOwners.Length != 1 || !softwareOwners[0].Equals(plc.Software) || budget.Truncated)
                    throw new BridgeException("TestSuiteProfileUnqualified", "Preservation is bounded to one PLC; other software owners remain unqualified.");
                var snapshot = owner.WatchSnapshot(selector);
                return new JObject { ["peers"] = snapshot["peers"].DeepClone(),
                    ["tables"] = new JArray(((JArray)snapshot["tables"]).Select(t => new JObject { ["kind"] = t["kind"], ["group"] = t["group"], ["name"] = t["name"], ["sha256"] = t["semanticSha256"] })),
                    ["definitions"] = StylePrerequisiteProbe.CaptureDefinitions(project, NewDirectory("definitions")),
                    ["coverage"] = "Exposed single-PLC engineering exports and Test Suite definitions; not yet a qualified complete project preservation profile." };
            }
            private static void Equal(JObject before, JObject after)
            { if (!StyleUiEvidence.SameContent(before, after)) throw new BridgeException("PreservationFailed", "Native project preservation manifests differ."); }
            private string NewDirectory(string name)
            { string value = Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(value); return value; }
            private JObject Artifact(string path) => new JObject { ["relativePath"] = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'), ["sha256"] = ChangeFileHash(path) };
            private JObject Save(string path, JObject value)
            { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { byte[] bytes = Encoding.UTF8.GetBytes(value.ToString(Formatting.None)); file.Write(bytes, 0, bytes.Length); file.Flush(true); } return Artifact(path); }
            private string Resolve(JToken artifact)
            {
                string path = Path.GetFullPath(Path.Combine(root, artifact.Value<string>("relativePath")));
                if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || ChangeFileHash(path) != artifact.Value<string>("sha256"))
                    throw new BridgeException("ArtifactChanged", "Exact retained checkpoint under the artifact root required.");
                return path;
            }
        }
    }
}
