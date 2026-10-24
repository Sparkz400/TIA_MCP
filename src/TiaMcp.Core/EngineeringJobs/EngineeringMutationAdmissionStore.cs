using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.EngineeringJobs;

public sealed record EngineeringMutationResource(string Kind, string Path, string Effect);
public sealed record EngineeringMutationScope(string Operation, string RequestSha256, string ScopeSha256,
    string SessionEpoch, string AdapterSha256, int ProcessId, string ObservationSha256, IReadOnlyList<EngineeringMutationResource> Resources);
public sealed record EngineeringMutationAdmission(string Id, int Revision, string State, EngineeringMutationScope Scope,
    string RequestSha256, IReadOnlyList<string> ResourceKeys, string? PreviousSha256,
    DateTimeOffset RecordedAt, EngineeringArtifactReference? Response, IReadOnlyList<ResourceReconciliation> Reconciliation);

/// <summary>Durable dispatch ownership for legacy routes; never a replacement for their recovery journals or qualification.</summary>
public sealed class EngineeringMutationAdmissionStore
{
    private readonly string artifacts;
    private readonly string directory;
    private readonly string lockPath;
    public EngineeringMutationAdmissionStore(string artifactRoot)
    {
        artifacts = Path.GetFullPath(artifactRoot); PackageStorage.EnsureNoLinks(artifacts);
        directory = PackageStorage.Under(artifacts, "engineering-admissions");
        string operations = PackageStorage.Under(artifacts, "engineering-operations");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(operations);
        lockPath = Path.Combine(operations, "store.lock");
    }

    public EngineeringMutationAdmission Acquire(EngineeringMutationScope scope, string requestSha256)
    {
        scope = Freeze(scope); ValidateScope(scope); EngineeringIdentityGuard.Sha256(requestSha256, "requestSha256");
        string[] keys = ScopeKeys(scope);
        using var held = PackageStorage.Lock(lockPath);
        var owned = new EngineeringOperationStore(artifacts).PendingResourceKeysUnderLock()
            .Concat(PendingResourceKeysUnderLock(artifacts));
        if (owned.Any(a => keys.Any(b => EngineeringResourceKey.Overlaps(a, b))))
            throw new BridgeException("ResourceBusy", "An unreconciled engineering operation or mutation admission owns a requested resource.");
        string id = Guid.NewGuid().ToString("N");
        var record = new EngineeringMutationAdmission(id, 0, "dispatchIntent", scope, requestSha256, keys, null, DateTimeOffset.UtcNow, null, []);
        string staging = PackageStorage.Under(directory, ".preparing-" + id);
        Directory.CreateDirectory(staging); Write(record, staging);
        PackageStorage.PublishDirectory(staging, PackageStorage.Under(directory, id));
        return record;
    }

    public EngineeringMutationAdmission Read(string id)
    { using var held = PackageStorage.Lock(lockPath); return ReadUnderLock(artifacts, id); }

    /// <summary>When the dispatch intent was committed: no admitted write can predate it.</summary>
    public DateTimeOffset AdmittedAt(string id)
    {
        using var held = PackageStorage.Lock(lockPath); _ = ReadUnderLock(artifacts, id);
        return PackageStorage.Load<EngineeringMutationAdmission>(PackageStorage.Under(directory, id + "/00000000.json")).RecordedAt;
    }

    public EngineeringMutationAdmission Returned(string id, int revision, EngineeringArtifactReference response) =>
        Change(id, revision, current => {
            State(current, "dispatchIntent"); VerifyArtifact(artifacts, response);
            // Only the dispatch reservation ends. No readback, reopen, restoration or native qualification is asserted.
            return current with { State = "returned", Response = response };
        });

    public EngineeringMutationAdmission Unknown(string id, int revision, EngineeringArtifactReference? failure = null) =>
        Change(id, revision, current => {
            State(current, "dispatchIntent"); if (failure is not null) VerifyArtifact(artifacts, failure);
            return current with { State = "outcomeUnknown", Response = failure };
        });

    /// <summary>Trusted adapters provide independently validated evidence. This is not a public arbitrary-string reconciliation endpoint.</summary>
    public EngineeringMutationAdmission Reconcile(string id, int revision, IReadOnlyList<ResourceReconciliation> evidence) =>
        Change(id, revision, current => {
            if (current.State is not ("dispatchIntent" or "outcomeUnknown")) throw new BridgeException("InvalidOperationState", "Only an unresolved dispatch may be reconciled.");
            evidence = Freeze(evidence); ValidateReconciliation(artifacts, current.ResourceKeys, evidence);
            return current with { State = "reconciled", Reconciliation = evidence };
        });

