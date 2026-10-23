using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW.TechnologicalObjects;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class TechnologyParameterTests
    {
        private const EngineeringAttributeAccessMode RW = EngineeringAttributeAccessMode.Read | EngineeringAttributeAccessMode.Write;
        private static TechnologicalParameter Parameter(string name = "Value", object? value = null) => new(name, value ?? 1);
        private static TechnologicalInstanceDB Owner(params TechnologicalParameter[] rows) => new(rows);
        private static JObject Request(params (string Name, JToken Value)[] rows) => new(rows.Select(r => new JProperty(r.Name,r.Value)));
        private static async Task Refuse(string code, Action action)
        { var error = await Check.ThrowsAsync<WorkerError>(() => { action(); return Task.CompletedTask; }); Check.Equal(code,error.Code); }

        internal static async Task AccessAndType()
        {
            foreach (bool apply in new[] { false,true })
            foreach (string fault in new[] { "absent", "nullMetadata", "duplicate", "readOnly", "writeOnly", "none", "nullValue", "wrongName", "sensitive", "missing" })
            {
                var p = Parameter(); var owner = Owner(p); string name = fault == "sensitive" ? "Password" : "Value";
                string code = "ParameterAccessUnknown";
                if (fault == "absent") p.Attributes.Clear();
                if (fault == "nullMetadata") p.Metadata = _ => null;
                if (fault == "duplicate") p.Attributes.Add(new("Value",RW));
                if (fault == "readOnly") { p.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read; code = "ReadOnlyParameter"; }
                if (fault == "writeOnly") { p.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write; code = "ParameterNotReadable"; }
                if (fault == "none") { p.Attributes[0].AccessMode = 0; code = "ReadOnlyParameter"; }
                if (fault == "nullValue") { p.Stored = null; code = "ParameterTypeUnknown"; }
                if (fault == "wrongName") { p.Name = "value"; owner.Parameters.Lookup = _ => p; code = "ParameterNotFound"; }
                if (fault == "sensitive") code = "ProtectedAttribute";
                if (fault == "missing") { owner.Parameters.Lookup = _ => null; code = "ParameterNotFound"; }
                await Refuse(code,() => { if (apply) OpennessSession.ApplyTechnologyForTest(owner,Request((name,2))); else OpennessSession.PreviewTechnologyForTest(owner,Request((name,2))); });
                Check.Equal(0,p.Writes);
                Check.Equal(fault == "nullValue" ? 1 : 0,p.Reads);
                if (fault == "sensitive") Check.Equal(0,owner.Parameters.Lookups);
            }
        }

        internal static async Task BatchPreflight()
        {
            foreach (string fault in new[] { "missing", "access", "type", "unsupported", "conversion", "getter", "metadata" })
            {
                var first = Parameter("First"); var second = Parameter("Second"); var owner = Owner(first,second);
                string code = "ParameterNotFound";
                if (fault == "missing") owner.Parameters.Rows.Remove(second);
                if (fault == "access") { second.Attributes.Clear(); code = "ParameterAccessUnknown"; }
                if (fault == "type") { second.Stored = null; code = "ParameterTypeUnknown"; }
                if (fault == "unsupported") { second.Stored = new object(); code = "UnsupportedAttributeType"; }
                if (fault == "conversion") code = "InvalidRequest";
                if (fault == "getter") { second.Read = _ => throw new InvalidOperationException("native read failed"); }
                if (fault == "metadata") { second.Metadata = _ => throw new InvalidOperationException("native metadata failed"); }
                Action act = () => OpennessSession.ApplyTechnologyForTest(owner,Request(("First",2),("Second",fault == "conversion" ? new JValue(1.5) : new JValue(3))));
                if (fault is "getter" or "metadata") await Check.ThrowsAsync<InvalidOperationException>(() => { act(); return Task.CompletedTask; }); else await Refuse(code,act);
                Check.Equal(0,first.Writes); Check.Equal(0,second.Writes); Check.Equal(1,first.Stored);
            }
            var empty = Owner(); Check.Equal(0,OpennessSession.PreviewTechnologyForTest(empty,new()).Count); OpennessSession.ApplyTechnologyForTest(empty,new()); Check.Equal(0,empty.Parameters.Lookups);
            var oversized = new JObject(Enumerable.Range(0,501).Select(i => new JProperty("P"+i,i)));
            await Refuse("InvalidRequest",() => OpennessSession.ApplyTechnologyForTest(empty,oversized)); Check.Equal(0,empty.Parameters.Lookups);
            var rows = Enumerable.Range(0,500).Select(i => Parameter("P"+i)).ToArray(); var maximum = Owner(rows);
            OpennessSession.ApplyTechnologyForTest(maximum,new JObject(rows.Select(p => new JProperty(p.Name,2)))); Check.Equal(500,rows.Sum(p => p.Writes));
        }

        internal static async Task DispatchAndReadback()
        {
            foreach (string fault in new[] { "permission", "type", "null", "name" })
            {
                var p = Parameter(); var owner = Owner(p);
                p.Metadata = n => { if (n == 2 && fault == "permission") return []; if (n == 2 && fault == "null") p.Stored = null; return p.Attributes; };
                if (fault == "type") p.Read = n => n == 2 ? (object)1L : 1;
                if (fault == "name") owner.Parameters.Lookup = _ => { if (owner.Parameters.Lookups == 2) p.Name = "Other"; return p; };
                await Refuse(fault == "permission" ? "ParameterAccessUnknown" : fault == "null" ? "ParameterTypeUnknown" : fault == "name" ? "ParameterNotFound" : "PreviewStale",
                    () => OpennessSession.ApplyTechnologyForTest(owner,Request(("Value",2)))); Check.Equal(0,p.Writes);
            }
            foreach (string fault in new[] { "permission", "type", "setter", "readbackValue", "readbackType", "readbackPermission" })
            {
                var first = Parameter("First"); var second = Parameter("Second"); var third = Parameter("Third"); var owner = Owner(first,second,third);
                first.AfterWrite = _ => { if (fault == "permission") second.Attributes.Clear(); if (fault == "type") second.Stored = 1L; };
                second.AfterWrite = _ => {
                    if (fault == "setter") throw new InvalidOperationException("native setter failed after dispatch");
                    if (fault == "readbackValue") second.Stored = 99;
                    if (fault == "readbackType") second.Stored = 2L;
                    if (fault == "readbackPermission") second.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Write;
                };
                Action act = () => OpennessSession.ApplyTechnologyForTest(owner,Request(("First",2),("Second",2),("Third",2)));
                if (fault == "setter") await Check.ThrowsAsync<InvalidOperationException>(() => { act(); return Task.CompletedTask; });
                else await Refuse(fault == "permission" ? "ParameterAccessUnknown" : fault == "type" ? "PreviewStale" : fault == "readbackPermission" ? "ParameterNotReadable" : "MutationVerificationFailed",act);
                Check.Equal(1,first.Writes); Check.Equal(fault is "permission" or "type" ? 0 : 1,second.Writes);
                Check.Equal(fault.StartsWith("readback",StringComparison.Ordinal) ? 1 : 0,third.Writes);
            }
            var readOnlyAfter = Parameter(); readOnlyAfter.AfterWrite = _ => readOnlyAfter.Attributes[0].AccessMode = EngineeringAttributeAccessMode.Read;
            OpennessSession.ApplyTechnologyForTest(Owner(readOnlyAfter),Request(("Value",2))); Check.Equal(1,readOnlyAfter.Writes);
        }

        internal static async Task ExplicitNullType()
        {
            foreach (Type type in new[] { typeof(string), typeof(int), typeof(bool), typeof(Choice) })
            {
                var p = Parameter(); p.Stored = null; p.Attributes[0].SupportedTypes = [type];
                JToken requested = type == typeof(string) ? new JValue("日本") : type == typeof(int) ? new JValue(3) : type == typeof(bool) ? new JValue(true) : new JValue("Second");
                var preview = OpennessSession.PreviewTechnologyForTest(Owner(p),Request(("Value",requested)));
                Check.Equal(JTokenType.Null,preview[0]!["before"]!.Type); Check.Equal(type.FullName,(string?)preview[0]!["type"]);
                OpennessSession.ApplyTechnologyForTest(Owner(p),Request(("Value",requested)));
                Check.Equal(type,p.Stored!.GetType()); Check.Equal(1,p.Writes);
            }
            foreach (IEnumerable<Type>? declarations in new IEnumerable<Type>?[] { null, [], [typeof(string),typeof(int)], [typeof(string),typeof(string)], [null!] })
            {
                var p = Parameter(); p.Stored = null; p.Attributes[0].SupportedTypes = declarations;
                await Refuse("ParameterTypeUnknown",() => OpennessSession.ApplyTechnologyForTest(Owner(p),Request(("Value","text")))); Check.Equal(0,p.Writes);
            }
            var unsupported = Parameter(); unsupported.Stored = null; unsupported.Attributes[0].SupportedTypes = [typeof(object)];
            await Refuse("UnsupportedAttributeType",() => OpennessSession.ApplyTechnologyForTest(Owner(unsupported),Request(("Value","text")))); Check.Equal(0,unsupported.Writes);
            var drift = Parameter(); drift.Stored = null; drift.Attributes[0].SupportedTypes = [typeof(int)];
            drift.Metadata = n => { if(n==2)drift.Attributes[0].SupportedTypes = [typeof(long)]; return drift.Attributes; };
            await Refuse("PreviewStale",() => OpennessSession.ApplyTechnologyForTest(Owner(drift),Request(("Value",3)))); Check.Equal(0,drift.Writes);
        }

        private enum Choice { First, Second }
        internal static async Task ScalarRoundtrip()
        {
            foreach (var (before,after) in new (object,object)[] { ((byte)1,(byte)2), ((sbyte)1,(sbyte)-2), ((short)1,(short)-2), ((ushort)1,(ushort)2), (1,2), (1U,2U), (1L,9007199254740993L), (1UL,ulong.MaxValue), (1F,2.5F), (1D,2.5D), (1M,2.5M), (false,true), ("old","日本語\r\n😀"), (Choice.First,Choice.Second) })
            {
                var p = Parameter(value:before); var request = Request(("Value",after is Choice c ? new JValue(c.ToString()) : JToken.FromObject(after))); var original = request.ToString();
                var preview = OpennessSession.PreviewTechnologyForTest(Owner(p),request); Check.Equal(before.GetType().FullName,(string?)preview[0]!["type"]); Check.Equal(0,p.Writes);
                OpennessSession.ApplyTechnologyForTest(Owner(p),request); Check.Equal(after,p.Stored); Check.Equal(after.GetType(),p.Stored!.GetType()); Check.Equal(1,p.Writes); Check.Equal(original,request.ToString());
            }
            foreach (var (before,token,code) in new (object,JToken,string)[] { (1,JValue.CreateNull(),"InvalidRequest"), (1,new JArray(1),"InvalidRequest"), (1,new JObject(),"InvalidRequest"), (1,new JValue(1.25),"InvalidRequest"), (1,new JValue(long.MaxValue),"InvalidRequest"), (1F,new JValue(double.MaxValue),"InvalidRequest"), (1D,new JValue(double.NaN),"InvalidRequest"), (1D,new JValue(double.PositiveInfinity),"InvalidRequest"), (false,new JValue(1),"InvalidRequest"), ("old",new JValue(1),"InvalidRequest"), (Choice.First,new JValue("second"),"InvalidRequest"), (new byte[]{1},new JValue(2),"UnsupportedAttributeType") })
            {
                var p = Parameter(value:before); await Refuse(code,() => OpennessSession.ApplyTechnologyForTest(Owner(p),Request(("Value",token)))); Check.Equal(0,p.Writes);
            }
        }
    }
}

