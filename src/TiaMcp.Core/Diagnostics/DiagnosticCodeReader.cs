using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Read-only native XML operands, bindings and conservative dependency evidence.</summary>
internal static class DiagnosticCodeReader
{
    internal const string Scl = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
    internal const string Graphical = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    internal sealed record Symbol(string Scope, string[] Components)
    {
        internal string Key => Scope + ":" + string.Join("\u001f", Components);
        internal JsonObject Json() => new() { ["scope"] = Scope, ["components"] = D.Strings(Components) };
    }

    internal static Symbol? Operand(XElement? access)
    {
        if (access is null || access.Name.LocalName != "Access" || access.Name.NamespaceName is not (Scl or Graphical)) return null;
        string scope = (string?)access.Attribute("Scope") ?? "";
        if (scope is not ("LocalVariable" or "GlobalVariable")) return null;
        var symbols = access.Elements().ToArray();
        if (symbols.Length != 1 || symbols[0].Name != access.Name.Namespace + "Symbol") return null;
        var components = symbols[0].Elements().ToArray();
        if (components.Length is < 1 or > 16 || components.Any(e => e.Name != access.Name.Namespace + "Component" || e.HasElements
            || string.IsNullOrWhiteSpace((string?)e.Attribute("Name"))
            || e.Attributes().Any(a => a.Name.LocalName is "AccessModifier" or "SimpleAccessModifier" or "SliceAccessModifier" && a.Value is not ("None" or "undef")))) return null;
        return new(scope, components.Select(e => (string)e.Attribute("Name")!).ToArray());
    }

    internal static JsonArray Bindings(DiagnosticEvidence source, string caller, XElement call)
    {
        var result = new JsonArray(); XNamespace ns = call.Name.Namespace;
        foreach (var parameter in call.Elements(ns + "Parameter"))
        {
            string name = (string?)parameter.Attribute("Name") ?? "";
            if (name.Length == 0) continue;
            var row = new JsonObject { ["parameter"] = name, ["direction"] = (string?)parameter.Attribute("Section"),
                ["evidence"] = source.Evidence(caller, parameter), ["resolution"] = "unresolved", ["actual"] = null };
            result.Add(row); XElement? access = null;
            if (ns.NamespaceName == Scl)
            {
                var tokens = parameter.Elements(ns + "Token").Select(e => (string?)e.Attribute("Text")).ToArray();
                string? token = tokens.Length == 1 ? tokens[0] : null;
                row["direction"] = token switch { ":=" => "Input", "=>" => "Output", "<=>" => "InOut", _ => null };
                if (token is ":=" or "=>" or "<=>" && parameter.Elements().All(e =>
                    e.Name == ns + "Access" || e.Name == ns + "Token" || e.Name == ns + "Blank" || e.Name == ns + "NewLine"))
                {
                    var accesses = parameter.Elements(ns + "Access").ToArray(); if (accesses.Length == 1) access = accesses[0];
                }
            }
            else if (ns.NamespaceName == Graphical && call.Parent?.Name == ns + "Call")
            {
                var network = call.Ancestors(ns + "FlgNet").FirstOrDefault(); string? uid = (string?)call.Parent.Attribute("UId");
                var wires = network?.Element(ns + "Wires")?.Elements(ns + "Wire").Where(w => w.Elements(ns + "NameCon")
                    .Any(c => (string?)c.Attribute("UId") == uid && (string?)c.Attribute("Name") == name)).ToArray() ?? [];
                if (wires.Length == 1 && wires[0].Elements().Count() == 2 && wires[0].Elements(ns + "IdentCon").Count() == 1)
                {
                    string? operandId = (string?)wires[0].Element(ns + "IdentCon")!.Attribute("UId");
                    var matches = network?.Element(ns + "Parts")?.Elements(ns + "Access").Where(e => (string?)e.Attribute("UId") == operandId).ToArray() ?? [];
                    if (matches.Length == 1) access = matches[0];
                }
            }
            var symbol = Operand(access);
            if (symbol is null) { row["reason"] = "Expression, literal, array/indirect operand or non-isolated graphical wire; no exact actual storage inferred."; continue; }
            row["resolution"] = "simpleSymbol"; row["actual"] = symbol.Json();
            row["operandEvidence"] = source.Evidence(caller, access!);
        }
        return result;
    }

