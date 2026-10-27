using Newtonsoft.Json.Linq;
using TiaMcp.Indexing;

namespace TiaMcp.Tests;

// Authored oracle: inputs mirror hardware_inspect, technology_read and captured unit
// metadata; expected endpoints and resolutions are written by hand, not derived.
internal static class ProjectModelNativeProviderTests
{
    private const string Root = "/projects/Fx", Plc = Root + "/devices/PLC_1/items/PLC_1/plc/PLC_1";
    private const string Subnet = Root + "/subnets/PN%2FIE_1", IoSystem = Subnet + "/ioSystems/IO-System_100";
    private const string PlcIf = Root + "/devices/PLC_1/items/PLC_1/items/PROFINET_1", EtIf = Root + "/devices/ET_1/items/ET_1/items/PN-IO";
    private const string HmiIf = Root + "/devices/HMI_1/items/HMI_1/items/PN", Dq = Root + "/devices/ET_1/items/ET_1/items/DQ_1";
    private const string Axis = Plc + "/technology/Axis_1", UnitA = Plc + "/units/Unit_A", UnitB = Plc + "/units/Unit_B";
    private const string Captured = "2026-10-26T00:00:00Z";

    private static JArray Nodes()
    {
        JObject Node(string id, string? parent, string kind, string name, string? path, string? plc = null, string? scope = null, JObject? metadata = null) => new()
        {
            ["nodeId"] = id, ["parentId"] = parent, ["kind"] = kind, ["name"] = name, ["origin"] = path == null ? "virtual" : "native",
            ["objectPath"] = path, ["plcPath"] = plc, ["scopePath"] = scope, ["access"] = new JObject { ["state"] = path == null ? "virtual" : "readable" },
            ["detailAvailability"] = "available", ["metadata"] = metadata ?? new JObject()
        };
        var nodes = new JArray(
            Node("root", null, "project", "Fx", Root), Node("subnets", "root", "folder", "Subnets", null),
            Node("subnet", "subnets", "subnet", "PN/IE_1", Subnet), Node("iosys", "subnet", "ioSystem", "IO-System_100", IoSystem),
            Node("plcif", "root", "hardware", "PROFINET_1", PlcIf), Node("etif", "root", "hardware", "PN-IO", EtIf),
            Node("hmiif", "root", "hardware", "PN", HmiIf), Node("dq", "root", "hardware", "DQ_1", Dq),
            Node("plc", "root", "plc", "PLC_1", Plc, Plc, Plc), Node("axis", "plc", "technologyObject", "Axis_1", Axis, Plc, Plc),
            Node("units", "plc", "softwareUnitGroup", "Units", Plc + "/units", Plc, Plc),
            Node("unitA", "units", "softwareUnit", "Unit_A", UnitA, Plc, UnitA, new JObject { ["relationsStatus"] = "observed", ["relations"] = new JArray(
                Relation("SoftwareUnit", "Unit_B"), Relation("NonUnitDB", "Shared_DB"), Relation("NonUnitDB", "Unit_B"),
                Relation("SoftwareUnit", "Missing"), Relation("NonUnitDB", "Dup_DB")) }),
            Node("unitB", "units", "softwareUnit", "Unit_B", UnitB, Plc, UnitB),
            Node("sharedDb", "plc", "block", "Shared_DB", Plc + "/blocks/Shared_DB", Plc, Plc, new JObject { ["kind"] = "GlobalDB" }),
            Node("unitDb", "unitB", "block", "Shared_DB", UnitB + "/blocks/Shared_DB", Plc, UnitB, new JObject { ["kind"] = "GlobalDB" }),
            Node("dup1", "plc", "block", "Dup_DB", Plc + "/blocks/Dup_DB", Plc, Plc, new JObject { ["kind"] = "GlobalDB" }),
            Node("dup2", "plc", "block", "Dup_DB", Plc + "/blocks/groups/G/Dup_DB", Plc, Plc, new JObject { ["kind"] = "InstanceDB" }));
        return nodes;
    }

    private static JObject Relation(string type, string name) => new() { ["relationType"] = type, ["relatedObject"] = name };

    private static JObject Resolved(string path) => new() { ["resolution"] = "resolved", ["ownerHardwarePath"] = path };

    private static JObject System(string? path) => new() { ["state"] = "assigned", ["name"] = "IO-System_100", ["number"] = 100, ["path"] = path,
        ["controllerOwner"] = Resolved(PlcIf), ["subnet"] = new JObject { ["state"] = "connected", ["path"] = Subnet, ["resolution"] = "resolved" } };

