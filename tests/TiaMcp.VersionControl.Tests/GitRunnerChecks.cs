using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

internal static class GitRunnerChecks
{
    private static readonly GitAuthor Author = new("VCI Fixture", "vci-fixture@example.invalid");
    internal static void Register(List<(string, Action)> tests, string executable, string fixtures)
    {
        fixtures = Path.GetFullPath(fixtures);
        if (Directory.Exists(fixtures)) throw new InvalidOperationException("Git fixtures require a new isolated run directory.");
        Directory.CreateDirectory(fixtures);
        void Test(string name, Func<Task> run) => tests.Add(("Git " + name, () => run().GetAwaiter().GetResult()));
        Test("long-path workspace retains raw commits across restart and export", () => GitLongPathChecks.Run(executable, Path.Combine(fixtures, "long-path")));
        Test("missing child metadata cannot select an enclosing repository", () => GitRepositorySelectionChecks.Run(executable, Path.Combine(fixtures, "repository-selection")));
        async Task<Fixture> New(string name, GitRunnerLimits? limits = null)
        {
            var directory = Path.Combine(fixtures, name); Directory.CreateDirectory(directory);
            var journal = new Journal(Path.Combine(directory, "journal"));
            var runner = new GitWorkspaceRunner(executable, Path.Combine(directory, "control"), journal, limits);
            var root = Path.Combine(directory, "repository");
            await runner.InitializeAsync(root, "main");
            return new(runner, root, directory, journal);
        }
        async Task<GitCommitResult> Commit(Fixture f, string path, byte[] bytes)
        {
            Write(f.Root, path, bytes);
            var snapshot = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new(path)], Author, "Fixture " + path);
            return await f.Runner.CommitAsync(plan, plan.Sha256);
        }
        Test("raw exact-file commit preserves BOM CRLF binary Unicode and excluded files", async () =>
        {
            var f = await New("raw-commit");
            var payloads = new Dictionary<string, byte[]> {
                ["PLC/Über Motor.s7dcl"] = [0xEF, 0xBB, 0xBF, 65, 13, 10, 66, 13, 10],
                ["PLC/Motor.s7res"] = [0, 1, 255, 10, 13], ["-n.xml"] = Encoding.UTF8.GetBytes("<raw>ä</raw>\n") };
            foreach (var pair in payloads) Write(f.Root, pair.Key, pair.Value);
            Write(f.Root, "unreviewed.txt", [42]);
            var before = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, payloads.Keys.Select(x => new GitFileChange(x)), Author, "Raw exports\n\nLiteral $(whoami) `command` --option");
            var result = await f.Runner.CommitAsync(plan, plan.Sha256);
            Equal(false, result.NativeQualified); Equal(40, result.Commit.Length);
            foreach (var pair in payloads) Bytes(pair.Value, await Raw(executable, f.Root, ["show", result.Commit + ":" + pair.Key]));
            var listing = Encoding.UTF8.GetString(await Raw(executable, f.Root, ["ls-tree", "-r", "--name-only", result.Commit]));
            True(!listing.Contains("unreviewed"));
            Equal("?? unreviewed.txt\0", (await f.Runner.InspectAsync(f.Root)).Status);
            Equal("VCI Fixture <vci-fixture@example.invalid>", Encoding.UTF8.GetString(await Raw(executable, f.Root, ["log", "-1", "--format=%an <%ae>"])).Trim());
            True(f.Journal.Records.Any(x => x.Phase == "publicationPrepared" && x.CandidateCommit == result.Commit));
            var publication = f.Journal.Records.Single(x => x.Phase == "publicationPrepared");
            Equal(before.Sha256, publication.ExpectedSnapshotSha256); Equal(before.IndexSha256, publication.ExpectedIndexSha256);
            Equal(before.ConfigurationSha256, publication.ExpectedConfigurationSha256);
            File.WriteAllText(Path.Combine(f.Directory, "commit-result.json"), JsonSerializer.Serialize(result, BridgeProtocol.Json));
        });
        Test("modify add delete exact tree and offline history diff branch inspection", async () =>
        {
            var f = await New("tree-change");
            var first = await Commit(f, "keep.xml", [1, 2, 3]);
            await Commit(f, "remove.xml", [4, 5]);
            File.Delete(Path.Combine(f.Root, "remove.xml")); Write(f.Root, "add.xml", [6]);
            var before = await f.Runner.InspectAsync(f.Root);
            True((await f.Runner.DiffAsync(f.Root, before.Sha256)).Contains("remove.xml"));
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, [new("remove.xml", true), new("add.xml")], Author, "Reviewed add/delete");
            var result = await f.Runner.CommitAsync(plan, plan.Sha256);
            Equal("", (await f.Runner.InspectAsync(f.Root)).Status);
            Bytes([1, 2, 3], await Raw(executable, f.Root, ["show", result.Commit + ":keep.xml"]));
            True((await f.Runner.HistoryAsync(f.Root)).Contains(first.Commit));
            Equal(1, (await f.Runner.BranchesAsync(f.Root)).Count);
            var after = await f.Runner.InspectAsync(f.Root);
            await Reject("GitNoChanges", () => f.Runner.PrepareCommitAsync(f.Root, after.Sha256, [new("keep.xml")], Author, "No-op"));
        });
        Test("immutable revision materialization verifies raw bytes and preserves source", async () =>
        {
            var f = await New("revision");
            var first = await Commit(f, "raw.bin", [0, 255, 13, 10]);
            await Commit(f, "raw.bin", [1, 2, 3]);
            var before = await f.Runner.InspectAsync(f.Root);
            var result = await f.Runner.MaterializeAsync(f.Root, first.Commit, Path.Combine(f.Directory, "restored"));
            Bytes([0, 255, 13, 10], File.ReadAllBytes(Path.Combine(result.Destination, "raw.bin")));
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
            True(!Directory.Exists(Path.Combine(result.Destination, ".git")));
            Equal(false, result.NativeQualified); Equal(1, result.Files.Count);
            Equal("100644", result.Files[0].Mode);
            File.WriteAllText(Path.Combine(f.Directory, "revision-result.json"), JsonSerializer.Serialize(result, BridgeProtocol.Json));
            await Reject("GitDestinationExists", () => f.Runner.MaterializeAsync(f.Root, first.Commit, result.Destination));
            await Reject("GitInvalidObjectId", () => f.Runner.MaterializeAsync(f.Root, "main", Path.Combine(f.Directory, "moving-ref")));
        });
        Test("reviewed plan binding and later working-byte drift refuse without ref change", async () =>
        {
            var f = await New("stale-bytes"); var first = await Commit(f, "a.xml", [1]);
            Write(f.Root, "a.xml", [2]); var snapshot = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a.xml")], Author, "Next");
            await Reject("PreviewStale", () => f.Runner.CommitAsync(plan, new string('a', 64)));
            Write(f.Root, "a.xml", [3]);
            await Reject("PreviewStale", () => f.Runner.CommitAsync(plan, plan.Sha256));
            Equal(first.Commit, (await f.Runner.InspectAsync(f.Root)).HeadCommit);
        });
        Test("HEAD movement and dirty index invalidate reviewed changes", async () =>
        {
            var f = await New("stale-head-index"); var first = await Commit(f, "a.xml", [1]);
            var second = await Commit(f, "a.xml", [2]); Write(f.Root, "a.xml", [3]);
            var snapshot = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a.xml")], Author, "Next");
            await Raw(executable, f.Root, ["update-ref", "refs/heads/main", first.Commit, second.Commit]);
            await Reject("PreviewStale", () => f.Runner.CommitAsync(plan, plan.Sha256));
            await Raw(executable, f.Root, ["update-ref", "refs/heads/main", second.Commit, first.Commit]);
            await Raw(executable, f.Root, ["add", "--", "a.xml"]);
            snapshot = await f.Runner.InspectAsync(f.Root);
            await Reject("GitDirtyIndex", () => f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a.xml")], Author, "Next"));
        });
        Test("unmerged index conflicts are never considered an admissible snapshot", async () =>
        {
            var f = await New("index-conflict"); await Commit(f, "a.xml", [1]);
            var one = Encoding.UTF8.GetString(await Raw(executable, f.Root, ["hash-object", "-w", "--stdin"], [1])).Trim();
            var two = Encoding.UTF8.GetString(await Raw(executable, f.Root, ["hash-object", "-w", "--stdin"], [2])).Trim();
            var input = $"0 {new string('0', 40)}\ta.xml\n100644 {one} 1\ta.xml\n100644 {one} 2\ta.xml\n100644 {two} 3\ta.xml\n";
            await Raw(executable, f.Root, ["update-index", "--index-info"], Encoding.UTF8.GetBytes(input));
            await Reject("GitConflict", () => f.Runner.InspectAsync(f.Root));
        });
        Test("intent-to-add assume-unchanged and skip-worktree index state refused", async () =>
        {
            var f = await New("index-flags"); await Commit(f, "a", [1]);
            await Raw(executable, f.Root, ["update-index", "--assume-unchanged", "--", "a"]);
            await Reject("GitConflict", () => f.Runner.InspectAsync(f.Root));
            await Raw(executable, f.Root, ["update-index", "--no-assume-unchanged", "--", "a"]);
            await Raw(executable, f.Root, ["update-index", "--skip-worktree", "--", "a"]);
            await Reject("GitConflict", () => f.Runner.InspectAsync(f.Root));
            await Raw(executable, f.Root, ["update-index", "--no-skip-worktree", "--", "a"]);
            Write(f.Root, "intent", [2]); await Raw(executable, f.Root, ["add", "--intent-to-add", "--", "intent"]);
            var snapshot = await f.Runner.InspectAsync(f.Root);
            await Reject("GitDirtyIndex", () => f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("intent")], Author, "Refuse intent state"));
        });
        Test("active hooks refused and hook marker never executed", async () =>
        {
            var f = await New("hook-refusal"); var hooks = Path.Combine(f.Root, ".git", "hooks"); Directory.CreateDirectory(hooks);
            var marker = Path.Combine(f.Directory, "hook-executed").Replace('\\', '/');
            File.WriteAllText(Path.Combine(hooks, "pre-commit"), "#!/bin/sh\nprintf executed > '" + marker + "'\n");
            await Reject("GitExecutableCustomizationRefused", () => f.Runner.InspectAsync(f.Root));
            True(!File.Exists(marker));
        });
        Test("filters attributes includes executable config and remotes refused", async () =>
        {
            var f = await New("config-refusal"); var path = Path.Combine(f.Root, ".git", "config"); var original = File.ReadAllText(path);
            foreach (var customization in new[] { "[filter \"evil\"]\nclean = cmd /c echo executed\n", "[include]\npath = evil.cfg\n",
                "[core]\nfsmonitor = evil.exe\n", "[diff \"evil\"]\ntextconv = evil.exe\n", "[remote \"origin\"]\nurl = https://example.invalid/repo\n" })
            {
                File.WriteAllText(path, original + "\n" + customization);
                await Reject("GitUnsupportedConfiguration", () => f.Runner.InspectAsync(f.Root));
            }
            File.WriteAllText(path, original); Write(f.Root, ".gitattributes", Encoding.UTF8.GetBytes("* filter=evil diff=evil\n"));
            await Reject("GitExecutableCustomizationRefused", () => f.Runner.InspectAsync(f.Root));
            File.Delete(Path.Combine(f.Root, ".gitattributes")); Write(f.Root, ".gitmodules", [1]);
            await Reject("GitExecutableCustomizationRefused", () => f.Runner.InspectAsync(f.Root));
        });
        Test("inherited Git configuration repository and execution environment ignored", async () =>
        {
            var f = await New("environment");
            var variables = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
                ["GIT_CONFIG_VALUE_0"] = "untrusted.exe", ["GIT_DIR"] = Path.Combine(f.Directory, "not-a-repo"),
                ["GIT_WORK_TREE"] = f.Directory, ["GIT_EXTERNAL_DIFF"] = "untrusted.exe", ["GIT_CONFIG_GLOBAL"] = "missing" };
            var old = variables.ToDictionary(x => x.Key, x => Environment.GetEnvironmentVariable(x.Key));
            try
            {
                foreach (var pair in variables) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                Equal(null, (await f.Runner.InspectAsync(f.Root)).HeadCommit);
                Write(f.Root, "a", [1]); var snapshot = await f.Runner.InspectAsync(f.Root);
                var plan = await f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a")], Author, "Environment isolated");
                await f.Runner.CommitAsync(plan, plan.Sha256);
            }
            finally { foreach (var pair in old) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        });
        Test("symbolic branch alias cannot redirect a reviewed publication", async () =>
        {
            var f = await New("symbolic-ref"); await Commit(f, "a", [1]);
            await Raw(executable, f.Root, ["symbolic-ref", "refs/heads/alias", "refs/heads/main"]);
            await Reject("GitUnsupportedRepository", () => f.Runner.InspectAsync(f.Root));
        });
        Test("deleted tracked attributes cannot hide executable configuration", async () =>
        {
            var f = await New("tracked-attributes"); await Commit(f, "a", [1]);
            Write(f.Root, ".gitattributes", Encoding.UTF8.GetBytes("* filter=external\n"));
            await Raw(executable, f.Root, ["add", "--", ".gitattributes"]);
            File.Delete(Path.Combine(f.Root, ".gitattributes"));
            await Reject("GitExecutableCustomizationRefused", () => f.Runner.InspectAsync(f.Root));
        });
        Test("branch compare-and-swap creates updates and refuses wrong prior state", async () =>
        {
            var f = await New("branch-cas"); var one = await Commit(f, "a", [1]); var two = await Commit(f, "a", [2]);
            var before = await f.Runner.InspectAsync(f.Root);
            await f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "review/export", one.Commit);
            await Reject("GitReferenceConflict", () => f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "review/export", two.Commit));
            await Reject("GitReferenceConflict", () => f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "review/export", two.Commit, two.Commit));
            await f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "review/export", two.Commit, one.Commit);
            Equal(two.Commit, (await f.Runner.BranchesAsync(f.Root)).Single(x => x.Reference == "refs/heads/review/export").Commit);
            await Reject("GitCurrentBranchRefused", () => f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "main", one.Commit, two.Commit));
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
        });
        Test("concurrent branch creation CAS retains competitor and unknown dispatch", async () =>
        {
            var f = await New("branch-race"); var one = await Commit(f, "a", [1]); var two = await Commit(f, "a", [2]);
            f.Journal.AfterRecord = async record => { if (record.Operation == "branch" && record.Phase == "dispatchPrepared")
                await Raw(executable, f.Root, ["update-ref", "refs/heads/race", one.Commit]); };
            var before = await f.Runner.InspectAsync(f.Root);
            await Reject("OutcomeUnknown", () => f.Runner.UpdateBranchAsync(f.Root, before.Sha256, "race", two.Commit));
            Equal(one.Commit, (await f.Runner.BranchesAsync(f.Root)).Single(x => x.Reference == "refs/heads/race").Commit);
            True(f.Journal.Records.Any(x => x.Phase == "outcomeUnknown"));
        });
        Test("fork selects a new branch and preserves every raw byte and prior ref", async () =>
        {
            var f = await New("fork-clean"); var one = await Commit(f, "raw", [0xEF, 0xBB, 0xBF, 65, 13, 10, 0, 255]);
            var before = await f.Runner.InspectAsync(f.Root);
            var fork = await f.Runner.ForkAsync(f.Root, before.Sha256, "review/named");
            var after = await f.Runner.InspectAsync(f.Root);
            Equal("refs/heads/review/named", after.HeadReference); Equal(one.Commit, fork.Commit);
            True(before.Files.SequenceEqual(after.Files)); Equal(before.HeadTree, after.HeadTree);
            Equal(one.Commit, (await f.Runner.BranchesAsync(f.Root)).Single(x => x.Reference == "refs/heads/main").Commit);
            await Reject("GitReferenceConflict", () => f.Runner.ForkAsync(f.Root, after.Sha256, "main"));
            await Reject("GitReferenceConflict", () => f.Runner.ForkAsync(f.Root, after.Sha256, "MAIN"));
            await Reject("GitReferenceConflict", () => f.Runner.ForkAsync(f.Root, after.Sha256, "review"));
            Write(f.Root, "raw", [42]); var dirty = await f.Runner.InspectAsync(f.Root);
            await Reject("GitDirtyRepository", () => f.Runner.ForkAsync(f.Root, dirty.Sha256, "dirty"));
        });
        Test("fork refuses a concurrent destination without resetting it", async () =>
        {
            var f = await New("fork-race"); var one = await Commit(f, "a", [1]); var two = await Commit(f, "a", [2]);
            var before = await f.Runner.InspectAsync(f.Root);
            f.Journal.AfterRecord = async row => { if (row.Operation == "fork" && row.Phase == "dispatchPrepared")
                await Raw(executable, f.Root, ["update-ref", "refs/heads/race", one.Commit]); };
            await Reject("OutcomeUnknown", () => f.Runner.ForkAsync(f.Root, before.Sha256, "race"));
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
            Equal(one.Commit, (await f.Runner.BranchesAsync(f.Root)).Single(x => x.Reference == "refs/heads/race").Commit);
        });
        Test("fork journal failures retain exact published state and never replay it", async () =>
        {
            var f = await New("fork-interrupted"); var one = await Commit(f, "a", [1]); var before = await f.Runner.InspectAsync(f.Root);
            f.Journal.AfterRecord = row => row.Operation == "fork" ? throw new IOException("before dispatch") : Task.CompletedTask;
            try { await f.Runner.ForkAsync(f.Root, before.Sha256, "new"); throw new Exception("Expected failure"); } catch (IOException) { }
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
            f.Journal.AfterRecord = row => row.Operation == "fork" && row.Phase == "verified" ? throw new IOException("lost result") : Task.CompletedTask;
            await Reject("OutcomeUnknown", () => f.Runner.ForkAsync(f.Root, before.Sha256, "new"));
            var after = await f.Runner.InspectAsync(f.Root); Equal("refs/heads/new", after.HeadReference); Equal(one.Commit, after.HeadCommit);
            await Reject("PreviewStale", () => f.Runner.ForkAsync(f.Root, before.Sha256, "new"));
            await Reject("GitReferenceConflict", () => f.Runner.ForkAsync(f.Root, after.Sha256, "new"));
        });
        Test("branch inspection rejects refs pointing at non-commit objects", async () =>
        {
            var f = await New("branch-noncommit"); var one = await Commit(f, "a", [1]);
            Directory.CreateDirectory(Path.Combine(f.Root, ".git", "refs", "heads"));
            File.WriteAllText(Path.Combine(f.Root, ".git", "refs", "heads", "invalid"), one.Tree + "\n");
            await Reject("GitInvalidOutput", () => f.Runner.BranchesAsync(f.Root));
        });
        Test("journal failure before dispatch prevents mutation", async () =>
        {
            var f = await New("journal-before"); await Commit(f, "a", [1]); Write(f.Root, "a", [2]);
            var before = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, [new("a")], Author, "Next");
            f.Journal.AfterRecord = record => record.Operation == "commit" ? throw new IOException("fixture durable store unavailable") : Task.CompletedTask;
            try { await f.Runner.CommitAsync(plan, plan.Sha256); throw new Exception("Expected journal failure"); } catch (IOException) { }
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
        });
        Test("lost completion record returns unknown and candidate reconciles by fresh readback", async () =>
        {
            var f = await New("journal-after"); await Commit(f, "a", [1]); Write(f.Root, "a", [2]);
            var before = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, [new("a")], Author, "Unknown response");
            f.Journal.AfterRecord = record => record.Operation == "commit" && record.Phase == "verified" ? throw new IOException("fixture lost acknowledgement") : Task.CompletedTask;
            GitOutcomeUnknownException? observed = null;
            try { await f.Runner.CommitAsync(plan, plan.Sha256); } catch (GitOutcomeUnknownException ex) { observed = ex; }
            True(observed?.CandidateCommit is not null);
            var after = await f.Runner.InspectAsync(f.Root); Equal(observed!.CandidateCommit, after.HeadCommit); Equal("", after.Status);
            Bytes([2], await Raw(executable, f.Root, ["show", after.HeadCommit + ":a"]));
            await Reject("PreviewStale", () => f.Runner.CommitAsync(plan, plan.Sha256));
        });
        Test("index lock and controlled reservation refusal preserve foreign locks", async () =>
        {
            var f = await New("locks"); Write(f.Root, "a", [1]); var snapshot = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a")], Author, "Initial");
            var indexLock = Path.Combine(f.Root, ".git", "index.lock"); File.WriteAllText(indexLock, "foreign");
            await Reject("GitRepositoryBusy", () => f.Runner.CommitAsync(plan, plan.Sha256)); Equal("foreign", File.ReadAllText(indexLock)); File.Delete(indexLock);
            var reservation = Path.Combine(f.Root, ".git", "tia-mcp.operation-reservation"); File.WriteAllText(reservation, "foreign");
            await Reject("GitRepositoryBusy", () => f.Runner.CommitAsync(plan, plan.Sha256)); Equal("foreign", File.ReadAllText(reservation));
        });
        Test("unreviewed option traversal identity and destination inputs refused", async () =>
        {
            var f = await New("inputs"); Write(f.Root, "a", [1]); var snapshot = await f.Runner.InspectAsync(f.Root);
            foreach (var path in new[] { "../a", "a:stream", ".git/config", "folder/.git/HEAD" })
                await Reject(path.Contains(".git") ? "GitUnsafePath" : "VciUnsafePath", () => f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new(path)], Author, "x"));
            await Reject("GitInvalidReference", () => f.Runner.UpdateBranchAsync(f.Root, snapshot.Sha256, "--evil", new string('a', 40)));
            await Reject("GitInvalidCommit", () => f.Runner.PrepareCommitAsync(f.Root, snapshot.Sha256, [new("a")], new("Injected\nAuthor", "a@b"), "x"));
            await Reject("GitDestinationExists", () => f.Runner.InitializeAsync(f.Root, "main"));
            Bytes([1], File.ReadAllBytes(Path.Combine(f.Root, "a")));
        });
        Test("symbolic-link and submodule index modes are refused", async () =>
        {
            var f = await New("index-modes"); var commit = await Commit(f, "a", [1]);
            var blob = Encoding.UTF8.GetString(await Raw(executable, f.Root, ["hash-object", "-w", "--stdin"], Encoding.UTF8.GetBytes("../outside"))).Trim();
            await Raw(executable, f.Root, ["update-index", "--add", "--cacheinfo", "120000," + blob + ",link"]);
            await Reject("GitConflict", () => f.Runner.InspectAsync(f.Root));
            await Raw(executable, f.Root, ["update-index", "--force-remove", "--", "link"]);
            await Raw(executable, f.Root, ["update-index", "--add", "--cacheinfo", "160000," + commit.Commit + ",submodule"]);
            await Reject("GitConflict", () => f.Runner.InspectAsync(f.Root));
        });
        Test("bounded inventory catches ignored files and all binary bytes", async () =>
        {
            var f = await New("bounds", new(MaxFiles: 5, MaxFileBytes: 16, MaxTotalBytes: 32));
            Write(f.Root, "oversize", new byte[17]); await Reject("GitLimitExceeded", () => f.Runner.InspectAsync(f.Root)); File.Delete(Path.Combine(f.Root, "oversize"));
            Write(f.Root, ".gitignore", Encoding.UTF8.GetBytes("*.bin\n"));
            Write(f.Root, "ignored.bin", new byte[16]); Write(f.Root, "second.bin", new byte[16]);
            await Reject("GitLimitExceeded", () => f.Runner.InspectAsync(f.Root));
        });
        Test("actual Git output is bounded and stopped without ref mutation", async () =>
        {
            var f = await New("output-bound"); await Commit(f, "a", Encoding.UTF8.GetBytes("base\n"));
            Write(f.Root, "a", Encoding.UTF8.GetBytes(new string('x', 2048) + "\n"));
            var bounded = new GitWorkspaceRunner(executable, Path.Combine(f.Directory, "bounded-control"), f.Journal, new(MaxOutputBytes: 256));
            var before = await bounded.InspectAsync(f.Root);
            await Reject("GitOutputLimitExceeded", () => bounded.DiffAsync(f.Root, before.Sha256));
            Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
        });
        Test("cancellation before dispatch leaves HEAD index and journal untouched", async () =>
        {
            var f = await New("cancel-before"); Write(f.Root, "a", [1]); var before = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, [new("a")], Author, "Initial");
            var count = f.Journal.Records.Count; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await f.Runner.CommitAsync(plan, plan.Sha256, cancelled.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Equal(count, f.Journal.Records.Count); Equal(before.Sha256, (await f.Runner.InspectAsync(f.Root)).Sha256);
        });
        Test("100-file complete package commit and independently hashed revision export", async () =>
        {
            var f = await New("scale-100");
            var paths = Enumerable.Range(0, 100).Select(i => $"PLC/b{i:D3}.xml").ToArray();
            foreach (var path in paths) Write(f.Root, path, Encoding.UTF8.GetBytes("<fixture path=\"" + path + "\"/>\r\n"));
            var before = await f.Runner.InspectAsync(f.Root);
            var plan = await f.Runner.PrepareCommitAsync(f.Root, before.Sha256, paths.Select(x => new GitFileChange(x)), Author, "100 raw fixture packages");
            var commit = await f.Runner.CommitAsync(plan, plan.Sha256);
            var restored = await f.Runner.MaterializeAsync(f.Root, commit.Commit, Path.Combine(f.Directory, "restored"));
            Equal(100, restored.Files.Count);
            foreach (var file in restored.Files) Bytes(File.ReadAllBytes(Path.Combine(f.Root, file.RelativePath)), File.ReadAllBytes(Path.Combine(restored.Destination, file.RelativePath)));
        });
        if (OperatingSystem.IsWindows())
        {
            Test("process timeout terminates isolated slow executable fixture", async () =>
            {
                var directory = Path.Combine(fixtures, "process-timeout"); Directory.CreateDirectory(directory);
                var fake = CopyProcessFixture(directory);
                var runner = new GitWorkspaceRunner(fake, Path.Combine(directory, "control"), new Journal(Path.Combine(directory, "journal")), new(TimeoutMilliseconds: 100));
                await Reject("GitTimedOut", () => runner.GetVersionAsync());
                CheckChildStopped(directory);
            });
            Test("process cancellation terminates isolated slow executable fixture", async () =>
            {
                var directory = Path.Combine(fixtures, "process-cancel"); Directory.CreateDirectory(directory);
                var fake = CopyProcessFixture(directory);
                var runner = new GitWorkspaceRunner(fake, Path.Combine(directory, "control"), new Journal(Path.Combine(directory, "journal")), new(TimeoutMilliseconds: 5000));
                using var stop = new CancellationTokenSource(200);
                await Reject("GitCancelled", () => runner.GetVersionAsync(stop.Token));
                CheckChildStopped(directory);
            });
        }
    }
    internal static bool RunProcessFixture()
    {
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "git.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "slow-process-fixture"))) return false;
        // Timeout may interrupt startup; publish only a complete PID receipt.
        var receipt = Path.Combine(AppContext.BaseDirectory, "child.pid");
        File.WriteAllText(receipt + ".tmp", Environment.ProcessId.ToString());
        File.Move(receipt + ".tmp", receipt);
        Thread.Sleep(30000); return true;
    }
    private static string CopyProcessFixture(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(x => new[] { ".dll", ".json" }.Contains(Path.GetExtension(x))))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        var output = Path.Combine(directory, "git.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TiaMcp.VersionControl.Tests.exe"), output);
        File.WriteAllText(Path.Combine(directory, "slow-process-fixture"), "Process control only; this executable is not Git.");
        return output;
    }
    private static void CheckChildStopped(string directory)
    {
        var path = Path.Combine(directory, "child.pid");
        if (!File.Exists(path)) return; // A short timeout may stop the host before managed entry.
        try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(path))); True(child.HasExited); } catch (ArgumentException) { }
    }
    private sealed record Fixture(GitWorkspaceRunner Runner, string Root, string Directory, Journal Journal);
    private sealed class Journal(string directory) : IGitOperationJournal
    {
        internal readonly List<GitOperationRecord> Records = [];
        internal Func<GitOperationRecord, Task>? AfterRecord;
        public async Task RecordAsync(GitOperationRecord record, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Records.Count.ToString("D4") + "-" + record.OperationId + "-" + record.Phase + ".json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, BridgeProtocol.Json), cancellationToken);
            Records.Add(record);
            if (AfterRecord is not null) await AfterRecord(record);
        }
    }
    private static void Write(string root, string path, byte[] bytes)
    {
        var target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllBytes(target, bytes);
    }
    private static async Task<byte[]> Raw(string executable, string root, string[] arguments, byte[]? input = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        using var output = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(output); var error = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input);
        process.StandardInput.Close();
        await Task.WhenAll(read, process.WaitForExitAsync());
        if (process.ExitCode != 0) throw new Exception("Fixture Git failed: " + await error);
        return output.ToArray();
    }
    private static async Task Reject(string code, Func<Task> run)
    {
        try { await run(); } catch (BridgeException ex) { Equal(code, ex.Code); return; }
        throw new Exception("Expected " + code);
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    private static void Bytes(byte[] expected, byte[] actual) { if (!expected.SequenceEqual(actual)) throw new Exception("Raw bytes differ"); }
}
