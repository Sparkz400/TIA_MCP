using System.Text.Json.Nodes;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Explicit bounded state intervals, never inferred enum or runtime semantics.</summary>
internal static class HmiNumericTextLists
{
    private static (long Min, long Max) Domain(string type) => type switch {
        "SInt" => (sbyte.MinValue, sbyte.MaxValue), "USInt" => (byte.MinValue, byte.MaxValue),
        "Int" => (short.MinValue, short.MaxValue), "UInt" => (ushort.MinValue, ushort.MaxValue),
        "DInt" => (int.MinValue, int.MaxValue), "UDInt" => (uint.MinValue, uint.MaxValue),
        "LInt" => (long.MinValue, long.MaxValue),
        _ => throw Error("Integer-state dataType must be SInt/USInt/Int/UInt/DInt/UDInt/LInt; bit strings, ULInt, floats, aliases and enums are unsupported.") };

    internal static JsonObject Intent(JsonObject template, string name)
    {
        Fields(template, "fileBase textLibrary languages defaultLanguage dataType requireFullTypeCoverage states");
        var domain = Domain(Text(template, "dataType", 16)); Boolean(template, "requireFullTypeCoverage");
        var texts = new JsonArray(); var entries = new JsonArray();
        foreach (var node in Array(template, "states", 256))
        {
            var state = Object(node); Fields(state, "id type fromValue toValue isDefault translations");
            string id = Text(state, "id", 128), type = Text(state, "type", 16);
            if (type is not ("SingleValue" or "Range")) throw Error("Integer states require explicit bounded SingleValue or Range intervals.");
            long from = Number(state["fromValue"]), to = Number(state["toValue"]);
            if (from < domain.Min || to > domain.Max || from > to || type == "SingleValue" && from != to)
                throw Error("State bounds must be ordered and fit the exact selected integer type.");
            texts.Add(new JsonObject { ["id"] = id, ["translations"] = state["translations"]?.DeepClone() });
            entries.Add(new JsonObject { ["id"] = id, ["type"] = type, ["fromValue"] = from, ["toValue"] = to,
                ["isDefault"] = state["isDefault"]?.DeepClone(), ["textId"] = id });
        }
        return new JsonObject {
            ["schemaVersion"] = "unified-text-list-author-request.v1", ["fileBase"] = template["fileBase"]?.DeepClone(),
            ["textLibrary"] = template["textLibrary"]?.DeepClone(), ["languages"] = template["languages"]?.DeepClone(),
            ["defaultLanguage"] = template["defaultLanguage"]?.DeepClone(), ["texts"] = texts,
            ["lists"] = new JsonArray(new JsonObject { ["name"] = name, ["rangeType"] = "Decimal", ["entries"] = entries }) };
    }

