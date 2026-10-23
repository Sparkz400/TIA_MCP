using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.HmiComponents;

if (args.Length == 2 && args[0] == "--integer-conversion")
{
    if (Directory.Exists(args[1])) throw new ArgumentException("Fresh evidence directory required.");
    IntegerTextListConversionTests.Run(Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 2 && args[0] == "--set-evolution")
{
    if (Directory.Exists(args[1])) throw new ArgumentException("Fresh evidence directory required.");
    TextListSetEvolutionTests.Run(Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 2 && args[0] == "--coordinated-generation")
{
    if (Directory.Exists(args[1])) throw new ArgumentException("Fresh evidence directory required.");
    CoordinatedTextListTests.Run(Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 2 && args[0] == "--numeric-generation")
{
    if (Directory.Exists(args[1])) throw new ArgumentException("Fresh evidence directory required.");
    NumericTextListGenerationTests.Run(Path.GetFullPath(args[1]));
    return;
}
if (args.Length == 2 && args[0] == "--text-generation")
{
    if (Directory.Exists(args[1])) throw new ArgumentException("Fresh evidence directory required.");
    TextListGenerationTests.Run(Path.GetFullPath(args[1]));
    return;
}
if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh evidence directory required.");
string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
void Reject(Action action, string label, params string[] codes)
{ try { action(); } catch (BridgeException e) when (codes.Length == 0 || codes.Contains(e.Code)) { Check(true, label + " (" + e.Code + ")"); return; } throw new Exception("Expected refusal: " + label); }
string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
const string project = "/projects/Fixture", plc = project + "/plc", block = plc + "/blocks/Component", type = plc + "/types/Status";
const string hmi = project + "/hmi/Panel", tag = hmi + "/tags/Run", foreign = project + "/hmi/Other";
var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root);
var uploaded = new List<string>(); var fileBytes = new Dictionary<string, string>();
string Upload(byte[] bytes, string name)
{
    var item = uploads.Begin(name, bytes.Length, Hash(bytes));
    for (int offset = 0; offset < bytes.Length; offset += item.MaxChunkBytes) uploads.Write(item.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(item.MaxChunkBytes, bytes.Length - offset)));
    uploaded.Add(item.UploadId); return uploads.Commit(item.UploadId).ArtifactRef!;
}
JsonObject FileInput(string id, string name, byte[] bytes)
{
    fileBytes[id] = Convert.ToBase64String(bytes);
    return new() { ["id"] = id, ["fileName"] = name, ["uploadRef"] = Upload(bytes, name), ["sha256"] = Hash(bytes), ["bytes"] = bytes.Length };
}
string Review(string dataType = "Bool", string? defect = null)
{
    var nodes = new JsonArray();
    void Node(string path, string kind, string? parent, string name, JsonObject? metadata = null) => nodes.Add(new JsonObject {
        ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["parentId"] = parent, ["name"] = name, ["metadata"] = metadata ?? new JsonObject() });
    Node(project, "project", null, "Fixture"); Node(plc, "plc", project, "CPU"); Node(block, "block", plc, "Component"); Node(type, "type", plc, "Status");
    Node(hmi, "hmi", project, "Panel", new() { ["family"] = "classic" }); Node(foreign, "hmi", project, "Other", new() { ["family"] = "classic" });
    Node(tag, "hmiTag", hmi, "Run", new() { ["hmiIdentity"] = new JsonObject { ["family"] = "classic", ["hmiPath"] = hmi },
        ["properties"] = new JsonObject { ["Value"] = "Run", ["Opaque"] = "Ãœber æ—¥æœ¬èªž ðŸ˜€ <script>not executed</script>" } });
    if (defect == "foreign") nodes[^1]!["parentId"] = foreign;
    if (defect == "virtual") nodes[^1]!["origin"] = "virtual";
    if (defect == "identity") nodes[^1]!["metadata"]!["hmiIdentity"]!["family"] = "unified";
    if (defect == "wide") nodes[^1]!["metadata"]!["properties"]!["Opaque"] = new string('x', 130 * 1024);
    if (defect == "protected") nodes[2]!["metadata"]!["isKnowHowProtected"] = true;
    Node(hmi + "/screens/Overview_A", "hmiScreen", hmi, "Overview_A", new() { ["hmiIdentity"] = new JsonObject { ["family"] = "classic", ["hmiPath"] = hmi } });
    string Xml(string kind, string name) => $"<Document><{kind}><AttributeList><Name>{name}</Name><Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Input\"><Member Name=\"Run\" Datatype=\"{dataType}\"/>{(defect == "ambiguous" ? "<Member Name=\"Run\" Datatype=\"Bool\"/>" : "")}<Member Name=\"State\" Datatype=\"Struct\"><Sections><Section Name=\"None\"><Member Name=\"Active\" Datatype=\"Bool\"/></Section></Sections></Member></Section></Sections></Interface></AttributeList></{kind}></Document>";
    var docs = new JsonArray();
    foreach (var (path, kind, name) in new[] { (block, "SW.Blocks.FB", "Component"), (type, "SW.Types.PlcStruct", "Status") })
    {
        string xml = Xml(kind, name);
        docs.Add(new JsonObject { ["objectPath"] = path, ["status"] = defect == "omitted" ? "unavailable" : "exported", ["xml"] = xml, ["utf8Sha256"] = Hash(Encoding.UTF8.GetBytes(xml)) });
    }
    var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" }, ["nodes"] = nodes, ["edges"] = new JsonArray() };
    return (string)reviews.Save(model, docs, new() { ["syntheticFixture"] = true })["reviewId"]!;
}
string beforeId = Review(), afterId = Review(), destinationId = Review();
JsonObject Component(string id) => new() { ["reviewId"] = id, ["objectPaths"] = new JsonArray(block, type) };
JsonObject Member(string path = block, params string[] members) => new() { ["objectPath"] = path, ["memberPath"] = new JsonArray((members.Length == 0 ? new[] { "Run" } : members).Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()) };
var files = new JsonArray();
foreach (string phase in new[] { "baseline", "current", "proposed" })
{
    files.Add(FileInput(phase + "-xml", "screen.xml", Encoding.UTF8.GetBytes(phase == "proposed" ? "proposed opaque screen bytes" : "original opaque screen bytes")));
    files.Add(FileInput(phase + "-script", "screen.js", new byte[] { 0, 255, 13, 10, 42 }));
}
var output = new JsonObject { ["id"] = "run", ["objectPath"] = tag, ["parentPath"] = hmi, ["name"] = "Run", ["kind"] = "hmiTag", ["action"] = "update", ["ownership"] = "generated",
    ["baseline"] = new JsonArray("baseline-xml", "baseline-script"), ["current"] = new JsonArray("current-xml", "current-script"), ["proposed"] = new JsonArray("proposed-xml", "proposed-script"),
    ["baselineComplete"] = true, ["currentComplete"] = true, ["proposedComplete"] = true, ["reason"] = "Explicit supplied generated-output mapping; no native format assumption." };
