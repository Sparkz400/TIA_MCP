using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.WatchTables;

/// <summary>watch_table_monitor against fake engineering and simulator workers: resolution, sampling, refusals and that nothing but reads is sent.</summary>
internal static class MonitorChecks
{
    internal static (string, Action)[] Checks => [
        ("Monitor resolves rows against the target tag list and samples only reads", Resolution),
        ("Monitor bounds and ownership refuse before any table read", Refusals),
        ("Monitor refuses a sim_read answer for other symbols", ForeignAnswer),
    ];

    private static readonly JsonObject[] Rows = [
        Row("PlcWatchTableEntry", "\"Gate_DB\".Active", null),
        Row("PlcTableCommentEntry", null, null),
        Row("PlcWatchTableEntry", null, "%MW10"),
        Row("PlcWatchTableEntry", "\"Missing\"", null),
        Row("PlcWatchTableEntry", "\"Gate_DB\".Pair", null),
        Row("PlcWatchTableEntry", "\"Gate_DB\".Text", null),
        Row("PlcWatchTableEntry", "\"Gate_DB\".Speed", null),
        Row("PlcWatchTableEntry", "\"Gate_DB\".Active", null),
        Row("PlcWatchTableEntry", "\"Gate_DB\".Twice", null),
    ];
    private static readonly JsonObject[] Tags = [
        Tag("Gate_DB.Active", "Bool", "Bool"), Tag("Gate_DB.Pair", "Struct", "Struct"), Tag("Gate_DB.Text", "String", "Unspecific"),
        Tag("Gate_DB.Speed", "Int", "Int16"), Tag("Gate_DB.Twice", "Bool", "Bool"), Tag("\"Gate_DB\".Twice", "Bool", "Bool"),
    ];

    private static void Resolution()
    {
        var (engineering, simulator, calls) = Workers();
        var result = Run(engineering, simulator, count: 9, samples: 3);
        var rows = result["rows"]!.AsArray();
        string[] statuses = rows.Select(r => r!["resolution"]!["status"]!.GetValue<string>()).ToArray();
        Equal("resolved,comment,unresolved,unresolved,unsupportedType,resolved,resolved,resolved,unresolved", string.Join(",", statuses));
        Equal("absoluteAddressOnly", rows[2]!["resolution"]!["reason"]!.GetValue<string>());
        Equal("notInTargetTagList", rows[3]!["resolution"]!["reason"]!.GetValue<string>());
        Equal("ambiguousInTargetTagList", rows[8]!["resolution"]!["reason"]!.GetValue<string>());
        Equal("string", rows[5]!["resolution"]!["type"]!.GetValue<string>());
        Equal("int16", rows[6]!["resolution"]!["type"]!.GetValue<string>());
        Equal("Gate_DB.Active", rows[0]!["resolution"]!["symbol"]!.GetValue<string>());
        // Stored configuration travels verbatim; it is never a live value.
        Equal("TRUE", rows[0]!["row"]!["modifyValue"]!.GetValue<string>());
        // One tag-list lookup per distinct filter, refreshed only on the first; three samples of three distinct symbols.
        string[] ops = calls.Select(c => c.Op).ToArray();
        Equal("sim_identity", ops[0]); Equal("watch_read", ops[1]);
        True(ops.All(o => o is "sim_identity" or "watch_read" or "sim_tags" or "sim_read"));
        var lookups = calls.Where(c => c.Op == "sim_tags").ToArray();
        Equal("Active,Missing,Pair,Text,Speed,Twice", string.Join(",", lookups.Select(c => c.Args["filter"]!.GetValue<string>())));
        True(lookups[0].Args["refresh"]!.GetValue<bool>() && lookups.Skip(1).All(c => !c.Args["refresh"]!.GetValue<bool>()));
        var reads = calls.Where(c => c.Op == "sim_read").ToArray();
        Equal(3, reads.Length);
        Equal(3, reads[0].Args["signals"]!.AsArray().Count);
        var sample = result["samples"]!.AsArray()[2]!["values"]!.AsArray();
        Equal("0,5,6,7", string.Join(",", sample.Select(v => v!["ordinal"]!.GetValue<int>())));
        True(sample.Where(v => v!["ordinal"]!.GetValue<int>() is 0 or 7).All(v => v!["value"]!.GetValue<bool>()));
        Equal("bad", sample.Single(v => v!["ordinal"]!.GetValue<int>() == 6)!["quality"]!.GetValue<string>());
        True(!result["forced"]!.GetValue<bool>() && !result["modified"]!.GetValue<bool>() && result["readOnly"]!.GetValue<bool>() && !result["programMatchVerified"]!.GetValue<bool>());
        Equal("Monitored", result["table"]!["name"]!.GetValue<string>());

        (engineering, simulator, calls) = Workers();
        var page = Run(engineering, simulator, offset: 1, count: 2)["page"]!;
        Equal(3, page["nextOffset"]!.GetValue<int>()); Equal(9, page["total"]!.GetValue<int>());
        // A page of rows without symbols reads nothing.
        True(calls.All(c => c.Op is "sim_identity" or "watch_read"));
        (engineering, simulator, _) = Workers();
        True(Run(engineering, simulator, offset: 8, count: 64)["page"]!["nextOffset"] is null);
    }

