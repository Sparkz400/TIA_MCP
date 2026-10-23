#if TESTSUITE_QUALIFICATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using TiaMcp.TestSuite.V21;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed partial class NativeStyleBoundary
        {
            private QualificationLease qualificationLease;
            private string Rule => context["run"]["definitions"][0].Value<string>("name");
            private sealed class QualificationLease : IDisposable
            {
                private readonly OpennessSession owner;
                private IDisposable held;
                private bool disposed;
                internal QualificationLease(OpennessSession owner, IDisposable held) { this.owner = owner; this.held = held; }
                internal void ObserveUi(Action observation)
                {
                    // The durable reservation and worker-session guard remain held. UI inspection
                    // needs TIA's mutex released; immediately recapture everything after reacquiring it.
                    var previous = held; held = null; previous.Dispose();
                    try { observation(); }
                    finally { if (!disposed) held = owner._portal.ExclusiveAccess("TIA MCP: verify Test Suite observation"); }
                }
                public void Dispose() { if (disposed) return; disposed = true; var previous = held; held = null; previous?.Dispose(); }
            }
            private JObject ObserveQualification()
            {
                var manifest = Capture(); owner.TsRequireUi(project, Rule, manifest);
                if (owner._testSuiteUiProof["challenge"].Value<bool?>("requireLicenseCheck") != true)
                    throw new BridgeException("TargetPrerequisiteMissing", "Execution admission requires a fresh license observation, not a reopen-only proof.");
                string output = NewDirectory("observation");
                var target = owner.TsTarget(project, selector, manifest);
                var probes = owner.TsProbes(root, output, target, context["operation"]["plan"]["resources"][0].Value<string>("resourceKey"));
                var definition = owner.TsDefinition(project, selector, Rule, manifest, target, output);
                var scope = QualificationScope(definition);
                var evidence = ScopeArtifact(scope, output);
                Save(Path.Combine(output, "observation-source.json"), new JObject { ["uiEvidence"] = TsArtifact(root, owner._testSuiteUiProofPath),
                    ["manifest"] = manifest, ["definition"] = definition, ["target"] = target });
                return new JObject { ["target"] = target, ["originalDefinitions"] = new JObject {
                    [definition.Value<string>("id")] = Convert.ToBase64String(File.ReadAllBytes(Resolve(definition["originalArtifact"]))) },
                    ["scope"] = scope, ["scopeEvidenceKind"] = "nativeReadback", ["scopeEvidence"] = evidence, ["probeEvidence"] = probes };
            }
            private JArray QualificationScope(JObject definition) => new JArray(new JObject {
                ["definitionId"] = definition["id"], ["definitionReference"] = definition["reference"].DeepClone(),
                ["effectiveScope"] = definition["requiredScope"].DeepClone(), ["ignoredScope"] = new JArray(), ["observationComplete"] = true });
            private JObject ScopeArtifact(JArray scope, string output) => Save(Path.Combine(output, "scope.json"), new JObject {
                ["planSha256"] = context["run"]["planSha256"], ["evidenceKind"] = "nativeReadback", ["observations"] = scope.DeepClone() });
            private JObject VerifyQualification()
            {
                var compile = ChangeCompilation(owner.SelectPlc(selector).Software.GetService<ICompilable>().Compile(), selector.Value<string>("plcPath"));
                var compileArtifact = Save(Path.Combine(directory, "compile.json"), compile);
                if (!CompilationEvidenceRules.IsSuccessfulTarget(compile)) throw new BridgeException("CompileFailed", "Successful complete fixture compilation required.");
                project.Save(); var after = Capture(); Equal(baseline, after);
                var preservation = Save(Path.Combine(directory, "preservation-after.json"), after);
                string path = project.Path.FullName, beforeEpoch = owner._testSuiteEpoch;
                project.Close(); project = owner._portal.Projects.Open(new FileInfo(path)); owner._scratchProject = project;
                owner._testSuiteEpoch = Guid.NewGuid().ToString("N"); owner._watchEpoch = Guid.NewGuid().ToString("N");
                owner._projectIndexes.Invalidate("testsuiteFreshReopen");
                var reopened = Capture(); Equal(baseline, reopened);
                qualificationLease.ObserveUi(() => owner.TsObserveUi(project, selector, root, Rule, reopened, "reopen", false));
                var locked = Capture(); Equal(baseline, locked); owner.TsRequireUi(project, Rule, locked);
                string output = NewDirectory("reopened-scope"); var target = owner.TsTarget(project, selector, locked);
                var scope = QualificationScope(owner.TsDefinition(project, selector, Rule, locked, target, output));
                var scopeArtifact = ScopeArtifact(scope, output);
                var reopenArtifact = Save(Path.Combine(directory, "fresh-reopen.json"), new JObject { ["beforeEpoch"] = beforeEpoch,
                    ["afterEpoch"] = owner._testSuiteEpoch, ["projectPath"] = path, ["manifest"] = locked,
                    ["uiEvidence"] = TsArtifact(root, owner._testSuiteUiProofPath) });
                project.Close(); project = null; owner._scratchProject = null;
                if (owner._portal.Projects.Count != 0) throw new BridgeException("NativeCleanupUnverified", "Fixture project did not close.");
                var cleanup = Save(Path.Combine(directory, "project-cleanup.json"), new JObject { ["processId"] = owner._processId,
                    ["projectPath"] = path, ["openProjects"] = owner._portal.Projects.Count,
                    ["nativeLeaseReleaseRequiredBeforeOutcome"] = true });
                JObject Gate(string name, JObject artifact) => new JObject { ["gate"] = name, ["artifact"] = artifact };
                return new JObject { ["scope"] = scope, ["scopeEvidenceKind"] = "nativeReadback", ["scopeEvidence"] = scopeArtifact,
                    ["gates"] = new JArray(Gate("project.compile", compileArtifact), Gate("project.preservation", preservation),
                        Gate("project.freshReopen", reopenArtifact), Gate("resource.cleanup", cleanup)) };
            }
            private JObject ReconcileQualification(JObject operation)
            {
                var preservation = ((JArray)operation["checkpoints"]).SingleOrDefault(c => c.Value<string>("relativePath").EndsWith("/preservation.json", StringComparison.Ordinal));
                if (preservation == null) throw new BridgeException("ReconciliationIncomplete", "Independent checkpoint preservation evidence is required.");
                baseline = StyleUiEvidence.Read(Resolve(preservation));
                string original = context["operation"]["plan"]["target"].Value<string>("projectFilePath");
                string resource = context["operation"]["plan"]["resources"][0].Value<string>("resourceKey");
                var originalEvidence = ReconcileProject(original, "original");
                var destinations = new JArray();
                foreach (var destination in (JArray)operation["recoveryDestinations"])
                {
                    string location = destination.Value<string>("recoveryLocation");
                    JObject evidence; string disposition;
                    if (!File.Exists(location) && !Directory.Exists(location))
                    {
                        disposition = "verifiedAbsent";
                        evidence = Save(Path.Combine(NewDirectory("absent-destination"), "absence.json"), new JObject {
                            ["location"] = location, ["fileExists"] = false, ["directoryExists"] = false, ["observedUtc"] = DateTime.UtcNow.ToString("o") });
                    }
                    else
                    {
                        string recovered = ResolveRecoveredProject(location);
                        evidence = ReconcileProject(recovered, "recovered"); disposition = "verifiedRestored";
                    }
                    destinations.Add(new JObject { ["resourceKey"] = resource, ["recoveryLocation"] = location,
                        ["disposition"] = disposition, ["evidence"] = evidence });
                }
                return new JObject { ["resources"] = new JArray(new JObject { ["resourceKey"] = resource, ["disposition"] = "unchanged",
                    ["recoveryLocation"] = null, ["evidence"] = originalEvidence }), ["destinations"] = destinations };
            }
            private JObject ReconcileProject(string path, string phase)
            {
                if (!ProjectPathIsAbsolute(path) || !File.Exists(path)) throw new BridgeException("ReconciliationIncomplete", "Exact existing project required.");
                project = owner._portal.Projects.Open(new FileInfo(path)); selector["projectPath"] = project.Path.FullName;
                owner._testSuiteEpoch = Guid.NewGuid().ToString("N"); owner._watchEpoch = Guid.NewGuid().ToString("N");
                JObject manifest; string epoch = owner._testSuiteEpoch, proof;
                try
                {
                    manifest = Capture(); Equal(baseline, manifest);
                    qualificationLease.ObserveUi(() => owner.TsObserveUi(project, selector, root, Rule, manifest, "reconcile-" + phase, false));
                    var locked = Capture(); Equal(baseline, locked); owner.TsRequireUi(project, Rule, locked); proof = owner._testSuiteUiProofPath;
                }
                finally { project.Close(); project = null; owner._scratchProject = null; }
                if (owner._portal.Projects.Count != 0) throw new BridgeException("NativeCleanupUnverified", "Reconciliation project did not close.");
                return Save(Path.Combine(NewDirectory("reconciled-" + phase), "receipt.json"), new JObject { ["path"] = path,
                    ["manifest"] = manifest, ["epoch"] = epoch, ["uiEvidence"] = TsArtifact(root, proof), ["openProjectsAfter"] = 0, ["automaticReplay"] = false });
            }
            private static string ResolveRecoveredProject(string location)
            {
                if (!ProjectPathIsAbsolute(location) || !Directory.Exists(location)) throw new BridgeException("ReconciliationIncomplete", "Recovery directory is missing.");
                var pending = new Queue<string>(); pending.Enqueue(location); var found = new List<string>(); int count = 0;
                while (pending.Count != 0)
                {
                    string directory = pending.Dequeue();
                    if (++count > 1000 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new BridgeException("ReconciliationIncomplete", "Recovery location exceeds bounds or contains a link.");
                    foreach (string file in Directory.EnumerateFiles(directory, "*.ap21"))
                    { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("InvalidRecovery", "Linked recovery project refused."); found.Add(file); }
                    foreach (string child in Directory.EnumerateDirectories(directory)) pending.Enqueue(child);
                }
                if (found.Count != 1) throw new BridgeException("ReconciliationIncomplete", "Exactly one independently resolved recovered project required.");
                return found[0];
            }
        }
    }
}
#endif
