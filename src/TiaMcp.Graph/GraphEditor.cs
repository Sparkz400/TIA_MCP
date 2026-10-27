using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Graph;

/// <summary>A normally open (or, when negated, normally closed) LAD contact on an exact Bool block-interface member.</summary>
public sealed record GraphContact(string Operand, bool Negated = false);

/// <summary>One typed edit. Steps and transitions are addressed by their exact chart-wide number, sequences by their 1-based ordinal.</summary>
public sealed record GraphEdit(string Op, int? Number = null, string? Name = null, int? AfterTransition = null, int? FromStep = null,
    int? ToStep = null, bool? Jump = null, IReadOnlyList<GraphContact>? Condition = null, int? Sequence = null, string? Lang = null, string? Text = null);

/// <summary>Applies typed edits by splicing the source text, so every unedited byte stays identical. Each result must pass the reader and
/// the sequence checks below or nothing is returned. It writes no project; native import stays the separate graph_import change.</summary>
public static class GraphEditor
{
    public const int MaxOperations = 32, MaxContacts = 16, MaxNameLength = 128, MaxTextLength = 4096, MaxJsonCharacters = 256 * 1024;
    private const string StepType = "G7_StepPlus_V6", TransitionType = "G7_TransitionPlus_V6";
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["addStep"] = ["number", "name", "afterTransition"],
        ["addTransition"] = ["number", "name", "fromStep", "toStep", "condition", "jump?"],
        ["removeStep"] = ["number"], ["removeTransition"] = ["number"],
        ["renameStep"] = ["number", "name"], ["renameTransition"] = ["number", "name"],
        ["setStepComment"] = ["number", "lang", "text"], ["setSequenceTitle"] = ["sequence", "lang", "text"],
        ["setTransitionCondition"] = ["number", "condition"]
    };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 8
    };

    public static IReadOnlyList<GraphEdit> Parse(string json)
    {
        Require(json is { Length: > 0 and <= MaxJsonCharacters }, "GraphEditLimit", $"Operations JSON must contain 1 through {MaxJsonCharacters} characters.");
        try { return JsonSerializer.Deserialize<GraphEdit[]>(json, Json) ?? throw new GraphException("GraphEditInput", "operations must be an array."); }
        catch (JsonException error) { throw new GraphException("GraphEditInput", "Invalid operations: " + error.Message); }
    }

    public static byte[] Apply(GraphDocument source, IReadOnlyList<GraphEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        Require(edits is { Count: > 0 and <= MaxOperations }, "GraphEditLimit", $"Supply 1 through {MaxOperations} operations.");
        foreach (var edit in edits) CheckFields(edit);
        Require(!source.Inspection.HasErrors && source.Inspection.Graphs.Count == 1, "GraphEditSource", "Only a document with exactly one GRAPH chart and no reader errors can be edited.");
        Validate(source, true);
        byte[] original = source.GetOriginalBytes();
        Encoding encoding = new UTF8Encoding(false, true); int preamble = 0;
        foreach (var candidate in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
            if (original.AsSpan().StartsWith(candidate.GetPreamble())) { encoding = candidate; preamble = candidate.GetPreamble().Length; break; }
        string text;
        try { text = encoding.GetString(original, preamble, original.Length - preamble); }
        catch (DecoderFallbackException) { throw new GraphException("GraphEditEncoding", "Only UTF-8 and UTF-16 documents can be edited byte-exactly."); }
        Require(original.AsSpan(preamble).SequenceEqual(encoding.GetBytes(text)), "GraphEditEncoding", "The source does not round-trip through its encoding.");
        for (int i = 0; i < edits.Count; i++)
        {
            var working = new Working(text);
            if (i == 0) Require(working.Declared is null || working.Declared.Equals(encoding is UTF8Encoding ? "utf-8" : "utf-16", StringComparison.OrdinalIgnoreCase),
                "GraphEditEncoding", "The XML declaration names an encoding other than the document's UTF-8/UTF-16 bytes.");
            try { Apply(working, edits[i]); text = working.Result(); }
            catch (GraphException error) { throw new GraphException(error.Code, $"Operation {i + 1} ({edits[i].Op}): {error.Message}"); }
        }
        byte[] result = [.. original.AsSpan(0, preamble), .. encoding.GetBytes(text)];
        var edited = GraphDocument.Parse(result);
        var problem = edited.Inspection.Diagnostics.FirstOrDefault(d => d.Severity == "error");
        Require(problem is null && edited.Inspection.Graphs.Count == 1, "GraphEditInvalid", $"The edited document fails the reader: {problem?.Code} at {problem?.Path}: {problem?.Message}");
        Validate(edited, false);
        return result;
    }

    private static void CheckFields(GraphEdit edit)
    {
        Require(edit is not null, "GraphEditInput", "Operations must not be null.");
        Require(edit.Op is not null && Fields.ContainsKey(edit.Op), "GraphEditInput", "Unknown op. Use " + string.Join(", ", Fields.Keys) + ".");
        var fields = Fields[edit.Op];
        foreach (var (field, set) in new[] { ("number", edit.Number is not null), ("name", edit.Name is not null), ("afterTransition", edit.AfterTransition is not null),
            ("fromStep", edit.FromStep is not null), ("toStep", edit.ToStep is not null), ("jump", edit.Jump is not null), ("condition", edit.Condition is not null),
            ("sequence", edit.Sequence is not null), ("lang", edit.Lang is not null), ("text", edit.Text is not null) })
            Require(set ? fields.Contains(field) || fields.Contains(field + "?") : !fields.Contains(field), "GraphEditInput", $"{edit.Op} {(set ? "does not accept" : "requires")} {field}.");
        Require(new[] { edit.Number, edit.AfterTransition, edit.FromStep, edit.ToStep, edit.Sequence }.All(n => n is null or > 0), "GraphEditInput", "Numbers and ordinals must be positive.");
        if (edit.Name is { } name)
            Require(name.Length is > 0 and <= MaxNameLength && name.Trim() == name && !name.Any(char.IsControl) && ValidXml(name),
                "GraphEditInput", $"Names must be 1..{MaxNameLength} characters without surrounding spaces or control characters.");
        if (edit.Lang is { } lang) Require(Regex.IsMatch(lang, "^[a-z]{2,3}-[A-Z]{2}$"), "GraphEditInput", "lang must be a culture such as en-US.");
        if (edit.Text is { } text)
            Require(text.Length <= MaxTextLength && !text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')) && ValidXml(text),
                "GraphEditInput", $"text must be at most {MaxTextLength} characters without control characters other than tab and line breaks.");
        if (edit.Condition is { } condition)
            Require(condition.Count is > 0 and <= MaxContacts && condition.All(c => c is { Operand.Length: > 0 and <= MaxNameLength }),
                "GraphEditInput", $"condition must hold 1..{MaxContacts} contacts with an operand name.");
    }

    private static void Apply(Working w, GraphEdit edit)
    {
        int number = edit.Number ?? edit.Sequence ?? 0;
        switch (edit.Op)
        {
            case "addStep":
            {
                Require(!w.Items("Step").Any(s => Number(s) == number), "GraphEditDuplicate", $"Step {number} already exists.");
                var transition = w.Find("Transition", edit.AfterTransition!.Value);
                var sequence = transition.Parent!.Parent!;
                var outgoing = w.Connections.Where(c => w.Is(c, "NodeFrom", "Transition", edit.AfterTransition.Value)).ToArray();
                Require(outgoing.Length <= 1, "GraphEditTopology", $"Transition {edit.AfterTransition} has more than one successor.");
                // Times are the V21 export defaults; the empty LAD modules are the shape the recorded seeds imported (TIA re-exports them without FlgNet).
                w.Append(sequence.Element(w.Ns + "Steps")!, new XElement("Step", new XAttribute("Number", number), new XAttribute("Init", "false"),
                    new XAttribute("Name", edit.Name!), new XAttribute("MaximumStepTime", "T#10S"), new XAttribute("WarningTime", "T#7S"), new XElement("Actions"),
                    new XElement("Supervisions", EmptyModule("Supervision")), new XElement("Interlocks", EmptyModule("Interlock"))));
                if (outgoing.Length == 0) w.Append(sequence.Element(w.Ns + "Connections")!, Connection("Transition", edit.AfterTransition.Value, "Step", number, false));
                else
                {
                    w.Replace(outgoing[0].Element(w.Ns + "NodeTo")!, new XElement("NodeTo", new XElement("StepRef", new XAttribute("Number", number))));
                    w.Replace(outgoing[0].Element(w.Ns + "LinkType")!, new XElement("LinkType", "Direct"));
                }
                break;
            }
            case "addTransition":
            {
                Require(!w.Items("Transition").Any(t => Number(t) == number), "GraphEditDuplicate", $"Transition {number} already exists.");
                var from = w.Find("Step", edit.FromStep!.Value); _ = w.Find("Step", edit.ToStep!.Value);
                Require(!w.Connections.Any(c => w.Is(c, "NodeFrom", "Step", edit.FromStep.Value)), "GraphEditTopology",
                    $"Step {edit.FromStep} already has a successor; alternative branches are not created.");
                var sequence = from.Parent!.Parent!;
                w.Append(sequence.Element(w.Ns + "Transitions")!, new XElement("Transition", new XAttribute("IsMissing", "false"), new XAttribute("Name", edit.Name!),
                    new XAttribute("Number", number), new XAttribute("ProgrammingLanguage", "LAD"), Condition(w, edit.Condition!)));
                w.Append(sequence.Element(w.Ns + "Connections")!, Connection("Step", edit.FromStep.Value, "Transition", number, false),
                    Connection("Transition", number, "Step", edit.ToStep.Value, edit.Jump == true));
                break;
            }
            case "removeStep" or "removeTransition":
            {
                string kind = edit.Op[6..];
                var element = w.Find(kind, number);
                var incoming = w.Connections.Where(c => w.Is(c, "NodeTo", kind, number)).ToArray();
                var outgoing = w.Connections.Where(c => w.Is(c, "NodeFrom", kind, number)).ToArray();
                Require(incoming.Length == 1 && outgoing.Length <= 1, "GraphEditTopology",
                    $"{kind} {number} has {incoming.Length} incoming and {outgoing.Length} outgoing connections; only an element of a linear chain can be removed.");
                if (outgoing.Length == 0) w.Remove(incoming[0]);
                else
                {
                    // Splice predecessor to successor; a jump on either side stays a jump.
                    bool jump = new[] { incoming[0], outgoing[0] }.Any(c => c.Element(w.Ns + "LinkType")?.Value == "Jump");
                    w.ReplaceRaw(incoming[0].Element(w.Ns + "NodeTo")!, w.Raw(outgoing[0].Element(w.Ns + "NodeTo")!));
                    w.Replace(incoming[0].Element(w.Ns + "LinkType")!, new XElement("LinkType", jump ? "Jump" : "Direct"));
                    w.Remove(outgoing[0]);
                }
                w.Remove(element);
                foreach (var member in w.Generated(kind, (string)element.Attribute("Name")!)) w.Remove(member);
                break;
            }
            case "renameStep" or "renameTransition":
            {
                string kind = edit.Op[6..];
                var element = w.Find(kind, number); string old = (string)element.Attribute("Name")!;
                w.SetAttribute(element, "Name", edit.Name!);
                // TIA names the generated step/transition structure in the FB interface after the element.
                foreach (var member in w.Generated(kind, old)) w.SetAttribute(member, "Name", edit.Name!);
                break;
            }
            case "setStepComment": SetText(w, w.Find("Step", number), "Comment", edit.Lang!, edit.Text!); break;
            case "setSequenceTitle":
            {
                var sequence = w.Graph.Elements(w.Ns + "Sequence").ElementAtOrDefault(number - 1);
                Require(sequence is not null, "GraphEditIdentity", $"Sequence {number} does not exist.");
                SetText(w, sequence, "Title", edit.Lang!, edit.Text!);
                break;
            }
            case "setTransitionCondition":
            {
                var transition = w.Find("Transition", number);
                Require((string?)transition.Attribute("ProgrammingLanguage") == "LAD", "GraphEditCondition", "Only a LAD transition condition can be replaced by contacts.");
                w.Replace(transition.Element(w.Ns + "FlgNet")!, Condition(w, edit.Condition!));
                break;
            }
        }
    }

    private static void SetText(Working w, XElement owner, string name, string lang, string text)
    {
        var line = new XElement("MultiLanguageText", new XAttribute("Lang", lang), text);
        var container = owner.Element(w.Ns + name);
        if (container is null)
        {
            // Schema order: StepName? Comment? Actions... for steps; Title? Comment? Steps... for sequences.
            w.InsertBefore(owner.Elements().First(e => e.Name != w.Ns + "StepName"), new XElement(name, line));
            return;
        }
        var existing = container.Elements(w.Ns + "MultiLanguageText").Where(e => (string?)e.Attribute("Lang") == lang).ToArray();
        Require(existing.Length <= 1, "GraphEditIdentity", $"{name} has more than one {lang} text.");
        if (existing.Length == 1) w.Replace(existing[0], line); else w.Append(container, line);
    }

    private static XElement Condition(Working w, IReadOnlyList<GraphContact> contacts)
    {
        string[] sections = ["Input", "Output", "InOut", "Static", "Temp"];
        var members = Members(w.Graph).ToArray();
        Require(members.Length > 0, "GraphEditCondition", "The document carries no block interface, so operand types cannot be proven.");
        foreach (var contact in contacts)
            Require(members.Count(m => (string?)m.Attribute("Name") == contact.Operand && (string?)m.Attribute("Datatype") == "Bool"
                && sections.Contains((string?)m.Parent!.Attribute("Name") ?? "")) == 1, "GraphEditCondition",
                $"Operand '{contact.Operand}' is not exactly one Bool member of the block interface.");
        // Contacts in series into the transition coil; UIds follow the native V21 export (Accesses, contacts, coil, then wires from 21).
        int n = contacts.Count, coil = 21 + 2 * n, wire = coil + 1;
        static XElement Pin(int uid, string name) => new("NameCon", new XAttribute("UId", uid), new XAttribute("Name", name));
        var parts = new XElement("Parts", contacts.Select((c, i) => new XElement("Access", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", 21 + i),
            new XElement("Symbol", new XElement("Component", new XAttribute("Name", c.Operand))))));
        parts.Add(contacts.Select((c, i) => new XElement("Part", new XAttribute("Name", "Contact"), new XAttribute("UId", 21 + n + i),
            c.Negated ? new XElement("Negated", new XAttribute("Name", "operand")) : null)));
        parts.Add(new XElement("Part", new XAttribute("Name", "TrCoil"), new XAttribute("UId", coil)));
        var wires = new XElement("Wires");
        for (int i = 0; i < n; i++)
        {
            wires.Add(new XElement("Wire", new XAttribute("UId", wire++), i == 0 ? new XElement("Powerrail") : Pin(20 + n + i, "out"), Pin(21 + n + i, "in")));
            wires.Add(new XElement("Wire", new XAttribute("UId", wire++), new XElement("IdentCon", new XAttribute("UId", 21 + i)), Pin(21 + n + i, "operand")));
        }
        wires.Add(new XElement("Wire", new XAttribute("UId", wire), Pin(coil - 1, "out"), Pin(coil, "in")));
        return new XElement("FlgNet", parts, wires);
    }

    private static XElement EmptyModule(string name) =>
        new(name, new XAttribute("ProgrammingLanguage", "LAD"), new XElement("FlgNet", new XElement("Parts")));
    private static XElement Connection(string fromKind, int from, string toKind, int to, bool jump) => new("Connection",
        new XElement("NodeFrom", new XElement(fromKind + "Ref", new XAttribute("Number", from))),
        new XElement("NodeTo", new XElement(toKind + "Ref", new XAttribute("Number", to))), new XElement("LinkType", jump ? "Jump" : "Direct"));

    /// <summary>Sequence well-formedness beyond the reader: unique names, alternating connections, linked transitions, reachable steps.</summary>
    private static void Validate(GraphDocument document, bool source)
    {
        string prefix = source ? "The source document is outside the supported edit structure: " : "";
        void Check(bool condition, string code, string message) => Require(condition, source ? "GraphEditSource" : code, prefix + message);
        var chart = document.Inspection.Graphs[0];
        var steps = chart.Sequences.SelectMany(s => s.Steps).ToArray();
        var transitions = chart.Sequences.SelectMany(s => s.Transitions).ToArray();
        // Steps and transitions become FB interface members, so names must differ from each other and from declared members.
        var declared = Members(GraphElement(document.Xml)).Where(m => (string?)m.Attribute("Datatype") is not (StepType or TransitionType)).Select(m => (string?)m.Attribute("Name") ?? "");
        string? duplicate = steps.Select(s => s.Name!).Concat(transitions.Select(t => t.Name!)).Concat(declared)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        Check(duplicate is null, "GraphEditDuplicate", $"Name '{duplicate}' is used by more than one step, transition or block interface member.");

        var branchTypes = chart.Sequences.SelectMany(s => s.Branches.Select(b => ($"Branch:{s.Ordinal}:{b.Number}", b.Type))).ToDictionary(b => b.Item1, b => b.Type);
        var into = new Dictionary<string, int>(); var from = new Dictionary<string, int>(); var direct = new Dictionary<string, int>();
        var next = new Dictionary<string, List<string>>();
        foreach (var sequence in chart.Sequences)
            foreach (var connection in sequence.Connections)
            {
                string? Key(GraphEndpoint? e) => e?.Kind switch { "Branch" => $"Branch:{sequence.Ordinal}:{e.Number}", "Step" or "Transition" => $"{e.Kind}:{e.Number}", _ => null };
                // A branch passes the step/transition alternation through: SimBegin/AltEnd sit where a step is entered, SimEnd/AltBegin where one is left.
                char Role(GraphEndpoint? e, bool input) => e?.Kind switch
                { "Step" => 'S', "Transition" => 'T', "Branch" => (branchTypes[Key(e)!] is "SimBegin" or "AltEnd") == input ? 'S' : 'T', _ => '-' };
                if (connection.LinkType == "Jump")
                    Check(connection.From?.Kind == "Transition" && connection.To?.Kind == "Step", "GraphEditTopology", $"Sequence {sequence.Ordinal} connection {connection.Ordinal}: a jump must lead from a transition to a step.");
                else
                {
                    char left = Role(connection.From, false), right = Role(connection.To, true);
                    Check(left == '-' || right == '-' || left != right, "GraphEditTopology", $"Sequence {sequence.Ordinal} connection {connection.Ordinal} does not alternate steps and transitions.");
                }
                string? a = Key(connection.From), b = Key(connection.To);
                if (a is not null) from[a] = from.GetValueOrDefault(a) + 1;
                if (b is null) continue;
                into[b] = into.GetValueOrDefault(b) + 1;
                if (connection.LinkType != "Jump" && connection.To!.Kind == "Step") direct[b] = direct.GetValueOrDefault(b) + 1;
                if (a is not null) (next.TryGetValue(a, out var list) ? list : next[a] = []).Add(b);
            }
        foreach (var t in transitions)
            Check(into.GetValueOrDefault($"Transition:{t.Number}") == 1 && from.GetValueOrDefault($"Transition:{t.Number}") == 1, "GraphEditDangling",
                $"Transition {t.Number} must have exactly one incoming and one outgoing connection.");
        foreach (var s in steps)
            Check(from.GetValueOrDefault($"Step:{s.Number}") <= 1 && direct.GetValueOrDefault($"Step:{s.Number}") <= 1, "GraphEditTopology",
                $"Step {s.Number} has more than one successor or more than one direct predecessor; use branches.");
        var seen = new HashSet<string>(); var queue = new Queue<string>(steps.Where(s => s.Init == true).Select(s => $"Step:{s.Number}"));
        Check(queue.Count > 0, "GraphEditUnreachable", "The chart has no initial step.");
        while (queue.TryDequeue(out var node))
            if (seen.Add(node)) foreach (var target in next.GetValueOrDefault(node) ?? []) queue.Enqueue(target);
        var unreachable = steps.FirstOrDefault(s => !seen.Contains($"Step:{s.Number}"));
        Check(unreachable is null, "GraphEditUnreachable", $"Step {unreachable?.Number} is not reachable from an initial step.");
    }

    private static XElement GraphElement(XDocument xml) => xml.Root!.DescendantsAndSelf().Single(e => e.Name.LocalName == "Graph"
        && (e.Name.NamespaceName is "" or GraphDocument.V6Namespace) && !e.Ancestors().Any(a => a.Name.LocalName == "Graph"));
    private static IEnumerable<XElement> Members(XElement graph) => graph.Ancestors().FirstOrDefault(a => a.Name.LocalName == "SW.Blocks.FB")?
        .Elements("AttributeList").Elements("Interface").Elements().Where(e => e.Name.LocalName == "Sections")
        .Elements().Where(e => e.Name.LocalName == "Section").Elements().Where(e => e.Name.LocalName == "Member") ?? [];
    private static int? Number(XElement e)
    {
        try { return (string?)e.Attribute("Number") is { } value ? XmlConvert.ToInt32(value.Trim()) : null; }
        catch (Exception error) when (error is FormatException or OverflowException) { return null; }
    }
    private static bool ValidXml(string value) { try { XmlConvert.VerifyXmlChars(value); return true; } catch (XmlException) { return false; } }
    private static bool Space(char c) => c is ' ' or '\t' or '\r' or '\n';
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new GraphException(code, message); }

    /// <summary>One parse of the current text with the exact character span of every element, collecting non-overlapping splices.</summary>
    private sealed class Working
    {
        private readonly List<(int Start, int End, string Value)> splices = [];
        private readonly Dictionary<XElement, (int Start, int TagEnd, int End)> spans = [];
        public string Text { get; }
        public XElement Graph { get; }
        public XNamespace Ns { get; }
        public string? Declared { get; }

        public Working(string text)
        {
            Text = text;
            try
            {
                XDocument xml;
                using (var reader = Reader(text)) xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                Declared = xml.Declaration?.Encoding;
                Graph = GraphElement(xml); Ns = Graph.Name.Namespace;
                var elements = xml.Root!.DescendantsAndSelf().ToArray();
                var lines = new List<int> { 0 };
                for (int i = 0; i < text.Length; i++)
                    if (text[i] == '\n' || text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) lines.Add(i + 1);
                int[] starts = new int[elements.Length], tags = new int[elements.Length], ends = new int[elements.Length];
                Array.Fill(tags, -1);
                int count = 0, pendingEnd = -1, pendingTag = -1; var open = new Stack<int>();
                using (var reader = Reader(text))
                {
                    var info = (IXmlLineInfo)reader;
                    while (reader.Read())
                    {
                        // Line positions name the node after its markup opener, e.g. the element name after '<'.
                        int at = lines[info.LineNumber - 1] + info.LinePosition - 1 - reader.NodeType switch
                        {
                            XmlNodeType.Element => 1, XmlNodeType.EndElement or XmlNodeType.ProcessingInstruction or XmlNodeType.XmlDeclaration => 2,
                            XmlNodeType.Comment => 4, XmlNodeType.CDATA => 9, _ => 0
                        };
                        if (pendingEnd >= 0) { ends[pendingEnd] = at; pendingEnd = -1; }
                        if (pendingTag >= 0) { tags[pendingTag] = at; pendingTag = -1; }
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            Require(count < elements.Length, "GraphEditUnsupported", "Element positions could not be mapped.");
                            starts[count] = at;
                            if (reader.IsEmptyElement) pendingEnd = count; else { open.Push(count); pendingTag = count; }
                            count++;
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement) pendingEnd = open.Pop();
                    }
                }
                if (pendingEnd >= 0) { int end = text.Length; while (end > 0 && Space(text[end - 1])) end--; ends[pendingEnd] = end; }
                Require(count == elements.Length, "GraphEditUnsupported", "Element positions could not be mapped.");
                for (int i = 0; i < elements.Length; i++)
                {
                    int tag = tags[i] < 0 ? ends[i] : tags[i], nameEnd = starts[i] + 1;
                    while (nameEnd < text.Length && !Space(text[nameEnd]) && text[nameEnd] is not ('/' or '>')) nameEnd++;
                    string name = starts[i] >= 0 && starts[i] < text.Length && text[starts[i]] == '<' ? text[(starts[i] + 1)..nameEnd] : "";
                    // The mapped markup must be exactly this element; otherwise nothing is edited.
                    Require((name == elements[i].Name.LocalName || name.EndsWith(":" + elements[i].Name.LocalName, StringComparison.Ordinal))
                        && text[tag - 1] == '>' && text[ends[i] - 1] == '>' && tag <= ends[i], "GraphEditUnsupported", "Element positions could not be mapped exactly.");
                    spans[elements[i]] = (starts[i], tag, ends[i]);
                }
            }
            catch (XmlException error) { throw new GraphException("GraphEditUnsupported", "The document could not be re-read for editing: " + error.Message); }
        }

        public IEnumerable<XElement> Items(string kind) => Graph.Elements(Ns + "Sequence").Elements(Ns + kind + "s").Elements(Ns + kind);
        public IEnumerable<XElement> Connections => Graph.Elements(Ns + "Sequence").Elements(Ns + "Connections").Elements(Ns + "Connection");
        public XElement Find(string kind, int number)
        {
            var hits = Items(kind).Where(e => Number(e) == number).Take(2).ToArray();
            Require(hits.Length == 1, "GraphEditIdentity", $"{kind} {number} does not exist exactly once in the chart.");
            return hits[0];
        }
        public bool Is(XElement connection, string side, string kind, int number) =>
            connection.Elements(Ns + side).Elements().FirstOrDefault() is { } e && e.Name == Ns + (kind + "Ref") && Number(e) == number;
        public IEnumerable<XElement> Generated(string kind, string name) => Members(Graph).Where(m =>
            (string?)m.Attribute("Datatype") == (kind == "Step" ? StepType : TransitionType) && (string?)m.Attribute("Name") == name).ToArray();

        public string Raw(XElement e) => Text[spans[e].Start..spans[e].End];
        public void ReplaceRaw(XElement e, string xml)
        {
            Require(!e.Attributes().Any(a => a.IsNamespaceDeclaration), "GraphEditUnsupported", "An edited element declares namespaces.");
            Add(spans[e].Start, spans[e].End, xml);
        }
        public void Replace(XElement e, XElement content) => ReplaceRaw(e, Serialize(e.Parent!, content));
        public void Remove(XElement e) => Add(spans[e].Start - Indent(e).Length, spans[e].End, "");
        public void InsertBefore(XElement e, XElement content) => Add(spans[e].Start, spans[e].Start, Serialize(e.Parent!, content) + Indent(e));
        public void Append(XElement parent, params XElement[] content)
        {
            if (parent.Elements().LastOrDefault() is { } last)
            {
                Add(spans[last].End, spans[last].End, string.Concat(content.Select(c => Indent(last) + Serialize(parent, c))));
                return;
            }
            var (start, tag, end) = spans[parent];
            string inner = string.Concat(content.Select(c => Serialize(parent, c)));
            if (tag != end) { Add(tag, tag, inner); return; }
            int close = end - 2, nameEnd = start + 1;
            while (Space(Text[close - 1])) close--;
            while (!Space(Text[nameEnd]) && Text[nameEnd] is not ('/' or '>')) nameEnd++;
            Add(close, end, ">" + inner + "</" + Text[(start + 1)..nameEnd] + ">");
        }
        public void SetAttribute(XElement e, string name, string value)
        {
            var (start, tag, _) = spans[e];
            int i = start + 1;
            while (!Space(Text[i]) && Text[i] is not ('/' or '>')) i++;
            while (true)
            {
                while (Space(Text[i])) i++;
                if (i >= tag || Text[i] is '/' or '>') break;
                int nameStart = i;
                while (Text[i] != '=' && !Space(Text[i])) i++;
                string attribute = Text[nameStart..i];
                while (Text[i] != '"' && Text[i] != '\'') i++;
                char quote = Text[i]; int valueStart = ++i; i = Text.IndexOf(quote, i);
                if (attribute == name)
                {
                    Add(valueStart, i, value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace(quote.ToString(), quote == '"' ? "&quot;" : "&apos;"));
                    return;
                }
                i++;
            }
            throw new GraphException("GraphEditUnsupported", $"Attribute {name} is absent.");
        }

        public string Result()
        {
            var ordered = splices.Select((s, i) => (s, i)).OrderBy(x => x.s.Start).ThenBy(x => x.s.End).ThenBy(x => x.i).Select(x => x.s).ToArray();
            var builder = new StringBuilder(Text.Length + ordered.Sum(s => s.Value.Length)); int at = 0;
            foreach (var splice in ordered)
            {
                Require(splice.Start >= at, "GraphEditConflict", "The operation touches overlapping XML.");
                builder.Append(Text, at, splice.Start - at).Append(splice.Value); at = splice.End;
            }
            return builder.Append(Text, at, Text.Length - at).ToString();
        }

        private void Add(int start, int end, string value) => splices.Add((start, end, value));
        private string Indent(XElement e) { int k = spans[e].Start; while (k > 0 && Space(Text[k - 1])) k--; return Text[k..spans[e].Start]; }
        private string Serialize(XElement context, XElement content)
        {
            // New markup is unprefixed, so it must land where the GRAPH namespace is the default namespace.
            Require(context.GetDefaultNamespace() == Ns && context.Name.Namespace == Ns, "GraphEditUnsupported", "The GRAPH namespace is not the default namespace at the edit position.");
            return content.ToString(SaveOptions.DisableFormatting);
        }
        private static XmlReader Reader(string text) => XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = false, IgnoreWhitespace = false, IgnoreProcessingInstructions = false });
    }
}
