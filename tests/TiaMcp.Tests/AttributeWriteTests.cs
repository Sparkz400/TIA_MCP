using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class AttributeWriteTests
    {
        private const EngineeringAttributeAccessMode RW = EngineeringAttributeAccessMode.Read | EngineeringAttributeAccessMode.Write;
        private static readonly HashSet<string> Allowed = new(["First", "Second", "Third"], StringComparer.Ordinal);
        private static JObject Request(params (string Name, JToken Value)[] rows) => new(rows.Select(r => new JProperty(r.Name, r.Value)));
        private static async Task Refuse(string code, Action action)
        { var error = await Check.ThrowsAsync<WorkerError>(() => { action(); return Task.CompletedTask; }); Check.Equal(code, error.Code); }

        internal sealed class Target : IEngineeringObject
        {
            internal readonly Dictionary<string, object?> Values = new() { ["First"] = 1, ["Second"] = 1, ["Third"] = 1 };
            internal readonly List<SyntheticAttribute> Attributes = [new("First", RW), new("Second", RW), new("Third", RW)];
            internal readonly List<string> Writes = [];
            internal int Reads, MetadataReads;
            internal Func<IEnumerable<SyntheticAttribute>?>? Metadata;
            internal Func<string, object?>? Read;
            internal Action<string, object?>? AfterWrite;
            public IEnumerable<SyntheticAttribute>? GetAttributeInfos() { MetadataReads++; return Metadata is null ? Attributes : Metadata(); }
            public object? GetAttribute(string name) { Reads++; return Read is null ? Values[name] : Read(name); }
            public void SetAttribute(string name, object value) { Writes.Add(name); Values[name] = value; AfterWrite?.Invoke(name, value); }
        }

        internal static async Task AccessAndTypes()
        {
            foreach (bool apply in new[] { false, true })
            foreach (string fault in new[] { "absent", "nullMetadata", "duplicate", "wrongCase", "readOnly", "writeOnly", "none", "sensitive", "outside" })
            {
                var t = new Target(); string name = fault == "sensitive" ? "Password" : fault == "outside" ? "Fourth" : "First";
                string code = "AttributeAccessUnknown";
                if (fault == "absent") t.Attributes.RemoveAt(0);
                if (fault == "nullMetadata") t.Metadata = () => null;
                if (fault == "duplicate") t.Attributes.Add(new("First", RW));
                if (fault == "wrongCase") t.Attributes[0].Name = "first";
                if (fault == "readOnly") { t.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read; code = "AttributeNotWritable"; }
                if (fault == "writeOnly") { t.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write; code = "AttributeNotReadable"; }
                if (fault == "none") { t.Attributes[0].AccessMode = 0; code = "AttributeNotWritable"; }
                if (fault == "sensitive") code = "ProtectedAttribute";
                if (fault == "outside") code = "UnsupportedAttribute";
                await Refuse(code, () => { if (apply) OpennessSession.ApplyAttributesForTest(t, Request((name, 2)), Allowed); else OpennessSession.PreviewAttributesForTest(t, Request((name, 2)), Allowed); });
                Check.Equal(0, t.Reads); Check.Equal(0, t.Writes.Count);
                if (fault is "sensitive" or "outside") Check.Equal(0, t.MetadataReads);
            }
            foreach (IEnumerable<Type>? types in new IEnumerable<Type>?[] { null, [], [typeof(int), typeof(long)], [typeof(int), typeof(int)], [null!] })
            {
                var t = new Target(); t.Values["First"] = null; t.Attributes[0].SupportedTypes = types;
                await Refuse("AttributeTypeUnknown", () => OpennessSession.ApplyAttributesForTest(t, Request(("First", 2)), Allowed)); Check.Equal(0, t.Writes.Count);
            }
        }

        internal static async Task BatchPreflight()
        {
            foreach (string fault in new[] { "access", "type", "unsupported", "conversion", "getter", "metadata" })
            {
                var t = new Target(); string code = "AttributeAccessUnknown";
                if (fault == "access") t.Attributes.RemoveAt(1);
                if (fault == "type") { t.Values["Second"] = null; code = "AttributeTypeUnknown"; }
                if (fault == "unsupported") { t.Values["Second"] = new object(); code = "UnsupportedAttributeType"; }
                if (fault == "conversion") code = "InvalidRequest";
                if (fault == "getter") t.Read = name => name == "Second" ? throw new InvalidOperationException("native read failed") : t.Values[name];
                if (fault == "metadata") t.Metadata = () => t.MetadataReads == 2 ? throw new InvalidOperationException("native metadata failed") : t.Attributes;
                Action act = () => OpennessSession.ApplyAttributesForTest(t, Request(("First", 2), ("Second", fault == "conversion" ? new JValue(1.5) : new JValue(2))), Allowed);
                if (fault is "getter" or "metadata") await Check.ThrowsAsync<InvalidOperationException>(() => { act(); return Task.CompletedTask; }); else await Refuse(code, act);
                Check.Equal(0, t.Writes.Count); Check.Equal(1, t.Values["First"]);
            }
            var empty = new Target(); OpennessSession.ApplyAttributesForTest(empty, new(), Allowed);
            Check.Equal(0, OpennessSession.PreviewAttributesForTest(empty, new(), Allowed).Count);
            Check.Equal(0, empty.MetadataReads); Check.Equal(0, empty.Reads); Check.Equal(0, empty.Writes.Count);
        }

        internal static async Task DispatchAndReadback()
        {
            foreach (string fault in new[] { "permission", "type", "null", "declaredType", "duplicate" })
            {
                var t = new Target();
                if (fault == "declaredType") { t.Values["First"] = null; t.Attributes[0].SupportedTypes = [typeof(int)]; }
                t.Metadata = () => {
                    if (t.MetadataReads == 2)
                    {
                        if (fault == "permission") t.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read;
                        if (fault == "type") t.Values["First"] = 1L;
                        if (fault == "null") t.Values["First"] = null;
                        if (fault == "declaredType") t.Attributes[0].SupportedTypes = [typeof(long)];
                        if (fault == "duplicate") t.Attributes.Add(new("First", RW));
                    }
                    return t.Attributes;
                };
                await Refuse(fault == "permission" ? "AttributeNotWritable" : fault == "null" ? "AttributeTypeUnknown" : fault == "duplicate" ? "AttributeAccessUnknown" : "PreviewStale",
                    () => OpennessSession.ApplyAttributesForTest(t, Request(("First", 2)), Allowed)); Check.Equal(0, t.Writes.Count);
            }
            foreach (string fault in new[] { "permission", "type", "setter", "valueReadback", "typeReadback", "accessReadback", "getterReadback", "nullReadback" })
            {
                var t = new Target();
                t.AfterWrite = (name, value) => {
                    if (name == "First")
                    {
                        if (fault == "permission") t.Attributes[1].AccessMode = EngineeringAttributeAccessMode.Read;
                        if (fault == "type") t.Values["Second"] = 1L;
                    }
                    if (name == "Second" && fault == "setter") throw new InvalidOperationException("failed after dispatch");
                    if (name == "Third")
                    {
                        if (fault == "valueReadback") t.Values["First"] = 99;
                        if (fault == "typeReadback") t.Values["First"] = 2L;
                        if (fault == "accessReadback") t.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write;
                        if (fault == "getterReadback") t.Read = _ => throw new InvalidOperationException("readback failed");
                        if (fault == "nullReadback") { t.Values["First"] = null; t.Attributes[0].SupportedTypes = [typeof(int)]; }
                    }
                };
                Action act = () => OpennessSession.ApplyAttributesForTest(t, Request(("First", 2), ("Second", 2), ("Third", 2)), Allowed);
                if (fault is "setter" or "getterReadback") await Check.ThrowsAsync<InvalidOperationException>(() => { act(); return Task.CompletedTask; });
                else await Refuse(fault == "permission" ? "AttributeNotWritable" : fault == "type" ? "PreviewStale" : fault == "accessReadback" ? "AttributeNotReadable" : "MutationVerificationFailed", act);
                Check.Equal(fault is "permission" or "type" ? "First" : fault == "setter" ? "First,Second" : "First,Second,Third", string.Join(',', t.Writes));
            }
            var readOnly = new Target(); readOnly.AfterWrite = (_, _) => readOnly.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read;
            OpennessSession.ApplyAttributesForTest(readOnly, Request(("First", 2)), Allowed); Check.Equal(1, readOnly.Writes.Count);
        }

        private enum Choice { First, Second }
        internal static async Task ScalarValues()
        {
            foreach (bool nullBefore in new[] { false, true })
            foreach (object after in new object[] { (byte)2, (sbyte)-2, (short)-2, (ushort)2, 2, 2U, 9007199254740993L, ulong.MaxValue, 2.5F, 2.5D, 2.5M, true, "日本語\r\n😀", Choice.Second })
            {
                var t = new Target(); t.Values["First"] = nullBefore ? null : after; t.Attributes[0].SupportedTypes = [after.GetType()];
                var request = Request(("First", after is Choice ? new JValue(after.ToString()) : JToken.FromObject(after))); string original = request.ToString();
                var preview = OpennessSession.PreviewAttributesForTest(t, request, Allowed);
                Check.Equal(after.GetType().FullName, (string?)preview[0]!["type"]); Check.Equal(0, t.Writes.Count);
                var normalized = OpennessSession.NormalizedAttributesForTest(t, request, Allowed);
                Check.Equal(after.GetType(), normalized["First"].GetType()); Check.Equal(after, normalized["First"]);
                OpennessSession.ApplyAttributesForTest(t, request, Allowed);
                Check.Equal(after, t.Values["First"]); Check.Equal(after.GetType(), t.Values["First"]!.GetType()); Check.Equal(original, request.ToString());
            }
            foreach (var (before, token, code) in new (object, JToken, string)[] {
                (1, JValue.CreateNull(), "InvalidRequest"), (1, new JArray(1), "InvalidRequest"), (1, new JValue(1.5), "InvalidRequest"),
                (1, new JValue(long.MaxValue), "InvalidRequest"), (1F, new JValue(double.MaxValue), "InvalidRequest"), (1D, new JValue(double.NaN), "InvalidRequest"),
                (false, new JValue(1), "InvalidRequest"), ("old", new JValue(1), "InvalidRequest"), (Choice.First, new JValue("second"), "InvalidRequest"),
                (new object(), new JValue(2), "UnsupportedAttributeType") })
            {
                var t = new Target(); t.Values["First"] = before;
                await Refuse(code, () => OpennessSession.ApplyAttributesForTest(t, Request(("First", token)), Allowed)); Check.Equal(0, t.Writes.Count);
            }
        }
    }
}

namespace Siemens.Engineering
{
    // Synthetic shape for the linked production helpers, not the Siemens SDK ABI.
    internal interface IEngineeringObject
    {
        IEnumerable<SyntheticAttribute>? GetAttributeInfos() => [];
        object? GetAttribute(string name) => throw new InvalidOperationException("No synthetic attribute getter");
        void SetAttribute(string name, object value) => throw new InvalidOperationException("No synthetic writes");
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JArray PreviewAttributesForTest(IEngineeringObject target, JObject attributes, HashSet<string> allowed) => PrepareAttributes(target, attributes, allowed);
        internal static void ApplyAttributesForTest(IEngineeringObject target, JObject attributes, HashSet<string> allowed) => ApplyAttributes(target, attributes, allowed);
        internal static Dictionary<string, object> NormalizedAttributesForTest(IEngineeringObject target, JObject attributes, HashSet<string> allowed)
        { PrepareAttributes(target, attributes, allowed, out var converted); return converted; }
    }
}
