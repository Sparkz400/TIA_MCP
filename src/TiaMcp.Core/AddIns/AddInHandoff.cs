using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TiaMcp.Core.AddIns;

// Supplied by the transport/authority, never deserialized from a producer request.
internal sealed record AddInHandoffPeer(int ProcessId, string StartedAtUtc, string ExecutablePath, int WindowsSessionId);
internal sealed record AddInHandoffBinding(int WorkerProcessId, string WorkerInstanceId, string SessionGeneration,
    AddInHandoffPeer TiaProcess, string ProjectHost, string ProjectPath, string SnapshotId,
    string AssemblySha256, string ConfigurationSha256, string AuthorityEvidenceSha256, bool SyntheticAuthority);

/// <summary>One-use handoff lifecycle. No transport or production enrollment authority is installed by this class.</summary>
internal sealed class AddInHandoff : IAsyncDisposable
{
    internal const int MaximumPending = 8, MaximumRequests = 64;
    internal const long MaximumStoreBytes = 80L << 20;
    internal static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(30);
    private const int MetadataLimit = 64 << 10;
    private sealed record Plan(int Version, string RequestId, string ReceiverInstanceId, int HostProcessId,
        string ReviewId, string SelectionSha256, AddInHandoffPeer Peer, AddInHandoffBinding Binding, DateTimeOffset IssuedUtc);
    private sealed record Consumption(string PlanSha256, string Action);
    private sealed record Receipt(int Version, string PlanSha256, string ConsumptionSha256, string SelectionRecordId);
    private sealed record Completion(string ReceiptSha256);
    private sealed record Pending(string Challenge, long IssuedTimestamp, string PlanSha256, AddInHandoffPeer Peer);
    private readonly string artifacts, root;
    private readonly Func<AddInHandoffPeer, string, string, CancellationToken, Task<AddInHandoffBinding>> verify;
    private readonly TimeProvider clock;
    private readonly FileStream owner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    internal string InstanceId => instanceId;
    private bool disposed;
    private static readonly JsonSerializerOptions Json = new(BridgeProtocol.Json) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true, MaxDepth = 24 };

    internal AddInHandoff(string artifactsRoot,
        Func<AddInHandoffPeer, string, string, CancellationToken, Task<AddInHandoffBinding>> verifyAuthority,
        TimeProvider? timeProvider = null)
    {
        verify = verifyAuthority ?? throw new ArgumentNullException(nameof(verifyAuthority));
        clock = timeProvider ?? TimeProvider.System;
        artifacts = Path.GetFullPath(artifactsRoot); root = Path.Combine(artifacts, "addin-handoffs");
        PackageStorage.EnsureNoLinks(root); Directory.CreateDirectory(root); PackageStorage.EnsureNoLinks(root);
        owner = PackageStorage.Lock(Path.Combine(root, "owner.lock"));
    }

    internal async Task<JsonObject> BeginAsync(string requestId, string reviewId, byte[] selection, AddInHandoffPeer peer, CancellationToken token = default)
    {
        Id(requestId); Digest(reviewId); ValidatePeer(peer);
        Require(selection is { Length: > 0 and <= AddInSelection.InputLimit }, "HandoffLimit");
        byte[] bytes = selection.ToArray(); // Freeze the producer's buffer before awaiting authority.
        var request = ValidateSelection(reviewId, bytes);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); Expire();
            Require(!Directory.Exists(PathFor(requestId)) && !File.Exists(PathFor(requestId)), "HandoffRequestExists");
            Require(pending.Count < MaximumPending && Directory.EnumerateDirectories(root).Take(MaximumRequests).Count() < MaximumRequests, "HandoffCapacity");
            Capacity(bytes.Length + MetadataLimit);
            string digest = Hash(bytes);
            var binding = await Verify(peer, reviewId, digest, token);
            ValidateBinding(binding, peer, request);
            token.ThrowIfCancellationRequested();
            var plan = new Plan(1, requestId, instanceId, Environment.ProcessId, reviewId, digest, peer, binding, clock.GetUtcNow());
            byte[] planBytes = Encode(plan); Require(planBytes.Length <= MetadataLimit, "HandoffLimit");
            Capacity(bytes.Length + planBytes.Length);
            Directory.CreateDirectory(PathFor(requestId));
            WriteNew(PathFor(requestId, "selection.input"), bytes);
            WriteNew(PathFor(requestId, "plan.json"), planBytes);
            token.ThrowIfCancellationRequested();
            string challenge = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            pending.Add(requestId, new(challenge, clock.GetTimestamp(), Hash(planBytes), peer));
            return new() { ["schemaVersion"] = "addin-handoff-challenge.v1", ["requestId"] = requestId,
                ["receiverInstanceId"] = instanceId, ["challenge"] = challenge, ["lifetimeMilliseconds"] = 30000,
                ["selectionSha256"] = digest, ["syntheticAuthority"] = binding.SyntheticAuthority,
                ["producerAuthenticated"] = false, ["liveSelectionVerified"] = false, ["nativeQualified"] = false };
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonObject> CompleteAsync(string requestId, string challenge, AddInHandoffPeer peer, CancellationToken token = default)
    {
        Id(requestId); Digest(challenge); ValidatePeer(peer);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); Expire();
            var (plan, bytes, issued) = Match(requestId, challenge, peer);
            // A failed publication/authority check after this point cannot make the challenge usable again.
            byte[] consumed = Encode(new Consumption(issued.PlanSha256, "complete"));
            WriteNew(PathFor(requestId, "consumed.json"), consumed);
            Require(clock.GetElapsedTime(issued.IssuedTimestamp) < ChallengeLifetime, "HandoffChallengeUnavailable");
            token.ThrowIfCancellationRequested();
            var binding = await Verify(peer, plan.ReviewId, plan.SelectionSha256, token);
            Require(binding == plan.Binding, "HandoffOwnerChanged");
            Require(clock.GetElapsedTime(issued.IssuedTimestamp) < ChallengeLifetime, "HandoffChallengeUnavailable");
            token.ThrowIfCancellationRequested();
            var result = new AddInSelectionStore(artifacts).ReviewBytes(plan.ReviewId, bytes);
            binding = await Verify(peer, plan.ReviewId, plan.SelectionSha256, token);
            Require(binding == plan.Binding, "HandoffOwnerChanged");
            Require(clock.GetElapsedTime(issued.IssuedTimestamp) < ChallengeLifetime, "HandoffChallengeUnavailable");
            token.ThrowIfCancellationRequested();
            byte[] receipt = Encode(new Receipt(1, issued.PlanSha256, Hash(consumed), (string)result["recordId"]!));
            WriteNew(PathFor(requestId, "receipt.json"), receipt);
            WriteNew(PathFor(requestId, "completed.json"), Encode(new Completion(Hash(receipt))));
            return ReadLocked(requestId);
        }
        finally { gate.Release(); }
    }

    internal async Task CancelAsync(string requestId, string challenge, AddInHandoffPeer peer, CancellationToken token = default)
    {
        Id(requestId); Digest(challenge); ValidatePeer(peer);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); Expire();
            var (_, _, issued) = Match(requestId, challenge, peer);
            WriteNew(PathFor(requestId, "consumed.json"), Encode(new Consumption(issued.PlanSha256, "cancel")));
        }
        finally { gate.Release(); }
    }

    internal async Task<JsonObject> ReadAsync(string requestId, CancellationToken token = default)
    {
        Id(requestId); await gate.WaitAsync(token);
        try { ObjectDisposedException.ThrowIf(disposed, this); Expire(); return ReadLocked(requestId); }
        finally { gate.Release(); }
    }

    private (Plan Plan, byte[] Selection, Pending Issued) Match(string requestId, string challenge, AddInHandoffPeer peer)
    {
        Require(pending.TryGetValue(requestId, out var issued), "HandoffChallengeUnavailable");
        Require(CryptographicOperations.FixedTimeEquals(Convert.FromHexString(challenge), Convert.FromHexString(issued!.Challenge)), "HandoffChallengeMismatch");
        Require(peer == issued.Peer, "HandoffPeerMismatch");
        pending.Remove(requestId);
        var (plan, bytes, planHash) = ReadPlan(requestId);
        Require(planHash == issued.PlanSha256 && plan.ReceiverInstanceId == instanceId, "HandoffIntegrityFailure");
        Require(peer == plan.Peer, "HandoffPeerMismatch");
        return (plan, bytes, issued);
    }

    private JsonObject ReadLocked(string requestId)
    {
        var (plan, _, planHash) = ReadPlan(requestId);
        if (pending.TryGetValue(requestId, out var issued)) Require(issued.PlanSha256 == planHash, "HandoffIntegrityFailure");
        string state = pending.ContainsKey(requestId) ? "awaitingResponse" : plan.ReceiverInstanceId == instanceId ? "expiredOrUnavailable" : "interrupted";
        JsonObject? selection = null;
        if (File.Exists(PathFor(requestId, "consumed.json")))
        {
            byte[] consumed = Read(PathFor(requestId, "consumed.json"), MetadataLimit);
            var marker = Decode<Consumption>(consumed);
            Require(marker.PlanSha256 == planHash && marker.Action is "complete" or "cancel", "HandoffIntegrityFailure");
            state = marker.Action == "cancel" ? "cancelled" : "consumedNoReceipt";
            if (File.Exists(PathFor(requestId, "completed.json")))
            {
                string expected = Decode<Completion>(Read(PathFor(requestId, "completed.json"), 1024)).ReceiptSha256; Digest(expected);
                byte[] raw = Read(PathFor(requestId, "receipt.json"), MetadataLimit); Require(Hash(raw) == expected, "HandoffIntegrityFailure");
                var receipt = Decode<Receipt>(raw);
                Require(marker.Action == "complete" && receipt.Version == 1 && receipt.PlanSha256 == planHash && receipt.ConsumptionSha256 == Hash(consumed), "HandoffIntegrityFailure");
                selection = new AddInSelectionStore(artifacts).Page(receipt.SelectionRecordId);
                Require((string?)selection["reviewId"] == plan.ReviewId && (string?)selection["input"]?["sha256"] == plan.SelectionSha256, "HandoffIntegrityFailure");
                state = "completed";
            }
            else if (File.Exists(PathFor(requestId, "receipt.json")))
            {
                Require(marker.Action == "complete", "HandoffIntegrityFailure"); state = "receiptUncommitted";
            }
        }
        else Require(!File.Exists(PathFor(requestId, "receipt.json")) && !File.Exists(PathFor(requestId, "completed.json")), "HandoffIntegrityFailure");
        return new() { ["schemaVersion"] = "addin-handoff-status.v1", ["requestId"] = requestId,
            ["state"] = state, ["receiverInstanceId"] = plan.ReceiverInstanceId, ["hostProcessId"] = plan.HostProcessId,
            ["reviewId"] = plan.ReviewId, ["selectionSha256"] = plan.SelectionSha256,
            ["binding"] = JsonSerializer.SerializeToNode(plan.Binding, Json), ["selection"] = selection,
            ["automaticReplay"] = false, ["producerAuthenticated"] = false, ["liveSelectionVerified"] = false, ["nativeQualified"] = false };
    }

    private (Plan Plan, byte[] Bytes, string Hash) ReadPlan(string id)
    {
        byte[] raw = Read(PathFor(id, "plan.json"), MetadataLimit); var plan = Decode<Plan>(raw);
        Require(plan.Version == 1 && plan.RequestId == id && plan.HostProcessId > 0 && plan.IssuedUtc.Offset == TimeSpan.Zero, "HandoffIntegrityFailure");
        Id(plan.ReceiverInstanceId); Digest(plan.ReviewId); Digest(plan.SelectionSha256); ValidatePeer(plan.Peer);
        byte[] bytes = Read(PathFor(id, "selection.input"), AddInSelection.InputLimit);
        Require(Hash(bytes) == plan.SelectionSha256, "HandoffIntegrityFailure");
        ValidateBinding(plan.Binding, plan.Peer, ValidateSelection(plan.ReviewId, bytes));
        return (plan, bytes, Hash(raw));
    }

    private JsonObject ValidateSelection(string reviewId, byte[] bytes)
    {
        var request = AddInSelection.Parse(bytes);
        _ = AddInSelection.Build(reviewId, new ProjectReviewStore(artifacts).Read(reviewId), request); return request;
    }
    private async Task<AddInHandoffBinding> Verify(AddInHandoffPeer peer, string review, string digest, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await verify(peer, review, digest, deadline.Token).WaitAsync(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested(); return result;
    }
    private static void ValidateBinding(AddInHandoffBinding binding, AddInHandoffPeer peer, JsonObject request)
    {
        Require(binding is not null && binding.WorkerProcessId > 0 && binding.TiaProcess == peer, "HandoffOwnerMismatch");
        Id(binding!.WorkerInstanceId); Id(binding.SessionGeneration); Digest(binding.AssemblySha256); Digest(binding.ConfigurationSha256); Digest(binding.AuthorityEvidenceSha256);
        Require(binding.ProjectHost == (string?)request["projectHost"] && binding.AssemblySha256 == (string?)request["producer"]?["assemblySha256"] &&
            binding.ConfigurationSha256 == (string?)request["producer"]?["configurationSha256"], "HandoffOwnerMismatch");
        foreach (var reference in request["references"]!.AsArray())
            Require(binding.ProjectPath == (string?)reference?["projectPath"] && binding.SnapshotId == (string?)reference?["snapshotId"] && binding.SessionGeneration == (string?)reference?["sessionGeneration"], "HandoffOwnerMismatch");
    }
    private static void ValidatePeer(AddInHandoffPeer peer)
    {
        Require(peer is not null && peer.ProcessId > 0 && peer.WindowsSessionId >= 0 && peer.ExecutablePath is { Length: > 0 and <= 32768 } &&
            !peer.ExecutablePath.Any(char.IsControl) && Path.IsPathFullyQualified(peer.ExecutablePath) &&
            DateTime.TryParseExact(peer.StartedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) && started.Kind == DateTimeKind.Utc, "HandoffPeerInvalid");
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(peer!.ExecutablePath);
    }
    private void Expire()
    {
        foreach (var pair in pending.Where(p => clock.GetElapsedTime(p.Value.IssuedTimestamp) >= ChallengeLifetime).ToArray()) pending.Remove(pair.Key);
    }
    private void Capacity(long additional)
    {
        long bytes = additional; int entries = 0;
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory)) foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            PackageStorage.EnsureNoLinks(path); Require(++entries <= 1024, "HandoffCapacity");
            if (Directory.Exists(path)) directories.Push(path); else bytes += new FileInfo(path).Length;
            Require(bytes <= MaximumStoreBytes, "HandoffCapacity");
        }
    }
    private string PathFor(string id, string? name = null) => PackageStorage.Under(root, id + (name is null ? "" : "/" + name));
    private void WriteNew(string path, byte[] bytes)
    {
        Capacity(bytes.Length); PackageStorage.EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true);
    }
    private static byte[] Read(string path, int limit)
    {
        PackageStorage.EnsureNoLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length <= limit, "HandoffLimit"); byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static T Decode<T>(byte[] bytes) where T : class => AddInSelection.Parse(bytes).Deserialize<T>(Json)
        ?? throw new BridgeException("HandoffIntegrityFailure", "Retained handoff metadata is missing.");
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Id(string value) { Require(value is { Length: 32 } && value.All(char.IsAsciiHexDigitLower), "HandoffIdentityInvalid"); }
    private static void Digest(string value) { Require(value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower), "HandoffIdentityInvalid"); }
    private static void Require(bool value, string code) { if (!value) throw new BridgeException(code, "Add-In handoff refused. Inspect the retained request; no handoff or native operation is replayed."); }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; pending.Clear(); owner.Dispose(); }
        finally { gate.Release(); }
    }
}
