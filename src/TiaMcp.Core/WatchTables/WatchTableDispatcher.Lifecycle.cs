using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.WatchTables;

public sealed partial class WatchTableDispatcher
{
    /// <summary>Review one table/group lifecycle operation against the exact parent group's complete subtree and peers. Never mutates.
    /// Apply, fresh reopen, recovery and reconciliation reuse the shared watch plan path.</summary>
    private async Task<object> PrepareLifecycle(JsonObject args, CancellationToken token)
    {
        if (args.Any(p => p.Key is not ("projectName" or "plcPath" or "group" or "operation" or "name" or "newName" or "idempotencyKey")))
            throw new BridgeException("InvalidRequest", "Unknown watch lifecycle field.");
        string operation = Text(args, "operation"), name = Text(args, "name"); string? newName = args["newName"]?.GetValue<string>();
        if (operation is not ("createTable" or "renameTable" or "deleteTable" or "createGroup" or "renameGroup" or "deleteGroup"))
            throw new BridgeException("UnsupportedProfile", "Unsupported watch lifecycle operation.");
        foreach (string? value in new[] { name, newName })
            if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
                throw new BridgeException("InvalidRequest", "Names must be 1..128 characters without control characters.");
        if (operation.StartsWith("rename", StringComparison.Ordinal) != (newName is not null) || newName == name)
            throw new BridgeException("InvalidRequest", "Rename requires a different newName; other operations take none.");
        var selector = new JsonObject { ["projectName"] = args["projectName"]?.DeepClone(), ["plcPath"] = args["plcPath"]?.DeepClone(),
            ["group"] = args["group"]?.GetValue<string>() ?? "root", ["targetKind"] = "watchTableGroup" };
        var before = await Snapshot(selector, token); var observation = Observation(before);
        NativeTargetGuard.Validate(observation);
        if (!observation.Offline || before["projectModified"]?.GetValue<bool>() != false || before["target"]?["isConsistent"]?.GetValue<bool>() != true)
            throw new BridgeException("InvalidNativeBaseline", "A saved offline consistent native baseline is required.");
        ValidateCapturedContent(before, observation.Reference.ContentSha256!);
        selector["projectPath"] = observation.ProjectFilePath; selector["plcPath"] = observation.Reference.Object.PlcPath;
        var tables = before["tables"]!.AsArray();
        string? claimed = operation == "createTable" ? name : operation == "renameTable" ? newName : null;
        // Watch and force table names are unique per PLC across all groups.
        if (claimed is not null && tables.Any(t => string.Equals(t?["name"]?.GetValue<string>(), claimed, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("WatchTableCollision", "A watch or force table already occupies this name in the selected PLC.");
        // Web/display access rules name their table; renaming or deleting it would change them outside this review.
        if (operation is "renameTable" or "deleteTable" && before["peers"]?["access"]?.AsArray().Any(owner =>
                owner?["watch"]?.AsArray().Any(rule => rule?["table"]?.GetValue<string>() == name) == true) == true)
            throw new BridgeException("WatchTableAccessAssigned", "Remove the web/display access rule for this watch table first.");
        string? renamed = null;
        if (operation == "renameTable")
        {
            var table = tables.FirstOrDefault(t => t?["kind"]?.GetValue<string>() == "watch" && t?["group"]?.GetValue<string>() == Text(selector, "group") && t?["name"]?.GetValue<string>() == name)
                ?? throw new BridgeException("WatchTableNotFound", "The exact watch table in the selected group is required.");
            var captured = Artifact(table["artifact"]!.AsObject()); var bytes = ReadRecoverySnapshot(Resolve(captured));
            if (WatchTableDocument.Hash(bytes) != captured.Sha256 || WatchTableDocument.SemanticHash(bytes) != table["semanticSha256"]?.GetValue<string>())
                throw new BridgeException("ArtifactChanged", "Captured table export changed.");
            var document = new WatchTableDocument(bytes); document.Rename(newName!); renamed = WatchTableDocument.SemanticHash(document.ToBytes());
        }
        var capture = Artifact(before["target"]!["artifact"]!.AsObject());
        var expected = WatchGroupManifest.Expect(ReadRecoverySnapshot(Resolve(capture)), operation, name, newName, renamed, out string? refusal)
            ?? throw new BridgeException(refusal!, "The lifecycle operation does not fit the observed group subtree.");
        string profile = "watch." + operation[^5..].ToLowerInvariant() + "-" + operation[..^5] + ".v1"; // e.g. watch.table-create.v1
        var edit = new JsonObject { ["operation"] = operation, ["name"] = name, ["newName"] = newName, ["renamedSemanticSha256"] = renamed,
            ["expectedGroup"] = new JsonArray(expected.Select(l => (JsonNode?)l).ToArray()), ["requiredProfiles"] = new JsonArray(profile) };
        var proposal = new JsonObject { ["selector"] = selector, ["mode"] = "lifecycle", ["before"] = before, ["edit"] = edit,
            ["profileGates"] = new JsonArray(WatchMutationProfiles.Status(profile)) };
        string directory = Path.Combine(root, "watch", Guid.NewGuid().ToString("N")[..12]); Directory.CreateDirectory(directory);
        var review = Save(directory, "review.json", proposal);
        var inputs = new List<EngineeringArtifactReference> { review, capture };
        if (before["receipt"] is JsonObject receipt) inputs.Add(Artifact(receipt));
        var plan = new EngineeringOperationPlan("watchLifecycle", observation,
            [new(EngineeringResourceKey.ForPath("project", observation.ProjectFilePath), "project", "reviewedWatchLifecycle:" + operation, "projectArchiveToNewLocation")],
            inputs.ToArray(), ["offline", "watchImport"], ["readback", "preservation", "saved", "freshReopen"]);
        var record = store.Prepare(plan, Text(args, "idempotencyKey"));
        return new { operation = record, review = proposal, reviewArtifact = review, productionApplyEnabled = false,
            applyRequires = "Exact operationId/reviewedPlanSha256 and native verification of the required mutation profile." };
    }
}