    private static long Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out long wide) ? wide :
        node is JsonValue narrow && narrow.TryGetValue<int>(out int integer) ? integer : throw Error("State bounds require exact signed 64-bit integer JSON values; fractions, strings and null refuse.");

    internal static JsonObject Coverage(JsonObject template, JsonObject intent)
    {
        string type = Text(template, "dataType", 16); var domain = Domain(type);
        var intervals = intent["lists"]![0]!["entries"]!.AsArray().Select(e => (From: Number(e!["fromValue"]), To: Number(e["toValue"]))).OrderBy(e => e.From).ToArray();
        var missing = new JsonArray(); long next = domain.Min; bool end = false;
        foreach (var interval in intervals)
        {
            if (interval.From > next) missing.Add(new JsonObject { ["fromValue"] = next, ["toValue"] = interval.From - 1 });
            if (interval.To == domain.Max) { end = true; break; }
            next = interval.To + 1; // Domain maximum handled first, including Int64.MaxValue.
        }
        if (!end) missing.Add(new JsonObject { ["fromValue"] = next, ["toValue"] = domain.Max });
        return new JsonObject { ["dataType"] = type, ["minimum"] = domain.Min, ["maximum"] = domain.Max,
            ["stateCount"] = intervals.Length, ["unlistedIntervals"] = missing, ["allTypeValuesListed"] = missing.Count == 0,
            ["requireFullTypeCoverage"] = template["requireFullTypeCoverage"]!.DeepClone(),
            ["defaultCoversUnlistedValuesVerified"] = false, ["nativeNumericSemanticsVerified"] = false };
    }

    internal static JsonArray Changes(JsonObject? previous, JsonObject current)
    {
        Dictionary<string, JsonObject> States(JsonObject intent)
        {
            var texts = intent["texts"]!.AsArray().ToDictionary(t => (string)t!["id"]!, t => t!["translations"]);
            return intent["lists"]!.AsArray().SelectMany(l => l!["entries"]!.AsArray()).GroupBy(e => (string)e!["id"]!)
                .ToDictionary(g => g.Key, g => new JsonObject { ["entries"] = new JsonArray(g.Select(e => e!.DeepClone()).ToArray()),
                    ["translations"] = new JsonArray(g.Select(e => texts[(string)e!["textId"]!]?.DeepClone()).ToArray()) });
        }
        var before = previous is null ? new Dictionary<string, JsonObject>() : States(previous); var after = States(current);
        return new JsonArray(before.Keys.Union(after.Keys).Order(StringComparer.Ordinal).Select(id => {
            before.TryGetValue(id, out var old); after.TryGetValue(id, out var next);
            return (JsonNode?)new JsonObject { ["id"] = id, ["change"] = old is null ? "added" : next is null ? "removed" : JsonNode.DeepEquals(old, next) ? "unchanged" : "changed",
                ["before"] = old?.DeepClone(), ["after"] = next?.DeepClone(), ["consumerCompatibilityVerified"] = false };
        }).ToArray());
    }

    internal static JsonObject Dependencies(JsonObject report, JsonObject binding)
    {
        var selected = new HashSet<(string Scope, string Path)>(); var relevantRoots = new HashSet<(string Scope, string Path)>();
        foreach (var (scope, field) in new[] { ("beforeComponent", "beforeMember"), ("afterComponent", "afterMember") })
        {
            string? path = (string?)binding["selection"]?[field]?["objectPath"];
            if (path is null) continue;
            selected.Add((scope, path)); relevantRoots.Add((scope, path));
            string? review = (string?)report[scope]?["selection"]?["reviewId"];
            foreach (var consumer in report["consumerPaths"]!.AsArray())
                if ((string?)consumer!["scope"] == scope && (string?)consumer["reviewId"] == review && (string?)consumer["consumerObjectPath"] == path)
                    relevantRoots.Add((scope, (string)consumer["rootObjectPath"]!));
        }
        selected.UnionWith(relevantRoots);
        var associations = new JsonArray(); int contradictions = 0;
        var source = report["dependencyAssociations"]!.AsArray();
        for (int index = 0; index < source.Count; index++)
        {
            var row = source[index]!; var s = row["selection"]!; string scope = (string)s["scope"]!, owner = (string)s["objectPath"]!, target = (string)s["targetObjectPath"]!;
            if (!relevantRoots.Contains((scope, owner)) && !relevantRoots.Contains((scope, target))) continue;
            selected.Add((scope, owner)); selected.Add((scope, target));
            if ((string?)row["status"] is "different" or "conflicting") contradictions++;
            associations.Add(new JsonObject { ["associationIndex"] = index, ["association"] = row.DeepClone() });
        }
        var coverage = new JsonArray(report["dependencyCoverage"]!.AsArray().Where(r =>
            selected.Contains(((string)r!["scope"]!, (string)r["objectPath"]!))).Select(r => r!.DeepClone()).ToArray());
        bool complete = coverage.Count == selected.Count && coverage.Count > 0 && coverage.All(r => (bool?)r!["allObservedIndicesMatched"] == true);
        return new JsonObject { ["selectedScopes"] = coverage, ["associations"] = associations, ["contradictionCount"] = contradictions,
            ["allObservedSelectedIndicesMatched"] = complete, ["dependencyClosureVerified"] = false, ["consumerCoverageComplete"] = false,
            ["sourceImpactCoverage"] = report["dependencyImpactCoverage"]!.DeepClone(),
            ["sourceConsumerCoverage"] = report["consumerCoverage"]!.DeepClone(),
            ["policy"] = "Exact selected member scopes plus original same-review incoming consumer roots select all original dependency associations, independent of bounded impact-page omission. Associated owner/target coverage stays explicit. No transitive closure, foreign-snapshot join or native identity is inferred." };
    }
}