var binding = new JsonObject { ["id"] = "run-binding", ["outputId"] = "run", ["property"] = "Value", ["beforeMember"] = Member(), ["afterMember"] = Member(), ["declaredHmiType"] = "Bool", ["reason"] = "Exact selected interface member." };
var packet = new JsonObject { ["schemaVersion"] = "hmi-component-review-request.v1", ["operation"] = "update", ["reason"] = "Review supplied component/HMI regeneration without executing content.",
    ["beforeComponent"] = Component(beforeId), ["afterComponent"] = Component(afterId),
    ["componentMatches"] = new JsonArray(new[] { block, type }.Select(p => (JsonNode?)new JsonObject { ["beforeObjectPath"] = p, ["afterObjectPath"] = p, ["reason"] = "Explicit component-version object mapping." }).ToArray()), ["destination"] = new JsonObject { ["reviewId"] = destinationId, ["hmiPath"] = hmi, ["family"] = "classic" },
    ["outputs"] = new JsonArray(output), ["bindings"] = new JsonArray(binding), ["files"] = files };
JsonObject Mutate(Action<JsonObject> action) { var p = packet.DeepClone().AsObject(); action(p); return p; }
JsonObject Build(JsonObject? request = null) => HmiComponentReview.Build(request ?? packet, reviews.Read);
int Conflicts(JsonObject r) => (int)r["summary"]!["conflictCount"]!;
int Gaps(JsonObject r) => (int)r["summary"]!["gapCount"]!;
var result = Build();
Check(Conflicts(result) == 0 && Gaps(result) == 0, "Exact supplied update has no observed contradiction; no qualification implied");
Check((string)result["bindings"]![0]!["afterDeclaration"]!["dataType"]! == "Bool", "Exact FB declaration read with source evidence");
Check((string)result["bindings"]![0]!["afterDeclaration"]!["evidence"]!["utf8Sha256"]! == (string)reviews.Read(afterId)["documents"]![0]!["utf8Sha256"]!, "Declaration bound to original XML bytes");
foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "sourceAuthenticityVerified", "wholeProjectPreservationVerified" }) Check(!(bool)result[flag]!, "False " + flag);
Check(!(bool)result["outputs"]![0]!["preservationVerified"]! && !(bool)result["bindings"]![0]!["runtimeStorageVerified"]!, "No preservation or runtime-storage promotion");
Check((string)Build(Mutate(p => { p["bindings"]![0]!["beforeMember"] = Member(type, "State", "Active"); p["bindings"]![0]!["afterMember"] = Member(type, "State", "Active"); }))["bindings"]![0]!["afterDeclaration"]!["dataType"]! == "Bool", "Nested UDT declaration resolves exact member components");
Check((int)Build(Mutate(p => p["afterComponent"] = Component(Review("Real"))))["summary"]!["changedInterfaceCount"]! == 2, "Both selected interfaces change even though only one is bound");
Check((int)Build(Mutate(p => p["componentMatches"] = new JsonArray()))["summary"]!["unresolvedComponentCount"]! == 4, "Same object paths never imply matching across component versions");
Check(Conflicts(Build(Mutate(p => p["afterComponent"] = Component(Review("Real"))))) == 2, "Changed PLC type and declared HMI mismatch both reported");
Check(Conflicts(Build(Mutate(p => p["bindings"]![0]!["afterMember"] = null))) == 1, "Removed PLC member blocks retained output binding");
Check((string)Build(Mutate(p => p["bindings"]![0]!["beforeMember"] = null))["bindings"]![0]!["change"]! == "added", "Explicitly added member mapping retained");
Check((string)Build(Mutate(p => p["bindings"]![0]!["afterMember"] = Member(type)))["bindings"]![0]!["change"]! == "explicitMemberMapping", "Cross-object association is explicit mapping, never inferred identity");
foreach (string defect in new[] { "protected", "ambiguous", "omitted" }) Check(Gaps(Build(Mutate(p => p["afterComponent"] = Component(Review(defect: defect))))) > 0, "Unavailable declaration stays a gap: " + defect);
foreach (string defect in new[] { "foreign", "virtual", "identity" }) Check(Gaps(Build(Mutate(p => p["destination"]!["reviewId"] = Review(defect: defect)))) > 0, "Unproven HMI identity stays a gap: " + defect);
foreach (string owner in new[] { "manual", "unknown" }) Check(Conflicts(Build(Mutate(p => p["outputs"]![0]!["ownership"] = owner))) == 1, "No silent overwrite for " + owner + " ownership");
Check(Conflicts(Build(Mutate(p => p["outputs"]![0]!["current"] = new JsonArray("proposed-xml", "current-script")))) == 1, "Independent changed current bytes flag possible manual edits");
Check(Gaps(Build(Mutate(p => p["outputs"]![0]!["currentComplete"] = false))) == 1, "Partial current content cannot prove preserved baseline");
Check(Gaps(Build(Mutate(p => p["outputs"]![0]!["proposed"] = new JsonArray()))) == 1, "Empty proposed content not complete despite supplied flag");
Check(Gaps(Build(Mutate(p => p["bindings"]![0]!["declaredHmiType"] = null))) == 1, "Missing HMI type stays unknown");
Check(Gaps(Build(Mutate(p => p["bindings"]![0]!["property"] = "UnobservedPort"))) == 1, "Unobserved destination port stays unknown");
var install = Mutate(p => { p["operation"] = "install"; p["beforeComponent"] = null; p["componentMatches"] = new JsonArray(); p["bindings"]![0]!["beforeMember"] = null; p["outputs"]![0]!["action"] = "create"; p["outputs"]![0]!["objectPath"] = null; p["outputs"]![0]!["name"] = "NewRun"; });
Check(Conflicts(Build(install)) == 0 && Gaps(Build(install)) >= 2, "Initial install still needs native name/identity/port validation");
var collision = install.DeepClone().AsObject(); collision["outputs"]![0]!["name"] = "rUN";
Check(Conflicts(Build(collision)) == 1, "Case-variant captured sibling collision reported");
var plannedCollision = install.DeepClone().AsObject(); var duplicate = plannedCollision["outputs"]![0]!.DeepClone().AsObject(); duplicate["id"] = "second"; duplicate["name"] = "newrun"; plannedCollision["outputs"]!.AsArray().Add(duplicate);
Check(Conflicts(Build(plannedCollision)) == 2, "Both proposed sibling collisions reported");
foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
    ("schema", p => p["schemaVersion"] = "other"), ("unknown field", p => p["nativeQualified"] = true), ("missing field", p => p.Remove("reason")),
    ("operation", p => p["operation"] = "native"), ("missing prior", p => p["beforeComponent"] = null), ("destination family", p => p["destination"]!["family"] = "unified"),
    ("duplicate block", p => p["afterComponent"]!["objectPaths"]!.AsArray().Add(block)), ("component HMI substitution", p => p["afterComponent"]!["objectPaths"] = new JsonArray(tag)),
    ("duplicate component match", p => p["componentMatches"]!.AsArray().Add(p["componentMatches"]![0]!.DeepClone())),
    ("unselected component match", p => p["componentMatches"]![0]!["afterObjectPath"] = plc),
    ("unselected member object", p => p["bindings"]![0]!["afterMember"]!["objectPath"] = plc), ("duplicate binding", p => p["bindings"]!.AsArray().Add(p["bindings"]![0]!.DeepClone())),
    ("unknown output", p => p["bindings"]![0]!["outputId"] = "absent"), ("empty member", p => p["bindings"]![0]!["afterMember"]!["memberPath"] = new JsonArray()),
    ("duplicate target", p => { var d = p["outputs"]![0]!.DeepClone(); d!["id"] = "other"; p["outputs"]!.AsArray().Add(d); }),
    ("missing native target", p => p["outputs"]![0]!["objectPath"] = null), ("unknown file", p => p["outputs"]![0]!["current"] = new JsonArray("missing")),
    ("duplicate filename", p => p["outputs"]![0]!["current"] = new JsonArray("current-xml", "baseline-xml")), ("duplicate file id", p => p["files"]!.AsArray().Add(p["files"]![0]!.DeepClone())),
    ("filename traversal", p => p["files"]![0]!["fileName"] = "../source"), ("source hash", p => p["files"]![0]!["sha256"] = "invalid"),
    ("source bytes", p => p["files"]![0]!["bytes"] = 16777217), ("fake completeness", p => p["outputs"]![0]!["currentComplete"] = "true"),
    ("total file bytes", p => { foreach (var f in p["files"]!.AsArray()) f!["bytes"] = HmiComponentReview.FileLimit; }),
    ("case variant filename", p => { p["files"]![1]!["fileName"] = "SCREEN.XML"; }),
    ("too many outputs", p => { var rows = p["outputs"]!.AsArray(); while (rows.Count <= 256) rows.Add(rows[0]!.DeepClone()); }),
    ("too many bindings", p => { var rows = p["bindings"]!.AsArray(); while (rows.Count <= 1024) rows.Add(rows[0]!.DeepClone()); }),
    ("invalid Unicode", p => p["reason"] = "bad\ud800"), ("control selector", p => p["outputs"]![0]!["name"] = "Run\n") })
    Reject(() => Build(Mutate(edit)), "Refuses " + label);
