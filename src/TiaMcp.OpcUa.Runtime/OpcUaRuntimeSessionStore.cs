using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Opc.Ua;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;
using static TiaMcp.OpcUa.Runtime.OpcUaRuntimeClient;

namespace TiaMcp.OpcUa.Runtime;

public delegate void OpcUaCredentialConsumer(OpcUaRuntimeSelection selection, Action<byte[]> consume);

/// <summary>Durable local ownership; records contain neither authentication tokens nor private keys.</summary>
public sealed class OpcUaRuntimeSessionStore : IAsyncDisposable
{
    private sealed record Live(OpcUaRuntimeClient Client, OpcUaRuntimeStatus Status);
    private readonly string root, hostId = Guid.NewGuid().ToString("N");
    private readonly string clientUri;
    private readonly X509Certificate2 certificate;
    private readonly OpcUaCredentialConsumer credentialConsumer;
    private readonly FileStream owner;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> acquisition = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();
    private readonly Task idle;
    private bool disposed;
    private bool idleFailed;
    private static readonly JsonSerializerOptions Json = new(BridgeProtocol.Json) { MaxDepth = 24 };

    public OpcUaRuntimeSessionStore(string directory, OpcUaCredentialConsumer? credentialConsumer = null)
    {
        root = Path.GetFullPath(directory); Plain(root); Directory.CreateDirectory(root); Plain(root);
        var ownerPath = Path.Combine(root, "owner.lock"); Plain(ownerPath);
        owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        this.credentialConsumer = credentialConsumer ?? ((_, _) => throw Error("UaProtectedCredentialUnavailable"));
        clientUri = "urn:tiamcp:owned-client:" + hostId;
        try
        {
            certificate = CertificateFactory.CreateCertificate(clientUri, "TIA MCP read-only UA", "CN=TIA MCP read-only UA",
                [System.Net.Dns.GetHostName()]).SetLifeTime(1).SetRSAKeySize(2048).CreateForRSA();
            idle = Idle();
        }
        catch { owner.Dispose(); throw; }
    }

