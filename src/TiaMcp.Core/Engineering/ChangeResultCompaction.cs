using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Engineering;

/// <summary>Summary views of change_prepare/change_apply results. The worker's full view echoes the payload (whole sources and XML),
/// every compiler message and all readback evidence; the journal keeps all of it. The summary keeps what a client needs to decide
/// and continue: identity, hash, state, stages, failures, compile counts with introduced/resolved findings, warnings and artifact paths.
/// Full detail stays available through includeDetails=true or change_status(includeDetails=true).</summary>
public static class ChangeResultCompaction
{
    public const int MaxString = 1024;
    public const int MaxArray = 50;
    public const int MaxCompileErrors = 20;

    private static readonly string[] TopLevel =
    [
        "origin", "observedAtUtc", "processId", "changeId", "state", "previewHash", "journalPath", "progressPath", "progress", "stages",
        "failure", "primaryFailure", "recoveryFailure", "recoveryDirectory", "recoveryArchive", "recoveryArchiveSha256", "previousStateArchive",
        "afterFingerprint", "automaticRetryAllowed", "recoveredFromDisk", "note",
    ];

    private static readonly string[] PreviewFields =
    [
        "changeId", "kind", "project", "isModified", "warnings", "effects", "language", "insertedNetworkId", "uidRenumbering",
        "targetScopePath", "targetGroupPath", "targetNamespace", "baseFingerprint", "checkpoint", "compileAndSave", "hardwareCrossCheck",
    ];

    /// <summary>A change view (or a change_status journal envelope) as a bounded summary. Results that are already summaries,
    /// and anything that is not a change view, are returned unchanged.</summary>
    public static JsonNode? Compact(JsonNode? node)
    {
        if (node is not JsonObject view || view["responseDetail"] is not null) return node;
        if (view["changeId"] is null || (view["preview"] is null && view["compile"] is null && view["journal"] is null)) return node;
        var result = new JsonObject();
        foreach (string name in TopLevel)
            if (view[name] is { } value) result[name] = Bound(value.DeepClone());
        var preview = view["preview"] as JsonObject ?? view["journal"]?["preview"] as JsonObject;
        if (preview is not null)
        {
            var summary = new JsonObject();
            foreach (string name in PreviewFields)
                if (preview[name] is { } value) summary[name] = Bound(value.DeepClone());
            if (preview["payload"] is JsonObject payload) summary["payload"] = Payload(payload);
            if (preview["intent"] is JsonObject intent)
                summary["intent"] = new JsonObject { ["conflicts"] = Bound(intent["conflicts"]?.DeepClone()), ["previouslyConfirmed"] = Bound(intent["previouslyConfirmed"]?.DeepClone()) };
            if (preview["backups"] is JsonArray backups) summary["backups"] = Artifacts(backups);
            result["preview"] = summary;
        }
        var compile = view["compile"] as JsonObject ?? view["journal"]?["compile"] as JsonObject;
        if (compile is not null) result["compile"] = Compile(compile);
        var journal = view["journal"] as JsonObject;
        if (journal is not null)
            foreach (string name in new[] { "kind", "state", "previewHash", "stages", "failure", "primaryFailure", "recoveryFailure", "recoveryArchive" })
                if (journal[name] is { } value && result[name] is null) result[name] = Bound(value.DeepClone());
        if (view["readbacks"] is JsonArray readbacks && readbacks.Count > 0) result["readbackCount"] = readbacks.Count;
        result["omitted"] = new JsonArray(view.Select(p => p.Key).Where(k => result[k] is null && k is not ("preview" or "compile" or "journal" or "readbacks"))
            .Concat(preview is null ? Array.Empty<string>() : new[] { "preview (full)" }).Select(k => (JsonNode)k).ToArray());
        result["responseDetail"] = "summary";
        result["fullEvidence"] = "The full preview, compiler messages and readback evidence are retained in journalPath. Pass includeDetails=true, or call change_status(includeDetails=true).";
        return result;
    }

    // Requested source/XML and other long values become length and hash, so the client can match them to what it sent.
    private static JsonObject Payload(JsonObject payload)
    {
        var result = new JsonObject();
        foreach (var (name, value) in payload) result[name] = Bound(value?.DeepClone());
        return result;
    }

    private static JsonArray Artifacts(JsonArray backups) => new(backups.OfType<JsonObject>().Take(MaxArray).Select(b => (JsonNode)new JsonObject
    {
        ["role"] = b["role"]?.DeepClone(), ["blockName"] = b["blockName"]?.DeepClone(), ["blockPath"] = b["blockPath"]?.DeepClone(),
        ["path"] = b["artifact"]?["path"]?.DeepClone(), ["sha256"] = b["artifact"]?["sha256"]?.DeepClone(),
    }).ToArray());

    private static JsonObject Compile(JsonObject compile)
    {
        var result = new JsonObject();
        foreach (var (name, value) in compile)
            if (value is JsonValue) result[name] = value.DeepClone();
        if (compile["targets"] is JsonArray targets)
            result["targets"] = new JsonArray(targets.OfType<JsonObject>().Take(MaxArray).Select(t =>
            {
                var row = new JsonObject();
                foreach (var (name, value) in t) if (value is JsonValue) row[name] = Bound(value.DeepClone());
                if (t["messages"] is JsonArray messages)
                {
                    var errors = messages.OfType<JsonObject>().Where(m => m["state"]?.GetValue<string>() == "Error" && !string.IsNullOrEmpty(m["description"]?.GetValue<string>())).ToArray();
                    row["errors"] = new JsonArray(errors.Take(MaxCompileErrors).Select(m => (JsonNode)new JsonObject { ["path"] = m["path"]?.DeepClone(), ["description"] = Bound(m["description"]?.DeepClone()) }).ToArray());
                    if (errors.Length > MaxCompileErrors) row["errorsTruncated"] = true;
                }
                return (JsonNode)row;
            }).ToArray());
        if (compile["baselineComparison"] is JsonObject baseline)
        {
            var row = new JsonObject();
            foreach (var (name, value) in baseline)
                if (name != "existing") row[name] = Bound(value?.DeepClone());
            result["baselineComparison"] = row;
        }
        // Warnings a Classic screen edit revealed outside its screens by removing the last error; accepted, so always shown.
        if (compile["unmaskedWarnings"] is JsonArray unmasked) result["unmaskedWarnings"] = Bound(unmasked.DeepClone());
        return result;
    }

    /// <summary>Bounds every string and array in a node; replaced strings keep their length and SHA-256.</summary>
    public static JsonNode? Bound(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue(out string? text) && text.Length > MaxString:
                return new JsonObject { ["omitted"] = true, ["characters"] = text.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) };
            case JsonArray array:
            {
                var result = new JsonArray(array.Take(MaxArray).Select(item => Bound(item?.DeepClone())).ToArray());
                if (array.Count > MaxArray) result.Add(new JsonObject { ["truncated"] = true, ["totalItems"] = array.Count });
                return result;
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj) result[name] = Bound(value?.DeepClone());
                return result;
            }
            default: return node;
        }
    }
}
