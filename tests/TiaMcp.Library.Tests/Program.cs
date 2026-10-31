using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Host;
using LibraryReferenceEdge = TiaMcp.Openness.V21.LibraryReferenceEdge;
using LibraryReferenceReachability = TiaMcp.Openness.V21.LibraryReferenceReachability;

if (args.Length == 2 && args[0] == "--scale-qualification")
    return ScaleQualification.Run(args[1]);
if (args.Length == 3 && args[0] == "--pagination-qualification")
    return PaginationQualification.Run(args[1], args[2]);

var testParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "library-tests"));
var root = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Test(string name, Action<string> test)
{
    var work = Path.Combine(root, (passed + failed).ToString());
    Directory.CreateDirectory(work);
    try { test(work); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Reject(string code, Action action)
{
    try { action(); }
    catch (BridgeException error) when (error.Code == code) { return; }
    throw new Exception("Expected bridge error " + code);
}
static void RejectReachability(string message, Action action)
{
    try { action(); }
    catch (ArgumentException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
    throw new Exception("Expected reference-proof rejection containing: " + message);
}
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string Write(string work, string name, byte[] bytes) { Directory.CreateDirectory(work); var path = Path.Combine(work, name); File.WriteAllBytes(path, bytes); return path; }
static byte[] Zip(params (string Name, byte[] Bytes, int? Attributes)[] files)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        foreach (var (name, bytes, attributes) in files)
        {
            var entry = archive.CreateEntry(name);
            if (attributes.HasValue) entry.ExternalAttributes = attributes.Value;
            using var stream = entry.Open(); stream.Write(bytes);
        }
    return output.ToArray();
}
static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);
static PackageStatus StageZip(string work, byte[] zip, PackageIntakeLimits? limits = null) =>
    new PackageStore(Path.Combine(work, "artifacts"), limits).Stage(hostPath: Write(work, "source.zip", zip));

Test("upload resume, exact retry and immutable identity", work =>
{
    var bytes = Text("a known payload");
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("sample.zal21", bytes.Length, Hash(bytes));
    store.Write(begin.UploadId, 0, Convert.ToBase64String(bytes[..5]));
    Equal(5L, new ArtifactUploadStore(work).Status(begin.UploadId).ReceivedBytes);
    Equal(5L, store.Write(begin.UploadId, 0, Convert.ToBase64String(bytes[..5])).ReceivedBytes);
    store.Write(begin.UploadId, 5, Convert.ToBase64String(bytes[5..]));
    var commit = new ArtifactUploadStore(work).Commit(begin.UploadId);
    Equal("committed", commit.State);
    Equal(commit.ArtifactRef, store.Commit(begin.UploadId).ArtifactRef);
    Equal(Hash(bytes), store.ResolveCommitted(commit.ArtifactRef!).Sha256);
});
Test("offset, changed retry, overlap and incomplete commit reject", work =>
{
    var store = new ArtifactUploadStore(work);
    var id = store.Begin("a.zip", 10, Hash(new byte[10])).UploadId;
    Reject("UploadOffsetMismatch", () => store.Write(id, 1, "AA=="));
    store.Write(id, 0, "AAAA");
    Reject("UploadChunkMismatch", () => store.Write(id, 0, "AQ=="));
    Reject("UploadOffsetMismatch", () => store.Write(id, 2, "AAA="));
    Reject("UploadIncomplete", () => store.Commit(id));
});
Test("wrong hash never yields usable artifact", work =>
{
    var store = new ArtifactUploadStore(work);
    var id = store.Begin("a.zip", 1, Hash([1])).UploadId;
    store.Write(id, 0, "AA==");
    Reject("PackageChanged", () => store.Commit(id));
    Reject("UploadIncomplete", () => store.ResolveCommitted("upload:" + id));
});
Test("crash tail is discarded before resume", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("a.zip", 3, Hash([1, 2, 3]));
    store.Write(begin.UploadId, 0, "AQ==");
    File.WriteAllBytes(Path.Combine(begin.HostPath, "data.bin"), [1, 9, 9]);
    new ArtifactUploadStore(work).Write(begin.UploadId, 1, "AgM=");
    Equal("committed", store.Commit(begin.UploadId).State);
});
Test("abort and quota reservations", work =>
{
    var store = new ArtifactUploadStore(work, new PackageIntakeLimits { MaxInputBytes = 100, MaxStoredBytes = 10000, MaxUploads = 1 });
    var id = store.Begin("a.zip", 1, Hash([1])).UploadId;
    Reject("PackageLimitExceeded", () => store.Begin("b.zip", 1, Hash([1])));
    store.Abort(id);
    Equal("aborted", store.Abort(id).State);
    Reject("UploadNotWritable", () => store.Write(id, 0, "AQ=="));
    _ = store.Begin("b.zip", 1, Hash([1]));
});
Test("cross-process lock rejects concurrent writes", work =>
{
    var store = new ArtifactUploadStore(work);
    var id = store.Begin("a.zip", 1, Hash([1])).UploadId;
    using var held = new FileStream(Path.Combine(work, "library-intake", "intake.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Reject("IntakeBusy", () => new ArtifactUploadStore(work).Write(id, 0, "AQ=="));
});
Test("committed corruption detected", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("a.zip", 1, Hash([1]));
    store.Write(begin.UploadId, 0, "AQ==");
    var commit = store.Commit(begin.UploadId);
    File.WriteAllBytes(Path.Combine(begin.HostPath, "data.bin"), [2]);
    Reject("PackageChanged", () => store.ResolveCommitted(commit.ArtifactRef!));
});
Test("zip candidate classification, documentation and paged manifest", work =>
{
    var status = StageZip(work, Zip(("Lib/Lib.al21", Text("marker"), null), ("Lib/System/data.bin", [1, 2], null),
        ("Other.zal20", [3], null), ("Example.zap21", [4], null), ("program.scl", Text("FUNCTION"), null), ("README.md", Text("example"), null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    Equal(4, status.Candidates);
    var page = store.Inspect(status.PackageId, count: 2);
    Equal(2, page.Items.Length);
    Equal((int?)2, page.NextOffset);
    var all = store.Inspect(status.PackageId).Items.Cast<PackageCandidate>().ToArray();
    Equal(1, all.Count(x => x.Kind == "globalLibraryDirectory" && x.ReadyForNativeInspection));
    Equal(1, all.Count(x => x.Kind == "sourceFile" && !x.ReadyForNativeInspection));
    Equal(1, store.Inspect(status.PackageId, "documents").Total);
    Equal(true, File.Exists(store.ResolveCandidate(status.PackageId, all.Single(x => x.Kind == "globalLibraryArchive").CandidateId)));
    Reject("InvalidRequest", () => store.Inspect(status.PackageId, count: 201));
});
Test("standalone native markers are incomplete", work =>
{
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var staged = store.Stage(hostPath: Write(work, "Library.al21", [1]));
    var candidate = (PackageCandidate)store.Inspect(staged.PackageId).Items[0];
    Equal(false, candidate.ReadyForNativeInspection);
    Equal("missingCompanionFiles", candidate.Completeness);
    Reject("PackageInvalid", () => store.ResolveCandidate(staged.PackageId, candidate.CandidateId));
});
Test("complete local directory copied without modifying originals", work =>
{
    var source = Path.Combine(work, "source"); Directory.CreateDirectory(Path.Combine(source, "System"));
    File.WriteAllBytes(Path.Combine(source, "Library.al21"), [1]); File.WriteAllBytes(Path.Combine(source, "System", "data.bin"), [2]);
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var staged = store.Stage(hostPath: source);
    Equal(2L, staged.InputBytes);
    Equal(true, ((PackageCandidate)store.Inspect(staged.PackageId).Items[0]).ReadyForNativeInspection);
    Equal(2, Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length);
});
Test("uploaded zip and local zip hashes match", work =>
{
    var bytes = Zip(("Library.zal21", [1, 2, 3], null));
    var artifacts = Path.Combine(work, "artifacts");
    var upload = new ArtifactUploadStore(artifacts);
    var begin = upload.Begin("Library.zip", bytes.Length, Hash(bytes));
    upload.Write(begin.UploadId, 0, Convert.ToBase64String(bytes));
    var reference = upload.Commit(begin.UploadId).ArtifactRef;
    var store = new PackageStore(artifacts);
    Equal(store.Stage(artifactRef: reference).SourceSha256, store.Stage(hostPath: Write(work, "Local.zip", bytes)).SourceSha256);
});
foreach (var entry in new[] { "../escape.zal21", "/escape.zal21", "C:/escape.zal21", "\\\\server\\share\\a", "safe/file:stream", "NUL.txt", "COM1/a", "file. ", "folder/../x", "a//b", "e\u0301.zal21" })
    Test("reject unsafe archive path " + entry, work => Reject("PackageInvalid", () => StageZip(work, Zip((entry, [1], null)))));
Test("case-insensitive collision rejected", work => Reject("PackageInvalid", () => StageZip(work, Zip(("Lib.zal21", [1], null), ("lib.ZAL21", [2], null)))));
Test("implicit directory/file collision rejected", work => Reject("PackageInvalid", () => StageZip(work, Zip(("folder/a", [1], null), ("folder", [2], null)))));
Test("symlink zip entry rejected", work => Reject("PackageInvalid", () => StageZip(work, Zip(("link", Text("../outside"), 0xa1ff << 16)))));
Test("zip entry expansion limit enforced on streamed bytes", work => Reject("PackageLimitExceeded", () => StageZip(work,
    Zip(("huge.zal21", new byte[1000], null)), new PackageIntakeLimits { MaxExpandedBytes = 999 })));
Test("entry count budget enforced", work => Reject("PackageLimitExceeded", () => StageZip(work,
    Zip(("a", [1], null), ("b", [2], null)), new PackageIntakeLimits { MaxEntries = 1 })));
Test("nested wrapper extraction and depth limit", work =>
{
    var zip = Zip(("inner.zip", Zip(("Library.zal21", [1], null)), null));
    Equal(1, StageZip(work, zip).Candidates);
    Reject("PackageLimitExceeded", () => StageZip(Path.Combine(work, "second"), zip, new PackageIntakeLimits { MaxNestingDepth = 1 }));
});
Test("corrupt zip rejected and failed record retained", work =>
{
    try { StageZip(work, Text("not a zip")); throw new Exception("Expected rejection"); }
    catch (BridgeOperationException error)
    {
        Equal("PackageInvalid", error.Code);
        var id = error.Details!.Value.GetProperty("packageId").GetString()!;
        var store = new PackageStore(Path.Combine(work, "artifacts"));
        Reject("PackageIncomplete", () => store.Inspect(id));
        var status = JsonSerializer.SerializeToElement(store.Status(id), BridgeProtocol.Json);
        Equal("failed", status.GetProperty("state").GetString());
        store.Release(id);
    }
});
Test("CRC mismatch rejected", work =>
{
    var zip = Zip(("Library.zal21", [1, 2, 3], null));
    // Corrupt the CRC in the central directory while retaining valid DEFLATE bytes.
    for (var i = 0; i < zip.Length - 20; i++)
        if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 1 && zip[i + 3] == 2) { zip[i + 16] ^= 1; break; }
    Reject("PackageInvalid", () => StageZip(work, zip));
});
Test("retention blocks deletion across restart", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    store.Retain(status.PackageId, "plan:one");
    Reject("PackageInUse", () => new PackageStore(Path.Combine(work, "artifacts")).Release(status.PackageId));
    store.Unretain(status.PackageId, "plan:one");
    store.Release(status.PackageId);
    store.Release(status.PackageId);
    Equal(true, File.Exists(Path.Combine(work, "source.zip")));
});
Test("staged companion drift blocks native use", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null), ("payload.bin", [2], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var candidate = (PackageCandidate)store.Inspect(status.PackageId).Items[0];
    var file = store.Inspect(status.PackageId, "files").Items.Cast<PackageFile>().Single(x => x.RelativePath.EndsWith("payload.bin"));
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(status.ManifestPath)!, file.RelativePath), [3]);
    Reject("PackageChanged", () => store.ResolveCandidate(status.PackageId, candidate.CandidateId));
});
Test("inspection never reuses changed same-length bytes with a restored timestamp", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var before = (PackageCandidate)store.Inspect(status.PackageId).Items[0];
    var stamp = File.GetLastWriteTimeUtc(status.ManifestPath);
    string original = File.ReadAllText(status.ManifestPath);
    string changed = original.Replace("nativeVerificationRequired", "NATIVEVerificationRequired", StringComparison.Ordinal);
    Equal(original.Length, changed.Length); Equal(false, original == changed);
    File.WriteAllText(status.ManifestPath, changed); File.SetLastWriteTimeUtc(status.ManifestPath, stamp);
    var after = (PackageCandidate)new PackageStore(Path.Combine(work, "artifacts")).Inspect(status.PackageId).Items[0];
    Equal("nativeVerificationRequired", before.Completeness); Equal("NATIVEVerificationRequired", after.Completeness);
});
Test("cached inspection page data cannot poison subsequent responses", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null), ("README.md", Text("notes"), null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    foreach (string section in new[] { "candidates", "files", "documents", "diagnostics" })
    {
        var page = store.Inspect(status.PackageId, section);
        string before = JsonSerializer.Serialize(page, BridgeProtocol.Json);
        if (page.Items[0] is PackageCandidate candidate) candidate.Notes[0] = "poisoned caller-owned notes";
        page.Items[0] = "poisoned caller-owned array";
        Equal(before, JsonSerializer.Serialize(new PackageStore(Path.Combine(work, "artifacts")).Inspect(status.PackageId, section), BridgeProtocol.Json));
    }
});
foreach (int changedIndex in new[] { 0, 70000, 150000 })
    Test("warm inspection compares every manifest chunk at " + changedIndex, work =>
    {
        var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
        var store = new PackageStore(Path.Combine(work, "artifacts"));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllBytes(status.ManifestPath), BridgeProtocol.Json)!;
        char[] text = new string('x', 150001).ToCharArray();
        manifest = manifest with { Diagnostics = [new string(text)] };
        File.WriteAllText(status.ManifestPath, JsonSerializer.Serialize(manifest, BridgeProtocol.Json));
        Equal(manifest.Diagnostics[0], store.Inspect(status.PackageId, "diagnostics").Items[0]);
        var stamp = File.GetLastWriteTimeUtc(status.ManifestPath);
        long size = new FileInfo(status.ManifestPath).Length;
        text[changedIndex] = 'y'; manifest = manifest with { Diagnostics = [new string(text)] };
        File.WriteAllText(status.ManifestPath, JsonSerializer.Serialize(manifest, BridgeProtocol.Json));
        File.SetLastWriteTimeUtc(status.ManifestPath, stamp); Equal(size, new FileInfo(status.ManifestPath).Length);
        Equal(manifest.Diagnostics[0], store.Inspect(status.PackageId, "diagnostics").Items[0]);
    });
