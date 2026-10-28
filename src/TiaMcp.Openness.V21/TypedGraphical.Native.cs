#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    internal static partial class TypedGraphical
    {
        private static XElement BuildPart(TypedGraphicalNode node, string uid, TypedGraphicalProfile profile, TypedGraphicalContext context, NativeIds ids)
        {
            if (node.Kind == "contact" || node.Kind == "coil" || node.Kind == "assign")
            {
                if (profile.NativeName != (node.Kind == "contact" ? "Contact" : "Coil"))
                    Fail("GraphicalProfileUnavailable", "The native primitive profile name does not match its typed instruction family.");
                var simple = new XElement(Net + "Part", new XAttribute("Name", profile.NativeName), new XAttribute("UId", uid));
                if (node.Negated) simple.Add(new XElement(Net + "Negated", new XAttribute("Name", "operand")));
                return simple;
            }
            if (node.Kind == "call")
            {
                var callee = ResolveCallee(context, node.CalleePath);
                if (profile.CalleeNameEncoding != null && profile.CalleeNameEncoding != "simple" && profile.CalleeNameEncoding != "namespaceQualified" && profile.CalleeNameEncoding != "crossUnitQualified")
                    Fail("GraphicalProfileUnavailable", "The native callee name encoding is not supported.");
                string nativeName = callee.Name;
                bool crossUnit = IsCrossUnit(context, callee);
                if (crossUnit && string.IsNullOrEmpty(callee.Namespace)) Fail("GraphicalCalleeUnsupported", "A cross-unit callee without a namespace has no captured CallInfo encoding.");
                if (!string.IsNullOrEmpty(callee.Namespace))
                {
                    // The qualified CallInfo Name is absolute, so one in-unit profile covers any callee namespace of the
                    // caller's own scope (or the standard route); a call into another unit needs its own capture.
                    if (profile.CalleeNameEncoding != (crossUnit ? "crossUnitQualified" : "namespaceQualified") || string.IsNullOrEmpty(profile.NativeTemplateXml))
                        Fail("GraphicalCalleeUnsupported", crossUnit ? "A call into another software unit requires an independently captured cross-unit CallInfo profile."
                            : "A namespaced call requires an independently captured namespace-qualified CallInfo profile.");
                    var observed = EngineeringSources.ReadXml(profile.NativeTemplateXml).Root;
                    string observedName = (string)observed?.Element(Net + "CallInfo")?.Attribute("Name");
                    if (observed?.Name != Net + "Call" || string.IsNullOrEmpty(observedName) || observedName.IndexOf('.') <= 0 || observedName.EndsWith(".", StringComparison.Ordinal))
                        Fail("GraphicalProfileUnavailable", "The selected qualified-callee profile does not retain an observed qualified CallInfo name.");
                    // Namespace and simple name are separate exact native metadata;
                    // never split or infer either from a caller-supplied dotted name.
                    nativeName = callee.Namespace + "." + callee.Name;
                }
                var info = new XElement(Net + "CallInfo", new XAttribute("Name", nativeName), new XAttribute("BlockType", callee.BlockType));
                if (callee.BlockType == "FB") info.Add(InstanceXml(ResolveInstance(context, node, profile), ids.Uid("instance:" + node.Key)));
                // Native FC serialization places Return before Output/InOut,
                // independently of declaration-section order (numeric FC captures).
                foreach (var p in callee.Parameters.Where(p => p.DataType != "Void").OrderBy(p => p.Section == "Input" ? 0 : p.Section == "Return" ? 1 : p.Section == "Output" ? 2 : 3))
                    info.Add(new XElement(Net + "Parameter", new XAttribute("Name", p.Name), new XAttribute("Section", p.Section), new XAttribute("Type", p.DataType)));
                return new XElement(Net + "Call", new XAttribute("UId", uid), info);
            }
            var template = EngineeringSources.ReadXml(profile.NativeTemplateXml).Root;
            if (template == null || template.Name != Net + "Part" || template.Descendants().Any(e => e.Name.Namespace != Net || e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal)))
                Fail("GraphicalProfileUnavailable", "The instruction template must be one native FlgNet/v5 Part, with no foreign engineering objects.");
            template = new XElement(template);
            string expectedName = node.Kind == "and" || node.Kind == "not" ? "A" : node.Kind == "or" || node.Kind == "branch" ? "O" : node.Kind == "compare" ? char.ToUpperInvariant(node.Comparison[0]) + node.Comparison.Substring(1) : node.Kind == "move" ? "MOVE" : node.Instruction;
            // MOVE/arithmetic/edge native casing (e.g. Move, Add) comes from their capture; only the family must match.
            bool capturedCasing = node.Kind == "move" || node.Kind == "math" || node.Kind == "edge";
            if (!string.Equals((string)template.Attribute("Name"), expectedName, capturedCasing ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) Fail("GraphicalProfileUnavailable", "The native template instruction name does not match its typed semantic family.");
            if ((node.Kind == "and" || node.Kind == "or" || node.Kind == "branch") && template.Elements(Net + "Negated").Any())
                Fail("GraphicalProfileUnavailable", "A non-negated Boolean operation cannot use a native template containing negated pins.");
            if (node.Kind == "compare" && !template.Elements(Net + "TemplateValue").Any(t => (string)t.Attribute("Name") == "SrcType" && t.Value == node.DataType))
                Fail("GraphicalProfileUnavailable", "The comparison template overload does not match the exact requested numeric type.");
            var typeValues = template.Elements(Net + "TemplateValue").Where(t => (string)t.Attribute("Type") == "Type").ToArray();
            if ((node.Kind == "move" || node.Kind == "math") && typeValues.Any(t => t.Value != node.DataType) || node.Kind == "math" && typeValues.Length == 0)
                Fail("GraphicalProfileUnavailable", "The MOVE/arithmetic template overload does not match the exact requested type.");
            foreach (var attribute in template.Descendants().Where(e => e.Name != Net + "Instance").Attributes("UId").ToArray()) attribute.Value = ids.Uid("template:" + node.Key + ":" + attribute.Value);
            template.SetAttributeValue("UId", uid);
            // TIA exports SUB/DIV with Card=2 but refuses that Card on import (native w9q typed-sub-int-lad); only ADD/MUL are expandable.
            if (node.Kind == "math" && (node.Instruction == "SUB" || node.Instruction == "DIV"))
                template.Elements(Net + "TemplateValue").Where(e => (string)e.Attribute("Name") == "Card").Remove();
            if (node.Kind == "and" || node.Kind == "or" || node.Kind == "branch" || node.Kind == "not")
            {
                var card = template.Elements(Net + "TemplateValue").SingleOrDefault(e => (string)e.Attribute("Name") == "Card");
                if (card == null) Fail("GraphicalProfileUnavailable", "The Boolean profile requires an observed Card template parameter.");
                card.Value = (node.Kind == "not" ? 1 : node.InputCount).ToString(CultureInfo.InvariantCulture);
                if (node.Kind == "not" && (template.Elements(Net + "Negated").Count() != 1 || (string)template.Element(Net + "Negated")?.Attribute("Name") != "in1")) Fail("GraphicalProfileUnavailable", "NOT requires exactly one observed negated input profile at in1.");
            }
            if (node.Kind == "timer" || node.Kind == "counter" || node.Kind == "edge")
            {
                var instance = template.Element(Net + "Instance");
                if (instance == null) Fail("GraphicalProfileUnavailable", "The selected stateful profile does not contain an observed instance representation.");
                instance.ReplaceWith(InstanceXml(ResolveInstance(context, node, profile), ids.Uid("instance:" + node.Key)));
            }
            return template;
        }
        private static XElement BuildAccess(TypedGraphicalOperand operand, string uid)
        {
            if (operand.Scope == "LiteralConstant") return new XElement(Net + "Access", new XAttribute("Scope", "LiteralConstant"), new XAttribute("UId", uid),
                new XElement(Net + "Constant", new XElement(Net + "ConstantType", operand.DataType), new XElement(Net + "ConstantValue", operand.DataType == "Bool" ? operand.Value.ToUpperInvariant() : operand.Value)));
            return new XElement(Net + "Access", new XAttribute("Scope", operand.Scope), new XAttribute("UId", uid), new XElement(Net + "Symbol", operand.Components.Select(c => new XElement(Net + "Component", new XAttribute("Name", c)))));
        }
        private static XElement InstanceXml(TypedGraphicalInstance instance, string uid) => new XElement(Net + "Instance", new XAttribute("Scope", instance.Scope), new XAttribute("UId", uid), instance.Components.Select(c => new XElement(Net + "Component", new XAttribute("Name", c))));
        private static XElement Pin(string uid, string name) => new XElement(Net + "NameCon", new XAttribute("UId", uid), new XAttribute("Name", name));
        private static XElement Endpoint(TypedGraphicalEndpoint endpoint, Dictionary<string, TypedGraphicalNode> nodes, NativeIds ids)
        {
            if (endpoint.Node.StartsWith("$", StringComparison.Ordinal)) return new XElement(Net + "IdentCon", new XAttribute("UId", ids.Mapping["operand:" + endpoint.Node.Substring(1)]));
            if (nodes[endpoint.Node].Kind == "powerrail") return new XElement(Net + "Powerrail");
            return Pin(ids.Mapping["node:" + endpoint.Node], endpoint.Port);
        }

        private static XElement CreateCompileUnit(XElement network, string id, string language, string templateXml, NativeIds ids)
        {
            if (templateXml == null) return new XElement("SW.Blocks.CompileUnit", new XAttribute("ID", id), new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList", new XElement("NetworkSource", network), new XElement("ProgrammingLanguage", language)));
            var unit = EngineeringSources.ReadXml(templateXml).Root;
            if (unit?.Name != "SW.Blocks.CompileUnit" || (string)unit.Attribute("CompositionName") != "CompileUnits" || unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != language
                || unit.Descendants().Any(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal))) Fail("GraphicalProfileUnavailable", "The native network envelope must be an independently observed compile unit of the same language without hidden engineering objects.");
            var source = unit.Element("AttributeList").Element("NetworkSource");
            if (source == null) Fail("GraphicalProfileUnavailable", "The native network envelope has no NetworkSource.");
            source.ReplaceNodes(network);
            foreach (var attribute in unit.Descendants().Attributes("ID").ToArray()) attribute.Value = ids.ObjectId("network-envelope:" + attribute.Value);
            unit.SetAttributeValue("ID", id);
            // Envelope text is part of the trusted template and is not silently erased.
            return unit;
        }

        /// <summary>Captured templates carry the capture project's culture; an import fails when the target project lacks it.
        /// Empty-text items move to the editing language (or are dropped when that culture already has an item); text is never discarded.</summary>
        internal static void RetargetTemplateCultures(XDocument document, ICollection<string> activeCultures, string editingCulture)
        {
            foreach (var item in document.Descendants("MultilingualTextItem").ToArray())
            {
                var culture = item.Element("AttributeList")?.Element("Culture");
                if (culture == null || activeCultures.Contains(culture.Value)) continue;
                if (!string.IsNullOrEmpty(item.Element("AttributeList").Element("Text")?.Value))
                    Fail("ProjectCultureUnavailable", "A captured template has text in culture " + culture.Value + ", which is not active in the target project.");
                bool taken = item.Parent.Elements("MultilingualTextItem").Any(other => other.Element("AttributeList")?.Element("Culture")?.Value == editingCulture);
                if (taken) item.Remove(); else culture.Value = editingCulture;
            }
        }

        /// <summary>V21 numbers export object IDs consecutively in document order, so an inserted network takes the IDs of the
        /// objects after it and those shift up by its own count; that exact shift counts as preserved (scope decision of
        /// 25 October 2026, docs/plans/insertion-id-renumbering-decision-20261025.md). Returns the inserted network's ID.</summary>
        private static string ShiftFollowingIdsForInsertion(XDocument document, XElement target, XElement insertedUnit, NativeIds ids, List<TypedGraphicalEffect> effects, bool insertBeforeTarget = false)
        {
            // Inserting after a network shifts what follows it; inserting before the block title shifts the title too.
            var following = document.Descendants().Where(e => insertBeforeTarget
                ? e == target || XNode.DocumentOrderComparer.Compare(e, target) > 0
                : XNode.DocumentOrderComparer.Compare(e, target) > 0 && !e.Ancestors().Contains(target)).Attributes("ID").ToArray();
            if (following.Length == 0) return (string)insertedUnit.Attribute("ID");
            var existing = document.Descendants().Attributes("ID").Select(a => ParseObjectId(a.Value)).ToArray();
            var inserted = insertedUnit.DescendantsAndSelf().Attributes("ID").ToArray();
            if (existing.Any(value => value < 0) || existing.Where((value, index) => value != existing[0] + index).Any() || existing[existing.Length - 1] > int.MaxValue - inserted.Length)
                Fail("NativeIdPreservationUnsupported", "Existing object IDs are not consecutive in document order, so V21's renumbering after the inserted network cannot be predicted exactly. Insertion was rejected before apply.");
            int start = ParseObjectId(following[0].Value);
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < inserted.Length; index++) { remap[inserted[index].Value] = (start + index).ToString("X", CultureInfo.InvariantCulture); inserted[index].Value = remap[inserted[index].Value]; }
            var shifted = following.Select(a => { string old = a.Value; a.Value = (ParseObjectId(old) + inserted.Length).ToString("X", CultureInfo.InvariantCulture); return old + "->" + a.Value; }).ToArray();
            foreach (var key in ids.Mapping.Keys.Where(k => k.StartsWith("object:", StringComparison.Ordinal)).ToArray())
            { string value; if (remap.TryGetValue(ids.Mapping[key], out value)) ids.Mapping[key] = value; }
            effects.Add(new TypedGraphicalEffect { Kind = "followingObjectIdShift", Identity = (string)insertedUnit.Attribute("ID"),
                Description = "V21 renumbers the " + following.Length.ToString(CultureInfo.InvariantCulture) + " existing object IDs after the inserted network by +" + inserted.Length.ToString(CultureInfo.InvariantCulture)
                    + " (" + string.Join(", ", shifted.Take(24)) + (shifted.Length > 24 ? ", ..." : "") + "); everything else is preserved exactly." });
            return (string)insertedUnit.Attribute("ID");
        }

        private static int ParseObjectId(string value)
        {
            int parsed;
            return value.Length > 0 && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'F' || c >= 'a' && c <= 'f')
                && int.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed) && parsed >= 0 ? parsed : -1;
        }

        private static void PreserveMatchingIds(XElement before, XElement after, TypedGraphicalModel model, NativeIds ids)
        {
            var oldParts = before.Element(Net + "Parts").Elements().ToArray();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in model.Nodes.Where(n => n.NativeUid != null))
            {
                var previous = oldParts.SingleOrDefault(e => (string)e.Attribute("UId") == node.NativeUid);
                string generated; ids.Mapping.TryGetValue("node:" + node.Key, out generated);
                var current = after.Element(Net + "Parts").Elements().SingleOrDefault(e => (string)e.Attribute("UId") == generated);
                if (previous == null || current == null || previous.Name != current.Name || (string)previous.Attribute("Name") != (string)current.Attribute("Name") || !claimed.Add(node.NativeUid))
                    Fail("UnsupportedTypedEdit", "A retained nativeUid must select exactly one matching instruction kind within the replaced network.");
                remap[generated] = node.NativeUid;
                if (XNode.DeepEquals(IdlessShape(previous), IdlessShape(current)))
                {
                    var oldNested = previous.Descendants().Attributes("UId").ToArray(); var newNested = current.Descendants().Attributes("UId").ToArray();
                    for (int i = 0; i < Math.Min(oldNested.Length, newNested.Length); i++) remap[newNested[i].Value] = oldNested[i].Value;
                    current.ReplaceWith(new XElement(previous));
                }
            }
            foreach (var current in after.Element(Net + "Parts").Elements().ToArray())
            {
                string generated = (string)current.Attribute("UId");
                if (remap.ContainsKey(generated) || claimed.Contains(generated)) continue;
                var match = oldParts.FirstOrDefault(e => !claimed.Contains((string)e.Attribute("UId")) && XNode.DeepEquals(IdlessShape(e), IdlessShape(current)));
                if (match == null) continue;
                string retained = (string)match.Attribute("UId"); claimed.Add(retained); remap[generated] = retained;
                // Matching instance subtrees retain their independently observed nested IDs too.
                var oldNested = match.Descendants().Attributes("UId").ToArray(); var newNested = current.Descendants().Attributes("UId").ToArray();
                for (int i = 0; i < Math.Min(oldNested.Length, newNested.Length); i++) remap[newNested[i].Value] = oldNested[i].Value;
                // Unchanged native parts/accesses keep their complete original
                // representation, including namespace bindings used by values.
                current.ReplaceWith(new XElement(match));
            }
            ApplyUidMap(after, remap, ids);
            remap.Clear();
            foreach (var current in after.Element(Net + "Wires").Elements().ToArray())
            {
                var match = before.Element(Net + "Wires").Elements().FirstOrDefault(e => !claimed.Contains((string)e.Attribute("UId")) && XNode.DeepEquals(RootIdlessShape(e), RootIdlessShape(current)));
                if (match == null) continue;
                string retained = (string)match.Attribute("UId"); claimed.Add(retained); remap[(string)current.Attribute("UId")] = retained;
                current.ReplaceWith(new XElement(match));
            }
            ApplyUidMap(after, remap, ids);
            // Native V21 reserializes by numeric UId after its Access-first pass.
            // Keep retained IDs in their native order; otherwise import renumbers
            // accesses/wires and strict complete readback correctly detects drift.
            var parts = after.Element(Net + "Parts");
            parts.ReplaceNodes(parts.Elements().OrderBy(e => e.Name == Net + "Access" ? 0 : 1)
                .ThenBy(e => ParseUid((string)e.Attribute("UId"))).ToArray());
            var wires = after.Element(Net + "Wires");
            wires.ReplaceNodes(wires.Elements().OrderBy(e => ParseUid((string)e.Attribute("UId"))).ToArray());
        }
        private static void ApplyUidMap(XElement network, Dictionary<string, string> remap, NativeIds ids)
        {
            var canonical = remap.ToDictionary(item => CanonicalUid(item.Key), item => item.Value, StringComparer.Ordinal);
            foreach (var uid in network.DescendantsAndSelf().Attributes("UId")) { string retained; if (canonical.TryGetValue(CanonicalUid(uid.Value), out retained)) uid.Value = retained; }
            foreach (var key in ids.Mapping.Keys.ToArray()) { string retained; if (!key.StartsWith("object:", StringComparison.Ordinal) && canonical.TryGetValue(CanonicalUid(ids.Mapping[key]), out retained)) ids.Mapping[key] = retained; }
        }
        private static void CanonicalizeNetworkIds(XElement network, XElement previous, TypedGraphicalModel model, Dictionary<string, Dictionary<string, TypedGraphicalPin>> pins, NativeIds ids, List<TypedGraphicalEffect> effects, string networkId)
        {
            // V21 assigns export-local UIds in document order, with instructions
            // ordered by dependency postorder and destination input position.
            // Structural replacement intentionally
            // rebuilds only this selected FlgNet; all other native IDs remain intact.
            var parts = network.Element(Net + "Parts");
            var instructions = model.Nodes.Where(n => n.Kind != "powerrail").ToDictionary(n => n.Key, StringComparer.Ordinal);
            var ordered = parts.Elements(Net + "Access").ToList();
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            Action<string> append = null;
            append = key =>
            {
                if (emitted.Contains(key)) return;
                if (!active.Add(key)) Fail("GraphicalCycle", "Instruction dependencies must form an acyclic native execution order.");
                var portNames = pins[key].Keys.ToArray();
                foreach (var edge in model.Connections.Where(c => c.To.Node == key && instructions.ContainsKey(c.From.Node))
                    .OrderBy(c => c.To.Port == "en" || c.To.Port == "pre" ? -1 : Array.IndexOf(portNames, c.To.Port))
                    .ThenBy(c => ParseUid(ids.Mapping["node:" + c.From.Node]))) append(edge.From.Node);
                string uid = ids.Mapping["node:" + key];
                ordered.Add(parts.Elements().Single(e => (string)e.Attribute("UId") == uid));
                active.Remove(key); emitted.Add(key);
            };
            foreach (var sink in instructions.Values.Where(n => !model.Connections.Any(c => c.From.Node == n.Key && instructions.ContainsKey(c.To.Node)))
                .OrderBy(n => ParseUid(ids.Mapping["node:" + n.Key]))) append(sink.Key);
            if (emitted.Count != instructions.Count) Fail("GraphicalCycle", "Every instruction must reach a terminal native output.");
            parts.ReplaceNodes(ordered);
            var remap = new Dictionary<string, string>(StringComparer.Ordinal);
            int next = 21;
            foreach (var definition in network.Descendants().Where(IsDefinition)) remap[(string)definition.Attribute("UId")] = (next++).ToString(CultureInfo.InvariantCulture);
            string[] oldIds = previous == null ? new string[0] : previous.Descendants().Where(IsDefinition).Select(e => (string)e.Attribute("UId")).ToArray();
            ApplyUidMap(network, remap, ids);
            if (previous != null)
            {
                var changes = new List<string>();
                foreach (string oldId in oldIds)
                {
                    string current;
                    if (remap.TryGetValue(oldId, out current))
                    {
                        ids.Mapping["previous:" + oldId] = current;
                        if (oldId != current) changes.Add(oldId + "->" + current);
                    }
                    else changes.Add(oldId + "->removed");
                }
                if (changes.Count > 0) effects.Add(new TypedGraphicalEffect { Kind = "selectedNetworkIdReallocation", Identity = networkId,
                    Description = "Structural replacement requires deterministic native export-local UId allocation within this selected network: " + string.Join(", ", changes) + ". Unrelated network and document object IDs are preserved." });
            }
        }
        private static XElement IdlessShape(XElement element)
        {
            var copy = Shape(element); copy.DescendantsAndSelf().Attributes("UId").Remove(); return copy;
        }
        private static XElement RootIdlessShape(XElement element)
        {
            var copy = Shape(element); copy.Attribute("UId")?.Remove();
            if (copy.Name == Net + "Wire") copy.ReplaceNodes(copy.Elements().OrderBy(e => e.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal).ToArray());
            return copy;
        }
        private static XElement Shape(XElement element)
        {
            var copy = new XElement(element.Name, element.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)));
            foreach (var node in element.Nodes())
            {
                if (node is XElement child) copy.Add(Shape(child));
                else if (node is XText text && (!element.HasElements || !string.IsNullOrWhiteSpace(text.Value))) copy.Add(new XText(text.Value));
                else if (node is XComment comment) copy.Add(new XComment(comment.Value));
                else if (node is XProcessingInstruction instruction) copy.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
            }
            return copy;
        }

        private static XDocument CreateEnvelope(TypedGraphicalModel model)
        {
            if (model.Operation != "createBlock" || model.Creation == null || model.Creation.Interface == null || model.Creation.Interface.Length > 2048
                || model.Creation.BlockType != "FC" && model.Creation.BlockType != "FB" || string.IsNullOrWhiteSpace(model.Creation.Name) || model.Creation.Name.Length > 128)
                Fail("InvalidTypedGraphicalModel", "createBlock requires a bounded named FC/FB declaration and explicit interface.");
            string[] allowedSections = model.Creation.BlockType == "FB" ? new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" } : new[] { "Input", "Output", "InOut", "Temp", "Constant", "Return" };
            if (model.Creation.Interface.Any(d => d == null || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 128 || !allowedSections.Contains(d.Section) || !ScalarTypes.Contains(d.DataType ?? ""))
                || model.Creation.Interface.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != model.Creation.Interface.Length)
                Fail("InvalidTypedGraphicalModel", "New declarations require unique names, supported sections and exact scalar types.");
            var sections = new XElement(Sections + "Sections");
            foreach (string section in allowedSections)
            {
                var native = new XElement(Sections + "Section", new XAttribute("Name", section), model.Creation.Interface.Where(d => d.Section == section).Select(d =>
                    new XElement(Sections + "Member", new XAttribute("Name", d.Name), new XAttribute("Datatype", d.DataType))));
                if (section == "Return" && !native.HasElements) native.Add(new XElement(Sections + "Member", new XAttribute("Name", "Ret_Val"), new XAttribute("Datatype", "Void")));
                sections.Add(native);
            }
            return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")),
                new XElement("SW.Blocks." + model.Creation.BlockType, new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("AutoNumber", "true"), new XElement("Interface", sections),
                    new XElement("MemoryLayout", "Optimized"), new XElement("Name", model.Creation.Name), new XElement("Namespace"), new XElement("ProgrammingLanguage", model.Language)), new XElement("ObjectList"))));
        }

        private static void RequireReplaceable(XElement unit, TypedGraphicalProfile[] profiles)
        {
            var network = ReadUnit(unit, profiles);
            if (network.Coverage != "complete") Fail("OpaqueNetworkBoundary", "A partial or opaque network cannot be replaced by typed generation. Use a bounded supported-node edit or the distinct native-fragment workflow.");
            var source = unit.Element("AttributeList")?.Element("NetworkSource");
            if (source == null || source.Elements().Count() != 1 || HasPayloadText(source) || source.Nodes().Any(n => n is XComment || n is XProcessingInstruction)) Fail("OpaqueNetworkBoundary", "Unknown network envelope content must remain opaque.");
        }

        private static TypedGraphicalBuildResult EditOperands(XDocument document, XDocument original, TypedGraphicalModel model, TypedGraphicalContext context)
        {
            if (model.Connections.Length != 0) Fail("UnsupportedTypedEdit", "editNetwork changes selected operand bindings only; supply no replacement connections.");
            var unit = document.Descendants("SW.Blocks.CompileUnit").SingleOrDefault(e => (string)e.Attribute("ID") == model.NetworkId);
            if (unit == null) Fail("NetworkNotFound", "The exact compile-unit ID was not found.");
            if (unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != model.Language) Fail("GraphicalLanguageMismatch", "The selected network has a different language.");
            var network = unit.Element("AttributeList")?.Element("NetworkSource")?.Element(Net + "FlgNet");
            if (network == null) Fail("OpaqueNetworkBoundary", "The selected network is not a supported native FlgNet/v5.");
            var parts = network.Element(Net + "Parts"); var wires = network.Element(Net + "Wires");
            if (parts == null || wires == null) Fail("OpaqueNetworkBoundary", "The selected network lacks observed parts or wires.");
            var ids = new Dictionary<string, string>(StringComparer.Ordinal); var effects = new List<TypedGraphicalEffect>(); var profiles = new List<string>();
            var edited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in model.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
            {
                if (node.Kind != "contact" && node.Kind != "coil" && node.Kind != "assign" || string.IsNullOrWhiteSpace(node.NativeUid) || !edited.Add(node.NativeUid))
                    Fail("UnsupportedTypedEdit", "Each operand edit requires one distinct nativeUid and a contact/coil/assign kind.");
                if (node.Negated || node.InputCount != 0 || node.DataType != null || node.Comparison != null || node.Instruction != null || node.CalleePath != null || node.Instance != null || node.AllowSharedInstance)
                    Fail("UnsupportedTypedEdit", "An operand-only edit cannot request instruction semantic changes.");
                var profile = SelectProfile(node, model.Language, context); profiles.Add(profile.Id);
                var part = parts.Elements(Net + "Part").SingleOrDefault(p => (string)p.Attribute("UId") == node.NativeUid);
                if (part == null || (string)part.Attribute("Name") != profile.NativeName || !IsSupportedPart(part, model.Language)) Fail("OpaqueNetworkBoundary", "The selected instruction is unknown or does not match the declared typed kind.");
                var connections = wires.Elements(Net + "Wire").Where(w => w.Elements(Net + "NameCon").Any(p => (string)p.Attribute("UId") == node.NativeUid && (string)p.Attribute("Name") == "operand")).ToArray();
                if (connections.Length != 1 || connections[0].Elements().Count() != 2 || connections[0].Elements(Net + "IdentCon").Count() != 1) Fail("OpaqueNetworkBoundary", "The selected operand must have one isolated access connection.");
                string accessUid = (string)connections[0].Element(Net + "IdentCon").Attribute("UId");
                var access = parts.Elements(Net + "Access").SingleOrDefault(a => (string)a.Attribute("UId") == accessUid);
                if (access == null || !IsSupportedAccess(access) || network.Descendants(Net + "IdentCon").Count(a => (string)a.Attribute("UId") == accessUid) != 1)
                    Fail("OpaqueNetworkBoundary", "Shared or decorated unknown access content prevents a bounded typed operand edit.");
                var operand = ResolveOperand(context, node.Operand); RequireType("Bool", operand.DataType);
                if (node.Kind != "contact" && !operand.Writable) Fail("OperandNotWritable", "Coil and assignment targets must be writable.");
                // Replace only the scope and Symbol/Constant, preserving UId and unrelated network XML.
                var replacement = BuildAccess(operand, accessUid);
                access.SetAttributeValue("Scope", operand.Scope);
                access.Elements().Single().ReplaceWith(replacement.Elements().Single());
                ids["node:" + node.Key] = node.NativeUid; ids["operand:" + node.Operand] = accessUid;
                effects.Add(new TypedGraphicalEffect { Kind = "operandEdit", Identity = operand.Identity, SourceSha256 = operand.DeclarationHash, Description = "Rebind native instruction " + node.NativeUid + "; retain its UId and isolated access UId " + accessUid + "." });
            }
            return Result(document, original, model.NetworkId, ids, profiles, effects);
        }

        internal static TypedGraphicalReadResult Read(XDocument document, string blockPath, TypedGraphicalProfile[] profiles = null)
        {
            if (document == null || document.Root == null) Fail("InvalidSimaticML", "A native document is required.");
            EngineeringSources.BlockDeclarations(document);
            var objectIds = document.Descendants().Attributes("ID").Select(a => CanonicalObjectId(a.Value)).ToArray();
            if (objectIds.Distinct(StringComparer.Ordinal).Count() != objectIds.Length) Fail("InvalidSimaticML", "Duplicate object ID definitions or hexadecimal aliases in the native document.");
            return new TypedGraphicalReadResult { BlockPath = blockPath, SourceSha256 = Hash(Xml(document)), NativeXml = Xml(document), Networks = document.Descendants("SW.Blocks.CompileUnit").Select(u => ReadUnit(u, profiles)).ToArray() };
        }
        private static TypedGraphicalReadNetwork ReadUnit(XElement unit, TypedGraphicalProfile[] profiles)
        {
            string language = unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value;
            var source = unit.Element("AttributeList")?.Element("NetworkSource");
            var network = source != null && source.Elements().Count() == 1 ? source.Elements().Single() : null;
            var result = new TypedGraphicalReadNetwork { NetworkId = (string)unit.Attribute("ID"), Language = language, Semantics = language == "LAD" ? "powerFlow" : language == "FBD" ? "dataflow" : "opaque", NativeXml = Xml(unit), SourceSha256 = Hash(Xml(unit)) };
            var diagnostics = new List<TypedGraphicalDiagnostic>();
            var nodes = new List<TypedGraphicalReadNode>(); var connections = new List<TypedGraphicalReadConnection>();
            bool supported = (language == "LAD" || language == "FBD") && network?.Name == Net + "FlgNet";
            if (supported)
            {
                var parts = network.Element(Net + "Parts"); var wires = network.Element(Net + "Wires");
                var definitions = network.Descendants().Where(IsDefinition).Select(e => (string)e.Attribute("UId")).ToArray();
                if (definitions.Any(string.IsNullOrWhiteSpace) || definitions.Select(CanonicalUid).Distinct(StringComparer.Ordinal).Count() != definitions.Length) Fail("InvalidSimaticML", "Duplicate, aliased or empty native UId definitions in compile unit " + result.NetworkId);
                foreach (var part in parts?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    var matched = MatchReadProfile(part, language, profiles);
                    bool known = part.Name == Net + "Access" ? IsSupportedAccess(part) : IsSupportedPart(part, language) || matched != null;
                    string uid = (string)part.Attribute("UId"); string nativeName = (string)part.Attribute("Name") ?? part.Name.LocalName;
                    string kind = !known ? "opaque" : part.Name == Net + "Access" ? "operand" : nativeName == "Contact" ? "contact" : nativeName == "Coil" ? (language == "LAD" ? "coil" : "assign") : matched?.Kind ?? "opaque";
                    nodes.Add(new TypedGraphicalReadNode { Key = "native:" + uid, NativeUid = uid, NativeName = nativeName, Kind = kind, Supported = known, NativeXml = Xml(part), SourceSha256 = Hash(Xml(part)), ProfileId = matched?.Id, Pins = matched == null ? new TypedGraphicalPin[0] : ReadPins(part, matched), NegatedPins = part.Elements(Net + "Negated").Select(n => (string)n.Attribute("Name")).ToArray() });
                }
                var uidNodes = nodes.Where(n => n.NativeUid != null).ToDictionary(n => CanonicalUid(n.NativeUid), StringComparer.Ordinal);
                foreach (var wire in wires?.Elements() ?? Enumerable.Empty<XElement>())
                {
                    bool resolved = wire.Name == Net + "Wire" && wire.Attribute("UId") != null && KnownAttributes(wire, "UId") && !HasPayloadText(wire) && wire.Elements().Count() >= 2;
                    var endpoints = new List<TypedGraphicalEndpoint>();
                    foreach (var endpoint in wire.Elements())
                    {
                        string uid = (string)endpoint.Attribute("UId");
                        if (endpoint.Name == Net + "Powerrail" && language == "LAD" && !endpoint.HasAttributes && !endpoint.HasElements && !HasPayloadText(endpoint))
                        {
                            endpoints.Add(new TypedGraphicalEndpoint { Node = "$powerrail", Port = "out" });
                            if (!nodes.Any(n => n.Key == "$powerrail")) nodes.Add(new TypedGraphicalReadNode { Key = "$powerrail", Kind = "powerrail", NativeName = "Powerrail", Supported = true,
                                NativeXml = Xml(endpoint), SourceSha256 = Hash(Xml(endpoint)), Pins = new[] { Port("out", "output", "Bool") } });
                        }
                        else if ((endpoint.Name == Net + "IdentCon" || endpoint.Name == Net + "NameCon") && uid != null)
                        {
                            TypedGraphicalReadNode target;
                            bool targetKnown = uidNodes.TryGetValue(CanonicalUid(uid), out target) && target.Supported;
                            string port = endpoint.Name == Net + "IdentCon" ? "value" : (string)endpoint.Attribute("Name");
                            if (!targetKnown || string.IsNullOrWhiteSpace(port) || !KnownAttributes(endpoint, "UId", "Name") || endpoint.HasElements || HasPayloadText(endpoint)) resolved = false;
                            if (targetKnown && endpoint.Name == Net + "IdentCon" && target.Kind != "operand") resolved = false;
                            if (targetKnown && endpoint.Name == Net + "NameCon" && !AllowedNativePin(target.Kind, port) && !target.Pins.Any(p => p.Name == port)) resolved = false;
                            endpoints.Add(new TypedGraphicalEndpoint { Node = target == null ? "native:" + uid : target.Key, Port = port });
                        }
                        else { resolved = false; endpoints.Add(new TypedGraphicalEndpoint { Node = "opaque:" + endpoint.Name, Port = null }); }
                    }
                    connections.Add(new TypedGraphicalReadConnection { NativeUid = (string)wire.Attribute("UId"), Endpoints = endpoints.ToArray(), Resolved = resolved, NativeXml = Xml(wire) });
                    if (!resolved) diagnostics.Add(new TypedGraphicalDiagnostic { Code = "UnresolvedNativeConnection", Severity = "warning", Message = "Native connection retained opaquely: " + (string)wire.Attribute("UId") });
                }
                supported = parts != null && wires != null && network.Elements(Net + "Parts").Count() == 1 && network.Elements(Net + "Wires").Count() == 1
                    && !HasPayloadText(network) && !HasPayloadText(parts) && !HasPayloadText(wires) && nodes.All(n => n.Supported) && connections.All(c => c.Resolved) && KnownAttributes(network)
                    && network.Elements().All(e => e.Name == Net + "Parts" || e.Name == Net + "Wires") && !network.DescendantNodes().Any(n => n is XComment || n is XProcessingInstruction)
                    && KnownAttributes(parts) && KnownAttributes(wires);
            }
            result.Coverage = supported ? "complete" : nodes.Any(n => n.Supported) ? "partial" : "opaque";
            if (result.Coverage != "complete") diagnostics.Add(new TypedGraphicalDiagnostic { Code = "OpaqueNativeContent", Severity = "information", Message = "Unsupported XML is retained in the native preservation envelope; omission from typed nodes does not imply absence." });
            result.Nodes = nodes.ToArray(); result.Connections = connections.ToArray(); result.Diagnostics = diagnostics.ToArray(); return result;
        }
        private static TypedGraphicalProfile MatchReadProfile(XElement part, string language, TypedGraphicalProfile[] profiles)
        {
            if ((part.Name != Net + "Part" && part.Name != Net + "Call") || part.Attribute("UId") == null) return null;
            foreach (var profile in profiles ?? new TypedGraphicalProfile[0])
            {
                if (profile == null || !profile.NativeFormatValidated || profile.Language != language || string.IsNullOrWhiteSpace(profile.SourceSha256) || string.IsNullOrWhiteSpace(profile.Provenance)) continue;
                if (part.Name == Net + "Call" && profile.Kind == "call")
                {
                    var info = part.Element(Net + "CallInfo");
                    if (KnownAttributes(part, "UId") && !HasPayloadText(part) && part.Elements().Count() == 1 && IsKnownCallInfo(info)) return profile;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(profile.NativeTemplateXml)) continue;
                var template = EngineeringSources.ReadXml(profile.NativeTemplateXml).Root;
                if (template == null || template.Name != part.Name) continue;
                if (profile.Kind == "not" && part.Elements(Net + "TemplateValue").FirstOrDefault(e => (string)e.Attribute("Name") == "Card")?.Value != "1") continue;
                if (XNode.DeepEquals(ProfileShape(template), ProfileShape(part))) return profile;
            }
            return null;
        }
        private static bool IsKnownCallInfo(XElement info)
        {
            if (info == null || !KnownAttributes(info, "Name", "BlockType") || HasPayloadText(info) || string.IsNullOrWhiteSpace((string)info.Attribute("Name"))) return false;
            string blockType = (string)info.Attribute("BlockType");
            if (blockType != "FC" && blockType != "FB") return false;
            if (info.Elements(Net + "Instance").Count() != (blockType == "FB" ? 1 : 0)) return false;
            var parameters = info.Elements(Net + "Parameter").ToArray();
            if (parameters.Select(p => (string)p.Attribute("Name")).Distinct(StringComparer.Ordinal).Count() != parameters.Length) return false;
            return info.Elements().All(e => e.Name == Net + "Instance" ? IsPlainInstance(e) : e.Name == Net + "Parameter" && KnownAttributes(e, "Name", "Section", "Type")
                && !e.HasElements && !HasPayloadText(e) && !string.IsNullOrWhiteSpace((string)e.Attribute("Name")) && ScalarTypes.Contains((string)e.Attribute("Type") ?? "")
                && new[] { "Input", "Output", "InOut", "Return" }.Contains((string)e.Attribute("Section")) && (blockType == "FC" || (string)e.Attribute("Section") != "Return"));
        }
        private static XElement ProfileShape(XElement element)
        {
            var copy = new XElement(element);
            foreach (var instance in copy.Elements(Net + "Instance"))
                if (IsPlainInstance(instance)) { instance.ReplaceAttributes(); instance.ReplaceNodes(); }
            foreach (var uid in copy.DescendantsAndSelf().Attributes("UId").ToArray()) uid.Remove();
            foreach (var card in copy.Elements(Net + "TemplateValue").Where(e => (string)e.Attribute("Name") == "Card"))
            {
                int value; if (int.TryParse(card.Value, out value) && value >= 1 && value <= 32) card.Value = "<cardinality>";
            }
            return Shape(copy);
        }
        private static bool IsPlainInstance(XElement instance) => instance.Attribute("UId") != null && KnownAttributes(instance, "Scope", "UId") && !HasPayloadText(instance) && new[] { "LocalVariable", "GlobalVariable" }.Contains((string)instance.Attribute("Scope"))
            && instance.Elements().Any() && instance.Elements().All(c => c.Name == Net + "Component" && KnownAttributes(c, "Name") && !c.HasElements && !HasPayloadText(c) && !string.IsNullOrWhiteSpace((string)c.Attribute("Name")));
        private static TypedGraphicalPin[] ReadPins(XElement part, TypedGraphicalProfile profile)
        {
            if (profile.Kind == "call") return (profile.Pins ?? new TypedGraphicalPin[0]).Concat(part.Element(Net + "CallInfo").Elements(Net + "Parameter").Select(p => new TypedGraphicalPin {
                Name = (string)p.Attribute("Name"), DataType = (string)p.Attribute("Type"), Direction = (string)p.Attribute("Section") == "Input" ? "input" : (string)p.Attribute("Section") == "InOut" ? "inout" : "output" })).ToArray();
            if (new[] { "and", "or", "not", "branch" }.Contains(profile.Kind))
            {
                int card; if (int.TryParse(part.Elements(Net + "TemplateValue").FirstOrDefault(p => (string)p.Attribute("Name") == "Card")?.Value, out card) && card >= 1 && card <= 32)
                    return Enumerable.Range(1, card).Select(i => Port("in" + i, "input", "Bool")).Concat(new[] { Port("out", "output", "Bool") }).ToArray();
            }
            return profile.Pins ?? new TypedGraphicalPin[0];
        }
        private static bool IsSupportedPart(XElement part, string language)
        {
            if (part.Name != Net + "Part" || part.Attribute("UId") == null || !KnownAttributes(part, "UId", "Name")) return false;
            string name = (string)part.Attribute("Name");
            return (name == "Coil" || name == "Contact" && language == "LAD") && !part.DescendantNodes().Any(n => n is XComment || n is XProcessingInstruction)
                && !HasPayloadText(part) && part.Elements().All(e => name == "Contact" && e.Name == Net + "Negated" && KnownAttributes(e, "Name") && (string)e.Attribute("Name") == "operand" && !e.HasElements && !HasPayloadText(e))
                && part.Elements().Count() <= 1;
        }
        private static bool IsSupportedAccess(XElement access)
        {
            if (access.Attribute("UId") == null || !KnownAttributes(access, "Scope", "UId") || HasPayloadText(access) || access.Elements().Count() != 1 || access.DescendantNodes().Any(n => n is XComment || n is XProcessingInstruction)) return false;
            string scope = (string)access.Attribute("Scope"); var child = access.Elements().Single();
            if (HasPayloadText(child)) return false;
            if (scope == "LocalVariable" || scope == "GlobalVariable") return child.Name == Net + "Symbol" && KnownAttributes(child) && child.Elements().Any()
                && child.Elements().All(c => c.Name == Net + "Component" && KnownAttributes(c, "Name") && !string.IsNullOrWhiteSpace((string)c.Attribute("Name")) && !c.HasElements && !HasPayloadText(c));
            return scope == "LiteralConstant" && child.Name == Net + "Constant" && KnownAttributes(child) && child.Elements().Count() == 2
                && ScalarTypes.Contains(child.Element(Net + "ConstantType")?.Value ?? "") && !string.IsNullOrWhiteSpace(child.Element(Net + "ConstantValue")?.Value) && child.Elements().All(e => KnownAttributes(e) && !e.HasElements);
        }
        private static bool AllowedNativePin(string kind, string port) => kind == "contact" ? port == "in" || port == "out" || port == "operand" : (kind == "coil" || kind == "assign") && (port == "in" || port == "operand");
        private static bool KnownAttributes(XElement element, params string[] names) => element.Attributes().All(a => a.IsNamespaceDeclaration || a.Name.NamespaceName.Length == 0 && names.Contains(a.Name.LocalName, StringComparer.Ordinal));
        private static bool HasPayloadText(XElement element) => element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
        private static bool IsDefinition(XElement element) => element.Attribute("UId") != null && element.Name != Net + "IdentCon" && element.Name != Net + "NameCon";
        private static int ParseUid(string value)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 0) { Fail("InvalidSimaticML", "Native UId must be a nonnegative 32-bit decimal integer."); return 0; }
            return parsed;
        }
        private static string CanonicalUid(string value) => ParseUid(value).ToString(CultureInfo.InvariantCulture);
        private static string CanonicalObjectId(string value)
        {
            if (!string.IsNullOrEmpty(value) && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
            {
                string normalized = value.TrimStart('0').ToUpperInvariant(); return "hex:" + (normalized.Length == 0 ? "0" : normalized);
            }
            return "opaque:" + value;
        }

        /// <summary>Conservatively reserve all existing IDs, including opaque definitions.</summary>
        private sealed class NativeIds
        {
            private readonly HashSet<string> objectIds;
            private readonly HashSet<string> uids;
            private int nextObject;
            private int nextUid = 21;
            internal Dictionary<string, string> Mapping { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
            internal NativeIds(XDocument document, XElement selectedNetwork)
            {
                var rawObjectIds = document.Descendants().Attributes("ID").Select(a => a.Value).ToArray();
                if (rawObjectIds.Select(CanonicalObjectId).Distinct(StringComparer.Ordinal).Count() != rawObjectIds.Length) Fail("InvalidSimaticML", "Duplicate object IDs or hexadecimal aliases prevent deterministic allocation.");
                objectIds = new HashSet<string>(rawObjectIds.Select(CanonicalObjectId), StringComparer.Ordinal);
                // UIds are scoped to a FlgNet. Independently exported native networks
                // each begin at 21; object IDs remain unique across the Document.
                uids = new HashSet<string>((selectedNetwork == null ? Enumerable.Empty<XAttribute>() : selectedNetwork.DescendantsAndSelf().Attributes("UId")).Select(a => CanonicalUid(a.Value)), StringComparer.Ordinal);
                foreach (var network in document.Descendants(Net + "FlgNet"))
                {
                    var definitions = network.Descendants().Where(IsDefinition).Select(e => (string)e.Attribute("UId")).ToArray();
                    if (definitions.Select(CanonicalUid).Distinct(StringComparer.Ordinal).Count() != definitions.Length) Fail("InvalidSimaticML", "Duplicate network UIds or numeric aliases prevent deterministic allocation.");
                }
            }
            internal string ObjectId(string key)
            {
                string candidate;
                do { if (nextObject == int.MaxValue) { Fail("NativeIdExhausted", "Object ID allocation exhausted."); } candidate = (nextObject++).ToString("X", CultureInfo.InvariantCulture); } while (!objectIds.Add(CanonicalObjectId(candidate)));
                Mapping["object:" + key] = candidate; return candidate;
            }
            internal string Uid(string key)
            {
                string existing; if (Mapping.TryGetValue(key, out existing)) return existing;
                string candidate;
                do { if (nextUid == int.MaxValue) { Fail("NativeIdExhausted", "UId allocation exhausted."); } candidate = (nextUid++).ToString(CultureInfo.InvariantCulture); } while (!uids.Add(candidate));
                Mapping[key] = candidate; return candidate;
            }
        }
    }
}
