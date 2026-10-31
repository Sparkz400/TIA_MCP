using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using TiaMcp.Analysis;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Both ends are exact port device items exposing NetworkPort; hardware_inspect lists them under network.ports.
        private NetworkPort[] PreparePortLink(EngineeringChange change, Project project, JObject preview)
        {
            bool connect = change.Kind == "hardware_port_connect";
            var ends = new[] { ChangeHardware(project, change.Payload), ChangeHardware(project, new JObject { ["hardwarePath"] = ChangeText(change.Payload, "partnerHardwarePath", 4096) }) };
            if (ends[0].Path == ends[1].Path) throw new BridgeException("InvalidRequest", "hardwarePath and partnerHardwarePath must select two different ports.");
            var ports = ends.Select(end => end.Item.GetService<NetworkPort>() ?? throw new BridgeException("UnsupportedCapability", "Selected hardware item exposes no native NetworkPort: " + end.Path)).ToArray();
            // Alternative partners (tool changers) are not reviewed: connect only two free ports.
            if (connect && ports.Any(port => port.ConnectedPorts.Count != 0)) throw new BridgeException("InvalidRequest", "Both ports must be unconnected; remove an existing partner first with hardware_port_disconnect.");
            if (!connect && !ports[0].ConnectedPorts.Contains(ports[1])) throw new BridgeException("InvalidRequest", "The selected ports are not connected to each other.");
            if (preview != null) ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = ends[0].Path, ["partnerHardwarePath"] = ends[1].Path, ["action"] = connect ? "connect" : "disconnect" });
            return ports;
        }

        private void ApplyPortLink(EngineeringChange change, Project project)
        {
            var ports = PreparePortLink(change, project, null);
            bool connect = change.Kind == "hardware_port_connect";
            if (connect) ports[0].ConnectToPort(ports[1]); else ports[0].DisconnectFromPort(ports[1]);
            if (ports[0].ConnectedPorts.Contains(ports[1]) != connect || ports[1].ConnectedPorts.Contains(ports[0]) != connect)
                throw new BridgeException("MutationVerificationFailed", "Native forward/reverse ConnectedPorts readback differs from the requested " + (connect ? "connection." : "disconnection."));
        }

        private DeviceItem PrepareHardwareReplace(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            EnsureHardwareDeleteEligible(item.Item);
            if (!(item.Item.Container is DeviceItem)) throw new BridgeException("UnsupportedCapability", "Type replacement covers pluggable modules in an existing rack.");
            string identifier = ChangeText(change.Payload, "typeIdentifier", 256);
            if (identifier == item.Item.TypeIdentifier) throw new BridgeException("InvalidRequest", "The requested type identifier is unchanged.");
            ValidateCatalogIdentifier(identifier);
            bool acceptLoss = change.Payload.Property("acceptLoss") != null && ExplicitChangeBool(change.Payload, "acceptLoss");
            bool addressChange = change.Payload.Property("acceptAddressChange") != null && ExplicitChangeBool(change.Payload, "acceptAddressChange");
            if (!addressChange && change.Payload["acknowledgedTags"] != null) throw new BridgeException("InvalidRequest", "acknowledgedTags applies only with acceptAddressChange: true.");
            if (addressChange)
            {
                // The new addresses are known only after ChangeType, so every tag on a current address of the module needs acknowledgement.
                var referenced = TagsOnAddresses(project, item);
                if (!HardwareEngineeringRules.TagsAcknowledged(referenced, change.Payload["acknowledgedTags"]))
                    throw new BridgeException("AddressReferenced", "PLC tags use addresses of this module, which an address-changing replacement may move. Prepare again with acknowledgedTags listing exactly these tag paths.", new JObject { ["referencedTags"] = referenced });
                if (preview != null) preview["addressImpact"] = new JObject { ["referencedTags"] = referenced,
                    ["basis"] = "Tags of the root tag tables of every PLC owning an address of the module whose absolute address overlaps it before the change; software-unit tags are not read. The readback maps every old range to its new one and lists the referenced tags whose range changed." };
            }
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["typeIdentifierBefore"] = item.Item.TypeIdentifier, ["typeIdentifierAfter"] = identifier, ["acceptLoss"] = acceptLoss, ["acceptAddressChange"] = addressChange });
                ((JArray)preview["warnings"]).Add("V21 exposes no native compatibility check before DeviceItem.ChangeType. Lost addresses or child items are found after the write by comparing inventoryBefore with the readback; without acceptLoss: true that fails with MutationVerificationFailed and the checkpoint must be restored. Lost port interconnections always fail." +
                    (addressChange ? " With acceptAddressChange, an address may move or resize but must keep its item, I/O type and owners; the device compile checks program references." : ""));
            }
            return item.Item;
        }

        // Root-table tags of the PLCs owning the module subtree's addresses, whose absolute address overlaps one of those ranges.
        private JArray TagsOnAddresses(Project project, HardwareEntry module)
        {
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(project, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Hardware lookup for the address impact is incomplete.");
            var plcs = PlcItems(hardware);
            var ranges = hardware.Where(entry => entry.Path == module.Path || entry.Path.StartsWith(module.Path + "/", StringComparison.Ordinal))
                .SelectMany(entry => entry.Item.Addresses.Select(address => new { entry.Path, Address = address })).ToArray();
            var owners = ranges.SelectMany(range => range.Address.AddressControllers)
                .Select(owner => OwningPlcItem(owner, plcs) ?? throw new BridgeException("UnresolvedIoOwnership", "An address controller of the module resolves to no PLC of the project.")).Distinct().ToArray();
            var tags = new JArray();
            foreach (var plc in owners)
            {
                var software = (PlcSoftware)plc.Item.GetService<SoftwareContainer>().Software;
                foreach (var table in TagTables(software.TagTableGroup, plc.Path + "/plc/" + Segment(software.Name) + "/tags", budget))
                    foreach (var tag in table.Item.Tags)
                    {
                        if (!budget.Take()) throw new BridgeException("TraversalLimit", "PLC tag lookup for the address impact is incomplete.");
                        var address = OfflineEvidence.ParseIoAddress(tag.LogicalAddress);
                        var range = address == null ? null : ranges.FirstOrDefault(row => row.Address.IoType.ToString() == address.Direction && OfflineEvidence.IoAddressOverlapsNativeRange(address, row.Address.StartAddress, row.Address.Length));
                        if (range != null) tags.Add(new JObject { ["path"] = table.Path + "/tags/" + Segment(tag.Name), ["logicalAddress"] = tag.LogicalAddress,
                            ["hardwarePath"] = range.Path, ["ioType"] = range.Address.IoType.ToString(), ["startAddress"] = range.Address.StartAddress, ["lengthBits"] = range.Address.Length });
                    }
            }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "PLC tag lookup for the address impact is incomplete.");
            return tags;
        }

        private static HardwareEntry[] PlcItems(HardwareEntry[] hardware) => hardware.Where(entry => entry.Item.GetService<SoftwareContainer>()?.Software is PlcSoftware).ToArray();

        // The project's PLC item above a native controller, I/O system or address controller; null when none resolves uniquely.
        private static HardwareEntry OwningPlcItem(IEngineeringInstance item, HardwareEntry[] plcs)
        {
            for (int depth = 0; item != null && depth < 64; depth++, item = item.Parent)
                if (item is DeviceItem device && device.GetService<SoftwareContainer>()?.Software is PlcSoftware)
                {
                    var matches = plcs.Where(entry => Equals(entry.Item, device)).Take(2).ToArray();
                    return matches.Length == 1 ? matches[0] : null;
                }
            return null;
        }

        private static readonly System.Collections.Generic.HashSet<string> MrpWriteAttributes = new System.Collections.Generic.HashSet<string>(new[] { "MediaRedundancyRole" }, StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> SharedAccessAttributes = new System.Collections.Generic.HashSet<string>(new[] { "SharedDeviceAccessType" }, StringComparer.Ordinal);

        // The one MRP instance of a PROFINET interface item (V21 MrpInstancesOwner).
        private static MrpInstance MrpInstanceOf(DeviceItem item)
        {
            var instances = item.GetService<MrpInstancesOwner>()?.MrpInstances.ToArray() ?? throw new BridgeException("UnsupportedCapability", "The selected item exposes no native MRP instances (MrpInstancesOwner); select a PROFINET interface.");
            if (instances.Length != 1) throw new BridgeException("UnsupportedCapability", "Media redundancy covers an interface with exactly one MRP instance; this one has " + instances.Length + ".");
            return instances[0];
        }

        // Name, domain, role and ring ports of each MRP instance of an interface item, null without MrpInstancesOwner.
        private static JArray MrpRows(DeviceItem item, Func<NetworkPort, JToken> port)
        {
            var owner = item.GetService<MrpInstancesOwner>();
            return owner == null ? null : new JArray(owner.MrpInstances.Select(instance => new JObject { ["name"] = HardwareEngineeringRules.MetadataString(instance.Name),
                ["domain"] = HardwareEngineeringRules.MetadataString(instance.ConnectedMrpDomain?.Name),
                ["role"] = HardwareEngineeringRules.MetadataString(instance.GetAttributeInfos().Any(info => info.Name == "MediaRedundancyRole" && (info.AccessMode & EngineeringAttributeAccessMode.Read) != 0)
                    ? instance.GetAttribute("MediaRedundancyRole")?.ToString() : null),
                ["ringPorts"] = new JArray(new[] { instance.RingPort1, instance.RingPort2 }.Select(ring => ring == null ? JValue.CreateNull() : port(ring))) }));
        }

        // One interface's MRP instance takes a MediaRedundancyRole and optionally joins (or first creates) a named MRP domain of its subnet.
        private MrpInstance PrepareMrp(EngineeringChange change, Project project, JObject preview, out MrpDomainComposition domains)
        {
            string role = ChangeText(change.Payload, "role", 32);
            if (Array.IndexOf(HardwareEngineeringRules.MediaRedundancyRoles, role) < 0) throw new BridgeException("InvalidRequest", "role must be one of " + string.Join(", ", HardwareEngineeringRules.MediaRedundancyRoles) + ".");
            var item = ChangeHardware(project, change.Payload);
            var network = item.Item.GetService<NetworkInterface>() ?? throw new BridgeException("UnsupportedCapability", "Media redundancy needs a PROFINET interface; the selected item exposes no NetworkInterface.");
            var instance = MrpInstanceOf(item.Item);
            var changes = PrepareAttributes(instance, new JObject { ["MediaRedundancyRole"] = role }, MrpWriteAttributes);
            string name = change.Payload["domainName"] == null ? null : ChangeText(change.Payload, "domainName", 128);
            bool create = change.Payload.Property("createDomain") != null && ExplicitChangeBool(change.Payload, "createDomain");
            if (create && name == null) throw new BridgeException("InvalidRequest", "createDomain needs domainName.");
            domains = null;
            MrpDomain domain = null;
            if (name != null)
            {
                var nodes = network.Nodes.ToArray();
                var subnet = nodes.Length == 1 ? nodes[0].ConnectedSubnet : null;
                if (subnet == null) throw new BridgeException("NetworkPrerequisite", "An MRP domain belongs to a subnet: the interface needs exactly one node connected to a subnet (network_configure).");
                domains = subnet.GetService<MrpDomainOwner>()?.MrpDomains ?? throw new BridgeException("UnsupportedCapability", "The interface's subnet exposes no native MRP domains (MrpDomainOwner).");
                domain = domains.Find(name);
                if (create && domain != null) throw new BridgeException("ObjectExists", "The subnet already has an MRP domain named " + name + ".");
                if (!create && domain == null) throw new BridgeException("ObjectNotFound", "The interface's subnet has no MRP domain named " + name + "; set createDomain: true to create it.");
            }
            string before = changes[0].Value<string>("before"), domainBefore = instance.ConnectedMrpDomain?.Name;
            if (before == role && (name == null || Equals(instance.ConnectedMrpDomain, domain)))
                throw new BridgeException("InvalidRequest", "The requested MRP role and domain are unchanged.");
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["mrpInstance"] = instance.Name, ["roleBefore"] = before, ["roleAfter"] = role,
                    ["domainBefore"] = domainBefore, ["domainAfter"] = name ?? domainBefore, ["createDomain"] = create });
                preview["attributeChanges"] = changes;
                ((JArray)preview["warnings"]).Add("Ring ports stay as configured; close or open the ring with hardware_port_connect and hardware_port_disconnect. A closed ring needs exactly one manager in its domain or the device compile fails.");
            }
            return instance;
        }

        private void ApplyMrp(EngineeringChange change, Project project)
        {
            var instance = PrepareMrp(change, project, null, out var domains);
            var item = ChangeHardware(project, change.Payload);
            string name = change.Payload.Value<string>("domainName");
            if (name != null)
            {
                // Join the domain first: TIA may reset the role of an instance that changes domain.
                var domain = domains.Find(name) ?? domains.Create(name);
                if (!Equals(instance.ConnectedMrpDomain, domain)) domain.DomainParticipants.Add(item.Item.GetService<NetworkInterface>());
                if (!Equals(MrpInstanceOf(item.Item).ConnectedMrpDomain, domain)) throw new BridgeException("MutationVerificationFailed", "The interface's MRP instance is not connected to the requested domain.");
            }
            var readback = MrpInstanceOf(item.Item);
            ApplyAttributes(readback, new JObject { ["MediaRedundancyRole"] = change.Payload.Value<string>("role") }, MrpWriteAttributes);
            change.Readbacks.Add(new JObject { ["role"] = "mrp", ["hardwarePath"] = item.Path, ["mrpInstance"] = readback.Name, ["domain"] = readback.ConnectedMrpDomain?.Name, ["mediaRedundancyRole"] = readback.GetAttribute("MediaRedundancyRole")?.ToString() });
        }

        // One module or submodule of an IO device takes a V21 SharedDeviceAccessType. The payload records the PLC items whose
        // I/O systems the station's connectors join, the only permitted address owners afterwards.
        private DeviceItem PrepareSharedDeviceAccess(EngineeringChange change, Project project, JObject preview)
        {
            string access = ChangeText(change.Payload, "accessType", 32);
            if (Array.IndexOf(HardwareEngineeringRules.SharedDeviceAccessTypes, access) < 0) throw new BridgeException("InvalidRequest", "accessType must be one of " + string.Join(", ", HardwareEngineeringRules.SharedDeviceAccessTypes) + ".");
            var item = ChangeHardware(project, change.Payload);
            string station = item.Path.Substring(0, item.Path.IndexOf("/items/", StringComparison.Ordinal));
            var budget = new ScanBudget(50000);
            var hardware = DeviceItems(project, budget).ToArray();
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Hardware lookup for the shared device is incomplete.");
            var members = hardware.Where(entry => entry.Path.StartsWith(station + "/items/", StringComparison.Ordinal)).ToArray();
            var plcs = PlcItems(hardware);
            if (members.Any(entry => plcs.Contains(entry))) throw new BridgeException("UnsupportedCapability", "Shared-device access covers modules of an IO device; the selected station holds PLC software.");
            var systems = members.Select(entry => entry.Item.GetService<NetworkInterface>()).Where(network => network != null).SelectMany(network => network.IoConnectors).Select(connector => connector.ConnectedToIoSystem).Where(system => system != null).ToArray();
            if (systems.Length == 0) throw new BridgeException("UnsupportedCapability", "Shared-device access applies to a module of an IO device assigned to an I/O system; assign the station first (network_io_assign).");
            var owners = systems.Select(system => OwningPlcItem(system.Parent, plcs) ?? throw new BridgeException("UnresolvedIoOwnership", "An I/O system of the station resolves to no PLC of the project.")).Select(entry => entry.Path).Distinct().OrderBy(path => path, StringComparer.Ordinal);
            HardwareEngineeringRules.RequireProfileWritable(HardwareEngineeringRules.Profile(item.Item.TypeIdentifier), null, new[] { "SharedDeviceAccessType" });
            var changes = PrepareAttributes(item.Item, new JObject { ["SharedDeviceAccessType"] = access }, SharedAccessAttributes);
            if (changes[0].Value<string>("before") == access) throw new BridgeException("InvalidRequest", "The item already has the requested SharedDeviceAccessType.");
            change.Payload["addressControllerOwnerPaths"] = new JArray(owners);
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["accessBefore"] = changes[0]["before"].DeepClone(), ["accessAfter"] = access });
                preview["attributeChanges"] = changes;
                ((JArray)preview["warnings"]).Add("TIA moves the module's address ownership with its access (OutsideOfProject: an IO controller outside this project); its subitems may follow. The mandatory device compile checks program references to the released addresses.");
            }
            return item.Item;
        }

        private void ApplySharedDeviceAccess(EngineeringChange change, Project project) =>
            ApplyAttributes(PrepareSharedDeviceAccess(change, project, null), new JObject { ["SharedDeviceAccessType"] = change.Payload.Value<string>("accessType") }, SharedAccessAttributes);

        private void ApplyHardwareReplace(EngineeringChange change, Project project)
        {
            string identifier = change.Payload.Value<string>("typeIdentifier");
            PrepareHardwareReplace(change, project, null).ChangeType(identifier);
            if (ChangeHardware(project, change.Payload).Item.TypeIdentifier != identifier)
                throw new BridgeException("MutationVerificationFailed", "The item at hardwarePath does not report the requested type identifier after ChangeType.");
        }

        private static JObject GsdInfo(IGsdObject gsd) => gsd == null ? null : new JObject { ["gsdId"] = gsd.GsdId, ["gsdName"] = gsd.GsdName, ["gsdType"] = gsd.GsdType, ["isProfinet"] = gsd.IsProfinet, ["isProfibus"] = gsd.IsProfibus };

        // V21 offers GsdExportProvider on every device item, but Export works only on an interface in I-device mode.
        private static bool IsIDeviceInterface(DeviceItem item)
        {
            var network = item.GetService<NetworkInterface>();
            return network != null && (network.InterfaceOperatingMode & InterfaceOperatingModes.IoDevice) != 0;
        }

        // An I-device interface's transfer areas by exact name, as hardware_inspect and the preservation manifest report them.
        private static JArray TransferAreas(NetworkInterface network) => new JArray(network.TransferAreas.Select(area => new JObject { ["name"] = area.Name, ["type"] = area.Type.ToString(),
            ["direction"] = area.Direction.ToString(), ["positionNumber"] = area.PositionNumber, ["localToPartnerLength"] = area.LocalToPartnerLength, ["partnerToLocalLength"] = area.PartnerToLocalLength,
            ["localAddresses"] = TransferAreaAddresses(area.LocalAddresses), ["partnerAddresses"] = TransferAreaAddresses(area.PartnerAddresses) }).OrderBy(area => area.Value<string>("name"), StringComparer.Ordinal));

        private static JArray TransferAreaAddresses(AddressComposition addresses) => new JArray(addresses.Select(address => new JObject { ["ioType"] = address.IoType.ToString(), ["startAddress"] = address.StartAddress, ["lengthBits"] = address.Length }));

        // One controller-device (CD) transfer area by exact name on an I-device PROFINET interface. TIA chooses its direction, lengths and local addresses.
        private TransferArea PrepareTransferArea(EngineeringChange change, Project project, JObject preview)
        {
            var item = ChangeHardware(project, change.Payload);
            if (!IsIDeviceInterface(item.Item))
                throw new BridgeException("UnsupportedCapability", "Transfer areas are available only on a PROFINET interface in I-device mode (InterfaceOperatingMode IoDevice).");
            string name = ChangeText(change.Payload, "name", 128);
            bool create = change.Kind == "hardware_transfer_area_create";
            var named = item.Item.GetService<NetworkInterface>().TransferAreas.Where(area => string.Equals(area.Name, name, create ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
            if (create && named.Length != 0) throw new BridgeException("ObjectExists", "The interface already has a transfer area named " + named[0].Name + ".");
            if (!create && named.Length != 1) throw new BridgeException("ObjectNotFound", "The interface has no transfer area with this exact name.");
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["action"] = create ? "create" : "delete", ["name"] = name, ["type"] = create ? TransferAreaType.CD.ToString() : named[0].Type.ToString() });
                ((JArray)preview["warnings"]).Add(create ? "TIA chooses the new area's direction, lengths and local addresses; the readback and hardware_inspect report them."
                    : "The area's local addresses are released; program references to them are checked only by the mandatory device compile.");
            }
            return create ? null : named[0];
        }

        private void ApplyTransferArea(EngineeringChange change, Project project)
        {
            var area = PrepareTransferArea(change, project, null);
            string name = change.Payload.Value<string>("name");
            var network = ChangeHardware(project, change.Payload).Item.GetService<NetworkInterface>();
            if (area == null) network.TransferAreas.Create(name, TransferAreaType.CD); else area.Delete();
            var readback = TransferAreas(network);
            var named = readback.Where(row => row.Value<string>("name") == name).ToArray();
            if (area == null ? named.Length != 1 || named[0].Value<string>("type") != TransferAreaType.CD.ToString() : named.Length != 0)
                throw new BridgeException("MutationVerificationFailed", "The interface's transfer areas do not show exactly the requested " + (area == null ? "creation." : "deletion."));
            change.Readbacks.Add(new JObject { ["role"] = "transfer-areas", ["hardwarePath"] = change.Payload.Value<string>("hardwarePath"), ["transferAreas"] = readback });
        }

        // Read-only for the project: Siemens writes the I-device GSD file(s) into a fresh artifact directory.
        private JObject HardwareGsdExport(JObject args)
        {
            var project = SelectProject(args);
            var item = ChangeHardware(project, args);
            var provider = item.Item.GetService<GsdExportProvider>() ?? throw new BridgeException("UnsupportedCapability", "The selected hardware item exposes no native GsdExportProvider (I-device GSD export).");
            if (!IsIDeviceInterface(item.Item))
                throw new BridgeException("UnsupportedCapability", "GSD export is available only on a PROFINET interface in I-device mode (InterfaceOperatingMode IoDevice).");
            string designation = ChangeText(args, "designation", 128);
            string description = args.Value<string>("description") ?? "";
            if (description.Length > 1024) throw new BridgeException("InvalidRequest", "description exceeds 1024 characters.");
            var directory = Directory.CreateDirectory(Path.Combine(ArtifactDirectory(args), "gsd-" + Guid.NewGuid().ToString("N")));
            provider.Export(new FileInfo(Path.Combine(directory.FullName, "GSDML.xml")), designation, description);
            // Siemens may name the GSDML file itself and add an icon; report what it actually wrote.
            var files = directory.EnumerateFiles("*", SearchOption.AllDirectories).Take(33).ToArray();
            if (files.Length == 0) throw new BridgeException("ExportFailed", "TIA reported no error but wrote no GSD file.");
            var artifacts = new JArray(files.Take(32).Select(file =>
            {
                var artifact = FileArtifact(file);
                if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) artifact["mediaType"] = "application/octet-stream";
                return artifact;
            }));
            var result = Envelope();
            result["project"] = ProjectInfo(project);
            result["hardwarePath"] = item.Path;
            result["designation"] = designation;
            result["artifacts"] = artifacts;
            result["truncated"] = files.Length > 32;
            result["completeness"] = new JObject { ["complete"] = files.Length <= 32, ["scope"] = "Files written by native GsdExportProvider.Export into a fresh artifact directory, each with SHA-256. The GSD content is Siemens output and is not validated by the bridge." };
            return result;
        }
    }
}
