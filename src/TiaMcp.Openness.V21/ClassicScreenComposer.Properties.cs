using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static partial class ClassicScreenComposer
    {
        private static void Fields(JObject value, params string[] allowed)
        {
            if (value == null || value.Properties().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Unknown or missing native composition field.");
        }
        private static JArray ObjectRows(JObject value, string field, int max)
        {
            if (value[field] == null) return new JArray();
            if (!(value[field] is JArray rows) || rows.Count > max || rows.Any(r => !(r is JObject))) throw new ArgumentException(field + " must be a bounded array of objects.");
            return rows;
        }
        private static XElement Unique(IEnumerable<XElement> elements, string label)
        {
            var matches = elements.ToArray();
            if (matches.Length != 1) throw new ArgumentException(label + " must resolve to exactly one native field.");
            return matches[0];
        }
        private static XElement Prototype(IReadOnlyDictionary<string, XDocument> sources, JObject request)
        {
            string path = request.Value<string>("sourceScreenPath");
            if (path == null || !sources.TryGetValue(path, out var document)) throw new ArgumentException("Source must be an exact captured screen.");
            return Unique(ClassicScreenPreflight.Items(document).Where(i => ClassicScreenPreflight.Attribute(i, "ObjectName") == request.Value<string>("sourceItem")), "sourceItem");
        }
        private static void AddLayers(XDocument result, IReadOnlyDictionary<string, XDocument> sources, JObject request)
        {
            foreach (JObject spec in ObjectRows(request, "layers", 32))
            {
                Fields(spec, "sourceScreenPath", "sourceLayer");
                string path = spec.Value<string>("sourceScreenPath");
                if (path == null || !sources.TryGetValue(path, out var source) || spec["sourceLayer"]?.Type != JTokenType.Integer) throw new ArgumentException("Layer source and index are required.");
                int index = spec.Value<int>("sourceLayer");
                var layers = source.Descendants("Hmi.Screen.ScreenLayer").ToArray();
                if (index < 0 || index >= layers.Length) throw new ArgumentException("Layer source index is outside the captured screen.");
                var layer = new XElement(layers[index]);
                layer.Element("ObjectList")?.Elements().Remove(); // Retain shell attributes/links and annotations.
                var root = Unique(result.Descendants("Hmi.Screen.Screen"), "Screen");
                if (root.Element("ObjectList") == null) root.Add(new XElement("ObjectList"));
                // A duplicate layer name/number would silently merge on native import.
                if (result.Descendants("Hmi.Screen.ScreenLayer").Any(l => ClassicScreenPreflight.Attribute(l, "Name") == ClassicScreenPreflight.Attribute(layer, "Name")))
                    throw new ArgumentException("Layer already exists in the destination.");
                root.Element("ObjectList").Add(layer);
            }
        }
        private static void ApplyNativeProperties(XElement item, JObject spec, IReadOnlyDictionary<string, XDocument> sources)
        {
            var captions = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject caption in ObjectRows(spec, "captions", 128))
            {
                Fields(caption, "compositionName", "culture", "text");
                if (caption["text"]?.Type != JTokenType.String || !captions.Add(caption.Value<string>("compositionName") + "\n" + caption.Value<string>("culture"))) throw new ArgumentException("Caption replacement must be unique and textual.");
                var owner = Unique(item.Descendants("MultilingualText").Where(x => (string)x.Attribute("CompositionName") == caption.Value<string>("compositionName")), "caption composition");
                var entry = Unique(owner.Descendants("MultilingualTextItem").Where(x => ClassicScreenPreflight.Attribute(x, "Culture") == caption.Value<string>("culture")), "caption culture");
                var field = entry.Element("AttributeList")?.Element("Text");
                if (field == null) throw new ArgumentException("Caption has no native Text field.");
                // Preserve the observed body/p representation and all formatting attributes.
                // Multi-run/unknown formatting is refused, never flattened or discarded.
                if (field.HasElements)
                {
                    if (field.Elements().Count() != 1) throw new ArgumentException("Unsupported formatted caption; native structure retained.");
                    var body = field.Elements().First();
                    if (field.Nodes().Any(n => !(n is XElement) && !(n is XText t && string.IsNullOrWhiteSpace(t.Value)))
                        || body?.Name != "body" || body.Elements().Count() != 1 || body.Elements().First().Name != "p"
                        || body.Nodes().Any(n => !(n is XElement) && !(n is XText t && string.IsNullOrWhiteSpace(t.Value)))) throw new ArgumentException("Unsupported formatted caption; native structure retained.");
                    ReplaceText(body.Element("p"), caption.Value<string>("text"));
                }
                else SetScalar(field, caption.Value<string>("text"));
            }
            if (spec["interfaceValues"] != null)
            {
                if (!(spec["interfaceValues"] is JObject properties)) throw new ArgumentException("interfaceValues must be an object.");
                foreach (var property in properties.Properties())
                {
                    var entry = Unique(item.Elements("ObjectList").Elements("Hmi.Screen.InterfacePropertySimple").Where(x => ClassicScreenPreflight.Attribute(x, "Name") == property.Name), "interface property");
                    TypedValue(entry.Element("AttributeList")?.Element("Value"), property.Value);
                }
            }
            var parameters = new HashSet<string>(StringComparer.Ordinal);
            foreach (JObject parameter in ObjectRows(spec, "eventParameters", 64))
            {
                Fields(parameter, "eventName", "functionName", "parameterName", "value");
                string key = new JArray(parameter["eventName"], parameter["functionName"], parameter["parameterName"]).ToString();
                if (!parameters.Add(key)) throw new ArgumentException("Repeated event parameter replacement.");
                var e = Unique(item.Descendants("Hmi.Event.Event").Where(x => ClassicScreenPreflight.Attribute(x, "Name") == parameter.Value<string>("eventName")), "event");
                var function = Unique(e.Descendants("Hmi.Event.FunctionListEntry").Where(x => ClassicScreenPreflight.Attribute(x, "Name") == parameter.Value<string>("functionName")), "function");
                var p = Unique(function.Descendants("Hmi.Event.FunctionListEntryParameter").Where(x => ClassicScreenPreflight.Attribute(x, "Name") == parameter.Value<string>("parameterName")), "parameter");
                TypedValue(p.Element("AttributeList")?.Element("Value"), parameter["value"]);
            }
            if (spec["visibility"] != null)
            {
                var visibility = spec["visibility"] as JObject;
                Fields(visibility, "sourceScreenPath", "sourceItem", "tag", "rangeStart", "rangeEnd");
                if (item.Descendants("Hmi.Dynamic.VisibilityAnimation").Any()) throw new ArgumentException("Destination already has visibility; use an unanimated prototype.");
                if (visibility["tag"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(visibility.Value<string>("tag"))
                    || visibility["rangeStart"]?.Type != JTokenType.Integer || visibility["rangeEnd"]?.Type != JTokenType.Integer
                    || visibility.Value<long>("rangeStart") > visibility.Value<long>("rangeEnd")) throw new ArgumentException("Visibility requires an explicit tag and ordered integer range.");
                var animation = new XElement(Unique(Prototype(sources, visibility).Descendants("Hmi.Dynamic.VisibilityAnimation"), "visibility prototype"));
                SetScalar(Unique(animation.Descendants("Tag").Elements("Name"), "visibility tag"), visibility.Value<string>("tag"));
                SetScalar(animation.Element("AttributeList")?.Element("RangeStart"), visibility["rangeStart"].ToString());
                SetScalar(animation.Element("AttributeList")?.Element("RangeEnd"), visibility["rangeEnd"].ToString());
                if (item.Element("ObjectList") == null) item.Add(new XElement("ObjectList"));
                item.Element("ObjectList").AddFirst(animation);
            }
        }
        private static void ReplaceText(XElement field, string value)
        {
            if (field == null || field.Nodes().Any(n => n.GetType() != typeof(XText))) throw new ArgumentException("Caption contains unsupported runs or annotations.");
            field.Value = value; // Attributes remain intact.
        }
        private static void TypedValue(XElement field, JToken value)
        {
            if (field == null || field.Attributes().Any(a => a.Name != "Type") || field.Nodes().Any(n => n.GetType() != typeof(XText)) || value == null || value is JContainer) throw new ArgumentException("Only existing native constant values may be replaced.");
            string type = (string)field.Attribute("Type");
            string text = value is JValue scalar ? scalar.ToString(CultureInfo.InvariantCulture) : value.ToString();
            bool valid = type == "System.String" && value.Type == JTokenType.String
                || type == "System.Boolean" && value.Type == JTokenType.Boolean
                || type == "System.Int16" && value.Type == JTokenType.Integer && short.TryParse(text, out _)
                || type == "System.Int32" && value.Type == JTokenType.Integer && int.TryParse(text, out _)
                || type == "System.Int64" && value.Type == JTokenType.Integer && long.TryParse(text, out _)
                || type == "System.UInt16" && value.Type == JTokenType.Integer && ushort.TryParse(text, out _)
                || type == "System.UInt32" && value.Type == JTokenType.Integer && uint.TryParse(text, out _)
                || type == "System.Byte" && value.Type == JTokenType.Integer && byte.TryParse(text, out _)
                || (type == "System.Single" || type == "System.Double") && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && !double.IsNaN(number) && !double.IsInfinity(number)
                    && (type != "System.Single" || Math.Abs(number) <= float.MaxValue);
            if (!valid) throw new ArgumentException("Constant does not match native type " + type);
            field.Value = value.Type == JTokenType.Boolean ? text.ToLowerInvariant() : text;
        }
    }
}
