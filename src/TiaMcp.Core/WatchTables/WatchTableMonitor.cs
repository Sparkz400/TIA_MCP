using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.WatchTables;

/// <summary>Live values for the rows of an offline watch or force table, read from a PLCSIM Advanced instance this Host owns.
/// Read only: V21 Openness, the PLCSIM V8 API, the S7-1500 Web API and OPC UA expose no force route, and a write is an assignment, not a force.</summary>
public static class WatchTableMonitor
{
    public const string Note = "Read only. Each sample reads the resolved symbols one after another over the PLCSIM API; values of one sample are not from one scan. " +
        "ModifyValue, ForceValue and ForceIntention are stored table configuration and are never applied: nothing is modified or forced. " +
        "The instance's program is not compared with the project.";
    private static readonly string[] Scalars = ["bool", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double"];

    public static async Task<JsonObject> Run(IBridgeClient engineering, IBridgeClient simulator, string tableName, string? projectName, string? plcPath, string group,
        bool forceTable, int instanceId, string instanceName, string creationId, int offset, int count, int samples, int intervalMs, CancellationToken token)
    {
        if (offset < 0 || count is < 1 or > 64 || samples is < 1 or > 20 || intervalMs is < 100 or > 5000)
            throw new BridgeException("InvalidRequest", "offset >= 0, count 1..64, samples 1..20 and intervalMs 100..5000 are required; nothing was read.");
        // Ownership first: it is the cheap refusal. Native simulator calls are never cancelled; a cancelled bridge call faults the simulator worker.
        var identity = await simulator.CallAsync("sim_identity", new { instanceId, instanceName, creationId }, CancellationToken.None);
        var snapshot = await engineering.CallAsync("watch_read", new { tableName, projectName, plcPath, group, targetKind = forceTable ? "forceTable" : null }, token);
        var target = snapshot.GetProperty("target");
        var all = target.GetProperty("rows");
        var lookups = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var rows = new JsonArray(); var resolved = new List<(int Ordinal, string Symbol, string Type)>();
        int end = Math.Min(all.GetArrayLength(), offset + count);
        for (int ordinal = offset; ordinal < end; ordinal++)
        {
            var row = all[ordinal];
            JsonElement? tags = null;
            if (Filter(row) is { } filter)
            {
                if (!lookups.TryGetValue(filter, out var found))
                    lookups[filter] = found = await simulator.CallAsync("sim_tags", new { instanceId, filter, maxItems = 2000, refresh = lookups.Count == 0 }, CancellationToken.None);
                tags = found;
            }
            var resolution = Resolve(row, tags);
            if (resolution["status"]!.GetValue<string>() == "resolved")
                resolved.Add((ordinal, resolution["symbol"]!.GetValue<string>(), resolution["type"]!.GetValue<string>()));
            rows.Add(new JsonObject { ["ordinal"] = ordinal, ["row"] = JsonNode.Parse(row.GetRawText()), ["resolution"] = resolution });
        }
        var signals = resolved.Select(r => (r.Symbol, r.Type)).Distinct().ToArray();
        var sampled = new JsonArray();
        for (int i = 0; i < samples && signals.Length > 0; i++)
        {
            if (i > 0) await Task.Delay(intervalMs, token);
            var read = await simulator.CallAsync("sim_read", new { instanceId, signals = signals.Select(s => new { symbol = s.Symbol, type = s.Type }) }, CancellationToken.None);
            sampled.Add(new JsonObject { ["sample"] = i, ["values"] = Values(read, instanceId, instanceName, signals, resolved) });
        }
        return new JsonObject
        {
            ["schemaVersion"] = "watch-table-monitor.v1",
            ["table"] = new JsonObject { ["kind"] = target.GetProperty("kind").GetString(), ["group"] = target.GetProperty("group").GetString(),
                ["name"] = target.GetProperty("name").GetString(), ["semanticSha256"] = target.GetProperty("semanticSha256").GetString(),
                ["isConsistent"] = target.GetProperty("isConsistent").GetBoolean(), ["rowCount"] = all.GetArrayLength(),
                ["reference"] = JsonNode.Parse(snapshot.GetProperty("observation").GetProperty("reference").GetRawText()) },
            ["target"] = new JsonObject { ["instanceId"] = instanceId, ["instanceName"] = instanceName, ["creationId"] = creationId,
                ["origin"] = identity.GetProperty("origin").GetString(), ["communicationInterface"] = JsonNode.Parse(identity.GetProperty("communicationInterface").GetRawText()) },
            ["page"] = new JsonObject { ["offset"] = offset, ["returned"] = rows.Count, ["total"] = all.GetArrayLength(), ["nextOffset"] = end < all.GetArrayLength() ? (int?)end : null },
            ["rows"] = rows, ["intervalMs"] = intervalMs, ["samples"] = sampled,
            ["readOnly"] = true, ["modified"] = false, ["forced"] = false, ["physicalPlc"] = false, ["programMatchVerified"] = false, ["note"] = Note,
        };
    }

    /// <summary>The PLCSIM tag-list filter for a row: the last segment of its unquoted symbol, or null for a row without a symbol.</summary>
    public static string? Filter(JsonElement row)
    {
        if (Text(row, "name") is not { } name) return null;
        string key = Key(name); int dot = key.LastIndexOf('.');
        return dot < 0 || dot == key.Length - 1 ? key : key[(dot + 1)..];
    }

    /// <summary>How one row maps to a PLCSIM tag; tags is the sim_tags result for Filter(row).</summary>
    public static JsonObject Resolve(JsonElement row, JsonElement? tags)
    {
        if (Text(row, "name") is not { } name)
            return Text(row, "address") is null ? new() { ["status"] = "comment" }
                // shortcut: absolute-address rows are not read (the worker has symbolic reads only); add PLCSIM area reads when a caller needs them.
                : new() { ["status"] = "unresolved", ["reason"] = "absoluteAddressOnly" };
        if (tags is not { } list) throw new ArgumentException("A symbolic row needs its tag-list lookup.");
        string key = Key(name);
        var matches = list.GetProperty("tags").EnumerateArray().Where(t => Key(t.GetProperty("symbol").GetString()!) == key).ToArray();
        if (matches.Length != 1)
            return new() { ["status"] = "unresolved", ["reason"] = matches.Length > 1 ? "ambiguousInTargetTagList"
                : list.GetProperty("truncated").GetBoolean() ? "targetTagListTruncated" : "notInTargetTagList" };
        var tag = matches[0];
        string dataType = tag.GetProperty("dataType").GetString()!;
        string? type = Type(tag.GetProperty("primitiveDataType").GetString()!, dataType);
        return new() { ["status"] = type is null ? "unsupportedType" : "resolved", ["symbol"] = tag.GetProperty("symbol").GetString(), ["type"] = type, ["dataType"] = dataType };
    }

    /// <summary>TIA quotes symbol segments ("DB".Member); the PLCSIM tag list need not, so both sides compare without quotes.</summary>
    public static string Key(string symbol) => symbol.Replace("\"", "");

    // The typed reads sim_read offers; Struct, Char, WChar and anything else have none.
    internal static string? Type(string primitive, string dataType) =>
        Scalars.FirstOrDefault(t => t.Equals(primitive, StringComparison.OrdinalIgnoreCase)) ?? dataType switch { "String" => "string", "WString" => "wstring", _ => null };

    private static JsonArray Values(JsonElement read, int instanceId, string instanceName, (string Symbol, string Type)[] signals, List<(int Ordinal, string Symbol, string Type)> resolved)
    {
        var values = read.GetProperty("values");
        if (read.GetProperty("instanceId").GetInt32() != instanceId || read.GetProperty("instanceName").GetString() != instanceName || values.GetArrayLength() != signals.Length ||
            signals.Where((s, i) => values[i].GetProperty("symbol").GetString() != s.Symbol || values[i].GetProperty("type").GetString() != s.Type).Any())
            throw new BridgeException("PlcsimResponseInvalid", "The simulator worker returned values for another instance or other symbols; nothing is reported.");
        return new JsonArray(resolved.Select(r =>
        {
            var value = JsonNode.Parse(values[Array.IndexOf(signals, (r.Symbol, r.Type))].GetRawText())!.AsObject();
            value["ordinal"] = r.Ordinal;
            return (JsonNode?)value;
        }).ToArray());
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length > 0 ? value.GetString() : null;
}
