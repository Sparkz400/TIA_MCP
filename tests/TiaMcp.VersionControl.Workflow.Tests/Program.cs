using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

if (args.Length != 2) throw new ArgumentException("Supply exact Git executable and a fresh fixture root.");
string root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new ArgumentException("Use a fresh fixture root.");
Directory.CreateDirectory(root);
var store = new VciWorkspaceStore(root, args[0]);
var uploads = new ArtifactUploadStore(root);
var author = new GitAuthor("Offline Fixture", "fixture@example.invalid");
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
async Task Reject(string code, Func<Task> action)
{
    try { await action(); } catch (BridgeException error) { Check(error.Code == code, "refuses " + code + ": " + error.Code); return; }
    throw new Exception("Expected " + code);
}
string Upload(byte[] bytes)
{
    var state = uploads.Begin("fixture.bin", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    for (int offset = 0; offset < bytes.Length; offset += state.MaxChunkBytes)
        uploads.Write(state.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(state.MaxChunkBytes, bytes.Length - offset)));
    return uploads.Commit(state.UploadId).ArtifactRef!;
}
byte[] FileBytes(string reference, string path) => Convert.FromBase64String(Json(store.ReadFile(reference, path)).GetProperty("data").GetString()!);
var raw = new byte[] { 0xEF, 0xBB, 0xBF, 65, 13, 10, 0, 255 };
var preview = Json(await store.PrepareAsync([new("PLC/Über Motor.s7dcl", Upload(raw)), new("empty.txt")], author, "first"));
string previewRef = Text(preview, "recordRef"), plan = Text(preview, "planSha256");
Check(Text(preview, "state") == "prepared" && preview.GetProperty("nativeQualified").GetBoolean() == false, "offline prepared review does not qualify native engineering");
Check(FileBytes(previewRef, "PLC/Über Motor.s7dcl").SequenceEqual(raw), "review retains exact BOM CRLF binary and Unicode bytes");
Check(FileBytes(previewRef, "empty.txt").Length == 0, "explicit null upload creates an empty file");
await Reject("PreviewStale", () => store.CommitAsync(previewRef, new string('a', 64)));
store = new VciWorkspaceStore(root, args[0]);
var first = Json(await store.CommitAsync(previewRef, plan)); string revision = Text(first, "recordRef");
Check(Text(first, "state") == "committed", "review survives store recreation and commits");
await Reject("RecoveryRequired", () => store.CommitAsync(previewRef, plan));
string firstCommit = Text(first, "commit");
string firstDirectory = Path.Combine(root, "vci-workspaces", "records", revision.Split(':')[2]);
File.Move(Path.Combine(firstDirectory, "commit.json"), Path.Combine(firstDirectory, "commit.json.lost-receipt"));
store = new VciWorkspaceStore(root, args[0]);
var unknown = Json(store.Reopen(previewRef));
Check(Text(unknown, "state") == "outcomeUnknown", "missing completion remains unknown despite a verified Git callback");
await Reject("RecoveryRequired", () => store.ReconcileAsync(previewRef, new string('a', 64)));
var reconciled = Json(await store.ReconcileAsync(previewRef, Text(unknown.GetProperty("commitJournal"), "sha256")));
Check(Text(reconciled, "commit") == firstCommit && Text(reconciled, "recordRef") == revision, "fresh reconciliation restores identical receipt without a second commit");
var next = Json(await store.PrepareAsync([new("PLC/Über Motor.s7dcl", Upload(Encoding.UTF8.GetBytes("second\r\n"))),
    new("empty.txt", Delete: true), new("notes.txt", Upload(Encoding.UTF8.GetBytes("new")))], author, "second", revision));
