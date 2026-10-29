using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Core.Security;

namespace TiaMcp.Host;

public sealed record RuntimeLoginSelection(string Endpoint, string PeerCertificateSha256, string User, string Mode,
    string VaultName, CredentialReference Credential);
public sealed record RuntimeSessionStatus(string AttemptId, string HostId, WebRuntimeContext Context, RuntimeLoginSelection Login,
    string State, string? ErrorCode, bool PeerVerified, string[] Methods, string ReadAvailability)
{
    [JsonRequired]
    public string SchemaVersion { get; init; } = "runtime-web-session.v2";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebRuntimeProfile? Profile { get; init; }
    public string Trust => "explicitLeafPinAndHostnameAndValidity";
    public bool DeviceIdentityVerified => false;
    public bool ProgramMatchVerified => false;
    public bool NativeQualified => false;
    public bool SecretReturned => false;
}

/// <summary>Owns only sessions created here. Never adopts inventory tokens or replays retained attempts.</summary>
public sealed class PhysicalRuntimeSessions : IAsyncDisposable
{
    private sealed class Live(WebRuntimeClient client, RuntimeSessionStatus status)
    {
        internal WebRuntimeClient Client = client;
        internal RuntimeSessionStatus Status = status;
        internal Dictionary<string, WebRuntimeSymbol> Selections = new(StringComparer.Ordinal);
        internal Dictionary<string, JsonElement> Metadata = new(StringComparer.Ordinal);
    }
    private readonly string root;
    private readonly string hostId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task idleWorker;
    private readonly FileStream owner;
    private static readonly JsonSerializerOptions Json = new(CredentialVault.Json) { MaxDepth = 16 };

    public PhysicalRuntimeSessions(HostOptions options)
    {
        root = Path.Combine(options.ArtifactsDirectory, "runtime-web");
        Plain(root); Directory.CreateDirectory(root); Plain(root);
        string ownerPath=Path.Combine(root,"owner.lock"); Plain(ownerPath);
        owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        idleWorker = ExpireIdle();
    }

    public static CredentialBinding Binding(string endpoint, string certificate, string user, string mode)
    {
        var context = WebRuntimeProtocol.Context(endpoint, new('0',32), new('0',32), certificate);
        if (context.Endpoint != endpoint || mode is not ("local" or "static" or "disabled" or "umc")) throw Error("RuntimeLoginSelectionInvalid");
        var binding = new CredentialBinding(new("siemensWebApiRuntime", endpoint, certificate, "authenticationMode", mode), "runtimeLogin", user);
        ProtectedCredentialGuard.ValidateBinding(binding); return binding;
    }

