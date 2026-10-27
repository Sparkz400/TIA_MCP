#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcp.Openness.V21
{
    internal static partial class TypedGraphical
    {
        private static TypedGraphicalProfile SelectProfile(TypedGraphicalNode node, string language, TypedGraphicalContext context)
        {
            bool lad = language == "LAD";
            if ((node.Kind == "powerrail" || node.Kind == "contact" || node.Kind == "coil" || node.Kind == "branch") && !lad
                || (node.Kind == "and" || node.Kind == "or" || node.Kind == "not" || node.Kind == "assign") && lad)
                Fail("GraphicalLanguageMismatch", "Instruction " + node.Kind + " is incompatible with " + language + " semantics.");
            var allKinds = new[] { "powerrail", "contact", "coil", "branch", "and", "or", "not", "assign", "compare", "call", "timer", "counter", "move", "math", "edge" };
            if (!allKinds.Contains(node.Kind, StringComparer.Ordinal)) Fail("GraphicalInstructionUnsupported", "Unsupported typed instruction kind: " + node.Kind);
            if (node.Comparison != null && node.Kind != "compare" || node.CalleePath != null && node.Kind != "call"
                || node.Instruction != null && node.Kind != "timer" && node.Kind != "counter" && node.Kind != "math" && node.Kind != "edge"
                || node.DataType != null && node.Kind != "compare" && node.Kind != "timer" && node.Kind != "counter" && node.Kind != "move" && node.Kind != "math"
                || node.InputCount != 0 && !new[] { "and", "or", "not", "branch" }.Contains(node.Kind))
                Fail("InvalidTypedGraphicalModel", "An instruction contains properties belonging to a different typed family.");
            bool stateful = node.Kind == "timer" || node.Kind == "counter" || node.Kind == "edge" || node.Kind == "call" && ResolveCallee(context, node.CalleePath).BlockType == "FB";
            if ((node.Instance != null || node.AllowSharedInstance) && !stateful) Fail("GraphicalInstanceMismatch", "Instance storage is only valid for FB calls, timers, counters and edge detectors.");
            if (node.Kind == "compare" && (!NumericTypes.Contains(node.DataType ?? "") || !new[] { "eq", "ne", "lt", "le", "gt", "ge" }.Contains(node.Comparison, StringComparer.Ordinal)))
                Fail("GraphicalComparisonUnsupported", "Comparisons require an explicit supported numeric type and eq/ne/lt/le/gt/ge operator; no implicit conversion is performed.");
            if ((node.Kind == "timer" && !new[] { "TON", "TOF", "TP", "TONR" }.Contains(node.Instruction, StringComparer.Ordinal)) || (node.Kind == "counter" && !new[] { "CTU", "CTD", "CTUD" }.Contains(node.Instruction, StringComparer.Ordinal))
                || (node.Kind == "edge" && !new[] { "R_TRIG", "F_TRIG" }.Contains(node.Instruction, StringComparer.Ordinal)))
                Fail("GraphicalInstructionUnsupported", "The stateful instruction variant is not supported.");
            if (node.Kind == "math" && (!NumericTypes.Contains(node.DataType ?? "") || !new[] { "ADD", "SUB", "MUL", "DIV" }.Contains(node.Instruction, StringComparer.Ordinal)))
                Fail("GraphicalArithmeticUnsupported", "Arithmetic requires ADD/SUB/MUL/DIV with one explicit numeric type for every operand and the result; no implicit conversion is performed.");
            if (node.Kind == "move" && !ScalarTypes.Contains(node.DataType ?? ""))
                Fail("GraphicalMoveUnsupported", "MOVE requires one explicit scalar type for its input and output; no implicit conversion is performed.");
            if (node.Negated && node.Kind != "contact") Fail("InvalidTypedGraphicalModel", "Negated is only valid for normally closed LAD contacts; use an explicit FBD not node.");
            var matches = (context.Profiles ?? new TypedGraphicalProfile[0]).Where(p => p != null && !p.ReaderOnly && p.Language == language && p.Kind == node.Kind
                && (p.Instruction ?? "") == (node.Kind == "compare" ? node.Comparison : (node.Instruction ?? ""))
                && (p.DataType ?? "") == (node.DataType ?? "") && p.TiaVersion == context.TiaVersion && p.CpuFamily == context.CpuFamily && p.Route == context.Route).ToArray();
            if (matches.Length == 0 && (node.Kind == "powerrail" || node.Kind == "contact" || node.Kind == "coil" || node.Kind == "assign"))
            {
                if (context.Route != "standard" || context.CpuFamily != "OrderNumber:6ES7 511-1AL03-0AB0/V4.1") Fail("GraphicalProfileUnavailable", "This basic profile is bounded to the observed CPU 1511 firmware V4.1 standard route. Other targets require separately validated instruction profiles.");
                return new TypedGraphicalProfile { Id = "v21." + language.ToLowerInvariant() + "." + node.Kind + ".v1", Language = language, Kind = node.Kind,
                    TiaVersion = "V21", CpuFamily = context.CpuFamily, Route = "standard", NativeName = node.Kind == "contact" ? "Contact" : "Coil", NativeFormatValidated = true,
                    Provenance = "Independently authored SIMATIC SD imported, compiled and exported on V21 Update 2: fixtures/typed-graphical/native/Tgp_Boolean_" + language + ".xml. These flags establish format only, not typed builder qualification.",
                    SourceSha256 = language == "LAD" ? "4748248b114d9d073d78d216a5e2c17ddb38a951fa7deb45da85960ba48eebe5" : "46d326fc9095f8aa149b5851c0db5d7a6bdb8da5a6bc047253fb8fda994aa860" };
            }
            if (matches.Length != 1) Fail("GraphicalProfileUnavailable", "Exactly one independently validated native profile is required for " + language + "/" + node.Kind + "/" + node.Instruction + "/" + node.DataType + " on " + context.CpuFamily + "/" + context.Route + ".");
            var profile = matches[0];
            if (!profile.NativeFormatValidated || string.IsNullOrWhiteSpace(profile.Provenance) || !Regex.IsMatch(profile.SourceSha256 ?? "", "^[0-9a-fA-F]{64}$") || profile.Version != 1)
                Fail("GraphicalProfileUnavailable", "The instruction profile lacks versioned, independently observed native format evidence.");
            if (node.Kind != "call" && node.Kind != "powerrail" && string.IsNullOrWhiteSpace(profile.NativeTemplateXml)) Fail("GraphicalProfileUnavailable", "An independently captured native instruction template is required.");
            return profile;
        }

        private static Dictionary<string, TypedGraphicalPin> BuildPins(TypedGraphicalNode node, TypedGraphicalProfile profile, TypedGraphicalContext context)
        {
            var pins = new List<TypedGraphicalPin>();
            if (node.Kind == "powerrail") pins.Add(Port("out", "output", "Bool"));
            else if (node.Kind == "contact") { pins.Add(Port("in", "input", "Bool")); pins.Add(Port("out", "output", "Bool")); }
            else if (node.Kind == "coil" || node.Kind == "assign") pins.Add(Port("in", "input", "Bool"));
            else if (node.Kind == "and" || node.Kind == "or" || node.Kind == "branch" || node.Kind == "not")
            {
                int cardinality = node.Kind == "not" ? 1 : node.InputCount;
                if (node.Kind == "not" && node.InputCount != 0 && node.InputCount != 1) Fail("InvalidGraphTopology", "A NOT node has exactly one input.");
                if (cardinality < (node.Kind == "not" ? 1 : 2) || cardinality > 32) Fail("InvalidGraphTopology", "Boolean gates and LAD branch joins require 2–32 inputs; NOT requires one.");
                for (int i = 1; i <= cardinality; i++) pins.Add(Port("in" + i, "input", "Bool"));
                pins.Add(Port("out", "output", "Bool"));
            }
            else if (node.Kind == "call")
            {
                var callee = ResolveCallee(context, node.CalleePath);
                foreach (var parameter in callee.Parameters.Where(p => p.DataType != "Void"))
                {
                    string direction = parameter.Section == "Input" ? "input" : parameter.Section == "InOut" ? "inout" : parameter.Section == "Output" || parameter.Section == "Return" ? "output" : null;
                    if (direction == null) Fail("GraphicalCalleeUnsupported", "Callee parameters must have exact Input/Output/InOut/Return sections.");
                    if (!ScalarTypes.Contains(parameter.DataType ?? "")) Fail("GraphicalCalleeUnsupported", "Structured or indirect callee parameters require a separately implemented profile.");
                    pins.Add(new TypedGraphicalPin { Name = parameter.Name, DataType = parameter.DataType, Direction = direction, Required = parameter.Required });
                }
                foreach (var pin in profile.Pins ?? new TypedGraphicalPin[0]) pins.Add(pin);
            }
            else pins.AddRange(profile.Pins ?? new TypedGraphicalPin[0]);
            // A MOVE/arithmetic overload has one exact type: every non-Bool pin carries it, so wires cannot convert.
            if ((node.Kind == "move" || node.Kind == "math") && (pins.Count(p => p?.DataType == node.DataType) < 2 || pins.Any(p => p != null && p.DataType != "Bool" && p.DataType != node.DataType)))
                Fail("GraphicalProfileUnavailable", "The " + node.Kind + " profile pins do not carry the exact requested type " + node.DataType + ".");
            if (pins.Count == 0 || pins.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name) || !new[] { "input", "output", "inout" }.Contains(p.Direction) || !ScalarTypes.Contains(p.DataType ?? ""))
                || pins.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != pins.Count) Fail("GraphicalProfileUnavailable", "The instruction requires a valid, unambiguous typed pin contract.");
            return pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        }
        private static TypedGraphicalPin Port(string name, string direction, string type) => new TypedGraphicalPin { Name = name, Direction = direction, DataType = type, Required = true };

        private static void ValidateConnections(TypedGraphicalModel model, TypedGraphicalContext context, Dictionary<string, TypedGraphicalNode> nodes, Dictionary<string, Dictionary<string, TypedGraphicalPin>> pins, HashSet<string> usedOperands, Dictionary<string, List<TypedGraphicalNode>> instanceUses)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var edges = nodes.Keys.ToDictionary(k => k, k => new List<string>(), StringComparer.Ordinal);
            var writes = new HashSet<string>(StringComparer.Ordinal);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in model.Nodes.Where(n => n.Kind == "coil" || n.Kind == "assign"))
                if (!writes.Add(ResolveOperand(context, node.Operand).Identity)) Fail("GraphicalConflictingOutputs", "More than one instruction writes the same resolved operand.");
            foreach (var edge in model.Connections)
            {
                if (edge == null || edge.From == null || edge.To == null || string.IsNullOrWhiteSpace(edge.From.Node) || string.IsNullOrWhiteSpace(edge.To.Node)) Fail("InvalidGraphTopology", "Connections require explicit source and destination endpoints.");
                if (!unique.Add(EndpointKey(edge.From) + "->" + EndpointKey(edge.To))) Fail("InvalidGraphTopology", "Duplicate graph connections are not accepted.");
                bool fromOperand = edge.From.Node.StartsWith("$", StringComparison.Ordinal), toOperand = edge.To.Node.StartsWith("$", StringComparison.Ordinal);
                if (fromOperand && toOperand) Fail("InvalidGraphTopology", "Direct operand-to-operand wires are not typed instructions.");
                TypedGraphicalPin from = null, to = null;
                TypedGraphicalOperand source = null, destination = null;
                if (fromOperand)
                {
                    source = ResolveOperand(context, edge.From.Node.Substring(1)); usedOperands.Add(edge.From.Node.Substring(1));
                    if (edge.From.Port != "value") Fail("InvalidGraphTopology", "Operand endpoints use the value port.");
                }
                else from = ResolvePin(pins, edge.From);
                if (toOperand)
                {
                    destination = ResolveOperand(context, edge.To.Node.Substring(1)); usedOperands.Add(edge.To.Node.Substring(1));
                    if (edge.To.Port != "value" || !destination.Writable) Fail("OperandNotWritable", "Output connections require the value port of a writable operand.");
                    if (!writes.Add(destination.Identity)) Fail("GraphicalConflictingOutputs", "More than one output writes the same resolved operand.");
                }
                else to = ResolvePin(pins, edge.To);
                if (from != null && from.Direction != "output") Fail("InvalidGraphTopology", "A connection source must be an output pin.");
                if (to != null && to.Direction != "input" && to.Direction != "inout") Fail("InvalidGraphTopology", "A connection destination must be an input or in-out pin.");
                if (to != null && to.Direction == "inout")
                {
                    if (source == null || !source.Writable) Fail("OperandNotWritable", "In-out parameters require a writable resolved variable, not constants or expression outputs.");
                    if (!writes.Add(source.Identity)) Fail("GraphicalConflictingOutputs", "In-out operands cannot have conflicting writers within the network.");
                }
                RequireType(from == null ? source.DataType : from.DataType, to == null ? destination.DataType : to.DataType);
                Count(counts, EndpointKey(edge.From)); Count(counts, EndpointKey(edge.To));
                if (!fromOperand && !toOperand) edges[edge.From.Node].Add(edge.To.Node);
            }
            foreach (var node in model.Nodes)
                foreach (var pin in pins[node.Key].Values)
                {
                    int count; counts.TryGetValue(node.Key + ":" + pin.Name, out count);
                    if ((pin.Direction == "input" || pin.Direction == "inout") && count > 1) Fail("InvalidGraphTopology", "An input pin has more than one source: " + node.Key + ":" + pin.Name);
                    if (pin.Required && count == 0) Fail("GraphicalMissingArgument", "A required instruction pin is unconnected: " + node.Key + ":" + pin.Name);
                }
            var pending = new HashSet<string>(StringComparer.Ordinal); var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in nodes.Keys) Visit(key, edges, pending, done);
            foreach (var users in instanceUses.Values.Where(u => u.Count > 1))
            {
                var reachable = users.ToDictionary(n => n.Key, n => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
                foreach (var user in users) Reach(user.Key, edges, reachable[user.Key]);
                for (int i = 0; i < users.Count; i++) for (int j = i + 1; j < users.Count; j++)
                    if (!reachable[users[i].Key].Contains(users[j].Key) && !reachable[users[j].Key].Contains(users[i].Key))
                        Fail("GraphicalSharedInstanceOrder", "Shared storage requires an explicit graph dependency establishing execution order between every user; independent nodes have no proven order.");
            }
            if (model.Language == "LAD")
            {
                var rails = model.Nodes.Where(n => n.Kind == "powerrail").ToArray();
                if (rails.Length != 1) Fail("InvalidGraphTopology", "A LAD network requires exactly one explicit powerrail.");
                var reachable = new HashSet<string>(StringComparer.Ordinal); Reach(rails[0].Key, edges, reachable);
                foreach (var node in model.Nodes.Where(n => n.Kind == "contact" || n.Kind == "coil" || n.Kind == "branch"))
                    if (!reachable.Contains(node.Key)) Fail("InvalidGraphTopology", "LAD power-flow instruction is disconnected from its powerrail: " + node.Key);
            }
        }
        private static void Count(Dictionary<string, int> counts, string key) { int count; counts.TryGetValue(key, out count); counts[key] = count + 1; }
        private static void Visit(string key, Dictionary<string, List<string>> edges, HashSet<string> pending, HashSet<string> done)
        {
            if (done.Contains(key)) return;
            if (!pending.Add(key)) Fail("InvalidGraphTopology", "Combinational cycles and implicit execution-order feedback are not supported.");
            foreach (string target in edges[key]) Visit(target, edges, pending, done);
            pending.Remove(key); done.Add(key);
        }
        private static void Reach(string key, Dictionary<string, List<string>> edges, HashSet<string> seen) { if (!seen.Add(key)) return; foreach (string target in edges[key]) Reach(target, edges, seen); }
        private static TypedGraphicalPin ResolvePin(Dictionary<string, Dictionary<string, TypedGraphicalPin>> pins, TypedGraphicalEndpoint endpoint)
        {
            Dictionary<string, TypedGraphicalPin> node; TypedGraphicalPin pin;
            if (!pins.TryGetValue(endpoint.Node, out node) || endpoint.Port == null || !node.TryGetValue(endpoint.Port, out pin)) { Fail("InvalidGraphTopology", "Unknown node or pin: " + EndpointKey(endpoint)); return null; }
            return pin;
        }
        private static string EndpointKey(TypedGraphicalEndpoint endpoint) => endpoint.Node + ":" + endpoint.Port;

        private static TypedGraphicalOperand ResolveOperand(TypedGraphicalContext context, string key)
        {
            TypedGraphicalOperand operand;
            if (key == null || !context.Operands.TryGetValue(key, out operand) || operand == null || string.IsNullOrWhiteSpace(operand.Identity)) { Fail("UnresolvedOperand", "No exact resolved declaration is available for operand: " + key); return null; }
            if (!ScalarTypes.Contains(operand.DataType ?? "")) Fail("OperandTypeUnsupported", "Only explicitly resolved scalar types are supported: " + operand.DataType);
            if (operand.Scope == "LiteralConstant")
            {
                if (operand.Writable) Fail("OperandNotWritable", "A constant cannot be writable.");
                ValidateConstant(operand.DataType, operand.Value);
            }
            else
            {
                if ((operand.Scope != "LocalVariable" && operand.Scope != "GlobalVariable") || operand.Components == null || operand.Components.Length == 0 || operand.Components.Length > 32
                    || operand.Components.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 128 || c.IndexOfAny(new[] { '[', ']', '\r', '\n', '"' }) >= 0))
                    Fail("OperandScopeUnsupported", "Operands require an exact local/global symbolic component path; indexing and indirect access are unsupported.");
                if (string.IsNullOrWhiteSpace(operand.DeclarationHash)) Fail("UnresolvedOperand", "Resolved symbolic operands require declaration fingerprint evidence.");
            }
            return operand;
        }
        internal static void ValidateConstant(string type, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128) Fail("InvalidTypedConstant", "A bounded, explicit constant value is required.");
            if (type == "Bool") { if (value != "true" && value != "false") Fail("InvalidTypedConstant", "Bool constants use true or false."); return; }
            if (type == "Real" || type == "LReal")
            {
                double number;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsInfinity(number) || double.IsNaN(number) || type == "Real" && (number > float.MaxValue || number < -float.MaxValue))
                    Fail("InvalidTypedConstant", "The floating-point constant is not finite or is out of range.");
                return;
            }
            if (type == "Time" || type == "LTime")
            {
                if (!Regex.IsMatch(value, type == "Time" ? @"^T#-?[0-9]+ms$" : @"^LT#-?[0-9]+ns$", RegexOptions.CultureInvariant)) Fail("InvalidTypedConstant", "Time constants use explicit T#<milliseconds>ms; LTime uses LT#<nanoseconds>ns.");
                string digits = value.Substring(type == "Time" ? 2 : 3); digits = digits.Substring(0, digits.Length - 2);
                long amount; if (!long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount) || type == "Time" && (amount < int.MinValue || amount > int.MaxValue)) Fail("InvalidTypedConstant", "Time constant is out of range.");
                return;
            }
            decimal integer = 0;
            if (!Regex.IsMatch(value, @"^-?[0-9]+$", RegexOptions.CultureInvariant) || !decimal.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)) Fail("InvalidTypedConstant", "Integer constants require a decimal value without implicit conversion.");
            decimal minimum, maximum;
            switch (type)
            {
                case "SInt": minimum = sbyte.MinValue; maximum = sbyte.MaxValue; break;
                case "Int": minimum = short.MinValue; maximum = short.MaxValue; break;
                case "DInt": minimum = int.MinValue; maximum = int.MaxValue; break;
                case "LInt": minimum = long.MinValue; maximum = long.MaxValue; break;
                case "USInt": case "Byte": minimum = 0; maximum = byte.MaxValue; break;
                case "UInt": case "Word": minimum = 0; maximum = ushort.MaxValue; break;
                case "UDInt": case "DWord": minimum = 0; maximum = uint.MaxValue; break;
                case "ULInt": case "LWord": minimum = 0; maximum = ulong.MaxValue; break;
                default: Fail("InvalidTypedConstant", "Unsupported constant type: " + type); return;
            }
            if (integer < minimum || integer > maximum) Fail("InvalidTypedConstant", "The constant is outside the exact " + type + " range.");
        }
        private static TypedGraphicalCallee ResolveCallee(TypedGraphicalContext context, string path)
        {
            TypedGraphicalCallee callee;
            if (path == null || !context.Callees.TryGetValue(path, out callee) || callee == null || callee.Path != path || string.IsNullOrWhiteSpace(callee.InterfaceHash) || string.IsNullOrWhiteSpace(callee.Name)
                || callee.BlockType != "FC" && callee.BlockType != "FB" || callee.Parameters == null)
            { Fail("GraphicalCalleeUnresolved", "Calls require an exact FC/FB identity and fresh interface declarations."); return null; }
            return callee;
        }
        private static TypedGraphicalInstance ResolveInstance(TypedGraphicalContext context, TypedGraphicalNode node, TypedGraphicalProfile profile)
        {
            TypedGraphicalInstance instance;
            if (node.Instance == null || !context.Instances.TryGetValue(node.Instance, out instance) || instance == null || string.IsNullOrWhiteSpace(instance.Identity) || string.IsNullOrWhiteSpace(instance.DeclarationHash)
                || instance.Components == null || instance.Components.Length == 0 || instance.Components.Any(c => string.IsNullOrWhiteSpace(c) || c.IndexOfAny(new[] { '[', ']', '\r', '\n', '"' }) >= 0)
                || instance.Scope != "LocalVariable" && instance.Scope != "GlobalVariable")
            { Fail("GraphicalInstanceUnresolved", "An exact verified global or multi-instance declaration is required."); return null; }
            if (node.Kind == "call" && instance.CalleePath != node.CalleePath) Fail("GraphicalInstanceMismatch", "The instance declaration does not reference the selected exact FB identity.");
            string storageType = instance.Scope == "GlobalVariable" ? profile.GlobalInstanceType : profile.InstanceType;
            if ((node.Kind == "timer" || node.Kind == "counter" || node.Kind == "edge") && (string.IsNullOrWhiteSpace(storageType) || instance.DataType != storageType))
                Fail("GraphicalInstanceMismatch", "Instruction instance storage must match the selected native profile exactly.");
            return instance;
        }
    }
}