string nextPreview = Text(next, "recordRef");
Check(FileBytes(revision, "PLC/Über Motor.s7dcl").SequenceEqual(raw), "preparing descendant leaves parent revision unchanged");
var diff = Json(store.ReadArtifact(nextPreview, "diff.txt"));
Check(Encoding.UTF8.GetString(Convert.FromBase64String(Text(diff, "data"))).Contains("empty.txt"), "tracked deletion is reviewable in Git diff");
var second = Json(await store.CommitAsync(nextPreview, Text(next, "planSha256"))); string secondRevision = Text(second, "recordRef");
var history = Json(await store.HistoryAsync(secondRevision));
Check(Text(history, "history").Contains(firstCommit) && Text(history, "history").Contains(Text(second, "commit")), "descendant retains original Git parent and full history");
Check(FileBytes(revision, "PLC/Über Motor.s7dcl").SequenceEqual(raw), "committing descendant leaves original bytes retrievable");
var export = Json(await store.MaterializeAsync(revision));
var zip = Json(store.ReadArtifact(Text(export, "recordRef"), "revision.zip"));
using (var archive = new ZipArchive(new MemoryStream(Convert.FromBase64String(Text(zip, "data")))))
{
    using var entry = archive.GetEntry("PLC/Über Motor.s7dcl")!.Open(); using var copy = new MemoryStream(); entry.CopyTo(copy);
    Check(copy.ToArray().SequenceEqual(raw) && archive.GetEntry("empty.txt") is not null, "exact old revision exports original raw bytes and empty file");
}
string treeId = Text(first, "tree");
string treePath = Path.Combine(firstDirectory, "repository", ".git", "objects", treeId[..2], treeId[2..]);
var originalTree = File.ReadAllBytes(treePath);
var treeAttributes = File.GetAttributes(treePath);
File.SetAttributes(treePath, treeAttributes & ~FileAttributes.ReadOnly);
try
{
    using var inflated = new MemoryStream();
    using (var compressed = new ZLibStream(new MemoryStream(originalTree), CompressionMode.Decompress)) compressed.CopyTo(inflated);
    var treeBytes = inflated.ToArray();
    var emptyBlob = SHA1.HashData(Encoding.ASCII.GetBytes("blob 0\0"));
    var rawBlob = SHA1.HashData(Encoding.ASCII.GetBytes($"blob {raw.Length}\0").Concat(raw).ToArray());
    int index = treeBytes.AsSpan().IndexOf(emptyBlob);
    Check(index >= 0, "independent fixture finds the empty-file Git object in the root tree");
    rawBlob.CopyTo(treeBytes, index);
    using (var file = File.Create(treePath))
    using (var compressed = new ZLibStream(file, CompressionLevel.Optimal)) compressed.Write(treeBytes);
    try { await store.MaterializeAsync(revision); throw new Exception("Altered Git tree was exported."); }
    catch (BridgeException error)
    {
        Check(error.Code is "PreviewStale" or "VciIntegrityFailure" or "GitCommandFailed", "altered Git tree cannot publish a revision export: " + error.Code);
        Check(Directory.EnumerateFiles(Path.Combine(root, "vci-workspaces", "records"), "export.json", SearchOption.AllDirectories).Count() == 1,
            "corrupt tree produces no additional completed export receipt");
    }
}
finally { File.WriteAllBytes(treePath, originalTree); File.SetAttributes(treePath, treeAttributes); }
await Reject("GitUnsafePath", () => store.PrepareAsync([new(".git/config")], author, "unsafe"));
await Reject("GitPathCollision", () => store.PrepareAsync([new("a"), new("A")], author, "aliases"));
await Reject("GitExecutableCustomizationRefused", () => store.PrepareAsync([new(".gitattributes")], author, "driver"));
await Reject("VciInvalidInput", () => store.PrepareAsync([new("gone", Delete: true)], author, "missing", revision));
await Reject("VciArtifactMissing", () => Task.FromResult(store.ReadFile(revision, "../../outside")));
await Reject("VciInputLimit", () => Task.FromResult(store.ReadArtifact(revision, "files.json", count: 65537)));
var stale = Json(await store.PrepareAsync([new("new.txt", Upload(Encoding.UTF8.GetBytes("new")))], author, "stale", revision));
string staleRef = Text(stale, "recordRef"), staleRoot = Path.Combine(root, "vci-workspaces", "records", staleRef.Split(':')[2]);
File.WriteAllText(Path.Combine(staleRoot, "repository", "new.txt"), "tampered");
await Reject("VciIntegrityFailure", () => store.CommitAsync(staleRef, Text(stale, "planSha256")));
Check(FileBytes(revision, "PLC/Über Motor.s7dcl").SequenceEqual(raw), "stale child cannot damage parent");
var lost = Json(await store.PrepareAsync([new("lost.txt")], author, "interrupted", revision));
string lostRef = Text(lost, "recordRef"), lostRoot = Path.Combine(root, "vci-workspaces", "records", lostRef.Split(':')[2]);
File.WriteAllText(Path.Combine(lostRoot, "commit-intent.json"), "interrupted fixture");
store = new VciWorkspaceStore(root, args[0]);
Check(Text(Json(store.Reopen(lostRef)), "state") == "outcomeUnknown", "retained commit intent survives recreation as unknown");
await Reject("RecoveryRequired", () => store.CommitAsync(lostRef, Text(lost, "planSha256")));
Check(Json(store.List()).GetProperty("rows").EnumerateArray().Any(r => Text(r, "state") == "integrity-failed"), "discovery reports damaged candidates without replay");
var receiptFailure = Json(await store.PrepareAsync([new("receipt.txt")], author, "receipt failure", revision));
string failureRef = Text(receiptFailure, "recordRef"), failureDirectory = Path.Combine(root, "vci-workspaces", "records", failureRef.Split(':')[2]);
Directory.CreateDirectory(Path.Combine(failureDirectory, "commit.json"));
await Reject("VciOutcomeUnknown", () => store.CommitAsync(failureRef, Text(receiptFailure, "planSha256")));
var failureStatus = Json(store.Reopen(failureRef));
Check(Text(failureStatus, "state") == "outcomeUnknown" && Text(failureStatus.GetProperty("commitJournal"), "phase") == "verified",
    "actual completion-write failure preserves verified Git callback and unknown Host outcome");