Reject(() => HmiComponentReview.Parse(Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")), "Duplicate JSON fields refuse");
Reject(() => HmiComponentReview.Parse(new byte[HmiComponentReview.InputLimit + 1]), "Oversized request refuses");
var store = new HmiComponentWorkflowStore(root);
string Input(JsonObject p) => Upload(Encoding.UTF8.GetBytes(p.ToJsonString() + "\n"), "mapping.json");
Reject(() => store.Review(Input(Mutate(p => p["files"]![0]!["sha256"] = new string('0', 64)))), "Mismatched committed file hash refuses", "ArtifactChanged");
Reject(() => store.Review(Input(Mutate(p => p["files"]![0]!["bytes"] = 1))), "Mismatched committed file size refuses");
Check(!Directory.Exists(Path.Combine(root, "hmi-component-reviews")), "Invalid file intake publishes no review artifacts");
var wide = store.Review(Input(Mutate(p => p["destination"]!["reviewId"] = Review(defect: "wide"))));
Check((bool)store.Page((string)wide["recordId"]!, "outputs")["rows"]![0]!["omitted"]!, "Oversized row pages as an explicit download marker");
var wideRecord = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, wide["reportArtifact"]!.AsObject()))!;
Check(((string)wideRecord["report"]!["outputs"]![0]!["capturedObject"]!["metadata"]!["properties"]!["Opaque"]!).Length == 130 * 1024, "Full report retains oversized row exactly");
string input = Input(packet); var receipt = store.Review(input); string recordId = (string)receipt["recordId"]!;
Check(JsonNode.DeepEquals(receipt, store.Page(recordId)), "Persistent receipt reopens identically");
Check((int)store.Page(recordId, "files", 0, 1)["nextOffset"]! == 1, "Independent files page with next offset");
foreach (string section in new[] { "components", "outputs", "bindings", "files" }) Check(store.Page(recordId, section)["rows"]!.AsArray().Count > 0, "Page section: " + section);
var retained = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!.AsObject();
foreach (var file in retained["report"]!["files"]!.AsArray()) Check(ProjectReviewStore.ReadArtifact(root, file!["artifact"]!.AsObject()).SequenceEqual(Convert.FromBase64String(fileBytes[(string)file["id"]!])), "Exact independent opaque file retained: " + (string)file["id"]!);
// v2 context uses independently authored saved graphs, never the supplied binding map.
string GraphReview(string phase, bool wideEdge = false)
{
    var source = reviews.Read(beforeId); var model = source["model"]!.DeepClone().AsObject(); var nodes = model["nodes"]!.AsArray();
    string caller = plc + "/blocks/Caller", peer = plc + "/blocks/Peer", screen = hmi + "/screens/Status", far = plc + "/blocks/Far", virtualPath = hmi + "/virtual";
    foreach (var (path, kind, parent) in new[] { (caller, "block", plc), (peer, "block", plc), (far, "block", plc), (screen, "hmiScreen", hmi), (virtualPath, "hmiTag", hmi) })
        nodes.Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["name"] = path.Split('/')[^1], ["parentId"] = parent,
            ["origin"] = path == virtualPath ? "virtual" : "native", ["metadata"] = new JsonObject() });
    var edges = new JsonArray();
    JsonObject Edge(string from, string to, string kind = "calls", string origin = "nativeConfirmed", bool unresolved = false) => new() {
        ["edgeId"] = $"edge-{edges.Count}", ["kind"] = kind,
        ["source"] = new JsonObject { ["objectPath"] = from, ["nodeId"] = from }, ["target"] = new JsonObject { ["objectPath"] = to, ["nodeId"] = to },
        ["evidence"] = new JsonObject { ["origin"] = origin, ["resolution"] = unresolved ? "unresolved" : "resolved", ["opaque"] = wideEdge ? new string('x', 1024 * 1024) : "synthetic observed context" },
        ["unresolved"] = unresolved ? new JsonObject { ["reason"] = "independent fixture gap" } : null };
    if (phase == "before")
    {
        edges.Add(Edge(caller, block)); edges.Add(Edge(peer, block)); edges.Add(Edge(screen, caller, "hmiBinding")); edges.Add(Edge(screen, peer, "hmiBinding")); edges.Add(Edge(block, screen));
    }
    else if (phase == "after") { edges.Add(Edge(far, caller)); edges.Add(Edge(screen, type, "usesType", "parsedSupported")); }
    else
    {
        edges.Add(Edge(screen, tag, "hmiBinding")); edges.Add(Edge(caller, screen, "reads")); edges.Add(Edge(peer, screen, "dependsOn", "parsedSupported")); edges.Add(Edge(tag, screen, "hmiBinding"));
        edges.Add(Edge(virtualPath, tag, "hmiBinding")); edges.Add(Edge(peer, tag, "screenNavigation")); edges.Add(Edge(peer, tag, "hmiBinding", unresolved: true));
    }
    model["edges"] = edges;
    return (string)reviews.Save(model, source["documents"]!.DeepClone().AsArray(), new() { ["syntheticFixture"] = true })["reviewId"]!;
}
string consumerBefore = GraphReview("before"), consumerAfter = GraphReview("after"), consumerDestination = GraphReview("destination");
var consumerPacket = Mutate(p => { p["schemaVersion"] = "hmi-component-review-request.v2"; p["beforeComponent"]!["reviewId"] = consumerBefore;
    p["afterComponent"]!["reviewId"] = consumerAfter; p["destination"]!["reviewId"] = consumerDestination;
    p["impactLimits"] = new JsonObject { ["impactDepth"] = 3, ["maxImpactPaths"] = 100, ["maxImpactEdgeVisits"] = 5000 }; });
