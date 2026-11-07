using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TiaMcp.Core;

public sealed record BridgeOptions(string WorkerPath, string? ApiDirectory = null, int TimeoutSeconds = 120,
    IReadOnlyList<string>? WorkerPrefixArguments = null, bool Disabled = false);

public interface IBridgeClient
{
    Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default);
}

/// <summary>A response that arrived after its caller cancelled or timed out; the worker finished the request on its own.</summary>
public sealed record LateBridgeResponse(string Operation, string RequestId, bool Succeeded, string? ErrorCode, DateTimeOffset ReceivedAt);

/// <summary>One serialized engineering session. A cancelled or timed-out request keeps running in the worker and is never
/// replayed: later calls are refused until its response arrives, which is then recorded. A broken transport is terminal.</summary>
public sealed class BridgeClient(BridgeOptions options) : IBridgeClient, IAsyncDisposable
{
    public const int WorkerExitBudgetSeconds = 120;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeServerStream? _pipe;
    private BoundedLineReader? _reader;
    private Process? _worker;
    private bool _faulted;
    private bool _disposed;
    private Task<string>? _inflight;
    private string? _inflightOperation, _inflightId;
    private readonly List<LateBridgeResponse> _late = [];
    public IReadOnlyList<LateBridgeResponse> LateResponses { get { lock (_late) return _late.ToArray(); } }
    public bool? WorkerShutdownSucceeded { get; private set; }

