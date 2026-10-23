using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Collaboration;

internal static class TeamcenterReviewChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        void Reject(Action action) { try { action(); } catch (Exception e) when (e is BridgeException or JsonException or ArgumentException or InvalidOperationException) { checks++; return; } throw new Exception("Expected Teamcenter review refusal."); }
        var input = TeamcenterReview.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/teamcenter/observations.json")));
        byte[] manifest = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures/teamcenter/local-manifest.json"));
        JsonObject Request() => input.DeepClone().AsObject();
        JsonObject Build(JsonObject value) => TeamcenterReview.Build(value, _ => manifest);
        var report = Build(input);
        Check((int)report["summary"]!["manifestCount"]! == 1, "Shared manifests resolve once.");
        Check(report["issues"]!.AsArray().Any(x => (string?)x!["code"] == "reportedVersionMismatch"), "Mismatch is retained without overwrite permission.");
        Check(report["issues"]!.AsArray().Any(x => (string?)x!["code"] == "unknownLock"), "Unknown lock is not unlocked.");
        Check(report["issues"]!.AsArray().Any(x => (string?)x!["code"] == "incompleteManifest"), "Partial local coverage remains visible.");
        Check((bool)report["manifests"]![0]!["manifestBytesVerified"]! && !(bool)report["manifests"]![0]!["fileBytesVerified"]!, "Manifest integrity does not prove file contents.");
        Check(!(bool)report["checkoutOwnershipVerified"]! && !(bool)report["nativeExecutable"]!, "No checkout authority.");
        Check(report["changes"]!.AsArray().Any(x => (string?)x!["section"] == "metadata"), "Connection epoch change retained.");
        var identical = Request(); identical["after"] = identical["before"]!.DeepClone(); Check((int)Build(identical)["summary"]!["changeCount"]! == 0, "Identical declarations have no changes.");
        foreach (string section in new[] { "items", "revisions", "datasets", "localCopies" })
        {
            var value = Request(); value["after"]![section]!["rows"] = new JsonArray();
            Check(Build(value)["changes"]!.AsArray().Any(x => (string?)x!["section"] == section && (string?)x!["status"] == "onlyBefore"), "Missing declaration is not native deletion: " + section);
        }
        foreach (string state in new[] { "partial", "unknown", "denied", "failed", "unavailable" })
        {
            var value = Request(); value["after"]!["items"] = new JsonObject { ["coverage"] = state, ["diagnostic"] = "public reason", ["rows"] = new JsonArray() };
            Check(Build(value)["issues"]!.AsArray().Any(x => (string?)x!["code"] == state), "Coverage retained: " + state);
        }
        void Bad(Action<JsonObject> change) { var value = Request(); change(value); Reject(() => Build(value)); }
        Bad(x => x["after"]!["captureId"] = x["before"]!["captureId"]!.DeepClone());
        Bad(x => x["after"]!["connection"]!["instance"] = "other");
        Bad(x => x["after"]!["connection"]!["serverUrl"] = "https://other.invalid/");
        foreach (string url in new[] { "https://u:p@tc.invalid/", "https://tc.invalid/?token=x", "https://tc.invalid/#secret", "file:///tmp/", "https://tc.invalid\\path" }) Bad(x => x["after"]!["connection"]!["serverUrl"] = url);
        foreach (string secret in new[] { "password", "SessionToken", "accessToken", "Authorization" })
        {
            Bad(x => x["after"]!["connection"]![secret] = "not-public");
            Bad(x => x["after"]!["items"]!["rows"]![0]!["properties"]![0]!["name"] = secret);
        }
        foreach (string section in new[] { "items", "revisions", "datasets", "localCopies" })
        {
            Bad(x => x["after"]![section]!["rows"]!.AsArray().Add(x["after"]![section]!["rows"]![0]!.DeepClone()));
            Bad(x => x["after"]![section]!["coverage"] = "failed");
            Bad(x => x["after"]![section]!["rows"]![0]!["extra"] = true);
        }
        Bad(x => x["after"]!["datasets"]!["rows"]![0]!["datasetType"] = "T4TiaLibraryDataset");
        Bad(x => x["after"]!["datasets"]!["rows"]![0]!["lock"]!["owner"] = "unknown-owner");
        Bad(x => x["after"]!["localCopies"]!["rows"]![0]!["path"] = "another-root");
        Bad(x => x["after"]!["localCopies"]!["rows"]![0]!["manifest"]!["sha256"] = new string('0', 64));
        Bad(x => x["after"]!["localCopies"]!["rows"]![0]!["manifest"]!["uploadRef"] = "arbitrary-file");
        void BadManifest(Action<JsonObject> change)
        {
            var document = TeamcenterReview.Parse(manifest); change(document); byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString()); var value = Request();
            foreach (string phase in new[] { "before", "after" }) value[phase]!["localCopies"]!["rows"]![0]!["manifest"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Reject(() => TeamcenterReview.Build(value, _ => bytes));
        }
        foreach (string path in new[] { "../secret", "/absolute", "C:/absolute", "folder\\file", "CON.txt", "a.", "a ", "a//b" }) BadManifest(x => x["files"]![0]!["path"] = path);
        BadManifest(x => x["files"]![1]!["path"] = "project.AP21");
        BadManifest(x => x["files"]![1]!["path"] = "Project.ap21/child");
        BadManifest(x => x["files"]![0]!["bytes"] = 9007199254740992L);
        BadManifest(x => x["files"]![0]!["bytes"] = -1);
        BadManifest(x => x["coverage"] = "failed");
        Reject(() => TeamcenterReview.Build(Request(), _ => manifest.Concat(new byte[] { 32 }).ToArray()));
        Reject(() => TeamcenterReview.Parse(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
        Reject(() => TeamcenterReview.Parse(new byte[] { 0xff })); Reject(() => TeamcenterReview.Parse(new byte[TeamcenterReview.InputLimit + 1]));
        foreach (var bound in new[] { ("items", 256), ("revisions", 512), ("datasets", 1024), ("localCopies", 128) })
            Bad(x => { var rows = x["after"]![bound.Item1]!["rows"]!.AsArray(); var row = rows[0]!.DeepClone(); while (rows.Count <= bound.Item2) rows.Add(row.DeepClone()); });
        var large = Request(); var datasets = large["after"]!["datasets"]!["rows"]!.AsArray(); var sample = datasets[0]!.DeepClone(); datasets.Clear();
        for (int i = 0; i < 1024; i++) { var row = sample.DeepClone(); row["name"] = "Dataset" + i; datasets.Add(row); }
        Check(Build(large)["observations"]!.AsArray().Count(x => (string?)x!["phase"] == "after" && (string?)x!["section"] == "datasets") == 1024, "Maximum dataset scope retained.");
        var manyManifests = Request(); var copies = manyManifests["after"]!["localCopies"]!["rows"]!.AsArray(); var local = copies[0]!.DeepClone(); copies.Clear();
        for (int i = 0; i < 15; i++) { var row = local.DeepClone(); row["dataset"]!["name"] = "Copy" + i; row["manifest"]!["uploadRef"] = "upload:" + i.ToString("x32"); copies.Add(row); }
        Check((int)Build(manyManifests)["summary"]!["manifestCount"]! == 16, "Sixteen distinct uploaded manifests accepted, including original before phase.");
        var seventeenth = local.DeepClone(); seventeenth["dataset"]!["name"] = "Copy16"; seventeenth["manifest"]!["uploadRef"] = "upload:" + 16.ToString("x32"); copies.Add(seventeenth); Reject(() => Build(manyManifests));
        var overBytes = Request(); byte[] oversized = new byte[TeamcenterReview.ManifestLimit + 1];
        foreach (string phase in new[] { "before", "after" }) overBytes[phase]!["localCopies"]!["rows"]![0]!["manifest"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(oversized));
        Reject(() => TeamcenterReview.Build(overBytes, _ => oversized));
        BadManifest(x => { var files = x["files"]!.AsArray(); var file = files[0]!.DeepClone(); while (files.Count <= 4096) files.Add(file.DeepClone()); });
        var library = Request();
        foreach (string phase in new[] { "before", "after" })
        {
            foreach (string section in new[] { "items", "revisions", "datasets" }) library[phase]![section]!["rows"]![0]!["itemType"] = "globalLibrary";
            library[phase]!["datasets"]!["rows"]![0]!["datasetType"] = "T4TiaLibraryDataset";
            library[phase]!["localCopies"]!["rows"]![0]!["dataset"]!["itemType"] = "globalLibrary";
            library[phase]!["localCopies"]!["rows"]![0]!["dataset"]!["datasetType"] = "T4TiaLibraryDataset";
        }
        Check(!Build(library)["issues"]!.AsArray().Any(x => ((string?)x!["code"])?.EndsWith("NotSupplied", StringComparison.Ordinal) == true), "Global-library identities resolve independently without project assumptions.");
        Console.WriteLine($"PASS Teamcenter supplied observations: {checks} checks; manifest bytes only, no native server execution.");
    }
}
