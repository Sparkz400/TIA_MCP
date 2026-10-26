#nullable disable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Maps independently observed FB declarations to native multi-instance storage.</summary>
    internal static class TypedGraphicalMultiInstanceTemplate
    {
        private static readonly string[] Persistent = { "Input", "Output", "InOut", "Static" };

        // Each route carries its own independently captured dependent-instance effect pair;
        // IEC instance members have their own captured pair because their native shape differs.
        internal static readonly string[] SystemInstanceTypes = { "TON_TIME", "TOF_TIME", "TP_TIME", "CTU_INT", "CTD_INT", "CTUD_INT" };
        internal static string EffectManifestName(string route, bool systemType = false) =>
            (systemType ? "instance-effects-iec" : "instance-effects") + (route == "standard" ? "" : "-" + route) + ".v1.json";

        // An IEC instance member has a fixed native shape: the new declaration must equal the captured
        // caller member exactly (name aside), and the dependent DB receives the captured DB member.
        internal static XDocument AppendSystemDependent(XDocument current, string exactCallerName, XElement declaration,
            XElement capturedCallerDeclaration, XElement capturedDependentDeclaration)
        {
            const string code = "DependencyProfileUnavailable";
            var db = current?.Root?.Elements("SW.Blocks.InstanceDB").SingleOrDefault();
            var attributes = db?.Element("AttributeList");
            if (attributes?.Element("InstanceOfName")?.Value != exactCallerName || attributes?.Element("InstanceOfType")?.Value != "FB")
                Fail("Only an exact direct FB InstanceDB dependency is covered.", code);
            string type = (string)declaration?.Attribute("Datatype");
            if (!SystemInstanceTypes.Contains(type) || capturedCallerDeclaration == null || capturedDependentDeclaration?.Name.LocalName != "Member"
                || (string)capturedCallerDeclaration.Attribute("Datatype") != type || (string)capturedDependentDeclaration.Attribute("Datatype") != type)
                Fail("An independently captured IEC caller/dependent member pair of the same type is required.", code);
            if (!SameDefaults(declaration, capturedCallerDeclaration)) Fail("The IEC caller declaration differs from the captured effect profile.", code);
            var mapped = new XElement(capturedDependentDeclaration);
            mapped.SetAttributeValue("Name", (string)declaration.Attribute("Name"));
            var result = new XDocument(current);
            var sections = result.Root.Element("SW.Blocks.InstanceDB").Element("AttributeList").Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            ValidateSections(sections, code);
            if (sections.Elements().SelectMany(s => s.Elements()).Any(m => (string)m.Attribute("Name") == (string)mapped.Attribute("Name")))
                Fail("The proposed dependent storage name already exists.", code);
            sections.Elements().Single(s => (string)s.Attribute("Name") == "Static").Add(mapped);
            return result;
        }

        internal static XElement Create(string name, string dataType, XDocument callee, XDocument referenceCallee, XElement capturedDeclaration)
        {
            if (!Regex.IsMatch(name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,79}$")) Fail("A simple explicit multi-instance name is required.");
            var actual = FbSections(callee); var reference = FbSections(referenceCallee);
            var actualAttributes = callee.Root.Element("SW.Blocks.FB").Element("AttributeList");
            var referenceAttributes = referenceCallee.Root.Element("SW.Blocks.FB").Element("AttributeList");
            string actualName = actualAttributes.Element("Name")?.Value;
            string referenceName = referenceAttributes.Element("Name")?.Value;
            string actualNamespace = actualAttributes.Element("Namespace")?.Value ?? "";
            string referenceNamespace = referenceAttributes.Element("Namespace")?.Value ?? "";
            if (actualNamespace != referenceNamespace) Fail("The actual FB namespace differs from the independently captured declaration route.");
            if (actualNamespace.Length > 0) actualName = actualNamespace + "." + actualName;
            if (referenceNamespace.Length > 0) referenceName = referenceNamespace + "." + referenceName;
            if (dataType != "\"" + actualName + "\"" || (string)capturedDeclaration?.Attribute("Datatype") != "\"" + referenceName + "\"")
                Fail("The declared storage type must bind the exact actual and captured FB names.");
            var result = MapDeclaration(actual, reference, capturedDeclaration, "GraphicalInstanceTemplateUnavailable");
            result.SetAttributeValue("Name", name); result.SetAttributeValue("Datatype", dataType);
            return result;
        }

        // The caller and dependent declarations must come from a separately captured
        // before/after dependency pair. The current DB is cloned in full; only the
        // explicitly new direct member is appended. Existing starts/attributes survive.
        internal static XDocument AppendDependent(XDocument current, string exactCallerName, XElement declaration,
            XElement capturedCallerDeclaration, XElement capturedDependentDeclaration)
        {
            const string code = "DependencyProfileUnavailable";
            var db = current?.Root?.Elements("SW.Blocks.InstanceDB").SingleOrDefault();
            var attributes = db?.Element("AttributeList");
            if (attributes?.Element("InstanceOfName")?.Value != exactCallerName || attributes?.Element("InstanceOfType")?.Value != "FB")
                Fail("Only an exact direct FB InstanceDB dependency is covered.", code);
            var actualSections = declaration?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            var referenceSections = capturedCallerDeclaration?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            if (actualSections == null || referenceSections == null || capturedDependentDeclaration == null)
                Fail("An independently captured caller/dependent storage declaration pair is required.", code);
            var actualOuter = new XElement(declaration); actualOuter.Elements().Where(e => e.Name.LocalName == "Sections").Remove();
            var referenceOuter = new XElement(capturedCallerDeclaration); referenceOuter.Elements().Where(e => e.Name.LocalName == "Sections").Remove();
            actualOuter.SetAttributeValue("Datatype", "prototype"); referenceOuter.SetAttributeValue("Datatype", "prototype");
            if (!SameDefaults(actualOuter, referenceOuter)) Fail("Caller declaration defaults differ from the dependency profile.", code);
            var mapped = MapDeclaration(actualSections, referenceSections, capturedDependentDeclaration, code);
            mapped.SetAttributeValue("Name", (string)declaration.Attribute("Name"));
            mapped.SetAttributeValue("Datatype", (string)declaration.Attribute("Datatype"));
            var result = new XDocument(current);
            var sections = result.Root.Element("SW.Blocks.InstanceDB").Element("AttributeList").Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            ValidateSections(sections, code);
            if (sections.Elements().SelectMany(s => s.Elements()).Any(m => (string)m.Attribute("Name") == (string)mapped.Attribute("Name")))
                Fail("The proposed dependent storage name already exists.", code);
            sections.Elements().Single(s => (string)s.Attribute("Name") == "Static").Add(mapped);
            return result;
        }

        private static XElement FbSections(XDocument document)
        {
            var attributes = document?.Root?.Elements("SW.Blocks.FB").SingleOrDefault()?.Element("AttributeList");
            bool capturedUnitShape = attributes?.Element("MemoryLayout") == null && !string.IsNullOrEmpty(attributes?.Element("Namespace")?.Value)
                && attributes?.Element("Access") != null;
            if (attributes?.Element("MemoryLayout")?.Value != "Optimized" && !capturedUnitShape)
                Fail("Only an observed optimized standard or exact software-unit FB interface is covered.");
            var sections = attributes.Element("Interface")?.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            ValidateSections(sections, "GraphicalInstanceTemplateUnavailable", true);
            return sections;
        }

        private static XElement MapDeclaration(XElement actual, XElement reference, XElement capturedDeclaration, string code)
        {
            if (capturedDeclaration?.Name.LocalName != "Member") Fail("A complete independently captured Member is required.", code);
            var result = new XElement(capturedDeclaration);
            var stored = result.Elements().SingleOrDefault(e => e.Name.LocalName == "Sections");
            ValidateSections(actual, code, true); ValidateSections(reference, code, true); ValidateSections(stored, code);
            if (result.Descendants().Any(e => e.Name.LocalName == "StartValue" || e.Name.LocalName == "DefaultValue"))
                Fail("Custom storage starts need their own native profile.", code);
            foreach (var name in Persistent)
            {
                var source = actual.Elements().Single(s => (string)s.Attribute("Name") == name);
                var referenceMembers = reference.Elements().Single(s => (string)s.Attribute("Name") == name).Elements().ToArray();
                var target = stored.Elements().Single(s => (string)s.Attribute("Name") == name);
                var storedMembers = target.Elements().ToArray();
                if (referenceMembers.Length != storedMembers.Length || referenceMembers.Any(m => storedMembers.Count(p => (string)p.Attribute("Name") == (string)m.Attribute("Name") && (string)p.Attribute("Datatype") == (string)m.Attribute("Datatype")) != 1))
                    Fail("The captured callee/storage declaration pair is incomplete or ambiguous.", code);
                var mapped = source.Elements().Select(member =>
                {
                    if (member.Name.LocalName != "Member" || member.Descendants().Any(e => e.Name.LocalName == "Member" || e.Name.LocalName == "StartValue" || e.Name.LocalName == "DefaultValue"))
                        Fail("Structured or custom-start FB declarations require another profile.", code);
                    var matches = referenceMembers.Where(p => (string)p.Attribute("Datatype") == (string)member.Attribute("Datatype") && SameDefaults(p, member)).ToArray();
                    if (matches.Length == 0) Fail("No captured matching FB defaults for " + name + "/" + (string)member.Attribute("Datatype") + ".", code);
                    var templates = matches.Select(m => storedMembers.Single(p => (string)p.Attribute("Name") == (string)m.Attribute("Name"))).ToArray();
                    if (templates.Skip(1).Any(p => !SameDefaults(templates[0], p))) Fail("Captured storage defaults are ambiguous.", code);
                    var copy = new XElement(templates[0]); copy.SetAttributeValue("Name", (string)member.Attribute("Name")); return copy;
                }).ToArray();
                target.ReplaceNodes(mapped);
            }
            return result;
        }

        private static void ValidateSections(XElement sections, string code, bool allowTransient = false)
        {
            if (sections == null || sections.Name.LocalName != "Sections" || Persistent.Any(name => sections.Elements().Count(s => s.Name.LocalName == "Section" && (string)s.Attribute("Name") == name) != 1)
                || sections.Elements().Any(s => !Persistent.Contains((string)s.Attribute("Name")) && !(allowTransient && new[] { "Temp", "Constant" }.Contains((string)s.Attribute("Name")))))
                Fail("The native persistent section topology is not covered.", code);
        }
        private static bool SameDefaults(XElement left, XElement right)
        {
            var a = new XElement(left); var b = new XElement(right); a.SetAttributeValue("Name", "prototype"); b.SetAttributeValue("Name", "prototype");
            try { EngineeringSources.RequireNativePreservation(new XDocument(new XElement("Document", a)), new XDocument(new XElement("Document", b))); return true; }
            catch (BridgeException) { return false; }
        }
        private static void Fail(string message, string code = "GraphicalInstanceTemplateUnavailable") => throw new BridgeException(code, message);
    }
}
