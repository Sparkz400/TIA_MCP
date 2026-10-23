using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.OpcUa;

const string ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
const string sensor = "nsu=urn%3Aexample%3Adevice;s=Sensor";
const string telemetry = "nsu=urn%3Aexample%3Atypes;i=1001";
const string reading = "nsu=urn%3Aexample%3Atypes;i=1002";
const string a = "nsu=urn%3Atest;s=A", c = "nsu=urn%3Atest;s=C";
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
string? Text(JsonNode? value) => value?.GetValue<string>();
void Reject(string code, Action action)
{
    try { action(); } catch (BridgeException ex) { Equal(code, ex.Code); return; }
    throw new Exception("Expected rejection: " + code);
}
OpcUaNodeSetSource Xml(string id, string body, string namespaces = "<Uri>urn:test</Uri>") => new(id,
    Encoding.UTF8.GetBytes($"<UANodeSet xmlns=\"{ns}\"><NamespaceUris>{namespaces}</NamespaceUris>{body}</UANodeSet>"));
OpcUaNodeSetSource Fixture(string name) => new(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
OpcUaNodeSetSource[] Before() => [Fixture("device.xml"), Fixture("types-v1.xml")];
OpcUaNodeSetSource[] After() => [Fixture("device.xml"), Fixture("types-v2.xml")];
// Independent hash implementation: do not obtain expected bindings from the production analyzer.
string SetHash(IEnumerable<OpcUaNodeSetSource> sources) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
    "opcua-model-set.v1\n" + string.Concat(sources.Select(s => Convert.ToHexStringLower(SHA256.HashData(s.Content))).Order(StringComparer.Ordinal).Select(h => h + "\n")))));
JsonObject Manifest(OpcUaNodeSetSource[] sources, params string[] keys) => new() { ["schemaVersion"] = "opcua-consumers.v2",
    ["modelSetSha256"] = SetHash(sources), ["consumers"] = new JsonArray(new JsonObject { ["id"] = "Client", ["nodeKeys"] = new JsonArray(keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) }) };
JsonObject Impact(OpcUaNodeSetSource[] old, OpcUaNodeSetSource[] newer, params string[] keys) =>
    OpcUaDependencyAnalysis.Compare(old, newer, Manifest(old, keys))["clientImpacts"]![0]!.AsObject();
string Object(string id, string target = "", string browse = "") => $"<UAObject NodeId=\"ns=1;s={id}\" BrowseName=\"1:{(browse == "" ? id : browse)}\">" +
    (target == "" ? "" : $"<References><Reference ReferenceType=\"ns=1;s=R\">ns=1;s={target}</Reference></References>") + "</UAObject>";
const string referenceType = "<UAReferenceType NodeId=\"ns=1;s=R\" BrowseName=\"1:R\" />";
bool HasCode(JsonObject report, string code) => report["diagnostics"]!.AsArray().Any(d => Text(d!["code"]) == code);

