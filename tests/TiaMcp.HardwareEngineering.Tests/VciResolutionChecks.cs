using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// Pure decisions of vci_conflict_resolve: the Host-bound package files and the live-mapping refusals.
internal static class VciResolutionChecks
{
    private static int _count;

    private static void Pass(string name, bool condition)
    {
        if (!condition) throw new Exception("Check failed: " + name);
        _count++; Console.WriteLine("PASS " + name);
    }

    private static string Code(Action action)
    {
        try { action(); return null; }
        catch (BridgeException error) { return error.Code; }
    }

    internal static int Run()
    {
        bool Safe(string segment) => segment.Length is > 0 and <= 128 && segment.Trim(' ', '.') == segment && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        byte[] document = [0xEF, 0xBB, 0xBF, 65, 13, 10];
        string sha = Convert.ToHexStringLower(SHA256.HashData(document));
        JObject Row(string path, string hash, byte[] bytes) => new() { ["path"] = path, ["sha256"] = hash, ["bytes"] = bytes?.Length,
            ["contentBase64"] = bytes == null ? null : Convert.ToBase64String(bytes) };
        JObject Payload(params JObject[] rows) => new() { ["files"] = new JArray(rows) };
        var files = VciResolutionRules.Files(Payload(Row("VciFc.scl", sha, document)), "files", true, Safe);
        Pass("bound package bytes keep path, hash and exact content", files.Count == 1 && files["VciFc.scl"].Sha256 == sha && files["VciFc.scl"].Content.SequenceEqual(document));
        Pass("hash-only rows carry no content", VciResolutionRules.Files(Payload(Row("dir/VciFc.scl", sha, null)), "files", false, Safe)["dir/VciFc.scl"].Content == null);
        foreach (var (name, payload, code) in new[]
        {
            ("traversal", Payload(Row("../VciFc.scl", sha, document)), "InvalidRequest"),
            ("absolute path", Payload(Row("C:/VciFc.scl", sha, document)), "InvalidRequest"),
            ("empty path", Payload(Row("", sha, document)), "InvalidRequest"),
            ("short hash", Payload(Row("VciFc.scl", sha[..63], document)), "InvalidRequest"),
            ("tampered content", Payload(Row("VciFc.scl", sha, [1, 2, 3])), "ArtifactChanged"),
            ("missing content", Payload(Row("VciFc.scl", sha, null)), "InvalidRequest"),
            ("repeated path", Payload(Row("VciFc.scl", sha, document), Row("VciFc.scl", sha, document)), "InvalidRequest"),
            ("non-object row", new JObject { ["files"] = new JArray("VciFc.scl") }, "InvalidRequest"),
            ("no array", new JObject(), "InvalidRequest"),
            ("more than 16 files", Payload(Enumerable.Range(0, 17).Select(i => Row("f" + i + ".scl", sha, document)).ToArray()), "InvalidRequest"),
        })
            Pass("package files refuse " + name, Code(() => VciResolutionRules.Files(payload, "files", true, Safe)) == code);
        var lengthLie = Row("VciFc.scl", sha, document); lengthLie["bytes"] = document.Length + 1;
        Pass("package files refuse a length that differs from the content", Code(() => VciResolutionRules.Files(Payload(lengthLie), "files", true, Safe)) == "ArtifactChanged");

        string remote = new('a', 64), live = new('c', 64);
        var reviewed = new Dictionary<string, VciResolutionRules.PackageFile> { ["VciFc.scl"] = new() { Sha256 = remote } };
        var result = new Dictionary<string, VciResolutionRules.PackageFile> { ["VciFc.scl"] = new() { Sha256 = sha, Content = document } };
        var none = new Dictionary<string, VciResolutionRules.PackageFile>();
        var package = new Dictionary<string, string> { ["VciFc.scl"] = remote };
        bool InPackage(string path) => path.StartsWith("VciFc.", StringComparison.OrdinalIgnoreCase);
        string Refusal(string take = "remote", string direction = "WorkspaceToProject", bool changed = true, string current = null, string planned = null,
            IDictionary<string, string> files = null, IDictionary<string, VciResolutionRules.PackageFile> review = null, IDictionary<string, VciResolutionRules.PackageFile> write = null) =>
            VciResolutionRules.Refusal(take, direction, changed, current ?? live, planned ?? live, files ?? package, review ?? reviewed, write ?? result, InPackage)?[0];
        Pass("take=remote over the exact reviewed remote side is admitted", Refusal() == null);
        Pass("take=merged writing the reviewed package is admitted", Refusal("merged") == null);
        Pass("take=native writes nothing and is admitted", Refusal("native", "ProjectToWorkspace", write: none) == null);
        Pass("take and direction must agree", Refusal("native") == "InvalidRequest" && Refusal("remote", "ProjectToWorkspace") == "InvalidRequest");
        Pass("a plan without the native object hash is refused", VciResolutionRules.Refusal("remote", "WorkspaceToProject", true, live, null, package, reviewed, result, InPackage)[0] == "InvalidRequest");
        Pass("no workspace-side change needs no resolution", Refusal(changed: false) == "VciResolutionNotNeeded");
        Pass("reviewed files outside the mapping's package are refused",
            Refusal(review: new Dictionary<string, VciResolutionRules.PackageFile> { ["Other.scl"] = new() { Sha256 = remote } }) == "VciResolutionMismatch");
        Pass("a workspace package other than the reviewed remote side is stale", Refusal(files: new Dictionary<string, string> { ["VciFc.scl"] = live }) == "VciResolutionStale");
        Pass("an extra package file is stale", Refusal(files: new Dictionary<string, string> { ["VciFc.scl"] = remote, ["VciFc.s7res"] = remote }) == "VciResolutionStale");
        Pass("a project object edited after the comparison is stale", Refusal(current: new string('d', 64)) == "VciResolutionStale"
            && Refusal("native", "ProjectToWorkspace", current: new string('d', 64), write: none) == "VciResolutionStale");
        Pass("take=native cannot write bytes", Refusal("native", "ProjectToWorkspace") == "InvalidRequest");
        Pass("remote and merged must write exactly the reviewed files", Refusal(write: none) == "InvalidRequest");
        return _count;
    }
}