JsonObject ConsumerRecord(Action<JsonObject>? edit = null)
{
    var request = consumerPacket.DeepClone().AsObject(); edit?.Invoke(request); var r = store.Review(Input(request));
    return JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, r["reportArtifact"]!.AsObject()))!.AsObject();
}
var consumerReceipt = store.Review(Input(consumerPacket)); string consumerRecordId = (string)consumerReceipt["recordId"]!;
var consumerRecord = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, consumerReceipt["reportArtifact"]!.AsObject()))!.AsObject();
var context = consumerRecord["report"]!; var consumerPaths = context["consumerPaths"]!.AsArray();
Check((string)context["schemaVersion"]! == "hmi-component-review.v2" && context["consumerCoverage"]!.AsArray().Count == 3, "v2 retains three independently scoped consumer observations");
Check(consumerPaths.Count(p => (string?)p!["scope"] == "beforeComponent" && (string?)p["consumerObjectPath"] == hmi + "/screens/Status" && (string?)p["rootObjectPath"] == block) == 2, "Diamond paths retain separate original edges");
Check(consumerPaths.All(p => p!["objectPaths"]!.AsArray().Select(x => (string)x!).Distinct(StringComparer.Ordinal).Count() == p["objectPaths"]!.AsArray().Count), "Consumer cycles never repeat an object");
Check(!consumerPaths.Any(p => (string?)p!["scope"] == "afterComponent" && (string?)p["rootObjectPath"] == block), "Missing after edge never joins the before snapshot");
Check(consumerPaths.Any(p => (string?)p!["scope"] == "afterComponent" && (string?)p["rootObjectPath"] == type), "Supported parsed edge retains exact after evidence");
Check(consumerPaths.Any(p => (string?)p!["scope"] == "destination" && (int)p["distance"]! == 2), "Unmapped HMI consumers are found through captured relationships");
Check(context["consumerCoverage"]!.AsArray().All(c => !(bool)c!["traversal"]!["complete"]!), "Even untruncated consumer observations remain incomplete");
var exclusions = context["consumerCoverage"]![2]!["traversal"]!["excludedEdges"]!;
foreach (string reason in new[] { "unsupportedRelationshipKind", "unresolvedOrUnsupportedEvidence", "endpointOutsideCapturedNodesOrIdentityMismatch" }) Check((int)exclusions[reason]! == 1, "Consumer exclusion retained: " + reason);
Check(consumerPaths.All(p => (string?)p!["reviewId"] == ((string?)p["scope"] == "beforeComponent" ? consumerBefore : (string?)p["scope"] == "afterComponent" ? consumerAfter : consumerDestination)), "Every path binds its own exact saved review");
Check(ConsumerRecord(p => { p["beforeComponent"]!["reviewId"] = beforeId; p["afterComponent"]!["reviewId"] = afterId; p["destination"]!["reviewId"] = destinationId; })["report"]!["consumerPaths"]!.AsArray().Count == 0, "Supplied bindings never become invented captured edges");
foreach (string field in new[] { "impactDepth", "maxImpactPaths", "maxImpactEdgeVisits" })
{
    var bounded = ConsumerRecord(p => p["impactLimits"]![field] = 1)["report"]!;
    Check(bounded["consumerCoverage"]!.AsArray().Any(c => (bool)c!["traversal"]!["truncated"]!), "Traversal bound is visible: " + field);
    Reject(() => ConsumerRecord(p => p["impactLimits"]![field] = 0), "Zero traversal limit refuses: " + field);
}
Check((bool)ConsumerRecord(p => p["beforeComponent"]!["reviewId"] = GraphReview("before", true))["report"]!["consumerCoverage"]![0]!["traversal"]!["truncated"]!, "Oversized source edges trip retained path-byte bound");
var noOutputs = ConsumerRecord(p => { p["outputs"] = new JsonArray(); p["bindings"] = new JsonArray(); })["report"]!;
Check(noOutputs["consumerCoverage"]![2]!["roots"]!.AsArray().Count == 0 && !noOutputs["consumerPaths"]!.AsArray().Any(p => (string?)p!["scope"] == "destination"), "Unselected output relationships never become proposed bindings");
foreach (string defect in new[] { "foreign", "virtual", "identity" })
    Check(ConsumerRecord(p => p["destination"]!["reviewId"] = Review(defect: defect))["report"]!["consumerCoverage"]![2]!["excludedOutputIds"]!.AsArray().Count == 1, "Unverified destination output excluded from traversal: " + defect);