    private static JObject HardwareInspection() => new()
    {
        ["items"] = new JArray(
            new JObject { ["path"] = PlcIf, ["network"] = new JObject {
                ["nodes"] = new JArray(new JObject { ["name"] = "X1", ["subnet"] = new JObject { ["state"] = "connected", ["path"] = Subnet, ["resolution"] = "resolved" } }),
                ["ioControllers"] = new JArray(new JObject { ["controllerIndex"] = 0, ["ioSystem"] = System(IoSystem), ["assignedConnectors"] = new JArray(Resolved(EtIf)) }),
                ["ioConnectors"] = new JArray() } },
            new JObject { ["path"] = EtIf, ["network"] = new JObject {
                ["nodes"] = new JArray(new JObject { ["name"] = "X1", ["subnet"] = new JObject { ["state"] = "connected", ["path"] = Subnet, ["resolution"] = "resolved" } },
                    new JObject { ["name"] = "X2", ["subnet"] = new JObject { ["state"] = "notConnected" } }),
                ["ioControllers"] = new JArray(), ["ioConnectors"] = new JArray(new JObject { ["connectorIndex"] = 0, ["connectedToIoSystem"] = System(IoSystem) }) } },
            // Adversarial: unresolved internal subnet, and a controller row whose native owner is another interface.
            new JObject { ["path"] = HmiIf, ["network"] = new JObject {
                ["nodes"] = new JArray(new JObject { ["name"] = "X1", ["subnet"] = new JObject { ["state"] = "connected", ["name"] = "PN/IE_1", ["path"] = null, ["resolution"] = "unresolved" } }),
                ["ioControllers"] = new JArray(new JObject { ["controllerIndex"] = 0, ["ioSystem"] = System(IoSystem) }), ["ioConnectors"] = new JArray() } },
            new JObject { ["path"] = Dq, ["networkObservation"] = "notExposed" })
    };

    private static JObject Reference(string kind, string? hardwarePath, string resolution = "resolved") => new()
    {
        ["valueSemantics"] = "engineeringReference", ["resolution"] = resolution, ["kind"] = kind, ["hardwarePath"] = hardwarePath
    };

    private static JObject TechnologyRead()
    {
        var actor = Reference("hardware", Dq); actor["container"] = Reference("hardware", Root + "/devices/ET_1/items/ET_1");
        var channel = Reference("channel", null); channel["parent"] = Reference("hardware", Dq);
        JObject Row(string role, string field, JToken value) => new() { ["role"] = role, ["service"] = role, ["observationPath"] = Axis + "/hardwareConnections/" + role, [field] = value };
        return new JObject { ["hardwareConnections"] = new JArray(
            Row("actor", "attributes", new JArray(new JObject { ["name"] = "Actor", ["value"] = actor })),
            Row("encoder", "channel", channel),
            Row("outputCam", "outputTag", Reference("tag", null)),
            Row("measuringInput", "inputModule", Reference("hardware", null, "unresolved")),
            Row("torque", "inputModule", Reference("hardware", Root + "/devices/Other/items/X"))) };
    }

    private static string Summary(JArray edges) => string.Join(";", edges.Select(e =>
        $"{e["kind"]}|{(e["source"] as JObject)?["objectPath"]}|{(e["target"] as JObject)?["objectPath"]}|{e["evidence"]!["resolution"]}").OrderBy(x => x, StringComparer.Ordinal));

    private static void Expect(JArray edges, params string[] expected)
        => Check.Equal(string.Join(";", expected.OrderBy(x => x, StringComparer.Ordinal)), Summary(edges));

