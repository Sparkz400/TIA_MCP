using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Existing-caller preservation under the insertion-ID scope decision of 25 October 2026
    /// (docs/plans/insertion-id-renumbering-decision-20261025.md): V21 shifts every existing object ID after an
    /// inserted network by the inserted unit's ID count. Exactly that shift is accepted; every other change fails closed.</summary>
    internal static class PlcComponentCallerInsertion
    {
        /// <summary>Returns null when V21's renumbering is predictable: every ID is uppercase hex, consecutive in document order.</summary>
        internal static string ShiftBlocker(XDocument caller)
        {
            var ids = caller.Descendants().Attributes("ID").Select(a => Parse(a.Value)).ToArray();
            return ids.Length == 0 || ids.Any(id => id < 0) || ids.Where((id, index) => id != ids[0] + index).Any()
                ? "Caller object IDs are not consecutive uppercase hex in document order, so V21's renumbering after the inserted network cannot be predicted exactly."
                : null;
        }

        /// <summary>Removes the one network inserted after <paramref name="insertAfterNetworkId"/>, reverses the following
        /// ID shift and requires the complete original caller back. Throws PlcComponentException otherwise.</summary>
        internal static JObject Verify(XDocument original, XDocument installed, string insertAfterNetworkId)
        {
            if (original?.Root == null || installed?.Root == null) Fail("Both original and installed caller exports are required.");
            if (original.Root.Descendants("SW.Blocks.CompileUnit").Count(e => (string)e.Attribute("ID") == insertAfterNetworkId) != 1) Fail("The original caller lacks the exact insertion network.");
            var copy = new XDocument(installed);
            var ids = copy.Descendants().Attributes("ID").Select(a => Parse(a.Value)).ToArray();
            if (ids.Any(id => id < 0) || ids.Distinct().Count() != ids.Length) Fail("Installed caller IDs are malformed, aliased or colliding.");
            var targets = copy.Descendants("SW.Blocks.CompileUnit").Where(e => (string)e.Attribute("ID") == insertAfterNetworkId).ToArray();
            var inserted = targets.Length == 1 ? targets[0].ElementsAfterSelf().FirstOrDefault() : null;
            if (inserted?.Name != "SW.Blocks.CompileUnit") Fail("Exactly one network must follow the insertion network directly.");
            var own = inserted.DescendantsAndSelf().Attributes("ID").ToArray();
            var following = copy.Descendants().Where(e => XNode.DocumentOrderComparer.Compare(e, inserted) > 0 && !e.Ancestors().Contains(inserted)).Attributes("ID").ToArray();
            var shifts = new List<string>();
            foreach (var attribute in following)
            {
                int restored = Parse(attribute.Value) - own.Length;
                if (restored < 0) Fail("A following ID is lower than the inserted unit's ID count.");
                string before = restored.ToString("X", CultureInfo.InvariantCulture);
                shifts.Add(before + "->" + attribute.Value); attribute.Value = before;
            }
            string insertedId = (string)inserted.Attribute("ID");
            // Native exports are read with whitespace preserved; the inserted network brings its own indentation.
            if (inserted.PreviousNode is XText indent && string.IsNullOrWhiteSpace(indent.Value)) indent.Remove();
            inserted.Remove();
            if (!XNode.DeepEquals(original.Root, copy.Root) || original.Declaration?.ToString() != copy.Declaration?.ToString())
                Fail("Reversing the one following-ID shift does not recover the complete original caller.");
            return new JObject { ["verified"] = true, ["policy"] = "insertion-following-id-shift-v1", ["insertAfterNetworkId"] = insertAfterNetworkId, ["insertedNetworkId"] = insertedId,
                ["insertedObjectIds"] = own.Length, ["shiftedObjectIds"] = new JArray(shifts) };
        }

        private static int Parse(string value)
        {
            int parsed;
            return value.Length > 0 && value.Length <= 7 && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F') && (value == "0" || value[0] != '0')
                && int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed) ? parsed : -1;
        }

        private static void Fail(string message) => throw new PlcComponentException("ComponentCallerPreservationMismatch", message);
    }
}
