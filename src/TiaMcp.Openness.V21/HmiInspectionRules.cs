using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure inspection rules. Export IDs identify only the retained native export, never a persistent native GUID.
    internal static class HmiInspectionRules
    {
        internal static readonly string[] Profiles = { "classic-comfort", "classic-rt-advanced", "classic-basic", "classic-rt-professional", "unified-comfort", "unified-pc" };
        internal static string Profile(string family, string identifier)
        {
            string id = (identifier ?? "").ToLowerInvariant();
            if (family == "unified")
            {
                if (id.Contains("unified") && id.Contains("pc")) return "unified-pc";
                if (id.Contains("unified") && (id.Contains("comfort") || id.Contains("mtp"))) return "unified-comfort";
            }
            if (family == "classic")
            {
                if (id.Contains("rt advanced")) return "classic-rt-advanced";
                if (id.Contains("rt professional")) return "classic-rt-professional";
                if (id.Contains("basic")) return "classic-basic";
                if (id.Contains("comfort") && !id.Contains("unified")) return "classic-comfort";
            }
            return "unknown";
        }

        internal static string Escape(string value) => Uri.EscapeDataString(value ?? "");

        internal static string NamedChildPath(string parent, string name)
            => string.IsNullOrEmpty(name) ? null : parent + "/" + Escape(name);

        internal static JObject ExactCatalogDefinition(string nativeIdentifier, IEnumerable<JObject> entries, bool complete)
        {
            if (!complete || string.IsNullOrEmpty(nativeIdentifier)) return null;
            var exact = entries.Where(row => row.Value<string>("typeIdentifier") == nativeIdentifier
                || row.Value<string>("typeIdentifierNormalized") == nativeIdentifier).Take(2).ToArray();
            return exact.Length == 1 ? (JObject)exact[0].DeepClone() : null;
        }

        internal static JObject CatalogSelection(string family, IEnumerable<JObject> candidates, bool scopeComplete, int familyRuntimeCount, string association)
        {
            var matches = candidates.Where(c => !string.IsNullOrEmpty(c.Value<string>("catalogIdentifier")) && Profile(family, c.Value<string>("classification")) != "unknown").ToList();
            bool unique = scopeComplete && familyRuntimeCount == 1 && matches.Count == 1;
            var selected = unique ? matches[0] : null;
            return new JObject { ["profile"] = unique ? Profile(family, selected.Value<string>("classification")) : "unknown",
                ["catalogIdentifier"] = selected?["catalogIdentifier"]?.DeepClone(), ["runtimeVersion"] = selected?["runtimeVersion"]?.DeepClone(),
                ["classification"] = unique ? selected.Value<string>("classification") : "",
                ["evidence"] = new JObject { ["status"] = unique ? "nativeConfirmed" : "unresolved", ["association"] = association,
                    ["scopeComplete"] = scopeComplete, ["familyRuntimeCount"] = familyRuntimeCount, ["candidates"] = new JArray(matches.Select(m => m.DeepClone())),
                    ["reason"] = unique ? "Unique family-compatible native catalog within the established hardware owner scope."
                        : !scopeComplete ? "Native catalog/runtime enumeration is incomplete." : familyRuntimeCount != 1 ? "The owning device does not establish a unique same-family runtime association." : "A unique family-compatible native catalog was not observed." } };
        }

        internal static IEnumerable<JObject> UnifiedAlarmReferences(string nativeType, string sourcePath, JObject properties,
            JToken parameterTags, Func<string, IEnumerable<JObject>> candidates, bool candidatesComplete)
        {
            bool discrete = nativeType == "Siemens.Engineering.HmiUnified.HmiAlarm.HmiDiscreteAlarm";
            if (!discrete && nativeType != "Siemens.Engineering.HmiUnified.HmiAlarm.HmiAnalogAlarm") yield break;
            foreach (string property in discrete ? new[] { "RaisedStateTag", "AcknowledgmentStateTag", "AcknowledgmentControlTag" } : new[] { "RaisedStateTag" })
            {
                var value = properties[property];
                if (value?.Type == JTokenType.Null || value?.Type == JTokenType.String && value.Value<string>() == "") continue;
                yield return AlarmTagReference(sourcePath, property, value, candidates, candidatesComplete, properties[property + "BitNumber"]);
            }
            if (parameterTags is JArray parameters && parameters.Count <= 10)
            {
                for (int i = 0; i < parameters.Count; i++)
                {
                    var value = parameters[i];
                    if (value.Type == JTokenType.Null || value.Type == JTokenType.String && value.Value<string>() == "") continue;
                    yield return AlarmTagReference(sourcePath, "AlarmParameterTags[" + i + "]", value, candidates, candidatesComplete, null);
                }
            }
            else
            {
                var edge = AlarmTagReference(sourcePath, "AlarmParameterTags", parameterTags, candidates, false, null);
                edge["reason"] = "The documented native list of at most ten tag names was not available; no parameter binding is inferred.";
                yield return edge;
            }
        }

        private static JObject AlarmTagReference(string sourcePath, string property, JToken value,
            Func<string, IEnumerable<JObject>> candidates, bool candidatesComplete, JToken bitNumber)
        {
            string spelling = value?.Type == JTokenType.String ? value.Value<string>() : null;
            var edge = Reference(sourcePath, "hmiAlarmTag", property, spelling,
                spelling == null ? Enumerable.Empty<JObject>() : candidates(spelling), false, candidatesComplete: candidatesComplete);
            edge["rawValue"] = value?.DeepClone();
            if (bitNumber != null) edge["bitNumber"] = bitNumber.DeepClone();
            edge["reason"] = spelling == null ? "Native alarm tag property could not be read as a complete string; no binding is inferred."
                : "Documented native alarm tag foreign key, restricted to this exact HMI software. Tag expression/member expansion and runtime behavior are not inferred.";
            edge["nativeQualification"] = "unverified";
            return edge;
        }

        internal static JObject UnresolvedNativeReference(string sourcePath, string relation, string property, JToken value, string reason)
        {
            var edge = Reference(sourcePath, relation, property, value?.Type == JTokenType.String ? value.Value<string>() : null, Enumerable.Empty<JObject>(), false, "unresolved");
            edge["rawValue"] = value?.DeepClone(); edge["reason"] = reason; edge["nativeQualification"] = "unverified";
            return edge;
        }

        internal static bool DirectTagBinding(JObject properties, JObject converter)
            => properties?.Value<bool?>("UseIndirectAddressing") == false && converter?.Value<bool?>("IsFormulaSelected") == false;

        internal static JObject SourceCoverage(string family, string nativeType, string hmiPath, string requestedPath)
        {
            bool textLists = family == "unified" && nativeType == "Siemens.Engineering.HmiUnified.TextGraphicList.HmiTextList";
            bool graphicLists = family == "unified" && nativeType == "Siemens.Engineering.HmiUnified.TextGraphicList.HmiGraphicList";
            bool composition = textLists || graphicLists;
            return new JObject { ["status"] = "partial", ["complete"] = false,
                ["scope"] = composition ? "nativeComposition" : "selectedObject", ["selectedObjectOnly"] = !composition,
                ["requestedObjectPath"] = requestedPath,
                ["exportOwnerPath"] = composition ? hmiPath + (textLists ? "/textLists" : "/graphicLists") : requestedPath,
                ["nativeComposition"] = textLists ? "HmiTextLists" : graphicLists ? "HmiGraphicLists" : null,
                ["reason"] = composition ? "The native collection export accepts an output filename, not an object filter; its artifacts can include other lists from this HMI software." : "Native selected-object export is retained; complete dependency and preservation coverage has not been established.",
                ["omissions"] = textLists ? new JArray("Siemens documents that text lists containing formatted text are skipped by native export; completeness of the selected list and collection cannot be established.") : new JArray("Unknown or unexposed dependency content is not established by this export.") };
        }

        internal static string[] ScalarSymbolPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return null;
            var components = new List<string>(); int index = 0;
            while (index < value.Length && components.Count < 33)
            {
                string component;
                if (value[index] == '"')
                {
                    int start = ++index;
                    while (index < value.Length && value[index] != '"' && value[index] != '$' && !char.IsControl(value[index])) index++;
                    if (index == value.Length || value[index] != '"' || index == start) return null;
                    component = value.Substring(start, index++ - start);
                }
                else
                {
                    int start = index;
                    if (!(char.IsLetter(value[index]) || value[index] == '_')) return null;
                    while (index < value.Length && (char.IsLetterOrDigit(value[index]) || value[index] == '_')) index++;
                    component = value.Substring(start, index - start);
                }
                components.Add(component);
                if (index == value.Length) return components.ToArray();
                if (value[index++] != '.' || index == value.Length) return null;
            }
            return null;
        }

        internal static string Section(string kind)
        {
            switch (kind)
            {
                case "hmiDevice": return "devices";
                case "hmiConnection": return "connections";
                case "hmiTag": case "hmiTagTable": return "tags";
                case "hmiScreen": return "screens";
                case "hmiScreenItem": case "hmiScreenLayer": return "items";
                case "hmiAlarm": return "alarms";
                case "hmiScript": return "scripts";
                case "hmiResource": return "resources";
                default: throw new ArgumentException("Unknown HMI object kind.");
            }
        }

        internal static JObject Reference(string sourcePath, string relation, string property, string spelling, IEnumerable<JObject> candidates, bool indirect, string evidence = "nativeConfirmed", bool candidatesComplete = true)
        {
            var rows = candidates.ToList();
            bool resolved = candidatesComplete && !indirect && rows.Count == 1;
            return new JObject { ["sourcePath"] = sourcePath, ["relation"] = relation,
                ["target"] = resolved ? rows[0].DeepClone() : null, ["targetCandidates"] = new JArray(rows.Select(r => r.DeepClone())),
                ["resolution"] = resolved ? "resolved" : indirect ? "dynamic" : !candidatesComplete || rows.Count == 0 ? "unresolved" : "ambiguous",
                ["evidence"] = resolved ? evidence : "unresolved", ["sourceProperty"] = property, ["nativeSpelling"] = spelling,
                ["candidateScopeComplete"] = candidatesComplete,
                ["automaticRewiringAllowed"] = false };
        }

        // Every Classic screen role exports as its own owner element; only its contents are items.
        private static readonly HashSet<string> ClassicScreenRoots = new HashSet<string>(StringComparer.Ordinal) {
            "Hmi.Screen.Screen", "Hmi.Screen.ScreenTemplate", "Hmi.Screen.ScreenPopup", "Hmi.Screen.ScreenSlidein",
            "Hmi.Screen.ScreenGlobalElements", "Hmi.Screen.ScreenOverview" };

        internal static IEnumerable<JObject> ClassicItems(XDocument document, string screenPath, string exportHash, Func<bool> take)
        {
            // Only named native HMI screen payload objects are materialized; unknown XML is retained in the artifact.
            foreach (var element in document.Descendants())
            {
                if (!take()) yield break;
                string nativeType = element.Name.LocalName;
                if (!nativeType.StartsWith("Hmi.Screen.", StringComparison.Ordinal) || ClassicScreenRoots.Contains(nativeType)) continue;
                var attributes = element.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                string name = attributes?.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectName")?.Value
                    ?? attributes?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
                string id = element.Attribute("ID")?.Value;
                if (name == null || id == null) continue;
                var properties = new JObject();
                var propertyPreview = attributes.Elements().Where(e => !e.HasElements).Take(129).ToList();
                foreach (var property in propertyPreview.Take(128))
                    properties[property.Name.LocalName] = property.Value.Length <= 8192 ? property.Value : property.Value.Substring(0, 8192);
                string parentId = element.Ancestors().FirstOrDefault(e => e.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal) && !ClassicScreenRoots.Contains(e.Name.LocalName) && e.Attribute("ID") != null)?.Attribute("ID")?.Value;
                bool structural = nativeType == "Hmi.Screen.ScreenLayer";
                var payloads = ClassicOwnedPayloads(element, take).Where(e => !e.HasElements &&
                    e.AncestorsAndSelf().TakeWhile(a => a != element).Take(64).Any(a => new[] { "Tag", "Connection", "Script", "Type", "Screen", "Navigation", "Event", "LinkList" }.Any(token => a.Name.LocalName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)))
                    .Take(65).Select(e => ClassicPayloadObservation(e, element)).ToList();
                yield return new JObject { ["kind"] = structural ? "hmiScreenLayer" : "hmiScreenItem", ["name"] = name, ["structural"] = structural,
                    ["objectPath"] = screenPath + "/exportItems/" + Escape(id),
                    ["parentPath"] = parentId == null ? screenPath : screenPath + "/exportItems/" + Escape(parentId),
                    ["nativeType"] = nativeType, ["properties"] = properties,
                    ["source"] = new JObject { ["kind"] = "nativeExport", ["exportSha256"] = exportHash, ["exportLocalId"] = id,
                        ["lifetime"] = "This native ID is scoped only to this exact exported artifact.", ["referencesComplete"] = false },
                    ["metadataCoverage"] = new JObject { ["complete"] = false, ["scalarPropertiesTruncated"] = propertyPreview.Count > 128 || propertyPreview.Any(p => p.Value.Length > 8192),
                        ["referencePayloadsTruncated"] = payloads.Count > 64 || payloads.Any(p => p.Value<bool>("truncated")),
                        ["limits"] = new JObject { ["scalarProperties"] = 128, ["textCharacters"] = 8192, ["referencePayloads"] = 64, ["attributesPerPayload"] = 64, ["ancestorDepth"] = 64 },
                        ["reason"] = "Bounded metadata preview; unknown constructs and content beyond these limits remain in the exact retained native export." },
                    ["referencePayloads"] = new JArray(payloads.Take(64)) };
            }
        }

        private static JObject ClassicPayloadObservation(XElement element, XElement owner)
        {
            var ancestors = element.AncestorsAndSelf().TakeWhile(a => a != owner).Take(65).ToList();
            var attributes = ancestors.Take(64).SelectMany(a => a.Attributes().Select(value => new JObject { ["element"] = a.Name.LocalName, ["name"] = value.Name.LocalName,
                ["value"] = value.Value.Length <= 8192 ? value.Value : value.Value.Substring(0, 8192), ["truncated"] = value.Value.Length > 8192 })).Take(65).ToList();
            return new JObject { ["property"] = string.Join("/", ancestors.Take(64).AsEnumerable().Reverse().Select(a => a.Name.LocalName)),
                ["nativeSpelling"] = element.Value.Length <= 8192 ? element.Value : element.Value.Substring(0, 8192),
                ["truncated"] = element.Value.Length > 8192 || ancestors.Count > 64 || attributes.Count > 64 || attributes.Any(a => a.Value<bool>("truncated")),
                ["rawAttributes"] = new JArray(attributes.Take(64)), ["resolution"] = "unresolved",
                ["reason"] = "Native binding/event/navigation preview retained; this XML shape has no qualified semantic parser. The exact export retains omitted content." };
        }

        private static IEnumerable<XElement> ClassicOwnedPayloads(XElement owner, Func<bool> take)
        {
            // A child's binding belongs to that child, never also to its layer/group. Use an
            // iterative bounded walk so deeply nested unknown payloads cannot consume the stack.
            var stack = new Stack<IEnumerator<XElement>>();
            stack.Push(owner.Elements().GetEnumerator());
            try
            {
                while (stack.Count > 0)
                {
                    var iterator = stack.Peek();
                    if (!iterator.MoveNext()) { iterator.Dispose(); stack.Pop(); continue; }
                    if (!take()) yield break;
                    var element = iterator.Current;
                    if (element.Name.LocalName.StartsWith("Hmi.Screen.", StringComparison.Ordinal) && element.Attribute("ID") != null)
                    {
                        var attributes = element.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                        if (attributes?.Elements().Any(e => e.Name.LocalName == "Name" || e.Name.LocalName == "ObjectName") == true) continue;
                    }
                    yield return element;
                    if (element.HasElements) stack.Push(element.Elements().GetEnumerator());
                }
            }
            finally { foreach (var iterator in stack) iterator.Dispose(); }
        }
    }
}
