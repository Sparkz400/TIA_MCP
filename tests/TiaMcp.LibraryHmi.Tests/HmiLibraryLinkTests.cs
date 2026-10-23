using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using TiaMcp.Openness.V21;

internal static class HmiLibraryLinkTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("linked read retains one coherent detached observation", () =>
        {
            var f = new Fixture(); var result = HmiLibraryLink.Read(f.Item);
            Equal("linked", result.Value<string>("linkStatus")); Equal(false, result.Value<bool>("nativeLinkVerified"));
            Equal(false, result.Value<bool>("runtimeVerified")); Equal(f.TypeGuid.ToString("D"), result.Value<string>("typeGuid"));
            Equal(f.VersionGuid.ToString("D"), result.Value<string>("versionGuid")); Equal("1.2.3", result.Value<string>("version"));
            foreach (var count in f.Reads.Values) Equal(1, count);
            string saved = result.ToString(); f.TypeGuid = Guid.NewGuid(); f.VersionGuid = Guid.NewGuid();
            Equal(saved, result.ToString());
        });
        test("required version compares the captured pair", () =>
        {
            var f = new Fixture(); var result = HmiLibraryLink.Read(f.Item, f.Expected);
            Equal(true, result.Value<bool>("nativeLinkVerified"));
            Equal(f.VersionGuid.ToString("D"), result.Value<string>("versionGuid"));
            foreach (var count in f.Reads.Values) Equal(1, count);
        });
        test("distinct native wrapper accepted by native equality", () =>
        {
            var f = new Fixture(); f.Info.Owner = () => new NativeObject { Identity = f.Item.Identity };
            Equal(true, HmiLibraryLink.Read(f.Item, f.Expected).Value<bool>("nativeLinkVerified"));
        });
        foreach (bool required in new[] { false, true })
            test("version getter drift cannot mix reported identity required=" + required, () =>
            {
                var f = new Fixture(); int reads = 0;
                f.Info.Version = () => ++reads == 1 ? f.Version : new Fixture().Version;
                var result = HmiLibraryLink.Read(f.Item, required ? f.Expected : null);
                Equal(1, reads); Equal(f.TypeGuid.ToString("D"), result.Value<string>("typeGuid"));
                Equal(f.VersionGuid.ToString("D"), result.Value<string>("versionGuid"));
            });
        test("changing GUID getter is captured once", () =>
        {
            var f = new Fixture(); int reads = 0;
            f.Version.Identity = () => ++reads == 1 ? f.VersionGuid : Guid.NewGuid();
            var result = HmiLibraryLink.Read(f.Item, f.Expected);
            Equal(1, reads); Equal(f.VersionGuid.ToString("D"), result.Value<string>("versionGuid"));
        });
        test("confirmed absent service alone permits preserving import", () =>
        {
            var f = new Fixture(); f.Item.Service = () => null;
            Equal("noService", HmiLibraryLink.Read(f.Item).Value<string>("linkStatus"));
            HmiLibraryLink.RequireNoLink(f.Item);
            Reject("PreservationMismatch", () => HmiLibraryLink.Read(f.Item, f.Expected));
        });
        test("linked preserving import requires library handoff", () => Reject("LibraryHandoffRequired", () => HmiLibraryLink.RequireNoLink(new Fixture().Item)));
        test("missing provider remains unavailable", () =>
        {
            var item = new NoProvider(); AssertUnavailable(HmiLibraryLink.Read(item));
            Reject("HmiPreservationUnavailable", () => HmiLibraryLink.RequireNoLink(item));
            Reject("PreservationMismatch", () => HmiLibraryLink.Read(item, new Fixture().Expected));
        });
        var invalid = new Dictionary<string, Action<Fixture>> {
            { "foreign owner", f => f.Info.Owner = () => new NativeObject() },
            { "missing owner", f => f.Info.Owner = () => null },
            { "missing version", f => f.Info.Version = () => null },
            { "missing type", f => f.Version.Type = () => null },
            { "empty type identity", f => f.TypeGuid = Guid.Empty },
            { "empty version identity", f => f.VersionGuid = Guid.Empty },
            { "missing display version", f => f.Version.Number = () => null }
        };
        foreach (var entry in invalid)
            test(entry.Key + " refuses readback verification and both import phases", () =>
            {
                var f = new Fixture(); entry.Value(f); AssertUnavailable(HmiLibraryLink.Read(f.Item));
                Reject("PreservationMismatch", () => HmiLibraryLink.Read(f.Item, f.Expected));
                Reject("HmiPreservationUnavailable", () => HmiLibraryLink.RequireNoLink(f.Item));
            });
        foreach (string point in new[] { "service", "owner", "equality", "version", "type", "typeGuid", "versionGuid", "number" })
        {
            test("native failure at " + point + " remains unknown", () =>
            {
                var f = new Fixture { FailAt = point }; AssertUnavailable(HmiLibraryLink.Read(f.Item));
                Reject("PreservationMismatch", () => HmiLibraryLink.Read(f.Item, f.Expected));
                Reject("HmiPreservationUnavailable", () => HmiLibraryLink.RequireNoLink(f.Item));
            });
            test("unexpected failure at " + point + " propagates", () =>
            {
                var f = new Fixture { FailAt = point, Unexpected = true };
                Unexpected(() => HmiLibraryLink.Read(f.Item));
                Unexpected(() => HmiLibraryLink.Read(f.Item, f.Expected));
                Unexpected(() => HmiLibraryLink.RequireNoLink(f.Item));
            });
        }
        foreach (string point in new[] { "expectedType", "expectedTypeGuid", "expectedGuid" })
        {
            test("native expected identity failure at " + point + " refuses verification", () =>
            {
                var f = new Fixture { FailAt = point };
                Reject("PreservationMismatch", () => HmiLibraryLink.Read(f.Item, f.Expected));
            });
            test("unexpected expected identity failure at " + point + " propagates", () =>
            {
                var f = new Fixture { FailAt = point, Unexpected = true };
                Unexpected(() => HmiLibraryLink.Read(f.Item, f.Expected));
            });
        }
        foreach (var entry in new Dictionary<string, Action<Fixture>> {
            { "missing requested type", f => f.Expected.Type = () => null },
            { "foreign requested type", f => f.ExpectedTypeGuid = Guid.NewGuid() },
            { "empty requested type", f => f.ExpectedTypeGuid = Guid.Empty },
            { "foreign requested version", f => f.ExpectedGuid = Guid.NewGuid() },
            { "empty requested version", f => f.ExpectedGuid = Guid.Empty }
        })
            test(entry.Key + " refuses verification", () =>
            {
                var f = new Fixture(); entry.Value(f);
                Reject("PreservationMismatch", () => HmiLibraryLink.Read(f.Item, f.Expected));
            });
        test("aliased expected version remains coherent per role", () =>
        {
            var f = new Fixture(); Equal(true, HmiLibraryLink.Read(f.Item, f.Version).Value<bool>("nativeLinkVerified"));
            Equal(1, f.Reads["version"]); Equal(2, f.Reads["versionGuid"]);
        });
        test("null selection is a caller error", () =>
        {
            try { HmiLibraryLink.Read(null); } catch (ArgumentNullException) { return; }
            throw new Exception("Expected null rejection");
        });
    }
    static void AssertUnavailable(JObject result)
    {
        Equal("unavailable", result.Value<string>("linkStatus")); Equal(false, result.Value<bool>("nativeLinkVerified"));
        Equal(false, result.Value<bool>("runtimeVerified"));
        foreach (string key in new[] { "typeGuid", "versionGuid", "version" }) Equal(JTokenType.Null, result[key].Type);
        if (string.IsNullOrEmpty(result.Value<string>("linkReadError"))) throw new Exception("Missing reason");
    }
    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
    static void Reject(string code, Action action)
    {
        try { action(); } catch (BridgeException ex) { Equal(code, ex.Code); return; }
        throw new Exception("Expected " + code);
    }
    static void Unexpected(Action action)
    {
        try { action(); } catch (InvalidOperationException ex) { Equal("unexpected fixture failure", ex.Message); return; }
        throw new Exception("Unexpected exception was swallowed");
    }
    sealed class Fixture
    {
        internal readonly NativeObject Item = new NativeObject();
        internal readonly LibraryTypeInstanceInfo Info = new LibraryTypeInstanceInfo();
        internal readonly LibraryTypeVersion Version = new LibraryTypeVersion(), Expected = new LibraryTypeVersion();
        internal readonly Dictionary<string, int> Reads = new Dictionary<string, int>();
        internal Guid TypeGuid = Guid.NewGuid(), VersionGuid = Guid.NewGuid(), ExpectedTypeGuid, ExpectedGuid;
        internal string FailAt; internal bool Unexpected;
        internal Fixture()
        {
            ExpectedTypeGuid = TypeGuid; ExpectedGuid = VersionGuid;
            Item.Service = () => Read("service", Info);
            Item.Equality = other => Read("equality", other is NativeObject && ((NativeObject)other).Identity == Item.Identity);
            Info.Owner = () => Read<IEngineeringObject>("owner", Item);
            Info.Version = () => Read("version", Version);
            var type = new LibraryType { Identity = () => Read("typeGuid", TypeGuid) };
            Version.Type = () => Read("type", type); Version.Identity = () => Read("versionGuid", VersionGuid);
            Version.Number = () => Read("number", new Version(1, 2, 3));
            var expectedType = new LibraryType { Identity = () => Read("expectedTypeGuid", ExpectedTypeGuid) };
            Expected.Type = () => Read("expectedType", expectedType); Expected.Identity = () => Read("expectedGuid", ExpectedGuid);
        }
        T Read<T>(string key, T value)
        {
            Reads[key] = Reads.ContainsKey(key) ? Reads[key] + 1 : 1;
            if (FailAt == key) { if (Unexpected) throw new InvalidOperationException("unexpected fixture failure"); throw new EngineeringException("native fixture failure: " + key); }
            return value;
        }
    }
}

