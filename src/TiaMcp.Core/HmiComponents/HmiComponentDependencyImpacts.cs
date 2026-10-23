using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.HmiComponents;

/// <summary>References between existing evidence sections, never inferred native relationships.</summary>
internal static class HmiComponentDependencyImpacts
{
    internal const int ByteLimit = 1024 * 1024;

    internal static void Add(JsonObject report)
    {
        var associations = report["dependencyAssociations"]!.AsArray();
        var consumers = report["consumerPaths"]!.AsArray();
        var bindings = report["bindings"]!.AsArray();
        var outputs = report["outputs"]!.AsArray();
        var effects = report["effectChecks"]!.AsArray();
        var derived = report["derivedEffects"]!.AsArray();
        var expectations = report["expectationCoverage"]!.AsArray();
        var rows = new JsonArray();
        int bytes = 2;
        JsonArray Indices(IEnumerable<int> values) => new(values.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
        JsonArray Paths(IEnumerable<string> values) => new(values.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        for (int index = 0; index < associations.Count; index++)
        {
            var association = associations[index]!;
            var selection = association["selection"]!;
            string scope = (string)selection["scope"]!, reviewId = (string)association["reviewId"]!;
            string owner = (string)selection["objectPath"]!, dependency = (string)selection["targetObjectPath"]!;
            string memberField = scope == "beforeComponent" ? "beforeMember" : "afterMember";
            var roots = new HashSet<string>([owner, dependency], StringComparer.Ordinal);
            int[] consumerIndices = Enumerable.Range(0, consumers.Count).Where(i =>
                (string?)consumers[i]!["scope"] == scope && (string?)consumers[i]!["reviewId"] == reviewId &&
                roots.Contains((string)consumers[i]!["rootObjectPath"]!)).ToArray();
            var affected = new HashSet<string>(roots, StringComparer.Ordinal);
            foreach (int i in consumerIndices) affected.Add((string)consumers[i]!["consumerObjectPath"]!);
            int[] bindingIndices = Enumerable.Range(0, bindings.Count).Where(i =>
                bindings[i]!["selection"]![memberField]?["objectPath"] is JsonValue path &&
                path.TryGetValue<string>(out var selected) && affected.Contains(selected)).ToArray();
            var mapped = bindingIndices.Select(i => (string)bindings[i]!["selection"]![memberField]!["objectPath"]!).ToHashSet(StringComparer.Ordinal);
            var outputIds = bindingIndices.Select(i => (string)bindings[i]!["selection"]!["outputId"]!).ToHashSet(StringComparer.Ordinal);
            var outputRows = new JsonArray();
            for (int outputIndex = 0; outputIndex < outputs.Count; outputIndex++)
            {
                string id = (string)outputs[outputIndex]!["id"]!;
                if (!outputIds.Contains(id)) continue;
                int effectIndex = Enumerable.Range(0, effects.Count).Single(i => (string?)effects[i]!["outputId"] == id);
                int expectationIndex = Enumerable.Range(0, expectations.Count).Single(i => (string?)expectations[i]!["outputId"] == id);
                outputRows.Add(new JsonObject {
                    ["outputId"] = id, ["outputIndex"] = outputIndex, ["action"] = outputs[outputIndex]!["selection"]!["action"]!.DeepClone(),
                    ["effectCheckIndex"] = effectIndex, ["effectStatus"] = effects[effectIndex]!["status"]!.DeepClone(),
                    ["expectationCoverageIndex"] = expectationIndex,
                    ["derivedEffectIndices"] = Indices(Enumerable.Range(0, derived.Count).Where(i => (string?)derived[i]!["expectation"]?["outputId"] == id))
                });
            }
            var row = new JsonObject {
                ["associationIndex"] = index, ["scope"] = scope, ["reviewId"] = reviewId,
                ["componentObjectPath"] = owner, ["dependencyObjectPath"] = dependency,
                ["associationStatus"] = association["status"]!.DeepClone(),
                ["rootObjectPaths"] = Paths(roots), ["consumerPathIndices"] = Indices(consumerIndices),
                ["bindingIndices"] = Indices(bindingIndices), ["outputs"] = outputRows,
                ["unmappedObjectPaths"] = Paths(affected.Except(mapped, StringComparer.Ordinal)),
                ["nativeImpactVerified"] = false, ["consumerCoverageComplete"] = false, ["outputMappingComplete"] = false
            };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length + (rows.Count == 0 ? 0 : 1);
            if (size > ByteLimit - bytes) break;
            rows.Add(row); bytes += size;
        }
        report["schemaVersion"] = "hmi-component-review.v7";
        report["dependencyImpacts"] = rows;
        report["dependencyImpactCoverage"] = new JsonArray(new JsonObject {
            ["associationCount"] = associations.Count, ["includedAssociationCount"] = rows.Count,
            ["omittedAssociationIndices"] = Indices(Enumerable.Range(rows.Count, associations.Count - rows.Count)),
            ["returnedBytes"] = bytes, ["maxBytes"] = ByteLimit, ["truncated"] = rows.Count != associations.Count,
            ["consumerCoverageComplete"] = false, ["outputMappingComplete"] = false, ["nativeImpactVerified"] = false
        });
        report["summary"]!["dependencyImpactCount"] = rows.Count;
        report["summary"]!["dependencyImpactOutputCount"] = rows.Sum(r => r!["outputs"]!.AsArray().Count);
        report["summary"]!["dependencyImpactOmittedCount"] = associations.Count - rows.Count;
        report["dependencyImpactPolicy"] = "References into existing evidence sections only. Each explicit dependency selection retains its agreement status, exact component scope and review. Its owner and target plus their saved incoming consumer paths select only explicitly supplied before/after member bindings in that same scope. Output links reference existing file-effect and derived-expectation checks; agreement or equal files never establishes an actual native effect. Unmapped objects, conflicting/unavailable associations and original traversal exclusions remain visible. No cross-review edges, transitive library closure, generated ownership, complete consumers or native admission are inferred. Rows form a bounded 1 MiB prefix; omitted association indices remain explicit.";
    }
}
