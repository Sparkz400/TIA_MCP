using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Formatting = Newtonsoft.Json.Formatting;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using TiaMcp.Analysis;
using NativeHmiConnection = Siemens.Engineering.HW.CommunicationConnections.HmiConnection;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // V21 Base CommunicationManagement supplies exact native endpoints. A configured HMI
        // symbolic foreign key is evaluated only after this owner proof, never against all PLC names.
        private void HmiIntegratedRelationships(Project project, HmiDiscoveredTarget target, HmiCapture capture, JArray edges,
            List<HmiNativeEntry> known, List<Tuple<DeviceItem, PlcSelection>> plcs, JObject args)
        {
            if (capture.Budget.Truncated || !capture.CandidateScopesComplete)
            {
                capture.Diagnostics.Add(new JObject { ["code"] = "HmiBindingScopeIncomplete", ["reason"] = "Configured symbolic bindings require a complete HMI/PLC candidate traversal; incomplete inspection is not used to prove uniqueness." });
                return;
            }
            CommunicationManagement management;
            try { management = target.Hardware.GetService<CommunicationManagement>(); }
            catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("CommunicationManagement", error)); capture.Status = "unavailable"; return; }
            if (management == null)
            {
                capture.Diagnostics.Add(new JObject { ["code"] = "HmiIntegratedConnectionProviderUnavailable", ["reason"] = "Native CommunicationManagement is not exposed on this HMI runtime device item. Textual partner names are not substituted." });
                return;
            }
            var proofs = new Dictionary<string, List<Tuple<PlcSelection, JObject>>>(StringComparer.Ordinal);
            var softwareConnectionsByName = capture.Entries.Where(e => e.Identity.Value<string>("kind") == "hmiConnection")
                .GroupBy(e => e.Identity.Value<string>("name"), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var native in management.Connections)
            {
                if (!capture.Budget.Take()) return;
                var connection = native as NativeHmiConnection;
                if (connection == null) continue;
                var localSoftware = connection.LocalTarget?.GetService<SoftwareContainer>()?.Software;
                bool localExact = Equals(connection.LocalTarget, target.Hardware) || localSoftware != null && Equals(localSoftware, target.Software);
                if (!localExact) continue;
                var partnerSoftware = connection.PartnerTarget?.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                var partners = plcs.Where(p => Equals(p.Item1, connection.PartnerTarget) || partnerSoftware != null && Equals(p.Item2.Software, partnerSoftware)).ToList();
                var softwareConnections = softwareConnectionsByName.TryGetValue(connection.LocalConnectionName, out var matchingConnections) ? matchingConnections : new List<HmiNativeEntry>();
                var evidence = new JObject { ["provider"] = "Siemens.Engineering.HW.Features.CommunicationManagement",
                    ["nativeType"] = connection.GetType().FullName, ["localTargetMatchesHmiOwner"] = localExact,
                    ["localConnectionName"] = connection.LocalConnectionName, ["isValid"] = connection.IsValid,
                    ["partnerTargetMatchedBy"] = "Native DeviceItem/Software object equality", ["localInterface"] = connection.LocalInterface?.Name,
                    ["partnerInterface"] = connection.PartnerInterface?.Name, ["accessPoint"] = connection.AccessPoint };
                foreach (var item in softwareConnections)
                {
                    string path = item.Identity.Value<string>("objectPath");
                    if (!proofs.TryGetValue(path, out var list)) proofs[path] = list = new List<Tuple<PlcSelection, JObject>>();
                    list.Add(Tuple.Create(connection.IsValid && partners.Count == 1 && softwareConnections.Count == 1 ? partners[0].Item2 : null, evidence));
                }
            }
            var exactConnections = new Dictionary<string, PlcSelection>(StringComparer.Ordinal);
            foreach (var pair in proofs)
            {
                bool exact = pair.Value.Count == 1 && pair.Value[0].Item1 != null;
                var plc = exact ? pair.Value[0].Item1 : null;
                var identity = plc == null ? null : IdentityInfo(project, plc.Path, plc.Path, plc.Path, "plc", plc.Software.Name);
                var edge = HmiInspectionRules.Reference(pair.Key, "hmiConnectionPlc", "CommunicationManagement.Connections.LocalTarget/PartnerTarget", null,
                    identity == null ? Enumerable.Empty<JObject>() : new[] { identity }, false);
                edge["nativeConnectionEvidence"] = new JArray(pair.Value.Select(p => p.Item2));
                if (!exact) edge["reason"] = "Native connection endpoints are invalid, ambiguous, or outside the completely observed PLC scope.";
                HmiReplaceRelationship(capture, edges, edge);
                if (exact) exactConnections[pair.Key] = plc;
            }
            // PlcTag/AccessMode semantics below are documented for Unified. Classic requires its
            // separately observed native XML binding shape; it does not inherit this interpretation.
            if (target.Family != "unified") return;
            var exported = new Dictionary<string, Tuple<OfflineEvidence.InterfaceData, JObject>>(StringComparer.Ordinal);
            long exportedBytes = 0;
            var nativeTags = known.Where(e => e.Native is PlcTag).GroupBy(e => Tuple.Create(e.Identity.Value<string>("plcPath"), e.Identity.Value<string>("name"))).ToDictionary(g => g.Key, g => g.ToList());
            var nativeBlocks = known.Where(e => e.Native is DataBlock).GroupBy(e => Tuple.Create(e.Identity.Value<string>("plcPath"), e.Identity.Value<string>("name"))).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var tag in capture.Entries.Where(e => e.Identity.Value<string>("kind") == "hmiTag"))
            {
                if (capture.Budget.Truncated) break;
                var properties = tag.Row["properties"] as JObject ?? new JObject();
                string plcTag = HmiStringProperty(properties, "PlcTag"), connectionName = HmiStringProperty(properties, "Connection");
                if (string.IsNullOrEmpty(plcTag) || string.IsNullOrEmpty(connectionName)) continue;
                var connections = softwareConnectionsByName.TryGetValue(connectionName, out var byName) ? byName : new List<HmiNativeEntry>();
                if (connections.Count != 1 || !exactConnections.TryGetValue(connections[0].Identity.Value<string>("objectPath"), out var plc)) continue;
                // SymbolicAccess is documented only for integrated connections. Absolute addresses,
                // multiplexing and indirect array/UDT expressions cannot identify a declaration here.
                string[] components = HmiInspectionRules.ScalarSymbolPath(plcTag);
                if (HmiStringProperty(properties, "AccessMode") != "SymbolicAccess" || properties.Value<bool?>("DBNameMultiplexing") == true || components == null ||
                    (HmiStringProperty(properties, "Address") ?? "").Contains("#{") || (HmiStringProperty(properties, "Address") ?? "").StartsWith("$", StringComparison.Ordinal)) continue;
                string observedPlc = HmiStringProperty(properties, "PlcName");
                if (!string.IsNullOrEmpty(observedPlc) && observedPlc != plc.Software.Name) continue;
                var candidates = new List<JObject>(); JObject artifact = null;
                string dataType = HmiStringProperty(properties, "DataType");
                if (components.Length == 1)
                {
                    var declarations = nativeTags.TryGetValue(Tuple.Create(plc.Path, components[0]), out var declarationMatches) ? declarationMatches : new List<HmiNativeEntry>();
                    if (declarations.Count == 1 && declarations[0].Identity.Value<string>("scopePath") == plc.Path && HmiScalarTypesAgree(dataType, ((PlcTag)declarations[0].Native).DataTypeName)) candidates.Add(declarations[0].Identity);
                }
                else
                {
                    var blocks = nativeBlocks.TryGetValue(Tuple.Create(plc.Path, components[0]), out var blockMatches) ? blockMatches : new List<HmiNativeEntry>();
                    if (blocks.Count == 1 && blocks[0].Identity.Value<string>("scopePath") == plc.Path && args.Value<string>("artifactDirectory") != null)
                    {
                        var block = blocks[0]; string blockPath = block.Identity.Value<string>("objectPath");
                        if (!exported.TryGetValue(blockPath, out var parsed) && exported.Count < 32)
                        {
                            try
                            {
                                var file = NewInspectionArtifact(args, "hmi-plc-declarations"); ((DataBlock)block.Native).Export(file, ExportOptions.WithDefaults);
                                artifact = FileArtifact(file); capture.Artifacts.Add(artifact.DeepClone());
                                if (file.Length + exportedBytes > 16 * 1024 * 1024) { capture.Budget.Truncated = true; capture.Diagnostics.Add(new JObject { ["code"] = "HmiDeclarationExportLimit", ["blockPath"] = blockPath, ["reason"] = "Combined declaration exports exceed the 16 MiB parsing budget." }); continue; }
                                exportedBytes += file.Length;
                                using (var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                                    parsed = Tuple.Create(OfflineEvidence.NormalizeInterface(XDocument.Load(reader, LoadOptions.SetLineInfo), capture.Budget.Take, () => capture.Budget.Truncated = true), artifact);
                                exported.Add(blockPath, parsed);
                            }
                            catch (Exception error) { capture.Diagnostics.Add(HmiDiagnostic("HMI binding declaration export " + blockPath, error)); capture.Status = "unavailable"; continue; }
                        }
                        if (parsed != null && !capture.Budget.Truncated)
                        {
                            artifact = parsed.Item2;
                            foreach (var section in parsed.Item1.Sections.Where(s => s.OwnerName == block.Identity.Value<string>("name")))
                                foreach (var member in HmiExplicitMembers(section.Members, capture.Budget))
                                {
                                    if (!member.Path.SequenceEqual(components.Skip(1), StringComparer.Ordinal) || member.Array != null || !HmiScalarTypesAgree(dataType, member.DataType)) continue;
                                    var identity = (JObject)block.Identity.DeepClone(); identity["kind"] = "plcDeclaration";
                                    identity["objectPath"] = blockPath + "/interface/" + Segment(section.Name) + "/members/" + string.Join("/members/", member.Path.Select(Segment));
                                    identity["name"] = member.Name; identity["blockPath"] = blockPath; identity["section"] = section.Name;
                                    identity["pathComponents"] = new JArray(member.Path); identity["dataType"] = member.DataType; identity["exportSha256"] = artifact["sha256"];
                                    identity["xmlLine"] = member.XmlLine; candidates.Add(identity);
                                }
                        }
                    }
                }
                if (capture.Budget.Truncated) candidates.Clear();
                var relation = HmiInspectionRules.Reference(tag.Identity.Value<string>("objectPath"), "hmiPlcDeclaration", "PlcTag/AccessMode with native CommunicationManagement owner", plcTag, candidates, false, "parsedSupported");
                relation["connectionPath"] = connections[0].Identity["objectPath"]; relation["plcPath"] = plc.Path;
                relation["sourceArtifact"] = artifact; relation["bindingProof"] = "Explicit native symbolic foreign key after exact native hardware endpoint ownership and declaration type checks";
                if (candidates.Count != 1) relation["reason"] = "The exact configured scalar declaration could not be proven uniquely; arrays, opaque UDTs, ambiguous namespaces and incomplete exports are not expanded.";
                HmiReplaceRelationship(capture, edges, relation);
            }
        }

        // Same proof as HmiIntegratedRelationships, for one connection before a write: exactly one
        // valid native endpoint pair whose partner is exactly one PLC in a completely traversed project.
        private PlcSelection HmiProveIntegratedOwner(Project project, HmiDiscoveredTarget target, string connectionName, JObject evidence)
        {
            var management = target.Hardware.GetService<CommunicationManagement>()
                ?? throw new BridgeException("DependencyUnresolved", "Native CommunicationManagement is not exposed on this HMI runtime device item; connection ownership cannot be proven.", evidence);
            var budget = new ScanBudget(50000);
            var plcs = new List<Tuple<DeviceItem, PlcSelection>>();
            foreach (var hardware in DeviceItems(project, budget))
                if (hardware.Item.GetService<SoftwareContainer>()?.Software is PlcSoftware software)
                    plcs.Add(Tuple.Create(hardware.Item, new PlcSelection { Project = project, Software = software, Path = hardware.Path + "/plc/" + Segment(software.Name) }));
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC owner traversal is incomplete; connection ownership cannot be proven unique.", evidence);
            var matches = new List<NativeHmiConnection>();
            foreach (var native in management.Connections)
            {
                if (!(native is NativeHmiConnection connection) || connection.LocalConnectionName != connectionName) continue;
                var localSoftware = connection.LocalTarget?.GetService<SoftwareContainer>()?.Software;
                if (Equals(connection.LocalTarget, target.Hardware) || localSoftware != null && Equals(localSoftware, target.Software)) matches.Add(connection);
            }
            if (matches.Count != 1 || !matches[0].IsValid)
                throw new BridgeException("DependencyUnresolved", "The HMI connection has no single valid native integrated endpoint pair on this HMI owner.", evidence);
            var partnerSoftware = matches[0].PartnerTarget?.GetService<SoftwareContainer>()?.Software as PlcSoftware;
            var partners = plcs.Where(p => Equals(p.Item1, matches[0].PartnerTarget) || partnerSoftware != null && Equals(p.Item2.Software, partnerSoftware)).ToList();
            evidence["provider"] = "Siemens.Engineering.HW.Features.CommunicationManagement";
            evidence["localConnectionName"] = matches[0].LocalConnectionName; evidence["isValid"] = matches[0].IsValid;
            evidence["partnerTargetMatchedBy"] = "Native DeviceItem/Software object equality"; evidence["partnerCount"] = partners.Count;
            if (partners.Count != 1) throw new BridgeException("DependencyUnresolved", "The integrated connection partner is not exactly one PLC in this project.", evidence);
            evidence["plcPath"] = partners[0].Item2.Path;
            return partners[0].Item2;
        }

        // Root tag tables only: the reader resolves an unquoted single-component PlcTag there.
        private PlcTag HmiRootPlcTag(Project project, PlcSelection plc, JObject identity)
        {
            HmiValidateIdentity(project, identity);
            if (identity.Value<string>("plcPath") != plc.Path)
                throw new BridgeException("DependencyUnresolved", "The PLC tag belongs to a different PLC than the connection's native partner.");
            var budget = new ScanBudget(50000);
            var matches = new List<PlcTag>();
            foreach (var table in TagTables(plc.Software.TagTableGroup, plc.Path + "/tags", budget))
                foreach (var tag in table.Item.Tags)
                    if (budget.Take() && table.Path + "/tags/" + Segment(tag.Name) == identity.Value<string>("objectPath")) matches.Add(tag);
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC tag lookup is incomplete; uniqueness is not established.");
            if (matches.Count != 1) throw new BridgeException(matches.Count == 0 ? "ObjectNotFound" : "AmbiguousTarget", "No unique root PLC tag matches the exact identity.");
            if (identity.Value<string>("name") is string name && name != matches[0].Name) throw new BridgeException("StaleRevision", "The PLC tag identity name changed.");
            var symbol = HmiInspectionRules.ScalarSymbolPath(matches[0].Name);
            if (symbol == null || symbol.Length != 1 || symbol[0] != matches[0].Name)
                throw new BridgeException("DependencyUnresolved", "The PLC tag name has no unquoted single-component symbolic spelling.");
            return matches[0];
        }

        private static IEnumerable<OfflineEvidence.InterfaceMember> HmiExplicitMembers(IEnumerable<OfflineEvidence.InterfaceMember> members, ScanBudget budget, int depth = 0)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var member in members) { if (!budget.Take()) yield break; yield return member; foreach (var child in HmiExplicitMembers(member.Members, budget, depth + 1)) yield return child; }
        }
        private static bool HmiScalarTypesAgree(string hmiType, string plcType)
        {
            var allowed = new[] { "Bool", "Byte", "Word", "DWord", "LWord", "SInt", "USInt", "Int", "UInt", "DInt", "UDInt", "LInt", "ULInt", "Real", "LReal", "Char", "WChar", "Time", "LTime", "Date", "Time_Of_Day", "LTime_Of_Day", "Date_And_Time", "DTL" };
            return allowed.Contains(plcType ?? "", StringComparer.OrdinalIgnoreCase) && string.Equals(hmiType, plcType, StringComparison.OrdinalIgnoreCase);
        }
        private static void HmiReplaceRelationship(HmiCapture capture, JArray edges, JObject edge)
        {
            foreach (var old in edges.OfType<JObject>().Where(e => e.Value<string>("sourcePath") == edge.Value<string>("sourcePath") && e.Value<string>("relation") == edge.Value<string>("relation")).ToList())
            { capture.RelationBytes -= System.Text.Encoding.UTF8.GetByteCount(old.ToString(Formatting.None)); old.Remove(); }
            HmiAddRelationship(capture, edges, edge);
        }
    }
}
