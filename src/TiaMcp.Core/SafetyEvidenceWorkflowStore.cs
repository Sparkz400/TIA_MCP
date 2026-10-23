using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Safety;

namespace TiaMcp.Core;

/// <summary>Immutable supplied Safety evidence review, retaining original JSON bytes and exact source association.</summary>
public sealed class SafetyEvidenceWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private const int SourceLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "safety-evidence-reviews");
    public JsonObject Review(string reviewId, string plcPath, string uploadRef, string comparisonUploadRef, JsonArray? sources = null)
    {
        byte[] ReadUpload(string reference)
        {
            var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(reference);
            return ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, SafetyEvidenceReview.InputLimit);
        }
        byte[] before = ReadUpload(uploadRef); byte[] after = ReadUpload(comparisonUploadRef);
        var report = SafetyEvidenceReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), plcPath, before, after);
        var retained = new JsonArray(); var captured = new List<byte[]>();
        if (sources is not null && sources.Count > 16) throw Invalid("Supply at most 16 supporting sources.");
        foreach (var item in sources ?? new JsonArray())
        {
            if (item is not JsonObject row || row.Count != 3 || !row.ContainsKey("uploadRef") || !row.ContainsKey("phase") || !row.ContainsKey("purpose"))
                throw Invalid("Supporting sources require uploadRef, phase and purpose only.");
            var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(SourceText(row, "uploadRef", 39));
            byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, SourceLimit);
            retained.Add(new JsonObject { ["phase"] = SourceText(row, "phase", 16), ["purpose"] = SourceText(row, "purpose", 256),
                ["fileName"] = upload.FileName, ["bytes"] = bytes.Length, ["artifact"] = Reference(upload.Sha256, ".source") });
            captured.Add(bytes);
            if (captured.Sum(b => (long)b.Length) > 64L * 1024 * 1024) throw Invalid("Supporting sources exceed 64 MiB total, including repeated roles.");
        }
        ValidateSources(retained, read: false);
        // Validate every declaration and upload before publishing any retained evidence.
        foreach (var bytes in captured) Publish(bytes, ".source", SourceLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "safety-evidence-review-record.v1", ["reviewId"] = reviewId, ["plcPath"] = plcPath,
            ["sources"] = retained,
            ["input"] = Publish(before, ".input", SafetyEvidenceReview.InputLimit),
            ["comparisonInput"] = Publish(after, ".input", SafetyEvidenceReview.InputLimit), ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations" or "signatures" or "issues" or "changes"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit)) as JsonObject
            ?? throw new BridgeException("InvalidSafetyEvidence", "Expected a record object.");
        if (envelope.Count != 7 || (string?)envelope["schemaVersion"] != "safety-evidence-review-record.v1" || envelope["reviewId"] is not JsonValue ||
            envelope["plcPath"] is not JsonValue || !envelope.ContainsKey("comparisonInput") || envelope["report"] is not JsonObject || envelope["sources"] is not JsonArray)
            throw new BridgeException("InvalidSafetyEvidence", "Unexpected record schema.");
        ValidateSources(envelope["sources"]!.AsArray(), read: true);
        byte[] ReadInput(JsonNode? value)
        {
            if (value is not JsonObject input || input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? hash) || hash is null)
                throw new BridgeException("InvalidSafetyEvidence", "Expected a retained snapshot input reference.");
            EngineeringIdentityGuard.Sha256(hash, "input.sha256");
            if (!JsonNode.DeepEquals(input, Reference(hash, ".input"))) throw new BridgeException("InvalidSafetyEvidence", "Expected a canonical retained snapshot input reference.");
            return ProjectReviewStore.ReadArtifact(artifactsRoot, input, SafetyEvidenceReview.InputLimit);
        }
        string reviewId = (string)envelope["reviewId"]!, plcPath = (string)envelope["plcPath"]!;
        var expected = SafetyEvidenceReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), plcPath,
            ReadInput(envelope["input"]), ReadInput(envelope["comparisonInput"]));
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input and saved review.");
        var result = Summary(envelope, artifact);
        if (section == "summary") return result;
        var source = expected[section]!.AsArray();
        if (offset > source.Count) throw new BridgeException("InvalidRequest", "Offset exceeds the selected section.");
        var rows = new JsonArray(); int next = offset, used = 0;
        int budget = 128 * 1024 - JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length - 1024;
        while (next < source.Count && rows.Count < count)
        {
            JsonObject row = new() { ["index"] = next, ["value"] = source[next]!.DeepClone() };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (size > budget) { row = new() { ["index"] = next, ["omitted"] = true, ["reason"] = "Download the complete report artifact for this oversized row." }; size = 256; }
            if (used + size > budget) break;
            rows.Add(row); used += size; next++;
        }
        result["section"] = section; result["offset"] = offset; result["total"] = source.Count; result["rows"] = rows;
        result["nextOffset"] = next < source.Count ? next : null;
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 128 * 1024) throw new BridgeException("ArtifactCapacity", "Page exceeds 128 KiB; download the complete report.");
        return result;
    }
    private void ValidateSources(JsonArray sources, bool read)
    {
        if (sources.Count > 16) throw Invalid("Supply at most 16 supporting sources.");
        var identities = new HashSet<(string Phase, string Purpose, string Name)>(); long total = 0;
        foreach (var item in sources)
        {
            if (item is not JsonObject row || row.Count != 5 || row["artifact"] is not JsonObject artifact) throw Invalid("Unexpected supporting source descriptor.");
            string phase = SourceText(row, "phase", 16), purpose = SourceText(row, "purpose", 256), name = SourceText(row, "fileName", 256);
            if (phase is not ("before" or "after" or "context")) throw Invalid("Unknown supporting source phase.");
            if (PackageStorage.Relative(name) != name || name.Contains('/')) throw Invalid("Expected a display filename, not a path.");
            if (!identities.Add((phase, purpose, name))) throw Invalid("Duplicate supporting source role and filename.");
            string hash = SourceText(artifact, "sha256", 64); EngineeringIdentityGuard.Sha256(hash, "source.sha256");
            if (!JsonNode.DeepEquals(artifact, Reference(hash, ".source"))) throw Invalid("Expected a canonical retained source reference.");
            if (row["bytes"] is not JsonValue v || !v.TryGetValue<int>(out int bytes) || bytes is < 1 or > SourceLimit) throw Invalid("Supporting source must contain 1 byte..16 MiB.");
            total += bytes; if (total > 64L * 1024 * 1024) throw Invalid("Supporting sources exceed 64 MiB total, including repeated roles.");
            if (read && ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, SourceLimit).Length != bytes) throw Invalid("Retained source size differs from its descriptor.");
        }
    }
    private static string SourceText(JsonObject row, string field, int maximum)
    {
        if (row[field] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null) throw Invalid("Expected source text: " + field);
        EngineeringIdentityGuard.Text(text, "source." + field, maximum); return text;
    }
    private static BridgeException Invalid(string message) => new("InvalidSafetyEvidence", message);
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "safety-evidence-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "Safety evidence review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Safety evidence review retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["comparisonInput"] = record["comparisonInput"]?.DeepClone(), ["plcPath"] = record["plcPath"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["sources"] = record["sources"]!.DeepClone(), ["supportingSourcePolicy"] = "Opaque supplied bytes. Roles are caller declarations; proprietary formats, authenticity and association with live state are not verified.",
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["nativeExecutable"] = false, ["nativeExecuted"] = false, ["nativeQualified"] = false,
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["requiredSignatureProfileVerified"] = false, ["safetyApproval"] = false, ["wholeProgramAnalysisComplete"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
