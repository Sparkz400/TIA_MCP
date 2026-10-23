using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.VersionControl;

public sealed record VciMappingEvidence(string SchemaVersion, string WorkspaceId, EngineeringSnapshotIdentity Snapshot,
    bool EnumerationComplete, VciPackageInput[] Packages);

public sealed partial class VciWorkspaceStore
{
    private sealed record ManifestReceipt(string SourceRecordRef, string ManifestSha256, VciStoredArtifact[] Artifacts);
    private sealed record ComparisonReceipt(string BaselineRef, string ProjectRef, string WorkspaceRef,
        VciExpectedHashes ExpectedHashes, string ReportSha256, VciStoredArtifact[] Artifacts);
    private static readonly JsonSerializerOptions MappingJson = new(BridgeProtocol.Json)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 32
    };

    public object CapturePackages(string sourceRecordRef, string mappingUploadRef)
    {
        using var gate = Lock();
        var uploaded = uploads.ResolveCommitted(mappingUploadRef);
        Require(new FileInfo(uploaded.Path).Length <= 1 << 20, "VciInputLimit", "Mapping evidence is limited to 1 MiB.");
        var bytes = ReadBytes(uploaded.Path);
        Require(bytes.Length <= 1 << 20 && GitSafety.Hash(bytes) == uploaded.Sha256, "VciInputChanged", "Mapping upload changed.");
        var manifest = CaptureMapped(sourceRecordRef, bytes);
        var entry = NewEntry();
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "manifest", sourceRecordRef, mappingUploadRef });
        WriteBytes(Path.Combine(entry.Path, "mapping.json"), bytes);
        WriteReport(Path.Combine(entry.Path, "manifest.json"), manifest.ToJson());
        WriteNew(Path.Combine(entry.Path, Receipt("manifest")), new ManifestReceipt(sourceRecordRef, manifest.Sha256,
            [Artifact(entry.Path, "mapping.json"), Artifact(entry.Path, "manifest.json")]));
        return Reopen(Reference("manifest", entry.Id, Path.Combine(entry.Path, Receipt("manifest"))));
    }

    public object ComparePackages(string baselineRef, string projectRef, string workspaceRef, VciExpectedHashes expectedHashes)
    {
        using var gate = Lock();
        var report = VciComparison.Compare(LoadManifest(baselineRef).Manifest, LoadManifest(projectRef).Manifest,
            LoadManifest(workspaceRef).Manifest, expectedHashes);
        var entry = NewEntry();
        var inputs = new { baselineRef, projectRef, workspaceRef, expectedHashes };
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "comparison", inputs });
        WriteNew(Path.Combine(entry.Path, "inputs.json"), inputs);
        WriteReport(Path.Combine(entry.Path, "comparison.json"), report.ToJson());
        WriteNew(Path.Combine(entry.Path, Receipt("comparison")), new ComparisonReceipt(baselineRef, projectRef, workspaceRef,
            expectedHashes, report.Sha256, [Artifact(entry.Path, "inputs.json"), Artifact(entry.Path, "comparison.json")]));
        return Reopen(Reference("comparison", entry.Id, Path.Combine(entry.Path, Receipt("comparison"))));
    }

    private VciManifest CaptureMapped(string reference, byte[] metadata)
    {
        Require(metadata.Length <= 1 << 20, "VciInputLimit", "Mapping evidence exceeds 1 MiB.");
        VciMappingEvidence mapping;
        try
        {
            using var document = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 32 });
            UniqueProperties(document.RootElement);
            mapping = document.Deserialize<VciMappingEvidence>(MappingJson) ?? throw new JsonException("Missing mapping evidence.");
            if (!document.RootElement.GetProperty("snapshot").TryGetProperty("tiaVersion", out _)) throw new JsonException("Explicit version required.");
            foreach (var package in document.RootElement.GetProperty("packages").EnumerateArray())
                if (package.ValueKind == JsonValueKind.Object &&
                    (!package.GetProperty("profile").TryGetProperty("complete", out _) ||
                     !package.GetProperty("object").GetProperty("snapshot").TryGetProperty("tiaVersion", out _)))
                    throw new JsonException("Explicit completeness and object snapshot version required.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new BridgeException("VciInvalidMapping", "Mapping evidence must match vci-mapping-evidence.v1 without duplicate, missing or unknown fields.", error); }
        Require(mapping.SchemaVersion == "vci-mapping-evidence.v1", "VciInvalidMapping", "Unsupported mapping evidence version.");
        var parsed = Parse(reference);
        Require(parsed.Kind is "preview" or "revision", "VciInvalidReference", "Mapping requires an exact preview or committed revision.");
        var preview = Preview(parsed.Kind == "revision" ? Revision(reference).Value.PreviewRef : reference);
        var files = preview.Value.Files.Select(f =>
        {
            var bytes = ReadBytes(PackageStorage.Under(preview.Repository, f.RelativePath));
            Require(bytes.LongLength == f.Bytes && GitSafety.Hash(bytes) == f.Sha256, "VciIntegrityFailure", "Mapped source bytes changed.");
            return new VciFileInput(f.RelativePath, bytes);
        });
        return VciManifest.Capture(mapping.WorkspaceId, mapping.Snapshot, mapping.Packages, files,
            mapping.EnumerationComplete, new VciLimits(MaxFiles, 1000, Limits.MaxTotalBytes));
    }

    private (ManifestReceipt Receipt, VciManifest Manifest) LoadManifest(string reference)
    {
        var entry = Read<ManifestReceipt>(reference, "manifest");
        VerifyArtifacts(entry.Directory, entry.Value.Artifacts, "mapping.json", "manifest.json");
        var manifest = CaptureMapped(entry.Value.SourceRecordRef, ReadBytes(Path.Combine(entry.Directory, "mapping.json")));
        Require(manifest.Sha256 == entry.Value.ManifestSha256 && ReportHash(entry.Directory, "manifest.json") == manifest.Sha256,
            "VciIntegrityFailure", "Manifest no longer matches its exact mapping and raw-file inputs.");
        return (entry.Value, manifest);
    }

    private (ComparisonReceipt Receipt, VciConflictReport Report) LoadComparison(string reference)
    {
        var entry = Read<ComparisonReceipt>(reference, "comparison"); VerifyArtifacts(entry.Directory, entry.Value.Artifacts, "inputs.json", "comparison.json");
        var receipt = entry.Value;
        var report = VciComparison.Compare(LoadManifest(receipt.BaselineRef).Manifest, LoadManifest(receipt.ProjectRef).Manifest,
            LoadManifest(receipt.WorkspaceRef).Manifest, receipt.ExpectedHashes);
        Require(report.Sha256 == receipt.ReportSha256 && ReportHash(entry.Directory, "comparison.json") == report.Sha256,
            "VciIntegrityFailure", "Comparison no longer matches its exact retained inputs.");
        return (receipt, report);
    }

    private object ReopenPackages(string reference, string kind)
    {
        if (kind == "manifest")
        {
            var record = LoadManifest(reference); var manifest = record.Manifest;
            var owned = manifest.Packages.SelectMany(p => p.ExpectedFiles).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);
            return new { recordRef = reference, kind, state = "complete", nativeQualified = false, targetProbed = false,
                record.Receipt.SourceRecordRef, manifestSha256 = manifest.Sha256, manifest.WorkspaceId, manifest.EnumerationComplete,
                fileCount = manifest.Files.Count, packageCount = manifest.Packages.Count,
                incompletePackageCount = manifest.Packages.Count(p => p.Issues.Count != 0),
                unmanagedFileCount = manifest.Files.Count(f => !owned.Contains(f.RelativePath)),
                artifacts = record.Receipt.Artifacts, receiptName = "receipt.json" };
        }
        var comparison = LoadComparison(reference);
        return new { recordRef = reference, kind, state = "complete", nativeQualified = false, canSynchronize = false, resolutionApplied = false,
            comparison.Receipt.BaselineRef, comparison.Receipt.ProjectRef, comparison.Receipt.WorkspaceRef,
            reportSha256 = comparison.Report.Sha256, rowCount = comparison.Report.Rows.Count,
            conflictCount = comparison.Report.Rows.Count(r => r.Conflict), issues = comparison.Report.Issues,
            stateCounts = comparison.Report.Rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count()),
            artifacts = comparison.Receipt.Artifacts, receiptName = "receipt.json" };
    }

    private VciStoredArtifact[] PackageArtifacts(string reference, string kind) =>
        kind == "manifest" ? LoadManifest(reference).Receipt.Artifacts : LoadComparison(reference).Receipt.Artifacts;
    private static string ReportHash(string directory, string name) => CanonicalJson.Sha256(JsonNode.Parse(ReadBytes(Path.Combine(directory, name)))!);
    private static void WriteReport(string path, JsonObject report)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, BridgeProtocol.Json);
        Require(bytes.Length <= MaxFileBytes, "VciOutputLimit", "Complete package report exceeds 16 MiB.");
        WriteBytes(path, bytes);
    }
    private static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate mapping property."); UniqueProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }
}
