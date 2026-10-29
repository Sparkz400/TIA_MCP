using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.HmiComponents;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TiaMcp.HmiComponents.Tests")]

namespace TiaMcp.Core;

/// <summary>Immutable component/HMI review with independently retained input files and exact saved reviews.</summary>
public sealed partial class HmiComponentWorkflowStore(string artifactsRoot)
{
    private const int ReportLimit = 16 * 1024 * 1024;
    private string Root => Path.Combine(Path.GetFullPath(artifactsRoot), "hmi-component-reviews");
    public JsonObject AuthorTextLists(string? uploadRef = null, string? recordId = null)
        => AuthorTextListsCore(uploadRef, recordId, 0, new AuthorReadBudget());

    private sealed class AuthorReadBudget { internal long Bytes; }
    private JsonObject AuthorTextListsCore(string? uploadRef, string? recordId, int depth, AuthorReadBudget budget)
    {
        if (depth > 16) throw HmiComponentReview.Error("Authoring lineage exceeds 16 predecessor records.");
        if ((uploadRef is null) == (recordId is null)) throw HmiComponentReview.Error("Select exactly one committed uploadRef or retained authoring recordId.");
        JsonObject input; byte[] bytes; JsonObject? retained = null;
        if (recordId is not null)
        {
            EngineeringIdentityGuard.Sha256(recordId, "recordId");
            retained = HmiComponentReview.Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, Reference(recordId, ".author.json"), ReportLimit)));
            input = HmiComponentReview.Object(retained["input"]);
            string hash = HmiComponentReview.Text(input, "sha256", 64); EngineeringIdentityGuard.Sha256(hash, "input.sha256");
            if (!JsonNode.DeepEquals(input, Reference(hash, ".author-input"))) throw HmiComponentReview.Error("Expected retained authoring input.");
            bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, input, HmiComponentReview.InputLimit);
        }
        else
        {
            var source = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef!);
            bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = source.Path, ["sha256"] = source.Sha256 }, HmiComponentReview.InputLimit);
            input = Reference(Convert.ToHexStringLower(SHA256.HashData(bytes)), ".author-input");
        }
        var request = HmiComponentReview.Parse(bytes);
        if ((string?)request["schemaVersion"] is "unified-text-list-author-request.v2" or "unified-text-list-author-request.v3")
            return AuthorBoundTextLists(request, bytes, input, retained, recordId, depth, budget);
        var candidates = UnifiedTextListAuthoring.Build(request);
        if (retained is null) UnifiedTextListAuthoring.RequireNativeImportable(request);
        var files = new JsonArray();
        foreach (var candidate in candidates)
        {
            var reference = Reference(Convert.ToHexStringLower(SHA256.HashData(candidate.Bytes)), ".candidate");
            if (retained is null) Publish(candidate.Bytes, ".candidate", UnifiedTextListAuthoring.OutputLimit);
            else if (!ProjectReviewStore.ReadArtifact(artifactsRoot, reference, UnifiedTextListAuthoring.OutputLimit).AsSpan().SequenceEqual(candidate.Bytes))
                throw new BridgeException("ArtifactChanged", "Retained candidate differs from authored intent.");
            files.Add(new JsonObject { ["fileName"] = candidate.Name, ["bytes"] = candidate.Bytes.Length, ["artifact"] = reference });
        }
        var record = new JsonObject { ["schemaVersion"] = "unified-text-list-author-record.v1", ["input"] = input.DeepClone(), ["files"] = files,
            ["listCount"] = request["lists"]!.AsArray().Count, ["textCount"] = request["texts"]!.AsArray().Count, ["languageCount"] = request["languages"]!.AsArray().Count,
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["preservationVerified"] = false, ["consumerCoverageComplete"] = false,
            ["policy"] = "Authored plain Unified text-list YAML candidates only. Download both files. No import, merge, system-list editing, formatted text, project language activation, native numeric-range certification or consumer completeness. Existing-list import can delete/recreate content; use independent baseline/current/proposed component review and native qualification before any import." };
        JsonObject artifact;
        if (retained is null)
        {
            Publish(bytes, ".author-input", HmiComponentReview.InputLimit);
            artifact = Publish(JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json), ".author.json", ReportLimit);
        }
        else
        {
            if (!JsonNode.DeepEquals(record, retained)) throw new BridgeException("ArtifactChanged", "Authoring record differs from its retained input and recomputed candidates.");
            artifact = Reference(recordId!, ".author.json");
        }
        var result = record.DeepClone().AsObject(); result["recordId"] = artifact["sha256"]!.DeepClone(); result["recordArtifact"] = artifact;
        return result;
    }

    public JsonObject Review(string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 }, HmiComponentReview.InputLimit);
        var request = HmiComponentReview.Parse(bytes);
        var files = HmiComponentReview.Files(request);
        var captured = files.Select(file =>
        {
            var source = new ArtifactUploadStore(artifactsRoot).ResolveCommitted((string)file["uploadRef"]!);
            byte[] data = ProjectReviewStore.ReadArtifact(artifactsRoot, new() { ["path"] = source.Path, ["sha256"] = file["sha256"]!.DeepClone() }, HmiComponentReview.FileLimit);
            if (data.Length != (int)file["bytes"]!) throw HmiComponentReview.Error("Uploaded file size differs from its declaration.");
            return data;
        }).ToArray();
        var originals = files.Select((file, index) => (Id: (string)file["id"]!, Bytes: captured[index])).ToDictionary(f => f.Id, f => f.Bytes, StringComparer.Ordinal);
        var report = Build(request, file => originals[(string)file["id"]!]);
        foreach (var data in captured) Publish(data, ".source", HmiComponentReview.FileLimit);
        AddFiles(report, files);
        var input = Publish(bytes, ".input", HmiComponentReview.InputLimit);
        var envelope = new JsonObject { ["schemaVersion"] = "hmi-component-review-record." + Version(request), ["input"] = input, ["report"] = report };
        return Summary(envelope, Publish(JsonSerializer.SerializeToUtf8Bytes(envelope, BridgeProtocol.Json), ".json", ReportLimit));
    }
    public JsonObject Page(string recordId, string section = "summary", int offset = 0, int count = 50)
    {
        EngineeringIdentityGuard.Sha256(recordId, "recordId");
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "components" or "outputs" or "bindings" or "files" or "consumerPaths" or "consumerCoverage" or "effectChecks" or "derivedEffects" or "expectationCoverage" or "libraryAssociations" or "libraryCoverage" or "dependencyAssociations" or "dependencyCoverage" or "dependencyIdentities" or "dependencyImpacts" or "dependencyImpactCoverage")) throw HmiComponentReview.Error("Select a documented review section, a nonnegative offset and 1..200 rows.");
        var artifact = Reference(recordId, ".json");
        var record = HmiComponentReview.Object(JsonNode.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, ReportLimit)));
        HmiComponentReview.Fields(record, "schemaVersion input report");
        if ((string?)record["schemaVersion"] is not ("hmi-component-review-record.v1" or "hmi-component-review-record.v2" or "hmi-component-review-record.v3" or "hmi-component-review-record.v4" or "hmi-component-review-record.v5" or "hmi-component-review-record.v6" or "hmi-component-review-record.v7")) throw HmiComponentReview.Error("Unexpected record schema.");
        var input = HmiComponentReview.Object(record["input"]);
        string inputHash = HmiComponentReview.Text(input, "sha256", 64); EngineeringIdentityGuard.Sha256(inputHash, "input.sha256");
        if (!JsonNode.DeepEquals(input, Reference(inputHash, ".input"))) throw HmiComponentReview.Error("Expected a retained input reference.");
        var request = HmiComponentReview.Parse(ProjectReviewStore.ReadArtifact(artifactsRoot, input, HmiComponentReview.InputLimit));
        if ((string?)record["schemaVersion"] != "hmi-component-review-record." + Version(request)) throw HmiComponentReview.Error("Record and request versions differ.");
        var expected = Build(request); AddFiles(expected, HmiComponentReview.Files(request));
        if (!JsonNode.DeepEquals(expected, record["report"])) throw new BridgeException("ArtifactChanged", "Report differs from its retained request, files or saved reviews.");
        var result = Summary(record, artifact);
        if (section == "summary") return result;
        var source = expected[section] as JsonArray ?? throw HmiComponentReview.Error("This section is unavailable in the retained review version.");
        if (offset > source.Count) throw HmiComponentReview.Error("Offset exceeds the selected section.");
        var rows = new JsonArray(); int next = offset, used = 0;
        while (next < source.Count && rows.Count < count)
        {
            JsonObject row = new() { ["index"] = next, ["value"] = source[next]!.DeepClone() };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (size > 120 * 1024) { row = new() { ["index"] = next, ["omitted"] = true, ["reason"] = "Download the complete report artifact for this oversized row." }; size = 256; }
            if (used + size > 120 * 1024) break;
            rows.Add(row); used += size; next++;
        }
        result["section"] = section; result["offset"] = offset; result["total"] = source.Count; result["rows"] = rows; result["nextOffset"] = next < source.Count ? next : null;
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 128 * 1024) throw new BridgeException("ArtifactCapacity", "Page exceeds 128 KiB; download the complete report.");
        return result;
    }
    private static string Version(JsonObject request) => (string?)request["schemaVersion"] switch {
        "hmi-component-review-request.v1" => "v1", "hmi-component-review-request.v2" => "v2", "hmi-component-review-request.v3" => "v3", "hmi-component-review-request.v4" => "v4", "hmi-component-review-request.v5" => "v5", "hmi-component-review-request.v6" => "v6", "hmi-component-review-request.v7" => "v7", _ => throw HmiComponentReview.Error("Unknown request version.") };
    private JsonObject Build(JsonObject request, Func<JsonObject, byte[]>? readFile = null)
    {
        JsonObject Read(string id) => new ProjectReviewStore(artifactsRoot).Read(id);
        if (Version(request) == "v1") return HmiComponentReview.Build(request, Read);
        bool impacts = Version(request) == "v7", dependencies = Version(request) is "v6" or "v7", libraries = Version(request) is "v5" or "v6" or "v7", derived = Version(request) is "v4" or "v5" or "v6" or "v7", effects = Version(request) is "v3" or "v4" or "v5" or "v6" or "v7";
        HmiComponentReview.Fields(request, "schemaVersion operation reason beforeComponent afterComponent componentMatches destination outputs bindings files impactLimits" + (effects ? " observations" : "") + (derived ? " expectations" : "") + (libraries ? " libraryAssociations" : "") + (dependencies ? " dependencyAssociations" : ""));
        var baseline = request.DeepClone().AsObject(); baseline.Remove("impactLimits"); if (effects) baseline.Remove("observations"); baseline["schemaVersion"] = "hmi-component-review-request.v1";
        if (derived) baseline.Remove("expectations");
        if (libraries) baseline.Remove("libraryAssociations");
        if (dependencies) baseline.Remove("dependencyAssociations");
        var report = HmiComponentReview.Build(baseline, Read); HmiComponentConsumers.Add(report, request, Read);
        if (effects) HmiComponentEffects.Add(report, request);
        long examinedBytes = 0;
        var readBytes = readFile ?? (file => ProjectReviewStore.ReadArtifact(artifactsRoot, Reference((string)file["sha256"]!, ".source"), HmiComponentReview.FileLimit));
        System.Xml.Linq.XDocument ReadXml(JsonObject file) => HmiComponentXml.Read(file, readBytes, ref examinedBytes);
        if (derived) HmiComponentExpectations.Add(report, request, ReadXml);
        if (libraries) HmiComponentLibraryAssociations.Add(report, request, ReadXml);
        if (dependencies) HmiComponentDependencyAssociations.Add(report, request);
        if (impacts) HmiComponentDependencyImpacts.Add(report);
        return report;
    }
    private void AddFiles(JsonObject report, JsonObject[] files)
    {
        var rows = new JsonArray();
        foreach (var file in files)
        {
            var artifact = Reference((string)file["sha256"]!, ".source");
            byte[] bytes = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, HmiComponentReview.FileLimit);
            if (bytes.Length != (int)file["bytes"]!) throw HmiComponentReview.Error("Retained file size differs from its declaration.");
            rows.Add(new JsonObject { ["id"] = file["id"]!.DeepClone(), ["fileName"] = file["fileName"]!.DeepClone(), ["bytes"] = bytes.Length, ["artifact"] = artifact });
        }
        report["files"] = rows;
    }
    private JsonObject Reference(string hash, string extension) => new() { ["path"] = "hmi-component-reviews/" + hash + extension, ["sha256"] = hash };
    private JsonObject Publish(byte[] bytes, string extension, int limit)
    {
        if (bytes.Length > limit) throw new BridgeException("ArtifactCapacity", "HMI component review artifact exceeds its byte bound.");
        PackageStorage.EnsureNoLinks(Root); Directory.CreateDirectory(Root);
        using var gate = PackageStorage.Lock(Path.Combine(Root, ".write.lock"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var artifact = Reference(hash, extension);
        string path = PackageStorage.Under(Root, hash + extension);
        if (!File.Exists(path))
        {
            if (new DirectoryInfo(Root).EnumerateFiles().Sum(f => f.Length) + bytes.Length > 512L * 1024 * 1024) throw new BridgeException("ArtifactCapacity", "Review retention exceeds 512 MiB, including incomplete files.");
            string temporary = PackageStorage.Under(Root, hash + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            PackageStorage.PublishFile(temporary, path);
        }
        _ = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, limit);
        return artifact;
    }
    private static JsonObject Summary(JsonObject record, JsonObject artifact) => new() {
        ["recordId"] = artifact["sha256"]!.DeepClone(), ["input"] = record["input"]!.DeepClone(), ["reportArtifact"] = artifact.DeepClone(),
        ["summary"] = record["report"]!["summary"]!.DeepClone(), ["operation"] = record["report"]!["operation"]!.DeepClone(),
        ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["wholeProjectPreservationVerified"] = false,
        ["componentMemberCoverageComplete"] = false, ["consumerCoverageComplete"] = false,
        ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };
}
