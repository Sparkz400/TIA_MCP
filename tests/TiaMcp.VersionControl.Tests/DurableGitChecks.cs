using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.VersionControl;

internal static class DurableGitChecks
{
    internal static void Register(List<(string, Action)> tests, string executable, string fixtures)
    {
        string basePath = Path.GetFullPath(fixtures + "-durable");
        if (Directory.Exists(basePath)) throw new Exception("Use a new durable fixture directory.");
        Directory.CreateDirectory(basePath);
        string executableHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
        void Test(string name, Func<Task> action) => tests.Add(("Durable Git " + name, () => action().GetAwaiter().GetResult()));
        async Task<(string Root, GitWorkspaceRunner Reader, string Repository, GitCommitPlan Plan)> Seed(string name)
        {
            string root = Path.Combine(basePath, name); Directory.CreateDirectory(root);
            var reader = new GitWorkspaceRunner(executable, Path.Combine(root, "seed-control"), new ForwardJournal());
            string repository = Path.Combine(root, "repository");
            await reader.InitializeAsync(repository, "main");
            File.WriteAllBytes(Path.Combine(repository, "raw.xml"), [0xef, 0xbb, 0xbf, 65, 13, 10]);
            var before = await reader.InspectAsync(repository);
            var plan = await reader.PrepareCommitAsync(repository, before.Sha256, [new("raw.xml")], new("Fixture", "fixture@example.invalid"), "Retained raw export");
            return (root, reader, repository, plan);
        }
        Admission Admit(string root, GitOperationBinding binding, Func<NativeTargetObservation, NativeTargetObservation>? alter = null)
        {
            var reference = new EngineeringObjectReference(new("AuthoredSyntheticProject", "PLC/A", null, "Project", "project"), new("fixture", new('a', 64)), new('b', 64));
            var target = new NativeTargetObservation(reference, Path.Combine(root, "synthetic.ap21"), "authored-epoch", new('c', 64), new('d', 64), "authored-hardware", null, true, []);
            var bindingArtifact = Save(root, "binding.json", binding);
            var plan = binding.BuildPlan(target, bindingArtifact, [], [], ["fixture.preservation"]);
            var store = new EngineeringOperationStore(root);
            var record = store.Prepare(plan, "reviewed-once"); record = store.Acquire(record.Id, record.Revision);
            record = store.BeginCheckpoint(record.Id, record.Revision, () => target);
            record = store.Checkpoint(record.Id, record.Revision, plan.Resources.Select((r, i) =>
                Save(root, "checkpoint-" + i + ".json", new { authoredSyntheticCheckpoint = true, nativeArchive = false, resource = r.ResourceKey })).ToArray());
            var journal = new EngineeringGitOperationJournal(root, record.Id, record.Revision, binding, () => alter?.Invoke(target) ?? target);
            return new(root, target, plan, store, record, journal, binding);
        }
        EngineeringOperationRecord Complete(Admission a, object result) => a.Journal.Complete([
            new("git.result", Save(a.Root, "result.json", result)),
            new("fixture.preservation", Save(a.Root, "preservation.json", new { authoredSyntheticNativeObservation = true, noNativeCalls = true }))]);

        Test("init persists exact branch intent and requires explicit result gates", async () =>
        {
            string root = Path.Combine(basePath, "init"); Directory.CreateDirectory(root);
            var binding = GitOperationBinding.Initialize(Path.Combine(root, "repository"), Path.Combine(root, "control"), executableHash, "main");
            var a = Admit(root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            var result = await runner.InitializeAsync(binding.RepositoryRoot, "main");
            Equal("dispatchIntent", new EngineeringOperationStore(root).Read(a.Ready.Id).State);
            Reject("VerificationIncomplete", () => a.Journal.Complete([]));
            Equal("succeeded", Complete(a, result).State);
            var callbacks = EngineeringGitOperationJournal.ReadCallbacks(root, a.Ready.Id);
            Equal(2, callbacks.Count); Equal(binding.Sha256, callbacks[0].Record.BindingSha256);
            Reject("RecoveryRequired", () => new EngineeringGitOperationJournal(root, a.Ready.Id, a.Ready.Revision, binding, () => a.Target));
        });
        Test("commit readback is durable and excludes unreviewed raw files", async () =>
        {
            var f = await Seed("commit"); File.WriteAllBytes(Path.Combine(f.Repository, "peer.bin"), [42]);
            var before = await f.Reader.InspectAsync(f.Repository);
            var plan = await f.Reader.PrepareCommitAsync(f.Repository, before.Sha256, [new("raw.xml")], f.Plan.Author, f.Plan.Message);
            var binding = GitOperationBinding.Commit(plan, Path.Combine(f.Root, "control"), executableHash);
            var a = Admit(f.Root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            var result = await runner.CommitAsync(plan, plan.Sha256);
            Equal("?? peer.bin\0", (await runner.InspectAsync(f.Repository)).Status);
            Equal(4, EngineeringGitOperationJournal.ReadCallbacks(f.Root, a.Ready.Id).Count);
            Equal("succeeded", Complete(a, result).State);
            Equal(false, result.NativeQualified);
        });
        Test("branch callback binds expected absent ref and immutable target", async () =>
        {
            var f = await Seed("branch"); var committed = await f.Reader.CommitAsync(f.Plan, f.Plan.Sha256);
            var before = await f.Reader.InspectAsync(f.Repository);
            var binding = GitOperationBinding.UpdateBranch(before, Path.Combine(f.Root, "control"), executableHash, "reviewed", committed.Commit, null);
            var a = Admit(f.Root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            var result = await runner.UpdateBranchAsync(f.Repository, before.Sha256, "reviewed", committed.Commit);
            Equal("refs/heads/reviewed", result.Reference); Equal("succeeded", Complete(a, result).State);
            True(binding.Sha256 != (binding with { ExpectedReferenceCommit = committed.Commit }).Sha256);
        });
        Test("materialize owns exact new destination and preserves source snapshot", async () =>
        {
            var f = await Seed("materialize"); var committed = await f.Reader.CommitAsync(f.Plan, f.Plan.Sha256);
            var before = await f.Reader.InspectAsync(f.Repository);
            var binding = GitOperationBinding.Materialize(before, Path.Combine(f.Root, "control"), executableHash, committed.Commit, Path.Combine(f.Root, "recovered"));
            var a = Admit(f.Root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            var result = await runner.MaterializeAsync(f.Repository, committed.Commit, binding.Destination!);
            True(File.ReadAllBytes(Path.Combine(result.Destination, "raw.xml")).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Repository, "raw.xml"))));
            Equal(before.Sha256, (await runner.InspectAsync(f.Repository)).Sha256); Equal(4, a.Plan.Resources.Count);
            Equal("succeeded", Complete(a, result).State);
        });
        Test("stale native recheck blocks dispatch and restart cannot reuse pending callback", async () =>
        {
            string root = Path.Combine(basePath, "stale-target"); Directory.CreateDirectory(root);
            var binding = GitOperationBinding.Initialize(Path.Combine(root, "repository"), Path.Combine(root, "control"), executableHash, "main");
            var a = Admit(root, binding, t => t with { SessionEpoch = "different-owner" });
            var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            await RejectAsync("StaleNativeTarget", () => runner.InitializeAsync(binding.RepositoryRoot, "main"));
            True(!Directory.Exists(binding.RepositoryRoot)); Equal("ready", a.Store.Read(a.Ready.Id).State);
            Reject("RecoveryRequired", () => new EngineeringGitOperationJournal(root, a.Ready.Id, a.Ready.Revision, binding, () => a.Target));
        });
        Test("changed branch choice is refused before Git writes", async () =>
        {
            string root = Path.Combine(basePath, "wrong-binding"); Directory.CreateDirectory(root);
            var binding = GitOperationBinding.Initialize(Path.Combine(root, "repository"), Path.Combine(root, "control"), executableHash, "main");
            var a = Admit(root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            await RejectAsync("GitJournalBindingMismatch", () => runner.InitializeAsync(binding.RepositoryRoot, "unreviewed"));
            True(!Directory.Exists(binding.RepositoryRoot)); Equal("ready", a.Store.Read(a.Ready.Id).State);
        });
        foreach (string phase in new[] { "publicationPrepared", "verified" })
            Test("interruption at " + phase + " retains ownership and refuses automatic replay", async () =>
            {
                var f = await Seed("interrupted-" + phase);
                var binding = GitOperationBinding.Commit(f.Plan, Path.Combine(f.Root, "control"), executableHash);
                var a = Admit(f.Root, binding);
                var journal = new ForwardJournal { Callback = async (record, token) =>
                {
                    await a.Journal.RecordAsync(record, token);
                    if (record.Phase == phase) throw new IOException("Authored interruption after durable " + phase);
                } };
                var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, journal);
                await RejectAsync("OutcomeUnknown", () => runner.CommitAsync(f.Plan, f.Plan.Sha256));
                var reopened = new EngineeringOperationStore(f.Root); var unknown = reopened.Read(a.Ready.Id);
                Equal("outcomeUnknown", unknown.State);
                Equal(phase == "verified", (await f.Reader.InspectAsync(f.Repository)).HeadCommit is not null);
                Equal("outcomeUnknown", EngineeringGitOperationJournal.ReadCallbacks(f.Root, a.Ready.Id)[^1].Record.Phase);
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TiaMcp.VersionControl.Tests.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "--git-durable-reader", f.Root, unknown.Id }) start.ArgumentList.Add(argument);
                using (var child = Process.Start(start)!)
                {
                    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try { await child.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { child.Kill(true); throw; }
                    Equal(0, child.ExitCode); True((await output).Contains("Fresh process retained outcomeUnknown without replay"));
                    Equal("", await error);
                }
                Reject("RecoveryRequired", () => new EngineeringGitOperationJournal(f.Root, unknown.Id, unknown.Revision, binding, () => a.Target));
                var competitor = reopened.Prepare(a.Plan, "competitor");
                Reject("ResourceBusy", () => reopened.Acquire(competitor.Id, competitor.Revision));
                Reject("VerificationIncomplete", () => a.Journal.Complete([]));
                var proof = Save(f.Root, "reconciliation.json", new { authoredFixture = true, nativeCalled = false, actualHead = (await f.Reader.InspectAsync(f.Repository)).HeadCommit });
                var resolved = reopened.Reconcile(unknown.Id, unknown.Revision, a.Plan.Resources.Select(r =>
                    new ResourceReconciliation(r.ResourceKey, r.Kind == "project" ? "unchanged" : "verifiedApplied", null, proof)).ToArray());
                Equal("reconciled", resolved.State); // Test fixture claims are explicitly synthetic; production supplies actual per-resource readback.
            });
        Test("tampered earlier callback prevents completion", async () =>
        {
            var f = await Seed("tampered-callback"); var binding = GitOperationBinding.Commit(f.Plan, Path.Combine(f.Root, "control"), executableHash);
            var a = Admit(f.Root, binding); var runner = new GitWorkspaceRunner(executable, binding.ControlDirectory, a.Journal);
            await runner.CommitAsync(f.Plan, f.Plan.Sha256);
            string first = Path.Combine(f.Root, "git-callbacks", a.Ready.Id, "0001.json");
            File.AppendAllText(first, " ");
            Reject("GitJournalInvalid", () => a.Journal.Complete([])); Equal("dispatchIntent", a.Store.Read(a.Ready.Id).State);
        });
    }

