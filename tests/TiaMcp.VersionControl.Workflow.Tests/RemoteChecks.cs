using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.VersionControl;

/// <summary>Remote delivery against a real local bare repository and the reviewed conflict-resolution payload; no Siemens products.</summary>
internal static class RemoteChecks
{
    public static async Task<int> Run(string fixtureRoot, string git)
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS remote " + name); }
        async Task Reject(string code, Func<Task> action)
        {
            try { await action(); } catch (BridgeException error) { Check(error.Code == code, "refuses " + code + ": " + error.Code + " " + error.Message); return; }
            throw new Exception("Expected " + code);
        }
        string root = Path.Combine(fixtureRoot, "remote-store"), bare = Path.Combine(fixtureRoot, "remote.git"), clone = Path.Combine(fixtureRoot, "other-engineer");
        Directory.CreateDirectory(root);
        // The other engineer uses plain Git outside the Host profile, as a real colleague would.
        string Plain(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo(git) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", "user.name=Other Engineer", "-c", "user.email=other@example.invalid", "-c", "core.autocrlf=false" }.Concat(arguments)) start.ArgumentList.Add(a);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("git " + string.Join(' ', arguments) + ": " + error);
            return output.Trim();
        }
        Plain(fixtureRoot, "init", "--bare", "--initial-branch=main", bare);

