using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Tests;

internal static class AddInHandoffTests
{
    internal sealed class Clock : TimeProvider
    {
        internal long Ticks;
        internal TimeSpan Wall;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero) + Wall;
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "tia-handoff-" + Guid.NewGuid().ToString("N"));
        internal readonly Clock Clock = new();
        internal readonly AddInHandoffPeer Peer;
        internal AddInHandoffBinding Binding;
        internal readonly string ReviewId;
        internal readonly JsonObject Request;
        internal byte[] Bytes => Encoding.UTF8.GetBytes(Request.ToJsonString() + "\n");
        internal AddInHandoff Store;
        internal int Calls;
        internal Func<int, CancellationToken, Task>? OnVerify;
        internal Fixture()
        {
            string? directory = AppContext.BaseDirectory; string path = "";
            while (directory is not null)
            {
                path = Path.Combine(directory, "fixtures/project-explorer/representative.project-model.json");
                if (File.Exists(path)) break; directory = Path.GetDirectoryName(directory);
            }
            if (directory is null) throw new FileNotFoundException("Missing original model fixture.");
            var model = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); model["sessionGeneration"] = Guid.NewGuid().ToString("N");
            ReviewId = (string)new ProjectReviewStore(Root).Save(model, [], new())["reviewId"]!;
            var node = model["nodes"]!.AsArray().OfType<JsonObject>().First(n => (string?)n["origin"] == "native" && n["objectPath"] is not null);
            Request = new() { ["schemaVersion"] = "addin-selection.v1", ["reviewId"] = ReviewId, ["selectionId"] = Id(),
                ["selectedAtUtc"] = "2026-10-18T00:00:00.0000000+00:00", ["surface"] = "projectTree", ["projectHost"] = model["project"]!["host"]!.DeepClone(),
                ["producer"] = new JsonObject { ["productId"] = "Synthetic lifecycle fixture", ["version"] = "0.0.0", ["tiaVersion"] = 21,
                    ["assemblySha256"] = new string('b', 64), ["configurationSha256"] = new string('c', 64) },
                ["references"] = new JsonArray(new JsonObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = model["snapshotId"]!.DeepClone(),
                    ["sessionGeneration"] = model["sessionGeneration"]!.DeepClone(), ["projectPath"] = model["project"]!["projectPath"]!.DeepClone(),
                    ["nodeId"] = node["nodeId"]!.DeepClone(), ["objectPath"] = node["objectPath"]!.DeepClone() }) };
            using var self = Process.GetCurrentProcess();
            Peer = new(self.Id, self.StartTime.ToUniversalTime().ToString("O"), self.MainModule!.FileName, self.SessionId);
            Binding = new(Environment.ProcessId, Id(), (string)model["sessionGeneration"]!, Peer, (string)model["project"]!["host"]!,
                (string)model["project"]!["projectPath"]!, (string)model["snapshotId"]!, new string('b', 64), new string('c', 64), new string('e', 64), true);
            Store = new(Root, Verify, Clock);
            Console.WriteLine("HANDOFF_FIXTURE " + JsonSerializer.Serialize(new { root = Root, nativeExecuted = false, authority = "synthetic", peer = "current process metadata; no transport authentication" }));
        }
        private async Task<AddInHandoffBinding> Verify(AddInHandoffPeer peer, string review, string hash, CancellationToken token)
        {
            Calls++; Check.Equal(Peer, peer); Check.Equal(ReviewId, review); Check.Equal(Hash(Bytes), hash);
            if (OnVerify is not null) await OnVerify(Calls, token); return Binding;
        }
        internal Task<JsonObject> Begin(string id, byte[]? bytes = null, CancellationToken token = default) => Store.BeginAsync(id, ReviewId, bytes ?? Bytes, Peer, token);
        internal string Journal(string id, string name) => Path.Combine(Root, "addin-handoffs", id, name);
        internal async Task Reopen() { await Store.DisposeAsync(); Store = new(Root, Verify, Clock); }
        internal void Evidence(string name, JsonObject status) => File.WriteAllText(Path.Combine(Root, name + ".status.json"), status.ToJsonString());
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Challenge(JsonObject value) => (string)value["challenge"]!;
    private static void Unqualified(JsonObject value)
    {
        foreach (string key in new[] { "producerAuthenticated", "liveSelectionVerified", "nativeQualified" }) Check.Equal(false, (bool)value[key]!);
    }
    internal static async Task CompletionAndRestart()
    {
        await using var f = new Fixture(); string id = Id(); byte[] original = f.Bytes, buffer = original.ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnVerify = async (_, token) => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var begun = f.Begin(id, buffer); await entered.Task; Array.Fill(buffer, (byte)' '); release.SetResult();
        var challenge = await begun; f.OnVerify = null; Unqualified(challenge);
        Check.Equal(Hash(original), (string)challenge["selectionSha256"]!);
        foreach (string file in Directory.GetFiles(Path.Combine(f.Root, "addin-handoffs"), "*", SearchOption.AllDirectories).Where(p => !p.EndsWith("owner.lock")))
            Check.Equal(false, File.ReadAllText(file).Contains(Challenge(challenge), StringComparison.Ordinal));
        var consuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnVerify = async (count, token) => { if (count == 2) { consuming.SetResult(); await allowCompletion.Task.WaitAsync(token); } };
        var complete = f.Store.CompleteAsync(id, Challenge(challenge), f.Peer); await consuming.Task;
        var duplicate = Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        Check.Equal(false, duplicate.IsCompleted); allowCompletion.SetResult();
        await Task.WhenAll(complete, duplicate); var result = await complete; f.OnVerify = null;
        Check.Equal("completed", (string)result["state"]!); Unqualified(result); Check.Equal(3, f.Calls);
        Check.Equal(true, original.SequenceEqual(ProjectReviewStore.ReadArtifact(f.Root, result["selection"]!["input"]!.AsObject())));
        await Check.BridgeError("HandoffRequestExists", () => f.Begin(id));
        var changed = JsonNode.Parse(Encoding.UTF8.GetString(original))!.AsObject(); changed["selectionId"] = Id();
        await Check.BridgeError("HandoffRequestExists", () => f.Begin(id, Encoding.UTF8.GetBytes(changed.ToJsonString())));
        await f.Reopen(); var reopened = await f.Store.ReadAsync(id);
        Check.Equal(true, JsonNode.DeepEquals(result, reopened)); Check.Equal(3, f.Calls);
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        await Check.BridgeError("HandoffRequestExists", () => f.Begin(id));
        f.Evidence("completed", reopened);
    }
    internal static async Task OwnerAndCancellation()
    {
        await using var f = new Fixture(); string id = Id(); var challenge = await f.Begin(id); int calls = f.Calls;
        await Check.BridgeError("HandoffChallengeMismatch", () => f.Store.CompleteAsync(id, new string('0', 64), f.Peer));
        await Check.BridgeError("HandoffPeerMismatch", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer with { ProcessId = f.Peer.ProcessId + 1 }));
        Check.Equal(calls, f.Calls);
        var binding = f.Binding; f.Binding = binding with { WorkerInstanceId = Id() };
        await Check.BridgeError("HandoffOwnerChanged", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        Check.Equal(false, Directory.Exists(Path.Combine(f.Root, "addin-selections")));
        f.Binding = binding; await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        var failed = await f.Store.ReadAsync(id); Check.Equal("consumedNoReceipt", (string)failed["state"]!); f.Evidence("owner-changed", failed);
        string cancelledId = Id(); var cancelledChallenge = await f.Begin(cancelledId);
        await f.Store.CancelAsync(cancelledId, Challenge(cancelledChallenge), f.Peer);
        Check.Equal("cancelled", (string)(await f.Store.ReadAsync(cancelledId))["state"]!);
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(cancelledId, Challenge(cancelledChallenge), f.Peer));
        f.Evidence("cancelled", await f.Store.ReadAsync(cancelledId));
        string interruptedId = Id(); var interruptedChallenge = await f.Begin(interruptedId);
        f.OnVerify = (_, token) => Task.Delay(Timeout.Infinite, token);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Check.ThrowsAsync<OperationCanceledException>(() => f.Store.CompleteAsync(interruptedId, Challenge(interruptedChallenge), f.Peer, cancellation.Token));
        f.OnVerify = null; Check.Equal("consumedNoReceipt", (string)(await f.Store.ReadAsync(interruptedId))["state"]!);
        await f.Reopen(); calls = f.Calls; await f.Store.ReadAsync(interruptedId); Check.Equal(calls, f.Calls);
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(interruptedId, Challenge(interruptedChallenge), f.Peer));
        string late = Id(); var lateChallenge = await f.Begin(late); calls = f.Calls;
        f.OnVerify = (count, _) => { if (count == calls + 2) f.Binding = binding with { WorkerInstanceId = Id() }; return Task.CompletedTask; };
        await Check.BridgeError("HandoffOwnerChanged", () => f.Store.CompleteAsync(late, Challenge(lateChallenge), f.Peer));
        Check.Equal(true, Directory.Exists(Path.Combine(f.Root, "addin-selections")));
        Check.Equal("consumedNoReceipt", (string)(await f.Store.ReadAsync(late))["state"]!);
        f.Binding = binding; f.OnVerify = null;
        string latePermit = Id(); var latePermitChallenge = await f.Begin(latePermit);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.OnVerify = async (_, _) => { entered.SetResult(); await release.Task; returned.SetResult(); };
        using var stop = new CancellationTokenSource();
        var interrupted = f.Store.CompleteAsync(latePermit, Challenge(latePermitChallenge), f.Peer, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => interrupted); release.SetResult(); await returned.Task;
        Check.Equal("consumedNoReceipt", (string)(await f.Store.ReadAsync(latePermit))["state"]!);
        Check.Equal(false, File.Exists(f.Journal(latePermit, "receipt.json")));
    }
    internal static async Task ExpiryAndLimits()
    {
        await using var f = new Fixture(); string id = Id(); var challenge = await f.Begin(id);
        f.Clock.Wall = TimeSpan.FromDays(3650); // Wall time alone neither expires nor extends a challenge.
        Check.Equal("completed", (string)(await f.Store.CompleteAsync(id, Challenge(challenge), f.Peer))["state"]!);
        string expired = Id(); var expiredChallenge = await f.Begin(expired); int calls = f.Calls;
        f.Clock.Wall = TimeSpan.FromDays(-3650); f.Clock.Ticks += TimeSpan.FromSeconds(30).Ticks;
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(expired, Challenge(expiredChallenge), f.Peer)); Check.Equal(calls, f.Calls);
        f.Evidence("expired", await f.Store.ReadAsync(expired));
        var ids = new List<(string Id, JsonObject Challenge)>();
        for (int i = 0; i < AddInHandoff.MaximumPending; i++) { string next = Id(); ids.Add((next, await f.Begin(next))); }
        calls = f.Calls; await Check.BridgeError("HandoffCapacity", () => f.Begin(Id())); Check.Equal(calls, f.Calls);
        await f.Reopen(); foreach (var row in ids)
        {
            Check.Equal("interrupted", (string)(await f.Store.ReadAsync(row.Id))["state"]!);
            await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(row.Id, Challenge(row.Challenge), f.Peer));
        }
        f.Evidence("interrupted", await f.Store.ReadAsync(ids[0].Id));
        for (int i = 2 + AddInHandoff.MaximumPending; i < AddInHandoff.MaximumRequests; i++)
        { string next = Id(); var issued = await f.Begin(next); await f.Store.CancelAsync(next, Challenge(issued), f.Peer); }
        calls = f.Calls; await Check.BridgeError("HandoffCapacity", () => f.Begin(Id())); Check.Equal(calls, f.Calls);
        await using var quota = new Fixture();
        using (var fill = new FileStream(Path.Combine(quota.Root, "addin-handoffs", "capacity.fixture"), FileMode.CreateNew, FileAccess.Write)) fill.SetLength(AddInHandoff.MaximumStoreBytes);
        await Check.BridgeError("HandoffCapacity", () => quota.Begin(Id())); Check.Equal(0, quota.Calls);
    }
    private static async Task PublicationRefused(Func<Task> action)
    {
        try { await action(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException error) when ((error.HResult & 0xffff) is 80 or 183)
        {
            // Windows can report FILE_EXISTS/ALREADY_EXISTS for CreateNew against a directory.
            // Keep other I/O failures visible; callers still verify the retained state and no replay.
            return;
        }
        throw new InvalidOperationException("Publication unexpectedly succeeded against an existing directory.");
    }
    internal static async Task PublicationAndTamper()
    {
        await using var f = new Fixture(); string id = Id(); var challenge = await f.Begin(id);
        Directory.CreateDirectory(f.Journal(id, "completed.json"));
        await PublicationRefused(() => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        var uncommitted = await f.Store.ReadAsync(id); Check.Equal("receiptUncommitted", (string)uncommitted["state"]!); f.Evidence("uncommitted", uncommitted);
        await f.Reopen(); Check.Equal("receiptUncommitted", (string)(await f.Store.ReadAsync(id))["state"]!);
        Directory.Delete(f.Journal(id, "completed.json")); // Removing the obstruction never adopts an uncommitted receipt.
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        Check.Equal("receiptUncommitted", (string)(await f.Store.ReadAsync(id))["state"]!);
        string completeId = Id(); var completeChallenge = await f.Begin(completeId); await f.Store.CompleteAsync(completeId, Challenge(completeChallenge), f.Peer);
        foreach (string name in new[] { "plan.json", "selection.input", "consumed.json", "receipt.json" })
        {
            string path = f.Journal(completeId, name); byte[] original = File.ReadAllBytes(path); File.AppendAllText(path, " ");
            await Check.BridgeError("HandoffIntegrityFailure", () => f.Store.ReadAsync(completeId)); File.WriteAllBytes(path, original);
        }
        string reviewPath = Path.Combine(f.Root, "project-reviews", f.ReviewId + ".json"); byte[] review = File.ReadAllBytes(reviewPath);
        File.AppendAllText(reviewPath, " "); await Check.BridgeError("ArtifactChanged", () => f.Store.ReadAsync(completeId)); File.WriteAllBytes(reviewPath, review);
        Check.Equal("completed", (string)(await f.Store.ReadAsync(completeId))["state"]!);
        string other = Id(); var otherChallenge = await f.Begin(other); await f.Store.CompleteAsync(other, Challenge(otherChallenge), f.Peer);
        var originals = new Dictionary<string, byte[]>();
        foreach (string name in new[] { "receipt.json", "completed.json" })
        {
            originals[name] = File.ReadAllBytes(f.Journal(completeId, name));
            File.Copy(f.Journal(other, name), f.Journal(completeId, name), true);
        }
        await Check.BridgeError("HandoffIntegrityFailure", () => f.Store.ReadAsync(completeId));
        foreach (var original in originals) File.WriteAllBytes(f.Journal(completeId, original.Key), original.Value);
        string blocked = Id(); var blockedChallenge = await f.Begin(blocked); Directory.CreateDirectory(f.Journal(blocked, "consumed.json"));
        await PublicationRefused(() => f.Store.CompleteAsync(blocked, Challenge(blockedChallenge), f.Peer));
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(blocked, Challenge(blockedChallenge), f.Peer));
        await f.Reopen(); Check.Equal("interrupted", (string)(await f.Store.ReadAsync(blocked))["state"]!);
        string changed = Id(); var changedChallenge = await f.Begin(changed);
        string input = f.Journal(changed, "selection.input"); byte[] exact = File.ReadAllBytes(input); File.AppendAllText(input, " ");
        await Check.BridgeError("HandoffIntegrityFailure", () => f.Store.CompleteAsync(changed, Challenge(changedChallenge), f.Peer));
        File.WriteAllBytes(input, exact);
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(changed, Challenge(changedChallenge), f.Peer));
    }
    internal static async Task InvalidInputsAndAuthority()
    {
        await using var f = new Fixture();
        await Check.BridgeError("IntakeBusy", async () => { await using var unused = new AddInHandoff(f.Root, (_, _, _, _) => throw new Exception("Must not run")); });
        await Check.BridgeError("HandoffIdentityInvalid", () => f.Begin("../escape"));
        await Check.BridgeError("HandoffPeerInvalid", () => f.Store.BeginAsync(Id(), f.ReviewId, f.Bytes, f.Peer with { ProcessId = 0 }));
        await Check.BridgeError("HandoffLimit", () => f.Begin(Id(), new byte[AddInSelection.InputLimit + 1]));
        await Check.BridgeError("InvalidAddInSelection", () => f.Begin(Id(), Encoding.UTF8.GetBytes("{\"references\":[],\"references\":[]}")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => f.Begin(Id(), token: cancelled.Token)); Check.Equal(0, f.Calls);
        string linked = Id(), link = Path.Combine(f.Root, "addin-handoffs", linked), target = Path.Combine(f.Root, "foreign");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep"), "original");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start)!; await child.WaitForExitAsync(); Check.Equal(0, child.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            await Check.BridgeError("PackageInvalid", () => f.Begin(linked));
            Check.Equal(0, f.Calls); Check.Equal("original", File.ReadAllText(Path.Combine(target, "keep")));
        }
        finally { Directory.Delete(link); }
        string occupied = Id(); File.WriteAllText(Path.Combine(f.Root, "addin-handoffs", occupied), "retained obstruction");
        await Check.BridgeError("HandoffRequestExists", () => f.Begin(occupied)); Check.Equal(0, f.Calls);
        string wrong = Id(); var valid = f.Binding; f.Binding = valid with { ConfigurationSha256 = new string('a', 64) };
        await Check.BridgeError("HandoffOwnerMismatch", () => f.Begin(wrong));
        Check.Equal(false, Directory.Exists(Path.Combine(f.Root, "addin-handoffs", wrong))); f.Binding = valid;
        f.OnVerify = (_, _) => throw new BridgeException("SyntheticAuthorityRefused", "Deliberate test refusal, no native producer exists.");
        await Check.BridgeError("SyntheticAuthorityRefused", () => f.Begin(Id()));
        Check.Equal(0, Directory.GetDirectories(Path.Combine(f.Root, "addin-handoffs")).Length);
        string incomplete = Id(); f.OnVerify = (_, _) => { Directory.CreateDirectory(f.Journal(incomplete, "plan.json")); return Task.CompletedTask; };
        await PublicationRefused(() => f.Begin(incomplete));
        Check.Equal(true, File.Exists(f.Journal(incomplete, "selection.input")));
        int calls = f.Calls; await Check.BridgeError("HandoffRequestExists", () => f.Begin(incomplete)); Check.Equal(calls, f.Calls);
        f.OnVerify = null; string id = Id(); var challenge = await f.Begin(id);
        f.OnVerify = (_, _) => { f.Clock.Ticks += TimeSpan.FromSeconds(30).Ticks; return Task.CompletedTask; };
        await Check.BridgeError("HandoffChallengeUnavailable", () => f.Store.CompleteAsync(id, Challenge(challenge), f.Peer));
        Check.Equal("consumedNoReceipt", (string)(await f.Store.ReadAsync(id))["state"]!);
        await f.Store.DisposeAsync(); await f.Store.DisposeAsync();
        await Check.ThrowsAsync<ObjectDisposedException>(() => f.Store.ReadAsync(id));
    }
}
