using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// Test double only. No Siemens references and no network or project operations.
var configPath = Environment.GetEnvironmentVariable("TIA_MCP_RELIABILITY_CONFIG");
if (configPath is null) return 2;
using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
var config = configDocument.RootElement;
var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
var token = config.GetProperty("token").GetString()!;
var mode = config.GetProperty("mode").GetString()!;
var eventsPath = Path.Combine(directory, "events.jsonl");
var journalPath = Path.Combine(directory, "fake-journal.json");
var eventGate = new object();
void Record(string kind, string? operation = null, string? id = null)
{
    lock (eventGate)
    {
        using var output = new FileStream(eventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            kind, operation, id, token, pid = Environment.ProcessId,
            timestamp = DateTimeOffset.UtcNow
        }) + "\n");
        output.Write(bytes);
        output.Flush(true);
    }
}

Record("started");
if (mode == "early-exit") { Record("stopped"); return 23; }
var pipeIndex = Array.IndexOf(args, "--pipe");
if (pipeIndex < 0 || pipeIndex + 1 >= args.Length) return 3;
using var pipe = new NamedPipeClientStream(".", args[pipeIndex + 1], PipeDirection.InOut, PipeOptions.Asynchronous);
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var writeGate = new SemaphoreSlim(1, 1);
var handlers = new List<Task>();
async Task Send(string text)
{
    await writeGate.WaitAsync(lifetime.Token);
    try
    {
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token);
        await pipe.FlushAsync(lifetime.Token);
    }
    finally { writeGate.Release(); }
}
async Task Handle(JsonElement request)
{
    var operation = request.GetProperty("operation").GetString()!;
    var id = request.GetProperty("id").GetString()!;
    var arguments = request.GetProperty("arguments");
    object result = new { testDouble = true, operation };
    if (operation == "change_apply")
    {
        // Durable side effect intentionally precedes the response. This demonstrates
        // why transport failure must not be interpreted as a failed mutation.
        if (File.Exists(journalPath)) throw new InvalidOperationException("A fake mutation was replayed.");
        var journal = new
        {
            testDouble = true, token, state = "committed", mutationCount = 1,
            changeId = arguments.GetProperty("changeId").GetString(), requestId = id,
            idempotencyKey = arguments.GetProperty("idempotencyKey").GetString()
        };
        using (var output = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
            output.Write(bytes);
            output.Flush(true);
        }
        Record("committed", operation, id);
        if (mode == "eof") { pipe.Dispose(); return; }
        if (mode == "partial-frame") { await Send("{\"protocolVersion\":1"); pipe.Dispose(); return; }
        if (mode == "wrong-id") { await Send(JsonSerializer.Serialize(new { protocolVersion = 1, id = "unexpected", success = true, result }) + "\n"); return; }
        if (mode == "malformed") { await Send("not-json\n"); return; }
        if (mode is "timeout" or "kill-worker" or "kill-host" or "cancel")
        {
            await Task.Delay(Timeout.Infinite, lifetime.Token);
            return;
        }
        result = journal;
    }
    else if (operation == "change_status")
    {
        using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
        result = new { testDouble = true, journal = journal.RootElement.Clone(), loadedFromDisk = true };
    }
    else if (operation == "block_read" && mode == "operation-error")
    {
        await Send(JsonSerializer.Serialize(new
        {
            protocolVersion = 1, id, success = false,
            error = new { code = "ProtectedBlock", message = "Intentional test-double rejection.", details = new { testDouble = true } }
        }) + "\n");
        return;
    }
    else if (operation == "bridge_info") result = new { testDouble = true, workerPid = Environment.ProcessId, token };
    await Send(JsonSerializer.Serialize(new { protocolVersion = 1, id, success = true, result }) + "\n");
}

try
{
    await pipe.ConnectAsync(lifetime.Token);
    Record("connected");
    using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), leaveOpen: true);
    while (!lifetime.IsCancellationRequested)
    {
        var line = await reader.ReadLineAsync(lifetime.Token);
        if (line is null) { Record("host-eof"); break; }
        if (line.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Test input exceeds its limit.");
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement.Clone();
        Record("request", request.GetProperty("operation").GetString(), request.GetProperty("id").GetString());
        handlers.Add(Handle(request));
    }
}
catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
finally
{
    lifetime.Cancel();
    try { await Task.WhenAll(handlers); }
    catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
    Record("stopped");
}
return 0;
