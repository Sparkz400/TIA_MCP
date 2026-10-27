using System.Text;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class ApplicationRunChecks
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
        var reference = new EngineeringObjectReference(new("Project/A", "PLC/A", "Root", "Tests/Root", "applicationTest"), new("app-1", new('a', 64)), new('b', 64));
        var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" } };
        TestSuiteDefinitionCapture Definition(string id, string family = "application", string[]? dependencies = null)
            => new(id, family, id, family switch { "application" => ".tat", "style" => ".xml", _ => ".ttst" }, reference with { Object = reference.Object with { ObjectPath = "Tests/" + id }, ContentSha256 = null },
                Encoding.UTF8.GetBytes(family == "style" ? "<r/>" : "opaque authored " + id), "authored-v1", [scope], dependencies ?? [], true, true, []);
        TestSuiteRunPlan Plan(TestSuiteDefinitionCapture[] all, string[] selected, string[]? excluded = null)
            => TestSuiteRunPlanner.Prepare(all, selected, excluded ?? [], "systemManaged");
        TestSuiteDefinitionCapture a = Definition("a"), b = Definition("b"), set = Definition("set", "applicationSet", ["a"]), other = Definition("other", "applicationSet", ["b"]);

        var single = Plan([a], ["a"]);
        Check(TestSuiteApplicationRun.Route(single, false) == "testCase", "One case uses Run(TestCase)");
        Check(TestSuiteApplicationRun.Route(Plan([a, b], ["a", "b"]), false) == "testCaseList", "Several cases use Run(IEnumerable<TestCase>)");
        Check(TestSuiteApplicationRun.Route(Plan([set, a], ["set"]), false) == "testSet", "One set uses Run(ApplicationTestSet)");
        Check(TestSuiteApplicationRun.Route(Plan([set, other, a, b], ["set", "other"]), false) == "testSetList", "Several sets use Run(IEnumerable<ApplicationTestSet>)");
        Check(TestSuiteApplicationRun.Route(Plan([a, b], ["a", "b"]), true) == "group", "Whole group dispatches every reviewed case");
        Reject(() => TestSuiteApplicationRun.Route(Plan([set, a, b], ["set", "b"]), false), "MixedApplicationDispatch");
        Reject(() => TestSuiteApplicationRun.Route(Plan([set, a], ["set"]), true), "InvalidSelection");
        Reject(() => TestSuiteApplicationRun.Route(Plan([a, b], ["a"], ["b"]), true), "InvalidSelection");
        Reject(() => TestSuiteApplicationRun.Route(TestSuiteRunPlanner.Prepare([Definition("r", "style")], ["r"], [], "style"), false), "InvalidExecutionMode");
        foreach (var invalid in new[] { new TestSuiteApplicationBinding("style", "ticket", false), new TestSuiteApplicationBinding("endpoint", "ticket", false),
            new TestSuiteApplicationBinding("systemManaged", "", false), new TestSuiteApplicationBinding("externallyManaged", "bad\n", false) })
            Reject(() => TestSuiteApplicationRun.Validate(invalid), "InvalidExecutionMode");

        NativeTestResult Tree(TestSuiteRunPlan plan, string state = "Success", bool scopeVerified = true, int errors = 0)
        {
            var first = plan.Definitions.Single(d => d.Id == plan.DispatchDefinitionIds[0]);
            return new(first.Id, first.Reference, state, errors, 0, [new("Success", "native case message", first.Name, null, "Unknown", 0, 0, [])], true, true, scopeVerified);
        }
        var style = TestSuiteRunPlanner.Prepare([Definition("r", "style")], ["r"], [], "style");
        var styleCapture = new NativeTestResult("r", style.Definitions[0].Reference, "Success", 0, 0, [], true, true, true);
        Check(ReferenceEquals(TestSuiteApplicationRun.Captures(style, [styleCapture])[0], styleCapture), "Style captures pass through unchanged");
        Check(TestSuiteResultNormalizer.Normalize(single, TestSuiteApplicationRun.Captures(single, [Tree(single)])).RequiredCoveragePassed,
            "A single-case closure owns its tree exactly");
        Check(TestSuiteResultNormalizer.Normalize(single, TestSuiteApplicationRun.Captures(single, [Tree(single, "Error", errors: 1)])).Results[0].Outcome == "executionError",
            "Native errors stay execution errors without an assertion profile");
        var list = Plan([a, b], ["a", "b"]);
        var listCaptures = TestSuiteApplicationRun.Captures(list, [Tree(list)]);
        var listReport = TestSuiteResultNormalizer.Normalize(list, listCaptures);
        Check(listCaptures.Length == 2 && listCaptures.All(c => c.Messages.Single().Description == "native case message"), "The whole tree is retained for every unit");
        Check(!listReport.RequiredCoveragePassed && listReport.Results.All(r => r.Outcome == "inconclusive"), "One aggregate tree cannot pass several units");
        var setPlan = Plan([set, a], ["set"]);
        var setReport = TestSuiteResultNormalizer.Normalize(setPlan, TestSuiteApplicationRun.Captures(setPlan, [Tree(setPlan)]));
        Check(setReport.Results.Select(r => r.Outcome).SequenceEqual(["inconclusive", "inconclusive"]), "Set members are not reported as passed or not run");
        var excluded = Plan([a, b], ["a"], ["b"]);
        var excludedReport = TestSuiteResultNormalizer.Normalize(excluded, TestSuiteApplicationRun.Captures(excluded, [Tree(excluded)]));
        Check(excludedReport.Results[1] is { Outcome: "excluded", RawJson: null }, "An excluded unit receives no tree");
        Reject(() => TestSuiteApplicationRun.Captures(list, [Tree(list), Tree(list)]), "InvalidTestResults");
        Reject(() => TestSuiteApplicationRun.Captures(list, [Tree(list) with { DefinitionId = "b" }]), "InvalidTestResults");
        Reject(() => TestSuiteApplicationRun.Captures(list, [Tree(list) with { DefinitionReference = list.Definitions[0].Reference with { ContentSha256 = new('f', 64) } }]), "StaleContent");
        var junit = XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(list, listReport, "raw/application.json"));
        Check(junit.Root!.Attribute("tests")!.Value == "2" && junit.Root.Attribute("errors")!.Value == "2", "JUnit lists every reviewed unit from one tree");

        // Durable review: application binding round-trips, and the production gate refuses before any native access.
        Directory.CreateDirectory(root);
        var jobs = new TestSuiteStyleJobs(root);
        string[] probes = ["testsuite.product", "testsuite.license", "testsuite.applicationService", "testsuite.scope", "simulator.ownedInstance"];
        var target = new NativeTargetObservation(reference, Path.Combine(root, "owned.ap21"), "epoch", new('c', 64), new('d', 64),
            "hardware", null, true, probes.Select(p => new TargetProbe(p, "observed", new('e', 64))).ToArray());
        var binding = new TestSuiteApplicationBinding("systemManaged", "opaque-dpapi-ticket", false);
        var review = jobs.Review([a, b], [new(["a", "b"], [])], target, "authored-profile", binding);
        var reopened = jobs.ReadReview(review);
        Check(reopened.Application == binding && reopened.Runs[0].Operation.Operation == "testSuite.application.run" &&
            reopened.Runs[0].Operation.RequiredProbes.Contains("simulator.ownedInstance"), "Application review binds mode, ticket, route and simulator probe");
        var unbound = jobs.Retain(reopened with { Application = null });
        Check(!File.ReadAllText(Path.Combine(root, unbound.RelativePath)).Contains("\"application\":"), "Absent binding is omitted, so style review hashes are unchanged");
        Reject(() => jobs.ReadReview(unbound), "InvalidTestSuiteJob");
        Reject(() => jobs.Review([a, set], [new(["a", "set"], [])], target, "p", binding), "MixedApplicationDispatch");
        Reject(() => jobs.Review([a], [new(["a"], [])], target with { Probes = [] }, "p", binding), "TargetPrerequisiteMissing");
        var job = jobs.Prepare(review, review.Sha256, "application-job");
        try { jobs.ExecuteNext(job.Job.Id, review.Sha256, job.Operations[0].Id, 0, new TestSuiteProductionAdapter()).GetAwaiter().GetResult(); throw new Exception("Expected refusal"); }
        catch (BridgeException e) when (e.Code == "TestSuiteProfileUnqualified" && e.Message.Contains("systemManaged")) { checks++; }
        Check(jobs.Status(job.Job.Id).Operations[0] is { State: "prepared", Revision: 0 }, "Production refusal leaves the application operation unacquired");
        return checks;
    }
}