    public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("Operation is required.", nameof(operation));
        var id = Guid.NewGuid().ToString("N");
        var frame = BridgeProtocol.EncodeRequest(id, operation, arguments);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (options.Disabled) throw new BridgeException("NativeWorkersDisabled", "This Host disables Siemens engineering and simulator workers. Use supplied-document tools, or use an engineering package on a Siemens-equipped workstation.");
            if (_faulted) throw new BridgeException("BridgeFaulted", "The previous operation lost its connection or timed out. Its outcome may be unknown. Inspect TIA and restart this MCP host before continuing; no request was retried.");
            await SettleInterruptedAsync();
            // Owner discovery observes an existing connection; it must not create one.
            if (operation == "addin_owner_context" && _pipe is null)
                throw new BridgeException("AddInOwnerUnavailable", "No connected worker owns this Host session. Owner observation never starts or attaches a worker.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            bool sent = false, interrupted = false;
            try
            {
                await EnsureConnectedAsync(deadline.Token);
                var bytes = Encoding.UTF8.GetBytes(frame + "\n");
                sent = true; // From here the worker may have received the request.
                await _pipe!.WriteAsync(bytes, deadline.Token);
                await _pipe.FlushAsync(deadline.Token);
                // The read itself is never cancelled: a cancelled pipe read could drop part of a frame.
                var read = _reader!.ReadAsync(CancellationToken.None);
                if (await Task.WhenAny(read, Task.Delay(Timeout.Infinite, deadline.Token)) != read)
                {
                    // Do not kill an engineering operation in progress. Its response is collected by a later call.
                    _inflight = read; _inflightOperation = operation; _inflightId = id; interrupted = true;
                    throw new BridgeException(cancellationToken.IsCancellationRequested ? "CancelledOutcomeUnknown" : "TimeoutOutcomeUnknown",
                        "The bridge call was interrupted. It may still be running in TIA and will not be retried automatically. " +
                        "Later calls are refused with PreviousOperationRunning until it finishes; then inspect its outcome (for a change, change_status).");
                }
                return BridgeProtocol.DecodeResponse(await read, id);
            }
            catch (BridgeOperationException) { throw; }
            catch (BridgeException) when (interrupted) { throw; }
            catch (Exception ex)
            {
                // Before the frame is written nothing can run in TIA: drop only this connection; the next call starts a worker.
                _faulted = sent;
                if (_pipe is not null) await _pipe.DisposeAsync();
                _pipe = null;
                if (ex is OperationCanceledException && !sent)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    throw new BridgeException("WorkerConnectTimeout", "The worker did not connect within the bridge timeout; no request was sent.", ex);
                }
                // Do not kill an engineering operation in progress. The worker observes EOF after its call returns.
                if (ex is OperationCanceledException)
                    throw new BridgeException(cancellationToken.IsCancellationRequested ? "CancelledOutcomeUnknown" : "TimeoutOutcomeUnknown",
                        "The bridge call was interrupted. It may still be running in TIA and will not be retried automatically.", ex);
                if (ex is BridgeException) throw;
                throw new BridgeException("WorkerDisconnected", "The TIA worker connection failed: " + ex.Message, ex);
            }
        }
        finally { _gate.Release(); }
    }

    // Under the gate: refuse while an interrupted request still runs; once it ended, record its response or fault the bridge.
    private async Task SettleInterruptedAsync()
    {
        if (_inflight is null) return;
        if (!_inflight.IsCompleted)
            throw new BridgeException("PreviousOperationRunning", $"The interrupted {_inflightOperation} request is still running in the worker; nothing was sent. " +
                "Try again after it finishes. It is never replayed.");
        var finished = _inflight; _inflight = null;
        try
        {
            string response = await finished;
            bool succeeded = true; string? code = null;
            try { BridgeProtocol.DecodeResponse(response, _inflightId!); }
            catch (BridgeOperationException error) { succeeded = false; code = error.Code; }
            lock (_late) _late.Add(new LateBridgeResponse(_inflightOperation!, _inflightId!, succeeded, code, DateTimeOffset.UtcNow));
            Console.Error.WriteLine(JsonSerializer.Serialize(new { bridgeLateResponse = new { operation = _inflightOperation, requestId = _inflightId, succeeded, errorCode = code } }));
        }
        catch (Exception ex) when (ex is not BridgeOperationException)
        {
            _faulted = true;
            if (_pipe is not null) await _pipe.DisposeAsync();
            _pipe = null;
            throw new BridgeException("BridgeFaulted", "The interrupted operation lost its connection before responding. Its outcome may be unknown. Inspect TIA and restart this MCP host before continuing; no request was sent.", ex);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is not null) { VerifyWorkerPeer(); return; }
        if (!OperatingSystem.IsWindows())
            throw new BridgeException("WorkerPlatformUnsupported", "Native worker process verification requires Windows; no worker was started.");
        if (!File.Exists(options.WorkerPath))
            throw new BridgeException("WorkerNotFound", "Build the V21 worker or set --worker to its executable: " + options.WorkerPath);
        var pipeName = "TiaMcp-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(options.WorkerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.WorkerPath))!
        };
        foreach (var argument in options.WorkerPrefixArguments ?? []) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        if (options.ApiDirectory is not null)
        {
            start.ArgumentList.Add("--api-dir");
            start.ArgumentList.Add(options.ApiDirectory);
        }
        // A previous worker never received a request on its dropped pipe; it exits on its own, so only the handle is released.
        _worker?.Dispose(); _worker = null;
        _worker = Process.Start(start) ?? throw new BridgeException("WorkerStartFailed", "Could not launch the V21 worker.");
        // Retain the launched process handle; never reopen a possibly recycled PID.
        _ = _worker.SafeHandle;
        _worker.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[V21] " + e.Data); };
        _worker.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[V21 stdout] " + e.Data); };
        _worker.BeginErrorReadLine();
        _worker.BeginOutputReadLine();
        var connected = _pipe.WaitForConnectionAsync(cancellationToken);
        var exited = _worker.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(connected, exited);
        if (completed == exited && !connected.IsCompletedSuccessfully)
        {
            await exited;
            throw new BridgeException("WorkerExited", $"The V21 worker exited with code {_worker.ExitCode} before connecting.");
        }
        await connected;
        VerifyWorkerPeer();
        _reader = new BoundedLineReader(_pipe, BridgeProtocol.MaxResponseBytes);
    }

    private void VerifyWorkerPeer()
    {
        if (_worker!.HasExited)
            throw new BridgeException("WorkerExited", "The launched worker has exited; no request was sent.");
        if (!GetNamedPipeClientProcessId(_pipe!.SafePipeHandle, out uint peerId))
            throw new BridgeException("WorkerPeerVerificationFailed", "Cannot verify the connected worker process; no request was sent.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (peerId != _worker.Id)
            throw new BridgeException("WorkerPeerMismatch", "The connected pipe peer is not the launched worker; no request was sent.");
        if (_worker.HasExited)
            throw new BridgeException("WorkerExited", "The launched worker has exited; no request was sent.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            WorkerShutdownSucceeded = _worker is null;
            if (_pipe is not null) await _pipe.DisposeAsync();
            // EOF releases the worker's attachment. Never terminate a TIA process here.
            if (_worker is not null)
            {
                var workerId = _worker.Id;
                var startedAtUtc = _worker.StartTime.ToUniversalTime();
                bool exited = false;
                int? exitCode = null;
                var exitWait = Stopwatch.StartNew();
                try
                {
                    // The worker exits only after its owned Portal closes: 42.2 s measured after a
                    // 1.5 h, 48-block capture, plus PLCSIM manager shutdown of just over ten seconds.
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(WorkerExitBudgetSeconds));
                    await _worker.WaitForExitAsync(deadline.Token);
                    exited = true;
                    exitCode = _worker.ExitCode;
                    WorkerShutdownSucceeded = exitCode == 0;
                }
                catch (OperationCanceledException) { /* Native work may still be in flight; never kill it. */ }
                finally
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { bridgeWorkerExit = new {
                        processId = workerId, startedAtUtc, processExitVerified = exited, exitCode,
                        waitBudgetSeconds = WorkerExitBudgetSeconds, elapsedMs = exitWait.ElapsedMilliseconds,
                        forcedTermination = false, projectLockReleaseVerified = false } }, BridgeProtocol.Json));
                    _worker.Dispose();
                }
            }
        }
        finally { _gate.Release(); }
    }
}
