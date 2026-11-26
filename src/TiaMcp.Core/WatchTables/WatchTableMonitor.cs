using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.WatchTables;

/// <summary>Live values for the rows of an offline watch or force table, read from a PLCSIM Advanced instance this Host owns.
/// Read only: V21 Openness, the PLCSIM V8 API, the S7-1500 Web API and OPC UA expose no force route, and a write is an assignment, not a force.</summary>
public static class WatchTableMonitor
{
    public const string Note = "Read only. Each sample reads the resolved symbols over the PLCSIM API in one batched call (per symbol where a batch cannot be used); values of one sample are not from one scan. " +
        "Absolute-address rows (%I, %Q, %M, bit, byte, word or double word) are read from the process image with sim_area_read and reported as raw bytes and an unsigned big-endian value; the display format is not applied. " +
        "ModifyValue, ForceValue and ForceIntention are stored table configuration and are never applied: nothing is modified or forced. " +
        "The instance's program is not compared with the project.";
    private static readonly string[] Scalars = ["bool", "int8", "uint8", "int16", "uint16", "int32", "uint32", "int64", "uint64", "float", "double"];

    public static async Task<JsonObject> Run(IBridgeClient engineering, IBridgeClient simulator, string tableName, string? projectName, string? plcPath, string group,
        bool forceTable, int instanceId, string instanceName, string creationId, int offset, int count, int samples, int intervalMs, CancellationToken token)
    {
        if (offset < 0 || count is < 1 or > 64 || samples is < 1 or > 20 || intervalMs is < 100 or > 5000)
            throw new BridgeException("InvalidRequest", "offset >= 0, count 1..64, samples 1..20 and intervalMs 100..5000 are required; nothing was read.");
        // Ownership first: it is the cheap refusal. Native simulator calls are never cancelled; a cancelled bridge call is parked and later calls are refused with PreviousOperationRunning.
        var identity = await simulator.CallAsync("sim_identity", new { instanceId, instanceName, creationId }, CancellationToken.None);
        var snapshot = await engineering.CallAsync("watch_read", new { tableName, projectName, plcPath, group, targetKind = forceTable ? "forceTable" : null }, token);
        DropExport(snapshot);
        var target = snapshot.GetProperty("target");
        var all = target.GetProperty("rows");
        var rows = new JsonArray(); var resolved = new List<(int Ordinal, string Symbol, string Type)>(); var areas = new List<(int Ordinal, string Address, JsonObject Access)>();
        int end = Math.Min(all.GetArrayLength(), offset + count);
        // One exact-symbol lookup for the whole page: a leaf-name filter per row hit the 2000-tag cap on common names (a false truncation).
        var symbols = Enumerable.Range(offset, Math.Max(0, end - offset)).Select(i => Symbol(all[i])).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        JsonElement? found = symbols.Length == 0 ? null
            : await simulator.CallAsync("sim_tags", new { instanceId, symbols, maxItems = 2000, refresh = true }, CancellationToken.None);
        for (int ordinal = offset; ordinal < end; ordinal++)
        {
            var row = all[ordinal];
            JsonElement? tags = Symbol(row) is null ? null : found;
            var resolution = Resolve(row, tags);
            if (resolution["status"]!.GetValue<string>() == "resolved")
                if (resolution["area"] is null) resolved.Add((ordinal, resolution["symbol"]!.GetValue<string>(), resolution["type"]!.GetValue<string>()));
                else areas.Add((ordinal, Text(row, "address")!, (JsonObject)resolution.DeepClone()));
            rows.Add(new JsonObject { ["ordinal"] = ordinal, ["row"] = JsonNode.Parse(row.GetRawText()), ["resolution"] = resolution });
        }
        var signals = resolved.Select(r => (r.Symbol, r.Type)).Distinct().ToArray();
        var sampled = new JsonArray();
        for (int i = 0; i < samples && (signals.Length > 0 || areas.Count > 0); i++)
        {
            if (i > 0) await Task.Delay(intervalMs, token);
            var values = new List<JsonNode?>();
            if (signals.Length > 0)
            {
                var read = await simulator.CallAsync("sim_read", new { instanceId, signals = signals.Select(s => new { symbol = s.Symbol, type = s.Type }) }, CancellationToken.None);
                values.AddRange(Values(read, instanceId, instanceName, signals, resolved));
            }
            foreach (var area in areas) values.Add(await AreaValue(simulator, instanceId, instanceName, area));
            sampled.Add(new JsonObject { ["sample"] = i, ["values"] = new JsonArray(values.OrderBy(v => v!["ordinal"]!.GetValue<int>()).ToArray()) });
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
            ["page"] = new JsonObject { ["offset"] = offset, ["returned"] = rows.Count, ["total"] = all.GetArrayLength(), ["nextOffset"] = end < all.GetArrayLength() ? (int?)end : null,
                ["totalItems"] = all.GetArrayLength(), ["pageCount"] = PageTotals.PageCount(all.GetArrayLength(), count) },
            ["rows"] = rows, ["intervalMs"] = intervalMs, ["samples"] = sampled,
            ["readOnly"] = true, ["modified"] = false, ["forced"] = false, ["physicalPlc"] = false, ["programMatchVerified"] = false, ["note"] = Note,
        };
    }

    /// <summary>The unquoted symbol of a row for the exact tag-list lookup, or null for a row without a symbol.</summary>
    public static string? Symbol(JsonElement row) => Text(row, "name") is { } name ? Key(name) : null;

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
            return Text(row, "address") is not { } address ? new() { ["status"] = "comment" } : ResolveAddress(address);
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

