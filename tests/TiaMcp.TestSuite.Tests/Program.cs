using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

int checks = 0;
void Check(bool yes, string message) { if (!yes) throw new Exception(message); checks++; }
void Reject(Action action, string? code = null)
{
    try { action(); } catch (BridgeException e) when (code is null || e.Code == code) { checks++; return; }
    catch (XmlException) when (code == "Xml") { checks++; return; }
    throw new Exception("Expected refusal " + code);
}
var reference = new EngineeringObjectReference(new("Project/A", "PLC/A", "Root", "Rules/Names", "ruleSet"), new("review-1", new('a', 64)));
var scope = reference with { Object = reference.Object with { ObjectPath = "Blocks/Motor", Kind = "block" } };
TestSuiteDefinitionCapture Definition(string id = "names", string family = "style", string[]? dependencies = null, bool complete = true, bool reviewed = true, string[]? syntax = null)
    => new(id, family, "Exact original " + id, family switch { "style" => ".xml", "application" => ".tat", "system" => ".tst", _ => ".TTST" },
        reference with { Object = reference.Object with { ObjectPath = "Defs/" + id } },
        Encoding.UTF8.GetBytes(family == "style" ? "<?xml version=\"1.0\"?><Rule opaque=\"Ω\"><!--preserve--></Rule>" : "RAW: profile is explicitly supplied; native syntax not claimed"),
        "synthetic-profile-v1", [scope], dependencies ?? [], complete, reviewed, syntax ?? []);
