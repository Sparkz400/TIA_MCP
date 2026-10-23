using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace TiaMcp.Core.VersionControl;

public sealed record VciUploadFile(string RelativePath, string? UploadRef = null, bool Delete = false);
public sealed record VciStoredArtifact(string Name, string Sha256, long Bytes);
public sealed record VciGitReview(string Id, string? ParentRef, string ExecutableSha256, string SnapshotSha256,
    string PlanSha256, GitFileChange[] Changes, GitAuthor Author, string Message, GitFileEvidence[] Files,
    VciStoredArtifact[] Artifacts, string? HeadReference = null);
public sealed record VciGitRevision(string PreviewRef, GitCommitResult Result, string SnapshotSha256, string CallbackSha256);
public sealed record VciGitExport(string RevisionRef, GitRevisionResult Result, VciStoredArtifact[] Artifacts);

/// <summary>Local document revisions only. Each candidate owns a separate repository; native admission is never inferred.</summary>
public sealed partial class VciWorkspaceStore
{
    private const int MaxFiles = 1000, MaxRecords = 128, MaxFileBytes = 16 << 20;
    private const long MaxTreeBytes = 256L << 20, MaxStoreBytes = 2L << 30;
    private static readonly GitRunnerLimits Limits = new(MaxFiles, 64L << 20, MaxFileBytes);
    private readonly string root, records, controls, executable;
    private readonly ArtifactUploadStore uploads;

    public VciWorkspaceStore(string artifactsDirectory, string gitExecutable, PackageIntakeLimits? uploadLimits = null)
    {
        executable = GitSafety.PathRoot(gitExecutable, false);
        if (!File.Exists(executable)) throw new BridgeException("GitExecutableUnavailable", "Configure one installed Git executable on the Host.");
        root = Path.GetFullPath(Path.Combine(artifactsDirectory, "vci-workspaces"));
        PackageStorage.EnsureNoLinks(root);
        records = PackageStorage.Under(root, "records"); controls = PackageStorage.Under(root, "controls");
        Directory.CreateDirectory(records); Directory.CreateDirectory(controls);
        uploads = new(artifactsDirectory, uploadLimits);
    }

