using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Host;

internal static partial class RuntimeCaptureSoakChecks
{
    private static async Task WaitForAcquisition(TimeSpan target, Func<TimeSpan> elapsed, Func<TimeSpan, Task> delay)
    {
        while (true)
        {
            var remaining = target - elapsed();
            if (remaining <= TimeSpan.Zero) return;
            await delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))));
        }
    }

    internal static async Task<int> CheckAcquisitionWaitBoundaries()
    {
        var elapsed = TimeSpan.Zero;
        var waits = new List<double>();
        var observations = new Queue<TimeSpan>(new[] { TimeSpan.FromMilliseconds(59999.2), TimeSpan.FromMilliseconds(59999.8), TimeSpan.FromSeconds(60) });
        await WaitForAcquisition(TimeSpan.FromSeconds(60), () => elapsed, delay =>
        {
            waits.Add(delay.TotalMilliseconds);
            elapsed = observations.Dequeue();
            return Task.CompletedTask;
        });
        if (!waits.SequenceEqual(new[] { 60000d, 1d, 1d }) || elapsed != TimeSpan.FromSeconds(60) || observations.Count != 0)
            throw new Exception("Early acquisition timers must rewait to the measured deadline with positive ceiling waits.");
        await WaitForAcquisition(TimeSpan.FromSeconds(60), () => TimeSpan.FromSeconds(60),
            _ => throw new Exception("An exact acquisition deadline must not wait again."));
        await WaitForAcquisition(TimeSpan.FromSeconds(60), () => TimeSpan.FromMilliseconds(60000.1),
            _ => throw new Exception("An elapsed acquisition deadline must not wait again."));
        try
        {
            await WaitForAcquisition(TimeSpan.FromSeconds(60), () => TimeSpan.Zero,
                _ => Task.FromCanceled(new CancellationToken(true)));
            throw new Exception("Acquisition wait failures must not become a successful elapsed interval.");
        }
        catch (OperationCanceledException) { }
        return 4;
    }

    internal static async Task RunLong(string root, int epochs, int seconds)
    {
        if (!OperatingSystem.IsWindows() || epochs is < 2 or > 15 || seconds is < 30 or > 120 || epochs * seconds > 900)
            throw new ArgumentException("Long qualification requires 2..15 bounded 30..120s epochs, at most 900s of acquisition.");
        NewRoot(root);
        const long maximumEvidenceBytes = 1L << 30;
        const long minimumInitialFreeBytes = 3L << 30;
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        long initialFree = drive.AvailableFreeSpace;
        if (initialFree < minimumInitialFreeBytes) throw new IOException("Long qualification requires 3 GiB free before admission.");
        var watch = Stopwatch.StartNew();
        var rows = new List<object>();
        var retained = new List<(string Root, Dictionary<string,string> Files)>();
        var released = new List<ResourceSample>();
        var checks = new List<string>();
        double totalActive = 0;
        using var currentProcess = Process.GetCurrentProcess();
        var processStartedUtc = new DateTimeOffset(currentProcess.StartTime.ToUniversalTime());
        Write(Path.Combine(root, "long-intent.json"), new
        {
            epochs, seconds, processId = Environment.ProcessId, startedUtc = DateTimeOffset.UtcNow,
            processStartedUtc,
            timestamp = Stopwatch.GetTimestamp(), timestampFrequency = Stopwatch.Frequency,
            maximumEvidenceBytes, minimumInitialFreeBytes, initialFree,
            crossEpochHandleAllowance = 32, crossEpochManagedAllowance = 64L << 20,
            crossEpochPrivateAllowance = 384L << 20, nativeExecuted = false
        });
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        try
        {
            for (int index = 0; index < epochs; index++)
            {
                string name = $"epoch-{index + 1:00}", epochRoot = Path.Combine(root, name);
                long started = Stopwatch.GetTimestamp();
                DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
                await Run(epochRoot, seconds);
                using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(epochRoot, "soak-checks.json")));
                totalActive += summary.RootElement.GetProperty("activeSeconds").GetDouble();
                var currentFiles = Hashes(Path.Combine(epochRoot, "store"));
                retained.Add((epochRoot, currentFiles));
                int priorCaptureCount = 0;
                int replayReads = 0;
                var replayProofs = new List<object>();
                foreach (var previous in retained)
                {
                    string storeRoot = Path.Combine(previous.Root, "store");
                    var beforeFiles = Hashes(storeRoot);
                    Check(SameHashes(previous.Files, beforeFiles), "Exact earlier committed prefix bytes remain unchanged: " + Path.GetFileName(previous.Root));
                    var scenario = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(previous.Root, "scenario.json")), BridgeProtocol.Json)!;
                    var healthy = JsonSerializer.Deserialize<CaptureCase>(File.ReadAllText(Path.Combine(previous.Root, "healthy.json")), BridgeProtocol.Json)!;
                    var sessions = new PhysicalRuntimeSessions(TimeProvider.System, []);
                    int readsBefore = sessions.Reads.Count;
                    await using (var captures = new RuntimeCaptures(sessions, new("", storeRoot, null, 5, false, "", null), TimeProvider.System))
                    {
                        foreach (var original in scenario.Captures.Append(healthy))
                        {
                            var observed = Element(await captures.Start(original.CaptureId, original.Request, CancellationToken.None));
                            Check(observed.GetProperty("state").GetString() == "ended", "Earlier terminal intent stays ended: " + original.CaptureId);
                            priorCaptureCount++;
                        }
                        await Task.Delay(25);
                        Check(sessions.Reads.Count == 0, "Repeated earlier intents perform zero acquisition: " + Path.GetFileName(previous.Root));
                    }
                    int readsAfter = sessions.Reads.Count;
                    replayReads += readsAfter - readsBefore;
                    var afterFiles = Hashes(storeRoot);
                    Check(readsAfter == readsBefore && SameHashes(previous.Files, afterFiles), "Reopening earlier intents cannot rewrite original prefixes: " + Path.GetFileName(previous.Root));
                    replayProofs.Add(new
                    {
                        directory = Path.GetFileName(previous.Root),
                        captureIds = scenario.Captures.Append(healthy).Select(c => c.CaptureId).ToArray(),
                        readsBefore, readsAfter, beforeFiles, afterFiles
                    });
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var sample = Measure("epochReleased", watch);
                released.Add(sample);
                var warm = released[0];
                Check(sample.HandleCount <= warm.HandleCount + 32, "Cross-epoch handles stay within the fixed post-warmup allowance");
                Check(sample.ManagedBytes <= warm.ManagedBytes + (64L << 20), "Cross-epoch managed memory stays within the fixed post-warmup allowance");
                Check(sample.PrivateBytes <= warm.PrivateBytes + (384L << 20), "Cross-epoch private bytes stay within the fixed post-warmup allowance");
                long evidenceBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
                Check(evidenceBytes <= maximumEvidenceBytes, "Long-run retained evidence remains below the fixed 1 GiB budget");
                var row = new
                {
                    index, directory = name, processId = Environment.ProcessId, started, completed = Stopwatch.GetTimestamp(),
                    startedUtc, completedUtc = DateTimeOffset.UtcNow,
                    activeSeconds = summary.RootElement.GetProperty("activeSeconds").GetDouble(),
                    priorCaptureCount, repeatedIntentReads = replayReads, replayProofs,
                    originalFiles = currentFiles, released = sample, evidenceBytes
                };
                rows.Add(row);
                Write(Path.Combine(root, name + "-receipt.json"), row);
                Console.WriteLine($"PASS epoch {index + 1}/{epochs}: {seconds}s loaded acquisition; {priorCaptureCount} earlier intents revalidated.");
                Console.Out.Flush();
            }
            Check(totalActive >= epochs * seconds, "Total measured acquisition meets the requested epoch budget");
            Write(Path.Combine(root, "long-checks.json"), new
            {
                schemaVersion = "runtime-long-soak.v1", processId = Environment.ProcessId, processStartedUtc, epochs, seconds,
                totalActiveSeconds = totalActive, elapsedSeconds = watch.Elapsed.TotalSeconds, checks, rounds = rows,
                released, qualifiesTenMinuteAcquisition = totalActive >= 600, productionPoller = true,
                clockBoundary = "system", sessionBoundary = "testDouble",
                separateStoresPerEpoch = true, systemResourcesExhausted = false, actualDiskFull = false, nativeExecuted = false
            });
        }
        finally
        {
            Write(Path.Combine(root, "long-observations.json"), new
            {
                processId = Environment.ProcessId, elapsedSeconds = watch.Elapsed.TotalSeconds,
                completedEpochs = rows.Count, totalActiveSeconds = totalActive, rounds = rows, released
            });
        }
        Check(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) <= maximumEvidenceBytes,
            "Final long-run summaries and observations remain within the fixed 1 GiB evidence budget");
    }
}
