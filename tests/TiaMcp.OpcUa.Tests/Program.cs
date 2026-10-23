using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.OpcUa;

const string ns = "http://opcfoundation.org/UA/2011/03/UANodeSet.xsd";
var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
byte[] Xml(string body, string namespaces = "<Uri>urn:test</Uri>") => Encoding.UTF8.GetBytes(
    $"<UANodeSet xmlns=\"{ns}\"><NamespaceUris>{namespaces}</NamespaceUris>{body}</UANodeSet>");
byte[] Variable(string extra = "", string content = "") => Xml($"<UAVariable NodeId=\"ns=1;s=Speed\" BrowseName=\"1:Speed\" DataType=\"i=10\" {extra}>{content}</UAVariable>");
byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Reject(string code, Action action)
{
    try { action(); } catch (BridgeException ex) { Equal(code, ex.Code); return; }
    throw new Exception("Expected rejection: " + code);
}
JsonArray Changes(byte[] a, byte[] b) => OpcUaModelReview.Compare(a, b)["changes"]!.AsArray();

Test("fixture inventory and nonqualification", () => {
    var bytes = Fixture("machine-v1.xml"); var report = OpcUaModelReview.Inspect(bytes);
    Equal(3, report["nodeCount"]!.GetValue<int>()); Equal(Hash(bytes), report["sourceSha256"]!.GetValue<string>());
    foreach (string field in new[] { "nativeQualified", "targetProbed", "tiaImportAccepted", "plcMappingsResolved", "dependencyClosureComplete" })
        Equal(false, report[field]!.GetValue<bool>());
    Equal("urn:example:machine:1", report["nodes"]![0]!["identity"]!["namespaceUri"]!.GetValue<string>());
});
Test("identical model", () => Equal(0, Changes(Fixture("machine-v1.xml"), Fixture("machine-v1.xml")).Count));
Test("additive fixture has no removed identities", () => {
    var report = OpcUaModelReview.Compare(Fixture("machine-v1.xml"), Fixture("machine-v1.1.xml"));
    Equal(0, report["breakingChangeCount"]!.GetValue<int>());
    Equal(1, report["changes"]!.AsArray().Count(c => c!["field"]!.GetValue<string>() == "nodeAdded"));
    Equal(false, report["compatibilityCertified"]!.GetValue<bool>());
});
Test("breaking version identifies old clients", () => {
    var before = Fixture("machine-v1.xml");
    var consumers = new JsonObject { ["schemaVersion"] = "opcua-consumers.v1", ["modelSha256"] = Hash(before),
        ["consumers"] = new JsonArray(new JsonObject { ["id"] = "Historian-A", ["nodeKeys"] = new JsonArray("nsu=urn%3Aexample%3Amachine%3A1;s=Machine.Speed") }) };
    var report = OpcUaModelReview.Compare(before, Fixture("machine-v2.xml"), consumers);
    Equal(3, report["breakingChangeCount"]!.GetValue<int>());
    Equal("affected", report["clientImpacts"]![0]!["status"]!.GetValue<string>());
    Equal("nodeRemoved", report["clientImpacts"]![0]!["directChanges"]![0]!["field"]!.GetValue<string>());
});
Test("namespace indexes are not node identities", () => {
    var a = Xml("<UAObject NodeId=\"ns=1;s=A\" BrowseName=\"1:A\" />", "<Uri>urn:a</Uri><Uri>urn:b</Uri>");
    var b = Xml("<UAObject NodeId=\"ns=2;s=A\" BrowseName=\"2:A\" />", "<Uri>urn:b</Uri><Uri>urn:a</Uri>");
    Equal(0, Changes(a,b).Count);
});
Test("alias expansion and numeric normalization", () => {
    var a = Xml("<Aliases><Alias Alias=\"Float\">i=10</Alias></Aliases><UAVariable NodeId=\"ns=1;i=001\" BrowseName=\"1:A\" DataType=\"Float\" />");
    var b = Xml("<UAVariable NodeId=\"ns=1;i=1\" BrowseName=\"1:A\" DataType=\"i=10\" />");
    Equal(0, Changes(a,b).Count);
});
Test("GUID and opaque identities", () => {
    var namespaces = new[] { OpcUaNodeIdentity.StandardNamespace, "urn:a" };
    Equal("12345678-1234-abcd-1234-123456789abc", OpcUaNodeIdentity.Parse("ns=1;g=12345678-1234-ABCD-1234-123456789ABC", namespaces).Identifier);
    Equal("AQI=", OpcUaNodeIdentity.Parse("ns=1;b=AQI=", namespaces).Identifier);
    Equal("a;b=c", OpcUaNodeIdentity.Parse("ns=1;s=a;b=c", namespaces).Identifier);
});
Test("numeric overflow", () => Reject("OpcUaInvalidIdentity", () => OpcUaModelReview.Inspect(Xml("<UAObject NodeId=\"i=4294967296\" BrowseName=\"A\"/>"))));
Test("unknown namespace index", () => Reject("OpcUaInvalidIdentity", () => OpcUaModelReview.Inspect(Xml("<UAObject NodeId=\"ns=2;i=1\" BrowseName=\"A\"/>"))));
Test("duplicate namespace", () => Reject("OpcUaInvalidIdentity", () => OpcUaModelReview.Inspect(Xml("", "<Uri>urn:a</Uri><Uri>urn:a</Uri>"))));
Test("normalized ID collision", () => Reject("OpcUaIdentityCollision", () => OpcUaModelReview.Inspect(Xml("<UAObject NodeId=\"ns=1;i=01\" BrowseName=\"A\"/><UAObject NodeId=\"ns=1;i=1\" BrowseName=\"B\"/>"))));
Test("duplicate aliases", () => Reject("OpcUaInvalidIdentity", () => OpcUaModelReview.Inspect(Xml("<Aliases><Alias Alias=\"A\">i=1</Alias><Alias Alias=\"A\">i=2</Alias></Aliases>"))));
Test("recursive alias rejected", () => Reject("OpcUaInvalidIdentity", () => OpcUaModelReview.Inspect(Xml("<Aliases><Alias Alias=\"A\">B</Alias><Alias Alias=\"B\">i=1</Alias></Aliases>"))));
Test("datatype change breaks clients", () => {
    var b = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Variable()).Replace("DataType=\"i=10\"", "DataType=\"i=11\""));
    var change = Changes(Variable(), b).Single()!; Equal("dataType", change["field"]!.GetValue<string>()); Equal("breaking", change["severity"]!.GetValue<string>());
});
Test("array rank and bounds", () => {
    OpcUaModelReview.Inspect(Variable("ValueRank=\"2\" ArrayDimensions=\"2,4\""));
    Reject("OpcUaInvalidShape", () => OpcUaModelReview.Inspect(Variable("ValueRank=\"2\" ArrayDimensions=\"4\"")));
    Reject("OpcUaInvalidShape", () => OpcUaModelReview.Inspect(Variable("ValueRank=\"-4\"")));
});
Test("schema required browse name", () => Reject("OpcUaSchemaMismatch", () => OpcUaModelReview.Inspect(Xml("<UAObject NodeId=\"i=1\"/>"))));
Test("array dimension overflow is structured", () => Reject("OpcUaInvalidShape", () => OpcUaModelReview.Inspect(Variable("ValueRank=\"1\" ArrayDimensions=\"4294967296\""))));
Test("wrong root namespace", () => Reject("OpcUaSchemaMismatch", () => OpcUaModelReview.Inspect(Encoding.UTF8.GetBytes("<UANodeSet xmlns=\"urn:wrong\"/>"))));
Test("DTD blocked", () => Reject("OpcUaInvalidXml", () => OpcUaModelReview.Inspect(Encoding.UTF8.GetBytes($"<!DOCTYPE UANodeSet [<!ENTITY x SYSTEM 'file:///C:/secret'>]><UANodeSet xmlns=\"{ns}\">&x;</UANodeSet>"))));
Test("schemaLocation is not fetched", () => OpcUaModelReview.Inspect(Encoding.UTF8.GetBytes($"<UANodeSet xmlns=\"{ns}\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:schemaLocation=\"{ns} https://invalid.invalid/not-loaded.xsd\"/>")));
Test("depth bound includes extensions", () => Reject("OpcUaCapacity", () => OpcUaModelReview.Inspect(Xml("<Extensions><Extension>" + string.Concat(Enumerable.Repeat("<a xmlns=\"urn:vendor\">", 70)) + string.Concat(Enumerable.Repeat("</a>", 70)) + "</Extension></Extensions>"))));
Test("input byte bound", () => Reject("OpcUaCapacity", () => OpcUaModelReview.Inspect(new byte[OpcUaModelReview.MaxBytes + 1])));
Test("reference dependencies explicit", () => {
    var report = OpcUaModelReview.Inspect(Variable(content: "<References><Reference ReferenceType=\"i=47\">ns=1;s=missing</Reference></References>"));
    Equal(true, report["diagnostics"]!.AsArray().Any(d => d!["code"]!.GetValue<string>() == "DependencyNodeNotProvided"));
});
Test("permission changes never hidden", () => {
    var change = Changes(Variable("AccessLevel=\"1\""), Variable("AccessLevel=\"3\"")).Single()!;
    Equal("attributes", change["field"]!.GetValue<string>()); Equal("reviewRequired", change["severity"]!.GetValue<string>());
});
Test("vendor extension changes require review", () => {
    byte[] Model(string value) => Variable(content: $"<Extensions><Extension><Mapping xmlns=\"urn:vendor\">{value}</Mapping></Extension></Extensions>");
    Equal("otherContentSha256", Changes(Model("DB1.Speed"), Model("DB2.Speed")).Single()!["field"]!.GetValue<string>());
});
Test("whitespace in value not discarded", () => {
    byte[] Model(string value) => Variable(content: $"<Value><String xmlns=\"http://opcfoundation.org/UA/2008/02/Types.xsd\">{value}</String></Value>");
    Equal("valueSha256", Changes(Model(" "), Model("  ")).Single()!["field"]!.GetValue<string>());
});
Test("opaque QName binding changes are retained", () => {
    byte[] Model(string uri) => Variable(content: $"<Extensions xmlns:m=\"{uri}\"><Extension><Mapping xmlns=\"urn:vendor\">m:Type</Mapping></Extension></Extensions>");
    Equal("otherContentSha256", Changes(Model("urn:before"), Model("urn:after")).Single()!["field"]!.GetValue<string>());
});
Test("method argument changes are breaking", () => {
    byte[] Model(string value) => Xml($"<UAVariable NodeId=\"ns=1;s=Args\" BrowseName=\"0:InputArguments\"><Value><String xmlns=\"urn:opaque\">{value}</String></Value></UAVariable>");
    Equal("breaking", Changes(Model("Int32"), Model("Double")).Single()!["severity"]!.GetValue<string>());
});
Test("stale consumer evidence", () => Reject("OpcUaStaleConsumers", () => OpcUaModelReview.Compare(Variable(), Variable(), new JsonObject { ["schemaVersion"]="opcua-consumers.v1", ["modelSha256"] = new string('0',64) })));
Test("invented consumer node denied", () => Reject("OpcUaInvalidConsumers", () => OpcUaModelReview.Compare(Variable(), Variable(), new JsonObject {
    ["schemaVersion"]="opcua-consumers.v1", ["modelSha256"] = Hash(Variable()), ["consumers"] = new JsonArray(new JsonObject { ["id"]="Client", ["nodeKeys"] = new JsonArray("unknown") }) })));