var definition = Definition();
byte[] raw = definition.OriginalBytes;
raw[0] = 0;
Check(definition.OriginalBytes[0] == '<', "Original bytes are privately retained");
var externalManifest = definition.Manifest;
externalManifest.RequiredScope[0] = scope with { Object = scope.Object with { ObjectPath = "Wrong" } };
Check(definition.Manifest.RequiredScope[0].Object.ObjectPath == "Blocks/Motor", "Manifest mutation cannot change capture");
Reject(() => new TestSuiteDefinitionCapture("bad", "style", "bad", ".xml", reference, Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY e 'x'>]><a>&e;</a>"), "fixture", [scope], [], true, true, []), "Xml");
Reject(() => new TestSuiteDefinitionCapture("bad", "style", "bad", ".tat", reference, [1], "fixture", [scope], [], true, true, []), "InvalidDefinition");
Reject(() => new TestSuiteDefinitionCapture("bad", "style", "bad", ".xml", reference with { ContentSha256 = new('b', 64) }, Encoding.UTF8.GetBytes("<r/>"), "fixture", [scope], [], true, true, []), "StaleContent");
var plan = TestSuiteRunPlanner.Prepare([definition], ["names"], [], "style");
TestSuiteRunPlanner.Validate(plan); checks++;
Check(plan == plan with { } && plan.NativeExecutionAuthorized == false, "Pure plan is not admission");
Check(plan.PlanSha256 == TestSuiteRunPlanner.Prepare([definition], ["names"], [], "style").PlanSha256, "Deterministic plan");
Reject(() => TestSuiteRunPlanner.Prepare([definition, definition], ["names"], [], "style"), "AmbiguousDefinition");
Reject(() => TestSuiteRunPlanner.Prepare([Definition(dependencies: ["missing"])], ["names"], [], "style"), "MissingDependency");
Reject(() => TestSuiteRunPlanner.Prepare([Definition("a", dependencies: ["b"]), Definition("b", dependencies: ["a"])], ["a"], [], "style"), "DefinitionCycle");
Reject(() => TestSuiteRunPlanner.Prepare([Definition("a", "application")], ["a"], [], "style"), "MixedExecutionResources");
Reject(() => TestSuiteRunPlanner.Validate(plan with { RequiredDefinitionIds = [] }), "InvalidRunPlan");
Reject(() => TestSuiteRunPlanner.Validate(plan with { ExecutionMode = "changed" }), "StaleRunPlan");
var blocked = TestSuiteRunPlanner.Prepare([Definition(complete: false, reviewed: false, syntax: ["opaque token"])], ["names"], [], "style");
Check(blocked.Blockers.Length == 2, "Unknown scope and effects stay blocked");
var setPlan = TestSuiteRunPlanner.Prepare([Definition("set", "applicationSet", ["case"]), Definition("case", "application")], ["set"], [], "systemManaged");
Check(setPlan.DispatchDefinitionIds.SequenceEqual(["set"]) && setPlan.RequiredDefinitionIds.SequenceEqual(["set", "case"]), "Sets remain intact and dependencies remain required");
var duplicateDispatch = TestSuiteRunPlanner.Prepare([Definition("set", "applicationSet", ["case"]), Definition("case", "application")], ["set", "case"], [], "externallyManaged");
Check(duplicateDispatch.Blockers.Any(s => s.Contains("twice")), "Do not dispatch member and parent set twice");
NativeTestResult Result(string state = "Success", bool complete = true, bool scopeVerified = true, int errors = 0, int warnings = 0, NativeTestMessage[]? messages = null)
    => new("names", definition.Manifest.Reference, state, errors, warnings, messages ?? [], true, complete, scopeVerified);
var passed = TestSuiteResultNormalizer.Normalize(plan, [Result()]);
Check(passed.RequiredCoveragePassed && !passed.ReleaseAccepted && !passed.NativeExecutedByNormalizer, "Captured scope pass does not claim execution or release");
foreach (var state in new[] { "Information", "Warning", "Unknown" })
    Check(!TestSuiteResultNormalizer.Normalize(plan, [Result(state)]).RequiredCoveragePassed, "Non-error state cannot imply pass: " + state);
Check(TestSuiteResultNormalizer.Normalize(plan, [Result("Error", errors: 1)]).Results[0].Outcome == "executionError", "Unclassified error is not guessed assertion");
Check(TestSuiteResultNormalizer.Normalize(plan, [Result("Error", errors: 1) with { FailureKind = "assertion" }]).Results[0].Outcome == "assertionFailed", "Explicit assertion profile retained");
Check(TestSuiteResultNormalizer.Normalize(plan, []).Results[0].Outcome == "notRun", "Required result cannot disappear");
Check(!TestSuiteResultNormalizer.Normalize(plan, [Result(complete: false)]).RequiredCoveragePassed, "Truncated result cannot pass");
Check(!TestSuiteResultNormalizer.Normalize(plan, [Result(scopeVerified: false)]).RequiredCoveragePassed, "Ignored native scope cannot pass");
Check(!TestSuiteResultNormalizer.Normalize(blocked, [Result()]).RequiredCoveragePassed, "Plan blockers survive apparent native success");
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result(), Result()]), "InvalidTestResults");
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result() with { DefinitionId = "alien" }]), "UnexpectedTestResult");
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result() with { DefinitionReference = definition.Manifest.Reference with { ContentSha256 = new('f', 64) } }]), "StaleContent");
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result(errors: -1)]), "InvalidTestResults");
var child = new NativeTestMessage("Error", "independent expected violation <Motor>", "Blocks/Motor", "2026-10-15T12:30:00", "Unspecified", 1, 0, []);
var parent = new NativeTestMessage("Error", "parent count includes child", "unknown", null, "Unknown", 1, 0, [child]);
var failure = Result("Error", errors: 1, messages: [parent]);
var normalized = TestSuiteResultNormalizer.Normalize(plan, [failure]);
var retained = JsonNode.Parse(normalized.Results[0].RawJson!)!;
Check(retained["errorCount"]!.GetValue<int>() == 1 && retained["messages"]![0]!["errorCount"]!.GetValue<int>() == 1, "Counts retained without double-summing");
Check(retained["messages"]![0]!["messages"]![0]!["nativeTimestampKind"]!.GetValue<string>() == "Unspecified", "No invented timestamp timezone");
var findings = TestSuiteResultNormalizer.Findings(failure, [scope]);
Check(findings.Length == 2 && findings[0].ResolvedObject is null && findings[1].ResolvedObject == scope, "Only exact observed object path joins");
Check(TestSuiteResultNormalizer.Findings(failure, [scope, scope])[1].ResolvedObject is null, "Ambiguous finding remains unresolved");
foreach (var (errors, warnings, outcome) in new[] { (1, 0, "executionError"), (0, 1, "inconclusive"), (2, 3, "executionError"), (int.MaxValue, int.MaxValue, "executionError") })
{
    var counterMessage = child with { State = "Success", ErrorCount = errors, WarningCount = warnings };
    var counterResult = Result(messages: [parent with { State = "Success", ErrorCount = 0, Messages = [counterMessage] }]);
    var counterReport = TestSuiteResultNormalizer.Normalize(plan, [counterResult]);
    string originalCounterCapture = JsonSerializer.Serialize(counterResult, BridgeProtocol.Json);
    var counterFindings = TestSuiteResultNormalizer.Findings(counterResult, [scope]);
    Check(counterReport.Results[0].Outcome == outcome && !counterReport.RequiredCoveragePassed && counterFindings.Length == 1,
        "Positive native counters cannot disappear from findings under successful parent/message states");
    Check(counterFindings[0].State == "Success" && counterFindings[0].Description == child.Description && counterFindings[0].ResolvedObject == scope,
        "Counter finding preserves the original native state/text and exact object join");
    var serializedFinding = JsonSerializer.SerializeToElement(counterFindings[0], BridgeProtocol.Json);
    Check(serializedFinding.GetProperty("errorCount").GetInt32() == errors && serializedFinding.GetProperty("warningCount").GetInt32() == warnings,
        "Finding JSON retains exact native counters without summation or overflow");
    Check(JsonSerializer.Serialize(counterResult, BridgeProtocol.Json) == originalCounterCapture,
        "Finding projection does not alter the supplied raw capture");
}
Check(TestSuiteResultNormalizer.Findings(Result(messages: [child with { State = "Success", ErrorCount = 0 }]), [scope]).Length == 0,
    "Zero-count success does not invent a finding from diagnostic-looking text");
