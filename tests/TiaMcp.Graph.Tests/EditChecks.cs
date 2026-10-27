using System.Text;
using TiaMcp.Graph;

internal static class EditChecks
{
    public static void Register(Action<string, Action> test)
    {
        static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static GraphDocument Edit(string fixture, params GraphEdit[] edits) => GraphDocument.Parse(GraphEditor.Apply(GraphDocument.Parse(Fixture(fixture)), edits));
        static string Text(GraphDocument document) => Encoding.UTF8.GetString(document.GetOriginalBytes());
        static void Refuse(string code, string fixture, params GraphEdit[] edits)
        {
            try { GraphEditor.Apply(GraphDocument.Parse(Fixture(fixture)), edits); }
            catch (GraphException error) { Require(error.Code == code, $"Expected {code}; got {error.Code}: {error.Message}"); return; }
            throw new Exception("Expected refusal " + code);
        }
        // The single changed region of a one-splice edit: (removed source text, inserted text).
        static (string Removed, string Inserted) Change(string before, string after)
        {
            int prefix = 0, suffix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
            return (before[prefix..^suffix], after[prefix..^suffix]);
        }
        const string Alternative = "native-v21/alternative/baseline.xml", Populated = "native-v21/populated/baseline.xml", Sequence = "sequence-v1.xml";
        GraphContact[] start = [new("StartCommand")];

        test("edit: rename step and transition changes only names and their generated FB members", () =>
        {
            var source = GraphDocument.Parse(Fixture(Alternative)); string before = Text(source);
            var result = Edit(Alternative, new GraphEdit("renameStep", Number: 4, Name: "Merge"), new GraphEdit("renameTransition", Number: 5, Name: "Again & <more>"));
            Require(before.Split("Name=\"Join\"").Length == 3 && before.Split("Name=\"Restart\"").Length == 3, "Fixture names occur as element and generated member");
            Require(Text(result) == before.Replace("Name=\"Join\"", "Name=\"Merge\"").Replace("Name=\"Restart\"", "Name=\"Again &amp; &lt;more&gt;\""), "Unedited bytes changed");
            Require(result.GetOriginalBytes().AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM lost");
            var sequence = result.Inspection.Graphs[0].Sequences[0];
            Require(sequence.Steps[3].Name == "Merge" && sequence.Transitions[4].Name == "Again & <more>", "Renamed identities not read back");
            var diff = source.CompareTo(result);
            Require(diff.MatchingReliable && diff.Changes.Count(c => c.Category == "metadata" && c.Field == "Name") == 2, "Comparison does not show both renames");
        });
        test("edit: add a step and transition after an existing transition with a contact conjunction", () =>
        {
            var source = GraphDocument.Parse(Fixture(Alternative)); string before = Text(source);
            var result = Edit(Alternative, new GraphEdit("addStep", Number: 5, Name: "Wait", AfterTransition: 5),
                new GraphEdit("addTransition", Number: 6, Name: "Resume", FromStep: 5, ToStep: 1, Jump: true,
                    Condition: [new("StartCommand"), new("ResetCommand", true)]));
            var sequence = result.Inspection.Graphs[0].Sequences[0];
            Require(sequence.Steps.Count == 5 && sequence.Steps[4] is { Number: 5, Name: "Wait", Init: false }, "Added step missing");
            Require(sequence.Transitions[5] is { Number: 6, Name: "Resume", ProgrammingLanguage: "LAD" }, "Added transition missing");
            string condition = sequence.Transitions[5].Condition!.Xml;
            Require(condition.Contains("<Component Name=\"StartCommand\" />") && condition.Contains("<Component Name=\"ResetCommand\" />")
                && condition.Split("Name=\"Contact\"").Length == 3 && condition.Split("<Negated Name=\"operand\" />").Length == 2 && condition.Contains("Name=\"TrCoil\" UId=\"25\""), "Contact series not built");
            Require(sequence.Connections.Any(c => c.From is { Kind: "Transition", Number: 5 } && c.To is { Kind: "Step", Number: 5 } && c.LinkType == "Direct")
                && sequence.Connections.Any(c => c.From is { Kind: "Step", Number: 5 } && c.To is { Kind: "Transition", Number: 6 })
                && sequence.Connections.Any(c => c.From is { Kind: "Transition", Number: 6 } && c.To is { Kind: "Step", Number: 1 } && c.LinkType == "Jump"), "Connections not spliced");
            string text = Text(result);
            Require(text.StartsWith(before[..(before.LastIndexOf("</Step>", StringComparison.Ordinal) + "</Step>".Length)]), "Bytes before the edit changed");
            Require(text.EndsWith(before[before.IndexOf("</Connections>", StringComparison.Ordinal)..]), "Bytes after the edit changed");
            foreach (var step in source.Inspection.Graphs[0].Sequences[0].Steps)
                Require(text.Contains(before.Substring(before.IndexOf($"<Step Number=\"{step.Number}\"", StringComparison.Ordinal), 200)), "An unrelated step changed");
            var diff = source.CompareTo(result);
            Require(diff.Changes.Any(c => c.Identity.EndsWith("Step:5") && c.Field == "added") && diff.Changes.Any(c => c.Identity.EndsWith("Transition:6") && c.Field == "added"), "Comparison misses additions");
        });
        test("edit: adding and removing the same chain restores the source structure", () =>
        {
            var source = GraphDocument.Parse(Fixture(Populated));
            var result = Edit(Populated, new GraphEdit("addStep", Number: 2, Name: "Wait", AfterTransition: 1),
                new GraphEdit("addTransition", Number: 2, Name: "Back", FromStep: 2, ToStep: 1, Jump: true, Condition: start),
                new GraphEdit("removeStep", Number: 2), new GraphEdit("removeTransition", Number: 2));
            Require(source.CompareTo(result).Changes.Count == 0, "Add/remove roundtrip changed structure");
        });
        test("edit: remove a step and transition from an alternative branch with their generated members", () =>
        {
            var result = Edit(Alternative, new GraphEdit("removeStep", Number: 2), new GraphEdit("removeTransition", Number: 3));
            var sequence = result.Inspection.Graphs[0].Sequences[0];
            Require(sequence.Steps.Select(s => s.Number).SequenceEqual(new int?[] { 1, 3, 4 }) && sequence.Transitions.All(t => t.Number != 3), "Removal missing");
            Require(sequence.Connections.Any(c => c.From is { Kind: "Transition", Number: 1 } && c.To is { Kind: "Branch", Number: 2, In: 0 }), "Chain not spliced into the branch");
            string text = Text(result);
            Require(!text.Contains("Name=\"Run A\"") && !text.Contains("Name=\"DoneA\"") && text.Contains("<Member Name=\"Run B\" Datatype=\"G7_StepPlus_V6\""), "Generated members not removed exactly");
        });
        test("edit: step comment insertion and sequence title replacement are local", () =>
        {
            string before = Text(GraphDocument.Parse(Fixture(Populated)));
            var commented = Edit(Populated, new GraphEdit("setStepComment", Number: 1, Lang: "en-US", Text: "Wait for start"));
            var (removed, inserted) = Change(before, Text(commented));
            Require(removed.Length == 0 && inserted.Contains("Comment><MultiLanguageText Lang=\"en-US\">Wait for start</MultiLanguageText></Comment>"), "Comment not a pure insertion");
            Require(commented.Inspection.Graphs[0].Sequences[0].Steps[0].Content.Xml.Contains("Wait for start"), "Comment not read back");
            var titled = Edit(Populated, new GraphEdit("setSequenceTitle", Sequence: 1, Lang: "en-US", Text: "Main cycle"));
            (removed, inserted) = Change(before, Text(titled));
            Require(removed == "Parallel" && inserted == "Main", $"Title change not local: '{removed}' -> '{inserted}'");
            var added = Edit(Sequence, new GraphEdit("setSequenceTitle", Sequence: 1, Lang: "fr-FR", Text: "Cycle"));
            Require(added.Inspection.Graphs[0].Sequences[0].Title!.Contains("[fr-FR] Cycle") && added.Inspection.Graphs[0].Sequences[0].Title!.Contains("[de-DE]"), "Language not appended");
        });
        test("edit: replace a LAD transition condition with a normally closed contact", () =>
        {
            var source = GraphDocument.Parse(Fixture(Populated));
            var result = Edit(Populated, new GraphEdit("setTransitionCondition", Number: 1, Condition: [new("GuardClosed", true)]));
            string condition = result.Inspection.Graphs[0].Sequences[0].Transitions[0].Condition!.Xml;
            Require(condition.Contains("GuardClosed") && condition.Contains("<Negated Name=\"operand\" />") && !condition.Contains("StartCommand"), "Condition not replaced");
            Require(source.CompareTo(result).Changes.Single().Field == "FlgNet", "Only the condition may change");
        });
        test("edit: an identity rename reproduces every fixture byte-exactly, including UTF-16", () =>
        {
            int edited = 0;
            var inputs = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.xml", SearchOption.AllDirectories).Select(File.ReadAllBytes).ToList();
            string utf8 = Encoding.UTF8.GetString(Fixture(Sequence)).Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"");
            inputs.Add([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(utf8)]);
            foreach (var bytes in inputs)
            {
                var source = GraphDocument.Parse(bytes);
                if (source.Inspection.HasErrors || source.Inspection.Graphs.Count != 1) continue;
                var step = source.Inspection.Graphs[0].Sequences[0].Steps[0];
                try { Require(GraphEditor.Apply(source, [new GraphEdit("renameStep", Number: step.Number, Name: step.Name)]).AsSpan().SequenceEqual(bytes), "Identity edit changed bytes"); edited++; }
                catch (GraphException error) when (error.Code == "GraphEditSource") { }
            }
            Require(edited >= 20, $"Only {edited} fixtures were editable");
        });
        test("edit: refusals leave no result", () =>
        {
            Refuse("GraphEditDuplicate", Alternative, new GraphEdit("addStep", Number: 1, Name: "Again", AfterTransition: 5));
            Refuse("GraphEditDuplicate", Alternative, new GraphEdit("addTransition", Number: 1, Name: "Again", FromStep: 4, ToStep: 1, Condition: start));
            Refuse("GraphEditDuplicate", Sequence, new GraphEdit("renameStep", Number: 2, Name: "idle"));
            Refuse("GraphEditDuplicate", Populated, new GraphEdit("renameStep", Number: 1, Name: "StartCommand"));
            Refuse("GraphEditUnreachable", Alternative, new GraphEdit("addStep", Number: 5, Name: "Detour", AfterTransition: 1));
            Refuse("GraphEditDangling", Alternative, new GraphEdit("addStep", Number: 5, Name: "End", AfterTransition: 5), new GraphEdit("removeStep", Number: 5));
            Refuse("GraphEditTopology", Alternative, new GraphEdit("removeStep", Number: 4));
            Refuse("GraphEditTopology", Alternative, new GraphEdit("addTransition", Number: 6, Name: "Twice", FromStep: 4, ToStep: 1, Jump: true, Condition: start));
            Refuse("GraphEditTopology", Alternative, new GraphEdit("removeStep", Number: 1));
            Refuse("GraphEditIdentity", Alternative, new GraphEdit("renameStep", Number: 99, Name: "Ghost"));
            Refuse("GraphEditIdentity", Alternative, new GraphEdit("addStep", Number: 5, Name: "Ghost", AfterTransition: 99));
            Refuse("GraphEditIdentity", Alternative, new GraphEdit("setSequenceTitle", Sequence: 2, Lang: "en-US", Text: "x"));
            Refuse("GraphEditLimit", Alternative, Enumerable.Repeat(new GraphEdit("renameStep", Number: 1, Name: "Idle"), GraphEditor.MaxOperations + 1).ToArray());
            Refuse("GraphEditLimit", Alternative);
            Refuse("GraphEditCondition", Alternative, new GraphEdit("setTransitionCondition", Number: 1, Condition: [new("RT_DATA")]));
            Refuse("GraphEditCondition", Alternative, new GraphEdit("setTransitionCondition", Number: 1, Condition: [new("NotDeclared")]));
            Refuse("GraphEditCondition", Sequence, new GraphEdit("setTransitionCondition", Number: 1, Condition: [new("StartCommand")]));
            Refuse("GraphEditCondition", Sequence, new GraphEdit("setTransitionCondition", Number: 2, Condition: start));
            Refuse("GraphEditInput", Alternative, new GraphEdit("addStep", Number: 5, Name: "Wait", AfterTransition: 5, ToStep: 1));
            Refuse("GraphEditInput", Alternative, new GraphEdit("renameStep", Number: 1, Name: " padded"));
            Refuse("GraphEditInput", Alternative, new GraphEdit("setStepComment", Number: 1, Lang: "english", Text: "x"));
            Refuse("GraphEditInput", Alternative, new GraphEdit("delete", Number: 1));
            var broken = Encoding.UTF8.GetString(Fixture(Sequence)).Replace("Number=\"3\" Name=\"Run B\"", "Number=\"2\" Name=\"Run B\"");
            try { GraphEditor.Apply(GraphDocument.Parse(Encoding.UTF8.GetBytes(broken)), [new GraphEdit("renameStep", Number: 1, Name: "x")]); throw new Exception("Source with reader errors was edited"); }
            catch (GraphException error) { Require(error.Code == "GraphEditSource", error.Code); }
        });
        test("edit: strict operation JSON", () =>
        {
            var edits = GraphEditor.Parse("""[{"op":"addTransition","number":6,"name":"Go","fromStep":4,"toStep":1,"jump":true,"condition":[{"operand":"StartCommand","negated":true}]}]""");
            Require(edits.Single() is { Op: "addTransition", Jump: true } edit && edit.Condition!.Single() == new GraphContact("StartCommand", true), "JSON not mapped");
            foreach (string json in new[] { """[{"op":"renameStep","number":1,"name":"x","password":"secret"}]""", """[{"number":1}]""", """{"op":"renameStep"}""",
                """[{"op":"setTransitionCondition","number":1,"condition":[{"negated":true}]}]""", "[" + new string(' ', GraphEditor.MaxJsonCharacters) + "]" })
                try { GraphEditor.Parse(json); throw new Exception("Accepted " + json[..Math.Min(60, json.Length)]); }
                catch (GraphException error) { Require(error.Code is "GraphEditInput" or "GraphEditLimit", error.Code); }
        });
    }
}
