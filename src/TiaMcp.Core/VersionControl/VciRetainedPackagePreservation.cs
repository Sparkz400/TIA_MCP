namespace TiaMcp.Core.VersionControl;

public sealed record VciRetainedPackageReport(IReadOnlyList<VciFileEvidence> ReviewedFiles,
    IReadOnlyList<VciFileEvidence> ObservedFiles, IReadOnlyList<string> MissingFiles,
    IReadOnlyList<string> ChangedFiles, IReadOnlyList<string> UnreviewedFiles,
    bool RetainedBytesPreserved, bool NativeImportQualified = false,
    string Scope = "exactRetainedPackageBytesOnly");

/// <summary>
/// Checks a complete retained package against independent reviewed hashes. Includes workspace configuration
/// and unknown companions without interpreting or rewriting them. Does not qualify native import or languages.
/// </summary>
public static class VciRetainedPackagePreservation
{
    public static VciRetainedPackageReport Inspect(IReadOnlyList<VciFileEvidence> reviewedFiles,
        IReadOnlyList<VciFileInput> observedFiles)
    {
        if (reviewedFiles is null || reviewedFiles.Count is < 1 or > 20000 || reviewedFiles.Any(x => x is null) ||
            observedFiles is null || observedFiles.Count > 20000 || observedFiles.Any(x => x is null))
            throw new BridgeException("VciInvalidPreservationReview", "Bounded reviewed and observed package inventories are required.");
        var reviewed = reviewedFiles.ToArray();
        var observed = observedFiles.ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in reviewed)
        {
            VciGuard.Path(file.RelativePath, 255); VciGuard.Hash(file.Sha256);
            if (file.Bytes < 0 || file.Bytes > 16777216 || (total += file.Bytes) > 16777216)
                throw new BridgeException("VciLimitExceeded", "Reviewed file sizes exceed the retained package bound.");
            if (!paths.Add(file.RelativePath.Normalize(System.Text.NormalizationForm.FormC)))
                throw new BridgeException("VciPathCollision", "Reviewed package paths collide under Windows path rules.");
        }
        foreach (var path in paths)
            if (VciGuard.Ancestors(path).Any(paths.Contains))
                throw new BridgeException("VciPathCollision", "A reviewed file is also a directory ancestor.");
        if (observed.Sum(x => (long)x.Bytes.Length) > 16777216)
            throw new BridgeException("VciLimitExceeded", "Observed file sizes exceed the retained package bound.");
        observed = observed.Select(x => x with { Bytes = x.Bytes.ToArray() }).ToArray();
        var inventory = VciManifest.Capture("retained-package-review", new("reviewed-package", new('0', 64)), [], observed,
            true, new(MaxTotalBytes: 16777216));
        var expected = reviewed.ToDictionary(x => x.RelativePath, StringComparer.Ordinal);
        var actual = inventory.Files.ToDictionary(x => x.RelativePath, StringComparer.Ordinal);
        var missing = expected.Keys.Where(x => !actual.ContainsKey(x)).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Keys.Where(x => !expected.ContainsKey(x)).Order(StringComparer.Ordinal).ToArray();
        var changed = expected.Values.Where(x => actual.TryGetValue(x.RelativePath, out var a) && a != x)
            .Select(x => x.RelativePath).Order(StringComparer.Ordinal).ToArray();
        return new(Array.AsReadOnly(reviewed.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray()),
            inventory.Files, Array.AsReadOnly(missing), Array.AsReadOnly(changed), Array.AsReadOnly(extra),
            missing.Length == 0 && extra.Length == 0 && changed.Length == 0);
    }
}
