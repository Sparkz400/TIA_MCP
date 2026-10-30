using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class TechnologyConnectionTests
    {
        internal static Task NullSensorsConsumeBudget()
        {
            int visited = 0;
            IEnumerable<AxisEncoderHardwareConnectionInterface> Sensors()
            {
                for (int i = 0; i < 100; i++) { visited++; yield return null!; }
            }
            var axis = new AxisHardwareConnectionProvider { Sensors = Sensors() };
            var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(AxisHardwareConnectionProvider) ? axis : null };
            OpennessSession.ConnectionsForTest(owner, 10, out _, out var truncated);
            Check.Equal(true, visited <= 11); Check.Equal(true, truncated);
            return Task.CompletedTask;
        }

        internal static Task IndependentFailures()
        {
            var encoder = new EncoderHardwareConnectionProvider { SensorInterface = new() };
            var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(AxisHardwareConnectionProvider)
                ? throw new Exception("SECRET") : type == typeof(EncoderHardwareConnectionProvider) ? encoder : null };
            var rows = OpennessSession.ConnectionsForTest(owner, 100, out var warnings, out _);
            Check.Equal(true, rows.Any(r => (string?)r["role"] == "encoder"));
            Check.Equal(false, warnings.ToString().Contains("SECRET"));
            return Task.CompletedTask;
        }

        private static JToken Service(JObject coverage, string name) => coverage["services"]!.Single(s => s.Value<string>("service") == name);

        internal static Task ServiceStates()
        {
            var owner = new TechnologicalInstanceDB([]);
            var rows = OpennessSession.ConnectionCoverageForTest(owner, 4, out var warnings, out var coverage, out bool truncated, out int used);
            Check.Equal(4, owner.ServiceReads); Check.Equal(4, used); Check.Equal(false, truncated); Check.Equal(0, warnings.Count);
            Check.Equal(0, rows.Count); Check.Equal(false, coverage.Value<bool>("complete")); Check.Equal(true, coverage.Value<bool>("scopeComplete"));
            foreach (var service in coverage["services"]!) { Check.Equal("notExposed", service.Value<string>("status")); Check.Equal(true, service.Value<bool>("scopeComplete")); }
            owner.TypedService = _ => throw new Exception("SECRET");
            OpennessSession.ConnectionCoverageForTest(owner, 3, out warnings, out coverage, out truncated, out used);
            Check.Equal(7, owner.ServiceReads); Check.Equal(3, used); Check.Equal(true, truncated); Check.Equal(4, warnings.Count);
            Check.Equal("readError", Service(coverage, "axis").Value<string>("status")); Check.Equal("notVisited", Service(coverage, "outputCam").Value<string>("status"));
            Check.Equal(false, warnings.ToString().Contains("SECRET")); Check.Equal(false, coverage.Value<bool>("scopeComplete"));
            return Task.CompletedTask;
        }

        internal static Task FieldsAndProjections()
        {
            foreach (string failed in new[] { "isConnected", "inputAddress", "inputModule", "channel", "channelIndex" })
            {
                var measuring = new MeasuringInputHardwareConnectionProvider { Read = name => name == failed ? throw new Exception("SECRET") : name == "isConnected" ? false : null };
                var output = new OutputCamHardwareConnectionProvider();
                var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(MeasuringInputHardwareConnectionProvider) ? measuring : type == typeof(OutputCamHardwareConnectionProvider) ? output : null };
                var rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out var warnings, out var coverage, out _, out _);
                var row = rows.Single(r => r.Value<string>("role") == "measuringInput");
                Check.Equal(5, measuring.Reads.Count); Check.Equal("readError", (string?)row["fieldStatus"]?[failed]); Check.Equal(false, row.Value<bool>("scopeComplete"));
                Check.Equal(true, rows.Single(r => r.Value<string>("role") == "outputCam").Value<bool>("scopeComplete"));
                Check.Equal("observed", Service(coverage, "outputCam").Value<string>("status")); Check.Equal(false, warnings.ToString().Contains("SECRET"));
                Check.Equal(false, coverage.Value<bool>("scopeComplete"));
            }
            foreach (object projection in new object[] { new Siemens.Engineering.HW.DeviceItem(), new string('x', 4097), double.NaN })
            {
                var output = new OutputCamHardwareConnectionProvider { Read = name => name == "isConnected" ? true : name == "channel" ? projection : null };
                var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(OutputCamHardwareConnectionProvider) ? output : null };
                var rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out var warnings, out var coverage, out _, out _);
                Check.Equal("displayOnly", (string?)rows[0]!["fieldStatus"]?["channel"]); Check.Equal(false, coverage.Value<bool>("scopeComplete")); Check.Equal(1, warnings.Count);
            }
            return Task.CompletedTask;
        }

        internal static Task SensorFailuresKeepPeers()
        {
            foreach (string failure in new[] { "null", "getter", "enumeration", "dispose", "nullEntry" })
            {
                int disposed = 0;
                IEnumerable<AxisEncoderHardwareConnectionInterface> Sensors()
                {
                    try { yield return new(); if (failure == "enumeration") throw new Exception("SECRET"); if (failure == "nullEntry") yield return null!; }
                    finally { disposed++; if (failure == "dispose") throw new Exception("SECRET"); }
                }
                var axis = new AxisHardwareConnectionProvider { Actor = new(), Sensors = failure == "null" ? null : Sensors(), Torque = new AxisEncoderHardwareConnectionInterface() };
                if (failure == "getter") axis.Read = name => name == "sensors" ? throw new Exception("SECRET") : name == "actor" ? axis.Actor : axis.Torque;
                var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(AxisHardwareConnectionProvider) ? axis : null };
                var rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out var warnings, out var coverage, out _, out _);
                Check.Equal(true, rows.Single(r => r.Value<string>("role") == "torque").Value<bool>("scopeComplete"));
                Check.Equal(1, axis.Reads.Count(n => n == "torque")); Check.Equal(false, coverage.Value<bool>("scopeComplete"));
                Check.Equal(failure is "null" or "getter" ? 0 : 1, disposed); Check.Equal(false, warnings.ToString().Contains("SECRET"));
                if (failure == "nullEntry") Check.Equal("missing", rows.Last(r => r.Value<string>("role") == "sensor").Value<string>("status"));
            }
            return Task.CompletedTask;
        }

        internal static Task BudgetBeforeGetters()
        {
            var owner = new TechnologicalInstanceDB([]) { TypedService = _ => throw new Exception("Getter must not run") };
            var rows = OpennessSession.ConnectionCoverageForTest(owner, 0, out var warnings, out var coverage, out var truncated, out int used);
            Check.Equal(0, owner.ServiceReads); Check.Equal(0, used); Check.Equal(0, rows.Count); Check.Equal(4, warnings.Count); Check.Equal(true, truncated);
            Check.Equal(true, coverage["services"]!.All(s => s.Value<string>("status") == "notVisited"));
            for (int limit = 1; limit <= 18; limit++)
            {
                int enumerated = 0, disposed = 0;
                IEnumerable<AxisEncoderHardwareConnectionInterface> Sensors()
                {
                    try { while (true) { enumerated++; yield return new(); } }
                    finally { disposed++; }
                }
                var axis = new AxisHardwareConnectionProvider { Actor = new(), Sensors = Sensors() };
                owner = new([]) { TypedService = type => type == typeof(AxisHardwareConnectionProvider) ? axis : throw new Exception("Budget exhausted before other services") };
                rows = OpennessSession.ConnectionCoverageForTest(owner, limit, out warnings, out coverage, out truncated, out used);
                Check.Equal(limit, used); Check.Equal(true, truncated); Check.Equal(1, owner.ServiceReads);
                Check.Equal(true, enumerated <= limit + 1); Check.Equal(enumerated == 0 ? 0 : 1, disposed);
                Check.Equal(false, coverage.Value<bool>("scopeComplete"));
                foreach (var row in rows.Where(r => r.Value<string>("status") == "notVisited")) Check.Equal(false, row.Value<bool>("scopeComplete"));
            }
            return Task.CompletedTask;
        }

        internal static Task CompleteAndAbsentSlots()
        {
            var axis = new AxisHardwareConnectionProvider { Actor = new(), Sensors = [new(), new()], Torque = new AxisEncoderHardwareConnectionInterface() };
            var encoder = new EncoderHardwareConnectionProvider { SensorInterface = new() };
            var measuring = new MeasuringInputHardwareConnectionProvider(); var output = new OutputCamHardwareConnectionProvider();
            var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(AxisHardwareConnectionProvider) ? axis : type == typeof(EncoderHardwareConnectionProvider) ? encoder : type == typeof(MeasuringInputHardwareConnectionProvider) ? measuring : output };
            var rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out var warnings, out var coverage, out bool truncated, out _);
            Check.Equal(7, rows.Count); Check.Equal(0, warnings.Count); Check.Equal(false, truncated); Check.Equal(true, coverage.Value<bool>("scopeComplete"));
            Check.Equal("/PLC/technology/Axis", coverage.Value<string>("technologyPath"));
            Check.Equal(7, rows.Select(r => r.Value<string>("observationPath")).Distinct().Count());
            Check.Equal(true, rows.All(r => r.Value<bool>("scopeComplete") && r.Value<string>("status") == "observed"));
            Check.Equal(4, Service(coverage, "axis").Value<int>("rowsCaptured"));
            Check.Equal(true, rows.All(r => r.Value<string>("observationPath")!.StartsWith("/PLC/technology/Axis/hardwareConnections/", StringComparison.Ordinal)));
            axis.Actor = null; axis.Sensors = []; axis.Torque = null; encoder.SensorInterface = null;
            rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out warnings, out coverage, out _, out _);
            Check.Equal(true, coverage.Value<bool>("scopeComplete")); Check.Equal(0, warnings.Count);
            Check.Equal(3, rows.Count(r => r.Value<string>("status") == "notExposed"));
            return Task.CompletedTask;
        }

        // Every role/target pair maps to a V21 Connect overload or refuses with a typed code before any native call.
        internal static async Task TargetFormsFollowV21Overloads()
        {
            JObject P(string json) => JObject.Parse(json);
            var targets = new Dictionary<string, string>
            {
                ["hardware"] = "{hardwarePath:'/d'}", ["channel"] = "{hardwarePath:'/d',channelType:'Technology',channelIoType:'Complex',channelNumber:0}",
                ["channelIndex"] = "{hardwarePath:'/d',channelIndex:1}", ["tag"] = "{tagName:'T'}", ["modules"] = "{inputHardwarePath:'/i',outputHardwarePath:'/o'}",
                ["dbMember"] = "{dbMemberPath:'DB.Tel'}",
            };
            var accepted = new Dictionary<string, string[]>
            {
                ["actor"] = ["hardware", "channel", "tag", "modules", "dbMember"], ["sensor"] = ["hardware", "channel", "tag", "modules", "dbMember"],
                ["encoder"] = ["hardware", "channel", "tag", "modules", "dbMember"], ["torque"] = ["hardware", "modules", "dbMember"],
                ["measuringInput"] = ["channel", "channelIndex"], ["outputCam"] = ["channel", "tag"],
            };
            foreach (var role in accepted)
                foreach (var target in targets)
                {
                    if (role.Value.Contains(target.Key)) { Check.Equal(target.Key, OpennessSession.TechnologyTargetForm(role.Key, P(target.Value))); continue; }
                    var refused = await Check.ThrowsAsync<BridgeException>(() => { OpennessSession.TechnologyTargetForm(role.Key, P(target.Value)); return Task.CompletedTask; });
                    Check.Equal("UnsupportedTarget", refused.Code);
                }
            // Address overloads: both addresses for axis/encoder/torque, one direction for a measuring input or an output cam.
            Check.Equal("addresses", OpennessSession.TechnologyTargetForm("actor", P("{inputAddress:256,outputAddress:256,connectOption:'AllowAllModules'}")));
            Check.Equal("addresses", OpennessSession.TechnologyTargetForm("measuringInput", P("{inputAddress:8}")));
            Check.Equal("addresses", OpennessSession.TechnologyTargetForm("outputCam", P("{outputAddress:8}")));
            Check.Equal("modules", OpennessSession.TechnologyTargetForm("torque", P("{inputHardwarePath:'/i',outputHardwarePath:'/o',connectOption:'Default'}")));
            foreach (var (role, json, code) in new[]
            {
                ("actor", "{inputAddress:256}", "InvalidRequest"), ("measuringInput", "{inputAddress:8,outputAddress:8}", "InvalidRequest"), ("outputCam", "{inputAddress:8}", "InvalidRequest"),
                ("actor", "{}", "InvalidRequest"), ("actor", "{hardwarePath:'/d',tagName:'T'}", "InvalidRequest"), ("actor", "{inputHardwarePath:'/i'}", "InvalidRequest"),
                ("actor", "{channelNumber:0}", "InvalidRequest"), ("measuringInput", "{hardwarePath:'/d',channelNumber:0,channelIndex:1}", "InvalidRequest"),
                ("actor", "{hardwarePath:'/d',connectOption:'AllowAllModules'}", "UnsupportedTarget"), ("outputCam", "{outputAddress:8,connectOption:'Default'}", "UnsupportedTarget"),
                ("cam", "{tagName:'T'}", "InvalidRequest"),
            })
            {
                var refused = await Check.ThrowsAsync<BridgeException>(() => { OpennessSession.TechnologyTargetForm(role, P(json)); return Task.CompletedTask; });
                Check.Equal(code, refused.Code);
            }
        }

        internal static Task AttributeFailuresKeepFieldEvidence()
        {
            foreach (string failure in new[] { "excluded", "metadata", "duplicate", "getter", "notReadable" })
            {
                var connection = new AxisEncoderHardwareConnectionInterface();
                connection.Metadata.Add(new(failure == "excluded" ? "Password" : "Scale", failure == "notReadable" ? EngineeringAttributeAccessMode.Write : EngineeringAttributeAccessMode.Read));
                if (failure == "duplicate") connection.Metadata.Add(new("Scale", EngineeringAttributeAccessMode.Read));
                if (failure == "metadata") connection.MetadataRead = () => throw new Exception("SECRET");
                if (failure == "getter") connection.Read = name => name == "Scale" ? throw new Exception("SECRET") : name == "isConnected" ? false : DayOfWeek.Monday;
                var encoder = new EncoderHardwareConnectionProvider { SensorInterface = connection };
                var owner = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(EncoderHardwareConnectionProvider) ? encoder : null };
                var rows = OpennessSession.ConnectionCoverageForTest(owner, 100, out var warnings, out var coverage, out _, out _);
                Check.Equal("captured", (string?)rows[0]!["fieldStatus"]?["isConnected"]); Check.Equal("incomplete", (string?)rows[0]!["fieldStatus"]?["attributes"]);
                Check.Equal(false, coverage.Value<bool>("scopeComplete")); Check.Equal(false, warnings.ToString().Contains("SECRET"));
                if (failure != "getter") Check.Equal(2, connection.Reads.Count);
            }
            return Task.CompletedTask;
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JArray ConnectionsForTest(TechnologicalInstanceDB owner, int limit, out JArray warnings, out bool truncated)
            => ConnectionCoverageForTest(owner, limit, out warnings, out _, out truncated, out _);

        internal static JArray ConnectionCoverageForTest(TechnologicalInstanceDB owner, int limit, out JArray warnings, out JObject coverage, out bool truncated, out int used)
        {
            var budget = new ScanBudget(limit); warnings = new();
            var rows = TechnologyConnections(owner, budget, warnings, "/PLC/technology/Axis", out coverage);
            truncated = budget.Truncated; used = budget.Used; return rows;
        }
    }
}
// Compile the production reader against synthetic shapes; this does not verify the Siemens SDK ABI.
namespace Siemens.Engineering.SW.TechnologicalObjects.Motion
{
    internal class AxisEncoderHardwareConnectionInterface : IEngineeringObject
    {
        internal Func<string, object?>? Read;
        internal readonly List<string> Reads = [];
        internal List<SyntheticAttribute> Metadata = [];
        internal Func<IEnumerable<SyntheticAttribute>?>? MetadataRead;
        internal object? Field(string name, object? fallback) { Reads.Add(name); return Read is null ? fallback : Read(name); }
        public bool IsConnected => (bool)Field("isConnected", false)!;
        public DayOfWeek ConnectOption => (DayOfWeek)Field("connectOption", DayOfWeek.Monday)!;
        public IEnumerable<SyntheticAttribute>? GetAttributeInfos() => MetadataRead is null ? Metadata : MetadataRead();
        public object? GetAttribute(string name) => Field(name, 1);
        public void SetAttribute(string name, object value) => throw new Exception("No writes expected");
    }
    internal sealed class AxisHardwareConnectionProvider
    {
        internal AxisEncoderHardwareConnectionInterface? Actor;
        internal IEnumerable<AxisEncoderHardwareConnectionInterface>? Sensors = [];
        internal IEngineeringObject? Torque;
        internal Func<string, object?>? Read;
        internal List<string> Reads = [];
        private object? Field(string name, object? fallback) { Reads.Add(name); return Read is null ? fallback : Read(name); }
        public AxisEncoderHardwareConnectionInterface? ActorInterface => (AxisEncoderHardwareConnectionInterface?)Field("actor", Actor);
        public IEnumerable<AxisEncoderHardwareConnectionInterface>? SensorInterface => (IEnumerable<AxisEncoderHardwareConnectionInterface>?)Field("sensors", Sensors);
        public IEngineeringObject? TorqueInterface => (IEngineeringObject?)Field("torque", Torque);
    }
    internal sealed class EncoderHardwareConnectionProvider { public AxisEncoderHardwareConnectionInterface? SensorInterface { get; set; } }
    internal class MeasuringInputHardwareConnectionProvider
    {
        internal Func<string, object?>? Read;
        internal List<string> Reads = [];
        private object? Field(string name, object? fallback) { Reads.Add(name); return Read is null ? fallback : Read(name); }
        public bool IsConnected => (bool)Field("isConnected", false)!;
        public object? InputAddress => Field("inputAddress", 0);
        public object? InputModule => Field("inputModule", null);
        public object? Channel => Field("channel", null);
        public object? ChannelIndex => Field("channelIndex", 0);
    }
    internal class OutputCamHardwareConnectionProvider
    {
        internal Func<string, object?>? Read;
        internal List<string> Reads = [];
        private object? Field(string name, object? fallback) { Reads.Add(name); return Read is null ? fallback : Read(name); }
        public bool IsConnected => (bool)Field("isConnected", false)!;
        public object? OutputAddress => Field("outputAddress", 0);
        public object? OutputTag => Field("outputTag", null);
        public object? Channel => Field("channel", null);
    }
}
