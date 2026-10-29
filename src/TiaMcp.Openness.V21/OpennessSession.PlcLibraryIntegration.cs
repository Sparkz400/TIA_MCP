using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private void PreparePlcLibraryIntegrationPlan(LibraryChange change)
        {
            try
            {
                PlcLibraryIntegrationPlanner.ValidatePayload(change.Payload);
                if (change.Versions.Count != 1 || change.Masters.Count != 0 || !(change.Versions[0] is CodeBlockLibraryTypeVersion))
                    throw new BridgeException("UnsupportedIntegrationType", "Select one exact native FB type/version. Master copies and HMI are not PLC recipes.");
                var scope = LibraryPlcScope(change, out var plc);
                var budget = new ScanBudget(50000);
                var blocks = ScopeBlocks(scope, budget).ToArray();
                var types = ScopeTypes(scope, budget).ToArray();
                // An installed same-name root is reused only with the exact-version proofs below; otherwise the import guards apply.
                string rootName = change.Versions[0].TypeObject.Name;
                bool rootInstalled = blocks.Any(b => string.Equals(b.Item.Name, rootName, StringComparison.OrdinalIgnoreCase)) || types.Any(t => string.Equals(t.Item.Name, rootName, StringComparison.OrdinalIgnoreCase));
                if (!rootInstalled) PreparePlcLibraryInstantiation(change); // Preserve existing collision, family and completeness guards.
                var tags = new JArray();
                foreach (var table in TagTables(scope.Tags, scope.Path + "/tags", budget))
                    foreach (var tag in table.Item.Tags)
                    {
                        if (!budget.Take()) break;
                        tags.Add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["tablePath"] = table.Path,
                            ["name"] = tag.Name, ["dataType"] = tag.DataTypeName, ["logicalAddress"] = tag.LogicalAddress, ["isSafety"] = tag.IsSafety });
                    }
                if (budget.Truncated) throw new BridgeException("IntegrationInventoryIncomplete", "Complete native inventory exceeded 50000 objects.");
                bool standalone = PlcLibraryIntegrationPlanner.CreatesStandaloneCaller(change.Payload);
                string callerPath = PlcLibraryIntegrationPlanner.CallerPath(change.Payload);
                var matches = blocks.Where(b => b.Path == callerPath).ToArray();
                JObject callerArtifact = null;
                if (!standalone)
                {
                    if (matches.Length != 1) throw new BridgeException("IntegrationCallerMismatch", "Exact caller must resolve within the selected standard PLC scope.");
                    EnsureEditableBlock(matches[0].Item, false);
                    string callerFile = Path.Combine(change.Directory, "integration-caller.xml");
                    matches[0].Item.Export(new FileInfo(callerFile), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    callerArtifact = ChangeArtifact(callerFile);
                }

                // Capture every dependency's actual bytes; GUID metadata alone is not a content proof.
                var nativeVersions = new List<LibraryTypeVersion>(); var seen = new HashSet<Guid>();
                var dependencyGuids = new Dictionary<Guid, string[]>();
                var queue = new Queue<LibraryTypeVersion>(change.Versions);
                while (queue.Count != 0)
                {
                    var version = queue.Dequeue(); if (!seen.Add(version.Guid)) continue;
                    if (seen.Count > 1000) throw new BridgeException("DependencyUnresolved", "Integration dependency closure exceeds 1000 versions.");
                    if (!(version is CodeBlockLibraryTypeVersion) && !(version is PlcTypeLibraryTypeVersion)) throw new BridgeException("TargetIncompatible", "Integration dependencies must be native PLC block or data-type versions.");
                    if (version.State != LibraryTypeVersionState.Committed || version.TypeObject.DoNotUse) throw new BridgeException("DependencyConflict", "Integration requires committed usable native dependencies.");
                    nativeVersions.Add(version);
                    var dependencies = LibraryDependencyCapture.Read(version);
                    dependencyGuids.Add(version.Guid, dependencies.VersionGuids);
                    foreach (var dependency in dependencies.Versions) queue.Enqueue(dependency);
                }
                var versions = new JArray();
                foreach (var version in nativeVersions)
                {
                    var path = Path.Combine(change.Directory, "integration-version-" + version.Guid.ToString("N") + ".xml");
                    version.Export(new FileInfo(path), ExportOptions.WithDefaults, DocumentInfoOptions.None);
                    var row = LibraryVersionInfo(version); row["requiredVersionGuids"] = new JArray(dependencyGuids[version.Guid]);
                    var artifact = ChangeArtifact(path);
                    row["contentSha256"] = artifact["sha256"].DeepClone(); row["artifact"] = artifact;
                    if (version is CodeBlockLibraryTypeVersion && change.Source.Global != null)
                    {
                        string sourceXml = PlcComponentContentContract.Artifact(artifact, row.Value<string>("contentSha256"));
                        if (PlcComponentContentContract.HasOmittedFbProperties(sourceXml))
                        {
                            string oracle = Path.Combine(change.Directory, "integration-oracle-" + version.Guid.ToString("N") + ".xml");
                            // The same exact native version object emits both separately pinned documents.
                            version.Export(new FileInfo(oracle), ExportOptions.WithDefaults | ExportOptions.WithReadOnly, DocumentInfoOptions.None);
                            var oracleArtifact = ChangeArtifact(oracle);
                            string oracleXml = PlcComponentContentContract.Artifact(oracleArtifact, oracleArtifact.Value<string>("sha256"));
                            row["contentPolicy"] = PlcComponentContentContract.CreatePolicy(row, oracleArtifact, sourceXml, oracleXml, change.Source.Global.Name);
                        }
                    }
                    LibraryDependencyCapture.RequireUnchanged(version, dependencyGuids[version.Guid]);
                    versions.Add(row);
                }
                var reused = (JArray)change.Preview["reusedDependencies"];
                if (rootInstalled)
                {
                    reused = new JArray();
                    foreach (var version in nativeVersions)
                    {
                        var node = new JObject { ["typeName"] = version.TypeObject.Name, ["typeGuid"] = version.TypeObject.Guid.ToString("D") };
                        RequireNoInstanceInOtherScope(change, plc, scope, node);
                        reused.Add(ProveInstalledLibraryDependency(change, plc, node, version));
                    }
                }
                string dbName = change.Payload["integration"]["instanceDb"]?.Value<string>("name");
                var inventory = new JObject { ["complete"] = true, ["objects"] = new JArray(blocks.Select(b =>
                {
                    var row = new JObject { ["name"] = b.Item.Name, ["path"] = b.Path, ["kind"] = BlockNumberKind(b.Item), ["number"] = b.Item.Number,
                        ["language"] = b.Item.ProgrammingLanguage.ToString(), ["protected"] = b.Item.IsKnowHowProtected, ["namespace"] = b.Item.Namespace };
                    if (b.Item is InstanceDB instance)
                    {
                        row["instanceOfName"] = instance.InstanceOfName;
                        if (string.Equals(b.Item.Name, dbName, StringComparison.OrdinalIgnoreCase)) row["users"] = PlcIntegrationInstanceUsers(plc, b.Path);
                    }
                    return row;
                }).Concat(types.Select(t => new JObject { ["name"] = t.Item.Name, ["path"] = t.Path, ["kind"] = "UDT" }))), ["tags"] = tags,
                    ["scope"] = new JObject { ["path"] = scope.Path, ["kind"] = scope.Kind, ["namespacePreset"] = scope.Unit?.NamespacePreset },
                    ["reusedVersions"] = reused ?? new JArray(), ["members"] = PlcIntegrationMembers(change, blocks, types, tags) };
                var plan = PlcLibraryIntegrationPlanner.BuildFromArtifacts(change.Payload, versions, callerArtifact, inventory);
                plan["inventorySha256"] = ChangeHash(inventory.ToString(Newtonsoft.Json.Formatting.None));
                plan["coordinator"] = new JObject {
                    ["implementation"] = "internal_staged_coordinator_with_existing_native_adapters", ["publicEntryPointAvailable"] = false,
                    ["qualificationEntryPoint"] = "Explicit --component-qualification launch; library_component_qualification preparation and per-step change_apply, owned scratch only.",
                    ["nativeQualificationExecuted"] = false, ["stepReviewRequired"] = true, ["automaticReplayAllowed"] = false,
                    ["initialRecoveryAnchor"] = "First library_instantiate complete-project checkpoint; never the subsequent typed-edit checkpoint.",
                    ["remainingBoundary"] = "Qualification-only dispatch exists; independent native qualification is still required. Production combined apply remains unavailable."
                };
                change.Preview["integrationPlan"] = plan;
                change.Preview["applySupported"] = false;
                change.Preview["applyError"] = PlcLibraryIntegrationPlanner.ApplyError;
                ((JArray)change.Preview["effects"]).Add(new JObject { ["action"] = "planPlcComponent", ["nativeMutation"] = false, ["proposedSteps"] = plan["steps"].DeepClone() });
                ((JArray)change.Preview["warnings"]).Add("Planning only. No checkpoint or mutation is dispatched. Separate prepared steps require fresh native validation and are not an atomic combined installation.");
            }
            catch (PlcIntegrationPlanException error) { throw new BridgeException(error.Code, error.Message, error.Details); }
            catch (PlcComponentException error) { throw new BridgeException(error.Code, error.Message); }
        }

        private readonly bool _libraryIntegrateApplyEnabled;

        private string PlcCombinedApplyRefusal(LibraryChange change)
        {
            var caller = change.Preview["integrationPlan"]?["callerArtifact"] as JObject; System.Xml.Linq.XDocument export;
            try { export = caller == null ? null : EngineeringSources.ReadXml(PlcComponentContentContract.Artifact(caller, caller.Value<string>("sha256"))); }
            catch (PlcComponentException error) { return "The retained caller export is unusable: " + error.Message; }
            return PlcComponentQualificationGate.CombinedApplyRefusal(change.Preview, export) ??
                (change.Source == null || change.Source.Project != null || string.IsNullOrEmpty(change.Source.SourcePath) || change.Source.Writable
                    ? "Only the verified read-only global source with its retained snapshot is eligible." : null);
        }

        // Every production Host enables this (Host default since 29 October 2026); the preview, and so the reviewed hash, states whether change_apply installs.
        private void PreparePlcCombinedApply(LibraryChange change)
        {
            if (!_libraryIntegrateApplyEnabled) return;
            string refusal = PlcCombinedApplyRefusal(change);
            change.Preview["applySupported"] = refusal == null;
            change.Preview["combinedApply"] = new JObject { ["enabled"] = true, ["eligible"] = refusal == null, ["refusal"] = refusal,
                ["sequence"] = "change_apply prepares, applies with compile and save, and reads back each step in order: library_instantiate, then graphical_typed_edit. Not atomic.",
                ["checkpoint"] = "The import step's complete-project archive, taken before the first mutation, is this change's single recovery checkpoint.",
                ["qualification"] = "Natively qualified for the verified shapes on owned de-DE-only scratch projects (local.library_integrate.combined_apply.rerun.20261028)." };
            if (refusal != null) return;
            var coordinator = (JObject)change.Preview["integrationPlan"]["coordinator"];
            coordinator["publicEntryPointAvailable"] = true; coordinator["remainingBoundary"] = "Combined change_apply runs only the verified shapes; every other shape stays plan-only.";
            ((JArray)change.Preview["effects"]).Add(new JObject { ["action"] = "applyPlcComponentSteps", ["nativeMutation"] = true, ["steps"] = ((JArray)change.Preview["integrationPlan"]["steps"]).Count });
            var warnings = (JArray)change.Preview["warnings"];
            warnings.Where(w => ((string)w).StartsWith("Planning only.", StringComparison.Ordinal)).ToList().ForEach(w => w.Remove());
            warnings.Add("Combined apply is enabled: change_apply installs these steps in order. They are not atomic; a failure stops at that step and leaves recovery_required.");
        }

        // Fresh preconditions under the apply lease, before any durable claim; the coordinator revalidates the exact hashed plan.
        private PlcComponentCoordinator PlcComponentApplyCoordinator(LibraryChange change)
        {
            if (_sharedMutationLease == null) throw new BridgeException("MutationScopeIncomplete", "Combined library_integrate apply runs only under the Host's shared-mutation admission, which holds one exclusive lease across every step.");
            try { return CreatePlcComponentProductionCoordinator(change, Path.Combine(change.Directory, "c")); }
            catch (PlcComponentException error) { throw new BridgeException(error.Code, error.Message, LibraryChangeView(change)); }
        }

        // The reviewed plan hash approves both steps: the coordinator binds each freshly prepared step preview to the plan,
        // verifies each step by readback and adopts the import step's project archive as the single recovery checkpoint.
        private JObject ApplyPlcComponentIntegration(LibraryChange change, PlcComponentCoordinator coordinator)
        {
            JObject record = null; string step = null, journal = null;
            change.State = "applying"; LibraryJournal(change, "claim", "completed");
            try
            {
                record = coordinator.Status(); journal = Path.Combine(change.Directory, "c", record.Value<string>("operationId"), "component-journal.json");
                change.Journal["combinedApply"] = CombinedApplySummary(journal, record, null); LibraryJournal(change, "component_steps", "dispatching");
                for (int i = 0; i < 6 && record.Value<string>("state") != "installation_verified"; i++)
                {
                    string state = record.Value<string>("state"); step = PlcComponentQualificationGate.CombinedApplyStep(state);
                    record = coordinator.Advance(state.EndsWith("_prepared", StringComparison.Ordinal) ? record[state.Split('_')[0]]["prepared"].Value<string>("previewHash") : null);
                }
                if (record.Value<string>("state") != "installation_verified") throw new BridgeException("ComponentOutcomeUnknown", "The component sequence did not reach verified installation.");
                ((JArray)change.Journal["checkpoints"]).Add(record["initialCheckpoint"].DeepClone());
                change.Journal["combinedApply"] = CombinedApplySummary(journal, record, null);
                change.State = "applied"; change.Journal["engineeringVerified"] = true; change.Journal["persistenceVerified"] = false; change.Journal["runtimeVerified"] = false;
                LibraryJournal(change, "transaction", "completed");
                return LibraryChangeView(change);
            }
            catch (Exception exception)
            {
                // Read-only: an uncertain step is reconciled from its own journal, never replayed. The primary error is reported either way.
                try { record = coordinator.Reconcile(); } catch { try { record = coordinator.Status(); } catch { } }
                if (record?["initialCheckpoint"] is JObject checkpoint && ((JArray)change.Journal["checkpoints"]).Count == 0) ((JArray)change.Journal["checkpoints"]).Add(checkpoint.DeepClone());
                string code = (exception as BridgeException)?.Code ?? (exception as PlcComponentException)?.Code ?? "NativeLibraryError";
                change.State = "recovery_required"; change.Journal["combinedApply"] = CombinedApplySummary(journal, record, step ?? "start");
                change.Journal["failure"] = new JObject { ["code"] = code, ["message"] = exception.Message, ["failedStep"] = step ?? "start", ["details"] = (exception as BridgeException)?.Details };
                LibraryJournal(change, "transaction", "failed");
                throw new BridgeException(code, "Combined apply failed at " + (step ?? "start") + ": " + exception.Message, LibraryChangeView(change));
            }
        }

        private static JObject CombinedApplySummary(string journal, JObject record, string failedStep) => new JObject {
            ["coordinatorJournal"] = journal, ["state"] = record?["state"]?.DeepClone(), ["failedStep"] = failedStep,
            ["libraryChangeId"] = record?["library"]?["prepared"]?["changeId"]?.DeepClone(), ["typedChangeId"] = record?["graphical"]?["prepared"]?["changeId"]?.DeepClone(),
            ["libraryStepDispatched"] = record?["library"]?["dispatch"] != null, ["initialCheckpointRecorded"] = record?["initialCheckpoint"] != null,
            ["recovery"] = "change_restore on this changeId retrieves the single initial checkpoint into a new location in an empty owned Portal. Originals are never overwritten and no step is replayed." };

        // Other blocks using an existing instance DB; null when the native cross-reference read is incomplete (the planner then refuses reuse).
        private JArray PlcIntegrationInstanceUsers(PlcSelection plc, string path)
        {
            var analysis = ReadNativeAnalysis(new JObject { ["projectName"] = plc.Project.Name, ["plcPath"] = plc.Path, ["blockPath"] = path, ["maxItems"] = 2000 });
            if (analysis.IndexTruncated || analysis.Budget.Truncated || analysis.Warnings.Count > 0) return null;
            return new JArray(analysis.References.Where(r => r.Subject?.BlockPath == path && r.Actor?.BlockPath != null && r.Actor.BlockPath != path && (r.Relation == ReferenceType.Uses || r.Relation == ReferenceType.UsedBy))
                .Select(r => r.Actor.BlockPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal));
        }

        // Flattened declarations for DB-member and UDT-tag-member mappings; a missing owner stays absent so the planner refuses it.
        private JObject PlcIntegrationMembers(LibraryChange change, BlockEntry[] blocks, TypeEntry[] types, JArray tags)
        {
            var result = new JObject();
            JArray Export(Action<FileInfo> export)
            {
                string file = Path.Combine(change.Directory, "integration-members-" + result.Count + ".xml"); export(new FileInfo(file));
                return PlcHmiIntegrationPlanner.Members(EngineeringSources.ReadXml(File.ReadAllText(file)));
            }
            foreach (JObject mapping in (JArray)change.Payload["integration"]["mappings"])
            {
                string owner = mapping.Value<string>("dbPath") ?? (mapping["members"] != null ? mapping.Value<string>("tagPath") : null);
                if (owner == null || result[owner] != null) continue;
                if (mapping["dbPath"] != null)
                {
                    var db = blocks.FirstOrDefault(b => b.Path == owner);
                    if (db?.Item is DataBlock && !db.Item.IsKnowHowProtected) result[owner] = Export(f => db.Item.Export(f, ExportOptions.WithDefaults, DocumentInfoOptions.None));
                    continue;
                }
                string type = tags.FirstOrDefault(t => t.Value<string>("path") == owner)?.Value<string>("dataType") ?? "";
                var udts = types.Where(t => "\"" + (string.IsNullOrEmpty(t.Item.Namespace) ? t.Item.Name : t.Item.Namespace + "." + t.Item.Name) + "\"" == type).ToArray();
                if (udts.Length == 1) result[owner] = Export(f => udts[0].Item.Export(f, ExportOptions.WithDefaults, DocumentInfoOptions.None));
            }
            return result;
        }
    }
}
