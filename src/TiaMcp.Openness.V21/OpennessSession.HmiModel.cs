using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using TiaMcp.Model;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        // Walk order matters: Screens/TagTables precede their group views so shared members keep
        // their hmi_inspect paths. HmiSoftware.Tags is the aggregate view of table members and is not walked.
        private static readonly string[] HmiModelRoots = { "Screens", "ScreenGroups", "TagTables", "TagTableGroups", "SystemTags", "Connections",
            "AlarmClasses", "DiscreteAlarms", "AnalogAlarms", "HmiAlarmAuditClass", "OpcUaAlarmTypes", "AlarmLogs", "DataLogs", "AuditTrails",
            "Scripts", "HmiTextLists", "HmiGraphicLists", "HmiSystemTextLists", "PlantObjectTags", "RuntimeSettings" };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void HmiUnifiedModel(Project project, HmiDiscoveredTarget target, JObject args, HmiCapture capture)
        {
            var roots = HmiModelRoots;
            if (args["modelRoots"] != null)
            {
                var requested = (args["modelRoots"] as JArray)?.Select(r => r.Type == JTokenType.String ? (string)r : null).ToArray();
                if (requested == null || requested.Length == 0 || requested.Any(r => !HmiModelRoots.Contains(r)))
                    throw new BridgeException("InvalidRequest", "modelRoots must be a nonempty array of: " + string.Join(", ", HmiModelRoots) + ".");
                roots = HmiModelRoots.Where(requested.Contains).ToArray();
            }
            var walk = new HmiModelWalk(capture.Budget);
            walk.Capture((Siemens.Engineering.HmiUnified.HmiSoftware)target.Software, target.Path, roots);
            foreach (var error in walk.Errors) if (capture.Diagnostics.Count < 100) capture.Diagnostics.Add(error);
            if (walk.ErrorCount > 0) capture.Status = "unavailable";
            foreach (var node in walk.Nodes)
            {
                var row = node.Row;
                // Same hash as the preservation manifest entry for this path.
                row["contentSha256"] = HmiPreservationRules.Hash(row.ToString(Formatting.None));
                node.Identity = HmiIdentity(project, target, "hmiModelNode", row.Value<string>("path"), row.Value<string>("name"));
                row["identity"] = node.Identity; row["kind"] = "hmiModelNode";
                row["source"] = new JObject { ["kind"] = "native", ["typedReadback"] = true, ["scriptsExecuted"] = false };
                capture.RetainedBytes += Encoding.UTF8.GetByteCount(row.ToString(Formatting.None));
                if (capture.RetainedBytes > 8 * 1024 * 1024) { capture.Budget.Truncated = true; break; }
                capture.Entries.Add(node);
            }
        }

        // One bounded typed walk over the Unified public object model, shared by hmi_inspect
        // section=model and the preservation manifest. Composition members become nodes; object-valued
        // parts, translations and script hashes are inlined into their owner's attributes.
        private sealed class HmiModelWalk
        {
            internal readonly List<HmiNativeEntry> Nodes = new List<HmiNativeEntry>();
            internal readonly JArray Errors = new JArray();
            internal int ErrorCount;
            internal readonly ScanBudget Budget;
            private readonly HashSet<object> _visited = new HashSet<object>();
            private List<object> _parts = new List<object>();
            internal HmiModelWalk(ScanBudget budget) { Budget = budget; }

            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void Capture(Siemens.Engineering.HmiUnified.HmiSoftware hmi, string hmiPath, IEnumerable<string> roots)
            {
                foreach (string root in roots)
                {
                    if (Budget.Truncated) return;
                    object value;
                    try { value = typeof(Siemens.Engineering.HmiUnified.HmiSoftware).GetProperty(root).GetValue(hmi, null); }
                    catch (Exception error) { Error(hmiPath, root, error); continue; }
                    if (value is IEngineeringObject single) Walk(single, hmiPath + "/" + CompositionSegment(root), hmiPath, root, root, 0);
                    else Read(hmiPath, root, () => value, new JObject(), 0);
                }
            }

            private void Walk(IEngineeringObject native, string path, string parentPath, string name, string composition, int depth)
            {
                if (!_visited.Add(native)) return;
                if (depth > 32) { Budget.Truncated = true; return; }
                if (!Budget.Take()) return;
                var row = new JObject { ["path"] = path, ["parentPath"] = parentPath, ["name"] = name, ["composition"] = composition, ["nativeType"] = native.GetType().FullName };
                Nodes.Add(new HmiNativeEntry { Native = native, Row = row });
                var owner = _parts; _parts = new List<object>(); // part nesting is per node
                try { row["attributes"] = Attributes(native, path, depth); } finally { _parts = owner; }
            }

            private JObject Attributes(IEngineeringObject native, string path, int depth)
            {
                var result = new JObject();
                var seen = new HashSet<string>(StringComparer.Ordinal) { "Parent" };
                _parts.Add(native);
                try
                {
                    // Typed public properties first, then attribute/composition infos the typed surface lacks.
                    foreach (var property in native.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
                        if (property.CanRead && property.GetIndexParameters().Length == 0 && seen.Add(property.Name)) Read(path, property.Name, () => property.GetValue(native, null), result, depth);
                    IList<EngineeringAttributeInfo> attributes = null; IList<EngineeringCompositionInfo> compositions = null;
                    try { attributes = native.GetAttributeInfos(); compositions = native.GetCompositionInfos(); }
                    catch (Exception error) { Error(path, "GetAttributeInfos", error); }
                    foreach (var info in (attributes ?? new EngineeringAttributeInfo[0]).OrderBy(a => a.Name, StringComparer.Ordinal))
                        if ((info.AccessMode & EngineeringAttributeAccessMode.Read) != 0 && seen.Add(info.Name)) Read(path, info.Name, () => native.GetAttribute(info.Name), result, depth);
                    foreach (var info in (compositions ?? new EngineeringCompositionInfo[0]).OrderBy(c => c.Name, StringComparer.Ordinal))
                        if (seen.Add(info.Name)) Read(path, info.Name, () => native.GetComposition(info.Name), result, depth);
                }
                finally { _parts.RemoveAt(_parts.Count - 1); }
                return result;
            }

            private void Read(string path, string name, Func<object> getter, JObject result, int depth)
            {
                if (IsSensitiveAttribute(name)) { result[name] = "<excluded>"; return; }
                try
                {
                    object value = getter();
                    if (value is IEngineeringComposition || value is IEngineeringAssociation) Children(path, name, (IEnumerable)value, depth);
                    else result[name] = Value(value, path + "/" + name, depth);
                }
                catch (Exception error) { result[name] = new JObject { ["readError"] = Error(path, name, error) }; }
            }

            private void Children(string ownerPath, string composition, IEnumerable members, int depth)
            {
                string prefix = ownerPath + "/" + CompositionSegment(composition) + "/";
                var used = new HashSet<string>(StringComparer.Ordinal);
                int index = 0;
                foreach (object member in members)
                {
                    if (Budget.Truncated) return;
                    index++;
                    if (!(member is IEngineeringObject child)) continue;
                    string name = ChildName(child), segment = string.IsNullOrEmpty(name) ? "#" + index : Segment(name);
                    if (!used.Add(segment)) segment += "#" + index;
                    Walk(child, prefix + segment, ownerPath, name, composition, depth + 1);
                }
            }

            private JToken Value(object value, string path, int depth)
            {
                switch (value)
                {
                    case null: return JValue.CreateNull();
                    case string text: return Text(text);
                    case Siemens.Engineering.HmiUnified.UI.Dynamization.Script.IHmiScript script:
                        return new JObject { ["scriptCode"] = Hashed(script.ScriptCode), ["globalDefinitionAreaScriptCode"] = Hashed(script.GlobalDefinitionAreaScriptCode), ["async"] = script.Async };
                    case MultilingualText multilingual:
                        var texts = new JObject();
                        foreach (MultilingualTextItem item in multilingual.Items) texts[item.Language?.Culture?.Name ?? "#" + texts.Count] = Text(item.Text);
                        return texts;
                    case Language language: return language.Culture?.Name;
                    case IEngineeringObject part:
                        if (_parts.Count >= 8 || _parts.Contains(part)) return new JObject { ["nativeType"] = part.GetType().FullName, ["expanded"] = false };
                        return new JObject { ["nativeType"] = part.GetType().FullName, ["attributes"] = Attributes(part, path, depth) };
                    case IEnumerable list:
                        var array = new JArray();
                        foreach (object item in list)
                        {
                            if (array.Count == 256) { Error(path, "list", new InvalidOperationException("Native list exceeds 256 values; remaining values are not compared.")); break; }
                            array.Add(Value(item, path, depth));
                        }
                        return array;
                }
                var type = value.GetType();
                if (type.IsEnum || value is Guid) return value.ToString();
                if (type.IsPrimitive || value is decimal) return JToken.FromObject(value);
                if (value is DateTime time) return time.ToString("O", CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            private static JToken Text(string text) => text.Length <= 4096 ? (JToken)text
                : new JObject { ["value"] = text.Substring(0, 4096), ["truncated"] = true, ["originalLength"] = text.Length, ["sha256"] = HmiPreservationRules.Hash(text) };
            private static JObject Hashed(string code) => new JObject { ["sha256"] = HmiPreservationRules.Hash(code ?? ""), ["length"] = code?.Length ?? 0 };

            private string Error(string path, string name, Exception error)
            {
                var diagnostic = HmiDiagnostic(name, error);
                diagnostic["path"] = path;
                if (++ErrorCount <= 100) Errors.Add(diagnostic);
                return diagnostic.Value<string>("nativeException");
            }

            // Name for exact names, PropertyName/EventType for dynamizations, handlers and interface entries.
            private static string ChildName(IEngineeringObject native)
            {
                var parts = new List<string>();
                foreach (string key in new[] { "Name", "PropertyName", "EventType", "Language" })
                {
                    try
                    {
                        var property = native.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
                        object value = property == null || property.GetIndexParameters().Length != 0 ? null : property.GetValue(native, null);
                        string text = value is Language language ? language.Culture?.Name : Convert.ToString(value, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    }
                    catch (Exception) { } // the failing getter is reported by the attribute read
                }
                return parts.Count == 0 ? null : string.Join(".", parts);
            }

            private static string CompositionSegment(string name)
            {
                switch (name)
                {
                    case "ScreenItems": return "items";
                    case "HmiTextLists": return "textLists";
                    case "HmiGraphicLists": return "graphicLists";
                    case "HmiSystemTextLists": return "systemTextLists";
                    default: return char.ToLowerInvariant(name[0]) + name.Substring(1);
                }
            }
        }
    }
}
