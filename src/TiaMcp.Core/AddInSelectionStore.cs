using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.AddIns;

namespace TiaMcp.Core;

/// <summary>Immutable detached selections; reopening rechecks the exact saved source and never resumes native work.</summary>
public sealed class AddInSelectionStore(string artifactsRoot)
{
    private const int ReportLimit = 2 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "addin-selections");
    public JsonObject Review(string reviewId, string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, AddInSelection.InputLimit);
        return ReviewBytes(reviewId, bytes);
    }
    internal JsonObject ReviewBytes(string reviewId, byte[] bytes)
    {
        var request = AddInSelection.Parse(bytes);
        var report = AddInSelection.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
        var input = Publish(bytes, ".input", AddInSelection.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "addin-selection-record.v1", ["reviewId"] = reviewId, ["input"] = input, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "selections"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit))!.AsObject();
        if ((string?)envelope["schemaVersion"] != "addin-selection-record.v1") throw new BridgeException("InvalidAddInSelection", "Unexpected record schema.");
        string reviewId = (string)envelope["reviewId"]!;
        var request = AddInSelection.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, envelope["input"]!.AsObject(), AddInSelection.InputLimit));
        var expected = AddInSelection.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
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
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "addin-selections/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit) =>
        PackageStorage.PublishContent(artifactsRoot, "addin-selections", bytes, extension, limit, 512L << 20, "Selection");
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["nativeExecutable"] = false, ["nativeQualified"] = false,
        ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["producerAuthenticated"] = false, ["liveSelectionVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
