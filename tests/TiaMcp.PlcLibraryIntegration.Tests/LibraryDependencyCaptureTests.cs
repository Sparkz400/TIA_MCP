using Siemens.Engineering.Library.Types;
using TiaMcp.Openness.V21;

internal static class LibraryDependencyCaptureTests
{
    internal static void Run(Action<string, Action> test)
    {
        test("dependency capture retains an empty observed collection", () => {
            var owner = new LibraryTypeVersion(); var capture = LibraryDependencyCapture.Read(owner);
            Check(capture.Versions.Length == 0 && capture.VersionGuids.Length == 0 && owner.DependencyReads == 1);
        });
        test("dependency capture preserves 1000 ordered exact native objects and GUIDs", () => {
            var children = Enumerable.Range(0, 1000).Select(_ => new LibraryTypeVersion()).ToArray();
            var ids = children.Select(c => c.Identity.ToString("D")).ToArray();
            var owner = new LibraryTypeVersion { ReadDependencies = () => children };
            var capture = LibraryDependencyCapture.Read(owner);
            Check(capture.Versions.SequenceEqual(children) && capture.VersionGuids.SequenceEqual(ids));
            Check(owner.DependencyReads == 1 && children.All(c => c.GuidReads == 1));
        });
        test("dependency capture stops at the first excess item and disposes enumeration", () => {
            int yielded = 0, disposed = 0; var child = new LibraryTypeVersion();
            IEnumerable<LibraryTypeVersion> Excess()
            {
                try { while (true) { if (++yielded > 1001) throw new Exception("Dependency bound was bypassed."); yield return child; } }
                finally { disposed++; }
            }
            Reject(() => LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = Excess }));
            Check(yielded == 1001 && disposed == 1 && child.GuidReads == 0);
        });
        test("dependency capture freezes original GUID evidence without rereading native state", () => {
            var child = new LibraryTypeVersion(); string original = child.Identity.ToString("D");
            var children = new[] { child }; var owner = new LibraryTypeVersion { ReadDependencies = () => children };
            var capture = LibraryDependencyCapture.Read(owner);
            owner.ReadDependencies = () => throw new Exception("Unexpected native re-enumeration.");
            child.ReadGuid = () => throw new Exception("Unexpected identity reread.");
            children[0] = new LibraryTypeVersion();
            Check(capture.Versions.Length == 1 && ReferenceEquals(capture.Versions[0], child));
            Check(capture.VersionGuids.Single() == original && owner.DependencyReads == 1 && child.GuidReads == 1);
        });
        test("dependency capture keeps repeated observations for the closure owner to resolve", () => {
            var child = new LibraryTypeVersion(); var capture = LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => new[] { child, child } });
            Check(capture.Versions.Length == 2 && capture.VersionGuids[0] == capture.VersionGuids[1]);
        });
        test("dependency freshness check accepts a separately observed identical list", () => {
            var child = new LibraryTypeVersion(); var owner = new LibraryTypeVersion { ReadDependencies = () => new[] { child } };
            var original = LibraryDependencyCapture.Read(owner);
            LibraryDependencyCapture.RequireUnchanged(owner, original.VersionGuids);
            Check(owner.DependencyReads == 2 && original.VersionGuids.Single() == child.Identity.ToString("D"));
        });
        test("dependency freshness check refuses changed export-time membership", () => {
            var owner = new LibraryTypeVersion { ReadDependencies = () => new[] { new LibraryTypeVersion() } };
            var original = LibraryDependencyCapture.Read(owner);
            Reject(() => LibraryDependencyCapture.RequireUnchanged(owner, original.VersionGuids), "DependencyConflict");
        });
        test("dependency freshness check conservatively refuses reordered observations", () => {
            var children = new[] { new LibraryTypeVersion(), new LibraryTypeVersion() };
            var owner = new LibraryTypeVersion { ReadDependencies = () => children };
            var original = LibraryDependencyCapture.Read(owner);
            owner.ReadDependencies = () => children.Reverse();
            Reject(() => LibraryDependencyCapture.RequireUnchanged(owner, original.VersionGuids), "DependencyConflict");
        });
        test("dependency capture refuses missing owner", () => Reject(() => LibraryDependencyCapture.Read(null)));
        test("dependency capture refuses unavailable collection", () => Reject(() => LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => null })));
        test("dependency capture refuses unresolved native entry", () => Reject(() => LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => new LibraryTypeVersion[] { null } })));
        test("dependency capture refuses empty native GUID", () => Reject(() => LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => new[] { new LibraryTypeVersion { ReadGuid = () => Guid.Empty } } })));
        test("dependency getter failure cannot become an empty successful observation", () => {
            var failure = new InvalidOperationException("native getter failed");
            try { LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => throw failure }); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { return; }
            throw new Exception("Getter failure was lost.");
        });
        test("dependency iterator failure disposes and never returns a partial capture", () => {
            int disposed = 0; var failure = new InvalidOperationException("native iterator failed");
            IEnumerable<LibraryTypeVersion> Broken()
            {
                try { yield return new LibraryTypeVersion(); throw failure; }
                finally { disposed++; }
            }
            try { LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = Broken }); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { Check(disposed == 1); return; }
            throw new Exception("Iterator failure was lost.");
        });
        test("dependency identity failure cannot publish a partial GUID list", () => {
            var failure = new InvalidOperationException("native identity failed");
            try { LibraryDependencyCapture.Read(new LibraryTypeVersion { ReadDependencies = () => new[] { new LibraryTypeVersion { ReadGuid = () => throw failure } } }); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { return; }
            throw new Exception("Identity failure was lost.");
        });
    }

    private static void Check(bool value) { if (!value) throw new Exception("Dependency observation differs."); }
    private static void Reject(Action action, string code = "DependencyUnresolved")
    {
        try { action(); }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new Exception("Expected " + code + ".");
    }
}

namespace Siemens.Engineering.Library.Types
{
    // Synthetic native shape for the linked production capture helper; no Siemens assembly is loaded.
    internal sealed class LibraryTypeVersion
    {
        internal readonly Guid Identity = Guid.NewGuid();
        internal Func<IEnumerable<LibraryTypeVersion>> ReadDependencies = () => Array.Empty<LibraryTypeVersion>();
        internal Func<Guid> ReadGuid;
        internal int DependencyReads, GuidReads;
        internal IEnumerable<LibraryTypeVersion> Dependencies { get { DependencyReads++; return ReadDependencies(); } }
        internal Guid Guid { get { GuidReads++; return ReadGuid == null ? Identity : ReadGuid(); } }
    }
}
