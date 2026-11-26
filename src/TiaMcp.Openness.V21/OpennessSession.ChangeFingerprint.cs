using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcp.Model;
using static TiaMcp.Openness.V21.ChangeArguments;

namespace TiaMcp.Openness.V21
{
    // The project content a change fingerprint covers, and the fingerprint itself.
    internal sealed partial class OpennessSession
    {
        // The project-wide part of a change fingerprint: every device item, software scope and block. Safety states are kept apart
        // because the safety login state is session state, not project content.
        private sealed class ProjectContent
        {
            internal JArray Hardware = new JArray(), Software = new JArray(), SafetyStates = new JArray();
            internal JArray GeneratedExclusions = new JArray(), MissingProviders = new JArray(), InconsistentBystanders = new JArray();
            internal int NativeFingerprintBlocks;
            internal readonly List<Tuple<string, PlcSoftware>> Plcs = new List<Tuple<string, PlcSoftware>>();
            internal readonly List<bool> SafetyCoverage = new List<bool>();

            // Tokens are attached to a snapshot when used, so the kept copy and every use are separate trees.
            internal ProjectContent Copy(JArray safetyStates)
            {
                var copy = new ProjectContent { Hardware = (JArray)Hardware.DeepClone(), Software = (JArray)Software.DeepClone(), SafetyStates = safetyStates ?? (JArray)SafetyStates.DeepClone(),
                    GeneratedExclusions = (JArray)GeneratedExclusions.DeepClone(), MissingProviders = (JArray)MissingProviders.DeepClone(),
                    InconsistentBystanders = (JArray)InconsistentBystanders.DeepClone(), NativeFingerprintBlocks = NativeFingerprintBlocks };
                copy.Plcs.AddRange(Plcs); copy.SafetyCoverage.AddRange(SafetyCoverage);
                return copy;
            }
        }

        private readonly FingerprintContentCache<ProjectContent> _projectContent = new FingerprintContentCache<ProjectContent>();
        /// <summary>Set by --enable-fingerprint-reuse. Off by default: every fingerprint reads the whole project.</summary>
        internal bool FingerprintReuse { get; set; }

        private FingerprintSaveState ProjectSaveState(Project project)
        {
            var saved = project.LastModified;
            return new FingerprintSaveState(_processId, project.Path?.FullName, saved.ToString("O") + "|" + saved.Kind, project.IsModified);
        }

        private static bool HasSafetySignatureCoverage(JObject safetyState)
        {
            var safetySignatures = safetyState["signatures"] as JArray;
            return safetyState.Value<bool>("available") && safetySignatures != null && safetySignatures.Count > 0
                && safetySignatures.All(signature => !string.IsNullOrWhiteSpace(signature.Value<string>("type"))
                    && signature["value"] != null && signature["value"].Type != JTokenType.Null);
        }

        // Reuses the last project-wide read while the project is in the same saved state (FingerprintContentCache). Only prepared
        // changes of this session ask for it; OPC UA kinds add per-PLC content inside the read and always read in full.
        private ProjectContent ProjectContentFor(EngineeringChange change, Project project, bool tagImport, HashSet<string> targets, JArray opcUa)
        {
            bool reusable = change.ReuseProjectContent && opcUa == null;
            string variant = FingerprintContentRules.Variant(change.Kind, targets);
            var before = ProjectSaveState(project);
            var kept = reusable ? _projectContent.Find(before, variant) : null;
            if (kept != null)
            {
                // Safety states are read fresh. A changed signature coverage changes how protected generated blocks are read, so it reads in full.
                var safetyStates = new JArray(kept.Plcs.Select(plc => new JObject { ["plcPath"] = plc.Item1, ["safety"] = ReadSafety(plc.Item2) }));
                if (safetyStates.Select(state => HasSafetySignatureCoverage((JObject)state["safety"])).SequenceEqual(kept.SafetyCoverage))
                {
                    change.ProjectContentReuses++;
                    return kept.Copy(safetyStates);
                }
            }
            var content = ReadProjectContent(change, project, tagImport, targets, opcUa);
            change.ProjectContentReads++;
            if (reusable) _projectContent.Store(before, ProjectSaveState(project), variant, content.Copy(null));
            else _projectContent.Clear();
            return content;
        }