foreach (string fault in new[] { "stateFailed", "stateMissing", "manifestMissing", "manifestCorrupt", "trailingCorruption", "wrongIdentity", "wrongManifestState" })
    Test("warm inspection revalidates " + fault, work =>
    {
        var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
        var store = new PackageStore(Path.Combine(work, "artifacts"));
        _ = store.Inspect(status.PackageId);
        string state = Path.Combine(Path.GetDirectoryName(status.ManifestPath)!, "state.json");
        string code;
        switch (fault)
        {
            case "stateFailed": File.WriteAllText(state, "{\"state\":\"failed\"}"); code = "PackageIncomplete"; break;
            case "stateMissing": File.Delete(state); code = "IntakeNotFound"; break;
            case "manifestMissing": File.Delete(status.ManifestPath); code = "IntakeNotFound"; break;
            case "manifestCorrupt":
                string raw = File.ReadAllText(status.ManifestPath); File.WriteAllText(status.ManifestPath, raw[..^1] + "!"); code = "PackageInvalid"; break;
            case "trailingCorruption": File.AppendAllText(status.ManifestPath, "!"); code = "PackageInvalid"; break;
            case "wrongIdentity":
                File.WriteAllText(status.ManifestPath, File.ReadAllText(status.ManifestPath).Replace(status.PackageId, new string('a', 32), StringComparison.Ordinal)); code = "PackageChanged"; break;
            default:
                File.WriteAllText(status.ManifestPath, File.ReadAllText(status.ManifestPath).Replace("\"ready\"", "\"other\"", StringComparison.Ordinal)); code = "PackageChanged"; break;
        }
        Reject(code, () => new PackageStore(Path.Combine(work, "artifacts")).Inspect(status.PackageId));
    });
