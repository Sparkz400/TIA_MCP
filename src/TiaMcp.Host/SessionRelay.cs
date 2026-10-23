using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaMcp.Host;

// An explicit deployment transport. Each connection owns one set of local,
// same-user pipes and one MCP host. It never interprets or replays MCP requests.
internal static class SessionRelay
{
    private sealed record Settings(string Channel, string UserSid, string[] HostArguments,
        string ReceiptPath, int ShutdownTimeoutSeconds);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--session-relay-client" or "--session-relay-server"))
            throw new ArgumentException("Expected --session-relay-client|--session-relay-server SETTINGS.json.");
        var path = Path.GetFullPath(args[1]);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Relay settings exceed 64 KiB.");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing relay settings.");
        if (!Regex.IsMatch(settings.Channel ?? "", "^[a-f0-9]{32}$") ||
            settings.UserSid != WindowsIdentity.GetCurrent().User?.Value ||
            settings.ShutdownTimeoutSeconds is < 15 or > 960 ||
            settings.HostArguments is null || settings.HostArguments.Length > 24 ||
            settings.HostArguments.Any(a => a is null || a.Contains('\0') || a.StartsWith("--session-relay-", StringComparison.Ordinal)) ||
            !string.Equals(Path.GetFullPath(settings.ReceiptPath), Path.Combine(Path.GetDirectoryName(path)!, "receipt.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid relay identity, arguments, timeout or receipt location.");
        return args[0] == "--session-relay-client" ? await ClientAsync(settings) : await ServerAsync(settings);
    }

    private static string PipeName(Settings settings, string suffix) => "tia-mcp-session-" + settings.Channel + "-" + suffix;

    private static NamedPipeServerStream Listen(Settings settings, string suffix, PipeDirection direction) =>
        new(PipeName(settings, suffix), direction, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);

    private static NamedPipeClientStream Connect(Settings settings, string suffix, PipeDirection direction) =>
        new(".", PipeName(settings, suffix), direction, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<int> ClientAsync(Settings settings)
    {
        using var input = Listen(settings, "in", PipeDirection.Out);
        using var output = Listen(settings, "out", PipeDirection.In);
        using var errors = Listen(settings, "err", PipeDirection.In);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try
        {
            await Task.WhenAll(input.WaitForConnectionAsync(startup.Token), output.WaitForConnectionAsync(startup.Token), errors.WaitForConnectionAsync(startup.Token));
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Interactive MCP session did not connect within 75 seconds. The selected Windows user must be logged in; inspect its scheduled task and relay receipt. No request was replayed.");
        }
        // A read on redirected console stdin is not reliably cancellable on
        // Windows. Do not wait for it after the remote output pipes close.
        var send = SendInputAsync(Console.OpenStandardInput(), input);
        await Task.WhenAll(output.CopyToAsync(Console.OpenStandardOutput()), errors.CopyToAsync(Console.OpenStandardError()));
        input.Dispose();
        _ = send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        // Server writes this before closing the output pipes. Missing/unfinished
        // evidence is failure, never a claim that remote work finished cleanly.
        var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(settings.ReceiptPath));
        using (receipt)
        {
            if (receipt.RootElement.GetProperty("channel").GetString() != settings.Channel ||
                receipt.RootElement.GetProperty("userSid").GetString() != settings.UserSid ||
                receipt.RootElement.GetProperty("sessionId").GetInt32() == 0)
                throw new InvalidDataException("Interactive relay receipt identity does not match this connection.");
            if (!receipt.RootElement.TryGetProperty("exitCode", out var code) || !code.TryGetInt32(out int exitCode)) return 1;
            return exitCode;
        }
    }

    private static async Task SendInputAsync(Stream source, Stream target)
    {
        try { await source.CopyToAsync(target); await target.FlushAsync(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { target.Dispose(); }
    }

    private static async Task DrainOutputAsync(Stream source, Stream target)
    {
        try { await source.CopyToAsync(target); await target.FlushAsync(); }
        catch (IOException) { await source.CopyToAsync(Stream.Null); }
        catch (ObjectDisposedException) { await source.CopyToAsync(Stream.Null); }
    }

    private static async Task<int> ServerAsync(Settings settings)
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        var receipt = new Dictionary<string, object?> {
            ["schemaVersion"] = 1, ["channel"] = settings.Channel, ["userSid"] = settings.UserSid,
            ["sessionId"] = sessionId, ["relayProcessId"] = Environment.ProcessId,
            ["startedAtUtc"] = DateTimeOffset.UtcNow, ["mode"] = "interactive-user",
            ["status"] = "starting", ["replayCount"] = 0,
            ["nativeProcessExitVerified"] = false
        };
        void Save()
        {
            string temporary = settings.ReceiptPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, settings.ReceiptPath, true);
        }
        Save();
        if (sessionId == 0)
        {
            receipt["status"] = "failed"; receipt["exitCode"] = 1;
            receipt["error"] = "Interactive mode requires the logged-in user's session, not service session 0.";
            Save(); return 1;
        }
        using var input = Connect(settings, "in", PipeDirection.In);
        using var output = Connect(settings, "out", PipeDirection.Out);
        using var errors = Connect(settings, "err", PipeDirection.Out);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        Process? host = null;
        try
        {
            await Task.WhenAll(input.ConnectAsync(startup.Token), output.ConnectAsync(startup.Token), errors.ConnectAsync(startup.Token));
            var start = new ProcessStartInfo(Environment.ProcessPath!) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!
            };
            foreach (string argument in settings.HostArguments) start.ArgumentList.Add(argument);
            host = Process.Start(start) ?? throw new IOException("Could not start the owned MCP host.");
            receipt["hostProcessId"] = host.Id; receipt["status"] = "running"; Save();
            var send = SendInputAsync(input, host.StandardInput.BaseStream);
            var stdout = DrainOutputAsync(host.StandardOutput.BaseStream, output);
            var stderr = DrainOutputAsync(host.StandardError.BaseStream, errors);
            var exited = host.WaitForExitAsync();
            await Task.WhenAny(exited, send);
            if (!exited.IsCompleted)
            {
                // EOF or SSH loss closes host stdin. Allow the native operation
                // to settle within the configured bound; never replay it.
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(settings.ShutdownTimeoutSeconds));
                try { await exited.WaitAsync(shutdown.Token); }
                catch (OperationCanceledException)
                {
                    receipt["forcedOwnedHostStop"] = true;
                    receipt["outcome"] = "unknown; consult the durable change journal; no automatic retry";
                    host.Kill(entireProcessTree: false);
                    await host.WaitForExitAsync();
                }
            }
            input.Dispose();
            await Task.WhenAll(stdout, stderr);
            _ = send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            receipt["exitCode"] = host.ExitCode;
            receipt["status"] = host.ExitCode == 0 ? "exited" : "failed";
            return host.ExitCode;
        }
        catch (Exception error)
        {
            receipt["status"] = "failed"; receipt["exitCode"] = 1; receipt["error"] = error.Message;
            if (host is not null && !host.HasExited) { host.Kill(entireProcessTree: false); await host.WaitForExitAsync(); receipt["forcedOwnedHostStop"] = true; }
            return 1;
        }
        finally
        {
            receipt["finishedAtUtc"] = DateTimeOffset.UtcNow;
            host?.Dispose();
            // SSH termination can prevent the outer launcher's finally block
            // from running. Remove only our uniquely named/described task here.
            try
            {
                string script = "$t=Get-ScheduledTask -TaskName 'TiaMcp-Session-" + settings.Channel + "' -ErrorAction SilentlyContinue; " +
                    "if($t -and $t.Description -ceq 'TIA MCP session relay " + settings.Channel + "') { Unregister-ScheduledTask -TaskName $t.TaskName -Confirm:$false -ErrorAction Stop }";
                var cleanupStart = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) cleanupStart.ArgumentList.Add(value);
                using var cleanup = Process.Start(cleanupStart)!;
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await cleanup.WaitForExitAsync(bound.Token); receipt["taskCleanupExitCode"] = cleanup.ExitCode; }
                catch (OperationCanceledException) { cleanup.Kill(); receipt["taskCleanupError"] = "Owned task cleanup timed out; task is inert and must be removed explicitly."; }
            }
            catch (Exception error) { receipt["taskCleanupError"] = error.Message; }
            Save();
        }
    }
}
