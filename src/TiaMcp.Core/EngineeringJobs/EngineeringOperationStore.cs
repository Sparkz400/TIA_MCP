using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.EngineeringJobs;

/// <summary>
/// Shared write-ahead admission substrate. Uses the existing PackageStorage lock/path/flush helpers.
/// It never calls or replays native operations and never steals a reservation on elapsed time.
/// Callers still hold the worker's native exclusive access across Dispatch and actual invocation.
/// </summary>
public sealed class EngineeringOperationStore
{
    private readonly string artifacts;
    private readonly string directory;
    private readonly string archive;
    private static readonly string[] Terminal = ["succeeded", "reconciled", "stopped"];
    // Every scan reads each record left in place, so once a store holds this many, records that finish move into its .archive folder.
    // An older Host's scan refuses that folder (InvalidRequest) instead of silently missing archived keys.
    internal static int ArchiveAbove = 1000;
    public EngineeringOperationStore(string artifactRoot)
    {
        artifacts = Path.GetFullPath(artifactRoot);
        PackageStorage.EnsureNoLinks(artifacts);
        directory = PackageStorage.Under(artifacts, "engineering-operations");
        archive = PackageStorage.Under(directory, ".archive");
        Directory.CreateDirectory(directory);
    }

    // Best effort once the finished revision is durable: a record left in place stays valid and only counts toward the store bound.
    // A failed index write (beforeMove) skips the move, so an archived operation is always findable by its key.
    internal static void Archive(string record, string destination, Action? beforeMove = null)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); beforeMove?.Invoke(); Directory.Move(record, destination); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BridgeException) { }
    }

    public EngineeringOperationRecord Prepare(EngineeringOperationPlan plan, string idempotencyKey)
    {
        // Validate exact selectors before JSON can replace malformed Unicode, then freeze caller-owned arrays.
        ValidatePlan(plan);
        plan = Freeze(plan);
        ValidatePlan(plan); Text(idempotencyKey);
        using var held = Lock();
        VerifyArtifacts(plan.Inputs);
        string hash = Hash(plan);
        var prior = Records().SingleOrDefault(r => r.IdempotencyKey == idempotencyKey);
        prior = prior is null ? null : ReadLocked(prior.Id); // The scan may have read it without filesystem checks.
        if (prior is null && File.Exists(Marker(idempotencyKey))) prior = ReadLocked(PackageStorage.Load<string>(Marker(idempotencyKey)));
        if (prior is not null)
        {
            if (prior.PlanSha256 != hash || prior.IdempotencyKey != idempotencyKey) Fail("IdempotencyConflict", "The key belongs to a different immutable plan.");
            return prior;
        }
        string id = Guid.NewGuid().ToString("N");
        string staging = PackageStorage.Under(directory, ".preparing-" + id);
        Directory.CreateDirectory(staging);
        var record = new EngineeringOperationRecord(id, 0, "prepared", idempotencyKey, hash, plan, null,
            DateTimeOffset.UtcNow, [], [], []);
        Write(record, staging);
        PackageStorage.PublishDirectory(staging, PackageStorage.Under(directory, id));
        return record;
    }

    public EngineeringOperationRecord Read(string id)
    {
        using var held = Lock(); return ReadLocked(id);
    }
    public IReadOnlyList<EngineeringOperationRecord> Pending()
    {
        using var held = Lock(); return Records().Where(HoldsResources).ToArray();
    }
    internal IReadOnlyList<string> PendingResourceKeysUnderLock() => Records().Where(HoldsResources).SelectMany(ReservedKeys).ToArray();

    public EngineeringOperationRecord Acquire(string id, int expectedRevision,
        Action<EngineeringOperationRecord>? verifyCredentials = null) => Change(id, expectedRevision, r =>
    {
        State(r, "prepared");
        Credentials(r, verifyCredentials);
        if (EngineeringMutationAdmissionStore.PendingResourceKeysUnderLock(artifacts).Any(x => r.Plan.Resources.Any(y => EngineeringResourceKey.Overlaps(x, y.ResourceKey))) ||
            Records().Any(other => other.Id != id && HoldsResources(other) &&
            ReservedKeys(other).Any(x => r.Plan.Resources.Any(y => EngineeringResourceKey.Overlaps(x, y.ResourceKey)))))
            Fail("ResourceBusy", "An unreconciled durable operation owns a requested resource.");
        return r with { State = "acquired" };
    });

    // Persist before any checkpoint save/archive call, since that call can itself mutate a resource.
    public EngineeringOperationRecord BeginCheckpoint(string id, int expectedRevision,
        Func<NativeTargetObservation> reacquire, Action<EngineeringOperationRecord>? verifyCredentials = null) => Change(id, expectedRevision, r =>
    {
        State(r, "acquired"); Credentials(r, verifyCredentials); Fresh(r, reacquire); return r with { State = "checkpointIntent" };
    });

    public EngineeringOperationRecord Checkpoint(string id, int expectedRevision,
        IReadOnlyList<EngineeringArtifactReference> checkpoints) => Change(id, expectedRevision, r =>
    {
        State(r, "checkpointIntent");
        if (checkpoints is null || checkpoints.Count != r.Plan.Resources.Count || checkpoints.Count > 256)
            Fail("CheckpointRequired", "One ordered retained checkpoint per planned resource is required.");
        foreach (var checkpoint in checkpoints!) ValidateArtifact(checkpoint);
        checkpoints = Freeze(checkpoints);
        VerifyArtifacts(checkpoints!); return r with { State = "ready", Checkpoints = checkpoints!.ToArray() };
    });

    public EngineeringOperationRecord Dispatch(string id, int expectedRevision,
        Func<NativeTargetObservation> reacquire, Action<EngineeringOperationRecord>? verifyCredentials = null) => Change(id, expectedRevision, r =>
    {
        State(r, "ready"); Credentials(r, verifyCredentials); VerifyArtifacts(r.Checkpoints); Fresh(r, reacquire);
        return r with { State = "dispatchIntent" };
    });

    public EngineeringOperationRecord RecordOutcome(string id, int expectedRevision, bool succeeded,
        IReadOnlyList<EngineeringGateEvidence> evidence) => Change(id, expectedRevision, r =>
    {
        State(r, "dispatchIntent"); ValidateEvidence(evidence);
        evidence = r.Evidence.Concat(Freeze(evidence)).ToArray(); ValidateEvidence(evidence);
        if (!succeeded && !evidence.Any(e => e.Gate == "failure")) Fail("FailureEvidenceRequired", "Retain original failure diagnostics before recording a failed outcome.");
        if (succeeded && r.Plan.RequiredGates.Any(g => !evidence.Any(e => e.Gate == g)))
            Fail("VerificationIncomplete", "Every required compile/readback/preservation/reopen/cleanup gate needs retained evidence.");
        return r with { State = succeeded ? "succeeded" : "recoveryRequired", Evidence = evidence.ToArray() };
    });

    /// <summary>Retain returned native results without releasing ownership or authorizing another dispatch.</summary>
    public EngineeringOperationRecord RecordDispatchEvidence(string id, int expectedRevision, EngineeringGateEvidence evidence) => Change(id, expectedRevision, r =>
    {
        State(r, "dispatchIntent");
        if (evidence is null || evidence.Gate?.StartsWith("dispatch.", StringComparison.Ordinal) != true)
            Fail("VerificationIncomplete", "Pending dispatch evidence requires a dispatch.* gate.");
        ValidateEvidence([evidence!]); evidence = Freeze(evidence);
        if (r.Evidence.Any(e => e.Gate == evidence!.Gate)) Fail("EvidenceAlreadyRecorded", "Returned native evidence is immutable.");
        var retained = r.Evidence.Concat([evidence!]).ToArray(); ValidateEvidence(retained);
        return r with { Evidence = retained };
    });

    public EngineeringOperationRecord MarkUnknown(string id, int expectedRevision, EngineeringArtifactReference? failure = null) => Change(id, expectedRevision, r =>
    {
        if (r.State is not ("acquired" or "ready" or "dispatchIntent" or "checkpointIntent" or "recoveryIntent")) Fail("InvalidOperationState", "Only acquired ownership or in-flight intent can have an unknown outcome.");
        if (failure is not null) ValidateArtifact(failure);
        var evidence = failure is null ? r.Evidence : r.Evidence.Concat([new EngineeringGateEvidence("interruption." + r.Revision, Freeze(failure))]).ToArray();
        ValidateEvidence(evidence);
        return r with { State = "outcomeUnknown", Evidence = evidence };
    });
    public EngineeringOperationRecord RecordRecoveryEvidence(string id, int expectedRevision, EngineeringGateEvidence evidence) => Change(id, expectedRevision, r =>
    {
        if (r.State is not ("recoveryIntent" or "outcomeUnknown") || r.RecoveryDestinations.Count == 0 && !r.Plan.Resources.Any(x => x.Kind == "external"))
            Fail("RecoveryRequired", "Evidence-only reconciliation requires an admitted local recovery or an unresolved external resource; it never replays restoration.");
        ValidateEvidence([evidence]);
        if (r.Evidence.Any(e => e.Gate == evidence.Gate)) Fail("EvidenceAlreadyRecorded", "Recovery observations are immutable and cannot be replaced.");
        return r with { Evidence = r.Evidence.Concat([Freeze(evidence)]).ToArray() };
    });

    public EngineeringOperationRecord Stop(string id, int expectedRevision) => Change(id, expectedRevision, r =>
    {
        if (r.State is not ("prepared" or "acquired")) Fail("RecoveryRequired", "After checkpoint intent, stop is not proof of cancellation or cleanup.");
        return r with { State = "stopped" };
    });

    public EngineeringOperationRecord Reconcile(string id, int expectedRevision,
        IReadOnlyList<ResourceReconciliation> evidence, IReadOnlyList<EngineeringRecoveryResolution>? recoveryEvidence = null) => Change(id, expectedRevision, r =>
    {
        if (r.State is not ("outcomeUnknown" or "recoveryRequired" or "dispatchIntent" or "checkpointIntent" or "ready" or "recoveryIntent"))
            Fail("InvalidOperationState", "Reconciliation requires an attempted checkpoint or dispatch.");
        ValidateReconciliation(r, r.Revision, evidence, r.RecoveryDestinations, recoveryEvidence ?? []);
        evidence = Freeze(evidence);
        recoveryEvidence = Freeze(recoveryEvidence ?? []);
        ValidateReconciliation(r, r.Revision, evidence, r.RecoveryDestinations, recoveryEvidence);
        return r with { State = "reconciled", Reconciliation = evidence!.ToArray(), RecoveryResolutions = recoveryEvidence };
    });

    /// <summary>Persist exact new destination ownership before calling an external restore API. Never replay it.</summary>
    public EngineeringOperationRecord ReserveRecovery(string id, int expectedRevision,
        IReadOnlyList<EngineeringRecoveryDestination> destinations, Action<EngineeringOperationRecord>? verifyCredentials = null,
        NativeTargetObservation? recoveryTarget = null, Func<NativeTargetObservation>? reacquireRecovery = null) => Change(id, expectedRevision, r =>
    {
        if (r.State is not ("outcomeUnknown" or "recoveryRequired" or "ready" or "succeeded") || r.RecoveryDestinations.Count != 0 || r.RecoveryTarget is not null)
            Fail("RecoveryRequired", "Reconcile earlier intent before preparing a separate recovery attempt.");
        if (r.Plan.Credentials is not null && r.State == "succeeded")
            Fail("RecoveryRequired", "A terminal credential-bearing operation requires a new plan and credential admission.");
        Credentials(r, verifyCredentials);
        if (r.Plan.Credentials is not null)
        {
            if (recoveryTarget is null || reacquireRecovery is null || !r.Plan.Credentials.Any(c => c.UsePhase == "recovery"))
                Fail("RecoveryObservationRequired", "Credential recovery requires an explicit reviewed target, fresh reacquisition and recovery slots.");
            NativeTargetGuard.EnsureRecoveryTarget(r.Plan.Target, recoveryTarget!);
            var reviewed = Freeze(recoveryTarget!);
            var observed = reacquireRecovery!();
            NativeTargetGuard.EnsureFresh(reviewed, observed, r.Plan.RequiredProbes);
            recoveryTarget = Freeze(observed);
            ValidateRecoveryTarget(r.Plan, recoveryTarget);
        }
        else if (recoveryTarget is not null || reacquireRecovery is not null)
            Fail("InvalidRecovery", "Credential recovery context requires an explicit credential-bearing plan.");
        ValidateDestinations(r.Plan, destinations, recoveryTarget is not null);
        destinations = Freeze(destinations);
        ValidateDestinations(r.Plan, destinations, recoveryTarget is not null);
        if (r.Checkpoints.Count != r.Plan.Resources.Count)
            Fail("CheckpointRequired", "Recovery requires a retained checkpoint for every planned resource.");
        // Journal replay verifies every external baseline; only selected local restores need their archive bytes here.
        VerifyArtifacts(destinations.Select(d => r.Checkpoints[r.Plan.Resources.Select((resource, index) => (resource, index)).Single(x => x.resource.ResourceKey == d.ResourceKey).index]));
        var keys = destinations.Select(d => EngineeringResourceKey.ForPath(r.Plan.Resources.Single(x => x.ResourceKey == d.ResourceKey).Kind, d.RecoveryLocation)).ToArray();
        if (EngineeringMutationAdmissionStore.PendingResourceKeysUnderLock(artifacts).Any(a => keys.Concat(r.Plan.Resources.Select(x => x.ResourceKey)).Any(b => EngineeringResourceKey.Overlaps(a, b))) ||
            Records().Any(other => other.Id != id && HoldsResources(other) && ReservedKeys(other).Any(a => keys.Concat(r.Plan.Resources.Select(x => x.ResourceKey)).Any(b => EngineeringResourceKey.Overlaps(a, b)))))
            Fail("ResourceBusy", "A recovery destination is owned by another operation.");
        return r with { State = "recoveryIntent", RecoveryDestinations = destinations, RecoveryTarget = recoveryTarget };
    });

    private static void ValidateRecoveryTarget(EngineeringOperationPlan plan, NativeTargetObservation target, bool live = true)
    {
        if (plan.Credentials?.Any(c => c.UsePhase == "recovery") != true)
            Fail("JournalInvalid", "Recovery context requires explicit recovery credentials.");
        if (!live) return;
        NativeTargetGuard.EnsureRecoveryTarget(plan.Target, target);
        NativeTargetGuard.EnsureFresh(target, target, plan.RequiredProbes);
    }

    private static void ValidateDestinations(EngineeringOperationPlan plan, IReadOnlyList<EngineeringRecoveryDestination> destinations, bool externalRecovery = false, bool live = true)
    {
        if (destinations is null || destinations.Count > 64 || destinations.Count == 0 && (!externalRecovery || !plan.Resources.Any(r => r.Kind == "external")) || destinations.Any(d => d is null) ||
            destinations.Select(d => d.ResourceKey).Distinct(StringComparer.Ordinal).Count() != destinations.Count)
            Fail("InvalidRecovery", "Recovery destinations must be bounded and unique.");
        var keys = new List<string>();
        foreach (var destination in destinations!)
        {
            Text(destination.ResourceKey); Text(destination.RecoveryLocation);
            var resource = plan.Resources.SingleOrDefault(r => r.ResourceKey == destination.ResourceKey);
            if (resource is null || resource.Kind == "external" || resource.RecoveryStrategy == "reconcileExternalCommit") Fail("InvalidRecovery", "Only a planned local resource can be restored to a new location.");
            if (!live) continue;
            string key = EngineeringResourceKey.ForPath(resource!.Kind, destination.RecoveryLocation);
            if (plan.Resources.Any(r => EngineeringResourceKey.Overlaps(r.ResourceKey, key)) || keys.Any(k => EngineeringResourceKey.Overlaps(k, key)))
                Fail("InvalidRecovery", "Recovery destinations cannot overlap original resources or each other.");
            keys.Add(key);
        }
    }

    private void ValidateReconciliation(EngineeringOperationRecord record, int revision, IReadOnlyList<ResourceReconciliation> evidence,
        IReadOnlyList<EngineeringRecoveryDestination> admittedDestinations, IReadOnlyList<EngineeringRecoveryResolution> recoveryEvidence, bool live = true)
    {
        var plan = record.Plan;
        if (recoveryEvidence is null || recoveryEvidence.Count != admittedDestinations.Count || recoveryEvidence.Any(e => e is null) ||
            recoveryEvidence.Select(e => e.ResourceKey).Distinct(StringComparer.Ordinal).Count() != recoveryEvidence.Count)
            Fail("ReconciliationIncomplete", "Every admitted recovery destination needs its own outcome/cleanup evidence.");
        foreach (var admitted in admittedDestinations)
        {
            var resolution = recoveryEvidence.SingleOrDefault(e => e.ResourceKey == admitted.ResourceKey);
            if (resolution is not null) Text(resolution.RecoveryLocation);
            string kind = plan.Resources.Single(r => r.ResourceKey == admitted.ResourceKey).Kind;
            if (resolution is null || resolution.Disposition is not ("verifiedRestored" or "verifiedAbsent" or "retainedPartial") ||
                live && EngineeringResourceKey.ForPath(kind, resolution.RecoveryLocation) != EngineeringResourceKey.ForPath(kind, admitted.RecoveryLocation))
                Fail("ReconciliationIncomplete", "Recovery destination identity or disposition is unresolved.");
            VerifyArtifacts([resolution!.Evidence], live);
        }
        if (evidence is null || evidence.Count != plan.Resources.Count || evidence.Any(e => e is null) ||
            evidence.Select(e => e.ResourceKey).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
            Fail("ReconciliationIncomplete", "Each affected resource needs independent reconciliation.");
        var destinations = new List<string>();
        foreach (var resource in plan.Resources)
        {
            var item = evidence!.SingleOrDefault(e => e.ResourceKey == resource.ResourceKey);
            if (item is null || item.Disposition is not ("unchanged" or "verifiedApplied" or "restoredToNewLocation" or "externalCommitRetained" or "externalBaselineObserved" or "externalDesiredStateObserved"))
                Fail("ReconciliationIncomplete", "A resource is missing or remains unresolved.");
            if (resource.Kind == "external")
            {
                if (item!.Disposition is not ("externalBaselineObserved" or "externalDesiredStateObserved") || item.RecoveryLocation is not null)
                    Fail("InvalidRecovery", "External resources require their own state readback, never a project-copy or external-commit substitute.");
                VerifyArtifacts([item.Evidence], live);
                if (live) EngineeringExternalEvidence.Verify(artifacts, record, revision, resource, "reconciliation",
                    item.Disposition == "externalDesiredStateObserved" ? resource.External!.After : resource.External!.Before, item.Evidence);
                continue;
            }
            if (item!.Disposition is "externalBaselineObserved" or "externalDesiredStateObserved") Fail("InvalidRecovery", "External state evidence cannot discharge a local restore obligation.");
            if (item!.Disposition == "restoredToNewLocation")
            {
                Text(item.RecoveryLocation!);
                if (resource.RecoveryStrategy == "reconcileExternalCommit") Fail("InvalidRecovery", "Restore must use a distinct location and cannot undo an external commit.");
                if (live)
                {
                    string destination = EngineeringResourceKey.ForPath(resource.Kind, item.RecoveryLocation!);
                    if (plan.Resources.Any(r => EngineeringResourceKey.Overlaps(destination, r.ResourceKey)) || destinations.Any(d => EngineeringResourceKey.Overlaps(destination, d)))
                        Fail("InvalidRecovery", "Restore must use a distinct location and cannot undo an external commit.");
                    destinations.Add(destination);
                    if (!admittedDestinations.Any(d => d.ResourceKey == resource.ResourceKey && EngineeringResourceKey.ForPath(resource.Kind, d.RecoveryLocation) == destination))
                        Fail("RecoveryAdmissionRequired", "A restore result must bind its previously reserved destination.");
                }
                if (!recoveryEvidence.Any(e => e.ResourceKey == resource.ResourceKey && e.Disposition == "verifiedRestored"))
                    Fail("ReconciliationIncomplete", "A restored resource requires verified recovered-copy evidence.");
            }
            else if (item.RecoveryLocation is not null) Fail("InvalidRecovery", "Only a restored resource has a recovery location.");
            if (item.Disposition == "externalCommitRetained" && resource.RecoveryStrategy != "reconcileExternalCommit")
                Fail("InvalidRecovery", "External-commit evidence cannot discharge a local restore obligation.");
            VerifyArtifacts([item.Evidence], live);
        }
    }

    private EngineeringOperationRecord Change(string id, int revision, Func<EngineeringOperationRecord, EngineeringOperationRecord> change)
    {
        using var held = Lock();
        var previous = ReadLocked(id);
        if (previous.Revision != revision) Fail("RevisionConflict", "The durable operation changed; reread before proceeding.");
        var next = change(previous) with { Revision = revision + 1, PreviousSha256 = Hash(previous), RecordedAt = DateTimeOffset.UtcNow };
        ValidateTransition(previous, next);
        string folder = Folder(id), active = PackageStorage.Under(directory, id);
        // An archived success that reserves recovery must be visible to scans before that revision is durable.
        if (folder != active && !Terminal.Contains(next.State))
        {
            try { Directory.Move(folder, active); folder = active; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new BridgeException("OperationStoreBusy", "The archived operation could not return to the active store; nothing was recorded. Retry the same request.", error); }
        }
        Write(next, folder);
        if (folder == active && Terminal.Contains(next.State) && Directory.GetDirectories(directory).Length > ArchiveAbove)
            Archive(active, PackageStorage.Under(archive, id), () => PackageStorage.Save(Marker(next.IdempotencyKey), id)); // Prepare still finds the key.
        return next;
    }

    private void Fresh(EngineeringOperationRecord r, Func<NativeTargetObservation> reacquire)
    {
        VerifyArtifacts(r.Plan.Inputs);
        NativeTargetGuard.EnsureFresh(r.Plan.Target, reacquire(), r.Plan.RequiredProbes);
    }

    private void VerifyArtifacts(IEnumerable<EngineeringArtifactReference> values, bool live = true)
    {
        foreach (var value in values)
        {
            ValidateArtifact(value);
            if (!live) continue;
            string path = PackageStorage.Under(artifacts, PackageStorage.Relative(value.RelativePath));
            if (!File.Exists(path) || PackageStorage.FileHash(path) != value.Sha256)
                Fail("ArtifactChanged", "A retained input/checkpoint/evidence artifact is missing or changed.");
        }
    }

    private static void ValidateArtifact(EngineeringArtifactReference value)
    {
        if (value is null) Fail("ArtifactInvalid", "Missing artifact reference.");
        Text(value!.RelativePath); EngineeringIdentityGuard.Sha256(value.Sha256, "artifactSha256");
    }

    private void ValidateEvidence(IReadOnlyList<EngineeringGateEvidence> evidence, bool live = true)
    {
        if (evidence is null || evidence.Count > 256 || evidence.Any(e => e is null) || evidence.Select(e => e.Gate).Distinct(StringComparer.Ordinal).Count() != evidence.Count)
            Fail("VerificationIncomplete", "Evidence gates must be bounded and unique.");
        foreach (var entry in evidence!) Text(entry.Gate);
        VerifyArtifacts(evidence!.Select(e => e.Artifact), live);
    }

    private static void ValidatePlan(EngineeringOperationPlan plan, bool live = true)
    {
        if (plan is null) Fail("InvalidOperationPlan", "An immutable plan is required.");
        Text(plan!.Operation); NativeTargetGuard.Validate(plan.Target, live);
        if (plan.Resources is null || plan.Resources.Count is < 1 or > 64 || plan.Resources.Any(r => r is null) ||
            plan.Resources.Select(r => r.ResourceKey).Distinct(StringComparer.Ordinal).Count() != plan.Resources.Count ||
            plan.Inputs is null || plan.Inputs.Count is < 1 or > 256 || plan.RequiredProbes is null || plan.RequiredProbes.Count > 256 ||
            plan.RequiredGates is null || plan.RequiredGates.Count is < 1 or > 256 ||
            plan.RequiredGates.Distinct(StringComparer.Ordinal).Count() != plan.RequiredGates.Count)
            Fail("InvalidOperationPlan", "Resources, artifacts and verification obligations must be explicit, unique and bounded.");
        foreach (var r in plan.Resources!)
        {
            Text(r.ResourceKey); Text(r.Kind); Text(r.Effect);
            EngineeringResourceKey.Validate(r, live);
            if (r.RecoveryStrategy is not ("projectArchiveToNewLocation" or "libraryCopyToNewLocation" or "immutableWorkspaceVersion" or "reconcileExternalCommit" or "externalStateReadback"))
                Fail("InvalidOperationPlan", "Unknown resource recovery strategy.");
            if (r.Kind == "project" && r.RecoveryStrategy != "projectArchiveToNewLocation" ||
                r.Kind == "library" && r.RecoveryStrategy != "libraryCopyToNewLocation" ||
                r.Kind == "workspace" && r.RecoveryStrategy is not ("immutableWorkspaceVersion" or "reconcileExternalCommit") ||
                r.Kind == "external" && r.RecoveryStrategy != "externalStateReadback")
                Fail("InvalidOperationPlan", "The recovery strategy must match its resource kind.");
            if (r.Kind == "external" && !plan.RequiredGates.Contains(EngineeringExternalEvidence.OutcomeGate(r.ResourceKey), StringComparer.Ordinal))
                Fail("InvalidOperationPlan", "Every external resource needs its own required readback gate.");
        }
        if (live && !plan.Resources!.Any(r => r.ResourceKey == EngineeringResourceKey.ForPath("project", plan.Target.ProjectFilePath)))
            Fail("InvalidOperationPlan", "The exact target project must be reserved in the effect set.");
        foreach (var name in plan.RequiredProbes!.Concat(plan.RequiredGates!)) Text(name);
        foreach (var input in plan.Inputs!) ValidateArtifact(input);
        Security.ProtectedCredentialGuard.ValidateRequirements(plan);
    }

    // Trusted adapter callback, like native reacquisition; it must verify durable pins while holding its vault lock.
    private static void Credentials(EngineeringOperationRecord record, Action<EngineeringOperationRecord>? verify)
    {
        if (record.Plan.Credentials is null) return;
        if (verify is null) throw Security.ProtectedCredentialGuard.Error("CredentialAdmissionRequired");
        verify(Freeze(record));
    }

    private IEnumerable<EngineeringOperationRecord> Records()
    {
        var paths = Directory.GetDirectories(directory);
        if (paths.Length > 10000) Fail("OperationStoreLimit", "Archive reconciled records before adding operations.");
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            // A staged prepare never authorizes checkpoint/dispatch and is not a committed operation.
            if (name.StartsWith(".preparing-", StringComparison.Ordinal) && name.Length == 43 && name[11..].All(char.IsAsciiHexDigit) || name == ".archive") continue;
            yield return ReadLocked(name, scan: true);
        }
    }

    // A finished record may have moved into .archive; ids are unique across both.
    private string Folder(string id)
    {
        string active = PackageStorage.Under(directory, id);
        return Directory.Exists(active) ? active : PackageStorage.Under(archive, id);
    }
    private string Marker(string idempotencyKey) =>
        PackageStorage.Under(archive, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey))) + ".json");

    private EngineeringOperationRecord ReadLocked(string id, bool scan = false)
    {
        PackageStorage.Id(id);
        string root = Folder(id);
        if (!Directory.Exists(root)) Fail("OperationNotFound", "No durable operation has this ID.");
        var files = Directory.GetFiles(root, "*.json").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (files.Length is 0 or > 10000) Fail("JournalInvalid", "Missing or excessive operation revisions.");
        // A scan needs a record that owns nothing only for its chain: its paths or artifacts may be gone (a removed drive, a deleted folder).
        if (new FileInfo(files[^1]).Length > 8 * 1024 * 1024) Fail("JournalInvalid", "A persisted revision exceeds 8 MiB.");
        var last = PackageStorage.Load<EngineeringOperationRecord>(files[^1]);
        bool live = !scan || HoldsResources(last);
        EngineeringOperationRecord? previous = null;
        for (int i = 0; i < files.Length; i++)
        {
            if (new FileInfo(files[i]).Length > 8 * 1024 * 1024) Fail("JournalInvalid", "A persisted revision exceeds 8 MiB.");
            var record = i == files.Length - 1 ? last : PackageStorage.Load<EngineeringOperationRecord>(files[i]);
            ValidatePlan(record.Plan, live);
            if (Path.GetFileName(files[i]) != i.ToString("D6") + ".json" || record.Id != id || record.Revision != i ||
                record.PlanSha256 != Hash(record.Plan) || record.PreviousSha256 != (previous is null ? null : Hash(previous)) ||
                previous is not null && (record.PlanSha256 != previous.PlanSha256 || record.IdempotencyKey != previous.IdempotencyKey))
                Fail("JournalInvalid", "The operation revision chain or immutable plan is invalid.");
            ValidateTransition(previous, record, live);
            previous = record;
        }
        return previous!;
    }

    private void ValidateTransition(EngineeringOperationRecord? previous, EngineeringOperationRecord current, bool live = true)
    {
        if (current.Checkpoints is null || current.Evidence is null || current.Reconciliation is null || current.RecoveryDestinations is null || current.RecoveryResolutions is null ||
            current.Checkpoints.Any(x => x is null) || current.Evidence.Any(x => x is null) || current.Reconciliation.Any(x => x is null))
            Fail("JournalInvalid", "Missing durable checkpoint/evidence collections.");
        string[] allowed = previous?.State switch
        {
            null => ["prepared"], "prepared" => ["acquired", "stopped"], "acquired" => ["checkpointIntent", "stopped", "outcomeUnknown"],
            "checkpointIntent" => ["ready", "outcomeUnknown", "reconciled"], "ready" => ["dispatchIntent", "reconciled", "recoveryIntent", "outcomeUnknown"],
            "dispatchIntent" => ["dispatchIntent", "succeeded", "recoveryRequired", "outcomeUnknown", "reconciled"],
            "outcomeUnknown" => ["reconciled", "recoveryIntent", "outcomeUnknown"], "recoveryRequired" => ["reconciled", "recoveryIntent"], "succeeded" => ["recoveryIntent"], "recoveryIntent" => ["reconciled", "outcomeUnknown", "recoveryIntent"], _ => []
        };
        if (!allowed.Contains(current.State)) Fail("JournalInvalid", "An impossible persisted state transition cannot release ownership.");
        if (previous?.State == "succeeded" && current.Plan.Credentials is not null)
            Fail("JournalInvalid", "Terminal credential-bearing operations cannot reacquire retired references.");
        bool checkpointChanged = Hash(current.Checkpoints) != Hash(previous?.Checkpoints ?? []);
        bool evidenceChanged = Hash(current.Evidence) != Hash(previous?.Evidence ?? []);
        bool recoveryChanged = Hash(current.Reconciliation) != Hash(previous?.Reconciliation ?? []);
        bool destinationChanged = Hash(current.RecoveryDestinations) != Hash(previous?.RecoveryDestinations ?? []);
        bool targetChanged = (current.RecoveryTarget is null ? null : Hash(current.RecoveryTarget)) !=
            (previous?.RecoveryTarget is null ? null : Hash(previous.RecoveryTarget));
        if (targetChanged && (current.State != "recoveryIntent" || previous is null || previous.State == "recoveryIntent" || previous.RecoveryTarget is not null))
            Fail("JournalInvalid", "A recovery target is immutable after first-use admission.");
        if (current.RecoveryTarget is not null) ValidateRecoveryTarget(current.Plan, current.RecoveryTarget, live);
        if (current.State == "recoveryIntent" && current.Plan.Credentials is not null &&
            (current.RecoveryTarget is null && current.RecoveryDestinations.Count == 0 || previous?.State != "recoveryIntent" && previous?.RecoveryTarget is not null))
            Fail("JournalInvalid", "Credential recovery requires first-use context and cannot replay after unknown completion.");
        if (Hash(current.RecoveryResolutions) != Hash(previous?.RecoveryResolutions ?? []) && current.State != "reconciled")
            Fail("JournalInvalid", "Recovery destination outcomes require explicit reconciliation.");
        if (destinationChanged && (current.State != "recoveryIntent" || previous!.State == "recoveryIntent" || previous.RecoveryDestinations.Count != 0 || previous.RecoveryTarget is not null))
            Fail("JournalInvalid", "Recovery destinations require first-use durable admission.");
        if (current.State == "recoveryIntent") ValidateDestinations(current.Plan, current.RecoveryDestinations!, current.RecoveryTarget is not null, live);
        if (previous?.State != "dispatchIntent" && current.State == "dispatchIntent" && evidenceChanged)
            Fail("JournalInvalid", "Initial dispatch intent cannot invent returned result evidence.");
        if (previous?.State == "dispatchIntent" && current.State == "dispatchIntent")
        {
            if (current.Evidence.Count != previous.Evidence.Count + 1 ||
                Hash(current.Evidence.Take(previous.Evidence.Count).ToArray()) != Hash(previous.Evidence) ||
                current.Evidence.Last().Gate?.StartsWith("dispatch.", StringComparison.Ordinal) != true)
                Fail("JournalInvalid", "A pending dispatch revision may only append one immutable returned result, never redispatch.");
            ValidateEvidence(current.Evidence, live);
        }
        if (previous?.State == "dispatchIntent" && current.State is "succeeded" or "recoveryRequired" &&
            Hash(current.Evidence.Take(previous.Evidence.Count).ToArray()) != Hash(previous.Evidence))
            Fail("JournalInvalid", "Terminal outcomes must retain all previously recorded native results.");
        if (current.State is "recoveryIntent" or "outcomeUnknown" && evidenceChanged)
        {
            bool interruption = current.State == "outcomeUnknown" && previous!.State is "acquired" or "ready" or "checkpointIntent" or "dispatchIntent" or "recoveryIntent" &&
                current.Evidence.LastOrDefault()?.Gate == "interruption." + previous.Revision;
            if ((!interruption && (previous!.State != current.State || current.RecoveryDestinations.Count == 0 && !current.Plan.Resources.Any(x => x.Kind == "external"))) || current.Evidence.Count != previous!.Evidence.Count + 1 ||
                Hash(current.Evidence.Take(previous.Evidence.Count).ToArray()) != Hash(previous.Evidence)) Fail("JournalInvalid", "Recovery evidence must append exactly one immutable observation.");
            ValidateEvidence(current.Evidence, live);
        }
        if (checkpointChanged && current.State != "ready" || evidenceChanged && current.State is not ("dispatchIntent" or "succeeded" or "recoveryRequired" or "recoveryIntent" or "outcomeUnknown") ||
            recoveryChanged && current.State != "reconciled") Fail("JournalInvalid", "Evidence was changed outside its allowed transition.");
        if (current.State is "ready" or "dispatchIntent" or "succeeded" or "recoveryRequired")
            if (current.Checkpoints.Count != current.Plan.Resources.Count) Fail("JournalInvalid", "The persisted operation lacks all resource checkpoints.");
        if (current.State == "ready" && live)
            for (int i = 0; i < current.Plan.Resources.Count; i++)
                if (current.Plan.Resources[i].Kind == "external")
                {
                    VerifyArtifacts([current.Checkpoints[i]]);
                    EngineeringExternalEvidence.Verify(artifacts, current, previous!.Revision, current.Plan.Resources[i], "checkpoint", current.Plan.Resources[i].External!.Before, current.Checkpoints[i]);
                }
        if (current.State == "succeeded")
        {
            if (current.Plan.RequiredGates.Any(g => !current.Evidence.Any(e => e.Gate == g)))
                Fail("JournalInvalid", "A successful persisted operation lacks its required gates.");
            ValidateEvidence(current.Evidence, live); VerifyArtifacts(current.Checkpoints, live);
            foreach (var resource in current.Plan.Resources.Where(r => live && r.Kind == "external"))
                EngineeringExternalEvidence.Verify(artifacts, current, previous!.Revision, resource, "outcome", resource.External!.After,
                    current.Evidence.Single(e => e.Gate == EngineeringExternalEvidence.OutcomeGate(resource.ResourceKey)).Artifact);
        }
        if (current.State == "recoveryRequired")
        {
            if (!current.Evidence!.Any(e => e.Gate == "failure")) Fail("JournalInvalid", "The original failure evidence is missing.");
            ValidateEvidence(current.Evidence, live);
        }
        if (current.State == "reconciled") ValidateReconciliation(current, previous!.Revision, current.Reconciliation!, current.RecoveryDestinations!, current.RecoveryResolutions!, live);
    }

    private void Write(EngineeringOperationRecord record, string folder)
    {
        string path = PackageStorage.Under(folder, record.Revision.ToString("D6") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json);
        if (bytes.Length > 8 * 1024 * 1024) Fail("OperationStoreLimit", "The operation record exceeds 8 MiB.");
        // A crash leaves a .tmp; only a fully flushed rename becomes a committed revision.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temp, path, false);
    }

    private FileStream Lock() => PackageStorage.Lock(Path.Combine(directory, "store.lock"));
    private static bool HoldsResources(EngineeringOperationRecord r) => r.State != "prepared" && !Terminal.Contains(r.State);
    private static IEnumerable<string> ReservedKeys(EngineeringOperationRecord r) => r.Plan.Resources.Select(x => x.ResourceKey).Concat(
        r.RecoveryDestinations.Select(d => EngineeringResourceKey.ForPath(r.Plan.Resources.Single(x => x.ResourceKey == d.ResourceKey).Kind, d.RecoveryLocation)));
    private static T Freeze<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, BridgeProtocol.Json), BridgeProtocol.Json)!;
    private static string Hash<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
    private static void State(EngineeringOperationRecord record, string required)
    { if (record.State != required) Fail("RecoveryRequired", "Expected " + required + "; durable state " + record.State + " cannot be replayed."); }
    private static void Text(string text)
    {
        try { EngineeringIdentityGuard.Text(text, "operation text", 8192); }
        catch (BridgeException) { Fail("InvalidOperationPlan", "Exact bounded nonblank Unicode text is required."); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new BridgeException(code, message);
}
