using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
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
                if (scope.Unit != null) throw new BridgeException("UnsupportedIntegrationScope", "PLC integration recipes currently require the standard PLC scope.");
                PreparePlcLibraryInstantiation(change); // Preserve existing collision, family and completeness guards.
                var budget = new ScanBudget(50000);
                var blocks = ScopeBlocks(scope, budget).ToArray();
                var types = ScopeTypes(scope, budget).ToArray();
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
                var inventory = new JObject { ["complete"] = true, ["objects"] = new JArray(blocks.Select(b => new JObject {
                    ["name"] = b.Item.Name, ["path"] = b.Path, ["kind"] = BlockNumberKind(b.Item), ["number"] = b.Item.Number,
                    ["language"] = b.Item.ProgrammingLanguage.ToString(), ["protected"] = b.Item.IsKnowHowProtected, ["namespace"] = b.Item.Namespace
                }).Concat(types.Select(t => new JObject { ["name"] = t.Item.Name, ["path"] = t.Path, ["kind"] = "UDT" }))), ["tags"] = tags };
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
            catch (PlcIntegrationPlanException error) { throw new BridgeException(error.Code, error.Message); }
            catch (PlcComponentException error) { throw new BridgeException(error.Code, error.Message); }
        }
    }
}
