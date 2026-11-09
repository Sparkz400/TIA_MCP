#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Opt-in prediction of V21's UId renumbering for a graphical_network_patch that adds or removes nodes.
    /// V21 renumbers a network's UIds on import: Access parts first in their given order, instructions in dependency post-order
    /// (each instruction after the instructions feeding it, by destination input: en/pre, in, in1..inN), wires in their given order,
    /// then every UId definition in document order from 21. This is the rule the qualified typed editor already relies on
    /// (TypedGraphical.CanonicalizeNetworkIds). Applying it at prepare time lets the exact readback check succeed when the
    /// prediction holds; when it does not, apply still fails closed with NativePreservationMismatch. Shapes whose order cannot
    /// be derived (cycles, several new terminal instructions, multiple instruction inputs on other pins) are refused before apply.</summary>
    internal static class GraphicalUidRenumbering
    {
        private const string FlgNet = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        private static readonly Regex InputPin = new Regex(@"^in(\d*)$", RegexOptions.CultureInvariant);

        internal static JObject Predict(XDocument document, string networkId, ICollection<string> originalUids)
        {
            XNamespace ns = FlgNet;
            var unit = document.Descendants("SW.Blocks.CompileUnit").Single(e => (string)e.Attribute("ID") == networkId);
            var network = unit.Element("AttributeList").Element("NetworkSource").Element(ns + "FlgNet");
            var parts = network.Element(ns + "Parts");
            var wires = network.Element(ns + "Wires");
            var accesses = parts.Elements(ns + "Access").ToList();
            var instructions = parts.Elements().Where(e => e.Name != ns + "Access").ToDictionary(e => (string)e.Attribute("UId"), StringComparer.Ordinal);
            // Instruction edges: destination UId -> (destination pin, source UId).
            var inputs = instructions.Keys.ToDictionary(k => k, k => new List<Tuple<string, string>>(), StringComparer.Ordinal);
            var hasSuccessor = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wire in wires.Elements(ns + "Wire"))
            {
                var ends = wire.Elements().ToArray();
                if (ends.Length < 2 || ends[0].Name != ns + "NameCon" || !instructions.ContainsKey((string)ends[0].Attribute("UId"))) continue;
                string source = (string)ends[0].Attribute("UId");
                foreach (var sink in ends.Skip(1).Where(e => e.Name == ns + "NameCon" && instructions.ContainsKey((string)e.Attribute("UId"))))
                {
                    inputs[(string)sink.Attribute("UId")].Add(Tuple.Create((string)sink.Attribute("Name"), source));
                    hasSuccessor.Add(source);
                }
            }
            var sinks = instructions.Keys.Where(k => !hasSuccessor.Contains(k)).OrderBy(Number).ToList();
            if (sinks.Count > 1 && sinks.Any(s => !originalUids.Contains(s)))
                Refuse("A new terminal instruction next to other terminal instructions has no derivable position in V21's order.");
            var ordered = new List<XElement>(accesses);
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            Action<string> append = null;
            append = uid =>
            {
                if (emitted.Contains(uid)) return;
                if (!active.Add(uid)) Refuse("Instruction dependencies form a cycle.");
                var feeding = inputs[uid];
                if (feeding.Count > 1 && feeding.Any(f => PinRank(f.Item1) == int.MaxValue))
                    Refuse("Instruction " + uid + " has several instruction inputs on pins whose native order is not known.");
                foreach (var edge in feeding.OrderBy(f => PinRank(f.Item1)).ThenBy(f => Number(f.Item2))) append(edge.Item2);
                ordered.Add(instructions[uid]);
                active.Remove(uid); emitted.Add(uid);
            };
            foreach (var sink in sinks) append(sink);
            if (emitted.Count != instructions.Count) Refuse("Every instruction must reach a terminal instruction.");
            parts.ReplaceNodes(ordered);
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            int next = 21;
            foreach (var definition in network.Descendants().Where(IsDefinition)) remap[(string)definition.Attribute("UId")] = (next++).ToString(CultureInfo.InvariantCulture);
            foreach (var attribute in network.Descendants().Attributes("UId"))
            {
                if (!remap.TryGetValue(attribute.Value, out string value)) Refuse("Connector UId " + attribute.Value + " has no definition.");
                attribute.Value = value;
            }
            var changes = new JArray(remap.Where(p => p.Key != p.Value).Select(p => new JObject
                { ["from"] = p.Key, ["to"] = p.Value, ["existing"] = originalUids.Contains(p.Key) }));
            return new JObject
            {
                ["networkId"] = networkId, ["applied"] = true, ["qualified"] = false, ["changes"] = changes,
                ["rule"] = "Access parts first in given order, instructions in dependency post-order by destination input (en/pre, in, in1..inN), wires in given order, UIds from 21 in document order.",
                ["note"] = "Predicted, not natively qualified for patches. The exact readback check still decides: a wrong prediction fails with NativePreservationMismatch after apply and needs checkpoint recovery. Try it on a project_copy first.",
            };
        }

        private static bool IsDefinition(XElement element) => element.Attribute("UId") != null && element.Name.LocalName != "IdentCon" && element.Name.LocalName != "NameCon";

        private static int PinRank(string pin)
        {
            if (pin == "en" || pin == "pre") return -1;
            var match = InputPin.Match(pin ?? "");
            if (!match.Success) return int.MaxValue;
            return match.Groups[1].Value.Length == 0 ? 0 : int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static long Number(string uid) => long.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : long.MaxValue;

        private static void Refuse(string reason) => throw new BridgeException("UidRenumberingUnpredictable",
            reason + " Use graphical_typed_edit replaceNetwork for supported networks, or block_import_xml of the whole block.");
    }
}
