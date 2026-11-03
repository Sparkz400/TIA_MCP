using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    // Copies native components, including their events/permissions/unknown fields.
    // Every requested replacement is explicit; there is no inferred PLC index conversion.
    internal static class ClassicScreenComposer
    {
        internal static XDocument Compose(XDocument original, IReadOnlyDictionary<string, XDocument> sources, JObject request)
        {
            if (request["components"] != null && !(request["components"] is JArray) || request["removeItems"] != null && !(request["removeItems"] is JArray))
                throw new ArgumentException("components and removeItems must be arrays.");
            var result = new XDocument(original);
            var components = request["components"] as JArray ?? new JArray();
            if (components.Count > 256 || components.Any(x => !(x is JObject))) throw new ArgumentException("At most 256 component objects may be composed per request.");
            var remove = request["removeItems"] as JArray ?? new JArray();
            if (remove.Count > 256 || remove.Any(x => x.Type != JTokenType.String)) throw new ArgumentException("removeItems must contain at most 256 names.");
            foreach (string name in remove.Values<string>())
            {
                var matches = ClassicScreenPreflight.Items(result).Where(i => ClassicScreenPreflight.Attribute(i, "ObjectName") == name).ToArray();
                if (matches.Length != 1) throw new ArgumentException("removeItems must identify exactly one existing item: " + name);
                matches[0].Remove();
            }
            foreach (var component in components.OfType<JObject>())
            {
                if (component.Properties().Any(p => !new[] { "sourceScreenPath", "sourceItem", "name", "layer", "attributes", "tagReplacements", "texts" }.Contains(p.Name)))
                    throw new ArgumentException("Unknown component field.");
                foreach (string field in new[] { "attributes", "tagReplacements", "texts" })
                    if (component[field] != null && !(component[field] is JObject)) throw new ArgumentException(field + " must be an object.");
                string path = component.Value<string>("sourceScreenPath"), name = component.Value<string>("sourceItem");
                if (path == null || !sources.TryGetValue(path, out var source)) throw new ArgumentException("Component source must be a screen in the captured HMI scope.");
                var matches = ClassicScreenPreflight.Items(source).Where(i => ClassicScreenPreflight.Attribute(i, "ObjectName") == name).ToArray();
                if (matches.Length != 1) throw new ArgumentException("sourceItem must resolve uniquely in its native screen.");
                var item = new XElement(matches[0]);
                var attributes = item.Element("AttributeList");
                string newName = component.Value<string>("name");
                if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("A new item name is required.");
                attributes.SetElementValue("ObjectName", newName);
                if (component["attributes"] is JObject values)
                    foreach (var value in values.Properties())
                    {
                        // Existing scalar attributes only. Event, link and permission trees cannot be replaced through this convenience adapter.
                        var field = attributes.Element(value.Name);
                        if (field == null || field.HasElements || value.Value is JContainer || value.Name == "ObjectName")
                            throw new ArgumentException("Only existing scalar native item attributes may be set: " + value.Name);
                        field.Value = value.Value.ToString();
                    }
                if (component["tagReplacements"] is JObject replacements)
                    foreach (var replacement in replacements.Properties())
                    {
                        var fields = item.Descendants("Tag").Elements("Name").Where(x => x.Value == replacement.Name).ToArray();
                        if (fields.Length == 0 || replacement.Value.Type != JTokenType.String) throw new ArgumentException("A tag replacement must match an existing native reference.");
                        foreach (var field in fields) field.Value = (string)replacement.Value;
                    }
                if (component["texts"] is JObject texts)
                    foreach (var text in texts.Properties())
                    {
                        var fields = item.Descendants("MultilingualTextItem").Where(x => ClassicScreenPreflight.Attribute(x, "Culture") == text.Name)
                            .Select(x => x.Element("AttributeList")?.Element("Text")).ToArray();
                        if (fields.Length != 1 || fields[0] == null || fields[0].HasElements || text.Value.Type != JTokenType.String)
                            throw new ArgumentException("Text replacement requires one plain native caption for culture " + text.Name + "; formatted or ambiguous text is retained.");
                        fields[0].Value = (string)text.Value;
                    }
                int layerIndex = component.Value<int?>("layer") ?? 0;
                var layers = result.Descendants("Hmi.Screen.ScreenLayer").ToArray();
                if (layerIndex < 0 || layerIndex >= layers.Length) throw new ArgumentException("Select an existing native screen layer index.");
                var list = layers[layerIndex].Element("ObjectList");
                if (list == null) { list = new XElement("ObjectList"); layers[layerIndex].Add(list); }
                list.Add(item);
            }
            foreach (var item in ClassicScreenPreflight.Items(result))
            {
                var list = item.Element("ObjectList");
                var anchor = list?.Elements().FirstOrDefault(x => x.Name.LocalName.StartsWith("Hmi.Globalization.Font", StringComparison.Ordinal) || x.Name.LocalName == "MultilingualText");
                if (anchor == null) continue;
                foreach (var animation in anchor.ElementsAfterSelf("Hmi.Dynamic.VisibilityAnimation").ToArray()) { animation.Remove(); anchor.AddBeforeSelf(animation); }
            }
            // Only the proven open-link form is accepted when regenerating export-local IDs.
            if (result.Descendants().Attributes("TargetID").Any(a => a.Value != "@OpenLink"))
                throw new ArgumentException("Local TargetID links require a preserving remapper; this composer supports native @OpenLink components only.");
            int id = 0;
            foreach (var attribute in result.Descendants().Attributes("ID")) attribute.Value = (id++).ToString("X");
            return result;
        }
    }
}