await Reject("RecoveryRequired", () => store.CommitAsync(failureRef, Text(receiptFailure, "planSha256")));
Directory.Delete(Path.Combine(failureDirectory, "commit.json"));
var fixedReceipt = Json(await store.ReconcileAsync(failureRef, Text(failureStatus.GetProperty("commitJournal"), "sha256")));
Check(Text(fixedReceipt, "commit") == Text(failureStatus.GetProperty("commitJournal"), "candidateCommit"), "receipt repair reuses exact candidate commit");
string callbackPath = Path.Combine(failureDirectory, "commit-journal", "0004.json");
var savedCallback = File.ReadAllBytes(callbackPath);
File.WriteAllText(callbackPath, File.ReadAllText(callbackPath).Replace("verified", "outcomeUnknown"));
await Reject("VciIntegrityFailure", () => Task.FromResult(store.Reopen(Text(fixedReceipt, "recordRef"))));
File.WriteAllBytes(callbackPath, savedCallback);
Check(Text(Json(store.Reopen(Text(fixedReceipt, "recordRef"))), "state") == "committed", "restored exact callback revalidates committed receipt");
var unsafeConfig = Json(await store.PrepareAsync([new("config.txt")], author, "config test", revision));
string unsafeRef = Text(unsafeConfig, "recordRef"), unsafeDirectory = Path.Combine(root, "vci-workspaces", "records", unsafeRef.Split(':')[2]);
File.AppendAllText(Path.Combine(unsafeDirectory, "repository", ".git", "config"), "\n[alias]\n dangerous = !echo no\n");
await Reject("GitUnsupportedConfiguration", () => store.CommitAsync(unsafeRef, Text(unsafeConfig, "planSha256")));
Check(!File.Exists(Path.Combine(unsafeDirectory, "commit-intent.json")), "unsupported configuration is refused before commit dispatch intent");
await Reject("VciInputLimit", () => store.PrepareAsync(Enumerable.Range(0, 1001).Select(i => new VciUploadFile("f" + i)).ToArray(), author, "too many"));
await Reject("VciInvalidReference", () => Task.FromResult(store.Reopen(new string(':', 2 << 20))));
await Reject("VciInvalidReference", () => Task.FromResult(store.Reopen(previewRef[..previewRef.LastIndexOf(':')] + ":" + previewRef.Split(':')[3].ToUpperInvariant())));
var largeBytes = new byte[16 << 20]; new Random(8917).NextBytes(largeBytes);
var large = Json(await store.PrepareAsync([new("maximum.bin", Upload(largeBytes))], author, "maximum file"));
large = Json(await store.CommitAsync(Text(large, "recordRef"), Text(large, "planSha256")));
string largeRoot = Path.Combine(root, "vci-workspaces", "records", Text(large, "recordRef").Split(':')[2], "repository");
Check(Directory.EnumerateFiles(Path.Combine(largeRoot, ".git", "objects"), "*", SearchOption.AllDirectories).Any(p => new FileInfo(p).Length > largeBytes.Length),
    "incompressible maximum-size input has Git metadata larger than the raw-file limit");
var largeChild = Json(await store.PrepareAsync([new("added.txt")], author, "copy maximum", Text(large, "recordRef")));
var largeRead = Json(store.ReadFile(Text(largeChild, "recordRef"), "maximum.bin"));
Check(Text(largeRead, "sha256") == Convert.ToHexStringLower(SHA256.HashData(largeBytes)) && largeRead.GetProperty("bytes").GetInt64() == largeBytes.Length,
    "bounded repository copying preserves maximum raw file even when Git object metadata is larger");
