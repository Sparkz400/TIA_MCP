using System.Globalization;
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

    internal static Task CaptureModesAreBounded()
    {
        Check.Equal<string?>(null, SimulationRuntimeControl.CaptureMode(new JObject()));
        Check.Equal("SingleStep_C", SimulationRuntimeControl.CaptureMode(JObject.Parse("{cycleSynchronous:true}")));
        foreach (string mode in new[] { "SingleStep_C", "SingleStep_P", "SingleStep_CP", "SingleStep_CT", "SingleStep_CPT" })
            Check.Equal(mode, SimulationRuntimeControl.CaptureMode(new JObject { ["cycleSynchronous"] = true, ["syncMode"] = mode }));
        foreach (string mode in new[] { "TimespanSynchronized_C", "TimespanSynchronized_P", "TimespanSynchronized_CP" })
        {
            var timespan = new JObject { ["cycleSynchronous"] = true, ["syncMode"] = mode, ["spanMilliseconds"] = 10 };
            Check.Equal(mode, SimulationRuntimeControl.CaptureMode(timespan));
            Check.Equal<long?>(10000000L, SimulationRuntimeControl.CaptureSpanNanoseconds(timespan));
        }
        Check.Equal<long?>(null, SimulationRuntimeControl.CaptureSpanNanoseconds(JObject.Parse("{cycleSynchronous:true,syncMode:'SingleStep_CP'}")));
        // SingleStep_Bus passes the I/O system and a time stamp step of one bus cycle; the others pass nothing.
        var bus = JObject.Parse("{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:100,busCycleMicroseconds:250}");
        Check.Equal("SingleStep_Bus", SimulationRuntimeControl.CaptureMode(bus));
        Check.Equal<(uint, ulong)?>((100u, 250000UL), SimulationRuntimeControl.CaptureBus(bus));
        Check.Equal<(uint, ulong)?>(null, SimulationRuntimeControl.CaptureBus(JObject.Parse("{cycleSynchronous:true,syncMode:'SingleStep_CP'}")));
        foreach (int io in new[] { 1, 32, 100, 115 })
            Check.Equal("SingleStep_Bus", SimulationRuntimeControl.CaptureMode(new JObject { ["cycleSynchronous"] = true, ["syncMode"] = "SingleStep_Bus", ["ioSystemId"] = io, ["busCycleMicroseconds"] = 1 }));
        foreach (string input in new[] { "{cycleSynchronous:'true'}", "{cycleSynchronous:true,syncMode:1}",
            "{syncMode:'SingleStep_C'}", "{cycleSynchronous:false,syncMode:'SingleStep_C'}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus'}", "{cycleSynchronous:true,syncMode:'TimespanSynchronized_C'}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:100}", "{cycleSynchronous:true,syncMode:'SingleStep_Bus',busCycleMicroseconds:250}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:0,busCycleMicroseconds:250}", "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:33,busCycleMicroseconds:250}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:99,busCycleMicroseconds:250}", "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:116,busCycleMicroseconds:250}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:100,busCycleMicroseconds:0}", "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:100,busCycleMicroseconds:1000001}",
            "{cycleSynchronous:true,syncMode:'SingleStep_Bus',ioSystemId:100.5,busCycleMicroseconds:250}", "{cycleSynchronous:true,syncMode:'SingleStep_CP',ioSystemId:100,busCycleMicroseconds:250}",
            "{cycleSynchronous:true,syncMode:'TimespanSynchronized_C',spanMilliseconds:0}", "{cycleSynchronous:true,syncMode:'TimespanSynchronized_P',spanMilliseconds:60001}",
            "{cycleSynchronous:true,syncMode:'TimespanSynchronized_CP',spanMilliseconds:1.5}", "{cycleSynchronous:true,syncMode:'SingleStep_C',spanMilliseconds:10}",
            "{cycleSynchronous:true,spanMilliseconds:10}", "{cycleSynchronous:true,syncMode:'TimespanSynchronized_CT',spanMilliseconds:10}",
            "{cycleSynchronous:true,syncMode:'Default'}", "{cycleSynchronous:true,syncMode:'singlestep_c'}" })
            Refused(() => SimulationRuntimeControl.CaptureMode(JObject.Parse(input)));
        return Task.CompletedTask;
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
        foreach (string type in new[] { "statusEvent", "updateEvent", "profileEvent" })
        {
            Check.Equal(65535, SimulationRuntimeControl.Event(new JObject { ["type"] = type, ["hardwareId"] = 9, ["specifier"] = 65535 }).Value<int>("specifier"));
            Refused(() => SimulationRuntimeControl.Event(new JObject { ["type"] = type, ["hardwareId"] = 9, ["specifier"] = 65536 }));
            Refused(() => SimulationRuntimeControl.Event(new JObject { ["type"] = type, ["hardwareId"] = 9, ["specifier"] = 1, ["extra"] = 1 }));
        }
        Check.Equal("OB56 (update)", SimulationRuntimeControl.ExpectedOb("updateEvent"));
        Check.Equal("OB57 (profile)", SimulationRuntimeControl.ExpectedOb("profileEvent"));
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

    // The area PLCSIM's tag list reports decides, never the name: a DB member called Output stays writable, a tag called Marker at %Q does not.
    internal static Task SymbolicOutputWritesAreRefusedByTheirTagListArea()
    {
        var tags = new (string, string)[] { ("Marker", "Output"), ("SigOutWord", "Output"), ("OutStruct", "Output"), ("SigInByte", "Input"),
            ("SigMarker", "Marker"), ("SigDb.Output", "DataBlock"), ("SigDb.VInt", "DataBlock"), ("SigDb.Arr", "DataBlock"), ("T1", "Timer") };
        Check.Equal("\"Marker\"", SimulationRuntimeControl.OutputSymbol(["\"Marker\""], tags));
        Check.Equal("sigoutword", SimulationRuntimeControl.OutputSymbol(["sigoutword"], tags));
        Check.Equal("\"OutStruct\".Member", SimulationRuntimeControl.OutputSymbol(["\"OutStruct\".Member"], tags));
        Check.Equal("OutStruct[1]", SimulationRuntimeControl.OutputSymbol(["OutStruct[1]"], tags));
        // A batch with one output among inputs, markers and DB members is refused as a whole, naming that symbol.
        Check.Equal("\"SigOutWord\"", SimulationRuntimeControl.OutputSymbol(["\"SigInByte\"", "\"SigMarker\"", "\"SigDb\".VInt", "\"SigOutWord\""], tags));
        Check.Equal(null, SimulationRuntimeControl.OutputSymbol(["\"SigInByte\"", "\"SigMarker\"", "\"SigDb\".Output", "\"SigDb\".Arr[3]", "T1"], tags));
        // Only a whole segment is a parent; OutputSymbol does not judge a symbol missing from the tag list (UnlistedSymbol does).
        Check.Equal(null, SimulationRuntimeControl.OutputSymbol(["\"SigOutWordCopy\"", "\"OutStructDb\".Member", "\"Missing\".X"], tags));
        // A refreshed tag list refuses what it does not list: itself, as a member or element of an entry (quoting and case ignored) are known.
        string[] names = tags.Select(t => t.Item1).ToArray();
        Check.Equal("\"Missing\".X", SimulationRuntimeControl.UnlistedSymbol(["\"SigDb\".VInt", "\"Missing\".X"], names));
        Check.Equal("\"SigDbX\".VInt", SimulationRuntimeControl.UnlistedSymbol(["\"SigDbX\".VInt"], names));
        Check.Equal(null, SimulationRuntimeControl.UnlistedSymbol(["\"sigdb\".vint", "\"SigDb\".Arr[3]", "T1", "\"OutStruct\".Member"], names));
        Check.Equal(null, SimulationRuntimeControl.UnlistedSymbol(["\"Missing\".X"], []));
        // Only the adapter's own newest reports stay; other files in the folder are never listed for deletion.
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        string Report(int n) => "C:/r/simulation-test-" + n.ToString("x32", CultureInfo.InvariantCulture) + ".json";
        var files = Enumerable.Range(0, 5).Select(n => (Report(n), now.AddMinutes(n))).Append(("C:/r/notes.json", now.AddDays(-9))).Append(("C:/r/simulation-test-keep.json", now.AddDays(-9))).ToArray();
        Check.Equal(string.Join(",", Report(1), Report(0)), string.Join(",", SimulationRuntimeControl.ExcessReports(files, 3)));
        Check.Equal(0, SimulationRuntimeControl.ExcessReports(files, 50).Count);
        return Task.CompletedTask;
    }

    // sim_prune: only storage of an unregistered, receipt-backed instance and unchanged receipted archives are ever deletable.
    internal static Task StoragePruneNeverTouchesLiveOrForeignStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "sim-prune-" + Guid.NewGuid().ToString("N"));
        string archives = Path.Combine(root, "archives");
        Directory.CreateDirectory(archives);
        try
        {
            const string marker = ".tiamcp-instance.json";
            string Name() => "TiaMcp_" + Guid.NewGuid().ToString("N");
            string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            var receipts = new List<JObject>();
            string Instance(string stage, bool receipt = true, bool changedMarker = false, int bytes = 10, int ageDays = 0)
            {
                string name = Name(), folder = Path.Combine(root, name);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, marker), "{\"m\":1}");
                File.WriteAllBytes(Path.Combine(folder, "card.bin"), new byte[bytes]);
                if (receipt) receipts.Add(new JObject { ["instanceName"] = name, ["stage"] = stage, ["storagePath"] = folder, ["markerSha256"] = Sha(Path.Combine(folder, marker)) });
                if (changedMarker) File.WriteAllText(Path.Combine(folder, marker), "{\"m\":2}");
                File.SetLastWriteTimeUtc(Path.Combine(folder, "card.bin"), DateTime.UtcNow.AddDays(-ageDays));
                File.SetLastWriteTimeUtc(Path.Combine(folder, marker), DateTime.UtcNow.AddDays(-ageDays));
                Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddDays(-ageDays));
                return name;
            }
            string old = Instance("unregistered", bytes: 100, ageDays: 30), failed = Instance("unregisteredAfterFailure", bytes: 100, ageDays: 20), recent = Instance("unregistered", bytes: 100);
            string registered = Instance("unregistered"), ready = Instance("ready"), noReceipt = Instance("unregistered", receipt: false), tampered = Instance("unregistered", changedMarker: true);
            Directory.CreateDirectory(Path.Combine(root, "other-folder"));
            var storage = SimulationStoragePrune.Storage(root, marker, receipts, n => n == registered);
            string Refusal(string name) => storage.Single(c => c.Name == name).Refusal ?? "";
            Check.Equal("", Refusal(old)); Check.Equal("", Refusal(failed)); Check.Equal("", Refusal(recent));
            Check.Equal("registeredOrSelected", Refusal(registered)); Check.Equal("instanceNotUnregistered", Refusal(ready));
            Check.Equal("noCreationReceipt", Refusal(noReceipt)); Check.Equal("ownershipMarkerChanged", Refusal(tampered));
            Check.Equal(7, storage.Count);
            // Age, then size: the oldest eligible go first; nothing refused is ever selected.
            Check.Equal(string.Join(",", old, failed), string.Join(",", SimulationStoragePrune.Select(storage, null, DateTime.UtcNow.AddDays(-10), null).Select(c => c.Name)));
            Check.Equal(old, string.Join(",", SimulationStoragePrune.Select(storage, null, null, 0).Take(1).Select(c => c.Name)));
            Check.Equal(0, SimulationStoragePrune.Select(storage, null, null, 100000).Count);
            Check.Equal(3, SimulationStoragePrune.Select(storage, null, null, 0).Count);
            // Named choice keeps a refused candidate refused; an unknown name is not accepted.
            var named = SimulationStoragePrune.Select(storage, new[] { registered, recent }, null, null);
            Check.Equal("registeredOrSelected", SimulationStoragePrune.Delete(named.Single(c => c.Name == registered), n => n == registered));
            try { SimulationStoragePrune.Select(storage, new[] { "other-folder" }, null, null); throw new InvalidOperationException("unknown name accepted"); } catch (ArgumentException) { }
            // The liveness check is repeated just before the delete.
            Check.Equal("registeredOrSelected", SimulationStoragePrune.Delete(storage.Single(c => c.Name == recent), n => n == recent));
            Check.Equal(true, Directory.Exists(Path.Combine(root, recent)));
            Check.Equal(null, SimulationStoragePrune.Delete(storage.Single(c => c.Name == old), n => false));
            Check.Equal(false, Directory.Exists(Path.Combine(root, old)));
            foreach (string kept in new[] { registered, ready, noReceipt, tampered, "other-folder" }) Check.Equal(true, Directory.Exists(Path.Combine(root, kept)));
            // A link inside a folder makes it ineligible and is never followed.
            string linked = Instance("unregistered"), target = Path.Combine(root, "link-target");
            Directory.CreateDirectory(target);
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(root, linked, "junction") + "\" \"" + target + "\"") { CreateNoWindow = true, UseShellExecute = false })!)
            {
                mklink.WaitForExit(10000);
                if (Directory.Exists(Path.Combine(root, linked, "junction")))
                    Check.Equal("containsReparsePoint", SimulationStoragePrune.Storage(root, marker, receipts, n => false).Single(c => c.Name == linked).Refusal);
            }
            // Archives need their receipt, unchanged.
            string Archive(bool receipt, bool changed = false)
            {
                string path = Path.Combine(archives, Name() + "-" + Guid.NewGuid().ToString("N") + ".zip");
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                if (receipt) File.WriteAllText(path + ".json", new JObject { ["schemaVersion"] = 1, ["archivePath"] = path, ["instanceName"] = "TiaMcp_x", ["sha256"] = Sha(path), ["bytes"] = 4 }.ToString());
                if (changed) File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });
                return Path.GetFileName(path);
            }
            string good = Archive(true), bare = Archive(false), edited = Archive(true, changed: true);
            File.WriteAllText(Path.Combine(archives, "notes.zip"), "x");
            var found = SimulationStoragePrune.Archives(archives);
            Check.Equal(3, found.Count);
            Check.Equal(null, found.Single(c => c.Name == good).Refusal);
            Check.Equal("noArchiveReceipt", found.Single(c => c.Name == bare).Refusal);
            Check.Equal("archiveChanged", found.Single(c => c.Name == edited).Refusal);
            Check.Equal(null, SimulationStoragePrune.Delete(found.Single(c => c.Name == good), n => true));
            Check.Equal(false, File.Exists(Path.Combine(archives, good)) || File.Exists(Path.Combine(archives, good + ".json")));
            Check.Equal(true, File.Exists(Path.Combine(archives, bare)) && File.Exists(Path.Combine(archives, edited)) && File.Exists(Path.Combine(archives, "notes.zip")));
            Check.Equal(0, SimulationStoragePrune.Storage(Path.Combine(root, "missing"), marker, receipts, n => false).Count);
        }
        finally
        {
            // A junction is removed on its own first: a recursive delete refuses to cross it.
            foreach (string junction in Directory.Exists(root) ? Directory.GetDirectories(root, "junction", SearchOption.AllDirectories) : []) { try { Directory.Delete(junction); } catch (IOException) { } }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    // TIA's quoting ("Tag".A) and an unquoted V8 tag list (Tag.A) name the same tag; PLCSIM resolves only its own spelling.
    internal static Task SymbolsTakeTheTagListSpelling()
    {
        string[] names = ["MC7_Probe_IO.A", "SigDb.VInt", "SigDb.Arr", "\"QuotedDb\".X", "Dup.A", "DUP.A"];
        Check.Equal("MC7_Probe_IO.A", SimulationRuntimeControl.TagListSpelling("\"MC7_Probe_IO\".A", names));
        Check.Equal("MC7_Probe_IO.A", SimulationRuntimeControl.TagListSpelling("MC7_Probe_IO.A", names));
        Check.Equal("SigDb.VInt", SimulationRuntimeControl.TagListSpelling("\"sigdb\".vint", names));
        // An element of a listed entry keeps its index; a quoted tag list is matched from the unquoted form too.
        Check.Equal("SigDb.Arr[3]", SimulationRuntimeControl.TagListSpelling("\"SigDb\".Arr[3]", names));
        Check.Equal("\"QuotedDb\".X", SimulationRuntimeControl.TagListSpelling("QuotedDb.X", names));
        // No match, an ambiguous match, or only a partial segment leaves the symbol to PLCSIM unchanged.
        Check.Equal("\"Missing\".X", SimulationRuntimeControl.TagListSpelling("\"Missing\".X", names));
        Check.Equal("\"Dup\".A", SimulationRuntimeControl.TagListSpelling("\"Dup\".A", names));
        Check.Equal("\"SigDbX\".VInt", SimulationRuntimeControl.TagListSpelling("\"SigDbX\".VInt", names));
        Check.Equal("\"SigDb\".VInt", SimulationRuntimeControl.TagListSpelling("\"SigDb\".VInt", []));
        return Task.CompletedTask;
    }

    internal static Task EventTimesAreUtc()
    {
        // The native shape: an Unspecified V8 event time already holds UTC and only gains its zone; a Local one is converted.
        Check.Equal("2026-10-30T07:36:19.8760000Z", SimulationRuntimeControl.EventTimeUtc(new DateTime(2026, 10, 30, 7, 36, 19, 876, DateTimeKind.Unspecified)));
        var local = new DateTime(2026, 10, 30, 8, 36, 19, 876, DateTimeKind.Local);
        Check.Equal(local.ToUniversalTime().ToString("O"), SimulationRuntimeControl.EventTimeUtc(local));
        Check.Equal(true, SimulationRuntimeControl.EventTimeUtc(local).EndsWith("Z", StringComparison.Ordinal));
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
        string signals = File.ReadAllText(Path.Combine(root.FullName, "src", "TiaMcp.Simulator.V8", "SimulatorSession.Signals.cs"));
        string Body(string text, string method) { int start = text.IndexOf(method, StringComparison.Ordinal); Check.Equal(true, start >= 0); int end = text.IndexOf("\n        private ", start + 1, StringComparison.Ordinal); return text[start..(end < 0 ? text.Length : end)]; }
        // Every sim_state action and sim_write: owned first; state changes also wait for a held capture mode to end.
        string state = Body(session, "JObject State(");
        int stateGuard = state.IndexOf("RequireOwned(selected);\n            RequireNoCapture(selected);", StringComparison.Ordinal);
        foreach (string call in new[] { "Instance.PowerOn(", "Instance.PowerOff(", "Instance.Run(", "Instance.Stop(", "Instance.MemoryReset(" })
            Check.Equal(true, stateGuard > 0 && state.IndexOf(call, StringComparison.Ordinal) > stateGuard);
        string writer = Body(session, "JObject Write(");
        Check.Equal(true, writer.IndexOf("RequireOwned(selected);", StringComparison.Ordinal) is > 0 and var writeGuard && writer.IndexOf("ApplyWrites(", StringComparison.Ordinal) > writeGuard);
        // sim_test: any step with writes demands ownership before the first runtime call; a release ends a held capture only when the Host (which has no live capture then) says so.
        string scenario = Body(session, "JObject Test(");
        Check.Equal(true, scenario.IndexOf("item[\"writes\"] != null) == true) RequireOwned(selected);", StringComparison.Ordinal) is > 0 and var first &&
            first < scenario.IndexOf("EnsureAdmitted(selected)", StringComparison.Ordinal) && first < scenario.IndexOf("ApplyWrites(", StringComparison.Ordinal));
        string release = Body(session, "JObject Release(");
        Check.Equal(true, release.Contains("else { if (!restoreHeld) RequireNoCapture(selected); RestoreCaptureMode(selected); }", StringComparison.Ordinal)
            && release.Contains("selected.CaptureHeld && !restoreHeld) throw new SimulatorException(\"SimulationCaptureModeActive\"", StringComparison.Ordinal));
        // Only RuntimeCaptures sets restoreHeldCapture, and it refuses first while one of its captures is live on that instance.
        string hostCaptures = File.ReadAllText(Path.Combine(root.FullName, "src", "TiaMcp.Host", "RuntimeCaptures.cs"));
        Check.Equal(true, hostCaptures.IndexOf("x.InstanceId == instanceId))", StringComparison.Ordinal) is > 0 and var live && hostCaptures.IndexOf("restoreHeldCapture = true", StringComparison.Ordinal) > live);
        Check.Equal(1, Directory.GetFiles(Path.Combine(root.FullName, "src", "TiaMcp.Host"), "*.cs").Count(f => File.ReadAllText(f).Contains("restoreHeldCapture", StringComparison.Ordinal)));
        // Only a running capture or scenario holds the instance: every restore, also a failed one, ends the hold first, so release can retry it.
        Check.Equal(true, Body(session, "static void RestoreCaptureMode(").Contains("{\n            selected.CaptureHeld = false;\n            if (selected.CaptureRestoreMode == null) return;", StringComparison.Ordinal));
        Check.Equal(true, Body(source, "static void RequireNoCapture(").Contains("if (selected.CaptureHeld) throw", StringComparison.Ordinal));
        foreach (string text in new[] { session, source })
            Check.Equal(true, text.Split("CaptureRestoreMode = previous;").Length == 2 && text.Contains("CaptureRestoreMode = previous;\n                selected.CaptureHeld = true;", StringComparison.Ordinal));
        // Batch and area writes: owned and admitted before the write; reads and observation call no write, state or mode change.
        foreach (var (method, write) in new[] { ("JObject SignalsWrite(", "Instance.WriteSignals("), ("JObject AreaWrite(", "area.WriteBytes("), ("JObject AreaWrite(", "area.WriteBit(") })
        {
            string body = Body(signals, method);
            int owned = body.IndexOf("RequireOwned(selected);\n            EnsureAdmitted(selected);", StringComparison.Ordinal), call = body.IndexOf(write, StringComparison.Ordinal);
            Check.Equal(true, owned > 0 && call > owned);
        }
        foreach (string method in new[] { "JObject SignalsRead(", "JObject AreaRead(", "JObject Observe(" })
            Check.Equal(false, System.Text.RegularExpressions.Regex.IsMatch(Body(signals, method), @"\.Write|\.Run\(|\.Stop\(|\.Power|OperatingMode\s*=|MemoryReset"));
        // Every symbolic writer refuses outputs from the tag list before its first read or write, so a refused request writes nothing.
        foreach (var (text, method, access) in new[] { (session, "JObject Write(", "ApplyWrites("), (session, "JObject WriteOwned(", "Capture("),
                     (session, "JObject Test(", "ApplyWrites("), (signals, "JObject SignalsWrite(", "Instance.WriteSignals(") })
        {
            string body = Body(text, method);
            int refusal = body.IndexOf("RefuseOutputs(selected, ", StringComparison.Ordinal);
            Check.Equal(true, refusal > 0 && body.IndexOf(access, StringComparison.Ordinal) > refusal);
        }
        // The tag list is refreshed first (a download can add tags), then both the output area and unlisted symbols refuse with InvalidRequest.
        string refusal2 = Body(session, "static void RefuseOutputs(");
        Check.Equal(true, refusal2.IndexOf("UpdateTagList(", StringComparison.Ordinal) is > 0 and var refresh && refusal2.IndexOf("selected.Instance.TagInfos", StringComparison.Ordinal) > refresh
            && refusal2.Contains("UnlistedSymbol(", StringComparison.Ordinal) && refusal2.Contains("new SimulatorException(\"InvalidRequest\"", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
