using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class SystemQualificationChecks
{
    public static int Run(string root)
    {
        int checks = 0;
        void Check(bool yes, string message) { if (!yes) throw new Exception(message); checks++; }
        void Reject(TestSuiteStyleContext value, string code = "TestSuiteProfileUnqualified")
        {
            try { TestSuiteSystemQualification.Admit(value, root); }
            catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected " + code);
        }
        Directory.CreateDirectory(root);
        var reference = new EngineeringObjectReference(new("Project/A", "PLC/A", "PLC/A", "PLC/A", "plcSoftware"), new("q03", new('a', 64)), new('a', 64));
        string profile = TestSuiteSystemQualification.Profile;
        string[] probes = ["testsuite.product", "testsuite.license", "testsuite.systemService", "testsuite.scope", "simulator.ownedInstance"];
        var target = new NativeTargetObservation(reference, Path.Combine(root, "scratch", "S", "S.ap21"), "epoch", new('b', 64), new('c', 64),
            "hardware", "native", true, probes.Select(p => new TargetProbe(p, "observed", new('e', 64))).ToArray());
        var binding = new TestSuiteApplicationBinding("endpoint", "opaque-ticket", false) { ServerAddress = "opc.tcp://192.168.0.1:4840/" };
        var jobs = new TestSuiteStyleJobs(root);
        foreach (string name in TestSuiteSystemQualification.Cases)
        {
            var definition = new TestSuiteDefinitionCapture(name, "system", name, ".tst", reference with { ContentSha256 = null },
                Encoding.ASCII.GetBytes("TEST_CASE \"" + name + "\""), profile, [reference], [], true, true, []);
            var review = jobs.Review([definition], [new([name], [])], target, profile, binding);
            var status = jobs.Prepare(review, review.Sha256, Guid.NewGuid().ToString("N"));
            var context = new TestSuiteStyleContext(root, jobs.ReadReview(review).Runs[0].Run, null!, status.Operations[0], profile) { Application = binding };
            TestSuiteSystemQualification.Admit(context, root); checks++;
            Reject(context with { Application = null });
            Reject(context with { ExecutionProfile = TestSuiteApplicationQualification.Profile });
            Reject(context with { Application = binding with { WholeGroup = true } });
            Reject(context with { Application = binding with { ExecutionMode = "externallyManaged" } });
            Reject(context with { Application = binding with { SimulatorTicket = "" } });
            Reject(context with { Run = context.Run with { ExcludedDefinitionIds = [name] } });
            Reject(context with { Run = context.Run with { DispatchDefinitionIds = [] } });
            foreach (string address in new[] { "opc.tcp://localhost:4840/", "opc.tcp://192.168.0.1:4841/", "opc.tcp://192.168.0.1:4840/other",
                "opc.tcp://192.168.0.1:4840/?q=1", "opc.tcp://user@192.168.0.1:4840/", "opc.tcp://192.168.0.1:4840" })
                Reject(context with { Application = binding with { ServerAddress = address } });
            foreach (string path in new[] { Path.Combine(root, "scratch-other", "S.ap21"), Path.Combine(root, "scratch", "..", "User.ap21"), "User.ap21" })
                Reject(context with { Operation = context.Operation with { Plan = context.Operation.Plan with { Target = target with { ProjectFilePath = path } } } }, "ProjectIsolationRequired");
            try { new TestSuiteProductionAdapter().RequireQualification(context, "execute"); throw new Exception("Production admitted TS-Q03"); }
            catch (BridgeException e) when (e.Code == "TestSuiteProfileUnqualified") { checks++; }
            var raw = new NativeTestResult(name, context.Run.Definitions[0].Reference, "Success", 0, 0, [], true, true, true);
            var result = TestSuiteResultNormalizer.Normalize(context.Run, [raw]);
            Check(!result.RequiredCoveragePassed && result.Results[0].Outcome == "inconclusive", "Even an all-success exploratory tree cannot qualify TS-Q03");
            result = TestSuiteResultNormalizer.Normalize(context.Run, [raw with { State = "Error", ErrorCount = 1, FailureKind = "assertion" }]);
            Check(result.Results[0].Outcome == "executionError", "Unprofiled native errors cannot become assertion proof");
        }
        return checks;
    }
}
