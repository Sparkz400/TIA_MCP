using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.Core.AddIns;

// Runs inside the owning Host. A null authority is the production enrollment gate, never a test fallback.
internal sealed class AddInHandoffReceiver : IAsyncDisposable
{
    internal const int Connections = 4, RequestLimit = ((AddInSelection.InputLimit + 2) / 3) * 4 + 4096, ResponseLimit = 2 << 20;
    internal static readonly TimeSpan ConnectionLifetime = TimeSpan.FromSeconds(20);
    private readonly AddInHandoff handoff;
    private readonly string discoveryPath;
    private readonly bool authorityConfigured;
    private readonly CancellationTokenSource stopping = new();
    private Task? running;
    private bool disposed;
    internal string PipeName { get; }

    internal AddInHandoffReceiver(string artifacts,
        Func<AddInHandoffPeer, string, string, CancellationToken, Task<AddInHandoffBinding>>? authority = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Add-In handoff requires Windows pipe peer verification.");
        authorityConfigured = authority is not null;
        handoff = new(artifacts, authority ?? ((_, _, _, _) => throw new BridgeException("AddInEnrollmentUnavailable", "No trusted producer enrollment provider is installed.")));
        discoveryPath = Path.Combine(Path.GetFullPath(artifacts), "addin-handoffs", "receiver.json");
        PipeName = "TiaMcp-AddIn-" + handoff.InstanceId;
    }

