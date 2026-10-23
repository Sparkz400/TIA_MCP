using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.VersionControl;

/// <summary>
/// Restricted local Git profile. No shell, remote, checkout, merge, arbitrary config or user supplied command route.
/// The caller owns resource admission; journal implementations must make pre-dispatch records durable.
/// </summary>
public sealed class GitWorkspaceRunner
{
    private readonly GitProcess git;
    private readonly string control;
    private readonly GitRunnerLimits limits;
    private readonly IGitOperationJournal journal;
    public string ExecutableSha256 => git.ExecutableSha256;
    public string ControlDirectory => control;
    public GitWorkspaceRunner(string gitExecutable, string newControlDirectory, IGitOperationJournal journal, GitRunnerLimits? limits = null)
    {
        this.limits = limits ?? new(); this.limits.Validate();
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        control = GitSafety.PathRoot(newControlDirectory, false);
        GitSafety.RequireProcessDirectory(control);
        if (Directory.Exists(control) || File.Exists(control)) throw new BridgeException("GitDestinationExists", "Control storage must be a new owned directory.");
        git = new GitProcess(gitExecutable, control, this.limits);
        Directory.CreateDirectory(control);
        Directory.CreateDirectory(Path.Combine(control, "empty"));
        File.WriteAllBytes(Path.Combine(control, "empty-attributes"), []);
        File.WriteAllBytes(Path.Combine(control, "empty-excludes"), []);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default) =>
        (await git.RunAsync(control, ["--version"], cancellationToken)).Text.Trim();

    public async Task<GitWorkspaceSnapshot> InitializeAsync(string newRepositoryDirectory, string initialBranch,
        CancellationToken cancellationToken = default)
    {
        var root = NewDestination(newRepositoryDirectory);
        GitSafety.RequireRepositoryDirectory(root);
        GitSafety.Branch(initialBranch);
        var id = Guid.NewGuid().ToString("N");
        var binding = GitOperationBinding.Initialize(root, control, ExecutableSha256, initialBranch).Sha256;
        await Record(id, "init", root, "dispatchPrepared", cancellationToken, binding: binding);
        try
        {
            Directory.CreateDirectory(root);
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new BridgeException("GitDestinationExists", "Destination changed before initialization.");
            await git.RunAsync(root, ["init", "--object-format=sha1", "--initial-branch=" + initialBranch,
                "--template=" + Path.Combine(control, "empty"), "."], cancellationToken);
            var snapshot = await InspectAsync(root, cancellationToken);
            await Record(id, "init", root, "verified", CancellationToken.None, binding: binding);
            return snapshot;
        }
        catch (Exception ex) { await Unknown(id, "init", root, null, null, null, ex, binding); throw new GitOutcomeUnknownException(id, null, ex); }
    }

    public Task<GitWorkspaceSnapshot> InspectAsync(string repositoryDirectory, CancellationToken cancellationToken = default) =>
        Inspect(repositoryDirectory, cancellationToken, false);

    private async Task<GitWorkspaceSnapshot> Inspect(string repositoryDirectory, CancellationToken token, bool ownsIndexLock)
    {
        token.ThrowIfCancellationRequested();
        var root = Repository(repositoryDirectory, ownsIndexLock);
        var beforeHead = File.ReadAllText(Path.Combine(root, ".git", "HEAD"), GitSafety.Utf8).Trim();
        if (!beforeHead.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            throw new BridgeException("GitUnsupportedRepository", "A normal repository on a named local branch is required.");
        var reference = GitSafety.Branch(beforeHead["ref: refs/heads/".Length..]);
        var config = GitSafety.HashFile(Path.Combine(root, ".git", "config"));
        var index = IndexHash(root);
        var commitRead = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], token, allowFailure: true);
        string? commit = null;
        string? tree = null;
        if (commitRead.ExitCode == 0)
        {
            commit = GitSafety.ObjectId(commitRead.Text.Trim());
            tree = GitSafety.ObjectId((await git.RunAsync(root, ["rev-parse", "--verify", commit + "^{tree}"], token)).Text.Trim());
        }
        else
        {
            var exists = await git.RunAsync(root, ["show-ref", "--verify", "--quiet", reference], token, allowFailure: true);
            if (commitRead.ExitCode != 1 || exists.ExitCode != 1)
                throw new BridgeException("GitUnsupportedRepository", "HEAD is invalid or is not a commit.");
        }
        await ReadIndex(root, token); // Reject conflicts, gitlinks and symbolic links before reporting an admissible snapshot.
        var files = Inventory(root, token);
        var status = (await git.RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none"], token)).Text;
        if (beforeHead != File.ReadAllText(Path.Combine(root, ".git", "HEAD"), GitSafety.Utf8).Trim() ||
            config != GitSafety.HashFile(Path.Combine(root, ".git", "config")) || index != IndexHash(root))
            throw new BridgeException("PreviewStale", "Repository metadata changed during capture.");
        var afterRef = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", reference], token, allowFailure: true);
        if ((commit is null && afterRef.ExitCode != 1) || (commit is not null && (afterRef.ExitCode != 0 || afterRef.Text.Trim() != commit)))
            throw new BridgeException("PreviewStale", "The branch moved during capture.");
        return new(root, reference, commit, tree, index, config, status, files);
    }