    public async Task<object> PrepareAsync(VciUploadFile[] files, GitAuthor author, string message,
        string? parentRevisionRef = null, CancellationToken token = default, string? branchName = null)
    {
        using var gate = Lock();
        Require(files is { Length: > 0 and <= MaxFiles }, "VciInputLimit", "Select 1..1000 explicit additions, updates or deletions.");
        Require(author is not null && !string.IsNullOrWhiteSpace(message), "VciInvalidInput", "An explicit author and nonempty commit message are required.");
        GitWorkspaceRunner.ValidateAuthor(author);
        if (branchName is not null) GitSafety.Branch(branchName);
        Require(message.Length <= 16384 && !message.Contains('\0'), "GitInvalidCommit", "Commit message exceeds its supported bounds.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloads = new Dictionary<string, byte[]>();
        long total = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            Require(file is not null, "VciInvalidInput", "File selections cannot be null.");
            GitSafety.RelativePath(file.RelativePath);
            Require(names.Add(file.RelativePath.Normalize(NormalizationForm.FormC)), "GitPathCollision", "Selected paths collide.");
            Require(!new[] { ".gitattributes", ".gitmodules" }.Contains(Path.GetFileName(file.RelativePath), StringComparer.OrdinalIgnoreCase),
                "GitExecutableCustomizationRefused", "Git attributes and submodules require a separate preserving profile.");
            Require(!file.Delete || file.UploadRef is null, "VciInvalidInput", "A deletion cannot also supply uploaded bytes.");
            if (file.Delete) continue;
            byte[] bytes = [];
            if (file.UploadRef is not null)
            {
                var uploaded = uploads.ResolveCommitted(file.UploadRef);
                bytes = ReadBytes(uploaded.Path);
                Require(GitSafety.Hash(bytes) == uploaded.Sha256, "VciInputChanged", "Committed upload changed while reading.");
            }
            total += bytes.LongLength;
            Require(total <= Limits.MaxTotalBytes, "VciInputLimit", "Selected raw bytes exceed 64 MiB.");
            payloads.Add(file.RelativePath, bytes);
        }
        var entry = NewEntry();
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "prepare", parentRevisionRef, branchName, files, author, message });
        string repository = Path.Combine(entry.Path, "repository");
        if (parentRevisionRef is null)
        {
            var journal = new Journal(Path.Combine(entry.Path, "init-journal"));
            var runner = Runner(journal);
            journal.Bind(GitOperationBinding.Initialize(repository, runner.ControlDirectory, runner.ExecutableSha256, branchName ?? "main"));
            await runner.InitializeAsync(repository, branchName ?? "main", token);
        }
        else
        {
            var parent = Revision(parentRevisionRef);
            var reader = Runner();
            var before = await reader.InspectAsync(parent.Repository, token);
            Require(before.Sha256 == parent.Value.SnapshotSha256, "PreviewStale", "Parent repository differs from its retained committed revision.");
            var branches = await reader.BranchesAsync(parent.Repository, token);
            CopyTree(parent.Repository, repository, token);
            Require((await reader.InspectAsync(parent.Repository, token)).Sha256 == before.Sha256,
                "PreviewStale", "Parent repository changed during copying.");
            Require(branches.SequenceEqual(await reader.BranchesAsync(parent.Repository, token)) &&
                branches.SequenceEqual(await reader.BranchesAsync(repository, token)), "PreviewStale", "Parent branch inventory changed during copying.");
            if (branchName is not null && GitSafety.Branch(branchName) != before.HeadReference)
            {
                var journal = new Journal(Path.Combine(entry.Path, "fork-journal")); var fork = Runner(journal);
                var copied = await fork.InspectAsync(repository, token);
                journal.Bind(GitOperationBinding.Fork(copied, fork.ControlDirectory, fork.ExecutableSha256, branchName));
                await fork.ForkAsync(repository, copied.Sha256, branchName, token);
            }
        }
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string destination = PackageStorage.Under(repository, file.RelativePath);
            if (file.Delete)
            {
                Require(File.Exists(destination), "VciInvalidInput", "Selected deletion is absent from the parent revision.");
                File.Delete(destination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, payloads[file.RelativePath]);
            }
        }
        var inspect = Runner();
        var snapshot = await inspect.InspectAsync(repository, token);
        var changes = files.Select(f => new GitFileChange(f.RelativePath, f.Delete)).ToArray();
        var plan = await inspect.PrepareCommitAsync(repository, snapshot.Sha256, changes, author, message, token);
        var diff = await inspect.DiffAsync(repository, snapshot.Sha256, token);
        WriteBytes(Path.Combine(entry.Path, "diff.txt"), Encoding.UTF8.GetBytes(diff));
        WriteNew(Path.Combine(entry.Path, "files.json"), snapshot.Files);
        var artifacts = new List<VciStoredArtifact> { Artifact(entry.Path, "diff.txt"), Artifact(entry.Path, "files.json") };
        if (Directory.Exists(Path.Combine(entry.Path, "fork-journal")))
            artifacts.AddRange(new[] { "fork-journal/intent.json", "fork-journal/0001.json", "fork-journal/0002.json" }.Select(name => Artifact(entry.Path, name)));
        var review = new VciGitReview(entry.Id, parentRevisionRef, inspect.ExecutableSha256, snapshot.Sha256,
            plan.Sha256, changes, author, message, snapshot.Files.ToArray(),
            artifacts.ToArray(), snapshot.HeadReference);
        WriteNew(Path.Combine(entry.Path, "review.json"), review);
        return Reopen(Reference("preview", entry.Id, Path.Combine(entry.Path, "review.json")));
    }

    public async Task<object> CommitAsync(string previewRef, string reviewedPlanSha256, CancellationToken token = default)
    {
        using var gate = Lock();
        var preview = Preview(previewRef);
        _ = TreeFiles(root, MaxStoreBytes - MaxTreeBytes);
        Require(preview.Value.PlanSha256 == reviewedPlanSha256, "PreviewStale", "Supply the exact reviewed commit-plan digest.");
        string intent = Path.Combine(preview.Directory, "commit-intent.json");
        Require(!File.Exists(intent) && !Directory.Exists(Path.Combine(preview.Directory, "commit-journal")),
            "RecoveryRequired", "This candidate already has commit intent. Inspect it; never replay an uncertain commit.");
        var journal = new Journal(Path.Combine(preview.Directory, "commit-journal"));
        var runner = Runner(journal);
        Require(runner.ExecutableSha256 == preview.Value.ExecutableSha256, "PreviewStale", "Configured Git executable changed since review.");
        var plan = await runner.PrepareCommitAsync(preview.Repository, preview.Value.SnapshotSha256,
            preview.Value.Changes, preview.Value.Author, preview.Value.Message, token);
        Require(plan.Sha256 == reviewedPlanSha256, "PreviewStale", "Reconstructed plan differs from the retained review.");
        var binding = GitOperationBinding.Commit(plan, runner.ControlDirectory, runner.ExecutableSha256);
        WriteNew(intent, new { previewRef, binding });
        journal.Bind(binding);
        try
        {
            var result = await runner.CommitAsync(plan, reviewedPlanSha256, token);
            var after = await runner.InspectAsync(preview.Repository, token);
            Require(after.HeadCommit == result.Commit && after.HeadTree == result.Tree && after.Status.Length == 0,
                "VciVerificationFailed", "Published revision, index and working tree are not clean and consistent.");
            var receipt = new VciGitRevision(previewRef, result, after.Sha256,
                Journal.Read(Path.Combine(preview.Directory, "commit-journal"), preview.Value, preview.Repository)!.Sha256);
            WriteNew(Path.Combine(preview.Directory, "commit.json"), receipt);
            return Reopen(Reference("revision", preview.Value.Id, Path.Combine(preview.Directory, "commit.json")));
        }
        catch (Exception error)
        {
            throw new BridgeException("VciOutcomeUnknown", "Commit intent remains retained. Inspect this candidate; do not repeat commit. Its parent revision is unchanged.", error);
        }
    }

    public object Reopen(string recordRef)
    {
        var parsed = Parse(recordRef);
        if (parsed.Kind is "manifest" or "comparison") return ReopenPackages(recordRef, parsed.Kind);
        if (parsed.Kind == "export")
        {
            var export = Read<VciGitExport>(recordRef, "export");
            VerifyArtifacts(export.Directory, export.Value.Artifacts, "revision.zip", "files.json");
            return new { recordRef, kind = "export", state = "complete", nativeQualified = false,
                revisionRef = export.Value.RevisionRef, commit = export.Value.Result.Commit,
                fileCount = export.Value.Result.Files.Count, artifacts = export.Value.Artifacts, receiptName = "receipt.json" };
        }
        var preview = parsed.Kind == "revision" ? Preview(Revision(recordRef).Value.PreviewRef) : Preview(recordRef);
        var committed = parsed.Kind == "revision" ? Revision(recordRef).Value : null;
        string? revisionRef = File.Exists(Path.Combine(preview.Directory, "commit.json"))
            ? Reference("revision", preview.Value.Id, Path.Combine(preview.Directory, "commit.json")) : null;
        if (committed is null && revisionRef is not null) committed = Revision(revisionRef).Value;
        return new { recordRef, kind = parsed.Kind, previewRef = committed?.PreviewRef ?? recordRef, revisionRef,
            state = committed is not null ? "committed" : HasCommitIntent(preview.Directory) ? "outcomeUnknown" : "prepared",
            nativeQualified = false, preview.Value.ParentRef, preview.Value.PlanSha256, preview.Value.Author, preview.Value.Message, preview.Value.HeadReference,
            fileCount = preview.Value.Files.Length, changeCount = preview.Value.Changes.Length,
            commit = committed?.Result.Commit, tree = committed?.Result.Tree, artifacts = preview.Value.Artifacts,
            receiptName = "receipt.json", executableSha256 = preview.Value.ExecutableSha256, gitVersion = committed?.Result.GitVersion,
            commitJournal = Journal.Read(Path.Combine(preview.Directory, "commit-journal"), preview.Value, preview.Repository),
            diffCoverage = "Tracked changes only; new files and exact raw contents are retained in files.json and file reads." };
    }

    public async Task<object> ReconcileAsync(string previewRef, string expectedCallbackSha256, CancellationToken token = default)
    {
        using var gate = Lock(); var preview = Preview(previewRef);
        var callback = Journal.Read(Path.Combine(preview.Directory, "commit-journal"), preview.Value, preview.Repository);
        Require(File.Exists(Path.Combine(preview.Directory, "commit-intent.json")) && callback is not null && callback.Phase == "verified" &&
            callback.Sha256 == expectedCallbackSha256 && callback.CandidateCommit is not null && callback.CandidateTree is not null,
            "RecoveryRequired", "Reconciliation requires the exact retained verified callback. Earlier or damaged outcomes remain unknown.");
        string receiptPath = Path.Combine(preview.Directory, "commit.json");
        if (File.Exists(receiptPath)) return Reopen(Reference("revision", preview.Value.Id, receiptPath));
        var reader = Runner(); var after = await reader.InspectAsync(preview.Repository, token);
        Require(reader.ExecutableSha256 == preview.Value.ExecutableSha256 && after.HeadCommit == callback.CandidateCommit &&
            after.HeadTree == callback.CandidateTree && after.Status.Length == 0, "PreviewStale", "Repository or executable differs from the verified callback.");
        string? parent = preview.Value.ParentRef is null ? null : Revision(preview.Value.ParentRef).Value.Result.Commit;
        var result = new GitCommitResult(callback.OperationId, preview.Repository, callback.CandidateCommit, callback.CandidateTree,
            parent, preview.Value.PlanSha256, reader.ExecutableSha256, await reader.GetVersionAsync(token));
        WriteNew(receiptPath, new VciGitRevision(previewRef, result, after.Sha256, callback.Sha256));
        return Reopen(Reference("revision", preview.Value.Id, receiptPath));
    }

    public async Task<object> HistoryAsync(string revisionRef, int count = 20, CancellationToken token = default)
    {
        using var gate = Lock();
        Require(count is > 0 and <= 50, "VciInputLimit", "History accepts 1..50 commits.");
        var revision = Revision(revisionRef); var runner = Runner();
        Require((await runner.InspectAsync(revision.Repository, token)).Sha256 == revision.Value.SnapshotSha256,
            "PreviewStale", "Revision repository changed.");
        var history = await runner.HistoryAsync(revision.Repository, count, token);
        Require(history.StartsWith(revision.Value.Result.Commit + "\0", StringComparison.Ordinal) &&
            (await runner.InspectAsync(revision.Repository, token)).Sha256 == revision.Value.SnapshotSha256,
            "PreviewStale", "History no longer belongs to the selected immutable revision.");
        Require(Encoding.UTF8.GetByteCount(history) <= 65536, "VciOutputLimit", "History exceeds 64 KiB; request fewer commits.");
        return new { revisionRef, nativeQualified = false, maximumCommits = count, history,
            format = "One LF-separated row per commit; NUL-separated commit, tree, parents, author, email, subject and empty terminator." };
    }

    public async Task<object> BranchesAsync(string revisionRef, int offset = 0, int count = 20, string? expectedInventorySha256 = null,
        CancellationToken token = default)
    {
        using var gate = Lock();
        Require(offset >= 0 && count is > 0 and <= 50, "VciInputLimit", "List 1..50 branch refs per page.");
        Require(offset == 0 || expectedInventorySha256 is not null, "VciInputLimit", "Subsequent pages require the first page's inventorySha256.");
        if (expectedInventorySha256 is not null) VciGuard.Hash(expectedInventorySha256);
        var revision = Revision(revisionRef); var reader = Runner();
        Require((await reader.InspectAsync(revision.Repository, token)).Sha256 == revision.Value.SnapshotSha256,
            "PreviewStale", "Revision repository changed.");
        var branches = await reader.BranchesAsync(revision.Repository, token);
        string hash = GitWorkspaceSnapshot.Digest(branches);
        Require((expectedInventorySha256 is null || expectedInventorySha256 == hash) &&
            hash == GitWorkspaceSnapshot.Digest(await reader.BranchesAsync(revision.Repository, token)) &&
            (await reader.InspectAsync(revision.Repository, token)).Sha256 == revision.Value.SnapshotSha256,
            "PreviewStale", "Branch inventory changed; restart from the first page.");
        Require(offset <= branches.Count, "VciInputLimit", "Branch offset exceeds the current inventory.");
        var rows = branches.Skip(offset).Take(count).ToArray();
        var result = new { revisionRef, inventorySha256 = hash, offset, rows, total = branches.Count,
            nextOffset = offset + rows.Length == branches.Count ? (int?)null : offset + rows.Length,
            nativeQualified = false, scope = "Fresh local refs in this exact revision repository; pin inventorySha256 across pages." };
        Require(JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length <= 65536, "VciOutputLimit", "Branch page exceeds 64 KiB; reduce count.");
        return result;
    }

    public async Task<object> MaterializeAsync(string revisionRef, CancellationToken token = default)
    {
        using var gate = Lock();
        var revision = Revision(revisionRef); var entry = NewEntry();
        WriteNew(Path.Combine(entry.Path, "intent.json"), new { kind = "export", revisionRef });
        var journal = new Journal(Path.Combine(entry.Path, "export-journal")); var runner = Runner(journal);
        var before = await runner.InspectAsync(revision.Repository, token);
        Require(before.Sha256 == revision.Value.SnapshotSha256, "PreviewStale", "Revision repository changed.");
        string destination = Path.Combine(entry.Path, "files");
        journal.Bind(GitOperationBinding.Materialize(before, runner.ControlDirectory, runner.ExecutableSha256, revision.Value.Result.Commit, destination));
        var result = await runner.MaterializeAsync(revision.Repository, revision.Value.Result.Commit, destination, token);
        var reviewedFiles = Preview(revision.Value.PreviewRef).Value.Files;
        Require(result.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => (f.RelativePath, f.Sha256, f.Bytes))
            .SequenceEqual(reviewedFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal).Select(f => (f.RelativePath, f.Sha256, f.Bytes))),
            "VciIntegrityFailure", "Exported revision bytes differ from the reviewed file manifest.");
        using (var output = new FileStream(Path.Combine(entry.Path, "revision.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var file in result.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = ReadBytes(PackageStorage.Under(destination, file.RelativePath));
                    Require(GitSafety.Hash(bytes) == file.Sha256, "VciInputChanged", "Materialized file changed before archive creation.");
                    var member = archive.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
                    using var stream = member.Open(); stream.Write(bytes);
                }
            output.Flush(true);
        }
        WriteNew(Path.Combine(entry.Path, "files.json"), result.Files);
        WriteNew(Path.Combine(entry.Path, "export.json"), new VciGitExport(revisionRef, result,
            [Artifact(entry.Path, "revision.zip"), Artifact(entry.Path, "files.json")]));
        return Reopen(Reference("export", entry.Id, Path.Combine(entry.Path, "export.json")));
    }

    public object ReadArtifact(string recordRef, string name, long offset = 0, int count = 65536)
    {
        var parsed = Parse(recordRef);
        if (name == "receipt.json")
        {
            string path = PackageStorage.Under(DirectoryFor(parsed.Id), Receipt(parsed.Kind));
            PackageStorage.EnsureNoLinks(path); long length = new FileInfo(path).Length;
            Require(length <= MaxFileBytes, "VciInputLimit", "Receipt exceeds its bounded read limit.");
            return Chunk(path, parsed.Hash, length, offset, count);
        }
        var artifacts = parsed.Kind is "manifest" or "comparison" ? PackageArtifacts(recordRef, parsed.Kind)
            : parsed.Kind == "export" ? Read<VciGitExport>(recordRef, "export").Value.Artifacts
            : Preview(parsed.Kind == "revision" ? Revision(recordRef).Value.PreviewRef : recordRef).Value.Artifacts;
        var artifact = artifacts.SingleOrDefault(a => a.Name == name);
        Require(artifact is not null, "VciArtifactMissing", "Use an artifact name returned by this record.");
        return Chunk(PackageStorage.Under(DirectoryFor(parsed.Id), artifact.Name), artifact.Sha256, artifact.Bytes, offset, count);
    }

    public object ReadFile(string recordRef, string relativePath, long offset = 0, int count = 65536)
    {
        var parsed = Parse(recordRef);
        Require(parsed.Kind is "preview" or "revision", "VciInvalidReference", "Use a preview or committed revision.");
        var preview = Preview(parsed.Kind == "revision" ? Revision(recordRef).Value.PreviewRef : recordRef);
        var file = preview.Value.Files.SingleOrDefault(f => f.RelativePath == relativePath);
        Require(file is not null, "VciArtifactMissing", "Use an exact retained file path from files.json.");
        return Chunk(PackageStorage.Under(preview.Repository, file.RelativePath), file.Sha256, file.Bytes, offset, count);
    }

    public object List(int offset = 0, int count = 20)
    {
        using var gate = Lock();
        var entries = Directory.EnumerateDirectories(records).Order(StringComparer.Ordinal).Take(MaxRecords + 1).ToArray();
        Require(entries.Length <= MaxRecords && offset >= 0 && offset <= entries.Length && count is > 0 and <= 50,
            "VciInputLimit", "List accepts 1..50 records within the retained inventory.");
        var rows = entries.Skip(offset).Take(count).Select(directory =>
        {
            PackageStorage.EnsureNoLinks(directory); string id = PackageStorage.Id(Path.GetFileName(directory));
            string? reference = null; string state = "incomplete";
            try
            {
                string? kind = File.Exists(Path.Combine(directory, "comparison-receipt.json")) ? "comparison" :
                    File.Exists(Path.Combine(directory, "manifest-receipt.json")) ? "manifest" :
                    File.Exists(Path.Combine(directory, "export.json")) ? "export" :
                    File.Exists(Path.Combine(directory, "commit.json")) ? "revision" : File.Exists(Path.Combine(directory, "review.json")) ? "preview" : null;
                if (kind is not null) { reference = Reference(kind, id, Path.Combine(directory, Receipt(kind))); _ = Reopen(reference); state = kind == "preview" ? "prepared" : "complete"; }
                if (kind == "preview" && HasCommitIntent(directory)) state = "outcomeUnknown";
                if (kind is null && Directory.Exists(Path.Combine(directory, "fork-journal"))) state = "outcomeUnknown";
            }
            catch (Exception e) when (e is BridgeException or IOException or JsonException or UnauthorizedAccessException) { state = "integrity-failed"; }
            return new { id, recordRef = reference, state };
        }).ToArray();
        return new { rows, total = entries.Length, nextOffset = offset + rows.Length == entries.Length ? (int?)null : offset + rows.Length,
            ordering = "record-id; relist after concurrent additions", nativeQualified = false };
    }

    private (VciGitReview Value, string Directory, string Repository) Preview(string reference)
    {
        var entry = Read<VciGitReview>(reference, "preview");
        Require(entry.Value.Id == Parse(reference).Id && entry.Value.Files.Length <= MaxFiles, "VciIntegrityFailure", "Review identity or bounds changed.");
        string[] expectedArtifacts = Directory.Exists(Path.Combine(entry.Directory, "fork-journal"))
            ? ["diff.txt", "files.json", "fork-journal/intent.json", "fork-journal/0001.json", "fork-journal/0002.json"] : ["diff.txt", "files.json"];
        VerifyArtifacts(entry.Directory, entry.Value.Artifacts, expectedArtifacts);
        string repository = Path.Combine(entry.Directory, "repository");
        var actual = TreeFiles(repository, MaxTreeBytes).Select(p => Path.GetRelativePath(repository, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith(".git/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(entry.Value.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal)),
            "VciIntegrityFailure", "Working-file inventory differs from the retained review.");
        foreach (var file in entry.Value.Files)
        {
            GitSafety.RelativePath(file.RelativePath); string path = PackageStorage.Under(repository, file.RelativePath);
            Require(new FileInfo(path).Length == file.Bytes && PackageStorage.FileHash(path) == file.Sha256,
                "VciIntegrityFailure", "Retained working bytes changed.");
        }
        return (entry.Value, entry.Directory, Path.Combine(entry.Directory, "repository"));
    }
    private (VciGitRevision Value, string Directory, string Repository) Revision(string reference)
    {
        var entry = Read<VciGitRevision>(reference, "revision"); var preview = Preview(entry.Value.PreviewRef);
        Require(entry.Directory == preview.Directory && entry.Value.Result.PlanSha256 == preview.Value.PlanSha256 &&
            entry.Value.Result.RepositoryRoot == preview.Repository && !entry.Value.Result.NativeQualified,
            "VciIntegrityFailure", "Revision does not bind this exact local review.");
        var callback = Journal.Read(Path.Combine(preview.Directory, "commit-journal"), preview.Value, preview.Repository);
        Require(callback is not null && callback.Phase == "verified" && callback.Sha256 == entry.Value.CallbackSha256 &&
            callback.CandidateCommit == entry.Value.Result.Commit && callback.CandidateTree == entry.Value.Result.Tree,
            "VciIntegrityFailure", "Completed revision differs from its verified durable callback.");
        return (entry.Value, entry.Directory, preview.Repository);
    }
    private (T Value, string Directory) Read<T>(string reference, string kind)
    {
        var parsed = Parse(reference); Require(parsed.Kind == kind, "VciInvalidReference", "Wrong record kind.");
        string directory = DirectoryFor(parsed.Id), path = PackageStorage.Under(directory, Receipt(kind));
        var bytes = ReadBytes(path);
        Require(GitSafety.Hash(bytes) == parsed.Hash, "VciIntegrityFailure", "Retained record digest changed.");
        return (JsonSerializer.Deserialize<T>(bytes, BridgeProtocol.Json) ?? throw new BridgeException("VciIntegrityFailure", "Missing record."), directory);
    }
    private static (string Kind, string Id, string Hash) Parse(string reference)
    {
        Require(reference is { Length: >= 108 and <= 112 }, "VciInvalidReference", "Use a bounded Host-issued VCI reference.");
        var parts = reference?.Split(':');
        Require(parts is { Length: 4 } && parts[0] == "vci" && parts[1] is "preview" or "revision" or "export" or "manifest" or "comparison",
            "VciInvalidReference", "Use the exact Host-issued VCI reference.");
        string hash = PackageStorage.Hash(parts[3]);
        Require(parts[3] == hash, "VciInvalidReference", "VCI references use canonical lowercase digests.");
        return (parts[1], PackageStorage.Id(parts[2]), hash);
    }
    private string DirectoryFor(string id) => PackageStorage.Under(records, id);
    private static bool HasCommitIntent(string directory) => File.Exists(Path.Combine(directory, "commit-intent.json")) || Directory.Exists(Path.Combine(directory, "commit-journal"));
    private static string Receipt(string kind) => kind switch
    { "preview" => "review.json", "revision" => "commit.json", "manifest" => "manifest-receipt.json", "comparison" => "comparison-receipt.json", _ => "export.json" };
    private static string Reference(string kind, string id, string path) => "vci:" + kind + ":" + id + ":" + PackageStorage.FileHash(path);
    private FileStream Lock() => PackageStorage.Lock(PackageStorage.Under(root, "store.lock"));
    private (string Id, string Path) NewEntry()
    {
        Require(Directory.EnumerateDirectories(records).Take(MaxRecords).Count() < MaxRecords, "VciStorageLimit", "The retained VCI record limit is 128.");
        _ = TreeFiles(root, MaxStoreBytes - 2 * MaxTreeBytes);
        string id = Guid.NewGuid().ToString("N"), path = DirectoryFor(id); Directory.CreateDirectory(path); return (id, path);
    }
    private GitWorkspaceRunner Runner(IGitOperationJournal? journal = null)
    {
        Require(Directory.EnumerateDirectories(controls).Take(4096).Count() < 4096, "VciStorageLimit", "VCI process-control retention reached its bound.");
        return new(executable, PackageStorage.Under(controls, Guid.NewGuid().ToString("N")), journal ?? new ReadOnlyJournal(), Limits);
    }
    private static void CopyTree(string source, string destination, CancellationToken token)
    {
        var files = TreeFiles(source, MaxTreeBytes); Directory.CreateDirectory(destination); long copied = 0;
        var buffer = new byte[65536];
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested(); string target = PackageStorage.Under(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Require(input.Length <= MaxTreeBytes, "VciStorageLimit", "A repository metadata file exceeds the copy budget.");
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    token.ThrowIfCancellationRequested(); copied += read;
                    Require(copied <= MaxTreeBytes, "VciStorageLimit", "Repository grew beyond the complete copy budget.");
                    output.Write(buffer, 0, read);
                }
                output.Flush(true);
            }
            Require(PackageStorage.FileHash(path) == PackageStorage.FileHash(target), "PreviewStale", "Source repository changed during copy.");
        }
    }
    private static List<string> TreeFiles(string root, long budget)
    {
        var files = new List<string>(); var pending = new Stack<string>(); pending.Push(root); long total = 0; int count = 0;
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                Require(++count <= 200000, "VciStorageLimit", "Filesystem enumeration exceeded its bound.");
                PackageStorage.EnsureNoLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
                else { total += new FileInfo(path).Length; Require(total <= budget, "VciStorageLimit", "Retained data exceeded its byte budget."); files.Add(path); }
            }
        return files;
    }
    private static byte[] ReadBytes(string path)
    {
        PackageStorage.EnsureNoLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length <= MaxFileBytes, "VciInputLimit", "Input exceeds 16 MiB.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    private static void WriteNew<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json));
    private static void WriteBytes(string path, byte[] bytes)
    {
        PackageStorage.EnsureNoLinks(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true);
    }
    private static VciStoredArtifact Artifact(string directory, string name)
    { string path = PackageStorage.Under(directory, name); return new(name, PackageStorage.FileHash(path), new FileInfo(path).Length); }
    private static void VerifyArtifacts(string directory, VciStoredArtifact[] artifacts, params string[] names)
    {
        Require(artifacts is not null && artifacts.All(a => a is not null) &&
            artifacts.Select(a => a.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)),
            "VciIntegrityFailure", "Retained artifact inventory differs from this record's exact expected files.");
        foreach (var artifact in artifacts)
        {
            string path = PackageStorage.Under(directory, PackageStorage.Relative(artifact.Name));
            Require(new FileInfo(path).Length == artifact.Bytes && PackageStorage.FileHash(path) == artifact.Sha256,
                "VciIntegrityFailure", "Retained artifact changed.");
        }
    }
    private static object Chunk(string path, string expected, long length, long offset, int count)
    {
        Require(offset >= 0 && offset <= length && count is > 0 and <= 65536, "VciInputLimit", "Read at most 64 KiB within the retained file.");
        var store = new ArtifactStore(Path.GetDirectoryName(path)!);
        var result = JsonSerializer.SerializeToElement(store.Read(path, offset, count), BridgeProtocol.Json);
        Require(result.GetProperty("sha256").GetString() == expected && result.GetProperty("bytes").GetInt64() == length,
            "VciIntegrityFailure", "Downloaded bytes no longer match this record.");
        return new { sha256 = expected, bytes = length, offset, count = result.GetProperty("count").GetInt32(),
            data = result.GetProperty("data").GetString(), encoding = "base64", nextOffset = result.GetProperty("nextOffset").GetInt64(),
            complete = result.GetProperty("complete").GetBoolean(), nativeQualified = false };
    }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message) { if (!condition) throw new BridgeException(code, message); }
    private sealed class ReadOnlyJournal : IGitOperationJournal
    { public Task RecordAsync(GitOperationRecord record, CancellationToken token) => throw new BridgeException("VciJournalRequired", "Read-only runner cannot dispatch mutations."); }
    private sealed class Journal(string directory) : IGitOperationJournal
    {
        private sealed record Callback(int Sequence, string? PreviousSha256, GitOperationRecord Record);
        internal sealed record Status(int Count, string Sha256, string Phase, string OperationId, string? CandidateCommit, string? CandidateTree);
        private GitOperationBinding? binding; private GitOperationRecord? latest; private string? previous; private int sequence;
        public static Status? Read(string directory, VciGitReview review, string repository)
        {
            PackageStorage.EnsureNoLinks(directory);
            if (!Directory.Exists(directory)) return null;
            var intent = JsonSerializer.Deserialize<GitOperationBinding>(ReadBytes(Path.Combine(directory, "intent.json")), BridgeProtocol.Json);
            Require(intent is not null && intent.Operation == "commit" && intent.RepositoryRoot == repository &&
                intent.PlanSha256 == review.PlanSha256 && intent.ExecutableSha256 == review.ExecutableSha256 && intent.SnapshotSha256 == review.SnapshotSha256 &&
                (review.HeadReference is null || intent.Branch == review.HeadReference),
                "VciIntegrityFailure", "Commit journal intent differs from review.");
            var paths = Directory.EnumerateFiles(directory).Where(p => Path.GetFileName(p) != "intent.json").Order(StringComparer.Ordinal).Take(6).ToArray();
            Require(paths.Length <= 5, "VciIntegrityFailure", "Unexpected callback count.");
            string? priorHash = null; GitOperationRecord? previousRecord = null; int count = 0;
            foreach (var path in paths)
            {
                var bytes = ReadBytes(path); var row = JsonSerializer.Deserialize<Callback>(bytes, BridgeProtocol.Json);
                Require(row is not null && row.Sequence == ++count && Path.GetFileName(path) == count.ToString("D4") + ".json" &&
                    row.PreviousSha256 == priorHash && row.Record.BindingSha256 == intent.Sha256 && row.Record.Operation == "commit" &&
                    row.Record.RepositoryRoot == repository && (previousRecord is null || row.Record.OperationId == previousRecord.OperationId) &&
                    Allowed("commit", previousRecord?.Phase, row.Record.Phase), "VciIntegrityFailure", "Commit callback sequence or hash chain changed.");
                priorHash = GitSafety.Hash(bytes); previousRecord = row.Record;
            }
            return previousRecord is null ? null : new(count, priorHash!, previousRecord.Phase, previousRecord.OperationId,
                previousRecord.CandidateCommit, previousRecord.CandidateTree);
        }
        private static bool Allowed(string operation, string? previousPhase, string phase) => phase switch
        {
            "dispatchPrepared" => previousPhase is null,
            "candidateRecorded" => operation == "commit" && previousPhase == "dispatchPrepared",
            "publicationPrepared" => operation == "commit" && previousPhase == "candidateRecorded",
            "verified" => previousPhase == (operation == "commit" ? "publicationPrepared" : "dispatchPrepared"),
            "outcomeUnknown" => previousPhase is "dispatchPrepared" or "candidateRecorded" or "publicationPrepared" or "verified",
            _ => false
        };
        public void Bind(GitOperationBinding value)
        {
            Require(binding is null && !Directory.Exists(directory), "RecoveryRequired", "Journal invocation cannot be reopened for replay.");
            PackageStorage.EnsureNoLinks(directory); Directory.CreateDirectory(directory); binding = value;
            WriteNew(Path.Combine(directory, "intent.json"), value);
        }
        public Task RecordAsync(GitOperationRecord record, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(binding is not null && record.BindingSha256 == binding.Sha256 && record.RepositoryRoot == binding.RepositoryRoot &&
                record.Operation == binding.Operation && (latest is null || record.OperationId == latest.OperationId), "VciJournalMismatch", "Callback differs from admitted local intent.");
            Require(Allowed(binding.Operation, latest?.Phase, record.Phase), "VciJournalSequence", "Invalid callback sequence.");
            string path = Path.Combine(directory, (++sequence).ToString("D4") + ".json");
            WriteNew(path, new { sequence, previousSha256 = previous, record }); previous = PackageStorage.FileHash(path); latest = record;
            return Task.CompletedTask;
        }
    }
}