Test("warm inspection still enforces current request bounds and release", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    _ = store.Inspect(status.PackageId);
    Reject("InvalidRequest", () => store.Inspect(status.PackageId, offset: 2));
    Reject("InvalidRequest", () => store.Inspect(status.PackageId, section: "unknown"));
    Reject("InvalidRequest", () => new PackageStore(Path.Combine(work, "artifacts"), new PackageIntakeLimits { MaxPageSize = 1 }).Inspect(status.PackageId, count: 2));
    Equal(0, store.Inspect(status.PackageId, offset: 1).Items.Length);
    store.Release(status.PackageId); Reject("IntakeNotFound", () => store.Inspect(status.PackageId));
});
Test("inspection preserves UTF-8 BOM manifest behavior", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    string before = JsonSerializer.Serialize(store.Inspect(status.PackageId), BridgeProtocol.Json);
    byte[] original = File.ReadAllBytes(status.ManifestPath);
    File.WriteAllBytes(status.ManifestPath, new byte[] { 0xef, 0xbb, 0xbf }.Concat(original).ToArray());
    Equal(before, JsonSerializer.Serialize(store.Inspect(status.PackageId), BridgeProtocol.Json));
    Equal(before, JsonSerializer.Serialize(store.Inspect(status.PackageId), BridgeProtocol.Json));
});
Test("inspection separates package locations even for the same copied ID", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var other = new PackageStore(Path.Combine(work, "other"));
    string directory = Path.Combine(work, "other", "library-intake", "packages", status.PackageId);
    Directory.CreateDirectory(directory);
    File.Copy(Path.Combine(Path.GetDirectoryName(status.ManifestPath)!, "state.json"), Path.Combine(directory, "state.json"));
    File.WriteAllText(Path.Combine(directory, "manifest.json"), File.ReadAllText(status.ManifestPath).Replace("nativeVerificationRequired", "NATIVEVerificationRequired", StringComparison.Ordinal));
    Equal("nativeVerificationRequired", ((PackageCandidate)store.Inspect(status.PackageId).Items[0]).Completeness);
    Equal("NATIVEVerificationRequired", ((PackageCandidate)other.Inspect(status.PackageId).Items[0]).Completeness);
    Equal("nativeVerificationRequired", ((PackageCandidate)store.Inspect(status.PackageId).Items[0]).Completeness);
});
Test("over-cache-size inspection stays fresh and the manifest read cap remains enforced", work =>
{
    var status = StageZip(work, Zip(("Lib.zal21", [1], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllBytes(status.ManifestPath), BridgeProtocol.Json)!;
    manifest = manifest with { Diagnostics = [new string('x', (8 << 20) + 1)] };
    File.WriteAllText(status.ManifestPath, JsonSerializer.Serialize(manifest, BridgeProtocol.Json));
    Equal(manifest.Diagnostics[0], store.Inspect(status.PackageId, "diagnostics").Items[0]);
    manifest = manifest with { Diagnostics = [new string('y', (8 << 20) + 1)] };
    File.WriteAllText(status.ManifestPath, JsonSerializer.Serialize(manifest, BridgeProtocol.Json));
    Equal(manifest.Diagnostics[0], store.Inspect(status.PackageId, "diagnostics").Items[0]);
    using (var stream = new FileStream(status.ManifestPath, FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength((64L << 20) + 1);
    Reject("PackageInvalid", () => store.Inspect(status.PackageId));
});
Test("unknown package and invalid IDs reject", work =>
{
    var store = new PackageStore(work);
    Reject("InvalidRequest", () => store.Status("../outside"));
    Reject("IntakeNotFound", () => store.Status(new string('a', 32)));
});
Test("case-colliding implicit parent directories rejected", work => Reject("PackageInvalid", () =>
    StageZip(work, Zip(("Library/a", [1], null), ("LIBRARY/b", [2], null)))));
Test("reparse attributes rejected", work => Reject("PackageInvalid", () =>
    StageZip(work, Zip(("junction/", [], (int)FileAttributes.ReparsePoint)))));
Test("encrypted entries rejected before decompression", work =>
{
    var zip = Zip(("Library.zal21", [1], null));
    for (var i = 0; i < zip.Length - 20; i++)
        if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 1 && zip[i + 3] == 2) { zip[i + 8] |= 1; break; }
    Reject("PackageInvalid", () => StageZip(work, zip));
});
Test("oversized central count rejected before directory allocation", work =>
{
    var zip = Zip(("Library.zal21", [1], null));
    zip[^12] = 0xfe; zip[^11] = 0xff;
    Reject("PackageLimitExceeded", () => StageZip(work, zip));
});
Test("expired upload rejects resume but permits explicit abort", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("a.zip", 1, Hash([1]));
    File.WriteAllText(Path.Combine(begin.HostPath, "upload.json"), JsonSerializer.Serialize(begin with { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(-1) }, BridgeProtocol.Json));
    Reject("UploadExpired", () => store.Write(begin.UploadId, 0, "AQ=="));
    Equal("aborted", store.Abort(begin.UploadId).State);
});
Test("lost retention ledger never enables deletion", work =>
{
    var status = StageZip(work, Zip(("Library.zal21", [1], null)));
    File.Delete(Path.Combine(Path.GetDirectoryName(status.ManifestPath)!, "references.json"));
    Reject("PackageChanged", () => new PackageStore(Path.Combine(work, "artifacts")).Release(status.PackageId));
});
Test("native archives remain opaque even when containing ZIP bytes", work =>
{
    var bytes = Zip(("../malicious", [1], null));
    var status = new PackageStore(Path.Combine(work, "artifacts")).Stage(hostPath: Write(work, "library.zal21", bytes));
    Equal(0L, status.ExpandedBytes);
    Equal(1, status.Candidates);
});
Test("empty native files are not usable packages", work => Reject("PackageInvalid", () =>
    new PackageStore(Path.Combine(work, "artifacts")).Stage(hostPath: Write(work, "library.zal21", []))));
Test("source directory cannot recursively contain intake destination", work =>
{
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    Write(work, "Library.al21", [1]);
    Reject("PackageInvalid", () => store.Stage(hostPath: work));
});
Test("host path accepts only library inputs and copies nothing otherwise", work =>
{
    var artifacts = Path.Combine(work, "artifacts");
    var store = new PackageStore(artifacts);
    var plain = Path.Combine(work, "plain"); Directory.CreateDirectory(Path.Combine(plain, "nested"));
    Write(plain, "notes.txt", Text("secret")); Write(Path.Combine(plain, "nested"), "Library.al21", [1]);
    // An archive in a folder does not make it a library directory: only uncompressed .alXX/.apXX markers count.
    var downloads = Path.Combine(work, "Downloads"); Write(downloads, "Vendor.zal21", [1]); Write(downloads, "notes.txt", Text("private"));
    foreach (var path in new[] { Write(work, "id_ed25519", Text("key")), Write(work, "secrets.txt", Text("secret")), Write(work, "Block.scl", Text("x")), plain, downloads })
        Reject("PackageInvalid", () => store.Stage(hostPath: path));
    Equal(0, Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Count(x => !x.EndsWith(".lock", StringComparison.Ordinal)));
    Equal("ready", store.Stage(hostPath: Write(work, "Library.zip", Zip(("Library.zal21", [1], null)))).State);
});
Test("host path is checked under its full on-disk name", work =>
{
    var artifacts = Path.Combine(work, "artifacts");
    var store = new PackageStore(artifacts);
    var zip = Zip(("Library.zal21", [1], null));
    foreach (var path in new[] { Write(work, "secret.zipx", zip), Write(work, "Library.al2x", [1]), Path.Combine(work, "Missing.zip") })
        Reject("PackageInvalid", () => store.Stage(hostPath: path));
    // An 8.3 short name such as SECRET~1.ZIP carries an extension the long name lacks; the temp volume is tried when this one makes none.
    string? ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        return GetShortPathNameW(path, buffer, (uint)buffer.Capacity) > 0 && Path.GetFileName(buffer.ToString()).Contains('~')
            ? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(buffer.ToString())) : null;
    }
    var temp = Path.Combine(Path.GetTempPath(), "tiamcp-short-" + Guid.NewGuid().ToString("N"));
    try
    {
        var shortPath = new[] { work, temp }.Select(folder => ShortPath(Write(folder, "secret.zipx", zip))).FirstOrDefault(path => path is not null);
        if (shortPath is null) Console.WriteLine("NOTE no volume here generates 8.3 short names; only the long-extension cases ran.");
        else Reject("PackageInvalid", () => store.Stage(hostPath: shortPath));
    }
    finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    Equal(0, Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Count(x => !x.EndsWith(".lock", StringComparison.Ordinal)));
});
Test("a failed directory stage keeps no partial copy", work =>
{
    var artifacts = Path.Combine(work, "artifacts");
    var source = Path.Combine(work, "source"); Write(source, "Library.al21", [1]); Write(Path.Combine(source, "System"), "data.bin", [2, 3]);
    Reject("PackageLimitExceeded", () => new PackageStore(artifacts, new PackageIntakeLimits { MaxInputBytes = 1 }).Stage(hostPath: source));
    Equal(0, Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories).Count(x => !x.EndsWith(".lock", StringComparison.Ordinal)));
});
Test("chunk response supplies exact accepted range and chunk digest", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("library.zal21", 3, Hash([1, 2, 3]));
    var accepted = store.Write(begin.UploadId, 0, "AQI=");
    Equal((long?)0, accepted.AcceptedOffset);
    Equal((int?)2, accepted.AcceptedBytes);
    Equal(Hash([1, 2]), accepted.ChunkSha256);
});
Test("server-side upload slices chunks and aborts a failed upload", work =>
{
    var bytes = Text("ten bytes!");
    var store = new ArtifactUploadStore(work, new PackageIntakeLimits { MaxChunkBytes = 4, MaxUploads = 1 });
    // A wrong digest lets all three chunks land and fails only the commit.
    try { store.Upload(bytes, "wrong.bin", Hash(Text("other"))); throw new Exception("Expected the commit to refuse a wrong digest."); }
    catch (BridgeException error) when (error.Code == "PackageChanged") { }
    var record = Directory.GetFiles(work, "upload.json", SearchOption.AllDirectories).Single();
    Equal("aborted", store.Status(Path.GetFileName(Path.GetDirectoryName(record))!).State);
    // The aborted upload released its slot, so the one allowed upload still succeeds.
    Equal(Hash(bytes), store.ResolveCommitted(store.Upload(bytes, "multi.bin")).Sha256);
});
Test("committed release frees lifetime quota and preserves staged package", work =>
{
    var limits = new PackageIntakeLimits { MaxUploads = 1 };
    var store = new ArtifactUploadStore(work, limits);
    var begin = store.Begin("library.zal21", 1, Hash([1]));
    Reject("UploadInUse", () => store.Release(begin.UploadId));
    store.Write(begin.UploadId, 0, "AQ==");
    var commit = store.Commit(begin.UploadId);
    var packages = new PackageStore(work, limits);
    var staged = packages.Stage(artifactRef: commit.ArtifactRef);
    packages.Retain(staged.PackageId, "plan:retained");
    store.Release(begin.UploadId);
    store.Release(begin.UploadId);
    Equal(1, packages.Inspect(staged.PackageId).Total);
    var candidate = (PackageCandidate)packages.Inspect(staged.PackageId).Items[0];
    Equal(true, File.Exists(packages.ResolveCandidate(staged.PackageId, candidate.CandidateId)));
    _ = store.Begin("second.zal21", 1, Hash([2]));
});
Test("release and staging share the same cross-process exclusion", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("library.zal21", 1, Hash([1]));
    store.Abort(begin.UploadId);
    using var gate = new FileStream(Path.Combine(work, "library-intake", "intake.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    Reject("IntakeBusy", () => store.Release(begin.UploadId));
});
Test("expired uploads can be released directly", work =>
{
    var store = new ArtifactUploadStore(work);
    var begin = store.Begin("library.zal21", 1, Hash([1]));
    File.WriteAllText(Path.Combine(begin.HostPath, "upload.json"), JsonSerializer.Serialize(begin with { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(-1) }, BridgeProtocol.Json));
    store.Release(begin.UploadId);
    Reject("IntakeNotFound", () => store.Status(begin.UploadId));
});
Test("CLI native disabling preserves normal defaults and refuses qualification modes", work =>
{
    Equal(false, HostOptions.Parse([]).NoNativeWorkers);
    // A source build resolves its workers and artifacts from the checkout root, not from bin/.
    Equal(true, File.Exists(Path.Combine(Path.GetDirectoryName(HostOptions.Parse([]).ArtifactsDirectory)!, "TiaMcp.slnx")));
    Equal(true, HostOptions.Parse([]).WorkerPath.EndsWith(Path.Combine("src", "TiaMcp.Openness.V21", "bin", "Debug", "net48", "TiaMcp.Openness.V21.exe")));
    Equal(true, HostOptions.Parse(["--no-native-workers", "--worker", Path.Combine(work, "worker.exe")]).NoNativeWorkers);
    foreach (var flag in new[] { "--diagnose", "--component-qualification", "--testsuite-scope-probe", "--translation-qualification" })
    {
        Equal(false, HostOptions.Parse([flag]).NoNativeWorkers);
        try { HostOptions.Parse([flag, "--no-native-workers"]); }
        catch (ArgumentException error) when (error.Message.Contains("Native diagnosis and qualification cannot run")) { continue; }
        throw new Exception("Native-disabled CLI accepted " + flag);
    }
});
Test("CLI toolsets select named groups and refuse unknown, empty or repeated values", work =>
{
    Equal(null, HostOptions.Parse([]).Toolsets);
    Equal(null, HostOptions.Parse(["--toolsets", "all"]).Toolsets);
    var selected = HostOptions.Parse(["--toolsets", "core, opcua"]).Toolsets!;
    Equal(2, selected.Count); Equal(true, selected.Contains("core") && selected.Contains("opcua"));
    foreach (var (value, message) in new[] { ("nope", "Unknown --toolsets group nope"), ("core,,opcua", "without empty entries"),
        ("all,core", "cannot be combined"), ("base", "Unknown --toolsets group base"), ("qualification", "Unknown --toolsets group qualification") })
    {
        try { HostOptions.Parse(["--toolsets", value]); }
        catch (ArgumentException error) when (error.Message.Contains(message)) { continue; }
        throw new Exception("--toolsets accepted " + value);
    }
    try { HostOptions.Parse(["--toolsets", "core", "--toolsets", "hmi"]); throw new Exception("Repeated --toolsets accepted."); }
    catch (ArgumentException error) when (error.Message.Contains("given once")) { }
    // Longer prefixes win over the groups they would otherwise fall under.
    Equal("explorer", ToolGroups.Of("project_model_search")); Equal("core", ToolGroups.Of("project_open"));
    Equal("simulation", ToolGroups.Of("plc_station_upload")); Equal("core", ToolGroups.Of("plc_compare"));
    Equal("reviews", ToolGroups.Of("software_unit_review")); Equal("core", ToolGroups.Of("software_inspect"));
    Equal(true, ToolGroups.Advertised("artifact_read", selected)); Equal(true, ToolGroups.Advertised("qualification_watch_compete", selected));
    Equal(false, ToolGroups.Advertised("hmi_inspect", selected)); Equal(true, ToolGroups.Advertised("hmi_inspect", null));
    try { ToolGroups.Advertised("unmapped_tool", null); throw new Exception("An unmapped tool was advertised."); }
    catch (InvalidOperationException error) when (error.Message.Contains("belongs to no --toolsets group")) { }
});
Test("CLI combined library_integrate apply is on by default, the old flag is a no-op and the opt-out turns it off", work =>
{
    Equal(true, HostOptions.Parse([]).LibraryIntegrateApply);
    Equal(true, HostOptions.Parse(["--enable-library-integrate-apply"]).LibraryIntegrateApply);
    Equal(false, HostOptions.Parse(["--disable-library-integrate-apply"]).LibraryIntegrateApply);
    try { HostOptions.Parse(["--enable-library-integrate-apply", "--disable-library-integrate-apply"]); throw new Exception("Contradicting flags were accepted."); }
    catch (ArgumentException error) when (error.Message.Contains("exclude each other")) { }
});
Test("CLI Add-In receiver is explicit and does not imply native admission", work =>
{
    Equal(false, HostOptions.Parse([]).AddInHandoff);
    var options = HostOptions.Parse(["--addin-handoff", "--no-native-workers"]);
    Equal(true, options.AddInHandoff); Equal(true, options.NoNativeWorkers);
    try { HostOptions.Parse(["--addin-handoff", "--diagnose"]); }
    catch (ArgumentException error) when (error.Message.Contains("long-running Host")) { return; }
    throw new Exception("Diagnosis silently ignored the requested receiver.");
});
Test("CLI intake limits are explicit, validated and recorded", work =>
{
    var options = HostOptions.Parse(["--library-limits-file", Write(work, "limits.json", Text("{\"maxInputBytes\":1024,\"maxExpandedBytes\":4096,\"maxChunkBytes\":128}"))]);
    Equal(1024L, options.LibraryLimits.MaxInputBytes);
    Equal(4096L, options.LibraryLimits.MaxExpandedBytes);
    var begin = new ArtifactUploadStore(work, options.LibraryLimits).Begin("a.zal21", 1, Hash([1]));
    Equal(128, begin.MaxChunkBytes);
    Equal(1024L, begin.Limits!.MaxInputBytes);
});
foreach (var invalid in new[] { "{\"unknown\":1}", "{\"maxInputBytes\":-1}", "{\"maxInputBytes\":1,\"maxInputBytes\":2}", "{\"maxPageSize\":201}", "{\"maxInputBytes\":null}", "{\"MaxInputBytes\":10}", "{\"maxInputBytes\":1.5}", "[]" })
    Test("CLI rejects invalid limits " + invalid, work =>
    {
        try { HostOptions.Parse(["--library-limits-file", Write(work, "limits.json", Text(invalid))]); }
        catch (Exception error) when (error is ArgumentException or JsonException) { return; }
        throw new Exception("Invalid limits accepted.");
    });
Test("candidate companion manifest carries original expected bytes and relative paths", work =>
{
    var staged = StageZip(work, Zip(("Library/Library.al21", [1], null), ("Library/System/data.bin", [2, 3], null), ("README.md", Text("external"), null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var candidate = (PackageCandidate)store.Inspect(staged.PackageId).Items[0];
    var expected = store.GetCandidateFiles(staged.PackageId, candidate.CandidateId);
    Equal(2, expected.Length);
    Equal("Library.al21", expected[0].RelativePath);
    Equal("System/data.bin", expected[1].RelativePath);
    Equal(2L, expected[1].Bytes);
    Equal(Hash([2, 3]), expected[1].Sha256);
});
Test("candidate manifest rejects added companions instead of trusting them", work =>
{
    var staged = StageZip(work, Zip(("Library/Library.al21", [1], null), ("Library/System/data.bin", [2], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var candidate = (PackageCandidate)store.Inspect(staged.PackageId).Items[0];
    var marker = store.ResolveCandidate(staged.PackageId, candidate.CandidateId);
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(marker)!, "unexpected.bin"), [9]);
    Reject("PackageChanged", () => store.GetCandidateFiles(staged.PackageId, candidate.CandidateId));
});
Test("candidate manifest rejects changed companion bytes", work =>
{
    var staged = StageZip(work, Zip(("Library/Library.al21", [1], null), ("Library/System/data.bin", [2], null)));
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var candidate = (PackageCandidate)store.Inspect(staged.PackageId).Items[0];
    var marker = store.ResolveCandidate(staged.PackageId, candidate.CandidateId);
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(marker)!, "System", "data.bin"), [9]);
    Reject("PackageChanged", () => store.GetCandidateFiles(staged.PackageId, candidate.CandidateId));
});
Test("resolved archive receipt preserves staged identity after path replacement", work =>
{
    var bytes = new byte[] { 1, 2, 3 };
    var store = new PackageStore(Path.Combine(work, "artifacts"));
    var staged = store.Stage(hostPath: Write(work, "Library.zal21", bytes));
    var candidate = (PackageCandidate)store.Inspect(staged.PackageId).Items[0];
    var receipt = store.ResolveCandidateReceipt(staged.PackageId, candidate.CandidateId);
    Equal(Hash(bytes), receipt.Sha256);
    Equal(3L, receipt.Bytes);
    Equal(receipt.Path, store.ResolveCandidate(staged.PackageId, candidate.CandidateId));
    // Same length, different contents after resolution: the worker must receive the
    // original approved digest, never adopt a fresh digest from this changed path.
    File.WriteAllBytes(receipt.Path, [9, 8, 7]);
    Equal(Hash(bytes), receipt.Sha256);
    Equal(false, receipt.Sha256 == Hash(File.ReadAllBytes(receipt.Path)));
    Reject("PackageChanged", () => store.ResolveCandidateReceipt(staged.PackageId, candidate.CandidateId));
});
Test("native reference proof follows directed OB to FB to UDT paths", _ =>
{
    var reachable = LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/Library", "/Types/Payload", "/FB/Unused"], ["/OB/Main"],
        [new("/OB/Main", "/FB/Library"), new("/FB/Library", "/Types/Payload")],
        ["/FB/Library", "/Types/Payload"], true);
    Equal("/FB/Library|/OB/Main|/Types/Payload", string.Join("|", reachable));
});
Test("native reference proof permits root-connected cycles and repeated locations", _ =>
{
    var reachable = LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A", "/FB/B"], ["/OB/Main"],
        [new("/OB/Main", "/FB/A"), new("/OB/Main", "/FB/A"), new("/FB/A", "/FB/B"), new("/FB/B", "/FB/A")],
        ["/FB/A", "/FB/B"], true);
    Equal("/FB/A|/FB/B|/OB/Main", string.Join("|", reachable));
});
Test("native reference proof rejects an isolated library-use cycle", _ =>
{
    RejectReachability("lack a trusted-root reference path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A", "/FB/B"], ["/OB/Main"],
        [new("/FB/A", "/FB/B"), new("/FB/B", "/FB/A")], ["/FB/A", "/FB/B"], true));
});
Test("native reference proof does not reverse caller and callee", _ =>
{
    RejectReachability("lack a trusted-root reference path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A"], ["/OB/Main"], [new("/FB/A", "/OB/Main")], ["/FB/A"], true));
});
Test("native reference proof includes untouched unrelated library uses", _ =>
{
    RejectReachability("/Types/Unrelated", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/Updated", "/Types/Unrelated"], ["/OB/Main"],
        [new("/OB/Main", "/FB/Updated")], ["/FB/Updated", "/Types/Unrelated"], true));
});
Test("native reference proof rejects missing program roots", _ =>
{
    RejectReachability("No trusted program entry point", () => LibraryReferenceReachability.RequireReachable(
        ["/FB/A"], [], [new("/FB/A", "/FB/A")], ["/FB/A"], true));
});
Test("native reference proof rejects incomplete evidence despite apparent reachability", _ =>
{
    RejectReachability("incomplete or truncated", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A"], ["/OB/Main"], [new("/OB/Main", "/FB/A")], ["/FB/A"], false));
});
Test("native reference proof rejects ambiguous duplicate object identities", _ =>
{
    RejectReachability("duplicate exact path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A", "/FB/A"], ["/OB/Main"], [new("/OB/Main", "/FB/A")], ["/FB/A"], true));
});
Test("native reference proof rejects unresolved edge endpoints", _ =>
{
    RejectReachability("unresolved object path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A"], ["/OB/Main"], [new("/OB/Main", "/FB/Missing")], ["/FB/A"], true));
});
Test("native reference proof requires exact case-sensitive identities", _ =>
{
    RejectReachability("unresolved object path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main", "/FB/A"], ["/OB/Main"], [new("/OB/Main", "/FB/a")], ["/FB/A"], true));
});
Test("native reference proof rejects unknown roots and baseline identities", _ =>
{
    RejectReachability("Unknown trusted root path", () => LibraryReferenceReachability.RequireReachable(
        ["/FB/A"], ["/OB/Main"], [], ["/FB/A"], true));
    RejectReachability("Unknown baseline library-use path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main"], ["/OB/Main"], [], ["/FB/A"], true));
});
Test("native reference proof rejects duplicate roots and baseline identities", _ =>
{
    RejectReachability("duplicate exact path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main"], ["/OB/Main", "/OB/Main"], [], [], true));
    RejectReachability("duplicate exact path", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main"], ["/OB/Main"], [], ["/OB/Main", "/OB/Main"], true));
});
Test("native reference proof rejects malformed paths and missing evidence arrays", _ =>
{
    foreach (var path in new[] { "", "/", "OB/Main", "/OB//Main", "/OB/../Main", "/OB/./Main", "/OB/Main/", "/OB\\Main", "/OB/Main\n" })
        RejectReachability("canonical exact object paths", () => LibraryReferenceReachability.RequireReachable([path], [path], [], [], true));
    RejectReachability("Known object paths", () => LibraryReferenceReachability.RequireReachable(null!, ["/OB/Main"], [], [], true));
    RejectReachability("Trusted root paths", () => LibraryReferenceReachability.RequireReachable(["/OB/Main"], null!, [], [], true));
    RejectReachability("Baseline library-use paths", () => LibraryReferenceReachability.RequireReachable(["/OB/Main"], ["/OB/Main"], [], null!, true));
    RejectReachability("edges", () => LibraryReferenceReachability.RequireReachable(["/OB/Main"], ["/OB/Main"], null!, [], true));
    RejectReachability("missing edge", () => LibraryReferenceReachability.RequireReachable(["/OB/Main"], ["/OB/Main"], [null!], [], true));
});
Test("native reference proof refuses over-limit evidence instead of truncating", _ =>
{
    RejectReachability("object limit", () => LibraryReferenceReachability.RequireReachable(
        Enumerable.Range(0, 50001).Select(i => "/FB/" + i), ["/FB/0"], [], [], true));
    RejectReachability("edge limit", () => LibraryReferenceReachability.RequireReachable(
        ["/OB/Main"], ["/OB/Main"], Enumerable.Repeat(new LibraryReferenceEdge("/OB/Main", "/OB/Main"), 200001), [], true));
});
Console.WriteLine($"Library intake tests: {passed} passed, {failed} failed.");
// Validate the concrete generated path before deleting only this run's owned fixture directory.
if (!Path.GetFullPath(root).StartsWith(testParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Test cleanup path escaped its fixture root.");
Directory.Delete(root, true);
return failed == 0 ? 0 : 1;
