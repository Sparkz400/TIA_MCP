using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TiaMcp.Graph;
using Cli = TiaMcp.Graph.Cli.Program;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
byte[] Fixture(string name = "sequence-v1.xml") => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
GraphDocument Parse(string xml) => GraphDocument.Parse(Encoding.UTF8.GetBytes(xml));
GraphDocument Original() => GraphDocument.Parse(Fixture());
GraphDocument Mutate(Action<XElement> change, string fixture = "sequence-v1.xml")
{
    var xml = XDocument.Parse(Encoding.UTF8.GetString(Fixture(fixture)), LoadOptions.PreserveWhitespace);
    change(xml.Root!);
    return Parse(xml.ToString(SaveOptions.DisableFormatting));
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void True(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
void Code(GraphDocument d, string code) => True(d.Inspection.Diagnostics.Any(x => x.Code == code), $"Missing diagnostic {code}");
void Reject(string code, Action action)
{
    try { action(); } catch (GraphException e) { Equal(code, e.Code); return; }
    throw new Exception("Expected rejection " + code);
}
XElement First(XElement root, string name) => root.DescendantsAndSelf().First(e => e.Name.LocalName == name);
XElement AddSecondSequence(XElement root)
{
    var copy = new XElement(First(root, "Sequence"));
    foreach (var element in copy.Descendants().Where(e => e.Name.LocalName is "Step" or "Transition" or "StepRef" or "TransitionRef"))
        element.SetAttributeValue("Number", (int)element.Attribute("Number")! + 10);
    First(root, "Sequence").AddAfterSelf(copy);
    return copy;
}
bool HasChange(GraphComparison comparison, string category, string field) => comparison.Changes.Any(c => c.Category == category && c.Field == field);
string generated = Path.Combine(AppContext.BaseDirectory, "generated", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(generated);

Test("independent fixture structure and qualification flags", () =>
{
    var report = Original().Inspection;
    True(!report.HasErrors); True(!report.NativeQualified); True(!report.FullSchemaValidated);
    Equal(1, report.Graphs.Count); Equal(1, report.Graphs[0].Sequences.Count);
    var s = report.Graphs[0].Sequences[0];
    Equal(3, s.Steps.Count); Equal(2, s.Transitions.Count); Equal(2, s.Branches.Count); Equal(8, s.Connections.Count);
    Equal(1, s.Steps[0].Number); Equal("Idle", s.Steps[0].Name); Equal(true, s.Steps[0].Init);
    Equal("T#10s", s.Steps[1].MaximumStepTime); Equal("T#8s", s.Steps[1].WarningTime);
    Equal("SimBegin", s.Branches[0].Type); Equal(2, s.Branches[0].Cardinality);
    Equal("Branch", s.Connections[3].From!.Kind); Equal(1, s.Connections[3].From!.Out);
    Equal("Jump", s.Connections[7].LinkType);
});
Test("actions, interlocks, supervision and conditions", () =>
{
    var s = Original().Inspection.Graphs[0].Sequences[0];
    Equal("S1", s.Steps[1].Actions[0].Event); Equal("N", s.Steps[1].Actions[0].Qualifier);
    Equal(true, s.Steps[1].Actions[0].Interlock); Equal(2, s.Steps[2].Actions.Count);
    True(s.Steps[1].Actions[0].Content.Xml.Contains("MotorA"));
    True(s.Steps[1].Interlocks[0].Content.Xml.Contains("GuardClosed"));
    True(s.Steps[1].Supervisions[0].Content.Xml.Contains("Travel timeout"));
    True(s.Transitions[0].Condition!.Xml.Contains("StartCommand"));
    Equal("FBD", s.Transitions[1].ProgrammingLanguage);
});
Test("all four branch kinds", () =>
{
    var d = Mutate(r => { foreach (var b in r.Descendants("Branch")) b.SetAttributeValue("Type", (string?)b.Attribute("Type") == "SimBegin" ? "AltBegin" : "AltEnd"); });
    True(!d.Inspection.HasErrors); Equal("AltBegin", d.Inspection.Graphs[0].Sequences[0].Branches[0].Type);
    True(HasChange(Original().CompareTo(d), "structure", "Type"));
});
Test("v6 namespaced export envelope and foreign lookalikes", () =>
{
    var d = GraphDocument.Parse(Fixture("unknown-envelope.xml"));
    True(!d.Inspection.HasErrors); Equal(1, d.Inspection.Graphs.Count); Equal(1, d.Inspection.Graphs[0].Sequences[0].Steps.Count);
    Code(d, "OpaqueEnvelope"); Code(d, "UnsupportedElement"); Code(d, "UnsupportedAttribute");
    True(d.Inspection.Unsupported.Any(u => u.Name == "{urn:fixture:future}Step"));
    True(d.Inspection.Unsupported.Any(u => u.Xml.Contains("CDATA")));
});
Test("unsupported namespace is never silently accepted", () =>
{
    var d = Parse("<Graph xmlns='urn:future:v7'><Sequence /></Graph>");
    Equal(0, d.Inspection.Graphs.Count); Code(d, "UnsupportedNamespace"); Code(d, "NoSupportedGraph");
});
Test("unsupported plain document reports no graph", () => Code(Parse("<Document><Step Number='1'/></Document>"), "NoSupportedGraph"));
Test("numeric duplicate identity and ambiguous reference", () =>
{
    var d = Mutate(r => First(r, "Steps").Add(new XElement("Step", new XAttribute("Number", "+001"), new XAttribute("Name", "Duplicate"))));
    Code(d, "DuplicateIdentity"); Code(d, "AmbiguousReference");
    True(!Original().CompareTo(d).MatchingReliable);
    True(HasChange(Original().CompareTo(d), "unresolved", "ambiguousIdentities"));
});
Test("duplicate transition and branch identities", () =>
{
    foreach (var kind in new[] { "Transition", "Branch" })
        Code(Mutate(r => First(r, kind).AddAfterSelf(new XElement(First(r, kind)))), "DuplicateIdentity");
});
Test("step and transition numbers are unique across sequences", () =>
{
    var d = Mutate(r => First(r, "Sequence").AddAfterSelf(new XElement(First(r, "Sequence"))));
    Code(d, "DuplicateIdentity"); Code(d, "AmbiguousReference"); Equal(2, d.Inspection.Graphs[0].Sequences.Count);
    True(!Original().CompareTo(d).MatchingReliable);
});
Test("kinds and charts have independent numbering; branch identities are sequence scoped", () =>
{
    True(!Original().Inspection.HasErrors);
    True(!Mutate(r => AddSecondSequence(r)).Inspection.HasErrors);
    var root = XDocument.Load(new MemoryStream(Fixture())).Root!;
    True(!Parse(new XElement("Document", new XElement(root), new XElement(root)).ToString()).Inspection.HasErrors);
});
Test("jumps resolve into another sequence and HTML links to the target", () =>
{
    var d = Mutate(r => { AddSecondSequence(r); First(r, "Sequence").Descendants("Connection").Last().Element("NodeTo")!.Element("StepRef")!.SetAttributeValue("Number", "11"); });
    True(!d.Inspection.HasErrors); True(d.ToHtml().Contains(System.Net.WebUtility.HtmlEncode("Sequence 2 · S11"))); True(d.ToHtml().Contains("href=\"#g1s2-step-0\""));
    True(!d.ToHtml().Contains("cannot be drawn"));
    File.WriteAllText(Path.Combine(generated, "cross-sequence.html"), d.ToHtml());
});
Test("direct references cannot cross sequence boundaries", () =>
{
    var d = Mutate(r => { AddSecondSequence(r); First(r, "StepRef").SetAttributeValue("Number", "11"); });
    Code(d, "CrossSequenceReference");
});
Test("broken references are checked by typed kind", () =>
{
    foreach (string kind in new[] { "StepRef", "TransitionRef", "BranchRef" })
        Code(Mutate(r => First(r, kind).SetAttributeValue("Number", "99")), "BrokenReference");
    Code(Mutate(r => First(r, "TransitionRef").SetAttributeValue("Number", "3")), "BrokenReference");
});
Test("IsMissing target is not a valid resolution", () => Code(Mutate(r => First(r, "Step").SetAttributeValue("IsMissing", "1")), "MissingReferenceTarget"));
Test("invalid and absent identity values stay unresolved", () =>
{
    Code(Mutate(r => First(r, "Step").Attribute("Number")!.Remove()), "MissingAttribute");
    foreach (string value in new[] { "x", "2147483648", "1.0" }) Code(Mutate(r => First(r, "Step").SetAttributeValue("Number", value)), "InvalidInteger");
    Code(Mutate(r => First(r, "StepRef").SetAttributeValue("Number", "x")), "InvalidInteger");
    Code(Mutate(r => First(r, "Step").SetAttributeValue("Init", "TRUE")), "InvalidBoolean");
});
Test("invalid enum values and language are reported", () =>
{
    Code(Mutate(r => First(r, "Action").SetAttributeValue("Qualifier", "Unknown")), "InvalidValue");
    Code(Mutate(r => First(r, "Action").SetAttributeValue("Event", "Unknown")), "InvalidValue");
    Code(Mutate(r => First(r, "Branch").SetAttributeValue("Type", "Unknown")), "InvalidValue");
    Code(Mutate(r => First(r, "Transition").SetAttributeValue("ProgrammingLanguage", "Mystery")), "InvalidValue");
    Code(Mutate(r => First(r, "LinkType").Value = "Unknown"), "InvalidValue");
});
Test("malformed structure and endpoints remain visible", () =>
{
    Code(Mutate(r => First(r, "Actions").Remove()), "InvalidStructure");
    Code(Mutate(r => First(r, "NodeTo").Add(new XElement("StepRef", new XAttribute("Number", "1")))), "InvalidEndpoint");
    Code(Mutate(r => First(r, "NodeTo").ReplaceNodes(new XElement("FutureRef"))), "InvalidEndpoint");
    Code(Mutate(r => First(r, "Transitions").AddFirst("unexpected text")), "UnexpectedText");
    Code(Mutate(r => { var steps = First(r, "Steps"); steps.Remove(); First(r, "Sequence").Add(steps); }), "InvalidOrder");
});
Test("duplicate containers do not hide identities", () => Code(Mutate(r => First(r, "Steps").AddAfterSelf(new XElement(First(r, "Steps")))), "DuplicateIdentity"));
Test("byte exact retention and defensive copying", () =>
{
    byte[] input = Fixture("unknown-envelope.xml"); byte[] expected = input.ToArray(); var d = GraphDocument.Parse(input);
    input[0] = 0; var copy = d.GetOriginalBytes(); copy[0] = 0;
    True(expected.SequenceEqual(d.GetOriginalBytes()));
    using var stream = new MemoryStream(); d.WriteOriginal(stream); True(expected.SequenceEqual(stream.ToArray()));
    Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), d.Inspection.SourceSha256);
    True(expected.SequenceEqual(Convert.FromBase64String(d.Inspection.OriginalSourceBase64)));
});
Test("UTF16 BOM and declaration preserved", () =>
{
    string xml = Encoding.UTF8.GetString(Fixture()).Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"");
    byte[] input = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)];
    var d = GraphDocument.Parse(input); True(!d.Inspection.HasErrors); True(input.SequenceEqual(d.GetOriginalBytes()));
});
Test("JSON is deterministic, complete and can restore source bytes", () =>
{
    var d = Original(); Equal(d.ToJson(), d.ToJson());
    using var json = JsonDocument.Parse(d.ToJson()); var root = json.RootElement;
    Equal("tia-graph-inspection.v1", root.GetProperty("format").GetString());
    Equal(3, root.GetProperty("graphs")[0].GetProperty("sequences")[0].GetProperty("steps").GetArrayLength());
    True(Fixture().SequenceEqual(Convert.FromBase64String(root.GetProperty("originalSourceBase64").GetString()!)));
});
Test("independent v1/v2 expected change set", () =>
{
    var diff = Original().CompareTo(GraphDocument.Parse(Fixture("sequence-v2.xml")));
    Equal(4, diff.Changes.Count); True(diff.MatchingReliable); True(diff.SourceBytesChanged);
    True(HasChange(diff, "conditions", "MaximumStepTime")); True(HasChange(diff, "actions", "Actions"));
    True(HasChange(diff, "conditions", "FlgNet")); True(HasChange(diff, "structure", "connections"));
    True(diff.Changes.Single(c => c.Field == "FlgNet").Before!.Contains("StartCommand"));
    True(diff.Changes.Single(c => c.Field == "FlgNet").After!.Contains("StartPermitted"));
    True(!diff.NativeQualified && !diff.BehavioralEquivalenceCertified);
});
Test("identical and formatting only comparisons", () =>
{
    var a = Original(); Equal(0, a.CompareTo(a).Changes.Count);
    var root = XDocument.Parse(Encoding.UTF8.GetString(Fixture()));
    var b = Parse(root.ToString(SaveOptions.DisableFormatting));
    Equal(0, a.CompareTo(b).Changes.Count); True(a.CompareTo(b).SourceBytesChanged);
});
Test("namespace prefixes and attribute order are not content changes", () =>
{
    var root = XDocument.Parse(Encoding.UTF8.GetString(Fixture())); XNamespace ns = GraphDocument.V6Namespace;
    foreach (var e in root.Descendants()) e.Name = ns + e.Name.LocalName;
    var a = Parse(root.ToString());
    root.Root!.Add(new XAttribute(XNamespace.Xmlns + "g", ns));
    foreach (var e in root.Descendants()) { var attrs = e.Attributes().Reverse().ToArray(); e.RemoveAttributes(); e.Add(attrs); }
    var b = Parse(root.ToString()); Equal(0, a.CompareTo(b).Changes.Count);
});
Test("added, removed and reordered nodes", () =>
{
    var removed = Mutate(r => r.Descendants("Step").Last().Remove());
    True(HasChange(Original().CompareTo(removed), "structure", "removed"));
    var added = Mutate(r => { var s = new XElement(First(r, "Step")); s.SetAttributeValue("Number", "4"); First(r, "Steps").Add(s); });
    True(HasChange(Original().CompareTo(added), "structure", "added"));
    var reorder = Mutate(r => { var s = First(r, "Step"); s.Remove(); First(r, "Steps").Add(s); });
    True(HasChange(Original().CompareTo(reorder), "structure", "documentOrder"));
});
Test("interlock, supervision and action ordering changes", () =>
{
    foreach (string name in new[] { "Interlock", "Supervision" })
    {
        var d = Mutate(r => First(r, name).SetAttributeValue("ProgrammingLanguage", "FBD"));
        True(HasChange(Original().CompareTo(d), "conditions", name + "s"));
    }
    var order = Mutate(r => { var parent = r.Descendants("Actions").Last(); parent.ReplaceNodes(parent.Elements().Reverse().ToArray()); });
    True(HasChange(Original().CompareTo(order), "actions", "Actions"));
});
Test("unknown elements, attributes, comments and mixed language envelope participate in diff", () =>
{
    foreach (Action<XElement> mutate in new Action<XElement>[] {
        r => First(r, "Step").SetAttributeValue("future", "changed"),
        r => First(r, "Steps").Add(new XElement("Future", "payload")),
        r => First(r, "Step").Add(new XComment("retained change")),
        r => First(r, "AlarmsSettings").SetAttributeValue("future", "changed"),
        r => First(r, "PreOperations").Add(new XElement("Future")) })
        True(Original().CompareTo(Mutate(mutate)).Changes.Any(c => c.Category == "retainedContent"));
    var a = GraphDocument.Parse(Fixture("unknown-envelope.xml"));
    var b = Mutate(r => First(r, "Engineering").SetAttributeValue("version", "changed"), "unknown-envelope.xml");
    True(HasChange(a.CompareTo(b), "retainedContent", "envelopeXml"));
});
Test("literal whitespace and xml:space are significant", () =>
{
    True(Original().CompareTo(Mutate(r => First(r, "MultiLanguageText").Value += " ")).Changes.Count > 0);
    var a = Parse("<Graph xml:space='preserve'> <Unknown/> </Graph>");
    var b = Parse("<Graph xml:space='preserve'>  <Unknown/> </Graph>");
    True(a.CompareTo(b).Changes.Count > 0);
});
Test("unknown content movement is reported alongside a known action change", () =>
{
    var a = Mutate(r => First(r, "Steps").AddFirst(new XElement("Future", "retain")));
    var b = Mutate(r => { First(r, "Steps").Add(new XElement("Future", "retain")); First(r, "Action").SetAttributeValue("Qualifier", "R"); });
    var diff = a.CompareTo(b);
    True(HasChange(diff, "actions", "Actions")); True(HasChange(diff, "retainedContent", "StepsOtherXml"));
});
Test("inherited whitespace is compared even alongside other changes", () =>
{
    var a = Mutate(r => r.SetAttributeValue(XNamespace.Xml + "space", "preserve"));
    var b = Mutate(r => { r.SetAttributeValue(XNamespace.Xml + "space", "preserve"); First(r, "Transition").SetAttributeValue("Name", "renamed"); First(First(r, "Transition"), "FlgNet").AddFirst("  "); });
    True(HasChange(a.CompareTo(b), "conditions", "FlgNet")); True(HasChange(a.CompareTo(b), "metadata", "Name"));
});
Test("mixed text content spacing survives normalization", () =>
{
    var a = Parse("<Graph><Future>text <b/> <i/></Future></Graph>");
    var b = Parse("<Graph><Future>text <b/>  <i/></Future></Graph>");
    True(a.CompareTo(b).Changes.Count > 0);
});
Test("HTML escapes all input and contains no active source content", () =>
{
    var d = GraphDocument.Parse(Fixture("unknown-envelope.xml")); string html = d.ToHtml();
    True(!html.Contains("<script", StringComparison.OrdinalIgnoreCase)); True(!html.Contains("<img", StringComparison.OrdinalIgnoreCase));
    True(html.Contains("&lt;script&gt;")); True(html.Contains("Content-Security-Policy"));
    True(html.Contains("data:application/xml;base64," + d.Inspection.OriginalSourceBase64));
    True(html.Contains("Standalone") || html.Contains("OFFLINE DOCUMENT REVIEW"));
    Equal(html, d.ToHtml());
    File.WriteAllText(Path.Combine(generated, "unknown.html"), html);
});
Test("HTML includes diagram, jumps, branch ports and all details", () =>
{
    string html = Original().ToHtml(); True(html.Contains("<svg")); True(html.Contains("edge jump"));
    True(html.Contains("Out=1")); True(html.Contains("Travel timeout")); True(html.Contains("GuardClosed"));
    True(html.Contains("MotorA")); True(html.Contains("Diagnostics"));
    File.WriteAllText(Path.Combine(generated, "sequence.html"), html);
});
Test("ambiguous identities do not produce a misleading diagram", () =>
{
    var d = Mutate(r => First(r, "Step").AddAfterSelf(new XElement(First(r, "Step"))));
    True(d.ToHtml().Contains("Diagram omitted"));
});
Test("unrelated end sentinels remain separate diagram nodes", () =>
{
    var d = Mutate(r => { var cs = r.Descendants("Connection").Take(2).ToArray(); foreach (var c in cs) First(c, "NodeTo").ReplaceNodes(new XElement("EndConnection")); });
    Equal(2, d.ToHtml().Split("<title>End connection</title>").Length - 1);
});
Test("bounded larger sequence remains completely inspectable", () =>
{
    var d = Mutate(r => { var steps = First(r, "Steps"); var sample = new XElement(First(r, "Step")); for (int n = 4; n <= 1000; n++) { var step = new XElement(sample); step.SetAttributeValue("Number", n); steps.Add(step); } });
    Equal(1000, d.Inspection.Graphs[0].Sequences[0].Steps.Count); True(!d.Inspection.HasErrors);
    True(d.ToHtml().Contains("Diagram omitted")); True(d.ToHtml().Contains("Step 1000"));
    Equal(0, d.CompareTo(d).Changes.Count);
});
Test("malformed XML and DTD/external entities refused", () =>
{
    Reject("InvalidXml", () => Parse("<Graph>"));
    Reject("InvalidXml", () => Parse("<!DOCTYPE Graph [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><Graph>&x;</Graph>"));
    Reject("InvalidXml", () => Parse("<!DOCTYPE Graph [<!ENTITY x 'expanded'>]><Graph>&x;</Graph>"));
});
Test("byte, depth and element budgets", () =>
{
    Reject("InputSize", () => GraphDocument.Parse([]));
    Reject("InputSize", () => GraphDocument.Parse(new byte[GraphDocument.MaxInputBytes + 1]));
    using var stream = new MemoryStream(new byte[GraphDocument.MaxInputBytes + 1]); Reject("InputSize", () => GraphDocument.Read(stream));
    Reject("XmlCapacity", () => Parse(string.Concat(Enumerable.Repeat("<x>", GraphDocument.MaxDepth + 2)) + string.Concat(Enumerable.Repeat("</x>", GraphDocument.MaxDepth + 2))));
    Reject("XmlCapacity", () => Parse("<Graph>" + string.Concat(Enumerable.Repeat("<x/>", GraphDocument.MaxElements)) + "</Graph>"));
    Reject("XmlCapacity", () => Parse("<" + new string('x', GraphDocument.MaxPathCharacters) + "/>"));
});
Test("CLI inspect, view, compare and preserve", () =>
{
    string input = Path.Combine(AppContext.BaseDirectory, "fixtures", "sequence-v1.xml");
    string second = Path.Combine(AppContext.BaseDirectory, "fixtures", "sequence-v2.xml");
    foreach (var command in new[] { "inspect", "view", "compare", "preserve" })
    {
        string path = Path.Combine(generated, command + (command == "view" ? ".html" : command == "preserve" ? ".xml" : ".json"));
        string[] cliArgs = command == "compare" ? [command, input, second, path] : [command, input, path];
        Equal(0, Cli.Run(cliArgs, new StringWriter(), new StringWriter())); True(new FileInfo(path).Length > 0);
        if (command == "preserve") True(File.ReadAllBytes(input).SequenceEqual(File.ReadAllBytes(path)));
        else if (command != "view") using (JsonDocument.Parse(File.ReadAllText(path))) { }
    }
});
Test("CLI refuses replacement, including input file", () =>
{
    string input = Path.Combine(generated, "protected.xml"); File.WriteAllBytes(input, Fixture());
    foreach (string command in new[] { "inspect", "view", "preserve" })
        Equal(1, Cli.Run([command, input, input], new StringWriter(), new StringWriter()));
    True(File.ReadAllBytes(input).SequenceEqual(Fixture()));
});
Test("CLI structural failure still produces reviewable evidence", () =>
{
    string input = Path.Combine(generated, "broken.xml"); File.WriteAllText(input, "<Graph/>");
    string output = Path.Combine(generated, "broken.json");
    Equal(3, Cli.Run(["inspect", input, output], new StringWriter(), new StringWriter()));
    using var report = JsonDocument.Parse(File.ReadAllText(output)); True(report.RootElement.GetProperty("hasErrors").GetBoolean());
});
Test("CLI usage, help, malformed XML and missing files", () =>
{
    Equal(2, Cli.Run([], new StringWriter(), new StringWriter()));
    Equal(0, Cli.Run(["--help"], new StringWriter(), new StringWriter()));
    Equal(1, Cli.Run(["inspect", Path.Combine(generated, "missing.xml"), Path.Combine(generated, "never.json")], new StringWriter(), new StringWriter()));
    string input = Path.Combine(generated, "malformed.xml"); File.WriteAllText(input, "<Graph>");
    string output = Path.Combine(generated, "not-written.json"); var errors = new StringWriter();
    Equal(1, Cli.Run(["inspect", input, output], new StringWriter(), errors)); True(!File.Exists(output)); True(errors.ToString().Contains("InvalidXml"));
});

