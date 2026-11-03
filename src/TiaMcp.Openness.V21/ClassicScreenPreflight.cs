using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class ClassicScreenPreflight
    {
        internal static IEnumerable<XElement> Items(XDocument document) => document.Descendants("Hmi.Screen.ScreenLayer").Elements("ObjectList").Elements();
        internal static string Attribute(XElement item, string name) => (string)item.Element("AttributeList")?.Element(name);
        internal static HashSet<string> References(XDocument document) => new HashSet<string>(document.Descendants("Tag").Elements("Name").Select(x => x.Value), StringComparer.Ordinal);
        internal static HashSet<string> NativeTags(IEnumerable<XDocument> tables)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tag in tables.SelectMany(d => d.Descendants("Hmi.Tag.Tag")))
            {
                string name = Attribute(tag, "Name");
                if (string.IsNullOrEmpty(name)) continue;
                Walk(tag, name, result); Walk(tag, "\"" + name + "\"", result);
            }
            return result;
        }
        private static void Walk(XElement tag, string path, HashSet<string> result)
        {
            result.Add(path);
            foreach (var child in tag.Element("ObjectList")?.Elements() ?? Enumerable.Empty<XElement>())
                if (child.Name.LocalName == "Hmi.Tag.TagStructureMember" || child.Name.LocalName == "Hmi.Tag.TagArrayMember")
                {
                    string name = Attribute(child, "Name");
                    if (!string.IsNullOrEmpty(name)) Walk(child, path + (name.StartsWith("[", StringComparison.Ordinal) ? "" : ".") + name, result);
                }
        }
        internal static JObject Check(XDocument candidate, XDocument original, IEnumerable<XDocument> baseline, ISet<string> knownTags)
        {
            var root = HmiPreservationRules.ClassicImportRoot(candidate, HmiPreservationRules.ClassicImportRoot(original));
            if (root.Name.LocalName != "Hmi.Screen.Screen") throw new ArgumentException("Preflight accepts regular Classic screens only.");
            var documents = baseline.ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in candidate.Descendants().Attributes("ID"))
                if (!ids.Add(id.Value)) throw new ArgumentException("Duplicate native XML ID: " + id.Value);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var warnings = new JArray();
            int width = Number(root, "Width"), height = Number(root, "Height");
            var graphics = documents.SelectMany(Items).Where(x => x.Name.LocalName == "Hmi.Screen.GraphicIOField")
                .GroupBy(GraphicName).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(Size)), StringComparer.Ordinal);
            var languages = new HashSet<string>(original.Descendants("MultilingualTextItem").Select(x => Attribute(x, "Culture")).Where(x => x != null), StringComparer.Ordinal);
            foreach (var item in Items(candidate))
            {
                string name = Attribute(item, "ObjectName");
                if (string.IsNullOrEmpty(name) || !names.Add(name)) throw new ArgumentException("Missing or duplicate screen-item name: " + name);
                int left = Number(item, "Left"), top = Number(item, "Top"), w = Number(item, "Width"), h = Number(item, "Height");
                if (left < 0 || top < 0 || w < 1 || h < 1 || (long)left + w > width || (long)top + h > height)
                    throw new ArgumentException("Item lies outside the native screen rectangle: " + name);
                var compositions = item.Element("ObjectList")?.Elements().ToArray() ?? Array.Empty<XElement>();
                int font = Array.FindIndex(compositions, x => x.Name.LocalName.StartsWith("Hmi.Globalization.Font", StringComparison.Ordinal) || x.Name.LocalName == "MultilingualText");
                if (font >= 0 && compositions.Skip(font).Any(x => x.Name.LocalName == "Hmi.Dynamic.VisibilityAnimation"))
                    throw new ArgumentException("Visibility animations must precede font/caption compositions: " + name + ". Use hmi_classic_screen_compose to normalize that known native ordering.");
                if (item.Name.LocalName == "Hmi.Screen.GraphicIOField"
                    && (!graphics.TryGetValue(GraphicName(item), out var sizes) || !sizes.Contains(Size(item))))
                    throw new ArgumentException("Graphic dimensions lack a matching native screen prototype: " + name + ". Preserve a known size to avoid resize warnings.");
                var cultures = new HashSet<string>(item.Descendants("MultilingualTextItem").Select(x => Attribute(x, "Culture")), StringComparer.Ordinal);
                if (cultures.Count > 0 && languages.Except(cultures).Any())
                    warnings.Add(new JObject { ["item"] = name, ["code"] = "MissingTranslation", ["cultures"] = new JArray(languages.Except(cultures)) });
            }
            var inherited = new HashSet<string>(documents.SelectMany(References), StringComparer.Ordinal);
            var unresolved = References(candidate).Where(r => !inherited.Contains(r) && !knownTags.Contains(r)).ToArray();
            if (unresolved.Length != 0) throw new ArgumentException("Unresolved native HMI tag references (array indices are taken from HMI exports, never inferred from PLC indices): " + string.Join(", ", unresolved.Take(16)));
            return new JObject { ["itemCount"] = names.Count, ["tagReferenceCount"] = References(candidate).Count, ["warnings"] = warnings,
                ["nativeCompileRequired"] = true, ["visualRuntimeVerified"] = false };
        }
        internal static int Number(XElement item, string name)
        {
            if (!int.TryParse(Attribute(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new ArgumentException("Missing/invalid native rectangle field " + name + ".");
            return value;
        }
        private static string GraphicName(XElement item) => (string)item.Element("LinkList")?.Element("PictureList")?.Element("Name") ?? "";
        private static string Size(XElement item) => Attribute(item, "Width") + "x" + Attribute(item, "Height");
    }
}
