using System.Security.Cryptography;
using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

internal static class RetainedPackageChecks
{
    internal static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action test) => tests.Add(("VCI retained package " + name, test));
        VciFileInput File(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));
        var files = new[] { File("fixture.scl", "source"), File(".vci/workspace.vci.config", "de-DE"), new("unknown.bin", new byte[] { 0, 255 }) };
        var reviewed = files.Select(x => new VciFileEvidence(x.RelativePath, Convert.ToHexStringLower(SHA256.HashData(x.Bytes.Span)), x.Bytes.Length)).ToArray();
        Test("all supplied native and unknown companion bytes participate", () =>
        {
            var r = VciRetainedPackagePreservation.Inspect(reviewed, files.Reverse().ToArray());
            True(r.RetainedBytesPreserved); True(!r.NativeImportQualified); Equal("exactRetainedPackageBytesOnly", r.Scope);
        });
        Test("changed workspace language and unknown companions fail", () =>
        {
            foreach (int index in new[] { 1, 2 })
            {
                var changed = files.ToArray(); changed[index] = File(changed[index].RelativePath, "changed");
                var r = VciRetainedPackagePreservation.Inspect(reviewed, changed);
                True(!r.RetainedBytesPreserved); True(r.ChangedFiles.SequenceEqual(new[] { changed[index].RelativePath }));
            }
        });
        Test("missing added and case-renamed companions cannot pass", () =>
        {
            var missing = VciRetainedPackagePreservation.Inspect(reviewed, files.Take(2).ToArray());
            True(!missing.RetainedBytesPreserved); True(missing.MissingFiles.Contains("unknown.bin"));
            var extra = VciRetainedPackagePreservation.Inspect(reviewed, [.. files, File("added.bin", "added")]);
            True(!extra.RetainedBytesPreserved); True(extra.UnreviewedFiles.Contains("added.bin"));
            var renamed = files.ToArray(); renamed[2] = renamed[2] with { RelativePath = "Unknown.bin" };
            var r = VciRetainedPackagePreservation.Inspect(reviewed, renamed);
            True(!r.RetainedBytesPreserved); Equal(1, r.MissingFiles.Count); Equal(1, r.UnreviewedFiles.Count);
        });
        Test("a matching hash with wrong reviewed byte count fails", () =>
        {
            var wrong = reviewed.ToArray(); wrong[0] = wrong[0] with { Bytes = wrong[0].Bytes + 1 };
            True(!VciRetainedPackagePreservation.Inspect(wrong, files).RetainedBytesPreserved);
        });
        Test("review inputs are immutable snapshots", () =>
        {
            var input = reviewed.ToArray(); var bytes = new byte[] { 0, 255 }; var observed = files.ToArray(); observed[2] = new("unknown.bin", bytes);
            var r = VciRetainedPackagePreservation.Inspect(input, observed); input[0] = reviewed[1]; bytes[0] = 1;
            True(r.RetainedBytesPreserved); True(r.ReviewedFiles.Any(x => x.RelativePath == "fixture.scl"));
            Equal(reviewed[2].Sha256, r.ObservedFiles.Single(x => x.RelativePath == "unknown.bin").Sha256);
        });
        Test("reviewed aliases and ancestor collisions are rejected", () =>
        {
            Reject("VciPathCollision", () => VciRetainedPackagePreservation.Inspect([reviewed[0], reviewed[0] with { RelativePath = "FIXTURE.SCL" }], files));
            Reject("VciPathCollision", () => VciRetainedPackagePreservation.Inspect([reviewed[0], reviewed[0] with { RelativePath = "fixture.scl/child" }], files));
            Reject("VciPathCollision", () => VciRetainedPackagePreservation.Inspect(reviewed, [.. files, files[0] with { RelativePath = "FIXTURE.SCL" }]));
        });
        Test("unsafe paths links malformed hashes and bounds are rejected", () =>
        {
            Reject("VciUnsafePath", () => VciRetainedPackagePreservation.Inspect([reviewed[0] with { RelativePath = "../bad" }], files));
            Reject("VciUnsafePath", () => VciRetainedPackagePreservation.Inspect(reviewed, [files[0] with { IsLink = true }]));
            Reject("VciInvalidHash", () => VciRetainedPackagePreservation.Inspect([reviewed[0] with { Sha256 = "invalid" }], files));
            Reject("VciLimitExceeded", () => VciRetainedPackagePreservation.Inspect([reviewed[0] with { Bytes = long.MaxValue }], files));
            Reject("VciLimitExceeded", () => VciRetainedPackagePreservation.Inspect([reviewed[0] with { Bytes = -1 }], files));
            Reject("VciInvalidPreservationReview", () => VciRetainedPackagePreservation.Inspect([], files));
        });
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
