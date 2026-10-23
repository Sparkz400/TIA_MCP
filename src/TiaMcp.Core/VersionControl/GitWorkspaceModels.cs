using System.Collections.ObjectModel;
using System.Text.Json;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.VersionControl;

public sealed record GitRunnerLimits(int MaxFiles = 20000, long MaxTotalBytes = 1073741824,
    int MaxFileBytes = 16777216, int MaxOutputBytes = 16777216, int TimeoutMilliseconds = 30000)
{
    internal void Validate()
    {
        if (MaxFiles is < 1 or > 20000 || MaxTotalBytes is < 1 or > 1073741824 ||
            MaxFileBytes is < 1 or > 16777216 || MaxOutputBytes is < 1 or > 16777216 ||
            TimeoutMilliseconds is < 1 or > 120000)
            throw new BridgeException("GitInvalidLimits", "Git bounds exceed the supported local profile.");
    }
}

public sealed record GitFileEvidence(string RelativePath, string Sha256, long Bytes);
public sealed record GitAuthor(string Name, string Email);
public sealed record GitFileChange(string RelativePath, bool Delete = false);
public sealed record GitBranch(string Reference, string Commit);
public sealed record GitRevisionFile(string RelativePath, string ObjectId, long Bytes, string Sha256, string Mode);

/// <summary>The bridge journal must persist each record before returning. A failed dispatch is never blindly retried.</summary>
public interface IGitOperationJournal
{
    Task RecordAsync(GitOperationRecord record, CancellationToken cancellationToken);
}

public sealed record GitOperationRecord(string OperationId, string Operation, string RepositoryRoot, string Phase,
    string? ExpectedHead = null, string? CandidateCommit = null, string? CandidateTree = null,
    string? PlanSha256 = null, string? ErrorCode = null, string? ResourcePath = null,
    DateTimeOffset? RecordedAt = null, string? ExpectedSnapshotSha256 = null,
    string? ExpectedIndexSha256 = null, string? ExpectedConfigurationSha256 = null,
    string? BindingSha256 = null);

public sealed class GitWorkspaceSnapshot
{
    public string RepositoryRoot { get; }
    public string HeadReference { get; }
    public string? HeadCommit { get; }
    public string? HeadTree { get; }
    public string IndexSha256 { get; }
    public string ConfigurationSha256 { get; }
    public string Status { get; }
    public IReadOnlyList<GitFileEvidence> Files { get; }
    public string Sha256 { get; }
    internal GitWorkspaceSnapshot(string root, string reference, string? commit, string? tree, string index,
        string configuration, string status, IEnumerable<GitFileEvidence> files)
    {
        RepositoryRoot = root; HeadReference = reference; HeadCommit = commit; HeadTree = tree;
        IndexSha256 = index; ConfigurationSha256 = configuration; Status = status;
        Files = Array.AsReadOnly(files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray());
        Sha256 = Digest(new { root, reference, commit, tree, index, configuration, status, files = Files });
    }
    internal static string Digest(object value) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
}

/// <summary>Created only by the runner. Review evidence and private copied bytes cannot be edited by callers.</summary>
public sealed class GitCommitPlan
{
    public GitWorkspaceSnapshot Before { get; }
    public IReadOnlyList<GitFileChange> Changes { get; }
    public GitAuthor Author { get; }
    public string Message { get; }
    public string Sha256 { get; }
    internal IReadOnlyDictionary<string, byte[]> Payloads { get; }
    internal GitCommitPlan(GitWorkspaceSnapshot before, GitFileChange[] changes, GitAuthor author,
        string message, Dictionary<string, byte[]> payloads)
    {
        Before = before; Changes = Array.AsReadOnly(changes); Author = author; Message = message;
        Payloads = new ReadOnlyDictionary<string, byte[]>(payloads);
        Sha256 = GitWorkspaceSnapshot.Digest(new { before = before.Sha256, changes, author, message,
            payloads = payloads.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { path = x.Key, sha256 = GitSafety.Hash(x.Value) }) });
    }
}

public sealed record GitCommitResult(string OperationId, string RepositoryRoot, string Commit, string Tree,
    string? Parent, string PlanSha256, string ExecutableSha256, string GitVersion,
    bool NativeQualified = false);
public sealed record GitRevisionResult(string OperationId, string SourceRepositoryRoot, string Commit,
    string Destination, IReadOnlyList<GitRevisionFile> Files, string ManifestSha256, bool NativeQualified = false);

/// <summary>Unknown means process dispatch or ref/index publication may have happened. Inspect before recovery.</summary>
public sealed class GitOutcomeUnknownException(string operationId, string? candidateCommit, Exception inner)
    : BridgeException("OutcomeUnknown", "Git operation outcome requires journal, HEAD, index and file reconciliation.", inner)
{
    public string OperationId { get; } = operationId;
    public string? CandidateCommit { get; } = candidateCommit;
}
