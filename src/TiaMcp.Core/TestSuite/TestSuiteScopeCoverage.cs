using System.Text.Json;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

/// <summary>Adapter-supplied observations, separate from requested scope and result success labels.</summary>
public sealed record TestSuiteScopeObservation(string DefinitionId, EngineeringObjectReference DefinitionReference,
    EngineeringObjectReference[] EffectiveScope, EngineeringObjectReference[] IgnoredScope, bool ObservationComplete);
public sealed record TestSuiteScopeCoverageEntry(string DefinitionId, string Status,
    EngineeringObjectReference[] RequiredScope, EngineeringObjectReference[] EffectiveScope,
    EngineeringObjectReference[] MissingScope, EngineeringObjectReference[] UnexpectedScope,
    EngineeringObjectReference[] IgnoredScope, bool ObservationComplete);
public sealed record TestSuiteScopeCoverageReport(string PlanSha256, TestSuiteScopeCoverageEntry[] Entries,
    bool RequiredScopePassed, bool NativeExecutedByComparer = false, bool ReleaseAccepted = false);
public sealed record TestSuiteRunAssessment(string PlanSha256, string ResultReportSha256, string ScopeReportSha256,
    bool RequiredCoveragePassed, bool NativeExecutionAuthorized = false, bool ReleaseAccepted = false);
/// <summary>Objects independently matched to retained result diagnostics; silence is not exclusion or coverage proof.</summary>
public sealed record TestSuiteDiagnosticScopeEvidence(string DefinitionId, EngineeringObjectReference DefinitionReference,
    EngineeringObjectReference[] MentionedObjects, string RawResultSha256);
public sealed record TestSuiteDiagnosticScopeReport(TestSuiteScopeCoverageReport Coverage, string[] RawResultSha256)
{
    public bool NativeExecutionAuthorized => false;
    public bool CompleteEffectiveScopeVerified => false;
}

