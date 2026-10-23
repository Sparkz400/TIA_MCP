#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// Prepare a complete native creation envelope from one independently
    /// captured export. Target/profile provenance is checked by the coordinator.
    /// No inferred default attributes, declaration layouts or XML normalization.
    /// </summary>
    internal static class TypedGraphicalCreationTemplate
    {
        internal static XDocument Create(TypedGraphicalModel model, string nameSpace, int exactNumber, XDocument template)
        {
            if (model == null || model.SchemaVersion != 1 || model.Operation != "createBlock" || model.Creation == null || model.Creation.Interface == null)
                Fail("A version 1 createBlock model and explicit interface are required.");
            if (model.Language != "LAD" && model.Language != "FBD" || model.Creation.BlockType != "FC" && model.Creation.BlockType != "FB")
                Fail("Creation template requires an explicit LAD/FBD FC/FB family.");
            if (!Regex.IsMatch(model.Creation.Name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,79}$") || exactNumber < 1 || exactNumber > 65535)
                Fail("Creation requires a bounded simple name and an explicitly reserved block number in 1..65535.");
            if (template == null) Fail("A complete independent native template is required.");
            var document = new XDocument(template);
            var declarations = EngineeringSources.BlockDeclarations(document);
            if (declarations.Length != 1 || declarations[0].Language != model.Language)
                Fail("The independent native template must contain exactly one block in the requested language.");
            var block = document.Root.Elements().SingleOrDefault(e => e.Name.LocalName == "SW.Blocks." + model.Creation.BlockType);
            if (block == null) Fail("The template block family must match the requested FC/FB.");
            var attributes = block.Element("AttributeList");
            // Independently exported software-unit blocks omit MemoryLayout;
            // retain that native absence only on the exact captured namespace.
            bool observedUnitEnvelope = attributes?.Element("MemoryLayout") == null && !string.IsNullOrEmpty(nameSpace)
                && attributes?.Element("Namespace")?.Value == nameSpace && attributes?.Element("Access") != null;
            if (attributes == null || attributes.Element("MemoryLayout")?.Value != "Optimized" && !observedUnitEnvelope)
                Fail("Only a captured optimized or exact software-unit native creation envelope is supported.");
            foreach (string field in new[] { "AutoNumber", "Name", "Namespace", "Number", "ProgrammingLanguage" })
                if (attributes.Elements(field).Count() != 1) Fail("The native template lacks one exact required attribute: " + field);
            var sections = attributes.Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            if (sections == null) Fail("The native template lacks its exact interface sections.");
            var requests = model.Creation.Interface;
            if (requests.Length > 500 || requests.Any(d => d == null || !Regex.IsMatch(d.Name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,127}$") || string.IsNullOrWhiteSpace(d.DataType))
                || requests.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Length)
                Fail("Creation declarations must have bounded, unique explicit names and types.");
            if (requests.Count(d => d.Section == "Return") > 1) Fail("An FC accepts at most one return declaration.");
            var sectionNames = sections.Elements().Select(e => e.Attribute("Name")?.Value).ToArray();
            if (sectionNames.Any(string.IsNullOrEmpty) || sectionNames.Distinct(StringComparer.Ordinal).Count() != sectionNames.Length)
                Fail("The native template sections are incomplete or ambiguous.");
            if (requests.Any(d => !sectionNames.Contains(d.Section, StringComparer.Ordinal)))
                Fail("A requested section is absent from the captured native block family.");

            // Capture exact declaration prototypes before replacing any members.
            // Equivalent duplicates are allowed; divergent defaults require a
            // separately captured explicit template instead of guessing.
            var prototypes = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var section in sections.Elements())
                foreach (var member in section.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    string type = member.Attribute("Datatype")?.Value;
                    if (string.IsNullOrEmpty(type)) continue;
                    var prototype = new XElement(member); prototype.SetAttributeValue("Name", "prototype");
                    if (prototype.Descendants().Any(e => e.Name.LocalName == "Member") || prototype.Descendants().Any(e => e.Name.LocalName == "StartValue" || e.Name.LocalName == "DefaultValue")) continue;
                    string key = section.Attribute("Name").Value + "\n" + type;
                    XElement existing;
                    if (prototypes.TryGetValue(key, out existing) && !XNode.DeepEquals(existing, prototype))
                        Fail("Captured declaration prototypes have divergent native defaults for " + key.Replace('\n', '/') + ".");
                    prototypes[key] = prototype;
                }
            foreach (var section in sections.Elements())
            {
                string sectionName = section.Attribute("Name").Value;
                if (section.Elements().Any(e => e.Name.LocalName != "Member")) Fail("Opaque interface section content cannot be reused as a creation template.");
                var members = new List<XElement>();
                var declared = requests.Where(d => d.Section == sectionName).ToArray();
                if (sectionName == "Return" && declared.Length == 0)
                    declared = new[] { new TypedGraphicalDeclaration { Name = "Ret_Val", Section = "Return", DataType = "Void" } };
                foreach (var requested in declared)
                {
                    XElement prototype;
                    if (!prototypes.TryGetValue(sectionName + "\n" + requested.DataType, out prototype))
                        Fail("No captured native declaration prototype for " + sectionName + "/" + requested.DataType + ".");
                    var member = new XElement(prototype); member.SetAttributeValue("Name", requested.Name); members.Add(member);
                }
                section.ReplaceNodes(members);
            }
            attributes.Element("Name").Value = model.Creation.Name;
            attributes.Element("Namespace").Value = nameSpace ?? "";
            // Native software-unit import rejects the standard manual-number
            // envelope. The separately captured route retains AutoNumber=true
            // while accepting/preserving the explicitly reserved Number.
            if (!observedUnitEnvelope) attributes.Element("AutoNumber").Value = "false";
            attributes.Element("Number").Value = exactNumber.ToString(CultureInfo.InvariantCulture);

            var objects = block.Element("ObjectList");
            var units = objects?.Elements("SW.Blocks.CompileUnit").ToArray();
            if (units == null || units.Length != 1) Fail("The creation template must contain exactly one native compile unit.");
            var unit = units[0];
            if (unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != model.Language)
                Fail("The captured compile unit language differs from the block language.");
            var source = unit.Element("AttributeList")?.Element("NetworkSource");
            if (source == null || source.Elements().Count() != 1 || source.Elements().Single().Name != TypedGraphical.Net + "FlgNet")
                Fail("The creation template requires one native FlgNet/v5 network.");
            source.ReplaceNodes(new XElement(TypedGraphical.Net + "FlgNet", new XElement(TypedGraphical.Net + "Parts"), new XElement(TypedGraphical.Net + "Wires")));
            // Only default empty multilingual text is a reusable blank template.
            if (block.Descendants("Text").Any(text => !string.IsNullOrEmpty(text.Value)))
                Fail("Nonempty native comments/titles require an explicit template adaptation profile.");
            EngineeringSources.BlockDeclarations(document);
            return document;
        }
        private static void Fail(string message) => throw new BridgeException("GraphicalCreationTemplateUnavailable", message);
    }
}
