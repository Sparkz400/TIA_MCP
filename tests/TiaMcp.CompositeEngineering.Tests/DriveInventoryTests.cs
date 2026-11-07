using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

/// <summary>#213 drive inventory joins against fake worker responses; no TIA Portal.</summary>
internal static class DriveInventoryTests
{
    private sealed class FakeWorker(Func<string, JsonObject, JsonObject> answer) : IBridgeClient
    {
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(answer(operation, JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject()), BridgeProtocol.Json));
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject Drive(string path, string name, int input, int output) => Obj($$$"""
        {"hardwarePath":"{{{path}}}","name":"{{{name}}}","typeIdentifier":"OrderNumber:G120C","driveObjects":[{"driveObjectNumber":null,"driveObjectIndex":0,"snapshot":{
        "identity":{"driveId":"DO@0"},"telegramsComplete":true,"telegrams":[{"id":"Standard telegram 1","type":"Main","number":1,"inputBytes":4,"outputBytes":4,"addressesComplete":true,
        "addresses":[{"direction":"Input","startAddress":{{{input}}},"length":32},{"direction":"Output","startAddress":{{{output}}},"length":32}]}]}}]}
        """);

    private static JsonObject Tag(string name, string address) => new() { ["name"] = name, ["dataType"] = "Word", ["logicalAddress"] = address, ["isSafety"] = false };

    private static JsonObject Run(JsonArray drives, JsonObject? driveCompleteness = null, string? driveError = null, JsonArray? otherPlcTags = null, bool twoLinks = false)
    {
        var worker = new FakeWorker((operation, args) => operation switch
        {
            "drive_observe" when driveError is not null => throw new BridgeOperationException(driveError, "Startdrive is not installed."),
            "drive_observe" => new JsonObject { ["project"] = new JsonObject { ["name"] = "P" }, ["drives"] = drives.DeepClone(),
                ["completeness"] = driveCompleteness ?? Obj("""{"complete":false,"scopeComplete":true,"truncated":false}"""), ["warnings"] = new JsonArray() },
            "project_inventory" => Obj("""{"project":{"name":"P"},"plcs":[{"path":"/p/PLC1"},{"path":"/p/PLC2"}]}"""),
            "software_inspect" => new JsonObject { ["tagTables"] = new JsonArray(new JsonObject { ["path"] = (string)args["plcPath"]! + "/tags/T",
                ["tags"] = (string)args["plcPath"]! == "/p/PLC1" ? new JsonArray(Tag("D1_ZSW", "%IW256"), Tag("D1_STW", "%QW256"), Tag("Other", "%M0.0")) : otherPlcTags?.DeepClone() ?? new JsonArray() }),
                ["completeness"] = new JsonObject { ["truncated"] = false } },
            "technology_read" when args["technologyPath"] is null => (string)args["plcPath"]! == "/p/PLC1"
                ? Obj("""{"technologyObjects":[{"path":"/p/PLC1/technology/Axis1","name":"Axis1","systemLibraryType":"TO_SpeedAxis"}]}""") : Obj("""{"technologyObjects":[]}"""),
            "technology_read" => Obj($$$"""
                {"technologyObject":{"path":"/p/PLC1/technology/Axis1"},"hardwareConnectionCoverage":{"scopeComplete":true},"hardwareConnections":[{"role":"actor","status":"observed","isConnected":true,
                "attributes":[{"name":"Actor","value":{"valueSemantics":"engineeringReference","resolution":"resolved","kind":"hardware","hardwarePath":"{{{(twoLinks ? "/p/devices/Drives" : "/p/devices/Drives/items/D1")}}}"}}]}]}
                """),
            _ => throw new InvalidOperationException(operation)
        });
        return DriveInventory.Capture(worker, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static void Run(Action<bool, string> check)
    {
        var two = new JsonArray(Drive("/p/devices/Drives/items/D1", "Drive", 256, 256), Drive("/p/devices/Drives/items/D2", "Drive", 260, 260));
        var result = Run(two);
        var d1 = result["drives"]![0]!["driveObjects"]![0]!["telegrams"]![0]!["addresses"]!;
        check((string?)d1[0]!["tags"]![0]!["name"] == "D1_ZSW" && (string?)d1[1]!["tags"]![0]!["name"] == "D1_STW", "Drive inventory joins tags to telegram ranges by address");
        check((string?)result["drives"]![0]!["technologyObjects"]![0]!["path"] == "/p/PLC1/technology/Axis1", "Drive inventory links a technology object by hardware path");
        check(result["reconciliation"]!["telegramRangesWithoutTags"]!.AsArray().Count == 2 && result["reconciliation"]!["drivesWithoutTechnologyObject"]![0]!.ToString() == "/p/devices/Drives/items/D2",
            "Drive inventory lists the unmapped second drive");
        check(result["reconciliation"]!["duplicateDriveNames"]![0]!.ToString() == "Drive" && result["writeReady"]!.GetValue<bool>(), "Duplicate display names are reported without blocking");

        var none = Run(new JsonArray());
        check(none["noDrivesConfirmed"]!.GetValue<bool>(), "A complete empty read confirms no drives");
        var unavailable = Run(new JsonArray(), driveError: "UnsupportedCapability");
        check(!unavailable["noDrivesConfirmed"]!.GetValue<bool>() && (string?)unavailable["coverage"]!["drives"]!["status"] == "unavailable" && !unavailable["writeReady"]!.GetValue<bool>(),
            "Unavailable Startdrive never yields no drives");
        var truncated = Run(new JsonArray(), Obj("""{"scopeComplete":true,"truncated":true}"""));
        check(!truncated["noDrivesConfirmed"]!.GetValue<bool>() && (string?)truncated["blockers"]![0]!["kind"] == "driveCoverage", "A truncated read never yields no drives");

        var indexed = Run(new JsonArray(), Obj("""{"scopeComplete":false,"truncated":false}"""));
        check(!indexed["noDrivesConfirmed"]!.GetValue<bool>(), "A scope-incomplete read with no number warning stays partial");

        var ambiguous = Run(two, otherPlcTags: new JsonArray(Tag("Shadow", "%IW256")));
        check(ambiguous["blockers"]!.AsArray().Any(b => (string?)b!["kind"] == "ambiguousTagOwnership") && !ambiguous["writeReady"]!.GetValue<bool>(), "Tags of two PLCs on one telegram block writes");
        var parent = Run(two, twoLinks: true);
        check(parent["blockers"]!.AsArray().Any(b => (string?)b!["kind"] == "ambiguousTechnologyLink"), "A technology link to the shared device is ambiguous");
    }
}
