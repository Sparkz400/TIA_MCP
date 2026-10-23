using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

internal static class CompositeTypedTests
{
    internal static void Run(string root, JsonObject baseline, JsonObject drivePacket, JsonObject encoderPacket)
    {
        var checks = new List<string>(); var uploads = new ArtifactUploadStore(root);
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        string Upload(JsonObject value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
            var begun = uploads.Begin("typed.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            uploads.Write(begun.UploadId, 0, Convert.ToBase64String(bytes)); return uploads.Commit(begun.UploadId).ArtifactRef!;
        }
        string reviewId = (string)baseline["before"]!["reviewId"]!;
        var model = new ProjectReviewStore(root).Read(reviewId)["model"]!.DeepClone().AsObject();
        string plc = (string)encoderPacket["before"]!["identity"]!["plcPath"]!, hardware = (string)drivePacket["hardwarePath"]!;
        string project = (string)model["nodes"]!.AsArray().Single(n => (string?)n!["kind"] == "project")!["objectPath"]!;
        string foreignPlc = project + "/OtherPLC", foreignHardware = foreignPlc + "/Hardware", block = plc + "/EncoderDB";
        JsonObject Node(string path, string kind, string parent, string scope) => new() { ["nodeId"] = path, ["objectPath"] = path,
            ["name"] = "Supplied", ["kind"] = kind, ["parentId"] = parent, ["plcPath"] = scope, ["metadata"] = new JsonObject() };
        model["nodes"]!.AsArray().Add(Node(foreignPlc, "plc", project, foreignPlc));
        model["nodes"]!.AsArray().Add(Node(foreignHardware, "hardware", foreignPlc, foreignPlc));
        model["nodes"]!.AsArray().Add(Node(block, "block", plc, plc));
        reviewId = (string)new ProjectReviewStore(root).Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        string oldConnection = (string)baseline["before"]!["connections"]![0]!;
        var connectionRecord = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, "connection-reviews", oldConnection + ".json")))!;
        var connectionPacket = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, (string)connectionRecord["input"]!["path"]!)))!.AsObject();
        connectionPacket["reviewId"] = reviewId;
        string connection = (string)new ConnectionWorkflowStore(root).Review(reviewId, Upload(connectionPacket))["recordId"]!;
        string Drive(JsonObject packet) => (string)new DriveProfileWorkflowStore(root).Review(reviewId, Upload(packet))["recordId"]!;
        string Technology(JsonObject packet) => (string)new TechnologyProfileWorkflowStore(root).Review(reviewId, Upload(packet))["recordId"]!;
        JsonObject Selector(string kind, string phase, string record, string id) => new() { ["kind"] = kind, ["phase"] = phase, ["recordRef"] = record, ["id"] = id };
        JsonObject Map(string id, string phase, string purpose, JsonNode source, JsonNode target) => new() {
            ["id"] = id, ["phase"] = phase, ["purpose"] = purpose, ["reason"] = "Explicit original reference", ["source"] = source.DeepClone(), ["target"] = target.DeepClone() };
        var drive = drivePacket.DeepClone().AsObject(); drive["reviewId"] = reviewId;
        foreach (string phase in new[] { "before", "after" })
        {
            var telegram = drive[phase]!["telegrams"]![0]!;
            telegram["addresses"] = new JsonArray(new JsonObject { ["id"] = "input", ["plcPath"] = plc, ["direction"] = "Input", ["context"] = "supplied", ["startAddress"] = int.MaxValue,
                ["addressUnit"] = "byte", ["length"] = 64, ["lengthUnit"] = "bit", ["unknownReason"] = null });
        }
        string driveRef = Drive(drive);
        var encoder = encoderPacket.DeepClone().AsObject(); encoder["reviewId"] = reviewId;
        foreach (string phase in new[] { "before", "after" })
        {
            encoder[phase]!["identity"]!["technologyType"] = "TO_ExternalEncoder";
            encoder["encoders"]![phase]!["observations"]![0]!["target"]!["recordId"] = driveRef;
        }
        string encoderRef = Technology(encoder);
        var request = baseline.DeepClone().AsObject(); request["schemaVersion"] = "composite-engineering-request.v3";
        request["mappings"] = new JsonArray(); request["correspondences"] = new JsonArray();
        foreach (string phase in new[] { "before", "after" })
        {
            request[phase]!["reviewId"] = reviewId; request[phase]!["connections"] = new JsonArray(connection);
            request[phase]!["drives"] = new JsonArray(driveRef); request[phase]!["technology"] = new JsonArray(encoderRef);
            var aml = baseline["mappings"]!.AsArray().First(m => (string?)m!["phase"] == phase && (string?)m["purpose"] == "parameter")!["source"]!;
            var telegram = Selector("driveTelegram", phase, driveRef, "main"); var sensor = Selector("technologyEncoder", phase, encoderRef, "sensor");
            var address = Selector("driveTelegramAddress", phase, driveRef, "main"); address["addressId"] = "input";
            request["mappings"]!.AsArray().Add(Map(phase + "-telegram", phase, "telegram", aml, telegram));
            request["mappings"]!.AsArray().Add(Map(phase + "-encoder", phase, "encoder", telegram, sensor));
            request["mappings"]!.AsArray().Add(Map(phase + "-address", phase, "telegram", aml, address));
        }
        var cases = new JsonArray();
        JsonObject Build(JsonObject? packet = null)
        {
            var value = packet ?? request; var result = CompositeEngineeringReview.Build(root, JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json));
            cases.Add(new JsonObject { ["request"] = value.DeepClone(), ["report"] = result.DeepClone() }); return result;
        }
        JsonObject Change(Action<JsonObject> edit) { var packet = request.DeepClone().AsObject(); edit(packet); return packet; }
        void Reject(JsonObject packet, string label)
        { try { Build(packet); } catch (BridgeException e) when (e.Code == "InvalidCompositeEngineeringReview") { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
        var report = Build();
        Check(report["mappings"]!.AsArray().All(m => (string?)m!["state"] == "declaredAssociation"), "Exact telegram, encoder and address declarations resolve in both phases");
        Check(report["selections"]!.AsArray().Any(s => (string?)s!["selector"]!["kind"] == "driveTelegramAddress" && (long)s["evidence"]!["address"]!["startBit"]! == 17179869176L), "Original independently unit-tagged Int64 address retained");
        Check(report["consumerContexts"]!.AsArray().All(c => c!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!)), "Explicit retained telegram links add declared owner context");
        Check(report["consumerContexts"]!.AsArray().All(c => c!["rootBindings"]!.AsArray().Any(r => (string?)r!["role"] == "encoderOwnerContext")), "Mapped encoder reaches owner context in both phases");
        Check(report["mappings"]![1]!["constraints"]!.AsArray().Any(c => (string?)c!["kind"] == "nativeEligibility" && (string?)c["state"] == "notVerified"), "Documented telegram number never qualifies native eligibility");
        Check(report["contexts"]!["before"]!["components"]!.AsArray().Any(c => c!["encoderCoverage"] is not null && c["encoderPolicy"] is not null), "Original typed coverage and policy retained");
        foreach (string version in new[] { "v1", "v2" }) Reject(Change(r => r["schemaVersion"] = "composite-engineering-request." + version), "Older " + version + " cannot reinterpret typed selectors");
        Reject(Change(r => r["mappings"]![1]!["source"]!["phase"] = "after"), "Wrong selector phase refuses");
        Reject(Change(r => r["mappings"]![2]!["target"]!["unknown"] = true), "Extra address selector fields refuse");
        Reject(Change(r => r["before"]!["drives"] = new JsonArray()), "Typed selector requires context-listed drive");
        foreach (string id in new[] { "Main", "missing" })
            Check((string)Build(Change(r => r["mappings"]![0]!["target"]!["id"] = id))["mappings"]![0]!["state"]! == "unresolvedSelection", "Exact telegram ID is not normalized: " + id);
        Check((string)Build(Change(r => r["mappings"]![2]!["target"]!["addressId"] = "missing"))["mappings"]![2]!["state"]! == "unresolvedSelection", "Missing address stays unresolved");
        var other = drive.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) other[phase]!["identity"]!["driveId"] = "Other";
        string otherRef = Drive(other);
        var different = Build(Change(r => { r["before"]!["drives"]!.AsArray().Add(otherRef); r["mappings"]![1]!["source"]!["recordRef"] = otherRef; }));
        Check((string)different["mappings"]![1]!["state"]! == "constraintMismatch", "Same telegram number and address in another record cannot replace original reference");
        Check(!different["impacts"]![0]!["destinationKeys"]!.AsArray().Any(k => (string)k! == CanonicalJson.Serialize(request["mappings"]![1]!["target"]!)), "Rejected reference never creates declared impact edge");
        JsonObject WithEncoder(Action<JsonObject> edit)
        {
            var packet = encoder.DeepClone().AsObject(); edit(packet); string record = Technology(packet);
            return Change(r => { foreach (string phase in new[] { "before", "after" }) r[phase]!["technology"] = new JsonArray(record);
                foreach (var mapping in r["mappings"]!.AsArray()) if ((string?)mapping!["target"]!["kind"] == "technologyEncoder") mapping["target"]!["recordRef"] = record; });
        }
        foreach (var (state, sensor, expected) in new[] { ("disconnected", 0, "disconnected"), ("unknown", 1, "unresolved"), ("connected", 0, "contradictoryDeclaration") })
        {
            var packet = WithEncoder(e => { var row = e["encoders"]!["before"]!["observations"]![0]!; row["state"] = state; row["sensorIndex"] = sensor; row["unknownReason"] = "Fixture state"; if (state == "disconnected") row["target"] = null; });
            var value = Build(packet);
            Check((string)value["mappings"]![1]!["targetState"]! == expected && (string)value["mappings"]![1]!["state"]! == "unresolvedSelection", "Encoder " + expected + " cannot create mapping");
            Check(!value["consumerContexts"]![0]!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!), "Encoder " + expected + " cannot create consumer link");
        }
        var domainMismatch = Build(WithEncoder(e => { e["afterKind"] = "observation"; e["before"]!["valueDomain"] = "online"; }));
        Check((string)domainMismatch["mappings"]![1]!["state"]! == "constraintMismatch", "Different online/offline domains remain incompatible declarations");
        Check(!domainMismatch["consumerContexts"]![0]!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!), "Domain mismatch cannot enter consumer closure");
        var noDrive = Build(Change(r => { r["before"]!["drives"] = new JsonArray(); r["mappings"] = new JsonArray(); }));
        Check(!noDrive["consumerContexts"]![0]!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!), "Indirect retained drive outside explicit context cannot create consumer link");
        var reversed = Build(Change(r => r["mappings"] = new JsonArray(r["mappings"]!.AsArray().Reverse().Select(m => m!.DeepClone()).ToArray())));
        Check(reversed["mappings"]!.AsArray().All(m => (string?)m!["state"] == "declaredAssociation"), "Mapping reorder preserves exact reference results");
        JsonObject Target(string path) => new() { ["nodeId"] = path, ["objectPath"] = path };
        JsonObject ModelSelector(string path) => new() { ["kind"] = "model", ["phase"] = "before", ["reviewId"] = reviewId, ["nodeId"] = path, ["objectPath"] = path };
        var outside = Build(Change(r => {
            r["before"]!["drives"] = new JsonArray(); r["before"]!["connections"] = new JsonArray();
            r["mappings"] = new JsonArray(Map("outside", "before", "dependent", ModelSelector(plc + "/Peer"), Selector("technologyEncoder", "before", encoderRef, "sensor"))); }));
        Check((string)outside["mappings"]![0]!["targetState"]! == "unresolved" && (string)outside["mappings"]![0]!["state"]! == "unresolvedSelection", "Dependent mapping cannot expose encoder hardware outside its explicit drive context");
        foreach (var (route, target, source) in new (string, JsonObject, string)[] {
            ("module", Target(hardware), hardware),
            ("splitModules", new() { ["inputModule"] = Target(hardware), ["outputModule"] = Target(plc + "/Peer") }, hardware),
            ("channel", new() { ["module"] = Target(hardware), ["number"] = 0, ["type"] = "supplied", ["direction"] = "Input" }, hardware),
            ("addresses", new() { ["plc"] = Target(plc), ["input"] = new JsonObject { ["value"] = 8, ["unit"] = "bit" }, ["output"] = new JsonObject { ["value"] = null, ["unit"] = "unknown" } }, plc),
            ("dbMember", new() { ["block"] = Target(block), ["member"] = "Exact.Member" }, block) })
        {
            var packet = WithEncoder(e => { var row = e["encoders"]!["before"]!["observations"]![0]!; row["route"] = route; row["target"] = target.DeepClone(); row["unknownReason"] = "Native member/channel/address eligibility unverified"; });
            packet["mappings"]![1]!["source"] = ModelSelector(source);
            var value = Build(packet);
            Check((string)value["mappings"]![1]!["state"]! == "declaredAssociation", "Exact original model reference supports declared " + route + " route");
            Check(value["consumerContexts"]![0]!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!), "Explicit " + route + " context retains consumer link");
            packet["mappings"]![1]!["source"] = ModelSelector(foreignHardware);
            Check((string)Build(packet)["mappings"]![1]!["state"]! == "constraintMismatch", "Other captured PLC object cannot substitute for " + route + " target");
        }
        var foreign = Build(WithEncoder(e => { var row = e["encoders"]!["before"]!["observations"]![0]!; row["route"] = "module"; row["target"] = Target(foreignHardware); }));
        Check((string)foreign["mappings"]![1]!["targetState"]! == "unresolved", "Original encoder target in another PLC remains unresolved");
        JsonObject WithDrive(Action<JsonObject> edit)
        {
            var packet = drive.DeepClone().AsObject(); edit(packet); string record = Drive(packet);
            var combined = WithEncoder(e => { foreach (string phase in new[] { "before", "after" }) e["encoders"]![phase]!["observations"]![0]!["target"]!["recordId"] = record; });
            foreach (string phase in new[] { "before", "after" }) combined[phase]!["drives"] = new JsonArray(record);
            foreach (var mapping in combined["mappings"]!.AsArray()) foreach (string side in new[] { "source", "target" })
                if ((string?)mapping![side]!["kind"] is "driveTelegram" or "driveTelegramAddress") mapping[side]!["recordRef"] = record;
            return combined;
        }
        var foreignDrive = Build(WithDrive(d => d["hardwarePath"] = foreignHardware));
        Check((string)foreignDrive["mappings"]![1]!["targetState"]! == "unresolved", "Same retained telegram number on another PLC cannot establish encoder wiring");
        var wrongNumber = Build(WithDrive(d => d["before"]!["telegrams"]![0]!["number"] = 999));
        Check((string)wrongNumber["mappings"]![1]!["state"]! == "constraintMismatch", "Known external-encoder number contradiction remains explicit");
        Check(!wrongNumber["consumerContexts"]![0]!["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyEncoder" && (bool)a["eligible"]!), "Known number contradiction cannot create consumer link");
        var unknownNumber = Build(WithDrive(d => d["before"]!["telegrams"]![0]!["number"] = null));
        Check((string)unknownNumber["mappings"]![1]!["state"]! == "declaredAssociation" && unknownNumber["mappings"]![1]!["constraints"]!.AsArray().Any(c => (string?)c!["state"] == "unknown"), "Unknown number retains exact association and explicit unknown eligibility");
        var oldDrive = drive.DeepClone().AsObject(); oldDrive["schemaVersion"] = "drive-profile-review-request.v1";
        foreach (string phase in new[] { "before", "after" }) { oldDrive[phase]!.AsObject().Remove("telegrams"); oldDrive[phase]!.AsObject().Remove("telegramsComplete"); }
        string oldDriveRef = Drive(oldDrive);
        Check((string)Build(Change(r => { r["before"]!["drives"]!.AsArray().Add(oldDriveRef); r["mappings"]![0]!["target"]!["recordRef"] = oldDriveRef; }))["mappings"]![0]!["targetState"]! == "unsupportedVersion", "Legacy drive record cannot acquire typed telegram observations");
        var reorderedTelegrams = Build(WithDrive(d => { foreach (string phase in new[] { "before", "after" }) {
            var spare = d[phase]!["telegrams"]![0]!.DeepClone(); spare["id"] = "spare";
            d[phase]!["telegrams"]!.AsArray().Insert(0, spare); } }));
        Check(reorderedTelegrams["mappings"]!.AsArray().All(m => (string?)m!["state"] == "declaredAssociation") &&
            reorderedTelegrams["selections"]!.AsArray().Any(s => (string?)s!["selector"]!["kind"] == "driveTelegram" && (int)s["evidence"]!["sourceIndex"]! == 1), "Telegram order changes retain exact ID selection and original index");
        var reorderedEncoders = Build(WithEncoder(e => { foreach (string phase in new[] { "before", "after" }) {
            var spare = e["encoders"]![phase]!["observations"]![0]!.DeepClone(); spare["id"] = "spare";
            e["encoders"]![phase]!["observations"]!.AsArray().Insert(0, spare); } }));
        Check(reorderedEncoders["mappings"]!.AsArray().All(m => (string?)m!["state"] == "declaredAssociation") &&
            reorderedEncoders["selections"]!.AsArray().Any(s => (string?)s!["selector"]!["kind"] == "technologyEncoder" && (int)s["evidence"]!["sourceIndex"]! == 1), "Encoder order changes retain exact ID selection and original index");
        var path = Path.Combine(root, "typed-v3"); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "request.json"), request.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(path, "report.json"), report.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(path, "cases.json"), cases.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(path, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, requestPath = Path.Combine(path, "request.json") }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(path, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS composite typed mappings: {checks.Count} checks; exact supplied declarations only.");
    }
}