NativeFixtureChecks.Register(Test, generated);
EditChecks.Register(Test);

if (args.Length == 2 && args[0] == "--schema-dir")
{
    Test("independent installed Siemens XSD validation of both authored fixtures", () =>
    {
        // Only an explicitly selected installed schema directory is loaded; no schema is bundled with this package.
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(null, Path.Combine(args[1], "SW.PlcBlocks.Graph_v6.xsd")); schemas.Compile();
        foreach (string name in new[] { "sequence-v1.xml", "sequence-v2.xml" })
        {
            var failures = new List<string>();
            XDocument.Load(new MemoryStream(Fixture(name))).Validate(schemas, (_, e) => failures.Add(e.Message));
            True(failures.Count == 0, name + ": " + string.Join(" | ", failures));
        }
    });
    Test("independent XSD validation of native graphs and known empty-module rejection", () =>
    {
        var schemas = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        schemas.Add(GraphDocument.V6Namespace, Path.Combine(args[1], "SW.PlcBlocks.Graph_v6.xsd")); schemas.Compile();
        foreach (string profile in new[] { "populated", "cross-sequence", "parallel", "alternative", "empty-condition" })
        {
            var source = XDocument.Load(new MemoryStream(Fixture("native-v21/" + profile + "/baseline.xml")));
            var chart = new XDocument(new XElement(source.Descendants(XName.Get("Graph", GraphDocument.V6Namespace)).Single()));
            var failures = new List<string>(); chart.Validate(schemas, (_, e) => failures.Add(e.Message));
            if (profile == "empty-condition") True(failures.Count > 0, "Expected native empty-module XSD mismatch");
            else True(failures.Count == 0, profile + ": " + string.Join(" | ", failures));
        }
        // Typed edits must keep a native chart schema-valid, including a new step, a contact series and new texts.
        var edited = GraphEditor.Apply(GraphDocument.Parse(Fixture("native-v21/alternative/baseline.xml")), [
            new GraphEdit("addStep", Number: 5, Name: "Wait", AfterTransition: 5),
            new GraphEdit("addTransition", Number: 6, Name: "Resume", FromStep: 5, ToStep: 1, Jump: true, Condition: [new("StartCommand"), new("ResetCommand", true)]),
            new GraphEdit("setStepComment", Number: 5, Lang: "en-US", Text: "Wait"), new GraphEdit("setTransitionCondition", Number: 1, Condition: [new("GuardClosed")]),
            new GraphEdit("removeStep", Number: 2), new GraphEdit("removeTransition", Number: 3)]);
        var editedChart = new XDocument(new XElement(XDocument.Load(new MemoryStream(edited)).Descendants(XName.Get("Graph", GraphDocument.V6Namespace)).Single()));
        var editFailures = new List<string>(); editedChart.Validate(schemas, (_, e) => editFailures.Add(e.Message));
        True(editFailures.Count == 0, "edited: " + string.Join(" | ", editFailures));
    });
}
else if (args.Length != 0) { Console.Error.WriteLine("Usage: [--schema-dir installed-schema-directory]"); return 2; }

int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed. Generated review artifacts: {generated}");
Console.WriteLine("Native evidence checks cover the recorded V21 fixtures; arbitrary input documents are not certified.");
return failed == 0 ? 0 : 1;
