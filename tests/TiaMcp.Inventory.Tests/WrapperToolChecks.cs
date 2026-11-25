using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using TiaMcp.Core;
using TiaMcp.Host;

/// <summary>review_list (kind composite_engineering) and drive_inventory wrappers: defaults, bounds and what reaches the bridge.</summary>
internal static class WrapperToolChecks
{
    private static string? Code(CallToolResult result) =>
        result.StructuredContent is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    internal static async Task Run(Action<bool, string> check, string artifacts)
    {
        var options = new HostOptions("", artifacts, null, 60, false, "", null);

        // review_list kind composite_engineering (formerly composite_engineering_list) is an offline store read: it never calls the worker.
        var reject = new RejectBridge();
        var review = new ReviewRecordTools(reject, options);
        var empty = await review.List("composite_engineering");
        check(empty.IsError != true && empty.StructuredContent!.Value.GetProperty("rows").GetArrayLength() == 0 && empty.StructuredContent.Value.GetProperty("total").GetInt32() == 0 &&
            empty.StructuredContent.Value.GetProperty("nextOffset").ValueKind == JsonValueKind.Null && !empty.StructuredContent.Value.GetProperty("nativeQualified").GetBoolean(),
            "review_list composite_engineering of an empty store returns no rows, no next offset and no native qualification");
        check((await review.List("composite_engineering", 0, 1)).IsError != true && (await review.List("composite_engineering", 0, 100)).IsError != true,
            "review_list composite_engineering accepts 1 and 100 records");
        foreach (var (offset, count) in new[] { (0, 0), (0, 101), (0, -1), (-1, 20), (1, 20), (int.MaxValue, 1) })
            check(Code(await review.List("composite_engineering", offset, count)) == "CompositePageLimit", $"review_list composite_engineering refuses offset {offset}, count {count}");
        check(Code(await review.List("composite_engineering", recordType: "review")) == "InvalidRequest", "review_list composite_engineering refuses a recordType");
        check(reject.Calls == 0, "review_list composite_engineering never calls the worker");

        // drive_inventory: the project selection and the fixed read bounds reach the worker; a worker refusal is a coded error.
        var calls = new List<(string Operation, JsonObject Arguments)>();
        var bridge = new FakeBridge((operation, arguments) =>
        {
            calls.Add((operation, (JsonObject)((JsonObject)arguments!).DeepClone()));
            if (operation == "project_inventory") throw new BridgeOperationException("NoProjectOpen", "No project is open.");
            return JsonDocument.Parse("""{"drives":[],"completeness":{"scopeComplete":true,"truncated":false},"warnings":[]}""").RootElement;
        });
        var tools = new TiaTools(bridge, options);
        var refused = await tools.DriveInventoryRead(CancellationToken.None, "Project1");
        check(refused.IsError == true && Code(refused) == "NoProjectOpen", "drive_inventory returns a worker refusal as a coded error");
        check(calls.Count == 2 && calls[0].Operation == "drive_observe" && calls[0].Arguments["projectName"]!.GetValue<string>() == "Project1" && calls[0].Arguments["maxItems"]!.GetValue<int>() == 2000 &&
            calls[0].Arguments["parameterNumbers"]!.ToJsonString() == "[1]" && calls[0].Arguments.Count == 3, "drive_inventory reads the drives of the named project with its fixed bounds");
        check(calls[1].Operation == "project_inventory" && calls[1].Arguments["projectName"]!.GetValue<string>() == "Project1" && calls[1].Arguments["maxItems"]!.GetValue<int>() == 2000,
            "drive_inventory reads the inventory of the same project");
        calls.Clear();
        await tools.DriveInventoryRead(CancellationToken.None);
        check(calls.Count == 2 && calls.All(call => call.Arguments.ContainsKey("projectName") && call.Arguments["projectName"] is null), "drive_inventory leaves the project unset by default");
    }
}
