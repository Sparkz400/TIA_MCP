using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.VersionControl;

public sealed record EngineeringGitCallback(string EngineeringOperationId, int Sequence, string BindingSha256,
    string? PreviousSha256, GitOperationRecord Record);

/// <summary>One admitted invocation. Reopening a journal never replays a process or releases resources.</summary>
public sealed class EngineeringGitOperationJournal : IGitOperationJournal
{
    private readonly EngineeringOperationStore store;
    private readonly string root;
    private readonly string directory;
    private readonly GitOperationBinding binding;
    private readonly Func<NativeTargetObservation> reacquire;
    private readonly object gate = new();
    private EngineeringOperationRecord operation;
    private EngineeringGitCallback? latest;
    private EngineeringArtifactReference? latestArtifact;

    public EngineeringGitOperationJournal(string artifactRoot, string operationId, int expectedRevision,
        GitOperationBinding binding, Func<NativeTargetObservation> reacquire)
    {
        root = Path.GetFullPath(artifactRoot); PackageStorage.EnsureNoLinks(root);
        store = new(root); operation = store.Read(operationId);
        this.binding = binding; this.reacquire = reacquire ?? throw new ArgumentNullException(nameof(reacquire));
        if (operation.Revision != expectedRevision || operation.State != "ready")
            throw new BridgeException("RecoveryRequired", "Only the exact ready operation can own one Git invocation; reconcile existing intent without replay.");
        if (operation.Plan.Operation != "vci.git." + binding.Operation ||
            !operation.Plan.Inputs.Any(a => a.Sha256 == binding.Sha256) ||
            !operation.Plan.RequiredGates.Contains("git.callbacks") || !operation.Plan.RequiredGates.Contains("git.result"))
            throw new BridgeException("GitJournalBindingMismatch", "Shared plan must retain the exact Git binding and callback/result gates.");
        foreach (string path in new[] { binding.RepositoryRoot, binding.ControlDirectory }.Concat(binding.Destination is null ? [] : new[] { binding.Destination }))
            if (!operation.Plan.Resources.Any(r => r.ResourceKey == EngineeringResourceKey.ForPath("workspace", path)))
                throw new BridgeException("GitJournalBindingMismatch", "Every Git resource must be durably acquired and checkpointed.");
        directory = PackageStorage.Under(root, "git-callbacks/" + PackageStorage.Id(operationId));
        using var held = PackageStorage.Lock(PackageStorage.Under(root, "git-callback-admission.lock"));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new BridgeException("RecoveryRequired", "A Git invocation already owns this operation, including an interrupted pre-dispatch callback.");
        Directory.CreateDirectory(directory);
    }

    public Task RecordAsync(GitOperationRecord record, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = store.Read(operation.Id);
            if (current.Revision != operation.Revision || current.State != operation.State)
                throw new BridgeException("RecoveryRequired", "The shared journal changed; do not continue the Git invocation.");
            if (record.BindingSha256 != binding.Sha256 || record.Operation != binding.Operation ||
                record.RepositoryRoot != binding.RepositoryRoot || (latest is not null && latest.Record.OperationId != record.OperationId))
                throw new BridgeException("GitJournalBindingMismatch", "Git callback differs from the retained exact intent.");
            PackageStorage.Id(record.OperationId);
            string? previousPhase = latest?.Record.Phase;
            bool allowed = record.Phase switch
            {
                "dispatchPrepared" => latest is null && operation.State == "ready",
                "candidateRecorded" => binding.Operation == "commit" && previousPhase == "dispatchPrepared",
                "publicationPrepared" => binding.Operation == "commit" && previousPhase == "candidateRecorded",
                "verified" => previousPhase == (binding.Operation == "commit" ? "publicationPrepared" : "dispatchPrepared"),
                "outcomeUnknown" => previousPhase is "dispatchPrepared" or "candidateRecorded" or "publicationPrepared" or "verified",
                _ => false
            };
            if (!allowed) throw new BridgeException("GitJournalSequence", "Callback order cannot authorize repeated dispatch or publication.");
            var callback = new EngineeringGitCallback(operation.Id, (latest?.Sequence ?? 0) + 1,
                binding.Sha256, latestArtifact?.Sha256, record);
            string relative = "git-callbacks/" + operation.Id + "/" + callback.Sequence.ToString("D4") + ".json";
            string path = PackageStorage.Under(root, relative);
            if (File.Exists(path)) throw new BridgeException("RecoveryRequired", "The callback already exists; reconcile without replay.");
            PackageStorage.Save(path, callback); // Flush before dispatch/publication is allowed to return.
            latest = callback; latestArtifact = new(relative, PackageStorage.FileHash(path));
            if (record.Phase == "dispatchPrepared") operation = store.Dispatch(operation.Id, operation.Revision, reacquire);
            else if (record.Phase == "outcomeUnknown") operation = store.MarkUnknown(operation.Id, operation.Revision);
        }
        return Task.CompletedTask;
    }

    /// <summary>Domain callers supply validated result, preservation and every other planned gate. Native acceptance is separate.</summary>
    public EngineeringOperationRecord Complete(IReadOnlyList<EngineeringGateEvidence> evidence)
    {
        lock (gate)
        {
            if (latest?.Record.Phase != "verified" || latestArtifact is null || operation.State != "dispatchIntent")
                throw new BridgeException("VerificationIncomplete", "A verified callback and all explicit caller gates are required.");
            var verified = ReadCallbacks(root, operation.Id);
            if (verified.Count != latest.Sequence || GitWorkspaceSnapshot.Digest(verified[^1]) != GitWorkspaceSnapshot.Digest(latest))
                throw new BridgeException("GitJournalInvalid", "Retained callbacks changed before completion.");
            operation = store.RecordOutcome(operation.Id, operation.Revision, true,
                [new("git.callbacks", latestArtifact), .. evidence]);
            return operation;
        }
    }

    /// <summary>Diagnostic read only; in-flight intent remains owned until explicit shared-store reconciliation.</summary>
    public static IReadOnlyList<EngineeringGitCallback> ReadCallbacks(string artifactRoot, string operationId)
    {
        string directory = PackageStorage.Under(Path.GetFullPath(artifactRoot), "git-callbacks/" + PackageStorage.Id(operationId));
        var results = new List<EngineeringGitCallback>(); string? previous = null;
        foreach (string path in Directory.EnumerateFiles(directory, "*.json").Take(6).Order(StringComparer.Ordinal))
        {
            if (results.Count >= 5) throw new BridgeException("GitJournalInvalid", "Callback count exceeds one bounded invocation.");
            var item = PackageStorage.Load<EngineeringGitCallback>(path);
            if (item.EngineeringOperationId != operationId || item.Sequence != results.Count + 1 ||
                Path.GetFileName(path) != item.Sequence.ToString("D4") + ".json" || item.PreviousSha256 != previous ||
                item.BindingSha256 != item.Record.BindingSha256 ||
                (results.Count > 0 && (item.BindingSha256 != results[0].BindingSha256 || item.Record.OperationId != results[0].Record.OperationId)))
                throw new BridgeException("GitJournalInvalid", "Callback identity, order or hash chain is invalid.");
            previous = PackageStorage.FileHash(path); results.Add(item);
        }
        return results.AsReadOnly();
    }
}
