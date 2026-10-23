#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class TechnologyEntry { internal TechnologicalInstanceDB Item; internal string Path, OwnerPath, Composition; }

        private static TechnologyEntry TechnologyEntryFor(TechnologicalInstanceDB item, string ownerPath, string composition)
            => new TechnologyEntry { Item = item, OwnerPath = ownerPath, Composition = composition,
                Path = ownerPath + (composition == "TechnologicalObjects" ? "" : "/children/" + composition) + "/" + Uri.EscapeDataString(item.Name ?? "") };

        private static IEnumerable<TechnologyEntry> TechnologyOwnedChildren(TechnologyEntry owner)
        {
            var container = owner.Item.GetService<OutputCamMeasuringInputContainer>();
            if (container == null) yield break;
            foreach (string composition in new[] { "OutputCams", "MeasuringInputs" })
            {
                var children = composition == "OutputCams" ? container.OutputCams : container.MeasuringInputs;
                if (children == null) throw new BridgeException("TraversalIncomplete", "Technology child composition returned no collection.");
                foreach (var child in children) yield return TechnologyEntryFor(child, owner.Path, composition);
            }
        }

        private static IEnumerable<TechnologyEntry> TechnologyTree(TechnologyEntry entry, ScanBudget budget, int depth)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            if (!budget.Take()) yield break;
            yield return entry;
            foreach (var child in TechnologyOwnedChildren(entry))
            {
                foreach (var descendant in TechnologyTree(child, budget, depth + 1)) yield return descendant;
                if (budget.Truncated) yield break;
            }
        }

        private static IEnumerable<TechnologyEntry> TechnologyObjects(TechnologicalInstanceDBGroup group, string path, ScanBudget budget, int depth = 0)
        {
            if (depth > 64) { budget.Truncated = true; yield break; }
            foreach (var item in group.TechnologicalObjects)
            {
                foreach (var entry in TechnologyTree(TechnologyEntryFor(item, path, "TechnologicalObjects"), budget, depth)) yield return entry;
                if (budget.Truncated) yield break;
            }
            foreach (var child in group.Groups)
            {
                if (!budget.Take()) yield break;
                foreach (var entry in TechnologyObjects(child, path + "/groups/" + Uri.EscapeDataString(child.Name ?? ""), budget, depth + 1)) yield return entry;
            }
        }

        private static TechnologyEntry FindTechnologyEntry(TechnologicalInstanceDBGroup group, string rootPath, JObject payload, bool required)
        {
            string path = payload["technologyPath"] == null ? null : ChangeText(payload, "technologyPath", 4096);
            string name = payload["name"] == null ? null : ChangeText(payload, "name", 128);
            if (path == null && name == null) throw new BridgeException("InvalidRequest", "An exact technologyPath or a unique legacy name is required.");
            if (path != null && !path.StartsWith(rootPath + "/", StringComparison.Ordinal))
                throw new BridgeException("TargetMismatch", "technologyPath is outside the selected PLC technology group tree.");
            var budget = new ScanBudget(50000);
            var matches = TechnologyObjects(group, rootPath, budget).Where(entry => path != null
                ? string.Equals(entry.Path, path, StringComparison.Ordinal)
                : string.Equals(entry.Item.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1) throw new BridgeException("AmbiguousTarget", "Technology selector matches more than one object; use an exact unambiguous path.");
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete technology selection within the scan bound.");
            if (matches.Length == 0)
            {
                if (required) throw new BridgeException("TechnologyObjectNotFound", path ?? name);
                return null;
            }
            if (path != null && name != null && !string.Equals(matches[0].Item.Name, name, StringComparison.Ordinal))
                throw new BridgeException("TargetMismatch", "name does not exactly match the object at technologyPath.");
            return matches[0];
        }

        private static TechnologyEntry BindTechnologySelection(TechnologicalInstanceDBGroup group, string rootPath, JObject payload)
        {
            var entry = FindTechnologyEntry(group, rootPath, payload, true);
            // The resolved path becomes reviewed intent before hashing the preview; later lookups never fall back to the name.
            payload["technologyPath"] = entry.Path;
            payload["name"] = entry.Item.Name;
            ChangeText(payload, "technologyPath", 4096);
            return entry;
        }

        private static void DeleteTechnologySelection(TechnologicalInstanceDBGroup group, string rootPath, JObject payload)
        {
            var entry = FindTechnologyEntry(group, rootPath, payload, true);
            entry.Item.Delete();
            if (FindTechnologyEntry(group, rootPath, new JObject { ["technologyPath"] = entry.Path }, false) != null)
                throw new BridgeException("MutationVerificationFailed", "The selected technology path remains after deletion; checkpoint recovery is required.");
        }

        private static JArray TechnologyIdentityFingerprint(TechnologicalInstanceDBGroup group, string rootPath)
        {
            var budget = new ScanBudget(50000);
            var rows = new JArray(TechnologyObjects(group, rootPath, budget).Select(entry => new JObject {
                ["path"] = entry.Path, ["ownerPath"] = entry.OwnerPath, ["composition"] = entry.Composition,
                ["name"] = entry.Item.Name, ["number"] = entry.Item.Number,
                ["typeIdentifier"] = entry.Item.OfSystemLibElement, ["version"] = entry.Item.OfSystemLibVersion.ToString(),
                ["modified"] = entry.Item.ModifiedDate.ToString("O") }));
            if (budget.Truncated) throw new BridgeException("TraversalLimit", "Cannot establish a complete technology identity fingerprint within the scan bound.");
            return rows;
        }
    }
}
