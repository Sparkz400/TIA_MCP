namespace TiaMcp.Core.VersionControl;

public sealed record VciGitFetch(string? ParentRevisionRef, string Remote, string Branch, string Commit, string Tree, string? PreviousCommit,
    string SnapshotSha256, GitFileEvidence[] Files, string ExecutableSha256, string GitVersion, string CallbackSha256, VciStoredArtifact[] Artifacts,
    bool NativeQualified = false);
public sealed record VciGitPush(string RevisionRef, string Remote, string Branch, string Commit, string? ExpectedRemoteCommit,
    string ExecutableSha256, string GitVersion, string CallbackSha256, VciStoredArtifact[] Artifacts, bool NativeQualified = false);

/// <summary>
/// Delivery to and from the one configured remote. A fetch is a new record whose repository fast-forwards a copied parent
/// (or a new repository) to the remote commit; a push publishes an exact retained commit with compare-and-swap. Neither
/// replays after interruption: a dispatched operation without its receipt stays outcomeUnknown.
/// </summary>
public sealed partial class VciWorkspaceStore
{
    public async Task<object> PushAsync(string revisionRef, string branchName, string? expectedRemoteCommit = null, CancellationToken token = default)
    {
        using var gate = Lock();
        var configured = RequireRemote(); GitSafety.Branch(branchName);
        if (expectedRemoteCommit is not null) GitSafety.ObjectId(expectedRemoteCommit);
        var source = Committed(revisionRef); var entry = NewEntry();
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "push", revisionRef, remote = configured.Location, branchName, expectedRemoteCommit });
        var journal = new Journal(Path.Combine(entry.Path, "push-journal")); var runner = Runner(journal);
        var before = await runner.InspectAsync(source.Repository, token);
        Require(before.Matches(source.SnapshotSha256), "PreviewStale", "Revision repository changed.");
        var binding = GitOperationBinding.Push(before, runner.ControlDirectory, runner.ExecutableSha256, configured.Location, branchName, source.Commit, expectedRemoteCommit);
        journal.Bind(binding);
        GitBranch result;
        try { result = await runner.PushAsync(source.Repository, before.Sha256, configured, branchName, source.Commit, expectedRemoteCommit, token); }
        catch (GitOutcomeUnknownException error) { throw new BridgeException("VciOutcomeUnknown", "The push may have reached the remote. Inspect the remote branch; do not repeat the push blindly.", error); }
        var callback = Journal.Read(Path.Combine(entry.Path, "push-journal"), "push", source.Repository, b => b.Sha256 == binding.Sha256)!;
        WriteNew(Path.Combine(entry.Path, "push.json"), new VciGitPush(revisionRef, configured.Location, result.Reference, result.Commit, expectedRemoteCommit,
            runner.ExecutableSha256, await runner.GetVersionAsync(token), callback.Sha256, []));
        return Reopen(Reference("push", entry.Id, Path.Combine(entry.Path, "push.json")));
    }

    public async Task<object> FetchAsync(string branchName, string? parentRevisionRef = null, string? expectedRemoteCommit = null, CancellationToken token = default)
    {
        using var gate = Lock();
        var configured = RequireRemote(); GitSafety.Branch(branchName);
        if (expectedRemoteCommit is not null) GitSafety.ObjectId(expectedRemoteCommit);
        var entry = NewEntry();
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "fetch", branchName, parentRevisionRef, remote = configured.Location, expectedRemoteCommit });
        string repository = Path.Combine(entry.Path, "repository");
        if (parentRevisionRef is null)
        {
            var init = new Journal(Path.Combine(entry.Path, "init-journal")); var initializer = Runner(init);
            init.Bind(GitOperationBinding.Initialize(repository, initializer.ControlDirectory, initializer.ExecutableSha256, branchName));
            await initializer.InitializeAsync(repository, branchName, token);
        }
        else await CopyRevision(parentRevisionRef, repository, token);
        var journal = new Journal(Path.Combine(entry.Path, "fetch-journal")); var runner = Runner(journal);
        var before = await runner.InspectAsync(repository, token);
        string remoteCommit = await runner.RemoteHeadAsync(repository, configured, branchName, token)
            ?? throw new BridgeException("GitReferenceMissing", "The remote branch does not exist.");
        Require(expectedRemoteCommit is null || expectedRemoteCommit == remoteCommit, "PreviewStale", "The remote branch differs from expectedRemoteCommit; nothing was fetched.");
        var binding = GitOperationBinding.Fetch(before, runner.ControlDirectory, runner.ExecutableSha256, configured.Location, branchName, remoteCommit);
        journal.Bind(binding);
        GitBranch result;
        try { result = await runner.FetchAsync(repository, before.Sha256, configured, branchName, remoteCommit, token); }
        catch (GitOutcomeUnknownException error) { throw new BridgeException("VciOutcomeUnknown", "The fetch outcome is retained as unknown. Inspect this record; fetch again into a new record.", error); }
        var after = await runner.InspectAsync(repository, token);
        WriteNew(Path.Combine(entry.Path, "files.json"), after.Files);
        var callback = Journal.Read(Path.Combine(entry.Path, "fetch-journal"), "fetch", repository, b => b.Sha256 == binding.Sha256)!;
        WriteNew(Path.Combine(entry.Path, "fetch.json"), new VciGitFetch(parentRevisionRef, configured.Location, result.Reference, result.Commit, after.HeadTree!,
            before.HeadCommit, after.Sha256, after.Files.ToArray(), runner.ExecutableSha256, await runner.GetVersionAsync(token), callback.Sha256,
            [Artifact(entry.Path, "files.json")]));
        return Reopen(Reference("fetch", entry.Id, Path.Combine(entry.Path, "fetch.json")));
    }

    private GitRemote RequireRemote() => remote ?? throw new BridgeException("GitRemoteNotConfigured", "Start the Host with --git-remote naming one bare repository (or, with --git-allow-network-remote, an https URL).");

    private (VciGitFetch Value, string Directory, string Repository) Fetched(string reference)
    {
        var entry = Read<VciGitFetch>(reference, "fetch");
        Require(!entry.Value.NativeQualified && entry.Value.Files.Length <= MaxFiles, "VciIntegrityFailure", "Fetch receipt bounds changed.");
        VerifyArtifacts(entry.Directory, entry.Value.Artifacts, "files.json");
        string repository = Path.Combine(entry.Directory, "repository");
        VerifyWorkingFiles(repository, entry.Value.Files);
        var callback = Journal.Read(Path.Combine(entry.Directory, "fetch-journal"), "fetch", repository, b =>
            b.Branch == entry.Value.Branch && b.TargetCommit == entry.Value.Commit && b.Destination == entry.Value.Remote && b.ExecutableSha256 == entry.Value.ExecutableSha256);
        Require(callback is not null && callback.Phase == "verified" && callback.Sha256 == entry.Value.CallbackSha256 &&
            callback.CandidateCommit == entry.Value.Commit && callback.CandidateTree == entry.Value.Tree,
            "VciIntegrityFailure", "Fetched revision differs from its verified durable callback.");
        return (entry.Value, entry.Directory, repository);
    }

    private VciGitPush Pushed(string reference)
    {
        var entry = Read<VciGitPush>(reference, "push"); var source = Committed(entry.Value.RevisionRef);
        Require(!entry.Value.NativeQualified && entry.Value.Commit == source.Commit && entry.Value.Artifacts.Length == 0, "VciIntegrityFailure", "Push receipt does not bind its revision.");
        var callback = Journal.Read(Path.Combine(entry.Directory, "push-journal"), "push", source.Repository, b =>
            b.Branch == entry.Value.Branch && b.TargetCommit == entry.Value.Commit && b.ExpectedReferenceCommit == entry.Value.ExpectedRemoteCommit &&
            b.Destination == entry.Value.Remote && b.ExecutableSha256 == entry.Value.ExecutableSha256);
        Require(callback is not null && callback.Phase == "verified" && callback.Sha256 == entry.Value.CallbackSha256 && callback.CandidateCommit == entry.Value.Commit,
            "VciIntegrityFailure", "Push receipt differs from its verified durable callback.");
        return entry.Value;
    }

    private object ReopenRemote(string reference, string kind)
    {
        if (kind == "push")
        {
            var push = Pushed(reference);
            return new { recordRef = reference, kind, state = "complete", nativeQualified = false, push.RevisionRef, push.Remote, push.Branch, push.Commit,
                push.ExpectedRemoteCommit, push.ExecutableSha256, push.GitVersion, receiptName = "receipt.json" };
        }
        var fetch = Fetched(reference).Value;
        return new { recordRef = reference, kind, state = "complete", nativeQualified = false, fetch.ParentRevisionRef, fetch.Remote, fetch.Branch,
            fetch.Commit, fetch.Tree, fetch.PreviousCommit, fileCount = fetch.Files.Length, artifacts = fetch.Artifacts, fetch.ExecutableSha256, fetch.GitVersion,
            receiptName = "receipt.json" };
    }
}