Test("annotation change follows explicit dependency paths", () => {
    var old = new[] { Xml("model", Object("A", "C") + Object("C").Replace("</UAObject>", "<!--before--></UAObject>") + referenceType) };
    var newer = new[] { old[0] with { Content = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(old[0].Content).Replace("<!--before-->", "<!--after-->")) } };
    var impact = Impact(old, newer, a);
    Equal("potentiallyAffected", Text(impact["status"]));
    Equal("annotationsSha256", Text(impact["indirectChanges"]![0]!["field"]));
    Equal(c, Text(impact["indirectChanges"]![0]!["nodeKey"]));
    Equal(1, impact["indirectChanges"]![0]!["path"]!.AsArray().Count);
    Equal("affected", Text(Impact(old, newer, c)["status"]));
});
Test("independent cross-file identities, alias, model URI and hash", () => {
    var report = OpcUaDependencyAnalysis.Analyze(Before());
    Equal(SetHash(Before()), Text(report["modelSetSha256"]));
    Equal(true, report["selectedDependenciesResolved"]!.GetValue<bool>());
    Equal(4, report["nodeCount"]!.GetValue<int>());
    var edges = report["dependencies"]!.AsArray();
    Equal(2, edges.Count);
    Equal(sensor, Text(edges[0]!["sourceNodeKey"])); Equal(telemetry, Text(edges[0]!["targetNodeKey"]));
    Equal("types-v1.xml", Text(edges[0]!["candidateSourceIds"]![0]));
    Equal(telemetry, Text(edges[1]!["sourceNodeKey"])); Equal(reading, Text(edges[1]!["targetNodeKey"]));
    Equal("resolved", Text(report["requiredModels"]![0]!["resolution"]));
    Equal("notEvaluated", Text(report["requiredModels"]![0]!["versionCompatibility"]));
    foreach (string field in new[] { "nativeQualified", "targetProbed", "tiaImportAccepted", "compatibilityCertified", "dependencyClosureComplete", "requiredModelVersionsEvaluated" })
        Equal(false, report[field]!.GetValue<bool>());
});
Test("input order deterministic, names excluded from content binding", () => {
    Equal(OpcUaDependencyAnalysis.Analyze(Before()).ToJsonString(), OpcUaDependencyAnalysis.Analyze(Before().Reverse().ToArray()).ToJsonString());
    var renamed = Before().Select(s => s with { Id = "renamed-" + s.Id }).ToArray();
    Equal(SetHash(Before()), Text(OpcUaDependencyAnalysis.Analyze(renamed)["modelSetSha256"]));
});
Test("missing external file and required model", () => {
    var report = OpcUaDependencyAnalysis.Analyze([Fixture("device.xml")]);
    Equal(false, report["selectedDependenciesResolved"]!.GetValue<bool>());
    Equal(true, HasCode(report, "DependencyNodeNotProvided")); Equal(true, HasCode(report, "RequiredModelNotProvided"));
    Equal(telemetry, Text(report["dependencies"]![0]!["targetNodeKey"]));
});
Test("namespace table alone does not provide a required model", () => {
    var provider = Xml("empty", "", "<Uri>urn:example:types</Uri>");
    Equal(true, HasCode(OpcUaDependencyAnalysis.Analyze([Fixture("device.xml"), provider]), "RequiredModelNotProvided"));
});
Test("standard nodes require explicit files", () => {
    var variable = Xml("v", "<UAVariable NodeId=\"ns=1;s=A\" BrowseName=\"1:A\" DataType=\"i=10\" />");
    Equal(true, HasCode(OpcUaDependencyAnalysis.Analyze([variable]), "StandardModelNotResolved"));
    var core = Xml("core", "<UADataType NodeId=\"i=10\" BrowseName=\"Float\"/>", "");
    Equal(true, OpcUaDependencyAnalysis.Analyze([variable, core])["selectedDependenciesResolved"]!.GetValue<bool>());
});
Test("identical duplicates remain ambiguous with all provenance", () => {
    var sources = Before().Append(Fixture("types-v1.xml") with { Id = "duplicate" }).ToArray();
    var report = OpcUaDependencyAnalysis.Analyze(sources);
    Equal(true, HasCode(report, "AmbiguousNodeDefinition")); Equal(true, HasCode(report, "AmbiguousDependencyNode"));
    Equal(true, HasCode(report, "AmbiguousRequiredModel")); Equal(true, HasCode(report, "AmbiguousModelDefinition"));
    Equal(2, report["dependencies"]!.AsArray().Single(e => Text(e!["sourceNodeKey"]) == sensor)!["candidateSourceIds"]!.AsArray().Count);
    Equal(false, SetHash(Before()) == Text(report["modelSetSha256"]));
});
Test("conflicting definitions are skipped, not arbitrarily compared", () => {
    var report = OpcUaDependencyAnalysis.Compare(Before(), After().Append(Fixture("types-v1.xml")).ToArray());
    Equal(0, report["changes"]!.AsArray().Count);
    Equal(2, report["comparisonSkippedNodeKeys"]!.AsArray().Count);
    Equal("ambiguousDefinitions", Text(report["outcome"]));
});
Test("two-hop client impact contains independently expected path", () => {
    var report = OpcUaDependencyAnalysis.Compare(Before(), After(), Manifest(Before(), sensor));
    Equal(1, report["breakingChangeCount"]!.GetValue<int>());
    var impact = report["clientImpacts"]![0]!;
    Equal("potentiallyAffected", Text(impact["status"])); Equal(0, impact["directChanges"]!.AsArray().Count);
    var row = impact["indirectChanges"]![0]!;
    Equal(reading, Text(row["nodeKey"])); Equal("browseName", Text(row["field"])); Equal(sensor, Text(row["consumedNodeKey"]));
    Equal("before", Text(row["graphSnapshot"])); Equal(2, row["path"]!.AsArray().Count);
    Equal("dataType", Text(row["path"]![0]!["kind"])); Equal(telemetry, Text(row["path"]![0]!["targetNodeKey"]));
    Equal("definitionFieldDataType", Text(row["path"]![1]!["kind"])); Equal(reading, Text(row["path"]![1]!["targetNodeKey"]));
});
Test("published consumer fixture binds the exact set and expected clients", () => {
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "consumers-v2.json")))!.AsObject();
    Equal(SetHash(Before()), Text(manifest["modelSetSha256"]));
    var impacts = OpcUaDependencyAnalysis.Compare(Before(), After(), manifest)["clientImpacts"]!.AsArray();
    Equal("SensorClient", Text(impacts[0]!["consumerId"])); Equal("potentiallyAffected", Text(impacts[0]!["status"]));
    Equal("TypeClient", Text(impacts[1]!["consumerId"])); Equal("affected", Text(impacts[1]!["status"]));
    Equal("UnrelatedClient", Text(impacts[2]!["consumerId"])); Equal("noKnownImpact", Text(impacts[2]!["status"]));
});
Test("direct changes separated from indirect and unrelated clients", () => {
    var direct = Impact(Before(), After(), reading, sensor);
    Equal("affected", Text(direct["status"])); Equal(1, direct["directChanges"]!.AsArray().Count); Equal(0, direct["indirectChanges"]!.AsArray().Count);
    var unrelated = Impact(Before(), After(), "nsu=urn%3Aexample%3Adevice;s=Unrelated");
    Equal("noKnownImpact", Text(unrelated["status"])); Equal(0, unrelated["indirectChanges"]!.AsArray().Count);
});
Test("cycles terminate and yield a shortest path", () => {
    var old = new[] { Xml("g", referenceType + Object("A", "B") + Object("B", "A") + Object("C")) };
    var newer = new[] { Xml("g", referenceType + Object("A", "B") + Object("B", "A", "Changed") + Object("C")) };
    var impact = Impact(old, newer, a);
    Equal(1, impact["indirectChanges"]!.AsArray().Count); Equal(1, impact["indirectChanges"]![0]!["path"]!.AsArray().Count);
});
Test("removed dependencies retain before impact evidence", () => {
    var old = new[] { Xml("g", referenceType + Object("A", "B") + Object("B")) };
    var newer = new[] { Xml("g", referenceType + Object("A")) };
    var impact = Impact(old, newer, a);
    Equal("nodeRemoved", Text(impact["indirectChanges"]![0]!["field"]));
    Equal("before", Text(impact["indirectChanges"]![0]!["graphSnapshot"]));
});
Test("new dependencies use after evidence", () => {
    var old = new[] { Xml("g", referenceType + Object("A") + Object("B")) };
    var newer = new[] { Xml("g", referenceType + Object("A", "B") + Object("B", browse: "Changed")) };
    Equal("after", Text(Impact(old, newer, a)["indirectChanges"]![0]!["graphSnapshot"]));
});
Test("no invented path assembled across snapshots", () => {
    var old = new[] { Xml("g", referenceType + Object("A", "B") + Object("B") + Object("C")) };
    var newer = new[] { Xml("g", referenceType + Object("A") + Object("B", "C") + Object("C", browse: "Changed")) };
    Equal(false, Impact(old, newer, a)["indirectChanges"]!.AsArray().Any(i => Text(i!["nodeKey"]) == c));
});
Test("ambiguous edges stop traversal and surface consumer gaps", () => {
    var old = Before().Append(Fixture("types-v1.xml") with { Id = "duplicate" }).ToArray();
    var impact = Impact(old, After(), sensor);
    Equal(0, impact["indirectChanges"]!.AsArray().Count);
    Equal("ambiguous", Text(impact["dependencyGaps"]![0]!["resolution"]));
    Reject("OpcUaInvalidConsumers", () => Impact(old, After(), reading));
});
Test("missing references surface in client coverage even without changes", () => {
    var old = new[] { Fixture("device.xml") };
    var impact = Impact(old, old, sensor);
    Equal("noKnownImpact", Text(impact["status"])); Equal(2, impact["dependencyGaps"]!.AsArray().Count);
});
Test("reference types, inverse targets, parent, method and roles resolve", () => {
    var graph = Xml("g", referenceType + Object("B") + "<UAMethod NodeId=\"ns=1;s=A\" BrowseName=\"1:A\" ParentNodeId=\"ns=1;s=B\" MethodDeclarationId=\"ns=1;s=B\"><References><Reference ReferenceType=\"ns=1;s=R\" IsForward=\"false\">ns=1;s=B</Reference></References><RolePermissions><RolePermission>ns=1;s=B</RolePermission></RolePermissions></UAMethod>");
    var report = OpcUaDependencyAnalysis.Analyze([graph]);
    Equal(true, report["selectedDependenciesResolved"]!.GetValue<bool>());
    Equal("MethodDeclarationId,ParentNodeId,referenceTarget,referenceType,rolePermission", string.Join(",", report["dependencies"]!.AsArray().Select(e => Text(e!["kind"])!).Order(StringComparer.Ordinal)));
});
Test("stale dependency bytes invalidate manifest even if primary unchanged", () => {
    Reject("OpcUaStaleConsumers", () => OpcUaDependencyAnalysis.Compare(After(), After(), Manifest(Before(), sensor)));
    var invalid = Manifest(Before(), sensor); invalid["schemaVersion"] = "opcua-consumers.v1";
    Reject("OpcUaStaleConsumers", () => OpcUaDependencyAnalysis.Compare(Before(), After(), invalid));
});
Test("strict consumer manifest validation", () => {
    Reject("OpcUaInvalidConsumers", () => Impact(Before(), After(), "absent"));
    Reject("OpcUaInvalidConsumers", () => Impact(Before(), After(), sensor, sensor));
    var extra = Manifest(Before(), sensor); extra["extra"] = true;
    Reject("OpcUaInvalidConsumers", () => OpcUaDependencyAnalysis.Compare(Before(), After(), extra));
    var duplicate = Manifest(Before(), sensor); duplicate["consumers"]!.AsArray().Add(duplicate["consumers"]![0]!.DeepClone());
    Reject("OpcUaInvalidConsumers", () => OpcUaDependencyAnalysis.Compare(Before(), After(), duplicate));
    var invalid = Manifest(Before(), sensor); invalid["consumers"]![0]!["nodeKeys"] = new JsonArray(42);
    Reject("OpcUaInvalidConsumers", () => OpcUaDependencyAnalysis.Compare(Before(), After(), invalid));
});
Test("metadata-only edits visible even without node changes", () => {
    var old = new[] { Xml("g", "<Models><Model ModelUri=\"urn:test\" Version=\"1\"/></Models>" + Object("A")) };
    var newer = new[] { Xml("g", "<Models><Model ModelUri=\"urn:test\" Version=\"2\"/></Models>" + Object("A")) };
    var report = OpcUaDependencyAnalysis.Compare(old, newer);
    Equal(0, report["changes"]!.AsArray().Count); Equal("artifactChangesRequireReview", Text(report["outcome"]));
});
Test("invalid identities and XML retain parser defenses", () => {
    Reject("OpcUaIdentityCollision", () => OpcUaDependencyAnalysis.Analyze([Xml("g", Object("A") + Object("A"))]));
    Reject("OpcUaInvalidIdentity", () => OpcUaDependencyAnalysis.Analyze([Xml("g", "<UAObject NodeId=\"ns=2;s=A\" BrowseName=\"1:A\"/>" )]));
    Reject("OpcUaInvalidXml", () => OpcUaDependencyAnalysis.Analyze([new("g", Encoding.UTF8.GetBytes($"<!DOCTYPE UANodeSet [<!ENTITY e SYSTEM 'file:///missing'>]><UANodeSet xmlns=\"{ns}\">&e;</UANodeSet>"))]));
    Reject("OpcUaSchemaMismatch", () => OpcUaDependencyAnalysis.Analyze([Xml("g", "<UAObject NodeId=\"i=1\"/>" )]));
});
Test("source and capacity bounds fail explicitly", () => {
    Reject("OpcUaCapacity", () => OpcUaDependencyAnalysis.Analyze([]));
    Reject("OpcUaCapacity", () => OpcUaDependencyAnalysis.Analyze(Enumerable.Range(0, 33).Select(i => Xml(i.ToString(), "")).ToArray()));
    Reject("OpcUaInvalidSources", () => OpcUaDependencyAnalysis.Analyze([Xml("g", ""), Xml("g", "")]));
    Reject("OpcUaCapacity", () => OpcUaDependencyAnalysis.Analyze([new("g", new byte[OpcUaModelReview.MaxBytes + 1])]));
    var oversized = new byte[OpcUaModelReview.MaxBytes];
    Reject("OpcUaCapacity", () => OpcUaDependencyAnalysis.Analyze(Enumerable.Range(0, 5).Select(i => new OpcUaNodeSetSource(i.ToString(), oversized)).ToArray()));
});
Test("unresolved-row bound applies after cross-file resolution", () => {
    string variables = string.Concat(Enumerable.Range(1, 10001).Select(i => $"<UAVariable NodeId=\"ns=1;i={i}\" BrowseName=\"1:V{i}\" DataType=\"i=10\"/>"));
    var source = Xml("g", variables);
    Reject("OpcUaCapacity", () => OpcUaDependencyAnalysis.Analyze([source]));
    var core = Xml("core", "<UADataType NodeId=\"i=10\" BrowseName=\"Float\"/>", "");
    Equal(true, OpcUaDependencyAnalysis.Analyze([source, core])["selectedDependenciesResolved"]!.GetValue<bool>());
});

