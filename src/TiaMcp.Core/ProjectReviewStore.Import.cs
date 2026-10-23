using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core;

public sealed partial class ProjectReviewStore
{
    public const string SourceEvidencePolicy = "Saved source observations are supplied evidence; authenticity, live state and native qualification are not verified by offline review.";

    public JsonObject Import(string uploadRef)
    {
        var uploaded = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ReadArtifact(artifactsRoot, new JsonObject { ["path"] = uploaded.Path, ["sha256"] = uploaded.Sha256 });
        var review = ValidateImport(bytes);
        string path = Publish(bytes), id = Hash(bytes);
        return new JsonObject { ["reviewId"] = id, ["artifact"] = new JsonObject { ["path"] = path, ["sha256"] = id, ["bytes"] = bytes.Length },
            ["nodeCount"] = NativeNodes(review["model"]!.AsObject()).Count, ["documentCount"] = Rows(review["documents"]).Count,
            ["nativeAccessAttempted"] = false, ["sourceAuthenticityVerified"] = false, ["nativeQualified"] = false,
            ["wholeProjectContentComplete"] = false, ["sourceEvidencePolicy"] = SourceEvidencePolicy };
    }

    private string Publish(byte[] data)
    {
        if (data.Length > MaxBytes) throw new BridgeException("ArtifactCapacity", "Review exceeds 64 MiB.");
        PackageStorage.EnsureNoLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        using var gate = PackageStorage.Lock(Path.Combine(DirectoryPath, ".write.lock"));
        string id = Hash(data), output = PackageStorage.Under(DirectoryPath, id + ".json");
        if (File.Exists(output)) { _ = Read(id); return output; }
        if (new DirectoryInfo(DirectoryPath).EnumerateFiles().Sum(file => file.Length) + data.Length > 1024L * 1024 * 1024)
            throw new BridgeException("ArtifactCapacity", "Review store exceeds 1 GiB; archive selected snapshots before adding more.");
        // Publish only complete bytes; an interrupted temporary file remains counted against the quota.
        string temporary = PackageStorage.Under(DirectoryPath, id + "." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(data); stream.Flush(true); }
        File.Move(temporary, output);
        _ = Read(id);
        return output;
    }