var newInstall = consumerPacket.DeepClone().AsObject(); newInstall["operation"] = "install"; newInstall["beforeComponent"] = null; newInstall["componentMatches"] = new JsonArray(); newInstall["bindings"]![0]!["beforeMember"] = null;
newInstall["outputs"]![0]!["action"] = "create"; newInstall["outputs"]![0]!["objectPath"] = null; newInstall["outputs"]![0]!["name"] = "NewRun";
var installedContext = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, store.Review(Input(newInstall))["reportArtifact"]!.AsObject()))!["report"]!;
Check(installedContext["consumerCoverage"]!.AsArray().Count == 2 && installedContext["consumerCoverage"]![1]!["excludedOutputIds"]!.AsArray().Count == 1, "Install invents no prior snapshot or new output identity");
foreach (string section in new[] { "consumerPaths", "consumerCoverage" }) Check(store.Page(consumerRecordId, section)["rows"]!.AsArray().Count > 0, "v2 pages " + section);
Reject(() => store.Page(recordId, "consumerPaths"), "Legacy v1 cannot imply consumer coverage");
Reject(() => ConsumerRecord(p => p["impactLimits"]!["maxImpactPaths"] = 201), "Oversized traversal limit refuses");
Reject(() => ConsumerRecord(p => p["impactLimits"]!["extra"] = true), "Unknown traversal field refuses");
Reject(() => ConsumerRecord(p => p.Remove("impactLimits")), "v2 requires explicit traversal limits");
var forgedConsumer = consumerRecord.DeepClone().AsObject(); forgedConsumer["report"]!["consumerPaths"]![0]!["reviewId"] = consumerAfter;
byte[] consumerBytes = JsonSerializer.SerializeToUtf8Bytes(forgedConsumer, BridgeProtocol.Json); string consumerHash = Hash(consumerBytes);
File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", consumerHash + ".json"), consumerBytes);
Reject(() => store.Page(consumerHash), "Rehashed invented cross-snapshot consumer refuses");
var wrongVersion = consumerRecord.DeepClone().AsObject(); wrongVersion["schemaVersion"] = "hmi-component-review-record.v1";
byte[] versionBytes = JsonSerializer.SerializeToUtf8Bytes(wrongVersion, BridgeProtocol.Json); string versionHash = Hash(versionBytes);
File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", versionHash + ".json"), versionBytes);
Reject(() => store.Page(versionHash), "Rehashed record cannot relabel a v2 request as v1");
string graphPath = Path.Combine(root, "project-reviews", consumerDestination + ".json"); byte[] originalGraph = File.ReadAllBytes(graphPath);
try { File.AppendAllText(graphPath, "tampered"); Reject(() => store.Page(consumerRecordId), "Changed retained consumer graph refuses", "ArtifactChanged"); }
finally { File.WriteAllBytes(graphPath, originalGraph); }

var effectPacket = consumerPacket.DeepClone().AsObject(); effectPacket["schemaVersion"] = "hmi-component-review-request.v3";
effectPacket["files"]!.AsArray().Add(FileInput("observed-xml", "screen.xml", Encoding.UTF8.GetBytes("proposed opaque screen bytes")));
effectPacket["files"]!.AsArray().Add(FileInput("observed-script", "screen.js", new byte[] { 0, 255, 13, 10, 42 }));
effectPacket["observations"] = new JsonArray(new JsonObject { ["outputId"] = "run", ["files"] = new JsonArray("observed-script", "observed-xml"), ["complete"] = true });
JsonObject EffectRecord(Action<JsonObject>? edit = null)
{
    var request = effectPacket.DeepClone().AsObject(); edit?.Invoke(request);
    return JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, store.Review(Input(request))["reportArtifact"]!.AsObject()))!.AsObject();
}
JsonNode Effect(Action<JsonObject>? edit = null) => EffectRecord(edit)["report"]!["effectChecks"]![0]!;
var effectReceipt = store.Review(Input(effectPacket)); string effectId = (string)effectReceipt["recordId"]!;
Check((string)Effect()["status"]! == "match", "Independent reordered observed files match exact reviewed proposal");
Check((int)effectReceipt["summary"]!["effectMatchCount"]! == 1, "Effect summary retained in public receipt");
Check((string)Effect(p => p["observations"]![0]!["files"] = new JsonArray("current-xml", "observed-script"))["status"]! == "mismatch", "Unapplied expected change detected");
Check((string)Effect(p => p["observations"]![0]!["files"] = new JsonArray("observed-xml"))["files"]![0]!["status"]! == "missing", "Missing script detected by exact filename");
var missingObservation = Effect(p => p["observations"] = new JsonArray());
Check((string)missingObservation["status"]! == "incomplete" && missingObservation["gaps"]!.AsArray().Count == 2, "Absent observation is incomplete, not successful removal or a proven mismatch");
Check((string)Effect(p => p["observations"]![0]!["complete"] = false)["status"]! == "incomplete", "Matching observed subset never proves complete supplied effect");
Check((string)Effect(p => p["outputs"]![0]!["proposedComplete"] = false)["status"]! == "incomplete", "Incomplete expected set cannot match conclusively");
var unexpected = Effect(p => { p["files"]!.AsArray().Add(FileInput("extra", "unexpected.txt", [1, 2, 3])); p["observations"]![0]!["files"]!.AsArray().Add("extra"); });
Check(unexpected["files"]!.AsArray().Any(f => (string)f!["status"]! == "unexpected"), "Unexpected observed file detected");
var incompleteExpected = Effect(p => { p["outputs"]![0]!["proposedComplete"] = false; p["outputs"]![0]!["proposed"] = new JsonArray("proposed-xml"); });
Check((string)incompleteExpected["status"]! == "incomplete" && incompleteExpected["files"]!.AsArray().Any(f => (string)f!["status"]! == "unresolvedExpected"), "Extra file under incomplete expectation remains unresolved");
var changedAndIncomplete = Effect(p => { p["observations"]![0]!["files"] = new JsonArray("current-xml"); p["observations"]![0]!["complete"] = false; });
Check((string)changedAndIncomplete["status"]! == "mismatch" && changedAndIncomplete["gaps"]!.AsArray().Count == 1, "Definite changed bytes remain a mismatch even when other observation coverage is missing");
void Retain(JsonObject p) { p["outputs"]![0]!["action"] = "retain"; p["outputs"]![0]!["ownership"] = "manual"; p["outputs"]![0]!["proposed"] = new JsonArray(); }
Check((string)Effect(p => { Retain(p); p["observations"]![0]!["files"] = new JsonArray("current-xml", "current-script"); })["status"]! == "match", "Untouched manual output checked against current bytes");
Check((string)Effect(Retain)["status"]! == "mismatch", "Changed manual peer is detected independently of proposed generated output");
Check((string)Effect(p => { Retain(p); p["outputs"]![0]!["currentComplete"] = false; })["status"]! == "mismatch", "Observed manual byte drift stays visible under incomplete expected coverage");
void Remove(JsonObject p) { p["outputs"]![0]!["action"] = "remove"; p["outputs"]![0]!["proposed"] = new JsonArray(); p["bindings"]![0]!["afterMember"] = null; }
Check((string)Effect(p => { Remove(p); p["observations"]![0]!["files"] = new JsonArray(); })["status"]! == "match", "Explicit complete empty supplied observation matches expected removal only within its declared scope");
Check((string)Effect(Remove)["status"]! == "mismatch", "Leftover files after intended removal detected");
Check((string)Effect(p => { Remove(p); p["observations"]![0]!["files"] = new JsonArray(); p["observations"]![0]!["complete"] = false; })["status"]! == "incomplete", "Incomplete empty observation is no proof of absence");
Check((string)Effect(p => { p["operation"] = "install"; p["beforeComponent"] = null; p["componentMatches"] = new JsonArray(); p["bindings"]![0]!["beforeMember"] = null; p["outputs"]![0]!["action"] = "create"; p["outputs"]![0]!["objectPath"] = null; p["outputs"]![0]!["name"] = "New"; })["status"]! == "match", "Initial creation checks supplied expected files without inventing native identity");
var caseChanged = Effect(p => { p["files"]!.AsArray().Add(FileInput("case", "Screen.xml", Encoding.UTF8.GetBytes("proposed opaque screen bytes"))); p["observations"]![0]!["files"] = new JsonArray("case", "observed-script"); });
Check(caseChanged["files"]!.AsArray().Count(f => (string)f!["status"]! is "missing" or "unexpected") == 2, "Case-changed observed name is never silently matched");
foreach (var (label, change) in new (string, Action<JsonObject>)[] {
    ("unknown output", p => p["observations"]![0]!["outputId"] = "unknown"),
    ("duplicate output", p => p["observations"]!.AsArray().Add(p["observations"]![0]!.DeepClone())),
    ("duplicate filename", p => p["observations"]![0]!["files"]!.AsArray().Add("proposed-xml")),
    ("unknown file", p => p["observations"]![0]!["files"] = new JsonArray("unknown")),
    ("non-string file", p => p["observations"]![0]!["files"] = new JsonArray(5)),
    ("false Boolean spelling", p => p["observations"]![0]!["complete"] = "true"),
    ("unrecognized field", p => p["observations"]![0]!["nativeEffectVerified"] = true),
    ("missing observations", p => p.Remove("observations")),
    ("excess observations", p => p["observations"] = new JsonArray(Enumerable.Range(0,257).Select(_ => p["observations"]![0]!.DeepClone()).ToArray())) })
    Reject(() => EffectRecord(change), "Invalid observation refuses: " + label);
