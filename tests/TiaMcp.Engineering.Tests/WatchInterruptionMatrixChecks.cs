using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class WatchInterruptionMatrixChecks
{
    internal static void Child(string[] args)
    {
        var store = new EngineeringOperationStore(args[1]); var r = store.Read(args[2]);
        if (args[3] == "contend")
        {
            try { store.Acquire(r.Id, r.Revision); Console.WriteLine("unexpected-acquisition"); }
            catch (BridgeException e) { Console.WriteLine(e.Code); }
            return;
        }
        r = store.Acquire(r.Id, r.Revision);
        if (args[3] == "acquired") Environment.Exit(73); // Lost Host before the lease reply is recorded.
        r = store.BeginCheckpoint(r.Id, r.Revision, () => r.Plan.Target);
        if (args[3] != "checkpointIntent")
        {
            string path = Path.Combine(args[1], "checkpoint.zap21"); File.WriteAllText(path, "Synthetic checkpoint, no native claim");
            r = store.Checkpoint(r.Id, r.Revision, [new("checkpoint.zap21", WatchTableDocument.Hash(File.ReadAllBytes(path)))]);
            if (args[3] != "ready") r = store.Dispatch(r.Id, r.Revision, () => r.Plan.Target);
            if (args[3] == "recoveryIntent")
            {
                r = store.MarkUnknown(r.Id, r.Revision);
                store.ReserveRecovery(r.Id, r.Revision, [new(r.Plan.Resources[0].ResourceKey, Path.Combine(args[1], "recovery"))]);
            }
        }
        Environment.Exit(73); // Deliberately bypass managed catch/finally; no native process exists.
    }

    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
        async Task Reject(Func<Task> call, string code)
        { try { await call(); } catch (BridgeException e) when (e.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
        foreach (string state in new[] { "acquired", "checkpointIntent", "ready", "dispatchIntent", "recoveryIntent" })
        {
            string root = Path.Combine(Path.GetTempPath(), "watch-host-loss-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root); var dispatcher = new WatchTableDispatcher(native, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = "review" }));
            string id = (string)prepared["operation"]!["id"]!; string digest = (string)prepared["operation"]!["planSha256"]!;
            var store = new EngineeringOperationStore(root); var contender = store.Prepare(store.Read(id).Plan, "competing-owner");
            var crashed = await Process(root, id, state);
            Check(crashed.Code == 73 && store.Read(id).State == state, "Abrupt host loss retains " + state);
            Check((await Process(root, contender.Id, "contend")).Output == "ResourceBusy", "Separate owner cannot steal " + state);
            var restarted = new WatchTableDispatcher(native, root);
            await Reject(() => restarted.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = digest }), "RecoveryRequired");
            native.NewInstance();
            var reconciled = Node(await restarted.CallAsync("watch_reconcile_interrupted", new { operationId = id, destinationDisposition = state == "recoveryIntent" ? "verifiedAbsent" : null }));
            Check((string)reconciled["operation"]!["state"]! == "reconciled" && (string)reconciled["originalDisposition"]! == "unchanged", "Fresh baseline resolves " + state);
            Check(native.ApplyCount == 0 && native.RestoreCount == 0 && native.CheckpointCount == 0, "No mutation replay at " + state);
            Check((await Process(root, contender.Id, "contend")).Output == "unexpected-acquisition", "Ownership released only after reconciliation at " + state);
            contender = store.Read(contender.Id); store.Stop(contender.Id, contender.Revision);
        }
        // A successful retrieval can lose its response. Locate retained output, then insist on another reopen.
        {
            string root = Path.Combine(Path.GetTempPath(), "watch-lost-retrieve-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root); var dispatcher = new WatchTableDispatcher(native, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = "review" }));
            string id = (string)prepared["operation"]!["id"]!;
            await dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = (string)prepared["operation"]!["planSha256"]! });
            native.LoseRestoreResponse = true;
            try { await dispatcher.CallAsync("watch_recover", new { operationId = id }); throw new Exception("Expected lost retrieval"); } catch (IOException) { passed++; }
            var store = new EngineeringOperationStore(root); var r = store.Read(id);
            Check(r.State == "outcomeUnknown" && r.Evidence.Any(e => e.Gate.StartsWith("interruption.")), "Lost retrieval diagnostics are bound in durable chain");
            var restarted = new WatchTableDispatcher(native, root);
            await Reject(() => restarted.CallAsync("watch_recover", new { operationId = id }), "RecoveryRequired");
            await Reject(() => restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" }), "RecoveryObservationRequired");
            var located = Node(await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" }));
            Check((bool)located["located"]! && store.Pending().Count == 1, "Locating output retains ownership");
            await Reject(() => restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" }), "FreshInstanceRequired");
            native.NewInstance(); await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
            native.SelectOriginal(); await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" });
            r = store.Read(id);
            Check(r.State == "reconciled" && r.RecoveryResolutions.Single().Disposition == "verifiedRestored" && native.RestoreCount == 1,
                "Lost receipt resolved through fresh original/restored evidence without repeated retrieval");
            var failure = r.Evidence.Single(e => e.Gate.StartsWith("interruption.")).Artifact;
            File.AppendAllText(Path.Combine(root, failure.RelativePath), "tampered");
            try { store.Read(id); throw new Exception("Missing diagnostic integrity check"); } catch (BridgeException e) when (e.Code == "ArtifactChanged") { passed++; }
        }
        Console.WriteLine($"PASS Watch interruption matrix: {passed} checks; actual managed child exit/restart/competing processes, synthetic bridge only.");
    }
    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    private static async Task<(int Code, string Output)> Process(string root, string id, string mode)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { typeof(WatchInterruptionMatrixChecks).Assembly.Location, "--watch-matrix-child", root, id, mode }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (!string.IsNullOrWhiteSpace(await error)) throw new Exception(await error);
        return (process.ExitCode, (await output).Trim());
    }
}
