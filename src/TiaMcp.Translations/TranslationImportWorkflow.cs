using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed record TranslationRetainedObservation(NativeTargetObservation Target, EngineeringArtifactReference Workbook,
    TranslationImportScope Scope, TranslationImportPeer[] Peers, EngineeringArtifactReference Receipt);
internal sealed record TranslationImportBinding(string CandidateRef, string CandidateReceiptSha256,
    string ApprovalSha256, string ReviewSha256, EngineeringArtifactReference Candidate,
    TranslationRetainedObservation Baseline, string BaselineContentSha256, string CandidateContentSha256);
internal sealed record TranslationRetainedCheckpoint(EngineeringArtifactReference Archive, EngineeringArtifactReference Receipt);

/// <summary>
/// Disabled native-import orchestration over the shared journal. Offline preparation is not native
/// admission. Each call executes at most one import or restoration. Restart/status never replay.
/// No background runner, process launch, Siemens reference or production-enabling flag exists here.
/// </summary>
public sealed class TranslationImportWorkflow
{
    private readonly string root;
    private readonly TranslationWorkflowStore workflow;
    private readonly EngineeringOperationStore store;
    private readonly TranslationImportArtifacts artifacts;
    private readonly Action<string>? boundary;
    private static readonly string[] Probes = ["translation.languages", "translation.scope", "translation.preservation"];
    private static readonly string[] Gates = ["translation.nativeResult", "translation.readback", "project.preservation",
        "project.compile", "project.save", "project.freshReopen", "resource.cleanup"];

    public TranslationImportWorkflow(string artifactRoot) : this(artifactRoot, null) { }
    internal TranslationImportWorkflow(string artifactRoot, Action<string>? boundary)
    {
        root = Path.GetFullPath(artifactRoot); TranslationRetention.NoLinks(root);
        workflow = new(root); store = new(root); artifacts = new(root); this.boundary = boundary;
        Directory.CreateDirectory(Path.Combine(root, "translation-import"));
    }

    /// <summary>The observation comes from a trusted export adapter, never a public tool's JSON.</summary>
    public EngineeringOperationRecord Prepare(string candidateRef, string candidateReceiptSha256,
        TranslationImportObservation observed, string idempotencyKey)
    {
        var candidate = workflow.NativeCandidate(candidateRef, candidateReceiptSha256);
        ValidateCandidateShape(candidate);
        var baseline = RetainObservation(observed);
        VerifyReceipt(baseline);
        NativeTargetGuard.EnsureFresh(observed.Target, observed.Target, Probes);
        EngineeringIdentityGuard.EnsureSameObject(candidate.Baseline.Project, observed.Target.Reference);
        Require(candidate.Baseline.Project.Snapshot.SourceSha256 == observed.Target.Reference.Snapshot.SourceSha256 &&
            candidate.Baseline.Project.ContentSha256 == observed.Target.Reference.ContentSha256 &&
            candidate.Baseline.Project.Snapshot.TiaVersion == observed.Target.Reference.Snapshot.TiaVersion,
            "StaleContent", "Fresh native provenance differs from the reviewed capture; create a new review.");
        ValidateScope(baseline, candidate.Baseline);
        string before = Content(candidate.Baseline), after = Content(candidate.Candidate);
        Require(Content(Open(baseline, candidate)) == before, "StaleWorkbook", "Fresh native export differs anywhere in the complete reviewed workbook.");
        var binding = new TranslationImportBinding(candidateRef, candidateReceiptSha256, candidate.ApprovalSha256,
            candidate.ReviewSha256, artifacts.RetainBytes(candidate.Candidate.OriginalBytes()), baseline, before, after);
        return store.Prepare(Plan(binding, artifacts.Retain(binding)), idempotencyKey);
    }

    public EngineeringOperationRecord Status(string id) => Context(id, null).Operation;

    /// <summary>Trusted adapters retain original peer exports here; no MCP tool exposes this seam.</summary>
    public EngineeringArtifactReference RetainNativeEvidence(byte[] bytes) => artifacts.RetainBytes(bytes);

