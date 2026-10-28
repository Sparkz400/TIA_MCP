using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

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
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["typeIdentifierBefore"] = item.Item.TypeIdentifier, ["typeIdentifierAfter"] = identifier, ["acceptLoss"] = acceptLoss });
                ((JArray)preview["warnings"]).Add("V21 exposes no native compatibility check before DeviceItem.ChangeType. Lost addresses or child items are found after the write by comparing inventoryBefore with the readback; without acceptLoss: true that fails with MutationVerificationFailed and the checkpoint must be restored. Lost port interconnections always fail.");
            }
            return item.Item;
        }

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