var named = Json(await store.PrepareAsync([new("branch.txt", Upload([1, 2, 3]))], author, "named descendant", revision, branchName: "review/offline"));
string namedPreview = Text(named, "recordRef");
Check(Text(named, "headReference") == "refs/heads/review/offline", "named candidate exposes its exact branch before commit");
Check(FileBytes(namedPreview, "PLC/Über Motor.s7dcl").SequenceEqual(raw), "fork preserves parent bytes before applying selected additions");
string namedReviewPath = Path.Combine(root, "vci-workspaces", "records", namedPreview.Split(':')[2], "review.json");
var namedReviewBytes = File.ReadAllBytes(namedReviewPath);
var forgedReview = System.Text.Json.Nodes.JsonNode.Parse(namedReviewBytes)!;
forgedReview["artifacts"]![4] = forgedReview["artifacts"]![0]!.DeepClone();
File.WriteAllBytes(namedReviewPath, JsonSerializer.SerializeToUtf8Bytes(forgedReview, BridgeProtocol.Json));
string forgedRef = namedPreview[..(namedPreview.LastIndexOf(':') + 1)] + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(namedReviewPath)));
await Reject("VciIntegrityFailure", () => Task.FromResult(store.Reopen(forgedRef)));
File.WriteAllBytes(namedReviewPath, namedReviewBytes);
var namedCommit = Json(await store.CommitAsync(namedPreview, Text(named, "planSha256"))); string namedRef = Text(namedCommit, "recordRef");
store = new VciWorkspaceStore(root, args[0]);
var branchPage = Json(await store.BranchesAsync(namedRef, count: 1));
Check(branchPage.GetProperty("total").GetInt32() == 2 && Text(branchPage.GetProperty("rows")[0], "reference") == "refs/heads/main" &&
    Text(branchPage.GetProperty("rows")[0], "commit") == firstCommit, "fork retains the exact parent main branch");
var nextBranchPage = Json(await store.BranchesAsync(namedRef, 1, 1, Text(branchPage, "inventorySha256")));
Check(Text(nextBranchPage.GetProperty("rows")[0], "reference") == "refs/heads/review/offline" &&
    Text(nextBranchPage.GetProperty("rows")[0], "commit") == Text(namedCommit, "commit"), "pinned branch continuation returns the named commit");
await Reject("VciInputLimit", () => store.BranchesAsync(namedRef, 1));
await Reject("PreviewStale", () => store.BranchesAsync(namedRef, expectedInventorySha256: new string('a', 64)));
Check(Json(await store.BranchesAsync(revision)).GetProperty("total").GetInt32() == 1, "creating and committing a named child leaves parent refs unchanged");
var continued = Json(await store.PrepareAsync([new("more.txt")], author, "continue named", namedRef));
Check(Text(continued, "headReference") == "refs/heads/review/offline", "omitted branch name inherits the exact parent's current branch");
await Reject("GitReferenceConflict", () => store.PrepareAsync([new("rejected.txt")], author, "no reset", namedRef, branchName: "main"));
await Reject("GitInvalidReference", () => store.PrepareAsync([new("rejected.txt")], author, "no flags", branchName: "--orphan"));
var newNamed = Json(await store.PrepareAsync([new("first.txt")], author, "initial named", branchName: "review/initial"));
Check(Text(newNamed, "headReference") == "refs/heads/review/initial", "new repositories accept an explicit initial branch");
var legacy = Json(await store.PrepareAsync([new("legacy.txt")], author, "old review shape"));
string legacyRef = Text(legacy, "recordRef"), legacyPath = Path.Combine(root, "vci-workspaces", "records", legacyRef.Split(':')[2], "review.json");
var legacyDocument = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(legacyPath))!.AsObject(); legacyDocument.Remove("headReference");
File.WriteAllBytes(legacyPath, JsonSerializer.SerializeToUtf8Bytes(legacyDocument, BridgeProtocol.Json));
legacyRef = legacyRef[..(legacyRef.LastIndexOf(':') + 1)] + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(legacyPath)));
Check(Json(store.Reopen(legacyRef)).GetProperty("headReference").ValueKind == JsonValueKind.Null, "older review shape keeps unknown branch provenance explicit");
Check(Text(Json(await store.CommitAsync(legacyRef, Text(legacy, "planSha256"))), "state") == "committed", "older review shape still commits without inventing branch provenance");
string forkReceipt = Path.Combine(root, "vci-workspaces", "records", namedRef.Split(':')[2], "fork-journal", "0002.json");
var forkBytes = File.ReadAllBytes(forkReceipt); File.AppendAllText(forkReceipt, " ");
await Reject("VciIntegrityFailure", () => Task.FromResult(store.Reopen(namedRef)));
File.WriteAllBytes(forkReceipt, forkBytes);
Check(Text(Json(store.Reopen(namedRef)), "headReference") == "refs/heads/review/offline", "fork callbacks are hash-bound downloadable retained artifacts");
checks += await PackageChecks.Run(root, args[0], revision, secondRevision);
Console.WriteLine($"{checks} VCI workflow checks passed; actual local Git, no Siemens products or native qualification.");
