using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.Engineering;

var tests = new (string, Action)[] {
    ("UA before/after comparisons preserve exact windows, mappings and original evidence", UaComparisonChecks.Run),
    ("UA diagnostic observations preserve complete prebound prefixes without promoting evidence", UaObservationChecks.Run),
    ("Prebound UA observations retain exact experiment and session intent without online authority", UaBindingChecks.Run),
    ("Versioned measurement quality and timing retain incomplete original evidence", QualityMeasurementChecks.Run),
    ("Retained runtime acquisition preserves original chains and unresolved sequential observations", RuntimeAcquisitionChecks.Run),
    ("Repeated experiment windows and combined exports retain original evidence without promoting quality", RepeatedExperimentChecks.Run),
    ("Retained experiments compose exact diagnostic evidence and review explicit before/after lineage", ExperimentChecks.Run),
    ("Runtime captures retain selected alarm evidence without granting runtime authority", RuntimeAlarmBindingChecks.Run),
    ("Explicit supplied alarm operands bind to exact executable declarations and captured arguments", AlarmBindingChecks.Run),
    ("Retained diagnostic consumers rederive source evidence before handoff", SourceRevalidationChecks.Run),
    ("Runtime capture analysis retains exact observations, gaps and unresolved behavioral verdicts", RuntimeAnalysisChecks.Run),
    ("Runtime capture mappings re-resolve exact declarations and retain unverified source context", RuntimeAssociationChecks.Run),
    ("Replanned instances retain role template without inheriting old target evidence", ReplanTemplateChecks.Run),
    ("Diagnostic report exports preserve source and readable evidence without executing content", ReportExportChecks.Run),
    ("Selected alarm context retains bounded evidence without selecting storage", SuppliedAlarmChecks.Run),
    ("Supplied Test Suite findings retain exact source through diagnostic planning and replan", SuppliedTestSuiteChecks.Run),
    ("Boolean predictions refuse missing and unsupported declarations explicitly", () => UnresolvedPredictions(false)),
    ("Numeric predictions cannot assume the type of an unresolved declaration", () => UnresolvedPredictions(true)),
    ("Saved diagnostic watch plan preserves independent signal oracle and immutable provenance", WatchPlanning),
    ("Watch planning refuses missing allowance, ambiguous instance, invalid request and source tamper", WatchRefusals),
    ("Watch planning preserves tag identity, storage scope and unresolved signals", WatchStorage),
    ("Virtual Explorer groups never enter native diagnostic identity indexes", VirtualGroups),
    ("Independent conveyor mapping and signal oracle", Mapping),
    ("Ambiguity and incomplete/protected call evidence", Ambiguity),
    ("Call context, unsupported XML and repeated invocation evidence", ContextEvidence),
    ("Qualified nested names preserve exact namespace identity", QualifiedNames),
    ("Unavailable storage and exact CPU boundary", Observability),
    ("Manual settings, stale review and unavailable native routes", Preparation),
    ("Injected synthetic event and independent predictions", Measurement),
    ("Unreproduced event and contradicted prediction", NegativePredictions),
    ("Polling, gaps, missing values and program uncertainty", Quality),
    ("Import lineage, units, timestamps, types and format boundaries", ImportBounds),
    ("Immutable persistence, tamper and path guards", Persistence),
    ("Report defaults and automatic dependency/argument selection", AutomaticSelection),
    ("IN_OUT actuals, competing writers and graphical argument evidence", BindingAndWriterEvidence),
    ("CSV mapping preserves negative pre-trigger times and explicit units", CsvMapping),
    ("Explicit capture alignment and source/instance reuse", CompareAndReuse),
    ("Independent supplied readback detects identity, settings and resource mismatches", Readback),
    ("Numeric evidence rejects silent underflow and precision loss", NumericPrecision),
    ("Gap thresholds preserve exact decimal intervals", ExactIntervalGaps),
    ("Retained imports are reassessed without rewriting historical evidence", RetainedTiming),
    ("Event alignment refuses rounded relative timestamps", ExactEventAlignment),
    ("Explicit comparison mappings cannot silently become automatic selection", ComparisonOptions),
    ("Comparison refuses changed declared programs without explicit admission", ComparisonProgramIdentity),
    ("Comparison retains both acquisition contexts without claiming synchronized clocks", ComparisonAcquisitionContext),
    ("Uploaded measurement bytes survive release and reject tamper or oversize", UploadedInputs),
    ("Nanosecond timestamps and separate documented AX capability remain explicit", NanosecondsAndAxRoute),
};
int failures = 0;
foreach (var (name, run) in tests)
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + e); }
Console.WriteLine($"{tests.Length - failures}/{tests.Length} offline diagnostic groups passed. No Siemens/native acquisition qualification.");
if (failures == 0 && args.Length == 1)
{
    using var f = new Fixture(); f.AddVirtualGroups(); var plan = f.Plan(); var trace = f.Prepare(plan); var measurement = f.Capture(Id(trace));
    string importId = Id(f.Import(Id(trace), measurement)); var report = f.Measurements.Analyze(importId);
    var readback = f.Readback(trace);
    var readbackCheck = new TraceReadback(f.Root).Check(Id(trace), f.Artifact(Encoding.UTF8.GetBytes(readback.ToJsonString()), ".json"));
    var comparisonOptions = JsonNode.Parse("""{"reason":"Synthetic same-capture comparison","beforeAnchorSeconds":0.02,"afterAnchorSeconds":0.02,"windowStartSeconds":-0.02,"windowEndSeconds":0.03}""")!.AsObject();
    var comparison = new TraceComparison(f.Root).Compare(importId, importId, comparisonOptions);
    using var automatic = new Fixture(); automatic.AddDependencyEvidence(); var automaticRequest = automatic.Request(); automaticRequest.Remove("signals");
    automaticRequest["hypotheses"]![0]!.AsObject().Remove("prediction");
    automaticRequest["autoSignals"] = JsonNode.Parse("""{"seeds":[{"scope":"LocalVariable","components":["RunCmd"],"reason":"Command dependencies"}],"maxDepth":4}""");
    var automaticPlan = automatic.Planner.Plan(automaticRequest);
    var samples = new JsonObject {
        ["uaComparisonFixture"] = UaComparisonChecks.Samples!.DeepClone(),
        ["uaBindingFixture"] = UaBindingChecks.Samples!.DeepClone(),
        ["uaObservationFixture"] = UaObservationChecks.Samples!.DeepClone(),
        ["qualityMeasurementFixture"] = QualityMeasurementChecks.Samples!.DeepClone(),
        ["runtimeAcquisitionFixture"] = RuntimeAcquisitionChecks.Samples!.DeepClone(),
        ["testSuitePacket"] = SuppliedTestSuiteChecks.Packet(f),
        ["diagnostic-case.v1"] = plan["plan"]!.DeepClone(), ["diagnostic-signal-manifest.v1"] = plan["plan"]!["signals"]!.DeepClone(),
        ["trace-plan.v1"] = trace["specification"]!.DeepClone(), ["trace-measurement.v1"] = measurement,
        ["diagnostic-report.v1"] = report["report"]!.DeepClone(), ["trace-capabilities.v1"] = TracePlanning.Capabilities(),
        ["trace-readback.v1"] = readback, ["trace-readback-check.v1"] = readbackCheck["readbackCheck"]!.DeepClone(),
        ["trace-comparison.v1"] = comparison["comparison"]!.DeepClone(), ["comparisonOptions"] = comparisonOptions,
        ["automaticCase"] = automaticPlan["plan"]!.DeepClone(), ["automaticRequest"] = automaticRequest,
        ["automaticReviewArtifactBase64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(automatic.Root, "project-reviews", automatic.ReviewId + ".json"))),
        ["reviewArtifactBase64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(f.Root, "project-reviews", f.ReviewId + ".json"))),
        ["diagnosticRequest"] = f.Request(), ["prepareRequest"] = f.Settings(), ["watchRequest"] = WatchRequest() };
    using var alarms = new Fixture(); SuppliedAlarmChecks.AddEvidence(alarms);
    samples["alarmRequest"] = SuppliedAlarmChecks.Request(alarms);
    samples["alarmReviewArtifactBase64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(alarms.Root, "project-reviews", alarms.ReviewId + ".json")));
    using var boundAlarms = new Fixture(); AlarmBindingChecks.AddEvidence(boundAlarms);
    samples["runtime-alarm-capture.v1"] = RuntimeAlarmBindingChecks.Exported!.DeepClone();
    samples["alarmBindingRequest"] = AlarmBindingChecks.Request(boundAlarms);
    samples["alarmBindingTestSuitePacket"] = SuppliedTestSuiteChecks.Packet(boundAlarms);
    samples["alarmBindingReviewArtifactBase64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(boundAlarms.Root, "project-reviews", boundAlarms.ReviewId + ".json")));
    samples["diagnostic-alarm-bindings.v1"] = boundAlarms.Planner.Plan(AlarmBindingChecks.Request(boundAlarms))["plan"]!["alarmBindings"]!.DeepClone();
    boundAlarms.ReplaceXml("FB_Conveyor", s => s.Replace("Name=\"Enable\" Datatype=\"Bool\"", "Name=\"Enable\" Datatype=\"Int\""));
    boundAlarms.ReplaceXml("DB_Line", s => s.Replace("Name=\"Enable\" Datatype=\"Bool\"", "Name=\"Enable\" Datatype=\"Int\""));
    samples["alarmBindingChangedRequest"] = AlarmBindingChecks.Request(boundAlarms);
    samples["alarmBindingChangedRequest"]!["hypotheses"]![0]!.AsObject().Remove("prediction");
    samples["alarmBindingChangedReviewArtifactBase64"] = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(boundAlarms.Root, "project-reviews", boundAlarms.ReviewId + ".json")));
    var watchReceipt = new DiagnosticWatchPlanning(f.Root).Plan(Id(plan), WatchRequest());
    samples["diagnostic-watch-plan.v1"] = new DiagnosticStore(f.Root).Read(Id(watchReceipt));
    var experiment = new DiagnosticExperiments(f.Root).Plan(Id(plan), ExperimentChecks.Request(f));
    samples["experimentRequest"] = ExperimentChecks.Request(f);
    samples["diagnostic-experiment.v1"] = experiment["experiment"]!.DeepClone();
    var experimentReview = new DiagnosticExperiments(f.Root).Review(Id(experiment), ExperimentChecks.ReviewRequest(Id(experiment), importId, importId));
    samples["diagnostic-experiment-review.v1"] = experimentReview["review"]!.DeepClone();
    File.WriteAllText(args[0], samples.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
return failures == 0 ? 0 : 1;

static JsonObject WatchRequest() => new() { ["tableName"] = "Conveyor 2 diagnosis", ["tiaVersion"] = 21,
    ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Compare external enable and final output with the selected conveyor command." }) };
static void WatchPlanning()
{
    using var f = new Fixture(); f.AddVirtualGroups(); string caseId = Id(f.Plan());
    var planner = new DiagnosticWatchPlanning(f.Root); var receipt = planner.Plan(caseId, WatchRequest());
    Equal(Id(receipt), Id(planner.Plan(caseId, WatchRequest())));
    Equal(11, receipt["rowCount"]!.GetValue<int>()); Equal(0, receipt["unresolvedCount"]!.GetValue<int>());
    var document = new DiagnosticStore(f.Root).Read(Id(receipt), "diagnostic-watch-plan.v1");
    Equal(false, document["sourceAuthenticityVerified"]!.GetValue<bool>());
    Equal("callerSuppliedUnverified", document["tiaVersionOrigin"]!.GetValue<string>());
    var plan = document["watchPlan"]!.AsObject();
    Equal(f.ReviewId, plan["source"]!["snapshot"]!["sourceSha256"]!.GetValue<string>());
    Equal(false, plan["nativeExecutable"]!.GetValue<bool>());
    foreach (var expected in f.Oracle["signals"]!.AsArray())
    {
        var actual = plan["rows"]!.AsArray().Single(r => r!["candidateId"]!.GetValue<string>() == expected!["id"]!.GetValue<string>())!["candidate"]!;
        Equal(expected!["type"]!.GetValue<string>(), actual["datatype"]!.GetValue<string>());
        Equal(true, JsonNode.DeepEquals(expected["memberPath"], actual["memberPath"]));
        Equal(Fixture.Block(expected["db"]!.GetValue<string>()), actual["storage"]!["object"]!["objectPath"]!.GetValue<string>());
        Equal(true, actual["reason"]!.GetValue<string>().Contains("writer prediction"));
    }
    string hash = plan["planSha256"]!.GetValue<string>(); plan.Remove("planSha256"); Equal(hash, CanonicalJson.Sha256(plan));
    var limited = WatchRequest(); limited["maxRows"] = 2;
    var bounded = planner.Plan(caseId, limited);
    Equal(2, bounded["rowCount"]!.GetValue<int>()); Equal(11, bounded["candidateCount"]!.GetValue<int>());
    Equal(9, bounded["unresolvedCount"]!.GetValue<int>()); Equal(true, bounded["truncated"]!.GetValue<bool>());
}
static void WatchRefusals()
{
    using var f = new Fixture(); var savedCase = f.Plan(); string caseId = Id(savedCase); var planner = new DiagnosticWatchPlanning(f.Root);
    var r = WatchRequest(); r.Remove("externalStorage"); Error("ExternalStorageNotSelected", () => planner.Plan(caseId, r));
    r = WatchRequest(); r["externalStorage"]!.AsArray().Add(r["externalStorage"]![0]!.DeepClone()); Error("InvalidRequest", () => planner.Plan(caseId, r));
    r = WatchRequest(); r["externalStorage"]![0]!["objectPath"] = Fixture.Block("Main"); Error("InvalidRequest", () => planner.Plan(caseId, r));
    foreach (string field in new[] { "tiaVersion", "maxRows", "externalStorage" })
    { r = WatchRequest(); r[field] = null; Error("InvalidRequest", () => planner.Plan(caseId, r)); }
    r = WatchRequest(); r.Remove("tiaVersion"); Error("InvalidRequest", () => planner.Plan(caseId, r));
    r = WatchRequest(); r["tiaVersion"] = 101; Error("InvalidRequest", () => planner.Plan(caseId, r));
    r = WatchRequest(); r["maxRows"] = 65; Error("InvalidRequest", () => planner.Plan(caseId, r));
    r = WatchRequest(); r["nativeExecutable"] = true; Error("InvalidRequest", () => planner.Plan(caseId, r));
    var ambiguous = f.Request(); ambiguous.Remove("instance"); ambiguous.Remove("signals");
    Error("InstanceResolutionIncomplete", () => planner.Plan(Id(f.Planner.Plan(ambiguous)), WatchRequest()));
    var changedCase = savedCase["plan"]!.DeepClone().AsObject(); changedCase["sourceRevision"]!["documents"]![0]!["utf8Sha256"] = new string('0', 64);
    Error("CaptureMismatch", () => planner.Plan(Id(new DiagnosticStore(f.Root).Save(changedCase)), WatchRequest()));
    string reviewPath = Path.Combine(f.Root, "project-reviews", f.ReviewId + ".json"); byte[] bytes = File.ReadAllBytes(reviewPath);
    File.AppendAllText(reviewPath, " "); Error("ArtifactChanged", () => planner.Plan(caseId, WatchRequest())); File.WriteAllBytes(reviewPath, bytes);
    string casePath = savedCase["artifact"]!["path"]!.GetValue<string>(); File.AppendAllText(casePath, " ");
    Error("ArtifactChanged", () => planner.Plan(caseId, WatchRequest()));
}
static void WatchStorage()
{
    using var f = new Fixture(); string tagPath = Fixture.Plc + "/tags/Sensor";
    f.Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "sensor", ["objectPath"] = tagPath, ["kind"] = "tag", ["name"] = "Sensor", ["plcPath"] = Fixture.Plc, ["scopePath"] = Fixture.Plc + "/tags",
        ["metadata"] = new JsonObject { ["dataType"] = "Bool", ["externalAccessible"] = true, ["externalVisible"] = true } });
    f.SaveReview(); var diagnostic = f.Request(); diagnostic["signals"]![0]!["storagePath"] = tagPath; diagnostic["signals"]![0]!["memberPath"] = new JsonArray("Sensor");
    diagnostic["signals"]![9]!["memberPath"] = new JsonArray("Conv2", "Scratch");
    var request = WatchRequest(); request["externalStorage"]!.AsArray().Add(new JsonObject { ["objectPath"] = tagPath, ["reason"] = "Observe exact captured sensor." });
    var receipt = new DiagnosticWatchPlanning(f.Root).Plan(Id(f.Planner.Plan(diagnostic)), request);
    Equal(10, receipt["rowCount"]!.GetValue<int>()); Equal(1, receipt["unresolvedCount"]!.GetValue<int>());
    var watch = new DiagnosticStore(f.Root).Read(Id(receipt))["watchPlan"]!;
    var storage = watch["rows"]![0]!["candidate"]!["storage"]!;
    Equal("tag", storage["object"]!["kind"]!.GetValue<string>());
    Equal(Fixture.Plc + "/tags", storage["object"]!["scopePath"]!.GetValue<string>());
    Equal(true, storage["contentSha256"] is null);
    Equal(false, watch["nativeDocumentGenerated"]!.GetValue<bool>());
}

static void VirtualGroups()
{
    using var f = new Fixture(); f.AddVirtualGroups(); var plan = f.Plan()["plan"]!;
    Equal("Conv2", plan["selectedInstance"]!["memberPath"]![0]!.GetValue<string>());
    Equal(11, plan["signals"]!.AsArray().Count);
    Equal(false, plan["candidates"]!.AsArray().Any(n => n!["objectPath"]!.GetValue<string>().Length == 0));
}

static void Mapping()
{
    using var f = new Fixture(); var result = f.Plan(); var plan = result["plan"]!;
    Equal(2, plan["instances"]!.AsArray().Count);
    Equal(f.Oracle["selectedDb"]!.GetValue<string>(), plan["selectedInstance"]!["dbPath"]!.GetValue<string>());
    Equal("Conv2", plan["selectedInstance"]!["memberPath"]![0]!.GetValue<string>());
    Equal("Conv1", plan["instances"]![0]!["memberPath"]![0]!.GetValue<string>());
    Equal(1, plan["selectedInstance"]!["rootCallSites"]!.AsArray().Count);
    Equal(11, plan["signals"]!.AsArray().Count);
    foreach (var expected in f.Oracle["signals"]!.AsArray())
    {
        var actual = plan["signals"]!.AsArray().Single(s => s!["id"]!.GetValue<string>() == expected!["id"]!.GetValue<string>())!;
        Equal(expected!["type"]!.GetValue<string>(), actual["datatype"]!.GetValue<string>());
        Equal(true, JsonNode.DeepEquals(expected["memberPath"], actual["memberPath"]));
        Equal("requiresTargetValidation", actual["traceability"]!.GetValue<string>());
        Equal(true, actual["evidence"]!["xmlLine"]!.GetValue<int>() > 0 && actual["onlineAddress"] is null);
    }
    Equal(false, plan["nativeExecutable"]!.GetValue<bool>());
}
static void Ambiguity()
{
    using var f = new Fixture(); var request = f.Request(); request.Remove("blockPath"); request.Remove("instance"); request.Remove("signals");
    var plan = f.Planner.Plan(request)["plan"]!;
    Equal(true, plan["selectedInstance"] is null && plan["candidates"]!.AsArray().Count > 0);
    request = f.Request(); request["instance"]!["memberPath"] = new JsonArray("Conv1");
    Equal("Conv1", f.Planner.Plan(request)["plan"]!["selectedInstance"]!["memberPath"]![0]!.GetValue<string>());
    // Duplicate unnamespaced FB names prevent silent first-match selection.
    var duplicate = f.Model["nodes"]![3]!.DeepClone(); duplicate["objectPath"] = Fixture.Plc + "/groups/Other/FB_Conveyor"; duplicate["nodeId"] = "other";
    f.Model["nodes"]!.AsArray().Add(duplicate);
    var doc = f.Documents.Single(d => d!["objectPath"]!.GetValue<string>().EndsWith("/FB_Conveyor"))!.DeepClone(); doc["objectPath"] = duplicate["objectPath"]!.DeepClone(); f.Documents.Add(doc);
    f.SaveReview(); Error("InstanceResolutionIncomplete", () => f.Plan());
    f.Model["nodes"]!.AsArray().Remove(duplicate); f.Documents.Remove(doc);
    var line = f.Documents.Single(d => d!["objectPath"]!.GetValue<string>().EndsWith("/FB_Line"))!;
    line["status"] = "unavailable"; f.SaveReview(); Error("InstanceResolutionIncomplete", () => f.Plan());
    line["status"] = "exported"; f.Model["nodes"]![2]!["metadata"]!["isKnowHowProtected"] = true;
    f.SaveReview(); Error("InstanceResolutionIncomplete", () => f.Plan());
}
static void Observability()
{
    using var f = new Fixture();
    foreach (string member in new[] { "Scratch", "Alias", "ArrayData", "Pointer^", "[0]" })
    {
        var r = f.Request(); r["signals"]![0]!["storagePath"] = Fixture.Block("DB_Line"); r["signals"]![0]!["memberPath"] = new JsonArray("Conv2", member);
        var p = f.Planner.Plan(r); Equal("unavailable", p["plan"]!["signals"]![0]!["resolution"]!.GetValue<string>());
        Error("SignalUnobservable", () => f.Prepare(p));
    }
    var wrongCpu = f.Request(); wrongCpu["signals"]![0]!["storagePath"] = "/another/CPU/DB_IO";
    Error("ObjectNotFound", () => f.Planner.Plan(wrongCpu));
    var unsupported = f.Request(); unsupported["signals"]![0]!["hypothesisIds"] = new JsonArray("missing");
    Error("InvalidRequest", () => f.Planner.Plan(unsupported));
}
static void UnresolvedPredictions(bool numeric)
{
    using var f = new Fixture();
    foreach (string member in new[] { "Missing", "ArrayData" })
    foreach (string section in new[] { "when", "expect" })
    {
        var request = f.Request();
        request["signals"]![1]!["memberPath"] = new JsonArray("Conv2", member);
        var prediction = request["hypotheses"]![0]!["prediction"]!.AsObject();
        prediction["when"] = JsonNode.Parse("""[{"signalId":"run","op":"eq","value":true}]""");
        prediction["expect"] = JsonNode.Parse("""[{"signalId":"final","op":"eq","value":true}]""");
        var condition = new JsonObject { ["signalId"] = "enable", ["op"] = "eq",
            ["value"] = numeric ? JsonValue.Create(1) : JsonValue.Create(true) };
        if (numeric) condition["unit"] = "1";
        prediction[section] = new JsonArray(condition);
        Error("SignalUnobservable", () => f.Planner.Plan(request));
    }
    // A captured scalar type is still checkable even when its access is unavailable.
    var restricted = f.Request(); restricted["signals"]![1]!["memberPath"] = new JsonArray("Conv2", "Alias");
    var caseReceipt = f.Planner.Plan(restricted);
    Equal("unavailable", caseReceipt["plan"]!["signals"]![1]!["resolution"]!.GetValue<string>());
    Error("SignalUnobservable", () => f.Prepare(caseReceipt));
    restricted["hypotheses"]![0]!["prediction"]!["expect"]![2]!["op"] = "lt";
    Error("InvalidRequest", () => f.Planner.Plan(restricted));
}
static void ContextEvidence()
{
    using (var f = new Fixture())
    {
        f.Documents.Single(d => d!["objectPath"]!.GetValue<string>() == Fixture.Block("Main"))!["status"] = "unavailable";
        f.SaveReview(); var plan = f.Plan(); Equal(0, plan["plan"]!["selectedInstance"]!["rootCallSites"]!.AsArray().Count);
        Error("InstanceResolutionIncomplete", () => f.Prepare(plan));
    }
    using (var f = new Fixture())
    {
        f.ReplaceXml("FB_Line", xml => xml.Replace("StructuredText/v4", "StructuredText/v99"));
        Error("InstanceResolutionIncomplete", () => f.Plan());
    }
    using (var f = new Fixture())
    {
        f.ReplaceXml("FB_Line", xml => xml.Replace("</StructuredText>", "<Access Scope='Call'><CallInfo Name='FB_Conveyor' BlockType='FB'><Instance Scope='LocalVariable'><Component Name='Conv2'/></Instance></CallInfo></Access></StructuredText>"));
        Equal(2, f.Plan()["plan"]!["selectedInstance"]!["callSites"]!.AsArray().Count);
    }
    using (var f = new Fixture())
    {
        f.Model["edges"]![0]!["source"]!["nodeId"] = "wrong-node"; f.SaveReview();
        Error("InstanceResolutionIncomplete", () => f.Plan());
    }
    using (var f = new Fixture())
    {
        f.Model["nodes"]![3]!["metadata"]!["namespace"] = "Other"; f.SaveReview();
        Error("InstanceResolutionIncomplete", () => f.Plan());
    }
}
static void Preparation()
{
    using var f = new Fixture(); var plan = f.Plan(); var prepared = f.Prepare(plan);
    Equal("partialManualHandoff", prepared["specification"]!["fulfillment"]!.GetValue<string>());
    Equal(true, prepared["specification"]!["effectiveSettings"] is null);
    Equal(false, prepared["specification"]!["mutationAttempted"]!.GetValue<bool>());
    var request = f.Settings(); request["reviewId"] = new string('0', 64);
    Error("TracePlanStale", () => f.Traces.Prepare(Id(plan), request));
    request = f.Settings(); request["backend"] = "nativeApi"; Error("UnsupportedCapability", () => f.Traces.Prepare(Id(plan), request));
    request = f.Settings(); request["settings"]!["trigger"]!["kind"] = "compound"; Error("UnsupportedCapability", () => f.Traces.Prepare(Id(plan), request));
    request = f.Settings(); request["settings"]!["trigger"]!["signalId"] = "step"; Error("InvalidRequest", () => f.Traces.Prepare(Id(plan), request));
    request = f.Settings(); request["settings"]!["sampleEvery"] = 0; Error("InvalidRequest", () => f.Traces.Prepare(Id(plan), request));
    request = f.Settings(); request["settings"]!["requireOneScanDetection"] = true; Error("AcquisitionRequirementUnsatisfied", () => f.Traces.Prepare(Id(plan), request));
    Equal(false, TracePlanning.Capabilities()["targetProbed"]!.GetValue<bool>());
    Equal(8, TracePlanning.Capabilities()["diagnosticTemplates"]!.AsArray().Count);
}
static void QualifiedNames()
{
    using var f = new Fixture();
    f.Model["nodes"]![3]!["metadata"]!["namespace"] = "LineTypes";
    f.ReplaceXml("FB_Line", xml => xml.Replace("&quot;FB_Conveyor&quot;", "&quot;LineTypes.FB_Conveyor&quot;").Replace("Name=\"FB_Conveyor\"", "Name=\"LineTypes.FB_Conveyor\""));
    f.ReplaceXml("DB_Line", xml => xml.Replace("&quot;FB_Conveyor&quot;", "&quot;LineTypes.FB_Conveyor&quot;"));
    Equal("LineTypes", f.Plan()["plan"]!["selectedInstance"]!["namespace"]!.GetValue<string>());
    f.Model["nodes"]![4]!["metadata"]!["namespace"] = "Storage";
    f.ReplaceXml("Main", xml => xml.Replace("Name=\"DB_Line\"", "Name=\"Storage.DB_Line\""));
    var result = f.Plan(); Equal(1, result["plan"]!["selectedInstance"]!["rootCallSites"]!.AsArray().Count);
    Equal("Storage.DB_Line", result["plan"]!["signals"]![1]!["symbolComponents"]![0]!.GetValue<string>());
    // A same-name FB in another namespace cannot steal the explicitly qualified binding.
    var duplicate = f.Model["nodes"]![3]!.DeepClone(); duplicate["nodeId"] = "other"; duplicate["objectPath"] = Fixture.Plc + "/blocks/namespaces/Other/FB_Conveyor"; duplicate["metadata"]!["namespace"] = "Other";
    f.Model["nodes"]!.AsArray().Add(duplicate); f.SaveReview(); Equal(2, f.Plan()["plan"]!["instances"]!.AsArray().Count);
}
static void Measurement()
{
    using var f = new Fixture(); var prepared = f.Prepare(f.Plan()); var input = f.Capture(Id(prepared));
    var imported = f.Import(Id(prepared), input); var report = f.Measurements.Analyze(Id(imported))["report"]!;
    foreach (string field in new[] { "mode", "clock", "timeUnit", "programIdentity" })
        Equal(input[field]!.GetValue<string>(), report["acquisitionContext"]![field]!.GetValue<string>());
    Equal(false, report["acquisitionContext"]!["identityVerified"]!.GetValue<bool>());
    Equal(false, report["clockSynchronizationVerified"]!.GetValue<bool>());
    Equal(f.Oracle["expectedPrediction"]!.GetValue<string>(), report["hypotheses"]![0]!["verdict"]!.GetValue<string>());
    Equal(f.Oracle["expectedMatches"]!.GetValue<int>(), report["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>());
    Equal(f.Oracle["expectedTransitions"]!.GetValue<int>(), report["transitionCount"]!.GetValue<int>());
    Equal(false, report["nativeQualification"]!.GetValue<bool>());
    Equal(false, report["causationProven"]!.GetValue<bool>());
    Equal("ms", report["units"]!["elapsed"]!.GetValue<string>());
    var numeric = f.Request(); numeric["hypotheses"]![0]!["prediction"]!["expect"]!.AsArray().Add(JsonNode.Parse("""{"signalId":"elapsed","op":"ge","value":1000,"unit":"ms"}"""));
    string numericTrace = Id(f.Prepare(f.Planner.Plan(numeric))); var wrongUnit = f.Capture(numericTrace); wrongUnit["units"]!["elapsed"] = "s";
    string importedWrongUnit = Id(f.Import(numericTrace, wrongUnit)); Error("MeasurementLineageMismatch", () => f.Measurements.Analyze(importedWrongUnit));
}
static void NegativePredictions()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan())); var m = f.Capture(trace);
    foreach (var row in m["samples"]!.AsArray()) row!["values"]!["final"] = true;
    Equal("unresolved", f.Measurements.Analyze(Id(f.Import(trace, m)))["report"]!["hypotheses"]![0]!["verdict"]!.GetValue<string>());
    m = f.Capture(trace); m["samples"]![2]!["values"]!["run"] = false;
    Equal("contradictedPrediction", f.Measurements.Analyze(Id(f.Import(trace, m)))["report"]!["hypotheses"]![0]!["verdict"]!.GetValue<string>());
}
static void Quality()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    foreach (string mutation in new[] { "nonAtomic", "stale", "gap", "missing", "reportedLoss", "unknownLoss" })
    {
        var m = f.Capture(trace);
        if (mutation == "nonAtomic") { m["mode"] = "polling"; m["quality"]!["atomicAcrossSignals"] = false; m["quality"]!["scanSynchronized"] = false; }
        if (mutation == "stale") m["quality"]!["programMatchVerified"] = false;
        if (mutation == "gap") m["samples"]![5]!["time"] = 80;
        if (mutation == "missing") m["samples"]![2]!["values"]!["run"] = null;
        if (mutation == "reportedLoss") m["quality"]!["reportedDroppedSamples"] = 1;
        if (mutation == "unknownLoss") m["quality"]!["dropoutsKnown"] = false;
        Equal("unresolved", f.Measurements.Analyze(Id(f.Import(trace, m)))["report"]!["hypotheses"]![0]!["verdict"]!.GetValue<string>());
    }
    var polling = f.Capture(trace); polling["mode"] = "polling";
    Error("AcquisitionRequirementUnsatisfied", () => f.Import(trace, polling));
}
static void ImportBounds()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var m = f.Capture(trace); m["reviewId"] = new string('0', 64); Error("MeasurementLineageMismatch", () => f.Import(trace, m));
    m = f.Capture(trace); m["samples"]![1]!["time"] = 0; Error("MeasurementIncomplete", () => f.Import(trace, m));
    m = f.Capture(trace); m["samples"]![0]!["values"]!["run"] = "true"; Error("MeasurementFormatUnsupported", () => f.Import(trace, m));
    m = f.Capture(trace); m["samples"]![0]!["values"]!["step"] = 65536; Error("MeasurementFormatUnsupported", () => f.Import(trace, m));
    m = f.Capture(trace); m["samples"]![0]!["values"]!["step"] = 1.5; Error("MeasurementFormatUnsupported", () => f.Import(trace, m));
    m = f.Capture(trace); m["units"]!.AsObject().Remove("run"); Error("MeasurementLineageMismatch", () => f.Import(trace, m));
    m = f.Capture(trace); m["schemaVersion"] = "ttrecx"; Error("MeasurementFormatUnsupported", () => f.Import(trace, m));
    m = f.Capture(trace); m["mode"] = "manualNativeExport"; Error("MeasurementIncomplete", () => f.Import(trace, m));
    m = f.Capture(trace); m["samples"]!.AsArray().Clear(); Error("ArtifactCapacity", () => f.Import(trace, m));
}
static void Persistence()
{
    using var f = new Fixture(); var plan = f.Plan(); string id = Id(plan);
    Equal("diagnostic-case.v1", new DiagnosticStore(f.Root).Read(id)["schemaVersion"]!.GetValue<string>());
    Equal(id, Id(f.Plan()));
    File.AppendAllText(plan["artifact"]!["path"]!.GetValue<string>(), " ");
    Error("ArtifactChanged", () => new DiagnosticStore(f.Root).Read(id));
    Error("InvalidRequest", () => new DiagnosticStore(f.Root).Read("../outside"));
    // Existing unrelated trace artifacts are never removed or replaced.
    string ownedByUser = Path.Combine(f.Root, "user.ttrecx"); File.WriteAllText(ownedByUser, "keep");
    _ = TracePlanning.Capabilities(); Equal("keep", File.ReadAllText(ownedByUser));
    using (var held = new FileStream(Path.Combine(f.Root, "trace-diagnostics", ".write.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Error("ArtifactBusy", () => new DiagnosticStore(f.Root).Save(new JsonObject { ["schemaVersion"] = "lock-test.v1" }));
}
static void AutomaticSelection()
{
    using var f = new Fixture(); var simple = new JsonObject { ["reviewId"] = f.ReviewId, ["report"] = "Conveyor 2 occasionally stops although enable remains true." };
    var discovery = f.Planner.Plan(simple)["plan"]!; Equal(4, discovery["hypotheses"]!.AsArray().Count); Equal(true, discovery["candidates"]!.AsArray().Count > 0);
    f.AddDependencyEvidence();
    var request = f.Request(); request.Remove("signals"); request["hypotheses"]![0]!.AsObject().Remove("prediction");
    request["autoSignals"] = JsonNode.Parse("""{"maxSignals":16,"maxDepth":4,"seeds":[{"scope":"LocalVariable","components":["RunCmd"],"reason":"Reported stop: compare internal command and its dependencies."}]}""");
    var autoReceipt = f.Planner.Plan(request); var plan = autoReceipt["plan"]!;
    var signals = plan["signals"]!.AsArray();
    bool Has(string db, params string[] path) => signals.Any(s => s!["storagePath"]!.GetValue<string>() == Fixture.Block(db)
        && s["memberPath"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(path));
    Equal(true, Has("DB_Line", "Conv2", "RunCmd")); Equal(true, Has("DB_Line", "Conv2", "Enable"));
    Equal(true, Has("DB_IO", "Enable2")); Equal(true, Has("DB_IO", "Final2"));
    Equal(false, Has("DB_Line", "Conv1", "RunCmd"));
    Equal(true, signals.All(s => s!["inclusionOrigin"]!.GetValue<string>() == "boundedDependencyEvidence" && s["selectionEvidence"]!.AsArray().Count > 0));
    var autoSettings = f.Settings(); autoSettings["settings"]!["trigger"]!["signalId"] = signals.Single(s => s!["memberPath"]!.ToJsonString() == "[\"Final2\"]")!["id"]!.DeepClone();
    Equal(false, f.Traces.Prepare(Id(autoReceipt), autoSettings)["specification"]!["signalSelectionCoverage"]!["complete"]!.GetValue<bool>());
    request["autoSignals"]!["maxSignals"] = 1;
    Equal(true, f.Planner.Plan(request)["plan"]!["automaticSelection"]!["truncated"]!.GetValue<bool>());
    // Captured tags are exact declarations, not inferred process-image addresses.
    f.Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "sensor", ["objectPath"] = Fixture.Plc + "/tags/Sensor", ["kind"] = "tag", ["name"] = "Sensor", ["plcPath"] = Fixture.Plc, ["scopePath"] = Fixture.Plc,
        ["metadata"] = new JsonObject { ["dataType"] = "Bool", ["logicalAddress"] = "%I0.0", ["externalAccessible"] = true, ["externalVisible"] = true, ["isSafety"] = false } });
    f.SaveReview(); request = f.Request(); request["signals"]![0]!["storagePath"] = Fixture.Plc + "/tags/Sensor"; request["signals"]![0]!["memberPath"] = new JsonArray("Sensor");
    Equal("savedNativeTagMetadata", f.Planner.Plan(request)["plan"]!["signals"]![0]!["evidence"]!["origin"]!.GetValue<string>());
}
static void CsvMapping()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan())); var m = f.Capture(trace);
    var columns = new JsonArray(); var header = new List<string> { "seconds" };
    foreach (var s in f.Oracle["signals"]!.AsArray()) { string id = s!["id"]!.GetValue<string>(); header.Add(id); columns.Add(new JsonObject { ["signalId"] = id, ["column"] = id, ["unit"] = m["units"]![id]!.DeepClone() }); }
    var lines = new List<string> { string.Join(';', header) };
    int index = 0;
    foreach (var sample in m["samples"]!.AsArray())
    {
        var values = new List<string> { ((index++ - 2) / 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (string id in header.Skip(1)) { var v = sample!["values"]![id]!; values.Add(v is JsonValue j && j.TryGetValue<bool>(out bool b) ? b ? "1" : "0" : v.ToJsonString()); }
        lines.Add(string.Join(';', values));
    }
    string csv = string.Join("\r\n", lines) + "\r\n";
    var quality = m["quality"]!.DeepClone(); quality["expectedInterval"] = 0.01m;
    var options = new JsonObject { ["delimiter"] = ";", ["decimalSeparator"] = ".", ["headerRecord"] = 0, ["timeColumn"] = "seconds", ["timeUnit"] = "s",
        ["mode"] = "synthetic", ["clock"] = "synthetic negative pre-trigger times", ["programIdentity"] = "fixture", ["quality"] = quality, ["columns"] = columns };
    var imported = f.Measurements.Import(trace, f.Artifact(Encoding.UTF8.GetBytes(csv), ".csv"), options);
    var stored = new DiagnosticStore(f.Root).Read(Id(imported)); Equal(-0.02m, stored["measurement"]!["samples"]![0]!["time"]!.GetValue<decimal>());
    Equal(3, f.Measurements.Analyze(Id(imported))["report"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>());
    Error("MeasurementIncomplete", () => f.Measurements.Import(trace, f.Artifact(Encoding.UTF8.GetBytes(csv + "broken\r\n"), ".csv"), options));
    var missingColumn = (JsonObject)options.DeepClone(); missingColumn["columns"]![0]!["column"] = "missing";
    Error("MeasurementLineageMismatch", () => f.Measurements.Import(trace, f.Artifact(Encoding.UTF8.GetBytes(csv), ".csv"), missingColumn));
}
static void BindingAndWriterEvidence()
{
    using var f = new Fixture(); f.AddDependencyEvidence();
    f.ReplaceXml("Main", xml => xml.Replace("</StructuredText>", "<Token Text='IF'/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Override2'/></Symbol></Access><Token Text='THEN'/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Final2'/></Symbol></Access><Token Text=':='/><Token Text='FALSE'/><Token Text=';'/><Token Text='END_IF'/></StructuredText>"));
    f.ReplaceXml("FB_Conveyor", xml => xml.Replace("<Section Name=\"Temp\">", "<Section Name='InOut'><Member Name='Alias' Datatype='Bool'/></Section><Section Name=\"Temp\">"));
    f.ReplaceXml("FB_Line", xml => xml.Replace("<Parameter Name='Enable'>", "<Parameter Name='Alias'><Token Text=':='/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Permit2'/></Symbol></Access></Parameter><Parameter Name='Enable'>"));
    var request = f.Request(); request.Remove("signals"); request["hypotheses"]![0]!.AsObject().Remove("prediction");
    request["autoSignals"] = JsonNode.Parse("""{"maxSignals":16,"maxDepth":5,"seeds":[{"scope":"LocalVariable","components":["RunCmd"],"reason":"Writer detection"},{"scope":"LocalVariable","components":["Alias"],"reason":"Resolve actual IN_OUT storage"}]}""");
    var signals = f.Planner.Plan(request)["plan"]!["signals"]!.AsArray();
    Equal(true, signals.Any(s => s!["memberPath"]!.ToJsonString() == "[\"Override2\"]"));
    Equal(true, signals.Any(s => s!["memberPath"]!.ToJsonString() == "[\"Permit2\"]"));
    Equal(false, signals.Any(s => s!["memberPath"]!.ToJsonString().Contains("Alias")));
    // Use a separately authored FlgNet wiring shape, preserving the same declarations.
    f.ReplaceXml("FB_Line", xml => System.Text.RegularExpressions.Regex.Replace(xml, "<NetworkSource>.*?</NetworkSource>", """
        <NetworkSource><FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"><Parts>
        <Access Scope="GlobalVariable" UId="1"><Symbol><Component Name="DB_IO"/><Component Name="Enable2"/></Symbol></Access>
        <Call UId="2"><CallInfo Name="FB_Conveyor" BlockType="FB"><Instance Scope="LocalVariable"><Component Name="Conv2"/></Instance><Parameter Name="Enable" Section="Input" Type="Bool"/></CallInfo></Call>
        </Parts><Wires><Wire UId="3"><IdentCon UId="1"/><NameCon UId="2" Name="Enable"/></Wire></Wires></FlgNet></NetworkSource>
        """));
    request = f.Request(); request.Remove("signals"); request["hypotheses"]![0]!.AsObject().Remove("prediction");
    request["autoSignals"] = JsonNode.Parse("""{"seeds":[{"scope":"LocalVariable","components":["Enable"],"reason":"Trace graphical actual binding"}]}""");
    signals = f.Planner.Plan(request)["plan"]!["signals"]!.AsArray();
    Equal(true, signals.Any(s => s!["memberPath"]!.ToJsonString() == "[\"Enable2\"]"));
}
static void CompareAndReuse()
{
    using var f = new Fixture(); var original = f.Plan(); string trace = Id(f.Prepare(original)); var before = f.Capture(trace); var after = f.Capture(trace);
    foreach (var sample in after["samples"]!.AsArray()) { sample!["values"]!["final"] = true; sample["values"]!["override"] = false; }
    string beforeId = Id(f.Import(trace, before)), afterId = Id(f.Import(trace, after));
    var options = JsonNode.Parse("""{"reason":"Compare the same synthetic event window without claiming a fix.","beforeAnchorSeconds":0.02,"afterAnchorSeconds":0.02,"windowStartSeconds":-0.02,"windowEndSeconds":0.03}""")!.AsObject();
    var report = new TraceComparison(f.Root).Compare(beforeId, afterId, options)["comparison"]!;
    var final = report["signals"]!.AsArray().Single(s => s!["beforeSignalId"]!.GetValue<string>() == "final")!;
    Equal(3, final["before"]!["falseSamples"]!.GetValue<int>()); Equal(0, final["after"]!["falseSamples"]!.GetValue<int>()); Equal(false, report["bugFixProven"]!.GetValue<bool>());
    var mappings = new JsonArray(); foreach (var s in original["plan"]!["signals"]!.AsArray())
    {
        var path = s!["memberPath"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(); if (path[0] == "Conv2") path[0] = "Conv1";
        mappings.Add(new JsonObject { ["id"] = s["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(), ["memberPath"] = new JsonArray(path.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["reason"] = "Explicit fixture instance substitution; external DB roles remain explicitly selected." });
    }
    var request = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"), ["instance"] = new JsonObject { ["dbPath"] = Fixture.Block("DB_Line"), ["memberPath"] = new JsonArray("Conv1") }, ["reason"] = "Independent conveyor comparison", ["signalMappings"] = mappings };
    var next = new DiagnosticReplanner(f.Root).Replan(Id(original), request);
    Equal("Conv1", next["plan"]!["selectedInstance"]!["memberPath"]![0]!.GetValue<string>()); Equal(Id(original), next["plan"]!["lineage"]!["previousPlanId"]!.GetValue<string>());
    request["signalMappings"]!.AsArray().RemoveAt(0); Error("InvalidRequest", () => new DiagnosticReplanner(f.Root).Replan(Id(original), request));
}
static void ComparisonProgramIdentity()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var before = f.Capture(trace); var after = f.Capture(trace);
    after["programIdentity"] = "another declared program, same saved review";
    string beforeId = Id(f.Import(trace, before)), afterId = Id(f.Import(trace, after));
    var options = JsonNode.Parse("""{"reason":"Explicitly compare separate declared programs","beforeAnchorSeconds":0,"afterAnchorSeconds":0,"windowStartSeconds":0,"windowEndSeconds":1}""")!.AsObject();
    var compare = new TraceComparison(f.Root);
    Error("ProgramRevisionMismatch", () => compare.Compare(beforeId, afterId, options));
    options["allowChangedSource"] = false;
    Error("ProgramRevisionMismatch", () => compare.Compare(beforeId, afterId, options));
    options["allowChangedSource"] = true;
    var report = compare.Compare(beforeId, afterId, options)["comparison"]!;
    Equal(false, report["sourceChanged"]!.GetValue<bool>());
    Equal(true, report["programIdentityChanged"]!.GetValue<bool>());
    Equal(before["programIdentity"]!.GetValue<string>(), report["beforeAcquisition"]!["programIdentity"]!.GetValue<string>());
    Equal(after["programIdentity"]!.GetValue<string>(), report["afterAcquisition"]!["programIdentity"]!.GetValue<string>());
    Equal(false, report["bugFixProven"]!.GetValue<bool>());
}
static void ComparisonAcquisitionContext()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var before = f.Capture(trace); var after = f.Capture(trace);
    before["clock"] = "PLC-A trigger-relative clock";
    after["clock"] = "Host-B unsynchronized clock";
    after["mode"] = "polling"; after["timeUnit"] = "us";
    after["quality"]!["atomicAcrossSignals"] = false;
    after["quality"]!["scanSynchronized"] = false;
    after["quality"]!["expectedInterval"] = 10000;
    foreach (var sample in after["samples"]!.AsArray()) sample!["time"] = sample["time"]!.GetValue<int>() * 1000;
    string beforeId = Id(f.Import(trace, before)), afterId = Id(f.Import(trace, after));
    var options = JsonNode.Parse("""{"reason":"Caller-selected anchors are not clock synchronization","beforeAnchorSeconds":0.02,"afterAnchorSeconds":0.02,"windowStartSeconds":-0.02,"windowEndSeconds":0.03}""")!.AsObject();
    var receipt = new TraceComparison(f.Root).Compare(beforeId, afterId, options);
    var report = receipt["comparison"]!;
    foreach (var pair in new[] { ("beforeAcquisition", before), ("afterAcquisition", after) })
    {
        foreach (string key in new[] { "mode", "clock", "timeUnit", "programIdentity" })
            Equal(pair.Item2[key]!.GetValue<string>(), report[pair.Item1]![key]!.GetValue<string>());
        Equal(false, report[pair.Item1]!["identityVerified"]!.GetValue<bool>());
    }
    Equal(false, report["programIdentityChanged"]!.GetValue<bool>());
    Equal(false, report["acquisitionModesEqual"]!.GetValue<bool>());
    Equal(false, report["clockSynchronizationVerified"]!.GetValue<bool>());
    Equal(report["signals"]![0]!["before"]!.ToJsonString(), report["signals"]![0]!["after"]!.ToJsonString());
    Equal(false, report["afterQuality"]!["atomicAcrossSignals"]!.GetValue<bool>());
    var reread = new DiagnosticStore(f.Root).Read(Id(receipt), "trace-comparison.v1");
    Equal(report.ToJsonString(), reread.ToJsonString());
}
static string Id(JsonNode receipt) => receipt["id"]!.GetValue<string>();
static void Readback()
{
    using var f = new Fixture(); var trace = f.Prepare(f.Plan()); var spec = trace["specification"]!;
    var observed = f.Readback(trace);
    JsonObject Check(JsonObject value) => new TraceReadback(f.Root).Check(Id(trace), f.Artifact(Encoding.UTF8.GetBytes(value.ToJsonString()), ".json"));
    var good = Check(observed)["readbackCheck"]!;
    Equal(true, good["matchesSuppliedReadback"]!.GetValue<bool>());
    Equal(false, good["nativeProbePerformed"]!.GetValue<bool>());
    Equal(false, good["nativeQualification"]!.GetValue<bool>());
    observed["target"]!["memberPath"] = new JsonArray("Conv1");
    observed["settings"]!["sampleEvery"] = 2;
    observed["signals"]![0]!["datatype"] = "Int";
    observed["signals"]!.AsArray().RemoveAt(1);
    observed["limits"]!["freeSlotsBefore"] = 0;
    observed["programMatchVerified"] = false;
    var bad = Check(observed)["readbackCheck"]!;
    Equal(false, bad["matchesSuppliedReadback"]!.GetValue<bool>());
    Equal(6, bad["differences"]!.AsArray().Count);
    observed["reviewId"] = new string('0', 64); Error("TracePlanStale", () => Check(observed));
    observed = f.Readback(trace); observed["evidenceArtifacts"]![0]!["sha256"] = new string('0', 64);
    Error("ArtifactChanged", () => Check(observed));
}
static void NumericPrecision()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    foreach (string value in new[] { "1e-100", "0.1234567890123456789012345678901", "1e40" })
    {
        var m = f.Capture(trace); m["samples"]![0]!["values"]!["elapsed"] = JsonNode.Parse(value);
        Error("MeasurementFormatUnsupported", () => f.Import(trace, m));
    }
    var exact = f.Capture(trace); exact["samples"]![0]!["values"]!["elapsed"] = JsonNode.Parse("1.000e3");
    _ = f.Import(trace, exact);
}
static void ComparisonOptions()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan())); string id = Id(f.Import(trace, f.Capture(trace)));
    var compare = new TraceComparison(f.Root);
    var options = JsonNode.Parse("""{"reason":"Explicit exact signal selection","beforeAnchorSeconds":0,"afterAnchorSeconds":0,"windowStartSeconds":0,"windowEndSeconds":1}""")!.AsObject();
    foreach (string invalid in new[] { "null", "{}", "true", "0", "\"final\"" })
    {
        var bad = options.DeepClone().AsObject(); bad["signalMap"] = JsonNode.Parse(invalid);
        Error("InvalidRequest", () => compare.Compare(id, id, bad));
    }
    foreach (string invalid in new[] { "null", "0", "\"true\"", "{}" })
    {
        var bad = options.DeepClone().AsObject(); bad["allowChangedSource"] = JsonNode.Parse(invalid);
        Error("InvalidRequest", () => compare.Compare(id, id, bad));
    }
    foreach (string invalid in new[] { "[]", "[null]", "[0]", "[{}]", "[{\"before\":1,\"after\":\"final\"}]",
        "[{\"before\":\"final\",\"after\":\"final\",\"ignored\":true}]" })
    {
        var bad = options.DeepClone().AsObject(); bad["signalMap"] = JsonNode.Parse(invalid);
        Error("InvalidRequest", () => compare.Compare(id, id, bad));
    }
    var typo = options.DeepClone().AsObject(); typo["signalMaps"] = new JsonArray();
    Error("InvalidRequest", () => compare.Compare(id, id, typo));
    options["signalMap"] = JsonNode.Parse("""[{"before":"final","after":"run"}]""");
    options["allowChangedSource"] = false;
    var report = compare.Compare(id, id, options)["comparison"]!;
    Equal(1, report["signals"]!.AsArray().Count); Equal(false, report["signals"]![0]!["sameStorage"]!.GetValue<bool>());
    Equal("run", report["signals"]![0]!["afterSignalId"]!.GetValue<string>());
}
static void ExactIntervalGaps()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    foreach (var (interval, last, gaps) in new[] {
        (0.0000000000000000000000000001m, 0.0000000000000000000000000001m, 0),
        (0.0000000000000000000000000001m, 0.0000000000000000000000000002m, 1),
        (0.0000000000000000000000000002m, 0.0000000000000000000000000003m, 0),
        (0.0000000000000000000000000002m, 0.0000000000000000000000000004m, 1) })
    {
        var m = f.Capture(trace); m["timeUnit"] = "s"; m["quality"]!["expectedInterval"] = interval;
        var samples = m["samples"]!.AsArray(); while (samples.Count > 2) samples.RemoveAt(samples.Count - 1);
        samples[0]!["time"] = 0; samples[1]!["time"] = last;
        var receipt = f.Import(trace, m);
        Equal(gaps, receipt["qualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
        Equal(last, new DiagnosticStore(f.Root).Read(Id(receipt))["measurement"]!["samples"]![1]!["time"]!.GetValue<decimal>());
    }
    var crossing = f.Capture(trace); crossing["timeUnit"] = "s"; crossing["quality"]!["expectedInterval"] = 6;
    var pair = crossing["samples"]!.AsArray(); while (pair.Count > 2) pair.RemoveAt(pair.Count - 1);
    pair[0]!["time"] = -9; pair[1]!["time"] = 0.0000000000000000000000000001m;
    Equal(1, f.Import(trace, crossing)["qualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
    pair[1]!["time"] = 0;
    Equal(0, f.Import(trace, crossing)["qualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
}
static void RetainedTiming()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var m = f.Capture(trace); m["timeUnit"] = "s"; m["quality"]!["expectedInterval"] = 0.0000000000000000000000000001m;
    var samples = m["samples"]!.AsArray(); var eventSample = samples[2]!.DeepClone(); samples.Clear();
    eventSample["time"] = 0; samples.Add(eventSample);
    eventSample = eventSample.DeepClone(); eventSample["time"] = 0.0000000000000000000000000002m; samples.Add(eventSample);
    var store = new DiagnosticStore(f.Root); var legacy = store.Read(Id(f.Import(trace, m)));
    legacy["qualityAssessment"]!["detectedIntervalGaps"] = 0;
    string id = Id(store.Save(legacy));
    var report = f.Measurements.Analyze(id)["report"]!;
    Equal(1, report["qualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
    Equal("unresolved", report["hypotheses"]![0]!["verdict"]!.GetValue<string>());
    Equal(2, report["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>());
    var options = JsonNode.Parse("""{"reason":"Reassess retained evidence","beforeAnchorSeconds":0,"afterAnchorSeconds":0,"windowStartSeconds":0,"windowEndSeconds":1}""")!.AsObject();
    var compared = new TraceComparison(f.Root).Compare(id, id, options)["comparison"]!;
    Equal(1, compared["beforeAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
    Equal(1, compared["afterAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
    Equal(0, store.Read(id)["qualityAssessment"]!["detectedIntervalGaps"]!.GetValue<int>());
}
static void ExactEventAlignment()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var m = f.Capture(trace); m["timeUnit"] = "s"; m["quality"]!["expectedInterval"] = 1;
    for (int i = 0; i < 6; i++) m["samples"]![i]!["time"] = 999999999999999995m + i;
    string id = Id(f.Import(trace, m)); var compare = new TraceComparison(f.Root);
    var options = JsonNode.Parse("""{"reason":"Exact supplied time arithmetic","beforeAnchorSeconds":1000000000000000000,"afterAnchorSeconds":1000000000000000000,"windowStartSeconds":-5,"windowEndSeconds":0}""")!.AsObject();
    var good = compare.Compare(id, id, options)["comparison"]!;
    Equal(6, good["signals"]![0]!["before"]!["samplesInWindow"]!.GetValue<int>());
    foreach (string side in new[] { "beforeAnchorSeconds", "afterAnchorSeconds" })
    {
        var rounded = options.DeepClone().AsObject(); rounded[side] = 0.0000000000000000000000000001m;
        Error("MeasurementFormatUnsupported", () => compare.Compare(id, id, rounded));
    }
    m = f.Capture(trace); m["timeUnit"] = "s"; m["quality"]!["expectedInterval"] = 0.0000000000000000000000000001m;
    for (int i = 0; i < 6; i++) m["samples"]![i]!["time"] = (i + 1) * 0.0000000000000000000000000001m;
    id = Id(f.Import(trace, m));
    options["windowStartSeconds"] = -1000000000000000000m; options["windowEndSeconds"] = 0;
    Error("MeasurementFormatUnsupported", () => compare.Compare(id, id, options));
}
static void NanosecondsAndAxRoute()
{
    using var f = new Fixture(); string trace = Id(f.Prepare(f.Plan()));
    var before = f.Capture(trace); var after = f.Capture(trace); after["timeUnit"] = "ns";
    after["quality"]!["expectedInterval"] = 10000000;
    foreach (var sample in after["samples"]!.AsArray()) sample!["time"] = sample["time"]!.GetValue<int>() * 1000000L + 7;
    string beforeId = Id(f.Import(trace, before)), afterId = Id(f.Import(trace, after));
    Equal(7L, new DiagnosticStore(f.Root).Read(afterId)["measurement"]!["samples"]![0]!["time"]!.GetValue<long>());
    Equal(3, f.Measurements.Analyze(afterId)["report"]!["hypotheses"]![0]!["matchedSamples"]!.GetValue<int>());
    var options = JsonNode.Parse("""{"reason":"Preserve exact seven-nanosecond clock offset","beforeAnchorSeconds":0.02,"afterAnchorSeconds":0.020000007,"windowStartSeconds":-0.02,"windowEndSeconds":0.03}""")!.AsObject();
    var compared = new TraceComparison(f.Root).Compare(beforeId, afterId, options)["comparison"]!;
    foreach (var signal in compared["signals"]!.AsArray()) Equal(true, JsonNode.DeepEquals(signal!["before"], signal["after"]));
    var ax = TracePlanning.Capabilities()["routes"]!.AsArray().Single(r => r!["backend"]!.GetValue<string>() == "simaticAxCli")!;
    Equal("documentedExternalCli", ax["documentedRoute"]!.GetValue<string>()); Equal("unavailable", ax["implementation"]!.GetValue<string>());
    Equal("unqualified", ax["qualification"]!.GetValue<string>());
    var unsupported = f.Settings(); unsupported["backend"] = "simaticAxCli";
    Error("UnsupportedCapability", () => f.Traces.Prepare(Id(f.Plan()), unsupported));
}
static void UploadedInputs()
{
    using var f = new Fixture(); var uploads = new ArtifactUploadStore(f.Root); var store = new DiagnosticStore(f.Root);
    string Upload(byte[] bytes)
    {
        var status = uploads.Begin("capture.csv", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        for (int offset = 0; offset < bytes.Length; offset += status.MaxChunkBytes)
            uploads.Write(status.UploadId, offset, Convert.ToBase64String(bytes.AsSpan(offset, Math.Min(status.MaxChunkBytes, bytes.Length - offset))));
        return uploads.Commit(status.UploadId).ArtifactRef!;
    }
    byte[] raw = Encoding.UTF8.GetBytes("time;Enable\r\n-10;1\r\n0;0\r\n"); string reference = Upload(raw);
    var retained = store.RetainUpload(reference); string path = retained["path"]!.GetValue<string>();
    Equal(true, raw.SequenceEqual(File.ReadAllBytes(path)));
    Equal(true, JsonNode.DeepEquals(retained, store.RetainUpload(reference)));
    uploads.Release(reference[7..]);
    Equal(true, raw.SequenceEqual(ProjectReviewStore.ReadArtifact(f.Root, retained)));
    string again = Upload(raw); File.AppendAllText(path, " ");
    Error("ArtifactChanged", () => store.RetainUpload(again));
    Equal(raw.Length + 1L, new FileInfo(path).Length);
    Error("ArtifactCapacity", () => store.RetainUpload(Upload(new byte[DiagnosticStore.MaxBytes + 1])));
}
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Error(string code, Action action) { try { action(); } catch (BridgeException e) { Equal(code, e.Code); return; } throw new Exception("Expected " + code); }

sealed class Fixture : IDisposable
{
    public const string Plc = "/projects/Fixture/plc/CPU";
    public static string Block(string name) => Plc + "/blocks/" + name;
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tia-diagnostics-" + Guid.NewGuid().ToString("N"));
    public JsonObject Model { get; } = new(); public JsonArray Documents { get; } = new();
    public JsonObject Oracle { get; }
    public string ReviewId { get; private set; } = "";
    public DiagnosticPlanner Planner => new(Root); public TracePlanning Traces => new(Root); public TraceMeasurements Measurements => new(Root);
    public Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TiaMcp.slnx"))) dir = dir.Parent;
        string fixture = Path.Combine(dir?.FullName ?? throw new Exception("Repository fixture root missing."), "fixtures", "trace-diagnostics");
        Oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, "expected-mappings.json")))!.AsObject();
        var nodes = new JsonArray(new JsonObject { ["nodeId"] = "project", ["kind"] = "project", ["objectPath"] = "/projects/Fixture", ["name"] = "Fixture" });
        foreach (string name in new[] { "Main", "FB_Line", "FB_Conveyor", "DB_Line", "DB_IO" })
        {
            nodes.Add(new JsonObject { ["nodeId"] = name, ["kind"] = "block", ["objectPath"] = Block(name), ["name"] = name, ["plcPath"] = Plc, ["scopePath"] = Plc,
                ["metadata"] = new JsonObject { ["namespace"] = "", ["programmingLanguage"] = name.StartsWith("DB_") ? "DB" : "SCL" } });
            string xml = File.ReadAllText(Path.Combine(fixture, name + ".xml"));
            Documents.Add(new JsonObject { ["objectPath"] = Block(name), ["status"] = "exported", ["xml"] = xml,
                ["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))) });
        }
        Model["schemaVersion"] = "project-model.v1"; Model["project"] = new JsonObject { ["projectPath"] = "synthetic-fixture.ap21", ["host"] = "offline-test" };
        Model["nodes"] = nodes; Model["edges"] = new JsonArray(new JsonObject { ["kind"] = "usesType",
            ["source"] = new JsonObject { ["objectPath"] = Block("DB_Line"), ["nodeId"] = "DB_Line" }, ["target"] = new JsonObject { ["objectPath"] = Block("FB_Line"), ["nodeId"] = "FB_Line" },
            ["evidence"] = new JsonObject { ["origin"] = "nativeConfirmed", ["resolution"] = "resolved", ["fixtureNote"] = "Synthetic edge; no native observation." } });
        SaveReview();
    }
    public void AddVirtualGroups()
    {
        foreach (string name in new[] { "Blocks", "Diagnostics" })
            Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "virtual-" + name, ["kind"] = "folder", ["name"] = name, ["origin"] = "virtual" });
        Model["edges"]!.AsArray().Add(new JsonObject { ["kind"] = "contains",
            ["source"] = new JsonObject { ["nodeId"] = "project", ["objectPath"] = "/projects/Fixture" },
            ["target"] = new JsonObject { ["nodeId"] = "virtual-Blocks" },
            ["evidence"] = new JsonObject { ["origin"] = "nativeConfirmed", ["resolution"] = "resolved", ["fixtureNote"] = "Synthetic grouping only." } });
        SaveReview();
    }
    public void SaveReview() => ReviewId = new ProjectReviewStore(Root).Save(Model, Documents, new JsonObject { ["syntheticFixture"] = true })["reviewId"]!.GetValue<string>();
    public void ReplaceXml(string block, Func<string, string> change)
    {
        var row = Documents.Single(d => d!["objectPath"]!.GetValue<string>() == Block(block))!;
        string xml = change(row["xml"]!.GetValue<string>()); row["xml"] = xml;
        row["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))); SaveReview();
    }
    public void AddDependencyEvidence()
    {
        ReplaceXml("FB_Conveyor", xml => xml.Replace("<ObjectList>", "<ObjectList><NetworkSource><StructuredText xmlns='http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4'><Access Scope='LocalVariable'><Symbol><Component Name='RunCmd'/></Symbol></Access><Token Text=':='/><Access Scope='LocalVariable'><Symbol><Component Name='Enable'/></Symbol></Access><Token Text=';'/></StructuredText></NetworkSource>"));
        ReplaceXml("FB_Line", xml => xml.Replace("<Component Name=\"Conv2\"/></Instance></CallInfo>", "<Component Name=\"Conv2\"/></Instance><Parameter Name='Enable'><Token Text=':='/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Enable2'/></Symbol></Access></Parameter><Parameter Name='RunCmd'><Token Text='=&gt;'/><Access Scope='GlobalVariable'><Symbol><Component Name='DB_IO'/><Component Name='Final2'/></Symbol></Access></Parameter></CallInfo>"));
    }
    public JsonObject Request()
    {
        var signals = new JsonArray();
        foreach (var row in Oracle["signals"]!.AsArray()) signals.Add(new JsonObject { ["id"] = row!["id"]!.DeepClone(), ["role"] = row["role"]!.DeepClone(),
            ["reason"] = "Independently authored " + row["role"]!.GetValue<string>() + " role tests the writer prediction.", ["hypothesisIds"] = new JsonArray("writer"),
            ["storagePath"] = Block(row["db"]!.GetValue<string>()), ["memberPath"] = row["memberPath"]!.DeepClone() });
        return new JsonObject { ["reviewId"] = ReviewId, ["report"] = "Conveyor 2 occasionally stops although its enable command remains true.",
            ["searchTerms"] = new JsonArray("Conveyor", "Conv2"), ["context"] = new JsonObject { ["timing"] = "Synthetic 10 ms samples only." },
            ["blockPath"] = Oracle["selectedBlock"]!.DeepClone(), ["instance"] = new JsonObject { ["dbPath"] = Oracle["selectedDb"]!.DeepClone(), ["memberPath"] = Oracle["selectedMemberPath"]!.DeepClone() },
            ["signals"] = signals, ["hypotheses"] = JsonNode.Parse("""[{"id":"writer","statement":"A separate writer changes the final output while the internal command remains true.","prediction":{"when":[{"signalId":"final","op":"eq","value":false}],"expect":[{"signalId":"run","op":"eq","value":true},{"signalId":"override","op":"eq","value":true},{"signalId":"enable","op":"eq","value":true}]}}]""") };
    }
    public JsonObject Plan() => Planner.Plan(Request());
    public JsonObject Settings() => new() { ["backend"] = "manual", ["family"] = "ordinaryPlc", ["reviewId"] = ReviewId,
        ["settings"] = new JsonObject { ["task"] = "Main (requires target verification)", ["sampleEvery"] = 1, ["preSamples"] = 100, ["postSamples"] = 100,
            ["rationale"] = "Capture the command loss with context; sample counts are requested, not device-qualified.", ["trigger"] = new JsonObject { ["signalId"] = "final", ["kind"] = "fallingEdge" } } };
    public JsonObject Prepare(JsonObject plan) => Traces.Prepare(plan["id"]!.GetValue<string>(), Settings());
    public JsonObject Capture(string trace)
    {
        var samples = new JsonArray();
        // Independent authored event, not generated by the analyzer or by the expected result.
        for (int i = 0; i < 6; i++) samples.Add(new JsonObject { ["time"] = i * 10, ["values"] = new JsonObject {
            ["enableSource"] = true, ["enable"] = true, ["run"] = true, ["final"] = i is not (2 or 3 or 4), ["feedback"] = true,
            ["permit"] = true, ["step"] = 20, ["elapsed"] = 1000, ["timerDone"] = true, ["fault"] = false, ["override"] = i is 2 or 3 or 4 } });
        var units = new JsonObject(); foreach (var s in Oracle["signals"]!.AsArray()) units[s!["id"]!.GetValue<string>()] = s["id"]!.GetValue<string>() == "elapsed" ? "ms" : "1";
        return new JsonObject { ["schemaVersion"] = "trace-measurement.v1", ["tracePlanId"] = trace, ["reviewId"] = ReviewId, ["mode"] = "synthetic", ["timeUnit"] = "ms", ["clock"] = "synthetic monotonic",
            ["programIdentity"] = "authored offline fixture; no online program", ["quality"] = new JsonObject { ["atomicAcrossSignals"] = true, ["scanSynchronized"] = false, ["dropoutsKnown"] = true,
                ["reportedDroppedSamples"] = 0, ["programMatchVerified"] = true, ["expectedInterval"] = 10 }, ["units"] = units, ["samples"] = samples };
    }
    public JsonObject Import(string trace, JsonObject m)
    {
        return Measurements.Import(trace, Artifact(Encoding.UTF8.GetBytes(m.ToJsonString()), ".json"));
    }
    public JsonObject Readback(JsonObject trace)
    {
        var spec = trace["specification"]!;
        var signals = spec["signals"]!.DeepClone().AsArray();
        foreach (var signal in signals) signal!["observable"] = true;
        return new JsonObject { ["schemaVersion"] = "trace-readback.v1", ["tracePlanId"] = trace["id"]!.DeepClone(), ["reviewId"] = ReviewId,
            ["project"] = spec["project"]!.DeepClone(), ["target"] = spec["target"]!.DeepClone(), ["signals"] = signals,
            ["settings"] = Settings()["settings"]!.DeepClone(), ["nativeTraceIdentity"] = "synthetic trace fixture only", ["targetIdentity"] = "synthetic CPU",
            ["programIdentity"] = "synthetic fixture", ["observationMethod"] = "Authored offline oracle; no native observation",
            ["programMatchVerified"] = true, ["triggerSupported"] = true, ["samplingTaskVerified"] = true,
            ["evidenceArtifacts"] = new JsonArray(Artifact(Encoding.UTF8.GetBytes("Synthetic readback evidence, NOT native proof"), ".txt")),
            ["limits"] = new JsonObject { ["maxSignals"] = 16, ["maxSamples"] = 1000, ["minSampleEvery"] = 1, ["maxSampleEvery"] = 100,
                ["freeSlotsBefore"] = 1, ["requiredSampleCapacity"] = 201, ["sampleWindowSemantics"] = "Authored fixture counts pre + trigger + post; not a device assertion" } };
    }
    public JsonObject Artifact(byte[] bytes, string suffix) { string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + suffix); File.WriteAllBytes(path, bytes); return new JsonObject { ["path"] = path, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) }; }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
