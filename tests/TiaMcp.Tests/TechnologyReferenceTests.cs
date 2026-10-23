using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class TechnologyReferenceTests
    {
        private static Project ProjectFor(params (IEngineeringObject Target, string Id)[] entries)
        {
            var hardware = entries.Select(entry => entry.Target).OfType<DeviceItem>().ToArray();
            foreach (var parent in hardware) parent.DeviceItems = hardware.Where(item => ReferenceEquals(item.Container, parent)).ToArray();
            return new Project { Devices = [new Device { Name = "Station", DeviceItems = hardware.Where(item => item.Container is not DeviceItem parent || !hardware.Contains(parent)).ToArray() }], Provider = new ObjectIdentifierProvider {
                Identify = target => entries.Single(e => ReferenceEquals(e.Target, target)).Id,
                FindObject = id => entries.First(e => e.Id == id).Target } };
        }

        internal static Task ExactIdentifiers()
        {
            foreach (IEngineeringObject item in new IEngineeringObject[] { new DeviceItem(), new PlcTag(), new PlcBlock(), new PlcUnitBase() })
            {
                var project = ProjectFor((item, "native-id"));
                var value = OpennessSession.ReferenceForTest(project, item, out bool complete, out var coverage);
                Check.Equal(true, complete); Check.Equal("native-id", (string?)value["nativeIdentifier"]);
                Check.Equal("engineeringReference", (string?)value["valueSemantics"]); Check.Equal(false, (bool)value["persistenceVerified"]!);
                Check.Equal(project.Path!.FullName, (string?)value["projectPath"]); Check.Equal("selected-plc", (string?)value["selectionPlcPath"]);
                Check.Equal("available", coverage.Value<string>("providerStatus")); Check.Equal(3, project.Provider!.Identifiers); Check.Equal(1, project.Provider.Finds);
            }
            var a = new PlcTag { Name = "same" }; var b = new PlcTag { Name = "same" };
            var p = ProjectFor((a, "A"), (b, "B"));
            var values = OpennessSession.ReferenceForTest(p, new object[] { a, b }, out bool ok, out _);
            Check.Equal(true, ok); Check.Equal("A", (string?)values[0]!["nativeIdentifier"]); Check.Equal("B", (string?)values[1]!["nativeIdentifier"]);
            p.Provider!.FindObject = _ => b;
            values = OpennessSession.ReferenceForTest(p, a, out ok, out _);
            Check.Equal(false, ok); Check.Equal("roundTripMismatch", (string?)values["reason"]);
            return Task.CompletedTask;
        }

        internal static Task HardwareMembership()
        {
            var module = new DeviceItem { Name = "Module/日本%" };
            var owner = new DeviceItem { Name = "Channel", Container = module };
            var project = ProjectFor((owner, "owner"), (module, "module"));
            project.Name = "P/Ω";
            var value = OpennessSession.ReferenceForTest(project, owner, out bool complete, out var coverage);
            Check.Equal(true, complete);
            Check.Equal("/projects/P%2F%CE%A9/devices/Station/items/Module%2F%E6%97%A5%E6%9C%AC%25/items/Channel", (string?)value["hardwarePath"]);
            Check.Equal("boundedProjectHardwareMembership", (string?)value["hardwarePathBasis"]);
            Check.Equal("complete", coverage.Value<string>("hardwareMembershipStatus")); Check.Equal(2, coverage.Value<int>("hardwareItemsVisited"));
            // Provider roundtrip alone cannot establish membership in the selected project's hardware tree.
            project.Devices = [];
            value = OpennessSession.ReferenceForTest(project, owner, out complete, out coverage);
            Check.Equal(false, complete); Check.Equal("hardwareNotInProject", (string?)value["reason"]);
            return Task.CompletedTask;
        }

        internal static Task AmbiguousHardwareMembership()
        {
            var item = new DeviceItem(); var project = ProjectFor((item, "id"));
            foreach (string defect in new[] { "duplicate", "cycle", "sameName" })
            {
                item.DeviceItems = []; var device = project.Devices.Single(); device.DeviceItems = [item];
                if (defect == "duplicate") device.DeviceItems = [item, item];
                if (defect == "cycle") item.DeviceItems = [item];
                if (defect == "sameName") device.DeviceItems = [item, new DeviceItem { Name = item.Name }];
                var value = OpennessSession.ReferenceForTest(project, item, out bool complete, out var coverage);
                Check.Equal(false, complete); Check.Equal("readError", coverage.Value<string>("hardwareMembershipStatus"));
                Check.Equal(false, value.Value<bool>("identityComplete"));
            }
            return Task.CompletedTask;
        }

        internal static Task FailedProvidersAndIdentityText()
        {
            foreach (string failure in new[] { "unavailable", "throw", "empty", "oversize", "unicode", "nullFind", "drift", "path" })
            {
                var item = new DeviceItem(); var p = ProjectFor((item, "ID"));
                if (failure == "unavailable") p.Provider = null;
                if (failure == "throw") p.Read = () => throw new Exception("SECRET");
                if (failure == "empty") p.Provider!.Identify = _ => "";
                if (failure == "oversize") p.Provider!.Identify = _ => new string('x', 4097);
                if (failure == "unicode") p.Provider!.Identify = _ => "bad\uD800";
                if (failure == "nullFind") p.Provider!.FindObject = _ => null;
                if (failure == "drift") p.Provider!.Identify = _ => p.Provider.Identifiers == 1 ? "ID" : "changed";
                if (failure == "path") p.Path = null;
                var value = OpennessSession.ReferenceForTest(p, new object[] { item, item }, out bool complete, out _);
                Check.Equal(false, complete); Check.Equal(false, value.ToString().Contains("SECRET"));
                Check.Equal(true, value.All(v => v.Value<bool>("identityComplete") == false));
                Check.Equal(failure == "path" ? 0 : 1, p.ServiceReads);
                if (failure is "empty" or "oversize" or "unicode") Check.Equal(0, p.Provider!.Finds);
            }
            return Task.CompletedTask;
        }

        internal static Task ChannelOwnership()
        {
            var module = new DeviceItem { Name = "module" }; var owner = new DeviceItem { Name = "channel owner", Container = module };
            var channel = new Channel { Number = 3, Parent = owner, Type = DayOfWeek.Monday, IoType = DayOfWeek.Friday };
            owner.Channels = [new Channel { Number = 4 }, channel];
            var p = ProjectFor((owner, "owner-id"), (module, "module-id"));
            var value = OpennessSession.ReferenceForTest(p, channel, out bool complete, out _);
            Check.Equal(true, complete); Check.Equal("channel", (string?)value["kind"]); Check.Equal(JTokenType.Null, value["nativeIdentifier"]!.Type);
            Check.Equal("owner-id", (string?)value["parent"]?["nativeIdentifier"]); Check.Equal("module-id", (string?)value["parent"]?["container"]?["nativeIdentifier"]);
            Check.Equal(3, (int)value["number"]!); Check.Equal("Monday", (string?)value["type"]); Check.Equal("Friday", (string?)value["ioType"]);
            foreach (string failure in new[] { "missing", "nullCollection", "nullEntry", "alias", "duplicateSelector", "foreignParent", "cycle" })
            {
                channel.Parent = owner; owner.Container = module; owner.Channels = [channel];
                if (failure == "missing") owner.Channels = [];
                if (failure == "nullCollection") owner.Channels = null;
                if (failure == "nullEntry") owner.Channels = [channel, null!];
                if (failure == "alias") owner.Channels = [channel, channel];
                if (failure == "duplicateSelector") owner.Channels = [channel, new Channel { Parent = owner, Number = 3, Type = channel.Type, IoType = channel.IoType }];
                if (failure == "foreignParent") channel.Parent = module;
                if (failure == "cycle") owner.Container = owner;
                value = OpennessSession.ReferenceForTest(p, channel, out complete, out _);
                Check.Equal(false, complete); Check.Equal(false, value.Value<bool>("identityComplete"));
            }
            return Task.CompletedTask;
        }

        internal static Task BoundsAndDisposal()
        {
            var owner = new DeviceItem(); var channel = new Channel { Parent = owner }; var p = ProjectFor((owner, "owner"));
            int disposed = 0, visited = 0;
            IEnumerable<Channel> Endless()
            {
                try { while (true) { visited++; yield return new Channel { Number = visited }; } }
                finally { disposed++; }
            }
            owner.Channels = Endless();
            var value = OpennessSession.ReferenceForTest(p, channel, out bool complete, out var coverage, 40);
            Check.Equal(false, complete); Check.Equal(true, coverage.Value<bool>("truncated")); Check.Equal(40, coverage.Value<int>("visitedReads"));
            Check.Equal(1, disposed); Check.Equal(true, visited < 40); Check.Equal(40, coverage.Value<int>("maximumReads"));
            p = ProjectFor((owner, "owner"));
            OpennessSession.ReferenceForTest(p, owner, out complete, out coverage, 0);
            Check.Equal(false, complete); Check.Equal(0, p.ServiceReads); Check.Equal(0, p.Provider!.Identifiers);
            IEnumerable<Channel> Broken() { yield return channel; throw new Exception("SECRET"); }
            owner.Channels = Broken(); value = OpennessSession.ReferenceForTest(p, channel, out complete, out coverage);
            Check.Equal(false, complete); Check.Equal(false, value.ToString().Contains("SECRET"));
            return Task.CompletedTask;
        }

        internal static Task IdentifierCollisions()
        {
            var a = new PlcTag(); var b = new PlcTag(); IEngineeringObject? current = null;
            var p = new Project { Provider = new ObjectIdentifierProvider { Identify = target => { current = target; return "same-id"; }, FindObject = _ => current } };
            var values = OpennessSession.ReferenceForTest(p, new object[] { a, b }, out bool complete, out var coverage);
            Check.Equal(false, complete); Check.Equal("identifierCollision", (string?)values[1]?["reason"]); Check.Equal(true, coverage.Value<bool>("identifierCollisionDetected"));
            return Task.CompletedTask;
        }

        internal static Task ConnectionIntegration()
        {
            var module = new DeviceItem { Name = "module" }; var owner = new DeviceItem { Container = module };
            var channel = new Channel { Parent = owner }; owner.Channels = [channel];
            var p = ProjectFor((owner, "owner"), (module, "module"));
            var encoder = new AxisEncoderHardwareConnectionInterface { Read = name => name == "Channel" ? channel : name == "isConnected" ? true : DayOfWeek.Monday };
            encoder.Metadata.Add(new("Channel", EngineeringAttributeAccessMode.Read));
            var technology = new TechnologicalInstanceDB([]) { TypedService = type => type == typeof(EncoderHardwareConnectionProvider) ? new EncoderHardwareConnectionProvider { SensorInterface = encoder } : null };
            var rows = OpennessSession.ConnectionReferencesForTest(p, technology, out var warnings, out var coverage);
            Check.Equal(true, coverage.Value<bool>("scopeComplete")); Check.Equal(0, warnings.Count);
            Check.Equal(true, rows[0]!["attributes"]![0]!.Value<bool>("valueComplete"));
            Check.Equal("owner", (string?)rows[0]!["attributes"]![0]!["value"]?["parent"]?["nativeIdentifier"]);
            p.Provider = null;
            rows = OpennessSession.ConnectionReferencesForTest(p, technology, out warnings, out coverage);
            Check.Equal(false, coverage.Value<bool>("scopeComplete")); Check.Equal(true, warnings.Count > 0);
            Check.Equal("incomplete", (string?)rows[0]!["fieldStatus"]?["attributes"]);
            return Task.CompletedTask;
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JToken ReferenceForTest(Project project, object value, out bool complete, out JObject coverage, int maximum = 50000)
        {
            var reader = new TechnologyReferenceReader(project, "selected-plc", maximum);
            var result = reader.Render(value, out complete); coverage = reader.Coverage(); return result;
        }
        internal static JArray ConnectionReferencesForTest(Project project, TechnologicalInstanceDB technology, out JArray warnings, out JObject coverage)
        {
            warnings = new();
            return TechnologyConnections(technology, new ScanBudget(100), warnings, "selected-plc/technology/Axis", out coverage, new TechnologyReferenceReader(project, "selected-plc"));
        }
    }
}
// Synthetic API signatures exercise the real reader, without compiling or invoking the Siemens SDK.
namespace Siemens.Engineering
{
    internal sealed class Project
    {
        internal string Name = "Fixture";
        internal IEnumerable<HW.Device> Devices = [];
        internal HW.DeviceUserGroup UngroupedDevicesGroup = new();
        internal IEnumerable<HW.DeviceUserGroup> DeviceGroups = [];
        internal FileInfo? Path = new("D:/fixture/reference.ap21");
        internal ObjectIdentifierProvider? Provider;
        internal Func<object?>? Read;
        internal int ServiceReads;
        internal T? GetService<T>() where T : class { ServiceReads++; return (Read is null ? Provider : Read()) as T; }
    }
    internal sealed class ObjectIdentifierProvider
    {
        internal Func<IEngineeringObject, string> Identify = _ => throw new Exception("No identifier");
        internal Func<string, IEngineeringObject?> FindObject = _ => null;
        internal int Identifiers, Finds;
        internal string GetIdentifier(IEngineeringObject target) { Identifiers++; return Identify(target); }
        internal IEngineeringObject? Find(string id) { Finds++; return FindObject(id); }
    }
}