foreach (string flag in new[] { "nativeEffectVerified", "contentAssociationVerified", "contentCompletenessVerified", "ownershipVerified" })
    Check(!(bool)Effect()[flag]!, "File equality cannot promote " + flag);
Check(store.Page(effectId, "effectChecks")["rows"]!.AsArray().Count == 1, "Effects page is available only with v3 evidence");
foreach (string oldId in new[] { recordId, consumerRecordId }) Reject(() => store.Page(oldId, "effectChecks"), "Legacy records never acquire invented post-operation evidence");
var forgedEffect = EffectRecord(); forgedEffect["report"]!["effectChecks"]![0]!["status"] = "mismatch";
byte[] forgedEffectBytes = JsonSerializer.SerializeToUtf8Bytes(forgedEffect, BridgeProtocol.Json); string forgedEffectId = Hash(forgedEffectBytes);
File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", forgedEffectId + ".json"), forgedEffectBytes);
Reject(() => store.Page(forgedEffectId), "Rehashed invented effect result rejected", "ArtifactChanged");
// Authored expected output is a literal edit of a retained original, not emitted by the production patcher.
string repository = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(repository, "fixtures/hmi/native-classic-screen.xml"))) repository = Directory.GetParent(repository)?.FullName ?? throw new Exception("Repository fixture unavailable.");
byte[] originalScreen = File.ReadAllBytes(Path.Combine(repository, "fixtures/hmi/native-classic-screen.xml"));
string screenText = new UTF8Encoding(false, true).GetString(originalScreen);
const string oldColor = "<BackColor>204, 204, 204</BackColor>", newColor = "<BackColor>64, 128, 192</BackColor>";
int colorAt = screenText.IndexOf(oldColor, StringComparison.Ordinal); Check(colorAt >= 0, "Retained native screen contains the independently selected scalar");
string expectedScreen = screenText[..colorAt] + newColor + screenText[(colorAt + oldColor.Length)..];
var derivedPacket = effectPacket.DeepClone().AsObject(); derivedPacket["schemaVersion"] = "hmi-component-review-request.v4";
derivedPacket["destination"]!["reviewId"] = destinationId;
derivedPacket["bindings"] = new JsonArray();
var screenOutput = output.DeepClone().AsObject(); screenOutput["id"] = "screen"; screenOutput["objectPath"] = hmi + "/screens/Overview_A";
screenOutput["kind"] = "hmiScreen"; screenOutput["name"] = "Overview_A";
screenOutput["baseline"] = new JsonArray("screen-current", "current-script"); screenOutput["current"] = new JsonArray("screen-current", "current-script");
screenOutput["proposed"] = new JsonArray("screen-proposed", "proposed-script");
derivedPacket["outputs"] = new JsonArray(screenOutput);
derivedPacket["files"]!.AsArray().Add(FileInput("screen-current", "screen.xml", originalScreen));
derivedPacket["files"]!.AsArray().Add(FileInput("screen-proposed", "screen.xml", Encoding.UTF8.GetBytes(expectedScreen)));
derivedPacket["files"]!.AsArray().Add(FileInput("screen-observed", "screen.xml", Encoding.UTF8.GetBytes(expectedScreen)));
derivedPacket["observations"] = new JsonArray(new JsonObject { ["outputId"] = "screen", ["files"] = new JsonArray("screen-observed", "observed-script"), ["complete"] = true });
derivedPacket["expectations"] = new JsonArray(new JsonObject { ["outputId"] = "screen", ["sourceFileId"] = "screen-current", ["profile"] = "classic-screen-backcolor.v1",
    ["ownerId"] = "0", ["expectedValue"] = "204, 204, 204", ["replacementValue"] = "64, 128, 192" });
