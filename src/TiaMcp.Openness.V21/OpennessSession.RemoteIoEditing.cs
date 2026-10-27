using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly HashSet<string> ChannelWriteAttributes = new HashSet<string>(new[]
        { "ChannelActivation", "MeasuringType", "MeasuringRange", "InputDelay", "SubstituteValue", "DigitalInputDelay", "Smoothing" }, StringComparer.Ordinal);

        private static JArray InspectChannelEngineeringAttributes(Channel channel, ScanBudget budget, JArray warnings, string path)
        {
            var attributes = InspectionAttributes(channel, budget, warnings, path);
            foreach (var row in attributes.OfType<JObject>())
            {
                string name = row.Value<string>("name");
                row["writeSupportedByBridge"] = ChannelWriteAttributes.Contains(name);
                if (!ChannelWriteAttributes.Contains(name) || row["error"] != null || row.Value<bool?>("valueComplete") != true) continue;
                try
                {
                    object value = channel.GetAttribute(name);
                    row["nativeValueType"] = value?.GetType().FullName;
                    Type symbolic = value?.GetType().IsEnum == true ? value.GetType() : typeof(DeviceItem).Assembly.GetType("Siemens.Engineering.HW." + name);
                    if (symbolic?.IsEnum == true) row["nativeEnumValues"] = new JArray(Enum.GetNames(symbolic).Select(symbol => new JObject { ["name"] = symbol,
                        ["value"] = value?.GetType().IsEnum == true ? new JValue(symbol) : ChangeScalar(Convert.ChangeType(Enum.Parse(symbolic, symbol), value?.GetType() ?? typeof(long))) }));
                }
                catch (Exception) { row["metadataUnavailable"] = "Native channel enum metadata could not be captured."; }
            }
            return attributes;
        }

        private static int HardwareIndex(JObject payload, string name)
        {
            if (payload[name]?.Type != JTokenType.Integer || !int.TryParse(payload[name].ToString(), out int value) || value < 0)
                throw new BridgeException("InvalidRequest", name + " must be a nonnegative Int32 integer.");
            return value;
        }

        private DeviceItem PrepareHardwareMove(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            EnsureHardwareDeleteEligible(item.Item);
            var parent = item.Item.Container as DeviceItem ?? throw new BridgeException("UnsupportedCapability", "Slot moves require a pluggable module in an existing rack.");
            int slot = HardwareIndex(change.Payload, "positionNumber");
            if (item.Item.PositionNumber == slot) throw new BridgeException("InvalidRequest", "The requested slot is unchanged.");
            if (!parent.CanPlugMove(item.Item, slot)) throw new BridgeException("IncompatibleHardware", "Native CanPlugMove rejects the requested slot in the current rack.");
            if (preview != null) ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["positionBefore"] = item.Item.PositionNumber, ["positionAfter"] = slot, ["containerPolicy"] = "same native rack only" });
            return item.Item;
        }

        private Channel PrepareHardwareChannel(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            string type = ChangeText(change.Payload, "channelType", 64), ioType = ChangeText(change.Payload, "channelIoType", 64);
            int number = HardwareIndex(change.Payload, "channelNumber");
            var matches = item.Item.Channels.Where(channel => channel.Number == number && channel.Type.ToString() == type && channel.IoType.ToString() == ioType).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "ChannelNotFound" : "AmbiguousTarget", "hardwarePath, channelType, channelIoType and channelNumber must select one native channel.");
            var attributes = ChangeObject(change.Payload, "attributes");
            if (attributes.Count == 0 || attributes.Count > ChannelWriteAttributes.Count) throw new BridgeException("InvalidRequest", "At least one reviewed channel attribute is required.");
            var changes = PrepareAttributes(matches[0], attributes, ChannelWriteAttributes, out var converted);
            // Store native scalar representations so the preservation comparison
            // matches native numeric/enum values rather than JSON input spelling.
            foreach (var property in attributes.Properties().ToArray())
                attributes[property.Name] = ChangeScalar(converted[property.Name]);
            if (preview != null) preview["attributeChanges"] = changes;
            return matches[0];
        }

        private IoConnector PrepareIoAssignment(EngineeringChange change, Project project, JObject preview, out IoController controller)
        {
            var payload = change.Payload;
            var item = ChangeHardware(project, payload);
            var network = item.Item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Selected device has no native network interface.");
            int index = HardwareIndex(payload, "connectorIndex");
            var connectors = network.IoConnectors.ToArray();
            if (index >= connectors.Length) throw new BridgeException("ConnectorNotFound", "connectorIndex is outside the observed native connector collection.");
            var connector = connectors[index];
            if (connectors.Where((row, i) => i != index).Any(row => row.ConnectedToIoSystem != null))
                throw new BridgeException("UnsupportedCapability", "Shared-device and multi-controller assignments require separate qualification; all other interface connectors must be unassigned.");
            var plc = SelectPlc(change.Selector);
            payload["addressControllerOwnerPath"] = plc.Path.Substring(0, plc.Path.LastIndexOf("/plc/", StringComparison.Ordinal));
            string station = item.Path.Substring(0, item.Path.IndexOf("/items/", StringComparison.Ordinal));
            var stationBudget = new ScanBudget(50000);
            foreach (var peer in DeviceItems(project, stationBudget).Where(row => row.Path.StartsWith(station + "/items/", StringComparison.Ordinal)))
            {
                var peerNetwork = peer.Item.GetService<NetworkInterface>();
                if (peerNetwork != null && peerNetwork.IoConnectors.Any(other => !Equals(other, connector) && other.ConnectedToIoSystem != null))
                    throw new BridgeException("UnsupportedCapability", "Another connector on this remote station is assigned. Shared-device/multi-interface assignment changes are excluded from this route.");
                foreach (var address in peer.Item.Addresses)
                {
                    var owners = address.AddressControllers.ToArray();
                    if (owners.Length > 1 || owners.Any(owner => ControllerMatchesSelectedPlc(owner, plc.Software) != true))
                        throw new BridgeException("UnresolvedIoOwnership", "Every existing remote-station address must be unassigned or owned solely by the selected PLC.");
                }
            }
            if (stationBudget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish complete remote-station assignment ownership.");
            if (connector.ConnectedToIoSystem != null && ControllerMatchesSelectedPlc(connector.ConnectedToIoSystem.Parent, plc.Software) != true)
                throw new BridgeException("UnresolvedIoOwnership", "The existing I/O system must belong to the selected PLC.");
            controller = null;
            bool disconnect = payload.Value<bool?>("disconnect") == true;
            if (disconnect)
            {
                if (payload["controllerHardwarePath"] != null || payload["controllerIndex"] != null || payload["createIoSystemName"] != null) throw new BridgeException("InvalidRequest", "Disconnect cannot also select or create an I/O controller/system.");
                if (connector.ConnectedToIoSystem == null) throw new BridgeException("InvalidRequest", "Connector is already unassigned.");
            }
            else
            {
                var target = ChangeHardware(project, new JObject { ["hardwarePath"] = ChangeText(payload, "controllerHardwarePath", 4096) });
                var targetNetwork = target.Item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Selected controller hardware has no network interface.");
                int controllerIndex = HardwareIndex(payload, "controllerIndex");
                var controllers = targetNetwork.IoControllers.ToArray();
                if (controllerIndex >= controllers.Length) throw new BridgeException("ControllerNotFound", "controllerIndex is outside the observed native controller collection.");
                controller = controllers[controllerIndex];
                if (ControllerMatchesSelectedPlc(controller, plc.Software) != true) throw new BridgeException("UnresolvedIoOwnership", "Selected controller must resolve natively to the selected PLC.");
                var targetNodes = targetNetwork.Nodes.ToArray(); var sourceNodes = network.Nodes.ToArray();
                if (targetNodes.Length != 1 || sourceNodes.Length != 1 || targetNodes[0].ConnectedSubnet == null || !Equals(targetNodes[0].ConnectedSubnet, sourceNodes[0].ConnectedSubnet))
                    throw new BridgeException("NetworkPrerequisite", "Both interfaces must expose one node connected to the same native subnet; prepare network_configure first.");
                if (controller.IoSystem == null) ChangeText(payload, "createIoSystemName", 128);
                else if (payload["createIoSystemName"] != null) throw new BridgeException("InvalidRequest", "The controller already has an I/O system; omit createIoSystemName.");
                if (controller.IoSystem != null && Equals(connector.ConnectedToIoSystem, controller.IoSystem))
                    throw new BridgeException("InvalidRequest", "The connector already belongs to the selected controller's I/O system.");
            }
            if (preview != null) ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["connectorIndex"] = index, ["beforeIoSystem"] = connector.ConnectedToIoSystem?.Name,
                ["controllerHardwarePath"] = payload["controllerHardwarePath"]?.DeepClone(), ["controllerIndex"] = payload["controllerIndex"]?.DeepClone(),
                ["afterIoSystem"] = controller?.IoSystem?.Name ?? payload.Value<string>("createIoSystemName"), ["disconnect"] = disconnect });
            ValidateExpectedServerModule(change, project, item, preview);
            return connector;
        }

        private void ValidateExpectedServerModule(EngineeringChange change, Project project, HardwareEntry item, JObject preview)
        {
            var expected = RemoteIoCompanionContract.ExpectedRows(change.Payload);
            if (expected.Count == 0) return;
            if (change.Payload.Value<bool?>("disconnect") == true) throw new BridgeException("InvalidRequest", "Disconnect cannot create a server companion.");
            DeviceItem head = null;
            IEngineeringInstance cursor = item.Item;
            for (int depth = 0; cursor != null && depth < 64; depth++, cursor = cursor.Parent)
                if (cursor is DeviceItem candidate && candidate.TypeIdentifier == RemoteIoCompanionContract.HeadType) { head = candidate; break; }
            if (head == null) throw new BridgeException("UnsupportedCapability", "Explicit server generation is limited to the captured native IM 155-6AU02 V6.4 ancestor profile.");
            var rack = ChangeHardware(project, new JObject { ["hardwarePath"] = expected[0]["containerPath"].DeepClone() });
            if (!Equals(head.Container, rack.Item)) throw new BridgeException("InvalidRequest", "Server companion container must be the head's exact native rack.");
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(project, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish server-companion absence or rack occupancy.");
            string path = expected[0].Value<string>("path");
            if (hardware.Any(row => row.Path == path || row.Path.StartsWith(path + "/", StringComparison.Ordinal)))
                throw new BridgeException("ObjectExists", "Expected generated server subtree already exists; omit expectedServerModule.");
            if (hardware.Any(row => Equals(row.Item.Container, rack.Item) && (row.Item.PositionNumber == 1 || row.Item.TypeIdentifier == RemoteIoCompanionContract.ServerType)))
                throw new BridgeException("UnsupportedCapability", "The captured initial server profile requires empty rack slot 1 and no existing server module under another name; automatic slot shifts are not covered.");
            ValidateCatalogIdentifier(RemoteIoCompanionContract.ServerType);
            if (preview != null) ((JArray)preview["effects"]).Add(new JObject { ["expectedNativeCompanion"] = expected, ["basis"] = "Exact native-02 generated server/diagnostic-child contract; actual after state is never used to construct expectations." });
        }

        private void ApplyIoAssignment(EngineeringChange change, Project project)
        {
            var connector = PrepareIoAssignment(change, project, null, out var controller);
            var previous = connector.ConnectedToIoSystem;
            if (change.Payload.Value<bool?>("disconnect") == true)
            {
                connector.DisconnectFromIoSystem();
                if (connector.ConnectedToIoSystem != null) throw new BridgeException("MutationVerificationFailed", "Connector disconnect did not persist.");
            }
            else
            {
                var system = controller.IoSystem ?? controller.CreateIoSystem(change.Payload.Value<string>("createIoSystemName"));
                if (connector.ConnectedToIoSystem != null && !Equals(connector.ConnectedToIoSystem, system)) connector.DisconnectFromIoSystem();
                connector.ConnectToIoSystem(system);
                if (!Equals(connector.ConnectedToIoSystem, system) || !system.ConnectedIoDevices.Any(candidate => Equals(candidate, connector)))
                    throw new BridgeException("MutationVerificationFailed", "Native forward/reverse I/O-system readback differs from the selected association.");
            }
            if (previous != null && !Equals(previous, connector.ConnectedToIoSystem) && previous.ConnectedIoDevices.Any(candidate => Equals(candidate, connector)))
                throw new BridgeException("MutationVerificationFailed", "The previous I/O system still contains the moved or disconnected connector.");
        }

        private JArray CaptureHardwareEngineering(Project project, bool cpuMemoryBytes = false)
        {
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(project, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Complete hardware preservation capture exceeds 50000 items.");
            string Owner(IEngineeringInstance value)
            {
                for (int depth = 0; value != null && depth < 64; depth++, value = value.Parent)
                    if (value is DeviceItem native)
                    {
                        var matches = hardware.Where(row => Equals(row.Item, native)).Take(2).ToArray();
                        if (matches.Length == 1) return matches[0].Path;
                        break;
                    }
                throw new BridgeException("DependencyCoverageIncomplete", "Native hardware association owner cannot be resolved uniquely.");
            }
            JObject Attributes(IEngineeringObject target, HashSet<string> allowlist)
            {
                var attributes = new JObject();
                foreach (var info in target.GetAttributeInfos().Where(info => allowlist.Contains(info.Name) && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0).OrderBy(info => info.Name, StringComparer.Ordinal))
                {
                    if (!budget.Take()) throw new BridgeException("TraversalLimit", "Hardware preservation metadata exceeds its 50000-value budget.");
                    attributes[info.Name] = ChangeScalar(target.GetAttribute(info.Name));
                }
                return attributes;
            }
            JObject System(IoSystem system)
            {
                if (system == null) return new JObject { ["state"] = "notAssigned" };
                string owner = Owner(system.Parent);
                var native = hardware.Single(row => row.Path == owner).Item.GetService<NetworkInterface>();
                var controllers = native.IoControllers.ToArray();
                int index = Array.FindIndex(controllers, candidate => Equals(candidate, system.Parent));
                if (index < 0) throw new BridgeException("DependencyCoverageIncomplete", "I/O system parent is not in its native controller collection.");
                var subnet = InspectSubnetReference(project, system.Subnet);
                if (subnet.Value<string>("resolution") != "resolved") throw new BridgeException("DependencyCoverageIncomplete", "I/O-system subnet is unresolved.");
                return new JObject { ["state"] = "assigned", ["name"] = system.Name, ["number"] = system.Number, ["ownerHardwarePath"] = owner, ["controllerIndex"] = index, ["subnet"] = subnet };
            }
            var rows = new JArray();
            foreach (var entry in hardware.OrderBy(row => row.Path, StringComparer.Ordinal))
            {
                var item = entry.Item;
                var row = new JObject { ["path"] = entry.Path, ["name"] = item.Name, ["typeIdentifier"] = HardwareEngineeringRules.MetadataString(item.TypeIdentifier), ["positionNumber"] = item.PositionNumber,
                    ["containerPath"] = HardwareEngineeringRules.MetadataString(item.Container is DeviceItem container ? Owner(container) : null),
                    ["attributes"] = Attributes(item, cpuMemoryBytes ? HardwareCpuMemoryPreservedAttributes : HardwareWriteAttributes),
                    ["channels"] = new JArray(item.Channels.OrderBy(channel => channel.Type).ThenBy(channel => channel.IoType).ThenBy(channel => channel.Number).Select(channel => new JObject { ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["attributes"] = Attributes(channel, ChannelWriteAttributes) })),
                    ["addresses"] = new JArray(item.Addresses.Select(address => new JObject { ["ioType"] = address.IoType.ToString(), ["startAddress"] = address.StartAddress, ["lengthBits"] = address.Length,
                        ["owners"] = new JArray(address.AddressControllers.Select(owner => Owner(owner)).OrderBy(value => value, StringComparer.Ordinal)) })) };
                var network = item.GetService<NetworkInterface>();
                if (network != null) row["network"] = new JObject { ["nodes"] = new JArray(network.Nodes.Select(NodeSnapshot)),
                    ["controllers"] = new JArray(network.IoControllers.Select((controller, index) => new JObject { ["index"] = index, ["system"] = System(controller.IoSystem) })),
                    ["connectors"] = new JArray(network.IoConnectors.Select((connector, index) => new JObject { ["index"] = index, ["system"] = System(connector.ConnectedToIoSystem) })) };
                rows.Add(row);
            }
            return rows;
        }

        private void PrepareHardwarePreservation(EngineeringChange change, Project project, JObject preview)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            change.HardwareBeforeManifest = CaptureHardwareEngineering(project, change.Kind == "hardware_cpu_memory_bytes");
            string path = Path.Combine(change.Directory, "hardware-before.json");
            File.WriteAllText(path, change.HardwareBeforeManifest.ToString(Formatting.Indented));
            change.Backups.Add(new JObject { ["role"] = "configured-hardware-preservation", ["artifact"] = ChangeArtifact(path) });
            preview["preservation"] = new JObject { ["policyVersion"] = 1, ["operation"] = change.Kind,
                ["covered"] = "All observed hardware identities/slots, reviewed hardware/channel attributes, configured address ranges/owners, node configuration and exact I/O-system assignments; fail closed on capture failure.",
                ["limitations"] = "Unexposed vendor payloads, non-allowlisted parameters, physical wiring, shared-device/multi-controller behavior and runtime are not qualified." };
        }

        private void VerifyHardwarePreservation(EngineeringChange change, Project project)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            var after = CaptureHardwareEngineering(project, change.Kind == "hardware_cpu_memory_bytes");
            string path = Path.Combine(change.Directory, "hardware-after.json");
            File.WriteAllText(path, after.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "configured-hardware-preservation", ["artifact"] = ChangeArtifact(path) });
            HardwareEngineeringRules.RequirePreserved(change.Kind, change.Payload, change.HardwareBeforeManifest, after);
            change.Stages.Add("configured_hardware_preservation_verified");
        }

        private void VerifyHardwareRecovery(EngineeringChange change, Project project)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            var restored = CaptureHardwareEngineering(project, change.Kind == "hardware_cpu_memory_bytes");
            string path = Path.Combine(change.Directory, "hardware-restored.json");
            File.WriteAllText(path, restored.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "configured-hardware-recovery", ["artifact"] = ChangeArtifact(path) });
            if (!JToken.DeepEquals(change.HardwareBeforeManifest, restored)) throw new BridgeException("RecoveryVerificationFailed", "Retrieved hardware metadata differs from the exact prechange configured manifest.");
            change.Stages.Add("configured_hardware_recovery_verified");
        }
    }
}
