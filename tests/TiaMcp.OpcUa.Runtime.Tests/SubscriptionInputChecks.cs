using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class SubscriptionInputChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string minimal = """{"attemptId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","nodes":[{"namespaceUri":"urn:fixture","identifierType":"string","identifier":"bool"}]}""";
        var expected = new OpcUaSubscriptionRequest(new('a',32), [new("urn:fixture","string","bool")]);
        var canonical = JsonSerializer.SerializeToElement(expected, BridgeProtocol.Json);
        foreach (var (name, options) in new[] { ("retained", BridgeProtocol.Json), ("independent Web", new JsonSerializerOptions(JsonSerializerDefaults.Web)) })
        {
            var actual = JsonSerializer.Deserialize<OpcUaSubscriptionRequest>(minimal, options)!;
            check(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(actual, options), canonical), name + " omitted defaults unchanged");
            check(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<OpcUaSubscriptionRequest>(canonical, options), options), canonical), name + " exact normalized roundtrip");
            foreach (string key in new[] { "reconnect", "transfer", "write", "resume", "maximumNotification", "discardOldets" })
            {
                var value = JsonNode.Parse(minimal)!; value[key] = true;
                bool refused = false;
                try { JsonSerializer.Deserialize<OpcUaSubscriptionRequest>(value, options); } catch (JsonException) { refused = true; }
                check(refused, name + " unknown request option refused: " + key);
            }
            foreach (string key in new[] { "namespaceIndex", "identifierTypo", "reconnect" })
            {
                var value = JsonNode.Parse(minimal)!; value["nodes"]![0]![key] = true;
                bool refused = false;
                try { JsonSerializer.Deserialize<OpcUaSubscriptionRequest>(value, options); } catch (JsonException) { refused = true; }
                check(refused, name + " unknown nested node option refused: " + key);
            }
            // Local converter clones options; no shared-node/global strictness migration.
            check(options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Skip, name + " shared serializer options unchanged");
            var oldNode = JsonNode.Parse(minimal)!["nodes"]![0]!; oldNode["legacyExtra"] = true;
            check(JsonSerializer.Deserialize<OpcUaRuntimeNode>(oldNode, options) == expected.Nodes[0], name + " existing shared node deserialization unchanged");
            var many = expected with { Nodes = Enumerable.Range(0,64).Select(i => new OpcUaRuntimeNode("urn:fixture","numeric",i.ToString())).ToArray() };
            var roundtrip = JsonSerializer.Deserialize<OpcUaSubscriptionRequest>(JsonSerializer.Serialize(many, options), options)!;
            check(roundtrip.Nodes.SequenceEqual(many.Nodes), name + " full node bound preserves exact identities");
        }
    }
}
