using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.WebApplications;

namespace TiaMcp.Core;

/// <summary>Immutable supplied web application reviews; exact input and saved source are revalidated on every reopen.</summary>
public sealed class WebApplicationWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "web-application-reviews");
    public JsonObject Review(byte[] bytes)
    {
        var request = WebApplicationReview.Parse(bytes);
        string reviewId = WebApplicationReview.ReviewId(request);
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var report = WebApplicationReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request, (phase, archive) => {
            var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted((string)archive["uploadRef"]!);
            if (upload.Sha256 != (string)archive["sha256"]!) throw new BridgeException("ArtifactChanged", "Archive differs from the explicitly expected SHA-256.");
            byte[] data = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, WebApplicationReview.ArchiveLimit);
            var manifest = InspectArchive(data, archive); originals.Add(phase, data); return manifest;
        });
        var archives = new JsonObject();
        foreach (var pair in originals) archives[pair.Key] = Publish(pair.Value, ".zip", WebApplicationReview.ArchiveLimit);
        var input = Publish(bytes, ".input", WebApplicationReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "web-application-review-record.v1", ["reviewId"] = reviewId, ["input"] = input, ["archives"] = archives, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations" or "configurationChanges" or "applicationChanges" or "fileChanges"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit)) as JsonObject
            ?? throw new BridgeException("InvalidWebApplicationReview", "Expected a record object.");
        if (envelope.Count != 5 || (string?)envelope["schemaVersion"] != "web-application-review-record.v1" || envelope["reviewId"] is not JsonValue reviewValue || !reviewValue.TryGetValue<string>(out _) || envelope["input"] is not JsonObject input || envelope["report"] is not JsonObject || envelope["archives"] is not JsonObject)
            throw new BridgeException("InvalidWebApplicationReview", "Unexpected record schema.");
        if (input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? inputHash) || inputHash is null ||
            !JsonNode.DeepEquals(input, Reference(inputHash, ".input"))) throw new BridgeException("InvalidWebApplicationReview", "Expected a retained web application input reference.");
        EngineeringIdentityGuard.Sha256(inputHash, "input.sha256");
        string reviewId = (string)envelope["reviewId"]!;
        var request = WebApplicationReview.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, envelope["input"]!.AsObject(), WebApplicationReview.InputLimit));
        var archives = envelope["archives"]!.AsObject(); var phases = new HashSet<string>(StringComparer.Ordinal);
        var expected = WebApplicationReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request, (phase, archive) => {
            phases.Add(phase); string hash = (string)archive["sha256"]!;
            if (!JsonNode.DeepEquals(archives[phase], Reference(hash, ".zip"))) throw new BridgeException("ArtifactChanged", "Retained archive reference differs from the supplied identity.");
            return InspectArchive(ProjectReviewStore.ReadArtifact(artifactsRoot, archives[phase]!.AsObject(), WebApplicationReview.ArchiveLimit), archive);
        });
        if (!archives.Select(p => p.Key).ToHashSet(StringComparer.Ordinal).SetEquals(phases)) throw new BridgeException("ArtifactChanged", "Unexpected retained archive phases.");
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input and saved review.");
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
    private JsonObject InspectArchive(byte[] bytes, JsonObject selection)
    {
        string analysisRoot = Path.Combine(Path.GetFullPath(artifactsRoot), "web-application-analysis");
        PackageStorage.EnsureNoLinks(analysisRoot); Directory.CreateDirectory(analysisRoot);
        using var gate = PackageStorage.Lock(Path.Combine(analysisRoot, ".inspect.lock"), TimeSpan.Zero);
        string scratch = PackageStorage.Under(analysisRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            string source = PackageStorage.Under(scratch, "input.zip"); File.WriteAllBytes(source, bytes);
            var packages = new PackageStore(scratch, new PackageIntakeLimits { MaxInputBytes = WebApplicationReview.ArchiveLimit, MaxExpandedBytes = 64L * 1024 * 1024, MaxStoredBytes = 128L * 1024 * 1024, MaxEntries = 4096, MaxWorkSeconds = 60 });
            var staged = packages.Stage(hostPath: source); var all = new List<PackageFile>(); int offset = 0;
            while (true) { var page = packages.Inspect(staged.PackageId, "files", offset, 200); all.AddRange(page.Items.Cast<PackageFile>()); if (page.NextOffset is not int next) break; offset = next; }
            string root = (string)selection["rootRelative"]!, prefix = "content/000001/" + (root.Length == 0 ? "" : root + "/"), defaultPage = (string)selection["defaultPage"]!;
            var files = all.Where(f => f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).Select(f => f with { RelativePath = f.RelativePath[prefix.Length..] }).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToArray();
            if (files.Length == 0) throw new BridgeException("InvalidWebApplicationReview", "Selected archive root contains no files.");
            var pageFile = files.SingleOrDefault(f => f.RelativePath == defaultPage) ?? throw new BridgeException("InvalidWebApplicationReview", "Default page is not an exact file inside the selected archive root.");
            var rows = new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["relativePath"] = f.RelativePath, ["bytes"] = f.Bytes, ["sha256"] = f.Sha256 }).ToArray());
            return new JsonObject { ["archiveSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["archiveBytes"] = bytes.Length,
                ["files"] = rows, ["selectedBytes"] = files.Sum(f => f.Bytes), ["defaultPageSha256"] = pageFile.Sha256,
                ["topLevelArchiveFiles"] = all.Count(f => f.RelativePath.StartsWith("content/000001/", StringComparison.Ordinal)),
                ["excludedTopLevelFiles"] = all.Count(f => f.RelativePath.StartsWith("content/000001/", StringComparison.Ordinal)) - files.Length,
                ["nestedExtractedFiles"] = all.Count(f => f.RelativePath.StartsWith("nested/", StringComparison.Ordinal)),
                ["manifestSha256"] = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows, BridgeProtocol.Json))) };
        }
        finally { PackageStorage.DeleteOwned(analysisRoot, scratch); }
    }
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "web-application-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "Public certificate review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Public certificate review retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["archiveChanges"] = record["report"]!["archiveChanges"]!.DeepClone(), ["archives"] = record["archives"]!.DeepClone(),
        ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["associationVerified"] = false,
        ["siemensCompatibilityVerified"] = false, ["generatedBlocksVerified"] = false, ["deploymentVerified"] = false, ["endpointVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
