using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // The explicitly launched qualification route reaches this factory; production
        // library_integrate uses CreatePlcComponentProductionCoordinator behind its own Host flag.
        private PlcComponentCoordinator CreatePlcComponentQualificationCoordinator(string planChangeId, string previewHash, string root)
        {
            EnsureAttached();
            if (!_libraryChanges.TryGetValue(planChangeId, out var plan) || plan.Kind != "library_integrate" || plan.PreviewHash != previewHash || IsHmiLibraryTarget(plan.Payload) || PlcHmiIntegrationPlanner.IsRecipe(plan.Payload))
                throw new BridgeException("InvalidComponentContract", "An exact current-session PLC integration preview is required.");
            if (!_ownsPortal || _scratchProject == null || !plan.Project.Equals(_scratchProject) || _portal.Projects.Count != 1)
                throw new BridgeException("OwnedScratchRequired", "Component coordinator qualification is limited to the worker's exact owned scratch project.");
            if (_componentWindowManifest == null || !_componentWindowInstallAllowed || _componentWindowDispatchStep != "start" || !_sharedMutationDispatched)
                throw new BridgeException("MutationScopeIncomplete", "The complete durable component window must be bound before preparation.");
            string workspace = _componentWindowManifest.Value<string>("workspace");
            var operations = new NativePlcComponentOperations(this, plan, workspace, false);
            return PlcComponentCoordinator.Create(root, LibraryChangeView(plan), operations, _componentWindowManifest.Value<string>("componentId"), workspace);
        }

        // Host-gated production route: the same coordinator and adapters on the plan's own (not owned-scratch) project.
        private PlcComponentCoordinator CreatePlcComponentProductionCoordinator(LibraryChange plan, string workspace) =>
            PlcComponentCoordinator.Create(workspace, LibraryChangeView(plan), new NativePlcComponentOperations(this, plan, workspace, true));

        private sealed class NativePlcComponentOperations : IPlcComponentOperations
        {
            private readonly OpennessSession _session;
            private readonly LibraryChange _plan;
            private readonly string _changes, _workspace;
            private readonly bool _production;
            internal NativePlcComponentOperations(OpennessSession session, LibraryChange plan, string workspace, bool production)
            { _session = session; _plan = plan; _workspace = workspace; _changes = Path.Combine(workspace, "changes"); _production = production; }
            public string SessionToken => _session._libraryGeneration;

            public JObject Prepare(JObject arguments)
            {
                CheckProject();
                arguments = (JObject)arguments.DeepClone(); arguments["artifactDirectory"] = _changes;
                string kind = arguments.Value<string>("kind");
                if (kind == "library_instantiate")
                {
                    if (_session.LibraryChangeFingerprint(_plan) != _plan.Preview.Value<string>("fingerprint")) throw new BridgeException("PreviewStale", "Source or target changed after the component plan; no native mutation was dispatched.");
                    return _session.LibraryChangePrepare(arguments, (JArray)_plan.Preview["integrationPlan"]["versions"]);
                }
                // The natively captured storage profile applies only to a plan whose installed FB/UDT content equals the captured one.
                if (kind == "graphical_typed_edit") return _session.ChangePrepare(arguments, _session.CapturedStorageContext(_plan, _production));
                throw new BridgeException("UnsupportedComponentStep", "Coordinator can prepare only library_instantiate and graphical_typed_edit.");
            }

            public JObject Apply(JObject arguments)
            {
                CheckProject();
                string id = arguments.Value<string>("changeId");
                if (id?.StartsWith("lib-", StringComparison.Ordinal) == true)
                {
                    if (!_session._libraryChanges.TryGetValue(id, out var library) || library.Kind != "library_instantiate" || !library.Project.Equals(_plan.Project))
                        throw new BridgeException("UnsupportedComponentStep", "Only the exact prepared import in this project is eligible.");
                    return _session.LibraryChangeApply(arguments);
                }
                if (!_session._changes.TryGetValue(id ?? "", out var graphical) || graphical.Kind != "graphical_typed_edit" || graphical.ProjectPath != _plan.Project.Path.FullName)
                    throw new BridgeException("UnsupportedComponentStep", "Only the exact freshly prepared typed change in this project is eligible.");
                var result = _session.ChangeApply(arguments);
                result["componentIdempotencyKeyHash"] = ChangeHash(graphical.IdempotencyKey);
                return result;
            }

            public JObject Status(string changeId)
            {
                var args = new JObject { ["changeId"] = changeId, ["artifactDirectory"] = _changes };
                if (changeId.StartsWith("lib-", StringComparison.Ordinal)) return _session.LibraryChangeStatus(args);
                var result = _session.ChangeStatus(args);
                if (_session._changes.TryGetValue(changeId, out var change) && change.IdempotencyKey != null) result["componentIdempotencyKeyHash"] = ChangeHash(change.IdempotencyKey);
                return result;
            }

            public JObject VerifyImport(JObject contract) => Capture(contract, null);
            public JObject VerifyInstallation(JObject contract, JObject typedPreparation) => Capture(contract, typedPreparation);

            private void CheckProject()
            {
                _session.EnsureAttached();
                if (!_production && (!_session._ownsPortal || _session._scratchProject == null || !_session._scratchProject.Equals(_plan.Project) || _session._portal.Projects.Count != 1))
                    throw new BridgeException("OwnedScratchRequired", "The exact qualification scratch project is no longer exclusively selected.");
                _session.EnsureProjectOffline(_plan.Project);
            }

            private JObject Capture(JObject contract, JObject typedPreparation)
            {
                CheckProject();
                if (contract.Value<string>("projectPath") != _plan.Project.Path.FullName || contract.Value<string>("planChangeId") != _plan.Id || contract.Value<string>("planPreviewHash") != _plan.PreviewHash)
                    throw new BridgeException("ComponentReceiptMismatch", "Readback contract differs from the exact native plan.");
                var scope = _session.LibraryPlcScope(_plan, out var plc); var budget = new ScanBudget(50000);
                if (scope.Unit != null) throw new BridgeException("UnsupportedIntegrationScope", "Component readback requires the standard PLC scope.");
                string directory = Path.Combine(_workspace, "captures", "component-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
                var result = new JObject { ["projectPath"] = _plan.Project.Path.FullName, ["plcPath"] = plc.Path };
                using (_session.SharedMutationExclusive("TIA MCP component qualification readback"))
                {
                    var blocks = ScopeBlocks(scope, budget).ToArray(); var types = ScopeTypes(scope, budget).ToArray();
                    var paths = new Dictionary<IEngineeringObject, string>();
                    foreach (var b in blocks) paths[b.Item] = b.Path;
                    foreach (var t in types) paths[t.Item] = t.Path;
                    var tags = new JArray();
                    foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                        foreach (var tag in table.Item.Tags)
                        {
                            if (!budget.Take()) break;
                            tags.Add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["tablePath"] = table.Path,
                                ["name"] = tag.Name, ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety });
                        }
                    if (budget.Truncated) throw new BridgeException("IntegrationInventoryIncomplete", "Readback requires complete native blocks/types/tags.");
                    var versions = new JArray(); result["versions"] = versions;
                    foreach (JObject pin in (JArray)contract["plan"]["versions"])
                    {
                        var source = ResolveLibraryVersion(_plan.Source, pin);
                        var installed = _plan.Project.ProjectLibrary.FindVersion(Guid.Parse(pin.Value<string>("versionGuid")));
                        if (installed == null || installed.TypeObject.Guid != Guid.Parse(pin.Value<string>("typeGuid"))) throw new BridgeException("ComponentReadbackMismatch", "An exact dependency version is absent from the project library.");
                        var native = LibraryVersionInfo(installed);
                        if (native.Value<string>("state") != "Committed" || native.Value<bool>("doNotUse")) throw new BridgeException("DependencyConflict", "Installed dependency is not committed and usable.");
                        string installedFile = Path.Combine(directory, installed.Guid.ToString("N") + "-installed.xml"), sourceFile = Path.Combine(directory, source.Guid.ToString("N") + "-source.xml");
                        installed.Export(new FileInfo(installedFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        source.Export(new FileInfo(sourceFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        var uses = installed.FindInstances(plc.Software).Take(1001).ToArray();
                        if (uses.Length == 0 || uses.Length > 1000 || uses.Any(u => u.LibraryTypeVersion.Guid != installed.Guid || !(u.LibraryTypeInstance is IEngineeringObject item) || !paths.ContainsKey(item)))
                            throw new BridgeException("ComponentReadbackMismatch", "Exact dependency uses cannot be resolved completely within the selected PLC.");
                        var observation = new JObject { ["typeGuid"] = installed.TypeObject.Guid.ToString("D"), ["versionGuid"] = installed.Guid.ToString("D"),
                            ["contentSha256"] = ChangeFileHash(installedFile), ["sourceContentSha256"] = ChangeFileHash(sourceFile),
                            ["objectPaths"] = new JArray(uses.Select(u => paths[(IEngineeringObject)u.LibraryTypeInstance]).OrderBy(p => p, StringComparer.Ordinal)),
                            ["artifact"] = ChangeArtifact(installedFile), ["sourceArtifact"] = ChangeArtifact(sourceFile) };
                        versions.Add(observation);
                        if (pin["contentPolicy"] != null)
                        {
                            string sourceOracle = Path.Combine(directory, source.Guid.ToString("N") + "-source-oracle.xml");
                            string installedOracle = Path.Combine(directory, installed.Guid.ToString("N") + "-installed-oracle.xml");
                            source.Export(new FileInfo(sourceOracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                            installed.Export(new FileInfo(installedOracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                            observation["contentPolicyId"] = pin["contentPolicy"].Value<string>("policyId");
                            observation["sourceOracleSha256"] = ChangeFileHash(sourceOracle); observation["sourceOracleArtifact"] = ChangeArtifact(sourceOracle);
                            observation["installedOracleSha256"] = ChangeFileHash(installedOracle); observation["installedOracleArtifact"] = ChangeArtifact(installedOracle);
                            if (uses.Length != 1 || !(uses[0].LibraryTypeInstance is FB fb))
                                observation["nativeFbSettings"] = new JObject { ["failure"] = "Policy requires exactly one linked native FB instance." };
                            else observation["nativeFbSettings"] = new JObject {
                                ["AutoNumber"] = CaptureSetting(() => fb.AutoNumber), ["MemoryLayout"] = CaptureSetting(() => fb.MemoryLayout),
                                ["IsIECCheckEnabled"] = CaptureSetting(() => {
                                    var info = ((IEngineeringObject)fb).GetAttributeInfos().SingleOrDefault(a => a.Name == "IsIECCheckEnabled");
                                    if (info == null || (info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) throw new InvalidOperationException("IEC check getter is not advertised readable.");
                                    return ((IEngineeringObject)fb).GetAttribute("IsIECCheckEnabled"); }),
                                ["CreationDate"] = CaptureSetting(() => fb.CreationDate) };
                        }
                    }
                    var mappings = new JArray(); result["mappings"] = mappings;
                    foreach (JObject mapping in (JArray)contract["plan"]["mappings"])
                    {
                        var matches = tags.Where(t => t.Value<string>("path") == mapping.Value<string>("tagPath")).ToArray();
                        if (matches.Length != 1 || tags.Count(t => string.Equals(t.Value<string>("name"), matches[0].Value<string>("name"), StringComparison.OrdinalIgnoreCase)) != 1)
                            throw new BridgeException("ComponentReadbackMismatch", "Mapped native tags are missing or symbolically ambiguous.");
                        var row = (JObject)mapping.DeepClone(); row["nativeTag"] = matches[0].DeepClone(); mappings.Add(row);
                    }
                    var payload = (JObject)contract["payload"];
                    bool standalone = PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(payload);
                    string callerPath = PlcLibraryIntegrationPlanner.CallerPath(payload);
                    var callers = blocks.Where(b => b.Path == callerPath).ToArray();
                    result["callerPath"] = callerPath;
                    result["callerExists"] = callers.Length != 0;
                    if (standalone && typedPreparation == null)
                    {
                        string name = payload["integration"]["caller"].Value<string>("name");
                        if (blocks.Any(b => string.Equals(b.Item.Name, name, StringComparison.OrdinalIgnoreCase)) || types.Any(t => string.Equals(t.Item.Name, name, StringComparison.OrdinalIgnoreCase)))
                            throw new BridgeException("ComponentReadbackMismatch", "Standalone caller name is no longer absent from the complete native scope.");
                    }
                    else
                    {
                    if (callers.Length != 1) throw new BridgeException("ComponentReadbackMismatch", "Exact caller disappeared.");
                    EnsureEditableBlock(callers[0].Item, false);
                    result["caller"] = new JObject { ["kind"] = BlockNumberKind(callers[0].Item), ["language"] = callers[0].Item.ProgrammingLanguage.ToString(), ["number"] = callers[0].Item.Number };
                    string callerFile = Path.Combine(directory, "caller.xml"); callers[0].Item.Export(new FileInfo(callerFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    result["callerSha256"] = ChangeFileHash(callerFile); result["callerArtifact"] = ChangeArtifact(callerFile);
                    if (typedPreparation != null)
                    {
                        var prepared = typedPreparation["preview"];
                        CompareArtifact((JObject)prepared["typed"]["nativeArtifact"], callerFile);
                        if (!standalone)
                        {
                            var original = (JObject)contract["plan"]["callerArtifact"];
                            result["callerInsertion"] = PlcComponentCallerInsertion.Verify(
                                EngineeringSources.ReadXml(PlcComponentContentContract.Artifact(original, original.Value<string>("sha256"))),
                                EngineeringSources.ReadXml(File.ReadAllText(callerFile)), payload["integration"]["caller"].Value<string>("insertAfterNetworkId"));
                        }
                        var effects = ((JArray)prepared["effects"]).Where(e => e.Value<string>("action") == "createInstanceDB").ToArray();
                        if (effects.Length != 1) throw new BridgeException("ComponentReadbackMismatch", "Expected exactly one prepared instance creation.");
                        var effect = effects[0];
                        var instances = blocks.Where(b => b.Path == effect.Value<string>("path")).ToArray();
                        if (instances.Length != 1 || !(instances[0].Item is InstanceDB instance) || instance.Number != effect.Value<int>("number") || instance.InstanceOfName != effect.Value<string>("instanceOfName"))
                            throw new BridgeException("ComponentReadbackMismatch", "Exact instance path/type/number differs from the prepared change.");
                        string instanceFile = Path.Combine(directory, "instance.xml"); instance.Export(new FileInfo(instanceFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                        CompareArtifact((JObject)effect["artifact"], instanceFile);
                        result["instance"] = new JObject { ["path"] = instances[0].Path, ["number"] = instance.Number, ["instanceOfName"] = instance.InstanceOfName, ["contentSha256"] = ChangeFileHash(instanceFile), ["artifact"] = ChangeArtifact(instanceFile) };
                        result["callerMatchesPrepared"] = true; result["instanceMatchesPrepared"] = true;
                    }
                    }
                    result["scopeComplete"] = true;
                    result["coverage"] = "Pinned raw source/dependency exports and native use links; an explicit FB-only full source-oracle contract may prove exactly three native property materializations while retaining all four raw hashes and all other XML. Exact mapped tags/caller/instance readback; unrelated-content preservation, reopen, recovery and runtime require independent qualification.";
                }
                return result;
            }

            private static void CompareArtifact(JObject expected, string actual)
            {
                string path = expected?.Value<string>("path");
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || ChangeFileHash(path) != expected.Value<string>("sha256")) throw new BridgeException("ArtifactChanged", "Prepared native artifact is missing or changed.");
                EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(File.ReadAllText(path)), EngineeringSources.ReadXml(File.ReadAllText(actual)));
            }

            private static JObject CaptureSetting(Func<object> getter)
            {
                try
                {
                    var value = getter(); var row = new JObject { ["type"] = value?.GetType().FullName };
                    if (value is DateTime date)
                    {
                        row["value"] = date.ToString("O", CultureInfo.InvariantCulture); row["ticks"] = date.Ticks; row["kind"] = date.Kind.ToString();
                    }
                    else row["value"] = value is bool boolean ? new JValue(boolean) : new JValue(value?.ToString());
                    return row;
                }
                catch (Exception error) { return new JObject { ["failure"] = error.Message, ["errorType"] = error.GetType().FullName }; }
            }
        }
    }
}
