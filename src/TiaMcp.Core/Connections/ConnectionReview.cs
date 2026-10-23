using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Connections;

/// <summary>Supplied connection comparison, without native property or resource interpretation.</summary>
public static class ConnectionReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Input must contain 1 byte..4 MiB.");
        string text = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                { _ = Utf8.GetByteCount(p.Name); if (!names.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
        }
        Unique(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an object.");
    }

    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request)
    {
        Fields(request, "schemaVersion reviewId afterKind before after matches");
        if (Text(request, "schemaVersion") != "connection-review-request.v1" || Text(request, "reviewId") != reviewId)
            throw Error("Request must name this exact saved review and schema.");
        if (Text(request, "afterKind") is not ("observation" or "proposal")) throw Error("afterKind must be observation or proposal.");
        var nodes = review["model"]!["nodes"]!.AsArray().Select(Object).ToDictionary(n => Text(n, "nodeId"), StringComparer.Ordinal);
        var paths = nodes.Values.Where(n => (string?)n["origin"] != "virtual" && !string.IsNullOrWhiteSpace((string?)n["objectPath"]))
            .ToDictionary(n => Text(n, "objectPath"), StringComparer.Ordinal);
        var before = Snapshot(Object(request["before"])); var after = Snapshot(Object(request["after"]));
        var matches = Array(request, "matches", 512);
        var usedBefore = new HashSet<string>(StringComparer.Ordinal); var usedAfter = new HashSet<string>(StringComparer.Ordinal);
        var comparisons = new JsonArray(); var outcomes = new Dictionary<(string Phase, string Id), string>();
        foreach (var value in matches)
        {
            var match = Object(value); Fields(match, "beforeId afterId");
            string left = Text(match, "beforeId", 256), right = Text(match, "afterId", 256);
            if (!before.TryGetValue(left, out var b) || !after.TryGetValue(right, out var a) || !usedBefore.Add(left) || !usedAfter.Add(right))
                throw Error("Every explicit match must name two supplied identities exactly once.");
            if (Text(b, "origin") != Text(a, "origin")) throw Error("Engineering, program and HMI software origins cannot be substituted.");
            var changed = new JsonArray();
            foreach (string field in new[] { "protocol", "local", "partner" })
                if (!JsonNode.DeepEquals(b[field], a[field])) changed.Add(field);
            var bp = Object(b["properties"]); var ap = Object(a["properties"]);
            foreach (string key in bp.Select(p => p.Key).Union(ap.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
                if (bp.ContainsKey(key) != ap.ContainsKey(key) || !JsonNode.DeepEquals(bp[key], ap[key]))
                    changed.Add("properties/" + key.Replace("~", "~0").Replace("/", "~1"));
            string status = changed.Count == 0 ? "unchanged" : "changed";
            outcomes.Add(("before", left), status); outcomes.Add(("after", right), status);
            comparisons.Add(new JsonObject { ["beforeId"] = left, ["afterId"] = right, ["status"] = status, ["changedFields"] = changed });
        }
        foreach (var (phase, state, used) in new[] { ("before", before, usedBefore), ("after", after, usedAfter) })
            foreach (string id in state.Keys.Where(id => !used.Contains(id)))
            {
                string status = phase == "before" ? "unmatchedBefore" : "unmatchedAfter";
                outcomes.Add((phase, id), status);
                comparisons.Add(new JsonObject { ["beforeId"] = phase == "before" ? id : null, ["afterId"] = phase == "after" ? id : null,
                    ["status"] = status, ["changedFields"] = new JsonArray() });
            }
        var observations = new JsonArray(); var consumers = new JsonArray(); int unresolvedEndpoints = 0, unresolvedConsumers = 0;
        foreach (var (phase, state) in new[] { ("before", before), ("after", after) })
        {
            foreach (var pair in state)
            {
                var local = Endpoint(Object(pair.Value["local"])); var partner = Endpoint(Object(pair.Value["partner"]));
                unresolvedEndpoints += ((string)local["status"]! == "capturedHardwareAncestry" ? 0 : 1) + ((string)partner["status"]! == "capturedHardwareAncestry" ? 0 : 1);
                observations.Add(new JsonObject { ["phase"] = phase, ["connection"] = pair.Value.DeepClone(), ["local"] = local, ["partner"] = partner,
                    ["advertisedFamily"] = Text(pair.Value, "protocol") is "S7" or "TCP" or "UDP" or "ISO" or "ISO-on-TCP" or "FDL" or "PtP" or "HMI",
                    ["resourceAssessment"] = "notAssessed" });
            }
            foreach (var value in Array(Object(request[phase]), "consumers", 2048))
            {
                var row = Object(value); Fields(row, "objectPath connectionId reason");
                string? path = NullableText(row, "objectPath"), connection = NullableText(row, "connectionId", 256); Text(row, "reason", 1024);
                bool captured = path is not null && paths.ContainsKey(path);
                bool supplied = connection is not null && state.ContainsKey(connection);
                if (connection is not null && !supplied && Boolean(Object(request[phase]), "connectionsComplete")) throw Error("Complete supplied connections contradict a declared consumer connection ID.");
                if (!captured || !supplied) unresolvedConsumers++;
                consumers.Add(new JsonObject { ["phase"] = phase, ["evidence"] = row.DeepClone(),
                    ["objectResolution"] = captured ? "capturedObject" : "unresolved",
                    ["connectionResolution"] = supplied ? "suppliedConnection" : "unresolved",
                    ["comparisonStatus"] = supplied ? outcomes[(phase, connection!)] : "unknown" });
            }
        }
        var summary = new JsonObject { ["beforeCount"] = before.Count, ["afterCount"] = after.Count, ["matchedCount"] = matches.Count,
            ["changedCount"] = comparisons.Count(c => (string?)c!["status"] == "changed"), ["unmatchedBeforeCount"] = before.Count - usedBefore.Count,
            ["unmatchedAfterCount"] = after.Count - usedAfter.Count, ["unresolvedEndpointCount"] = unresolvedEndpoints,
            ["consumerCount"] = consumers.Count, ["unresolvedConsumerCount"] = unresolvedConsumers };
        return new JsonObject { ["schemaVersion"] = "connection-review.v1", ["reviewId"] = reviewId, ["afterKind"] = request["afterKind"]!.DeepClone(),
            ["coverage"] = new JsonObject { ["beforeConnectionsComplete"] = Boolean(Object(request["before"]), "connectionsComplete"),
                ["afterConnectionsComplete"] = Boolean(Object(request["after"]), "connectionsComplete"),
                ["beforeConsumersComplete"] = Boolean(Object(request["before"]), "consumersComplete"), ["afterConsumersComplete"] = Boolean(Object(request["after"]), "consumersComplete") },
            ["observations"] = observations, ["comparisons"] = comparisons, ["consumers"] = consumers, ["matches"] = matches.DeepClone(), ["summary"] = summary,
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["wholeProjectAnalysisComplete"] = false,
            ["limits"] = "Explicit supplied mappings and saved model ancestry only. Properties and declared consumers are unverified evidence. Unmatched rows do not prove creation/deletion. No native property validation, resource/capacity/port rules, live topology, compilation or communication assessment." };

        Dictionary<string, JsonObject> Snapshot(JsonObject snapshot)
        {
            Fields(snapshot, "connectionsComplete consumersComplete connections consumers"); Boolean(snapshot, "connectionsComplete"); Boolean(snapshot, "consumersComplete");
            _ = Array(snapshot, "consumers", 2048);
            var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var value in Array(snapshot, "connections", 512))
            {
                var row = Object(value); Fields(row, "id origin protocol local partner properties");
                if (!rows.TryAdd(Text(row, "id", 256), row)) throw Error("Duplicate supplied connection identity.");
                if (Text(row, "origin") is not ("engineering" or "program" or "hmiSoftware")) throw Error("Unknown connection origin.");
                Text(row, "protocol", 64); _ = Object(row["properties"]);
                foreach (string side in new[] { "local", "partner" })
                {
                    var e = Object(row[side]); Fields(e, "ownerPath interfacePath reason");
                    string? owner = NullableText(e, "ownerPath"), face = NullableText(e, "interfacePath"), reason = NullableText(e, "reason", 1024);
                    if ((owner is null || face is null) && reason is null) throw Error("Missing endpoint selectors require an explicit reason.");
                }
            }
            return rows;
        }
        JsonObject Endpoint(JsonObject endpoint)
        {
            string? owner = NullableText(endpoint, "ownerPath"), face = NullableText(endpoint, "interfacePath"); string status = "selectorUnknown";
            if (owner is not null && face is not null)
            {
                if (!paths.TryGetValue(owner, out var target) || !paths.TryGetValue(face, out var item)) status = "pathNotCaptured";
                else if ((string?)target["kind"] != "hardware" || (string?)item["kind"] != "hardware") status = "notHardware";
                else
                {
                    status = "ownershipUnproven"; var visited = new HashSet<string>(StringComparer.Ordinal);
                    while (visited.Add(Text(item, "nodeId")))
                    {
                        if (Text(item, "nodeId") == Text(target, "nodeId")) { status = "capturedHardwareAncestry"; break; }
                        if ((string?)item["parentId"] is not string parent || !nodes.TryGetValue(parent, out item!)) break;
                    }
                }
            }
            return new JsonObject { ["status"] = status, ["interfaceCapabilityVerified"] = false, ["nativeOwnershipVerified"] = false };
        }
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object row.");
    private static void Fields(JsonObject value, string fields)
    { var expected = fields.Split(' '); if (value.Count != expected.Length || expected.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown fields; expected: " + fields); }
    private static JsonArray Array(JsonObject value, string key, int max) => value[key] is JsonArray rows && rows.Count <= max ? rows : throw Error(key + " collection exceeds bounds or is absent.");
    private static bool Boolean(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean " + key);
    private static string? NullableText(JsonObject value, string key, int max = 8192) => value[key] is null ? null : Text(value, key, max);
    private static string Text(JsonNode node, string key, int max = 8192)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null || text.Length > max || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Error("Invalid exact text: " + key);
        _ = Utf8.GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidConnectionReview", message);
}
