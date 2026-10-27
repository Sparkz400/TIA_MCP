using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

// TS-06 system tests: endpoint plans bound to an owned PLCSIM opc.tcp address, one SystemTestCaseExecutor.Run tree per run.
internal static class SystemRunChecks
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
        var reference = new EngineeringObjectReference(new("Project/A", "PLC/A", "Root", "Tests/Root", "systemTest"), new("sys-1", new('a', 64)), new('b', 64));
        var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" } };
        TestSuiteDefinitionCapture Definition(string id) => new(id, "system", id, ".tst", reference with { Object = reference.Object with { ObjectPath = "Tests/" + id }, ContentSha256 = null },
            Encoding.UTF8.GetBytes("opaque authored " + id), "authored-v1", [scope], [], true, true, []);
        TestSuiteRunPlan Plan(TestSuiteDefinitionCapture[] all, string[] selected, string[]? excluded = null)
            => TestSuiteRunPlanner.Prepare(all, selected, excluded ?? [], "endpoint");
        TestSuiteDefinitionCapture a = Definition("a"), b = Definition("b");
        TestSuiteRunPlan single = Plan([a], ["a"]), list = Plan([a, b], ["a", "b"]);

        Check(TestSuiteApplicationRun.Route(single, false) == "systemTestCase", "One case uses Run(SystemTestCase)");
        Check(TestSuiteApplicationRun.Route(list, false) == "systemTestCaseList", "Several cases use Run(IEnumerable<SystemTestCase>)");
        Check(TestSuiteApplicationRun.Route(list, true) == "systemGroup", "Whole group uses Run(SystemTestSystemGroup)");
        Reject(() => TestSuiteApplicationRun.Route(Plan([a, b], ["a"], ["b"]), true), "InvalidSelection");

        const string address = "opc.tcp://192.168.0.1:4840/";
        var binding = new TestSuiteApplicationBinding("endpoint", "opaque-dpapi-ticket", false) { ServerAddress = address };
        TestSuiteApplicationRun.Validate(binding); checks++;
        foreach (var invalid in new[] { binding with { ServerAddress = null }, binding with { ServerAddress = "opc.tcp://plc.local:4840/" },
            binding with { ServerAddress = "http://192.168.0.1:4840/" }, binding with { ServerAddress = "opc.tcp://u@192.168.0.1:4840/" },
            binding with { ServerAddress = "opc.tcp://192.168.0.1/" }, binding with { ServerAddress = "opc.tcp://192.168.0.1:4840/?x" },
            binding with { SimulatorTicket = "" }, binding with { ExecutionMode = "systemManaged" } })
            Reject(() => TestSuiteApplicationRun.Validate(invalid), "InvalidExecutionMode");

        // Result normalization: the generic normalizer over one aggregate system tree.
        NativeTestResult Tree(TestSuiteRunPlan plan, string state = "Success", int errors = 0, int warnings = 0)
        {
            var first = plan.Definitions.Single(d => d.Id == plan.DispatchDefinitionIds[0]);
            return new(first.Id, first.Reference, state, errors, warnings, [new(state, "native system message", first.Name, null, "Unknown", errors, warnings, [])], true, true, true);
        }
        TestSuiteResultReport Report(TestSuiteRunPlan plan, NativeTestResult tree) => TestSuiteResultNormalizer.Normalize(plan, TestSuiteApplicationRun.Captures(plan, [tree]));
        Check(Report(single, Tree(single)).RequiredCoveragePassed, "A single system case owns its tree exactly");
        Check(Report(single, Tree(single, "Error", errors: 1)).Results[0].Outcome == "executionError", "Native system errors stay execution errors");
        Check(Report(single, Tree(single, "Warning", warnings: 1)).Results[0].Outcome == "inconclusive", "Inconclusive runtime outcomes never pass");
        var listReport = Report(list, Tree(list));
        Check(!listReport.RequiredCoveragePassed && listReport.Results.All(r => r.Outcome == "inconclusive"), "One aggregate system tree cannot pass several cases");
        Reject(() => TestSuiteApplicationRun.Captures(list, [Tree(list), Tree(list)]), "InvalidTestResults");

        // Durable review: endpoint binding round-trips with the owned-simulator probe; production refuses before native access.
        Directory.CreateDirectory(root);
        var jobs = new TestSuiteStyleJobs(root);
        string[] probes = ["testsuite.product", "testsuite.license", "testsuite.systemService", "testsuite.scope", "simulator.ownedInstance"];
        var target = new NativeTargetObservation(reference, Path.Combine(root, "owned.ap21"), "epoch", new('c', 64), new('d', 64),
            "hardware", null, true, probes.Select(p => new TargetProbe(p, "observed", new('e', 64))).ToArray());
        var review = jobs.Review([a, b], [new(["a", "b"], [])], target, "authored-profile", binding);
        var reopened = jobs.ReadReview(review);
        Check(reopened.Application == binding && reopened.Runs[0].Operation.Operation == "testSuite.system.run" &&
            reopened.Runs[0].Operation.RequiredProbes.Contains("simulator.ownedInstance"), "System review binds ticket, address and owned-simulator probe");
        var application = jobs.Retain(new TestSuiteApplicationBinding("systemManaged", "t", false));
        Check(!File.ReadAllText(Path.Combine(root, application.RelativePath)).Contains("serverAddress"), "Application bindings omit serverAddress, so their review hashes are unchanged");
        Reject(() => jobs.Review([a], [new(["a"], [])], target with { Probes = [] }, "p", binding), "TargetPrerequisiteMissing");
        var job = jobs.Prepare(review, review.Sha256, "system-job");
        try { jobs.ExecuteNext(job.Job.Id, review.Sha256, job.Operations[0].Id, 0, new TestSuiteProductionAdapter()).GetAwaiter().GetResult(); throw new Exception("Expected refusal"); }
        catch (BridgeException e) when (e.Code == "TestSuiteProfileUnqualified" && e.Message.Contains("endpoint")) { checks++; }
        Check(jobs.Status(job.Job.Id).Operations[0] is { State: "prepared", Revision: 0 }, "Production refusal leaves the system operation unacquired");
        return checks;
    }
}
