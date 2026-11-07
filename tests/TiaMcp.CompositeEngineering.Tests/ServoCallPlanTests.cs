using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

/// <summary>#214 servo call planning against fake worker responses; no TIA Portal.</summary>
internal static class ServoCallPlanTests
{
    private sealed class FakeWorker(Func<string, JsonObject, JsonObject> answer) : IBridgeClient
    {
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(answer(operation, arguments is null ? new JsonObject() : JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject()), BridgeProtocol.Json));
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private const string Wrapper = """
        <Document><SW.Blocks.FB><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
        <Section Name="Input"><Member Name="zsw" Datatype="Word" /><Member Name="reset" Datatype="Bool" /><Member Name="ss1" Datatype="Bool" /></Section>
        <Section Name="Output"><Member Name="stw" Datatype="Word" /><Member Name="fault" Datatype="Bool" /></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>
        """;

    private static JsonObject Run(string writer = "Servos", string statusType = "Word", JsonObject? parameters = null)
    {
        var worker = new FakeWorker((operation, args) => operation switch
        {
            "drive_observe" => Obj("""{"project":{"name":"P"},"completeness":{"scopeComplete":true,"truncated":false},"warnings":[],"drives":[{"hardwarePath":"/p/D1","name":"D1","driveObjects":[{"driveObjectIndex":0,"snapshot":{"identity":{"driveId":"DO@0"},"telegramsComplete":true,"telegrams":[{"id":"Main","type":"MainTelegram","number":1,"addressesComplete":true,"addresses":[{"direction":"Input","startAddress":256,"length":32},{"direction":"Output","startAddress":256,"length":32}]}]}}]}]}"""),
            "project_inventory" => Obj("""{"plcs":[{"path":"/p/PLC"}]}"""),
            "software_inspect" => new JsonObject { ["completeness"] = new JsonObject { ["truncated"] = false }, ["tagTables"] = new JsonArray(new JsonObject { ["path"] = "/p/PLC/tags/T",
                ["tags"] = new JsonArray(new JsonObject { ["name"] = "D1_ZSW", ["dataType"] = statusType, ["logicalAddress"] = "%IW256" }, new JsonObject { ["name"] = "D1_STW", ["dataType"] = "Word", ["logicalAddress"] = "%QW256" }) }) },
            "technology_read" => Obj("""{"technologyObjects":[]}"""),
            "block_read" => new JsonObject { ["xml"] = Wrapper },
            "symbol_trace" => new JsonObject { ["writers"] = new JsonArray(new JsonObject { ["actor"] = new JsonObject { ["name"] = writer } }), ["completeness"] = new JsonObject { ["scopeComplete"] = true } },
            _ => throw new InvalidOperationException(operation)
        });
        var request = new JsonObject { ["plcPath"] = "/p/PLC", ["callerName"] = "Servos", ["wrapperName"] = "ServoWrap",
            ["parameters"] = parameters ?? Obj("""{"status":"zsw","control":"stw","reset":"reset","ss1":"ss1","fault":"fault"}"""),
            ["resetExpression"] = "\"Reset\" AND NOT \"DoorOpen\"", ["ss1Expression"] = "\"SS1\"", ["drives"] = new JsonArray(Obj("""{"hardwarePath":"/p/D1","instanceName":"Axis1"}""")) };
        return ServoCallPlan.Plan(worker, request, "C:/exports", CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static void Run(Action<bool, string> check)
    {
        var ready = Run();
        string source = (string?)ready["source"] ?? "";
        check(ready["writeReady"]!.GetValue<bool>() && source.Contains("Axis1 : \"ServoWrap\";") && source.Contains("zsw := \"D1_ZSW\"") && source.Contains("stw => \"D1_STW\"")
            && source.Contains("fault => #Axis1_Fault") && source.Contains("reset := \"Reset\" AND NOT \"DoorOpen\""), "Servo plan wires one instance, the drive's tags, the confirmed reset and one fault Bool");
        check((string?)ready["change"]!["kind"] == "source_upsert", "A ready servo plan carries a source_upsert payload");
        var duplicate = Run(writer: "OldServos");
        check(!duplicate["writeReady"]!.GetValue<bool>() && duplicate["change"] is null && duplicate["blockers"]!.AsArray().Any(b => (string?)b!["kind"] == "duplicateControlWriter"),
            "Another writer of a drive's control tag blocks the plan");
        var mismatch = Run(statusType: "DWord");
        check(mismatch["blockers"]!.AsArray().Any(b => (string?)b!["kind"] == "typeMismatch"), "A status tag of another type blocks the plan");
        var guessed = Run(parameters: Obj("""{"status":"ZSW1","control":"stw","reset":"reset","ss1":"ss1","fault":"fault"}"""));
        check(guessed["blockers"]!.AsArray().Any(b => (string?)b!["kind"] == "wrapperParameter"), "A parameter the wrapper lacks blocks the plan instead of being guessed");
    }
}
