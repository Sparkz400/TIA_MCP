using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class TechnologyReadTests
    {
        private const EngineeringAttributeAccessMode RW = EngineeringAttributeAccessMode.Read | EngineeringAttributeAccessMode.Write;
        private static TechnologicalParameter Parameter(string name = "Scale", object? value = null) => new(name, value ?? 2);

        internal static Task AccessAndExclusions()
        {
            foreach (string name in new[] { "Password", "Nested.CREDENTIAL", "PrivateKey", "SecretValue", "Token" })
            {
                var p = Parameter(name); p.Read = _ => throw new Exception("SECRET"); p.Metadata = _ => throw new Exception("SECRET");
                var row = OpennessSession.ReadParameterForTest(p);
                Check.Equal("excluded", (string?)row["valueStatus"]); Check.Equal("<excluded>", (string?)row["value"]);
                Check.Equal(false, row.Value<bool>("valueComplete")); Check.Equal(0, p.Reads); Check.Equal(0, p.MetadataReads);
            }
            foreach (string fault in new[] { "absent", "null", "duplicate", "wrongCase", "none", "writeOnly", "metadataThrow", "typesThrow", "typeLimit" })
            {
                var p = Parameter(); string status = "unknownAccess";
                if (fault == "absent") p.Attributes.Clear();
                if (fault == "null") p.Metadata = _ => null;
                if (fault == "duplicate") p.Attributes.Add(new("Value", RW));
                if (fault == "wrongCase") p.Attributes[0].Name = "value";
                if (fault == "none") { p.Attributes[0].AccessMode = 0; status = "notReadable"; }
                if (fault == "writeOnly") { p.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write; status = "notReadable"; }
                if (fault == "metadataThrow") { p.Metadata = _ => throw new Exception("SECRET"); status = "metadataError"; }
                if (fault == "typesThrow") { p.Attributes[0].SupportedTypes = ThrowTypes(); status = "metadataError"; }
                if (fault == "typeLimit") { p.Attributes[0].SupportedTypes = Enumerable.Repeat(typeof(int), 65); status = "metadataLimit"; }
                var row = OpennessSession.ReadParameterForTest(p);
                Check.Equal(status, (string?)row["valueStatus"]); Check.Equal(0, p.Reads); Check.Equal(0, p.Writes);
                Check.Equal(false, row.Value<bool>("valueCaptured")); Check.Equal(true, row["value"] == null); Check.Equal(true, !row.ToString().Contains("SECRET"));
                Check.Equal(1, OpennessSession.ParameterWarningsForTest(new(row)).Count);
            }
            var readOnly = Parameter(); readOnly.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read;
            var observed = OpennessSession.ReadParameterForTest(readOnly);
            Check.Equal("captured", (string?)observed["valueStatus"]); Check.Equal(true, observed.Value<bool>("readable"));
            Check.Equal(false, observed.Value<bool>("writable")); Check.Equal(1, readOnly.Reads); Check.Equal(0, readOnly.Writes);
            return Task.CompletedTask;
        }

        private static IEnumerable<Type> ThrowTypes() { yield return typeof(int); throw new Exception("SECRET"); }
        private enum Choice { First, Second }
        internal static Task ValuesAndErrors()
        {
            foreach (object? value in new object?[] { null, false, (byte)2, (sbyte)-2, (short)-2, (ushort)2, 2, 2U, 9007199254740993L,
                ulong.MaxValue, 2.5F, 2.5D, 2.5M, "日本語\r\n😀", new string('x', 4097), Choice.Second })
            {
                var p = Parameter(); p.Stored = value; p.Attributes[0].SupportedTypes = [typeof(int), typeof(string)];
                var row = OpennessSession.ReadParameterForTest(p);
                Check.Equal(value?.GetType().FullName, (string?)row["type"]); Check.Equal(true, row.Value<bool>("valueCaptured"));
                Check.Equal(true, row.Value<bool>("valueComplete")); Check.Equal(2, ((JArray)row["supportedTypes"]!).Count);
                var expected = value is Choice ? new JValue(value.ToString()) : value == null ? JValue.CreateNull() : JToken.FromObject(value);
                Check.Equal(true, JToken.DeepEquals(expected, row["value"])); Check.Equal(1, p.Reads); Check.Equal(0, p.Writes);
            }
            foreach (object value in new object[] { new byte[] { 1, 2 }, DateTime.UnixEpoch, double.NaN, float.PositiveInfinity, new object() })
            {
                var row = OpennessSession.ReadParameterForTest(Parameter(value: value));
                Check.Equal(true, row.Value<bool>("valueCaptured")); Check.Equal(false, row.Value<bool>("valueComplete")); Check.Equal("displayOnly", (string?)row["valueStatus"]);
            }
            var truncated = OpennessSession.ReadParameterRenderedForTest(Parameter(value: new string('x', 4097)), _ => new JObject { ["text"] = "x", ["truncated"] = true });
            Check.Equal(false, truncated.Value<bool>("valueComplete")); Check.Equal("displayOnly", (string?)truncated["valueStatus"]);
            var readFailure = Parameter(); readFailure.Read = _ => throw new Exception("SECRET");
            foreach (var row in new[] { OpennessSession.ReadParameterForTest(readFailure),
                OpennessSession.ReadParameterRenderedForTest(Parameter(), _ => throw new Exception("SECRET")),
                OpennessSession.ReadParameterRenderedForTest(Parameter(), _ => null!) })
            {
                Check.Equal("readError", (string?)row["valueStatus"]); Check.Equal(false, row.Value<bool>("valueComplete"));
                Check.Equal(false, row.Value<bool>("valueCaptured")); Check.Equal(true, row["value"] == null); Check.Equal(true, !row.ToString().Contains("SECRET"));
            }
            return Task.CompletedTask;
        }

        internal static Task BoundedCapture()
        {
            var filtered = Parameter("Other"); var matching = Parameter("Axis.Scale"); int admitted = 0;
            var rows = OpennessSession.CaptureParametersForTest([filtered, matching], "Axis.", () => ++admitted <= 1, out bool truncated, out int visited);
            Check.Equal(2, visited); Check.Equal(false, truncated); Check.Equal(1, rows.Count); Check.Equal(0, filtered.MetadataReads); Check.Equal(0, filtered.Reads);
            var later = Parameter("Axis.Peer"); admitted = 0;
            rows = OpennessSession.CaptureParametersForTest([matching, later], "Axis.", () => ++admitted <= 1, out truncated, out visited);
            Check.Equal(1, rows.Count); Check.Equal(2, admitted); Check.Equal(0, later.Reads);
            int enumerated = 0;
            IEnumerable<TechnologicalParameter> Many(int count)
            { for (int i = 0; i < count; i++) { enumerated++; yield return filtered; } }
            rows = OpennessSession.CaptureParametersForTest(Many(50001), "Axis.", () => throw new Exception("No matching row"), out truncated, out visited);
            Check.Equal(50000, visited); Check.Equal(50001, enumerated); Check.Equal(true, truncated); Check.Equal(0, rows.Count); Check.Equal(0, filtered.Reads);
            rows = OpennessSession.CaptureParametersForTest(Many(50000), "Axis.", () => true, out truncated, out visited);
            Check.Equal(false, truncated); Check.Equal(50000, visited);
            rows = OpennessSession.CaptureParametersForTest([], null, () => throw new Exception(), out truncated, out visited);
            Check.Equal(0, visited); Check.Equal(false, truncated); Check.Equal(0, rows.Count);
            return Task.CompletedTask;
        }

        internal static Task Completeness()
        {
            var good = OpennessSession.ReadParameterForTest(Parameter());
            var excluded = OpennessSession.ReadParameterForTest(Parameter("Secret"));
            Check.Equal(true, OpennessSession.ParametersCompleteForTest(new(good)));
            Check.Equal(false, OpennessSession.ParametersCompleteForTest(new(good, excluded)));
            Check.Equal(false, OpennessSession.ParametersCompleteForTest(new(new JObject { ["name"] = "Missing", ["unresolved"] = true })));
            Check.Equal(false, OpennessSession.ParametersCompleteForTest(new(good, good.DeepClone())));
            Check.Equal(1, OpennessSession.ParameterWarningsForTest(new(good, good.DeepClone())).Count);
            var missingName = (JObject)good.DeepClone(); missingName.Remove("name");
            Check.Equal(false, OpennessSession.ParametersCompleteForTest(new(missingName)));
            Check.Equal(1, OpennessSession.ParameterWarningsForTest(new(missingName)).Count);
            var warnings = OpennessSession.ParameterWarningsForTest(new(good, excluded));
            Check.Equal(1, warnings.Count); Check.Equal("Secret", (string?)warnings[0]!["path"]);
            Check.Equal("captured", (string?)good["valueStatus"]); Check.Equal(true, excluded.Parent != warnings);
            return Task.CompletedTask;
        }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JObject ReadParameterForTest(TechnologicalParameter parameter) => EngineeringParameterInfo(parameter);
        internal static JObject ReadParameterRenderedForTest(TechnologicalParameter parameter, Func<object, JToken> render) => TechnologyParameterInfo(parameter, render);
        internal static JArray CaptureParametersForTest(IEnumerable<TechnologicalParameter> parameters, string? prefix, Func<bool> take, out bool truncated, out int visited)
            => CaptureTechnologyParameters(parameters, prefix, take, ChangeScalar, out truncated, out visited);
        internal static bool ParametersCompleteForTest(JArray rows) => TechnologyParametersComplete(rows);
        internal static JArray ParameterWarningsForTest(JArray rows) => TechnologyParameterWarnings(rows);
    }
}
