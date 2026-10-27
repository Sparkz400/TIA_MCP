using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;

if (args is ["--publication-child", var childRoot, var childUpload, var childPhase, var signalPath])
{
    new CompositeEngineeringWorkflowStore(childRoot, null, (phase, directory) => {
        if (phase == childPhase) { File.WriteAllText(signalPath, directory); Thread.Sleep(Timeout.Infinite); }
    }).Review(childUpload);
    return;
}
if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
void Reject(Action run, string label, string? code = "InvalidCompositeEngineeringReview")
{ try { run(); } catch (BridgeException e) when (code is null || e.Code == code) { Check(true, label); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
JsonObject Obj(string text) => JsonNode.Parse(text)!.AsObject();
const string p = "/projects/Fixture", plc = p + "/PLC", hw = plc + "/Hardware", peer = plc + "/Peer", to = plc + "/Axis";
JsonObject Node(string path, string kind, string? parent) => new() { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = "Shared",
    ["parentId"] = parent, ["plcPath"] = plc, ["metadata"] = new JsonObject() };
var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
    ["nodes"] = new JsonArray(Node(p, "project", null), Node(plc, "plc", p), Node(hw, "hardware", plc), Node(hw + "/port", "hardware", hw),
        Node(peer, "hardware", plc), Node(peer + "/port", "hardware", peer), Node(to, "technologyObject", plc)), ["edges"] = new JsonArray() };
var reviews = new ProjectReviewStore(root); string reviewId = (string)reviews.Save(model, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var uploads = new ArtifactUploadStore(root);
string Upload(byte[] bytes, string name)
{
    var u = uploads.Begin(name, bytes.Length, Hash(bytes));
    for (int i = 0; i < bytes.Length; i += u.MaxChunkBytes) uploads.Write(u.UploadId, i, Convert.ToBase64String(bytes, i, Math.Min(u.MaxChunkBytes, bytes.Length - i)));
    string result = uploads.Commit(u.UploadId).ArtifactRef!; return result;
}
string JsonUpload(JsonObject o) => Upload(JsonSerializer.SerializeToUtf8Bytes(o, BridgeProtocol.Json), "fixture.json");
string Aml(string value = "1", bool duplicate = false) => $"""
<CAEXFile SchemaVersion="2.15" FileName="fixture.aml"><InstanceHierarchy Name="Plant"><InternalElement Name="Drive" ID="drive"><Attribute Name="Speed"><Value>{value}</Value></Attribute><ExternalInterface Name="P" ID="port1"/></InternalElement><InternalElement Name="Peer" ID="{(duplicate ? "drive" : "peer")}"><ExternalInterface Name="P" ID="port2"/></InternalElement><InternalLink Name="Link" RefPartnerSideA="drive:P" RefPartnerSideB="peer:P"/></InstanceHierarchy></CAEXFile>
""";
string Inspect(string aml) => (string)JsonSerializer.SerializeToNode(new AutomationMlWorkflowStore(root).Inspect(Upload(Encoding.UTF8.GetBytes(aml), "fixture.aml")), BridgeProtocol.Json)!["recordRef"]!;
string amlBefore = Inspect(Aml()), amlAfter = Inspect(Aml("2"));
JsonObject Endpoint(string owner) => new() { ["ownerPath"] = owner, ["interfacePath"] = owner + "/port", ["reason"] = null };
var connectionState = new JsonObject { ["connectionsComplete"] = false, ["consumersComplete"] = false,
    ["connections"] = new JsonArray(new JsonObject { ["id"] = "c", ["origin"] = "engineering", ["protocol"] = "S7", ["local"] = Endpoint(hw), ["partner"] = Endpoint(peer), ["properties"] = new JsonObject() }), ["consumers"] = new JsonArray() };
var connectionRequest = new JsonObject { ["schemaVersion"] = "connection-review-request.v1", ["reviewId"] = reviewId, ["afterKind"] = "proposal", ["before"] = connectionState, ["after"] = connectionState.DeepClone(),
    ["matches"] = new JsonArray(new JsonObject { ["beforeId"] = "c", ["afterId"] = "c" }) };
string connection = (string)new ConnectionWorkflowStore(root).Review(reviewId, JsonUpload(connectionRequest))["recordId"]!;
var driveState = Obj("""{"identity":{"driveId":"D","family":"supplied","orderNumber":"supplied","firmware":"1","parameterSet":null,"unknownReason":null},"valueDomain":"offline","parametersComplete":false,"parameters":[{"name":"p1","number":1,"arrayIndex":-1,"parentName":null,"unit":"","access":"unknown","value":{"kind":"scalar","type":"opaque","text":"1"},"metadata":{}}]}""");
var driveRequest = new JsonObject { ["schemaVersion"] = "drive-profile-review-request.v1", ["reviewId"] = reviewId, ["hardwarePath"] = hw, ["afterKind"] = "proposal", ["before"] = driveState, ["after"] = driveState.DeepClone() };
driveRequest["after"]!["parameters"]![0]!["value"]!["text"] = "2";
string Drive(JsonObject packet) => (string)new DriveProfileWorkflowStore(root).Review(reviewId, JsonUpload(packet))["recordId"]!;
string drive = Drive(driveRequest);
var techState = new JsonObject { ["identity"] = new JsonObject { ["plcPath"] = plc, ["orderNumber"] = "supplied", ["firmware"] = "1", ["technologyType"] = "TO_PositioningAxis", ["technologyVersion"] = "10.0", ["options"] = new JsonArray(), ["unknownReason"] = null },
    ["valueDomain"] = "offline", ["parametersComplete"] = false, ["relationshipsComplete"] = false, ["parameters"] = new JsonArray(),
    ["relationships"] = new JsonArray(new JsonObject { ["id"] = "actor", ["kind"] = "hardware", ["sourcePath"] = to, ["targetPath"] = hw, ["targetMember"] = null, ["state"] = "declared", ["reason"] = null, ["metadata"] = new JsonObject() },
        new JsonObject { ["id"] = "off", ["kind"] = "hardware", ["sourcePath"] = to, ["targetPath"] = null, ["targetMember"] = null, ["state"] = "disconnected", ["reason"] = null, ["metadata"] = new JsonObject() }) };
var techRequest = new JsonObject { ["schemaVersion"] = "technology-profile-review-request.v1", ["reviewId"] = reviewId, ["technologyPath"] = to, ["afterKind"] = "proposal", ["before"] = techState, ["after"] = techState.DeepClone() };
string technology = (string)new TechnologyProfileWorkflowStore(root).Review(reviewId, JsonUpload(techRequest))["recordId"]!;
const string amlPath = "/CAEXFile[1]/InstanceHierarchy[1]/InternalElement[1]";
JsonObject A(string phase, string kind = "amlObject", string? path = null) => new() { ["kind"] = kind, ["phase"] = phase, ["recordRef"] = phase == "before" ? amlBefore : amlAfter, ["path"] = path ?? amlPath };
JsonObject M(string phase, string path = hw) => new() { ["kind"] = "model", ["phase"] = phase, ["reviewId"] = reviewId, ["nodeId"] = path, ["objectPath"] = path };
JsonObject D(string phase) => new() { ["kind"] = "driveParameter", ["phase"] = phase, ["recordRef"] = drive, ["name"] = "p1", ["number"] = 1, ["arrayIndex"] = -1 };
JsonObject T(string phase, string id = "actor") => new() { ["kind"] = "technologyRelationship", ["phase"] = phase, ["recordRef"] = technology, ["id"] = id };
JsonObject C(string phase) => new() { ["kind"] = "connectionEndpoint", ["phase"] = phase, ["recordRef"] = connection, ["id"] = "c", ["endpoint"] = "local" };
JsonObject Mapping(string id, string phase, string purpose, JsonObject source, JsonObject target) => new() { ["id"] = id, ["phase"] = phase, ["purpose"] = purpose, ["reason"] = "Explicit fixture association", ["source"] = source, ["target"] = target };
JsonObject Context(string aml) => new() { ["reviewId"] = reviewId, ["automationMlRef"] = aml, ["connections"] = new JsonArray(connection), ["drives"] = new JsonArray(drive), ["technology"] = new JsonArray(technology) };
var request = new JsonObject { ["schemaVersion"] = "composite-engineering-request.v1", ["before"] = Context(amlBefore), ["after"] = Context(amlAfter), ["mappings"] = new JsonArray(), ["correspondences"] = new JsonArray(), ["allowDifferentProjectCopy"] = false };
foreach (string phase in new[] { "before", "after" })
{
    request["mappings"]!.AsArray().Add(Mapping(phase + "-hardware", phase, "hardware", A(phase), M(phase)));
    request["mappings"]!.AsArray().Add(Mapping(phase + "-parameter", phase, "parameter", A(phase, "amlAttribute", amlPath + "/Attribute[1]"), D(phase)));
    request["mappings"]!.AsArray().Add(Mapping(phase + "-dependent", phase, "dependent", M(phase), T(phase)));
    request["mappings"]!.AsArray().Add(Mapping(phase + "-communication", phase, "communication", A(phase, "amlLink", "/CAEXFile[1]/InstanceHierarchy[1]/InternalLink[1]"), C(phase)));
}
request["correspondences"]!.AsArray().Add(new JsonObject { ["id"] = "parameter-continuity", ["kind"] = "continuity", ["reason"] = "Supplied claim", ["before"] = D("before"), ["after"] = D("after") });
JsonObject Mutate(Action<JsonObject> change) { var r = request.DeepClone().AsObject(); change(r); return r; }
JsonObject Build(JsonObject? r = null) => CompositeEngineeringReview.Build(root, JsonSerializer.SerializeToUtf8Bytes(r ?? request, BridgeProtocol.Json));
string State(JsonObject r, int index = 0) => (string)r["mappings"]![index]!["state"]!;
var result = Build();
Check((int)result["contexts"]!["before"]!["components"]!.AsArray().Single(c => (string?)c!["kind"] == "drive")!["summary"]!["unknownAccessChangeCount"]! == 1,
    "Composite context retains source proposal conflicts, coverage and limits");
Check(result["mappings"]!.AsArray().All(m => (string?)m!["state"] == "declaredAssociation"), "All four supported declared associations resolve independently in both phases");
Check(result["selections"]!.AsArray().Any(s => (string?)s!["selector"]?["kind"] == "driveParameter" && (string?)s!["profile"]?["identity"]?["driveId"] == "D"), "Exact drive profile remains attached to selected parameter");
Check(result["impacts"]!.AsArray().Any(i => i!["destinationKeys"]!.AsArray().Count >= 3), "Changed AML reaches mapped hardware, parameter and explicitly dependent TO");
Check((string)result["correspondences"]![0]!["state"]! == "declaredCorrespondence" && !(bool)result["correspondences"]![0]!["identityEqualityVerified"]!, "Continuity claim never proves identity equality");
foreach (string flag in new[] { "nativeQualified", "nativeExecutable", "sourceAuthenticityVerified", "liveFreshnessVerified", "wholeImpactAnalysisComplete" }) Check(!(bool)result[flag]!, "Supplied mappings cannot promote " + flag);
Check(State(Build(Mutate(r => r["mappings"]![0]!["target"]!["objectPath"] = peer))) == "unresolvedSelection", "Same-name different object cannot resolve by node ID alone");
Check(State(Build(Mutate(r => r["mappings"]![0]!["source"]!["path"] = amlPath.ToLowerInvariant()))) == "unresolvedSelection", "AML paths remain ordinal");
Check(State(Build(Mutate(r => r["mappings"]![1]!["target"]!["number"] = 2)), 1) == "unresolvedSelection", "Equal parameter name cannot override number mismatch");
Check(State(Build(Mutate(r => r["mappings"]![1]!["target"]!["name"] = "p01")), 1) == "unresolvedSelection", "Numeric-looking parameter names are never normalized");
Check(State(Build(Mutate(r => r["mappings"]![2]!["target"]!["id"] = "off")), 2) == "unresolvedSelection", "Disconnected TO stays unresolved for mapping");
Check(State(Build(Mutate(r => r["mappings"]![2]!["purpose"] = "encoder")), 2) == "unsupportedSemantics", "Encoder label cannot establish profile semantics");
var conflict = Build(Mutate(r => r["mappings"]!.AsArray().Add(Mapping("conflict", "before", "hardware", A("before", path: amlPath.Replace("InternalElement[1]", "InternalElement[2]")), M("before")))));
Check(conflict["coverageGaps"]!.AsArray().Any(g => (string?)g!["kind"] == "conflictingAssignment"), "Many-to-one assignment conflict is retained");
var cycle = Build(Mutate(r => r["mappings"]!.AsArray().Add(Mapping("cycle", "before", "dependent", T("before"), M("before")))));
Check(cycle["impacts"]!.AsArray().Any(i => (bool)i!["declaredCycle"]!), "Declared dependency cycles terminate and remain visible");
var unmapped = Build(Mutate(r => { r["mappings"] = new JsonArray(); r["correspondences"] = new JsonArray(); }));
Check(unmapped["impacts"]!.AsArray().All(i => (string?)i!["state"] == "unmapped"), "No mappings never triggers name-based association");
Reject(() => Build(Mutate(r => r["mappings"]![0]!["target"]!["phase"] = "after")), "Mixed-phase selector refused");
Reject(() => Build(Mutate(r => r["before"]!["connections"]!.AsArray().Add(connection))), "Duplicate component reference refused");
Reject(() => Build(Mutate(r => r["mappings"]!.AsArray().Add(r["mappings"]![0]!.DeepClone()))), "Duplicate mapping ID refused");
Reject(() => Build(Mutate(r => { var m = r["mappings"]![0]!.DeepClone(); m["id"] = "same-edge"; r["mappings"]!.AsArray().Add(m); })), "Duplicate edge cannot hide under another ID");
Reject(() => Build(Mutate(r => r["mappings"]![0]!["target"]!["path"] = hw)), "Unknown selector fields refused");
Reject(() => Build(Mutate(r => r["before"]!["connections"] = new JsonArray())), "Selector cannot borrow a record absent from its phase context");
Reject(() => CompositeEngineeringReview.Build(root, Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":2}")), "Duplicate JSON property refused before evidence loading");
Reject(() => CompositeEngineeringReview.Build(root, new byte[CompositeEngineeringReview.InputLimit + 1]), "Oversized input refused before parsing");
var migrated = driveRequest.DeepClone().AsObject(); foreach (string phase in new[] { "before", "after" }) migrated[phase]!["identity"]!["firmware"] = "2";
string replacement = Drive(migrated);
var migration = Mutate(r => { r["after"]!["drives"] = new JsonArray(replacement); r["mappings"]![5]!["target"]!["recordRef"] = replacement; r["correspondences"]![0]!["after"]!["recordRef"] = replacement; });
Check((string)Build(migration)["correspondences"]![0]!["state"]! == "profileMismatch", "Changed firmware cannot become continuity");
migration["correspondences"]![0]!["kind"] = "replacement";
Check((string)Build(migration)["correspondences"]![0]!["state"]! == "declaredCorrespondence", "Explicit replacement preserves independent profiles without certifying migration");
var typedDrive = driveRequest.DeepClone().AsObject(); typedDrive["schemaVersion"] = "drive-profile-review-request.v2";
foreach (string phase in new[] { "before", "after" })
{
    typedDrive[phase]!["telegramsComplete"] = false;
    typedDrive[phase]!["telegrams"] = new JsonArray(Obj("""{"id":"main","type":"MainTelegram","number":81,"inputBytes":8,"outputBytes":4,"addressesComplete":false,"addresses":[],"unknownReason":"Addresses not captured"}"""));
}
string typedDriveRef = Drive(typedDrive);
var typedContext = Mutate(r => { r["after"]!["drives"] = new JsonArray(typedDriveRef); r["mappings"]![5]!["target"]!["recordRef"] = typedDriveRef; r["correspondences"]![0]!["after"]!["recordRef"] = typedDriveRef; });
foreach (string version in new[] { "v1", "v2" })
{
    typedContext["schemaVersion"] = "composite-engineering-request." + version; var typedResult = Build(typedContext);
    Check((int)typedResult["contexts"]!["after"]!["components"]!.AsArray().Single(c => (string?)c!["kind"] == "drive")!["summary"]!["afterTelegramCount"]! == 1 && State(typedResult,5) == "declaredAssociation",
        "Composite " + version + " verified reader preserves typed drive summary and exact parameter selection");
}
typedContext["mappings"]![5]!["purpose"] = "telegram";
Check(State(Build(typedContext),5) == "unsupportedSemantics", "Typed drive observations cannot invent a missing composite telegram selector or compatibility rule");
var encoderProfile = techRequest.DeepClone().AsObject(); encoderProfile["schemaVersion"] = "technology-profile-review-request.v4";
encoderProfile["sources"] = new JsonArray(); encoderProfile["consumers"] = Obj("""{"before":[],"after":[]}""");
encoderProfile["impactLimits"] = Obj("""{"impactDepth":1,"maxImpactPaths":10,"maxImpactEdgeVisits":100}"""); encoderProfile["encoders"] = new JsonObject();
foreach (string phase in new[] { "before", "after" }) encoderProfile["encoders"]![phase] = new JsonObject { ["complete"] = false, ["observations"] = new JsonArray(new JsonObject {
    ["id"] = "sensor", ["provider"] = "supplied", ["interfaceName"] = "SensorInterface", ["state"] = "connected", ["route"] = "startdriveTelegram", ["sensorIndex"] = 1, ["connectOption"] = "Default", ["unknownReason"] = null,
    ["target"] = new JsonObject { ["recordId"] = typedDriveRef, ["phase"] = phase, ["telegramId"] = "main" } }) };
string encoderRef = (string)new TechnologyProfileWorkflowStore(root).Review(reviewId, JsonUpload(encoderProfile))["recordId"]!;
var encoderContext = Mutate(r => { r["after"]!["technology"] = new JsonArray(encoderRef); r["mappings"]![6]!["target"]!["recordRef"] = encoderRef; });
foreach (string version in new[] { "v1", "v2" })
{
    encoderContext["schemaVersion"] = "composite-engineering-request." + version; var encoderResult = Build(encoderContext);
    Check((int)encoderResult["contexts"]!["after"]!["components"]!.AsArray().Single(c => (string?)c!["kind"] == "technology")!["summary"]!["afterEncoderCount"]! == 1 && State(encoderResult, 6) == "declaredAssociation", "Composite " + version + " retains v4 encoder summary and existing relationship selectors");
}
string encoderDrivePath = Path.Combine(root, (string)new DriveProfileWorkflowStore(root).Page(typedDriveRef)["input"]!["path"]!); byte[] encoderDriveOriginal = File.ReadAllBytes(encoderDrivePath);
try { File.WriteAllText(encoderDrivePath, "damaged"); Reject(() => Build(encoderContext), "Composite reopen revalidates indirect encoder drive dependency", "ArtifactChanged"); } finally { File.WriteAllBytes(encoderDrivePath, encoderDriveOriginal); }
encoderContext["mappings"]![6]!["purpose"] = "encoder";
Check(State(Build(encoderContext), 6) == "unsupportedSemantics", "Typed encoder rows do not invent unsupported composite encoder selector semantics");
string duplicateAml = Inspect(Aml(duplicate: true));
foreach (int element in new[] { 1, 2 })
{
    var ambiguous = Mutate(r => { r["before"]!["automationMlRef"] = duplicateAml; r["mappings"] = new JsonArray(Mapping("duplicate", "before", "hardware", A("before", path: amlPath.Replace("InternalElement[1]", $"InternalElement[{element}]")), M("before"))); r["mappings"]![0]!["source"]!["recordRef"] = duplicateAml; r["correspondences"] = new JsonArray(); });
    Check((string)Build(ambiguous)["mappings"]![0]!["sourceState"]! == "ambiguous", "Every duplicate ID path remains ambiguous: " + element);
}
string inputPath = Path.Combine(root, "drive-profile-reviews", (string)new DriveProfileWorkflowStore(root).Page(drive)["input"]!["sha256"]! + ".input");
byte[] original = File.ReadAllBytes(inputPath); File.AppendAllText(inputPath, " ");
Reject(() => Build(), "Changed retained component input refuses before composite evaluation", "ArtifactChanged"); File.WriteAllBytes(inputPath, original);
Check(JsonNode.DeepEquals(result, Build()), "Reopening verified stores reproduces the exact result after restored original bytes");
var differentModel = model.DeepClone().AsObject(); differentModel["project"]!["projectPath"] = "other-copy.ap21";
string differentReview = (string)reviews.Save(differentModel, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var crossContext = Mutate(r => r["after"]!["reviewId"] = differentReview);
Reject(() => Build(crossContext), "A component from another review cannot enter the same phase context");
foreach (string domain in new[] { "connections", "drives", "technology" }) crossContext["after"]![domain] = new JsonArray();
crossContext["mappings"] = new JsonArray();
crossContext["correspondences"] = new JsonArray(new JsonObject { ["id"] = "model-copy", ["kind"] = "continuity", ["reason"] = "Supplied copy claim", ["before"] = M("before"), ["after"] = M("after") });
crossContext["correspondences"]![0]!["after"]!["reviewId"] = differentReview;
Reject(() => Build(crossContext), "Cross-copy review needs explicit admission"); crossContext["allowDifferentProjectCopy"] = true;
Check((string)Build(crossContext)["correspondences"]![0]!["state"]! == "crossCopyDeclaration", "Same path in a different project copy stays a cross-copy declaration");
Reject(() => Build(Mutate(r => { for (int i = 0; i < 64; i++) r["before"]!["connections"]!.AsArray().Add(connection); })), "Oversized component collection refused before reopening records");
Reject(() => Build(Mutate(r => { for (int i = 0; i < 4096; i++) r["mappings"]!.AsArray().Add(r["mappings"]![0]!.DeepClone()); })), "Mapping count bound enforced before evaluation");
var chainModel = model.DeepClone().AsObject();
for (int i = 0; i < 140; i++) chainModel["nodes"]!.AsArray().Add(Node(plc + "/Chain/" + i, "hardware", plc));
string chainReview = (string)reviews.Save(chainModel, new(), new() { ["syntheticFixture"] = true })["reviewId"]!;
JsonObject Chain(int i) { var m = M("before", plc + "/Chain/" + i); m["reviewId"] = chainReview; return m; }
var deep = Mutate(r => { foreach (string phase in new[] { "before", "after" }) { r[phase]!["reviewId"] = chainReview; foreach (string domain in new[] { "connections", "drives", "technology" }) r[phase]![domain] = new JsonArray(); } r["correspondences"] = new JsonArray(); r["mappings"] = new JsonArray(Mapping("chain-start", "before", "hardware", A("before"), Chain(0))); });
for (int i = 0; i < 139; i++) deep["mappings"]!.AsArray().Add(Mapping("chain-" + i, "before", "dependent", Chain(i), Chain(i + 1)));
var bounded = Build(deep);
Check(bounded["impacts"]!.AsArray().Any(i => (bool)i!["traversalExhausted"]!), "Long dependency traversal exposes its bound instead of claiming complete closure");
string reportPath = Path.Combine(root, "connection-reviews", connection + ".json");
byte[] originalReport = File.ReadAllBytes(reportPath); File.AppendAllText(reportPath, " ");
Reject(() => Build(), "Changed component report refuses even when original inputs remain intact", "ArtifactChanged"); File.WriteAllBytes(reportPath, originalReport);
string amlPathOnDisk = Path.Combine(root, "automationml-workflow", amlBefore.Split(':')[1], "original.aml");
byte[] originalAml = File.ReadAllBytes(amlPathOnDisk); File.AppendAllText(amlPathOnDisk, " ");
Reject(() => Build(), "Changed original AML bytes refuse composite evidence", "AutomationMlIntegrityFailure"); File.WriteAllBytes(amlPathOnDisk, originalAml);
string reviewPath = Path.Combine(root, "project-reviews", reviewId + ".json");
byte[] originalReview = File.ReadAllBytes(reviewPath); File.AppendAllText(reviewPath, " ");
Reject(() => Build(), "Changed saved model refuses before mappings", "ArtifactChanged"); File.WriteAllBytes(reviewPath, originalReview);
var largeRecords = new List<string>();
for (int recordIndex = 0; recordIndex < 12; recordIndex++)
{
    var large = driveRequest.DeepClone().AsObject();
    foreach (string phase in new[] { "before", "after" })
    {
        large[phase]!["identity"]!["driveId"] = "large-" + recordIndex;
        large[phase]!["parameters"] = new JsonArray();
        for (int i = 0; i < 8; i++)
        {
            var parameter = driveState["parameters"]![0]!.DeepClone(); parameter["name"] = "p" + i; parameter["number"] = i;
            parameter["metadata"]!["opaque"] = new string('x', 192 * 1024); large[phase]!["parameters"]!.AsArray().Add(parameter);
        }
    }
    largeRecords.Add(Drive(large));
}
var largeRequest = Mutate(r => { r["mappings"] = new JsonArray(); r["correspondences"] = new JsonArray(); });
largeRequest["after"]!["drives"] = new JsonArray();
foreach (string phase in new[] { "before" })
{
    largeRequest[phase]!["drives"] = new JsonArray(largeRecords.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray());
    foreach (string record in largeRecords) for (int i = 0; i < 8; i++) foreach (int offset in new[] { 0, 1000 })
    {
        var target = D(phase); target["recordRef"] = record; target["name"] = "p" + i; target["number"] = i + offset;
        largeRequest["mappings"]!.AsArray().Add(Mapping(phase + record + i + "-" + offset, phase, "parameter", A(phase, "amlAttribute", amlPath + "/Attribute[1]"), target));
    }
}
bool reportBound = false;
try { Build(largeRequest); } catch (BridgeException e) when (e.Code == "InvalidCompositeEngineeringReview" && e.Message.Contains("32 MiB", StringComparison.Ordinal)) { reportBound = true; }
Check(reportBound, "Accumulated selected evidence is bounded during report construction");
largeRequest["after"]!["drives"] = largeRequest["before"]!["drives"]!.DeepClone(); largeRequest["mappings"] = new JsonArray();
bool inputBound = false;
try { Build(largeRequest); } catch (BridgeException e) when (e.Code == "InvalidCompositeEngineeringReview" && e.Message.Contains("128 MiB", StringComparison.Ordinal)) { inputBound = true; }
Check(inputBound, "Retained original requests and reports both count against resolved input budget");
File.WriteAllText(Path.Combine(root, "request.json"), request.ToJsonString(BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root, "report.json"), result.ToJsonString(BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
Console.WriteLine($"PASS composite engineering evaluator: {checks.Count} checks; supplied retained evidence only.");
CompositeWorkflowTests.Run(root);
CompositeConsumerTests.Run(root);
CompositeTypedTests.Run(root, request, typedDrive, encoderProfile);
CompositeCaptureTests.Run(root);
