using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class ProductionRecoveryChecks
{
    internal static void Run()
    {
        int passed = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; }
        void Reject(Action action, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
        string root = Path.Combine(Path.GetTempPath(), "production-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var store = new EngineeringOperationStore(root);
        EngineeringArtifactReference Save(string name, string text)
        { File.WriteAllText(Path.Combine(root, name), text); return new(name, WatchTableDocument.Hash(File.ReadAllBytes(Path.Combine(root, name)))); }
        var input = Save("input.json", "{\"syntheticFixture\":true}");
        var checkpoint = Save("checkpoint.zap21", "Authored checkpoint fixture, not a native archive");
        var proof = Save("proof.json", "Authored recovery fixture, no native claims");
        NativeTargetObservation Target(string name) => new(
            new(new("/authored/" + name, "/PLC", null, "/watch/fixture", "watchTable"), new("capture", new('a', 64)), new('b', 64)),
            Path.Combine(root, name, "fixture.ap21"), "authored-epoch", new('c', 64), new('d', 64), "authored-hardware", null, true, []);
        EngineeringOperationPlan Plan(string name, params EngineeringResourceEffect[] additional)
        {
            var target = Target(name);
            return new("authoredRecovery", target, [new(EngineeringResourceKey.ForPath("project", target.ProjectFilePath), "project", "fixture", "projectArchiveToNewLocation"), .. additional],
                [input], [], ["verified"]);
        }
        EngineeringOperationRecord Succeed(string name)
        {
            var plan = Plan(name); var record = store.Prepare(plan, name);
            record = store.Acquire(record.Id, record.Revision);
            record = store.BeginCheckpoint(record.Id, record.Revision, () => plan.Target);
            record = store.Checkpoint(record.Id, record.Revision, [checkpoint]);
            record = store.Dispatch(record.Id, record.Revision, () => plan.Target);
            return store.RecordOutcome(record.Id, record.Revision, true, [new("verified", proof)]);
        }
        EngineeringRecoveryDestination[] Destination(EngineeringOperationRecord r, string name) =>
            [new(r.Plan.Resources[0].ResourceKey, Path.Combine(root, name))];

        var success = Succeed("success");
        Check(store.Pending().Count == 0, "Successful outcomes release ordinary reservations.");
        var prepared = store.Prepare(Plan("unacquired"), "unacquired");
        Check(store.Pending().Count == 0, "Preparation alone is not durable resource ownership.");
        var owner = store.Acquire(prepared.Id, prepared.Revision);
        Check(store.Pending().Single().Id == owner.Id, "Pending returns an acquired owner.");
        store.Stop(owner.Id, owner.Revision);
        Check(store.Pending().Count == 0, "Stopped outcomes are not pending owners.");

        var competing = store.Prepare(Plan("competitor", new EngineeringResourceEffect(EngineeringResourceKey.ForPath("workspace", Path.Combine(root, "success")),
            "workspace", "overlappingParent", "immutableWorkspaceVersion")), "competitor");
        competing = store.Acquire(competing.Id, competing.Revision);
        Reject(() => store.ReserveRecovery(success.Id, success.Revision, Destination(success, "recovered-success")), "ResourceBusy");
        Check(store.Read(success.Id).State == "succeeded", "Refused success recovery cannot reacquire the original resource.");
        store.Stop(competing.Id, competing.Revision);
        Reject(() => store.ReserveRecovery(success.Id, success.Revision, [new(success.Plan.Resources[0].ResourceKey, Path.Combine(root, "success"))]), "InvalidRecovery");

        var destinationOwner = store.Prepare(Plan("other-target", new EngineeringResourceEffect(EngineeringResourceKey.ForPath("workspace", Path.Combine(root, "recovered-success", "child")),
            "workspace", "overlappingDestinationChild", "immutableWorkspaceVersion")), "destination-owner");
        destinationOwner = store.Acquire(destinationOwner.Id, destinationOwner.Revision);
        Reject(() => store.ReserveRecovery(success.Id, success.Revision, Destination(success, "recovered-success")), "ResourceBusy");
        store.Stop(destinationOwner.Id, destinationOwner.Revision);

        var recovery = store.ReserveRecovery(success.Id, success.Revision, Destination(success, "recovered-success"));
        Check(recovery.State == "recoveryIntent" && store.Pending().Single().Id == recovery.Id, "Recovery from success reacquires original and destination resources.");
        Reject(() => store.ReserveRecovery(recovery.Id, recovery.Revision, Destination(recovery, "other-recovery")), "RecoveryRequired");
        Reject(() => store.Dispatch(recovery.Id, recovery.Revision, () => recovery.Plan.Target), "RecoveryRequired");
        var firstProof = Save("retrieve.json", "Immutable initial recovery observation");
        recovery = store.RecordRecoveryEvidence(recovery.Id, recovery.Revision, new("recovery.retrieve", firstProof));
        Check(recovery.Evidence.Select(x => x.Gate).SequenceEqual(new[] { "verified", "recovery.retrieve" }), "Recovery evidence appends after original verification.");
        Reject(() => store.RecordRecoveryEvidence(recovery.Id, recovery.Revision, new("recovery.retrieve", proof)), "EvidenceAlreadyRecorded");
        Reject(() => store.RecordRecoveryEvidence(recovery.Id, recovery.Revision - 1, new("recovery.original", proof)), "RevisionConflict");
        var unknown = store.MarkUnknown(recovery.Id, recovery.Revision);
        Check(new EngineeringOperationStore(root).Pending().Single().State == "outcomeUnknown", "Unknown recovery remains owned after store reopen.");
        unknown = store.RecordRecoveryEvidence(unknown.Id, unknown.Revision, new("recovery.original", proof));
        Check(unknown.State == "outcomeUnknown" && unknown.Evidence.Count == 3, "Read-only reconciliation proof does not replay or silently resolve unknown recovery.");
        Reject(() => store.ReserveRecovery(unknown.Id, unknown.Revision, Destination(unknown, "replayed-recovery")), "RecoveryRequired");
        Reject(() => store.RecordRecoveryEvidence(unknown.Id, unknown.Revision, new("recovery.original", firstProof)), "EvidenceAlreadyRecorded");
        File.AppendAllText(Path.Combine(root, firstProof.RelativePath), "tampered");
        Reject(() => new EngineeringOperationStore(root).Read(unknown.Id), "ArtifactChanged");
        File.WriteAllText(Path.Combine(root, firstProof.RelativePath), "Immutable initial recovery observation");
        unknown = store.RecordRecoveryEvidence(unknown.Id, unknown.Revision, new("recovery.restored", proof));
        string last = Path.Combine(root, "engineering-operations", unknown.Id, unknown.Revision.ToString("D6") + ".json");
        byte[] beforeTamper = File.ReadAllBytes(last); var altered = JsonNode.Parse(beforeTamper)!;
        altered["evidence"]![0]!["gate"] = "replaced-old-evidence";
        File.WriteAllText(last, altered.ToJsonString(BridgeProtocol.Json));
        Reject(() => new EngineeringOperationStore(root).Read(unknown.Id), "JournalInvalid");
        File.WriteAllBytes(last, beforeTamper);
        Reject(() => store.Reconcile(unknown.Id, unknown.Revision, [new(unknown.Plan.Resources[0].ResourceKey, "verifiedApplied", null, proof)]), "ReconciliationIncomplete");
        var reconciled = store.Reconcile(unknown.Id, unknown.Revision, [new(unknown.Plan.Resources[0].ResourceKey, "verifiedApplied", null, proof)],
            [new(unknown.Plan.Resources[0].ResourceKey, unknown.RecoveryDestinations[0].RecoveryLocation, "verifiedRestored", proof)]);
        Check(reconciled.State == "reconciled" && store.Pending().Count == 0, "Full original/destination reconciliation releases ownership.");
        Reject(() => store.RecordRecoveryEvidence(reconciled.Id, reconciled.Revision, new("recovery.late", proof)), "RecoveryRequired");

        var withoutRecovery = store.Prepare(Plan("unknown-no-recovery"), "unknown-no-recovery");
        withoutRecovery = store.Acquire(withoutRecovery.Id, withoutRecovery.Revision);
        withoutRecovery = store.BeginCheckpoint(withoutRecovery.Id, withoutRecovery.Revision, () => withoutRecovery.Plan.Target);
        withoutRecovery = store.MarkUnknown(withoutRecovery.Id, withoutRecovery.Revision);
        Reject(() => store.RecordRecoveryEvidence(withoutRecovery.Id, withoutRecovery.Revision, new("recovery.invented", proof)), "RecoveryRequired");
        Console.WriteLine($"PASS Production recovery store contracts: {passed} checks; synthetic checkpoints, no native execution; fixtures {root}");
        RunDispatcher().GetAwaiter().GetResult();
        InterruptedWatchRecoveryChecks.Run().GetAwaiter().GetResult();
    }

    private static async Task RunDispatcher()
    {
        int passed = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; }
        async Task Reject(Func<Task> run, string code)
        { try { await run(); } catch (BridgeException e) when (e.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
        async Task<(string Root, FakeWatch Native, WatchTableDispatcher Dispatcher, JsonObject Prepared)> New(string suffix)
        {
            string root = Path.Combine(Path.GetTempPath(), "dispatcher-" + suffix + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var native = new FakeWatch(root); var dispatcher = new WatchTableDispatcher(native, root);
            var prepared = Node(await dispatcher.CallAsync("watch_prepare", new { projectName = "Fixture", plcPath = "/PLC", tableName = "FixtureWatch", ordinal = 0, modifyValue = "TRUE", idempotencyKey = "review" }));
            return (root, native, dispatcher, prepared);
        }
        static object ApplyArgs(JsonObject prepared) => new { operationId = (string)prepared["operation"]!["id"]!, reviewedPlanSha256 = (string)prepared["operation"]!["planSha256"]! };
        static object Id(JsonObject prepared) => new { operationId = (string)prepared["operation"]!["id"]! };

        var happy = await New("happy"); string id = (string)happy.Prepared["operation"]!["id"]!;
        await Reject(() => happy.Dispatcher.CallAsync("watch_apply_native", new { }), "InternalOperation");
        Check(happy.Native.ApplyCount == 0, "Private mutation cannot bypass dispatcher review.");
        await Reject(() => happy.Dispatcher.CallAsync("watch_apply", new { operationId = id, reviewedPlanSha256 = new string('f', 64) }), "ReviewMismatch");
        var applied = Node(await happy.Dispatcher.CallAsync("watch_apply", ApplyArgs(happy.Prepared)));
        Check((string)applied["operation"]!["state"]! == "dispatchIntent" && happy.Native.ApplyCount == 1, "Apply retains intent and invokes native mutation once.");
        await Reject(() => happy.Dispatcher.CallAsync("watch_apply", ApplyArgs(happy.Prepared)), "RecoveryRequired");
        await Reject(() => happy.Dispatcher.CallAsync("change_apply", new { }), "ResourceBusy");
        await Reject(() => happy.Dispatcher.CallAsync("watch_verify_reopen", Id(happy.Prepared)), "FreshInstanceRequired");
        happy.Native.NewInstance();
        var verified = Node(await happy.Dispatcher.CallAsync("watch_verify_reopen", Id(happy.Prepared)));
        Check((string)verified["operation"]!["state"]! == "succeeded", "Only a fresh fake instance with exact saved readback completes the fake workflow.");
        var recovered = Node(await happy.Dispatcher.CallAsync("watch_recover", Id(happy.Prepared)));
        Check((string)recovered["operation"]!["state"]! == "recoveryIntent" && happy.Native.RestoreCount == 1, "Successful operation admits one separate recovery.");
        await Reject(() => happy.Dispatcher.CallAsync("watch_recover", Id(happy.Prepared)), "RecoveryRequired");
        var owned = new EngineeringOperationStore(happy.Root); var pending = owned.Read(id);
        owned.MarkUnknown(id, pending.Revision); // Retained retrieval response, then worker interruption.
        happy.Native.NewInstance();
        var restoredProof = Node(await happy.Dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" }));
        Check((string)restoredProof["operation"]!["state"]! == "outcomeUnknown", "Fresh restored proof can append after interruption without replay.");
        happy.Native.SelectOriginal();
        var reconciled = Node(await happy.Dispatcher.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" }));
        Check((string)reconciled["operation"]!["state"]! == "reconciled" && happy.Native.ApplyCount == 1 && happy.Native.RestoreCount == 1,
            "Both resources reconcile after interruption without a second apply or retrieve.");

        var lost = await New("lost-response"); lost.Native.LoseApplyResponse = true;
        try { await lost.Dispatcher.CallAsync("watch_apply", ApplyArgs(lost.Prepared)); throw new Exception("Expected lost response"); }
        catch (IOException) { passed++; }
        var lostState = Node(await lost.Dispatcher.CallAsync("engineering_operation_status", Id(lost.Prepared)));
        Check((string)lostState["state"]! == "outcomeUnknown" && lost.Native.ApplyCount == 1 && lost.Native.EndCount == 1,
            "Lost apply response preserves unknown outcome and ends the worker lease.");
        var restarted = new WatchTableDispatcher(lost.Native, lost.Root);
        await Reject(() => restarted.CallAsync("watch_apply", ApplyArgs(lost.Prepared)), "RecoveryRequired");
        Check(lost.Native.ApplyCount == 1, "New dispatcher never replays an unknown native apply.");
        Console.WriteLine($"PASS Production watch dispatcher: {passed} checks using authored fake IBridgeClient, no native execution.");
    }

    private static JsonObject Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();
    internal sealed class FakeWatch(string root, byte[]? fixture = null) : IBridgeClient
    {
        private const string Before = "<Document><Engineering version=\"V21\"/><DocumentInfo><ExportSetting>WithDefaults</ExportSetting></DocumentInfo><SW.WatchAndForceTables.PlcWatchTable ID=\"0\"><AttributeList><Name>FixtureWatch</Name></AttributeList><ObjectList><SW.WatchAndForceTables.PlcWatchTableEntry ID=\"1\" CompositionName=\"Entries\"><AttributeList><Name>Tag</Name><Address/><DisplayFormat>Bool</DisplayFormat><ModifyValue>FALSE</ModifyValue></AttributeList></SW.WatchAndForceTables.PlcWatchTableEntry></ObjectList></SW.WatchAndForceTables.PlcWatchTable></Document>";
        private readonly byte[] baseline = fixture ?? Encoding.UTF8.GetBytes(Before);
        private byte[] current = fixture ?? Encoding.UTF8.GetBytes(Before);
        private byte[] originalState = fixture ?? Encoding.UTF8.GetBytes(Before.Replace("FALSE", "TRUE"));
        private string project = Path.Combine(root, "original", "fixture.ap21");
        private int epoch = 1;
        internal int ApplyCount, RestoreCount, EndCount;
        internal int CheckpointCount;
        internal bool LoseApplyResponse, LoseCheckpointResponse, LoseRestoreResponse;
        internal Func<JsonObject, JsonObject>? TransformSnapshot;
        internal void NewInstance() => epoch++;
        internal void SelectOriginal() { project = Path.Combine(root, "original", "fixture.ap21"); current = originalState; NewInstance(); }
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            var args = JsonSerializer.SerializeToNode(arguments ?? new { }, BridgeProtocol.Json)!.AsObject();
            object result;
            switch (operation)
            {
                case "mutation_begin":
                    result = new { leaseToken = "authored-mutation-lease", scope = new EngineeringMutationScope((string)args["operation"]!, new('1', 64), new('2', 64), "fake-" + epoch,
                        new('c', 64), epoch, new('3', 64), [new("project", project, "authored-scope")]) }; break;
                case "mutation_end": result = new { released = true }; break;
                case "watch_read_native": result = Snapshot(); break;
                case "watch_begin": var begin = Snapshot(); begin["leaseToken"] = "authored-lease"; result = begin; break;
                case "watch_checkpoint":
                    CheckpointCount++;
                    string archive = Path.Combine((string)args["artifactDirectory"]!, "fake-checkpoint.zap21");
                    File.WriteAllText(archive, "Authored checkpoint, not a native archive");
                    if (LoseCheckpointResponse) throw new IOException("Authored lost checkpoint response");
                    result = new { checkpoint = new { path = archive, sha256 = WatchTableDocument.Hash(File.ReadAllBytes(archive)) }, snapshot = Snapshot() }; break;
                case "watch_apply_native":
                    ApplyCount++; current = File.ReadAllBytes((string)args["inputPath"]!); originalState = current;
                    if (LoseApplyResponse) throw new IOException("Authored lost apply response");
                    result = Snapshot(); break;
                case "watch_end": EndCount++; result = new { ended = true }; break;
                case "watch_restore_native":
                    RestoreCount++; project = Path.Combine((string)args["destination"]!, "fixture.ap21"); NewInstance(); current = baseline;
                    if (LoseRestoreResponse) throw new IOException("Authored lost restore response");
                    result = new { project = new { name = "Fixture", path = project }, processId = epoch, sessionEpoch = "fake-" + epoch }; break;
                default: result = new { forwarded = operation }; break;
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(result, BridgeProtocol.Json));
        }
        private JsonObject Snapshot()
        {
            string path = Path.Combine(root, "capture-" + Guid.NewGuid().ToString("N") + ".xml"); File.WriteAllBytes(path, current);
            string hash = WatchTableDocument.SemanticHash(current);
            var observation = new NativeTargetObservation(new(new(project, "/PLC", "root", "/watch/FixtureWatch", "watchTable"), new("capture-" + epoch, new('a', 64)), hash),
                project, "fake-" + epoch, new('c', 64), new('d', 64), "authored-hardware", null, true,
                [new("offline", "observed", new('e', 64)), new("watchImport", "observed", new('f', 64))]);
            var snapshot = JsonSerializer.SerializeToNode(new { observation, processId = epoch, projectModified = false, peerSha256 = new string('9', 64),
                target = new { artifact = new { path, sha256 = WatchTableDocument.Hash(current) }, semanticSha256 = hash, isConsistent = true,
                    rows = Enumerable.Range(0, new WatchTableDocument(current).Count).Select(i => new {
                        kind = new WatchTableDocument(current).RowKind(i), name = new WatchTableDocument(current).Field(i, "Name"),
                        address = new WatchTableDocument(current).Field(i, "Address"), displayFormat = new WatchTableDocument(current).Field(i, "DisplayFormat"),
                        modifyValue = new WatchTableDocument(current).Field(i, "ModifyValue") }).ToArray() } }, BridgeProtocol.Json)!.AsObject();
            return TransformSnapshot?.Invoke(snapshot) ?? snapshot;
        }
    }
}
