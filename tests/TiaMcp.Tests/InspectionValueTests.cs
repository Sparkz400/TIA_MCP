using System.Collections;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class InspectionValueTests
    {
        private sealed class Opaque
        {
            internal int Reads;
            public override string ToString() { Reads++; return "SECRET"; }
        }

        internal static Task SensitiveGetters()
        {
            var target = new AttributeWriteTests.Target(); target.Attributes.Clear();
            foreach (string name in new[] { "Password", "Nested.CREDENTIAL", "PrivateKey", "SecretValue", "Token" })
                target.Attributes.Add(new(name, EngineeringAttributeAccessMode.Read));
            target.Read = _ => throw new Exception("SECRET");
            var rows = OpennessSession.ReadAttributesForTest(target, 20, out var warnings, out _);
            Check.Equal(0, target.Reads); Check.Equal(5, rows.Count); Check.Equal(5, warnings.Count);
            foreach (var row in rows) { Check.Equal("excluded", row.Value<string>("valueStatus")); Check.Equal(false, row.Value<bool>("valueComplete")); }
            Check.Equal(false, warnings.ToString().Contains("SECRET"));
            return Task.CompletedTask;
        }

        internal static Task OpaqueValues()
        {
            var opaque = new Opaque(); var value = OpennessSession.ReadValueForTest(opaque);
            Check.Equal(0, opaque.Reads); Check.Equal(false, value.Value<bool>("valueComplete"));
            Check.Equal(false, value.ToString().Contains("SECRET"));
            return Task.CompletedTask;
        }

        internal static Task DuplicateMetadata()
        {
            var t = new AttributeWriteTests.Target(); t.Attributes.Add(new("First", EngineeringAttributeAccessMode.Read));
            var rows = OpennessSession.ReadAttributesForTest(t, 1, out var warnings, out bool truncated);
            Check.Equal(0, t.Reads); Check.Equal("ambiguousIdentity", rows[0]!.Value<string>("valueStatus"));
            Check.Equal(1, warnings.Count); Check.Equal(true, truncated);
            return Task.CompletedTask;
        }

        internal static Task Scalars()
        {
            foreach (object? scalar in new object?[] { null, "", "text", true, (byte)1, (sbyte)-1, (short)-2, (ushort)2, -3, 3u, long.MinValue, ulong.MaxValue, 1.25f, 1.25d, 1.25m })
            {
                var value = OpennessSession.ReadValueCompleteForTest(scalar, out bool complete);
                Check.Equal(true, complete); Check.Equal(true, JToken.DeepEquals(new JValue(scalar), value));
            }
            Check.Equal("Friday", (string?)OpennessSession.ReadValueForTest(DayOfWeek.Friday));
            Check.Equal("10.0", (string?)OpennessSession.ReadValueForTest(new Version(10, 0)));
            foreach (object number in new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var value = OpennessSession.ReadValueCompleteForTest(number, out bool complete);
                Check.Equal(false, complete); Check.Equal("nonFinite", value.Value<string>("valueStatus"));
                JToken.Parse(value.ToString());
            }
            var t = new AttributeWriteTests.Target(); t.Values["First"] = ulong.MaxValue; t.Values["Second"] = null; t.Values["Third"] = DayOfWeek.Friday;
            var rows = OpennessSession.ReadAttributesForTest(t, 3, out var warnings, out var truncated);
            Check.Equal(0, warnings.Count); Check.Equal(false, truncated); Check.Equal(3, t.Reads);
            Check.Equal("System.UInt64", rows[0]!.Value<string>("type"));
            foreach (var row in rows) { Check.Equal(true, row.Value<bool>("valueComplete")); Check.Equal(true, row.Value<bool>("readable")); }
            return Task.CompletedTask;
        }

        internal static Task MetadataFailures()
        {
            foreach (string fault in new[] { "null", "emptyName", "nullName", "longName", "nullEntry", "throw", "limit" })
            {
                var t = new AttributeWriteTests.Target();
                if (fault == "null") t.Metadata = () => null;
                if (fault == "emptyName") t.Attributes[0].Name = "";
                if (fault == "nullName") t.Attributes[0].Name = null!;
                if (fault == "longName") t.Attributes[0].Name = new string('a', 4097);
                if (fault == "nullEntry") t.Attributes.Add(null!);
                if (fault == "throw") t.Metadata = () => throw new Exception("SECRET");
                int enumerated = 0;
                IEnumerable<SyntheticAttribute> Endless()
                {
                    while (true) { if (++enumerated > 4097) throw new Exception("unbounded"); yield return new("Name" + enumerated, EngineeringAttributeAccessMode.Read); }
                }
                if (fault == "limit") t.Metadata = Endless;
                var rows = OpennessSession.ReadAttributesForTest(t, 1, out var warnings, out bool truncated);
                Check.Equal(0, t.Reads); Check.Equal(0, rows.Count); Check.Equal(1, warnings.Count);
                Check.Equal(fault == "limit", truncated); Check.Equal(false, warnings.ToString().Contains("SECRET"));
                if (fault == "limit") Check.Equal(4097, enumerated);
            }
            var empty = new AttributeWriteTests.Target(); empty.Attributes.Clear();
            Check.Equal(0, OpennessSession.ReadAttributesForTest(empty, 1, out var w, out _).Count); Check.Equal(0, w.Count);
            return Task.CompletedTask;
        }

        internal static Task IncompleteAttributes()
        {
            foreach (string fault in new[] { "writeOnly", "noAccess", "throw", "opaque", "long", "nonFinite", "native", "enumeration" })
            {
                var t = new AttributeWriteTests.Target(); t.Attributes.RemoveRange(1, 2);
                if (fault == "writeOnly") t.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write;
                if (fault == "noAccess") t.Attributes[0].AccessMode = 0;
                if (fault == "throw") t.Read = _ => throw new Exception("SECRET");
                if (fault == "opaque") t.Values["First"] = new Opaque();
                if (fault == "long") t.Values["First"] = new string('a', 4097);
                if (fault == "nonFinite") t.Values["First"] = double.NaN;
                if (fault == "native") t.Values["First"] = new Siemens.Engineering.HW.DeviceItem();
                if (fault == "enumeration") t.Values["First"] = Broken();
                var rows = OpennessSession.ReadAttributesForTest(t, 1, out var warnings, out _);
                Check.Equal(1, rows.Count); Check.Equal(1, warnings.Count); Check.Equal(false, rows[0]!.Value<bool>("valueComplete"));
                Check.Equal(false, rows.ToString().Contains("SECRET")); Check.Equal(false, warnings.ToString().Contains("SECRET"));
                Check.Equal(fault is "writeOnly" or "noAccess" ? 0 : 1, t.Reads);
            }
            return Task.CompletedTask;
        }

        private static IEnumerable Broken() { yield return 7; throw new Exception("SECRET"); }

        internal static Task CollectionBounds()
        {
            var exact = OpennessSession.ReadValueCompleteForTest(Enumerable.Range(0, 64), out bool complete);
            Check.Equal(true, complete); Check.Equal(64, ((JArray)exact).Count);
            int visits = 0, disposals = 0;
            IEnumerable Infinite()
            {
                try { while (true) { visits++; yield return 1; } }
                finally { disposals++; }
            }
            var limited = OpennessSession.ReadValueCompleteForTest(Infinite(), out complete);
            Check.Equal(false, complete); Check.Equal(65, visits); Check.Equal(1, disposals); Check.Equal(65, ((JArray)limited).Count);
            var cyclic = new ArrayList(); cyclic.Add(cyclic);
            var cycleValue = OpennessSession.ReadValueCompleteForTest(cyclic, out complete);
            Check.Equal(false, complete); Check.Equal(true, cycleValue.ToString().Contains("traversalLimit"));
            object broad = Enumerable.Range(0, 64).ToArray();
            for (int i = 0; i < 5; i++) broad = Enumerable.Repeat(broad, 64).ToArray();
            var broadValue = OpennessSession.ReadValueCompleteForTest(broad, out complete);
            Check.Equal(false, complete); Check.Equal(true, ((JContainer)broadValue).Descendants().Count() < 650);
            var failed = OpennessSession.ReadValueCompleteForTest(Broken(), out complete);
            Check.Equal(false, complete); Check.Equal(7, (int)failed[0]!); Check.Equal("readError", failed[1]!.Value<string>("valueStatus"));
            Check.Equal(false, failed.ToString().Contains("SECRET"));
            return Task.CompletedTask;
        }

        internal static Task TextBounds()
        {
            var exact = OpennessSession.ReadValueCompleteForTest(new string('a', 4096), out bool complete);
            Check.Equal(true, complete); Check.Equal(4096, ((string)exact!).Length);
            var large = OpennessSession.ReadValueCompleteForTest(new string('a', 4095) + "😀tail", out complete);
            Check.Equal(false, complete); Check.Equal(4095, large.Value<string>("text")!.Length); Check.Equal(4101, large.Value<int>("originalCharacters"));
            var nested = OpennessSession.ReadValueCompleteForTest(new[] { new string('a', 4097) }, out complete);
            Check.Equal(false, complete); Check.Equal(true, nested[0]!.Value<bool>("truncated"));
            return Task.CompletedTask;
        }

        internal static Task NativeProjections()
        {
            var module = new Siemens.Engineering.HW.DeviceItem { Name = "rack" };
            var device = new Siemens.Engineering.HW.DeviceItem { Name = "owner", Container = module };
            var channel = new Siemens.Engineering.HW.Features.Channel { Parent = device, Number = 3 };
            var row = OpennessSession.ReadValueCompleteForTest(channel, out bool complete);
            Check.Equal(false, complete); Check.Equal(false, row.Value<bool>("identityComplete"));
            Check.Equal("owner", (string?)row["parent"]?["name"]); Check.Equal("rack", (string?)row["parent"]?["container"]?["name"]);
            Check.Equal(1, channel.ParentReads);
            channel.Parent = channel;
            row = OpennessSession.ReadValueCompleteForTest(channel, out complete);
            Check.Equal(false, complete); Check.Equal(true, row.ToString().Contains("traversalLimit"));
            foreach (object item in new object[] { device, new Siemens.Engineering.SW.Tags.PlcTag(), new Siemens.Engineering.SW.Blocks.PlcBlock(), new Siemens.Engineering.SW.Units.PlcUnitBase() })
            {
                row = OpennessSession.ReadValueCompleteForTest(item, out complete);
                Check.Equal(false, complete); Check.Equal(false, row.Value<bool>("identityComplete"));
            }
            device.Name = new string('a', 4097); row = OpennessSession.ReadValueForTest(device);
            Check.Equal(true, row["name"]!.Value<bool>("truncated"));
            return Task.CompletedTask;
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JToken ReadValueForTest(object? value) => InspectionValue(value!);
        internal static JToken ReadValueCompleteForTest(object? value, out bool complete) => InspectionValue(value!, out complete);
        internal static JArray ReadAttributesForTest(IEngineeringObject target, int limit, out JArray warnings, out bool truncated)
        {
            warnings = new(); var budget = new ScanBudget(limit);
            var rows = InspectionAttributes(target, budget, warnings, "/fixture"); truncated = budget.Truncated; return rows;
        }
    }
}
// Synthetic native shapes compile the shared renderer, not the Siemens SDK or native workers.
namespace Siemens.Engineering { internal interface IEngineeringInstance { } }
namespace Siemens.Engineering.HW
{
    internal class DeviceItem : IEngineeringInstance, IEngineeringObject
    {
        public string Name { get; set; } = "module";
        public IEnumerable<DeviceItem> DeviceItems { get; set; } = [];
        public int PositionNumber { get; set; }
        public string TypeIdentifier { get; set; } = "type";
        public object? Container { get; set; }
        public IEnumerable<Siemens.Engineering.HW.Features.Channel>? Channels { get; set; } = [];
    }
}
namespace Siemens.Engineering.HW.Features
{
    internal class Channel : IEngineeringInstance
    {
        public int Number { get; set; }
        public DayOfWeek Type { get; set; }
        public DayOfWeek IoType { get; set; }
        private object? parent;
        internal int ParentReads;
        public object? Parent { get { ParentReads++; return parent; } set { parent = value; } }
    }
}
namespace Siemens.Engineering.SW.Tags
{
    internal class PlcTag : IEngineeringInstance, IEngineeringObject { public string Name { get; set; } = "tag"; public string LogicalAddress { get; set; } = "%I0.0"; }
}
namespace Siemens.Engineering.SW.Blocks
{
    internal class PlcBlock : IEngineeringInstance, IEngineeringObject { public string Name { get; set; } = "block"; public int Number { get; set; } }
}
namespace Siemens.Engineering.SW.Units
{
    internal class PlcUnitBase : IEngineeringInstance, IEngineeringObject { public string Name { get; set; } = "unit"; }
}
