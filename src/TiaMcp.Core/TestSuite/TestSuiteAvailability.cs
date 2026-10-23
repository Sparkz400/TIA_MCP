using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

/// <summary>An operation-specific native observation, not a transferable license entitlement.</summary>
public sealed record TestSuiteAvailabilityObservation(string SessionEpoch, string ApiSha256, string ProjectFilePath,
    string Operation, string State, string EvidenceSha256, string? RawResultSha256 = null);

public sealed record TestSuiteAvailabilityAssessment(string Service, string License, string Execution,
    string[] EvidenceSha256)
{
    public bool NativeExecutionAuthorized => false;
    public bool TestsPassed => false;
}

/// <summary>Separates installation/service discovery, license refusal and an observed executor return.</summary>
public static class TestSuiteAvailability
{
    private static readonly string[] Operations = ["service", "style.import", "style.export", "style.run",
        "application.import", "application.export", "application.systemManaged.run", "application.externallyManaged.run",
        "system.import", "system.export", "system.run"];
    private static readonly string[] States = ["observed", "absent", "returned", "licenseNotFound", "failed", "unknown"];

    public static TestSuiteAvailabilityAssessment Assess(string sessionEpoch, string apiSha256, string projectFilePath,
        string executionMode, IReadOnlyList<TestSuiteAvailabilityObservation> observations)
    {
        if (!TestSuiteRunPlanner.Text(sessionEpoch, 8192) ||
            observations is null || observations.Count > 1000 || observations.Any(o => o is null))
            throw new BridgeException("InvalidTestSuiteAvailability", "Exact session identity and bounded observations are required.");
        EngineeringIdentityGuard.Sha256(apiSha256, "apiSha256");
        string project = EngineeringJobs.EngineeringResourceKey.ForPath("project", projectFilePath);
        string run = executionMode switch { "style" => "style.run", "systemManaged" => "application.systemManaged.run",
            "externallyManaged" => "application.externallyManaged.run", "endpoint" => "system.run",
            _ => throw new BridgeException("InvalidTestSuiteAvailability", "An exact execution mode is required.") };
        string family = run.Split('.')[0];
        foreach (var item in observations)
        {
            if (item.SessionEpoch != sessionEpoch || item.ApiSha256 != apiSha256 ||
                EngineeringJobs.EngineeringResourceKey.ForPath("project", item.ProjectFilePath) != project)
                throw new BridgeException("StaleTestSuiteAvailability", "Availability cannot transfer across project, native session or installed API.");
            if (!Operations.Contains(item.Operation) || !States.Contains(item.State) ||
                item.Operation == "service" && item.State is not ("observed" or "absent" or "failed" or "unknown") ||
                item.Operation != "service" && item.State is "observed" or "absent")
                throw new BridgeException("InvalidTestSuiteAvailability", "Invalid operation-specific observation state.");
            EngineeringIdentityGuard.Sha256(item.EvidenceSha256, "evidenceSha256");
            bool returnedRun = item.Operation.EndsWith(".run", StringComparison.Ordinal) && item.State == "returned";
            if (returnedRun != (item.RawResultSha256 is not null))
                throw new BridgeException("InvalidTestSuiteAvailability", "A returned executor call requires its retained raw result; other observations cannot claim one.");
            if (item.RawResultSha256 is not null) EngineeringIdentityGuard.Sha256(item.RawResultSha256, "rawResultSha256");
        }
        // Conflicting/repeated observations require a new explicit snapshot; array order cannot silently select truth.
        if (observations.Select(o => o.Operation).Distinct(StringComparer.Ordinal).Count() != observations.Count)
            throw new BridgeException("AmbiguousTestSuiteAvailability", "Only one observation per operation belongs in an availability snapshot.");
        string service = observations.SingleOrDefault(o => o.Operation == "service")?.State ?? "unknown";
        var execution = observations.SingleOrDefault(o => o.Operation == run);
        string license = "unknown";
        if (execution?.State == "licenseNotFound") license = "refusedForExecution";
        else if (execution?.State == "returned") license = "acceptedForObservedExecution";
        else
        {
            var definitions = observations.Where(o => o.Operation.StartsWith(family + ".", StringComparison.Ordinal) &&
                !o.Operation.EndsWith(".run", StringComparison.Ordinal)).ToArray();
            if (definitions.Any(o => o.State == "returned") && definitions.Any(o => o.State == "licenseNotFound"))
                throw new BridgeException("AmbiguousTestSuiteAvailability", "Definition license acceptance/refusal conflicts; retain separate dated snapshots.");
            if (definitions.Any(o => o.State == "licenseNotFound")) license = "refusedForDefinitionOperation";
            else if (definitions.Any(o => o.State == "returned")) license = "definitionOperationOnly";
        }
        if (service == "absent" && observations.Any(o => o.State == "returned"))
            throw new BridgeException("AmbiguousTestSuiteAvailability", "Absent service conflicts with a returned operation in the same snapshot.");
        return new(service, license, execution?.State ?? "notAttempted",
            observations.SelectMany(o => o.RawResultSha256 is null ? new[] { o.EvidenceSha256 } : new[] { o.EvidenceSha256, o.RawResultSha256 })
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}