    private static readonly System.Text.RegularExpressions.Regex Absolute = new(@"^%([IQM])(?:(\d{1,6})\.([0-7])|([BWD])(\d{1,6}))$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>An absolute address (%I0.0, %QB4, %MW10, %ID8) as a process-image area read. Peripheral (:P) and other areas stay unresolved:
    /// sim_area_read reaches the input, output and marker images only. Offsets beyond the instance's area size fail per row at read time.</summary>
    public static JsonObject ResolveAddress(string address)
    {
        var match = Absolute.Match(address.Trim());
        if (!match.Success) return new() { ["status"] = "unresolved", ["reason"] = address.TrimEnd().EndsWith(":P", StringComparison.OrdinalIgnoreCase) ? "peripheralAddress" : "unsupportedAddress" };
        var g = match.Groups;
        string area = char.ToUpperInvariant(g[1].Value[0]) switch { 'I' => "input", 'Q' => "output", _ => "marker" };
        var result = new JsonObject { ["status"] = "resolved", ["area"] = area };
        if (g[2].Success) { result["offset"] = int.Parse(g[2].Value, CultureInfo.InvariantCulture); result["bit"] = int.Parse(g[3].Value, CultureInfo.InvariantCulture); result["length"] = 1; }
        else { result["offset"] = int.Parse(g[5].Value, CultureInfo.InvariantCulture); result["length"] = char.ToUpperInvariant(g[4].Value[0]) switch { 'B' => 1, 'W' => 2, _ => 4 }; }
        return result;
    }

    // One sim_area_read per row and sample; a refused or failed read marks only that row.
    private static async Task<JsonNode?> AreaValue(IBridgeClient simulator, int instanceId, string instanceName, (int Ordinal, string Address, JsonObject Access) row)
    {
        var access = row.Access;
        var value = new JsonObject { ["ordinal"] = row.Ordinal, ["address"] = row.Address, ["area"] = access["area"]!.GetValue<string>(), ["offset"] = access["offset"]!.GetValue<int>() };
        try
        {
            var read = await simulator.CallAsync("sim_area_read", new { instanceId, area = access["area"]!.GetValue<string>(), offset = access["offset"]!.GetValue<int>(),
                length = access["bit"] is null ? access["length"]!.GetValue<int>() : (int?)null, bit = access["bit"]?.GetValue<int>() }, CancellationToken.None);
            if (read.GetProperty("instanceId").GetInt32() != instanceId || read.GetProperty("instanceName").GetString() != instanceName)
                throw new BridgeException("PlcsimResponseInvalid", "The simulator worker returned an area read for another instance; nothing is reported.");
            if (access["bit"] is not null) { value["bit"] = access["bit"]!.GetValue<int>(); value["value"] = read.GetProperty("value").GetBoolean(); }
            else
            {
                var bytes = read.GetProperty("bytes").EnumerateArray().Select(b => b.GetInt32()).ToArray();
                if (bytes.Length != access["length"]!.GetValue<int>() || bytes.Any(b => b is < 0 or > 255))
                    throw new BridgeException("PlcsimResponseInvalid", "The simulator worker returned an unexpected number of bytes; nothing is reported.");
                value["bytes"] = new JsonArray(bytes.Select(b => (JsonNode?)b).ToArray());
                value["value"] = bytes.Aggregate(0L, (sum, b) => sum << 8 | (uint)b);
            }
            value["quality"] = "good";
        }
        catch (BridgeException error) when (error.Code != "PlcsimResponseInvalid")
        { value["quality"] = "bad"; value["errorCode"] = error.Code; }
        return value;
    }

    /// <summary>TIA quotes symbol segments ("DB".Member); the PLCSIM tag list need not, so both sides compare without quotes.</summary>
    public static string Key(string symbol) => symbol.Replace("\"", "");

    // The typed reads sim_read offers; Struct, Char, WChar and anything else have none.
    internal static string? Type(string primitive, string dataType) =>
        Scalars.FirstOrDefault(t => t.Equals(primitive, StringComparison.OrdinalIgnoreCase)) ?? dataType switch { "String" => "string", "WString" => "wstring", _ => null };

    private static JsonNode?[] Values(JsonElement read, int instanceId, string instanceName, (string Symbol, string Type)[] signals, List<(int Ordinal, string Symbol, string Type)> resolved)
    {
        var values = read.GetProperty("values");
        if (read.GetProperty("instanceId").GetInt32() != instanceId || read.GetProperty("instanceName").GetString() != instanceName || values.GetArrayLength() != signals.Length ||
            signals.Where((s, i) => values[i].GetProperty("symbol").GetString() != s.Symbol || values[i].GetProperty("type").GetString() != s.Type).Any())
            throw new BridgeException("PlcsimResponseInvalid", "The simulator worker returned values for another instance or other symbols; nothing is reported.");
        return resolved.Select(r =>
        {
            var value = JsonNode.Parse(values[Array.IndexOf(signals, (r.Symbol, r.Type))].GetRawText())!.AsObject();
            value["ordinal"] = r.Ordinal;
            return (JsonNode?)value;
        }).ToArray();
    }

    // The monitor uses only the snapshot's rows; the export folder watch_read leaves for review would otherwise pile up, one per call.
    private static void DropExport(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("receipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object || Text(receipt, "path") is not { } path) return;
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!, parent = Path.GetDirectoryName(folder)!;
        // Only the worker's own export layout: <artifacts>/watch-observations/w_<12 hex>/snapshot.json.
        if (Path.GetFileName(path) != "snapshot.json" || Path.GetFileName(parent) != "watch-observations" ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder), "^w_[0-9a-f]{12}$")) return;
        try { PackageStorage.DeleteOwned(parent, folder); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BridgeException) { } // A leftover export is clutter only.
    }

    private static string? Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim().Length > 0 ? value.GetString() : null;
}
