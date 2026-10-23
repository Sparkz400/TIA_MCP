#nullable disable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Library.Types;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class PlcLibraryInstanceCaptureTests
    {
        internal static void Run(Action<string, Action> test)
        {
            PlcLibraryDependencyObservationTests.Run(test);
            foreach (var kind in new[] { "FB", "FC", "PlcType" })
                test("library instance captures exact " + kind + " identity once", () => {
                    var f = new Fixture(kind); var actual = PlcLibraryInstanceCapture.Read(f.Item);
                    Equal("linked", actual.Value<string>("status"));
                    Equal(f.Type.Identity.ToString("D"), actual.Value<string>("typeGuid"));
                    Equal(f.Version.Identity.ToString("D"), actual.Value<string>("versionGuid"));
                    Equal(false, actual.Value<bool>("nativeQualified"));
                    Equal(1, f.Item.ServiceReads); Equal(1, f.Item.EqualityReads);
                    Equal(1, f.Info.InstanceReads); Equal(1, f.Info.VersionReads);
                    Equal(1, f.Version.TypeReads); Equal(1, f.Version.GuidReads); Equal(1, f.Type.GuidReads);
                });
            test("library instance accepts different wrappers for the same native identity", () => {
                var f = new Fixture(); f.Info.ReadInstance = () => new FB { Identity = f.Item.Identity };
                Equal("linked", PlcLibraryInstanceCapture.Read(f.Item).Value<string>("status"));
            });
            test("library instance unavailable service remains distinct from read failure", () => {
                var f = new Fixture(); f.Item.ReadService = () => null;
                NoIdentity(PlcLibraryInstanceCapture.Read(f.Item), "noService"); Equal(0, f.Info.VersionReads);
            });
            test("library instance unsupported DB does not invoke a native service", () => {
                var item = new InstanceDB { ReadService = () => throw new Exception("Unsupported query") };
                NoIdentity(PlcLibraryInstanceCapture.Read(item), "notApplicable"); Equal(0, item.ServiceReads);
            });
            test("library instance rejects null selected object", () => {
                try { PlcLibraryInstanceCapture.Read(null); } catch (ArgumentNullException) { return; }
                throw new Exception("Null selected object was accepted.");
            });
            foreach (var missing in new[] { "instance", "otherInstance", "version", "type", "typeGuid", "versionGuid" })
                test("library instance does not publish partial identity for " + missing, () => {
                    var f = new Fixture();
                    switch (missing)
                    {
                        case "instance": f.Info.ReadInstance = () => null; break;
                        case "otherInstance": f.Info.ReadInstance = () => new FB(); break;
                        case "version": f.Info.ReadVersion = () => null; break;
                        case "type": f.Version.ReadType = () => null; break;
                        case "typeGuid": f.Type.ReadGuid = () => Guid.Empty; break;
                        case "versionGuid": f.Version.ReadGuid = () => Guid.Empty; break;
                    }
                    NoIdentity(PlcLibraryInstanceCapture.Read(f.Item), "unavailable");
                });
            foreach (var stage in new[] { "service", "instance", "equality", "version", "type", "typeGuid", "versionGuid" })
                test("library instance retains unavailable status after native " + stage + " failure", () => {
                    var f = new Fixture(); var failure = new EngineeringException("Private native diagnostic");
                    switch (stage)
                    {
                        case "service": f.Item.ReadService = () => throw failure; break;
                        case "instance": f.Info.ReadInstance = () => throw failure; break;
                        case "equality": f.Item.Compare = _ => throw failure; break;
                        case "version": f.Info.ReadVersion = () => throw failure; break;
                        case "type": f.Version.ReadType = () => throw failure; break;
                        case "typeGuid": f.Type.ReadGuid = () => throw failure; break;
                        case "versionGuid": f.Version.ReadGuid = () => throw failure; break;
                    }
                    var actual = PlcLibraryInstanceCapture.Read(f.Item); NoIdentity(actual, "unavailable");
                    Equal("nativeReadFailed", actual.Value<string>("reason"));
                    Equal(false, actual.ToString().Contains("Private native diagnostic"));
                });
            test("library instance does not hide unexpected non-native exceptions", () => {
                var f = new Fixture(); var failure = new InvalidOperationException("unexpected");
                f.Item.ReadService = () => throw failure;
                try { PlcLibraryInstanceCapture.Read(f.Item); }
                catch (InvalidOperationException actual) when (ReferenceEquals(actual, failure)) { return; }
                throw new Exception("Unexpected exception was hidden.");
            });
            test("library instance result is frozen and later captures observe new identities", () => {
                var f = new Fixture(); var first = PlcLibraryInstanceCapture.Read(f.Item);
                string original = first.ToString(); var next = new LibraryTypeVersion { ReadType = () => f.Type };
                f.Info.ReadVersion = () => next;
                var second = PlcLibraryInstanceCapture.Read(f.Item);
                Equal(original, first.ToString()); Equal(next.Identity.ToString("D"), second.Value<string>("versionGuid"));
                Equal(false, first.Value<string>("versionGuid") == second.Value<string>("versionGuid"));
            });
        }
        internal static void Equal<T>(T expected, T actual)
        { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void NoIdentity(JObject value, string status)
        {
            Equal(status, value.Value<string>("status")); Equal(false, value.Value<bool>("nativeQualified"));
            Equal(true, value["typeGuid"] == null && value["versionGuid"] == null);
        }
        internal sealed class Fixture
        {
            internal readonly NativeObject Item;
            internal readonly LibraryType Type = new LibraryType();
            internal readonly LibraryTypeVersion Version = new LibraryTypeVersion();
            internal readonly LibraryTypeInstanceInfo Info = new LibraryTypeInstanceInfo();
            internal Fixture(string kind = "FB")
            {
                Item = kind == "FB" ? (NativeObject)new FB() : kind == "FC" ? new FC() : new PlcType();
                Item.ReadService = () => Info; Info.ReadInstance = () => Item; Info.ReadVersion = () => Version; Version.ReadType = () => Type;
            }
        }
    }
}

