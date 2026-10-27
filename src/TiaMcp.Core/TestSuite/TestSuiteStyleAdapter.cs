using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.TestSuite;

public sealed record TestSuiteStyleContext(string ArtifactRoot, TestSuiteRunPlan Run,
    TestSuiteDefinitionBundle Definitions, EngineeringOperationRecord Operation, string ExecutionProfile)
{
    public TestSuiteApplicationBinding? Application { get; init; }
}
public sealed record TestSuiteStyleObservation(NativeTargetObservation Target,
    IReadOnlyDictionary<string, byte[]> OriginalDefinitions, TestSuiteScopeObservation[] Scope,
    string ScopeEvidenceKind, EngineeringArtifactReference ScopeEvidence,
    IReadOnlyList<EngineeringArtifactReference> ProbeEvidence);
public sealed record TestSuiteStyleVerification(TestSuiteScopeObservation[] Scope,
    string ScopeEvidenceKind, EngineeringArtifactReference ScopeEvidence, EngineeringGateEvidence[] Gates);
public sealed record TestSuiteStyleReconciliation(ResourceReconciliation[] Resources,
    EngineeringRecoveryResolution[] Destinations);
public sealed record TestSuiteStyleScopeReceipt(string PlanSha256, string EvidenceKind, TestSuiteScopeObservation[] Observations);
public sealed record TestSuiteStyleProbeReceipt(string Name, string State, string ProjectResourceKey, string SessionEpoch,
    string AdapterSha256, string ProfileSha256, string SourceSha256, string ContentSha256, string HardwareIdentity, string? NativeIdentity);

/// <summary>Trusted adapter seam, never supplied by an MCP request. Qualification precedes even opening a native lease.</summary>
public interface ITestSuiteStyleAdapter
{
    void RequireQualification(TestSuiteStyleContext context, string action);
    Task<ITestSuiteStyleSession> OpenExclusive(TestSuiteStyleContext context, CancellationToken token);
}

/// <summary>
/// Own the worker's exclusive native access until DisposeAsync. Run invokes exactly once and returns
/// unmodified UTF-8 NativeTestResult[] JSON. No import, SetScope, simulator, retry or cancellation route.
/// Checkpoint must include a genuine archive and preservation evidence; Verify observes compile,
/// preservation, a fresh reopened epoch and cleanup. Recovery never overwrites the original.
/// </summary>
public interface ITestSuiteStyleSession : IAsyncDisposable
{
    Task<TestSuiteStyleObservation> Observe(CancellationToken token);
    Task<EngineeringArtifactReference[]> Checkpoint(CancellationToken token);
    Task<byte[]> Run(CancellationToken token);
    Task<TestSuiteStyleVerification> Verify(CancellationToken token);
    Task<EngineeringArtifactReference> Restore(EngineeringRecoveryDestination[] destinations, CancellationToken token);
    Task<TestSuiteStyleReconciliation> Reconcile(CancellationToken token);
}

/// <summary>
/// Production admission has no accepted profile. The retained V21 availability call had no complete
/// effective-scope proof and cannot authorize execution, checkpoint creation or archive retrieval.
/// This is deliberately code-owned: no request Boolean, fixture receipt or Host flag can enable it.
/// </summary>
public sealed class TestSuiteProductionAdapter : ITestSuiteStyleAdapter
{
    private readonly ITestSuiteStyleAdapter? worker;
    public TestSuiteProductionAdapter() { }
    public TestSuiteProductionAdapter(IBridgeClient owningWorker) => worker = new TestSuiteWorkerAdapter(owningWorker, RequireQualification);
    public static string[] QualifiedProfiles => [];
    public void RequireQualification(TestSuiteStyleContext context, string action) =>
        throw new BridgeException("TestSuiteProfileUnqualified",
            "No production Test Suite " + (context?.Run?.ExecutionMode ?? "style") + " " + action + " profile has native qualification. TS-Q01/TS-Q02 require current product/license observations, independent complete effective scope, preservation, fresh reopen and recovery evidence.");
    public Task<ITestSuiteStyleSession> OpenExclusive(TestSuiteStyleContext context, CancellationToken token)
    {
        RequireQualification(context, "openExclusive");
        return (worker ?? throw new InvalidOperationException("Owning worker is not registered.")).OpenExclusive(context, token);
    }
}