    // MUST be called while engineering-operations/store.lock is held. Both directions of conflict admission use this scanner.
    internal static IReadOnlyList<string> PendingResourceKeysUnderLock(string artifactRoot) => RecordsUnderLock(artifactRoot)
        .Where(r => r.State is "dispatchIntent" or "outcomeUnknown").SelectMany(r => r.ResourceKeys)
        .Concat(ComponentQualificationWindowStore.PendingResourceKeysUnderLock(artifactRoot)).ToArray();

    private EngineeringMutationAdmission Change(string id, int revision, Func<EngineeringMutationAdmission, EngineeringMutationAdmission> update)
    {
        using var held = PackageStorage.Lock(lockPath); var prior = ReadUnderLock(artifacts, id);
        if (prior.Revision != revision) throw new BridgeException("RevisionConflict", "Mutation admission changed; reread it before reconciliation.");
        var next = update(prior) with { Revision = revision + 1, PreviousSha256 = Hash(prior), RecordedAt = DateTimeOffset.UtcNow };
        ValidateRecord(artifacts, prior, next); Write(next, PackageStorage.Under(directory, id)); return next;
    }

    private static IEnumerable<EngineeringMutationAdmission> RecordsUnderLock(string artifacts)
    {
        string root = PackageStorage.Under(artifacts, "engineering-admissions");
        if (!Directory.Exists(root)) yield break;
        var folders = Directory.GetDirectories(root);
        if (folders.Length > 10000) throw new BridgeException("OperationStoreLimit", "Too many retained mutation admissions.");
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(".preparing-", StringComparison.Ordinal) && name.Length == 43 && name[11..].All(char.IsAsciiHexDigit)) continue;
            yield return ReadUnderLock(artifacts, name);
        }
    }

    private static EngineeringMutationAdmission ReadUnderLock(string artifacts, string id)
    {
        PackageStorage.Id(id); string root = PackageStorage.Under(artifacts, "engineering-admissions/" + id);
        if (!Directory.Exists(root)) throw new BridgeException("OperationNotFound", "Mutation admission was not found.");
        var files = Directory.GetFiles(root, "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 16) throw new BridgeException("JournalInvalid", "Mutation admission revisions are missing or excessive.");
        EngineeringMutationAdmission? previous = null;
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetFileName(files[i]) != i.ToString("D8") + ".json" || new FileInfo(files[i]).Length > 1024 * 1024)
                throw new BridgeException("JournalInvalid", "Mutation admission revisions must be contiguous and bounded.");
            var next = PackageStorage.Load<EngineeringMutationAdmission>(files[i]);
            if (next.Id != id || next.Revision != i) throw new BridgeException("JournalInvalid", "Mutation admission revision identity differs.");
            ValidateRecord(artifacts, previous, next); previous = next;
        }
        return previous!;
    }

    private static void ValidateRecord(string artifacts, EngineeringMutationAdmission? previous, EngineeringMutationAdmission current)
    {
        ValidateScope(current.Scope); EngineeringIdentityGuard.Sha256(current.RequestSha256, "requestSha256");
        if (current.ResourceKeys is null || !ScopeKeys(current.Scope).SequenceEqual(current.ResourceKeys) || current.Reconciliation is null)
            throw new BridgeException("JournalInvalid", "Mutation resource keys differ from their exact scope.");
        bool valid = previous is null ? current.State == "dispatchIntent" && current.PreviousSha256 is null && current.Response is null && current.Reconciliation.Count == 0
            : current.PreviousSha256 == Hash(previous) && current.RequestSha256 == previous.RequestSha256 && Hash(current.Scope) == Hash(previous.Scope) &&
              current.RecordedAt >= previous.RecordedAt && (previous.State == "dispatchIntent" && current.State is "returned" or "outcomeUnknown" or "reconciled" ||
              previous.State == "outcomeUnknown" && current.State == "reconciled");
        if (!valid) throw new BridgeException("JournalInvalid", "Mutation admission transition or immutable scope changed.");
        if (current.Response is not null) VerifyArtifact(artifacts, current.Response);
        if (current.State == "returned" && current.Response is null) throw new BridgeException("JournalInvalid", "Returned dispatch needs its exact retained response.");
        if (current.State == "reconciled") ValidateReconciliation(artifacts, current.ResourceKeys, current.Reconciliation);
        else if (current.Reconciliation.Count != 0) throw new BridgeException("JournalInvalid", "Unreconciled admission contains reconciliation claims.");
        if (previous is not null && current.State == "reconciled" && Hash(current.Response) != Hash(previous.Response))
            throw new BridgeException("JournalInvalid", "Reconciliation cannot replace the retained dispatch outcome.");
    }

    private static void ValidateScope(EngineeringMutationScope scope)
    {
        if (scope is null || !IsMutation(scope.Operation) || string.IsNullOrWhiteSpace(scope.SessionEpoch) || scope.SessionEpoch.Length > 128 || scope.ProcessId <= 0 ||
            scope.Resources is null || scope.Resources.Count is < 1 or > 64 || scope.Resources.Any(r => r is null))
            throw new BridgeException("InvalidMutationScope", "Exact native mutation scope, session, process and bounded resources are required.");
        EngineeringIdentityGuard.Sha256(scope.RequestSha256, "nativeRequestSha256"); EngineeringIdentityGuard.Sha256(scope.ScopeSha256, "nativeScopeSha256");
        EngineeringIdentityGuard.Sha256(scope.ObservationSha256, "nativeObservationSha256");
        EngineeringIdentityGuard.Sha256(scope.AdapterSha256, "adapterSha256");
        foreach (var resource in scope.Resources)
        {
            _ = EngineeringResourceKey.ForPath(resource.Kind, resource.Path);
            if (string.IsNullOrWhiteSpace(resource.Effect) || resource.Effect.Length > 256 || resource.Effect.Any(char.IsControl))
                throw new BridgeException("InvalidMutationScope", "Every resource needs an exact bounded effect.");
        }
        var keys = ScopeKeys(scope);
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new BridgeException("InvalidMutationScope", "Duplicate resource keys are refused.");
    }

    public static bool IsMutation(string operation) => operation is "change_apply" or "change_restore" or "simulation_download" or
        "simulation_provision_scratch_credential" or "library_open" or "scratch_create" or "project_retrieve" or "watch_fixture_seed" or "watch_authoring_source_negative";
    private static string[] ScopeKeys(EngineeringMutationScope scope) => scope.Resources.Select(r => EngineeringResourceKey.ForPath(r.Kind, r.Path)).Order(StringComparer.Ordinal).ToArray();
    private static void State(EngineeringMutationAdmission record, string expected)
    { if (record.State != expected) throw new BridgeException("RecoveryRequired", "An attempted native dispatch is never replayed."); }
    private static void ValidateReconciliation(string artifacts, IReadOnlyList<string> keys, IReadOnlyList<ResourceReconciliation> evidence)
    {
        if (evidence is null || evidence.Count != keys.Count || evidence.Any(e => e is null) ||
            !evidence.Select(e => e.ResourceKey).Order(StringComparer.Ordinal).SequenceEqual(keys))
            throw new BridgeException("ReconciliationIncomplete", "Every admitted resource needs separate independent reconciliation evidence.");
        foreach (var item in evidence)
        {
            if (item.Disposition is not ("unchanged" or "verifiedApplied") || item.RecoveryLocation is not null)
                throw new BridgeException("ReconciliationIncomplete", "This dispatch-only adapter cannot assert restoration or partial cleanup.");
            VerifyArtifact(artifacts, item.Evidence);
        }
    }
    private static void VerifyArtifact(string artifacts, EngineeringArtifactReference reference)
    {
        if (reference is null) throw new BridgeException("ArtifactInvalid", "Evidence is required.");
        EngineeringIdentityGuard.Sha256(reference.Sha256, "evidenceSha256");
        string path = PackageStorage.Under(artifacts, PackageStorage.Relative(reference.RelativePath));
        if (!File.Exists(path) || PackageStorage.FileHash(path) != reference.Sha256) throw new BridgeException("ArtifactChanged", "Mutation admission evidence is missing or changed.");
    }
    private static T Freeze<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, BridgeProtocol.Json), BridgeProtocol.Json)!;
    private static string Hash<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json) ?? JsonValue.Create("null")!);
    private static void Write(EngineeringMutationAdmission record, string directory)
    {
        string path = PackageStorage.Under(directory, record.Revision.ToString("D8") + ".json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, record, BridgeProtocol.Json); stream.Flush(true); }
        File.Move(temporary, path, false);
    }
}
