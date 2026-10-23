using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Collaboration;

namespace TiaMcp.Core;

/// <summary>Immutable supplied collaboration observations; reopening rechecks retained bytes and never resumes native work.</summary>
public sealed class TeamcenterReviewStore(string artifactsRoot)
{
    private const int ReportLimit = 64 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "teamcenter-reviews");
    public JsonObject Review(string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, TeamcenterReview.InputLimit);
        var request = TeamcenterReview.Parse(bytes);
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var report = TeamcenterReview.Build(request, reference => {
            var manifest = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(reference);
            var data = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = manifest.Path, ["sha256"] = manifest.Sha256 }, TeamcenterReview.ManifestLimit);
            sources.Add(reference, data); return data;
        });
        var manifests = new JsonObject();
        foreach (var source in sources.OrderBy(x => x.Key, StringComparer.Ordinal)) manifests[source.Key] = Publish(source.Value, ".manifest", TeamcenterReview.ManifestLimit);
        var input = Publish(bytes, ".input", TeamcenterReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "teamcenter-review-record.v1", ["input"] = input, ["manifests"] = manifests, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations" or "changes" or "issues" or "manifests"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit))!.AsObject();
        if ((string?)envelope["schemaVersion"] != "teamcenter-review-record.v1") throw new BridgeException("InvalidTeamcenterObservation", "Unexpected record schema.");
        var request = TeamcenterReview.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, envelope["input"]!.AsObject(), TeamcenterReview.InputLimit));
        var manifests = envelope["manifests"]!.AsObject(); var usedManifests = new HashSet<string>(StringComparer.Ordinal);
        var expected = TeamcenterReview.Build(request, reference => {
            if (!manifests.ContainsKey(reference)) throw new BridgeException("ArtifactChanged", "Retained manifest is missing.");
            usedManifests.Add(reference); return ProjectReviewStore.ReadArtifact(artifactsRoot, manifests[reference]!.AsObject(), TeamcenterReview.ManifestLimit);
        });
        if (usedManifests.Count != manifests.Count) throw new BridgeException("ArtifactChanged", "Unexpected unreferenced manifest evidence.");
        if (!JsonNode.DeepEquals(expected, envelope["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained input.");
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
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "teamcenter-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "Observation artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024)
                throw new BridgeException("ArtifactCapacity", "Observation retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["manifestArtifacts"] = record["manifests"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["nativeExecutable"] = false, ["nativeQualified"] = false,
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["checkoutOwnershipVerified"] = false, ["fileBytesVerified"] = false, ["nativeCacheVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
