using System;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    internal static class SourceRefreshRules
    {
        internal sealed class BlockParts { internal string Number, Interface, Body, Formatted; }

        // UIds number source tokens and change with layout; NewLine and Blank tokens are layout only.
        private static string Canonical(XElement element, bool layout)
        {
            var clone = new XElement(element);
            clone.DescendantsAndSelf().Attributes("UId").Remove();
            if (!layout) clone.Descendants().Where(e => e.Name.LocalName == "NewLine" || e.Name.LocalName == "Blank").Remove();
            return TagTableImportRules.Canonical(clone);
        }

        internal static BlockParts Parts(string xml)
        {
            var document = XDocument.Parse(xml);
            var block = document.Root.Elements().First(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            var attributes = block.Element("AttributeList");
            var face = attributes?.Element("Interface");
            var body = new XElement(block);
            body.Element("AttributeList")?.Element("Interface")?.Remove();
            return new BlockParts { Number = (string)attributes?.Element("Number"),
                Interface = face == null ? "" : Canonical(face, false), Body = Canonical(body, false), Formatted = Canonical(body, true) };
        }
    }
}
