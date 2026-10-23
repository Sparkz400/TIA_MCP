using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.VersionControl;

public sealed record VciExpectedHashes(string Baseline, string Project, string Workspace);
public sealed record VciConflictRow(string PackageId, string State, bool Conflict, string ProjectChange,
    string WorkspaceChange, string? BaselineContentSha256, string? ProjectContentSha256,
    string? WorkspaceContentSha256, string? BaselineNativeStatus, string? ProjectNativeStatus,
    string? WorkspaceNativeStatus, IReadOnlyList<string> Reasons);

public sealed class VciConflictReport
{
    public IReadOnlyList<VciConflictRow> Rows { get; }
    public IReadOnlyList<string> Issues { get; }
    public string Sha256 { get; }
    private readonly JsonObject document;
    internal VciConflictReport(VciManifest baseline, VciManifest project, VciManifest workspace,
        VciExpectedHashes expected, IEnumerable<VciConflictRow> rows, IEnumerable<string> issues)
    {
        Rows = Array.AsReadOnly(rows.ToArray()); Issues = Array.AsReadOnly(issues.ToArray());
        document = new JsonObject
        {
            ["schemaVersion"] = "vci-conflict-report.v1", ["workspaceId"] = baseline.WorkspaceId,
            ["baselineSha256"] = baseline.Sha256, ["projectSha256"] = project.Sha256,
            ["workspaceSha256"] = workspace.Sha256,
            ["expectedHashes"] = new JsonObject { ["baseline"] = expected.Baseline, ["project"] = expected.Project, ["workspace"] = expected.Workspace },
            ["nativeQualified"] = false, ["canSynchronize"] = false, ["resolutionApplied"] = false,
            ["issues"] = Strings(Issues),
            ["rows"] = new JsonArray(Rows.Select(r => (JsonNode)new JsonObject
            {
                ["packageId"] = r.PackageId, ["state"] = r.State, ["conflict"] = r.Conflict,
                ["projectChange"] = r.ProjectChange, ["workspaceChange"] = r.WorkspaceChange,
                ["baselineContentSha256"] = r.BaselineContentSha256, ["projectContentSha256"] = r.ProjectContentSha256,
                ["workspaceContentSha256"] = r.WorkspaceContentSha256,
                ["baselineNativeStatus"] = r.BaselineNativeStatus, ["projectNativeStatus"] = r.ProjectNativeStatus,
                ["workspaceNativeStatus"] = r.WorkspaceNativeStatus, ["reasons"] = Strings(r.Reasons)
            }).ToArray())
        };
        Sha256 = CanonicalJson.Sha256(document);
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
    public JsonObject ToJson() => (JsonObject)document.DeepClone();
}

/// <summary>Pure three-way comparison. Equality compares complete package bytes/identity, not native green status.</summary>
public static class VciComparison
{
    public static VciConflictReport Compare(VciManifest baseline, VciManifest project, VciManifest workspace, VciExpectedHashes expected)
    {
        if (baseline is null || project is null || workspace is null || expected is null)
            throw new BridgeException("VciInvalidComparison", "Three manifests and their expected hashes are required.");
        VciGuard.Hash(expected.Baseline); VciGuard.Hash(expected.Project); VciGuard.Hash(expected.Workspace);
        if (baseline.WorkspaceId != project.WorkspaceId || baseline.WorkspaceId != workspace.WorkspaceId)
            throw new BridgeException("VciWorkspaceMismatch", "Historical observations must identify the same explicit workspace lineage.");
        var global = new List<string>();
        if (baseline.Sha256 != expected.Baseline || project.Sha256 != expected.Project || workspace.Sha256 != expected.Workspace)
            global.Add("staleManifestBinding");
        if (!baseline.EnumerationComplete || !project.EnumerationComplete || !workspace.EnumerationComplete)
            global.Add("incompleteEnumeration");
        var manifests = new[] { baseline, project, workspace };
        var maps = manifests.Select(m => m.Packages.ToDictionary(p => p.PackageId, StringComparer.Ordinal)).ToArray();
        var files = manifests.Select(m => m.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal)).ToArray();
        var windowsFiles = manifests.Select(m => m.Files.ToDictionary(f => f.RelativePath.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase)).ToArray();
        var ownership = manifests.Select(m => m.Packages.SelectMany(p => p.ExpectedFiles.Select(f =>
                (p.PackageId, Path: f.RelativePath.Normalize(NormalizationForm.FormC))))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PackageId).ToArray(), StringComparer.OrdinalIgnoreCase)).ToArray();
        var rows = new List<VciConflictRow>();
        foreach (var id in maps.SelectMany(m => m.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var packages = maps.Select(m => m.GetValueOrDefault(id)).ToArray();
            var reasons = new List<string>();
            for (var side = 0; side < 3; side++)
                if (packages[side] is { } p) reasons.AddRange(p.Issues.Select(issue => Side(side) + ":" + issue));
            // A missing mapping cannot claim an occupied unowned file path for import/export.
            var claimedPaths = packages.Where(p => p is not null).SelectMany(p => p!.ExpectedFiles)
                .Select(f => f.RelativePath.Normalize(NormalizationForm.FormC)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var side = 0; side < 3; side++)
            {
                foreach (var path in claimedPaths)
                {
                    if (!windowsFiles[side].TryGetValue(path, out var f)) continue;
                    var owners = ownership[side].GetValueOrDefault(path) ?? [];
                    if (owners.Length == 0) reasons.Add(Side(side) + ":unmanagedFileCollision:" + f.RelativePath);
                    else if (owners.Any(owner => owner != id)) reasons.Add(Side(side) + ":mappingCollision");
                }
            }
            var identityCollision = packages.Where(p => p is not null).Select(p => VciGuard.ObjectKey(p!.Object)).Distinct(StringComparer.Ordinal).Count() > 1;
            identityCollision |= packages.Where(p => p is not null).Select(p => p!.LibraryTypeGuid).Distinct(StringComparer.Ordinal).Count() > 1;
            var nativeIdentities = packages.Where(p => p is not null).Select(p => p!.NativeObjectIdentity).Distinct(StringComparer.Ordinal).ToArray();
            identityCollision |= nativeIdentities.Count(x => x is not null) > 1;
            var identityUnknown = nativeIdentities.Length > 1 && nativeIdentities.Contains(null);
            var contentKnownness = packages.Where(p => p is not null).Select(p => p!.Object.ContentSha256 is not null).Distinct().Count();
            var contentObservationUnknown = contentKnownness > 1;
            var hashes = packages.Select((p, side) => p is null ? null : Content(p, files[side])).ToArray();
            var projectChange = Change(hashes[0], hashes[1]);
            var workspaceChange = Change(hashes[0], hashes[2]);
            string state; bool conflict;
            if (global.Contains("staleManifestBinding")) { state = "stale"; conflict = true; }
            else if (identityCollision) { state = "identityCollision"; conflict = true; reasons.Add("samePackageDifferentObject"); }
            else if (reasons.Any(r => r.Contains("Collision", StringComparison.Ordinal))) { state = "ownershipConflict"; conflict = true; }
            else if (global.Contains("incompleteEnumeration") || packages.Any(p => p is { ProfileComplete: false }) || identityUnknown || contentObservationUnknown) { state = "unknown"; conflict = true; }
            else if (reasons.Any(r => r.Contains("missingCompanion:", StringComparison.Ordinal))) { state = "incompletePackage"; conflict = true; }
            else (state, conflict) = Classify(hashes[0], hashes[1], hashes[2]);
            if (state is "stale" or "unknown" or "incompletePackage" or "ownershipConflict" or "identityCollision")
            { projectChange = "unknown"; workspaceChange = "unknown"; }
            reasons.AddRange(global);
            if (identityUnknown) reasons.Add("nativeIdentityObservationIncomplete");
            if (contentObservationUnknown) reasons.Add("objectContentObservationIncomplete");
            rows.Add(new(id, state, conflict, projectChange, workspaceChange, hashes[0], hashes[1], hashes[2],
                packages[0]?.NativeStatus, packages[1]?.NativeStatus, packages[2]?.NativeStatus,
                Array.AsReadOnly(reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));
        }
        return new(baseline, project, workspace, expected, rows, global);
    }

    private static string Side(int side) => side switch { 0 => "baseline", 1 => "project", _ => "workspace" };
    private static string Change(string? before, string? after) => before == after ? "unchanged" : before is null ? "added" : after is null ? "deleted" : "modified";
    private static (string, bool) Classify(string? b, string? p, string? w)
    {
        if (p == b && w == b) return ("unchanged", false);
        if (p == w) return (p is null ? "deletedBoth" : b is null ? "addedBothIdentically" : "bothChangedIdentically", false);
        if (p == b) return (w is null ? "deletedWorkspaceOnly" : b is null ? "addedWorkspaceOnly" : "workspaceOnly", false);
        if (w == b) return (p is null ? "deletedProjectOnly" : b is null ? "addedProjectOnly" : "projectOnly", false);
        if (b is null) return ("divergentAddition", true);
        if (p is null || w is null) return ("deleteVsChange", true);
        return ("divergentChange", true);
    }

    private static string Content(VciPackageEvidence package, Dictionary<string, VciFileEvidence> files)
    {
        // Observation IDs, source observation hash and opaque native status are deliberately separate.
        var json = new JsonObject
        {
            ["objectIdentity"] = VciGuard.ObjectKey(package.Object), ["nativeObjectIdentity"] = package.NativeObjectIdentity, ["family"] = package.Family,
            ["objectContentSha256"] = package.Object.ContentSha256,
            ["nativeFormat"] = package.NativeFormat, ["libraryTypeGuid"] = package.LibraryTypeGuid,
            ["libraryVersionGuid"] = package.LibraryVersionGuid,
            ["files"] = new JsonArray(package.ExpectedFiles.Select(f => (JsonNode)new JsonObject
            {
                ["relativePath"] = f.RelativePath, ["role"] = f.Role,
                ["sha256"] = files.GetValueOrDefault(f.RelativePath)?.Sha256,
                ["bytes"] = files.GetValueOrDefault(f.RelativePath)?.Bytes
            }).ToArray())
        };
        return CanonicalJson.Sha256(json);
    }
}