    public async Task<RuntimeSessionStatus> Connect(string attemptId, RuntimeLoginSelection login, CancellationToken cancellationToken)
    {
        Id(attemptId); var binding = Binding(login.Endpoint, login.PeerCertificateSha256, login.User, login.Mode);
        ProtectedCredentialGuard.ValidateReference(login.Credential);
        if (login.Credential.BindingSha256 != ProtectedCredentialGuard.BindingHash(binding)) throw Error("RuntimeCredentialContextMismatch");
        if (login.VaultName != "default") Id(login.VaultName);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(PathFor(attemptId)))
            {
                var existing = Read(attemptId);
                if (existing.Login != login) throw Error("RuntimeAttemptContextMismatch");
                return Observe(existing);
            }
            if (live.Count >= 4) throw Error("RuntimeSessionCapacity");
            var records = Directory.EnumerateFiles(root, "*.json").Select(path => Read(Path.GetFileNameWithoutExtension(path))).ToArray();
            // Bounded retention: the oldest resolved records make room for a new attempt; unresolved records are never removed.
            var retired = records.Where(r => Resolved(r.State)).OrderBy(r => File.GetLastWriteTimeUtc(PathFor(r.AttemptId))).Take(records.Length - 1023).ToArray();
            if (records.Length - retired.Length >= 1024) throw Error("RuntimeSessionCapacity");
            foreach (var old in retired) File.Delete(PathFor(old.AttemptId));
            // A new ID cannot bypass an unresolved login for the same endpoint in this artifact root.
            if (records.Any(r => r.Context.Endpoint == login.Endpoint && !Resolved(r.State))) throw Error("RuntimeEndpointSessionUnresolved");
            var vault = Vault(login); vault.Use(login.Credential, binding, ValidatePassword);
            var context = WebRuntimeProtocol.Context(login.Endpoint, attemptId, Guid.NewGuid().ToString("N"), login.PeerCertificateSha256);
            var status = new RuntimeSessionStatus(attemptId, hostId, context, login, "connecting", null, false, [], "permissionUnknown");
            Save(status, create: true);
            var client = new WebRuntimeClient(context); var entry = new Live(client,status); live.Add(attemptId,entry);
            try
            {
                await client.ProbeAsync(cancellationToken);
                client.ValidateLoginMode(login.User, login.Mode);
                // Keep the vault lock through the trusted consumer: retirement cannot race secret use.
                Consume(vault,login.Credential, binding, bytes =>
                {
                    ValidatePassword(bytes);
                    client.LoginAsync(attemptId, login.User, login.Mode, bytes, cancellationToken).GetAwaiter().GetResult();
                });
                await client.VerifySessionAsync(login.User, login.Mode, cancellationToken);
                var permissions = await client.PermissionsAsync(cancellationToken);
                status = status with { State = "connected", PeerVerified = client.PeerVerified, Methods = client.Methods, Profile = client.Profile,
                    ReadAvailability = client.Profile.HasNoPermissions == true ? "permissionDenied" : WebRuntimeProtocol.ReadAvailability(client.Methods, permissions) };
                Save(status); entry.Status = status; return status;
            }
            catch (Exception error)
            {
                bool uncertain = client.LoginDispatched && !client.LoginRejected;
                status = status with { State = uncertain ? "remoteSessionUnknown" : "notConnected", ErrorCode = Code(error), PeerVerified = client.PeerVerified,
                    Methods = client.Methods, Profile = client.Profile };
                entry.Status = status;
                if (!uncertain) { client.Dispose(); live.Remove(attemptId); }
                Save(status); return status;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<object> Browse(string attemptId, string? symbol, bool children, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Live entry = Active(attemptId);
            JsonElement metadata = default;
            Use(entry, () => metadata = entry.Client.BrowseAsync(symbol, children, cancellationToken).GetAwaiter().GetResult(), cancellationToken);
            string? selectionId = null; WebRuntimeSymbol? selection = null;
            if (!children)
            {
                if (entry.Selections.Count >= 256) throw Error("RuntimeSelectionCapacity");
                selection = WebRuntimeProtocol.Symbol(entry.Client.Context, symbol!, "simple", metadata[0]);
                selectionId = Guid.NewGuid().ToString("N"); entry.Selections.Add(selectionId,selection);
                entry.Metadata.Add(selectionId,metadata[0].Clone());
            }
            return new { attemptId, context = entry.Client.Context, metadata, selectionId, selection,
                scope = children ? "immediateChildrenOnly" : "exactSuppliedSymbol", nativeQualified = false };
        }
        finally { gate.Release(); }
    }

