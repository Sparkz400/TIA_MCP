using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal sealed record WorkerEvent(string Kind, string? Operation = null, string? Id = null);

/// <summary>A subprocess that only implements the private pipe protocol. It never loads Siemens assemblies.</summary>
internal static class FakeWorker
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--exit-before-connect")) return 23;
        string? pipeName = null;
        string? eventsPath = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length) pipeName = args[++i];
            else if (args[i] == "--events" && i + 1 < args.Length) eventsPath = args[++i];
        }
        if (pipeName is null) return 2;
        var eventLock = new object();
        void Record(WorkerEvent item)
        {
            if (eventsPath is null) return;
            lock (eventLock)
            {
                using var output = new FileStream(eventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item) + "\n");
                output.Write(line);
            }
        }

        if (args.Contains("--delegate-connection"))
        {
            Record(new("launcherStarted", Id: Environment.ProcessId.ToString()));
            var start = new ProcessStartInfo(BridgeFixture.DotnetPath()) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var argument in args.Where(a => a != "--delegate-connection")) start.ArgumentList.Add(argument);
            using var peer = Process.Start(start) ?? throw new InvalidOperationException("Could not start impostor fixture.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await peer.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!peer.HasExited) { peer.Kill(); await peer.WaitForExitAsync(); }
                Record(new("launcherStopped", Id: Environment.ProcessId.ToString()));
            }
            return peer.ExitCode;
        }
        Record(new("started", Id: Environment.ProcessId.ToString()));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var writeGate = new SemaphoreSlim(1, 1);
        var handlers = new List<Task>();
        var active = 0;
        var maximumInFlight = 0;
        var exitCode = 0;
        async Task WriteAsync(string text)
        {
            await writeGate.WaitAsync(lifetime.Token);
            try
            {
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token);
                await pipe.FlushAsync(lifetime.Token);
            }
            finally { writeGate.Release(); }
        }

        async Task HandleAsync(JsonElement request)
        {
            var operation = request.GetProperty("operation").GetString()!;
            var id = request.GetProperty("id").GetString()!;
            var arguments = request.GetProperty("arguments");
            var current = Interlocked.Increment(ref active);
            maximumInFlight = Math.Max(maximumInFlight, current);
            var complete = false;
            void CompleteOperation()
            {
                if (complete) return;
                complete = true;
                Interlocked.Decrement(ref active);
            }
            async Task ReplyAsync(string text)
            {
                // Once the response is available, the client is allowed to issue its next request.
                CompleteOperation();
                await WriteAsync(text);
            }
            try
            {
                if (arguments.TryGetProperty("delayMs", out var delay)) await Task.Delay(delay.GetInt32(), lifetime.Token);
                switch (operation)
                {
                    case "exitAfterReply":
                        await ReplyAsync(JsonSerializer.Serialize(new { protocolVersion = 1, id, success = true, result = new { } }) + "\n");
                        lifetime.Cancel();
                        return;
                    case "exitOnShutdown":
                        exitCode = 27;
                        goto default;
                    case "stall":
                        await Task.Delay(Timeout.Infinite, lifetime.Token);
                        return;
                    case "disconnect":
                        pipe.Dispose();
                        return;
                    case "truncated":
                        await ReplyAsync("{\"protocolVersion\":1");
                        pipe.Dispose();
                        return;
                    case "malformed":
                        await ReplyAsync("not-json\n");
                        return;
                    case "operationError":
                        await ReplyAsync(JsonSerializer.Serialize(new
                        {
                            protocolVersion = 1, id, success = false,
                            error = new { code = "ProtectedBlock", message = "Cannot access block.", details = new { block = "FB10" } }
                        }) + "\n");
                        return;
                    default:
                        await ReplyAsync(JsonSerializer.Serialize(new
                        {
                            protocolVersion = operation == "wrongVersion" ? 2 : 1,
                            id = operation == "wrongId" ? "wrong-request-id" : id,
                            success = true,
                            result = new { arguments, maximumInFlight }
                        }) + "\n");
                        return;
                }
            }
            finally { CompleteOperation(); }
        }

        try
        {
            await pipe.ConnectAsync(lifetime.Token);
            var reader = new BoundedLineReader(pipe, BridgeProtocol.MaxRequestBytes);
            while (!lifetime.IsCancellationRequested)
            {
                var line = await reader.ReadAsync(lifetime.Token);
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement.Clone();
                Record(new("request", request.GetProperty("operation").GetString(), request.GetProperty("id").GetString()));
                // Read ahead while processing so the tests can detect accidental request pipelining.
                handlers.Add(HandleAsync(request));
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine("Fake-worker named-pipe connection was denied: " + exception.Message);
            exitCode = 3;
        }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(handlers); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            Record(new("stopped"));
        }
        return exitCode;
    }
}

internal sealed class BridgeFixture : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TiaMcpTests-" + Guid.NewGuid().ToString("N"));
    private readonly string _eventsPath;
    public BridgeClient Client { get; }

    public BridgeFixture(int timeoutSeconds = 10, bool delegateConnection = false)
    {
        Directory.CreateDirectory(_directory);
        _eventsPath = Path.Combine(_directory, "events.jsonl");
        List<string> arguments = [Assembly.GetExecutingAssembly().Location, "--fake-worker", "--events", _eventsPath];
        if (delegateConnection) arguments.Add("--delegate-connection");
        Client = new BridgeClient(new BridgeOptions(DotnetPath(), TimeoutSeconds: timeoutSeconds, WorkerPrefixArguments: arguments));
    }

    public static string DotnetPath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")));
        return candidates.FirstOrDefault(path => path is not null && File.Exists(path))
            ?? throw new FileNotFoundException("Cannot find dotnet host for fake-worker process tests.");
    }

    public IReadOnlyList<WorkerEvent> Events()
    {
        if (!File.Exists(_eventsPath)) return [];
        using var stream = new FileStream(_eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        // Ignore a concurrently appended incomplete line; the next poll will see it complete.
        var completeLength = text.LastIndexOf('\n') + 1;
        return text[..completeLength].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<WorkerEvent>(line)!).ToArray();
    }

    public async Task WaitForRequestAsync(string operation)
    {
        await WaitForAsync(() => Events().Any(item => item.Kind == "request" && item.Operation == operation), "worker request " + operation);
    }

    public async Task AssertTerminalAsync(string operation)
    {
        await Check.BridgeError("BridgeFaulted", () => Client.CallAsync("mustNotRun"));
        await Check.BridgeError("BridgeFaulted", () => Client.CallAsync(operation));
        await WaitForAsync(() => Events().Any(item => item.Kind == "stopped"), "worker EOF exit");
        var events = Events();
        Check.Equal(1, events.Count(item => item.Kind == "started"));
        Check.Equal(1, events.Count(item => item.Kind == "request" && item.Operation == operation));
        Check.Equal(0, events.Count(item => item.Operation == "mustNotRun"));
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        if (Events().Any(item => item.Kind == "started"))
            await WaitForAsync(() => Events().Any(item => item.Kind == "stopped"), "worker shutdown");
        File.Delete(_eventsPath);
        Directory.Delete(_directory); // Only this fixture's known, now-empty directory; never recursive.
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Timed out waiting for " + description + ".");
            await Task.Delay(20);
        }
    }
}