    internal Task RunAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (running is not null) throw new InvalidOperationException("Receiver already started.");
        return running = RunCoreAsync(token);
    }

    private async Task RunCoreAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
        var pipes = new List<NamedPipeServerStream>();
        var loops = new List<Task>();
        try
        {
            // Reserve every pipe instance before publishing discovery; no other process supplies the endpoint name.
            for (int i = 0; i < Connections; i++) pipes.Add(Listen(i == 0));
            PublishDiscovery("listening");
            foreach (var pipe in pipes) loops.Add(AcceptAsync(pipe, lifetime));
            await Task.WhenAll(loops);
        }
        finally
        {
            await lifetime.CancelAsync();
            foreach (var pipe in pipes) pipe.Dispose();
            try { await Task.WhenAll(loops); }
            finally { PublishDiscovery("stopped"); }
        }
    }

    private NamedPipeServerStream Listen(bool first)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        // CurrentUserOnly clients compare against the token's default owner, which is Administrators when elevated.
        security.SetOwner(identity.Owner ?? identity.User!); security.SetAccessRuleProtection(true, false);
        // CurrentUserOnly would replace this ACL and discard the network-logon denial.
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, Connections, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 8192, 8192, security);
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe, CancellationTokenSource lifetime)
    {
        using (pipe)
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                await ServeAsync(pipe, lifetime.Token);
                pipe.Disconnect();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { await lifetime.CancelAsync(); throw; }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(ConnectionLifetime);
        Process? process = null;
        Task? disconnected = null, exited = null;
        try
        {
            DateTime observed = DateTime.UtcNow;
            uint pid = PeerId(pipe);
            process = Process.GetProcessById(checked((int)pid));
            _ = process.SafeHandle;
            var peer = Identity(process);
            using var self = Process.GetCurrentProcess();
            if (peer.WindowsSessionId != self.SessionId || process.StartTime.ToUniversalTime() > observed)
                throw new BridgeException("HandoffPeerMismatch", "The peer is not a local process in this Windows session.");
            VerifyPeer(pipe, process, peer);
            exited = WatchExitAsync(process, deadline);
            string frame = await new BoundedLineReader(pipe, RequestLimit).ReadAsync(deadline.Token);
            var request = Parse(frame);
            // One request per connection; disconnect or further unbuffered input cancels any pending admission.
            disconnected = WatchDisconnectAsync(pipe, deadline);
            VerifyPeer(pipe, process, peer);
            JsonObject result = await DispatchAsync(request, peer, deadline.Token);
            VerifyPeer(pipe, process, peer);
            await ReplyAsync(pipe, new() { ["protocolVersion"] = 1, ["success"] = true, ["result"] = result }, deadline.Token);
            // Disconnect() discards unread pipe output. Retain it until the reader closes or the fixed deadline expires.
            await disconnected;
        }
        catch (Exception error) when (error is BridgeException or JsonException or ArgumentException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException or OverflowException)
        {
            if (!deadline.IsCancellationRequested)
            {
                string code = error is BridgeException bridge ? bridge.Code : "HandoffRequestRejected";
                try {
                    await ReplyAsync(pipe, new() { ["protocolVersion"] = 1, ["success"] = false, ["error"] = code }, deadline.Token);
                    disconnected ??= WatchDisconnectAsync(pipe, deadline);
                    await disconnected;
                }
                catch (Exception write) when (write is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
        finally
        {
            await deadline.CancelAsync();
            if (disconnected is not null) await disconnected;
            if (exited is not null) await exited;
            process?.Dispose();
        }
    }

    private async Task<JsonObject> DispatchAsync(JsonObject request, AddInHandoffPeer peer, CancellationToken token)
    {
        string action = (string)request["action"]!;
        if (action == "status") return Discovery("listening");
        if (!authorityConfigured && action is "begin" or "complete" or "cancel")
            throw new BridgeException("AddInEnrollmentUnavailable", "No trusted producer enrollment provider is installed.");
        string id = (string)request["requestId"]!;
        if (action == "read") return await handoff.ReadAsync(id, token);
        if (action == "begin")
        {
            string encoded = (string)request["selectionBase64"]!;
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > AddInSelection.InputLimit || Convert.ToBase64String(bytes) != encoded)
                throw new BridgeException("HandoffLimit", "Selection must use bounded canonical base64.");
            return await handoff.BeginAsync(id, (string)request["reviewId"]!, bytes, peer, token);
        }
        if (action == "complete") return await handoff.CompleteAsync(id, (string)request["challenge"]!, peer, token);
        await handoff.CancelAsync(id, (string)request["challenge"]!, peer, token);
        return await handoff.ReadAsync(id, token);
    }

    private static JsonObject Parse(string frame)
    {
        using var document = JsonDocument.Parse(frame, new() { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.EnumerateObject())
            if (!names.Add(field.Name) || (field.Name != "protocolVersion" && field.Value.ValueKind != JsonValueKind.String)) throw new JsonException();
        var request = JsonNode.Parse(frame)!.AsObject();
        if ((int?)request["protocolVersion"] != 1) throw new JsonException();
        string fields = (string?)request["action"] switch {
            "status" => "protocolVersion action", "begin" => "protocolVersion action requestId reviewId selectionBase64",
            "complete" or "cancel" => "protocolVersion action requestId challenge", "read" => "protocolVersion action requestId",
            _ => throw new JsonException() };
        if (!names.SetEquals(fields.Split(' '))) throw new JsonException();
        foreach (string key in new[] { "requestId", "reviewId", "challenge" })
            if (request[key] is not null) {
                string value = (string)request[key]!;
                if (value.Length != (key == "requestId" ? 32 : 64) || !value.All(char.IsAsciiHexDigitLower)) throw new JsonException();
            }
        return request;
    }

    private static async Task ReplyAsync(Stream pipe, JsonObject value, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");
        if (bytes.Length > ResponseLimit) throw new BridgeException("HandoffResponseTooLarge", "Retained result exceeds the transport bound; inspect local evidence.");
        await pipe.WriteAsync(bytes, token); await pipe.FlushAsync(token);
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource deadline)
    {
        try { _ = await pipe.ReadAsync(new byte[1], deadline.Token); await deadline.CancelAsync(); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (IOException) { await deadline.CancelAsync(); }
    }
    private static async Task WatchExitAsync(Process process, CancellationTokenSource deadline)
    {
        try { await process.WaitForExitAsync(deadline.Token); await deadline.CancelAsync(); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }
    private static AddInHandoffPeer Identity(Process process) => new(process.Id, process.StartTime.ToUniversalTime().ToString("O"),
        process.MainModule?.FileName ?? throw new BridgeException("HandoffPeerUnavailable", "Peer image unavailable."), process.SessionId);
    private static uint PeerId(NamedPipeServerStream pipe) => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint id)
        ? id : throw new Win32Exception(Marshal.GetLastPInvokeError());
    private static void VerifyPeer(NamedPipeServerStream pipe, Process process, AddInHandoffPeer expected)
    {
        if (process.HasExited || PeerId(pipe) != expected.ProcessId || Identity(process) != expected || process.HasExited)
            throw new BridgeException("HandoffPeerMismatch", "The connected peer changed or exited.");
    }
    private JsonObject Discovery(string state)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        return new() { ["schemaVersion"] = "addin-handoff-discovery.v1", ["protocolVersion"] = 1, ["state"] = state,
            ["receiverInstanceId"] = handoff.InstanceId, ["pipeName"] = PipeName,
            ["host"] = JsonSerializer.SerializeToNode(Identity(self), BridgeProtocol.Json),
            ["userSid"] = identity.User!.Value, ["authorityConfigured"] = authorityConfigured,
            ["maximumConnections"] = Connections, ["requestBytes"] = RequestLimit, ["responseBytes"] = ResponseLimit,
            ["connectionLifetimeMilliseconds"] = (int)ConnectionLifetime.TotalMilliseconds,
            ["producerAuthenticated"] = false, ["liveSelectionVerified"] = false, ["nativeQualified"] = false };
    }
    private void PublishDiscovery(string state) => PackageStorage.Save(discoveryPath, Discovery(state));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        await stopping.CancelAsync();
        try { if (running is not null) await running; }
        finally { await handoff.DisposeAsync(); stopping.Dispose(); }
    }
}