namespace Siemens.Engineering
{
    internal interface IEngineeringObject { }
    internal interface IEngineeringServiceProvider { T GetService<T>() where T : class; }
    internal sealed class NoProvider : IEngineeringObject { }
    internal sealed class NativeObject : IEngineeringObject, IEngineeringServiceProvider
    {
        internal Guid Identity = Guid.NewGuid(); internal Func<object> Service; internal Func<object, bool> Equality;
        public T GetService<T>() where T : class { return Service() as T; }
        public override bool Equals(object other) { return Equality != null ? Equality(other) : other is NativeObject && ((NativeObject)other).Identity == Identity; }
        public override int GetHashCode() { return Identity.GetHashCode(); }
    }
    internal sealed class EngineeringException : Exception { internal EngineeringException(string message) : base(message) { } }
}
namespace Siemens.Engineering.Library.Types
{
    internal sealed class LibraryType { internal Func<Guid> Identity; public Guid Guid { get { return Identity(); } } }
    internal sealed class LibraryTypeVersion
    {
        internal Func<LibraryType> Type; internal Func<Guid> Identity; internal Func<Version> Number;
        public LibraryType TypeObject { get { return Type(); } }
        public Guid Guid { get { return Identity(); } }
        public Version VersionNumber { get { return Number(); } }
    }
    internal sealed class LibraryTypeInstanceInfo
    {
        internal Func<LibraryTypeVersion> Version; internal Func<Siemens.Engineering.IEngineeringObject> Owner;
        public LibraryTypeVersion LibraryTypeVersion { get { return Version(); } }
        public Siemens.Engineering.IEngineeringObject LibraryTypeInstance { get { return Owner(); } }
    }
}
