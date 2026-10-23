using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

internal static class CompositeConsumerTests
{
    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "consumer-v2"); Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        byte[] Bytes(JsonNode value) => JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
        JsonObject Read(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        JsonObject Json(object value) => JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!.AsObject();
        var original = Read(Path.Combine(parent, "request.json"));
        var model = Read(Path.Combine(parent, "project-reviews", (string)original["before"]!["reviewId"]! + ".json"))["model"]!.DeepClone().AsObject();
        const string plc = "/projects/Fixture/PLC", hw = plc + "/Hardware", axis = plc + "/Axis";
        string caller = plc + "/Caller", device = plc + "/ExtraDevice", connectionUser = plc + "/ConnectionUser", axisUser = plc + "/AxisUser", tail = plc + "/Tail";
        string foreign = "/projects/Fixture/OtherPLC/Foreign", ghost = plc + "/Virtual", wrong = plc + "/Wrong";
        JsonObject Node(string path, string kind = "block", string? owner = plc) => new() { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind,
            ["name"] = "Same name", ["parentId"] = owner, ["plcPath"] = owner, ["metadata"] = new JsonObject() };
        foreach (string path in new[] { caller, connectionUser, axisUser, tail, wrong }) model["nodes"]!.AsArray().Add(Node(path));
        model["nodes"]!.AsArray().Add(Node(device, "hardware")); model["nodes"]!.AsArray().Add(Node(foreign, "hardware", "/projects/Fixture/OtherPLC"));
        var virtualNode = Node(ghost); virtualNode["origin"] = "virtual"; model["nodes"]!.AsArray().Add(virtualNode);
        JsonObject Edge(string id, string from, string to, string origin = "parsedSupported") => new() {
            ["edgeId"] = id, ["kind"] = "dependsOn", ["source"] = new JsonObject { ["nodeId"] = from, ["objectPath"] = from, ["resolution"] = "resolved" },
            ["target"] = new JsonObject { ["nodeId"] = to, ["objectPath"] = to, ["resolution"] = "resolved" },
            ["evidence"] = new JsonObject { ["origin"] = origin, ["resolution"] = "resolved", ["syntheticFixture"] = true } };
        model["edges"] = new JsonArray(Edge("caller", caller, hw), Edge("device", device, caller), Edge("tail", tail, axisUser),
            Edge("unsupported", wrong, hw, "unverified"), Edge("virtual", ghost, hw));
        string reviewId = (string)new ProjectReviewStore(root).Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        var uploads = new ArtifactUploadStore(root);
        string Upload(byte[] bytes, string name = "fixture.json")
        {
            var u = uploads.Begin(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            for (int offset = 0; offset < bytes.Length; offset += u.MaxChunkBytes) uploads.Write(u.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(u.MaxChunkBytes, bytes.Length - offset)));
            return uploads.Commit(u.UploadId).ArtifactRef!;
        }
        string ComponentInput(string field, string directory)
        {
            string id = (string)original["before"]![field]![0]!;
            return Path.Combine(parent, (string)Read(Path.Combine(parent, directory, id + ".json"))["input"]!["path"]!);
        }
        var connection = Read(ComponentInput("connections", "connection-reviews")); connection["reviewId"] = reviewId;
        foreach (string phase in new[] { "before", "after" }) connection[phase]!["consumers"] = new JsonArray(
            new JsonObject { ["objectPath"] = connectionUser, ["connectionId"] = "c", ["reason"] = "Declared supplied consumer" },
            new JsonObject { ["objectPath"] = "missing", ["connectionId"] = "c", ["reason"] = "Unknown saved object" },
            new JsonObject { ["objectPath"] = wrong, ["connectionId"] = "absent", ["reason"] = "Unknown connection" });
        string connectionId = (string)new ConnectionWorkflowStore(root).Review(reviewId, Upload(Bytes(connection)))["recordId"]!;
        var drive = Read(ComponentInput("drives", "drive-profile-reviews")); drive["reviewId"] = reviewId;
        string driveId = (string)new DriveProfileWorkflowStore(root).Review(reviewId, Upload(Bytes(drive)))["recordId"]!;
        var technology = Read(ComponentInput("technology", "technology-profile-reviews")); technology["reviewId"] = reviewId;
        technology["schemaVersion"] = "technology-profile-review-request.v3"; technology["sources"] = new JsonArray();
        technology["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 50, ["maxImpactEdgeVisits"] = 500 };
        JsonObject Consumer(string id, string subject, string path, string? nodeId = null) => new() { ["id"] = id, ["subjectKind"] = "relationship",
            ["subjectKey"] = subject, ["objectPath"] = path, ["nodeId"] = nodeId ?? path, ["member"] = "UnverifiedMember", ["state"] = "declared", ["reason"] = null };
        technology["consumers"] = new JsonObject();
        foreach (string phase in new[] { "before", "after" })
        {
            var relations = technology[phase]!["relationships"]!.AsArray(); relations[0]!["targetPath"] = device;
            var cross = relations[0]!.DeepClone(); cross["id"] = "foreign"; cross["targetPath"] = foreign; relations.Add(cross);
            var cycle = relations[0]!.DeepClone(); cycle["id"] = "cycle"; cycle["sourcePath"] = device; cycle["targetPath"] = axis; relations.Add(cycle);
            technology["consumers"]![phase] = new JsonArray(Consumer("axis", "actor", axisUser), Consumer("missingSubject", "absent", wrong),
                Consumer("mismatchedId", "actor", wrong, "wrong-id"), Consumer("crossScope", "actor", foreign), Consumer("disconnected", "off", wrong));
        }
        string technologyId = (string)new TechnologyProfileWorkflowStore(root).Review(reviewId, Upload(Bytes(technology)))["recordId"]!;
        var request = original.DeepClone().AsObject(); request["schemaVersion"] = "composite-engineering-request.v2";
        request["mappings"] = new JsonArray(); request["correspondences"] = new JsonArray();
        foreach (string phase in new[] { "before", "after" })
        {
            string oldAml = (string)original[phase]!["automationMlRef"]!;
            string amlRef = (string)Json(new AutomationMlWorkflowStore(root).Inspect(Upload(File.ReadAllBytes(Path.Combine(parent, "automationml-workflow", oldAml.Split(':')[1], "original.aml")), "fixture.aml")))["recordRef"]!;
            request[phase] = new JsonObject { ["reviewId"] = reviewId, ["automationMlRef"] = amlRef,
                ["connections"] = new JsonArray(connectionId), ["drives"] = new JsonArray(driveId), ["technology"] = new JsonArray(technologyId) };
            var mapping = original["mappings"]!.AsArray().Single(m => (string?)m!["phase"] == phase && (string?)m["purpose"] == "hardware")!.DeepClone();
            mapping["source"]!["recordRef"] = amlRef; mapping["target"]!["reviewId"] = reviewId; request["mappings"]!.AsArray().Add(mapping);
        }
        JsonObject Build(JsonObject? value = null) => CompositeEngineeringReview.Build(root, Bytes(value ?? request));
        var result = Build(); var contexts = result["consumerContexts"]!.AsArray();
        string[] Paths(JsonNode context) => context["candidates"]!.AsArray().Select(c => (string)c!["objectPath"]!).ToArray();
        foreach (var context in contexts)
        {
            var found = Paths(context!); var links = context!["associations"]!.AsArray();
            Check(new[] { hw, hw + "/port", plc + "/Peer", caller, device, axis, axisUser, tail, connectionUser }.All(found.Contains), "Alternating saved and declared consumer closure reaches exact context in " + (string)context["phase"]!);
            Check(!found.Contains(foreign) && !found.Contains(ghost) && !found.Contains(wrong), "Unknown, virtual, mismatched and cross-PLC consumers do not become candidates");
            Check(links.Any(l => (string?)l!["kind"] == "technologyConsumer" && (bool)l["traversed"]! && (string?)l["evidence"]?["memberResolution"] == "notVerified"), "Member uncertainty survives a declared consumer candidate");
            Check(links.Any(l => (string?)l!["sourceState"] == "disconnected" && !(bool)l["traversed"]!), "Disconnected relationship remains visible and untraversed");
            Check(links.Any(l => (string?)l!["fromResolution"] == "outOfScope" && !(bool)l["eligible"]!), "TO scope guard excludes a captured foreign PLC target");
            Check((int)context["coverage"]!["visitedEdges"]! < 100 && !(bool)context["coverage"]!["complete"]!, "Mixed cycles terminate without complete-impact claims");
            Check(context["savedLinks"]!.AsArray().All(l => l!["edges"]!.AsArray().All(e => (string?)e!["evidence"]?["origin"] == "parsedSupported")), "Supplied relationships never become saved graph edges");
        }
        Check(JsonNode.DeepEquals(result, Build()), "Version 2 output is deterministic for unchanged retained inputs");
        var allSelectors = request.DeepClone().AsObject(); allSelectors["mappings"] = original["mappings"]!.DeepClone();
        foreach (var mapping in allSelectors["mappings"]!.AsArray()) foreach (string side in new[] { "source", "target" })
        {
            var selector = mapping![side]!; string kind = (string)selector["kind"]!, phase = (string)selector["phase"]!;
            if (kind.StartsWith("aml", StringComparison.Ordinal)) selector["recordRef"] = request[phase]!["automationMlRef"]!.DeepClone();
            else if (kind == "model") selector["reviewId"] = reviewId;
            else selector["recordRef"] = kind == "driveParameter" ? driveId : kind == "connectionEndpoint" ? connectionId : technologyId;
        }
        var roles = Build(allSelectors)["consumerContexts"]![0]!["rootBindings"]!.AsArray().Select(r => (string)r!["role"]!).ToHashSet(StringComparer.Ordinal);
        Check(new[] { "mappedObject", "driveSelectionContext", "connection.ownerPath", "connection.interfacePath", "technology.sourcePath", "technology.targetPath" }.All(roles.Contains),
            "Every supported mapped selector preserves its explicit object or component context-root role");
        var unseeded = request.DeepClone().AsObject(); unseeded["mappings"] = new JsonArray();
        Check(Build(unseeded)["consumerContexts"]!.AsArray().All(c => c!["candidates"]!.AsArray().Count == 0), "Unmapped AML cannot seed unrelated component candidates");
        var oldRequest = request.DeepClone().AsObject(); oldRequest["schemaVersion"] = "composite-engineering-request.v1";
        Check(Build(oldRequest)["consumerContexts"] is null, "Version 1 cannot silently acquire version 2 semantics");
        var afterOnly = request.DeepClone().AsObject(); afterOnly["mappings"] = new JsonArray(request["mappings"]![1]!.DeepClone());
        var separated = Build(afterOnly)["consumerContexts"]!.AsArray();
        Check(Paths(separated[0]!).Length == 0 && Paths(separated[1]!).Contains(axisUser), "Equal before/after paths never join context phases");
        var connectionReport = Read(Path.Combine(root, "connection-reviews", connectionId + ".json"))["report"]!.AsObject();
        var technologyReport = Read(Path.Combine(root, "technology-profile-reviews", technologyId + ".json"))["report"]!.AsObject();
        JsonObject Context(JsonObject changedModel, int budget = 10000, Action<JsonNode>? charge = null, JsonObject? combined = null) => CompositeEngineeringConsumers.Build("before", reviewId, changedModel,
            new() { [connectionId] = connectionReport }, new() { [technologyId] = technologyReport }, combined ?? result, budget, charge ?? (_ => { }));
        Check((bool)Context(model, 0)["coverage"]!["truncated"]! && (int)Context(model, 0)["coverage"]!["visitedEdges"]! == 0, "Already exhausted shared edge budget cannot be reset by consumer expansion");
        Check((bool)Context(model, 1)["coverage"]!["truncated"]! && (int)Context(model, 1)["coverage"]!["visitedEdges"]! == 1, "Partial association expansion preserves exact remaining edge budget");
        var nearDepth = result.DeepClone().AsObject();
        foreach (var impact in nearDepth["impacts"]!.AsArray().Where(i => (string?)i!["phase"] == "before"))
            foreach (string key in impact!["destinationDepths"]!.AsObject().Select(p => p.Key).ToArray()) impact["destinationDepths"]![key] = 127;
        var depthContext = Context(model, combined: nearDepth);
        Check(depthContext["candidates"]!.AsArray().Any(c => (string?)c!["objectPath"] == caller && (int)c["discoveryDepth"]! == 128) && !Paths(depthContext).Contains(device),
            "Consumer expansion cannot reset the declared mapping witness depth");
        string peer = plc + "/Peer";
        var peerSelector = new JsonObject { ["kind"] = "model", ["phase"] = "before", ["reviewId"] = reviewId, ["nodeId"] = peer, ["objectPath"] = peer };
        string peerKey = CanonicalJson.Serialize(peerSelector);
        nearDepth["selections"]!.AsArray().Add(new JsonObject { ["key"] = peerKey, ["selector"] = peerSelector, ["state"] = "resolvedDeclaration", ["evidence"] = model["nodes"]!.AsArray().Single(n => (string?)n!["objectPath"] == peer)!.DeepClone(), ["profile"] = null });
        var seeded = nearDepth["impacts"]!.AsArray().First(i => (string?)i!["phase"] == "before" && i["destinationKeys"]!.AsArray().Count > 0)!;
        seeded["destinationKeys"]!.AsArray().Add(peerKey); seeded["destinationDepths"]![peerKey] = 1;
        var shorter = Context(model, combined: nearDepth);
        Check(shorter["candidates"]!.AsArray().Any(c => (string?)c!["objectPath"] == hw && (int)c["discoveryDepth"]! == 2) && Paths(shorter).Contains(axisUser) &&
            !shorter["coverage"]!["truncationReasons"]!.AsArray().Any(r => (string?)r == "maxContextDepth"), "A shorter exact context route supersedes a deep pending root without stale depth exhaustion");
        var ambiguous = model.DeepClone().AsObject(); var duplicate = Node(plc + "/Alias"); duplicate["nodeId"] = caller; ambiguous["nodes"]!.AsArray().Add(duplicate);
        Check(!Paths(Context(ambiguous)).Contains(caller) && !Paths(Context(ambiguous)).Contains(axisUser), "Duplicate node IDs cannot supply an indirect context path");
        var mismatch = model.DeepClone().AsObject(); mismatch["edges"]![0]!["source"]!["nodeId"] = "different-id";
        Check(!Paths(Context(mismatch)).Contains(caller), "Saved edge path alone cannot override mismatched node identity");
        var unknownScope = model.DeepClone().AsObject(); unknownScope["nodes"]!.AsArray().Single(n => (string?)n!["objectPath"] == axisUser)!.AsObject().Remove("plcPath");
        Check(!Paths(Context(unknownScope)).Contains(axisUser), "TO consumer with unknown PLC scope is excluded despite supplied captured status");
        var invalidIdentity = model.DeepClone().AsObject(); string invalidPath = plc + "/bad\0identity";
        invalidIdentity["nodes"]!.AsArray().Add(Node(invalidPath)); invalidIdentity["edges"]!.AsArray().Add(Edge("invalid", invalidPath, hw));
        Check(!Paths(Context(invalidIdentity)).Contains(invalidPath), "Invalid captured identity text cannot become automatic context");
        var missingRows = model.DeepClone().AsObject(); missingRows["nodes"]!.AsArray().Insert(0, null);
        var indexed = Context(missingRows);
        Check(indexed["associations"]!.AsArray().Where(a => (string?)a!["kind"] == "capturedHardwareChild").All(a =>
            JsonNode.DeepEquals(a!["evidence"]!["nodeId"], missingRows["nodes"]![(int)a["sourceIndex"]!]!["nodeId"])), "Skipped model rows never shift original ancestry evidence indices");
        var brokenUnicode = model.DeepClone().AsObject(); var brokenNode = Node(plc + "/InvalidId"); brokenNode["nodeId"] = "\ud800";
        brokenUnicode["nodes"]!.AsArray().Add(brokenNode); brokenUnicode["edges"]!.AsArray().Add(Edge("invalid-id", plc + "/InvalidId", hw));
        Check(!Paths(Context(brokenUnicode)).Contains(plc + "/InvalidId"), "Shared exact identity guard excludes invalid UTF-16 selectors");
        var wide = model.DeepClone().AsObject();
        for (int i = 0; i < 10010; i++) { var node = Node(hw + "/extra" + i, "hardware"); node["parentId"] = hw; wide["nodes"]!.AsArray().Add(node); }
        var bounded = Context(wide); var reasons = bounded["coverage"]!["truncationReasons"]!.AsArray().Select(s => (string)s!).ToArray();
        Check(reasons.Contains("maxAssociations") && reasons.Contains("maxCandidates") && bounded["associations"]!.AsArray().Count == 10000 && bounded["candidates"]!.AsArray().Count == 4096, "Association and candidate bounds retain explicit omitted coverage");
        var broad = model.DeepClone().AsObject(); broad["edges"] = new JsonArray();
        for (int i = 0; i < 2050; i++) { string path = plc + "/dependent" + i; broad["nodes"]!.AsArray().Add(Node(path)); broad["edges"]!.AsArray().Add(Edge("edge" + i, path, hw)); }
        var savedBound = Context(broad);
        Check(savedBound["coverage"]!["truncationReasons"]!.AsArray().Any(r => (string?)r == "maxImpactPathBytes") && (bool)savedBound["coverage"]!["truncated"]!, "Saved dependency byte capacity is visible and never complete");
        var layered = model.DeepClone().AsObject(); layered["edges"] = new JsonArray();
        for (int layer = 0; layer < 7; layer++) for (int i = 0; i < 300; i++)
        {
            string path = plc + "/layer" + layer + "_" + i, target = layer == 0 ? hw : plc + "/layer" + (layer - 1) + "_" + i;
            layered["nodes"]!.AsArray().Add(Node(path)); layered["edges"]!.AsArray().Add(Edge("layer" + layer + "_" + i, path, target));
        }
        var pathBound = Context(layered);
        Check(pathBound["savedLinks"]!.AsArray().Count == 2048 && (bool)pathBound["coverage"]!["truncated"]!, "Saved dependency path capacity is shared across expansion rounds");
        var deep = model.DeepClone().AsObject(); string previous = hw;
        for (int i = 0; i < 130; i++) { string path = hw + "/depth" + i; var node = Node(path, "hardware"); node["parentId"] = previous; deep["nodes"]!.AsArray().Add(node); previous = path; }
        Check(Context(deep)["coverage"]!["truncationReasons"]!.AsArray().Any(r => (string?)r == "maxContextDepth"), "Context depth exhaustion remains explicit");
        int charged = 0; bool chargedRows = false;
        try { Context(model, charge: row => { charged += Bytes(row).Length; if (charged > 1000) throw new InvalidOperationException("budget"); }); }
        catch (InvalidOperationException e) when (e.Message == "budget") { chargedRows = true; }
        Check(chargedRows, "Consumer evidence is charged incrementally before accumulation");
        var crossConnection = connection.DeepClone().AsObject();
        foreach (string phase in new[] { "before", "after" })
        { crossConnection[phase]!["connections"]![0]!["partner"]!["ownerPath"] = foreign; crossConnection[phase]!["connections"]![0]!["partner"]!["interfacePath"] = foreign; }
        string crossId = (string)new ConnectionWorkflowStore(root).Review(reviewId, Upload(Bytes(crossConnection)))["recordId"]!;
        var crossRequest = request.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) crossRequest[phase]!["connections"] = new JsonArray(crossId);
        var crossContext = Build(crossRequest)["consumerContexts"]![0]!;
        Check(Paths(crossContext).Contains(foreign) && crossContext["associations"]!.AsArray().Any(a => (string?)a!["kind"] == "technologyRelationship" && (string?)a["fromObjectPath"] == foreign && !(bool)a["traversed"]!),
            "Explicit cross-PLC connection context cannot weaken the separate TO scope guard");
        var legacyTechnology = technology.DeepClone().AsObject(); legacyTechnology["schemaVersion"] = "technology-profile-review-request.v1";
        foreach (string field in new[] { "sources", "consumers", "impactLimits" }) legacyTechnology.Remove(field);
        string legacyId = (string)new TechnologyProfileWorkflowStore(root).Review(reviewId, Upload(Bytes(legacyTechnology)))["recordId"]!;
        var legacyRequest = request.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) legacyRequest[phase]!["technology"] = new JsonArray(legacyId);
        var unavailable = Build(legacyRequest)["consumerContexts"]![0]!;
        Check(!Paths(unavailable).Contains(axisUser) && unavailable["coverage"]!["technologyRecordsWithoutConsumerDeclarations"]!.AsArray().Any(r => (string?)r == legacyId),
            "Older TO records retain missing consumer declarations as explicit unknown coverage");
        var largeRequest = request.DeepClone().AsObject(); var largeIds = new JsonArray();
        for (int i = 0; i < 5; i++)
        {
            var large = connection.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) large[phase]!["connections"]![0]!["properties"]!["large"] = new string('x', 1024 * 1024) + i;
            largeIds.Add((string)new ConnectionWorkflowStore(root).Review(reviewId, Upload(Bytes(large)))["recordId"]!);
        }
        foreach (string phase in new[] { "before", "after" }) largeRequest[phase]!["connections"] = largeIds.DeepClone();
        bool reportBound = false;
        try { Build(largeRequest); } catch (BridgeException e) when (e.Code == "InvalidCompositeEngineeringReview" && e.Message.Contains("32 MiB", StringComparison.Ordinal)) { reportBound = true; }
        Check(reportBound, "Actual retained v2 inputs cannot accumulate consumer evidence beyond the shared 32 MiB report bound");
        string wideReview = (string)new ProjectReviewStore(root).Save(wide, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        var wideRequest = request.DeepClone().AsObject();
        var wideConnection = connection.DeepClone().AsObject(); wideConnection["reviewId"] = wideReview;
        var wideDrive = drive.DeepClone().AsObject(); wideDrive["reviewId"] = wideReview;
        var wideTechnology = technology.DeepClone().AsObject(); wideTechnology["reviewId"] = wideReview;
        string wc = (string)new ConnectionWorkflowStore(root).Review(wideReview, Upload(Bytes(wideConnection)))["recordId"]!;
        string wd = (string)new DriveProfileWorkflowStore(root).Review(wideReview, Upload(Bytes(wideDrive)))["recordId"]!;
        string wt = (string)new TechnologyProfileWorkflowStore(root).Review(wideReview, Upload(Bytes(wideTechnology)))["recordId"]!;
        foreach (string phase in new[] { "before", "after" })
        {
            wideRequest[phase]!["reviewId"] = wideReview; wideRequest[phase]!["connections"] = new JsonArray(wc);
            wideRequest[phase]!["drives"] = new JsonArray(wd); wideRequest[phase]!["technology"] = new JsonArray(wt);
        }
        foreach (var mapping in wideRequest["mappings"]!.AsArray()) mapping!["target"]!["reviewId"] = wideReview;
        var wideContexts = Build(wideRequest)["consumerContexts"]!.AsArray();
        Check(wideContexts.Sum(c => (int)c!["coverage"]!["visitedEdges"]!) <= 10000 && (int)wideContexts[1]!["coverage"]!["limits"]!["edgeVisitsRemainingAtStart"]! == 0 &&
            (int)wideContexts[1]!["coverage"]!["visitedEdges"]! == 0 && (bool)wideContexts[1]!["coverage"]!["truncated"]!, "Actual retained v2 expansion shares one edge budget across both phases and declared impact");
        var summary = Json(new CompositeEngineeringWorkflowStore(root).Review(Upload(Bytes(request))));
        string reference = (string)summary["recordRef"]!;
        Check(JsonNode.DeepEquals(summary, Json(new CompositeEngineeringWorkflowStore(root).Reopen(reference))), "Version 2 retains exact output across store recreation");
        foreach (string section in new[] { "consumerContexts", "consumerCoverage", "beforeConsumerRoots", "afterConsumerRoots", "beforeConsumerAssociations", "afterConsumerAssociations", "beforeConsumerSavedLinks", "afterConsumerSavedLinks", "beforeConsumerCandidates", "afterConsumerCandidates" })
            Check(Json(new CompositeEngineeringWorkflowStore(root).Page(reference, section))["rows"] is JsonArray, "Version 2 page route: " + section);
        File.WriteAllBytes(Path.Combine(root, "request.json"), Bytes(request)); File.WriteAllBytes(Path.Combine(root, "report.json"), Bytes(result));
        File.WriteAllBytes(Path.Combine(root, "verification.json"), JsonSerializer.SerializeToUtf8Bytes(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        File.WriteAllBytes(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.SerializeToUtf8Bytes(new { artifactRoot = root, requestPath = Path.Combine(root, "request.json"), reportPath = Path.Combine(root, "report.json") }, BridgeProtocol.Json));
        Console.WriteLine($"PASS composite consumer context: {checks.Count} checks; explicit v2, retained v1 semantics, no native qualification.");
    }
}
