using System;
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
        private Address PrepareHardwareAddress(EngineeringChange change, Project project, JObject preview)
        {
            var payload = change.Payload;
            string ioType = ChangeText(payload, "ioType", 32);
            int oldStart = RequiredIoByteAddress(payload, "oldStartAddress");
            int start = RequiredIoByteAddress(payload, "startAddress");
            var item = ChangeHardware(project, payload);
            var ranges = item.Item.Addresses.Where(address => address.IoType.ToString() == ioType && address.StartAddress == oldStart).ToArray();
            if (ranges.Length != 1) throw new BridgeException("AmbiguousTarget", "ioType and oldStartAddress must identify exactly one configured address range on hardwarePath.");
            var selected = ranges[0];
            int selectedLengthBytes = OfflineEvidence.IoBitLengthToByteLength(selected.Length);
            if (selectedLengthBytes <= 0) throw new BridgeException("UnsupportedCapability", "The selected address has no positive configured bit length.");
            if (!OfflineEvidence.ValidIoByteRange(start, selectedLengthBytes)) throw new BridgeException("InvalidRequest", "The requested address span exceeds the supported integer byte-address range.");
            var plc = SelectPlc(change.Selector);
            var selectedOwners = selected.AddressControllers.Select(controller => ControllerMatchesSelectedPlc(controller, plc.Software)).ToArray();
            if (selectedOwners.Length == 0 || selectedOwners.Any(owner => owner != true))
                throw new BridgeException("UnresolvedIoOwnership", "Every controller of the selected address must resolve to the selected PLC; unresolved or shared PLC ownership cannot be changed through this route.");
            var budget = new ScanBudget(50000);
            var conflicts = new JArray();
            foreach (var other in DeviceItems(project, budget))
                foreach (var address in other.Item.Addresses)
                {
                    if (!budget.Take()) break;
                    if (other.Path == item.Path && address.IoType == selected.IoType && address.StartAddress == oldStart) continue;
                    if (address.IoType != selected.IoType || address.Length <= 0) continue;
                    var owners = address.AddressControllers.Select(controller => ControllerMatchesSelectedPlc(controller, plc.Software)).ToArray();
                    if (!OfflineEvidence.IoOwnershipMayMatch(owners)) continue;
                    int lengthBytes = OfflineEvidence.IoBitLengthToByteLength(address.Length);
                    if (!OfflineEvidence.ValidIoByteRange(address.StartAddress, lengthBytes)) throw new BridgeException("DependencyCoverageIncomplete", "A configured I/O address has invalid bounds; collision absence cannot be established.");
                    if (OfflineEvidence.IoByteRangesOverlap(start, selectedLengthBytes, address.StartAddress, lengthBytes))
                        conflicts.Add(new JObject { ["hardwarePath"] = other.Path, ["startAddress"] = address.StartAddress, ["length"] = address.Length,
                            ["lengthUnit"] = "bits", ["lengthBytes"] = lengthBytes,
                            ["ownerResolved"] = owners.Length != 0 && owners.All(owner => owner.HasValue) });
                }
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Address collision lookup is incomplete.");
            if (conflicts.Count != 0) throw new BridgeException("AddressConflict", "Requested byte range overlaps configured I/O; ranges with unresolved ownership conservatively count as conflicts.", new JObject { ["conflicts"] = conflicts });
            var attrs = new JObject { ["StartAddress"] = start };
            var prepared = PrepareAttributes(selected, attrs, new System.Collections.Generic.HashSet<string>(new[] { "StartAddress" }, StringComparer.Ordinal));
            if (preview != null)
            {
                ((JArray)preview["effects"]).Add(new JObject { ["hardwarePath"] = item.Path, ["ioType"] = ioType, ["before"] = oldStart, ["after"] = start,
                    ["lengthBits"] = selected.Length, ["lengthBytes"] = selectedLengthBytes, ["attributes"] = prepared });
                ((JArray)preview["warnings"]).Add("Only the configured offline address range changes. Symbolic tags retaining old absolute addresses must be reviewed separately; native hardware compile validates the configuration.");
                ((JArray)preview["warnings"]).Add("Siemens may couple input/output addresses on some modules. Any collateral type, length or start-address change is rejected after write and requires checkpoint recovery.");
            }
            return selected;
        }

        private static int RequiredIoByteAddress(JObject payload, string name)
        {
            var token = payload[name];
            if (token == null || token.Type != JTokenType.Integer || !OfflineEvidence.TryIoByteAddress(token.ToString(), out int address))
                throw new BridgeException("InvalidRequest", name + " must be a nonnegative Int32 byte address.");
            return address;
        }

        private void ApplyHardwareAddress(EngineeringChange change, Project project)
        {
            var address = PrepareHardwareAddress(change, project, null);
            var item = ChangeHardware(project, change.Payload).Item;
            Func<OfflineEvidence.IoRangeSnapshot[]> snapshot = () => item.Addresses.Select(row => new OfflineEvidence.IoRangeSnapshot
                { IoType = row.IoType.ToString(), StartAddress = row.StartAddress, LengthBits = row.Length }).ToArray();
            var before = snapshot();
            int requested = RequiredIoByteAddress(change.Payload, "startAddress");
            ApplyAttributes(address, new JObject { ["StartAddress"] = requested },
                new System.Collections.Generic.HashSet<string>(new[] { "StartAddress" }, StringComparer.Ordinal));
            var after = snapshot();
            if (!OfflineEvidence.IoAddressMutationPreserved(before, after, change.Payload.Value<string>("ioType"),
                RequiredIoByteAddress(change.Payload, "oldStartAddress"), requested))
                throw new BridgeException("MutationVerificationFailed", "Hardware address write changed a different address, type or length, or failed requested read-back. Restore the checkpoint before further changes.",
                    new JObject { ["before"] = HardwareAddressRows(before), ["after"] = HardwareAddressRows(after) });
        }

        private static JArray HardwareAddressRows(OfflineEvidence.IoRangeSnapshot[] rows) => new JArray(rows.Select(row => new JObject
            { ["ioType"] = row.IoType, ["startAddress"] = row.StartAddress, ["length"] = row.LengthBits, ["lengthUnit"] = "bits" }));

        private static bool? ControllerMatchesSelectedPlc(IEngineeringInstance item, PlcSoftware selected)
        {
            for (int depth = 0; item != null && depth < 64; depth++, item = item.Parent)
            {
                if (!(item is DeviceItem device)) continue;
                var owner = device.GetService<SoftwareContainer>()?.Software as PlcSoftware;
                if (owner != null) return owner.Equals(selected);
            }
            return null;
        }
    }
}
