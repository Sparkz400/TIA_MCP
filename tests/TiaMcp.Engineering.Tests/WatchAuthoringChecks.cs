using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.WatchTables;

internal static class WatchAuthoringChecks
{
    internal static async Task Run()
    {
        int passed = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
        async Task Reject(Func<Task> action, string code)
        { try { await action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; } throw new Exception("Expected " + code); }
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"))) repo = Path.GetDirectoryName(repo) ?? throw new Exception("Native fixture absent");
        WatchAuthoringProfileChecks.Run(repo);
        await WatchSourceNegativeRecoveryChecks.Run();
        byte[] template = File.ReadAllBytes(Path.Combine(repo, "fixtures/watch-tables/native-owned-v21/MilestoneWatch.before.xml"));
        async Task<(Fake Native, WatchTableDispatcher Dispatcher, JsonObject Request)> Fixture(bool existing = false)
        {
            var fake = new Fake(template, existing);
            var dispatcher = new WatchTableDispatcher(fake, fake.Root, allowUnqualifiedDocumentEdits: true);
            var generation = Node(await dispatcher.CallAsync("watch_generate", new { projectPath = fake.Original, plcPath = "/PLC", blockPath = "/PLC/FB", instanceDbPath = "/PLC/DB_A", tableName = "Diagnostic", maxRows = 64 }));
            Check(generation["plan"]!["rows"]!.AsArray().Count == 2 && fake.Imports == 0, "Generate runs planner without native mutation.");
            var request = new JsonObject { ["generationId"] = generation["generationId"]!.DeepClone(), ["generationSha256"] = generation["generationSha256"]!.DeepClone(),
                ["idempotencyKey"] = "authoring", ["mode"] = existing ? "update" : "create",
                ["destination"] = new JsonObject { ["projectPath"] = fake.Original, ["plcPath"] = "/PLC", ["group"] = "root", ["tableName"] = "Diagnostic" },
                ["template"] = new JsonObject { ["tableName"] = "MilestoneWatch", ["group"] = "root" },
                ["steps"] = new JsonArray(new JsonObject { ["kind"] = "add", ["candidateId"] = "active", ["templateOrdinal"] = 0 },
                    new JsonObject { ["kind"] = "add", ["candidateId"] = "count", ["templateOrdinal"] = 0, ["comments"] = new JsonObject { ["de-DE"] = "Zähler Ω\r\n{0}", ["fr-FR"] = "Compteur" } }) };
            if (existing) request.Remove("template");
            return (fake, dispatcher, request);
        }
        foreach (bool existing in new[] { false, true })
        {
            var f = await Fixture(existing);
            if (existing)
            {
                f.Request["steps"]!.AsArray().Add(new JsonObject { ["kind"] = "remove", ["ordinal"] = 0 });
                f.Request["steps"]!.AsArray().Add(new JsonObject { ["kind"] = "reorder", ["order"] = new JsonArray(1, 0) });
            }
            var prepared = Node(await f.Dispatcher.CallAsync("watch_prepare_authoring", f.Request));
            string id = prepared["operation"]!["id"]!.GetValue<string>();
            var apply = new { operationId = id, reviewedPlanSha256 = prepared["operation"]!["planSha256"]!.GetValue<string>() };
            var rows = prepared["review"]!["edit"]!["after"]!["rows"]!.AsArray();
            Check(rows.Count == 2 && rows.All(r => r!["modifyValue"]!.GetValue<string>() == ""), "Generated rows clear stored modification literals.");
            int countOrdinal = existing ? 0 : 1;
            Check(rows[countOrdinal]!["name"]!.GetValue<string>() == "\"DB_A\".Count" && rows[countOrdinal]!["displayFormat"]!.GetValue<string>() == "DEC_signed" &&
                rows[countOrdinal]!["comments"]!["fr-FR"]!.GetValue<string>() == "Compteur", "Exact scalar selector, format and new multilingual comment survive the complete review.");
            Check(prepared["review"]!["edit"]!["requiredProfiles"]!.AsArray().Any(p => p!.GetValue<string>() == "watch.add-instance-dint.v1"), "Datatype has its own qualification gate.");
            var production = new WatchTableDispatcher(f.Native, f.Native.Root);
            await Reject(() => production.CallAsync("watch_apply", apply), "UnsupportedProfile");
            Check(f.Native.Begins == 0 && f.Native.Archives == 0 && f.Native.Imports == 0, "Unqualified production apply refuses before lease/checkpoint/import.");
            await f.Dispatcher.CallAsync("watch_apply", apply);
            Check(f.Native.Imports == 1 && f.Native.Archives == 1, "Qualified harness uses one checkpoint and one native import.");
            Check(new EngineeringOperationStore(f.Native.Root).Read(id).Evidence.Any(e => e.Gate == "dispatch.readback"), "Apply readback is durably retained.");
            f.Native.NewInstance(); await f.Dispatcher.CallAsync("watch_verify_reopen", new { operationId = id });
            // Production recovery remains available even when the mutation itself is not production-admitted.
            await production.CallAsync("watch_recover", new { operationId = id });
            f.Native.NewInstance(); await production.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
            f.Native.SelectOriginal(); await production.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" });
            Check(new EngineeringOperationStore(f.Native.Root).Read(id).State == "reconciled" && f.Native.Retrievals == 1 && f.Native.Imports == 1,
                "Creation absence/update baseline, recovered reopen and original outcome reconcile without replay.");
            Check(template.SequenceEqual(f.Native.Template), "Captured template bytes are unchanged.");
        }
        var stale = await Fixture(); stale.Native.SourceVersion++;
        await Reject(() => stale.Dispatcher.CallAsync("watch_prepare_authoring", stale.Request), "StaleSource");
        Check(stale.Native.Archives == 0 && stale.Native.Imports == 0, "Stale generation refuses before mutation.");
        var collision = await Fixture(); collision.Native.Occupy();
        await Reject(() => collision.Dispatcher.CallAsync("watch_prepare_authoring", collision.Request), "WatchTableCollision");
        var wrong = await Fixture(); wrong.Request["destination"]!["plcPath"] = "/OtherPLC";
        await Reject(() => wrong.Dispatcher.CallAsync("watch_prepare_authoring", wrong.Request), "IdentityMismatch");
        var culture = await Fixture(); culture.Request["steps"]![1]!["comments"]!["ja-JP"] = "未承認";
        await Reject(() => culture.Dispatcher.CallAsync("watch_prepare_authoring", culture.Request), "InactiveCulture");
        var unknown = await Fixture(); unknown.Request["steps"]![0]!["candidateId"] = "other-instance";
        await Reject(() => unknown.Dispatcher.CallAsync("watch_prepare_authoring", unknown.Request), "UnresolvedSignal");
        var injection = await Fixture(); injection.Request["steps"]![0]!["name"] = "\"DB_B\".Active";
        await Reject(() => injection.Dispatcher.CallAsync("watch_prepare_authoring", injection.Request), "InvalidRequest");
        var budget = await Fixture(); budget.Request["steps"] = new JsonArray(Enumerable.Range(0, 129).Select(_ => (JsonNode)new JsonObject { ["kind"] = "remove", ["ordinal"] = 0 }).ToArray());
        await Reject(() => budget.Dispatcher.CallAsync("watch_prepare_authoring", budget.Request), "WatchTableLimitExceeded");
        foreach (bool emptyUpdate in new[] { false, true })
        {
            var packaged = await Fixture(emptyUpdate); packaged.Request.Remove("template");
            packaged.Native.ActiveCultures = ["fr-FR"];
            packaged.Request["steps"]![1]!["comments"]!.AsObject().Remove("de-DE");
            if (emptyUpdate) packaged.Native.MakeEmpty();
            var preview = Node(await packaged.Dispatcher.CallAsync("watch_prepare_authoring", packaged.Request));
            var resultRows = preview["review"]!["edit"]!["after"]!["rows"]!.AsArray();
            Check(preview["review"]!["template"]!["origin"]!.GetValue<string>() == "packagedGenuineNativeCapture" && resultRows.Count == 2,
                "First-table creation and empty-table update use the packaged hash-pinned native grammar.");
            Check(resultRows.All(r => r!["comments"]!.AsObject().Count == 1 && r["comments"]!["fr-FR"] is not null),
                "New rows use active project languages without carrying inactive template cultures.");
        }
        var mixed = await Fixture(true); mixed.Native.MakeMixed();
        mixed.Request["steps"] = new JsonArray(new JsonObject { ["kind"] = "reorder", ["order"] = new JsonArray(1, 0) });
        var mixedReview = Node(await mixed.Dispatcher.CallAsync("watch_prepare_authoring", mixed.Request));
        Check(mixedReview["review"]!["edit"]!["after"]!["rows"]![0]!["kind"]!.GetValue<string>() == "PlcTableCommentEntry" &&
            mixedReview["review"]!["edit"]!["after"]!["rows"]![0]!["comments"]!["de-DE"]!.GetValue<string>() == "Freigabestatus {0}" &&
            mixedReview["review"]!["edit"]!["requiredProfiles"]!.AsArray().Any(p => p!.GetValue<string>() == "watch.comment-row-preservation.v1"),
            "Mixed native row ordering retains every comment and has an independent preservation gate.");
        var changed = await Fixture();
        var reviewed = Node(await changed.Dispatcher.CallAsync("watch_prepare_authoring", changed.Request));
        changed.Native.Occupy();
        await Reject(() => changed.Dispatcher.CallAsync("watch_apply", new { operationId = (string)reviewed["operation"]!["id"]!, reviewedPlanSha256 = (string)reviewed["operation"]!["planSha256"]! }), "StaleContent");
        Check(changed.Native.Archives == 0 && changed.Native.Imports == 0, "Concurrent destination collision is refused before checkpoint.");
        var lateSource = await Fixture(); var lateReview = Node(await lateSource.Dispatcher.CallAsync("watch_prepare_authoring", lateSource.Request)); lateSource.Native.SourceVersion++;
        await Reject(() => lateSource.Dispatcher.CallAsync("watch_apply", new { operationId = (string)lateReview["operation"]!["id"]!, reviewedPlanSha256 = (string)lateReview["operation"]!["planSha256"]! }), "StaleContent");
        Check(lateSource.Native.Archives == 0 && lateSource.Native.Imports == 0, "Source manifest drift after review refuses before checkpoint.");
        foreach (bool loseRestore in new[] { false, true })
        {
            var lost = await Fixture(); var p = Node(await lost.Dispatcher.CallAsync("watch_prepare_authoring", lost.Request)); string id = (string)p["operation"]!["id"]!;
            var apply = new { operationId = id, reviewedPlanSha256 = (string)p["operation"]!["planSha256"]! };
            lost.Native.LoseImport = !loseRestore;
            try { await lost.Dispatcher.CallAsync("watch_apply", apply); if (!loseRestore) throw new Exception("Expected reply loss"); }
            catch (IOException) { passed++; }
            var restarted = new WatchTableDispatcher(lost.Native, lost.Native.Root, allowUnqualifiedDocumentEdits: true);
            await Reject(() => restarted.CallAsync("watch_apply", apply), "RecoveryRequired");
            if (loseRestore)
            {
                lost.Native.LoseRestore = true;
                try { await restarted.CallAsync("watch_recover", new { operationId = id }); throw new Exception("Expected retrieval loss"); }
                catch (IOException) { passed++; }
                await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
                lost.Native.NewInstance(); await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "restored" });
                lost.Native.SelectOriginal(); await restarted.CallAsync("watch_reconcile_recovery", new { operationId = id, role = "original" });
            }
            else { lost.Native.NewInstance(); await restarted.CallAsync("watch_reconcile_interrupted", new { operationId = id }); }
            Check(new EngineeringOperationStore(lost.Native.Root).Read(id).State == "reconciled" && lost.Native.Imports == 1,
                "Creation lost-response recovery observes absence/applied content without replay.");
        }
        Console.WriteLine($"PASS WT-04 authoring: {passed} managed checks; native mutation profiles remain unverified.");
    }
    private static JsonObject Node(JsonElement element) => JsonNode.Parse(element.GetRawText())!.AsObject();

    private sealed class Fake : IBridgeClient
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "watch-authoring-" + Guid.NewGuid().ToString("N"));
        internal string Original => Path.Combine(Root, "original", "fixture.ap21");
        internal readonly byte[] Template;
        private readonly byte[]? baseline;
        private byte[]? current, applied;
        private string project;
        private int epoch = 1;
        internal int Begins, Archives, Imports, Retrievals, SourceVersion;
        internal bool LoseImport, LoseRestore;
        internal string[] ActiveCultures = ["de-DE", "en-GB", "nl-NL", "fr-FR"];
        internal Fake(byte[] template, bool existing)
        {
            Directory.CreateDirectory(Root); Template = (byte[])template.Clone(); project = Original;
            if (existing) { Occupy(); baseline = current; }
        }
        internal void Occupy() { var d = new WatchTableDocument(Template); d.Rename("Diagnostic"); current = d.ToBytes(); }
        internal void MakeEmpty()
        {
            var xml = System.Xml.Linq.XDocument.Load(new MemoryStream(current!));
            xml.Root!.Elements().Last().Element("ObjectList")!.Remove();
            using var bytes = new MemoryStream(); xml.Save(bytes); current = bytes.ToArray();
        }
        internal void MakeMixed()
        {
            var document = new WatchTableDocument(current!); document.Duplicate(0, 1);
            var xml = System.Xml.Linq.XDocument.Load(new MemoryStream(document.ToBytes()));
            var comment = xml.Root!.Elements().Last().Element("ObjectList")!.Elements().Last();
            comment.Name = "SW.WatchAndForceTables.PlcTableCommentEntry"; comment.Element("AttributeList")!.Remove();
            using var bytes = new MemoryStream(); xml.Save(bytes); current = bytes.ToArray();
        }
        internal void NewInstance() => epoch++;
        internal void SelectOriginal() { project = Original; current = applied; epoch++; }
        private JsonObject Capture()
        {
            string hash = new((char)('a' + SourceVersion), 64);
            var snapshot = new EngineeringSnapshotIdentity("capture-" + epoch, hash);
            var fb = new EngineeringObjectReference(new(Original, "/PLC", "/PLC", "/PLC/FB", "block"), snapshot, hash);
            var db = fb with { Object = fb.Object with { ObjectPath = "/PLC/DB_A" } };
            WatchTableCandidate Candidate(string id, string member, string type) => new(id, "instanceDb", db, db, [member], type, "Static", "Captured diagnostic " + member,
                "capturedDeclaration", new JsonObject { ["observationId"] = snapshot.ObservationId, ["sourceSha256"] = hash, ["objectPath"] = "/PLC/DB_A",
                    ["declaration"] = new JsonObject { ["storagePath"] = "/PLC/DB_A", ["memberPath"] = new JsonArray(member), ["datatype"] = type, ["contentSha256"] = hash } },
                ["DB_A", member], StorageClass: "instanceMember");
            string evidence = Path.Combine(Root, "source.txt"); if (!File.Exists(evidence)) File.WriteAllText(evidence, "Authored native source evidence");
            return JsonSerializer.SerializeToNode(new { artifacts = new[] { new { path = evidence, sha256 = WatchTableDocument.Hash(File.ReadAllBytes(evidence)) } },
                binding = new { projectPath = Original, plcPath = "/PLC", blockPath = "/PLC/FB", instanceDbPath = "/PLC/DB_A", blockSha256 = hash, instanceSha256 = hash },
                selectors = new { active = "\"DB_A\".Active", count = "\"DB_A\".Count" },
                request = new WatchTablePlanningRequest { ExpectedSource = fb, CapturedSource = fb, Instance = new(db, []), TableName = "Diagnostic", Candidates = [Candidate("active", "Active", "Bool"), Candidate("count", "Count", "DInt")] } }, BridgeProtocol.Json)!.AsObject();
        }
        private JsonObject Snapshot(JsonObject args)
        {
            string name = args["tableName"]?.GetValue<string>() ?? "Diagnostic"; byte[]? data = name == "MilestoneWatch" ? Template : current;
            byte[] bytes = data ?? WatchTableDocument.AbsentBytes("root", name);
            string file = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".capture"); File.WriteAllBytes(file, bytes);
            string hash = data is null ? WatchTableDocument.Hash(bytes) : WatchTableDocument.SemanticHash(bytes);
            var document = data is null ? null : new WatchTableDocument(data);
            var observation = new NativeTargetObservation(new(new(project, "/PLC", "/PLC", "/PLC/watch/root/" + name, "watchTable"), new("capture-" + epoch, new((char)('b' + SourceVersion), 64)), hash),
                project, "epoch-" + epoch, new('c', 64), new('d', 64), "hardware", null, true, [new("offline", "observed", new('e', 64)), new("watchImport", "observed", new('f', 64))]);
            var result = JsonSerializer.SerializeToNode(new { observation, processId = epoch, projectModified = false, peerSha256 = new string('9', 64),
                peers = new { languages = new { active = ActiveCultures } },
                target = new { name, group = "root", exists = data is not null, artifact = new { path = file, sha256 = WatchTableDocument.Hash(bytes) }, semanticSha256 = hash, isConsistent = true,
                    rows = document is null ? [] : Enumerable.Range(0, document.Count).Select(i => new { kind = document.RowKind(i), name = document.Field(i, "Name"), address = document.Field(i, "Address"),
                        displayFormat = document.Field(i, "DisplayFormat"), modifyValue = document.Field(i, "ModifyValue"), modifyIntention = false }).ToArray() } }, BridgeProtocol.Json)!.AsObject();
            result["peers"]!["block:/PLC/FB"] = new string((char)('a' + SourceVersion), 64);
            result["peers"]!["block:/PLC/DB_A"] = new string((char)('a' + SourceVersion), 64);
            return result;
        }
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            var args = JsonSerializer.SerializeToNode(arguments, BridgeProtocol.Json)!.AsObject(); object response;
            switch (operation)
            {
                case "watch_capture_instance_native": response = Capture(); break;
                case "watch_read_native": response = Snapshot(args); break;
                case "watch_begin": Begins++; var begin = Snapshot(args); begin["leaseToken"] = "lease"; response = begin; break;
                case "watch_checkpoint":
                    Archives++; string archive = Path.Combine(Root, "baseline.zap21"); File.WriteAllText(archive, "fake-checkpoint");
                    response = new { checkpoint = new { path = archive, sha256 = WatchTableDocument.Hash(File.ReadAllBytes(archive)) }, snapshot = Snapshot(new()) }; break;
                case "watch_apply_native": Imports++; current = File.ReadAllBytes((string)args["inputPath"]!); applied = current;
                    if (LoseImport) throw new IOException("Lost native reply");
                    var after = Snapshot(new()); after["saved"] = true; response = after; break;
                case "watch_end": response = new { released = true }; break;
                case "watch_restore_native": Retrievals++; project = Path.Combine((string)args["destination"]!, "fixture.ap21"); current = baseline; epoch++;
                    if (LoseRestore) throw new IOException("Lost retrieval reply");
                    response = new { project = new { path = project, name = "Fixture" }, processId = epoch, sessionEpoch = "epoch-" + epoch }; break;
                default: throw new Exception("Unexpected native operation " + operation);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(response, BridgeProtocol.Json));
        }
    }
}