    public async Task<EngineeringOperationRecord> Apply(string id, string planSha256, int revision,
        ITranslationImportAdapter adapter, CancellationToken token = default)
    {
        using var execution = Execution(id);
        var context = Context(id, revision); Match(context, planSha256);
        Require(context.Operation.State == "prepared", "RecoveryRequired", "An attempted import never redispatches; reopen and reconcile its original outcome.");
        adapter.RequireQualification(context, "apply");
        token.ThrowIfCancellationRequested();
        var op = store.Acquire(id, revision);
        ITranslationImportSession? session = null;
        bool returned = false, nativePending = false;
        async Task<T> Native<T>(Func<Task<T>> call)
        { nativePending = true; var value = await call(); nativePending = false; return value; }
        try
        {
            boundary?.Invoke("acquired");
            session = await Native(() => adapter.OpenExclusive(context with { Operation = op }, token));
            var before = RetainObservation(await Native(() => session.Observe(token)));
            VerifyObservation(context, before, candidateExpected: false, freshReopen: false, beforeMutation: true);
            op = store.BeginCheckpoint(id, op.Revision, () => before.Target);
            boundary?.Invoke("checkpointIntent");
            var checkpoint = await Native(() => session.Checkpoint(CancellationToken.None));
            var retainedCheckpoint = new TranslationRetainedCheckpoint(artifacts.RetainBytes(checkpoint.Archive), artifacts.RetainBytes(checkpoint.Receipt));
            ValidateCheckpoint(context, retainedCheckpoint);
            boundary?.Invoke("checkpointReturned");
            op = store.Checkpoint(id, op.Revision, [artifacts.Retain(retainedCheckpoint)]);
            boundary?.Invoke("checkpointRetained");
            var fresh = RetainObservation(await Native(() => session.Observe(token)));
            VerifyObservation(context, fresh, candidateExpected: false, freshReopen: false, beforeMutation: true);
            // Recheck original approval/candidate/archive bytes after the external await.
            context = Context(id, op.Revision); ValidateCheckpoint(context, retainedCheckpoint);
            token.ThrowIfCancellationRequested();
            op = store.Dispatch(id, op.Revision, () => fresh.Target);
            boundary?.Invoke("dispatchIntent");
            var result = await Native(() => session.Import(artifacts.ReadBytes(Binding(op).Candidate), CancellationToken.None));
            var raw = artifacts.RetainBytes(result.RawResult);
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.rawResult", raw));
            returned = true;
            boundary?.Invoke("rawResultRetained");
            var log = artifacts.RetainBytes(result.CompleteLog);
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.completeLog", log));
            ValidateReturn(context, raw, log);
            var readback = RetainObservation(await Native(() => session.Observe(CancellationToken.None)));
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.readback", artifacts.Retain(readback)));
            VerifyObservation(context, readback, candidateExpected: true, freshReopen: false);
            boundary?.Invoke("readbackRetained");
            var compile = artifacts.RetainBytes(await Native(() => session.Compile(CancellationToken.None)));
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.compile", compile)); ValidateStep(context, compile, "compile", true);
            boundary?.Invoke("compiled");
            var save = artifacts.RetainBytes(await Native(() => session.Save(CancellationToken.None)));
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.save", save)); ValidateStep(context, save, "save");
            boundary?.Invoke("saved");
            var reopened = RetainObservation(await Native(() => session.Reopen(CancellationToken.None)));
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.reopen", artifacts.Retain(reopened)));
            VerifyObservation(context, reopened, candidateExpected: true, freshReopen: true);
            boundary?.Invoke("reopened");
            var cleanup = artifacts.RetainBytes(await Native(() => session.Close(CancellationToken.None))); ValidateStep(context, cleanup, "cleanup");
            op = store.RecordDispatchEvidence(id, op.Revision, new("dispatch.cleanup", cleanup));
            var closing = session; session = null; nativePending = true; await closing.DisposeAsync(); nativePending = false;
            boundary?.Invoke("closed");
            var evidence = new EngineeringGateEvidence[] { new("translation.nativeResult", raw),
                new("translation.readback", artifacts.Retain(readback)), new("project.preservation", artifacts.Retain(reopened)),
                new("project.compile", compile), new("project.save", save), new("project.freshReopen", artifacts.Retain(reopened)), new("resource.cleanup", cleanup) };
            op = store.RecordOutcome(id, op.Revision, true, evidence);
            boundary?.Invoke("completed");
            return op;
        }
        catch (Exception error) { Failure(id, error, returned, nativePending); throw; }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    public EngineeringOperationRecord MarkInterrupted(string id, int revision)
    {
        using var execution = Execution(id); _ = Context(id, revision);
        return store.MarkUnknown(id, revision, artifacts.Retain(new { reason = "Explicit interrupted outcome; no automatic replay" }));
    }

    // Explicit qualification repair for a controller that failed before even
    // checkpoint intent. A changed worker is recorded honestly, never relabelled
    // as the previous binary. No dispatched/checkpointed operation can use this.
    internal async Task<EngineeringOperationRecord> ReconcileBeforeCheckpoint(string id, int revision,
        ITranslationImportAdapter adapter, byte[] originalClosedLifetime, CancellationToken token = default)
    {
        using var execution = Execution(id);
        var context = Context(id, revision); var op = context.Operation;
        Require(op.State == "outcomeUnknown" && op.Revision == 2 && op.Checkpoints.Count == 0 && op.RecoveryDestinations.Count == 0 &&
            op.Evidence.Count == 1 && op.Evidence[0].Gate == "interruption.1", "TranslationReconciliationUnqualified", "Only the exact prepared/acquired/interrupted pre-checkpoint history is eligible.");
        var history = Directory.GetFiles(Path.Combine(root, "engineering-operations", id), "*.json").Order(StringComparer.Ordinal).ToArray();
        Require(history.Length == 3 && Parse<EngineeringOperationRecord>(File.ReadAllText(history[0])).State == "prepared" &&
            Parse<EngineeringOperationRecord>(File.ReadAllText(history[1])).State == "acquired", "TranslationReconciliationUnqualified", "Checkpoint or dispatch history cannot migrate adapters.");
        ValidateClosedWorker(op, originalClosedLifetime);
        adapter.RequireQualification(context, "reconcileBeforeCheckpoint");
        await using var session = await adapter.OpenExclusive(context, token);
        var actual = RetainObservation(await session.Reopen(token));
        var binding = Binding(op); var candidate = Candidate(binding);
        VerifyReceipt(actual); ValidateScope(actual, candidate.Baseline);
        var previous = binding.Baseline.Target;
        NativeTargetGuard.EnsureFresh(previous with { SessionEpoch = actual.Target.SessionEpoch, AdapterSha256 = actual.Target.AdapterSha256 }, actual.Target, Probes);
        Require(previous.SessionEpoch != actual.Target.SessionEpoch && HashJson(actual.Scope) == HashJson(binding.Baseline.Scope) &&
            PeersEqual(binding.Baseline.Peers, actual.Peers, candidate) && Content(Open(actual, candidate)) == binding.BaselineContentSha256,
            "TranslationReconciliationUnqualified", "Fresh unchanged full native content, scope and peers are required.");
        var cleanup = artifacts.RetainBytes(await session.Close(CancellationToken.None)); ValidateStep(context, cleanup, "cleanup");
        var evidence = artifacts.Retain(new { originalAdapter = previous.AdapterSha256, observedAdapter = actual.Target.AdapterSha256,
            reason = "Explicit read-only pre-checkpoint adapter transition; no replay", originalClosedLifetime = artifacts.RetainBytes(originalClosedLifetime), actual, cleanup });
        return store.Reconcile(id, revision, [new(ProjectKey(context), "unchanged", null, evidence)]);
    }

    internal async Task<EngineeringArtifactReference> ResumeClosedFixture(string id, int revision,
        ITranslationImportAdapter adapter, byte[] originalClosedLifetime, CancellationToken token = default)
    {
        using var execution = Execution(id); var context = Context(id, revision); var op = context.Operation;
        Require(op.State == "reconciled" && op.Checkpoints.Count == 1 && op.RecoveryResolutions.Count == 1 &&
            op.RecoveryResolutions[0].Disposition == "verifiedRestored", "TranslationReconciliationUnqualified", "Completed native lifecycle and reconciled checkpoint recovery are required.");
        Require(store.Pending().Count == 0,
            "ResourceBusy", "Closed-fixture qualification requires a store without unresolved operations.");
        ValidateClosedWorker(op, originalClosedLifetime); adapter.RequireQualification(context, "resumeClosedFixture");
        await using var session = await adapter.OpenExclusive(context, token);
        var observed = RetainObservation(await session.Reopen(token));
        VerifyObservation(context, observed, op.Reconciliation.Single().Disposition == "verifiedApplied", true,
            permittedClosedAdapter: observed.Target.AdapterSha256);
        var cleanup = artifacts.RetainBytes(await session.Close(CancellationToken.None)); ValidateStep(context, cleanup, "cleanup");
        return artifacts.Retain(new { reason = "Fresh inspection of an already reconciled owned fixture; no replay and no journal rewrite",
            originalOperationId = id, originalAdapter = op.Plan.Target.AdapterSha256, observedAdapter = observed.Target.AdapterSha256,
            originalClosedLifetime = artifacts.RetainBytes(originalClosedLifetime), observed, cleanup });
    }

    private static void ValidateClosedWorker(EngineeringOperationRecord op, byte[] originalClosedLifetime)
    {
        var close = System.Text.Json.Nodes.JsonNode.Parse(originalClosedLifetime)!;
        Require(close["tree"]!["completed"]!.GetValue<bool>() && !close["tree"]!["forcedTermination"]!.GetValue<bool>() &&
            !close["tree"]!["startupOutcomeUnknown"]!.GetValue<bool>(), "NativeCleanupUnverified", "Original exact process tree must be closed.");
        var exits = close["tree"]!["exits"]!.AsArray();
        Require(exits.Count == 3 && exits.Select(e => e!["role"]!.GetValue<string>()).Order().SequenceEqual(new[] { "host", "native", "worker" }) &&
            exits.All(e => e!["proof"]!["completed"]!.GetValue<bool>() && e["proof"]!["processExitVerified"]!.GetValue<bool>() &&
                e["proof"]!["birthAndImageVerified"]!.GetValue<bool>() && e["proof"]!["sameOsHandleRetained"]!.GetValue<bool>() &&
                e["proof"]!["handleClosed"]!.GetValue<bool>() && e["proof"]!["exitCode"]!.GetValue<int>() == 0 &&
                !e["proof"]!["deadlineOrCancellationObserved"]!.GetValue<bool>()), "NativeCleanupUnverified", "Every original owned process requires normal retained-handle terminal proof.");
        string oldWorker = exits.Single(e => e!["role"]!.GetValue<string>() == "worker")!["identity"]!["exe"]!.GetValue<string>();
        Require(Sha256(File.ReadAllBytes(oldWorker)) == op.Plan.Target.AdapterSha256, "NativeCleanupUnverified", "Closed worker proof does not bind the original adapter.");
    }

    public async Task<EngineeringOperationRecord> Recover(string id, string planSha256, int revision,
        string newProjectPath, ITranslationImportAdapter adapter, CancellationToken token = default)
    {
        using var execution = Execution(id);
        var context = Context(id, revision); Match(context, planSha256);
        adapter.RequireQualification(context, "recover");
        Require(context.Operation.Checkpoints.Count == 1, "CheckpointRequired", "An original retained checkpoint is required; lost checkpoint returns are not replayed.");
        var checkpoint = artifacts.Read<TranslationRetainedCheckpoint>(context.Operation.Checkpoints[0]); ValidateCheckpoint(context, checkpoint);
        _ = EngineeringResourceKey.ForPath("project", newProjectPath);
        Require(!File.Exists(newProjectPath) && !Directory.Exists(newProjectPath) && !Directory.Exists(Path.GetDirectoryName(newProjectPath)),
            "TranslationRecoveryDestinationExists", "Recovery requires a new project directory; an existing destination is never overwritten.");
        token.ThrowIfCancellationRequested();
        var op = store.ReserveRecovery(id, revision, [new(ProjectKey(context), newProjectPath)]);
        ITranslationImportSession? session = null;
        try
        {
            boundary?.Invoke("recoveryIntent");
            session = await adapter.OpenExclusive(context with { Operation = op }, CancellationToken.None);
            var returned = artifacts.RetainBytes(await session.Restore(artifacts.ReadBytes(checkpoint.Archive), op.RecoveryDestinations.Single(), CancellationToken.None));
            op = store.RecordRecoveryEvidence(id, op.Revision, new("recovery.return", returned));
            boundary?.Invoke("recoveryReturned");
            var cleanup = artifacts.RetainBytes(await session.Close(CancellationToken.None)); ValidateStep(context, cleanup, "cleanup");
            op = store.RecordRecoveryEvidence(id, op.Revision, new("recovery.cleanup", cleanup));
            var closing = session; session = null; await closing.DisposeAsync();
            // A return alone never proves restored bytes or releases either reservation.
            return op;
        }
        catch (Exception error) { Failure(id, error, false); throw; }
        finally { if (session is not null) await session.DisposeAsync(); }
    }

    /// <summary>Read-only reconciliation, including a lost restore response. Never calls Import or Restore.</summary>
    public async Task<EngineeringOperationRecord> Reconcile(string id, string planSha256, int revision,
        ITranslationImportAdapter adapter, CancellationToken token = default)
    {
        using var execution = Execution(id);
        var context = Context(id, revision); Match(context, planSha256);
        Require(context.Operation.State is "outcomeUnknown" or "recoveryRequired" or "dispatchIntent" or "checkpointIntent" or "ready" or "recoveryIntent",
            "RecoveryRequired", "Only an attempted, unresolved operation can reconcile.");
        adapter.RequireQualification(context, "reconcile");
        var session = await adapter.OpenExclusive(context, token);
        var resolutions = new List<EngineeringRecoveryResolution>();
        ResourceReconciliation original;
        try
        {
            var observation = RetainObservation(await session.Reopen(token));
            var binding = Binding(context.Operation);
            bool applied = Content(Open(observation, Candidate(binding))) != binding.BaselineContentSha256;
            VerifyObservation(context, observation, applied, freshReopen: true);
            original = new(ProjectKey(context), applied ? "verifiedApplied" : "unchanged", null, artifacts.Retain(observation));
            foreach (var destination in context.Operation.RecoveryDestinations)
            {
                var inspected = await session.ObserveRecovered(destination, false, token);
                Require((inspected.Present is null) != (inspected.AbsentReceipt is null), "TranslationRecoveryMismatch", "Recovery inspection must prove exactly one present or absent state.");
                if (inspected.Present is null)
                {
                    var absence = artifacts.RetainBytes(inspected.AbsentReceipt!);
                    var proof = artifacts.Read<TranslationRecoveryAbsenceReceipt>(absence);
                    Require(proof.OperationId == id && proof.PlanSha256 == planSha256 && proof.ProjectResourceKey == destination.ResourceKey &&
                        proof.RecoveryLocation == destination.RecoveryLocation && proof.AdapterSha256 == context.Operation.Plan.Target.AdapterSha256 &&
                        proof.State == "absent" && !string.IsNullOrWhiteSpace(proof.Detail), "TranslationRecoveryMismatch", "Exact reserved destination absence is unresolved.");
                    resolutions.Add(new(destination.ResourceKey, destination.RecoveryLocation, "verifiedAbsent", absence));
                    continue;
                }
                var restored = RetainObservation(inspected.Present);
                VerifyRecovered(context, destination, restored);
                var second = await session.ObserveRecovered(destination, true, token);
                Require(second.Present is not null && second.AbsentReceipt is null, "TranslationRecoveryMismatch", "Recovered copy disappeared during reopen.");
                var reopened = RetainObservation(second.Present);
                VerifyRecovered(context, destination, reopened);
                Require(reopened.Target.SessionEpoch != restored.Target.SessionEpoch &&
                    reopened.Target.Reference.Snapshot.ObservationId != restored.Target.Reference.Snapshot.ObservationId,
                    "TranslationReopenRequired", "Recovered bytes require a fresh native reopen, not repeated in-memory readback.");
                resolutions.Add(new(destination.ResourceKey, destination.RecoveryLocation, "verifiedRestored", artifacts.Retain(new { restored, reopened })));
            }
            var cleanup = artifacts.RetainBytes(await session.Close(CancellationToken.None)); ValidateStep(context, cleanup, "cleanup");
            original = original with { Evidence = artifacts.Retain(new { observation = original.Evidence, cleanup }) };
        }
        finally { await session.DisposeAsync(); }
        boundary?.Invoke("reconciledEvidence");
        return store.Reconcile(id, revision, [original], resolutions);
    }

    private EngineeringOperationPlan Plan(TranslationImportBinding b, EngineeringArtifactReference binding) => new("translation.import", b.Baseline.Target,
        [new(EngineeringResourceKey.ForPath("project", b.Baseline.Target.ProjectFilePath), "project", "translationImportAndCheckpoint", "projectArchiveToNewLocation")],
        [binding, b.Candidate, b.Baseline.Workbook, b.Baseline.Receipt], Probes.ToArray(), Gates.ToArray());
    private TranslationImportContext Context(string id, int? revision)
    {
        var op = store.Read(id);
        Require(op.Plan.Operation == "translation.import" && op.Plan.Inputs.Count == 4, "InvalidTranslationOperation", "Not a translation import operation.");
        Require(revision is null || op.Revision == revision, "RevisionConflict", "Reread the durable operation revision.");
        var binding = Binding(op); var candidate = Candidate(binding);
        Require(HashJson(Plan(binding, op.Plan.Inputs[0])) == HashJson(op.Plan) &&
            binding.Candidate.Sha256 == candidate.Candidate.WorkbookSha256 &&
            binding.BaselineContentSha256 == Content(candidate.Baseline) && binding.CandidateContentSha256 == Content(candidate.Candidate),
            "TranslationIntegrityFailure", "Operation differs from its exact approved candidate and original capture.");
        _ = artifacts.ReadBytes(binding.Candidate); _ = Open(binding.Baseline, candidate); VerifyReceipt(binding.Baseline);
        return new(root, op, binding.CandidateRef, binding.CandidateReceiptSha256, binding.Candidate.Sha256, Clone(binding.Baseline.Scope), candidate.UpdateSourceLanguage);
    }
    private TranslationImportBinding Binding(EngineeringOperationRecord op) => artifacts.Read<TranslationImportBinding>(op.Plan.Inputs[0]);
    private TranslationImportCandidate Candidate(TranslationImportBinding b)
    {
        var value = workflow.NativeCandidate(b.CandidateRef, b.CandidateReceiptSha256);
        ValidateCandidateShape(value);
        Require(value.ApprovalSha256 == b.ApprovalSha256 && value.ReviewSha256 == b.ReviewSha256, "ReviewApprovalMismatch", "Original approval changed.");
        return value;
    }
    private TranslationRetainedObservation RetainObservation(TranslationImportObservation observed)
    {
        var retained = new TranslationRetainedObservation(Clone(observed.Target), artifacts.RetainBytes(observed.Workbook),
            Clone(observed.Scope), Clone(observed.Peers), artifacts.RetainBytes(observed.Receipt));
        return retained;
    }
    private void VerifyReceipt(TranslationRetainedObservation value)
    {
        var receipt = artifacts.Read<TranslationObservationReceipt>(value.Receipt);
        Require(HashJson(receipt) == HashJson(new TranslationObservationReceipt(value.Target, value.Workbook.Sha256,
            HashJson(value.Scope), HashJson(value.Peers))), "TranslationObservationMismatch", "Retained native receipt does not bind these exact exported bytes, target, languages, scope and complete peers.");
        Require(value.Peers is { Length: > 0 and <= 10000 } && value.Peers.All(p => p?.Evidence is not null),
            "TranslationPreservationFailure", "Complete peers require retained original export artifacts, not unsupported hash claims.");
        long total = 0;
        foreach (var peer in value.Peers)
        {
            Require(peer.Evidence.Sha256 == peer.Sha256, "TranslationPreservationFailure", "Peer digest does not bind its original export.");
            Require(peer.ComparisonProfile is "exact" or "tia21-simaticml-translation-v1", "TranslationPeerProfileUnqualified", "Unknown native peer comparison profile.");
            total = checked(total + artifacts.ReadBytes(peer.Evidence).LongLength);
            Require(total <= TranslationRetention.WorkbookLimit, "TranslationArtifactLimit", "Original peer exports exceed the 100 MiB per-observation budget.");
        }
    }
    private TranslationWorkbook Open(TranslationRetainedObservation value, TranslationImportCandidate candidate) =>
        TranslationWorkbook.Open(artifacts.ReadBytes(value.Workbook), candidate.Baseline.Profile, value.Target.Reference, value.Workbook.Sha256);
    private static string Content(TranslationWorkbook workbook) => TranslationCategoryScope.NativeContentSha256(workbook);

    private static void ValidateCandidateShape(TranslationImportCandidate value)
    {
        foreach (var row in value.Baseline.Profile.Rows)
        {
            // Source cells may change only under the review's separately approved source-text mode, with the target rules below.
            foreach (var location in row.Targets.Select(t => t.Location).Prepend(row.SourceCell))
            {
                var before = value.Baseline.ReadText(location); var after = value.Candidate.ReadText(location);
                if (before == after) continue;
                Require(location != row.SourceCell || value.UpdateSourceLanguage,
                    "TranslationImportShapeUnqualified", "Source-language edits require the separately approved source-text mode.");
                Require(before.State == "text" && after.State == "text" && !before.RichText && !after.RichText &&
                    !string.IsNullOrWhiteSpace(before.Text) && !string.IsNullOrWhiteSpace(after.Text),
                    "TranslationImportShapeUnqualified", "Only nonempty plain existing targets are eligible for this disabled adapter; clear, missing, empty and rich-text writes need separate qualification.");
            }
        }
    }

    private static void ValidateScope(TranslationRetainedObservation value, TranslationWorkbook workbook)
    {
        var scope = value.Scope;
        static bool Unique(string[]? a, int max) => a is { Length: > 0 } && a.Length <= max && a.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 8192 && !s.Any(char.IsControl)) && a.Distinct(StringComparer.Ordinal).Count() == a.Length;
        Require(scope is not null && Unique(scope.ActiveCultures, 256) && Unique(scope.ObjectSelections, 256) && Unique(scope.CategoryIds, 256) &&
            scope.SourceCulture == workbook.Profile.SourceCulture && scope.TargetCulture != scope.SourceCulture &&
            scope.ActiveCultures.Contains(scope.SourceCulture) && scope.ActiveCultures.Contains(scope.TargetCulture) &&
            TranslationCategoryScope.NativeTargetCulture(workbook) == scope.TargetCulture &&
            workbook.Profile.Rows.SelectMany(r => r.Targets).All(t => t.Culture == scope.TargetCulture),
            "TranslationScopeMismatch", "Exact bounded bilingual scope and complete active language inventory are required.");
        Require(value.Target.ProfileSha256 == workbook.ProfileSha256, "TranslationScopeMismatch", "Native profile differs from the reviewed complete workbook map.");
        Require(EngineeringResourceKey.ForPath("project", value.Target.ProjectFilePath) == EngineeringResourceKey.ForPath("project", value.Target.Reference.Object.ProjectPath),
            "IdentityMismatch", "Native project path and captured exact project selector differ.");
        Require(value.Peers is { Length: > 0 and <= 10000 } && value.Peers.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Identity) && p.Identity.Length <= 8192 && !p.Identity.Any(char.IsControl)) &&
            value.Peers.Select(p => p.Identity).Distinct(StringComparer.Ordinal).Count() == value.Peers.Length,
            "TranslationPreservationFailure", "Require a complete independently captured peer inventory, including untouched languages.");
        foreach (var peer in value.Peers) EngineeringIdentityGuard.Sha256(peer.Sha256, "peerSha256");
    }

    private void VerifyObservation(TranslationImportContext context, TranslationRetainedObservation value, bool candidateExpected,
        bool freshReopen, bool beforeMutation = false, string? permittedClosedAdapter = null)
    {
        var b = Binding(context.Operation); var expected = b.Baseline.Target;
        if (permittedClosedAdapter is not null) expected = expected with { AdapterSha256 = permittedClosedAdapter };
        var candidate = Candidate(b); VerifyReceipt(value); ValidateScope(value, candidate.Baseline);
        EngineeringIdentityGuard.EnsureSameObject(expected.Reference, value.Target.Reference);
        if (beforeMutation) NativeTargetGuard.EnsureFresh(expected, value.Target, Probes);
        else
        {
            // Content changes are expected after import; installation, project, hardware,
            // profile, native identity and offline state remain independently bound.
            NativeTargetGuard.EnsureFresh(expected with { Reference = value.Target.Reference,
                SessionEpoch = freshReopen ? value.Target.SessionEpoch : expected.SessionEpoch }, value.Target, Probes);
            Require(expected.Reference.Snapshot.TiaVersion == value.Target.Reference.Snapshot.TiaVersion, "StaleNativeTarget", "TIA version changed.");
        }
        if (freshReopen) Require(value.Target.SessionEpoch != expected.SessionEpoch &&
            value.Target.Reference.Snapshot.ObservationId != expected.Reference.Snapshot.ObservationId,
            "TranslationReopenRequired", "A fresh native epoch and observation are required.");
        Require(HashJson(value.Scope) == HashJson(b.Baseline.Scope), "TranslationScopeMismatch", "Active languages, object selection or category inventory changed.");
        Require(PeersEqual(b.Baseline.Peers, value.Peers, candidate), "TranslationPreservationFailure", "An unaffected peer or untouched language changed.");
        Require(Content(Open(value, candidate)) == (candidateExpected ? b.CandidateContentSha256 : b.BaselineContentSha256),
            beforeMutation ? "StaleWorkbook" : "TranslationReadbackMismatch", "Complete native keys, categories, metadata, sources or targets differ. Re-review drift; never rebase silently.");
    }
    private void VerifyRecovered(TranslationImportContext context, EngineeringRecoveryDestination destination, TranslationRetainedObservation value)
    {
        var b = Binding(context.Operation); var expected = b.Baseline.Target;
        VerifyReceipt(value); ValidateScope(value, Candidate(b).Baseline);
        NativeTargetGuard.EnsureFresh(value.Target, value.Target, Probes);
        Require(EngineeringResourceKey.ForPath("project", value.Target.ProjectFilePath) == EngineeringResourceKey.ForPath("project", destination.RecoveryLocation) &&
            EngineeringResourceKey.ForPath("project", value.Target.Reference.Object.ProjectPath) == EngineeringResourceKey.ForPath("project", destination.RecoveryLocation) &&
            value.Target.Reference.Object.Kind == "project" && value.Target.AdapterSha256 == expected.AdapterSha256 &&
            value.Target.HardwareIdentity == expected.HardwareIdentity && value.Target.Reference.Snapshot.TiaVersion == expected.Reference.Snapshot.TiaVersion &&
            value.Target.SessionEpoch != expected.SessionEpoch, "TranslationRecoveryMismatch", "Recovered observation must belong to the reserved new project and pinned installation.");
        Require(HashJson(value.Scope) == HashJson(b.Baseline.Scope) && PeersEqual(b.Baseline.Peers, value.Peers, Candidate(b)) &&
            Content(Open(value, Candidate(b))) == b.BaselineContentSha256,
            "TranslationRecoveryMismatch", "Recovered complete texts, languages and peers must equal the original checkpoint baseline.");
    }
    private void ValidateCheckpoint(TranslationImportContext context, TranslationRetainedCheckpoint value)
    {
        var b = Binding(context.Operation); _ = artifacts.ReadBytes(value.Archive);
        Require(HashJson(artifacts.Read<TranslationCheckpointReceipt>(value.Receipt)) == HashJson(new TranslationCheckpointReceipt(context.Operation.Id,
            context.Operation.PlanSha256, ProjectKey(context), context.Operation.Plan.Target.SessionEpoch, value.Archive.Sha256, b.BaselineContentSha256)),
            "TranslationCheckpointMismatch", "Checkpoint must bind the operation, exact project/session and original complete content.");
    }
    private bool PeersEqual(TranslationImportPeer[] expected, TranslationImportPeer[] observed, TranslationImportCandidate candidate)
    {
        if (expected.Length != observed.Length) return false;
        var actual = observed.ToDictionary(p => p.Identity, StringComparer.Ordinal);
        return expected.All(p => actual.TryGetValue(p.Identity, out var q) && p.ComparisonProfile == q.ComparisonProfile &&
            TranslationNativeComparison.Peer(artifacts.ReadBytes(p.Evidence), p.ComparisonProfile, candidate).AsSpan()
                .SequenceEqual(TranslationNativeComparison.Peer(artifacts.ReadBytes(q.Evidence), q.ComparisonProfile, candidate)));
    }
    private void ValidateReturn(TranslationImportContext context, EngineeringArtifactReference raw, EngineeringArtifactReference log)
    {
        var r = artifacts.Read<TranslationImportResultReceipt>(raw);
        Require(r.OperationId == context.Operation.Id && r.PlanSha256 == context.Operation.PlanSha256 && r.ProjectResourceKey == ProjectKey(context) &&
            r.CandidateSha256 == context.CandidateSha256 && r.LogSha256 == log.Sha256 && r.Accepted && r.Complete && r.ErrorCount == 0,
            "TranslationImportFailed", "Native import result/log is incomplete, failed or bound to another operation/candidate.");
    }
    private void ValidateStep(TranslationImportContext context, EngineeringArtifactReference artifact, string step, bool allowNotApplicable = false)
    {
        var r = artifacts.Read<TranslationImportStepReceipt>(artifact);
        Require(r.OperationId == context.Operation.Id && r.PlanSha256 == context.Operation.PlanSha256 && r.ProjectResourceKey == ProjectKey(context) &&
            r.Step == step && (r.Disposition == "passed" || allowNotApplicable && r.Disposition == "notApplicable") && !string.IsNullOrWhiteSpace(r.Detail),
            "TranslationVerificationIncomplete", "The exact operation needs retained complete " + step + " evidence; applicability is explicit.");
    }
    private void Failure(string id, Exception error, bool returned, bool nativePending = false)
    {
        var op = store.Read(id);
        var failure = artifacts.Retain(new { error = error.Message, code = (error as BridgeException)?.Code, nativeReturned = returned, nativeCallOutcomeUnknown = nativePending, automaticReplay = false });
        if (op.State == "dispatchIntent" && returned && !nativePending) store.RecordOutcome(id, op.Revision, false, [new("failure", failure)]);
        else if (op.State is "acquired" or "checkpointIntent" or "ready" or "dispatchIntent" or "recoveryIntent") store.MarkUnknown(id, op.Revision, failure);
    }
    private static string ProjectKey(TranslationImportContext c) => c.Operation.Plan.Resources.Single().ResourceKey;
    private static void Match(TranslationImportContext c, string sha) => Require(c.Operation.PlanSha256 == sha, "ReviewApprovalMismatch", "Bind the exact durable import plan.");
    private IDisposable Execution(string id)
    {
        Require(id.Length == 32 && id.All(c => char.IsAsciiHexDigit(c)), "InvalidTranslationOperation", "Invalid operation handle.");
        string path = Path.Combine(root, "translation-import", id + ".lock"); TranslationRetention.NoLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new BridgeException("TranslationImportBusy", "This operation already has an active controller; status is read-only."); }
    }
}