    internal static Task NetworkTechnologyAndUnitProviders()
    {
        var nodes = Nodes().OfType<JObject>().ToArray();
        var byPath = nodes.Where(n => (string?)n["objectPath"] != null).ToDictionary(n => (string)n["objectPath"]!, StringComparer.Ordinal);
        JObject? Lookup(string path) => byPath.TryGetValue(path, out var node) ? node : null;

        var network = ProjectModelRelationshipProviders.NetworkEdges(HardwareInspection(), Lookup, Captured);
        Expect(network,
            $"connectedSubnet|{PlcIf}|{Subnet}|resolved", $"connectedSubnet|{EtIf}|{Subnet}|resolved", $"connectedSubnet|{HmiIf}||unresolved",
            $"ioSystemController|{PlcIf}|{IoSystem}|resolved", $"ioSystemController|{HmiIf}|{IoSystem}|unresolved",
            // Observed from both the controller and the device connector: one edge identity.
            $"ioSystemDevice|{EtIf}|{IoSystem}|resolved");
        Check.Equal(true, network.All(e => (string?)e["evidence"]!["contentRevision"]!["scope"] == "configuredNetwork" && e["evidence"]!["capturedAtUtc"]!.ToString() == Captured));
        Check.Equal(true, network.All(e => (e["evidence"]!.Value<string>("resolution") == "resolved") == (e["unresolved"]!.Type == JTokenType.Null)));

        var technology = ProjectModelRelationshipProviders.TechnologyEdges(TechnologyRead(), byPath[Axis], Lookup, Captured);
        Expect(technology, $"technologyConnection|{Axis}|{Dq}|resolved", $"technologyConnection|{Axis}|{Dq}|resolved",
            $"technologyConnection|{Axis}||unresolved", $"technologyConnection|{Axis}||unresolved", $"technologyConnection|{Axis}||unresolved");
        Check.Equal(5, technology.Select(e => (string)e["edgeId"]!).Distinct().Count());

        var units = ProjectModelRelationshipProviders.UnitRelationEdges(byPath[UnitA], nodes, true, Captured);
        Expect(units, $"unitRelation|{UnitA}|{UnitB}|resolved", $"unitRelation|{UnitA}|{Plc}/blocks/Shared_DB|resolved",
            $"unitRelation|{UnitA}||unresolved", $"unitRelation|{UnitA}||unresolved", $"unitRelation|{UnitA}||unresolved");
        Check.Equal(true, ProjectModelRelationshipProviders.UnitRelationEdges(byPath[UnitA], nodes, false, Captured)
            .All(e => e["target"]!.Type == JTokenType.Null && e["unresolved"]!["reason"]!.ToString().Contains("partial")));
        Check.Equal(0, ProjectModelRelationshipProviders.UnitRelationEdges(byPath[UnitB], nodes, true, Captured).Count);

        // The real store must accept every provider edge and traverse it from the network side.
        var edges = new JArray(network.Concat(technology).Concat(units).Select(e => e.DeepClone()));
        var document = new JObject { ["sessionGeneration"] = "native-providers", ["project"] = new JObject { ["name"] = "Fx", ["projectPath"] = "D:/fixture/Fx.ap21", ["host"] = "fixture" },
            ["nodes"] = new JArray(nodes.Select(n => n.DeepClone())), ["edges"] = edges, ["coverage"] = new JArray() };
        var store = new ProjectModelStore(); string id = (string)store.Capture(document)["snapshotId"]!;
        var incoming = store.Relationships(id, "subnet", new[] { "connectedSubnet" }, "in", 1, 100, 100);
        Check.Equal(2, incoming["edges"]!.Count());
        var system = store.Relationships(id, "iosys", null!, "in", 1, 100, 100);
        Check.Equal(3, system["edges"]!.Count());
        var ioDevice = store.Relationships(id, "etif", new[] { "ioSystemDevice" }, "out", 2, 100, 100);
        Check.Equal(IoSystem, (string)ioDevice["edges"]![0]!["target"]!["objectPath"]!);
        return Task.CompletedTask;
    }

    internal static Task EveryCapturedKindHasDeclaredScope()
    {
        string[] provided = { "hardware", "plc", "softwareUnitGroup", "softwareUnit", "safetyUnit", "blockGroup", "systemBlockGroup", "block", "typeGroup",
            "systemTypeGroup", "type", "tagGroup", "tagTable", "tag", "constant", "systemConstant", "technologyGroup", "technologyObject", "hmi",
            "subnet", "ioSystem", "watchTableGroup", "watchTable", "forceTable", "externalSourceGroup", "externalSource" };
        string[] unsupported = { "project", "folder", "deviceGroup", "device", "namespace", "unsupportedSoftware", "alarmTextList", "notAKnownKind" };
        string[] schemaKinds = { "contains", "calls", "instanceOf", "reads", "writes", "usesType", "dependsOn", "configuredIo", "connectedSubnet", "ioSystemController",
            "ioSystemDevice", "technologyConnection", "unitRelation", "hmiBinding", "screenNavigation", "libraryVersion" };
        foreach (string kind in provided)
        {
            var scope = ProjectModelRelationshipProviders.Scope(kind);
            Check.Equal("provided", (string?)scope["status"]);
            Check.Equal(true, scope["providers"]!.Any() && scope["edgeKinds"]!.All(k => schemaKinds.Contains((string)k!)));
            Check.Equal("notEstablished", (string?)scope["projectCompleteness"]);
        }
        foreach (string kind in unsupported)
        {
            var scope = ProjectModelRelationshipProviders.Scope(kind);
            Check.Equal("unsupported", (string?)scope["status"]);
            Check.Equal(true, !scope["providers"]!.Any() && !string.IsNullOrEmpty((string?)scope["reason"]));
        }
        Check.Equal(true, ProjectModelRelationshipProviders.Scope("hardware")["edgeKinds"]!.Values<string>().Contains("ioSystemDevice"));
        Check.Equal(true, ProjectModelRelationshipProviders.Scope("technologyObject")["providers"]!.Values<string>().Contains("technologyConnections"));
        Check.Equal(true, ProjectModelRelationshipProviders.Scope("safetyUnit")["edgeKinds"]!.Values<string>().Contains("unitRelation"));
        return Task.CompletedTask;
    }
}