Check(findings.All(f => f.ErrorCount == 1 && f.WarningCount == 0), "Parent and child counters remain separate native values");
foreach (string invalid in IdentityTextChecks.InvalidText)
    Reject(() => TestSuiteResultNormalizer.Findings(failure with { DefinitionId = invalid }, [scope]), "InvalidTestResults");
Check(TestSuiteResultNormalizer.Findings(failure with { DefinitionId = "rule-😀-e\u0301-\ufffd" }, [scope])[0].DefinitionId == "rule-😀-e\u0301-\ufffd",
    "Direct finding inspection preserves a valid exact Unicode definition ID");
var junit = XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(plan, normalized, "raw/results.json"));
Check(junit.Root!.Attribute("tests")!.Value == "1" && junit.Root.Attribute("errors")!.Value == "1", "JUnit counts execution units, not native aggregate assertions");
var missingJUnit = XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(plan, TestSuiteResultNormalizer.Normalize(plan, []), "raw/missing.json"));
Check(missingJUnit.Descendants("skipped").Count() == 1 && missingJUnit.Descendants("property").Any(p => p.Attribute("name")!.Value == "requiredCoveragePassed" && p.Attribute("value")!.Value == "false"), "CI skipped view retains failing machine gate");
var stopped = new TestSuiteJobState().RequestStop();
Check(stopped.State == "stopped" && !stopped.ResourceReleasePermitted, "Stop before dispatch still requires resource cleanup observation");
Reject(() => stopped.Advance("queued"), "InvalidJobTransition");
var running = new TestSuiteJobState().Advance("queued").Advance("admitted").Advance("dispatched");
Check(running.RequestStop().State == "dispatched" && !running.RequestStop().NativeCancellationSupported, "Stop request does not claim native cancellation");
var unknown = running.RequestStop().LoseResponse();
Check(unknown.State == "outcomeUnknown" && !unknown.ResourceReleasePermitted, "Lost response retains unknown outcome and resource hold");
Reject(() => unknown.VerifyCleanup(), "UnreconciledOutcome");
Reject(() => unknown.Advance("passed"), "InvalidJobTransition");
var reconciled = unknown.Reconcile("failed", new('c', 64));
Check(!reconciled.ResourceReleasePermitted && reconciled.VerifyCleanup().ResourceReleasePermitted, "Observed reconciliation and cleanup both required");
Reject(() => new TestSuiteJobState().Advance("passed"), "InvalidJobTransition");
Reject(() => new TestSuiteJobState("passed", Dispatched: false, CleanupVerified: true).Validate(), "InvalidJobState");
Reject(() => reconciled.VerifyCleanup().LoseResponse(), "InvalidJobTransition");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, passed with { Results = [], RequiredDefinitionIds = [] }, "raw.json"), "InvalidTestResults");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, normalized with { RequiredCoveragePassed = true }, "raw.json"), "InvalidTestResults");
var changedRaw = passed.Results[0] with { RawJson = "{}" };
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, passed with { Results = [changedRaw] }, "raw.json"), "StaleTestResult");
var cyclicChildren = new NativeTestMessage[1];
var cyclicMessage = new NativeTestMessage("Success", "cycle", null, null, "Unknown", 0, 0, cyclicChildren);
cyclicChildren[0] = cyclicMessage;
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result(messages: [cyclicMessage])]), "TestResultLimit");
TestSuiteRunPlan Rehash(TestSuiteRunPlan value) => value with { PlanSha256 = TestSuiteRunPlanner.Fingerprint(value) };
Reject(() => TestSuiteRunPlanner.Validate(Rehash(plan with { DispatchDefinitionIds = ["names", "names"] })), "AmbiguousDefinition");
Reject(() => TestSuiteRunPlanner.Validate(Rehash(plan with { Definitions = [definition.Manifest with { Dependencies = ["names"] }] })), "DefinitionCycle");
Reject(() => TestSuiteRunPlanner.Validate(Rehash(plan with { Definitions = [definition.Manifest with { OriginalBytes = -1 }] })), "InvalidDefinition");
Reject(() => TestSuiteRunPlanner.Validate(Rehash(plan with { Definitions = [definition.Manifest with { RequiredScope = [scope, scope] }] })), "InvalidDefinition");
Reject(() => TestSuiteRunPlanner.Validate(Rehash(plan with { ExcludedDefinitionIds = ["unknown"] })), "InvalidSelection");
Reject(() => TestSuiteRunPlanner.Validate(Rehash(blocked with { Blockers = [] })), "InvalidRunPlan");
var excludedPlan = TestSuiteRunPlanner.Prepare([definition, Definition("omitted")], ["names"], ["omitted"], "style");
Check(excludedPlan.RequiredDefinitionIds.SequenceEqual(["names", "omitted"]) && TestSuiteResultNormalizer.Normalize(excludedPlan, [Result()]).Results[1].Outcome == "excluded", "Explicit exclusion cannot remove required coverage");
var nestedSets = TestSuiteRunPlanner.Prepare([Definition("outer", "applicationSet", ["inner"]), Definition("inner", "applicationSet", ["leaf"]), Definition("leaf", "application")], ["outer", "leaf"], [], "systemManaged");
Check(nestedSets.Blockers.Any(b => b.Contains("twice")), "Transitive set members cannot be dispatched twice");
var overlappingSets = TestSuiteRunPlanner.Prepare([Definition("left", "applicationSet", ["inner"]), Definition("right", "applicationSet", ["leaf"]), Definition("inner", "applicationSet", ["leaf"]), Definition("leaf", "application")], ["left", "right"], [], "systemManaged");
Check(overlappingSets.Blockers.Any(b => b.Contains("share a member")), "Two selected sets cannot dispatch a shared transitive member twice");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, normalized with { Results = [normalized.Results[0] with { Outcome = "passed", Reasons = [] }], RequiredCoveragePassed = true }, "raw.json"), "InvalidTestResults");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(blocked, TestSuiteResultNormalizer.Normalize(blocked, [Result()]) with { RequiredCoveragePassed = true }, "raw.json"), "InvalidTestResults");
Check(TestSuiteResultNormalizer.Findings(failure, [scope with { Object = scope.Object with { PlcPath = "PLC/Other" } }])[1].ResolvedObject is null, "Finding cannot link same path in another PLC");
Reject(() => TestSuiteResultNormalizer.Findings(failure, Enumerable.Repeat(scope, 10001).ToArray()), "TestResultLimit");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, passed, new string('a', 8193)), "InvalidTestResults");
Reject(() => TestSuiteResultNormalizer.ExportJUnit(plan, passed, "bad\u0001"), "InvalidTestResults");
NativeTestMessage[] deep = [];
for (int i = 0; i < 32; i++) deep = [new("Success", "bounded nested message", null, null, "Unknown", 0, 0, deep)];
var deepReport = TestSuiteResultNormalizer.Normalize(plan, [Result(messages: deep)]);
Check(XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(plan, deepReport, "deep.json")).Root!.Attribute("tests")!.Value == "1", "32 levels survive raw hashing and round-trip export");
deep = [new("Success", "too deep", null, null, "Unknown", 0, 0, deep)];
Reject(() => TestSuiteResultNormalizer.Normalize(plan, [Result(messages: deep)]), "TestResultLimit");
checks += IdentityTextChecks.Run();
var extendedReceipt = ExtendedChecks.Run();
checks += extendedReceipt.Checks;
checks += AvailabilityChecks.Run();
checks += CasingResultProfileChecks.Run();
checks += ApplicationRunChecks.Run(Path.Combine(Path.GetTempPath(), "TiaMcp-TestSuite-Application-" + Guid.NewGuid().ToString("N")));
checks += SystemRunChecks.Run(Path.Combine(Path.GetTempPath(), "TiaMcp-TestSuite-System-" + Guid.NewGuid().ToString("N")));
var bundleReceipt = DefinitionBundleChecks.Run();
checks += bundleReceipt.Checks;
checks += DurableChecks.Run(args.Length == 1 ? args[0] + "-durable" : Path.Combine(Path.GetTempPath(), "TiaMcp-TestSuite-" + Guid.NewGuid().ToString("N")));
checks += await ProductionJobChecks.Run(args.Length == 1 ? args[0] + "-production" : Path.Combine(Path.GetTempPath(), "TiaMcp-TestSuite-Production-" + Guid.NewGuid().ToString("N")));
checks += ResultIntakeChecks.Run(args.Length == 1 ? args[0] + "-intake" : Path.Combine(Path.GetTempPath(), "TiaMcp-TestSuite-Intake-" + Guid.NewGuid().ToString("N")));
if (args.Length == 1)
{
    var directory = Path.GetFullPath(args[0]);
    if (Directory.Exists(directory)) throw new Exception("Use fresh fixture output to preserve existing evidence.");
    Directory.CreateDirectory(directory);
    void Save(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, BridgeProtocol.Json));
    Save("definition.json", definition.Manifest); Save("run.json", plan); Save("result.json", normalized);
    Save("job-state.json", unknown); File.WriteAllText(Path.Combine(directory, "junit.xml"), TestSuiteResultNormalizer.ExportJUnit(plan, normalized, "result.json"));
    Save("extended-checks.json", extendedReceipt);
    Save("definition-bundle-checks.json", bundleReceipt);
    Save("provenance.json", new { authoredSyntheticFixture = true, nativeExecuted = false, nativeQualified = false, releaseAccepted = false, checks });
}
Console.WriteLine($"PASS Test Suite offline definitions/results/job semantics: {checks} independent checks; no native Test Suite execution.");