JsonObject DerivedRecord(Action<JsonObject>? edit = null)
{
    var request = derivedPacket.DeepClone().AsObject(); edit?.Invoke(request);
    return JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, store.Review(Input(request))["reportArtifact"]!.AsObject()))!.AsObject();
}
JsonNode Derived(Action<JsonObject>? edit = null) => DerivedRecord(edit)["report"]!["derivedEffects"]![0]!;
var derivedReceipt = store.Review(Input(derivedPacket)); string derivedId = (string)derivedReceipt["recordId"]!;
Check((string?)Derived()["proposal"]?["status"] == "match" && (string?)Derived()["observation"]?["status"] == "match", "Both independent files match a separately derived expected document");
var derivedReport = DerivedRecord()["report"]!;
Check((string?)derivedReport["expectationCoverage"]![0]!["unassessedFileNames"]![0] == "screen.js", "Opaque script remains explicitly outside derived-effect coverage");
Check(!(bool)derivedReport["expectationCoverage"]![0]!["completeOutputEffectsVerified"]!, "Selected scalar cannot establish complete output effects");
foreach (var (label, changed) in new[] {
    ("unintended name", expectedScreen.Replace("<Name>Overview_A</Name>", "<Name>Other</Name>")),
    ("unintended nested scalar", expectedScreen.Replace("<BackColor>128, 128, 128</BackColor>", "<BackColor>0, 0, 0</BackColor>")),
    ("added document comment", expectedScreen + "<!--not expected-->"),
    ("missing declaration", expectedScreen[(expectedScreen.IndexOf("?>", StringComparison.Ordinal) + 2)..]),
    ("changed export timestamp", expectedScreen.Replace("2026-10-08", "2026-10-09")),
    ("unchanged original", screenText) })
{
    var r = DerivedRecord(p => {
        p["files"]!.AsArray().Add(FileInput("unexpected-screen", "screen.xml", Encoding.UTF8.GetBytes(changed)));
        p["outputs"]![0]!["proposed"] = new JsonArray("unexpected-screen", "proposed-script");
        p["observations"]![0]!["files"] = new JsonArray("unexpected-screen", "observed-script");
    })["report"]!;
    Check((string?)r["effectChecks"]![0]!["status"] == "match", "Equal wrong proposal/observation still pass opaque comparison: " + label);
    Check((string?)r["derivedEffects"]![0]!["proposal"]?["status"] == "different" && (string?)r["derivedEffects"]![0]!["observation"]?["status"] == "different", "Independent expectation detects " + label);
}
foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
    ("unsupported profile", p => p["expectations"]![0]!["profile"] = "all-hmi"),
    ("stale expected value", p => p["expectations"]![0]!["expectedValue"] = "0, 0, 0"),
    ("wrong export owner", p => p["expectations"]![0]!["ownerId"] = "3"),
    ("proposed as source", p => p["expectations"]![0]!["sourceFileId"] = "screen-proposed"),
    ("duplicate selection", p => p["expectations"]!.AsArray().Add(p["expectations"]![0]!.DeepClone())),
    ("unselected output", p => p["expectations"]![0]!["outputId"] = "unknown"),
    ("no expectation", p => p["expectations"] = new JsonArray()),
    ("implicit ownership", p => p["expectations"]![0]!["nativeQualified"] = true),
    ("unsupported action", p => p["outputs"]![0]!["action"] = "retain") })
    Reject(() => Derived(edit), "Derived expectation refuses " + label);
