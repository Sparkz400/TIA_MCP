using Newtonsoft.Json.Linq;
using TiaMcp.Simulation;

namespace TiaMcp.Tests;

internal static class SimulationRuntimeControlTests
{
    private static void Refused(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected an ArgumentException.");
    }

    internal static Task TimeBoundsAndUtc()
    {
        Check.Equal(1.0, SimulationRuntimeControl.ScaleFactor(new JValue(1)));
        Check.Equal(0.5, SimulationRuntimeControl.ScaleFactor(new JValue(0.5)));
        Check.Equal(1000.0, SimulationRuntimeControl.ScaleFactor(new JValue(1000)));
        foreach (var bad in new JToken[] { new JValue(0), new JValue(-1), new JValue(1000.5), new JValue(double.NaN), new JValue("2"), JValue.CreateNull() })
            Refused(() => SimulationRuntimeControl.ScaleFactor(bad));
        var time = SimulationRuntimeControl.SystemTimeUtc(new JValue("2030-01-02T03:04:05.250Z"));
        Check.Equal(DateTimeKind.Utc, time.Kind);
        Check.Equal(new DateTime(2030, 1, 2, 3, 4, 5, 250, DateTimeKind.Utc), time);
        foreach (string bad in new[] { "2030-01-02T03:04:05", "2030-01-02T03:04:05+01:00", "1969-12-31T23:59:59Z", "2201-01-01T00:00:00Z", "noon Z" })
            Refused(() => SimulationRuntimeControl.SystemTimeUtc(new JValue(bad)));
        Refused(() => SimulationRuntimeControl.SystemTimeUtc(new JValue(5)));
        return Task.CompletedTask;
    }

