using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.TestSuite;

public sealed record TestSuiteStyleSelection(string[] SelectedDefinitionIds, string[] ExcludedDefinitionIds);
public sealed record TestSuiteStyleReviewedRun(TestSuiteRunPlan Run, EngineeringOperationPlan Operation);
public sealed record TestSuiteStyleReview(int SchemaVersion, string ExecutionProfile, TestSuiteStyleReviewedRun[] Runs,
    string StopSemantics = "stopBetweenRuns; active native calls are never cancelled")
{
    // Omission keeps every style review hash unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TestSuiteApplicationBinding? Application { get; init; }
}
public sealed record TestSuiteStyleJob(string Id, string ReviewSha256, EngineeringArtifactReference Review,
    string IdempotencyKey, string[] OperationIds);
public sealed record TestSuiteStyleJobStatus(TestSuiteStyleJob Job, bool StopRequested,
    EngineeringOperationRecord[] Operations, string? NextOperationId, bool NativeCancellationSupported = false);
public sealed record TestSuiteStyleRetainedResult(string Classification, EngineeringArtifactReference Raw,
    EngineeringArtifactReference Results, EngineeringArtifactReference Scope, EngineeringArtifactReference Assessment);

/// <summary>
/// Durable bounded queue over EngineeringOperationStore. No timers, startup resumption or automatic
/// redispatch. Each ExecuteNext admits one reviewed operation; the shared journal owns resources.
/// Job controls use a separate short lock so stop/status remain available during a blocking Run.
/// </summary>
public sealed class TestSuiteStyleJobs
{
    private readonly string root, directory;
    private readonly EngineeringOperationStore store;
    private static readonly JsonSerializerOptions Strict = new(BridgeProtocol.Json)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 128 };
    public TestSuiteStyleJobs(string artifactRoot)
    {
        root = Path.GetFullPath(artifactRoot); PackageStorage.EnsureNoLinks(root);
        directory = PackageStorage.Under(root, "testsuite-jobs"); Directory.CreateDirectory(directory);
        store = new(root);
    }

    public EngineeringArtifactReference Review(TestSuiteDefinitionCapture[] definitions, TestSuiteStyleSelection[] selections,
        NativeTargetObservation target, string executionProfile, TestSuiteApplicationBinding? application = null)
    {
        if (selections is null || selections.Length is < 1 or > 32 || selections.Any(s => s is null) ||
            !TestSuiteRunPlanner.Text(executionProfile, 256))
            throw new BridgeException("TestSuiteJobLimit", "Review one to 32 explicit runs and a bounded execution profile.");
        if (definitions is null || definitions.Length is < 1 or > 1000 || definitions.Any(d => d is null) ||
            definitions.Sum(d => (long)d.OriginalBytes.Length) > TestSuiteDefinitionBundle.MaxOriginalBytes)
            throw new BridgeException("TestSuiteJobLimit", "A job retains at most 1,000 definitions and 32 MiB of originals.");
        if (application is not null) TestSuiteApplicationRun.Validate(application);
        var runs = new List<TestSuiteStyleReviewedRun>();
        foreach (var selection in selections)
        {
            var run = TestSuiteRunPlanner.Prepare(definitions, selection.SelectedDefinitionIds, selection.ExcludedDefinitionIds, application?.ExecutionMode ?? "style");
            if (application is not null) _ = TestSuiteApplicationRun.Route(run, application.WholeGroup);
            var bundle = TestSuiteDefinitionBundle.Capture(run, definitions.Where(d => run.RequiredDefinitionIds.Contains(d.Manifest.Id)).ToArray());
            var runArtifact = Retain(run); var bundleArtifact = RetainBytes(bundle.Bytes);
            var operation = TestSuiteStyleOperation.BuildPlan(run, target, runArtifact, bundleArtifact, bundle);
            runs.Add(new(run, operation));
        }
        return Retain(new TestSuiteStyleReview(1, executionProfile, runs.ToArray()) { Application = application });
    }

    public TestSuiteStyleReview ReadReview(EngineeringArtifactReference review)
    {
        var value = Read<TestSuiteStyleReview>(review);
        if (value.SchemaVersion != 1 || value.Runs is null || value.Runs.Length is < 1 or > 32 ||
            !TestSuiteRunPlanner.Text(value.ExecutionProfile, 256) ||
            value.StopSemantics != "stopBetweenRuns; active native calls are never cancelled")
            throw new BridgeException("InvalidTestSuiteJob", "Invalid retained style review.");
        if (value.Application is not null) TestSuiteApplicationRun.Validate(value.Application);
        foreach (var run in value.Runs)
        {
            if (run?.Run?.ExecutionMode != (value.Application?.ExecutionMode ?? "style")) throw new BridgeException("InvalidTestSuiteJob", "Every run must use the review's execution mode.");
            if (value.Application is not null) _ = TestSuiteApplicationRun.Route(run.Run, value.Application.WholeGroup);
            if (run?.Operation?.Inputs?.Count != 2) throw new BridgeException("InvalidTestSuiteJob", "Both immutable inputs are required.");
            var retainedRun = Read<TestSuiteRunPlan>(run.Operation.Inputs[0]);
            var bundle = TestSuiteDefinitionBundle.Restore(retainedRun, ReadBytes(run.Operation.Inputs[1]));
            var rebuilt = TestSuiteStyleOperation.BuildPlan(retainedRun, run.Operation.Target, run.Operation.Inputs[0], run.Operation.Inputs[1], bundle);
            if (Hash(rebuilt) != Hash(run.Operation) || Hash(retainedRun) != Hash(run.Run))
                throw new BridgeException("ReviewMismatch", "The complete reviewed run and durable effect plan must match retained inputs.");
        }
        if (value.Runs.Select(r => r.Operation.Resources[0].ResourceKey).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new BridgeException("InvalidTestSuiteJob", "All runs in a style job must bind one exact project.");
        return value;
    }

    public TestSuiteStyleJobStatus Prepare(EngineeringArtifactReference review, string reviewedSha256, string idempotencyKey)
    {
        if (review.Sha256 != reviewedSha256) throw new BridgeException("ReviewMismatch", "Review the complete plan and supply its exact SHA-256.");
        if (!TestSuiteRunPlanner.Text(idempotencyKey, 256))
            throw new BridgeException("InvalidRequest", "A bounded idempotency key is required.");
        var plan = ReadReview(review);
        string id = BytesHash(Encoding.UTF8.GetBytes(idempotencyKey))[..32];
        using (PackageStorage.Lock(Path.Combine(directory, "prepare.lock")))
        {
            string path = JobPath(id);
            if (File.Exists(path))
            {
                var prior = ReadJob(id);
                if (prior.IdempotencyKey != idempotencyKey || prior.ReviewSha256 != reviewedSha256)
                    throw new BridgeException("IdempotencyConflict", "This job key already binds another complete review.");
            }
            else
            {
                // A partially prepared queue owns no resources. Repeating this request finishes only
                // preparation with identical store keys; it never checkpoints or dispatches.
                var ids = plan.Runs.Select((r, i) => store.Prepare(r.Operation, "testsuite:" + id + ":" + i).Id).ToArray();
                PackageStorage.Save(path, new TestSuiteStyleJob(id, reviewedSha256, review, idempotencyKey, ids));
            }
        }
        return Status(id);
    }

    public TestSuiteStyleJobStatus Status(string id)
    {
        var job = ReadJob(id);
        var review = ReadReview(job.Review);
        var operations = job.OperationIds.Select(store.Read).ToArray();
        if (operations.Length != review.Runs.Length || operations.Where((op, i) => op.PlanSha256 != Hash(review.Runs[i].Operation) ||
            op.IdempotencyKey != "testsuite:" + id + ":" + i).Any())
            throw new BridgeException("InvalidTestSuiteJob", "Job membership differs from its immutable operations.");
        bool stop = Stopped(id);
        var first = operations.FirstOrDefault(o => o.State is not ("succeeded" or "reconciled" or "stopped"));
        return new(job, stop, operations, !stop && first?.State == "prepared" ? first.Id : null);
    }

    public TestSuiteStyleJobStatus RequestStop(string id, string reviewedSha256)
    {
        var job = ReadJob(id); Match(job, reviewedSha256);
        using (Control(id))
        {
            var marker = StopPath(id);
            if (!File.Exists(marker)) PackageStorage.Save(marker, new StopRecord(id, job.ReviewSha256));
            foreach (var op in job.OperationIds.Select(store.Read).Where(o => o.State == "prepared")) store.Stop(op.Id, op.Revision);
        }
        return Status(id);
    }

    public async Task<TestSuiteStyleJobStatus> ExecuteNext(string id, string reviewedSha256, string operationId,
        int expectedRevision, ITestSuiteStyleAdapter adapter, CancellationToken token = default)
    {
        using var execution = Execution(id);
        var status = Status(id); Match(status.Job, reviewedSha256);
        var context = Context(status, operationId, expectedRevision);
        if (status.NextOperationId != operationId) throw new BridgeException("RecoveryRequired", "Only the next unattempted run can execute. Stops and interrupted outcomes never resume automatically.");
        adapter.RequireQualification(context, "execute"); // No native calls, ownership or checkpoint before this gate.
        EngineeringOperationRecord op;
        using (Control(id))
        {
            EnsureRunning(id); op = store.Acquire(operationId, expectedRevision);
        }
        ITestSuiteStyleSession? session = null;
        bool nativeReturned = false;
        try
        {
            session = await adapter.OpenExclusive(context with { Operation = op }, token);
            var before = await session.Observe(token); ValidateObservation(context, before);
            bool stopBeforeCheckpoint;
            using (Control(id))
            {
                stopBeforeCheckpoint = Stopped(id);
                if (!stopBeforeCheckpoint) op = store.BeginCheckpoint(op.Id, op.Revision, () => before.Target);
            }
            if (stopBeforeCheckpoint)
            {
                // Stopping releases the durable project reservation. Confirm native release first;
                // a lost/failed end response must reach the catch below while ownership is retained.
                var stoppedSession = session; session = null;
                await stoppedSession.DisposeAsync();
                using (Control(id)) store.Stop(op.Id, op.Revision);
                return Status(id);
            }
            var checkpoints = await session.Checkpoint(token);
            op = store.Checkpoint(op.Id, op.Revision, checkpoints);
            var fresh = await session.Observe(token); ValidateObservation(context, fresh);
            // Keep the worker's native mutex across reobservation, flushed intent and one invocation.
            using (Control(id))
            {
                EnsureRunning(id); token.ThrowIfCancellationRequested();
                op = store.Dispatch(op.Id, op.Revision, () => fresh.Target);
            }
            // A cancelled request after intent must not cancel a native call or make it replayable.
            var raw = await session.Run(CancellationToken.None);
            var rawArtifact = RetainBytes(raw); // Flush the unmodified response before decoding anything.
            op = store.RecordDispatchEvidence(op.Id, op.Revision, new("dispatch.rawResults", rawArtifact));
            nativeReturned = true;
            var captures = TestSuiteApplicationRun.Captures(context.Run, Parse<NativeTestResult[]>(raw));
            var results = TestSuiteResultNormalizer.Normalize(context.Run, captures);
            var verified = await session.Verify(CancellationToken.None);
            ValidateScope(context.Run, verified.Scope, verified.ScopeEvidenceKind, verified.ScopeEvidence, requireComplete: false);
            var scope = TestSuiteScopeCoverage.Compare(context.Run, verified.Scope);
            var assessment = TestSuiteScopeCoverage.Assess(context.Run, results, scope);
            var classification = Classify(results, scope, assessment);
            var result = new TestSuiteStyleRetainedResult(classification, rawArtifact, Retain(results), Retain(scope), Retain(assessment));
            op = store.RecordDispatchEvidence(op.Id, op.Revision, new("dispatch.result", Retain(result)));
            var evidence = new List<EngineeringGateEvidence>
            {
                new("testsuite.rawResults", rawArtifact), new("testsuite.results", result.Results), new("testsuite.scope", result.Scope)
            };
            string[] required = ["project.compile", "project.preservation", "project.freshReopen", "resource.cleanup"];
            if (verified.Gates is null || verified.Gates.Length != required.Length || required.Any(g => verified.Gates.Count(e => e.Gate == g) != 1))
                throw new BridgeException("VerificationIncomplete", "Compile, preservation, fresh reopen and cleanup require independent adapter observations.");
            foreach (var gate in verified.Gates) VerifyArtifact(gate.Artifact);
            evidence.AddRange(verified.Gates);
            var completedSession = session; session = null;
            await completedSession.DisposeAsync(); // Failed lease cleanup cannot release durable project ownership.
            if (classification == "passed")
                store.RecordOutcome(op.Id, op.Revision, true, TestSuiteStyleOperation.SuccessfulEvidence(context.Run, results, scope, evidence));
            else
                store.RecordOutcome(op.Id, op.Revision, false, evidence.Append(new EngineeringGateEvidence("failure", Retain(new { classification, result }))).ToArray());
        }
        catch (Exception error)
        {
            op = store.Read(operationId);
            var failure = Retain(new { classification = nativeReturned ? "incompleteEvidence" : "unknownOutcome", code = (error as BridgeException)?.Code,
                message = error.Message, automaticReplay = false });
            if (op.State == "dispatchIntent" && nativeReturned) store.RecordOutcome(op.Id, op.Revision, false, [new("failure", failure)]);
            else if (op.State is "acquired" or "checkpointIntent" or "ready" or "dispatchIntent") store.MarkUnknown(op.Id, op.Revision, failure);
            throw;
        }
        finally { if (session is not null) await session.DisposeAsync(); }
        return Status(id);
    }

    public TestSuiteStyleJobStatus MarkInterrupted(string id, string operationId, int expectedRevision)
    {
        using var held = Execution(id);
        var status = Status(id); _ = Context(status, operationId, expectedRevision);
        store.MarkUnknown(operationId, expectedRevision, Retain(new { classification = "unknownOutcome", reason = "Explicit interruption reconciliation requested", automaticReplay = false }));
        return Status(id);
    }

    public async Task<TestSuiteStyleJobStatus> Recover(string id, string reviewedSha256, string operationId, int expectedRevision,
        EngineeringRecoveryDestination[] destinations, ITestSuiteStyleAdapter adapter, CancellationToken token = default)
    {
        using var held = Execution(id);
        var status = Status(id); Match(status.Job, reviewedSha256);
        var context = Context(status, operationId, expectedRevision);
        adapter.RequireQualification(context, "recover");
        var op = store.ReserveRecovery(operationId, expectedRevision, destinations);
        try
        {
            await using var session = await adapter.OpenExclusive(context with { Operation = op }, token);
            var evidence = await session.Restore(op.RecoveryDestinations.ToArray(), CancellationToken.None);
            store.RecordRecoveryEvidence(op.Id, op.Revision, new("recovery.return", evidence));
        }
        catch (Exception error)
        {
            op = store.Read(operationId);
            if (op.State == "recoveryIntent") store.MarkUnknown(op.Id, op.Revision, Retain(new { error = error.Message, automaticReplay = false }));
            throw;
        }
        return Status(id);
    }

    public async Task<TestSuiteStyleJobStatus> Reconcile(string id, string reviewedSha256, string operationId, int expectedRevision,
        ITestSuiteStyleAdapter adapter, CancellationToken token = default)
    {
        using var held = Execution(id);
        var status = Status(id); Match(status.Job, reviewedSha256);
        var context = Context(status, operationId, expectedRevision);
        if (context.Operation.State is not ("outcomeUnknown" or "recoveryRequired" or "dispatchIntent" or "checkpointIntent" or "ready" or "recoveryIntent"))
            throw new BridgeException("RecoveryRequired", "Only an interrupted or unreconciled outcome can be reconciled.");
        adapter.RequireQualification(context, "reconcile");
        var session = await adapter.OpenExclusive(context, token);
        TestSuiteStyleReconciliation evidence;
        try { evidence = await session.Reconcile(token); }
        finally { await session.DisposeAsync(); }
        store.Reconcile(operationId, expectedRevision, evidence.Resources, evidence.Destinations);
        return Status(id);
    }

    public object Results(string id, string operationId)
    {
        var status = Status(id); var context = Context(status, operationId, null);
        var entry = context.Operation.Evidence.SingleOrDefault(e => e.Gate == "dispatch.result");
        var raw = context.Operation.Evidence.SingleOrDefault(e => e.Gate == "dispatch.rawResults");
        TestSuiteStyleRetainedResult? result = null;
        if (raw is not null) VerifyArtifact(raw.Artifact);
        if (entry is not null)
        {
            result = Read<TestSuiteStyleRetainedResult>(entry.Artifact);
            if (raw?.Artifact != result.Raw) throw new BridgeException("TestSuiteArtifactMismatch", "Report must bind the original retained response.");
            var normalized = TestSuiteResultNormalizer.Normalize(context.Run, TestSuiteApplicationRun.Captures(context.Run, Parse<NativeTestResult[]>(ReadBytes(result.Raw))));
            var scope = Read<TestSuiteScopeCoverageReport>(result.Scope);
            var assessment = TestSuiteScopeCoverage.Assess(context.Run, normalized, scope);
            if (Hash(normalized) != result.Results.Sha256 || Hash(assessment) != result.Assessment.Sha256 || result.Classification != Classify(normalized, scope, assessment))
                throw new BridgeException("TestSuiteArtifactMismatch", "Recomputed reports disagree with retained raw results and scope.");
            VerifyArtifact(result.Results); VerifyArtifact(result.Assessment);
        }
        return new { operation = context.Operation, result, raw = raw?.Artifact, nativeQualification = false };
    }

    public byte[] ReadBytes(EngineeringArtifactReference artifact)
    {
        string path = ArtifactPath(artifact);
        if (!File.Exists(path) || new FileInfo(path).Length > 64L * 1024 * 1024) throw new BridgeException("ArtifactChanged", "Missing or oversized retained Test Suite artifact.");
        var bytes = File.ReadAllBytes(path);
        if (BytesHash(bytes) != artifact.Sha256) throw new BridgeException("ArtifactChanged", "Retained Test Suite artifact changed.");
        return bytes;
    }
    public EngineeringArtifactReference RetainBytes(byte[] bytes)
    {
        if (bytes is null || bytes.Length is < 1 or > 64 * 1024 * 1024) throw new BridgeException("TestSuiteJobLimit", "Retained artifacts must be nonempty and at most 64 MiB.");
        string hash = BytesHash(bytes), relative = "testsuite-jobs/blobs/" + hash + ".json";
        string path = PackageStorage.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            try { File.Move(temp, path, false); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        }
        var result = new EngineeringArtifactReference(relative, hash); VerifyArtifact(result); return result;
    }
    public EngineeringArtifactReference Retain<T>(T value) => RetainBytes(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(JsonSerializer.SerializeToNode(value, Strict)!)));
    private T Read<T>(EngineeringArtifactReference artifact) => Parse<T>(ReadBytes(artifact));
    public static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in element.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException("Duplicate field."); Unique(p.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Unique(child);
                else if (element.ValueKind == JsonValueKind.String) _ = element.GetString();
            }
            // JsonDocument defers escaped UTF-16 validation until strings and property names are read.
            try { Unique(doc.RootElement); }
            catch (InvalidOperationException error) { throw new JsonException("Invalid Unicode string or field name.", error); }
            return doc.RootElement.Deserialize<T>(Strict) ?? throw new JsonException("Null document.");
        }
        catch (JsonException error) { throw new BridgeException("InvalidTestSuiteArtifact", "Test Suite JSON must have exact, unique, known fields.", error); }
    }
    private void ValidateObservation(TestSuiteStyleContext context, TestSuiteStyleObservation observation)
    {
        NativeTargetGuard.EnsureFresh(context.Operation.Plan.Target, observation.Target, context.Operation.Plan.RequiredProbes);
        if (observation.ProbeEvidence is null) throw new BridgeException("TargetPrerequisiteMissing", "Original product/license/service/scope observations are required.");
        foreach (var probe in observation.Target.Probes.Where(p => context.Operation.Plan.RequiredProbes.Contains(p.Name)))
        {
            var artifact = observation.ProbeEvidence.FirstOrDefault(e => e.Sha256 == probe.EvidenceSha256)
                ?? throw new BridgeException("TargetPrerequisiteMissing", "Probe artifact is absent: " + probe.Name);
            var target = observation.Target;
            var expected = new TestSuiteStyleProbeReceipt(probe.Name, probe.State, EngineeringResourceKey.ForPath("project", target.ProjectFilePath),
                target.SessionEpoch, target.AdapterSha256, target.ProfileSha256, target.Reference.Snapshot.SourceSha256,
                target.Reference.ContentSha256!, target.HardwareIdentity, target.NativeIdentity);
            if (Hash(Read<TestSuiteStyleProbeReceipt>(artifact)) != Hash(expected))
                throw new BridgeException("TargetPrerequisiteMissing", "Probe evidence belongs to another installation/profile/project/session: " + probe.Name);
        }
        if (observation.OriginalDefinitions is null || observation.OriginalDefinitions.Count != context.Run.Definitions.Length ||
            context.Run.Definitions.Any(d => !observation.OriginalDefinitions.TryGetValue(d.Id, out var bytes) || !bytes.AsSpan().SequenceEqual(context.Definitions.OriginalBytes(d.Id))))
            throw new BridgeException("StaleContent", "Native definitions differ from the restored reviewed bundle.");
        ValidateScope(context.Run, observation.Scope, observation.ScopeEvidenceKind, observation.ScopeEvidence, requireComplete: true);
    }
    private void ValidateScope(TestSuiteRunPlan run, TestSuiteScopeObservation[] scope, string kind, EngineeringArtifactReference evidence, bool requireComplete)
    {
        if (kind is not ("nativeReadback" or "qualifiedExhaustiveProof")) throw new BridgeException("TestSuiteScopeIncomplete", "SetScope returns and diagnostic mentions are not complete effective-scope evidence.");
        if (Hash(Read<TestSuiteStyleScopeReceipt>(evidence)) != Hash(new TestSuiteStyleScopeReceipt(run.PlanSha256, kind, scope)))
            throw new BridgeException("TestSuiteScopeIncomplete", "Independent scope evidence must bind this exact run and complete observation.");
        if (!TestSuiteScopeCoverage.Compare(run, scope).RequiredScopePassed && requireComplete)
            throw new BridgeException("TestSuiteScopeIncomplete", "Independent effective scope must exactly cover every reviewed object before checkpoint or execution.");
    }
    private TestSuiteStyleContext Context(TestSuiteStyleJobStatus status, string operationId, int? revision)
    {
        var op = status.Operations.SingleOrDefault(o => o.Id == operationId) ?? throw new BridgeException("InvalidTestSuiteJob", "Operation is outside this job.");
        if (revision is not null && op.Revision != revision) throw new BridgeException("RevisionConflict", "Reread the operation revision.");
        var review = ReadReview(status.Job.Review);
        var run = review.Runs[Array.IndexOf(status.Job.OperationIds, operationId)].Run;
        return new(root, run, TestSuiteDefinitionBundle.Restore(run, ReadBytes(op.Plan.Inputs[1])), op, review.ExecutionProfile) { Application = review.Application };
    }
    private sealed record StopRecord(string JobId, string ReviewSha256);
    private bool Stopped(string id)
    {
        if (!File.Exists(StopPath(id))) return false;
        var marker = PackageStorage.Load<StopRecord>(StopPath(id));
        if (marker.JobId != id || marker.ReviewSha256 != ReadJob(id).ReviewSha256) throw new BridgeException("InvalidTestSuiteJob", "Stop marker identity changed.");
        return true;
    }
    private TestSuiteStyleJob ReadJob(string id)
    {
        var job = PackageStorage.Load<TestSuiteStyleJob>(JobPath(id));
        if (!TestSuiteRunPlanner.Text(job.IdempotencyKey, 256) || job.Id != id || job.ReviewSha256 != job.Review.Sha256 || job.OperationIds is null || job.OperationIds.Length is < 1 or > 32 ||
            job.OperationIds.Distinct(StringComparer.Ordinal).Count() != job.OperationIds.Length || BytesHash(Encoding.UTF8.GetBytes(job.IdempotencyKey))[..32] != id)
            throw new BridgeException("InvalidTestSuiteJob", "Invalid retained style job identity.");
        return job;
    }
    private string JobPath(string id) => PackageStorage.Under(directory, PackageStorage.Id(id) + ".json");
    private string StopPath(string id) => PackageStorage.Under(directory, PackageStorage.Id(id) + ".stop.json");
    private FileStream Control(string id) => PackageStorage.Lock(PackageStorage.Under(directory, PackageStorage.Id(id) + ".control.lock"));
    private FileStream Execution(string id) => PackageStorage.Lock(PackageStorage.Under(directory, PackageStorage.Id(id) + ".execution.lock"), TimeSpan.Zero);
    private void EnsureRunning(string id) { if (Stopped(id)) throw new BridgeException("TestSuiteStopRequested", "The durable stop request prevents another dispatch; it cannot cancel an active call."); }
    private static void Match(TestSuiteStyleJob job, string hash) { if (job.ReviewSha256 != hash) throw new BridgeException("ReviewMismatch", "Exact complete reviewed job SHA-256 required."); }
    private string ArtifactPath(EngineeringArtifactReference artifact) => PackageStorage.Under(root, PackageStorage.Relative(artifact.RelativePath));
    private void VerifyArtifact(EngineeringArtifactReference artifact) { _ = ReadBytes(artifact); }
    private static string Classify(TestSuiteResultReport results, TestSuiteScopeCoverageReport scope, TestSuiteRunAssessment assessment) =>
        !scope.RequiredScopePassed || results.Results.Any(r => r.Outcome is "inconclusive" or "notRun" or "blocked" or "excluded") ? "incompleteEvidence" :
        results.Results.Any(r => r.Outcome == "executionError") ? "executionFailure" :
        results.Results.Any(r => r.Outcome == "assertionFailed") ? "assertionFailure" :
        assessment.RequiredCoveragePassed ? "passed" : "incompleteEvidence";
    private static string Hash<T>(T value) => TestSuiteStyleOperation.CanonicalHash(value);
    private static string BytesHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
