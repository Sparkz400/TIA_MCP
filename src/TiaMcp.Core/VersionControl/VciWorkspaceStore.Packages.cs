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

    private static readonly string[] ResolutionFields = ["workspacePath", "objectPath", "comparisonRef", "packageId", "take", "mergedRecordRef"];
    private const int MaxResolutionFiles = 16, MaxResolutionBytes = 4 << 20;

    /// <summary>
    /// Bind a vci_conflict_resolve request to one reviewed divergentChange row of a retained three-way comparison (baseline,
    /// project = native side, workspace = remote side). The worker receives the exact remote package files it must find in the
    /// workspace and, for take=remote or take=merged, the exact bytes it writes before WorkspaceToProject; take=native
    /// synchronizes ProjectToWorkspace. The caller never supplies hashes or bytes itself.
    /// </summary>
    public JsonObject ConflictResolutionPayload(JsonElement payload)
    {
        using var gate = Lock();
        Require(payload.ValueKind == JsonValueKind.Object, "InvalidRequest", "The vci_conflict_resolve payload must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
            Require(ResolutionFields.Contains(property.Name) && seen.Add(property.Name) &&
                (property.Value.ValueKind == JsonValueKind.String || property.Name == "mergedRecordRef" && property.Value.ValueKind == JsonValueKind.Null),
                "InvalidRequest", "vci_conflict_resolve takes the strings workspacePath, objectPath, comparisonRef, packageId, take and, for take=merged, mergedRecordRef.");
        string Field(string name) => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length is > 0 and <= 4096
            ? value.GetString()! : throw new BridgeException("InvalidRequest", name + " is required.");
        string take = Field("take"), objectPath = Field("objectPath"), packageId = Field("packageId"), comparisonRef = Field("comparisonRef");
        string? merged = payload.TryGetProperty("mergedRecordRef", out var mergedValue) && mergedValue.ValueKind == JsonValueKind.String ? mergedValue.GetString() : null;
        Require(take is "native" or "remote" or "merged", "InvalidRequest", "take must be native, remote or merged.");
        Require(take == "merged" == (merged is not null), "InvalidRequest", "mergedRecordRef is required for take=merged and only then.");
        var (receipt, report) = LoadComparison(comparisonRef);
        var row = report.Rows.SingleOrDefault(r => r.PackageId == packageId);
        Require(row is not null, "VciPackageNotFound", "The comparison has no row for packageId.");
        Require(row.State == "divergentChange", "VciResolutionUnsupported", "Only a reviewed divergentChange row is resolved (this row is " + row.State +
            "); one-sided changes use vci_synchronize, and additions, deletions, identity, ownership, stale and incomplete rows stay refused.");
        var remoteSide = LoadManifest(receipt.WorkspaceRef); var nativeSide = LoadManifest(receipt.ProjectRef);
        var package = remoteSide.Manifest.Packages.Single(p => p.PackageId == packageId);
        var native = nativeSide.Manifest.Packages.Single(p => p.PackageId == packageId);
        var paths = package.ExpectedFiles.Select(f => f.RelativePath).ToArray();
        string workspacePath = Field("workspacePath");
        // The comparison's workspace lineage names the exact vci_workspace_inspect workspace path.
        Require(remoteSide.Manifest.WorkspaceId == workspacePath, "VciResolutionMismatch", "workspacePath differs from the reviewed comparison's workspace " + remoteSide.Manifest.WorkspaceId + ".");
        Require(package.Object.Object.ObjectPath == objectPath, "VciResolutionMismatch", "objectPath differs from the reviewed package object " + package.Object.Object.ObjectPath + ".");
        Require(native.ExpectedFiles.Select(f => f.RelativePath).SequenceEqual(paths),
            "VciResolutionUnsupported", "The native and remote sides of the package differ in file layout.");
        // The worker re-exports the live object and must find this reviewed native-side hash before anything is written.
        Require(native.Object.ContentSha256 is not null, "VciResolutionUnsupported",
            "The native side must carry object.contentSha256 as vci_workspace_inspect reports it for the mapped object.");
        Require(paths.Length is > 0 and <= MaxResolutionFiles, "VciInputLimit", "A resolved package holds 1..16 files.");
        var remoteFiles = PackageFiles(remoteSide.Receipt.SourceRecordRef, paths);
        var result = take switch { "native" => null, "remote" => remoteFiles, _ => PackageFiles(merged!, paths) };
        Require(remoteFiles.Values.Concat(result?.Values ?? Enumerable.Empty<byte[]>()).Sum(b => (long)b.Length) <= MaxResolutionBytes, "VciInputLimit", "Resolved package files exceed 4 MiB.");
        JsonArray Rows(Dictionary<string, byte[]> files, bool content) => new(files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Key, ["sha256"] = GitSafety.Hash(f.Value), ["bytes"] = f.Value.Length,
            ["contentBase64"] = content ? Convert.ToBase64String(f.Value) : null
        }).ToArray());
        return new JsonObject
        {
            ["workspacePath"] = workspacePath, ["objectPath"] = objectPath, ["take"] = take,
            ["direction"] = take == "native" ? "ProjectToWorkspace" : "WorkspaceToProject",
            ["plan"] = new JsonObject
            {
                ["comparisonRef"] = comparisonRef, ["reportSha256"] = report.Sha256, ["workspaceId"] = remoteSide.Manifest.WorkspaceId,
                ["packageId"] = packageId, ["state"] = row.State, ["projectObjectContentSha256"] = native.Object.ContentSha256,
                ["baselineContentSha256"] = row.BaselineContentSha256, ["projectContentSha256"] = row.ProjectContentSha256,
                ["workspaceContentSha256"] = row.WorkspaceContentSha256, ["mergedRecordRef"] = merged
            },
            ["workspaceFiles"] = Rows(remoteFiles, false),
            ["resultFiles"] = result is null ? new JsonArray() : Rows(result, true)
        };
    }

    // The exact bytes of a package's files in one retained record; every file must be present.
    private Dictionary<string, byte[]> PackageFiles(string reference, string[] paths)
    {
        var source = Source(reference);
        return paths.ToDictionary(path => path, path =>
        {
            var file = source.Files.SingleOrDefault(f => f.RelativePath == path)
                ?? throw new BridgeException("VciPackageIncomplete", "The record lacks package file " + path + ".");
            var bytes = ReadBytes(PackageStorage.Under(source.Repository, path));
            Require(bytes.LongLength == file.Bytes && GitSafety.Hash(bytes) == file.Sha256, "VciIntegrityFailure", "Retained package bytes changed.");
            return bytes;
        }, StringComparer.Ordinal);
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
        var source = Source(reference);
        var files = source.Files.Select(f =>
        {
            var bytes = ReadBytes(PackageStorage.Under(source.Repository, f.RelativePath));
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
