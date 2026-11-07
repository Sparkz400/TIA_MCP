using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // A declaration-only import does not need unrelated blocks' generated checksums.
    // This policy is deliberately separate from the full-project recovery policy.
    internal static class TagTableImportRules
    {
        internal const string Kind = "tag_table_import_xml";
        internal const string Policy = "tag-table-content-and-project-metadata-v1";
        internal const string Coverage = "Native tag-table contents in the selected software scope; project save state, hardware/network identities and software/type/block metadata. Block payloads and unexposed vendor data are not covered. Whole-project automatic recovery and outcome reconciliation are not authorized by this scoped fingerprint.";

        internal static string Canonical(XElement element)
        {
            var clone = new XElement(element);
            clone.DescendantsAndSelf().Attributes("ID").Remove();
            return Normalize(clone).ToString(SaveOptions.DisableFormatting);
        }

        private static XElement Normalize(XElement element)
        {
            var result = new XElement(element.Name, element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal));
            if (!element.HasElements) { result.Value = element.Value; return result; }
            // Native export order and serialization IDs are not tag identities.
            result.Add(element.Elements().Select(Normalize).OrderBy(e => e.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal));
            return result;
        }

        internal static XElement[] Tables(XDocument document)
        {
            EngineeringSources.NamedXmlObjects(document, "SW.Tags.PlcTagTable");
            return document.Root.Elements("SW.Tags.PlcTagTable").ToArray();
        }

        internal static void Validate(XDocument document)
        {
            foreach (var table in Tables(document)) Members(table);
        }

        internal static void RequireExistingMembers(XElement before, XElement requested)
        {
            if (before == null) return;
            var incoming = Members(requested);
            foreach (var member in Members(before))
                if (!incoming.TryGetValue(member.Key, out var replacement) || member.Value.Name != replacement.Name)
                    throw new BridgeException("TagScopeMismatch", "Native override replaces the table. Include every existing tag/constant with its original kind; implicit deletion or rename is refused: " + member.Key);
        }

        private static string Name(XElement member) => (string)member.Element("AttributeList")?.Element("Name");
        private static Dictionary<string, XElement> Members(XElement table)
        {
            var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in table.Element("ObjectList")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (member.Name == "MultilingualText") continue;
                if (member.Name != "SW.Tags.PlcTag" && member.Name != "SW.Tags.PlcUserConstant" && member.Name != "SW.Tags.PlcSystemConstant")
                    throw new BridgeException("TagScopeMismatch", "Unsupported object in tag-table import: " + member.Name);
                string name = Name(member);
                if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name)) throw new BridgeException("TagScopeMismatch", "Every table member needs a unique name.");
                result.Add(name, member);
            }
            return result;
        }

        internal static void Verify(XElement before, XElement requested, XElement after)
        {
            var old = before == null ? new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase) : Members(before);
            var incoming = Members(requested); var actual = Members(after);
            RequireExistingMembers(before, requested);
            if (!new HashSet<string>(incoming.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(actual.Keys))
                throw new BridgeException("ImportVerificationFailed", "Native tag import added, renamed or removed unexpected members.");
            foreach (var member in old.Where(m => Canonical(m.Value) == Canonical(incoming[m.Key])))
                if (Canonical(member.Value) != Canonical(actual[member.Key]))
                    throw new BridgeException("NativePreservationMismatch", "Unrequested table member changed: " + member.Key);
            foreach (var member in incoming)
            {
                var native = actual[member.Key];
                if (native.Name != member.Value.Name) throw new BridgeException("ImportVerificationFailed", "Tag/constant kind changed: " + member.Key);
                RequireRequested(member.Value, native);
            }
            RequireRequested(requested.Element("AttributeList"), after.Element("AttributeList"));
            var requestedTexts = requested.Element("ObjectList")?.Elements("MultilingualText").ToArray() ?? Array.Empty<XElement>();
            var nativeTexts = after.Element("ObjectList")?.Elements("MultilingualText").ToArray() ?? Array.Empty<XElement>();
            foreach (var text in requestedTexts)
            {
                var matches = nativeTexts.Where(t => (string)t.Attribute("CompositionName") == (string)text.Attribute("CompositionName")).ToArray();
                if (matches.Length != 1) throw new BridgeException("ImportVerificationFailed", "Requested table comment/title is missing or ambiguous.");
                RequireRequested(text, matches[0]);
            }
            foreach (var text in before?.Element("ObjectList")?.Elements("MultilingualText") ?? Enumerable.Empty<XElement>())
                if (!requestedTexts.Any(t => (string)t.Attribute("CompositionName") == (string)text.Attribute("CompositionName")))
                {
                    var matches = nativeTexts.Where(t => (string)t.Attribute("CompositionName") == (string)text.Attribute("CompositionName")).ToArray();
                    if (matches.Length != 1 || Canonical(text) != Canonical(matches[0]))
                        throw new BridgeException("NativePreservationMismatch", "An unrequested table comment/title changed.");
                }
        }

        // Read back every supplied value, including multilingual comments. Native
        // defaults may add fields; the requested fields cannot disappear or change.
        private static void RequireRequested(XElement requested, XElement actual)
        {
            if (requested == null) return;
            if (actual == null || requested.Name != actual.Name) throw new BridgeException("ImportVerificationFailed", "A requested native element is missing.");
            foreach (var attr in requested.Attributes().Where(a => a.Name != "ID"))
                if ((string)actual.Attribute(attr.Name) != attr.Value) throw new BridgeException("ImportVerificationFailed", "Requested native attribute differs: " + attr.Name);
            if (!requested.HasElements)
            {
                if (requested.Value != actual.Value) throw new BridgeException("ImportVerificationFailed", "Requested native value differs: " + requested.Name);
                return;
            }
            foreach (var child in requested.Elements())
            {
                var candidates = actual.Elements(child.Name).Where(e => (string)e.Attribute("CompositionName") == (string)child.Attribute("CompositionName"));
                string culture = (string)child.Element("AttributeList")?.Element("Culture");
                if (culture != null) candidates = candidates.Where(e => (string)e.Element("AttributeList")?.Element("Culture") == culture);
                var matches = candidates.ToArray();
                if (matches.Length != 1) throw new BridgeException("ImportVerificationFailed", "Requested native element is missing or ambiguous: " + child.Name);
                RequireRequested(child, matches[0]);
            }
        }

        internal static bool NoNewCompileProblems(JObject before, JObject after) => CompileBaselineRules.NoNewProblems(before, after);
    }
}
