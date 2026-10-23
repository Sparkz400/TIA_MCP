using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using TiaMcp.Core;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Tests;

internal static class AddInReceiverTests
{
    internal static async Task<int> Child(string role, string root)
    {
        if (role == "--handoff-client")
        {
            Console.WriteLine("client-ready");
            while (await Console.In.ReadLineAsync() is { } frame) Console.WriteLine((await Send(root, frame)).ToJsonString());
            return 0;
        }
        var binding = JsonSerializer.Deserialize<AddInHandoffBinding>(File.ReadAllText(Path.Combine(root, "receiver-fixture.json")), BridgeProtocol.Json)!;
        int calls = 0;
        await using var receiver = new AddInHandoffReceiver(root, async (peer, review, hash, token) => {
            File.AppendAllText(Path.Combine(root, "authority-calls.log"), (++calls) + "\n");
            if (peer != binding.TiaProcess) throw new BridgeException("SyntheticPeerRefused", "A different same-user process is not the selected producer.");
            string mode = File.Exists(Path.Combine(root, "control")) ? File.ReadAllText(Path.Combine(root, "control")) : "";
            if (mode == "block") {
                File.WriteAllText(Path.Combine(root, "entered"), calls.ToString());
                await Task.Delay(Timeout.Infinite, token);
            }
            if (mode == "hold") {
                File.WriteAllText(Path.Combine(root, "entered"), calls.ToString());
                while (!File.Exists(Path.Combine(root, "release"))) await Task.Delay(10, token);
            }
            return mode == "changed" ? binding with { WorkerInstanceId = new string('f', 32) } : binding;
        });
        using var stopping = new CancellationTokenSource();
        var run = receiver.RunAsync(stopping.Token);
        Console.WriteLine("server-ready");
        var input = Console.In.ReadLineAsync();
        if (await Task.WhenAny(run, input) == input) await stopping.CancelAsync();
        await run; return 0;
    }

