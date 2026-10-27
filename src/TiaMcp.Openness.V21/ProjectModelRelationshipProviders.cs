#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Indexing
{
    // Siemens-free adapters from existing native inspection results to explorer
    // edges. Endpoints resolve only through exact captured object paths.
    internal static class ProjectModelRelationshipProviders
    {
        private static readonly string[] PlcObjectKinds = { "plc", "blockGroup", "systemBlockGroup", "block", "typeGroup", "systemTypeGroup", "type",
            "tagGroup", "tagTable", "tag", "constant", "systemConstant", "technologyGroup", "softwareUnitGroup",
            "watchTableGroup", "watchTable", "forceTable", "externalSourceGroup", "externalSource" };

        // Every node kind the native capture emits has a declared provider or an
        // explicit unsupported reason; unknown kinds are never silently empty.
        internal static JObject Scope(string kind)
        {
            string[] providers, edges; string reason = null;
            if (kind == "hardware") { providers = new[] { "configuredIo", "configuredNetwork" }; edges = new[] { "configuredIo", "connectedSubnet", "ioSystemController", "ioSystemDevice" }; }
            else if (kind == "subnet" || kind == "ioSystem") { providers = new[] { "configuredNetwork" }; edges = new[] { "connectedSubnet", "ioSystemController", "ioSystemDevice" }; }
            else if (kind == "softwareUnit" || kind == "safetyUnit") { providers = new[] { "nativeCrossReference", "softwareUnitRelations" }; edges = new[] { "calls", "reads", "writes", "instanceOf", "usesType", "dependsOn", "unitRelation" }; }
            else if (kind == "technologyObject") { providers = new[] { "nativeCrossReference", "technologyConnections" }; edges = new[] { "calls", "reads", "writes", "instanceOf", "usesType", "dependsOn", "technologyConnection" }; }
            else if (PlcObjectKinds.Contains(kind)) { providers = new[] { "nativeCrossReference" }; edges = kind == "tag" ? new[] { "calls", "reads", "writes", "instanceOf", "usesType", "dependsOn", "configuredIo" } : new[] { "calls", "reads", "writes", "instanceOf", "usesType", "dependsOn" }; }
            else if ((kind ?? "").StartsWith("hmi", StringComparison.OrdinalIgnoreCase)) { providers = new[] { "hmi.relationships" }; edges = new[] { "hmiBinding", "screenNavigation", "libraryVersion" }; }
            else
            {
                providers = new string[0]; edges = new string[0];
                reason = kind == "project" || kind == "folder" || kind == "deviceGroup" || kind == "namespace"
                    ? "Containment is the captured tree itself; this organizational node has no native relationship service."
                    : kind == "device" ? "A device has no native relationship service; select its hardware items for configured I/O and network associations."
                    : kind == "unsupportedSoftware" ? "This software family has no explorer provider; its device ancestry is not a content relationship."
                    : kind == "alarmTextList" ? "V21 exposes no alarm-to-text-list references; plc_alarm_inspect reads text-list entries from the native export."
                    : "No relationship provider is registered for this node kind.";
            }
            return new JObject { ["kind"] = kind, ["status"] = providers.Length == 0 ? "unsupported" : "provided",
                ["providers"] = new JArray(providers), ["edgeKinds"] = new JArray(edges), ["reason"] = reason,
                ["projectCompleteness"] = "notEstablished" };
        }

        // Input: an existing hardware_inspect envelope (configured network rows).
        internal static JArray NetworkEdges(JObject inspection, Func<string, JObject> node, string captured)
        {
            var edges = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var item in (inspection?["items"] as JArray ?? new JArray()).OfType<JObject>())
            {
                string path = item.Value<string>("path");
                if (!(item["network"] is JObject network)) continue;
                foreach (var row in (network["nodes"] as JArray ?? new JArray()).OfType<JObject>())
                    if (row["subnet"] is JObject subnet && subnet.Value<string>("state") == "connected")
                        Add(edges, Network("connectedSubnet", path, subnet.Value<string>("path"), subnet.Value<string>("resolution") == "resolved",
                            "Interface node " + row.Value<string>("name"), row, node, captured));
                foreach (var row in (network["ioControllers"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    if (!(row["ioSystem"] is JObject system) || system.Value<string>("state") != "assigned") continue;
                    string owner = system["controllerOwner"]?.Value<string>("resolution") == "resolved" ? system["controllerOwner"].Value<string>("ownerHardwarePath") : null;
                    Add(edges, Network("ioSystemController", path, system.Value<string>("path"), owner == path, "IO controller " + row.Value<int?>("controllerIndex"), row, node, captured));
                    // The controller side names its assigned devices; record them under the same identity the device side produces.
                    foreach (var device in (row["assignedConnectors"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string devicePath = device.Value<string>("resolution") == "resolved" ? device.Value<string>("ownerHardwarePath") : null;
                        Add(edges, Network("ioSystemDevice", devicePath, system.Value<string>("path"), devicePath != null, "Assigned IO device of " + path, device, node, captured));
                    }
                }
                foreach (var row in (network["ioConnectors"] as JArray ?? new JArray()).OfType<JObject>())
                    if (row["connectedToIoSystem"] is JObject system && system.Value<string>("state") == "assigned")
                        Add(edges, Network("ioSystemDevice", path, system.Value<string>("path"), true, "IO connector " + row.Value<int?>("connectorIndex"), row, node, captured));
            }
            return new JArray(edges.Values);
        }

        private static JObject Network(string kind, string sourcePath, string targetPath, bool ownerVerified, string location, JObject native, Func<string, JObject> node, string captured)
        {
            var source = sourcePath == null ? null : node(sourcePath);
            var target = targetPath == null ? null : node(targetPath);
            bool resolved = ownerVerified && source != null && target != null;
            return Edge("net-", kind, sourcePath + "\n" + (targetPath ?? native.ToString(Formatting.None)), source, target, resolved,
                resolved ? null : "Native association owner or configured network object is not an exact captured node; names are not substituted.",
                "configuredNetwork", sourcePath, location, captured, "configuredNetwork", native);
        }

        // Input: an existing technology_read envelope. Only references the native
        // reader resolved to an exact hardware path become resolved endpoints.
        internal static JArray TechnologyEdges(JObject technology, JObject owner, Func<string, JObject> node, string captured)
        {
            var edges = new Dictionary<string, JObject>(StringComparer.Ordinal);
            string ownerPath = owner.Value<string>("objectPath");
            foreach (var row in (technology?["hardwareConnections"] as JArray ?? new JArray()).OfType<JObject>())
                foreach (var reference in row.DescendantsAndSelf().OfType<JObject>().Where(o => o.Value<string>("valueSemantics") == "engineeringReference"))
                {
                    if (reference.Parent?.Parent is JObject container && container.Value<string>("valueSemantics") == "engineeringReference") continue;
                    string kind = reference.Value<string>("kind");
                    string path = reference.Value<string>("resolution") != "resolved" ? null
                        : kind == "hardware" ? reference.Value<string>("hardwarePath") : kind == "channel" ? reference["parent"]?.Value<string>("hardwarePath") : null;
                    var target = path == null ? null : node(path);
                    var native = new JObject { ["role"] = row["role"], ["service"] = row["service"], ["field"] = reference.Path, ["reference"] = reference.DeepClone() };
                    Add(edges, Edge("to-", "technologyConnection", ownerPath + "\n" + row.Value<string>("observationPath") + "\n" + reference.Path,
                        ModelEndpoint(owner), target, target != null,
                        target != null ? null : path == null ? "Native reference has no exact explorer hardware path (tags, blocks and unresolved references keep provider identity only)." : "Referenced hardware is outside the captured scope.",
                        "technologyConnections", ownerPath, row.Value<string>("observationPath"), captured, "technologyHardwareConnections", native));
                }
            return new JArray(edges.Values);
        }

        // V21 stores unit relations as native names. A target resolves only when the
        // complete captured PLC scope has exactly one object of the documented type.
        internal static JArray UnitRelationEdges(JObject unit, IEnumerable<JObject> nodes, bool hierarchyComplete, string captured)
        {
            var edges = new Dictionary<string, JObject>(StringComparer.Ordinal);
            string plc = unit.Value<string>("plcPath");
            var relations = unit["metadata"]?["relations"] as JArray;
            if (relations == null) return new JArray();
            var all = nodes.ToArray();
            foreach (var relation in relations.OfType<JObject>())
            {
                string type = relation.Value<string>("relationType"), name = relation.Value<string>("relatedObject");
                var candidates = all.Where(n => n.Value<string>("plcPath") == plc && n.Value<string>("name") == name && n.Value<string>("nodeId") != unit.Value<string>("nodeId")
                    && (type == "SoftwareUnit" ? n.Value<string>("kind") == "softwareUnit" || n.Value<string>("kind") == "safetyUnit"
                        : type == "NonUnitDB" && n.Value<string>("kind") == "block" && n.Value<string>("scopePath") == plc && (n["metadata"]?.Value<string>("kind") ?? "").EndsWith("DB", StringComparison.Ordinal))).Take(2).ToArray();
                bool resolved = hierarchyComplete && candidates.Length == 1;
                string reason = resolved ? null : !hierarchyComplete ? "Captured hierarchy is partial; a unique native name cannot be established."
                    : candidates.Length == 0 ? "No captured object of the relation's native type has this name in the same PLC." : "Native relation name is ambiguous in the captured PLC scope.";
                var edge = Edge("unit-", "unitRelation", unit.Value<string>("objectPath") + "\n" + type + "\n" + name, ModelEndpoint(unit), resolved ? candidates[0] : null, resolved, reason,
                    "softwareUnitRelations", unit.Value<string>("objectPath"), type + ":" + name, captured, "softwareUnitRelations", relation.DeepClone());
                if (resolved) edge["evidence"]["resolutionBasis"] = "uniqueNativeNameOfRelationTypeInCompletePlcCapture";
                Add(edges, edge);
            }
            return new JArray(edges.Values);
        }

        private static JObject ModelEndpoint(JObject node) => new JObject { ["nodeId"] = node["nodeId"], ["objectPath"] = node["objectPath"], ["resolution"] = "resolved" };

        private static JObject Edge(string prefix, string kind, string identity, JObject source, JObject target, bool resolved, string reason,
            string provider, string sourcePath, string location, string captured, string revisionScope, JToken native)
        {
            return new JObject { ["edgeId"] = prefix + Hash(kind + "\n" + identity).Substring(0, 20), ["kind"] = kind,
                ["source"] = source == null ? null : ModelEndpoint(source), ["target"] = target == null ? null : ModelEndpoint(target),
                ["unresolved"] = resolved ? null : new JObject { ["reason"] = reason, ["native"] = native.DeepClone() },
                ["evidence"] = new JObject { ["provider"] = provider, ["origin"] = resolved ? "nativeConfirmed" : "unresolved", ["resolution"] = resolved ? "resolved" : "unresolved",
                    ["sourcePath"] = sourcePath, ["location"] = location, ["capturedAtUtc"] = captured,
                    ["contentRevision"] = new JObject { ["status"] = "unavailable", ["scope"] = revisionScope }, ["native"] = native.DeepClone() } };
        }

        private static void Add(Dictionary<string, JObject> edges, JObject edge)
        {
            if (!edges.ContainsKey(edge.Value<string>("edgeId"))) edges.Add(edge.Value<string>("edgeId"), edge);
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}