Check((string?)Derived(p => p["observations"] = new JsonArray())["observation"]?["status"] == "unobserved", "Missing observation does not become success");
Check((string?)Derived(p => p["observations"]![0]!["files"] = new JsonArray())["observation"]?["status"] == "missing", "Complete missing file is explicit");
Check((string?)Derived(p => { p["files"]!.AsArray().Add(FileInput("malformed", "screen.xml", Encoding.UTF8.GetBytes("<Document>"))); p["observations"]![0]!["files"] = new JsonArray("malformed"); })["observation"]?["status"] == "invalidDocument", "Malformed observed XML remains visible without parser diagnostics or native access");
foreach (var (label, text) in new[] {
    ("DTD", screenText.Replace("<Document>", "<!DOCTYPE Document [<!ENTITY local 'value'>]><Document>")),
    ("ambiguous attributes", screenText.Replace("<Hmi.Screen.Screen ID=\"0\">", "<Hmi.Screen.Screen ID=\"0\"><AttributeList/>")),
    ("non-scalar name", screenText.Replace("<Name>Overview_A</Name>", "<Name><Nested>Overview_A</Nested></Name>")),
    ("depth", screenText.Replace("</Document>", string.Concat(Enumerable.Repeat("<x>", 66)) + "text" + string.Concat(Enumerable.Repeat("</x>", 66)) + "</Document>")),
    ("events", screenText.Replace("</Document>", string.Concat(Enumerable.Repeat("<x/>", 200001)) + "</Document>")) })
{
    Reject(() => Derived(p => {
        p["files"]!.AsArray().Add(FileInput("bounded-source", "screen.xml", Encoding.UTF8.GetBytes(text)));
        p["outputs"]![0]!["current"] = new JsonArray("bounded-source"); p["expectations"]![0]!["sourceFileId"] = "bounded-source";
    }), "Source parsing refuses " + label);
}
fileBytes.Remove("bounded-source");
Reject(() => Derived(p => {
    byte[] large = Encoding.UTF8.GetBytes(screenText.Replace("</Document>", "<!--" + new string('x', 11 * 1024 * 1024) + "--></Document>"));
    var ids = new JsonArray(); var rules = new JsonArray();
    foreach (string suffix in new[] { "a", "b" })
    {
        string id = "large-" + suffix; p["files"]!.AsArray().Add(FileInput(id, suffix + ".xml", large)); fileBytes.Remove(id); ids.Add(id);
        var rule = p["expectations"]![0]!.DeepClone(); rule["sourceFileId"] = id; rule["replacementValue"] = "204, 204, 204"; rules.Add(rule);
    }
    foreach (string phase in new[] { "baseline", "current", "proposed" }) p["outputs"]![0]![phase] = ids.DeepClone();
    p["observations"]![0]!["files"] = ids.DeepClone(); p["expectations"] = rules;
}), "Repeated source/proposed/observed roles share the 64 MiB comparison budget");
foreach (string oldId in new[] { recordId, consumerRecordId, effectId }) Reject(() => store.Page(oldId, "derivedEffects"), "Prior versions never acquire invented expectations");
foreach (string section in new[] { "derivedEffects", "expectationCoverage" }) Check(store.Page(derivedId, section)["rows"]!.AsArray().Count == 1, "Derived review page: " + section);
foreach (string id in uploaded.ToArray()) uploads.Release(id);
Check(JsonNode.DeepEquals(derivedReceipt, new HmiComponentWorkflowStore(root).Page(derivedId)), "Derived expectation rebuilds from original files after upload release");
Check(JsonNode.DeepEquals(effectReceipt, new HmiComponentWorkflowStore(root).Page(effectId)), "All independent observation bytes survive upload release and store recreation");
Check(JsonNode.DeepEquals(consumerReceipt, new HmiComponentWorkflowStore(root).Page(consumerRecordId)), "v2 graph evidence survives all upload release and store recreation");
Check(JsonNode.DeepEquals(receipt, new HmiComponentWorkflowStore(root).Page(recordId)), "All upload release and store recreation preserve original evidence");
foreach (var artifact in new[] { receipt["input"]!.AsObject(), receipt["reportArtifact"]!.AsObject(), retained["report"]!["files"]![0]!["artifact"]!.AsObject() })
{
    string path = Path.Combine(root, (string)artifact["path"]!); byte[] original = File.ReadAllBytes(path); File.AppendAllText(path, "tampered");
    Reject(() => store.Page(recordId), "Tampered retained input/report/file refuses", "ArtifactChanged"); File.WriteAllBytes(path, original);
}
var forged = retained.DeepClone().AsObject(); forged["report"]!["nativeQualified"] = true;
byte[] forgedBytes = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string forgedId = Hash(forgedBytes); File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", forgedId + ".json"), forgedBytes);
Reject(() => store.Page(forgedId), "Rehashed invented qualification refuses recomputation", "ArtifactChanged");
foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
    ("invented source filename", p => p["report"]!["files"]![0]!["fileName"] = "other.xml"),
    ("invented interface comparison", p => p["report"]!["components"]![0]!["interfaceChanged"] = true),
    ("foreign input namespace", p => p["input"]!["path"] = "project-reviews/" + (string)p["input"]!["sha256"]! + ".json"),
    ("extra envelope claim", p => p["nativeQualified"] = true) })
{
    var record = retained.DeepClone().AsObject(); edit(record); byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json); string id = Hash(data);
    File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", id + ".json"), data);
    Reject(() => store.Page(id), "Rehashed " + label + " refuses");
}
foreach (string section in new[] { "unknown", "../outputs" }) Reject(() => store.Page(recordId, section), "Unknown page section refuses");
Reject(() => store.Page(recordId, "files", -1), "Negative offset refuses"); Reject(() => store.Page(recordId, "files", 7), "Past-end offset refuses"); Reject(() => store.Page(recordId, "files", 0, 201), "Oversized page refuses");
string reviewPath = Path.Combine(root, "project-reviews", afterId + ".json"); byte[] sourceBytes = File.ReadAllBytes(reviewPath); File.AppendAllText(reviewPath, "changed");
Reject(() => store.Page(recordId), "Changed saved PLC source refuses", "ArtifactChanged"); File.WriteAllBytes(reviewPath, sourceBytes);
var capacityRequest = Mutate(p => p["reason"] = "Fresh request under exhausted retention.");
int releasedCount = uploaded.Count;
foreach (var file in capacityRequest["files"]!.AsArray()) file!["uploadRef"] = Upload(Convert.FromBase64String(fileBytes[(string)file["id"]!]), (string)file["fileName"]!);
string capacityInput = Input(capacityRequest);
string incomplete = Path.Combine(root, "hmi-component-reviews", "incomplete.tmp");
try
{
    using (var stream = new FileStream(incomplete, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(512L * 1024 * 1024);
    Reject(() => store.Review(capacityInput), "Retention capacity includes incomplete files", "ArtifactCapacity");
    Check(JsonNode.DeepEquals(receipt, store.Page(recordId)), "Full capacity does not invalidate existing retained evidence");
}
finally { File.Delete(incomplete); }
foreach (string id in uploaded.Skip(releasedCount)) uploads.Release(id);
string changedId = Review("Real");
var libraryFixture = LibraryAssociationTests.Run(root, repository, derivedPacket);
DependencyAssociationRulesTests.Run();
var dependencyFixture = DependencyAssociationTests.Run(root, libraryFixture);
var dependencyImpactFixture = DependencyImpactTests.Run(dependencyFixture);
TextListAuthoringTests.Run(root, repository);
TextListGenerationTests.Run(root);
NumericTextListGenerationTests.Run(root);
CoordinatedTextListTests.Run(root);
TextListSetEvolutionTests.Run(root);
var unifiedSource = reviews.Read(destinationId); var unifiedModel = unifiedSource["model"]!.DeepClone().AsObject();
foreach (var node in unifiedModel["nodes"]!.AsArray().OfType<JsonObject>())
{
    if (node["metadata"]?["family"] is not null) node["metadata"]!["family"] = "unified";
    if (node["metadata"]?["hmiIdentity"] is JsonObject identity) identity["family"] = "unified";
}
string unifiedReviewId = (string)reviews.Save(unifiedModel, unifiedSource["documents"]!.DeepClone().AsArray(), new() { ["syntheticFixture"] = true })["reviewId"]!;
var fixture = new { artifactRoot = root, reviewIds = new[] { beforeId, afterId, destinationId, changedId, unifiedReviewId }, unifiedReviewId, changedReviewId = changedId, consumerReviewIds = new[] { consumerBefore, consumerAfter, consumerDestination }, consumerPacket, consumerInstall = newInstall, effectPacket, derivedPacket, packet, files = fileBytes, install, libraryFixture, dependencyFixture, dependencyImpactFixture };
File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(fixture, BridgeProtocol.Json));
File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, count = checks.Count, checks }, BridgeProtocol.Json));
Console.WriteLine($"PASS component/HMI review: {checks.Count} checks; synthetic saved observations and opaque supplied files only.");
