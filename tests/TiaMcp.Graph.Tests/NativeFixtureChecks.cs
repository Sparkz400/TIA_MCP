using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using TiaMcp.Graph;

internal static class NativeFixtureChecks
{
    public static void Register(Action<string, Action> test, string generated)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "native-v21");
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        test("native evidence artifact integrity and owned process receipts", () =>
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            foreach (var profile in manifest.RootElement.GetProperty("profiles").EnumerateArray())
            {
                string dir = Path.Combine(root, profile.GetProperty("directory").GetString()!);
                foreach (var artifact in profile.GetProperty("artifacts").EnumerateArray())
                {
                    string file = artifact.GetProperty("file").GetString()!;
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, file));
                    Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == artifact.GetProperty("sha256").GetString(), "Changed native evidence: " + file);
                }
                bool complete = profile.GetProperty("outcome").GetString() == "completed";
                using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, complete ? "result.json" : "failure.json")));
                Require(result.RootElement.GetProperty("completed").GetBoolean() == complete, "Unexpected native run outcome");
                bool duplicate = profile.GetProperty("outcome").GetString() == "duplicate-number-rejected";
                if (!complete) Require(result.RootElement.GetProperty("stage").GetString() == (duplicate ? "import-authored-seed" : "no-op-import"), "Unexpected native failure stage");
                var ids = new HashSet<int>();
                foreach (string phase in complete ? new[] { "create", "reopen", "recovery" } : new[] { "create" })
                {
                    using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "process-" + phase + ".json")));
                    Require(ids.Add(receipt.RootElement.GetProperty("processId").GetInt32()), "Fresh-process gate reused a process");
                    Require(receipt.RootElement.GetProperty("ownership").GetString()!.Contains("never attached"), "Ownership receipt missing");
                }
                foreach (string phase in complete ? new[] { "imported", "roundtrip", "reopened", "recovered" } : duplicate ? Array.Empty<string>() : new[] { "imported" })
                {
                    using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "compile-" + phase + ".json")));
                    Require(receipt.RootElement.GetProperty("errors").GetInt32() == 0, "Native compiler errors: " + phase);
                }
            }
        });
        test("native empty modules are readable and explicitly diagnosed", () =>
        {
            var document = GraphDocument.Load(Path.Combine(root, "empty-condition", "baseline.xml"));
            Require(!document.Inspection.HasErrors, "Native structure rejected");
            Require(document.Inspection.Diagnostics.Count(d => d.Code == "EmptyNativeCondition") == 2, "Missing native empty-module warnings");
            var graph = document.Inspection.Graphs.Single();
            Require(graph.Namespace == GraphDocument.V6Namespace, "Unexpected native namespace");
            var sequence = graph.Sequences.Single();
            Require(sequence.Steps.Count == 1 && sequence.Transitions.Count == 1 && sequence.Branches.Count == 0 && sequence.Connections.Count == 2, "Unexpected minimal topology");
            Require(sequence.Steps[0].Number == 1 && sequence.Steps[0].Name == "Idle" && sequence.Steps[0].Init == true, "Initial step differs");
            Require(sequence.Transitions[0].Name == "Next" && sequence.Transitions[0].Condition!.Xml.Contains("TrCoil"), "Transition condition absent");
            Require(sequence.Connections.Last().LinkType == "Jump" && sequence.Connections.Last().To!.Number == 1, "Jump target differs");
        });
        test("native rich graph retains independent branch, action and condition expectations", () =>
        {
            var document = GraphDocument.Load(Path.Combine(root, "parallel", "baseline.xml"));
            Require(!document.Inspection.HasErrors, string.Join("; ", document.Inspection.Diagnostics.Where(d => d.Severity == "error").Select(d => d.Message)));
            var sequence = document.Inspection.Graphs.Single().Sequences.Single();
            Require(sequence.Steps.Count == 3 && sequence.Transitions.Count == 2 && sequence.Branches.Count == 2 && sequence.Connections.Count == 8, "Unexpected parallel topology");
            Require(sequence.Branches.Select(b => b.Type).SequenceEqual(new[] { "SimBegin", "SimEnd" }) && sequence.Branches.All(b => b.Cardinality == 2), "Branch shape differs");
            var run = sequence.Steps.Single(s => s.Number == 2);
            Require(run.Actions.Count == 1 && run.Actions[0].Qualifier == "N" && run.Actions[0].Event == "S1" && run.Actions[0].Interlock == true && run.Actions[0].Content.Xml.Contains("#MotorA"), "Action differs");
            Require(run.Interlocks.Single().Content.Xml.Contains("GuardClosed"), "Interlock operand absent");
            Require(run.Supervisions.Single().Content.Xml.Contains("SupervisionTrip"), "Supervision operand absent");
            Require(sequence.Transitions[0].Condition!.Xml.Contains("StartCommand") && sequence.Transitions[1].Condition!.Xml.Contains("ResetCommand"), "Transition operands differ");
            Require(sequence.Title!.Contains("Parallel cycle") && sequence.Title.Contains("Paralleler Ablauf"), "Multilingual title differs");
            File.WriteAllText(Path.Combine(generated, "native-parallel.html"), document.ToHtml());
            File.WriteAllText(Path.Combine(generated, "native-parallel.json"), document.ToJson());
        });
        test("native roundtrip, fresh reopen and recovery preserve the graph", () =>
        {
            foreach (string profile in new[] { "populated", "cross-sequence", "parallel", "alternative" })
            {
                var baseline = GraphDocument.Load(Path.Combine(root, profile, "baseline.xml"));
                foreach (string phase in new[] { "roundtrip", "reopened", "recovered" })
                {
                    var after = GraphDocument.Load(Path.Combine(root, profile, phase + ".xml"));
                    var comparison = baseline.CompareTo(after);
                    Require(comparison.MatchingReliable, "Unreliable native alignment");
                    Require(comparison.Changes.All(c => c.Kind == "Document" && c.Field == "envelopeXml"), profile + "/" + phase + " changed GRAPH content");
                    // Independent XML comparison, separate from the package's categorized comparison.
                    XElement Chart(GraphDocument d) => XDocument.Load(new MemoryStream(d.GetOriginalBytes())).Descendants(XName.Get("Graph", GraphDocument.V6Namespace)).Single();
                    Require(XNode.DeepEquals(Chart(baseline), Chart(after)), "Native graph subtree differs");
                    XDocument WithoutExportTimestamp(GraphDocument d)
                    {
                        var xml = XDocument.Load(new MemoryStream(d.GetOriginalBytes()));
                        xml.Root!.Element("DocumentInfo")!.Element("Created")!.Remove();
                        return xml;
                    }
                    Require(XNode.DeepEquals(WithoutExportTimestamp(baseline), WithoutExportTimestamp(after)), "Native document changed beyond the export timestamp");
                    File.WriteAllText(Path.Combine(generated, profile + "-" + phase + "-comparison.json"), comparison.ToJson());
                }
            }
        });
        test("native alternative branches retain split, merge and jump topology", () =>
        {
            var document = GraphDocument.Load(Path.Combine(root, "alternative", "baseline.xml"));
            Require(!document.Inspection.HasErrors, "Native alternative graph rejected");
            var sequence = document.Inspection.Graphs.Single().Sequences.Single();
            Require(sequence.Steps.Count == 4 && sequence.Transitions.Count == 5 && sequence.Connections.Count == 12, "Alternative topology differs");
            Require(sequence.Branches.Select(b => b.Type).SequenceEqual(new[] { "AltBegin", "AltEnd" }), "Alternative branch kinds differ");
            Require(sequence.Connections[0].From!.Kind == "Step" && sequence.Connections[0].To!.Kind == "Branch", "Alternative split differs");
            Require(sequence.Connections.Last().LinkType == "Jump" && sequence.Connections.Last().To!.Number == 1, "Alternative loop differs");
            File.WriteAllText(Path.Combine(generated, "native-alternative.html"), document.ToHtml());
        });
        test("native cross-sequence jumps and duplicate rejection agree with reader scope", () =>
        {
            var document = GraphDocument.Load(Path.Combine(root, "cross-sequence", "baseline.xml"));
            Require(!document.Inspection.HasErrors, "Cross-sequence native graph rejected");
            var sequences = document.Inspection.Graphs.Single().Sequences;
            Require(sequences.Count == 2, "Expected two native sequences");
            Require(sequences[0].Steps.Single().Number == 1 && sequences[1].Steps.Single().Number == 11, "Native numbers differ");
            Require(sequences[0].Connections.Last().To!.Number == 11 && sequences[1].Connections.Last().To!.Number == 1, "Native jump targets differ");
            Require(document.ToHtml().Contains("href=\"#g1s2-step-0\""), "Native cross-sequence navigation absent");
            File.WriteAllText(Path.Combine(generated, "native-cross-sequence.html"), document.ToHtml());
            var duplicate = GraphDocument.Load(Path.Combine(root, "duplicate-number", "seed.xml"));
            Require(duplicate.Inspection.Diagnostics.Any(d => d.Code == "DuplicateIdentity"), "Cross-sequence duplicate accepted");
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "duplicate-number", "failure.json")));
            Require(receipt.RootElement.GetProperty("message").GetString()!.Contains("transition number 1 exists twice"), "Native duplicate rejection missing");
        });
        test("native bytes and embedded language XML remain available without certification", () =>
        {
            var path = Path.Combine(root, "parallel", "baseline.xml");
            var bytes = File.ReadAllBytes(path); var document = GraphDocument.Parse(bytes);
            Require(document.GetOriginalBytes().SequenceEqual(bytes), "Native source changed");
            Require(Convert.FromBase64String(document.Inspection.OriginalSourceBase64).SequenceEqual(bytes), "Native JSON retention differs");
            Require(!document.Inspection.NativeQualified && !document.Inspection.FullSchemaValidated, "Individual document was incorrectly certified");
            Require(document.Inspection.Unsupported.Any(u => u.Name.EndsWith("FlgNet", StringComparison.Ordinal)), "Embedded XML boundary not reported");
        });
    }
}
