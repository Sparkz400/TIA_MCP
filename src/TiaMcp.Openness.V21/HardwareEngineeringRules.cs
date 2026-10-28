using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // A bounded, explicit preservation contract over native configured metadata.
    // This deliberately makes no claim about unexposed vendor payloads or wiring.
    internal static class HardwareEngineeringRules
    {
        internal static bool Applies(string kind) => new[] { "hardware_plug", "hardware_move", "hardware_channel_attributes", "hardware_address_set", "network_io_assign", "hardware_cpu_memory_bytes",
            "hardware_port_connect", "hardware_port_disconnect", "hardware_replace", "cax_import", "safety_fparameter_set", "network_configure",
            "hardware_transfer_area_create", "hardware_transfer_area_delete" }.Contains(kind);

        // Port interconnections join the manifest only for kinds that change or can lose them.
        internal static bool CapturesPorts(string kind) => kind == "hardware_port_connect" || kind == "hardware_port_disconnect" || kind == "hardware_replace";

        // Addresses and child items of one replaced subtree, read from a preservation manifest.
        internal static JObject ReplaceInventory(JArray rows, string path)
        {
            var tree = rows.OfType<JObject>().Where(row => InTree(row, path)).OrderBy(row => row.Value<string>("path"), StringComparer.Ordinal).ToArray();
            return new JObject { ["typeIdentifier"] = tree.FirstOrDefault(row => row.Value<string>("path") == path)?["typeIdentifier"]?.DeepClone(),
                ["addresses"] = new JArray(tree.SelectMany(row => (row["addresses"] as JArray ?? new JArray()).OfType<JObject>().Select(address => { var copy = (JObject)address.DeepClone(); copy["hardwarePath"] = row["path"].DeepClone(); return copy; }))),
                ["children"] = new JArray(tree.Where(row => row.Value<string>("path") != path).Select(row => row["path"].DeepClone())) };
        }

        private static bool InTree(JToken row, string path) => row.Value<string>("path") == path || row.Value<string>("path").StartsWith(path + "/", StringComparison.Ordinal);

        // Exact V21 attributes of a CPU device item. Openness declares each byte number only while its flag is enabled.
        internal static readonly string[] CpuMemoryByteAttributes = { "SystemMemoryByte", "SystemMemoryByteAddress", "ClockMemoryByte", "ClockMemoryByteAddress" };
        internal static readonly string[] CpuMemoryByteFlags = { "SystemMemoryByte", "ClockMemoryByte" };

        // The settings after the request; a disabled flag has no byte number.
        internal static JObject CpuMemoryBytesAfter(JObject current, JObject requested)
        {
            var effective = (JObject)current.DeepClone();
            foreach (var property in requested.Properties()) effective[property.Name] = property.Value.DeepClone();
            foreach (string flag in CpuMemoryByteFlags)
                if (effective.Value<bool?>(flag) != true) effective.Remove(flag + "Address");
            return effective;
        }

        // Checks the request against the CPU's current native values (flags plus the byte numbers
        // Openness declares). Siemens validates the CPU-specific bit-memory size during the write and compile.
        internal static void RequireCpuMemoryBytes(JObject current, JObject requested)
        {
            if (requested == null || requested.Count == 0) throw new BridgeException("InvalidRequest", "At least one system or clock memory attribute is required.");
            foreach (var property in requested.Properties())
            {
                if (Array.IndexOf(CpuMemoryByteAttributes, property.Name) < 0)
                    throw new BridgeException("UnsupportedAttribute", "Attribute is outside the reviewed CPU memory byte subset: " + property.Name);
                bool address = property.Name.EndsWith("Address", StringComparison.Ordinal);
                if (address ? property.Value.Type != JTokenType.Integer || property.Value.Value<long>() < 0 || property.Value.Value<long>() > ushort.MaxValue : property.Value.Type != JTokenType.Boolean)
                    throw new BridgeException("InvalidRequest", property.Name + (address ? " must be a byte number from 0 to 65535." : " must be a boolean."));
            }
            foreach (string flag in CpuMemoryByteFlags)
            {
                bool enabled = (requested[flag] ?? current[flag])?.Value<bool>() == true;
                if (requested[flag + "Address"] != null && !enabled) throw new BridgeException("InvalidRequest", flag + "Address requires " + flag + " to be enabled.");
                if (enabled && current.Value<bool?>(flag) != true && requested[flag + "Address"] == null)
                    throw new BridgeException("InvalidRequest", "Enabling " + flag + " requires an explicit " + flag + "Address.");
            }
            var effective = CpuMemoryBytesAfter(current, requested);
            if (JToken.DeepEquals(effective, CpuMemoryBytesAfter(current, new JObject()))) throw new BridgeException("InvalidRequest", "The requested system and clock memory settings are unchanged.");
            if (effective["SystemMemoryByteAddress"] != null && effective["ClockMemoryByteAddress"] != null &&
                effective.Value<long>("SystemMemoryByteAddress") == effective.Value<long>("ClockMemoryByteAddress"))
                throw new BridgeException("AddressConflict", "System memory and clock memory must use different bit-memory bytes.");
        }

        // Newtonsoft's implicit string conversion keeps a String token for a
        // null native string, although its JSON text is null. Capture the actual
        // JSON value explicitly; preservation equality itself remains strict.
        internal static JValue MetadataString(string value) => value == null ? JValue.CreateNull() : new JValue(value);

        internal static void RequirePreserved(string kind, JObject payload, JArray before, JArray after)
        {
            RequireIdentities(before); RequireIdentities(after);
            if (payload == null) Fail("Hardware preservation payload is absent.");
            var expected = (JArray)before.DeepClone();
            string path = payload.Value<string>("hardwarePath");
            JObject Target(JArray rows, string target) => rows.Cast<JObject>().SingleOrDefault(row => row.Value<string>("path") == target)
                ?? throw new BridgeException("MutationVerificationFailed", "The exact selected hardware path is absent from the preservation manifest.");
            var selected = kind == "cax_import" || kind == "safety_fparameter_set" ? null : Target(expected, path);
            if (kind == "cax_import")
            {
                // Siemens keeps every name-conflicting device for MoveToParkingLot and RetainTiaDevice, so existing rows
                // stay exact and only new rows may appear. OverwriteTiaDevice replaces devices as the AML file dictates;
                // no expected manifest exists for it, so its difference is only recorded for review.
                if (payload.Value<string>("importOption") == "OverwriteTiaDevice") return;
                var known = new HashSet<string>(before.Select(row => row.Value<string>("path")), StringComparer.Ordinal);
                foreach (var row in after.Where(row => !known.Contains(row.Value<string>("path")))) expected.Add(row.DeepClone());
            }
            else if (kind == "hardware_plug")
            {
                // Logical DeviceItems can be siblings of their physical rack.
                // Native Container identity, not string ancestry, proves placement.
                var modules = after.OfType<JObject>().Where(row => row.Value<string>("name") == payload.Value<string>("name") && row.Value<string>("containerPath") == path &&
                    !before.OfType<JObject>().Any(old => old.Value<string>("path") == row.Value<string>("path"))).ToArray();
                if (modules.Length != 1) Fail("Exactly one new module must occupy the reviewed native container.");
                string createdPath = modules[0].Value<string>("path");
                var created = after.OfType<JObject>().Where(row => row.Value<string>("path") == createdPath || row.Value<string>("path").StartsWith(createdPath + "/", StringComparison.Ordinal)).ToArray();
                if (created.Length == 0) Fail("The requested module is absent after plugging.");
                var module = modules[0];
                if (module.Value<int>("positionNumber") != payload.Value<int>("positionNumber") || module.Value<string>("typeIdentifier") != payload.Value<string>("typeIdentifier")) Fail("The created module differs from its reviewed catalog identifier or slot.");
                foreach (var row in created) expected.Add(row.DeepClone());
            }
            else if (kind == "hardware_move") selected["positionNumber"] = payload["positionNumber"].DeepClone();
            else if (kind == "hardware_channel_attributes")
            {
                var channel = ((JArray)selected["channels"]).OfType<JObject>().Single(row => row.Value<int>("number") == payload.Value<int>("channelNumber") && row.Value<string>("type") == payload.Value<string>("channelType") && row.Value<string>("ioType") == payload.Value<string>("channelIoType"));
                foreach (var property in ((JObject)payload["attributes"]).Properties()) channel["attributes"][property.Name] = property.Value.DeepClone();
                // TIA applies a technology module's OperatingMode to all its channels and resizes that item's own address ranges in place.
                if (payload["attributes"]["OperatingMode"] != null)
                {
                    foreach (var peer in ((JArray)selected["channels"]).OfType<JObject>().Where(row => row["attributes"]?["OperatingMode"] != null))
                        peer["attributes"]["OperatingMode"] = payload["attributes"]["OperatingMode"].DeepClone();
                    var addresses = (JArray)selected["addresses"]; var observed = Target(after, path)["addresses"] as JArray;
                    if (observed?.Count == addresses.Count)
                        for (int index = 0; index < addresses.Count; index++) addresses[index]["lengthBits"] = observed[index]?["lengthBits"]?.DeepClone();
                }
            }
            else if (kind == "safety_fparameter_set")
            {
                foreach (var row in ((JArray)payload["parameters"]).OfType<JObject>())
                    Target(expected, row.Value<string>("hardwarePath"))["attributes"][row.Value<string>("name")] = row["value"].DeepClone();
            }
            else if (kind == "hardware_cpu_memory_bytes")
            {
                var memory = CpuMemoryBytesAfter(new JObject(((JObject)selected["attributes"]).Properties().Where(p => Array.IndexOf(CpuMemoryByteAttributes, p.Name) >= 0)), (JObject)payload["attributes"]);
                foreach (string name in CpuMemoryByteAttributes) ((JObject)selected["attributes"]).Remove(name);
                foreach (var property in memory.Properties()) selected["attributes"][property.Name] = property.Value.DeepClone();
            }
            else if (kind == "hardware_address_set")
            {
                var address = ((JArray)selected["addresses"]).OfType<JObject>().Single(row => row.Value<string>("ioType") == payload.Value<string>("ioType") && row.Value<int>("startAddress") == payload.Value<int>("oldStartAddress"));
                address["startAddress"] = payload["startAddress"].DeepClone();
            }
            else if (kind == "network_io_assign")
            {
                int connectorIndex = payload.Value<int>("connectorIndex");
                string station = path.Substring(0, path.IndexOf("/items/", StringComparison.Ordinal));
                foreach (var planned in RemoteIoCompanionContract.ExpectedRows(payload)) expected.Add(planned.DeepClone());
                foreach (var row in expected.OfType<JObject>().Where(row => row.Value<string>("path").StartsWith(station + "/items/", StringComparison.Ordinal)))
                    foreach (var peer in (row["network"]?["connectors"] as JArray ?? new JArray()).OfType<JObject>())
                        if ((row.Value<string>("path") != path || peer.Value<int>("index") != connectorIndex) && peer["system"]?.Value<string>("state") == "assigned")
                            Fail("Another remote-station interface/connector is assigned; its address ownership must not be rewritten.");
                var observed = Target(after, path);
                var connector = (JObject)selected["network"]["connectors"][connectorIndex];
                var actual = (JObject)observed["network"]["connectors"][connectorIndex]["system"];
                bool disconnect = payload.Value<bool?>("disconnect") == true;
                if (disconnect)
                {
                    if (actual.Value<string>("state") != "notAssigned") Fail("The selected connector remains assigned.");
                }
                else
                {
                    string controllerPath = payload.Value<string>("controllerHardwarePath");
                    int controllerIndex = payload.Value<int>("controllerIndex");
                    var owner = Target(expected, controllerPath);
                    var current = (JObject)owner["network"]["controllers"][controllerIndex]["system"];
                    if (current.Value<string>("state") == "notAssigned")
                    {
                        current = (JObject)Target(after, controllerPath)["network"]["controllers"][controllerIndex]["system"];
                        if (current.Value<string>("name") != payload.Value<string>("createIoSystemName") || current.Value<string>("ownerHardwarePath") != controllerPath || current.Value<int>("controllerIndex") != controllerIndex || current.Value<string>("state") != "assigned") Fail("Created I/O system differs from the reviewed controller/name.");
                        owner["network"]["controllers"][controllerIndex]["system"] = current.DeepClone();
                    }
                    if (!JToken.DeepEquals(current, actual)) Fail("Connector does not reference the exact selected controller's I/O system.");
                }
                connector["system"] = actual.DeepClone();
                // Native address ownership follows the assigned controller. It is
                // the only allowed address change, and only on this remote station.
                foreach (var row in expected.OfType<JObject>().Where(row => row.Value<string>("path").StartsWith(station + "/items/", StringComparison.Ordinal) &&
                    before.OfType<JObject>().Any(original => original.Value<string>("path") == row.Value<string>("path"))))
                {
                    var actualRow = Target(after, row.Value<string>("path"));
                    var addresses = (JArray)row["addresses"];
                    for (int i = 0; i < addresses.Count; i++)
                    {
                        var previousOwners = (JArray)addresses[i]["owners"];
                        if (previousOwners.Count > 1 || previousOwners.Any(owner => owner.Value<string>() != payload.Value<string>("addressControllerOwnerPath"))) Fail("Prechange remote-station address ownership is shared, foreign or unresolved.");
                        var owners = (JArray)actualRow["addresses"][i]["owners"];
                        if (owners.Count > 1 || (owners.Count == 1 && (disconnect || owners[0].Value<string>() != payload.Value<string>("addressControllerOwnerPath")))) Fail("Address ownership became shared or differs from the selected PLC address controller.");
                        addresses[i]["owners"] = owners.DeepClone();
                    }
                }
            }
            else if (kind == "network_configure")
            {
                // Only the selected node's requested attributes and subnet may change; a change TIA couples to them refuses.
                string nodeName = payload.Value<string>("nodeName"), subnet = payload.Value<string>("subnetName");
                JObject Node(JObject row)
                {
                    var nodes = (row["network"]?["nodes"] as JArray ?? new JArray()).OfType<JObject>().Where(node => nodeName == null || node.Value<string>("name") == nodeName).Take(2).ToArray();
                    return nodes.Length == 1 ? nodes[0] : throw new BridgeException("MutationVerificationFailed", "The selected network node is not exactly one manifest node.");
                }
                var node = Node(selected); var observed = Node(Target(after, path));
                foreach (var property in (payload["attributes"] as JObject ?? new JObject()).Properties()) node["attributes"][property.Name] = property.Value.DeepClone();
                if (subnet != null || payload.Value<bool?>("disconnect") == true)
                {
                    // Apply connects to an existing subnet by case-insensitive name; disconnect leaves none.
                    if (!string.Equals(observed.Value<string>("subnet"), subnet, StringComparison.OrdinalIgnoreCase)) Fail("The selected node's subnet differs from the request.");
                    node["subnet"] = observed["subnet"]?.DeepClone();
                }
            }
            else if (kind == "hardware_port_connect" || kind == "hardware_port_disconnect")
            {
                bool connect = kind == "hardware_port_connect";
                string partnerPath = payload.Value<string>("partnerHardwarePath");
                if (partnerPath == path) Fail("A port cannot be linked to itself.");
                void Link(JObject row, string other)
                {
                    var links = (row["port"] as JObject)?["connectedPorts"] as JArray ?? throw new BridgeException("MutationVerificationFailed", "A selected port has no captured interconnection list.");
                    var paths = links.Select(link => link.Value<string>()).ToList();
                    if (paths.Contains(other) == connect) Fail(connect ? "The ports were already connected." : "The ports were not connected.");
                    if (connect) paths.Add(other); else paths.Remove(other);
                    row["port"]["connectedPorts"] = new JArray(paths.OrderBy(value => value, StringComparer.Ordinal));
                }
                Link(selected, partnerPath);
                Link(Target(expected, partnerPath), path);
            }
            else if (kind == "hardware_replace")
            {
                var observed = Target(after, path);
                if (selected.Value<string>("typeIdentifier") == payload.Value<string>("typeIdentifier") || observed.Value<string>("typeIdentifier") != payload.Value<string>("typeIdentifier") ||
                    observed.Value<string>("name") != selected.Value<string>("name") || !JToken.DeepEquals(observed["positionNumber"], selected["positionNumber"]) || !JToken.DeepEquals(observed["containerPath"], selected["containerPath"]))
                    Fail("The replaced item differs from its reviewed type identifier, or its name, slot or container changed.");
                if (payload.Value<bool?>("acceptLoss") != true)
                {
                    var lost = ReplaceLosses(ReplaceInventory(before, path), ReplaceInventory(after, path));
                    if (lost.Count != 0) Fail("The replacement lost configured addresses or child items; restore the checkpoint, or prepare again with acceptLoss: true. Lost: " + lost.ToString(Newtonsoft.Json.Formatting.None));
                }
                // The new type may bring its own channels, attributes and children; everything outside the subtree stays exact.
                foreach (var row in expected.Where(row => InTree(row, path)).ToArray()) row.Remove();
                foreach (var row in after.Where(row => InTree(row, path))) expected.Add(row.DeepClone());
            }
            else if (kind == "hardware_transfer_area_create" || kind == "hardware_transfer_area_delete")
            {
                // Only the named area of the selected interface may appear or disappear; its other areas stay exact.
                bool create = kind == "hardware_transfer_area_create";
                string name = payload.Value<string>("name");
                JArray Areas(JObject row) => row["network"]?["transferAreas"] as JArray ?? throw new BridgeException("MutationVerificationFailed", "The selected interface has no captured transfer-area list.");
                JArray Others(JArray areas) => new JArray(areas.Where(area => area.Value<string>("name") != name).Select(area => area.DeepClone()));
                var old = Areas(selected); var now = Areas(Target(after, path));
                if (old.Count(area => area.Value<string>("name") == name) != (create ? 0 : 1) || now.Count(area => area.Value<string>("name") == name) != (create ? 1 : 0) || !JToken.DeepEquals(Others(old), Others(now)))
                    Fail("The selected interface's transfer areas differ from exactly one " + (create ? "created" : "deleted") + " area named " + name + ".");
                selected["network"]["transferAreas"] = now.DeepClone();
            }
            else Fail("Unknown hardware preservation policy.");
            JArray Sorted(JArray rows) => new JArray(rows.Cast<JObject>().OrderBy(row => row.Value<string>("path"), StringComparer.Ordinal));
            if (!JToken.DeepEquals(Sorted(expected), Sorted(after))) Fail("An unrelated configured hardware, channel, address or network field changed; restore the checkpoint before further edits.");
        }

        // An address survives when the subtree still has its ioType and start with at least its length and the same owners.
        private static JArray ReplaceLosses(JObject before, JObject after)
        {
            var lost = new JArray();
            foreach (var address in before["addresses"].OfType<JObject>())
                if (!after["addresses"].OfType<JObject>().Any(row => row.Value<string>("ioType") == address.Value<string>("ioType") && row.Value<long>("startAddress") == address.Value<long>("startAddress") &&
                    row.Value<long>("lengthBits") >= address.Value<long>("lengthBits") && JToken.DeepEquals(row["owners"], address["owners"])))
                    lost.Add(new JObject { ["address"] = address.DeepClone() });
            var kept = new HashSet<string>(after["children"].Select(child => child.Value<string>()), StringComparer.Ordinal);
            foreach (var child in before["children"]) if (!kept.Contains(child.Value<string>())) lost.Add(new JObject { ["child"] = child.DeepClone() });
            return lost;
        }

        // Added, removed and changed configured rows by exact path, for review of a CAx import.
        internal static JObject Difference(JArray before, JArray after)
        {
            RequireIdentities(before); RequireIdentities(after);
            var old = before.ToDictionary(row => row.Value<string>("path"), StringComparer.Ordinal);
            var now = after.ToDictionary(row => row.Value<string>("path"), StringComparer.Ordinal);
            JArray Paths(IEnumerable<string> paths) => new JArray(paths.OrderBy(path => path, StringComparer.Ordinal));
            return new JObject { ["added"] = Paths(now.Keys.Where(path => !old.ContainsKey(path))), ["removed"] = Paths(old.Keys.Where(path => !now.ContainsKey(path))),
                ["changed"] = Paths(old.Keys.Where(path => now.ContainsKey(path) && !JToken.DeepEquals(old[path], now[path]))) };
        }

        private static void RequireIdentities(JArray rows)
        {
            if (rows == null) Fail("Hardware preservation manifest is absent.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in rows)
            {
                if (!(token is JObject row) || row["path"]?.Type != JTokenType.String)
                    Fail("Every hardware preservation row must be an object with an exact string path.");
                string path = token.Value<string>("path");
                if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || !paths.Add(path))
                    Fail("Hardware preservation paths must be nonempty, control-free and unique.");
            }
        }

        private static void Fail(string reason) => throw new BridgeException("MutationVerificationFailed", reason);
    }
}