        Reject2("GitRemoteRefused", () => GitRemote.Parse("relative/remote.git", false));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("https://example.invalid/r.git", false));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("https://user:secret@example.invalid/r.git", true));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("ssh://example.invalid/r.git", true));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("git@example.invalid:r.git", true));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("file://server/share/r.git", false));
        Reject2("GitUnsafePath", () => GitRemote.Parse("//server/share/r.git", false));
        Reject2("GitUnsafePath", () => GitRemote.Parse("\\??\\UNC\\server\\share\\r.git", false));
        Reject2("GitUnsafePath", () => GitRemote.Parse("\\\\?\\" + Path.GetFullPath(bare), false));
        Reject2("GitUnsafePath", () => GitRemote.Parse("\\\\server\\share\\r.git", false));
        Reject2("GitRemoteRefused", () => GitRemote.Parse("ext::sh -c touch% /tmp/pwned", true));
        Check(GitRemote.Parse("https://example.invalid/r.git", true).Protocol == "https", "credential-free https needs the explicit network option");
        var remote = GitRemote.Parse(new Uri(bare).AbsoluteUri, false);
        Check(remote.IsLocal && remote.Location == Path.GetFullPath(bare), "a file URL names the exact local bare repository");
        void Reject2(string code, Action action)
        {
            try { action(); } catch (BridgeException error) { Check(error.Code == code, "parse refuses " + code); return; }
            throw new Exception("Expected " + code);
        }

        var uploads = new ArtifactUploadStore(root);
        var author = new GitAuthor("Host Engineer", "host@example.invalid");
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
        string Upload(byte[] bytes)
        {
            var state = uploads.Begin("fixture.bin", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            uploads.Write(state.UploadId, 0, Convert.ToBase64String(bytes));
            return uploads.Commit(state.UploadId).ArtifactRef!;
        }
        byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        var unconfigured = new VciWorkspaceStore(root, git);
        var store = new VciWorkspaceStore(root, git, remote: remote);
        byte[] FileBytes(string reference, string path) => Convert.FromBase64String(Json(store.ReadFile(reference, path)).GetProperty("data").GetString()!);
        const string package = "VciFc.scl";
        byte[] baseDocument = Bytes("FUNCTION \"VciFc\" : Void\r\nBEGIN\r\n\t#Value := 1;\r\nEND_FUNCTION\r\n");

        var preview = Json(await store.PrepareAsync([new(package, Upload(baseDocument)), new("notes.txt", Upload(Bytes("workspace notes")))], author, "baseline"));
        string rev0 = Text(Json(await store.CommitAsync(Text(preview, "recordRef"), Text(preview, "planSha256"))), "recordRef");
        string c0 = Text(Json(store.Reopen(rev0)), "commit");
        await Reject("GitRemoteNotConfigured", () => unconfigured.PushAsync(rev0, "main"));
        var push0 = Json(await store.PushAsync(rev0, "main"));
        Check(Text(push0, "kind") == "push" && Text(push0, "commit") == c0 && Text(push0, "branch") == "refs/heads/main" && !push0.GetProperty("nativeQualified").GetBoolean(),
            "first push creates the remote branch at the exact revision commit");
        Check(Plain(fixtureRoot, "--git-dir=" + bare, "rev-parse", "refs/heads/main") == c0, "the bare repository independently holds the pushed commit");
        Check(Text(Json(new VciWorkspaceStore(root, git, remote: remote).Reopen(Text(push0, "recordRef"))), "state") == "complete", "push receipt revalidates its verified callback after restart");
        await Reject("GitReferenceConflict", () => store.PushAsync(rev0, "main"));
        Check(Json(store.List(count: 50)).GetProperty("rows").EnumerateArray().All(r => Text(r, "state") != "outcomeUnknown"), "a push refused before dispatch is not an unknown outcome");

        Plain(fixtureRoot, "clone", "--branch", "main", bare, clone);
        byte[] remoteDocument = Bytes("FUNCTION \"VciFc\" : Void\r\nBEGIN\r\n\t#Value := 5;\r\nEND_FUNCTION\r\n");
        File.WriteAllBytes(Path.Combine(clone, package), remoteDocument);
        Plain(clone, "commit", "-am", "Remote value 5"); Plain(clone, "push", "origin", "main");
        string c1 = Plain(clone, "rev-parse", "HEAD");

        await Reject("PreviewStale", () => store.FetchAsync("main", rev0, expectedRemoteCommit: c0));
        await Reject("GitReferenceMissing", () => store.FetchAsync("absent", rev0));
        var fetch1 = Json(await store.FetchAsync("main", rev0, c1));
        string revF = Text(fetch1, "recordRef");
        Check(Text(fetch1, "commit") == c1 && Text(fetch1, "previousCommit") == c0 && Text(fetch1, "parentRevisionRef") == rev0, "fetch fast-forwards a copy of the parent to the remote commit");
        Check(FileBytes(revF, package).SequenceEqual(remoteDocument) && FileBytes(revF, "notes.txt").SequenceEqual(Bytes("workspace notes")), "fetched working files are the exact remote blobs");
        Check(FileBytes(rev0, package).SequenceEqual(baseDocument), "the parent revision is unchanged by the fetch");
        var history = Text(Json(await store.HistoryAsync(revF)), "history");
        Check(history.StartsWith(c1 + "\0", StringComparison.Ordinal) && history.Contains(c0), "fetched history keeps the remote commit on top of the baseline");
        Check(Text(Json(new VciWorkspaceStore(root, git, remote: remote).Reopen(revF)), "state") == "complete", "fetched revision revalidates after restart");
        await Reject("GitNotFastForward", () => store.PushAsync(rev0, "main", c1));

        byte[] mergedDocument = Bytes("FUNCTION \"VciFc\" : Void\r\n{ S7_Optimized_Access := 'TRUE' }\r\nBEGIN\r\n\t#Value := 9;\r\nEND_FUNCTION\r\n");
        var resolved = Json(await store.PrepareAsync([new(package, Upload(mergedDocument))], author, "Resolve VciFc: merged", revF));
        Check(Text(resolved, "headReference") == "refs/heads/main", "a candidate on a fetched parent inherits the remote branch");
        var revM = Json(await store.CommitAsync(Text(resolved, "recordRef"), Text(resolved, "planSha256")));
        var push1 = Json(await store.PushAsync(Text(revM, "recordRef"), "main", c1));
        Check(Plain(fixtureRoot, "--git-dir=" + bare, "rev-parse", "refs/heads/main") == Text(revM, "commit") &&
            Plain(fixtureRoot, "--git-dir=" + bare, "rev-parse", Text(revM, "commit") + "^") == c1, "the resolution commit fast-forwards the remote over the remote commit");
        Check(Text(push1, "expectedRemoteCommit") == c1, "the push receipt binds the reviewed remote commit");
        var fresh = Json(await store.FetchAsync("main"));
        Check(Text(fresh, "commit") == Text(revM, "commit") && fresh.GetProperty("previousCommit").ValueKind == JsonValueKind.Null &&
            FileBytes(Text(fresh, "recordRef"), package).SequenceEqual(mergedDocument), "a parentless fetch reviews the remote branch in a new repository");

        Plain(clone, "pull", "--ff-only", "origin", "main");
        File.WriteAllBytes(Path.Combine(clone, "notes.txt"), Bytes("remote notes")); Plain(clone, "commit", "-am", "Remote notes"); Plain(clone, "push", "origin", "main");
        var local = Json(await store.PrepareAsync([new("local.txt", Upload(Bytes("local")))], author, "unpushed local work", Text(revM, "recordRef")));
        string revL = Text(Json(await store.CommitAsync(Text(local, "recordRef"), Text(local, "planSha256"))), "recordRef");
        await Reject("GitNotFastForward", () => store.FetchAsync("main", revL));
        Check(FileBytes(revL, "notes.txt").SequenceEqual(Bytes("workspace notes")), "a refused divergent fetch leaves the local revision intact");

        string hook = Path.Combine(bare, "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\nexit 0\n");
        await Reject("GitExecutableCustomizationRefused", () => store.PushAsync(revL, "review/local"));
        File.Delete(hook);
        // The remote must pass the runner's config allowlist; every spelling of a redirection is outside it.
        string config = Path.Combine(bare, "config"), original = File.ReadAllText(config);
        foreach (var redirect in new[] { "[core]\n\thooksPath = elsewhere\n", "[core] hooksPath = elsewhere\n", "[core][include]path = elsewhere\n",
            "[core]\n\talternateRefsCommand = elsewhere\n", "[receive]\n\tdenyCurrentBranch = ignore\n" })
        {
            File.WriteAllText(config, original + redirect);
            await Reject("GitUnsupportedConfiguration", () => store.PushAsync(revL, "review/local"));
            await Reject("GitUnsupportedConfiguration", () => store.FetchAsync("main"));
        }
        File.WriteAllText(config, original);
        string alternates = Path.Combine(bare, "objects", "info", "alternates");
        Directory.CreateDirectory(Path.GetDirectoryName(alternates)!); File.WriteAllText(alternates, Path.Combine(root, "elsewhere") + "\n");
        await Reject("GitUnsupportedRepository", () => store.PushAsync(revL, "review/local"));
        File.Delete(alternates);
        // Git's non-strict lookup enters <remote>/.git before <remote>: an inner bare repository with a hook, or a gitdir file.
        // Each case first shows with plain Git that the indirection is live, then that the Host refuses it before any Git call.
        string inner = Path.Combine(bare, ".git"), elsewhere = Path.Combine(fixtureRoot, "elsewhere.git"), marker = Path.Combine(fixtureRoot, "hook-ran");
        string records = Path.Combine(root, "vci-workspaces", "records");
        string Refs(string repository) => Plain(fixtureRoot, "--git-dir=" + repository, "for-each-ref", "--format=%(refname)");
        async Task RefusedFetchReadsNothing(string code, string label)
        {
            var before = Directory.GetDirectories(records).ToHashSet(StringComparer.Ordinal);
            await Reject(code, () => store.FetchAsync("main"));
            var created = Directory.GetDirectories(records).Single(d => !before.Contains(d));
            Check(!Directory.EnumerateFiles(Path.Combine(created, "repository", ".git", "objects"), "*", SearchOption.AllDirectories).Any() &&
                !File.Exists(Path.Combine(created, "fetch-journal", "0001.json")), "a refused fetch dispatched nothing and read no object: " + label);
        }
        Plain(fixtureRoot, "init", "--bare", inner);
        File.WriteAllText(Path.Combine(inner, "hooks", "pre-receive"), "#!/bin/sh\necho ran > \"" + marker.Replace('\\', '/') + "\"\n");
        Plain(clone, "push", bare, "HEAD:refs/heads/control");
        Check(File.Exists(marker) && Refs(inner).Contains("refs/heads/control") && !Refs(bare).Contains("refs/heads/control"),
            "control: plain Git pushing to the remote path runs the inner repository's hook and updates only the inner repository");
        File.Delete(marker);
        await Reject("GitUnsupportedRepository", () => store.PushAsync(revL, "review/local"));
        await RefusedFetchReadsNothing("GitUnsupportedRepository", "inner .git repository");
        Check(!File.Exists(marker) && !Refs(inner).Contains("refs/heads/review/local"), "the Host push never ran the inner hook or reached the inner repository");
        foreach (var file in Directory.EnumerateFiles(inner, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); // Git objects are read-only.
        Directory.Delete(inner, true);
        Plain(fixtureRoot, "init", "--bare", elsewhere);
        File.WriteAllText(inner, "gitdir: " + elsewhere.Replace('\\', '/') + "\n");
        Plain(clone, "push", bare, "HEAD:refs/heads/control");
        Check(Refs(elsewhere) == "refs/heads/control" && !Refs(bare).Contains("refs/heads/control"), "control: plain Git follows a gitdir file at <remote>/.git");
        await Reject("GitUnsupportedRepository", () => store.PushAsync(revL, "review/local"));
        await RefusedFetchReadsNothing("GitUnsupportedRepository", "gitdir file");
        File.Delete(inner);
        Check(Refs(elsewhere) == "refs/heads/control", "the Host push never reached the gitdir target");
        // A junction below refs or objects lets receive-pack write outside the checked remote (reproduced with plain Git first).
        void Junction(string link, string target)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!; process.StandardOutput.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("mklink /J failed: " + process.StandardError.ReadToEnd());
        }
        foreach (var (relative, label) in new[] { ("refs/heads", "refs/heads"), ("objects/pack", "objects/pack") })
        {
            string link = Path.Combine(bare, relative.Replace('/', Path.DirectorySeparatorChar)), saved = link + ".saved", outside = Path.Combine(fixtureRoot, "outside-" + label.Replace('/', '-'));
            Directory.Move(link, saved); Directory.CreateDirectory(outside); Junction(link, outside);
            if (relative == "refs/heads")
            {
                Plain(clone, "push", bare, "HEAD:refs/heads/junctioned");
                Check(File.Exists(Path.Combine(outside, "junctioned")), "control: plain Git writes a pushed ref through a nested refs/heads junction");
            }
            await Reject("GitUnsafePath", () => store.PushAsync(revL, "review/local"));
            await RefusedFetchReadsNothing("GitUnsafePath", "nested " + label + " junction");
            Directory.Delete(link); Directory.Move(saved, link);
        }
        Check(Refs(bare).Split('\n').All(r => r is "refs/heads/main"), "the remote keeps only its own branch after the indirection checks");
        string packed = Path.Combine(bare, "packed-refs"), packedOriginal = File.Exists(packed) ? File.ReadAllText(packed) : null;
        File.AppendAllText(packed, new string('a', 40) + " refs/replace/" + new string('b', 40) + "\n");
        await Reject("GitUnsupportedRepository", () => store.PushAsync(revL, "review/local"));
        if (packedOriginal is null) File.Delete(packed); else File.WriteAllText(packed, packedOriginal);
        Check(Plain(fixtureRoot, "--git-dir=" + bare, "for-each-ref", "refs/heads/review/local") == "", "refused pushes never created the remote branch");
        File.WriteAllText(Path.Combine(clone, ".gitattributes"), "*.scl filter=evil\n"); Plain(clone, "add", ".gitattributes"); Plain(clone, "commit", "-m", "attributes"); Plain(clone, "push", "origin", "main");
        await Reject("GitExecutableCustomizationRefused", () => store.FetchAsync("main"));

        // Three-way plan: baseline = rev0, project = native side (same files, the block's changed native export hash), workspace = the fetched remote side.
        var snapshot = new EngineeringSnapshotIdentity("authored-git-roundtrip", new string('a', 64));
        EngineeringObjectReference Object(char content, string path = "/PLC/blocks/VciFc") => new(new("D:/Fixture/P.ap21", "/PLC", null, path, "block"), snapshot, new string(content, 64));
        var profile = new VciProfileInput("vci-scl-fc", "nativeDocument", "block", "SCL", new string('b', 64), "nativeExport", [new(package, "document")]);
        string Capture(string source, char content, string path = "/PLC/blocks/VciFc") => Text(Json(store.CapturePackages(source, Upload(JsonSerializer.SerializeToUtf8Bytes(
            new VciMappingEvidence("vci-mapping-evidence.v1", "vci/Root/W", snapshot, true, [new("VciFc", Object(content, path), profile, "Equal")]), BridgeProtocol.Json)))), "recordRef");
        string Hash(string manifest) => Text(Json(store.Reopen(manifest)), "manifestSha256");
        string baseline = Capture(rev0, 'c'), native = Capture(rev0, 'd'), remoteSide = Capture(revF, 'c');
        string Compare(string b, string p, string w) => Text(Json(store.ComparePackages(b, p, w, new(Hash(b), Hash(p), Hash(w)))), "recordRef");
        string comparison = Compare(baseline, native, remoteSide);
        JsonElement Resolve(object payload) => JsonSerializer.SerializeToElement(store.ConflictResolutionPayload(JsonSerializer.SerializeToElement(payload, BridgeProtocol.Json)), BridgeProtocol.Json);
        var request = new Dictionary<string, object?> { ["workspacePath"] = "vci/Root/W", ["objectPath"] = "/PLC/blocks/VciFc", ["comparisonRef"] = comparison, ["packageId"] = "VciFc", ["take"] = "remote" };
        var takeRemote = Resolve(request);
        var reviewedFile = takeRemote.GetProperty("workspaceFiles")[0]; var written = takeRemote.GetProperty("resultFiles")[0];
        Check(Text(takeRemote, "direction") == "WorkspaceToProject" && Text(reviewedFile, "path") == package && Text(reviewedFile, "sha256") == Convert.ToHexStringLower(SHA256.HashData(remoteDocument)) &&
            reviewedFile.GetProperty("contentBase64").ValueKind == JsonValueKind.Null && Convert.FromBase64String(Text(written, "contentBase64")).SequenceEqual(remoteDocument) &&
            Text(takeRemote.GetProperty("plan"), "state") == "divergentChange", "take=remote binds the reviewed remote bytes and imports them");
        Check(Text(takeRemote.GetProperty("plan"), "projectObjectContentSha256") == new string('d', 64) && Text(takeRemote.GetProperty("plan"), "workspaceId") == "vci/Root/W",
            "the plan carries the reviewed native object hash and workspace for the worker's live recheck");
        await Reject("VciResolutionMismatch", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["workspacePath"] = "vci/Root/Other" })));
        var takeNative = Resolve(new Dictionary<string, object?>(request) { ["take"] = "native" });
        Check(Text(takeNative, "direction") == "ProjectToWorkspace" && takeNative.GetProperty("resultFiles").GetArrayLength() == 0 &&
            Text(takeNative.GetProperty("workspaceFiles")[0], "sha256") == Text(reviewedFile, "sha256"), "take=native exports the project over the exact reviewed remote side");
        var takeMerged = Resolve(new Dictionary<string, object?>(request) { ["take"] = "merged", ["mergedRecordRef"] = Text(resolved, "recordRef") });
        Check(Convert.FromBase64String(Text(takeMerged.GetProperty("resultFiles")[0], "contentBase64")).SequenceEqual(mergedDocument) &&
            Text(takeMerged.GetProperty("plan"), "mergedRecordRef") == Text(resolved, "recordRef"), "take=merged writes the reviewed merged document from its record");
        await Reject("InvalidRequest", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["take"] = "merged" })));
        await Reject("InvalidRequest", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["mergedRecordRef"] = Text(resolved, "recordRef") })));
        await Reject("InvalidRequest", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["resultFiles"] = Array.Empty<object>() })));
        await Reject("InvalidRequest", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["take"] = "project" })));
        await Reject("VciResolutionMismatch", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["objectPath"] = "/PLC/blocks/Other" })));
        await Reject("VciPackageNotFound", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["packageId"] = "Other" })));
        await Reject("VciResolutionUnsupported", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["comparisonRef"] = Compare(baseline, baseline, remoteSide) })));
        var lacking = Json(await store.PrepareAsync([new("other.txt", Upload(Bytes("no package")))], author, "no package"));
        await Reject("VciPackageIncomplete", () => Task.FromResult(Resolve(new Dictionary<string, object?>(request) { ["take"] = "merged", ["mergedRecordRef"] = Text(lacking, "recordRef") })));
        return checks;
    }
}
