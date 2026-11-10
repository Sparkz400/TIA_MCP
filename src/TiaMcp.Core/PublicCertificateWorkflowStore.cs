using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.Security;

namespace TiaMcp.Core;

/// <summary>Immutable supplied public certificate reviews; exact input and saved source are revalidated on every reopen.</summary>
public sealed class PublicCertificateWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "public-certificate-reviews");
    public JsonObject Review(byte[] bytes)
    {
        var request = PublicCertificateReview.Parse(bytes);
        string reviewId = PublicCertificateReview.ReviewId(request);
        var report = PublicCertificateReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
        var input = Publish(bytes, ".input", PublicCertificateReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "public-certificate-review-record.v1", ["reviewId"] = reviewId, ["input"] = input, ["report"] = report };
        var artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit);
        return Summary(envelope, artifact);
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "observations"))
            throw new BridgeException("InvalidRequest", "Select a report section, nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var envelope = JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit)) as JsonObject
            ?? throw new BridgeException("InvalidPublicCertificateReview", "Expected a record object.");
        if (envelope.Count != 4 || (string?)envelope["schemaVersion"] != "public-certificate-review-record.v1" || envelope["reviewId"] is not JsonValue reviewValue || !reviewValue.TryGetValue<string>(out _) || envelope["input"] is not JsonObject input || envelope["report"] is not JsonObject)
            throw new BridgeException("InvalidPublicCertificateReview", "Unexpected record schema.");
        if (input["sha256"] is not JsonValue hashValue || !hashValue.TryGetValue<string>(out string? inputHash) || inputHash is null ||
            !JsonNode.DeepEquals(input, Reference(inputHash, ".input"))) throw new BridgeException("InvalidPublicCertificateReview", "Expected a retained public certificate input reference.");
        EngineeringIdentityGuard.Sha256(inputHash, "input.sha256");
        string reviewId = (string)envelope["reviewId"]!;
        var request = PublicCertificateReview.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, envelope["input"]!.AsObject(), PublicCertificateReview.InputLimit));
        var expected = PublicCertificateReview.Build(reviewId, new ProjectReviewStore(artifactsRoot).Read(reviewId), request);
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
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "public-certificate-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit) =>
        PackageStorage.PublishContent(artifactsRoot, "public-certificate-reviews", bytes, extension, limit, 512L << 20, "Public certificate review");
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["reviewId"] = record["reviewId"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["changedFields"] = record["report"]!["changedFields"]!.DeepClone(),
        ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["associationVerified"] = false, ["signatureVerified"] = false,
        ["chainVerified"] = false, ["revocationChecked"] = false, ["endpointAuthenticated"] = false, ["privateKeyAvailabilityVerified"] = false, ["usagePolicyVerified"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
