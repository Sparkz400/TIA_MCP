using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static readonly HashSet<string> ChannelWriteAttributes = new HashSet<string>(new[]
        // OperatingMode MotionControl lets a technology object use a TM PosInput/TM Count channel. The rising-edge hardware
        // interrupt of a digital input channel is its ...Active flag plus ...Interrupt, a link to a hardware interrupt OB.
        { "ChannelActivation", "MeasuringType", "MeasuringRange", "InputDelay", "SubstituteValue", "DigitalInputDelay", "Smoothing", "OperatingMode",
          ChannelInterruptActive, ChannelInterruptOb }, StringComparer.Ordinal);
        private const string ChannelInterruptActive = "HardwareInterruptRisingEdgeActive", ChannelInterruptOb = "HardwareInterruptRisingEdgeInterrupt";

        // A linked OB is identified by its block name, unique within its PLC.
        private static JToken ChannelValue(object value) => value is PlcBlock block ? (JToken)block.Name : ChangeScalar(value);

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

        private Channel HardwareChannel(Project project, JObject payload) => HardwareChannel(project, payload, out _);

        private Channel HardwareChannel(Project project, JObject payload, out HardwareEntry item)
        {
            item = ChangeHardware(project, payload);
            string type = ChangeText(payload, "channelType", 64), ioType = ChangeText(payload, "channelIoType", 64);
            int number = HardwareIndex(payload, "channelNumber");
            var matches = item.Item.Channels.Where(channel => channel.Number == number && channel.Type.ToString() == type && channel.IoType.ToString() == ioType).Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "ChannelNotFound" : "AmbiguousTarget", "hardwarePath, channelType, channelIoType and channelNumber must select one native channel.");
            return matches[0];
        }

        private Channel PrepareHardwareChannel(EngineeringChange change, Project project, JObject preview, out PlcBlock interrupt)
        {
            var channel = HardwareChannel(project, change.Payload, out var module);
            var attributes = ChangeObject(change.Payload, "attributes");
            if (attributes.Count == 0 || attributes.Count > ChannelWriteAttributes.Count) throw new BridgeException("InvalidRequest", "At least one reviewed channel attribute is required.");
            var scalars = ChannelScalars(attributes);
            // The OB link is declared only while its flag is on, so a profile captured with the flag off cannot list it; its native check follows.
            HardwareEngineeringRules.RequireProfileWritable(ItemProfile(module.Item, out string profilePath), profilePath,
                new JObject { ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["number"] = channel.Number }, scalars.Properties().Select(property => property.Name));
            var changes = PrepareAttributes(channel, scalars, ChannelWriteAttributes, out var converted);
            // Store native scalar representations so the preservation comparison
            // matches native numeric/enum values rather than JSON input spelling.
            foreach (var property in scalars.Properties())
                attributes[property.Name] = ChangeScalar(converted[property.Name]);
            // V21 declares the OB link only while the flag is on (DI 16x24VDC HF, 30 October 2026), so both change together.
            bool? flag = converted.TryGetValue(ChannelInterruptActive, out var active) ? Equals(active, true) || active?.ToString() == "Active" : (bool?)null;
            if (flag.HasValue && flag.Value != (attributes[ChannelInterruptOb] != null))
                throw new BridgeException("InvalidRequest", flag.Value ? "Enabling " + ChannelInterruptActive + " requires " + ChannelInterruptOb + " (the hardware interrupt OB) in the same request."
                    : "Disabling " + ChannelInterruptActive + " removes its OB link; omit " + ChannelInterruptOb + ".");
            interrupt = attributes[ChannelInterruptOb] == null ? null : ChannelInterruptBlock(project, module, channel, attributes[ChannelInterruptOb], changes, flag == true);
            if (preview != null) preview["attributeChanges"] = changes;
            return channel;
        }

        private static JObject ChannelScalars(JObject attributes) => new JObject(attributes.Properties().Where(property => property.Name != ChannelInterruptOb)
            .Select(property => new JProperty(property.Name, property.Value.DeepClone())));

        // The OB link takes a hardware interrupt OB of the one PLC in the module's own device, named exactly.
        private PlcBlock ChannelInterruptBlock(Project project, HardwareEntry module, Channel channel, JToken value, JArray changes, bool enabling)
        {
            string name = value.Type == JTokenType.String ? value.Value<string>() : null;
            if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidRequest", ChannelInterruptOb + " takes the exact name of a hardware interrupt OB.");
            int declared = channel.GetAttributeInfos().Count(info => info.Name == ChannelInterruptOb);
            // Enabling the flag in the same request declares the link; apply checks its access after the flag is written.
            if (!(declared == 0 && enabling) && !ChannelInterruptWritable(channel))
                throw new BridgeException("AttributeNotWritable", "The channel declares no single readable and writable " + ChannelInterruptOb + "; V21 declares it only while " + ChannelInterruptActive + " is true.");
            string device = module.Path.Substring(0, module.Path.IndexOf("/items/", StringComparison.Ordinal));
            var budget = new ScanBudget(50000);
            var plcs = DeviceItems(project, budget).Where(entry => entry.Path.StartsWith(device + "/items/", StringComparison.Ordinal))
                .Select(entry => new { entry.Path, Software = entry.Item.GetService<SoftwareContainer>()?.Software as PlcSoftware }).Where(row => row.Software != null).Take(2).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC lookup of the module's device is incomplete.");
            var blocks = plcs.Length != 1 ? new BlockEntry[0] : AllBlocks(new PlcSelection { Project = project, Software = plcs[0].Software, Path = plcs[0].Path + "/plc/" + Segment(plcs[0].Software.Name) }, budget)
                .Where(block => string.Equals(block.Item.Name, name, StringComparison.Ordinal)).Take(2).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Block lookup of the module's PLC is incomplete.");
            HardwareEngineeringRules.RequireInterruptOb(name, plcs.Length, blocks.Select(block => block.Item is OB ob ? ob.SecondaryType : null).ToArray());
            changes.Add(new JObject { ["name"] = ChannelInterruptOb, ["type"] = blocks[0].Item.GetType().FullName, ["before"] = declared == 0 ? null : ChannelValue(channel.GetAttribute(ChannelInterruptOb)),
                ["after"] = name, ["blockPath"] = blocks[0].Path });
            return blocks[0].Item;
        }

        private static bool ChannelInterruptWritable(Channel channel)
        {
            var declared = channel.GetAttributeInfos().Where(info => info.Name == ChannelInterruptOb).Take(2).ToArray();
            return declared.Length == 1 && (declared[0].AccessMode & EngineeringAttributeAccessMode.Read) != 0 && (declared[0].AccessMode & EngineeringAttributeAccessMode.Write) != 0;
        }

        private void ApplyHardwareChannel(EngineeringChange change, Project project)
        {
            var channel = PrepareHardwareChannel(change, project, null, out var interrupt);
            // Scalars first: the interrupt flag precedes its OB link.
            ApplyAttributes(channel, ChannelScalars(ChangeObject(change.Payload, "attributes")), ChannelWriteAttributes);
            if (interrupt == null) return;
            if (!ChannelInterruptWritable(channel)) throw new BridgeException("AttributeNotWritable", "After enabling " + ChannelInterruptActive + ", the channel declares no single readable and writable " + ChannelInterruptOb + ". Restore the checkpoint.");
            channel.SetAttribute(ChannelInterruptOb, interrupt);
            if (!JToken.DeepEquals(ChannelValue(channel.GetAttribute(ChannelInterruptOb)), interrupt.Name))
                throw new BridgeException("MutationVerificationFailed", "The channel's hardware interrupt OB readback differs from the request.");
        }

        private IoConnector PrepareIoAssignment(EngineeringChange change, Project project, JObject preview, out IoController controller, out HardwareEntry[] sharedItems)
        {
            var payload = change.Payload;
            var item = ChangeHardware(project, payload);
            var network = item.Item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Selected device has no native network interface.");
            int index = HardwareIndex(payload, "connectorIndex");
            var connectors = network.IoConnectors.ToArray();
            if (index >= connectors.Length) throw new BridgeException("ConnectorNotFound", "connectorIndex is outside the observed native connector collection.");
            var connector = connectors[index];
            bool disconnect = payload.Value<bool?>("disconnect") == true;
            // A shared device: a further connector of the same interface joins (or leaves) another controller's I/O system.
            bool shared = payload.Property("sharedDevice") != null && ExplicitChangeBool(payload, "sharedDevice");
            var others = connectors.Where((row, i) => i != index && row.ConnectedToIoSystem != null).ToArray();
            if (!shared && others.Length != 0)
                throw new BridgeException("UnsupportedCapability", "Another connector of this interface is assigned; adding or removing a further controller is a shared-device assignment (sharedDevice: true).");
            if (!shared && payload["sharedAddressControllerOwnerPaths"] != null) throw new BridgeException("InvalidRequest", "sharedAddressControllerOwnerPaths is recorded by the bridge for sharedDevice only.");
            if (shared && others.Length == 0) throw new BridgeException("InvalidRequest", "sharedDevice needs another connector of this interface assigned to another controller's I/O system.");
            if (shared && !disconnect && connector.ConnectedToIoSystem != null) throw new BridgeException("InvalidRequest", "A shared-device assignment connects an unassigned connector; disconnect this one first.");
            if (shared && payload["expectedServerModule"] != null) throw new BridgeException("InvalidRequest", "The generated server module belongs to the first assignment; omit expectedServerModule.");
            var plc = SelectPlc(change.Selector);
            payload["addressControllerOwnerPath"] = plc.Path.Substring(0, plc.Path.LastIndexOf("/plc/", StringComparison.Ordinal));
            string station = item.Path.Substring(0, item.Path.IndexOf("/items/", StringComparison.Ordinal));
            var stationBudget = new ScanBudget(50000);
            var hardware = DeviceItems(project, stationBudget).ToArray();
            if (stationBudget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish complete remote-station assignment ownership.");
            var plcItems = PlcItems(hardware);
            var peers = others.Select(other => OwningPlcItem(other.ConnectedToIoSystem.Parent, plcItems) ?? throw new BridgeException("UnresolvedIoOwnership", "Another connector's I/O system resolves to no PLC of the project.")).ToArray();
            var allowed = new[] { plc.Software }.Concat(peers.Select(peer => (PlcSoftware)peer.Item.GetService<SoftwareContainer>().Software)).ToArray();
            if (allowed.Skip(1).Any(software => software.Equals(plc.Software))) throw new BridgeException("InvalidRequest", "The selected PLC already controls this IO device through another connector of the interface.");
            var peerPaths = peers.Select(peer => peer.Path).Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (shared) payload["sharedAddressControllerOwnerPaths"] = new JArray(peerPaths);
            // A disconnect's items go to the remaining PLC (the only one, or remainingOwnerPath); the connection's go to the selected one.
            string remaining = payload["remainingOwnerPath"] == null ? null : ChangeText(payload, "remainingOwnerPath", 4096);
            if (remaining != null && !(shared && disconnect && peerPaths.Contains(remaining, StringComparer.Ordinal)))
                throw new BridgeException("InvalidRequest", "remainingOwnerPath names the PLC item of another connector that stays assigned, for a sharedDevice disconnect only: " + string.Join(", ", peerPaths) + ".");
            string newOwner = disconnect ? remaining ?? (peerPaths.Length == 1 ? peerPaths[0] : null) : payload.Value<string>("addressControllerOwnerPath");
            sharedItems = SharedSubmodules(project, payload, station, shared, disconnect, hardware, newOwner);
            foreach (var peer in hardware.Where(row => row.Path.StartsWith(station + "/items/", StringComparison.Ordinal)))
            {
                var peerNetwork = peer.Item.GetService<NetworkInterface>();
                if (peerNetwork != null && !(shared && Equals(peer.Item, item.Item)) && peerNetwork.IoConnectors.Any(other => !Equals(other, connector) && other.ConnectedToIoSystem != null))
                    throw new BridgeException("UnsupportedCapability", "Another interface on this remote station is assigned. Multi-interface assignment changes are excluded from this route.");
                foreach (var address in peer.Item.Addresses)
                {
                    var owners = address.AddressControllers.ToArray();
                    if (owners.Length > 1 || owners.Any(owner => !allowed.Any(software => ControllerMatchesSelectedPlc(owner, software) == true)))
                        throw new BridgeException("UnresolvedIoOwnership", "Every existing remote-station address must be unassigned or owned solely by the selected PLC" + (shared ? " or by the PLC of another assigned connector." : "."));
                }
            }
            if (connector.ConnectedToIoSystem != null && ControllerMatchesSelectedPlc(connector.ConnectedToIoSystem.Parent, plc.Software) != true)
                throw new BridgeException("UnresolvedIoOwnership", "The existing I/O system must belong to the selected PLC.");
            controller = null;
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
                ["afterIoSystem"] = controller?.IoSystem?.Name ?? payload.Value<string>("createIoSystemName"), ["disconnect"] = disconnect, ["sharedDevice"] = shared,
                ["sharedSubmodules"] = sharedItems.Length == 0 ? null : new JArray(sharedItems.Select(entry => new JObject { ["hardwarePath"] = entry.Path,
                    ["assignmentTargetBefore"] = SharedTargetPath(entry.Item, hardware), ["assignmentTargetAfter"] = newOwner })) });
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

        // V21 compiles a further IO controller of a shared device only with at least one submodule assigned to it ("Connected PLCs must
        // have at least one assigned submodule as a shared device", discovery hw3), so a sharedDevice connection names 1 to 64 items of
        // the station (sharedSubmodules) whose SharedDeviceAssignmentTarget becomes the selected PLC item in the same change. Each must own
        // I/O addresses (itself or through a subitem) and not be assigned to that PLC yet. A sharedDevice disconnect fails the device
        // compile while an item stays assigned to the leaving PLC ("An attempt was made to compile a submodule that is not assigned",
        // discovery rd1), so it names every such item, and they go to the one remaining PLC (newOwner) in the same change.
        private HardwareEntry[] SharedSubmodules(Project project, JObject payload, string station, bool shared, bool disconnect, HardwareEntry[] hardware, string newOwner)
        {
            var list = payload["sharedSubmodules"];
            string owner = payload.Value<string>("addressControllerOwnerPath");
            var leaving = shared && disconnect ? hardware.Where(row => row.Path.StartsWith(station + "/items/", StringComparison.Ordinal) && SharedTargetPath(row.Item, hardware) == owner).ToArray() : new HardwareEntry[0];
            if (!shared || disconnect && leaving.Length == 0)
            {
                if (list != null || payload["remainingOwnerPath"] != null) throw new BridgeException("InvalidRequest", "sharedSubmodules and remainingOwnerPath apply only to a sharedDevice connection, or to its disconnect while items are assigned to the leaving PLC.");
                return new HardwareEntry[0];
            }
            if (disconnect && newOwner == null) throw new BridgeException("InvalidRequest", "Several other PLCs control the device; name the one that takes the leaving PLC's items in remainingOwnerPath.");
            if (!(list is JArray paths) || paths.Count == 0 || paths.Count > 64 || paths.Any(path => path.Type != JTokenType.String) || paths.Select(path => path.Value<string>()).Distinct(StringComparer.Ordinal).Count() != paths.Count)
                throw new BridgeException("InvalidRequest", disconnect ? "A shared disconnect needs sharedSubmodules: the 1 to 64 exact hardware paths of the items assigned to the leaving PLC, which go to " + newOwner + "."
                    : "A sharedDevice connection needs sharedSubmodules: 1 to 64 distinct exact hardware paths of the station's items to assign to the selected PLC.");
            var named = paths.Select(path =>
            {
                if (!path.Value<string>().StartsWith(station + "/items/", StringComparison.Ordinal)) throw new BridgeException("InvalidRequest", "Every sharedSubmodules path must belong to the selected IO device's station.");
                var entry = ChangeHardware(project, new JObject { ["hardwarePath"] = path.DeepClone() });
                if (!Writable(entry.Item, SharedTarget)) throw new BridgeException("AttributeNotWritable", "A sharedSubmodules item declares no readable and writable " + SharedTarget + ".");
                HardwareEngineeringRules.RequireProfileWritable(ItemProfile(entry.Item, out string profilePath), profilePath, null, new[] { SharedTarget });
                if (!hardware.Any(row => (row.Path == entry.Path || row.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal)) && row.Item.Addresses.Any(address => address.Length > 0)))
                    throw new BridgeException("InvalidRequest", "A sharedSubmodules item must own I/O addresses itself or through a subitem: " + entry.Path);
                if (!disconnect && SharedTargetPath(entry.Item, hardware) == owner) throw new BridgeException("InvalidRequest", "A sharedSubmodules item is already assigned to the selected PLC: " + entry.Path);
                if (disconnect && SharedTargetPath(entry.Item, hardware) != owner) throw new BridgeException("InvalidRequest", "A shared disconnect names only items assigned to the leaving PLC: " + entry.Path);
                return entry;
            }).ToArray();
            var missing = leaving.Where(row => !named.Any(entry => row.Path == entry.Path || row.Path.StartsWith(entry.Path + "/", StringComparison.Ordinal))).Select(row => row.Path).ToArray();
            if (missing.Length != 0) throw new BridgeException("InvalidRequest", "A shared disconnect names every item assigned to the leaving PLC in sharedSubmodules; missing: " + string.Join(", ", missing.Take(8)) + ".");
            return named;
        }

        private void ApplyIoAssignment(EngineeringChange change, Project project)
        {
            var connector = PrepareIoAssignment(change, project, null, out var controller, out var sharedItems);
            var previous = connector.ConnectedToIoSystem;
            void Assign(JToken cpuPath)
            {
                var cpu = ChangeHardware(project, new JObject { ["hardwarePath"] = cpuPath.DeepClone() }).Item;
                foreach (var entry in sharedItems)
                {
                    var item = ChangeHardware(project, new JObject { ["hardwarePath"] = entry.Path }).Item;
                    item.SetAttribute(SharedTarget, cpu);
                    if (!Equals(item.GetAttribute(SharedTarget), cpu)) throw new BridgeException("MutationVerificationFailed", "A shared submodule does not read back its new PLC as its assignment target.");
                }
            }
            if (change.Payload.Value<bool?>("disconnect") == true)
            {
                // The leaving PLC's items go to the remaining PLC while the connector still holds both, then the connector leaves.
                if (sharedItems.Length != 0) Assign(change.Payload["remainingOwnerPath"] ?? change.Payload["sharedAddressControllerOwnerPaths"][0]);
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
                if (sharedItems.Length != 0) Assign(change.Payload["addressControllerOwnerPath"]);
            }
            if (previous != null && !Equals(previous, connector.ConnectedToIoSystem) && previous.ConnectedIoDevices.Any(candidate => Equals(candidate, connector)))
                throw new BridgeException("MutationVerificationFailed", "The previous I/O system still contains the moved or disconnected connector.");
        }

        private JArray CaptureHardwareEngineering(Project project, string kind = null, JObject payload = null, IEnumerable<string> fParameters = null)
        {
            bool cpuMemoryBytes = kind == "hardware_cpu_memory_bytes", ports = HardwareEngineeringRules.CapturesPorts(kind), mrp = HardwareEngineeringRules.CapturesMrp(kind);
            var attributeAllowlist = cpuMemoryBytes ? HardwareCpuMemoryPreservedAttributes : kind == "hardware_cpu_parameters" ? HardwareCpuParameterPreservedAttributes : kind == "safety_fparameter_set" ? new HashSet<string>(HardwareFParameterPreservedAttributes.Concat(fParameters ?? Enumerable.Empty<string>()), StringComparer.Ordinal)
                : kind == "hardware_shared_device_access" ? new HashSet<string>(HardwareWriteAttributes.Concat(SharedAccessAttributes).Concat(ExternalControllerAttributes), StringComparer.Ordinal)
                : HardwareEngineeringRules.CapturesSharedAccess(kind, payload) ? new HashSet<string>(HardwareWriteAttributes.Concat(SharedAccessAttributes), StringComparer.Ordinal) : HardwareWriteAttributes;
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
                    object value = target.GetAttribute(info.Name);
                    attributes[info.Name] = value is DeviceItem device ? (JToken)Owner(device) : ChannelValue(value);
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
                    ["attributes"] = Attributes(item, attributeAllowlist),
                    ["channels"] = new JArray(item.Channels.OrderBy(channel => channel.Type).ThenBy(channel => channel.IoType).ThenBy(channel => channel.Number).Select(channel => new JObject { ["number"] = channel.Number, ["type"] = channel.Type.ToString(), ["ioType"] = channel.IoType.ToString(), ["attributes"] = Attributes(channel, ChannelWriteAttributes) })),
                    ["addresses"] = new JArray(item.Addresses.Select(address => new JObject { ["ioType"] = address.IoType.ToString(), ["startAddress"] = address.StartAddress, ["lengthBits"] = address.Length,
                        ["owners"] = new JArray(address.AddressControllers.Select(owner => Owner(owner)).OrderBy(value => value, StringComparer.Ordinal)) })) };
                var network = item.GetService<NetworkInterface>();
                if (network != null) row["network"] = new JObject { ["nodes"] = new JArray(network.Nodes.Select(NodeSnapshot)),
                    ["controllers"] = new JArray(network.IoControllers.Select((controller, index) => new JObject { ["index"] = index, ["system"] = System(controller.IoSystem) })),
                    ["connectors"] = new JArray(network.IoConnectors.Select((connector, index) => new JObject { ["index"] = index, ["system"] = System(connector.ConnectedToIoSystem) })) };
                if (network != null && IsIDeviceInterface(item)) row["network"]["transferAreas"] = TransferAreas(network);
                if (ports && item.GetService<NetworkPort>() is NetworkPort port)
                    row["port"] = new JObject { ["connectedPorts"] = new JArray(port.ConnectedPorts.Select(partner => Owner(partner.OwnedBy)).OrderBy(value => value, StringComparer.Ordinal)) };
                if (mrp && MrpRows(item, ring => Owner(ring.OwnedBy)) is JArray instances) row["mrp"] = instances;
                if (mrp && network != null && MrpDomainNames(network) is JObject domains) row["mrpDomains"] = domains;
                rows.Add(row);
            }
            return rows;
        }

        private void PrepareHardwarePreservation(EngineeringChange change, Project project, JObject preview)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            change.HardwareBeforeManifest = CaptureHardwareEngineering(project, change.Kind, change.Payload, FParameterNames(change));
            string path = Path.Combine(change.Directory, "hardware-before.json");
            File.WriteAllText(path, change.HardwareBeforeManifest.ToString(Formatting.Indented));
            change.Backups.Add(new JObject { ["role"] = "configured-hardware-preservation", ["artifact"] = ChangeArtifact(path) });
            preview["preservation"] = new JObject { ["policyVersion"] = 1, ["operation"] = change.Kind,
                ["covered"] = "All observed hardware identities/slots, reviewed hardware/channel attributes, configured address ranges/owners, node configuration and exact I/O-system assignments; fail closed on capture failure.",
                ["limitations"] = "Unexposed vendor payloads, non-allowlisted parameters, physical wiring, shared-device/multi-controller behavior and runtime are not qualified." };
            if (HardwareEngineeringRules.CapturesPorts(change.Kind)) preview["preservation"]["covered"] += " Port interconnections (native ConnectedPorts) are included.";
            if (HardwareEngineeringRules.CapturesMrp(change.Kind)) preview["preservation"]["covered"] += " Every interface's MRP instances (domain, MediaRedundancyRole, ring ports) and its subnet's MRP domain names are included.";
            if (change.Kind == "hardware_shared_device_access") preview["preservation"]["covered"] += " SharedDeviceAccessType, SharedDeviceAccessConfigured, SharedDeviceAssignmentTarget and NumberOfExternalIoControllers of every device item are included.";
            else if (HardwareEngineeringRules.CapturesSharedAccess(change.Kind, change.Payload)) preview["preservation"]["covered"] += " SharedDeviceAccessType, SharedDeviceAccessConfigured and SharedDeviceAssignmentTarget of every device item are included.";
            if (change.Kind == "safety_fparameter_set") preview["preservation"]["covered"] += " Failsafe_FDestinationAddress, Failsafe_FMonitoringtime and every written F-parameter of every device item are included.";
            if (change.Kind == "hardware_replace") preview["inventoryBefore"] = HardwareEngineeringRules.ReplaceInventory(change.HardwareBeforeManifest, change.Payload.Value<string>("hardwarePath"));
        }

        private void VerifyHardwarePreservation(EngineeringChange change, Project project)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            var after = CaptureHardwareEngineering(project, change.Kind, change.Payload, FParameterNames(change));
            string path = Path.Combine(change.Directory, "hardware-after.json");
            File.WriteAllText(path, after.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "configured-hardware-preservation", ["artifact"] = ChangeArtifact(path) });
            if (change.Kind == "hardware_replace")
            {
                string module = change.Payload.Value<string>("hardwarePath");
                var inventoryBefore = HardwareEngineeringRules.ReplaceInventory(change.HardwareBeforeManifest, module);
                var inventoryAfter = HardwareEngineeringRules.ReplaceInventory(after, module);
                var map = HardwareEngineeringRules.ReplaceAddressMap(inventoryBefore, inventoryAfter);
                change.Readbacks.Add(new JObject { ["role"] = "hardware-replace-inventory", ["before"] = inventoryBefore, ["after"] = inventoryAfter, ["addressMap"] = map,
                    ["changedReferencedTags"] = HardwareEngineeringRules.ChangedReferencedTags(change.Preview?["addressImpact"]?["referencedTags"] as JArray ?? new JArray(), map) });
            }
            if (change.Kind == "cax_import") RecordCaxHardwareDifference(change, after);
            HardwareEngineeringRules.RequirePreserved(change.Kind, change.Payload, change.HardwareBeforeManifest, after);
            change.Stages.Add(change.Kind == "cax_import" && change.Payload.Value<string>("importOption") == "OverwriteTiaDevice" ? "configured_hardware_difference_recorded" : "configured_hardware_preservation_verified");
        }

        private void VerifyHardwareRecovery(EngineeringChange change, Project project)
        {
            if (!HardwareEngineeringRules.Applies(change.Kind)) return;
            var restored = CaptureHardwareEngineering(project, change.Kind, change.Payload, FParameterNames(change));
            string path = Path.Combine(change.Directory, "hardware-restored.json");
            File.WriteAllText(path, restored.ToString(Formatting.Indented));
            change.Readbacks.Add(new JObject { ["role"] = "configured-hardware-recovery", ["artifact"] = ChangeArtifact(path) });
            if (!JToken.DeepEquals(change.HardwareBeforeManifest, restored)) throw new BridgeException("RecoveryVerificationFailed", "Retrieved hardware metadata differs from the exact prechange configured manifest.");
            change.Stages.Add("configured_hardware_recovery_verified");
        }
    }
}
