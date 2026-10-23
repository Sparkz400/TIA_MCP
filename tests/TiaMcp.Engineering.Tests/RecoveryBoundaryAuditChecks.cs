using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class RecoveryBoundaryAuditChecks
{
    internal static async Task Run()
    {
        int passed = 0; var failures = new List<string>();
        void Check(bool condition, string name) { if (!condition) failures.Add(name); else passed++; }
        async Task Refuse(Fixture fixture, string name, string operation, object arguments)
        {
            bool refused = false;
            try { await fixture.Dispatcher.CallAsync(operation, arguments); }
            catch (BridgeException) { refused = true; }
            Check(refused, name + " must be refused");
            Check(fixture.Store.Read(fixture.Id).State is not ("succeeded" or "reconciled"), name + " retains durable ownership");
            Check(fixture.Native.ApplyCount == 1 && fixture.Native.RestoreCount <= 1, name + " never replays native work");
        }
        async Task<Fixture> New(string name, bool recover, bool lost = false, bool alterRestore = false, bool alterRestoreIdentity = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "recovery-boundary-audit-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new ProductionRecoveryChecks.FakeWatch(root);
            IBridgeClient adapter = alterRestore || alterRestoreIdentity ? new RestoreReplyAdapter(native,
                alterRestore ? Path.Combine(root, "outside-reserved", "fixture.ap21") : null, alterRestoreIdentity) : native;
            var dispatcher = new WatchTableDispatcher(adapter, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { projectName = "Fixture", plcPath = "/PLC", tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = name }));
            string id = (string)prepared["operation"]!["id"]!;
            await dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = (string)prepared["operation"]!["planSha256"]! });
            var fixture = new Fixture(root, native, dispatcher, new(root), id);
            if (recover)
            {
                native.LoseRestoreResponse = lost;
                try { await dispatcher.CallAsync("watch_recover", new { operationId = id }); }
                catch (IOException) when (lost) { }
            }
            return fixture;
        }
        object Reconcile(Fixture f, string role) => new { operationId = f.Id, role };
        var shapeCases = new (string Name, Action<JsonObject> Alter)[] {
            ("online", s => s["observation"]!["offline"] = false),
            ("native-identity", s => s["observation"]!["nativeIdentity"] = "different-native-object"),
            ("tia-version", s => s["observation"]!["reference"]!["snapshot"]!["tiaVersion"] = 20),
            ("wrong-object", s => s["observation"]!["reference"]!["object"]!["objectPath"] = "/watch/another-table"),
            ("wrong-plc", s => s["observation"]!["reference"]!["object"]!["plcPath"] = "/OTHER-PLC"),
            ("reference-project", s => s["observation"]!["reference"]!["object"]!["projectPath"] = Path.Combine(Path.GetTempPath(), "foreign.ap21")),
            ("missing-probe", s => s["observation"]!["probes"] = new JsonArray()),
            ("content-reference", s => s["observation"]!["reference"]!["contentSha256"] = new string('1', 64)),
            ("inconsistent-target", s => s["target"]!["isConsistent"] = false),
            ("invalid-process", s => s["processId"] = 0),
            ("negative-process", s => s["processId"] = -1),
            ("missing-process", s => s.Remove("processId"))
        };
        foreach (var item in shapeCases)
        {
            var f = await New("located-" + item.Name, true, true);
            f.Native.TransformSnapshot = snapshot => { item.Alter(snapshot); return snapshot; };
            await Refuse(f, "located " + item.Name, "watch_reconcile_recovery", Reconcile(f, "restored"));
        }
        foreach (var item in shapeCases)
        {
            var f = await New("bound-" + item.Name, true); f.Native.NewInstance();
            f.Native.TransformSnapshot = snapshot => { item.Alter(snapshot); return snapshot; };
            await Refuse(f, "bound restored " + item.Name, "watch_reconcile_recovery", Reconcile(f, "restored"));
        }
        foreach (var item in shapeCases)
        {
            var f = await New("verify-" + item.Name, false); f.Native.NewInstance();
            f.Native.TransformSnapshot = snapshot => { item.Alter(snapshot); return snapshot; };
            await Refuse(f, "fresh verification " + item.Name, "watch_verify_reopen", new { operationId = f.Id });
        }
        var original = await New("original-stale-session", true); original.Native.NewInstance();
        await original.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(original, "restored")); original.Native.SelectOriginal();
        original.Native.TransformSnapshot = s => { s["processId"] = 1; s["observation"]!["sessionEpoch"] = "fake-1"; return s; };
        await Refuse(original, "original role same pre-apply process", "watch_reconcile_recovery", Reconcile(original, "original"));

        var wrongReply = await New("wrong-retrieve-reply", false, alterRestore: true);
        await Refuse(wrongReply, "restore reply outside admitted destination", "watch_recover", new { operationId = wrongReply.Id });
        Check(!wrongReply.Store.Read(wrongReply.Id).Evidence.Any(e => e.Gate == "recovery.retrieve"), "Outside retrieve reply cannot bind successful retrieval proof");
        var wrongIdentity = await New("wrong-retrieve-identity", false, alterRestoreIdentity: true);
        await Refuse(wrongIdentity, "restore reply process/session differs from actual readback", "watch_recover", new { operationId = wrongIdentity.Id });
        Check(!wrongIdentity.Store.Read(wrongIdentity.Id).Evidence.Any(e => e.Gate == "recovery.retrieve"), "Dishonest retrieve identity cannot relax fresh reopen proof");

        async Task PreserveOrphan(string name, string file, bool recover, bool lost, string operation, string? role)
        {
            var f = await New(name, recover, lost); f.Native.NewInstance();
            string directory = Path.GetDirectoryName(Path.Combine(f.Root, f.Store.Read(f.Id).Plan.Inputs[0].RelativePath))!;
            string orphan = Path.Combine(directory, file); byte[] retained = [0, 1, 2, 255]; File.WriteAllBytes(orphan, retained);
            try
            {
                await f.Dispatcher.CallAsync(operation, role is null ? new { operationId = f.Id } : Reconcile(f, role));
                Check(true, name + " retry succeeds without replacing uncommitted receipt");
            }
            catch (Exception error) { failures.Add(name + " blocked by uncommitted receipt: " + error.GetType().Name); }
            Check(File.ReadAllBytes(orphan).SequenceEqual(retained), name + " preserves orphaned receipt bytes");
        }
        await PreserveOrphan("located-orphan", "recovery-located.json", true, true, "watch_reconcile_recovery", "restored");
        await PreserveOrphan("bound-orphan", "reconcile-restored.json", true, false, "watch_reconcile_recovery", "restored");
        await PreserveOrphan("verify-orphan", "fresh-reopen.json", false, false, "watch_verify_reopen", null);

        var lostLifecycle = await New("lost-lifecycle", true, true);
        await lostLifecycle.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(lostLifecycle, "restored"));
        Check(lostLifecycle.Store.Read(lostLifecycle.Id).Evidence.Any(e => e.Gate == "recovery.located"), "Lost retrieve response binds located proof without re-retrieval");
        lostLifecycle.Native.NewInstance();
        await lostLifecycle.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(lostLifecycle, "restored"));
        lostLifecycle.Native.SelectOriginal();
        await lostLifecycle.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(lostLifecycle, "original"));
        Check(lostLifecycle.Store.Read(lostLifecycle.Id).State == "reconciled" && lostLifecycle.Native.RestoreCount == 1,
            "Located then fresh-reopened recovery plus fresh original reconciles without native replay");

        var interruptedFinal = await New("final-proof-commit-interrupted", true); interruptedFinal.Native.NewInstance();
        await interruptedFinal.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(interruptedFinal, "restored"));
        interruptedFinal.Native.SelectOriginal();
        var retainedOriginal = await interruptedFinal.Native.CallAsync("watch_read_native", new { });
        byte[] proofBytes = JsonSerializer.SerializeToUtf8Bytes(retainedOriginal, BridgeProtocol.Json);
        var priorFinal = interruptedFinal.Store.Read(interruptedFinal.Id);
        string proofDirectory = Path.GetDirectoryName(Path.Combine(interruptedFinal.Root, priorFinal.Plan.Inputs[0].RelativePath))!;
        string proofPath = Path.Combine(proofDirectory, "audited-original-proof-before-host-exit.json"); File.WriteAllBytes(proofPath, proofBytes);
        var proofReference = new EngineeringArtifactReference(Path.GetRelativePath(interruptedFinal.Root, proofPath).Replace('\\', '/'), WatchTableDocument.Hash(proofBytes));
        interruptedFinal.Store.RecordRecoveryEvidence(priorFinal.Id, priorFinal.Revision, new("recovery.original", proofReference));
        try
        {
            await interruptedFinal.Dispatcher.CallAsync("watch_reconcile_recovery", Reconcile(interruptedFinal, "original"));
            Check(interruptedFinal.Store.Read(interruptedFinal.Id).State == "reconciled", "Host exit after final proof append remains reachable for final reconciliation");
        }
        catch (BridgeException error) { failures.Add("Final proof append cannot resume reconciliation: " + error.Code); }
        Check(interruptedFinal.Native.RestoreCount == 1 && interruptedFinal.Native.ApplyCount == 1, "Final proof resume never repeats native apply/retrieval");

        var retainedEvidence = await New("mark-unknown", true); var before = retainedEvidence.Store.Read(retainedEvidence.Id);
        var unknown = retainedEvidence.Store.MarkUnknown(before.Id, before.Revision, before.Plan.Inputs[0]);
        Check(unknown.Evidence.Count == before.Evidence.Count + 1 && before.Evidence.All(e => unknown.Evidence.Contains(e)) &&
            unknown.Evidence.Last().Gate == "interruption." + before.Revision && unknown.Checkpoints.SequenceEqual(before.Checkpoints),
            "MarkUnknown appends interruption diagnostics and preserves retrieval/checkpoint evidence");

        string gateRoot = Path.Combine(Path.GetTempPath(), "watch-expanded-gate-audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(gateRoot);
        var gateNative = new ProductionRecoveryChecks.FakeWatch(gateRoot);
        var harness = new WatchTableDispatcher(gateNative, gateRoot, true);
        var expanded = Node(await harness.CallAsync("watch_prepare", new { projectName = "Fixture", plcPath = "/PLC", tableName = "FixtureWatch", editKind = "clone", ordinal = 0, insertAt = 1, idempotencyKey = "expanded" }));
        bool gated = false;
        try { await new WatchTableDispatcher(gateNative, gateRoot).CallAsync("watch_apply", new { operationId = (string)expanded["operation"]!["id"]!, reviewedPlanSha256 = (string)expanded["operation"]!["planSha256"]! }); }
        catch (BridgeException) { gated = true; }
        Check(gated && gateNative.CheckpointCount == 0 && gateNative.ApplyCount == 0, "Production dispatcher cannot apply a harness-prepared expanded plan");
        if (failures.Count != 0) throw new Exception("Recovery boundary audit failures:\n" + string.Join("\n", failures));
        Console.WriteLine($"PASS Recovery boundary independent audit: {passed} checks; fake bridge only, no native execution.");
    }

    private sealed record Fixture(string Root, ProductionRecoveryChecks.FakeWatch Native, WatchTableDispatcher Dispatcher, EngineeringOperationStore Store, string Id);
    private static JsonObject Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!.AsObject();
    private sealed class RestoreReplyAdapter(IBridgeClient native, string? outside, bool alterIdentity = false) : IBridgeClient
    {
        public async Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            var response = await native.CallAsync(operation, arguments, cancellationToken);
            if (operation != "watch_restore_native") return response;
            var result = Node(response);
            if (outside is not null) result["project"]!["path"] = outside;
            if (alterIdentity) { result["processId"] = 900001; result["sessionEpoch"] = "different-retrieved-epoch"; }
            return JsonSerializer.SerializeToElement(result, BridgeProtocol.Json);
        }
    }
}
