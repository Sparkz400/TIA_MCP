using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Graph;

internal sealed class GraphReader(GraphDocument document)
{
    private readonly List<GraphDiagnostic> diagnostics = [];
    private readonly List<GraphUnsupported> unsupported = [];
    private readonly List<GraphChart> charts = [];
    private XNamespace ns = XNamespace.None;

    // Direct-child grammar from SW.PlcBlocks.Graph_v6.xsd. Embedded languages and text modules are explicit opaque boundaries.
    private sealed record Rule(string Attributes, string Children, bool Choice = false);
    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["Graph"] = new("", "PreOperations Sequence+ PostOperations AlarmsSettings"),
        ["Sequence"] = new("", "Title? Comment? Steps Transitions Branches Connections"),
        ["Steps"] = new("", "Step+"), ["Transitions"] = new("", "Transition*"),
        ["Branches"] = new("", "Branch*"), ["Connections"] = new("", "Connection+"),
        ["Step"] = new("Number Name Init IsMissing MaximumStepTime WarningTime", "StepName? Comment? Actions Supervisions Interlocks"),
        ["Transition"] = new("Number Name IsMissing ProgrammingLanguage", "TransitionName? Comment? FlgNet"),
        ["Branch"] = new("Number Type Cardinality", ""),
        ["Actions"] = new("", "Title? Action*"),
        ["Action"] = new("Event Interlock Qualifier", "Access* Token* Comment* LineComment* Blank* NewLine*", true),
        ["Supervisions"] = new("", "Supervision"), ["Interlocks"] = new("", "Interlock"),
        // V21 Update 2 emits empty modules without FlgNet, although its importer/XSD requires it.
        ["Supervision"] = new("ProgrammingLanguage", "Title? AlarmText? FlgNet?"),
        ["Interlock"] = new("ProgrammingLanguage", "Title? AlarmText? FlgNet?"),
        ["Connection"] = new("", "NodeFrom NodeTo LinkType"),
        ["NodeFrom"] = new("", "StepRef? TransitionRef? BranchRef? EndConnection?", true),
        ["NodeTo"] = new("", "StepRef? TransitionRef? BranchRef? EndConnection?", true),
        ["StepRef"] = new("Number", ""), ["TransitionRef"] = new("Number", ""),
        ["BranchRef"] = new("Number In Out", ""),
        ["LinkType"] = new("", ""),
        ["PreOperations"] = new("", "Title? Comment? PermanentOperation*"),
        ["PostOperations"] = new("", "Title? Comment? PermanentOperation*"),
        ["PermanentOperation"] = new("ProgrammingLanguage", "Title? FlgNet?")
    };
    private static readonly HashSet<string> Opaque = new(StringComparer.Ordinal)
    {
        "FlgNet", "Access", "Token", "Comment", "LineComment", "Blank", "NewLine", "Title", "StepName",
        "TransitionName", "AlarmText", "AlarmsSettings", "EndConnection"
    };
    private static readonly string[] Languages = ["STL", "FBD", "LAD", "FBD_IEC", "LAD_IEC", "GRAPH", "DB", "SDB", "DB_CPU", "FB_IDB", "SFB_IDB", "DT_DB", "SCL"];

    public GraphInspection Read(string hash, byte[] original)
    {
        var root = document.Xml.Root!;
        var candidates = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Graph"
            && !e.Ancestors().Any(a => a.Name.LocalName == "Graph")).ToArray();
        if (root.Name.LocalName != "Graph")
            Retain(root, "OpaqueEnvelope", "The export envelope and non-GRAPH content are retained without semantic validation.");
        foreach (var graph in candidates)
        {
            if (graph.Name.NamespaceName is not ("" or GraphDocument.V6Namespace))
            {
                Retain(graph, "UnsupportedNamespace", "Only the v6 namespace and unqualified schema fragments are structurally read.");
                continue;
            }
            ns = graph.Name.Namespace;
            Scan(graph);
            var sequences = Children(graph, "Sequence").Select((s, i) => ReadSequence(s, i + 1)).ToArray();
            ValidateReferences(sequences);
            charts.Add(new(charts.Count + 1, Path(graph), ns.NamespaceName, Array.AsReadOnly(sequences),
                Fragments(graph, "PreOperations"), Fragments(graph, "PostOperations"), Fragments(graph, "AlarmsSettings")));
        }
        if (charts.Count == 0) Error(root, "NoSupportedGraph", "No supported GRAPH structure was found.");
        return new("tia-graph-inspection.v1", GraphDocument.Profile, hash, original.Length, false, false,
            diagnostics.Any(d => d.Severity == "error"), unsupported.Count != 0,
            charts.AsReadOnly(), diagnostics.AsReadOnly(), unsupported.AsReadOnly(), Convert.ToBase64String(original));
    }

    private GraphSequence ReadSequence(XElement sequence, int ordinal)
    {
        // All repeated containers are retained and inspected so malformed input cannot conceal a second identity.
        var steps = Children(sequence, "Steps").SelectMany(e => Children(e, "Step")).Select(ReadStep).ToArray();
        var transitions = Children(sequence, "Transitions").SelectMany(e => Children(e, "Transition")).Select(e =>
            new GraphTransition(Int(e, "Number", true), Required(e, "Name"), Bool(e, "IsMissing", false),
                Language(e), Children(e, "FlgNet").Select(Fragment).FirstOrDefault(), Fragment(e))).ToArray();
        var branches = Children(sequence, "Branches").SelectMany(e => Children(e, "Branch")).Select(e =>
            new GraphBranch(Int(e, "Number", true), Enum(e, "Type", ["SimBegin", "SimEnd", "AltBegin", "AltEnd"], true),
                Int(e, "Cardinality", true), Fragment(e))).ToArray();
        var connections = Children(sequence, "Connections").SelectMany(e => Children(e, "Connection")).Select((e, i) =>
            new GraphConnection(i + 1, Endpoint(e, "NodeFrom"), Endpoint(e, "NodeTo"), Link(e), Fragment(e))).ToArray();

        var title = Children(sequence, "Title").FirstOrDefault();
        var localizedTitle = title?.Elements(ns + "MultiLanguageText").Select(t => $"[{(string?)t.Attribute("Lang") ?? "?"}] {t.Value}").ToArray();
        return new(ordinal, Path(sequence), localizedTitle is { Length: > 0 } ? string.Join(" · ", localizedTitle) : title?.Value,
            Array.AsReadOnly(steps), Array.AsReadOnly(transitions), Array.AsReadOnly(branches), Array.AsReadOnly(connections));
    }

    private void ValidateReferences(IReadOnlyList<GraphSequence> sequences)
    {
        // Step/transition numbers are chart-wide; branches remain scoped to their sequence.
        var identities = new Dictionary<(int Scope, string Kind, int Number), List<(string Path, bool Missing, int Sequence)>>();
        void Add(GraphSequence sequence, string kind, int? number, string path, bool missing)
        {
            if (number is not int n) return;
            var key = (kind == "Branch" ? sequence.Ordinal : 0, kind, n);
            if (!identities.TryGetValue(key, out var entries)) identities[key] = entries = [];
            entries.Add((path, missing, sequence.Ordinal));
            if (entries.Count > 1) diagnostics.Add(new("error", "DuplicateIdentity", path,
                $"{kind} {n} occurs more than once in its {(kind == "Branch" ? "sequence" : "chart")}; first occurrence: {entries[0].Path}."));
        }
        foreach (var sequence in sequences)
        {
            foreach (var step in sequence.Steps) Add(sequence, "Step", step.Number, step.Content.Path, step.IsMissing == true);
            foreach (var transition in sequence.Transitions) Add(sequence, "Transition", transition.Number, transition.Content.Path, transition.IsMissing == true);
            foreach (var branch in sequence.Branches) Add(sequence, "Branch", branch.Number, branch.Content.Path, false);
        }
        foreach (var sequence in sequences)
            foreach (var connection in sequence.Connections)
                foreach (bool isTo in new[] { false, true })
                {
                    var endpoint = isTo ? connection.To : connection.From;
                    if (endpoint is null || endpoint.Kind == "EndConnection" || endpoint.Number is not int n) continue;
                    var key = (endpoint.Kind == "Branch" ? sequence.Ordinal : 0, endpoint.Kind, n);
                    if (!identities.TryGetValue(key, out var entries))
                        diagnostics.Add(new("error", "BrokenReference", endpoint.Path, $"No {endpoint.Kind} {n} exists in its identity scope."));
                    else if (entries.Count != 1)
                        diagnostics.Add(new("error", "AmbiguousReference", endpoint.Path, $"{endpoint.Kind} {n} has multiple definitions."));
                    else if (entries[0].Missing)
                        diagnostics.Add(new("error", "MissingReferenceTarget", endpoint.Path, $"{endpoint.Kind} {n} is marked IsMissing."));
                    else if (entries[0].Sequence != sequence.Ordinal && !(isTo && connection.LinkType == "Jump" && endpoint.Kind == "Step"))
                        diagnostics.Add(new("error", "CrossSequenceReference", endpoint.Path, "Only a jump destination step may resolve into another sequence."));
                }
    }

    private GraphStep ReadStep(XElement step)
    {
        var actions = Children(step, "Actions").SelectMany(a => Children(a, "Action")).Select((a, i) => new GraphAction(i + 1,
            Enum(a, "Event", ["", "A1", "L0", "L1", "R1", "S0", "S1", "V0", "V1"]),
            Enum(a, "Qualifier", ["", "CD", "CR", "CS", "CU", "D", "L", "N", "ON", "OFF", "R", "S", "TD", "TF", "TL", "TR"]),
            Bool(a, "Interlock"), Fragment(a))).ToArray();
        IReadOnlyList<GraphCondition> Conditions(string container, string name) => Array.AsReadOnly(Children(step, container)
            .SelectMany(c => Children(c, name)).Select(e => new GraphCondition(Language(e), Fragment(e))).ToArray());
        return new(Int(step, "Number", true), Required(step, "Name"), Bool(step, "Init", false), Bool(step, "IsMissing", false),
            (string?)step.Attribute("MaximumStepTime"), (string?)step.Attribute("WarningTime"), Array.AsReadOnly(actions),
            Conditions("Interlocks", "Interlock"), Conditions("Supervisions", "Supervision"), Fragment(step));
    }

    private GraphEndpoint? Endpoint(XElement connection, string container)
    {
        var parent = Children(connection, container).FirstOrDefault();
        if (parent is null) return null;
        var elements = parent.Elements().ToArray();
        if (elements.Length != 1)
        {
            Error(parent, "InvalidEndpoint", "An endpoint must contain exactly one typed reference or EndConnection.");
            return null;
        }
        var element = elements[0];
        if (element.Name.Namespace != ns || element.Name.LocalName is not ("StepRef" or "TransitionRef" or "BranchRef" or "EndConnection"))
        {
            Error(element, "InvalidEndpoint", "Unsupported endpoint kind.");
            return null;
        }
        string kind = element.Name.LocalName.Replace("Ref", "", StringComparison.Ordinal);
        return new(kind, kind == "EndConnection" ? null : Int(element, "Number", true),
            kind == "Branch" ? Int(element, "In") : null, kind == "Branch" ? Int(element, "Out") : null, Path(element));
    }

    private string? Link(XElement connection)
    {
        var link = Children(connection, "LinkType").FirstOrDefault();
        if (link is null) return null;
        if (link.Value is not ("Direct" or "Jump")) Error(link, "InvalidValue", $"Unsupported LinkType '{link.Value}'.");
        return link.Value;
    }

    private void Scan(XElement element)
    {
        if (element.Name.LocalName is "Supervision" or "Interlock" && !Children(element, "FlgNet").Any())
            diagnostics.Add(new("warning", "EmptyNativeCondition", Path(element),
                "Condition has no FlgNet body. This shape is emitted by TIA V21 Update 2 for empty modules but rejected by its XML importer. Original XML is retained; no body is synthesized."));
        if (Opaque.Contains(element.Name.LocalName))
        {
            // EndConnection has xs:anyType; nonempty payload is deliberately opaque.
            if (element.Name.LocalName == "EndConnection" && !element.HasElements && !element.HasAttributes && element.Value.Length == 0) return;
            Retain(element, "OpaqueContent", "Retained as XML; embedded language, text, alarm or extension semantics are not interpreted.");
            return;
        }
        var rule = Rules[element.Name.LocalName];
        var attrs = rule.Attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            if (attr.Name.NamespaceName != "" || !attrs.Contains(attr.Name.LocalName))
            {
                var path = Path(element) + "/@" + attr.Name;
                unsupported.Add(new("UnsupportedAttribute", path, attr.Name.ToString(), attr.ToString()));
                diagnostics.Add(new("warning", "UnsupportedAttribute", path, "Attribute retained without interpretation."));
            }
        var specs = rule.Children.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var names = specs.Select(s => s.TrimEnd('?', '*', '+')).ToArray();
        for (int i = 0; i < specs.Length; i++)
        {
            int count = Children(element, names[i]).Count();
            int min = specs[i].EndsWith('?') || specs[i].EndsWith('*') ? 0 : 1;
            bool many = specs[i].EndsWith('*') || specs[i].EndsWith('+');
            if (count < min || (!many && count > 1)) Error(element, "InvalidStructure", $"Expected {(many ? "at least " + min : min == 0 ? "zero or one" : "one")} {names[i]}; found {count}.");
        }
        int previous = -1;
        foreach (var child in element.Elements())
        {
            int position = child.Name.Namespace == ns ? Array.IndexOf(names, child.Name.LocalName) : -1;
            if (position < 0)
            {
                Retain(child, "UnsupportedElement", "Element is outside the supported GRAPH structural grammar.");
                continue;
            }
            if (!rule.Choice && position < previous) Error(child, "InvalidOrder", "Element order differs from the GRAPH v6 schema.");
            previous = position;
            Scan(child);
        }
        if (element.Name.LocalName != "LinkType" && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            Error(element, "UnexpectedText", "Structural element contains unexpected text; original text is retained.");
        if (element.Name.LocalName == "PermanentOperation") Language(element);
    }

    private string? Required(XElement e, string name)
    {
        var value = (string?)e.Attribute(name);
        if (value is null) Error(e, "MissingAttribute", $"Required attribute {name} is absent.");
        return value;
    }
    private int? Int(XElement e, string name, bool required = false)
    {
        var value = required ? Required(e, name) : (string?)e.Attribute(name);
        if (value is null) return null;
        try { return XmlConvert.ToInt32(value.Trim()); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { Error(e, "InvalidInteger", $"{name} must be an xs:int, got '{value}'."); return null; }
    }
    private bool? Bool(XElement e, string name, bool? fallback = null)
    {
        var value = (string?)e.Attribute(name);
        if (value is null) return fallback;
        try { return XmlConvert.ToBoolean(value.Trim()); }
        catch (FormatException) { Error(e, "InvalidBoolean", $"{name} must be an XML boolean, got '{value}'."); return null; }
    }
    private string? Enum(XElement e, string name, string[] allowed, bool required = false)
    {
        var value = required ? Required(e, name) : (string?)e.Attribute(name);
        if (value is not null && !allowed.Contains(value, StringComparer.Ordinal)) Error(e, "InvalidValue", $"Unsupported {name} '{value}'.");
        return value;
    }
    private string? Language(XElement e) => Enum(e, "ProgrammingLanguage", Languages, true);
    private string Path(XElement e) => document.Paths[e];
    private GraphFragment Fragment(XElement e) => new(Path(e), e.ToString(SaveOptions.DisableFormatting));
    private IReadOnlyList<GraphFragment> Fragments(XElement parent, string name) => Array.AsReadOnly(Children(parent, name).Select(Fragment).ToArray());
    private IEnumerable<XElement> Children(XElement e, string name) => e.Elements(ns + name);
    private void Error(XElement e, string code, string message) => diagnostics.Add(new("error", code, Path(e), message));
    private void Retain(XElement e, string code, string message)
    {
        unsupported.Add(new(code, Path(e), e.Name.ToString(), e.ToString(SaveOptions.DisableFormatting)));
        diagnostics.Add(new("warning", code, Path(e), message));
    }
}