    private sealed class Server(Process process) : IAsyncDisposable
    {
        internal readonly Process Process = process;
        internal static async Task<Server> Start(string root, AddInHandoffBinding binding)
        {
            File.WriteAllText(Path.Combine(root, "receiver-fixture.json"), JsonSerializer.Serialize(binding, BridgeProtocol.Json));
            var process = Launch("--handoff-server", root);
            try {
                Check.Equal("server-ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                return new(process);
            } catch { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); process.Dispose(); throw; }
        }
        internal async Task Crash() { Process.Kill(); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) {
                await Process.StandardInput.WriteLineAsync("stop");
                try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal(0, Process.ExitCode); }
                finally { if (!Process.HasExited) { Process.Kill(); await Process.WaitForExitAsync(); } }
            }
            Process.Dispose();
        }
    }
    private static Process Launch(string role, string root)
    {
        var start = new ProcessStartInfo(BridgeFixture.DotnetPath()) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add(role); start.ArgumentList.Add(root);
        return Process.Start(start)!;
    }
    private static JsonObject Discovery(string root) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "addin-handoffs/receiver.json")))!.AsObject();
    private static JsonObject Request(string action, string? id = null) {
        var value = new JsonObject { ["protocolVersion"] = 1, ["action"] = action }; if (id is not null) value["requestId"] = id; return value;
    }
    private static JsonObject Begin(AddInHandoffTests.Fixture f, string id) {
        var r = Request("begin", id); r["reviewId"] = f.ReviewId; r["selectionBase64"] = Convert.ToBase64String(f.Bytes); return r;
    }
    private static JsonObject Complete(string id, string challenge) { var r = Request("complete", id); r["challenge"] = challenge; return r; }
    private static JsonObject Result(JsonObject response) { Check.Equal(true, (bool)response["success"]!); return response["result"]!.AsObject(); }
    private static void Refused(JsonObject response, string? code = null) {
        Check.Equal(false, (bool)response["success"]!); if (code is not null) Check.Equal(code, (string)response["error"]!);
    }
    private static async Task<NamedPipeClientStream> Connect(string root, int? expectedPid = null)
    {
        var discovery = Discovery(root); Check.Equal("listening", (string)discovery["state"]!);
        int pid = (int)discovery["host"]!["processId"]!;
        if (expectedPid.HasValue) Check.Equal(expectedPid.Value, pid);
        using var process = Process.GetProcessById(pid); _ = process.SafeHandle;
        Check.Equal((string)discovery["host"]!["startedAtUtc"]!, process.StartTime.ToUniversalTime().ToString("O"));
        Check.Equal((string)discovery["host"]!["executablePath"]!, process.MainModule!.FileName);
        var pipe = new NamedPipeClientStream(".", (string)discovery["pipeName"]!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try {
            await pipe.ConnectAsync(3000); Check.Equal(true, GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint observed));
            Check.Equal((uint)pid, observed); Check.Equal(false, process.HasExited); return pipe;
        } catch { pipe.Dispose(); throw; }
    }
    private static async Task Write(Stream pipe, string frame) { await pipe.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n")); await pipe.FlushAsync(); }
    private static async Task<JsonObject> Read(Stream pipe) {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        return JsonNode.Parse(await new BoundedLineReader(pipe, AddInHandoffReceiver.ResponseLimit).ReadAsync(deadline.Token))!.AsObject();
    }
    private static async Task<JsonObject> Send(string root, string frame, int? pid = null) {
        await using var pipe = await Connect(root, pid); await Write(pipe, frame); return await Read(pipe);
    }
    private static Task<JsonObject> Send(string root, JsonObject frame, int? pid = null) => Send(root, frame.ToJsonString(), pid);
    private static async Task Until(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Evidence(string root, string name, object value) {
        string path = Path.Combine(root, name + ".receiver.json");
        File.WriteAllText(path, JsonSerializer.Serialize(value, BridgeProtocol.Json));
        Console.WriteLine("RECEIVER_FIXTURE " + JsonSerializer.Serialize(new { path, name }));
    }

    internal static async Task ExchangeAndCrash()
    {
        await using var f = new AddInHandoffTests.Fixture(); await f.Store.DisposeAsync();
        string id = Guid.NewGuid().ToString("N"), pendingId = Guid.NewGuid().ToString("N"), interruptedId = Guid.NewGuid().ToString("N");
        JsonObject completed; string oldPipe, pendingChallenge;
        await using (var server = await Server.Start(f.Root, f.Binding)) {
            oldPipe = (string)Discovery(f.Root)["pipeName"]!;
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            await using (var aclPipe = await Connect(f.Root)) {
                var acl = aclPipe.GetAccessControl();
                using var self = WindowsIdentity.GetCurrent();
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Check.Equal(2, rules.Length); Check.Equal(true, acl.AreAccessRulesProtected);
                bool deniedNetwork = false, allowedUser = false;
                foreach (var rule in rules) {
                    deniedNetwork |= rule.AccessControlType == AccessControlType.Deny && ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.NetworkSid) && rule.PipeAccessRights == PipeAccessRights.FullControl;
                    allowedUser |= rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(self.User) && rule.PipeAccessRights == PipeAccessRights.FullControl;
                }
                Check.Equal(true, deniedNetwork); Check.Equal(true, allowedUser);
            }
            var status = Result(await Send(f.Root, Request("status"), server.Process.Id));
            Check.Equal(true, (bool)status["authorityConfigured"]!); Check.Equal(false, (bool)status["producerAuthenticated"]!);
            string concurrentId = Guid.NewGuid().ToString("N");
            string concurrentChallenge = (string)Result(await Send(f.Root, Begin(f, concurrentId)))["challenge"]!;
            File.WriteAllText(Path.Combine(f.Root, "control"), "hold");
            var first = Send(f.Root, Complete(concurrentId, concurrentChallenge));
            await Until(() => File.Exists(Path.Combine(f.Root, "entered")));
            var second = Send(f.Root, Complete(concurrentId, concurrentChallenge));
            await Task.Delay(50); Check.Equal(false, first.IsCompleted); Check.Equal(false, second.IsCompleted);
            File.WriteAllText(Path.Combine(f.Root, "release"), "release");
            Result(await first); Refused(await second, "HandoffChallengeUnavailable");
            File.WriteAllText(Path.Combine(f.Root, "control"), ""); File.Delete(Path.Combine(f.Root, "entered"));
            string changedId = Guid.NewGuid().ToString("N");
            string changedChallenge = (string)Result(await Send(f.Root, Begin(f, changedId)))["challenge"]!;
            File.WriteAllText(Path.Combine(f.Root, "control"), "changed");
            Refused(await Send(f.Root, Complete(changedId, changedChallenge)), "HandoffOwnerChanged");
            File.WriteAllText(Path.Combine(f.Root, "control"), "");
            Refused(await Send(f.Root, Complete(changedId, changedChallenge)), "HandoffChallengeUnavailable");
            var challenge = Result(await Send(f.Root, Begin(f, id), server.Process.Id));
            Check.Equal((string)status["receiverInstanceId"]!, (string)challenge["receiverInstanceId"]!);
            pendingChallenge = (string)Result(await Send(f.Root, Begin(f, pendingId)))["challenge"]!;
            string interruptedChallenge = (string)Result(await Send(f.Root, Begin(f, interruptedId)))["challenge"]!;
            // Commit the request but deliberately never read its reply, then kill the actual receiver process.
            await using (var unread = await Connect(f.Root)) {
                await Write(unread, Complete(id, (string)challenge["challenge"]!).ToJsonString());
                await Until(() => File.Exists(f.Journal(id, "completed.json")));
                completed = Result(await Send(f.Root, Request("read", id)));
                File.WriteAllText(Path.Combine(f.Root, "control"), "block");
                await using var interrupted = await Connect(f.Root);
                await Write(interrupted, Complete(interruptedId, interruptedChallenge).ToJsonString());
                await Until(() => File.Exists(Path.Combine(f.Root, "entered")));
                await server.Crash();
            }
        }
        await using (var restarted = await Server.Start(f.Root, f.Binding)) {
            Check.Equal(false, oldPipe == (string)Discovery(f.Root)["pipeName"]!);
            Check.Equal(true, JsonNode.DeepEquals(completed, Result(await Send(f.Root, Request("read", id), restarted.Process.Id))));
            Refused(await Send(f.Root, Begin(f, id)), "HandoffRequestExists");
            Refused(await Send(f.Root, Complete(pendingId, pendingChallenge)), "HandoffChallengeUnavailable");
            Check.Equal("interrupted", (string)Result(await Send(f.Root, Request("read", pendingId)))["state"]!);
            var consumed = Result(await Send(f.Root, Request("read", interruptedId)));
            Check.Equal("consumedNoReceipt", (string)consumed["state"]!);
            Evidence(f.Root, "crash-recovery", new { nativeExecuted = false, actualReceiverCrash = true, unreadReply = true, concurrentConsumption = true, ownerChangeRefused = true, completed, consumed, pendingId });
        }
        Check.Equal("stopped", (string)Discovery(f.Root)["state"]!);
    }

    internal static async Task ImpostorAndMalformed()
    {
        await using var f = new AddInHandoffTests.Fixture(); await f.Store.DisposeAsync();
        await using var server = await Server.Start(f.Root, f.Binding);
        using (var impostor = Launch("--handoff-client", f.Root)) {
            try {
                Check.Equal("client-ready", await impostor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
                await impostor.StandardInput.WriteLineAsync(Begin(f, Guid.NewGuid().ToString("N")).ToJsonString());
                Refused(JsonNode.Parse((await impostor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)!.AsObject(), "SyntheticPeerRefused");
                impostor.StandardInput.Close(); await impostor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check.Equal(0, impostor.ExitCode);
            } finally { if (!impostor.HasExited) { impostor.Kill(); await impostor.WaitForExitAsync(); } }
        }
        string[] malformed = ["[]", "{\"protocolVersion\":1,\"action\":\"status\",\"action\":\"status\"}",
            "{\"protocolVersion\":2,\"action\":\"status\"}", "{\"protocolVersion\":1,\"action\":\"native_dispatch\"}",
            "{\"protocolVersion\":1,\"action\":\"status\",\"peer\":123}", "{\"protocolVersion\":null,\"action\":\"status\"}"];
        foreach (string frame in malformed) Refused(await Send(f.Root, frame));
        var bad64 = Begin(f, Guid.NewGuid().ToString("N")); bad64["selectionBase64"] = "%"; Refused(await Send(f.Root, bad64));
        await using (var invalidUtf8 = await Connect(f.Root)) { await invalidUtf8.WriteAsync(new byte[] { 255, 10 }); Refused(await Read(invalidUtf8)); }
        // More than one MiB after decoding is refused before storage/authority.
        bad64["selectionBase64"] = Convert.ToBase64String(new byte[AddInSelection.InputLimit + 1]); Refused(await Send(f.Root, bad64), "HandoffLimit");
        await using (var oversized = await Connect(f.Root)) {
            try { await Write(oversized, new string('x', AddInHandoffReceiver.RequestLimit + 1)); Refused(await Read(oversized), "ResponseTooLarge"); }
            catch (IOException) { }
        }
        Check.Equal(1, File.ReadAllLines(Path.Combine(f.Root, "authority-calls.log")).Length);
        Check.Equal(0, Directory.GetDirectories(Path.Combine(f.Root, "addin-handoffs")).Length);
        Result(await Send(f.Root, Request("status")));
        Evidence(f.Root, "impostor-malformed", new { nativeExecuted = false, actualSameUserImpostor = true, malformed = malformed.Length + 3, authorityCalls = 1 });
    }

    internal static async Task CapacityDeadlineAndDisconnect()
    {
        await using var f = new AddInHandoffTests.Fixture(); await f.Store.DisposeAsync();
        await using var server = await Server.Start(f.Root, f.Binding);
        var slow = new List<NamedPipeClientStream>(); var timer = Stopwatch.StartNew();
        try {
            for (int i = 0; i < AddInHandoffReceiver.Connections; i++) {
                var pipe = await Connect(f.Root); slow.Add(pipe); await pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"protocolVersion\":"));
            }
            await using var extra = new NamedPipeClientStream(".", (string)Discovery(f.Root)["pipeName"]!, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Check.ThrowsAsync<TimeoutException>(() => extra.ConnectAsync(200));
            foreach (var pipe in slow) {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                Check.Equal(0, await pipe.ReadAsync(new byte[1], limit.Token));
            }
            Check.Equal(true, timer.Elapsed < TimeSpan.FromSeconds(25));
        } finally { foreach (var pipe in slow) pipe.Dispose(); }
        Result(await Send(f.Root, Request("status")));
        string id = Guid.NewGuid().ToString("N"); var challenge = Result(await Send(f.Root, Begin(f, id)));
        File.WriteAllText(Path.Combine(f.Root, "control"), "block");
        await using (var disconnect = await Connect(f.Root)) {
            await Write(disconnect, Complete(id, (string)challenge["challenge"]!).ToJsonString());
            await Until(() => File.Exists(Path.Combine(f.Root, "entered")));
        }
        File.WriteAllText(Path.Combine(f.Root, "control"), "");
        var status = Result(await Send(f.Root, Request("read", id)));
        Check.Equal("consumedNoReceipt", (string)status["state"]!);
        Refused(await Send(f.Root, Complete(id, (string)challenge["challenge"]!)), "HandoffChallengeUnavailable");
        Check.Equal(false, File.Exists(f.Journal(id, "receipt.json")));
        Evidence(f.Root, "bounded-disconnect", new { nativeExecuted = false, connections = slow.Count, elapsedMs = timer.ElapsedMilliseconds, status });
    }

    internal static async Task PeerExit()
    {
        await using var f = new AddInHandoffTests.Fixture(); await f.Store.DisposeAsync();
        using var peer = Launch("--handoff-client", f.Root);
        try {
            Check.Equal("client-ready", await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var identity = new AddInHandoffPeer(peer.Id, peer.StartTime.ToUniversalTime().ToString("O"), peer.MainModule!.FileName, peer.SessionId);
            await using var server = await Server.Start(f.Root, f.Binding with { TiaProcess = identity });
            string id = Guid.NewGuid().ToString("N");
            await peer.StandardInput.WriteLineAsync(Begin(f, id).ToJsonString());
            var challenge = Result(JsonNode.Parse((await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!)!.AsObject());
            File.WriteAllText(Path.Combine(f.Root, "control"), "block");
            await peer.StandardInput.WriteLineAsync(Complete(id, (string)challenge["challenge"]!).ToJsonString());
            await Until(() => File.Exists(Path.Combine(f.Root, "entered")));
            peer.Kill(); await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var status = Result(await Send(f.Root, Request("read", id)));
            Check.Equal("consumedNoReceipt", (string)status["state"]!); Check.Equal(false, File.Exists(f.Journal(id, "receipt.json")));
            Check.Equal(false, server.Process.HasExited);
            Evidence(f.Root, "peer-exit", new { nativeExecuted = false, actualPeerExit = true, receiverSurvived = true, status });
        } finally { if (!peer.HasExited) { peer.Kill(); await peer.WaitForExitAsync(); } }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