// Synthetic SDK shapes exercise linked production logic; they do not validate Siemens signatures.
namespace Siemens.Engineering
{
    internal interface IEngineeringObject { }
    internal interface IEngineeringServiceProvider { T GetService<T>() where T : class; }
    internal sealed class EngineeringException : Exception { internal EngineeringException(string message) : base(message) { } }
    internal class NativeObject : IEngineeringObject, IEngineeringServiceProvider
    {
        internal Guid Identity = Guid.NewGuid();
        internal Func<LibraryTypeInstanceInfo> ReadService;
        internal Func<object, bool> Compare;
        internal int ServiceReads, EqualityReads;
        public T GetService<T>() where T : class { ServiceReads++; return ReadService() as T; }
        public override bool Equals(object other) { EqualityReads++; return Compare != null ? Compare(other) : other is NativeObject native && native.Identity == Identity; }
        public override int GetHashCode() => Identity.GetHashCode();
    }
}
namespace Siemens.Engineering.SW.Blocks
{
    internal sealed class FB : NativeObject { }
    internal sealed class FC : NativeObject { }
    internal sealed class InstanceDB : NativeObject { }
}
namespace Siemens.Engineering.SW.Types { internal sealed class PlcType : NativeObject { } }
namespace Siemens.Engineering.Library.Types
{
    internal sealed class LibraryTypeInstanceInfo
    {
        internal Func<IEngineeringObject> ReadInstance;
        internal Func<LibraryTypeVersion> ReadVersion;
        internal int InstanceReads, VersionReads;
        internal IEngineeringObject LibraryTypeInstance { get { InstanceReads++; return ReadInstance(); } }
        internal LibraryTypeVersion LibraryTypeVersion { get { VersionReads++; return ReadVersion(); } }
    }
    internal sealed class LibraryType
    {
        internal readonly Guid Identity = Guid.NewGuid();
        internal Func<Guid> ReadGuid;
        internal int GuidReads;
        internal Guid Guid { get { GuidReads++; return ReadGuid == null ? Identity : ReadGuid(); } }
    }
    internal sealed class LibraryTypeVersion
    {
        internal readonly Guid Identity = Guid.NewGuid();
        internal Func<LibraryType> ReadType;
        internal Func<Guid> ReadGuid;
        internal int TypeReads, GuidReads;
        internal int DependencyReads;
        internal Func<IEnumerable<LibraryTypeVersion>> ReadDependencies = () => new LibraryTypeVersion[0];
        internal IEnumerable<LibraryTypeVersion> Dependencies { get { DependencyReads++; return ReadDependencies(); } }
        internal LibraryType TypeObject { get { TypeReads++; return ReadType(); } }
        internal Guid Guid { get { GuidReads++; return ReadGuid == null ? Identity : ReadGuid(); } }
    }
}