    public async Task<WebRuntimeReadResult> ReadValue(string attemptId, string selectionId, string representation, CancellationToken cancellationToken, RuntimeCaptureHandoff? handoff = null)
    {
        Id(selectionId); if (representation is not ("simple" or "raw")) throw Error("RuntimeRepresentationUnsupported");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Live entry = Active(attemptId);
            if (!entry.Selections.TryGetValue(selectionId, out var selection)) throw Error("RuntimeSelectionUnavailable");
            if (handoff is not null) RuntimeCaptureHandoffs.ValidateRead(Path.GetDirectoryName(root)!, handoff, CaptureSession(entry.Status));
            WebRuntimeReadResult? value = null;
            Use(entry, () =>
            {
                string[] permissions = entry.Client.PermissionsAsync(cancellationToken).GetAwaiter().GetResult();
                if (entry.Client.Profile.HasNoPermissions == true || WebRuntimeProtocol.ReadAvailability(entry.Client.Methods, permissions) != "permittedAtObservation") throw Error("RuntimePermissionDenied");
                value = Current(entry, selection, representation, cancellationToken);
            }, cancellationToken);
            return value!;
        }
        finally { gate.Release(); }
    }

    /// <summary>Owned-PLCSIM writes after the caller verified the binding: every exact precondition holds before the first write; each write is read back.</summary>
    public async Task<object> Write(string attemptId, WebRuntimeWriteItem[] writes, CancellationToken cancellationToken, Action? afterSend = null)
    {
        if (writes is not { Length: >= 1 and <= 16 } || writes.Any(w => w is null) || writes.Select(w => w.SelectionId).Distinct(StringComparer.Ordinal).Count() != writes.Length)
            throw Error("RuntimeWriteLimit");
        foreach (var write in writes) Id(write.SelectionId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Live entry = Active(attemptId);
            var selections = writes.Select(w => entry.Selections.TryGetValue(w.SelectionId, out var s) ? s : throw Error("RuntimeSelectionUnavailable")).ToArray();
            object? result = null;
            Use(entry, () =>
            {
                Permitted(entry, "write_value", cancellationToken);
                var before = selections.Select(s => Current(entry, s, "simple", cancellationToken)).ToArray();
                if (before.Where((read, i) => !Holds(read, writes[i].Expected)).Any())
                { result = new { attemptId, state = "preconditionMismatch", dispatched = 0, before }; return; }
                var items = new List<object>(); bool complete = true;
                for (int i = 0; i < writes.Length && complete; i++)
                {
                    string dispatch = "unknown"; int? code = null; string? errorCode = null; WebRuntimeReadResult? after = null;
                    try
                    {
                        (dispatch, code) = entry.Client.MutateAsync(WebRuntimeProtocol.Write(Guid.NewGuid().ToString("N"), before[i].Selection, writes[i].Value), cancellationToken).GetAwaiter().GetResult();
                        afterSend?.Invoke(); // Qualification fault point only: the Host exits before the outcome is read back.
                        if (dispatch == "accepted") after = Current(entry, selections[i], "simple", cancellationToken);
                    }
                    // A lost response leaves the outcome unknown and a failed readback unobserved; both are reported, never retried.
                    catch (BridgeException error) { errorCode = error.Code; }
                    bool matches = Holds(after, writes[i].Value);
                    items.Add(new { selectionId = writes[i].SelectionId, symbol = selections[i].Symbol, dispatch, providerErrorCode = code, errorCode, readback = after, readbackMatches = matches });
                    complete = dispatch == "accepted" && matches;
                }
                result = new { attemptId, state = complete ? "written" : "incomplete", dispatched = items.Count, before, items,
                    note = "Readback is one later observation; the PLCSIM program scan may overwrite assigned values. Assignment is not forcing." };
            }, cancellationToken);
            return result!;
        }
        finally { gate.Release(); }
    }

    /// <summary>Owned-PLCSIM RUN/STOP request after the caller verified the binding; the exact current mode must hold first and the result is observed.</summary>
    public async Task<object> ChangeOperatingMode(string attemptId, string expectedMode, string mode, CancellationToken cancellationToken)
    {
        _ = WebRuntimeProtocol.ChangeOperatingMode(new('0',32), mode); // Refuse a bad selection before session or credential use.
        if (expectedMode is not ("run" or "stop") || expectedMode == mode) throw Error("RuntimeOperatingModeInvalid");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Live entry = Active(attemptId);
            object? result = null;
            Use(entry, () =>
            {
                Permitted(entry, "change_operating_mode", cancellationToken);
                WebRuntimeDiagnosticResult Observe() => entry.Client.DiagnosticAsync("operatingMode", null, null, cancellationToken).GetAwaiter().GetResult();
                var before = Observe();
                if (!IsMode(before, expectedMode)) { result = new { attemptId, state = "preconditionMismatch", dispatched = false, before }; return; }
                result = RequestMode(attemptId, expectedMode, mode, before,
                    () => entry.Client.MutateAsync(WebRuntimeProtocol.ChangeOperatingMode(Guid.NewGuid().ToString("N"), mode), cancellationToken).GetAwaiter().GetResult(),
                    Observe, cancellationToken);
            }, cancellationToken);
            return result!;
        }
        finally { gate.Release(); }
    }

    /// <summary>Sends one RUN/STOP request and observes the mode for a bounded time. Once the request may have gone out, a lost
    /// response or a failed or cancelled readback is reported as dispatched with an unknown outcome; nothing is retried.</summary>
    internal static object RequestMode(string attemptId, string expectedMode, string mode, WebRuntimeDiagnosticResult before,
        Func<(string Status, int? ProviderErrorCode)> mutate, Func<WebRuntimeDiagnosticResult> observe, CancellationToken cancellationToken)
    {
        string dispatch = "unknown"; int? code = null; WebRuntimeDiagnosticResult? after = null;
        try
        {
            (dispatch, code) = mutate();
            // Boolean success only acknowledges the request; observe Plc.ReadOperatingMode for a bounded time.
            for (int i = 0; dispatch == "accepted" && i < 20 && !IsMode(after, mode); i++)
            {
                if (i > 0) Task.Delay(250, cancellationToken).GetAwaiter().GetResult();
                after = observe();
            }
        }
        catch (Exception error) when (error is BridgeException or OperationCanceledException)
        { return new { attemptId, state = "unknown", dispatched = true, before, dispatch, errorCode = error is BridgeException e ? e.Code : "RuntimeCancelled" }; }
        return new { attemptId, state = IsMode(after, mode) ? "changed" : "incomplete", dispatched = true, expectedMode, requestedMode = mode,
            dispatch, providerErrorCode = code, before, readback = after, readbackMatches = IsMode(after, mode) };
    }
    private static bool IsMode(WebRuntimeDiagnosticResult? read, string wanted) =>
        read is { Status: "read", Value: { ValueKind: JsonValueKind.String } v } && v.GetString() == wanted;

    private static bool Holds(WebRuntimeReadResult? read, JsonElement wanted) =>
        read is { Status: "read", Value: { } value } && JsonElement.DeepEquals(value, wanted);
    private static void Permitted(Live entry, string permission, CancellationToken cancellationToken)
    {
        string[] permissions = entry.Client.PermissionsAsync(cancellationToken).GetAwaiter().GetResult();
        if (entry.Client.Profile.HasNoPermissions == true || !permissions.Contains(permission, StringComparer.Ordinal)) throw Error("RuntimePermissionDenied");
    }
    private static WebRuntimeReadResult Current(Live entry, WebRuntimeSymbol selection, string representation, CancellationToken cancellationToken)
    {
        var current = entry.Client.BrowseAsync(selection.Symbol, false, cancellationToken).GetAwaiter().GetResult();
        var refreshed = WebRuntimeProtocol.Symbol(entry.Client.Context, selection.Symbol, representation, current[0]);
        if (refreshed.MetadataSha256 != selection.MetadataSha256) throw Error("RuntimeSymbolMetadataChanged");
        return entry.Client.ReadAsync(refreshed, cancellationToken).GetAwaiter().GetResult();
    }

    public async Task<WebRuntimeDiagnosticResult> Diagnostics(string attemptId, string kind, string? language, int? count, CancellationToken cancellationToken)
    {
        _ = WebRuntimeProtocol.Diagnostic(new('0',32), kind, language, count); // Refuse a bad selection before session or credential use.
        await gate.WaitAsync(cancellationToken);
        try
        {
            Live entry = Active(attemptId);
            WebRuntimeDiagnosticResult? result = null;
            Use(entry, () => result = entry.Client.DiagnosticAsync(kind, language, count, cancellationToken).GetAwaiter().GetResult(), cancellationToken);
            return result!;
        }
        finally { gate.Release(); }
    }

    public async Task<RuntimeSessionStatus> Status(string attemptId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return Observe(Read(attemptId)); } finally { gate.Release(); }
    }
    public async Task<(RuntimeCaptureSession Session, RuntimeCaptureSignal[] Signals)> CaptureContext(string attemptId, string[] selectionIds, string representation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = Active(attemptId);
            var signals = selectionIds.Select(id => entry.Selections.TryGetValue(id,out var selection)
                ? new RuntimeCaptureSignal(id,selection with { Representation = representation },entry.Metadata[id]) : throw Error("RuntimeSelectionUnavailable")).ToArray();
            return (CaptureSession(entry.Status), signals);
        }
        finally { gate.Release(); }
    }
    public async Task<RuntimeSessionStatus> Disconnect(string attemptId, CancellationToken cancellationToken, bool acknowledgeUnknown = false)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var status = Read(attemptId);
            if (live.TryGetValue(attemptId,out var owned)) EnsureRecord(owned);
            if (acknowledgeUnknown)
            {
                if (live.TryGetValue(attemptId,out var active) && active.Status.State == "connected") throw Error("RuntimeSessionStillConnected");
                if (status.State is not ("connecting" or "connected" or "disconnecting" or "remoteSessionUnknown")) return Observe(status);
                if (live.Remove(attemptId,out var abandoned)) { abandoned.Client.Dispose(); abandoned.Selections.Clear(); abandoned.Metadata.Clear(); }
                status = status with { State = "abandonedRemoteUnknown" }; Save(status); return status;
            }
            if (!live.TryGetValue(attemptId, out var entry)) return Observe(status);
            return await Close(entry);
        }
        finally { gate.Release(); }
    }
    private async Task<RuntimeSessionStatus> Close(Live entry)
    {
        var status = entry.Status with { State = "disconnecting" };
        Save(status); entry.Status = status; entry.Selections.Clear(); entry.Metadata.Clear();
        try { await entry.Client.LogoutAsync(CancellationToken.None); status = status with { State = "closed", ErrorCode = null }; }
        catch (Exception error) { status = status with { State = "remoteSessionUnknown", ErrorCode = Code(error) }; }
        finally { entry.Client.Dispose(); live.Remove(status.AttemptId); }
        Save(status); return status;
    }
    private static RuntimeCaptureSession CaptureSession(RuntimeSessionStatus status) =>
        new(status.HostId, status.Context, status.Login.User, status.Login.Mode, status.Login.Credential, status.PeerVerified);
    private Live Active(string id)
    {
        Id(id);
        if (!live.TryGetValue(id, out var entry) || entry.Status.State != "connected" || entry.Client.IdleExpired || entry.Client.SessionUnusable) throw Error("RuntimeSessionUnavailable");
        EnsureRecord(entry);
        return entry;
    }
    private void EnsureRecord(Live entry)
    {
        var retained=Read(entry.Status.AttemptId);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(retained,Json),JsonSerializer.SerializeToElement(entry.Status,Json)))
            throw Error("RuntimeRecordChanged");
    }
    private void Use(Live entry, Action action, CancellationToken cancellationToken)
    {
        var login = entry.Status.Login;
        try
        {
            Consume(Vault(login),login.Credential, Binding(login.Endpoint, login.PeerCertificateSha256, login.User, login.Mode), _ =>
            {
                entry.Client.VerifySessionAsync(login.User,login.Mode,cancellationToken).GetAwaiter().GetResult();
                var status = entry.Status with { Profile = entry.Client.Profile };
                Save(status); entry.Status = status;
                action();
            });
        }
        catch (Exception error) when (entry.Client.SessionUnusable)
        {
            entry.Status = entry.Status with { State = "remoteSessionUnknown", ErrorCode = Code(error), ReadAvailability = "permissionUnknown" };
            Save(entry.Status); throw;
        }
    }
    private static void Consume(CredentialVault vault,CredentialReference reference,CredentialBinding binding,Action<byte[]> action)
    {
        string? failure=null;
        vault.Use(reference,binding,bytes =>
        {
            try { action(bytes); }
            catch (Exception error)
            {
                // Only fixed codes escape the vault consumer; never retain its exception or message.
                failure = error is BridgeException e && e.Code is
                    "RuntimePermissionDenied" or "RuntimeSymbolMetadataChanged" or "RuntimeResponseInvalid" or "RuntimeResponseLimit" or
                    "RuntimeAuthenticationDenied" or "RuntimeTransportFailure" or "RuntimeTimeout" or "RuntimeCancelled" or
                    "RuntimeTlsUnverified" or "RuntimeHttpFailure" or "RuntimeSessionExpired" or "RuntimeBrowseFailed" or
                    "RuntimeDiscoveryFailed" or "RuntimeRequestLimit" or "RuntimeLoginResponseInvalid" or "RuntimeProviderRejected" or "RuntimeSecretEchoRejected"
                    or "RuntimeProfileInvalid" or "RuntimeSessionIdentityMismatch"
                    ? e.Code : "RuntimeOperationFailed";
            }
        });
        if (failure is not null) throw Error(failure);
    }
    private static CredentialVault Vault(RuntimeLoginSelection login) => new(CredentialVault.UserRoot(login.VaultName));
    private static void ValidatePassword(byte[] bytes)
    {
        using var password = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 1 });
        if (password.RootElement.ValueKind != JsonValueKind.String || bytes.Length > 32*1024) throw Error("RuntimeCredentialFormatInvalid");
        _ = new System.Text.UTF8Encoding(false,true).GetByteCount(password.RootElement.GetString()!);
    }
    private RuntimeSessionStatus Observe(RuntimeSessionStatus status) => live.ContainsKey(status.AttemptId) || Resolved(status.State) ? status :
        status with { State = "hostEndedRemoteUnknown" };
    private static bool Resolved(string state) => state is "closed" or "notConnected" or "abandonedRemoteUnknown";
    private async Task ExpireIdle()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                await gate.WaitAsync(stop.Token);
                try
                {
                    foreach (var entry in live.Values.Where(x => x.Client.IdleExpired).ToArray())
                        try { await Close(entry); } catch { entry.Client.Dispose(); live.Remove(entry.Status.AttemptId); }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private RuntimeSessionStatus Read(string id)
    {
        string path = PathFor(id); Plain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 128*1024) throw Error("RuntimeRecordInvalid");
        var record = JsonSerializer.Deserialize<RuntimeSessionStatus>(stream,Json) ?? throw Error("RuntimeRecordInvalid");
        if (record.SchemaVersion is not ("runtime-web-session.v1" or "runtime-web-session.v2") ||
            (record.SchemaVersion == "runtime-web-session.v1" && record.Profile is not null)) throw Error("RuntimeRecordInvalid");
        WebRuntimeProtocol.Validate(record.Context); Id(record.HostId);
        if (record.AttemptId != id || record.Context.ConnectionId != id || record.Login.Endpoint != record.Context.Endpoint ||
            record.Login.PeerCertificateSha256 != record.Context.PeerCertificateSha256 || record.State is not
            ("connecting" or "connected" or "disconnecting" or "closed" or "notConnected" or "remoteSessionUnknown" or "abandonedRemoteUnknown")) throw Error("RuntimeRecordInvalid");
        return record;
    }
    private void Save(RuntimeSessionStatus status, bool create = false)
    {
        string path = PathFor(status.AttemptId); Plain(path);
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(status,Json);
        if (bytes.Length>128*1024) throw Error("RuntimeRecordLimit");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        if (create) File.Move(temporary,path); else JournalFileReplacement.Replace(temporary,path);
    }
    private string PathFor(string id) { Id(id); return Path.Combine(root,id+".json"); }
    private static void Plain(string path) => _ = EngineeringResourceKey.ForPath("workspace",Path.GetFullPath(path));
    private static void Id(string id) { if (!ProtectedCredentialGuard.IsId(id)) throw Error("RuntimeIdentityInvalid"); }
    private static string Code(Exception error) => error is BridgeException e ? e.Code : "RuntimeOperationFailed";
    private static BridgeException Error(string code) => new(code,"Runtime operation refused; inspect the exact attempt and local credential binding. No secret or remote error text is returned.");
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await idleWorker; await gate.WaitAsync();
        try
        {
            foreach (var entry in live.Values.ToArray())
                try { await Close(entry); } catch { entry.Client.Dispose(); }
        }
        finally { gate.Release(); owner.Dispose(); stop.Dispose(); }
    }
}
