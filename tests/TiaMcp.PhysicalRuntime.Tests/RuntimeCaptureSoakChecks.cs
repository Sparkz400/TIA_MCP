using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.PhysicalRuntime;
using TiaMcp.Host;

// Opt-in real-clock qualification. Only the authenticated session is a test double.
internal static partial class RuntimeCaptureSoakChecks
{
    internal static async Task Run(string root, int seconds)
    {
        if (!OperatingSystem.IsWindows() || seconds is < 30 or > 120)
            throw new ArgumentException("Windows resource soak requires 30..120 real seconds.");
        NewRoot(root);
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        var watch = Stopwatch.StartNew();
        await using var fixture = new Fixture(root, 4, 16);
        var captures = Enumerable.Range(0, 4).Select(i => fixture.Case(i, seconds + 30, 100000)).ToArray();
        Write(Path.Combine(root, "scenario.json"), new { captures, clock = "TimeProvider.System", seconds, sessionBoundary = "testDouble" });
        var samples = new List<ResourceSample>();
        var baseline = Measure("baseline", watch);
        samples.Add(baseline);
        var resources = new OwnedLoad();
        var loaded = Measure("loadHeld", watch);
        samples.Add(loaded);
        Check(loaded.ManagedBytes - baseline.ManagedBytes >= 60L << 20, "The owned 64 MiB allocation is resident in the managed heap");
        Check(loaded.HandleCount - baseline.HandleCount >= 256, "256 owned OS wait handles are actually open");
        using var sampleStop = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, sampleStop.Token);
                    lock (samples) samples.Add(Measure("during", watch));
                }
            }
            catch (OperationCanceledException) when (sampleStop.IsCancellationRequested) { }
        });
        try
        {
            foreach (var capture in captures)
                await fixture.Captures.Start(capture.CaptureId, capture.Request, CancellationToken.None);
            string refusedId = Guid.NewGuid().ToString("N");
            try
            {
                await fixture.Captures.Start(refusedId, fixture.Case(4, seconds + 30, 1).Request, CancellationToken.None);
                throw new Exception("Fifth concurrent worker was admitted");
            }
            catch (BridgeException error) when (error.Code == "RuntimeCaptureSessionBusy")
            {
                Check(!Directory.Exists(Path.Combine(fixture.StoreRoot, "runtime-captures", refusedId)),
                    "Actual worker saturation refuses a fifth worker before durable intent");
            }
            var active = Stopwatch.StartNew();
            await WaitForAcquisition(TimeSpan.FromSeconds(seconds), () => active.Elapsed, delay => Task.Delay(delay));
            double activeSeconds = active.Elapsed.TotalSeconds;
            Check(activeSeconds >= seconds, "The loaded acquisition interval meets its real wall-clock lower bound");
            fixture.Block = true;
            await fixture.AllBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancellation = Stopwatch.StartNew();
            await Task.WhenAll(captures.Select(c => fixture.Captures.Stop(c.CaptureId, CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(10));
            double cancellationMilliseconds = cancellation.Elapsed.TotalMilliseconds;
            var statuses = new List<JsonElement>();
            foreach (var capture in captures)
            {
                var status = await fixture.Status(capture.CaptureId);
                statuses.Add(status);
                var progress = status.GetProperty("recordedProgress");
                Check(status.GetProperty("state").GetString() == "ended" && progress.GetProperty("terminalReason").GetString() == "stopped",
                    "In-flight cancellation commits stopped terminal state for " + capture.CaptureId);
                Check(progress.GetProperty("readAttempts").GetInt64() > 256 && progress.GetProperty("skippedPolls").GetInt64() > 0 &&
                      progress.GetProperty("droppedReadResults").GetInt64() == 0 && status.GetProperty("localFailure").ValueKind == JsonValueKind.Null,
                    "Sustained acquisition commits multiple buffers, gaps and every accepted result for " + capture.CaptureId);
                var page = Element(fixture.Captures.Page(status.GetProperty("snapshotRef").GetString()!,
                    checked((int)progress.GetProperty("rows").GetInt64() - 2), 2));
                Check(page.GetProperty("rows").EnumerateArray().Any(row => row.GetProperty("errorCode").GetString() == "RuntimeCancelled"),
                    "The blocked read retains explicit cancellation for " + capture.CaptureId);
                await fixture.Export(capture.CaptureId, "export-" + capture.CaptureId + ".json");
            }
            var prefix = Hashes(fixture.StoreRoot);
            int readCount = fixture.ReadCount;
            foreach (var capture in captures) await fixture.Captures.Start(capture.CaptureId, capture.Request, CancellationToken.None);
            await Task.Delay(250);
            Check(fixture.ReadCount == readCount && SameHashes(prefix, Hashes(fixture.StoreRoot)),
                "Repeated stopped intents neither replay reads nor rewrite committed files");
            fixture.Block = false;
            var healthy = fixture.Case(4, 5, 16);
            await fixture.Captures.Start(healthy.CaptureId, healthy.Request, CancellationToken.None);
            var healthyStatus = await fixture.Ended(healthy.CaptureId);
            Check(healthyStatus.GetProperty("recordedProgress").GetProperty("terminalReason").GetString() == "sampleLimit",
                "Capacity is released for a healthy capture in the same loaded process");
            fixture.FlushLedger();
            Write(Path.Combine(root, "healthy.json"), healthy);
            Write(Path.Combine(root, "statuses.json"), statuses);
            Write(Path.Combine(root, "healthy-status.json"), healthyStatus);
            await fixture.Export(healthy.CaptureId, "export-healthy.json");
            await fixture.Captures.DisposeAsync();
            fixture.CapturesDisposed = true;
            using (var reopened = new RuntimeCaptureStore(fixture.StoreRoot))
                Check(captures.All(c => Element(reopened.Status(c.CaptureId, false)).GetProperty("state").GetString() == "ended"),
                    "All cancelled captures reopen without changing their terminal state");
            await resources.DisposeAsync();
            sampleStop.Cancel();
            await sampling;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var released = Measure("resourcesReleased", watch);
            lock (samples) samples.Add(released);
            Check(loaded.HandleCount - released.HandleCount >= 200 && released.HandleCount <= baseline.HandleCount + 64,
                "Owned handle pressure is released without persistent handle growth beyond the stated 64-handle allowance");
            Check(released.ManagedBytes < loaded.ManagedBytes - (32L << 20),
                "The touched allocation is reclaimable after release");
            Check(samples.Max(s => s.PrivateBytes) < baseline.PrivateBytes + (384L << 20),
                "Measured process private bytes stay within the explicit 384 MiB growth bound");
            Write(Path.Combine(root, "resources.json"), samples);
            Write(Path.Combine(root, "soak-checks.json"), new
            {
                schemaVersion = "runtime-real-clock-soak.v1", checks, seconds, activeSeconds, cancellationMilliseconds,
                readCount = fixture.ReadCount, refusedId, allocatedBytes = 64L << 20, ownedWaitHandles = 256,
                cpuHashIterations = resources.Iterations, timestampFrequency = Stopwatch.Frequency,
                elapsedSeconds = watch.Elapsed.TotalSeconds, productionPoller = true, clockBoundary = "system",
                sessionBoundary = "testDouble", systemResourcesExhausted = false, actualDiskFull = false, nativeExecuted = false
            });
            Console.WriteLine($"PASS real-clock runtime soak: {checks.Count} checks, {activeSeconds:F3}s, {fixture.ReadCount} reads.");
        }
        finally
        {
            sampleStop.Cancel();
            await sampling;
            await resources.DisposeAsync();
            lock (samples) Write(Path.Combine(root, "resource-observations.json"), samples, replace: true);
        }
    }

    internal static async Task Crash(string root)
    {
        NewRoot(root);
        // Environment.Exit deliberately bypasses await-using/Dispose and leaves the store lock to OS process teardown.
        await using var fixture = new Fixture(root, 1, 16);
        var capture = fixture.Case(0, 30, 100000);
        Write(Path.Combine(root, "scenario.json"), new { captures = new[] { capture }, clock = "TimeProvider.System", sessionBoundary = "testDouble" });
        fixture.BeforeOutcome = async number =>
        {
            if (number < 177) return;
            var status = await fixture.Status(capture.CaptureId);
            long committedReads = status.GetProperty("recordedProgress").GetProperty("readAttempts").GetInt64();
            if (committedReads < 128 || committedReads == number - 1) return;
            if (status.GetProperty("recordedProgress").GetProperty("state").GetString() != "running")
                throw new Exception("Expected a running prefix before the controlled process failure");
            fixture.FlushLedger();
            Write(Path.Combine(root, "failure-intent.json"), new
            {
                expectedExitCode = 73, processId = Environment.ProcessId, completedReadOutcomes = number - 1,
                pendingReadNumber = number, snapshotRef = status.GetProperty("snapshotRef").GetString(),
                status, originalFiles = Hashes(fixture.StoreRoot), disposalInvoked = false,
                failure = "Owned test process calls Environment.Exit(73) before the pending read returns"
            });
            Console.Out.Flush(); Console.Error.Flush();
            Environment.Exit(73);
        };
        await fixture.Captures.Start(capture.CaptureId, capture.Request, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(40));
        throw new Exception("Controlled owned-process failure was not reached");
    }

    internal static async Task Recover(string root)
    {
        var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(root, "scenario.json")), BridgeProtocol.Json)!;
        var original = scenario.Captures.Single();
        var plan = JsonSerializer.Deserialize<RuntimeCapturePlan>(File.ReadAllText(Path.Combine(root, "store", "runtime-captures", original.CaptureId, "plan.json")), BridgeProtocol.Json)!;
        var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "failure-intent.json"))).RootElement;
        var before = Hashes(Path.Combine(root, "store"));
        await using var fixture = new Fixture(root, plan.Signals);
        var interrupted = await fixture.Status(original.CaptureId);
        if (interrupted.GetProperty("state").GetString() != "interrupted" || !interrupted.GetProperty("uncommittedDataUnknown").GetBoolean() ||
            interrupted.GetProperty("snapshotRef").GetString() != expected.GetProperty("snapshotRef").GetString())
            throw new Exception("Process restart must reopen the original running prefix as interrupted with unknown uncommitted loss");
        await fixture.Captures.Start(original.CaptureId, original.Request, CancellationToken.None);
        await Task.Delay(750);
        if (fixture.ReadCount != 0 || !SameHashes(before, Hashes(fixture.StoreRoot)))
            throw new Exception("Restart must not replay acquisition or alter the original prefix");
        await fixture.Export(original.CaptureId, "export-recovered-prefix.json");
        var healthy = fixture.Case(0, 5, 16);
        await fixture.Captures.Start(healthy.CaptureId, healthy.Request, CancellationToken.None);
        var healthyStatus = await fixture.Ended(healthy.CaptureId);
        if (healthyStatus.GetProperty("recordedProgress").GetProperty("terminalReason").GetString() != "sampleLimit" || fixture.ReadCount != 16)
            throw new Exception("A fresh capture must acquire exactly 16 reads after the crashed owner's store lock is released");
        await fixture.Export(healthy.CaptureId, "export-recovery-healthy.json");
        fixture.FlushLedger();
        Write(Path.Combine(root, "recovery.json"), new
        {
            schemaVersion = "runtime-soak-recovery.v1", processId = Environment.ProcessId, original, interrupted,
            repeatedIntentReads = 0, originalPrefixUnchanged = true, healthy, healthyStatus, reads = fixture.ReadCount,
            acquisitionResumed = false, uncommittedRowsRecovered = false, nativeExecuted = false
        });
        Console.WriteLine("PASS process-restart recovery: interrupted committed prefix, no replay, healthy 16-read capture.");
    }

    internal sealed record CaptureCase(string CaptureId, RuntimeCaptureRequest Request);
    private sealed record Scenario(CaptureCase[] Captures);
    private sealed record ResourceSample(string Phase, double ElapsedSeconds, DateTimeOffset AtUtc, long ManagedBytes,
        long HeapBytes, long WorkingSetBytes, long PrivateBytes, int HandleCount, int ThreadCount, double ProcessorSeconds);

    private static ResourceSample Measure(string phase, Stopwatch elapsed)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(phase, elapsed.Elapsed.TotalSeconds, DateTimeOffset.UtcNow, GC.GetTotalMemory(false),
            GC.GetGCMemoryInfo().HeapSizeBytes, process.WorkingSet64, process.PrivateMemorySize64,
            process.HandleCount, process.Threads.Count, process.TotalProcessorTime.TotalSeconds);
    }

    private sealed class OwnedLoad : IAsyncDisposable
    {
        private byte[]? bytes = GC.AllocateUninitializedArray<byte>(64 << 20);
        private readonly EventWaitHandle[] handles = Enumerable.Range(0, 256).Select(_ => new EventWaitHandle(false, EventResetMode.AutoReset)).ToArray();
        private readonly CancellationTokenSource stop = new();
        private readonly Task cpu;
        private bool disposed;
        internal long Iterations;
        internal OwnedLoad()
        {
            Array.Fill(bytes!, (byte)0x5a);
            cpu = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        _ = SHA256.HashData(bytes!.AsSpan(checked((int)(Iterations % 64)) << 20, 1 << 20));
                        Interlocked.Increment(ref Iterations);
                        await Task.Delay(20, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true; stop.Cancel(); await cpu;
            foreach (var handle in handles) handle.Dispose();
            bytes = null; stop.Dispose();
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root, StoreRoot;
        internal readonly PhysicalRuntimeSessions Sessions;
        internal readonly RuntimeCaptures Captures;
        internal bool CapturesDisposed;
        internal volatile bool Block;
        internal Func<int, Task>? BeforeOutcome;
        internal readonly TaskCompletionSource AllBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<string> blocked = [];
        private readonly StreamWriter ledger;
        private readonly object ledgerGate = new();
        private readonly int workers;
        internal int ReadCount { get { lock (Sessions.Reads) return Sessions.Reads.Count; } }

        internal Fixture(string root, int workers, int signalCount) : this(root, Signals(workers, signalCount), workers) { }
        internal Fixture(string root, RuntimeCaptureSignal[] signals, int workers = 1)
        {
            Root = root; StoreRoot = Path.Combine(root, "store"); this.workers = workers;
            ledger = new StreamWriter(new FileStream(Path.Combine(root, File.Exists(Path.Combine(root, "failure-intent.json")) ?
                "recovery-ledger.jsonl" : "acquisition-ledger.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Sessions = new(TimeProvider.System, signals);
            Captures = new(Sessions, new("", StoreRoot, null, 5, false, "", null), TimeProvider.System);
            Sessions.ResponseMember = Response;
            Sessions.BeforeRead = async (number, token) =>
            {
                string selectionId; long started;
                lock (Sessions.Reads) (selectionId, started) = Sessions.Reads[number - 1];
                string? error = null;
                if (BeforeOutcome is not null) await BeforeOutcome(number);
                try
                {
                    if (Block)
                    {
                        lock (blocked)
                        {
                            blocked.Add(signals.Single(s => s.SelectionId == selectionId).Selection!.Context.ConnectionId);
                            if (blocked.Count == this.workers) AllBlocked.TrySetResult();
                        }
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    await Task.Delay(number % 37 == 0 ? 145 : 2, token);
                    error = number % 17 == 0 ? "RuntimeTimeout" : number % 29 == 0 ? "RuntimeTransportFailure" : null;
                    if (error is not null) throw new BridgeException(error, "Explicit bounded test-session outcome");
                }
                catch (OperationCanceledException) { error = "RuntimeCancelled"; throw; }
                finally
                {
                    lock (ledgerGate) ledger.WriteLine(JsonSerializer.Serialize(new
                    {
                        number, selectionId, started, completed = Stopwatch.GetTimestamp(), error,
                        responseMember = error is null ? Response(number) : null
                    }, BridgeProtocol.Json));
                }
            };
        }
        private static RuntimeCaptureSignal[] Signals(int workers, int count)
        {
            return Enumerable.Range(0, workers + 1).SelectMany(worker =>
            {
                var context = WebRuntimeProtocol.Context("https://soak.example", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), new string('a', 64));
                return Enumerable.Range(0, worker == workers ? 1 : count).Select(index =>
                {
                    var metadata = JsonSerializer.SerializeToElement(new { name = "Value" + index, datatype = "variant" });
                    return new RuntimeCaptureSignal(Guid.NewGuid().ToString("N"),
                        WebRuntimeProtocol.Symbol(context, "\"DB\".Value" + index, "simple", metadata), metadata);
                });
            }).ToArray();
        }
        internal CaptureCase Case(int worker, int duration, int maximumSamples)
        {
            var selected = Sessions.Signals.GroupBy(s => s.Selection!.Context.ConnectionId).ElementAt(worker).ToArray();
            return new(Guid.NewGuid().ToString("N"), new(selected[0].Selection!.Context.ConnectionId,
                selected.Select(s => s.SelectionId).ToArray(), 100, duration, maximumSamples, 16L << 20));
        }
        internal async Task<JsonElement> Status(string id) => Element(await Captures.Status(id, CancellationToken.None));
        internal async Task<JsonElement> Ended(string id)
        {
            async Task<JsonElement> Wait()
            {
                while (true)
                {
                    var status = await Status(id);
                    if (status.GetProperty("state").GetString() != "running") return status;
                    await Task.Delay(10);
                }
            }
            return await Wait().WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal async Task Export(string id, string receipt)
        {
            var status = await Status(id);
            Write(Path.Combine(Root, receipt), await Captures.Export(status.GetProperty("snapshotRef").GetString()!, "json", CancellationToken.None));
        }
        internal void FlushLedger() { lock (ledgerGate) { ledger.Flush(); ((FileStream)ledger.BaseStream).Flush(true); } }
        public async ValueTask DisposeAsync()
        {
            if (!CapturesDisposed) { CapturesDisposed = true; await Captures.DisposeAsync(); }
            ledger.Dispose();
        }
    }

    private static string Response(int number) => number % 31 == 0 ? "\"error\":{\"code\":201,\"message\":\"not retained\"}" :
        "\"result\":" + ((number % 6) switch
        {
            0 => "18446744073709551615", 1 => "-9223372036854775808", 2 => "1.234567890123456789e-100",
            3 => "{\"nested\":[true,null,\"text\",42]}", 4 => "\"" + new string('x', 1024) + "\"", _ => "false"
        });
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, BridgeProtocol.Json);
    private static void NewRoot(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new ArgumentException("A new evidence directory is required");
        Directory.CreateDirectory(root);
    }
    private static void Write(string path, object value, bool replace = false)
    {
        using var stream = new FileStream(path, replace ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, value, BridgeProtocol.Json);
        stream.Flush(true);
    }
    private static Dictionary<string, string> Hashes(string root) => Directory.EnumerateFiles(Path.Combine(root, "runtime-captures"), "*", SearchOption.AllDirectories)
        .Where(p => Path.GetExtension(p) == ".segment" || Path.GetFileName(p) is "plan.json" or "head.json")
        .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());
    private static bool SameHashes(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var hash) && pair.Value == hash);
}