    private static void Refusals()
    {
        foreach (var (offset, count, samples, interval) in new[] { (-1, 1, 1, 500), (0, 0, 1, 500), (0, 65, 1, 500), (0, 1, 0, 500), (0, 1, 21, 500), (0, 1, 1, 99), (0, 1, 1, 5001) })
        {
            var (engineering, simulator, calls) = Workers();
            Code("InvalidRequest", () => Run(engineering, simulator, offset, count, samples, interval));
            Equal(0, calls.Count);
        }
        var (e, s, log) = Workers(identityError: "SimulationOwnershipUnverified");
        Code("SimulationOwnershipUnverified", () => Run(e, s));
        True(log.All(c => c.Op == "sim_identity"));
    }

    private static void ForeignAnswer()
    {
        var (engineering, simulator, _) = Workers(foreign: true);
        Code("PlcsimResponseInvalid", () => Run(engineering, simulator));
    }

    private static JsonObject Run(IBridgeClient engineering, IBridgeClient simulator, int offset = 0, int count = 32, int samples = 1, int intervalMs = 100) =>
        WatchTableMonitor.Run(engineering, simulator, "Monitored", null, null, "root", false, 7, "owned", new string('a', 32), offset, count, samples, intervalMs, default).GetAwaiter().GetResult();

    private static (IBridgeClient, IBridgeClient, List<(string Op, JsonObject Args)>) Workers(string? identityError = null, bool foreign = false)
    {
        var calls = new List<(string Op, JsonObject Args)>();
        var engineering = new Bridge((op, args) =>
        {
            calls.Add((op, args));
            return new JsonObject { ["target"] = new JsonObject { ["kind"] = "watch", ["group"] = "root", ["name"] = args["tableName"]!.GetValue<string>(), ["semanticSha256"] = new string('b', 64),
                ["isConsistent"] = true, ["rows"] = new JsonArray(Rows.Select(r => (JsonNode?)r.DeepClone()).ToArray()) },
                ["observation"] = new JsonObject { ["reference"] = new JsonObject { ["object"] = new JsonObject { ["kind"] = "watchTable" } } } };
        });
        var simulator = new Bridge((op, args) =>
        {
            calls.Add((op, args));
            if (op == "sim_identity" && identityError is not null) throw new BridgeException(identityError, "Not owned.");
            return op switch
            {
                "sim_identity" => new JsonObject { ["origin"] = "PLCSIM Advanced V8/local API", ["communicationInterface"] = "Softbus" },
                "sim_tags" => new JsonObject { ["tags"] = new JsonArray(Tags.Where(t => t["symbol"]!.GetValue<string>().Contains(args["filter"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    .Select(t => (JsonNode?)t.DeepClone()).ToArray()), ["truncated"] = false },
                "sim_read" => new JsonObject { ["instanceId"] = 7, ["instanceName"] = "owned", ["values"] = new JsonArray(args["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject {
                    ["symbol"] = foreign ? "Other" : s!["symbol"]!.GetValue<string>(), ["type"] = s!["type"]!.GetValue<string>(),
                    ["value"] = s["type"]!.GetValue<string>() switch { "bool" => (JsonNode?)true, "string" => "text", _ => null },
                    ["quality"] = s["type"]!.GetValue<string>() == "int16" ? "bad" : "good" }).ToArray()) },
                _ => throw new BridgeException("UnexpectedOperation", op)
            };
        });
        return (engineering, simulator, calls);
    }

    private static JsonObject Row(string kind, string? name, string? address)
    {
        var row = new JsonObject { ["kind"] = kind };
        if (kind != "PlcTableCommentEntry") { row["name"] = name; row["address"] = address; row["displayFormat"] = "Bool"; row["modifyValue"] = "TRUE"; row["modifyIntention"] = false; }
        return row;
    }
    private static JsonObject Tag(string symbol, string dataType, string primitive) => new() { ["symbol"] = symbol, ["dataType"] = dataType, ["primitiveDataType"] = primitive };

    private sealed class Bridge(Func<string, JsonObject, JsonObject> answer) : IBridgeClient
    {
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
            => Task.FromResult(JsonSerializer.SerializeToElement(answer(operation, JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject()), BridgeProtocol.Json));
    }
    private static void Code(string code, Action action)
    {
        try { action(); } catch (BridgeException e) when (e.Code == code) { return; }
        throw new Exception("Expected refusal " + code);
    }
    private static void True(bool value) { if (!value) throw new Exception("Monitor assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], observed [{actual}]"); }
}
