using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    private async Task<object> Generate(JsonObject args, CancellationToken token)
    {
        if (args["maxRows"]?.GetValue<int>() is < 1 or > WatchTableAuthoring.MaxRows)
            throw new BridgeException("WatchTableLimitExceeded", "Generation row budget is 1..256.");
        var request = (JsonObject)args.DeepClone(); request["artifactDirectory"] = Path.Combine(root, "watch-source-captures");
        var capture = Node(await native.CallAsync("watch_capture_instance_native", request, token));
        var planning = capture["request"]!.Deserialize<WatchTablePlanningRequest>(BridgeProtocol.Json)!;
        var plan = WatchTablePlanner.Plan(planning);
        string id = Guid.NewGuid().ToString("N"), directory = Path.Combine(root, "watch-generations"); Directory.CreateDirectory(directory);
        var generation = JsonSerializer.SerializeToUtf8Bytes(new { capture, plan }, BridgeProtocol.Json);
        if (generation.Length > 16 * 1024 * 1024) throw new BridgeException("WatchTableLimitExceeded", "Complete generation exceeds 16 MiB.");
        var artifact = SaveBytes(directory, id + ".json", generation);
        return new { generationId = id, generationSha256 = artifact.Sha256, artifact, plan,
            materializationProfile = "Symbolic scalar rows from captured native row grammar; each scalar datatype requires its own native verification.", nativeMutationAttempted = false };
    }

    private async Task<object> PrepareAuthoring(JsonObject args, CancellationToken token)
    {
        if (args.Any(p => p.Key is not ("generationId" or "generationSha256" or "idempotencyKey" or "mode" or "destination" or "template" or "steps")))
            throw new BridgeException("InvalidRequest", "Unknown authoring request field. See watch-table-authoring.v1.schema.json.");
        string id = Text(args, "generationId");
        if (!Guid.TryParseExact(id, "N", out _)) throw new BridgeException("InvalidRequest", "Exact generated capture ID required.");
        var generationRef = new EngineeringArtifactReference("watch-generations/" + id + ".json", Text(args, "generationSha256"));
        var generationBytes = ReadRecoverySnapshot(Resolve(generationRef));
        if (WatchTableDocument.Hash(generationBytes) != generationRef.Sha256) throw new BridgeException("ArtifactChanged", "Generated source and planner intent changed.");
        var generation = JsonNode.Parse(generationBytes)!.AsObject(); var capture = generation["capture"]!.AsObject(); var generated = generation["plan"]!.AsObject();
        if (!JsonNode.DeepEquals(generated, WatchTablePlanner.Plan(capture["request"]!.Deserialize<WatchTablePlanningRequest>(BridgeProtocol.Json)!)))
            throw new BridgeException("ArtifactChanged", "Retained plan does not match the captured planner request.");
        var binding = capture["binding"]!.AsObject();
        var destination = args["destination"]?.AsObject() ?? throw new BridgeException("InvalidRequest", "Exact destination required.");
        if (destination.Any(p => p.Key is not ("projectPath" or "plcPath" or "group" or "tableName"))) throw new BridgeException("InvalidRequest", "Unknown destination field.");
        string mode = Text(args, "mode"); if (mode is not ("create" or "update")) throw new BridgeException("InvalidRequest", "mode must be create or update.");
        foreach (string field in new[] { "projectPath", "plcPath" })
            if (Text(destination, field) != Text(binding, field)) throw new BridgeException("IdentityMismatch", "Source and destination must identify the same exact project/PLC.");
        if (Text(destination, "tableName") != Text(generated, "tableName")) throw new BridgeException("IdentityMismatch", "Destination name differs from the generated plan.");
        _ = Text(destination, "group");
        var selector = new JsonObject { ["projectPath"] = Text(destination, "projectPath"), ["plcPath"] = Text(destination, "plcPath"),
            ["group"] = Text(destination, "group"), ["tableName"] = Text(destination, "tableName"), ["allowAbsent"] = mode == "create", ["sourceBinding"] = binding.DeepClone() };
        // Capture again before preparing; the worker repeats this under the exclusive mutation lease.
        var currentRequest = (JsonObject)binding.DeepClone(); currentRequest["tableName"] = Text(destination, "tableName");
        currentRequest["artifactDirectory"] = Path.Combine(root, "watch-source-captures");
        currentRequest["maxRows"] = generated["maxRows"]!.DeepClone();
        var current = Node(await native.CallAsync("watch_capture_instance_native", currentRequest, token));
        if (!JsonNode.DeepEquals(binding, current["binding"]) || !JsonNode.DeepEquals(capture["selectors"], current["selectors"]) ||
            !JsonNode.DeepEquals(CaptureShape(capture["request"]!), CaptureShape(current["request"]!)))
            throw new BridgeException("StaleSource", "The FB or selected instance changed after generation; generate and review again.");
        var before = await Snapshot(selector, token); var observation = Observation(before);
        NativeTargetGuard.Validate(observation);
        if (!observation.Offline || before["projectModified"]?.GetValue<bool>() != false || before["target"]?["isConsistent"]?.GetValue<bool>() != true)
            throw new BridgeException("InvalidNativeBaseline", "A saved offline consistent native baseline is required.");
        if (observation.ProjectFilePath != Text(destination, "projectPath") || observation.Reference.Object.PlcPath != Text(destination, "plcPath"))
            throw new BridgeException("IdentityMismatch", "Native destination differs from the exact requested project/PLC.");
        foreach (var source in new[] { ("blockPath", "blockSha256"), ("instanceDbPath", "instanceSha256") })
            if (before["peers"]?["block:" + Text(binding, source.Item1)]?.GetValue<string>() != Text(binding, source.Item2))
                throw new BridgeException("StaleSource", "Source export and destination preservation snapshot disagree.");
        bool absent = before["target"]?["exists"]?.GetValue<bool>() == false;
        if (mode == "create" && !absent) throw new BridgeException("WatchTableCollision", "Creation requires an independently observed absent destination.");
        if (mode == "update" && absent) throw new BridgeException("WatchTableNotFound", "Update requires the existing exact table.");
        ValidateCapturedContent(before, observation.Reference.ContentSha256!);
        string directory = Path.Combine(root, "watch", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        JsonObject templateSnapshot;
        EngineeringArtifactReference templateArtifact;
        if (args["template"] is JsonObject template)
        {
            var templateSelector = (JsonObject)selector.DeepClone();
            if (template.Any(p => p.Key is not ("group" or "tableName"))) throw new BridgeException("InvalidRequest", "Template accepts only the exact tableName and group in this PLC.");
            templateSelector["tableName"] = Text(template, "tableName"); templateSelector["group"] = Text(template, "group"); templateSelector["allowAbsent"] = false;
            templateSnapshot = await Snapshot(templateSelector, token);
            templateArtifact = Artifact(templateSnapshot["target"]!["artifact"]!.AsObject());
            ValidateCapturedContent(templateSnapshot, Text(templateSnapshot["target"]!.AsObject(), "semanticSha256"));
        }
        else if (mode == "update" && before["target"]!["rows"]!.AsArray().Count > 0)
        { templateSnapshot = before; templateArtifact = Artifact(before["target"]!["artifact"]!.AsObject()); }
        else
        {
            templateArtifact = SaveBytes(directory, "native-template.xml", WatchTableNativeTemplate.Read());
            templateSnapshot = new JsonObject { ["origin"] = "packagedGenuineNativeCapture", ["profile"] = WatchTableNativeTemplate.Profile,
                ["sourceSha256"] = WatchTableNativeTemplate.Sha256, ["artifact"] = JsonSerializer.SerializeToNode(templateArtifact, BridgeProtocol.Json),
                ["nativeAuthoringQualified"] = false };
        }
        var prototype = new WatchTableDocument(File.ReadAllBytes(Resolve(templateArtifact)));
        var document = new WatchTableDocument(mode == "create" ? prototype.ToBytes() : File.ReadAllBytes(Resolve(Artifact(before["target"]!["artifact"]!.AsObject()))));
        if (mode == "create") { for (int i = document.Count - 1; i >= 0; i--) document.Remove(i); document.Rename(Text(destination, "tableName")); }
        var cultures = before["peers"]?["languages"]?["active"]?.AsArray().Select(c => c!.GetValue<string>()).ToArray() ?? [];
        var edit = WatchTableAuthoring.Edit(document, prototype, generated, capture["selectors"]!.AsObject(),
            args["steps"]?.AsArray() ?? throw new BridgeException("InvalidRequest", "Explicit ordered authoring steps required."), cultures, mode == "create");
        string expected = WatchTableDocument.SemanticHash(document.ToBytes());
        if (expected == observation.Reference.ContentSha256) throw new BridgeException("NoChange", "The complete proposed document is unchanged.");
        var input = SaveBytes(directory, "reviewed.xml", document.ToBytes());
        var proposal = new JsonObject { ["selector"] = selector, ["mode"] = mode, ["before"] = before, ["edit"] = edit,
            ["generation"] = generation.DeepClone(), ["generationArtifact"] = JsonSerializer.SerializeToNode(generationRef, BridgeProtocol.Json),
            ["template"] = templateSnapshot.DeepClone(), ["expectedSemanticSha256"] = expected, ["input"] = JsonSerializer.SerializeToNode(input, BridgeProtocol.Json),
            ["profileGates"] = new JsonArray(edit["requiredProfiles"]!.AsArray().Select(p => (JsonNode)WatchMutationProfiles.Status(p!.GetValue<string>())).ToArray()) };
        proposal["exactProfileCandidate"] = WatchAuthoringProfile.Assess(proposal, document.ToBytes());
        if (System.Text.Encoding.UTF8.GetByteCount(proposal.ToJsonString()) > 16 * 1024 * 1024)
            throw new BridgeException("WatchTableLimitExceeded", "Complete authoring review exceeds 16 MiB.");
        var review = Save(directory, "review.json", proposal);
        var sourceArtifacts = capture["artifacts"]?.AsArray().Select(a => Artifact(a!.AsObject())).ToArray()
            ?? throw new BridgeException("InvalidRequest", "Native source capture artifacts are required.");
        var captures = new List<EngineeringArtifactReference> { Artifact(before["target"]!["artifact"]!.AsObject()) };
        if (before["receipt"] is JsonObject baselineReceipt) captures.Add(Artifact(baselineReceipt));
        if (templateSnapshot["receipt"] is JsonObject templateReceipt) captures.Add(Artifact(templateReceipt));
        var plan = new EngineeringOperationPlan("watchAuthoring", observation,
            [new(EngineeringResourceKey.ForPath("project", observation.ProjectFilePath), "project", "reviewedWatchAuthoring:" + mode, "projectArchiveToNewLocation")],
            new[] { review, input, generationRef, templateArtifact }.Concat(sourceArtifacts).Concat(captures).Distinct().ToArray(),
            ["offline", "watchImport"], ["readback", "preservation", "saved", "freshReopen"]);
        var record = store.Prepare(plan, Text(args, "idempotencyKey"));
        return new { operation = record, review = proposal, reviewArtifact = review, productionApplyEnabled = false,
            applyRequires = "Exact operationId/reviewedPlanSha256 and native verification of every required mutation profile." };
    }

    private void ValidateCapturedContent(JsonObject snapshot, string expected)
    {
        var target = snapshot["target"]!.AsObject(); var artifact = Artifact(target["artifact"]!.AsObject());
        byte[] bytes = ReadRecoverySnapshot(Resolve(artifact));
        if (WatchTableDocument.Hash(bytes) != artifact.Sha256) throw new BridgeException("ArtifactChanged", "Captured content bytes changed.");
        if (target["exists"]?.GetValue<bool>() == false)
        {
            if (!bytes.SequenceEqual(WatchTableDocument.AbsentBytes(Text(target, "group"), Text(target, "name"))) ||
                WatchTableDocument.Hash(bytes) != expected || target["rows"]?.AsArray().Count != 0)
                throw new BridgeException("ArtifactChanged", "Absence evidence differs from the exact destination.");
        }
        else
        {
            if (WatchTableDocument.SemanticHash(bytes) != expected) throw new BridgeException("ArtifactChanged", "Native export differs from its expected content.");
            ValidateTypedRows(bytes, snapshot);
        }
    }

    private static JsonObject RecoverySelector(JsonObject review, string? projectPath = null, string? projectName = null)
    {
        var selector = review["selector"]!.DeepClone().AsObject();
        if (projectPath is not null) selector["projectPath"] = projectPath;
        if (projectName is not null) selector["projectName"] = projectName;
        selector.Remove("sourceBinding"); // Recovery compares the complete baseline manifest at its reserved new location.
        return selector;
    }

    private static JsonNode CaptureShape(JsonNode value)
    {
        var copy = value.DeepClone();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj) { obj.Remove("observationId"); foreach (var item in obj) Visit(item.Value); }
            else if (node is JsonArray array) foreach (var item in array) Visit(item);
        }
        Visit(copy); return copy;
    }
}
