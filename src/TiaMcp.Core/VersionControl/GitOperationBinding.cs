using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.VersionControl;

/// <summary>Exact process intent retained before admission. It does not establish native qualification.</summary>
public sealed record GitOperationBinding(string Operation, string RepositoryRoot, string ControlDirectory,
    string ExecutableSha256, string? PlanSha256 = null, string? SnapshotSha256 = null,
    string? Branch = null, string? TargetCommit = null, string? ExpectedReferenceCommit = null,
    string? Destination = null)
{
    [JsonIgnore] public string Sha256 => GitWorkspaceSnapshot.Digest(this);

    public static GitOperationBinding Initialize(string root, string control, string executableSha256, string initialBranch) =>
        new("init", Root(root), Root(control), Hash(executableSha256), Branch: GitSafety.Branch(initialBranch));

    public static GitOperationBinding Commit(GitCommitPlan plan, string control, string executableSha256) =>
        new("commit", Root(plan.Before.RepositoryRoot), Root(control), Hash(executableSha256),
            plan.Sha256, plan.Before.Sha256, plan.Before.HeadReference, ExpectedReferenceCommit: plan.Before.HeadCommit);

    public static GitOperationBinding UpdateBranch(GitWorkspaceSnapshot before, string control, string executableSha256,
        string branch, string targetCommit, string? expectedReferenceCommit) =>
        new("branch", Root(before.RepositoryRoot), Root(control), Hash(executableSha256), SnapshotSha256: before.Sha256,
            Branch: GitSafety.Branch(branch), TargetCommit: GitSafety.ObjectId(targetCommit),
            ExpectedReferenceCommit: expectedReferenceCommit is null ? null : GitSafety.ObjectId(expectedReferenceCommit));

    public static GitOperationBinding Materialize(GitWorkspaceSnapshot before, string control, string executableSha256,
        string exactCommit, string destination) =>
        new("materialize", Root(before.RepositoryRoot), Root(control), Hash(executableSha256), SnapshotSha256: before.Sha256,
            TargetCommit: GitSafety.ObjectId(exactCommit), Destination: Root(destination));

    // Offline private-candidate operation; deliberately not admitted by the native BuildPlan whitelist.
    public static GitOperationBinding Fork(GitWorkspaceSnapshot before, string control, string executableSha256, string branch) =>
        new("fork", Root(before.RepositoryRoot), Root(control), Hash(executableSha256), SnapshotSha256: before.Sha256,
            Branch: GitSafety.Branch(branch), TargetCommit: before.HeadCommit);

    /// <summary>The caller retains canonical binding bytes, project/package provenance and real resource checkpoints.</summary>
    public EngineeringOperationPlan BuildPlan(NativeTargetObservation target, EngineeringArtifactReference bindingArtifact,
        IReadOnlyList<EngineeringArtifactReference> inputs, IReadOnlyList<string> requiredProbes,
        IReadOnlyList<string> additionalGates)
    {
        if (bindingArtifact.Sha256 != Sha256 || Operation is not ("init" or "commit" or "branch" or "materialize"))
            throw new BridgeException("GitJournalBindingMismatch", "Retain the exact canonical supported Git operation binding.");
        NativeTargetGuard.EnsureFresh(target, target, requiredProbes);
        var resources = new List<EngineeringResourceEffect>
        {
            new(EngineeringResourceKey.ForPath("project", target.ProjectFilePath), "project", "linkedEngineeringProvenance", "projectArchiveToNewLocation"),
            new(EngineeringResourceKey.ForPath("workspace", RepositoryRoot), "workspace", "git." + Operation, "immutableWorkspaceVersion"),
            new(EngineeringResourceKey.ForPath("workspace", ControlDirectory), "workspace", "git.privateIndexAndProcessControl", "immutableWorkspaceVersion")
        };
        if (Destination is not null) resources.Add(new(EngineeringResourceKey.ForPath("workspace", Destination),
            "workspace", "git.materializedRevision", "immutableWorkspaceVersion"));
        for (int i = 0; i < resources.Count; i++)
            for (int j = i + 1; j < resources.Count; j++)
                if (EngineeringResourceKey.Overlaps(resources[i].ResourceKey, resources[j].ResourceKey))
                    throw new BridgeException("GitUnsafePath", "Project, repository, control storage and destination resources must be disjoint.");
        return new("vci.git." + Operation, target, resources.ToArray(), [bindingArtifact, .. inputs],
            requiredProbes.ToArray(), new[] { "git.callbacks", "git.result" }.Concat(additionalGates).Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string Root(string path) => GitSafety.PathRoot(path, false);
    private static string Hash(string value) { VciGuard.Hash(value); return value; }
}
