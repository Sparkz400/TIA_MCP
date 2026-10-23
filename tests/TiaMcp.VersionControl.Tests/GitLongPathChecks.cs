using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

internal static class GitLongPathChecks
{
    internal static async Task Run(string executable, string fixture)
    {
        fixture = Path.GetFullPath(fixture);
        if (Directory.Exists(fixture)) throw new Exception("Use a fresh long-path fixture.");
        string root = fixture;
        // Keep the process cwd below Windows MAX_PATH while object and data
        // paths exceed it. Windows cannot start Git with a >=260-character cwd.
        if (root.Length > 165) throw new Exception("Select a fixture root of at most 165 characters.");
        while (root.Length < 165) root = Path.Combine(root, new string('x', Math.Min(40, Math.Max(1, 164 - root.Length))));
        Directory.CreateDirectory(root);
        var store = new VciWorkspaceStore(root, executable);
        var uploads = new ArtifactUploadStore(root);
        byte[] original = [0xEF, 0xBB, 0xBF, 65, 13, 10, 0, 255];
        string name = "PLC/Exports/Controls/Über Motor With Long Name.s7dcl";
        string Upload(byte[] data)
        {
            var upload = uploads.Begin("fixture.bin", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
            uploads.Write(upload.UploadId, 0, Convert.ToBase64String(data));
            return uploads.Commit(upload.UploadId).ArtifactRef!;
        }
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
        string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        byte[] Read(string reference) => Convert.FromBase64String(Text(Json(store.ReadFile(reference, name)), "data"));
        var author = new GitAuthor("Long Path Fixture", "fixture@example.invalid");
        var review = Json(await store.PrepareAsync([new(name, Upload(original))], author, "Long path original"));
        var committed = Json(await store.CommitAsync(Text(review, "recordRef"), Text(review, "planSha256")));
        string revision = Text(committed, "recordRef"), commit = Text(committed, "commit");
        Check(Text(committed, "state") == "committed", "Deep workspace did not commit.");
        string repository = Path.Combine(root, "vci-workspaces", "records", revision.Split(':')[2], "repository");
        Check(repository.Length < 260 && Path.Combine(repository, ".git", "objects", "aa", new string('b', 38)).Length > 260,
            "Fixture must exercise long Git metadata paths below the process cwd limit.");
        Check(Path.Combine(repository, name).Length > 260, "Fixture must exercise a long raw file path.");
        var config = File.ReadAllBytes(Path.Combine(repository, ".git", "config"));
        store = new VciWorkspaceStore(root, executable);
        Check(Read(revision).SequenceEqual(original), "Restart changed original raw bytes.");
        var descendant = Json(await store.PrepareAsync([new(name, Upload([4, 5, 6]))], author, "Long path descendant", revision));
        var second = Json(await store.CommitAsync(Text(descendant, "recordRef"), Text(descendant, "planSha256")));
        Check(Read(revision).SequenceEqual(original), "Descendant changed its parent.");
        Check(Read(Text(second, "recordRef")).SequenceEqual(new byte[] { 4, 5, 6 }), "Descendant bytes differ.");
        Check(Text(Json(await store.HistoryAsync(Text(second, "recordRef"))), "history").Contains(commit), "Parent history is missing.");
        var export = Json(await store.MaterializeAsync(revision));
        var zip = Json(store.ReadArtifact(Text(export, "recordRef"), "revision.zip"));
        using (var archive = new ZipArchive(new MemoryStream(Convert.FromBase64String(Text(zip, "data")))))
        using (var source = archive.GetEntry(name)!.Open())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            Check(buffer.ToArray().SequenceEqual(original), "Deep export changed original bytes.");
        }
        // Independent Git reader verifies the committed blob rather than trusting the store's retained copy.
        var start = new ProcessStartInfo(executable) { WorkingDirectory = repository, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var arg in new[] { "-c", "core.longpaths=true", "--git-dir=.git", "--work-tree=.", "show", commit + ":" + name }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output); var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(read, process.WaitForExitAsync());
        Check(process.ExitCode == 0, "Independent Git read failed: " + await error);
        Check(output.ToArray().SequenceEqual(original), "Committed Git blob differs from original.");
        Check(config.SequenceEqual(File.ReadAllBytes(Path.Combine(repository, ".git", "config"))), "Read/export changed repository configuration.");
        Check(!System.Text.Encoding.UTF8.GetString(config).Contains("longpaths", StringComparison.OrdinalIgnoreCase), "Long-path setting leaked into repository configuration.");
        if (OperatingSystem.IsWindows())
        {
            string overlong = fixture;
            while (overlong.Length < 300) overlong = Path.Combine(overlong, new string('y', 40));
            var journal = new Journal();
            try { _ = new GitWorkspaceRunner(executable, Path.Combine(overlong, "control"), journal); throw new Exception("Long control directory was accepted."); }
            catch (BridgeException refusal) when (refusal.Code == "GitWorkingDirectoryTooLong") { }
            var runner = new GitWorkspaceRunner(executable, Path.Combine(fixture, "refusal-control"), journal);
            Directory.CreateDirectory(Path.GetDirectoryName(overlong)!);
            try { await runner.InitializeAsync(overlong, "main"); throw new Exception("Long process directory was accepted."); }
            catch (BridgeException refusal) when (refusal.Code == "GitWorkingDirectoryTooLong") { }
            Check(journal.Count == 0 && !Directory.Exists(overlong), "Unsupported cwd reached dispatch or created its directory.");
        }
        File.WriteAllText(Path.Combine(fixture, "result.json"), JsonSerializer.Serialize(new {
            status = "passed", repository, repositoryPathLength = repository.Length, commit,
            originalSha256 = Convert.ToHexStringLower(SHA256.HashData(original)),
            restart = true, descendant = true, independentGitRead = true, exactExport = true,
            repositoryConfigurationUnchanged = true, nativeExecuted = false
        }, BridgeProtocol.Json));
        Console.WriteLine("PASS Git long-path workspace: commit, restart, descendant, independent blob read and exact export.");
    }
    private sealed class Journal : IGitOperationJournal
    {
        internal int Count;
        public Task RecordAsync(GitOperationRecord record, CancellationToken cancellationToken) { Count++; return Task.CompletedTask; }
    }
}
