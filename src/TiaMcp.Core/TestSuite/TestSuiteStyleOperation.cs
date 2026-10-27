using System.Text.Json;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core.TestSuite;

/// <summary>Domain binding to the shared write-ahead store. This class never invokes a native test.</summary>
public static class TestSuiteStyleOperation
{
    private static readonly string[] Probes = ["testsuite.product", "testsuite.license", "testsuite.styleService", "testsuite.scope"];
    private static readonly string[] ApplicationProbes = ["testsuite.product", "testsuite.license", "testsuite.applicationService", "testsuite.scope", "simulator.ownedInstance"];
    private static readonly string[] Gates = ["testsuite.results", "testsuite.scope", "testsuite.rawResults", "project.compile", "project.preservation", "project.freshReopen", "resource.cleanup"];

    public static EngineeringOperationPlan BuildPlan(TestSuiteRunPlan run, NativeTargetObservation target,
        EngineeringArtifactReference runArtifact, EngineeringArtifactReference definitionBundleArtifact,
        TestSuiteDefinitionBundle definitions)
    {
        TestSuiteRunPlanner.Validate(run);
        bool application = TestSuiteApplicationRun.Applies(run.ExecutionMode);
        if (run.ExecutionMode != "style" && !application || run.Blockers.Length != 0)
            throw new BridgeException("TestSuiteStyleNotReady", "A blocker-free style or application plan is required; system execution needs separate endpoint admission.");
        var probes = application ? ApplicationProbes : Probes;
        NativeTargetGuard.EnsureFresh(target, target, probes);
        foreach (var definition in run.Definitions) EngineeringIdentityGuard.EnsureSameSnapshot(target.Reference, definition.Reference);
        string projectResourceKey = EngineeringResourceKey.ForPath("project", target.ProjectFilePath);
        ValidateArtifact(runArtifact); ValidateArtifact(definitionBundleArtifact);
        if (definitions is null) throw new BridgeException("InvalidDefinitionBundle", "The original definition bundle must be verified before admission.");
        definitions.EnsureMatches(run);
        if (runArtifact.RelativePath == definitionBundleArtifact.RelativePath || runArtifact.Sha256 != CanonicalHash(run) ||
            definitionBundleArtifact.Sha256 != definitions.Sha256)
            throw new BridgeException("TestSuiteArtifactMismatch", "Retain the exact canonical run plan and its verified original-definition bundle as separate artifacts.");
        // Application runs also persist SetScope on reviewed cases; the same checkpoint precedes it.
        return new(application ? "testSuite.application.run" : "testSuite.style.run", target,
            [new(projectResourceKey, "project", application ? "nativeApplicationScopeExecutionAndCheckpoint" : "nativeStyleExecutionAndCheckpoint", "projectArchiveToNewLocation")],
            [runArtifact, definitionBundleArtifact], probes.ToArray(), Gates.ToArray());
    }

    /// <summary>Successful native outcomes need separately retained scope evidence; raw ScopeVerified alone is insufficient.</summary>
    public static EngineeringGateEvidence[] SuccessfulEvidence(TestSuiteRunPlan run, TestSuiteResultReport results,
        TestSuiteScopeCoverageReport scope, IReadOnlyList<EngineeringGateEvidence> evidence)
    {
        var assessment = TestSuiteScopeCoverage.Assess(run, results, scope);
        if (!assessment.RequiredCoveragePassed)
            throw new BridgeException("TestSuiteCoverageIncomplete", "Results and independently observed exact scope must both pass before recording successful completion.");
        if (evidence is null || evidence.Count != Gates.Length || evidence.Any(e => e is null) ||
            evidence.Select(e => e.Gate).Distinct(StringComparer.Ordinal).Count() != evidence.Count || Gates.Any(g => !evidence.Any(e => e.Gate == g)))
            throw new BridgeException("VerificationIncomplete", "All Test Suite result, scope, raw, compile, preservation, reopen and cleanup gates are required.");
        foreach (var item in evidence) ValidateArtifact(item.Artifact);
        if (evidence.Single(e => e.Gate == "testsuite.results").Artifact.Sha256 != assessment.ResultReportSha256 ||
            evidence.Single(e => e.Gate == "testsuite.scope").Artifact.Sha256 != assessment.ScopeReportSha256)
            throw new BridgeException("TestSuiteArtifactMismatch", "Result and scope evidence must identify the canonical validated reports.");
        return evidence.ToArray();
    }

    private static void ValidateArtifact(EngineeringArtifactReference artifact)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.RelativePath))
            throw new BridgeException("TestSuiteArtifactMismatch", "An exact retained artifact reference is required.");
        EngineeringIdentityGuard.Sha256(artifact.Sha256, "artifactSha256");
    }

    public static string CanonicalHash<T>(T value) => CanonicalJson.Sha256(JsonSerializer.SerializeToNode(value, BridgeProtocol.Json)!);
}
