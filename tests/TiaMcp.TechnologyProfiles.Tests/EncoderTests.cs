using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class EncoderTests
{
    internal static void Run(string parent, JsonObject originalReview, JsonObject originalPacket)
    {
        string root = Path.Combine(parent, "encoders-v4"); Directory.CreateDirectory(root); var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        void Reject(Action action, string label, string code = "InvalidTechnologyProfile")
        { try { action(); } catch (BridgeException e) when (e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var model = originalReview["model"]!.DeepClone().AsObject(); string plc = (string)originalPacket["before"]!["identity"]!["plcPath"]!, technology = (string)originalPacket["technologyPath"]!;
        string hardware = plc + "/hardware/Drive", block = plc + "/blocks/DB", otherPlc = "/projects/Fixture/OtherPLC", foreign = otherPlc + "/hardware/Drive";
        model["nodes"]!.AsArray().Single(n => (string?)n!["objectPath"] == technology)!["metadata"]!["systemLibraryType"] = "TO_ExternalEncoder";
        JsonObject Node(string path, string kind, string owner) => new() { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = "Fixture", ["origin"] = "native", ["plcPath"] = owner, ["parentId"] = owner, ["metadata"] = new JsonObject() };
        model["nodes"]!.AsArray().Add(Node(block, "block", plc)); var otherNode = Node(otherPlc, "plc", otherPlc); otherNode["parentId"] = "/projects/Fixture"; model["nodes"]!.AsArray().Add(otherNode); model["nodes"]!.AsArray().Add(Node(foreign, "hardware", otherPlc));
        var reviews = new ProjectReviewStore(root); string id = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        var review = reviews.Read(id); var uploads = new ArtifactUploadStore(root); var store = new TechnologyProfileWorkflowStore(root);
        (string Id, string Ref) Upload(byte[] bytes, string name = "supplied.json")
        { var b = uploads.Begin(name, bytes.Length, Hash(bytes)); for (int o = 0; o < bytes.Length; o += b.MaxChunkBytes) uploads.Write(b.UploadId, o, Convert.ToBase64String(bytes, o, Math.Min(b.MaxChunkBytes, bytes.Length - o))); return (b.UploadId, uploads.Commit(b.UploadId).ArtifactRef!); }
        var driveSnapshot = new JsonObject { ["identity"] = new JsonObject { ["driveId"] = "supplied-drive", ["family"] = "supplied-family", ["orderNumber"] = "supplied-order", ["firmware"] = "supplied-version", ["parameterSet"] = null, ["unknownReason"] = null },
            ["valueDomain"] = "offline", ["parametersComplete"] = false, ["parameters"] = new JsonArray(), ["telegramsComplete"] = true,
            ["telegrams"] = new JsonArray(new JsonObject { ["id"] = "encoder", ["type"] = "MainTelegram", ["number"] = 81, ["inputBytes"] = 8, ["outputBytes"] = 4, ["addressesComplete"] = true, ["addresses"] = new JsonArray(), ["unknownReason"] = null }) };
        var drivePacket = new JsonObject { ["schemaVersion"] = "drive-profile-review-request.v2", ["reviewId"] = id, ["hardwarePath"] = hardware, ["afterKind"] = "observation", ["before"] = driveSnapshot.DeepClone(), ["after"] = driveSnapshot.DeepClone() };
        drivePacket["after"]!["telegrams"]![0]!["number"] = 83;
        JsonObject SaveDrive(JsonObject packet) { var up = Upload(JsonSerializer.SerializeToUtf8Bytes(packet, BridgeProtocol.Json)); var receipt = new DriveProfileWorkflowStore(root).Review((string)packet["reviewId"]!, up.Ref); uploads.Release(up.Id); return receipt; }
        var driveReceipt = SaveDrive(drivePacket); string driveId = (string)driveReceipt["recordId"]!;
        JsonObject Selector(string path) => new() { ["nodeId"] = path, ["objectPath"] = path };
        JsonObject Address(int? value, string unit) => new() { ["value"] = value, ["unit"] = unit };
        JsonObject Row(string key, string route, JsonNode? target) => new() { ["id"] = key, ["provider"] = "supplied-provider", ["interfaceName"] = "SensorInterface", ["state"] = "connected", ["route"] = route, ["sensorIndex"] = 1, ["connectOption"] = "Default", ["target"] = target, ["unknownReason"] = null };
        var rows = new JsonArray(Row("module", "module", Selector(hardware)), Row("split", "splitModules", new JsonObject { ["inputModule"] = Selector(hardware), ["outputModule"] = Selector(foreign) }),
            Row("channel", "channel", new JsonObject { ["module"] = Selector(hardware), ["number"] = 0, ["type"] = "Technology", ["direction"] = "Complex" }),
            Row("😀", "addresses", new JsonObject { ["plc"] = Selector(plc), ["input"] = Address(256, "byte"), ["output"] = Address(int.MaxValue, "byte") }),
            Row("db", "dbMember", new JsonObject { ["block"] = Selector(block), ["member"] = "Encoder.Data" }),
            Row("telegram", "startdriveTelegram", new JsonObject { ["recordId"] = driveId, ["phase"] = "before", ["telegramId"] = "encoder" }), Row("off", "module", null), Row("\uE000", "unknown", null));
        rows[6]!["state"] = "disconnected"; rows[6]!["sensorIndex"] = 0; rows[7]!["state"] = "unknown"; rows[7]!["sensorIndex"] = null; rows[7]!["unknownReason"] = "Not captured";
        var packet = originalPacket.DeepClone().AsObject(); packet["schemaVersion"] = "technology-profile-review-request.v4"; packet["reviewId"] = id;
        foreach (string phase in new[] { "before", "after" }) packet[phase]!["identity"]!["technologyType"] = "TO_ExternalEncoder";
        packet["sources"] = new JsonArray(); packet["consumers"] = new JsonObject { ["before"] = new JsonArray(), ["after"] = new JsonArray() };
        foreach (string phase in new[] { "before", "after" }) packet["consumers"]![phase]!.AsArray().Add(new JsonObject { ["id"] = "existing-consumer", ["subjectKind"] = "parameter", ["subjectKey"] = "Scale", ["objectPath"] = hardware, ["nodeId"] = hardware, ["member"] = null, ["state"] = "declared", ["reason"] = null });
        packet["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 20, ["maxImpactEdgeVisits"] = 100 };
        packet["encoders"] = new JsonObject { ["before"] = new JsonObject { ["complete"] = false, ["observations"] = rows.DeepClone() }, ["after"] = new JsonObject { ["complete"] = true, ["observations"] = rows.DeepClone() } };
        packet["encoders"]!["after"]!["observations"]![5]!["target"]!["phase"] = "after";
        packet["encoders"]!["after"]!["observations"]![3]!["target"]!["input"]!["value"] = 257;
        JsonArray Rows(JsonObject p, string phase = "before") => p["encoders"]![phase]!["observations"]!.AsArray();
        JsonObject Mutate(Action<JsonObject> edit) { var p = packet.DeepClone().AsObject(); edit(p); return p; }
        JsonObject Save(JsonObject p) { var up = Upload(JsonSerializer.SerializeToUtf8Bytes(p, BridgeProtocol.Json)); try { return store.Review((string)p["reviewId"]!, up.Ref); } finally { uploads.Release(up.Id); } }
        JsonObject Report(JsonObject p) => JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, Save(p)["reportArtifact"]!.AsObject()))!["report"]!.AsObject();
        var receipt = Save(packet); var result = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!["report"]!.AsObject();
        JsonNode Evidence(JsonObject report, int i) => report["encoderConnections"]![i]!["targetEvidence"]!;
        Check((long)Evidence(result, 3)["inputBitAddress"]! == 2048 && (long)Evidence(result, 3)["outputBitAddress"]! == 17179869176L, "Explicit units and maximum Int32 normalize with exact Int64 arithmetic");
        Check((string)Evidence(result, 1)["outputModuleResolution"]! == "differentPlc", "Equal display names do not bind foreign PLC hardware");
        Check((string)Evidence(result, 2)["channelResolution"]! == "notVerified" && (string)Evidence(result, 4)["memberResolution"]! == "notVerified", "Captured objects cannot certify channel or DB member existence");
        Check((string)Evidence(result, 5)["telegramResolution"]! == "retainedDeclaration" && (int)Evidence(result, 13)["telegramEvidence"]!["telegram"]!["number"]! == 83, "Exact retained record and phase resolve original telegram values");
        Check((string)Evidence(result, 5)["externalEncoderNumberCheck"]! == "documentedNumber" && (string)Evidence(result, 5)["telegramChange"]!["status"]! == "changed", "Documented number and underlying drive change remain inspectable without native qualification");
        Check((int)result["summary"]!["changedEncoderCount"]! == 1 && (string)result["encoderChanges"]!.AsArray().Single(r => (string?)r!["id"] == "telegram")!["status"]! == "unchanged", "Required phase selectors do not manufacture connection changes");
        Check((string)Evidence(result, 6)["state"]! == "declaredDisconnected" && (string)Evidence(result, 7)["state"]! == "notCaptured", "Disconnected and unknown remain distinct");
        Check((string)result["encoderCoverage"]!["ownerResolution"]! == "capturedObject" && !(bool)result["encoderCoverage"]!["beforeComplete"]!, "Exact owner and supplied incomplete coverage retained");
        Check(result["consumers"]!.AsArray().Count == 2 && (string)result["consumers"]![0]!["objectResolution"]! == "capturedObject", "Existing supplied consumer context composes with typed encoder rows");
        Check(result["encoderConnections"]!.AsArray().All(r => !(bool)r!["nativeConnectionVerified"]! && !(bool)r["targetCompatibilityVerified"]!), "No encoder observation grants connection or compatibility authority");
        var reorder = Report(Mutate(p => { var r = Rows(p, "after"); var first = r[0]; r.RemoveAt(0); r.Add(first); }));
        Check(JsonNode.DeepEquals(result["encoderChanges"], reorder["encoderChanges"]), "Exact IDs compare independently of array order");
        var unknown = Report(Mutate(p => { Rows(p)[3]!["target"]!["input"]!["unit"] = "unknown"; Rows(p)[3]!["unknownReason"] = "Readback unit not documented"; }));
        Check(Evidence(unknown, 3)["inputBitAddress"] is null, "Unknown readback units never become byte addresses");
        var contradictions = Report(Mutate(p => { Rows(p)[0]!["sensorIndex"] = 0; Rows(p)[6]!["sensorIndex"] = 2; }));
        Check(contradictions["encoderConnections"]![0]!["contradictions"]!.AsArray().Count == 1 && contradictions["encoderConnections"]![6]!["contradictions"]!.AsArray().Count == 1, "Contradictory connected and disconnected sensor declarations remain explicit");
        foreach (var (field, value) in new (string, JsonNode?)[] { ("route", JsonValue.Create("guessed")), ("state", JsonValue.Create("qualified")), ("sensorIndex", JsonValue.Create(3)), ("sensorIndex", JsonValue.Create(true)), ("connectOption", JsonValue.Create("allowAll")), ("id", JsonValue.Create("bad\nidentity")), ("provider", JsonValue.Create("")), ("unknownReason", JsonValue.Create(7)) })
            Reject(() => Report(Mutate(p => Rows(p)[0]![field] = value?.DeepClone())), "Malformed encoder field refused: " + field + ":" + value);
        Reject(() => Report(Mutate(p => Rows(p).Add(Rows(p)[0]!.DeepClone()))), "Duplicate exact IDs refuse");
        Reject(() => Report(Mutate(p => Rows(p)[0]!["extra"] = true)), "Unknown encoder fields refuse");
        Reject(() => Report(Mutate(p => Rows(p)[0]!["sensorIndex"] = null)), "Unknown sensor requires reason");
        Reject(() => Report(Mutate(p => Rows(p)[0]!["target"] = null)), "Uncaptured connected target requires reason");
        Reject(() => Report(Mutate(p => Rows(p)[0]!["state"] = "disconnected")), "Disconnected cannot retain a target");
        Reject(() => Report(Mutate(p => Rows(p)[3]!["target"]!["input"]!["unit"] = "word")), "Undocumented units refuse");
        Reject(() => Report(Mutate(p => Rows(p)[3]!["target"]!["output"]!["value"] = -1)), "Negative address refuses");
        Reject(() => Report(Mutate(p => Rows(p)[3]!["target"]!["output"]!["value"] = 2147483648L)), "Address above Int32 refuses");
        Reject(() => Report(Mutate(p => Rows(p)[5]!["target"]!["phase"] = "after")), "Cross-phase telegram substitution refuses");
        Reject(() => Report(Mutate(p => Rows(p)[5]!["target"]!["recordId"] = "not-a-hash")), "Malformed retained reference refuses", "InvalidIdentity");
        Reject(() => Report(Mutate(p => p["schemaVersion"] = false)), "Malformed version has structured refusal");
        var missing = Report(Mutate(p => Rows(p)[5]!["target"]!["telegramId"] = "missing")); Check((string)Evidence(missing, 5)["telegramResolution"]! == "missing", "Absent exact telegram ID remains missing");
        var mismatched = Report(Mutate(p => Rows(p)[0]!["target"]!["nodeId"] = block)); Check((string)Evidence(mismatched, 0)["moduleResolution"]! == "identityMismatch", "Path and ID cannot resolve independently");
        var wrong = Report(Mutate(p => Rows(p)[0]!["target"] = Selector(block))); Check((string)Evidence(wrong, 0)["moduleResolution"]! == "wrongKind", "DB cannot substitute hardware");
        foreach (string defect in new[] { "ambiguous", "virtualObject", "scopeUnknown" })
        {
            var changed = model.DeepClone().AsObject(); var selected = changed["nodes"]!.AsArray().Single(n => (string?)n!["objectPath"] == hardware)!;
            if (defect == "ambiguous") { var duplicate = selected.DeepClone(); duplicate["objectPath"] = hardware + "/duplicate"; changed["nodes"]!.AsArray().Add(duplicate); }
            else if (defect == "virtualObject") selected["origin"] = "virtual";
            else selected.AsObject().Remove("plcPath");
            string changedId = (string)reviews.Save(changed, new(), new())["reviewId"]!;
            var changedPacket = Mutate(p => { p["reviewId"] = changedId; foreach (string phase in new[] { "before", "after" }) Rows(p, phase).RemoveAt(5); });
            Check((string)Evidence(Report(changedPacket), 0)["moduleResolution"]! == defect, "Saved hardware identity remains explicit: " + defect);
        }
        var oldDrive = drivePacket.DeepClone().AsObject(); oldDrive["schemaVersion"] = "drive-profile-review-request.v1"; foreach (string phase in new[] { "before", "after" }) { oldDrive[phase]!.AsObject().Remove("telegrams"); oldDrive[phase]!.AsObject().Remove("telegramsComplete"); }
        var oldDriveReceipt = SaveDrive(oldDrive); var untyped = Report(Mutate(p => Rows(p)[5]!["target"]!["recordId"] = oldDriveReceipt["recordId"]!.DeepClone())); Check((string)Evidence(untyped, 5)["telegramResolution"]! == "unsupportedVersion", "Old drive records do not acquire typed telegram evidence");
        var changedReview = model.DeepClone().AsObject(); changedReview["project"]!["projectPath"] = "other.ap21"; string otherId = (string)reviews.Save(changedReview, new(), new())["reviewId"]!;
        var foreignDrive = drivePacket.DeepClone().AsObject(); foreignDrive["reviewId"] = otherId; var foreignReceipt = SaveDrive(foreignDrive);
        Reject(() => Report(Mutate(p => Rows(p)[5]!["target"]!["recordId"] = foreignReceipt["recordId"]!.DeepClone())), "Different saved review cannot supply encoder telegram");
        var dependentPacket = Mutate(p => { foreach (string phase in new[] { "before", "after" }) Rows(p, phase).Clear(); });
        for (int i = 0; i < 9; i++)
        {
            var nextDrive = drivePacket.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) nextDrive[phase]!["identity"]!["driveId"] = "dependency-" + i;
            var nextReceipt = SaveDrive(nextDrive); Rows(dependentPacket).Add(Row("dependency-" + i, "startdriveTelegram", new JsonObject { ["recordId"] = nextReceipt["recordId"]!.DeepClone(), ["phase"] = "before", ["telegramId"] = "encoder" }));
            if (i == 7) Check(Report(dependentPacket)["encoderConnections"]!.AsArray().Count == 8, "Eight distinct original drive dependencies are supported");
        }
        var retainedBefore = Directory.GetFiles(Path.Combine(root, "technology-profile-reviews")).Order().ToArray();
        Reject(() => Report(dependentPacket), "Ninth distinct drive dependency refuses");
        Check(retainedBefore.SequenceEqual(Directory.GetFiles(Path.Combine(root, "technology-profile-reviews")).Order()), "Dependency-count refusal publishes no technology record");
        var bytePacket = Mutate(p => { foreach (string phase in new[] { "before", "after" }) Rows(p, phase).Clear(); });
        var largeParameters = new JsonArray();
        for (int i = 0; i < 4096; i++) largeParameters.Add(new JsonObject { ["name"] = "p" + i, ["number"] = i, ["arrayIndex"] = -1, ["parentName"] = null, ["unit"] = "", ["access"] = "unknown", ["value"] = new JsonObject { ["kind"] = "scalar", ["type"] = "opaque", ["text"] = "0" }, ["metadata"] = new JsonObject { ["padding"] = "" } });
        var byteDrive = drivePacket.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) byteDrive[phase]!["parameters"] = largeParameters.DeepClone();
        int padding = ((4 << 20) - JsonSerializer.SerializeToUtf8Bytes(byteDrive, BridgeProtocol.Json).Length - 4096) / 8192;
        foreach (string phase in new[] { "before", "after" }) foreach (var parameter in byteDrive[phase]!["parameters"]!.AsArray()) parameter!["metadata"]!["padding"] = new string('x', padding);
        long dependencyBytes = 0;
        for (int i = 0; i < 8 && dependencyBytes <= (64L << 20); i++)
        {
            foreach (string phase in new[] { "before", "after" }) byteDrive[phase]!["identity"]!["driveId"] = "byte-limit-" + i;
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(byteDrive, BridgeProtocol.Json); Check(raw.Length <= (4 << 20), "Byte-budget fixture respects original input bound: " + i);
            var padded = new byte[4 << 20]; Array.Fill(padded, (byte)' '); raw.CopyTo(padded, 0); var up = Upload(padded);
            var retained = new DriveProfileWorkflowStore(root).Review(id, up.Ref); uploads.Release(up.Id);
            dependencyBytes += new FileInfo(Path.Combine(root, (string)retained["input"]!["path"]!)).Length + new FileInfo(Path.Combine(root, (string)retained["reportArtifact"]!["path"]!)).Length;
            Rows(bytePacket).Add(Row("bytes-" + i, "startdriveTelegram", new JsonObject { ["recordId"] = retained["recordId"]!.DeepClone(), ["phase"] = "before", ["telegramId"] = "encoder" }));
        }
        Check(dependencyBytes > (64L << 20) && Rows(bytePacket).Count <= 8, "Genuine retained record and original-input bytes cross the independent 64 MiB bound");
        Reject(() => Report(bytePacket), "Dependency byte bound refuses independently of dependency count");
        var wideDrive = drivePacket.DeepClone().AsObject(); var wideAddresses = new JsonArray();
        for (int i = 0; i < 16; i++) wideAddresses.Add(new JsonObject { ["id"] = "address-" + i, ["plcPath"] = "/missing/" + new string('x', 8000), ["direction"] = "Input", ["context"] = "supplied", ["startAddress"] = 0, ["addressUnit"] = "bit", ["length"] = 0, ["lengthUnit"] = "bit", ["unknownReason"] = null });
        foreach (string phase in new[] { "before", "after" }) wideDrive[phase]!["telegrams"]![0]!["addresses"] = wideAddresses.DeepClone();
        var wideDriveReceipt = SaveDrive(wideDrive);
        var large = Mutate(p => { p["afterKind"] = "observation"; foreach (string phase in new[] { "before", "after" }) { var r = Rows(p, phase); r.Clear(); for (int i = 0; i < 256; i++) r.Add(Row("wide-" + i, "startdriveTelegram", new JsonObject { ["recordId"] = wideDriveReceipt["recordId"]!.DeepClone(), ["phase"] = phase, ["telegramId"] = "encoder" })); } });
        Reject(() => Report(large), "Repeated large retained telegram evidence refuses at incremental report byte bound", "ArtifactCapacity");
        foreach (string phase in new[] { "before", "after" }) while (Rows(large, phase).Count > 1) Rows(large, phase).RemoveAt(1);
        var largeReceipt = Save(large); var largePage = store.Page((string)largeReceipt["recordId"]!, "encoderConnections", 0, 1);
        Check((bool)largePage["rows"]![0]!["omitted"]! && ProjectReviewStore.ReadArtifact(root, largeReceipt["reportArtifact"]!.AsObject()).Length > 120 * 1024, "Oversized encoder evidence is explicitly omitted on pages and retained for exact download");
        var bounded = Mutate(p => { p["afterKind"] = "observation"; foreach (string phase in new[] { "before", "after" }) { var r = Rows(p, phase); r.Clear(); for (int i = 0; i < 256; i++) r.Add(Row(phase + i, "module", Selector(hardware))); } });
        Check(Report(bounded)["encoderConnections"]!.AsArray().Count == 512, "Both full 256-row phases are retained");
        bounded["afterKind"] = "proposal"; Reject(() => Report(bounded), "Combined proposal changes above 500 refuse");
        bounded["afterKind"] = "observation"; Rows(bounded).Add(Row("extra", "module", Selector(hardware))); Reject(() => Report(bounded), "257 observations refuse");
        var bytes = Encoding.UTF8.GetBytes("\uFEFF<Original>日本\r\n</Original>"); var source = Upload(bytes, "source.xml");
        var withSource = Mutate(p => p["sources"] = new JsonArray(new JsonObject { ["label"] = "original", ["purpose"] = "supplied", ["phase"] = "context", ["fileName"] = "source.xml", ["uploadRef"] = source.Ref, ["sha256"] = Hash(bytes), ["bytes"] = bytes.Length }));
        var sourceReceipt = Save(withSource); uploads.Release(source.Id);
        var sourceRecord = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, sourceReceipt["reportArtifact"]!.AsObject()))!;
        Check((string)sourceRecord["report"]!["schemaVersion"]! == "technology-profile-review.v4" && ProjectReviewStore.ReadArtifact(root, sourceRecord["report"]!["sources"]![0]!["artifact"]!.AsObject()).SequenceEqual(bytes), "V4 retains exact source bytes without version downgrade");
        foreach (string section in new[] { "encoderConnections", "encoderChanges", "encoderCoverage" }) Check(store.Page((string)receipt["recordId"]!, section)["rows"]!.AsArray().Count > 0, "Existing page tool exposes " + section);
        Check(JsonNode.DeepEquals(receipt, new TechnologyProfileWorkflowStore(root).Page((string)receipt["recordId"]!)), "V4 survives upload release and store recreation");
        string dependent = Path.Combine(root, (string)driveReceipt["input"]!["path"]!); byte[] original = File.ReadAllBytes(dependent);
        try { File.WriteAllText(dependent, "damaged"); Reject(() => store.Page((string)receipt["recordId"]!), "Changed original drive dependency prevents reopen", "ArtifactChanged"); } finally { File.WriteAllBytes(dependent, original); }
        foreach (string defect in new[] { "version", "report", "authority" })
        {
            var forged = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!;
            if (defect == "version") forged["schemaVersion"] = "technology-profile-review-record.v3";
            else if (defect == "report") forged["report"]!["encoderConnections"]![3]!["targetEvidence"]!["inputBitAddress"] = 1;
            else forged["report"]!["encoderConnections"]![0]!["nativeConnectionVerified"] = true;
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string hash = Hash(data); File.WriteAllBytes(Path.Combine(root, "technology-profile-reviews", hash + ".json"), data);
            Reject(() => store.Page(hash), "Rehashed encoder " + defect + " forgery refuses", defect == "version" ? "InvalidTechnologyProfile" : "ArtifactChanged");
        }
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, reviewId = id, packet, drivePacket, driveReceipt, receipt, oversizedReceipt = largeReceipt, oversizedDriveReceipt = wideDriveReceipt }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(result, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, count = checks.Count, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS typed encoder profiles: {checks.Count} checks; supplied observations only.");
    }
}
