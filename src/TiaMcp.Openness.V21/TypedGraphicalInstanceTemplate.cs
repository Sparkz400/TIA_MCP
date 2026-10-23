#nullable disable
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Complete global FB instance envelope; never fabricates native storage layout.</summary>
    internal static class TypedGraphicalInstanceTemplate
    {
        internal static XDocument Create(string name, int exactNumber, XDocument callee, XDocument template)
        {
            if (!Regex.IsMatch(name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,79}$") || exactNumber < 1 || exactNumber > 65535)
                Fail("An explicit safe name and reserved block number in 1..65535 are required.");
            if (callee == null || template == null) Fail("Fresh exact callee XML and an independently captured native instance template are required.");
            EngineeringSources.BlockDeclarations(callee); EngineeringSources.BlockDeclarations(template);
            var fb = callee.Root.Elements("SW.Blocks.FB").SingleOrDefault();
            var document = new XDocument(template);
            var db = document.Root.Elements("SW.Blocks.InstanceDB").SingleOrDefault();
            if (fb == null || db == null) Fail("Only an exact FB and a captured standard FB InstanceDB template are supported.");
            var source = fb.Element("AttributeList"); var attributes = db.Element("AttributeList");
            string sourceNamespace = source?.Element("Namespace")?.Value ?? "";
            string targetNamespace = attributes?.Element("Namespace")?.Value ?? "";
            bool unitEnvelope = sourceNamespace.Length > 0 && sourceNamespace == targetNamespace
                && source?.Element("MemoryLayout") == null && source?.Element("Access") != null && attributes?.Element("Access") != null;
            if (source?.Element("MemoryLayout")?.Value != "Optimized" && !unitEnvelope || attributes?.Element("InstanceOfType")?.Value != "FB"
                || attributes?.Element("ProgrammingLanguage")?.Value != "DB") Fail("The captured instance/callee native families do not match.");
            if (sourceNamespace != targetNamespace || sourceNamespace.Length > 0 && !unitEnvelope)
                Fail("The callee namespace must match the exact separately captured storage envelope namespace.");
            foreach (string field in new[] { "AutoNumber", "Name", "Number", "InstanceOfName", "InstanceOfType", "Namespace" })
                if (attributes.Elements(field).Count() != 1) Fail("The captured instance lacks one exact native attribute: " + field);
            if (attributes.Element("InstanceOfNumber") != null) Fail("This observed standard template binds InstanceOfName; another binding format needs a separate profile.");
            string calleeName = source.Element("Name")?.Value;
            if (string.IsNullOrWhiteSpace(calleeName)) Fail("The exact exported FB name is missing.");
            if (sourceNamespace.Length > 0) calleeName = sourceNamespace + "." + calleeName;
            var sourceSections = source.Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            var targetSections = attributes.Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            string[] persistent = { "Input", "Output", "InOut", "Static" };
            if (sourceSections == null || targetSections == null || targetSections.Elements().Count() != persistent.Length
                || persistent.Any(section => targetSections.Elements().Count(s => (string)s.Attribute("Name") == section) != 1)
                || persistent.Any(section => sourceSections.Elements().Count(s => (string)s.Attribute("Name") == section) != 1))
                Fail("The native instance/callee persistent section topology is unavailable.");
            foreach (string sectionName in persistent)
            {
                var from = sourceSections.Elements().Single(s => (string)s.Attribute("Name") == sectionName);
                var to = targetSections.Elements().Single(s => (string)s.Attribute("Name") == sectionName);
                var prototypes = to.Elements().ToArray();
                var members = from.Elements().ToArray();
                if (members.Any(member => member.Name.LocalName != "Member" || member.Descendants().Any(e => e.Name.LocalName == "Member" || e.Name.LocalName == "StartValue" || e.Name.LocalName == "DefaultValue")))
                    Fail("Structured declarations and custom start values need their own independently observed storage profile.");
                foreach (var member in members)
                {
                    string type = (string)member.Attribute("Datatype");
                    var matching = prototypes.Where(p => p.Name == member.Name && (string)p.Attribute("Datatype") == type).ToArray();
                    if (matching.Length == 0 || !matching.Any(p => SameMemberDefaults(p, member)))
                        Fail("No observed matching native instance declaration defaults for " + sectionName + "/" + type + ".");
                }
                to.ReplaceNodes(members.Select(member => new XElement(member)));
            }
            if (db.Descendants("Text").Any(text => !string.IsNullOrEmpty(text.Value)))
                Fail("The reusable native storage template must have default empty comments/titles.");
            if (!unitEnvelope) attributes.Element("AutoNumber").Value = "false";
            attributes.Element("Name").Value = name;
            attributes.Element("Number").Value = exactNumber.ToString(CultureInfo.InvariantCulture);
            attributes.Element("InstanceOfName").Value = calleeName;
            return document;
        }
        private static bool SameMemberDefaults(XElement prototype, XElement actual)
        {
            var left = new XElement(prototype); var right = new XElement(actual);
            left.SetAttributeValue("Name", "prototype"); right.SetAttributeValue("Name", "prototype");
            try
            {
                EngineeringSources.RequireNativePreservation(new XDocument(new XElement("Document", left)), new XDocument(new XElement("Document", right)));
                return true;
            }
            catch (BridgeException) { return false; }
        }
        private static void Fail(string message) => throw new BridgeException("GraphicalInstanceTemplateUnavailable", message);
    }
}
