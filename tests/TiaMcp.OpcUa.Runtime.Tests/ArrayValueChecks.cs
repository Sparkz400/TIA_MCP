using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.OpcUa.Runtime;

namespace TiaMcp.OpcUa.Runtime.Tests;

internal static class ArrayValueChecks
{
    private static readonly Dictionary<string, string> Expected = new() {
        ["array"] = """{"dimensions":[3],"elementEncoding":"decimalInteger","elements":["1","2","3"]}""",
        ["byteArray"] = """{"dimensions":[3],"elementEncoding":"decimalInteger","elements":["1","2","3"]}""",
        ["uint64Array"] = """{"dimensions":[3],"elementEncoding":"decimalInteger","elements":["0","9007199254740993","18446744073709551615"]}""",
        ["int64Matrix"] = """{"dimensions":[2,3],"elementEncoding":"decimalInteger","elements":["-9223372036854775808","-1","0","9223372036854775807","2","3"]}""",
        ["boolArray"] = """{"dimensions":[2],"elementEncoding":"boolean","elements":[true,false]}""",
        ["stringArray"] = """{"dimensions":[4],"elementEncoding":"string","elements":["Größe <tag>",null,"","\"\n"]}""",
        ["byteStrings"] = """{"dimensions":[3],"elementEncoding":"base64","elements":["AP8=",null,""]}""",
        ["emptyArray"] = """{"dimensions":[0],"elementEncoding":"decimalInteger","elements":[]}""",
        ["emptyMatrix"] = """{"dimensions":[0,3],"elementEncoding":"decimalInteger","elements":[]}""",
        ["floatArray"] = """{"dimensions":[2],"elementEncoding":"roundTripFloat","elements":["0.125","-0"]}""",
        ["doubleMatrix"] = """{"dimensions":[2,2],"elementEncoding":"roundTripDouble","elements":["0.125","-2.5","3","4"]}""",
        ["dateArray"] = """{"dimensions":[1],"elementEncoding":"utcDateTime","elements":["2026-10-19T12:34:56.0000000Z"]}""",
        ["guidArray"] = """{"dimensions":[1],"elementEncoding":"guid","elements":["01234567-89ab-cdef-0123-456789abcdef"]}""",
        ["uncertainArray"] = """{"dimensions":[1],"elementEncoding":"decimalInteger","elements":["42"]}"""
    };
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
    private static string Id() => Guid.NewGuid().ToString("N");
    internal static async Task Run(FixtureServer server, string output, Action<bool, string> check)
    {
        string root = Path.Combine(output, "array-checks"), sessionRoot = Path.Combine(root, "sessions");
        string pollRef, subscriptionRef;
        JsonElement pollRows, subscriptionRows;
        string[] names = [..Expected.Keys, "badArray", "nonfiniteArray", "limitArray", "byteString", "structure", "nullArray", "expandedArray"];
        var nodes = names.Select(FixtureServer.Node).ToArray();
        await using (var sessions = new OpcUaRuntimeSessionStore(sessionRoot))
        await using (var captures = new OpcUaRuntimeCaptures(sessions, root))
        await using (var subscriptions = new OpcUaRuntimeSubscriptions(sessions, root))
        {
            string attempt = Id();
            check((await sessions.Connect(attempt, server.Selection)).State == "connected", "Arrays use encrypted loopback session");
            var read = await sessions.Read(attempt, nodes);
            File.WriteAllText(Path.Combine(root, "read.json"), Json(read).GetRawText());
            Verify(read.Items, "read", check);
            server.Mode = "arrayShapeMismatch";
            try
            {
                var mismatch = await sessions.Read(attempt, [FixtureServer.Node("int64Matrix"), FixtureServer.Node("uint64Array")]);
                check(mismatch.Items[0].Outcome == "unsupported" && mismatch.Items[0].Value is null &&
                    mismatch.Items[0].ErrorCode == "UaArrayShapeMismatch" && mismatch.Items[1].Outcome == "value", "Wire shape mismatch refuses only its original item");
            }
            finally { server.Mode = "normal"; }
            string capture = Id();
            JsonElement poll;
            // Exercise the gap rows a slower packaged runner can legitimately retain.
            server.ReadDelayMs = 25;
            try
            {
                await captures.Start(capture, new(attempt, nodes, 100, 10, MaximumSamples: names.Length), default);
                poll = await End(() => captures.Status(capture, default));
            }
            finally { server.ReadDelayMs = 0; }
            pollRef = poll.GetProperty("snapshotRef").GetString()!;
            pollRows = Json(captures.Page(pollRef, 0, 200)).GetProperty("rows").Clone();
            var reads = pollRows.EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "read").ToArray();
            check(reads.Length == names.Length && reads.All(row => row.GetProperty("readResultJson").ValueKind == JsonValueKind.String),
                "Array polling retains every successful read separately from gaps");
            check(pollRows.EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "gap"), "Delayed array polling retains explicit skipped-slot gaps");
            Verify(reads.Select(row =>
            {
                using var result = JsonDocument.Parse(row.GetProperty("readResultJson").GetString()!);
                return result.RootElement.GetProperty("items")[0].Deserialize<OpcUaRuntimeValue>(BridgeProtocol.Json)!;
            }), "poll", check);
            var pollExport = Json(await captures.Export(pollRef, "csv", default));
            var exported = VerifyExport(pollExport, pollRows, "poll", check);
            var plan = exported.GetProperty("capture").GetProperty("plan").Deserialize<RuntimeCapturePlan>(BridgeProtocol.Json)!;
            var original = pollRows.EnumerateArray().Select(r => r.Deserialize<RuntimeCaptureRow>(BridgeProtocol.Json)!)
                .First(r => r.SelectionId == OpcUaCaptureContracts.SelectionId(FixtureServer.Node("int64Matrix")));
            RetainedControls(plan, original, check);
            string subscription = Id();
            await subscriptions.Start(subscription, new(attempt, nodes, DurationSeconds: 10, MaximumNotifications: names.Length), default);
            var status = await End(() => subscriptions.Status(subscription, default));
            check(status.GetProperty("recordedProgress").GetProperty("remoteCleanupConfirmed").GetBoolean(), "Array subscription cleanup confirmed");
            subscriptionRef = status.GetProperty("snapshotRef").GetString()!;
            subscriptionRows = Json(subscriptions.Page(subscriptionRef, 0, 200)).GetProperty("rows").Clone();
            var events = subscriptionRows.EnumerateArray().Select(r => r.GetProperty("event").Deserialize<OpcUaSubscriptionEvent>(BridgeProtocol.Json)!).ToArray();
            Verify(events.SelectMany(e => e.Notifications ?? []).Select(n => n.Value), "subscription", check);
            VerifyExport(Json(await subscriptions.Export(subscriptionRef, "csv", default)), subscriptionRows, "subscription", check);
            check((await sessions.Disconnect(attempt)).State == "closed", "Array session closes after acquisitions");
        }
        int audit = server.Audit.Count;
        await using (var sessions = new OpcUaRuntimeSessionStore(sessionRoot))
        await using (var captures = new OpcUaRuntimeCaptures(sessions, root))
        await using (var subscriptions = new OpcUaRuntimeSubscriptions(sessions, root))
        {
            check(JsonElement.DeepEquals(pollRows, Json(captures.Page(pollRef, 0, 200)).GetProperty("rows")), "Array polling evidence reopens unchanged");
            check(JsonElement.DeepEquals(subscriptionRows, Json(subscriptions.Page(subscriptionRef, 0, 200)).GetProperty("rows")), "Array subscription evidence reopens unchanged");
            VerifyExport(Json(await captures.Export(pollRef, "json", default)), pollRows, "reopened poll", check);
            VerifyExport(Json(await subscriptions.Export(subscriptionRef, "json", default)), subscriptionRows, "reopened subscription", check);
        }
        check(server.Audit.Count == audit, "Array evidence reopen and export perform no remote services");
    }
    private static void Verify(IEnumerable<OpcUaRuntimeValue> source, string route, Action<bool, string> check)
    {
        var values = source.ToDictionary(v => v.Node.Identifier);
        foreach (var (name, expected) in Expected)
        {
            var value = values[name];
            check(value.Outcome == "value" && value.Encoding == "array" && JsonElement.DeepEquals(Json(value.Value), JsonDocument.Parse(expected).RootElement), route + " exact " + name);
        }
        check(values["uint64Array"].BuiltInType == "UInt64" && values["int64Matrix"].ValueRank == 2 &&
            values["int64Matrix"].ArrayDimensions!.SequenceEqual([2u, 3u]), route + " exact array type and declared matrix shape");
        check(values["badArray"].Outcome == "itemError" && values["badArray"].Value is null && values["badArray"].Quality == "Bad", route + " bad array suppressed");
        check(values["uncertainArray"].Quality == "Uncertain", route + " uncertain array not promoted");
        check(values["nonfiniteArray"].Outcome == "unsupported" && values["nonfiniteArray"].Value is null, route + " unsupported element refuses whole array");
        check(values["expandedArray"].ErrorCode == "UaArrayValueLimit" && values["expandedArray"].Value is null, route + " JSON expansion bound refuses whole array");
        check(values["structure"].Outcome == "unsupported" && values["byteString"].Encoding == "base64", route + " structures refused and scalar ByteString unchanged");
        check(values["nullArray"].Outcome == "value" && values["nullArray"].Encoding == "null" && values["nullArray"].Value is null &&
            values["nullArray"].ValueRank == 1 && values["nullArray"].BuiltInType is null, route + " null Variant preserves declared array rank without inventing decoded type or shape");
        var elements = Json(values["limitArray"].Value).GetProperty("elements");
        check(elements.GetArrayLength() == 4096 && elements.EnumerateArray().Select(e => e.GetString()).SequenceEqual(Enumerable.Range(0, 4096).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))), route + " all 4096 elements retained");
    }
    private static JsonElement VerifyExport(JsonElement export, JsonElement rows, string route, Action<bool, string> check)
    {
        foreach (var artifact in export.GetProperty("artifacts").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(artifact.GetProperty("path").GetString()!);
            check(Convert.ToHexStringLower(SHA256.HashData(bytes)) == artifact.GetProperty("sha256").GetString(), route + " export integrity");
        }
        var document = JsonDocument.Parse(File.ReadAllBytes(export.GetProperty("artifacts")[0].GetProperty("path").GetString()!)).RootElement.Clone();
        check(JsonElement.DeepEquals(rows, document.GetProperty("rows")), route + " exact original export values");
        return document;
    }
    private static void RetainedControls(RuntimeCapturePlan plan, RuntimeCaptureRow row, Action<bool, string> check)
    {
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
            ("wrong count", v => v["value"]!["dimensions"] = new JsonArray(2, 4)),
            ("wrong rank", v => v["valueRank"] = 1),
            ("declared maximum exceeded", v => v["arrayDimensions"] = new JsonArray(1, 3)),
            ("negative dimension", v => v["value"]!["dimensions"] = new JsonArray(-2, 3)),
            ("oversized shape", v => v["value"]!["dimensions"] = new JsonArray(4096, 4096)),
            ("excessive rank", v => v["value"]!["dimensions"] = new JsonArray(Enumerable.Range(0, 33).Select(_ => (JsonNode?)JsonValue.Create(1)).ToArray())),
            ("null elements with known shape", v => v["value"]!["elements"] = null),
            ("null integer element", v => v["value"]!["elements"]![0] = null),
            ("integer overflow", v => v["value"]!["elements"]![0] = "-9223372036854775809"),
            ("integer rounded to number", v => v["value"]!["elements"]![0] = 9007199254740992d),
            ("wrong element encoding", v => v["value"]!["elementEncoding"] = "string"),
            ("extra payload", v => v["value"]!["unexpected"] = true),
            ("nested element", v => v["value"]!["elements"]![0] = new JsonArray(1)),
            ("bad quality exposed", v => { v["statusCode"] = "0x808c0000"; v["quality"] = "Bad"; }) })
        {
            var packet = JsonNode.Parse(row.ReadResultJson!)!;
            mutate(packet["items"]![0]!.AsObject());
            bool refused = false;
            try { OpcUaCaptureContracts.ValidateRead(plan, row with { ReadResultJson = packet.ToJsonString() }); }
            catch (BridgeException e) when (e.Code == "UaCaptureInvalid") { refused = true; }
            check(refused, "Retained array refuses " + name);
        }
        foreach (int rank in new[] { -2, 0, 2 })
        {
            var packet = JsonNode.Parse(row.ReadResultJson!)!;
            packet["items"]![0]!["valueRank"] = rank;
            packet["items"]![0]!["arrayDimensions"] = new JsonArray(0, 8);
            OpcUaCaptureContracts.ValidateRead(plan, row with { ReadResultJson = packet.ToJsonString() });
            check(true, "Array accepts declared unknown/max bounds and rank " + rank);
        }
        check(OpcUaCaptureContracts.ArrayShapeMatches(2, [0, 3], [0, 3], 0), "Empty matrix shape is explicit");
        check(!OpcUaCaptureContracts.ArrayShapeMatches(1, null, [4097], 4097), "Array element limit is enforced");
    }
    private static async Task<JsonElement> End(Func<Task<object>> status)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(25))
        {
            var result = Json(await status());
            if (result.GetProperty("state").GetString() != "running") return result;
            await Task.Delay(30);
        }
        throw new Exception("Array acquisition did not terminate");
    }
}
