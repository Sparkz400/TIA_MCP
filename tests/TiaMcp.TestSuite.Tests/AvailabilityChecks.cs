using TiaMcp.Core;
using TiaMcp.Core.TestSuite;

internal static class AvailabilityChecks
{
    public static int Run()
    {
        int checks = 0;
        const string epoch = "synthetic-session";
        string api = new('a', 64), project = Path.Combine(Path.GetTempPath(), "availability.synthetic.ap21");
        TestSuiteAvailabilityObservation Observation(string operation, string state) => new(epoch, api, project, operation, state,
            new('b', 64), operation.EndsWith(".run") && state == "returned" ? new('c', 64) : null);
        TestSuiteAvailabilityAssessment Assess(params TestSuiteAvailabilityObservation[] o) => TestSuiteAvailability.Assess(epoch, api, project, "style", o);
        void Check(bool condition) { if (!condition) throw new Exception("Availability evidence was incorrectly promoted."); checks++; }
        void Reject(Action action, string code) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        foreach (string invalid in IdentityTextChecks.InvalidText)
            Reject(() => TestSuiteAvailability.Assess(invalid, api, project, "style", []), "InvalidTestSuiteAvailability");
        Check(TestSuiteAvailability.Assess("epoch-😀-e\u0301-\ufffd", api, project, "style", []) is { NativeExecutionAuthorized: false, License: "unknown" });
        var service = Observation("service", "observed");
        Check(Assess().License == "unknown");
        Check(Assess(service) is { Service: "observed", License: "unknown", Execution: "notAttempted" });
        Check(Assess(service, Observation("style.import", "returned")) is { License: "definitionOperationOnly", Execution: "notAttempted" });
        Check(Assess(service, Observation("style.export", "returned")) is { NativeExecutionAuthorized: false, TestsPassed: false });
        Check(Assess(service, Observation("style.import", "licenseNotFound")) is { License: "refusedForDefinitionOperation", Execution: "notAttempted" });
        Check(Assess(service, Observation("style.run", "licenseNotFound")) is { License: "refusedForExecution", Execution: "licenseNotFound" });
        Check(Assess(service, Observation("style.run", "failed")).License == "unknown");
        var returned = Assess(service, Observation("style.run", "returned"));
        Check(returned is { License: "acceptedForObservedExecution", Execution: "returned", NativeExecutionAuthorized: false, TestsPassed: false });
        Check(returned.EvidenceSha256.Length == 2);
        Check(TestSuiteAvailability.Assess(epoch, api, project, "systemManaged", [service, Observation("style.run", "returned")]) is { License: "unknown", Execution: "notAttempted" });
        Check(TestSuiteAvailability.Assess(epoch, api, project, "externallyManaged", [service, Observation("application.systemManaged.run", "returned")]).License == "unknown");
        Reject(() => Assess(service, service), "AmbiguousTestSuiteAvailability");
        Reject(() => Assess(Observation("service", "absent"), Observation("style.run", "returned")), "AmbiguousTestSuiteAvailability");
        Reject(() => Assess(service with { SessionEpoch = "fresh-session" }), "StaleTestSuiteAvailability");
        Reject(() => Assess(service with { ApiSha256 = new('d', 64) }), "StaleTestSuiteAvailability");
        Reject(() => Assess(service with { ProjectFilePath = project + ".other" }), "StaleTestSuiteAvailability");
        Reject(() => Assess(Observation("style.run", "returned") with { RawResultSha256 = null }), "InvalidTestSuiteAvailability");
        Reject(() => Assess(service with { RawResultSha256 = new('d', 64) }), "InvalidTestSuiteAvailability");
        Reject(() => Assess(Observation("service", "returned")), "InvalidTestSuiteAvailability");
        Reject(() => Assess(Observation("style.run", "observed")), "InvalidTestSuiteAvailability");
        Reject(() => Assess(service, Observation("style.import", "returned"), Observation("style.export", "licenseNotFound")), "AmbiguousTestSuiteAvailability");
        return checks;
    }
}