    internal static JsonArray Dependencies(DiagnosticEvidence source, string blockPath)
    {
        var result = new JsonArray(); var document = source.Xml(blockPath); if (document is null) return result; int references = 0;
        foreach (var network in document.Descendants().Where(e => e.Name.LocalName is "StructuredText" or "FlgNet"))
        {
            if (network.Name.NamespaceName == Scl && network.Name.LocalName == "StructuredText")
            {
                // Every symbolic reference in the compile unit is included as a conservative input,
                // so IF/CASE/loop guards cannot disappear from the proposed slice. No execution semantics claimed.
                var elements = network.Elements().Where(e => e.Name.LocalName is not ("Blank" or "NewLine" or "Comment")).ToArray();
                var all = network.Descendants(network.Name.Namespace + "Access").Select(Operand).OfType<Symbol>().DistinctBy(s => s.Key).ToArray();
                if (all.Length > 256) throw new BridgeException("TraversalLimit", "Compile unit exceeds 256 symbolic dependencies.");
                for (int i = 1; i < elements.Length; i++)
                {
                    if (elements[i].Name != network.Name.Namespace + "Token" || (string?)elements[i].Attribute("Text") != ":=") continue;
                    var output = Operand(elements[i - 1]); if (output is null) continue;
                    references += all.Length; if (references > 5000) throw new BridgeException("TraversalLimit", "Block dependency reference budget exceeded.");
                    result.Add(new JsonObject { ["kind"] = "sclCompileUnitDependencySuperset", ["output"] = output.Json(),
                        ["inputs"] = new JsonArray(all.Where(s => s.Key != output.Key).Select(s => (JsonNode?)s.Json()).ToArray()),
                        ["evidence"] = source.Evidence(blockPath, elements[i - 1]), ["exactDataflow"] = false,
                        ["limits"] = "All symbolic compile-unit references included; execution order, alias effects and called-block side effects remain unknown." });
                }
            }
            else if (network.Name.NamespaceName == Graphical && network.Name.LocalName == "FlgNet")
            {
                var ns = network.Name.Namespace; var parts = network.Element(ns + "Parts"); var wires = network.Element(ns + "Wires");
                if (parts is null || wires is null) continue;
                var all = parts.Elements(ns + "Access").Select(Operand).OfType<Symbol>().DistinctBy(s => s.Key).ToArray();
                if (all.Length > 256) throw new BridgeException("TraversalLimit", "Graphical network exceeds 256 symbolic dependencies.");
                // Only directly wired output parameters are known writers. Arbitrary instruction pin
                // direction is never guessed from a pin name or drawing position.
                foreach (var call in parts.Elements(ns + "Call").Elements(ns + "CallInfo"))
                foreach (var binding in Bindings(source, blockPath, call).OfType<JsonObject>())
                {
                    if (D.Text(binding, "direction") is not ("Output" or "InOut") || binding["actual"] is null) continue;
                    references += all.Length; if (references > 5000) throw new BridgeException("TraversalLimit", "Block dependency reference budget exceeded.");
                    result.Add(new JsonObject { ["kind"] = "graphicalNetworkDependencySuperset", ["output"] = binding["actual"]!.DeepClone(),
                        ["inputs"] = new JsonArray(all.Select(s => (JsonNode?)s.Json()).ToArray()), ["evidence"] = binding["evidence"]!.DeepClone(),
                        ["exactDataflow"] = false, ["limits"] = "All symbolic network references included; unqualified instruction pin directions and side effects remain unknown." });
                }
            }
            if (result.Count > 2000) throw new BridgeException("TraversalLimit", "Too many dependency statements; narrow the saved review.");
        }
        return result;
    }
}
