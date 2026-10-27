using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.CommunicationConnections;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Transport parameters only: names, IDs and resource IDs identify the connection to program/HMI consumers.
        private static readonly HashSet<string> ConnectionWriteAttributes = new HashSet<string>(new[]
        {
            "LocalActiveEstablishment", "PartnerActiveEstablishment", "LocalPort", "PartnerPort", "PartnerAddress", "LocalTsap", "PartnerTsap", "LocalLsap", "PartnerLsap"
        }, StringComparer.Ordinal);
        // V21 ConnectionType names mapped to connection_review protocol families.
        private static readonly Dictionary<string, string> ConnectionProtocols = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["S7Connection"] = "S7", ["TcpConnection"] = "TCP", ["UdpConnection"] = "UDP", ["IsoConnection"] = "ISO",
            ["IsoOnTcpConnection"] = "ISO-on-TCP", ["FdlConnection"] = "FDL", ["PtpConnection"] = "PtP", ["HmiConnection"] = "HMI"
        };

        private JObject ConnectionInspect(JObject args)
        {
            var project = SelectProject(args);
            string wanted = string.IsNullOrWhiteSpace(args.Value<string>("hardwarePath")) ? null : args.Value<string>("hardwarePath");
            var lookup = new ScanBudget(50000); var budget = new ScanBudget(Limit(args, 500));
            var warnings = new JArray(); var rows = new JArray(); var owners = new JArray();
            var hardware = DeviceItems(project, lookup).ToArray();
            if (lookup.Truncated) warnings.Add("Hardware lookup reached its 50000-item traversal bound.");
            if (wanted != null && !hardware.Any(entry => entry.Path == wanted) && !lookup.Truncated)
                throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            foreach (var entry in hardware)
            {
                if (wanted != null && entry.Path != wanted) continue;
                if (budget.Truncated) break;
                try
                {
                    var management = entry.Item.GetService<CommunicationManagement>();
                    if (management == null) { if (wanted != null) throw new BridgeException("UnsupportedCapability", "Selected hardware item exposes no native CommunicationManagement."); continue; }
                    string owner = ConnectionOwnerPath(management, entry, hardware);
                    if (owners.Any(path => (string)path == owner)) continue;
                    owners.Add(owner);
                    foreach (var connection in management.Connections)
                    {
                        if (!budget.Take()) break;
                        rows.Add(ConnectionRow(connection, owner, hardware, warnings));
                    }
                }
                catch (BridgeException) { throw; }
                catch (Exception error) { warnings.Add(InspectionWarning(entry.Path, error)); }
            }
            foreach (var duplicate in rows.GroupBy(row => row.Value<string>("id"), StringComparer.Ordinal).Where(group => group.Count() > 1))
                warnings.Add(new JObject { ["path"] = duplicate.Key, ["message"] = "Duplicate connection identity; connection_review requires unique ids." });
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["owners"] = owners;
            // Exactly a connection-review-request.v1 before/after snapshot; consumers are not captured here.
            result["reviewSnapshot"] = new JObject { ["connectionsComplete"] = wanted == null && !lookup.Truncated && !budget.Truncated && warnings.Count == 0,
                ["consumersComplete"] = false, ["connections"] = rows, ["consumers"] = new JArray() };
            result["warnings"] = warnings;
            result["completeness"] = InspectionCoverage(budget, warnings, "Configured connections of CommunicationManagement.Connections on bounded project hardware, typed V21 properties and endpoint owners by native object equality.",
                "Program-created connection descriptions and HMI software connection objects are not enumerated", "No consumers, resource/capacity rules or runtime communication");
            return result;
        }

        private static string ConnectionOwnerPath(CommunicationManagement management, HardwareEntry entry, HardwareEntry[] hardware)
            => NetworkOwnerReference(management.OwnedBy, hardware).Value<string>("ownerHardwarePath") ?? entry.Path;

        // connection_review row: every public scalar/endpoint property the V21 connection type declares.
        private static JObject ConnectionRow(Connection connection, string ownerPath, HardwareEntry[] hardware, JArray warnings)
        {
            var properties = new JObject();
            foreach (var property in connection.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType?.Namespace == typeof(Connection).Namespace && p.Name != "Parent" && p.GetIndexParameters().Length == 0)
                .GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (IsSensitiveAttribute(property.Name)) { properties[property.Name] = "<excluded>"; continue; }
                try
                {
                    object value = property.GetValue(connection);
                    properties[property.Name] = value is Node node
                        ? new JObject { ["nodeName"] = node.Name, ["nodeId"] = node.NodeId, ["interfacePath"] = NetworkOwnerReference(node, hardware)["ownerHardwarePath"] }
                        : value is DeviceItem item ? NetworkOwnerReference(item, hardware)["ownerHardwarePath"] : ChangeScalar(value);
                }
                catch (Exception error) { warnings.Add(InspectionWarning(ownerPath + "/connections/" + property.Name, error.InnerException ?? error)); }
            }
            string type = properties.Value<string>("ConnectionType");
            return new JObject { ["id"] = ownerPath + "/connections/" + Segment(properties.Value<string>("LocalConnectionName")), ["origin"] = "engineering",
                ["protocol"] = type != null && ConnectionProtocols.TryGetValue(type, out string protocol) ? protocol : type ?? "unknown",
                ["local"] = ConnectionEndpoint(properties, "Local"), ["partner"] = ConnectionEndpoint(properties, "Partner"), ["properties"] = properties };
        }

        private static JObject ConnectionEndpoint(JObject properties, string side)
        {
            string owner = properties.Value<string>(side + "Target"), face = (properties[side + "Interface"] as JObject)?.Value<string>("interfacePath");
            return new JObject { ["ownerPath"] = owner, ["interfacePath"] = face,
                ["reason"] = owner != null && face != null ? null : side + " target or interface is unset, unreadable or not uniquely resolved within the bounded hardware capture." };
        }

        // preview == null applies. Prepare and apply resolve the same owner, endpoints and complete owner connection table.
        private void ConnectionEdit(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload;
            var lookup = new ScanBudget(50000);
            var hardware = DeviceItems(project, lookup).ToArray();
            if (lookup.Truncated) throw new BridgeException("TraversalLimit", "Hardware lookup is incomplete; connection endpoints cannot be resolved exactly.");
            string path = ChangeText(payload, "hardwarePath", 4096);
            var entry = hardware.FirstOrDefault(item => item.Path == path) ?? throw new BridgeException("HardwareNotFound", "No hardware item matches hardwarePath.");
            var management = entry.Item.GetService<CommunicationManagement>() ?? throw new BridgeException("UnsupportedCapability", "Selected hardware item exposes no native CommunicationManagement.");
            string owner = ConnectionOwnerPath(management, entry, hardware);
            var before = ConnectionManifest(management, owner, hardware);
            var manifest = new JArray(before.Select(row => row.Item2));
            if (preview != null)
            {
                preview["connectionsBefore"] = manifest;
                ((JArray)preview["warnings"]).Add("Only this owner's connection table is read back and preservation-checked. Partner-side counterparts, program instructions and HMI tags that use the connection are covered only by the mandatory whole-project device compile.");
            }
            else if (!JToken.DeepEquals(manifest, change.Preview["connectionsBefore"]))
                throw new BridgeException("StaleRevision", "The owner's connections changed since preparation; no write was applied.");
            var effects = preview?["effects"] as JArray;
            if (change.Kind == "connection_create")
            {
                string type = ChangeText(payload, "connectionType", 64);
                if (!ConnectionProtocols.ContainsKey(type)) throw new BridgeException("InvalidRequest", "connectionType must be one of: " + string.Join(", ", ConnectionProtocols.Keys));
                var local = ChangeNode(project, payload, "localInterfacePath", "localNodeName");
                var partnerTarget = ChangeHardware(project, payload, "partnerTargetPath").Item;
                var partner = ChangeNode(project, payload, "partnerInterfacePath", "partnerNodeName");
                if (effects != null)
                {
                    effects.Add(new JObject { ["createConnection"] = type, ["ownerPath"] = owner, ["localInterfacePath"] = payload["localInterfacePath"], ["localNodeName"] = local.Name,
                        ["partnerTargetPath"] = payload["partnerTargetPath"], ["partnerInterfacePath"] = payload["partnerInterfacePath"], ["partnerNodeName"] = partner.Name });
                    return;
                }
                var created = CreateConnection(management.Connections, type, local, partnerTarget, partner);
                if (created == null || created.ConnectionType.ToString() != type || !Equals(created.LocalInterface, local) || !Equals(created.PartnerTarget, partnerTarget) || !Equals(created.PartnerInterface, partner))
                    throw new BridgeException("MutationVerificationFailed", "The created connection's native type or endpoints differ from the request.");
                var after = ConnectionManifest(management, owner, hardware);
                RequireConnectionsPreserved(before, after.Where(row => !Equals(row.Item1, created)));
                change.Readbacks.Add(new JObject { ["role"] = "connection-created", ["connection"] = ConnectionReadback(after, created) });
                return;
            }
            string name = ChangeText(payload, "connectionName", 256);
            string id = payload["connectionId"] == null ? null : ChangeText(payload, "connectionId", 64);
            var matches = before.Where(row => row.Item2["properties"].Value<string>("LocalConnectionName") == name && (id == null || row.Item2["properties"].Value<string>("LocalConnectionId") == id)).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "ObjectNotFound" : "AmbiguousTarget", "No single connection of this owner has the exact connectionName" + (id == null ? "." : " and connectionId."));
            var selected = matches[0].Item1;
            var others = before.Where(row => !Equals(row.Item1, selected)).ToList();
            if (change.Kind == "connection_delete")
            {
                if (effects != null) { effects.Add(new JObject { ["deleteConnection"] = matches[0].Item2.DeepClone() }); return; }
                selected.Delete();
                RequireConnectionsPreserved(others, ConnectionManifest(management, owner, hardware));
                return;
            }
            var attributes = ChangeObject(payload, "attributes");
            if (attributes.Count == 0) throw new BridgeException("InvalidRequest", "attributes must contain at least one editable field.");
            if (effects != null)
            {
                change.AttributeTarget = selected;
                effects.Add(new JObject { ["connection"] = matches[0].Item2["id"], ["attributes"] = PrepareAttributes(selected, attributes, ConnectionWriteAttributes) });
                return;
            }
            ApplyAttributes(selected, attributes, ConnectionWriteAttributes);
            var updated = ConnectionManifest(management, owner, hardware);
            RequireConnectionsPreserved(others, updated.Where(row => !Equals(row.Item1, selected)));
            change.Readbacks.Add(new JObject { ["role"] = "connection-set", ["connection"] = ConnectionReadback(updated, selected) });
        }

        private static Connection CreateConnection(ConnectionComposition connections, string type, Node local, DeviceItem partnerTarget, Node partner)
        {
            switch (type)
            {
                case "S7Connection": return connections.Create<S7Connection>(local, partnerTarget, partner);
                case "TcpConnection": return connections.Create<TcpConnection>(local, partnerTarget, partner);
                case "UdpConnection": return connections.Create<UdpConnection>(local, partnerTarget, partner);
                case "IsoConnection": return connections.Create<IsoConnection>(local, partnerTarget, partner);
                case "IsoOnTcpConnection": return connections.Create<IsoOnTcpConnection>(local, partnerTarget, partner);
                case "FdlConnection": return connections.Create<FdlConnection>(local, partnerTarget, partner);
                case "PtpConnection": return connections.Create<PtpConnection>(local, partnerTarget, partner);
                case "HmiConnection": return connections.Create<HmiConnection>(local, partnerTarget, partner);
                default: throw new BridgeException("InvalidRequest", "Unsupported connectionType: " + type);
            }
        }

        private static List<Tuple<Connection, JObject>> ConnectionManifest(CommunicationManagement management, string owner, HardwareEntry[] hardware)
        {
            var warnings = new JArray(); var rows = new List<Tuple<Connection, JObject>>();
            foreach (var connection in management.Connections)
            {
                if (rows.Count == 2048) throw new BridgeException("TraversalLimit", "The owner exposes more than 2048 connections; preservation cannot be proven.");
                rows.Add(Tuple.Create(connection, ConnectionRow(connection, owner, hardware, warnings)));
            }
            if (warnings.Count != 0) throw new BridgeException("DependencyUnresolved", "Connection properties could not be read completely; preservation cannot be proven.", new JObject { ["warnings"] = warnings });
            return rows;
        }

        private static void RequireConnectionsPreserved(IEnumerable<Tuple<Connection, JObject>> expected, IEnumerable<Tuple<Connection, JObject>> actual)
        {
            Func<IEnumerable<Tuple<Connection, JObject>>, string[]> text = rows => rows.Select(row => row.Item2.ToString(Formatting.None)).OrderBy(row => row, StringComparer.Ordinal).ToArray();
            if (!text(expected).SequenceEqual(text(actual)))
                throw new BridgeException("MutationVerificationFailed", "Other connections of this owner changed or the target is still present; the checkpoint is retained for recovery.");
        }

        private static JObject ConnectionReadback(List<Tuple<Connection, JObject>> rows, Connection target)
        {
            var matches = rows.Where(row => Equals(row.Item1, target)).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException("MutationVerificationFailed", "The changed connection is not present exactly once in the owner's readback.");
            return matches[0].Item2;
        }
    }
}
