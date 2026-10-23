using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.DbSnapshots;

namespace TiaMcp.Core;

/// <summary>Immutable supplied DB snapshot review, retaining original XML encodings and exact source association.</summary>
public sealed class DbSnapshotWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "db-snapshot-reviews");
    public JsonObject Review(string reviewId, string objectPath, string uploadRef, string? comparisonUploadRef = null)
    {
        byte[] ReadUpload(string reference)
        {
            var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(reference);
            return ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, DbSnapshotReview.InputLimit);
        }
        byte[] before = ReadUpload(uploadRef); byte[]? after = comparisonUploadRef is null ? null : ReadUpload(comparisonUploadRef);
        var report = DbSnapshotReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), objectPath, before, after);
        var envelope = new JsonObject { ["schemaVersion"] = "db-snapshot-review-record.v1", ["reviewId"] = reviewId, ["objectPath"] = objectPath,
            ["input"] = Publish(before, ".xml", DbSnapshotReview.InputLimit),
            ["comparisonInput"] = after is null ? null : Publish(after, ".xml", DbSnapshotReview.InputLimit), ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "beforeValues" or "afterValues" or "changes"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit)) as JsonObject
            ?? throw new BridgeException("InvalidDbSnapshot", "Expected a record object.");
        if (envelope.Count != 6 || (string?)envelope["schemaVersion"] != "db-snapshot-review-record.v1" || envelope["reviewId"] is not JsonValue ||
            envelope["objectPath"] is not JsonValue || !envelope.ContainsKey("comparisonInput") || envelope["report"] is not JsonObject)
            throw new BridgeException("InvalidDbSnapshot", "Unexpected record schema.");
        byte[] ReadInput(JsonNode? value)
        {
            if (value is not JsonObject input || input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? hash) || hash is null)
                throw new BridgeException("InvalidDbSnapshot", "Expected a retained snapshot input reference.");
            EngineeringIdentityGuard.Sha256(hash, "input.sha256");
            if (!JsonNode.DeepEquals(input, Reference(hash, ".xml"))) throw new BridgeException("InvalidDbSnapshot", "Expected a canonical retained snapshot input reference.");
            return ProjectReviewStore.ReadArtifact(artifactsRoot, input, DbSnapshotReview.InputLimit);
        }
        string reviewId = (string)envelope["reviewId"]!, objectPath = (string)envelope["objectPath"]!;
        var expected = DbSnapshotReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), objectPath,
            ReadInput(envelope["input"]), envelope["comparisonInput"] is null ? null : ReadInput(envelope["comparisonInput"]));
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input and saved review.");
        var result = Summary(envelope, artifact);
        if (section == "summary") return result;
        var source = section == "changes" ? expected["changes"]!.AsArray() : expected[section == "beforeValues" ? "before" : "after"]?["values"]?.AsArray() ?? new JsonArray();
        if (offset > source.Count) throw new BridgeException("InvalidRequest", "Offset exceeds the selected section.");
        var rows = new JsonArray(); int next = offset, used = 0;
        while (next < source.Count && rows.Count < count)
        {
            JsonObject row = new() { ["index"] = next, ["value"] = source[next]!.DeepClone() };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (size > 120 * 1024) { row = new() { ["index"] = next, ["omitted"] = true, ["reason"] = "Download the complete report artifact for this oversized row." }; size = 256; }
            if (used + size > 120 * 1024) break;
            rows.Add(row); used += size; next++;
        }
        result["section"] = section; result["offset"] = offset; result["total"] = source.Count; result["rows"] = rows;
        result["nextOffset"] = next < source.Count ? next : null;
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 128 * 1024) throw new BridgeException("ArtifactCapacity", "Page exceeds 128 KiB; download the complete report.");
        return result;
    }
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "db-snapshot-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "DB snapshot review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "DB snapshot review retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["comparisonInput"] = record["comparisonInput"]?.DeepClone(), ["objectPath"] = record["objectPath"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["nativeExecutable"] = false, ["nativeQualified"] = false,
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["programIdentityVerified"] = false, ["structureCompatibilityVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