    internal static void ReadInFreshProcess(string root, string operationId)
    {
        var store = new EngineeringOperationStore(root); var record = store.Read(operationId);
        Equal("outcomeUnknown", record.State);
        var binding = JsonSerializer.Deserialize<GitOperationBinding>(File.ReadAllText(Path.Combine(root, "binding.json")), BridgeProtocol.Json)!;
        Reject("RecoveryRequired", () => new EngineeringGitOperationJournal(root, operationId, record.Revision, binding, () => record.Plan.Target));
        True(EngineeringGitOperationJournal.ReadCallbacks(root, operationId).Count >= 4);
        Equal(record.Revision, store.Read(operationId).Revision);
        Console.WriteLine("Fresh process retained outcomeUnknown without replay");
    }

    private sealed record Admission(string Root, NativeTargetObservation Target, EngineeringOperationPlan Plan,
        EngineeringOperationStore Store, EngineeringOperationRecord Ready, EngineeringGitOperationJournal Journal, GitOperationBinding Binding);
    private sealed class ForwardJournal : IGitOperationJournal
    {
        internal Func<GitOperationRecord, CancellationToken, Task>? Callback;
        public Task RecordAsync(GitOperationRecord record, CancellationToken token) => Callback?.Invoke(record, token) ?? Task.CompletedTask;
    }
    private static EngineeringArtifactReference Save(string root, string path, object value)
    {
        string content = CanonicalJson.Serialize(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
        byte[] bytes = Encoding.UTF8.GetBytes(content); File.WriteAllBytes(Path.Combine(root, path), bytes);
        return new(path, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    private static void True(bool value) { if (!value) throw new Exception("Expected true."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (BridgeException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static async Task RejectAsync(string code, Func<Task> action)
    { try { await action(); } catch (BridgeException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
