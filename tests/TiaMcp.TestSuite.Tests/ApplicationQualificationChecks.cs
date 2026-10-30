using System.Text;
using System.Text.Json;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

// TS-Q02 qualification-mode gate: only the exact owned fixture case, externally managed, in this Host's owned scratch.
internal static class ApplicationQualificationChecks
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool yes, string message) { if (!yes) throw new Exception(message); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected refusal " + code);
        }
        Directory.CreateDirectory(root);
        string profile = TestSuiteApplicationQualification.Profile;
        var reference = new EngineeringObjectReference(new("Project/A", "PLC/A", "PLC/A", "PLC/A", "plcSoftware"), new("q02", new('a', 64)), new('a', 64));
        TestSuiteDefinitionCapture Case(string name, string family = "application", string? id = null)
        {
            var bytes = Encoding.ASCII.GetBytes("TEST_CASE \"" + name + "\"");
            return new(id ?? name, family, name, ".tat", reference with { Object = reference.Object with { ObjectPath = "PLC/A/testsuite/application/" + name, Kind = "applicationTest" },
                ContentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) }, bytes, profile, [reference], [], true, true, []);
        }
        string[] probes = ["testsuite.product", "testsuite.license", "testsuite.applicationService", "testsuite.scope", "simulator.ownedInstance"];
        NativeTargetObservation Target(string project) => new(reference, project, "epoch", new('c', 64), new('d', 64), "hardware", null, true,
            probes.Select(p => new TargetProbe(p, "observed", new('e', 64))).ToArray());
        string owned = Path.Combine(root, "scratch", "TiaMcpScratch_1", "TiaMcpScratch_1.ap21");
        var jobs = new TestSuiteStyleJobs(root);
        var pass = Case("TiaMcp_Q02_Pass");
        var external = new TestSuiteApplicationBinding("externallyManaged", "opaque-dpapi-ticket", false);
        TestSuiteStyleContext Context(TestSuiteDefinitionCapture[] cases, string[] selected, TestSuiteApplicationBinding binding, string? project = null, string? executionProfile = null)
        {
            var review = jobs.Review(cases, [new(selected, [])], Target(project ?? owned), executionProfile ?? profile, binding);
            var status = jobs.Prepare(review, review.Sha256, Guid.NewGuid().ToString("N"));
            return new(root, jobs.ReadReview(review).Runs[0].Run, null!, status.Operations[0], executionProfile ?? profile) { Application = binding };
        }
        void Admit(TestSuiteStyleContext context) => TestSuiteApplicationQualification.Admit(context, root);

        Admit(Context([pass], [pass.Manifest.Id], external)); checks++;
        Admit(Context([Case("TiaMcp_Q02_Fail")], ["TiaMcp_Q02_Fail"], external)); checks++;
        Reject(() => Admit(Context([pass], [pass.Manifest.Id], external, executionProfile: "v21.owned-root-codeblocks.ui-reviewed.qualification-v1")), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([pass], [pass.Manifest.Id], external) with { Application = null }), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([pass], [pass.Manifest.Id], external with { ExecutionMode = "systemManaged" })), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([pass], [pass.Manifest.Id], external with { WholeGroup = true })), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([pass, Case("TiaMcp_Q02_Fail")], [pass.Manifest.Id, "TiaMcp_Q02_Fail"], external)), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([Case("UserCase")], ["UserCase"], external)), "TestSuiteProfileUnqualified");
        Reject(() => Admit(Context([Case("TiaMcp_Q02_Pass", id: "other")], ["other"], external)), "TestSuiteProfileUnqualified");
        foreach (string project in new[] { Path.Combine(root, "user", "Plant.ap21"), Path.Combine(root, "scratch-other", "P.ap21"), Path.Combine(Path.GetTempPath(), "Plant.ap21") })
            Reject(() => Admit(Context([pass], [pass.Manifest.Id], external, project)), "ProjectIsolationRequired");

        // The gate precedes every native call: refused reviews never reach the bridge or acquire the project.
        var wire = new Wire();
        var adapter = new TestSuiteWorkerAdapter(wire, (context, action) => TestSuiteApplicationQualification.Admit(context, root));
        async Task<TestSuiteStyleJobStatus> Execute(TestSuiteDefinitionCapture[] cases, TestSuiteApplicationBinding binding, string? project = null)
        {
            var review = jobs.Review(cases, [new([cases[0].Manifest.Id], [])], Target(project ?? owned), profile, binding);
            var status = jobs.Prepare(review, review.Sha256, Guid.NewGuid().ToString("N"));
            try { await jobs.ExecuteNext(status.Job.Id, review.Sha256, status.Operations[0].Id, 0, adapter); }
            catch (BridgeException error) { wire.Codes.Add(error.Code); }
            return jobs.Status(status.Job.Id);
        }
        foreach (var (binding, project, code) in new[] { (external with { ExecutionMode = "systemManaged" }, owned, "TestSuiteProfileUnqualified"),
            (external, Path.Combine(root, "user", "Other.ap21"), "ProjectIsolationRequired") })
        {
            var refused = Execute([pass], binding, project).GetAwaiter().GetResult();
            Check(wire.Codes.Last() == code && wire.Calls.Count == 0 && refused.Operations[0] is { State: "prepared", Revision: 0 },
                code + " refuses before the owning-worker lease, acquisition or checkpoint");
        }
        var admitted = Execute([pass], external).GetAwaiter().GetResult();
        Check(wire.Calls.SequenceEqual(["testsuite_style_begin"]) && wire.Codes.Last() == "FakeNative" && wire.Route == "testCase" &&
            admitted.Operations[0].State == "outcomeUnknown", "The admitted case reaches one owning-worker begin with Run(TestCase); a failed begin is never retried");

        // The production adapter stays closed for this profile.
        Reject(() => new TestSuiteProductionAdapter().RequireQualification(Context([pass], [pass.Manifest.Id], external), "execute"), "TestSuiteProfileUnqualified");

        // Result mapping: only a scope-verified single-case tree can pass; an unverified readback stays inconclusive.
        var plan = jobs.ReadReview(jobs.Review([pass], [new([pass.Manifest.Id], [])], Target(owned), profile, external)).Runs[0].Run;
        NativeTestResult Tree(bool scopeVerified) => new(pass.Manifest.Id, plan.Definitions[0].Reference, "Success", 0, 0,
            [new("Success", "native", pass.Manifest.Name, null, "Unknown", 0, 0, [])], true, true, scopeVerified);
        Check(TestSuiteResultNormalizer.Normalize(plan, TestSuiteApplicationRun.Captures(plan, [Tree(true)])).Results[0].Outcome == "passed", "Verified single-case scope can pass");
        Check(TestSuiteResultNormalizer.Normalize(plan, TestSuiteApplicationRun.Captures(plan, [Tree(false)])).Results[0].Outcome == "inconclusive", "Unverified scope never passes");

        // The exact trees TIA wrote on 30 October 2026: an Information footer, so only the fixture profile can pass or fail them.
        var instanceScope = reference with { Object = reference.Object with { ObjectPath = "PLC/A/plcsim/TiaMcp_owned", Kind = "plcsimInstance" } };
        foreach (bool passing in new[] { true, false })
        {
            string name = passing ? "TiaMcp_Q02_Pass" : "TiaMcp_Q02_Fail", state = passing ? "Success" : "Error";
            int errors = passing ? 0 : 1;
            var bytes = Encoding.ASCII.GetBytes("TEST_CASE \"" + name + "\"");
            var definition = new TestSuiteDefinitionCapture(name, "application", name, ".tat", reference with { Object = reference.Object with { ObjectPath = "PLC/A/testsuite/application/" + name, Kind = "applicationTest" }, ContentSha256 = null },
                bytes, profile, [reference, instanceScope], [], true, true, []);
            var run = TestSuiteRunPlanner.Prepare([definition], [name], [], "externallyManaged");
            NativeTestMessage M(string? path, string description, string nodeState, int nodeErrors, params NativeTestMessage[] children) =>
                new(nodeState, description, path, "2026-10-30T07:52:05.7232752Z", "Utc", nodeErrors, 0, children);
            NativeTestResult Observed(string instance = "TiaMcp_owned", string expected = "", string footer = "Test case(s) execution completed.") => new(name, run.Definitions[0].Reference, state, errors, 0, [
                M("Application test", "Test cases executed : 1 ; Passed : " + (1 - errors) + " ; Failed : " + errors + " ; Inconclusive : 0", state, errors,
                    M(name, "ScratchPLC[PLCSIM Instance : " + instance + "]", state, errors,
                        M("\"Enable gate output\"", passing ? "Pass" : "Fail", state, errors,
                            M("TiaMcp_EnableGate_DB.Active", "Actual: True, Expected: " + (expected == "" ? (passing ? "True" : "False") : expected) + ", Assert type: Equal", state, errors)))),
                M(null, footer, "Information", 0)], true, true, true);
            string Outcome(NativeTestResult tree, TestSuiteRunPlan? p = null) => TestSuiteResultNormalizer.Normalize(p ?? run, TestSuiteApplicationRun.Captures(p ?? run, [tree])).Results[0].Outcome;
            Check(Outcome(Observed()) == (passing ? "passed" : "assertionFailed"), name + " maps to " + (passing ? "passed" : "an assertion failure"));
            Check(Outcome(Observed() with { ScopeVerified = false }) == "inconclusive", name + " without a verified scope readback stays inconclusive");
            Check(Outcome(Observed("Foreign")) == (passing ? "inconclusive" : "executionError"), name + " on another instance is not the fixture result");
            Check(Outcome(Observed(expected: passing ? "False" : "True")) == (passing ? "inconclusive" : "executionError"), name + " with another assertion is not the fixture result");
            Check(Outcome(Observed(footer: "Testfall-Ausführung abgeschlossen.")) == (passing ? "inconclusive" : "executionError"), name + " under an unobserved UI language fails closed");
            var generic = TestSuiteRunPlanner.Prepare([new TestSuiteDefinitionCapture(name, "application", name, ".tat", definition.Manifest.Reference, bytes, "other-profile", [reference, instanceScope], [], true, true, [])], [name], [], "externallyManaged");
            Check(Outcome(Observed() with { DefinitionReference = generic.Definitions[0].Reference }, generic) == (passing ? "inconclusive" : "executionError"), name + " tree under another profile gets no fixture interpretation");
        }
        return checks;
    }

    private sealed class Wire : IBridgeClient
    {
        public readonly List<string> Calls = [], Codes = [];
        public string? Route;
        public Task<JsonElement> CallAsync(string operation, object? arguments = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(operation);
            Route = JsonSerializer.SerializeToElement(arguments, BridgeProtocol.Json).GetProperty("application").GetProperty("route").GetString();
            throw new BridgeException("FakeNative", "Authored fake: no native worker.");
        }
    }
}
