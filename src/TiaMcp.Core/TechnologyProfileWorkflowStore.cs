using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TechnologyObjects;

namespace TiaMcp.Core;

/// <summary>Immutable supplied technology profile reviews; exact input and saved source are revalidated on every reopen.</summary>
public sealed class TechnologyProfileWorkflowStore(string artifactsRoot)
{
    private const int SourceLimit = 16 * 1024 * 1024;
    private const int SourcesTotalLimit = 64 * 1024 * 1024;
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "technology-profile-reviews");
    public JsonObject Review(string reviewId, string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, TechnologyProfileReview.InputLimit);
        var request = TechnologyProfileReview.Parse(bytes);
        var sources = Sources(request);
        var report = Build(reviewId, request);
        // Validate every upload before publishing any owned source; failed capture cannot publish a record.
        var captured = sources.Select(source =>
        {
            var resolved = new ArtifactUploadStore(artifactsRoot).ResolveCommitted((string)source["uploadRef"]!);
            if (resolved.FileName != (string)source["fileName"]! || resolved.Sha256 != (string)source["sha256"]!)
                throw Invalid("Source filename or digest differs from its committed upload.");
            var content = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = resolved.Path, ["sha256"] = resolved.Sha256 }, SourceLimit);
            if (content.Length != (int)source["bytes"]!) throw Invalid("Source byte count differs from its declaration.");
            return content;
        }).ToArray();
        foreach (var content in captured) Publish(content, ".source", SourceLimit);
        AddSources(report, sources);
        var input = Publish(bytes, ".input", TechnologyProfileReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "technology-profile-review-record." + Version(request), ["reviewId"] = reviewId, ["input"] = input, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations" or "relationships" or "changes" or "context" or "sources" or "consumers" or "consumerChanges" or "impactPaths" or "impactTraversal" or "encoderConnections" or "encoderChanges" or "encoderCoverage"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var envelope = ReadVerified(recordId);
        var artifact = Reference(recordId, ".json");
        var expected = envelope["report"]!.AsObject();
        var result = Summary(envelope, artifact);
        if (section == "summary") return result;
        if (section.StartsWith("encoder", StringComparison.Ordinal) && (string?)expected["schemaVersion"] != "technology-profile-review.v4") throw new BridgeException("InvalidRequest", "Encoder sections require a retained v4 record.");
        var source = section is "context" or "impactTraversal" or "encoderCoverage" ? expected[section] is JsonObject context ? new JsonArray(context.DeepClone()) : new JsonArray() : expected[section] as JsonArray ?? new JsonArray();
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
    private static JsonObject[] Sources(JsonObject request)
    {
        if (Version(request) == "v1") return [];
        if (request["sources"] is not JsonArray sources || sources.Count > 16 || Version(request) == "v2" && sources.Count == 0) throw Invalid("Supply up to 16 source declarations; v2 requires at least one.");
        var rows = new List<JsonObject>(); var labels = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var item in sources)
        {
            string[] fields = ["label", "purpose", "phase", "fileName", "uploadRef", "sha256", "bytes"];
            if (item is not JsonObject row || row.Count != fields.Length || fields.Any(f => !row.ContainsKey(f))) throw Invalid("Unexpected source declaration fields.");
            if (!labels.Add(SourceText(row,"label",256))) throw Invalid("Duplicate exact source label.");
            SourceText(row,"purpose",1024);
            if (SourceText(row,"phase",16) is not ("before" or "after" or "context")) throw Invalid("Unknown source phase.");
            string name = SourceText(row,"fileName",256);
            if (PackageStorage.Relative(name) != name || name.Contains('/')) throw Invalid("Source filename must be an exact display filename, not a path.");
            string reference = SourceText(row,"uploadRef",39);
            if (reference.Length != 39 || !reference.StartsWith("upload:",StringComparison.Ordinal) || reference.Skip(7).Any(c => !"0123456789abcdef".Contains(c))) throw Invalid("Expected a committed upload reference.");
            EngineeringIdentityGuard.Sha256(SourceText(row,"sha256",64),"source.sha256");
            if (row["bytes"] is not JsonValue v || !v.TryGetValue<int>(out int bytes) || bytes is < 1 or > SourceLimit) throw Invalid("Source size must be 1 byte..16 MiB.");
            total += bytes; if (total > SourcesTotalLimit) throw Invalid("Source declarations exceed 64 MiB total, including repeated roles.");
            rows.Add(row);
        }
        return rows.ToArray();
    }
    private static string SourceText(JsonObject row,string field,int maximum)
    {
        if (row[field] is not JsonValue v || !v.TryGetValue<string>(out string? text) || text is null) throw Invalid("Expected source text: " + field);
        EngineeringIdentityGuard.Text(text,"source."+field,maximum); return text;
    }
    private static string Version(JsonObject request) => request["schemaVersion"] is JsonValue v && v.TryGetValue<string>(out string? text) ? text switch {
        "technology-profile-review-request.v1" => "v1", "technology-profile-review-request.v2" => "v2",
        "technology-profile-review-request.v3" => "v3", "technology-profile-review-request.v4" => "v4", _ => throw Invalid("Unknown technology request version.") } : throw Invalid("Expected technology request version text.");
    private JsonObject Build(string reviewId, JsonObject request, Action<int>? accountBytes = null)
    {
        var review = new ProjectReviewStore(artifactsRoot).Read(reviewId);
        var report = TechnologyProfileReview.Build(reviewId, review, BaseRequest(request));
        if (Version(request) is "v3" or "v4") TechnologyProfileConsumers.Add(report, review, request);
        if (Version(request) == "v4")
        {
            var dependencies = new Dictionary<string, JsonObject>(StringComparer.Ordinal); long bytes = 0;
            TechnologyEncoderReview.Add(report, review, request, id =>
            {
                if (dependencies.TryGetValue(id, out var retained)) return retained;
                if (dependencies.Count == 8) throw Invalid("At most eight distinct retained drive records per encoder review.");
                retained = new DriveProfileWorkflowStore(artifactsRoot).ReadVerified(id, count =>
                {
                    bytes += count; if (bytes > 64L * 1024 * 1024) throw Invalid("Retained encoder drive records and inputs exceed 64 MiB.");
                    accountBytes?.Invoke(count);
                });
                dependencies.Add(id, retained); return retained;
            });
        }
        return report;
    }
    private static JsonObject BaseRequest(JsonObject request)
    {
        if (Version(request) == "v1") return request;
        var baseline = request.DeepClone().AsObject(); baseline.Remove("sources"); if (Version(request) is "v3" or "v4") { baseline.Remove("consumers"); baseline.Remove("impactLimits"); } if (Version(request) == "v4") baseline.Remove("encoders"); baseline["schemaVersion"] = "technology-profile-review-request.v1"; return baseline;
    }
    private void AddSources(JsonObject report, JsonObject[] sources, Action<int>? accountBytes = null)
    {
        if (sources.Length == 0) { if ((string?)report["schemaVersion"] is "technology-profile-review.v3" or "technology-profile-review.v4") report["sources"] = new JsonArray(); return; } // Preserve old records exactly.
        var rows = new JsonArray();
        foreach (var source in sources)
        {
            var artifact = Reference((string)source["sha256"]!, ".source");
            byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, SourceLimit); accountBytes?.Invoke(bytes.Length);
            if (bytes.Length != (int)source["bytes"]!) throw Invalid("Retained source size differs from its declaration.");
            rows.Add(new JsonObject { ["label"] = source["label"]!.DeepClone(), ["purpose"] = source["purpose"]!.DeepClone(),
                ["phase"] = source["phase"]!.DeepClone(), ["fileName"] = source["fileName"]!.DeepClone(), ["bytes"] = bytes.Length, ["artifact"] = artifact });
        }
        if ((string?)report["schemaVersion"] is not ("technology-profile-review.v3" or "technology-profile-review.v4")) report["schemaVersion"] = "technology-profile-review.v2"; report["sources"] = rows;
    }
    private static BridgeException Invalid(string message) => new("InvalidTechnologyProfile", message);
    internal JsonObject ReadVerified(string recordId, Action<int>? accountBytes = null)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        var artifact = Reference(recordId, ".json");
        byte[] recordBytes = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit); accountBytes?.Invoke(recordBytes.Length);
        var envelope = JsonNode.Parse(recordBytes) as JsonObject
            ?? throw new BridgeException("InvalidTechnologyProfile", "Expected a record object.");
        if (envelope.Count != 4 || (string?)envelope["schemaVersion"] is not ("technology-profile-review-record.v1" or "technology-profile-review-record.v2" or "technology-profile-review-record.v3" or "technology-profile-review-record.v4") || envelope["reviewId"] is not JsonValue reviewValue || !reviewValue.TryGetValue<string>(out _) || envelope["input"] is not JsonObject input || envelope["report"] is not JsonObject)
            throw new BridgeException("InvalidTechnologyProfile", "Unexpected record schema.");
        if (input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? inputHash) || inputHash is null ||
            !JsonNode.DeepEquals(input, Reference(inputHash, ".input"))) throw new BridgeException("InvalidTechnologyProfile", "Expected a retained technology profile input reference.");
        EngineeringIdentityGuard.Sha256(inputHash, "input.sha256");
        string reviewId = (string)envelope["reviewId"]!;
        byte[] inputBytes = ProjectReviewStore.ReadArtifact(artifactsRoot, envelope["input"]!.AsObject(), TechnologyProfileReview.InputLimit); accountBytes?.Invoke(inputBytes.Length);
        var request = TechnologyProfileReview.Parse(inputBytes);
        var sources = Sources(request);
        if ((string?)envelope["schemaVersion"] != "technology-profile-review-record." + Version(request)) throw Invalid("Record version differs from retained request.");
        var expected = Build(reviewId, request, accountBytes);
        AddSources(expected, sources, accountBytes);
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input and saved review.");
        return envelope;
    }
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "technology-profile-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "Technology profile review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Technology profile review retention exceeds 512 MiB, including incomplete files.");
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
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["nativeExecuted"] = false, ["targetCompatibilityVerified"] = false, ["wholeObjectAnalysisComplete"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