public static class TestSuiteScopeCoverage
{
    public static TestSuiteDiagnosticScopeReport CompareDiagnosticEvidence(TestSuiteRunPlan plan,
        TestSuiteDiagnosticScopeEvidence[] evidence)
    {
        if (evidence is null || evidence.Length > 1000 || evidence.Any(e => e is null))
            throw new BridgeException("InvalidScopeCoverage", "Bounded retained diagnostic evidence is required.");
        foreach (var item in evidence) EngineeringIdentityGuard.Sha256(item.RawResultSha256, "rawResultSha256");
        // Even an exact list of diagnostic mentions cannot prove that silent objects were not also evaluated.
        // Never turn result counts, SetScope success or expected violation text into a complete scope observation.
        var observations = evidence.Select(e => new TestSuiteScopeObservation(e.DefinitionId, e.DefinitionReference,
            e.MentionedObjects, [], ObservationComplete: false)).ToArray();
        return new(Compare(plan, observations), evidence.Select(e => e.RawResultSha256).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    public static TestSuiteScopeCoverageReport Compare(TestSuiteRunPlan plan, TestSuiteScopeObservation[] observations)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (observations is null || observations.Length > 1000 || observations.Any(o => o is null || string.IsNullOrWhiteSpace(o.DefinitionId)) ||
            observations.Select(o => o.DefinitionId).Distinct(StringComparer.Ordinal).Count() != observations.Length)
            throw new BridgeException("InvalidScopeCoverage", "Scope observations must have unique exact definition IDs within the 1,000-definition bound.");
        var definitions = plan.Definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var observed = observations.ToDictionary(o => o.DefinitionId, StringComparer.Ordinal);
        long references = 0;
        foreach (var observation in observations)
        {
            if (!definitions.TryGetValue(observation.DefinitionId, out var definition))
                throw new BridgeException("UnexpectedScopeCoverage", "Scope observation belongs to an unrequested definition.");
            EngineeringIdentityGuard.EnsureFresh(definition.Reference, observation.DefinitionReference);
            ValidateScope(observation.EffectiveScope, definition.Reference);
            ValidateScope(observation.IgnoredScope, definition.Reference);
            references += observation.EffectiveScope.Length + observation.IgnoredScope.Length;
            if (references > 100000) throw new BridgeException("ScopeCoverageLimit", "Observed scope exceeds 100,000 references.");
            if (observation.EffectiveScope.Select(r => r.Object).Intersect(observation.IgnoredScope.Select(r => r.Object)).Any())
                throw new BridgeException("InvalidScopeCoverage", "An object cannot be both effectively included and explicitly ignored.");
        }
        var entries = new List<TestSuiteScopeCoverageEntry>();
        foreach (var definition in plan.Definitions)
        {
            observed.TryGetValue(definition.Id, out var observation);
            var effective = observation?.EffectiveScope ?? [];
            var ignored = observation?.IgnoredScope ?? [];
            var requiredIndex = definition.RequiredScope.ToDictionary(r => r.Object);
            var effectiveIndex = effective.ToDictionary(r => r.Object);
            // A matching path with stale content is not acceptable scope coverage.
            foreach (var reference in effective.Concat(ignored))
                if (requiredIndex.TryGetValue(reference.Object, out var expected)) EngineeringIdentityGuard.EnsureFresh(expected, reference);
            var missing = definition.RequiredScope.Where(r => !effectiveIndex.ContainsKey(r.Object)).ToArray();
            var unexpected = effective.Where(r => !requiredIndex.ContainsKey(r.Object)).ToArray();
            string status = plan.ExcludedDefinitionIds.Contains(definition.Id, StringComparer.Ordinal) ? "excluded" :
                observation is null ? "notObserved" : !observation.ObservationComplete ? "incomplete" :
                missing.Length != 0 || unexpected.Length != 0 || ignored.Length != 0 ? "mismatch" : "covered";
            entries.Add(new(definition.Id, status, definition.RequiredScope.ToArray(), effective.ToArray(), missing, unexpected,
                ignored.ToArray(), observation?.ObservationComplete ?? false));
        }
        return new(plan.PlanSha256, entries.ToArray(), plan.Blockers.Length == 0 && entries.All(e => e.Status == "covered"));
    }

    private static void ValidateScope(EngineeringObjectReference[] references, EngineeringObjectReference definition)
    {
        if (references is null || references.Length > 10000)
            throw new BridgeException("ScopeCoverageLimit", "An explicit scope array of at most 10,000 objects is required.");
        foreach (var reference in references) EngineeringIdentityGuard.EnsureSameSnapshot(definition, reference);
        if (references.Select(r => r.Object).Distinct().Count() != references.Length)
            throw new BridgeException("InvalidScopeCoverage", "Duplicate effective or ignored scope objects.");
    }

    public static void Validate(TestSuiteRunPlan plan, TestSuiteScopeCoverageReport report)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (report is null || report.Entries is null || report.Entries.Length != plan.Definitions.Length ||
            report.Entries.Any(e => e is null || e.RequiredScope is null || e.MissingScope is null || e.UnexpectedScope is null) ||
            !report.Entries.Select(e => e.DefinitionId).SequenceEqual(plan.RequiredDefinitionIds) ||
            report.PlanSha256 != plan.PlanSha256 || report.NativeExecutedByComparer || report.ReleaseAccepted)
            throw new BridgeException("InvalidScopeCoverage", "Scope report must retain exact plan coverage and offline provenance.");
        var definitions = plan.Definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var observations = report.Entries.Where(e => e.Status != "notObserved" && !(e.Status == "excluded" && !e.ObservationComplete && e.EffectiveScope?.Length == 0 && e.IgnoredScope?.Length == 0))
            .Select(e => new TestSuiteScopeObservation(e.DefinitionId, definitions[e.DefinitionId].Reference, e.EffectiveScope, e.IgnoredScope, e.ObservationComplete)).ToArray();
        var derived = Compare(plan, observations);
        if (CanonicalJson.Serialize(JsonSerializer.SerializeToNode(report, BridgeProtocol.Json)!) !=
            CanonicalJson.Serialize(JsonSerializer.SerializeToNode(derived, BridgeProtocol.Json)!))
            throw new BridgeException("InvalidScopeCoverage", "Scope status, requested objects or gaps differ from independently recomputed coverage.");
    }

    public static TestSuiteRunAssessment Assess(TestSuiteRunPlan plan, TestSuiteResultReport results, TestSuiteScopeCoverageReport scope)
    {
        TestSuiteResultNormalizer.ValidateReport(plan, results);
        Validate(plan, scope);
        return new(plan.PlanSha256, CanonicalJson.Sha256(JsonSerializer.SerializeToNode(results, BridgeProtocol.Json)!),
            CanonicalJson.Sha256(JsonSerializer.SerializeToNode(scope, BridgeProtocol.Json)!), results.RequiredCoveragePassed && scope.RequiredScopePassed);
    }
}
