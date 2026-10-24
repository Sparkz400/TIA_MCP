using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static partial class Program
{
    private static async Task FeatureCatalogPagination()
    {
        var catalog = FeatureCatalog.LoadBundled();
        var first = JsonSerializer.SerializeToElement(catalog.Query(maxItems: 2));
        var second = JsonSerializer.SerializeToElement(catalog.Query(offset: 2, maxItems: 2));
        Check.Equal(2, first.GetProperty("features").GetArrayLength());
        Check.Equal(false, first.GetProperty("targetAvailabilityProbed").GetBoolean());
        Check.Equal(2, first.GetProperty("pagination").GetProperty("nextOffset").GetInt32());
        var ids = first.GetProperty("features").EnumerateArray().Concat(second.GetProperty("features").EnumerateArray())
            .Select(f => f.GetProperty("featureId").GetString()).ToArray();
        Check.Equal(4, ids.Distinct().Count());
        var gaps = JsonSerializer.SerializeToElement(catalog.Query(implementationStatus: "not_implemented", maxItems: 200));
        foreach (var feature in gaps.GetProperty("features").EnumerateArray())
        {
            Check.Equal(0, feature.GetProperty("mcpTools").GetArrayLength());
            Check.Equal("unverified", feature.GetProperty("qualification").GetProperty("status").GetString());
        }
        var startup = JsonSerializer.SerializeToElement(catalog.Query(featureId: "sim_state"));
        // sim_state is verified only by a record that exercised all four actions (powerOn, run, stop, powerOff).
        Check.Equal("verified", startup.GetProperty("features")[0].GetProperty("qualification").GetProperty("status").GetString());
        var startupEvidence = startup.GetProperty("evidence").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Check.Equal("passed", startupEvidence["local.sim.state.20261024"].GetProperty("status").GetString());
        await Check.BridgeError("InvalidRequest", () => { catalog.Query(maxItems: 0); return Task.CompletedTask; });
        await Check.BridgeError("InvalidRequest", () => { catalog.Query(implementationStatus: "perfect"); return Task.CompletedTask; });
        await Check.BridgeError("FeatureNotFound", () => { catalog.Query(featureId: "unknown"); return Task.CompletedTask; });
    }

    private static async Task FeatureCatalogValidation()
    {
        using var stream = typeof(FeatureCatalog).Assembly.GetManifestResourceStream("TiaMcp.FeatureCatalog.v1.json")!;
        using var reader = new StreamReader(stream);
        var original = reader.ReadToEnd();
        // Claim verified explicitly so the verified-only rules do not depend on the bundled entry's state.
        static JsonNode Verified(JsonNode root) { root["features"]![0]!["qualification"]!["status"] = "verified"; return root; }
        static JsonNode Cited(JsonNode root) => root["evidence"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() ==
            root["features"]![0]!["qualification"]!["evidenceIds"]![0]!.GetValue<string>())!;
        foreach (var corrupt in new Action<JsonNode>[] {
            root => root["features"]![1]!["featureId"] = root["features"]![0]!["featureId"]!.GetValue<string>(),
            root => root["features"]![0]!["qualification"]!["evidenceIds"] = new JsonArray("invented-proof"),
            root => Verified(root)["features"]![0]!["qualification"]!["evidenceIds"] = new JsonArray(),
            root => Verified(root)["features"]![0]!["qualification"]!["evidenceIds"] = new JsonArray("local.sim.startup.v02"),
            root => { root["features"]![0]!["qualification"]!["status"] = "failed";
                root["features"]![0]!["qualification"]!["evidenceIds"] = new JsonArray("local.offline.v02"); },
            root => Cited(Verified(root))["status"] = "blocked",
            root => Cited(Verified(root))["status"] = "skipped",
            root => root["evidence"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "local.offline.v02")!["status"] = "perfect",
            root => root["features"]![0]!["implementation"]!["status"] = "not_implemented",
            root => root["features"]![0]!["acceptanceCriteria"] = new JsonArray("unknown-criterion")
        })
        {
            var changed = JsonNode.Parse(original)!;
            corrupt(changed);
            await Check.ThrowsAsync<InvalidDataException>(() => Task.FromResult(new FeatureCatalog(changed.ToJsonString())));
        }
    }
}