    public object ClientIdentity() => new { schemaVersion = "opcua-runtime-client-identity.v1", hostId,
        applicationUri = clientUri, certificateSha256 = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)),
        certificateDerBase64 = Convert.ToBase64String(certificate.RawData), privateKeyStorage = "ephemeralHostMemory",
        expiresAt = certificate.NotAfter.ToUniversalTime(), physicalQualified = false,
        runtimeBinaries = new[] { typeof(OpcUaRuntimeSessionStore).Assembly, typeof(OpcUaRuntimeSelection).Assembly,
            typeof(Opc.Ua.Client.Session).Assembly, typeof(CertificateFactory).Assembly, typeof(Variant).Assembly }
            .Distinct().Select(a => new { name = a.GetName().Name, version = a.GetName().Version!.ToString(),
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location))) }) };

    public static CredentialBinding Binding(OpcUaRuntimeSelection selection)
    {
        Validate(selection);
        if (selection.Authentication != "username") throw Error("UaCredentialBindingInvalid");
        return new(new("opcUaRuntime", selection.Endpoint, selection.CertificateSha256, "applicationUri", selection.ApplicationUri),
            "opcUaRuntimeLogin", selection.User!);
    }
    public static void Validate(OpcUaRuntimeSelection selection)
    {
        if (selection is null || !Uri.TryCreate(selection.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != "opc.tcp" || endpoint.Port is < 1 or > 65535 || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || selection.Endpoint.Length > 2048 ||
            selection.Endpoint != endpoint.AbsoluteUri || !Uri.TryCreate(selection.ApplicationUri, UriKind.Absolute, out _) ||
            selection.ApplicationUri.Length > 4096 || selection.CertificateSha256 is not { Length: 64 } ||
            selection.CertificateSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Error("UaEndpointSelectionInvalid");
        if (selection.Authentication == "anonymous")
        {
            if (selection.User is not null || selection.VaultName is not null || selection.Credential is not null)
                throw Error("UaAuthenticationSelectionInvalid");
        }
        else if (selection.Authentication == "username")
        {
            if (string.IsNullOrWhiteSpace(selection.User) || selection.User.Length > 256 || selection.User.Any(char.IsControl) ||
                selection.VaultName is null || (selection.VaultName != "default" && !ProtectedCredentialGuard.IsId(selection.VaultName)))
                throw Error("UaAuthenticationSelectionInvalid");
        }
        else throw Error("UaAuthenticationSelectionInvalid");
    }

    public async Task<OpcUaRuntimeStatus> Connect(string attemptId, OpcUaRuntimeSelection selection, CancellationToken ct = default)
    {
        Id(attemptId); Validate(selection);
        if (selection.Authentication == "username")
        {
            ProtectedCredentialGuard.ValidateReference(selection.Credential!);
            if (selection.Credential!.BindingSha256 != ProtectedCredentialGuard.BindingHash(Binding(selection)))
                throw Error("UaCredentialBindingMismatch");
        }
        using var deadline = Deadline(ct);
        await gate.WaitAsync(deadline.Token);
        try
        {
            Open();
            if (File.Exists(PathFor(attemptId)))
            {
                var old = Load(attemptId);
                if (old.Selection != selection) throw Error("UaAttemptContextMismatch");
                return Observe(old);
            }
            var records = Directory.EnumerateFiles(root, "*.json").Take(1025).ToArray();
            if (live.Count >= 4 || records.Length >= 1024) throw Error("UaSessionCapacity");
            foreach (var path in records)
            {
                var old = Load(Path.GetFileNameWithoutExtension(path));
                if (old.Selection.Endpoint == selection.Endpoint && !Terminal(old.State)) throw Error("UaEndpointSessionUnresolved");
            }
            if (selection.Authentication == "username") credentialConsumer(selection, _ => { });
            var status = new OpcUaRuntimeStatus("opcua-runtime-session.v1", attemptId, hostId, selection,
                "connecting", null, false, DateTimeOffset.UtcNow);
            Save(status, true);
            var pkiPath = Path.Combine(root, "pki");
            foreach (var name in new[] { "trusted", "issuers", "rejected" }) Plain(Path.Combine(pkiPath, name));
            var client = new OpcUaRuntimeClient(selection, certificate, clientUri, pkiPath);
            live.Add(attemptId, new(client, status));
            try
            {
                if (selection.Authentication == "anonymous") await client.Connect(null, deadline.Token);
                else
                {
                    string? failure = null;
                    credentialConsumer(selection, bytes => {
                        try { client.Connect(bytes, deadline.Token).GetAwaiter().GetResult(); }
                        catch (Exception e) { failure = Code(e); }
                    });
                    if (failure is not null) throw Error(failure);
                }
                status = status with { State = "connected", PeerVerified = client.PeerVerified, UpdatedAt = DateTimeOffset.UtcNow };
            }
            catch (Exception e)
            {
                status = status with { State = client.SessionDispatched ? "remoteSessionUnknown" : "notConnected",
                    PeerVerified = client.PeerVerified, ErrorCode = Code(e), UpdatedAt = DateTimeOffset.UtcNow };
                if (!client.SessionDispatched) { await client.DisposeAsync(); live.Remove(attemptId); }
                else client.Invalidate();
            }
            Save(status);
            if (live.ContainsKey(attemptId)) live[attemptId] = new(client, status);
            return status;
        }
        finally { gate.Release(); }
    }

    public Task<OpcUaRuntimeReadResult> Read(string attemptId, IReadOnlyList<OpcUaRuntimeNode> nodes, CancellationToken ct = default)
    {
        if (nodes is null || nodes.Count is < 1 or > 64) throw Error("UaRequestLimit");
        foreach (var node in nodes) ValidateNode(node);
        return Use(attemptId, (client, token) => client.Read(attemptId, nodes, token), ct);
    }
    public Task<OpcUaRuntimeBrowseResult> Browse(string attemptId, OpcUaRuntimeNode node, int maximum = 256, CancellationToken ct = default)
    {
        ValidateNode(node);
        if (maximum is < 1 or > 256) throw Error("UaRequestLimit");
        return Use(attemptId, (client, token) => client.Browse(attemptId, node, maximum, token), ct);
    }
    private async Task<T> Use<T>(string attemptId, Func<OpcUaRuntimeClient, CancellationToken, Task<T>> action, CancellationToken ct, string? acquisitionId = null)
    {
        Id(attemptId);
        using var deadline = Deadline(ct);
        await gate.WaitAsync(deadline.Token);
        try
        {
            Open();
            if (!live.TryGetValue(attemptId, out var entry) || entry.Status.State != "connected") throw Error("UaSessionUnavailable");
            if (acquisition.TryGetValue(attemptId,out var reserved) && reserved!=acquisitionId) throw Error("UaSessionAcquisitionBusy");
            if (acquisitionId is not null && reserved!=acquisitionId) throw Error("UaSessionAcquisitionUnavailable");
            if (Load(attemptId) != entry.Status) throw Error("UaSessionRecordChanged");
            try
            {
                if (entry.Status.Selection.Authentication == "username")
                {
                    T? value = default;
                    BridgeException? failure = null;
                    credentialConsumer(entry.Status.Selection, _ => {
                        try { value = action(entry.Client, deadline.Token).GetAwaiter().GetResult(); }
                        catch (Exception e) { failure = e is UaContinuationCleanupFailure cleanup
                            ? new UaContinuationCleanupFailure(cleanup.OperationErrorCode) : Error(Code(e)); }
                    });
                    if (failure is not null) throw failure;
                    return value!;
                }
                return await action(entry.Client, deadline.Token);
            }
            catch (Exception e)
            {
                entry.Client.Invalidate();
                var status = entry.Status with { State = "remoteSessionUnknown",
                    ErrorCode = e is UaContinuationCleanupFailure ? Code(e) :
                        ct.IsCancellationRequested ? "UaCancelled" : deadline.IsCancellationRequested ? "UaTimeout" : Code(e),
                    OperationErrorCode = e is UaContinuationCleanupFailure cleanup ? cleanup.OperationErrorCode : null, UpdatedAt = DateTimeOffset.UtcNow };
                Save(status); live[attemptId] = new(entry.Client, status);
                throw Error(status.ErrorCode!);
            }
        }
        finally { gate.Release(); }
    }
    internal async Task<OpcUaRuntimeStatus> Acquire(string attemptId,string captureId,CancellationToken ct)
    {
        Id(attemptId); Id(captureId); using var deadline=Deadline(ct);await gate.WaitAsync(deadline.Token);
        try {
            Open(); if(acquisition.ContainsKey(attemptId))throw Error("UaSessionAcquisitionBusy");
            if(!live.TryGetValue(attemptId,out var entry) || entry.Status.State!="connected" || Load(attemptId)!=entry.Status)throw Error("UaSessionUnavailable");
            acquisition.Add(attemptId,captureId);return entry.Status;
        } finally {gate.Release();}
    }
    internal async Task Release(string attemptId,string captureId)
    { await gate.WaitAsync();try {if(acquisition.GetValueOrDefault(attemptId)==captureId)acquisition.Remove(attemptId);}finally{gate.Release();} }
    internal Task<OpcUaRuntimeReadResult> ReadAcquisition(string attemptId,string captureId,OpcUaRuntimeNode[] nodes,CancellationToken ct) =>
        Use(attemptId,(client,token)=>client.Read(attemptId,nodes,token),ct,captureId);
    internal Task<OpcUaSubscriptionCreated> CreateSubscription(string captureId,OpcUaSubscriptionRequest request,CancellationToken ct) =>
        Use(request.AttemptId,(client,token)=>client.CreateSubscription(request.AttemptId,request,token),ct,captureId);
    internal Task<OpcUaSubscriptionEvent> Publish(string attemptId,string captureId,uint[] acknowledgements,CancellationToken ct) =>
        Use(attemptId,(client,token)=>client.Publish(acknowledgements,token),ct,captureId);
    internal Task<OpcUaSubscriptionEvent> Republish(string attemptId,string captureId,uint sequence,CancellationToken ct) =>
        Use(attemptId,(client,token)=>client.Republish(sequence,token),ct,captureId);
    internal async Task<OpcUaSubscriptionEvent?> DeleteSubscription(string attemptId,string captureId)
    {
        using var deadline=new CancellationTokenSource(TimeoutMs);await gate.WaitAsync(deadline.Token);
        try {
            if(acquisition.GetValueOrDefault(attemptId)!=captureId || !live.TryGetValue(attemptId,out var entry))return null;
            var result=await entry.Client.DeleteSubscription(deadline.Token);
            if(result is {Kind:"failure"} || result is {Kind:"deleted",StatusCode:not "0x00000000"})
            {
                entry.Client.Invalidate();
                var status=entry.Status with {State="remoteSessionUnknown",ErrorCode="UaSubscriptionCleanupUnknown",UpdatedAt=DateTimeOffset.UtcNow};
                Save(status);live[attemptId]=new(entry.Client,status);
            }
            return result;
        } finally {gate.Release();}
    }
    public async Task<OpcUaRuntimeStatus> Status(string attemptId, CancellationToken ct = default)
    {
        Id(attemptId); using var deadline = Deadline(ct); await gate.WaitAsync(deadline.Token);
        try { Open(); return Observe(Load(attemptId)); } finally { gate.Release(); }
    }
    public async Task<OpcUaRuntimeStatus> Disconnect(string attemptId, bool acknowledgeUnknown = false, CancellationToken ct = default)
    {
        Id(attemptId); using var deadline = Deadline(ct); await gate.WaitAsync(deadline.Token);
        try { Open(); if(acquisition.ContainsKey(attemptId))throw Error("UaSessionAcquisitionBusy"); return await Close(attemptId, acknowledgeUnknown); } finally { gate.Release(); }
    }
    private async Task<OpcUaRuntimeStatus> Close(string attemptId, bool acknowledgeUnknown)
    {
        var status = Load(attemptId);
        if (Terminal(status.State) && !live.ContainsKey(attemptId)) return status;
        bool closed = false;
        if (live.Remove(attemptId, out var entry)) { status = entry.Status; closed = await entry.Client.Close(); }
        status = status with { State = closed ? "closed" : acknowledgeUnknown ? "abandonedRemoteUnknown" : "remoteSessionUnknown",
            ErrorCode = closed ? null : "UaRemoteCloseUnconfirmed", UpdatedAt = DateTimeOffset.UtcNow };
        Save(status); return status;
    }
    private OpcUaRuntimeStatus Observe(OpcUaRuntimeStatus status) =>
        Terminal(status.State) || live.ContainsKey(status.AttemptId) ? status :
            status with { State = status.HostId == hostId ? "remoteSessionUnknown" : "hostEndedRemoteUnknown" };
    private static bool Terminal(string state) => state is "closed" or "notConnected" or "abandonedRemoteUnknown";
    private async Task Idle()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    foreach (var id in live.Where(x => !acquisition.ContainsKey(x.Key) && DateTimeOffset.UtcNow - x.Value.Client.LastUsed > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray())
                        await Close(id, false);
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { idleFailed = true; }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        stop.Cancel(); await idle;
        await gate.WaitAsync();
        bool failed = idleFailed;
        try
        {
            if (disposed) return;
            foreach (var id in live.Keys.ToArray())
            {
                var captured = live[id];
                try { await Close(id, false); }
                catch { failed = true; }
                finally
                {
                    live.Remove(id);
                    try { await captured.Client.Close(); } catch { failed = true; }
                }
            }
        }
        finally
        {
            disposed = true;
            try { certificate.Dispose(); } finally { owner.Dispose(); gate.Release(); }
        }
        if (failed) throw Error("UaCleanupPublicationFailed");
    }
    private void Open() { if (disposed) throw Error("UaOwnerClosed"); if (idleFailed) throw Error("UaIdleCleanupFailed"); }
    private static CancellationTokenSource Deadline(CancellationToken ct) { var d = CancellationTokenSource.CreateLinkedTokenSource(ct); d.CancelAfter(TimeoutMs); return d; }
    private static void Id(string id) { if (!ProtectedCredentialGuard.IsId(id)) throw Error("UaAttemptInvalid"); }
    private string PathFor(string id) => Path.Combine(root, id + ".json");
    private OpcUaRuntimeStatus Load(string id)
    {
        Id(id); var path = PathFor(id); Plain(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 65_536) throw Error("UaSessionRecordInvalid");
        var record = JsonSerializer.Deserialize<OpcUaRuntimeStatus>(File.ReadAllBytes(path), Json);
        if (record is null || record.SchemaVersion != "opcua-runtime-session.v1" || record.AttemptId != id ||
            record.State is not ("connecting" or "connected" or "remoteSessionUnknown" or "closed" or "notConnected" or "abandonedRemoteUnknown"))
            throw Error("UaSessionRecordInvalid");
        Validate(record.Selection); return record;
    }
    private void Save(OpcUaRuntimeStatus record, bool create = false)
    {
        var path = PathFor(record.AttemptId); Plain(root); Plain(path);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (data.Length > 65_536) throw Error("UaSessionRecordInvalid");
        string temp = Path.Combine(root, record.AttemptId + ".tmp"); Plain(temp);
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { output.Write(data); output.Flush(true); }
        File.Move(temp, path, !create);
    }
    private static void Plain(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error("UaUnsafeArtifactPath");
    }
    private static string Code(Exception error) => error switch {
        BridgeException e => e.Code.StartsWith("Ua", StringComparison.Ordinal) ? e.Code : "UaCredentialUnavailable",
        OperationCanceledException => "UaCancelled",
        ServiceResultException e when e.StatusCode == StatusCodes.BadRequestCancelledByClient => "UaCancelled",
        ServiceResultException e when e.StatusCode is StatusCodes.BadTimeout or StatusCodes.BadRequestTimeout => "UaTimeout",
        ServiceResultException e when e.StatusCode is StatusCodes.BadUserAccessDenied or StatusCodes.BadIdentityTokenRejected or StatusCodes.BadIdentityTokenInvalid => "UaAuthenticationDenied",
        ServiceResultException e when e.StatusCode is StatusCodes.BadSessionIdInvalid or StatusCodes.BadSessionClosed => "UaSessionExpired",
        ServiceResultException e when (e.StatusCode & 0xffff0000) is >= StatusCodes.BadCertificateInvalid and <= StatusCodes.BadCertificateIssuerRevoked => "UaCertificateRejected",
        _ => "UaProtocolFailure"
    };
}
