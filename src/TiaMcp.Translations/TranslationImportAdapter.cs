using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Translations;

public sealed record TranslationImportScope(string SourceCulture, string TargetCulture, string[] ActiveCultures,
    string[] ObjectSelections, string[] CategoryIds);
public sealed record TranslationImportPeer(string Identity, string Sha256, EngineeringArtifactReference Evidence,
    string ComparisonProfile = "exact");
public sealed record TranslationImportObservation(NativeTargetObservation Target, byte[] Workbook,
    TranslationImportScope Scope, TranslationImportPeer[] Peers, byte[] Receipt);
public sealed record TranslationObservationReceipt(NativeTargetObservation Target, string WorkbookSha256,
    string ScopeSha256, string PeersSha256);
public sealed record TranslationImportContext(string ArtifactRoot, EngineeringOperationRecord Operation,
    string CandidateRef, string CandidateReceiptSha256, string CandidateSha256, TranslationImportScope Scope);
public sealed record TranslationImportCheckpoint(byte[] Archive, byte[] Receipt);
public sealed record TranslationCheckpointReceipt(string OperationId, string PlanSha256, string ProjectResourceKey,
    string SessionEpoch, string ArchiveSha256, string BaselineContentSha256);
public sealed record TranslationImportReturn(byte[] RawResult, byte[] CompleteLog);
public sealed record TranslationImportResultReceipt(string OperationId, string PlanSha256, string ProjectResourceKey,
    string CandidateSha256, bool Accepted, bool Complete, int ErrorCount, string LogSha256);
public sealed record TranslationImportStepReceipt(string OperationId, string PlanSha256, string ProjectResourceKey,
    string Step, string Disposition, string Detail);
public sealed record TranslationRecoveryReadback(TranslationImportObservation? Present, byte[]? AbsentReceipt);
public sealed record TranslationRecoveryAbsenceReceipt(string OperationId, string PlanSha256, string ProjectResourceKey,
    string RecoveryLocation, string AdapterSha256, string State, string Detail);

/// <summary>Trusted, code-owned seam. No caller/MCP object or Boolean supplies native admission.</summary>
public interface ITranslationImportAdapter
{
    void RequireQualification(TranslationImportContext context, string action);
    Task<ITranslationImportSession> OpenExclusive(TranslationImportContext context, CancellationToken token);
}

/// <summary>
/// One worker exclusive scope across fresh export, checkpoint, second export, durable intent and import.
/// Observe must export the COMPLETE selected native scope and independently inventory every unaffected
/// project peer/language. No implicit save, language activation, retry or native cancellation is allowed.
/// Reopen uses a fresh native session; recovery reads/restore target only the recorded new destination.
/// Restore must recheck that the reserved directory is absent immediately before retrieval and must
/// never overwrite it. Close supplies exact lifetime/cleanup evidence; DisposeAsync also cleans exceptions.
/// </summary>
public interface ITranslationImportSession : IAsyncDisposable
{
    Task<TranslationImportObservation> Observe(CancellationToken token);
    Task<TranslationImportCheckpoint> Checkpoint(CancellationToken token);
    Task<TranslationImportReturn> Import(byte[] exactCandidate, CancellationToken token);
    Task<byte[]> Compile(CancellationToken token);
    Task<byte[]> Save(CancellationToken token);
    Task<TranslationImportObservation> Reopen(CancellationToken token);
    Task<byte[]> Restore(byte[] archive, EngineeringRecoveryDestination destination, CancellationToken token);
    Task<TranslationRecoveryReadback> ObserveRecovered(EngineeringRecoveryDestination destination, bool freshReopen, CancellationToken token);
    Task<byte[]> Close(CancellationToken token);
}

/// <summary>No production profile is admitted. Neither a request nor a retained offline receipt opens this gate.</summary>
public sealed class TranslationProductionImportAdapter : ITranslationImportAdapter
{
    private readonly TranslationProductionRegistry? registry;
    private readonly TranslationWorkerAdapter? worker;
    private readonly string? profileId;
    public TranslationProductionImportAdapter() { }
    internal TranslationProductionImportAdapter(TranslationProductionRegistry registry, TranslationWorkerAdapter worker, string profileId)
    { this.registry = registry; this.worker = worker; this.profileId = profileId; }
    // No compiled-in profiles; operator-reviewed deployment entries are verified
    // separately on every action without changing the qualified executable.
    public static string[] QualifiedProfiles => [];
    public void RequireQualification(TranslationImportContext context, string action)
    {
        if (registry is not null && worker is not null && profileId is not null)
        { registry.RequireAdmission(profileId, context); return; }
        throw new BridgeException("TranslationImportProfileUnqualified",
            "Native translation " + action + " is disabled: accepted whole-project no-op/edit evidence does not yet cover " +
            "every exact production admission case and current binary. Restricted categories remain unqualified.");
    }
    public Task<ITranslationImportSession> OpenExclusive(TranslationImportContext context, CancellationToken token)
    {
        RequireQualification(context, "openExclusive");
        return worker!.OpenExclusive(context, token);
    }
}
