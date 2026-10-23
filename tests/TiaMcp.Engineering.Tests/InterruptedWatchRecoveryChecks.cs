using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class InterruptedWatchRecoveryChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; }
        async Task Reject(Func<Task> action, string code)
        { try { await action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
        async Task Lost(Func<Task> action)
        { try { await action(); } catch (IOException) { return; } throw new Exception("Expected authored response loss"); }
        async Task<Fixture> New(string name, bool lostCheckpoint = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "interrupted-watch-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root);
            var dispatcher = new WatchTableDispatcher(native, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { projectName = "Fixture", plcPath = "/PLC", tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = "review" }));
            string id = (string)prepared["operation"]!["id"]!;
            native.LoseCheckpointResponse = lostCheckpoint; native.LoseApplyResponse = !lostCheckpoint;
            await Lost(() => dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = (string)prepared["operation"]!["planSha256"]! }));
            return new(root, native, dispatcher, new(root), id);
        }
        async Task<JsonObject> Reconcile(Fixture fixture, string? disposition = null) => Node(await fixture.Dispatcher.CallAsync("watch_reconcile_interrupted",
            new { operationId = fixture.Id, destinationDisposition = disposition }));
        string AdmitDestination(Fixture fixture, string? destination = null)
        {
            var record = fixture.Store.Read(fixture.Id); destination ??= Path.Combine(fixture.Root, "admitted-recovery");
            fixture.Store.ReserveRecovery(record.Id, record.Revision, [new(record.Plan.Resources[0].ResourceKey, destination)]); return destination;
        }

        var checkpoint = await New("checkpoint", true);
        await Reject(() => Reconcile(checkpoint), "FreshInstanceRequired");
        checkpoint.Native.NewInstance(); var checkpointResult = await Reconcile(checkpoint);
        Check((string)checkpointResult["originalDisposition"]! == "unchanged" && (string)checkpointResult["operation"]!["state"]! == "reconciled",
            "Lost checkpoint response reconciles independently observed unchanged original without an archive reference.");
        Check(checkpoint.Native.CheckpointCount == 1 && checkpoint.Native.ApplyCount == 0 && checkpoint.Native.RestoreCount == 0,
            "Checkpoint loss reconciliation never repeats checkpoint, apply or restore.");
        await Reject(() => Reconcile(checkpoint), "InvalidOperationState");

        var apply = await New("apply"); apply.Native.NewInstance(); var applyResult = await Reconcile(apply);
        Check((string)applyResult["originalDisposition"]! == "verifiedApplied" && (string)applyResult["operation"]!["state"]! == "reconciled",
            "Lost apply response reconciles exact reviewed readback without replay.");
        Check(apply.Native.ApplyCount == 1 && apply.Native.RestoreCount == 0 && apply.Store.Pending().Count == 0,
            "Read-only applied reconciliation releases the original only after proof.");

        var intent = await New("checkpoint-intent", true);
        // A separate acquired operation models a worker disappearing before checkpoint returns or is marked unknown.
        var original = intent.Store.Read(intent.Id);
        var proof = original.Plan.Inputs[0];
        intent.Store.Reconcile(original.Id, original.Revision, [new(original.Plan.Resources[0].ResourceKey, "unchanged", null, proof)]);
        var another = intent.Store.Prepare(original.Plan, "still-checkpoint-intent"); another = intent.Store.Acquire(another.Id, another.Revision);
        another = intent.Store.BeginCheckpoint(another.Id, another.Revision, () => another.Plan.Target);
        intent = intent with { Id = another.Id }; intent.Native.NewInstance();
        Check((string)(await Reconcile(intent))["originalDisposition"]! == "unchanged", "Persisted checkpointIntent is reachable through evidence-only reconciliation.");

        var absent = await New("absent"); string absentDestination = AdmitDestination(absent); absent.Native.NewInstance();
        await Reject(() => Reconcile(absent), "InvalidRequest");
        var absentResult = await Reconcile(absent, "verifiedAbsent");
        Check((string)absentResult["destinationDisposition"]! == "verifiedAbsent" && !Directory.Exists(absentDestination) && absent.Native.RestoreCount == 0,
            "A reserved but never created destination is explicitly discharged as absent without restore.");
        Check(absent.Store.Read(absent.Id).Evidence.Any(e => e.Gate.StartsWith("recovery.interrupted.", StringComparison.Ordinal)),
            "Interrupted destination proof is durably bound before final reconciliation.");

        var empty = await New("empty"); string emptyDestination = AdmitDestination(empty); Directory.CreateDirectory(emptyDestination); empty.Native.NewInstance();
        await Reject(() => Reconcile(empty, "retainedPartial"), "RecoveryDispositionMismatch");
        Check((string)(await Reconcile(empty, "verifiedAbsent"))["operation"]!["state"]! == "reconciled" && Directory.Exists(emptyDestination),
            "An empty reserved directory is reported absent and is retained unchanged.");

        var partial = await New("partial"); string partialDestination = AdmitDestination(partial); Directory.CreateDirectory(Path.Combine(partialDestination, "Subdir"));
        string partialFile = Path.Combine(partialDestination, "Subdir", "partial.bin"); byte[] payload = [0, 255, 13, 10]; File.WriteAllBytes(partialFile, payload); partial.Native.NewInstance();
        await Reject(() => Reconcile(partial, "verifiedAbsent"), "RecoveryDispositionMismatch");
        using (var held = new FileStream(partialFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Reject(() => Reconcile(partial, "retainedPartial"), "RecoveryInspectionFailed");
        Check(partial.Store.Read(partial.Id).State == "recoveryIntent", "Locked partial output retains all reservations.");
        var partialResult = await Reconcile(partial, "retainedPartial");
        string proofPath = Path.Combine(partial.Root, (string)partialResult["evidence"]!["relativePath"]!);
        var retained = JsonNode.Parse(File.ReadAllText(proofPath))!;
        Check((string)retained["destination"]!["files"]![0]!["relativePath"]! == "Subdir/partial.bin" &&
            (string)retained["destination"]!["files"]![0]!["sha256"]! == WatchTableDocument.Hash(payload), "Partial proof hashes the actual retained relative file bytes.");
        Check((bool)partialResult["restoredProjectVerified"]! == false && (bool)retained["nativeRestoreInvoked"]! == false && File.ReadAllBytes(partialFile).SequenceEqual(payload),
            "Retained partial output is never described as a restored project and remains byte-identical.");

        var restore = await New("restore"); restore.Native.LoseRestoreResponse = true;
        await Lost(() => restore.Dispatcher.CallAsync("watch_recover", new { operationId = restore.Id }));
        restore.Native.SelectOriginal(); var restoredMissing = await Reconcile(restore, "verifiedAbsent");
        Check((string)restoredMissing["operation"]!["state"]! == "reconciled" && restore.Native.RestoreCount == 1 && restore.Native.ApplyCount == 1,
            "Lost restore response reconciles original and missing destination without repeating retrieve.");

        var known = await New("known-proof"); await known.Dispatcher.CallAsync("watch_recover", new { operationId = known.Id }); known.Native.SelectOriginal();
        await Reject(() => Reconcile(known, "verifiedAbsent"), "RecoveryProofAlreadyBound");
        Check(known.Native.RestoreCount == 1, "A bound retrieve proof keeps the established original/restored reconciliation route.");

        var outside = await New("outside"); AdmitDestination(outside, Path.Combine(Path.GetDirectoryName(outside.Root)!, "outside-" + Guid.NewGuid().ToString("N"))); outside.Native.NewInstance();
        await Reject(() => Reconcile(outside, "verifiedAbsent"), "PackageInvalid");
        Check(outside.Store.Read(outside.Id).State == "recoveryIntent", "An outside-root reservation cannot be inspected or silently discharged.");

        var linked = await New("hardlink"); string linkedDestination = AdmitDestination(linked); Directory.CreateDirectory(linkedDestination);
        string originalFile = Path.Combine(linked.Root, "unrelated.bin"); File.WriteAllBytes(originalFile, [42]);
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLink(Path.Combine(linkedDestination, "linked.bin"), originalFile, IntPtr.Zero)) throw new Exception("Cannot create hard-link regression fixture: " + Marshal.GetLastWin32Error());
            linked.Native.NewInstance(); await Reject(() => Reconcile(linked, "retainedPartial"), "InvalidResourceKey");
        }

        var bounded = await New("bounded"); string boundedDestination = AdmitDestination(bounded); Directory.CreateDirectory(boundedDestination);
        for (int i = 0; i < 4097; i++) File.WriteAllBytes(Path.Combine(boundedDestination, i.ToString("D4") + ".bin"), []);
        bounded.Native.NewInstance(); await Reject(() => Reconcile(bounded, "retainedPartial"), "RecoveryInspectionLimit");

        foreach (var change in new (string Name, string Code, Action<JsonObject> Alter)[]
        {
            ("wrong-owner", "IdentityMismatch", x => x["observation"]!["projectFilePath"] = Path.Combine(Path.GetTempPath(), "different.ap21")),
            ("wrong-hardware", "IdentityMismatch", x => x["observation"]!["hardwareIdentity"] = "different-hardware"),
            ("wrong-binary", "IdentityMismatch", x => x["observation"]!["adapterSha256"] = new string('1', 64)),
            ("wrong-profile", "IdentityMismatch", x => x["observation"]!["profileSha256"] = new string('1', 64)),
            ("online", "IdentityMismatch", x => x["observation"]!["offline"] = false),
            ("unsaved", "IdentityMismatch", x => x["projectModified"] = true),
            ("same-process", "FreshInstanceRequired", x => x["processId"] = 1),
            ("same-epoch", "FreshInstanceRequired", x => x["observation"]!["sessionEpoch"] = "fake-1"),
            ("changed-peer", "PreservationFailed", x => x["peerSha256"] = new string('1', 64)),
            ("unreviewed-content", "ReconciliationIncomplete", x => x["target"]!["semanticSha256"] = new string('1', 64)),
            ("tampered-export", "ArtifactChanged", x => x["target"]!["artifact"]!["sha256"] = new string('1', 64)),
            ("typed-row-disagrees", "VerificationFailed", x => x["target"]!["rows"]![0]!["modifyValue"] = "FALSE")
        })
        {
            var f = await New(change.Name); f.Native.NewInstance();
            f.Native.TransformSnapshot = snapshot => { change.Alter(snapshot); return snapshot; };
            await Reject(() => Reconcile(f), change.Code);
            Check(f.Store.Read(f.Id).State == "outcomeUnknown" && f.Native.ApplyCount == 1 && f.Native.RestoreCount == 0,
                "Rejected " + change.Name + " cannot resolve unknown state or replay native work.");
        }
        var wrongArgument = await New("path-injection"); wrongArgument.Native.NewInstance();
        await Reject(() => wrongArgument.Dispatcher.CallAsync("watch_reconcile_interrupted", new { operationId = wrongArgument.Id, destination = "C:/unreviewed" }), "InvalidRequest");
        await Reject(() => Reconcile(wrongArgument, "verifiedAbsent"), "InvalidRequest");
        Console.WriteLine($"PASS Interrupted watch recovery reachability: {passed} checks; fake bridge only, retained bounded filesystem fixtures, no native calls.");
    }

    private sealed record Fixture(string Root, ProductionRecoveryChecks.FakeWatch Native, WatchTableDispatcher Dispatcher, EngineeringOperationStore Store, string Id);
    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
