using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Security;

namespace TiaMcp.Core;

/// <summary>Immutable supplied public access reviews; exact input and saved source are revalidated on every reopen.</summary>
public sealed class AccessWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "access-reviews");
    public JsonObject Review(string reviewId, string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, AccessReview.InputLimit);
        var request = AccessReview.Parse(bytes);
        var report = AccessReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
        var input = Publish(bytes, ".input", AccessReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "access-review-record.v1", ["reviewId"] = reviewId, ["input"] = input, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations" or "assessments" or "changes"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var envelope = ReadVerified(recordId);
        var artifact = Reference(recordId, ".json");
        var expected = envelope["report"]!.AsObject();
        var result = Summary(envelope, artifact);
        if (section == "summary") return result;
        var source = expected[section]!.AsArray();
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
    internal JsonObject ReadVerified(string recordId, Action<int>? accountBytes = null)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        byte[] recordBytes = ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(recordId, ".json"), ReportLimit);
        accountBytes?.Invoke(recordBytes.Length);
        var envelope = JsonNode.Parse(recordBytes) as JsonObject
            ?? throw new BridgeException("InvalidAccessReview", "Expected a record object.");
        if (envelope.Count != 4 || (string?)envelope["schemaVersion"] != "access-review-record.v1" || envelope["reviewId"] is not JsonValue reviewValue || !reviewValue.TryGetValue<string>(out _) || envelope["input"] is not JsonObject input || envelope["report"] is not JsonObject)
            throw new BridgeException("InvalidAccessReview", "Unexpected record schema.");
        if (input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? inputHash) || inputHash is null ||
            !JsonNode.DeepEquals(input, Reference(inputHash, ".input"))) throw new BridgeException("InvalidAccessReview", "Expected a retained public access input reference.");
        EngineeringIdentityGuard.Sha256(inputHash, "input.sha256");
        string reviewId = (string)envelope["reviewId"]!;
        byte[] inputBytes = ProjectReviewStore.ReadArtifact(artifactsRoot, input, AccessReview.InputLimit);
        accountBytes?.Invoke(inputBytes.Length);
        var request = AccessReview.Parse(inputBytes);
        var expected = AccessReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input and saved review.");
        return envelope;
    }
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "access-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "Public access review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Public access review retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["nativeExecutable"] = false, ["nativeQualified"] = false,
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["effectiveRightsVerified"] = false, ["minimumRightsVerified"] = false, ["lockoutSafetyVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