        private ProjectContent ReadProjectContent(EngineeringChange change, Project project, bool tagImport, HashSet<string> targets, JArray opcUa)
        {
            var read = new ProjectContent();
            JArray hardware = read.Hardware, software = read.Software, safetyStates = read.SafetyStates;
            JArray generatedExclusions = read.GeneratedExclusions, missingProviders = read.MissingProviders, inconsistentBystanders = read.InconsistentBystanders;
            var budget = new ScanBudget(50000);
            foreach (var entry in DeviceItems(project, budget))
            {
                var item = entry.Item;
                var hw = new JObject { ["path"] = entry.Path, ["typeIdentifier"] = item.TypeIdentifier, ["position"] = item.PositionNumber };
                var network = item.GetService<NetworkInterface>();
                if (network != null) hw["nodes"] = new JArray(network.Nodes.Select(CaptureNodeSnapshot));
                hw["addresses"] = new JArray(item.Addresses.Select(address => new JObject { ["type"] = address.IoType.ToString(), ["start"] = address.StartAddress, ["length"] = address.Length }));
                hardware.Add(hw);
                var plc = item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (plc == null) continue;
                var selection = new PlcSelection { Project = project, Software = plc, Path = entry.Path + "/plc/" + Segment(plc.Name) };
                opcUa?.Add(new JObject { ["plcPath"] = selection.Path, ["serverInterfaceGroup"] = CaptureOpcUaSnapshot(OpcUaProviderFor(plc)?.CommunicationGroup?.ServerInterfaceGroup, budget) });
                var safetyState = ReadSafety(plc);
                bool hasSafetySignatureCoverage = HasSafetySignatureCoverage(safetyState);
                safetyStates.Add(new JObject { ["plcPath"] = selection.Path, ["safety"] = safetyState });
                read.Plcs.Add(Tuple.Create(selection.Path, plc));
                read.SafetyCoverage.Add(hasSafetySignatureCoverage);
                var scopes = SoftwareScopes(selection, budget);
                foreach (var scope in scopes)
                {
                    var blockItems = new JArray();
                    foreach (var block in ScopeBlocks(scope, budget))
                    {
                        bool protectedBlock = block.Item.IsKnowHowProtected;
                        bool consistentBlock = block.Item.IsConsistent;
                        var info = new JObject { ["name"] = block.Item.Name, ["path"] = block.Path, ["number"] = block.Item.Number,
                            ["language"] = block.Item.ProgrammingLanguage.ToString(), ["modified"] = block.Item.ModifiedDate.ToString("O"),
                            ["protected"] = protectedBlock, ["consistent"] = consistentBlock, ["nativeSystemGroup"] = block.NativeSystemGroup };
                        if (tagImport)
                        {
                            info["fingerprintMode"] = "tagImportBlockMetadataOnly";
                            blockItems.Add(info);
                            continue;
                        }
                        var content = EngineeringFingerprintRules.ReadBlockContent(block.NativeSystemGroup, protectedBlock, consistentBlock, hasSafetySignatureCoverage, block.Path, () =>
                        {
                            var provider = block.Item.GetService<FingerprintProvider>();
                            return provider == null ? null : new JArray(provider.GetFingerprints().OrderBy(f => f.Id).Select(f => new JObject { ["id"] = f.Id.ToString(), ["value"] = ChangeScalar(f.Value) }));
                        }, targets.Contains(block.Item.Name));
                        info["fingerprintMode"] = content.Mode;
                        if (content.Payload != null) { info["fingerprints"] = content.Payload; read.NativeFingerprintBlocks++; }
                        else if (content.Mode == "protectedGeneratedMetadata")
                            generatedExclusions.Add(new JObject { ["path"] = block.Path, ["name"] = block.Item.Name,
                                ["mode"] = content.Mode, ["consistent"] = consistentBlock,
                                ["reason"] = "Protected native system-group derived payload: checksum API can reject generated safety blocks after compilation; identity/modified/protection/consistency metadata remains covered." });
                        else if (content.Mode == "inconsistentMetadataOnly") inconsistentBystanders.Add(block.Path);
                        else missingProviders.Add(block.Path);
                        blockItems.Add(info);
                    }
                    software.Add(new JObject { ["name"] = scope.Name, ["path"] = scope.Path, ["kind"] = scope.Kind, ["blocks"] = blockItems,
                        ["userGroups"] = new JArray(EngineeringGroupPaths(scope.Blocks, scope.Path + "/blocks", 0)
                            .Concat(EngineeringGroupPaths(scope.Types, scope.Path + "/types", 0)).Concat(EngineeringGroupPaths(scope.Tags, scope.Path + "/tags", 0))),
                        ["namespacePreset"] = scope.Unit?.NamespacePreset,
                        ["relations"] = scope.Unit == null ? null : new JArray(scope.Unit.Relations.Select(relation => new JObject { ["relationType"] = relation.RelationType.ToString(), ["relatedObject"] = InspectionValue(relation.RelatedObject) })),
                        ["types"] = new JArray(ScopeTypes(scope, budget).Select(type => EngineeringFingerprintRules.RequireConsistentType(change.Kind, () => type.Item.IsConsistent, type.Path, new JObject { ["name"] = type.Item.Name, ["path"] = type.Path, ["modified"] = type.Item.ModifiedDate.ToString("O"), ["protected"] = type.Item.IsKnowHowProtected }))),
                        ["tagTables"] = new JArray(TagTables(scope.Tags, scope.Path + "/tags", budget).Select(table => new JObject { ["name"] = table.Item.Name, ["path"] = table.Path, ["modified"] = table.Item.ModifiedTimeStamp.ToString("O") })) });
                }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete change fingerprint for this project size.");
            return read;
        }

        // locationIndependent omits only the project location and save state, to compare a retrieved archive copy with its original.
        private string EngineeringFingerprint(EngineeringChange change, Project project, bool locationIndependent = false)
        {
            change.FingerprintCoverage = null;
            bool tagImport = change.Kind == TagTableImportRules.Kind;
            if (IsCompileOnlyKind(change.Kind))
            {
                // Compilation must remain usable when inconsistent user blocks cannot
                // be exported. It compiles the current selected scope; no source import.
                var scope = CompileIdentityScope(change, project);
                change.FingerprintCoverage = new JObject { ["policyVersion"] = 1, ["complete"] = false,
                    ["scopePolicy"] = "compile-target-identity-and-project-save-state-v1", ["nativeFingerprintBlocks"] = 0,
                    ["limitations"] = new JArray("Compiler target identities and project save state only. Compilation operates on current source; generated state and source contents are not preservation claims.") };
                return HmiEngineeringRules.ClassicScreenFingerprint(project.Path.FullName, project.LastModified.ToString("O"), project.IsModified,
                    ChangeHash(scope.ToString(Formatting.None)), locationIndependent);
            }
            // This named kind promises screen-scope equality, not PLC payload or hardware equality.
            // A regular-screen import cannot write PLC blocks, and an inconsistent unrelated block
            // must not force a PLC compile merely to edit a screen. Keep project-state stale guards.
            if (change.Kind == "hmi_classic_screen_edit")
            {
                string contentHash = HmiEngineeringFingerprint(change, project);
                change.FingerprintCoverage = new JObject { ["policyVersion"] = 1, ["complete"] = false,
                    ["scopePolicy"] = HmiPreservationRules.ClassicScreenPolicy, ["nativeFingerprintBlocks"] = 0,
                    ["limitations"] = new JArray(HmiPreservationRules.ClassicScreenExportNote, "PLC and hardware content is not fingerprinted; project path, last save time and unsaved-state flag are checked.") };
                return HmiEngineeringRules.ClassicScreenFingerprint(project.Path.FullName, project.LastModified.ToString("O"), project.IsModified, contentHash, locationIndependent);
            }
            var snapshot = new JObject { ["fingerprintPolicyVersion"] = 2, ["projectPath"] = project.Path.FullName, ["lastModified"] = project.LastModified.ToString("O"), ["isModified"] = project.IsModified,
                ["simulationDuringBlockCompilationEnabled"] = project.GetService<PlcSimulationSettingsProvider>()?.IsSimulationDuringBlockCompilationEnabled };
            if (locationIndependent) { snapshot.Remove("projectPath"); snapshot.Remove("lastModified"); snapshot.Remove("isModified"); }
            var targets = new HashSet<string>(change.BlockNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var opcUa = change.Kind.StartsWith("opcua_", StringComparison.Ordinal) ? new JArray() : null;
            var content = ProjectContentFor(change, project, tagImport, targets, opcUa);
            JArray safetyStates = content.SafetyStates, generatedExclusions = content.GeneratedExclusions, missingProviders = content.MissingProviders, inconsistentBystanders = content.InconsistentBystanders;
            int nativeFingerprintBlocks = content.NativeFingerprintBlocks;
            snapshot["hardware"] = content.Hardware;
            snapshot["software"] = content.Software;
            snapshot["safety"] = safetyStates;
            if (tagImport)
                snapshot["tagTableContent"] = new JArray(CaptureTagImportScope(change, "fingerprint").Select(row =>
                    new JObject { ["path"] = row["path"].DeepClone(), ["contentSha256"] = row["contentSha256"].DeepClone() }));
            if (opcUa != null) snapshot["opcUa"] = opcUa;
            if (IsHmiChangeKind(change.Kind)) snapshot["hmiContent"] = HmiEngineeringFingerprint(change, project);
            if (change.Kind == "project_languages_set") snapshot["languageSettings"] = ProjectLanguageView(project.LanguageSettings);
            if (change.Kind.StartsWith("connection_", StringComparison.Ordinal)) snapshot["connections"] = ConnectionFingerprint(project, change.Payload);
            if (HardwareEngineeringRules.Applies(change.Kind)) snapshot["configuredHardware"] = CaptureHardwareEngineering(project, change.Kind, change.Payload, FParameterNames(change));
            if (IsPlcAlarmKind(change.Kind)) snapshot["plcAlarmContent"] = PlcAlarmFingerprint(change.Kind, project);
            if (change.Kind.StartsWith("vci_", StringComparison.Ordinal)) snapshot["vci"] = VciFingerprint(project);
            if (change.Kind.StartsWith("certificate_", StringComparison.Ordinal)) snapshot["certificates"] = CertificateFingerprint(project, change.Payload);
            if (change.Kind == "webserver_page_set") snapshot["webServer"] = WebServerFingerprint(project);
            if (change.Kind == "opcua_server_settings_set") snapshot["opcUaServerSettings"] = OpcUaServerSettingsFingerprint(project);
            if (change.Kind == "umac_role_set") snapshot["umac"] = UmacFingerprint(project);
            if (change.AttributeTarget != null && change.Kind != "tag_delete" && change.Kind != "constant_delete")
            {
                var attrs = change.Payload["attributes"] as JObject ?? new JObject();
                snapshot["targetAttributes"] = new JObject(attrs.Properties().Select(p => new JProperty(p.Name, ChangeScalar(change.AttributeTarget.GetAttribute(p.Name)))));
            }
            // A batch covers the requested attributes of every item's tag.
            // A batch of attribute edits covers each member's requested attributes; members without attributes hold null.
            if (change.BatchMembers != null && change.BatchMembers.Any(member => member.AttributeTarget != null))
                snapshot["targetAttributes"] = new JArray(change.BatchMembers.Select(member => member.AttributeTarget == null ? (JToken)JValue.CreateNull()
                    : new JObject((member.Payload["attributes"] as JObject ?? new JObject()).Properties().Select(p => new JProperty(p.Name, ChangeScalar(member.AttributeTarget.GetAttribute(p.Name)))))));
            if (change.TagBatchTargets != null)
                snapshot["targetAttributes"] = new JArray(change.TagBatchTargets.Select(target =>
                    new JObject(target.Item.Attributes.Properties().Select(p => new JProperty(p.Name, ChangeScalar(target.Tag.GetAttribute(p.Name)))))));
            if (change.Kind == "to_parameters")
            {
                var target = RequiredTechnologyObject(SelectPlc(change.Selector), change.Payload);
                var values = new JObject();
                foreach (var p in (change.Payload["parameters"] as JObject ?? new JObject()).Properties())
                {
                    object value = ReadTechnologyParameterValue(target, p.Name, false, out _, out var type);
                    values[p.Name] = new JObject { ["type"] = type.FullName, ["value"] = ChangeScalar(value) };
                }
                snapshot["technologyParameters"] = values;
            }
            if (change.Kind == "to_connect" || change.Kind == "to_disconnect")
            {
                // The identity rows below do not prove connection state; compare the selected TO's resolved connection observation.
                var plc = SelectPlc(change.Selector);
                var entry = RequiredTechnologyEntry(plc, change.Payload);
                var connectionBudget = new ScanBudget(500);
                snapshot["technologyConnections"] = TechnologyConnections(entry.Item, connectionBudget, new JArray(), entry.Path, out _, new TechnologyReferenceReader(plc.Project, plc.Path));
                if (connectionBudget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete technology connection fingerprint within the scan bound.");
            }
            if (change.Kind == "drive_motor_configure") snapshot["driveMotor"] = Startdrive(() => DriveMotorFingerprint(change, project));
            if (change.Kind == "drive_parameter_set" || change.Kind == "drive_telegram_set" || change.Kind == "drive_motor_configure" || change.Kind == "safety_drive_tags") snapshot["driveObject"] = Startdrive(() => DriveObjectFingerprint(change, project));
            if (change.Kind == "drive_parameter_set") snapshot["driveParameters"] = Startdrive(() => DriveParameterFingerprint(change, project));
            if (change.Kind == "drive_telegram_set") snapshot["driveTelegrams"] = Startdrive(() => DriveTelegramFingerprint(change, project));
            // to_connect's telegram form connects to a drive's telegram, so the drive object and its telegrams join the fingerprint.
            if (change.Kind == "to_connect" && change.Payload["telegramType"] != null)
            {
                snapshot["driveObject"] = Startdrive(() => DriveObjectFingerprint(change, project));
                snapshot["driveTelegrams"] = Startdrive(() => DriveTelegramFingerprint(change, project));
            }
            if (change.Kind.StartsWith("to_", StringComparison.Ordinal))
            {
                var plc = SelectPlc(change.Selector);
                snapshot["technologyObjects"] = TechnologyIdentityFingerprint(plc.Software.TechnologicalObjectGroup, plc.Path + "/technology");
            }
            change.FingerprintCoverage = new JObject { ["policyVersion"] = 2, ["complete"] = false, ["nativeFingerprintBlocks"] = nativeFingerprintBlocks,
                ["protectedGeneratedPayloadExclusions"] = generatedExclusions, ["blocksWithoutNativeProvider"] = missingProviders,
                ["inconsistentBlocksMetadataOnly"] = inconsistentBystanders,
                ["safetyCoverage"] = new JArray(safetyStates.Select(state => new JObject { ["plcPath"] = state["plcPath"].DeepClone(),
                    ["available"] = state["safety"]["available"].DeepClone(), ["signatureCount"] = (state["safety"]["signatures"] as JArray)?.Count ?? 0 })),
                ["limitations"] = new JArray("Generated payload bytes and unexposed vendor data are not fingerprinted; explicit covered native metadata and available safety signatures are compared.",
                    "Native fingerprint failures for user/editable blocks propagate; exclusions require protected native system-group provenance and available owning-PLC safety signatures.") };
            if (tagImport) change.FingerprintCoverage = new JObject { ["policyVersion"] = 1, ["complete"] = false,
                ["scopePolicy"] = TagTableImportRules.Policy, ["nativeFingerprintBlocks"] = 0, ["limitations"] = new JArray(TagTableImportRules.Coverage) };
            return ChangeHash(snapshot.ToString(Formatting.None));
        }

        private static IEnumerable<string> EngineeringGroupPaths(IEngineeringObject group, string path, int depth)
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Group fingerprint nesting exceeds 64 levels.");
            yield return path;
            if (group is PlcBlockGroup blocks)
                foreach (var child in blocks.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is PlcTypeGroup types)
                foreach (var child in types.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is PlcTagTableGroup tags)
                foreach (var child in tags.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
            else if (group is TechnologicalInstanceDBGroup technology)
                foreach (var child in technology.Groups) foreach (string value in EngineeringGroupPaths(child, path + "/groups/" + Segment(child.Name), depth + 1)) yield return value;
        }

        private static JObject CaptureNodeSnapshot(Node node)
        {
            var attributes = new JObject();
            var infos = node.GetAttributeInfos();
            foreach (var name in NetworkWriteAttributes.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!infos.Any(info => info.Name == name && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)) continue;
                attributes[name] = ChangeScalar(node.GetAttribute(name));
            }
            return new JObject { ["name"] = node.Name, ["type"] = node.NodeType.ToString(), ["subnet"] = node.ConnectedSubnet?.Name, ["attributes"] = attributes };
        }
    }
}