// Synthetic API shapes compile the production routines, not the Siemens worker or its SDK ABI.
namespace Siemens.Engineering
{
    [Flags] internal enum EngineeringAttributeAccessMode { Read = 1, Write = 2 }
    internal sealed class SyntheticAttribute(string name,EngineeringAttributeAccessMode access)
    { internal string Name = name; internal EngineeringAttributeAccessMode AccessMode = access; internal IEnumerable<Type>? SupportedTypes = []; }
}
namespace Siemens.Engineering.SW.TechnologicalObjects
{
    internal sealed class TechnologicalParameter(string name,object? value)
    {
        internal string Name = name; internal object? Stored = value;
        internal int Reads, Writes, MetadataReads;
        internal List<SyntheticAttribute> Attributes = [new("Value",EngineeringAttributeAccessMode.Read | EngineeringAttributeAccessMode.Write)];
        internal Func<int,IEnumerable<SyntheticAttribute>?>? Metadata;
        internal Func<int,object?>? Read; internal Action<object?>? AfterWrite;
        internal IEnumerable<SyntheticAttribute>? GetAttributeInfos() { MetadataReads++; return Metadata is null ? Attributes : Metadata(MetadataReads); }
        internal object? Value { get { Reads++; return Read is null ? Stored : Read(Reads); } set { Writes++; Stored = value; AfterWrite?.Invoke(value); } }
    }
    internal sealed class TechnologicalInstanceDB(TechnologicalParameter[] rows)
    {
        internal SyntheticParameters Parameters = new(rows);
        internal string Name = "Axis", OfSystemLibElement = "TO_PositioningAxis";
        internal int Number = 1, Deletes;
        internal Version OfSystemLibVersion = new(8,0);
        internal DateTime ModifiedDate = new(2026,10,17,0,0,0,DateTimeKind.Utc);
        internal Action? OnDelete;
        internal void Delete() { Deletes++; OnDelete?.Invoke(); }
        internal Motion.OutputCamMeasuringInputContainer? ChildContainer;
        internal Func<object?>? Service;
        internal Func<Type,object?>? TypedService;
        internal int ServiceReads;
        internal T? GetService<T>() where T : class { ServiceReads++; return (TypedService is not null ? TypedService(typeof(T)) : Service is null ? ChildContainer : Service()) as T; }
    }
    internal sealed class TechnologicalInstanceDBGroup
    {
        internal string Name = "";
        internal IEnumerable<TechnologicalInstanceDB> TechnologicalObjects = [];
        internal IEnumerable<TechnologicalInstanceDBGroup> Groups = [];
    }
    internal sealed class SyntheticParameters(TechnologicalParameter[] rows)
    {
        internal List<TechnologicalParameter> Rows = [..rows]; internal int Lookups;
        internal Func<string,TechnologicalParameter?>? Lookup;
        internal TechnologicalParameter? Find(string name) { Lookups++; return Lookup is null ? Rows.SingleOrDefault(p => p.Name == name) : Lookup(name); }
    }
}
namespace Siemens.Engineering.SW.TechnologicalObjects.Motion
{
    internal sealed class OutputCamMeasuringInputContainer
    {
        internal IEnumerable<TechnologicalInstanceDB> OutputCams = [], MeasuringInputs = [];
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JArray PreviewTechnologyForTest(TechnologicalInstanceDB technology,JObject parameters) => PrepareTechnologyParameters(technology,parameters,out _);
        internal static void ApplyTechnologyForTest(TechnologicalInstanceDB technology,JObject parameters) => ApplyTechnologyParameters(technology,parameters);
    }
}
