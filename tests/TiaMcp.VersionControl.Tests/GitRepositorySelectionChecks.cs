using System.Reflection;
using TiaMcp.Core;
using TiaMcp.Core.VersionControl;

internal static class GitRepositorySelectionChecks
{
    internal static async Task Run(string executable, string fixture)
    {
        fixture = Path.GetFullPath(fixture);
        if (Directory.Exists(fixture)) throw new Exception("Use a fresh repository-selection fixture.");
        Directory.CreateDirectory(fixture);
        var journal = new Journal();
        var runner = new GitWorkspaceRunner(executable, Path.Combine(fixture, "control"), journal);
        string repository = Path.Combine(fixture, "outer");
        await runner.InitializeAsync(repository, "main");
        File.WriteAllBytes(Path.Combine(repository, "sentinel.bin"), [0, 255, 13, 10]);
        var snapshot = await runner.InspectAsync(repository);
        var plan = await runner.PrepareCommitAsync(repository, snapshot.Sha256, [new("sentinel.bin")],
            new GitAuthor("Isolation Fixture", "fixture@example.invalid"), "Owned outer fixture");
        var committed = await runner.CommitAsync(plan, plan.Sha256);
        string child = Path.Combine(repository, "child-without-metadata");
        Directory.CreateDirectory(child);
        var before = await runner.InspectAsync(repository);

        // Exercise the actual private process boundary after an inner directory
        // has no metadata. A read-only command must not discover the outer HEAD.
        var process = typeof(GitWorkspaceRunner).GetField("git", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
        var method = process.GetType().GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(process, [child, new[] { "rev-parse", "--verify", "HEAD" }, CancellationToken.None, null, null, true])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        int exit = (int)result.GetType().GetProperty("ExitCode")!.GetValue(result)!;
        if (exit == 0) throw new Exception("Missing child metadata selected the enclosing repository HEAD.");
        if ((await runner.InspectAsync(repository)).Sha256 != before.Sha256)
            throw new Exception("Repository selection probe changed the owned outer repository.");

        if (OperatingSystem.IsWindows())
        {
            string target = fixture;
            while (target.Length < 247) target = Path.Combine(target, new string('z', Math.Min(40, Math.Max(1, 246 - target.Length))));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            int count = journal.Count;
            try { await runner.InitializeAsync(target, "main"); throw new Exception("Startup metadata path bound was not enforced."); }
            catch (BridgeException refusal) when (refusal.Code == "GitWorkingDirectoryTooLong") { }
            if (Directory.Exists(target) || journal.Count != count)
                throw new Exception("Unsupported metadata path reached mutation dispatch.");
        }
        Console.WriteLine("PASS Git exact repository selection: missing child metadata refuses, parent unchanged, startup bound refuses before dispatch; outer commit " + committed.Commit);
    }
    private sealed class Journal : IGitOperationJournal
    {
        internal int Count;
        public Task RecordAsync(GitOperationRecord record, CancellationToken cancellationToken) { Count++; return Task.CompletedTask; }
    }
}
