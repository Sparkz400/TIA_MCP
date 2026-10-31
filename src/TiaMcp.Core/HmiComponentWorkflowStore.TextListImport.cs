using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.HmiComponents;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core;

public sealed partial class HmiComponentWorkflowStore
{
    /// <summary>The worker payload of change kind hmi_text_list_import. The worker cannot read review records, so the
    /// Host reopens the selected author or set record (full revalidation), refuses content V21's import changes
    /// (also for records published before that refusal existed) and hands over only the exact candidate pair.</summary>
    public JsonObject TextListImportPayload(JsonElement payload)
    {
        var request = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? throw new BridgeException("InvalidRequest", "payload must be an object.");
        if (request.Any(p => p.Key is not ("schemaVersion" or "target" or "expectedContentHash" or "recordId" or "outputId")))
            throw new BridgeException("InvalidRequest", "hmi_text_list_import accepts schemaVersion, target, expectedContentHash, recordId and outputId only.");
        if (request["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out int schema) || schema != 1)
            throw new BridgeException("InvalidRequest", "payload.schemaVersion must be 1.");
        if (request["target"] is not JsonObject target) throw new BridgeException("InvalidRequest", "target must be the exact hmiDevice identity from hmi_inspect section=devices.");
        string recordId = Text(request, "recordId", 64); EngineeringIdentityGuard.Sha256(recordId, "recordId");
        string? outputId = request["outputId"] is null ? null : Text(request, "outputId", 128);
        bool author = File.Exists(Path.Combine(Root, recordId + ".author.json")), set = File.Exists(Path.Combine(Root, recordId + ".text-set.json"));
        if (author == set) throw new BridgeException("HmiTextListRecordNotFound", "recordId must name exactly one retained hmi_text_list_author or hmi_text_list_generate_set record.");
        if (author != (outputId is null)) throw new BridgeException("InvalidRequest", "outputId selects one output of a set record and is not used for an author record.");
        var record = author ? AuthorTextLists(recordId: recordId) : GenerateTextListSet(recordId: recordId);
        if ((string?)record["status"] is not (null or "candidatesReady")) throw new BridgeException("HmiTextListCandidateUnavailable", "The record published no candidates (status conflict).");
        JsonObject intent; JsonNode?[] files;
        if (set)
        {
            var output = record["outputs"]!.AsArray().OfType<JsonObject>().SingleOrDefault(o => (string?)o["outputId"] == outputId)
                ?? throw new BridgeException("HmiTextListCandidateUnavailable", "The set has no output " + outputId + ".");
            intent = Object(output["intent"]);
            files = record["files"]!.AsArray().Where(f => (string?)f!["outputId"] == outputId).ToArray();
        }
        else
        {
            intent = record["intent"] as JsonObject ?? Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Object(record["input"]), InputLimit));
            files = record["files"]!.AsArray().ToArray();
        }
        // The same refusals as new authoring, applied to the exact intent of the imported pair.
        UnifiedTextListAuthoring.RequireNativeImportable(intent);
        var built = UnifiedTextListAuthoring.Build(intent);
        if (files.Length != 2 || !built.Select(c => c.Name).SequenceEqual(files.Select(f => (string?)f!["fileName"])))
            throw new BridgeException("HmiTextListCandidateUnavailable", "The selected record does not publish exactly its configuration and TextLibrary pair.");
        var rows = new JsonArray();
        for (int i = 0; i < 2; i++)
        {
            var artifact = Object(files[i]!["artifact"]);
            byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, UnifiedTextListAuthoring.OutputLimit);
            if (!bytes.AsSpan().SequenceEqual(built[i].Bytes)) throw new BridgeException("ArtifactChanged", "Retained candidate differs from its intent.");
            rows.Add(new JsonObject { ["fileName"] = built[i].Name, ["bytes"] = bytes.Length, ["sha256"] = artifact["sha256"]!.DeepClone(),
                ["path"] = Path.GetFullPath(Path.Combine(artifactsRoot, (string)artifact["path"]!)) });
        }
        return new JsonObject { ["schemaVersion"] = 1, ["target"] = target.DeepClone(), ["expectedContentHash"] = request["expectedContentHash"]?.DeepClone(),
            ["candidate"] = new JsonObject { ["recordId"] = recordId, ["recordKind"] = author ? "author" : "set", ["outputId"] = outputId,
                ["recordSchemaVersion"] = record["schemaVersion"]!.DeepClone(), ["languages"] = intent["languages"]!.DeepClone(),
                ["lists"] = new JsonArray(intent["lists"]!.AsArray().Select(l => (JsonNode?)l!["name"]!.DeepClone()).ToArray()), ["files"] = rows } };
    }
}
