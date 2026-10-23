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
        internal static bool Applies(string kind) => new[] { "hardware_plug", "hardware_move", "hardware_channel_attributes", "hardware_address_set", "network_io_assign" }.Contains(kind);

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
            var selected = Target(expected, path);
            if (kind == "hardware_plug")
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
            else Fail("Unknown hardware preservation policy.");
            JArray Sorted(JArray rows) => new JArray(rows.Cast<JObject>().OrderBy(row => row.Value<string>("path"), StringComparer.Ordinal));
            if (!JToken.DeepEquals(Sorted(expected), Sorted(after))) Fail("An unrelated configured hardware, channel, address or network field changed; restore the checkpoint before further edits.");
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