    private static JsonObject ValidateImport(byte[] bytes)
    {
        // Decode strictly before JSON parsing so malformed UTF-8 cannot change the observed text.
        string text = new UTF8Encoding(false, true).GetString(bytes);
        using var parsed = JsonDocument.Parse(text);
        Unique(parsed.RootElement);
        var review = JsonNode.Parse(text) as JsonObject ?? throw Invalid("Expected a saved review object.");
        if (Text(review, "schemaVersion") != "project-review.v1" || review["wholeProjectContentComplete"]?.GetValue<bool>() != false ||
            review["capture"] is not JsonObject || !DateTimeOffset.TryParse(Text(review, "createdAtUtc"), out _) ||
            review["consistency"] is not JsonValue consistency || !consistency.TryGetValue<string>(out _))
            throw Invalid("Expected a dated partial project-review.v1 with capture and consistency evidence.");
        var model = review["model"] as JsonObject ?? throw Invalid("Missing project model.");
        if (Text(model, "schemaVersion") != "project-model.v1" || model["project"] is not JsonObject ||
            string.IsNullOrWhiteSpace(Text(model["project"], "projectPath")) || string.IsNullOrWhiteSpace(Text(model["project"], "host")))
            throw Invalid("Expected an exact saved project-copy identity.");
        var rows = Rows(model["nodes"]); var edges = Rows(model["edges"]); var documents = Rows(review["documents"]);
        if (rows.Count is < 1 or > 50000 || edges.Count > 50000 || documents.Count > 200)
            throw new BridgeException("ArtifactCapacity", "Review allows 1..50000 nodes, 50000 edges and 200 documents.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject || string.IsNullOrWhiteSpace(Text(row, "nodeId")) || !ids.Add(Text(row, "nodeId")) ||
                string.IsNullOrWhiteSpace(Text(row, "kind")) || row["metadata"] is not (null or JsonObject))
                throw Invalid("Node identities must be present and unique; metadata must be an object.");
            foreach (string key in new[] { "objectPath", "nodeId", "kind", "name", "origin", "parentId", "plcPath", "scopePath" }) StringField(row, key);
            foreach (string key in new[] { "namespace", "dataType", "programmingLanguage", "logicalAddress" }) StringField(row["metadata"], key);
            if (row["access"] is not (null or JsonObject)) throw Invalid("Access metadata must be an object.");
            StringField(row["access"], "state");
            foreach (string key in new[] { "isConsistent", "isKnowHowProtected", "isSafety", "externalAccessible", "externalVisible" })
                if (row["metadata"]?[key] is JsonNode value && (value is not JsonValue v || !v.TryGetValue<bool>(out _)))
                    throw Invalid("Known Boolean metadata has an invalid type.");
        }
        var allNodes = rows.ToDictionary(n => Text(n, "nodeId"), n => n!, StringComparer.Ordinal);
        var nodes = NativeNodes(model);
        if (rows.Count(n => Text(n, "kind") == "project") != 1) throw Invalid("Expected exactly one project root.");
        string root = ProjectRoot(model);
        if (!root.StartsWith("/projects/", StringComparison.Ordinal)) throw Invalid("Missing native project root path.");
        foreach (var path in nodes.Keys) _ = Relative(path, root);
        foreach (var edge in edges)
        {
            if (edge is not JsonObject || string.IsNullOrWhiteSpace(Text(edge, "kind")) || edge["evidence"] is not JsonObject)
                throw Invalid("Malformed relationship evidence.");
            bool resolved = Text(edge["evidence"], "resolution") == "resolved" && Text(edge["evidence"], "origin") is "nativeConfirmed" or "parsedSupported";
            foreach (string side in new[] { "source", "target" })
            {
                var endpoint = edge[side];
                if (endpoint is not (null or JsonObject)) throw Invalid("Malformed relationship endpoint.");
                string path = Text(endpoint, "objectPath");
                if (resolved && (!allNodes.TryGetValue(Text(endpoint, "nodeId"), out var node) || path != Text(node, "objectPath")))
                    throw Invalid("Resolved relationship endpoints must match exact captured paths and node IDs.");
            }
        }
        var paths = new HashSet<string>(StringComparer.Ordinal); long totalXml = 0;
        foreach (var row in documents)
        {
            string path = Text(row, "objectPath");
            if (row is not JsonObject || !nodes.TryGetValue(path, out var node) || Text(node, "kind") is not ("block" or "type") ||
                Text(node, "origin") == "virtual" || !paths.Add(path) || Text(row, "status") is not ("exported" or "unavailable"))
                throw Invalid("Invalid, duplicate, virtual or foreign block/type document.");
            if (Text(row, "status") != "exported") continue;
            string xml = Text(row, "xml"); int length = Encoding.UTF8.GetByteCount(xml); totalXml += length;
            if (length > 16 * 1024 * 1024 || totalXml > 40L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "XML exceeds per-document or total export limits.");
            if (Hash(Encoding.UTF8.GetBytes(xml)) != Text(row, "utf8Sha256") || !JsonNode.DeepEquals(row["signatures"], XmlSignatures(xml)))
                throw new BridgeException("ArtifactChanged", "XML bytes or derived comparison signatures differ from the saved review.");
        }
        return review;
    }

    private static void StringField(JsonNode? node, string key)
    {
        if (node?[key] is JsonNode value && (value is not JsonValue v || !v.TryGetValue<string>(out _)))
            throw Invalid("Expected a string or null for " + key + ".");
    }

    private static BridgeException Invalid(string message) => new("InvalidSnapshot", message);
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!names.Add(field.Name)) throw Invalid("Duplicate JSON field: " + field.Name); Unique(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
}