    internal static Task EventsMapToV8Codes()
    {
        var interrupt = SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"hardwareInterrupt\",\"hardwareId\":263,\"channel\":0,\"edge\":\"fallingEdge\"}"));
        Check.Equal(2, interrupt.Value<int>("code"));
        Check.Equal(263, interrupt.Value<int>("hardwareId"));
        Check.Equal(4, SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"pullOrPlug\",\"hardwareId\":1,\"action\":\"plugWrongModule\"}")).Value<int>("code"));
        Check.Equal(0, SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"rackOrStationFault\",\"hardwareId\":1,\"action\":\"fault\"}")).Value<int>("code"));
        Check.Equal(1, SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"rackOrStationFault\",\"hardwareId\":1,\"action\":\"return\"}")).Value<int>("code"));
        Check.Equal(7, SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"statusEvent\",\"hardwareId\":9,\"specifier\":7}")).Value<int>("specifier"));
        var alarm = SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"diagnosticAlarm\",\"hardwareId\":5,\"moduleState\":\"maintenanceRequired\",\"channels\":[{\"channel\":3,\"errorType\":1,\"severity\":\"maintenanceDemanded\",\"direction\":\"disappear\"}]}"));
        Check.Equal(4, alarm.Value<int>("moduleStateCode"));
        Check.Equal(1, alarm["channels"]![0]!.Value<int>("severityCode"));
        Check.Equal(2, alarm["channels"]![0]!.Value<int>("directionCode"));
        Check.Equal(0, alarm["channels"]![0]!.Value<int>("extErrorType"));
        Check.Equal(0, SimulationRuntimeControl.Event(JObject.Parse("{\"type\":\"diagnosticAlarm\",\"hardwareId\":5,\"moduleState\":\"ok\",\"channels\":[]}"))["channels"]!.Count());
        Check.Equal("OB40 (hardware interrupt)", SimulationRuntimeControl.ExpectedOb("hardwareInterrupt"));
        Check.Equal("OB86 (rack or station failure)", SimulationRuntimeControl.ExpectedOb("rackOrStationFault"));
        return Task.CompletedTask;
    }

    internal static Task EventsRefuseBeforeTheRuntimeCall()
    {
        foreach (string bad in new[] {
            "{\"type\":\"hardwareInterrupt\",\"hardwareId\":0,\"channel\":0,\"edge\":\"risingEdge\"}",
            "{\"type\":\"hardwareInterrupt\",\"hardwareId\":65536,\"channel\":0,\"edge\":\"risingEdge\"}",
            "{\"type\":\"hardwareInterrupt\",\"hardwareId\":1,\"edge\":\"risingEdge\"}",
            "{\"type\":\"hardwareInterrupt\",\"hardwareId\":1,\"channel\":0,\"edge\":\"rising\"}",
            "{\"type\":\"hardwareInterrupt\",\"hardwareId\":1,\"channel\":0,\"edge\":\"risingEdge\",\"action\":\"pull\"}",
            "{\"type\":\"pullOrPlug\",\"hardwareId\":\"1\",\"action\":\"pull\"}",
            "{\"type\":\"rackOrStationFault\",\"hardwareId\":1,\"action\":\"failure\"}",
            "{\"type\":\"diagnosticAlarm\",\"hardwareId\":1,\"moduleState\":\"ok\"}",
            "{\"type\":\"diagnosticAlarm\",\"hardwareId\":1,\"moduleState\":\"ok\",\"channels\":[{\"channel\":1,\"errorType\":1,\"severity\":\"failure\",\"direction\":\"appear\",\"x\":1}]}",
            "{\"type\":\"profileEvent\",\"hardwareId\":1}" })
            Refused(() => SimulationRuntimeControl.Event(JObject.Parse(bad)));
        var tooMany = new JArray(Enumerable.Range(0, 17).Select(i => new JObject { ["channel"] = i, ["errorType"] = 1, ["severity"] = "failure", ["direction"] = "appear" }));
        Refused(() => SimulationRuntimeControl.Event(new JObject { ["type"] = "diagnosticAlarm", ["hardwareId"] = 1, ["moduleState"] = "error", ["channels"] = tooMany }));
        Refused(() => SimulationRuntimeControl.Event(new JArray()));
        return Task.CompletedTask;
    }

    internal static Task ArchiveRetrieveNeedsOwnUnchangedArchive()
    {
        string directory = Path.Combine(Path.GetTempPath(), "archives");
        string archive = Path.Combine(directory, "TiaMcp_x-1.zip");
        var receipt = new JObject { ["archivePath"] = archive, ["instanceId"] = 3, ["instanceName"] = "TiaMcp_x", ["sha256"] = "aa" };
        Check.Equal(null, SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveOutsideStore", SimulationRuntimeControl.ArchiveRefusal(Path.Combine(directory, "sub", "a.zip"), directory, receipt, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveOutsideStore", SimulationRuntimeControl.ArchiveRefusal(Path.ChangeExtension(archive, ".7z"), directory, receipt, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveOutsideStore", SimulationRuntimeControl.ArchiveRefusal("TiaMcp_x-1.zip", directory, receipt, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveReceiptMissing", SimulationRuntimeControl.ArchiveRefusal(archive, directory, null, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveReceiptMissing", SimulationRuntimeControl.ArchiveRefusal(archive, directory, new JObject { ["archivePath"] = Path.Combine(directory, "other.zip"), ["instanceId"] = 3, ["instanceName"] = "TiaMcp_x", ["sha256"] = "aa" }, 3, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveTargetMismatch", SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 4, "TiaMcp_x", "aa", "aa"));
        Check.Equal("SimulationArchiveTargetMismatch", SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 3, "TiaMcp_y", "aa", "aa"));
        Check.Equal("SimulationArchiveChanged", SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 3, "TiaMcp_x", "bb", "aa"));
        Check.Equal("SimulationArchiveChanged", SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 3, "TiaMcp_x", null, "aa"));
        Check.Equal("SimulationArchiveChanged", SimulationRuntimeControl.ArchiveRefusal(archive, directory, receipt, 3, "TiaMcp_x", "aa", "bb"));
        return Task.CompletedTask;
    }

    internal static Task AreaAccessIsBoundedBeforeTheRuntimeCall()
    {
        var bytes = SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"output\",\"offset\":20,\"length\":1024,\"bit\":null}"), false);
        Check.Equal("output", bytes.Value<string>("area"));
        Check.Equal(1024, bytes.Value<int>("length"));
        Check.Equal(null, bytes["bit"]);
        Check.Equal(1, SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"input\",\"offset\":0}"), false).Value<int>("length"));
        var bit = SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"marker\",\"offset\":34,\"bit\":7}"), false);
        Check.Equal(7, bit.Value<int>("bit"));
        Check.Equal(1, bit.Value<int>("length"));
        var write = SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"input\",\"offset\":22,\"bytes\":[18,52],\"bit\":null,\"value\":null}"), true);
        Check.Equal(2, write.Value<int>("length"));
        Check.Equal(52, write["bytes"]![1]!.Value<int>());
        var bitWrite = SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"marker\",\"offset\":34,\"bit\":5,\"value\":true,\"bytes\":null}"), true);
        Check.Equal(true, bitWrite.Value<bool>("value"));
        Check.Equal(true, SimulationRuntimeControl.AreaFits(write, 24));
        Check.Equal(false, SimulationRuntimeControl.AreaFits(write, 23));
        var far = SimulationRuntimeControl.AreaAccess(JObject.Parse("{\"area\":\"input\",\"offset\":2147483647,\"length\":1024}"), false);
        Check.Equal(false, SimulationRuntimeControl.AreaFits(far, int.MaxValue + 1023L));
        Check.Equal(true, SimulationRuntimeControl.AreaFits(far, int.MaxValue + 1024L));
        foreach (string bad in new[] {
            "{\"area\":\"Input\",\"offset\":0}", "{\"area\":\"timer\",\"offset\":0}", "{\"offset\":0}", "{\"area\":\"input\"}", "{\"area\":\"input\",\"offset\":-1}",
            "{\"area\":\"input\",\"offset\":\"1\"}", "{\"area\":\"input\",\"offset\":0,\"length\":0}", "{\"area\":\"input\",\"offset\":0,\"length\":1025}",
            "{\"area\":\"input\",\"offset\":0,\"bit\":8}", "{\"area\":\"input\",\"offset\":0,\"bit\":1,\"length\":1}", "{\"area\":\"input\",\"offset\":0,\"value\":true}",
            "{\"area\":\"input\",\"offset\":0,\"bytes\":[1]}" })
            Refused(() => SimulationRuntimeControl.AreaAccess(JObject.Parse(bad), false));
        foreach (string bad in new[] {
            "{\"area\":\"output\",\"offset\":0,\"bytes\":[1]}", "{\"area\":\"input\",\"offset\":0}", "{\"area\":\"input\",\"offset\":0,\"bytes\":[]}",
            "{\"area\":\"input\",\"offset\":0,\"bytes\":[256]}", "{\"area\":\"input\",\"offset\":0,\"bytes\":[-1]}", "{\"area\":\"input\",\"offset\":0,\"bytes\":[1.5]}",
            "{\"area\":\"input\",\"offset\":0,\"bytes\":[1],\"value\":true}", "{\"area\":\"input\",\"offset\":0,\"bytes\":[1],\"length\":1}", "{\"area\":\"input\",\"offset\":0,\"bit\":0}",
            "{\"area\":\"input\",\"offset\":0,\"bit\":0,\"value\":1}", "{\"area\":\"input\",\"offset\":0,\"bit\":0,\"value\":true,\"bytes\":[1]}" })
            Refused(() => SimulationRuntimeControl.AreaAccess(JObject.Parse(bad), true));
        Refused(() => SimulationRuntimeControl.AreaAccess(new JObject { ["area"] = "marker", ["offset"] = 0, ["bytes"] = new JArray(Enumerable.Repeat(0, 1025)) }, true));
        return Task.CompletedTask;
    }

    internal static Task SignalBatchesCarryPrimitiveValues()
    {
        Check.Equal(null, SimulationRuntimeControl.SignalBatchRefusal(["\"Db\".A", "\"Db\".A", new string('x', 299)], ["bool", "bool", "uint64"], false));
        Check.Equal(null, SimulationRuntimeControl.SignalBatchRefusal(["\"Db\".A", "\"Db\".B"], ["double", "int8"], true));
        Check.Equal("UnsupportedSignalType", SimulationRuntimeControl.SignalBatchRefusal(["\"Db\".A", "\"Db\".S"], ["bool", "string"], false));
        Check.Equal("UnsupportedSignalType", SimulationRuntimeControl.SignalBatchRefusal(["\"Db\".W"], ["wstring"], true));
        Check.Equal("InvalidRequest", SimulationRuntimeControl.SignalBatchRefusal([new string('x', 300)], ["bool"], false));
        Check.Equal("InvalidRequest", SimulationRuntimeControl.SignalBatchRefusal(["\"Db\".A", "\"Db\".A"], ["bool", "bool"], true));
        return Task.CompletedTask;
    }

    // Every new runtime write sits behind the owned-instance check; the source is read like the network-mode guard.
    internal static Task WritesRequireAnOwnedInstance()
    {
        string relative = Path.Combine("src", "TiaMcp.Simulator.V8", "SimulatorRuntimeControl.cs");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, relative))) root = root.Parent;
        string source = File.ReadAllText(Path.Combine(root?.FullName ?? throw new InvalidOperationException("Simulator source missing."), relative));
        foreach (var (method, write) in new[] { ("JObject Time(", "Instance.ScaleFactor ="), ("JObject Time(", "Instance.SystemTime ="), ("JObject Event(", "Instance.ProcessEvent("),
                     ("JObject Event(", "Instance.RackOrStationFaultEvent("), ("JObject Storage(", "Instance.ArchiveStorage("), ("JObject Storage(", "Instance.RetrieveStorage(") })
        {
            int start = source.IndexOf(method, StringComparison.Ordinal), guard = source.IndexOf("RequireOwned(selected)", start, StringComparison.Ordinal), call = source.IndexOf(write, start, StringComparison.Ordinal);
            Check.Equal(true, start >= 0 && guard > start && call > guard);
        }
        string session = File.ReadAllText(Path.Combine(root.FullName, "src", "TiaMcp.Simulator.V8", "SimulatorSession.cs"));
        Check.Equal(true, System.Text.RegularExpressions.Regex.IsMatch(session, @"case ""memoryReset"": RequireOwned\(selected\); EnsureAdmitted\(selected\); selected\.Instance\.MemoryReset\("));
        // Batch and area writes: owned and admitted before the write; reads and observation call no write, state or mode change.
        string signals = File.ReadAllText(Path.Combine(root.FullName, "src", "TiaMcp.Simulator.V8", "SimulatorSession.Signals.cs"));
        string Body(string method) { int start = signals.IndexOf(method, StringComparison.Ordinal); Check.Equal(true, start >= 0); int end = signals.IndexOf("\n        private ", start + 1, StringComparison.Ordinal); return signals[start..(end < 0 ? signals.Length : end)]; }
        foreach (var (method, write) in new[] { ("JObject SignalsWrite(", "Instance.WriteSignals("), ("JObject AreaWrite(", "area.WriteBytes("), ("JObject AreaWrite(", "area.WriteBit(") })
        {
            string body = Body(method);
            int owned = body.IndexOf("RequireOwned(selected);\n            EnsureAdmitted(selected);", StringComparison.Ordinal), call = body.IndexOf(write, StringComparison.Ordinal);
            Check.Equal(true, owned > 0 && call > owned);
        }
        foreach (string method in new[] { "JObject SignalsRead(", "JObject AreaRead(", "JObject Observe(" })
            Check.Equal(false, System.Text.RegularExpressions.Regex.IsMatch(Body(method), @"\.Write|\.Run\(|\.Stop\(|\.Power|OperatingMode\s*=|MemoryReset"));
        return Task.CompletedTask;
    }
}
