using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal sealed record TestSuiteExtendedReceipt(int Checks, int ScaleDefinitions, int ScaleMessages,
    long CapturedRawBytes, long DefinitionAndPlanMilliseconds, long NormalizeMilliseconds, long JUnitMilliseconds,
    bool NativeExecuted = false, bool ReleaseAccepted = false);

internal static class ExtendedChecks
{
    public static TestSuiteExtendedReceipt Run()
    {
        int checks = NativeScopeEvidenceChecks.Run();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string code)
        {
            try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; }
            throw new Exception("Expected refusal: " + code);
        }
        var reference = new EngineeringObjectReference(new("Project/Scale", "PLC/A", "Root", "Rules/A", "ruleSet"), new("scope-observation", new('d', 64)));
        var motor = reference with { Object = reference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" }, ContentSha256 = new('b', 64) };
        var valve = motor with { Object = motor.Object with { ObjectPath = "Blocks/Valve" } };
        TestSuiteDefinitionCapture Definition(string id) => new(id, "style", id, ".xml",
            reference with { Object = reference.Object with { ObjectPath = "Rules/" + id } }, Encoding.UTF8.GetBytes("<Rule untouched=\"Ω\"/>"),
            "authored-scale-v1", [motor, valve], [], true, true, []);
        var definition = Definition("selected");
        var plan = TestSuiteRunPlanner.Prepare([definition], ["selected"], [], "style");
        NativeTestResult Result(NativeTestMessage[]? messages = null) => new("selected", definition.Manifest.Reference,
            "Success", 0, 0, messages ?? [], true, true, true);
        var scope = new TestSuiteScopeObservation("selected", definition.Manifest.Reference, [motor, valve], [], true);
        var coverage = TestSuiteScopeCoverage.Compare(plan, [scope]);
        TestSuiteScopeCoverage.Validate(plan, coverage); checks++;
        var passed = TestSuiteResultNormalizer.Normalize(plan, [Result()]);
        var assessed = TestSuiteScopeCoverage.Assess(plan, passed, coverage);
        Check(coverage.RequiredScopePassed && assessed.RequiredCoveragePassed && !assessed.NativeExecutionAuthorized && !assessed.ReleaseAccepted,
            "Independently matched exact scope and complete results pass only the selected coverage gate.");
        var missing = TestSuiteScopeCoverage.Compare(plan, [scope with { EffectiveScope = [motor] }]);
        Check(missing.Entries[0].MissingScope.SequenceEqual([valve]) && !missing.RequiredScopePassed, "Missing required scope remains explicit.");
        Check(!TestSuiteScopeCoverage.Assess(plan, passed, missing).RequiredCoveragePassed, "A supplied ScopeVerified flag cannot override an observed scope gap.");
        var other = motor with { Object = motor.Object with { PlcPath = "PLC/B" } };
        var crossPlc = TestSuiteScopeCoverage.Compare(plan, [scope with { EffectiveScope = [other, valve] }]);
        Check(crossPlc.Entries[0].MissingScope.SequenceEqual([motor]) && crossPlc.Entries[0].UnexpectedScope.SequenceEqual([other]), "Same native path in another PLC is different coverage.");
        Check(!TestSuiteScopeCoverage.Compare(plan, [scope with { ObservationComplete = false }]).RequiredScopePassed, "Incomplete effective scope cannot pass.");
        var absent = TestSuiteScopeCoverage.Compare(plan, []);
        Check(absent.Entries[0].Status == "notObserved" && absent.Entries[0].MissingScope.Length == 2, "Missing observations retain all required scope.");
        TestSuiteScopeCoverage.Validate(plan, absent); checks++;
        var ignored = TestSuiteScopeCoverage.Compare(plan, [scope with { EffectiveScope = [motor], IgnoredScope = [valve] }]);
        Check(!ignored.RequiredScopePassed && ignored.Entries[0].IgnoredScope.SequenceEqual([valve]), "Ignored required objects stay visible and fail coverage.");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope with { IgnoredScope = [motor] }]), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope, scope]), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope with { DefinitionId = "alien" }]), "UnexpectedScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope with { EffectiveScope = [motor, motor] }]), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope with { EffectiveScope = [motor with { ContentSha256 = new('c', 64) }, valve] }]), "StaleContent");
        Reject(() => TestSuiteScopeCoverage.Compare(plan, [scope with { DefinitionReference = definition.Manifest.Reference with { Snapshot = new("later", new('d', 64)) } }]), "StaleObservation");
        Reject(() => TestSuiteScopeCoverage.Validate(plan, missing with { RequiredScopePassed = true }), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Validate(plan, missing with { Entries = [missing.Entries[0] with { Status = "covered", MissingScope = [] }] }), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Validate(plan, coverage with { Entries = [coverage.Entries[0] with { RequiredScope = [motor] }] }), "InvalidScopeCoverage");
        Reject(() => TestSuiteScopeCoverage.Validate(plan, coverage with { NativeExecutedByComparer = true }), "InvalidScopeCoverage");
        var excludedPlan = TestSuiteRunPlanner.Prepare([definition, Definition("excluded")], ["selected"], ["excluded"], "style");
        var excludedCoverage = TestSuiteScopeCoverage.Compare(excludedPlan, [scope]);
        Check(excludedCoverage.Entries[1].Status == "excluded" && !excludedCoverage.RequiredScopePassed, "Exclusions remain required scope obligations.");
        TestSuiteScopeCoverage.Validate(excludedPlan, excludedCoverage); checks++;
        var unicode = new NativeTestMessage("Success", "日本語 Ω 😀\u0000\nretained", "Blocks/日本語", "2026-10-15T17:00:00+05:30", "Local", 0, 0, []);
        var unicodeReport = TestSuiteResultNormalizer.Normalize(plan, [Result([unicode])]);
        var retained = JsonNode.Parse(unicodeReport.Results[0].RawJson!)!;
        Check(retained["messages"]![0]!["description"]!.GetValue<string>() == unicode.Description &&
            retained["messages"]![0]!["nativeTimestamp"]!.GetValue<string>() == unicode.NativeTimestamp, "Unicode, JSON controls and original timestamp text are retained exactly.");
        Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result([unicode with { Description = "bad\ud800" }])]), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result([unicode with { Path = "bad\udc00" }])]), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.Normalize(plan, [null!]), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result() with { DefinitionId = null! }]), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.ValidateReport(plan, passed with { Results = [null!] }), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.ValidateReport(plan, passed with { Results = [passed.Results[0] with { Reasons = [new string('x', 4001)] }] }), "InvalidTestResults");
        Reject(() => TestSuiteResultNormalizer.ValidateReport(plan, passed with { Results = [passed.Results[0] with { RawJson = "{" }] }), "InvalidTestResults");
        var partialReport = TestSuiteResultNormalizer.Normalize(excludedPlan, [Result()]);
        Reject(() => TestSuiteResultNormalizer.ValidateReport(excludedPlan, partialReport with { Results = [partialReport.Results[0]], RequiredDefinitionIds = ["selected"] }), "InvalidTestResults");
        var extraFieldRaw = JsonNode.Parse(passed.Results[0].RawJson!)!;
        extraFieldRaw["unknownMustNotDisappear"] = "retained-or-refused";
        Reject(() => TestSuiteResultNormalizer.ValidateReport(plan, passed with { Results = [passed.Results[0] with { RawJson = CanonicalJson.Serialize(extraFieldRaw), RawSha256 = CanonicalJson.Sha256(extraFieldRaw) }] }), "InvalidTestResults");
        var largeMessage = unicode with { Description = "scale", Path = null, NativeTimestamp = null, NativeTimestampKind = "Unknown" };
        Reject(() => TestSuiteResultNormalizer.Flatten(Enumerable.Repeat(largeMessage, 100001).ToArray()).Count(), "TestResultLimit");

        var stopwatch = Stopwatch.StartNew();
        var definitions = Enumerable.Range(0, 1000).Select(i => Definition("rule-" + i.ToString("D4"))).ToArray();
        var scalePlan = TestSuiteRunPlanner.Prepare(definitions, definitions.Select(d => d.Manifest.Id).ToArray(), [], "style");
        long planMs = stopwatch.ElapsedMilliseconds;
        Check(scalePlan.RequiredDefinitionIds.Length == 1000 && scalePlan.Blockers.Length == 0, "1,000 definitions preserve exact required closure.");
        var scaleResults = scalePlan.Definitions.Select((d, i) => new NativeTestResult(d.Id, d.Reference, "Success", 0, 0,
            Enumerable.Range(0, 100).Select(j => largeMessage with { Description = $"definition={i}; message={j}; Ω" }).ToArray(), true, true, true)).ToArray();
        stopwatch.Restart();
        var scaleReport = TestSuiteResultNormalizer.Normalize(scalePlan, scaleResults);
        long normalizeMs = stopwatch.ElapsedMilliseconds;
        long rawBytes = scaleReport.Results.Sum(r => (long)Encoding.UTF8.GetByteCount(r.RawJson!));
        Check(scaleReport.RequiredCoveragePassed && scaleReport.Results.Length == 1000, "100,000 retained messages across 1,000 results pass the bounded selected gate.");
        Check(scaleReport.Results[999].RawJson!.Contains("definition=999; message=99"), "Last scale message is retained without truncation.");
        stopwatch.Restart();
        var junit = XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(scalePlan, scaleReport, "synthetic-scale-results.json"));
        long junitMs = stopwatch.ElapsedMilliseconds;
        Check(junit.Descendants("testcase").Count() == 1000 && junit.Root!.Attribute("errors")!.Value == "0", "Scale JUnit counts exactly 1,000 independently validated execution units.");
        var scaleCoverage = TestSuiteScopeCoverage.Compare(scalePlan, scalePlan.Definitions.Select(d => new TestSuiteScopeObservation(d.Id, d.Reference, [motor, valve], [], true)).ToArray());
        Check(scaleCoverage.RequiredScopePassed && scaleCoverage.Entries.Length == 1000, "1,000 observations reconcile 2,000 exact required objects.");
        TestSuiteScopeCoverage.Validate(scalePlan, scaleCoverage); checks++;
        scaleResults[999] = scaleResults[999] with { Messages = scaleResults[999].Messages.Append(largeMessage).ToArray() };
        Reject(() => TestSuiteResultNormalizer.Normalize(scalePlan, scaleResults), "TestResultLimit");
        return new(checks, 1000, 100000, rawBytes, planMs, normalizeMs, junitMs);
    }
}
