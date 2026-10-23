using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Exact native-02 capture: this is one explicit ET 200SP factory companion,
    // not a wildcard exemption for generated or newly observed hardware.
    internal static class RemoteIoCompanionContract
    {
        internal const string HeadType = "OrderNumber:6ES7 155-6AU02-0BN0/V6.4";
        internal const string ServerType = "OrderNumber:6ES7 193-6PA00-0AA0/V1.2";
        internal static JArray ExpectedRows(JObject payload)
        {
            if (payload["expectedServerModule"] == null) return new JArray();
            var server = payload["expectedServerModule"] as JObject ?? throw new BridgeException("InvalidRequest", "expectedServerModule must be an explicit object.");
            string[] fields = { "name", "typeIdentifier", "containerHardwarePath", "positionNumber", "attributes" };
            if (server.Properties().Any(property => !fields.Contains(property.Name)) || fields.Any(name => server[name] == null)) Invalid();
            if (new[] { "name", "typeIdentifier", "containerHardwarePath" }.Any(field => server[field].Type != JTokenType.String)) Invalid();
            string name = server.Value<string>("name"), container = server.Value<string>("containerHardwarePath");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || server.Value<string>("typeIdentifier") != ServerType ||
                server["positionNumber"].Type != JTokenType.Integer || !int.TryParse(server["positionNumber"].ToString(), out int slot) || slot != 1) Invalid();
            string target = payload.Value<string>("hardwarePath");
            int marker = target?.IndexOf("/items/", StringComparison.Ordinal) ?? -1;
            if (marker < 0 || string.IsNullOrEmpty(container)) Invalid();
            string station = target.Substring(0, marker);
            if (!container.StartsWith(station + "/items/", StringComparison.Ordinal)) Invalid();
            string path = station + "/items/" + Uri.EscapeDataString(name);
            string[] attributeNames = { "AdditionalInformation", "Author", "Comment", "LocationIdentifier", "Name", "PlantDesignation" };
            var attributes = server["attributes"] as JObject;
            if (attributes == null || attributes.Count != attributeNames.Length || attributeNames.Any(key => attributes[key]?.Type != JTokenType.String || attributes.Value<string>(key).Length > 4096) || attributes.Value<string>("Name") != name) Invalid();
            return new JArray(new JObject { ["path"] = path, ["name"] = name, ["typeIdentifier"] = ServerType, ["positionNumber"] = 1,
                ["containerPath"] = container, ["attributes"] = attributes.DeepClone(), ["channels"] = new JArray(), ["addresses"] = new JArray() },
                new JObject { ["path"] = path + "/items/" + Uri.EscapeDataString(name), ["name"] = name, ["typeIdentifier"] = null, ["positionNumber"] = 1,
                    ["containerPath"] = path, ["attributes"] = new JObject { ["Comment"] = "", ["Name"] = name }, ["channels"] = new JArray(),
                    ["addresses"] = new JArray(new JObject { ["ioType"] = "Diagnosis", ["startAddress"] = -1, ["lengthBits"] = 0, ["owners"] = new JArray() }) });
        }
        private static void Invalid() => throw new BridgeException("InvalidRequest", "expectedServerModule must match the captured IM 155-6AU02 V6.4 companion: exact server V1.2 identifier, name, same-station native rack, slot 1 and six explicit string attributes.");
    }
}