Test("node capacity", () => Reject("OpcUaCapacity", () => OpcUaModelReview.Inspect(Xml(string.Concat(Enumerable.Range(1, OpcUaModelReview.MaxNodes + 1).Select(i => $"<UAObject NodeId=\"ns=1;i={i}\" BrowseName=\"1:N{i}\"/>"))))));

Test("node annotations are reviewed across selected and opaque content", () => {
    foreach (string annotation in new[] { "<!--VALUE-->", "<?vendor VALUE?>" })
    foreach (Func<string, byte[]> model in new Func<string, byte[]>[] {
        a => Variable(content: a),
        a => Variable(content: "<References><Reference ReferenceType=\"i=47\">i=85" + a + "</Reference></References>"),
        a => Variable(content: "<DisplayName>A" + a + "</DisplayName>"),
        a => Variable(content: "<Value><String xmlns=\"urn:opaque\">same" + a + "</String></Value>"),
        a => Variable(content: "<Extensions><Extension><Mapping xmlns=\"urn:vendor\">" + a + "</Mapping></Extension></Extensions>"),
        a => Xml("<UADataType NodeId=\"ns=1;s=T\" BrowseName=\"1:T\"><Definition Name=\"T\">" + a + "</Definition></UADataType>") })
    {
        byte[] before = model(annotation.Replace("VALUE", "before")), after = model(annotation.Replace("VALUE", "after"));
        var changes = Changes(before, after); Equal(1, changes.Count);
        Equal("annotationsSha256", changes[0]!["field"]!.GetValue<string>());
        Equal("reviewRequired", changes[0]!["severity"]!.GetValue<string>());
        Equal(0, Changes(before, before).Count);
    }
});
Test("envelope annotations survive outside root and projected metadata", () => {
    foreach (string annotation in new[] { "<!--VALUE-->", "<?vendor VALUE?>" })
    foreach (Func<string, byte[]> model in new Func<string, byte[]>[] {
        a => Encoding.UTF8.GetBytes(a + Encoding.UTF8.GetString(Variable())),
        a => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Variable()) + a),
        a => Xml(a), a => Xml("", "<Uri>urn:test" + a + "</Uri>"),
        a => Xml("<Aliases><Alias Alias=\"A\">i=10" + a + "</Alias></Aliases>"),
        a => Xml("<Models>" + a + "<Model ModelUri=\"urn:test\"/></Models>"),
        a => Xml("<Extensions><Extension><Mapping xmlns=\"urn:vendor\">" + a + "</Mapping></Extension></Extensions>") })
    {
        var changes = Changes(model(annotation.Replace("VALUE", "before")), model(annotation.Replace("VALUE", "after")));
        Equal(true, changes.Any(c => c!["field"]!.GetValue<string>() == "envelopeOrExtensions"));
    }
});
Test("annotation position and instruction target are significant", () => {
    Equal(1, Changes(Variable(content: "<!--keep--><DisplayName>A</DisplayName>"), Variable(content: "<DisplayName>A</DisplayName><!--keep-->")).Count);
    Equal(1, Changes(Variable(content: "<?first same?>"), Variable(content: "<?second same?>")).Count);
});
Test("annotations cannot fabricate a method argument payload change", () => {
    byte[] Model(string value) => Xml("<UAVariable NodeId=\"ns=1;s=Args\" BrowseName=\"0:InputArguments\"><Value><String xmlns=\"urn:opaque\">" + value + "</String></Value></UAVariable>");
    var changes = Changes(Model("AB"), Model("A<!--keep--><?vendor note?>B"));
    Equal(1, changes.Count); Equal("annotationsSha256", changes[0]!["field"]!.GetValue<string>());
    Equal("reviewRequired", changes[0]!["severity"]!.GetValue<string>());
    var moved = Changes(Model("A<!--keep-->B"), Model("AB<!--keep-->"));
    Equal("annotationsSha256", moved.Single()!["field"]!.GetValue<string>());
    var reordered = Changes(Model("A<Child/>B"), Model("AB<Child/>"));
    Equal("valueSha256", reordered.Single()!["field"]!.GetValue<string>());
    Equal("breaking", reordered.Single()!["severity"]!.GetValue<string>());
});
Test("annotation changes affect declared consumers", () => {
    byte[] before = Variable(content: "<!--before-->"), after = Variable(content: "<!--after-->");
    var key = OpcUaModelReview.Inspect(before)["nodes"]![0]!["nodeKey"]!.DeepClone();
    var manifest = new JsonObject { ["schemaVersion"] = "opcua-consumers.v1", ["modelSha256"] = Hash(before),
        ["consumers"] = new JsonArray(new JsonObject { ["id"] = "Client", ["nodeKeys"] = new JsonArray(key) }) };
    var report = OpcUaModelReview.Compare(before, after, manifest);
    Equal("affected", report["clientImpacts"]![0]!["status"]!.GetValue<string>());
    Equal("annotationsSha256", report["clientImpacts"]![0]!["directChanges"]![0]!["field"]!.GetValue<string>());
});
Test("annotation review retains encoding independence and exact byte identity", () => {
    byte[] utf8 = Variable(content: "<!--Ω metadata--><?vendor keep?>");
    string text = Encoding.UTF8.GetString(utf8);
    foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
    {
        byte[] input = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(); var copy = input.ToArray();
        var report = OpcUaModelReview.Inspect(input); Equal(Hash(input), report["sourceSha256"]!.GetValue<string>());
        Equal(0, Changes(utf8, input).Count); Equal(true, input.SequenceEqual(copy));
    }
});
Test("XML event budget includes annotations and opaque elements before DOM allocation", () => {
    foreach (string item in new[] { "<!--x-->", "<?v x?>", "<x/>" })
    {
        string repeated = string.Concat(Enumerable.Repeat(item, 500001));
        byte[] input = Xml(item == "<x/>" ? "<Extensions><Extension><v xmlns=\"urn:vendor\">" + repeated + "</v></Extension></Extensions>" : repeated);
        Reject("OpcUaCapacity", () => OpcUaModelReview.Inspect(input));
    }
});

int failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} offline OPC UA checks passed. No native qualification.");
return failures == 0 ? 0 : 1;