// Run the actual standalone executable when supplied by the isolated runner.
if (args.Length == 1)
{
    string cli = Path.GetFullPath(args[0]);
    (int Exit, string Out, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(cli);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new Exception("Owned CLI process timed out."); }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
    void WithDirectory(Action<string> run)
    {
        string directory = Path.Combine(Path.GetTempPath(), "opcua-dependency-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { run(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
    string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    Test("CLI dependencies, compare-set and v2 manifest", () => WithDirectory(directory => {
        string output = Path.Combine(directory, "dependencies.json");
        Equal(0, Run("dependencies", output, FixturePath("device.xml"), FixturePath("types-v1.xml")).Exit);
        Equal(SetHash(Before()), Text(JsonNode.Parse(File.ReadAllText(output))!["modelSetSha256"]));
        string manifest = Path.Combine(directory, "consumers.json"); File.WriteAllText(manifest, Manifest(Before(), sensor).ToJsonString());
        string comparison = Path.Combine(directory, "comparison.json");
        var run = Run("compare-set", comparison, "--before", FixturePath("device.xml"), "--before", FixturePath("types-v1.xml"),
            "--after", FixturePath("device.xml"), "--after", FixturePath("types-v2.xml"), "--consumers", manifest);
        Equal(0, run.Exit);
        Equal(reading, Text(JsonNode.Parse(File.ReadAllText(comparison))!["clientImpacts"]![0]!["indirectChanges"]![0]!["nodeKey"]));
    }));
    Test("CLI unresolved report exit, overwrite and input preservation", () => WithDirectory(directory => {
        string output = Path.Combine(directory, "missing.json");
        Equal(3, Run("dependencies", output, FixturePath("device.xml")).Exit);
        string retained = File.ReadAllText(output);
        Equal(1, Run("dependencies", output, FixturePath("device.xml"), FixturePath("types-v1.xml")).Exit);
        Equal(retained, File.ReadAllText(output));
        string input = Path.Combine(directory, "input.xml"); File.Copy(FixturePath("device.xml"), input);
        string original = File.ReadAllText(input); Equal(1, Run("dependencies", input, input).Exit); Equal(original, File.ReadAllText(input));
    }));
    Test("CLI comparison without consumers and stale manifest refusal", () => WithDirectory(directory => {
        string output = Path.Combine(directory, "missing.json");
        Equal(3, Run("compare-set", output, "--before", FixturePath("device.xml"), "--after", FixturePath("device.xml")).Exit);
        Equal(0, JsonNode.Parse(File.ReadAllText(output))!["clientImpacts"]!.AsArray().Count);
        string manifest = Path.Combine(directory, "stale.json"); File.WriteAllText(manifest, Manifest(Before(), sensor).ToJsonString());
        string refused = Path.Combine(directory, "refused.json");
        var run = Run("compare-set", refused, "--before", FixturePath("device.xml"), "--after", FixturePath("device.xml"), "--consumers", manifest);
        Equal(1, run.Exit); Equal("OpcUaStaleConsumers", Text(JsonNode.Parse(run.Error)!["code"])); Equal(false, File.Exists(refused));
    }));
    Test("CLI errors never publish reports", () => WithDirectory(directory => {
        string output = Path.Combine(directory, "report.json");
        Equal(2, Run("compare-set", output, "--before", FixturePath("device.xml")).Exit);
        Equal(2, Run("compare-set", output, "--bogus", "x").Exit);
        Equal(2, Run("compare-set", output, "--after").Exit);
        Equal(1, Run("dependencies", output, Path.Combine(directory, "absent.xml")).Exit);
        Equal(1, Run("dependencies", output, FixturePath("device.xml"), FixturePath("device.xml")).Exit);
        string bad = Path.Combine(directory, "bad.xml"); File.WriteAllText(bad, "<broken");
        Equal(1, Run("dependencies", output, bad).Exit); Equal(false, File.Exists(output));
        Equal(0, Run("--help").Exit);
    }));
}
else if (args.Length != 0) throw new ArgumentException("Optional argument: path to the built OPC UA CLI DLL.");

int failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} offline OPC UA dependency checks passed.");
if (args.Length == 0) Console.WriteLine("CLI subprocess checks not run; supply the CLI DLL path or use Test-OpcUaOffline.ps1.");
return failures == 0 ? 0 : 1;