    public async Task<string> DiffAsync(string repositoryDirectory, string expectedSnapshotSha256, CancellationToken cancellationToken = default)
    {
        var snapshot = await Fresh(repositoryDirectory, expectedSnapshotSha256, cancellationToken);
        if (snapshot.HeadCommit is null) return "";
        var result = (await git.RunAsync(snapshot.RepositoryRoot, ["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--binary", snapshot.HeadCommit, "--"], cancellationToken)).Text;
        await Fresh(snapshot.RepositoryRoot, expectedSnapshotSha256, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(string repositoryDirectory, CancellationToken cancellationToken = default)
    {
        var snapshot = await InspectAsync(repositoryDirectory, cancellationToken);
        var output = (await git.RunAsync(snapshot.RepositoryRoot, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(objecttype)", "refs/heads/"], cancellationToken)).Text;
        var branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row =>
        {
            var fields = row.TrimEnd('\r').Split('\0');
            if (fields.Length != 3 || fields[2] != "commit") throw new BridgeException("GitInvalidOutput", "Branch must name a commit object.");
            return new GitBranch(fields[0], GitSafety.ObjectId(fields[1]));
        }).ToArray();
        if (branches.Length > limits.MaxFiles) throw new BridgeException("GitLimitExceeded", "Branch listing exceeded the configured bound.");
        return Array.AsReadOnly(branches);
    }

    /// <summary>Create and select a new branch at the exact clean HEAD of a private candidate. Never resets existing refs.</summary>
    public async Task<GitBranch> ForkAsync(string repositoryDirectory, string expectedSnapshotSha256, string branchName,
        CancellationToken cancellationToken = default)
    {
        string reference = GitSafety.Branch(branchName), root = Repository(repositoryDirectory, false);
        using var lease = Acquire(root);
        var before = await Fresh(root, expectedSnapshotSha256, cancellationToken);
        await RequireCleanIndex(before, cancellationToken);
        if (before.HeadCommit is null || before.Status.Length != 0)
            throw new BridgeException("GitDirtyRepository", "Fork requires a committed clean private candidate.");
        var branches = await BranchesAsync(root, cancellationToken);
        if (branches.Any(b => b.Reference.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            b.Reference.StartsWith(reference + "/", StringComparison.OrdinalIgnoreCase) || reference.StartsWith(b.Reference + "/", StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("GitReferenceConflict", "New branch collides with an existing ref; no branch is reset.");
        string id = Guid.NewGuid().ToString("N"), binding = GitOperationBinding.Fork(before, control, ExecutableSha256, branchName).Sha256;
        await Record(id, "fork", root, "dispatchPrepared", cancellationToken, before.HeadCommit, before.Sha256,
            before.HeadCommit, resource: reference, snapshot: before, binding: binding);
        try
        {
            await Fresh(root, expectedSnapshotSha256, cancellationToken);
            // The start commit is unchanged; checkout must preserve every raw file and index entry.
            await git.RunAsync(root, ["checkout", "--no-track", "-b", branchName, before.HeadCommit], cancellationToken);
            var after = await InspectAsync(root, CancellationToken.None);
            await RequireCleanIndex(after, CancellationToken.None);
            var expected = branches.Append(new GitBranch(reference, before.HeadCommit)).OrderBy(b => b.Reference, StringComparer.Ordinal);
            if (after.HeadReference != reference || after.HeadCommit != before.HeadCommit || after.HeadTree != before.HeadTree ||
                after.Status.Length != 0 || after.ConfigurationSha256 != before.ConfigurationSha256 || !after.Files.SequenceEqual(before.Files) ||
                !(await BranchesAsync(root, CancellationToken.None)).OrderBy(b => b.Reference, StringComparer.Ordinal).SequenceEqual(expected))
                throw new BridgeException("GitReadbackMismatch", "Fork changed source bytes, configuration or an unrelated branch.");
            await Record(id, "fork", root, "verified", CancellationToken.None, before.HeadCommit, before.Sha256,
                before.HeadCommit, resource: reference, snapshot: before, binding: binding);
            return new(reference, before.HeadCommit);
        }
        catch (Exception ex)
        { await Unknown(id, "fork", root, before.HeadCommit, before.HeadCommit, before.Sha256, ex, binding); throw new GitOutcomeUnknownException(id, before.HeadCommit, ex); }
    }

    public async Task<string> HistoryAsync(string repositoryDirectory, int maximumCommits = 100, CancellationToken cancellationToken = default)
    {
        if (maximumCommits is < 1 or > 1000) throw new BridgeException("GitInvalidLimits", "History is bounded to 1–1000 commits.");
        var snapshot = await InspectAsync(repositoryDirectory, cancellationToken);
        if (snapshot.HeadCommit is null) return "";
        return (await git.RunAsync(snapshot.RepositoryRoot, ["log", "--no-show-signature", "--max-count=" + maximumCommits,
            "--format=%H%x00%T%x00%P%x00%an%x00%ae%x00%s%x00", snapshot.HeadCommit, "--"], cancellationToken)).Text;
    }

    public async Task<GitCommitPlan> PrepareCommitAsync(string repositoryDirectory, string expectedSnapshotSha256,
        IEnumerable<GitFileChange> changes, GitAuthor author, string message, CancellationToken cancellationToken = default)
    {
        ValidateAuthor(author);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 16384 || message.Contains('\0'))
            throw new BridgeException("GitInvalidCommit", "A nonempty commit message of at most 16384 characters is required.");
        var selected = changes.Take(limits.MaxFiles + 1).ToArray();
        if (selected.Length is 0 || selected.Length > limits.MaxFiles) throw new BridgeException("GitLimitExceeded", "Select an explicit bounded file set.");
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in selected)
        {
            GitSafety.RelativePath(change.RelativePath);
            if (!destinations.Add(change.RelativePath.Normalize(NormalizationForm.FormC))) throw new BridgeException("GitPathCollision", "Selected paths collide.");
        }
        var before = await Fresh(repositoryDirectory, expectedSnapshotSha256, cancellationToken);
        await RequireCleanIndex(before, cancellationToken);
        var entries = await ReadIndex(before.RepositoryRoot, cancellationToken);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var differs = false;
        foreach (var change in selected)
        {
            if (change.Delete)
            {
                if (!entries.ContainsKey(change.RelativePath) || before.Files.Any(x => x.RelativePath == change.RelativePath))
                    throw new BridgeException("GitInvalidCommit", "A reviewed deletion must be tracked and absent from the working directory.");
                differs = true;
            }
            else
            {
                var evidence = before.Files.SingleOrDefault(x => x.RelativePath == change.RelativePath)
                    ?? throw new BridgeException("GitInvalidCommit", "A selected file is missing from the exact inventory.");
                var bytes = ReadFile(Path.Combine(before.RepositoryRoot, change.RelativePath), cancellationToken);
                if (GitSafety.Hash(bytes) != evidence.Sha256) throw new BridgeException("PreviewStale", "Selected bytes changed during preparation.");
                payloads.Add(change.RelativePath, bytes);
                var oid = GitSafety.ObjectId((await git.RunAsync(before.RepositoryRoot, ["hash-object", "--no-filters", "--stdin"], cancellationToken, bytes)).Text.Trim());
                if (!entries.TryGetValue(change.RelativePath, out var prior) || prior.ObjectId != oid) differs = true;
            }
        }
        if (!differs) throw new BridgeException("GitNoChanges", "The selected raw files match the current tree.");
        await Fresh(before.RepositoryRoot, before.Sha256, cancellationToken);
        return new(before, selected.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray(), author, message, payloads);
    }

    public async Task<GitCommitResult> CommitAsync(GitCommitPlan plan, string reviewedPlanSha256, CancellationToken cancellationToken = default)
    {
        VciGuard.Hash(reviewedPlanSha256);
        if (plan.Sha256 != reviewedPlanSha256) throw new BridgeException("PreviewStale", "Commit plan differs from the reviewed binding.");
        var root = Repository(plan.Before.RepositoryRoot, false);
        using var lease = Acquire(root);
        await Fresh(root, plan.Before.Sha256, cancellationToken);
        await RequireCleanIndex(plan.Before, cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var privateIndex = Path.Combine(control, id + ".index");
        var binding = GitOperationBinding.Commit(plan, control, ExecutableSha256).Sha256;
        var indexLockPath = Path.Combine(root, ".git", "index.lock");
        FileStream? indexLock;
        try { indexLock = new FileStream(indexLockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new BridgeException("GitRepositoryBusy", "The Git index is already reserved.", ex); }
        string? commit = null;
        string? tree = null;
        var dispatched = false;
        var ownsIndexLock = true;
        try
        {
            await Record(id, "commit", root, "dispatchPrepared", cancellationToken, plan.Before.HeadCommit, plan.Sha256, resource: privateIndex, snapshot: plan.Before, binding: binding);
            dispatched = true;
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = privateIndex };
            var entries = await ReadIndex(root, cancellationToken);
            await git.RunAsync(root, plan.Before.HeadCommit is null ? ["read-tree", "--empty"] : ["read-tree", plan.Before.HeadCommit], cancellationToken, environment: environment);
            using var indexInput = new MemoryStream();
            foreach (var change in plan.Changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mode = entries.TryGetValue(change.RelativePath, out var prior) ? prior.Mode : "100644";
                var oid = change.Delete ? new string('0', 40) : GitSafety.ObjectId((await git.RunAsync(root,
                    ["hash-object", "--no-filters", "-w", "--stdin"], cancellationToken, plan.Payloads[change.RelativePath])).Text.Trim());
                indexInput.Write(GitSafety.Utf8.GetBytes((change.Delete ? "0" : mode) + " " + oid + "\t" + change.RelativePath + "\0"));
            }
            await git.RunAsync(root, ["update-index", "-z", "--index-info"], cancellationToken, indexInput.ToArray(), environment);
            tree = GitSafety.ObjectId((await git.RunAsync(root, ["write-tree"], cancellationToken, environment: environment)).Text.Trim());
            var date = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            environment["GIT_AUTHOR_NAME"] = environment["GIT_COMMITTER_NAME"] = plan.Author.Name;
            environment["GIT_AUTHOR_EMAIL"] = environment["GIT_COMMITTER_EMAIL"] = plan.Author.Email;
            environment["GIT_AUTHOR_DATE"] = environment["GIT_COMMITTER_DATE"] = date;
            var args = new List<string> { "commit-tree", tree };
            if (plan.Before.HeadCommit is not null) { args.Add("-p"); args.Add(plan.Before.HeadCommit); }
            commit = GitSafety.ObjectId((await git.RunAsync(root, args, cancellationToken, GitSafety.Utf8.GetBytes(plan.Message + "\n"), environment)).Text.Trim());
            await Record(id, "commit", root, "candidateRecorded", cancellationToken, plan.Before.HeadCommit, plan.Sha256, commit, tree, privateIndex, plan.Before, binding);
            var current = await Inspect(root, cancellationToken, true);
            if (current.Sha256 != plan.Before.Sha256) throw new BridgeException("PreviewStale", "Repository changed before ref publication.");
            await Record(id, "commit", root, "publicationPrepared", cancellationToken, plan.Before.HeadCommit, plan.Sha256, commit, tree, privateIndex, plan.Before, binding);
            await git.RunAsync(root, ["update-ref", plan.Before.HeadReference, commit, plan.Before.HeadCommit ?? new string('0', 40)], cancellationToken);
            await indexLock.WriteAsync(await File.ReadAllBytesAsync(privateIndex, CancellationToken.None), CancellationToken.None);
            indexLock.Flush(true); indexLock.Dispose(); indexLock = null;
            File.Move(indexLockPath, Path.Combine(root, ".git", "index"), true);
            ownsIndexLock = false;
            var verified = await InspectAsync(root, CancellationToken.None);
            if (verified.HeadCommit != commit || verified.HeadTree != tree) throw new BridgeException("GitReadbackMismatch", "Published commit readback did not match.");
            await RequireCleanIndex(verified, CancellationToken.None);
            var version = await GetVersionAsync(CancellationToken.None);
            await Record(id, "commit", root, "verified", CancellationToken.None, plan.Before.HeadCommit, plan.Sha256, commit, tree, privateIndex, plan.Before, binding);
            return new(id, root, commit, tree, plan.Before.HeadCommit, plan.Sha256, git.ExecutableSha256, version);
        }
        catch (Exception ex)
        {
            if (!dispatched) throw;
            await Unknown(id, "commit", root, plan.Before.HeadCommit, commit, plan.Sha256, ex, binding);
            throw new GitOutcomeUnknownException(id, commit, ex);
        }
        finally
        {
            indexLock?.Dispose();
            if (ownsIndexLock && File.Exists(indexLockPath)) File.Delete(indexLockPath); // Only the lock created by this invocation.
        }
    }

    public async Task<GitBranch> UpdateBranchAsync(string repositoryDirectory, string expectedSnapshotSha256,
        string branchName, string targetCommit, string? expectedOldCommit = null, CancellationToken cancellationToken = default)
    {
        var reference = GitSafety.Branch(branchName); GitSafety.ObjectId(targetCommit);
        if (expectedOldCommit is not null) GitSafety.ObjectId(expectedOldCommit);
        var root = Repository(repositoryDirectory, false);
        using var lease = Acquire(root);
        var before = await Fresh(root, expectedSnapshotSha256, cancellationToken);
        await RequireCleanIndex(before, cancellationToken);
        if (reference == before.HeadReference) throw new BridgeException("GitCurrentBranchRefused", "Use reviewed commit publication for the checked-out branch.");
        await ExactCommit(root, targetCommit, cancellationToken);
        var current = await git.RunAsync(root, ["show-ref", "--verify", "--hash", reference], cancellationToken, allowFailure: true);
        if ((expectedOldCommit is null && current.ExitCode == 0) ||
            (expectedOldCommit is not null && (current.ExitCode != 0 || current.Text.Trim() != expectedOldCommit)))
            throw new BridgeException("GitReferenceConflict", "The branch does not match its expected prior reference.");
        var id = Guid.NewGuid().ToString("N");
        var binding = GitOperationBinding.UpdateBranch(before, control, ExecutableSha256, branchName, targetCommit, expectedOldCommit).Sha256;
        await Record(id, "branch", root, "dispatchPrepared", cancellationToken, before.HeadCommit, before.Sha256, targetCommit, resource: reference, snapshot: before, binding: binding);
        try
        {
            await git.RunAsync(root, ["update-ref", reference, targetCommit, expectedOldCommit ?? new string('0', 40)], cancellationToken);
            var after = await git.RunAsync(root, ["show-ref", "--verify", "--hash", reference], CancellationToken.None);
            if (after.Text.Trim() != targetCommit) throw new BridgeException("GitReadbackMismatch", "Branch changed before readback.");
            await Record(id, "branch", root, "verified", CancellationToken.None, before.HeadCommit, before.Sha256, targetCommit, resource: reference, snapshot: before, binding: binding);
            return new(reference, targetCommit);
        }
        catch (Exception ex) { await Unknown(id, "branch", root, before.HeadCommit, targetCommit, before.Sha256, ex, binding); throw new GitOutcomeUnknownException(id, targetCommit, ex); }
    }

    public async Task<GitRevisionResult> MaterializeAsync(string repositoryDirectory, string exactCommit,
        string newDestinationDirectory, CancellationToken cancellationToken = default)
    {
        GitSafety.ObjectId(exactCommit);
        var before = await InspectAsync(repositoryDirectory, cancellationToken);
        await ExactCommit(before.RepositoryRoot, exactCommit, cancellationToken);
        var destination = NewDestination(newDestinationDirectory);
        if (Contained(destination, before.RepositoryRoot) || Contained(before.RepositoryRoot, destination))
            throw new BridgeException("GitUnsafePath", "Revision destination must be separate from the source repository.");
        var output = (await git.RunAsync(before.RepositoryRoot, ["ls-tree", "-r", "-z", "-l", exactCommit], cancellationToken)).Text;
        var entries = new List<(string Path, string Oid, long Bytes, string Mode)>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var row in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = row.IndexOf('\t');
            if (tab < 0) throw new BridgeException("GitInvalidOutput", "Invalid tree output.");
            var fields = row[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = row[(tab + 1)..]; GitSafety.RelativePath(path);
            if (fields.Length != 4 || fields[0] is not ("100644" or "100755") || fields[1] != "blob" ||
                !long.TryParse(fields[3], out var size) || size < 0)
                throw new BridgeException("GitUnsupportedRepository", "Only regular raw file blobs can be materialized.");
            if (!destinations.Add(path.Normalize(NormalizationForm.FormC))) throw new BridgeException("GitPathCollision", "Revision paths collide on the destination.");
            entries.Add((path, GitSafety.ObjectId(fields[2]), size, fields[0])); total += size;
            if (entries.Count > limits.MaxFiles || size > limits.MaxFileBytes || total > limits.MaxTotalBytes)
                throw new BridgeException("GitLimitExceeded", "Revision exceeds the configured file or byte budget.");
        }
        foreach (var entry in entries)
            if (VciGuard.Ancestors(entry.Path).Any(x => destinations.Contains(x.Normalize(NormalizationForm.FormC))))
                throw new BridgeException("GitPathCollision", "A revision file collides with a directory.");
        var id = Guid.NewGuid().ToString("N");
        var binding = GitOperationBinding.Materialize(before, control, ExecutableSha256, exactCommit, destination).Sha256;
        await Record(id, "materialize", before.RepositoryRoot, "dispatchPrepared", cancellationToken, before.HeadCommit, candidate: exactCommit, resource: destination, snapshot: before, binding: binding);
        try
        {
            Directory.CreateDirectory(destination);
            if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new BridgeException("GitDestinationExists", "Revision destination changed.");
            var evidence = new List<GitRevisionFile>();
            foreach (var entry in entries)
            {
                var bytes = (await git.RunAsync(before.RepositoryRoot, ["cat-file", "blob", entry.Oid], cancellationToken)).Bytes;
                if (bytes.LongLength != entry.Bytes || GitSafety.BlobObjectId(bytes) != entry.Oid)
                    throw new BridgeException("GitReadbackMismatch", "Blob bytes do not match the captured object ID and length.");
                var path = Path.Combine(destination, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                GitSafety.PathRoot(Path.GetDirectoryName(path)!, true);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await stream.WriteAsync(bytes, cancellationToken); stream.Flush(true); }
                var sha = GitSafety.Hash(bytes);
                if (GitSafety.HashFile(path) != sha) throw new BridgeException("GitReadbackMismatch", "Materialized bytes failed readback.");
                evidence.Add(new(entry.Path, entry.Oid, entry.Bytes, sha, entry.Mode));
            }
            var immutable = Array.AsReadOnly(evidence.ToArray());
            var digest = GitWorkspaceSnapshot.Digest(new { exactCommit, files = immutable });
            await Record(id, "materialize", before.RepositoryRoot, "verified", CancellationToken.None, before.HeadCommit, digest, exactCommit, resource: destination, snapshot: before, binding: binding);
            return new(id, before.RepositoryRoot, exactCommit, destination, immutable, digest);
        }
        catch (Exception ex) { await Unknown(id, "materialize", before.RepositoryRoot, before.HeadCommit, exactCommit, null, ex, binding); throw new GitOutcomeUnknownException(id, exactCommit, ex); }
    }

    private string NewDestination(string path)
    {
        var result = GitSafety.PathRoot(path, false);
        if (Directory.Exists(result) || File.Exists(result)) throw new BridgeException("GitDestinationExists", "Only a new destination is permitted.");
        if (Contained(result, control) || Contained(control, result)) throw new BridgeException("GitUnsafePath", "Control storage and output resources must be disjoint.");
        if (!Directory.Exists(Path.GetDirectoryName(result))) throw new BridgeException("GitUnsafePath", "The owned destination parent must already exist.");
        return result;
    }

    private string Repository(string path, bool ownsIndexLock)
    {
        var root = GitSafety.PathRoot(path, true);
        GitSafety.RequireRepositoryDirectory(root);
        if (Contained(control, root) || Contained(root, control)) throw new BridgeException("GitUnsafePath", "Control storage and repository must be disjoint.");
        var metadata = Path.Combine(root, ".git");
        if (!Directory.Exists(metadata)) throw new BridgeException("GitUnsupportedRepository", "A normal repository with its own .git directory is required; worktrees and bare repositories are unsupported.");
        GitSafety.NoLinks(metadata);
        var walked = 0;
        var directories = new Stack<string>(); directories.Push(metadata);
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++walked > 100000) throw new BridgeException("GitLimitExceeded", "Git metadata enumeration exceeded its safety bound.");
                GitSafety.NoLinks(entry);
                if (Directory.Exists(entry)) directories.Push(entry);
                else if (entry.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !(ownsIndexLock && entry == Path.Combine(metadata, "index.lock")))
                    throw new BridgeException("GitRepositoryBusy", "A Git metadata lock is present.");
                else if (Contained(entry, Path.Combine(metadata, "refs")))
                {
                    if (new FileInfo(entry).Length > 128 || !Regex.IsMatch(File.ReadAllText(entry, GitSafety.Utf8).Trim(), "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
                        throw new BridgeException("GitUnsupportedRepository", "Symbolic or malformed loose references are unsupported; only HEAD may be symbolic.");
                }
            }
        foreach (var unsupported in new[] { "commondir", "gitdir", "shallow", "info/attributes", "info/grafts", "objects/info/alternates", "objects/info/http-alternates",
            "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-apply", "rebase-merge", "refs/replace" })
            if (File.Exists(Path.Combine(metadata, unsupported)) || Directory.Exists(Path.Combine(metadata, unsupported)))
                throw new BridgeException("GitUnsupportedRepository", "Repository has unsupported indirection, attributes or an unfinished operation: " + unsupported);
        var hooks = Path.Combine(metadata, "hooks");
        if (Directory.Exists(hooks) && Directory.EnumerateFileSystemEntries(hooks).Any(x => !x.EndsWith(".sample", StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("GitExecutableCustomizationRefused", "Repository hooks require a separate reviewed execution profile.");
        var config = Path.Combine(metadata, "config");
        if (!File.Exists(config) || new FileInfo(config).Length > 65536) throw new BridgeException("GitUnsupportedConfiguration", "Missing or oversized repository config.");
        string section = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(config, GitSafety.Utf8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.Equals("[core]", StringComparison.OrdinalIgnoreCase)) { section = "core"; continue; }
            if (line.StartsWith('[') || section != "core") throw new BridgeException("GitUnsupportedConfiguration", "Only the minimal local core config profile is supported; includes, drivers and remotes are refused.");
            var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !seen.Add(pair[0])) throw new BridgeException("GitUnsupportedConfiguration", "Ambiguous repository configuration.");
            var valid = pair[0].ToLowerInvariant() switch
            {
                "repositoryformatversion" => pair[1] == "0",
                "bare" => pair[1] == "false",
                "filemode" or "logallrefupdates" or "symlinks" or "ignorecase" => pair[1] is "true" or "false",
                _ => false
            };
            if (!valid) throw new BridgeException("GitUnsupportedConfiguration", "Repository config requires an unsupported behavior: " + pair[0]);
        }
        if (!seen.Contains("repositoryformatversion") || !seen.Contains("bare")) throw new BridgeException("GitUnsupportedConfiguration", "Repository format and non-bare configuration are required.");
        return root;
    }

    private IReadOnlyList<GitFileEvidence> Inventory(string root, CancellationToken token)
    {
        var files = new List<GitFileEvidence>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root);
        long total = 0; int walked = 0;
        while (directories.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (path == Path.Combine(root, ".git")) continue;
                if (++walked > limits.MaxFiles * 2) throw new BridgeException("GitLimitExceeded", "Working inventory exceeds the bounded entry count.");
                GitSafety.NoLinks(path);
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                GitSafety.RelativePath(relative);
                if (!names.Add(relative.Normalize(NormalizationForm.FormC))) throw new BridgeException("GitPathCollision", "Working paths collide by case or Unicode normalization.");
                if (Directory.Exists(path)) { directories.Push(path); continue; }
                if (Path.GetFileName(path).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("GitExecutableCustomizationRefused", "Attributes and submodule configuration require a separate reviewed profile.");
                var bytes = ReadFile(path, token); total += bytes.LongLength;
                if (files.Count >= limits.MaxFiles || total > limits.MaxTotalBytes) throw new BridgeException("GitLimitExceeded", "Working files exceed the configured budget.");
                files.Add(new(relative, GitSafety.Hash(bytes), bytes.LongLength));
            }
        return files;
    }

    private byte[] ReadFile(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); GitSafety.NoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limits.MaxFileBytes) throw new BridgeException("GitLimitExceeded", "A file exceeds the per-file byte budget.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new BridgeException("PreviewStale", "File length changed while reading.");
        return bytes;
    }

    private async Task<GitWorkspaceSnapshot> Fresh(string root, string expected, CancellationToken token)
    {
        VciGuard.Hash(expected);
        var current = await InspectAsync(root, token);
        if (current.Sha256 != expected) throw new BridgeException("PreviewStale", "HEAD, index, configuration or working bytes differ from the reviewed snapshot.");
        return current;
    }
    private async Task RequireCleanIndex(GitWorkspaceSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.HeadCommit is null)
        {
            if ((await ReadIndex(snapshot.RepositoryRoot, token)).Count != 0) throw new BridgeException("GitDirtyIndex", "Unborn repository index is not empty.");
        }
        else
        {
            var result = await git.RunAsync(snapshot.RepositoryRoot, ["diff", "--cached", "--quiet", "--ita-visible-in-index", "--no-ext-diff", "--no-textconv", snapshot.HeadCommit, "--"], token, allowFailure: true);
            if (result.ExitCode != 0) throw new BridgeException("GitDirtyIndex", "The index must exactly match HEAD before a reviewed commit.");
        }
    }
    private async Task<Dictionary<string, (string Mode, string ObjectId)>> ReadIndex(string root, CancellationToken token)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (await git.RunAsync(root, ["ls-files", "-v", "--stage", "-z"], token)).Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(entry, "^H (100644|100755) ([0-9a-f]{40}) 0\\t(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new BridgeException("GitConflict", "Unmerged stages, skip/assume flags, symbolic links, submodules and unsupported index entries are refused.");
            var path = match.Groups[3].Value; GitSafety.RelativePath(path);
            if (!destinations.Add(path.Normalize(NormalizationForm.FormC))) throw new BridgeException("GitPathCollision", "Index paths collide by case or Unicode normalization.");
            if (Path.GetFileName(path).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals(".gitmodules", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("GitExecutableCustomizationRefused", "Tracked attributes or submodule configuration require a separate profile, including when absent from the worktree.");
            if (!result.TryAdd(path, (match.Groups[1].Value, match.Groups[2].Value)) || result.Count > limits.MaxFiles)
                throw new BridgeException("GitLimitExceeded", "Index entries are duplicate or exceed the configured bound.");
        }
        foreach (var path in result.Keys)
            if (VciGuard.Ancestors(path).Any(x => destinations.Contains(x.Normalize(NormalizationForm.FormC))))
                throw new BridgeException("GitPathCollision", "An index file collides with a directory.");
        return result;
    }
    private async Task ExactCommit(string root, string oid, CancellationToken token)
    {
        var observed = await git.RunAsync(root, ["rev-parse", "--verify", oid + "^{commit}"], token);
        if (observed.Text.Trim() != oid) throw new BridgeException("GitInvalidObjectId", "The supplied object must itself be a commit.");
    }
    private static string IndexHash(string root) => File.Exists(Path.Combine(root, ".git", "index")) ? GitSafety.HashFile(Path.Combine(root, ".git", "index")) : GitSafety.Hash([]);
    private static bool Contained(string child, string parent) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void ValidateAuthor(GitAuthor author)
    {
        if (author is null || string.IsNullOrWhiteSpace(author.Name) || author.Name.Length > 200 || author.Name.Any(x => char.IsControl(x) || x is '<' or '>') ||
            string.IsNullOrWhiteSpace(author.Email) || author.Email.Length > 254 || !Regex.IsMatch(author.Email, "^[^\\s<>@]+@[^\\s<>@]+$"))
            throw new BridgeException("GitInvalidCommit", "Supply a bounded local author name and email without control characters.");
    }
    private static IDisposable Acquire(string root)
    {
        var path = Path.Combine(root, ".git", "tia-mcp.operation-reservation");
        try { return new Reservation(path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new BridgeException("GitRepositoryBusy", "Another controlled Git operation owns the repository; reconcile abandoned reservations explicitly.", ex); }
    }
    private sealed class Reservation(string path, FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); File.Delete(path); }
    }
    private Task Record(string id, string operation, string root, string phase, CancellationToken token,
        string? expected = null, string? plan = null, string? candidate = null, string? tree = null, string? resource = null, GitWorkspaceSnapshot? snapshot = null, string? binding = null) =>
        journal.RecordAsync(new(id, operation, root, phase, expected, candidate, tree, plan, ResourcePath: resource, RecordedAt: DateTimeOffset.UtcNow,
            ExpectedSnapshotSha256: snapshot?.Sha256, ExpectedIndexSha256: snapshot?.IndexSha256, ExpectedConfigurationSha256: snapshot?.ConfigurationSha256, BindingSha256: binding), token);
    private async Task Unknown(string id, string operation, string root, string? expected, string? candidate, string? plan, Exception ex, string binding)
    {
        try { await journal.RecordAsync(new(id, operation, root, "outcomeUnknown", expected, candidate, PlanSha256: plan,
            ErrorCode: ex is BridgeException bridge ? bridge.Code : ex.GetType().Name, RecordedAt: DateTimeOffset.UtcNow, BindingSha256: binding), CancellationToken.None); }
        catch { /* The durable pre-dispatch record remains authoritative when result recording fails. */ }
    }
}
