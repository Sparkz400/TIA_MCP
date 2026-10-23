namespace TiaMcp.Core;

/// <summary>Server policy. Package-provided values never override these limits.</summary>
public sealed record PackageIntakeLimits
{
    public long MaxInputBytes { get; init; } = 1L << 30;
    public long MaxExpandedBytes { get; init; } = 4L << 30;
    public int MaxEntries { get; init; } = 20_000;
    public int MaxNestingDepth { get; init; } = 3;
    public int MaxChunkBytes { get; init; } = 65_536;
    public int MaxPageSize { get; init; } = 200;
    public long MaxStoredBytes { get; init; } = 16L << 30;
    public int MaxUploads { get; init; } = 128;
    public int MaxPackages { get; init; } = 128;
    public int MaxWorkSeconds { get; init; } = 600;
    public int UploadLifetimeHours { get; init; } = 24;

    public void Validate()
    {
        if (MaxInputBytes < 1 || MaxInputBytes > 1L << 46 || MaxExpandedBytes < 1 || MaxExpandedBytes > 1L << 48 || MaxEntries is < 1 or > 100_000 ||
            MaxNestingDepth is < 0 or > 10 || MaxChunkBytes is < 1 or > 1_048_576 ||
            MaxPageSize is < 1 or > 200 || MaxStoredBytes < MaxInputBytes || MaxStoredBytes > 1L << 50 ||
            MaxUploads is < 1 or > 10_000 || MaxPackages is < 1 or > 10_000 ||
            MaxWorkSeconds is < 1 or > 3600 || UploadLifetimeHours is < 1 or > 720)
            throw new ArgumentException("Invalid package intake limits.");
    }
}

public sealed record ArtifactUploadStatus(int SchemaVersion, string UploadId, string FileName, long ExpectedBytes,
    string ExpectedSha256, long ReceivedBytes, long RemainingBytes, string State, string? ArtifactRef,
    DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, int MaxChunkBytes, string Host, string HostPath,
    long? AcceptedOffset = null, int? AcceptedBytes = null, string? ChunkSha256 = null, PackageIntakeLimits? Limits = null);

public sealed record PackageFile(string RelativePath, long Bytes, string Sha256, string Role);
/// <summary>Native input identity pinned to the original staged manifest, even if its path later changes.</summary>
public sealed record ResolvedPackageCandidate(string Path, string Sha256, long Bytes);
public sealed record PackageCandidate(string CandidateId, string RelativePath, string Kind, int? TiaMajorVersion,
    bool ReadyForNativeInspection, string Completeness, string[] Notes);
public sealed record PackageManifest(int SchemaVersion, string PackageId, string State, DateTimeOffset CreatedUtc,
    string Host, string SourceKind, string SourceLabel, string? SourceUrl, string SourceSha256,
    long InputBytes, long ExpandedBytes, PackageIntakeLimits Limits, PackageFile[] Files,
    PackageCandidate[] Candidates, string[] Documents, string[] Diagnostics);
public sealed record PackagePage(int SchemaVersion, string PackageId, string Section, int Offset, int Total,
    int? NextOffset, object[] Items, string ManifestPath, string Host);
public sealed record PackageStatus(int SchemaVersion, string PackageId, string State, DateTimeOffset CreatedUtc,
    string SourceSha256, long InputBytes, long ExpandedBytes, int Files, int Candidates, string[] References,
    string Retention, string ManifestPath, string Host, PackageIntakeLimits Limits);
