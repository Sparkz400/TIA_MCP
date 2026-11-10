#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Readable text of exported LAD/FBD networks (FlgNet v5). Pure: reads an exported document and never calls native APIs.
    /// Contacts, O/A/X boxes and comparators become Boolean expressions; coils, instructions and calls become statements with their pin
    /// bindings. Unknown instructions are rendered by name and pins, not interpreted. This is a reading aid, not a semantic model:
    /// the native XML stays authoritative.</summary>
    internal static class GraphicalNetworkText
    {
        internal const int MaxNetworkCharacters = 16 * 1024;

        internal sealed class Network
        {
            internal int Number;
            internal string Id, Language, Title, Comment, Text;
            internal bool Truncated, Rendered;
        }

        internal static Network[] Render(XDocument document, string culture = null, int maxNetworkCharacters = MaxNetworkCharacters)
        {
            var result = new List<Network>();
            int number = 0;
            foreach (var unit in document.Descendants("SW.Blocks.CompileUnit"))
            {
                var attributes = unit.Element("AttributeList");
                var network = new Network
                {
                    Number = ++number, Id = (string)unit.Attribute("ID"),
                    Language = attributes?.Element("ProgrammingLanguage")?.Value,
                    Title = Text(unit, "Title", culture), Comment = Text(unit, "Comment", culture),
                };
                var source = attributes?.Element("NetworkSource");
                var flg = source?.Elements().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
                var structured = source?.Elements().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
                string body;
                if (flg != null) { body = new Renderer(flg).Render(); network.Rendered = true; }
                else if (structured != null) { body = StructuredText(structured); network.Rendered = true; }
                else if (source == null || !source.HasElements) body = "(empty network)";
                else body = "(" + (network.Language ?? "unknown") + " network is not rendered; read block_read format=generatedSource or the native XML)";
                if (body.Length > maxNetworkCharacters) { body = body.Substring(0, maxNetworkCharacters) + Environment.NewLine + "... (truncated)"; network.Truncated = true; }
                network.Text = body;
                result.Add(network);
            }
            return result.ToArray();
        }

        internal static string Format(IEnumerable<Network> networks)
        {
            var text = new StringBuilder();
            foreach (var network in networks)
            {
                text.Append("Network ").Append(network.Number.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(network.Title)) text.Append(": ").Append(network.Title.Trim());
                text.Append(" [").Append(network.Language ?? "?").Append(", ID ").Append(network.Id).Append(']').AppendLine();
                if (!string.IsNullOrWhiteSpace(network.Comment))
                    foreach (var line in network.Comment.Trim().Split('\n')) text.Append("  // ").AppendLine(line.TrimEnd('\r'));
                foreach (var line in network.Text.Split('\n')) text.Append("  ").AppendLine(line.TrimEnd('\r'));
                text.AppendLine();
            }
            return text.ToString();
        }

        /// <summary>SCL source of an exported StructuredText network, rebuilt from its tokens in document order. Local variables carry
        /// their '#', quoted names their quotes; a block call shows its instance or function name, never the type of the instance.</summary>
        internal static string StructuredText(XElement source)
        {
            var text = new StringBuilder();
            foreach (var child in source.Elements()) AppendStructured(child, text);
            return text.ToString().TrimEnd();
        }

        private static void AppendStructured(XElement element, StringBuilder text)
        {
            string name = element.Name.LocalName;
            switch (name)
            {
                case "Token": text.Append((string)element.Attribute("Text")); return;
                case "Blank": text.Append(' ', Count(element)); return;
                case "NewLine": for (int i = Count(element); i > 0; i--) text.Append(Environment.NewLine); return;
                case "ConstantValue": case "Text": text.Append(element.Value); return;
                case "LineComment": text.Append("//"); break;
                case "Instruction": case "Parameter": text.Append((string)element.Attribute("Name")); break;
                case "Address": text.Append(Renderer.Absolute(element)); return;
                case "Constant":
                    if (element.Attribute("Name") != null) text.Append('"').Append((string)element.Attribute("Name")).Append('"');
                    break;
                case "Component":
                    var previous = element.ElementsBeforeSelf().LastOrDefault();
                    // Older exports list the components of a symbol without the '.' tokens.
                    if (previous != null && previous.Name.LocalName == "Component") text.Append('.');
                    // The scope is on the Access around a Symbol, or on the Instance of a call.
                    bool local = previous == null && (string)(element.Parent?.Attribute("Scope") ?? element.Parent?.Parent?.Attribute("Scope")) == "LocalVariable";
                    bool quoted = element.Elements().Any(e => e.Name.LocalName == "BooleanAttribute" && (string)e.Attribute("Name") == "HasQuotes" && e.Value == "true");
                    string component = (string)element.Attribute("Name");
                    text.Append(local ? "#" + component : quoted ? "\"" + component + "\"" : component);
                    string slice = (string)element.Attribute("SliceAccessModifier");
                    if (!string.IsNullOrEmpty(slice)) text.Append(".%").Append(slice.ToUpperInvariant());
                    break;
                default:
                    // Declared attributes and template arguments are not part of the source text.
                    if (name == "TemplateValue" || name == "ConstantType" || name.EndsWith("Attribute", StringComparison.Ordinal)) return;
                    break;
            }
            foreach (var child in element.Elements()) AppendStructured(child, text);
        }

        private static int Count(XElement element) =>
            int.TryParse((string)element.Attribute("Num"), NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0 ? Math.Min(count, 4096) : 1;

        private static string Text(XElement unit, string composition, string culture)
        {
            var items = (unit.Element("ObjectList")?.Elements("MultilingualText") ?? Enumerable.Empty<XElement>())
                .Where(t => (string)t.Attribute("CompositionName") == composition)
                .SelectMany(t => t.Descendants("MultilingualTextItem"))
                .Select(i => new { Culture = i.Element("AttributeList")?.Element("Culture")?.Value, Text = i.Element("AttributeList")?.Element("Text")?.Value })
                .Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToArray();
            return (items.FirstOrDefault(i => i.Culture == culture) ?? items.FirstOrDefault(i => i.Culture == "en-US") ?? items.FirstOrDefault())?.Text;
        }

        private sealed class Expr
        {
            internal readonly string Text; internal readonly int Precedence; // 1 OR, 2 XOR, 3 AND, 4 atom
            internal Expr(string text, int precedence) { Text = text; Precedence = precedence; }
            internal static readonly Expr True = new Expr("TRUE", 4);
            internal string In(int precedence) => Precedence < precedence ? "(" + Text + ")" : Text;
        }

        private sealed class Node
        {
            internal string Uid, Name, Label, Reference, Kind; // Kind: access, part, call. Reference names a box output in expressions.
            internal XElement Element;
            internal HashSet<string> Negated = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class Renderer
        {
            private static readonly Dictionary<string, string> Comparators = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["Eq"] = "=", ["Ne"] = "<>", ["Gt"] = ">", ["Ge"] = ">=", ["Lt"] = "<", ["Le"] = "<=" };
            private static readonly Dictionary<string, string> Gates = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "AND", ["O"] = "OR", ["X"] = "XOR" };

            private readonly XNamespace ns;
            private readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
            private readonly List<Node> order = new List<Node>();
            // Sink (uid|pin) -> source endpoint; source (uid|pin) -> sinks.
            private readonly Dictionary<string, XElement> sourceOf = new Dictionary<string, XElement>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<XElement>> sinksOf = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
            private readonly HashSet<string> visiting = new HashSet<string>(StringComparer.Ordinal);

            internal Renderer(XElement flg)
            {
                ns = flg.Name.Namespace;
                foreach (var element in flg.Element(ns + "Parts")?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    string uid = (string)element.Attribute("UId");
                    if (uid == null) continue;
                    var node = new Node { Uid = uid, Element = element, Kind = element.Name.LocalName == "Access" ? "access" : element.Name.LocalName == "Call" ? "call" : "part" };
                    node.Name = node.Kind == "call" ? (string)element.Element(ns + "CallInfo")?.Attribute("Name") : (string)element.Attribute("Name") ?? element.Name.LocalName;
                    foreach (var negated in element.Elements(ns + "Negated")) node.Negated.Add((string)negated.Attribute("Name"));
                    nodes[uid] = node; order.Add(node);
                }
                foreach (var node in order)
                {
                    node.Label = node.Kind == "access" ? Operand(node.Element) : BoxLabel(node);
                    var instance = (node.Element.Element(ns + "CallInfo") ?? node.Element).Element(ns + "Instance");
                    node.Reference = node.Kind == "access" ? node.Label : instance != null ? Symbol(instance, (string)instance.Attribute("Scope")) : node.Name + "#" + node.Uid;
                }
                foreach (var wire in flg.Element(ns + "Wires")?.Elements(ns + "Wire") ?? Enumerable.Empty<XElement>())
                {
                    var ends = wire.Elements().ToArray();
                    if (ends.Length < 2) continue;
                    string source = Key(ends[0]);
                    if (!sinksOf.TryGetValue(source, out var sinks)) sinksOf[source] = sinks = new List<XElement>();
                    foreach (var sink in ends.Skip(1)) { sourceOf[Key(sink)] = ends[0]; sinks.Add(sink); }
                }
            }

            private static string Key(XElement end) => (string)end.Attribute("UId") + "|" + ((string)end.Attribute("Name") ?? "");
            private static bool IsCoil(Node n) => n.Kind == "part" && n.Name.EndsWith("Coil", StringComparison.Ordinal);
            private static bool IsContact(Node n) => n.Kind == "part" && n.Name.EndsWith("Contact", StringComparison.Ordinal);
            private bool IsInline(Node n) => IsContact(n) || n.Kind == "part" && (Gates.ContainsKey(n.Name) || Comparators.ContainsKey(n.Name));

            internal string Render()
            {
                var lines = new List<string>();
                foreach (var node in order.Where(n => n.Kind != "access"))
                {
                    if (IsCoil(node)) lines.Add(Coil(node));
                    else if (!IsInline(node)) lines.Add(Box(node));
                    else if (!Outputs(node).Any(pin => sinksOf.ContainsKey(node.Uid + "|" + pin)))
                        lines.Add("(unconnected) " + Output(node, "out").Text);
                }
                return lines.Count == 0 ? "(no instructions)" : string.Join(Environment.NewLine, lines);
            }

            private IEnumerable<string> Outputs(Node node) => sinksOf.Keys.Where(k => k.StartsWith(node.Uid + "|", StringComparison.Ordinal)).Select(k => k.Substring(node.Uid.Length + 1)).DefaultIfEmpty("out");

            private string Coil(Node node)
            {
                string operand = Input(node, "operand", false)?.Text ?? "?";
                var power = Input(node, "in") ?? new Expr("?", 4);
                if (node.Name == "Coil") return operand + " := " + (node.Negated.Contains("operand") ? "NOT " + power.In(4) : power.Text);
                string verb = node.Name == "SCoil" ? "SET" : node.Name == "RCoil" ? "RESET" : node.Name;
                var extra = Pins(node).Where(p => p != "in" && p != "operand").Select(p => p + " := " + (Input(node, p)?.Text ?? "?"));
                return verb + " " + operand + (extra.Any() ? " (" + string.Join(", ", extra) + ")" : "") + " IF " + power.Text;
            }

            private string Box(Node node)
            {
                var arguments = new List<string>();
                foreach (string pin in Pins(node))
                {
                    if (pin == "en" && sourceOf.TryGetValue(node.Uid + "|en", out var source) && source.Name.LocalName == "Powerrail") continue;
                    arguments.Add(pin + " := " + (Input(node, pin)?.Text ?? "?"));
                }
                foreach (var output in Outputs(node).Where(pin => sinksOf.ContainsKey(node.Uid + "|" + pin)))
                    foreach (var sink in sinksOf[node.Uid + "|" + output])
                    {
                        if (sink.Name.LocalName == "IdentCon" && nodes.TryGetValue((string)sink.Attribute("UId"), out var target))
                            arguments.Add(output + " => " + target.Label);
                        else if (sink.Name.LocalName == "OpenCon") arguments.Add(output + " => (open)");
                    }
                string prefix = node.Kind == "call" ? "CALL " : "";
                return prefix + node.Label + "(" + string.Join(", ", arguments) + ")";
            }

            // Input pins in native order: call parameters first, then any other wired sink pins.
            private IEnumerable<string> Pins(Node node)
            {
                var wired = sourceOf.Keys.Where(k => k.StartsWith(node.Uid + "|", StringComparison.Ordinal)).Select(k => k.Substring(node.Uid.Length + 1)).ToList();
                var declared = node.Element.Element(ns + "CallInfo")?.Elements(ns + "Parameter").Select(p => (string)p.Attribute("Name")).Where(wired.Contains).ToList() ?? new List<string>();
                var first = wired.Where(p => p == "en" || p == "in" || p == "pre").ToList();
                return first.Concat(declared).Concat(wired.Except(first).Except(declared)).Distinct(StringComparer.Ordinal);
            }

            private Expr Input(Node node, string pin, bool applyNegation = true)
            {
                if (!sourceOf.TryGetValue(node.Uid + "|" + pin, out var source)) return null;
                var value = Source(source);
                return applyNegation && node.Negated.Contains(pin) ? Not(value) : value;
            }

            private static Expr Not(Expr value) => new Expr("NOT " + value.In(4), 4);

            private Expr Source(XElement end)
            {
                switch (end.Name.LocalName)
                {
                    case "Powerrail": return Expr.True;
                    case "OpenCon": return new Expr("(open)", 4);
                    case "IdentCon": return nodes.TryGetValue((string)end.Attribute("UId"), out var access) ? new Expr(access.Label, 4) : new Expr("?" + (string)end.Attribute("UId"), 4);
                    case "NameCon":
                        return nodes.TryGetValue((string)end.Attribute("UId"), out var node) ? Output(node, (string)end.Attribute("Name")) : new Expr("?" + (string)end.Attribute("UId"), 4);
                    default: return new Expr("?" + end.Name.LocalName, 4);
                }
            }

            private Expr Output(Node node, string pin)
            {
                string key = node.Uid + "|" + pin;
                if (!visiting.Add(key)) return new Expr("(loop " + node.Label + ")", 4);
                try
                {
                    var value = Compute(node, pin);
                    return node.Negated.Contains(pin) ? Not(value) : value;
                }
                finally { visiting.Remove(key); }
            }

            private Expr Compute(Node node, string pin)
            {
                if (IsContact(node) && pin == "out")
                {
                    var operand = Input(node, "operand", false) ?? new Expr("?", 4);
                    Expr condition = node.Name == "Contact" ? (node.Negated.Contains("operand") ? Not(operand) : operand)
                        : new Expr(node.Name + "(" + string.Join(", ", new[] { operand.Text }.Concat(Pins(node).Where(p => p != "in" && p != "operand").Select(p => p + ": " + (Input(node, p)?.Text ?? "?")))) + ")", 4);
                    return And(Input(node, "in"), condition);
                }
                if (IsCoil(node) && pin == "out") return Input(node, "in") ?? new Expr("?", 4);
                if (node.Kind == "part" && Gates.TryGetValue(node.Name, out var gate) && pin == "out")
                {
                    var inputs = Pins(node).Where(p => p.StartsWith("in", StringComparison.Ordinal)).Select(p => Input(node, p) ?? new Expr("?", 4)).ToArray();
                    int precedence = gate == "OR" ? 1 : gate == "XOR" ? 2 : 3;
                    return inputs.Length == 1 ? inputs[0] : new Expr(string.Join(" " + gate + " ", inputs.Select(i => i.In(precedence + (gate == "AND" ? 0 : 1)))), precedence);
                }
                if (node.Kind == "part" && Comparators.TryGetValue(node.Name, out var op) && pin == "out")
                    return And(Input(node, "pre"), new Expr((Input(node, "in1")?.Text ?? "?") + " " + op + " " + (Input(node, "in2")?.Text ?? "?"), 4));
                return new Expr(node.Reference + "." + pin, 4);
            }

            private static Expr And(Expr power, Expr condition)
            {
                if (power == null || power == Expr.True) return condition;
                return new Expr(power.In(3) + " AND " + condition.In(3), 3);
            }

            private string BoxLabel(Node node)
            {
                var info = node.Element.Element(ns + "CallInfo");
                var instance = (info ?? node.Element).Element(ns + "Instance");
                string name = node.Name ?? "?";
                if (node.Kind == "call") name = "\"" + name + "\"";
                var types = node.Element.Elements(ns + "TemplateValue").Where(t => (string)t.Attribute("Type") == "Type").Select(t => t.Value).ToArray();
                if (types.Length > 0) name += "<" + string.Join(", ", types) + ">";
                if (instance != null) return name + " " + Symbol(instance, (string)instance.Attribute("Scope"));
                return node.Kind == "call" ? name : name + "#" + node.Uid;
            }

            private string Operand(XElement access)
            {
                string scope = (string)access.Attribute("Scope");
                var constant = access.Element(ns + "Constant");
                if (constant != null)
                {
                    string value = constant.Element(ns + "ConstantValue")?.Value;
                    if (value != null) return value;
                    string name = (string)constant.Attribute("Name");
                    return scope == "LocalConstant" ? "#" + name : "\"" + name + "\"";
                }
                var address = access.Element(ns + "Address");
                if (address != null) return Absolute(address);
                if (access.Element(ns + "Symbol") != null) return Symbol(access.Element(ns + "Symbol"), scope);
                return "?" + scope;
            }

            private string Symbol(XElement symbol, string scope)
            {
                var text = new StringBuilder();
                bool first = true;
                foreach (var component in symbol.Elements(ns + "Component"))
                {
                    string name = (string)component.Attribute("Name");
                    if (first) text.Append(scope == "GlobalVariable" ? "\"" + name + "\"" : scope == "LocalVariable" || scope == "Undef" ? "#" + name : name);
                    else text.Append('.').Append(name.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : "\"" + name + "\"");
                    first = false;
                    var indices = component.Elements(ns + "Access").Select(Operand).ToArray();
                    if (indices.Length > 0) text.Append('[').Append(string.Join(", ", indices)).Append(']');
                    string slice = (string)component.Attribute("SliceAccessModifier");
                    if (!string.IsNullOrEmpty(slice)) text.Append(".%").Append(slice.ToUpperInvariant());
                }
                var symbolAddress = symbol.Element(ns + "Address");
                if (first && symbolAddress != null) return Absolute(symbolAddress);
                return text.ToString();
            }

            internal static string Absolute(XElement address)
            {
                string area = (string)address.Attribute("Area");
                string prefix = area == "Input" ? "I" : area == "Output" ? "Q" : area == "Memory" ? "M" : area ?? "?";
                if (!int.TryParse((string)address.Attribute("BitOffset"), NumberStyles.None, CultureInfo.InvariantCulture, out int bits)) return "%" + prefix + "?";
                string type = (string)address.Attribute("Type");
                if (type == "Bool" || type == null) return "%" + prefix + (bits / 8).ToString(CultureInfo.InvariantCulture) + "." + (bits % 8).ToString(CultureInfo.InvariantCulture);
                string width = type == "Byte" ? "B" : type == "Word" || type == "Int" ? "W" : "D";
                return "%" + prefix + width + (bits / 8).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
