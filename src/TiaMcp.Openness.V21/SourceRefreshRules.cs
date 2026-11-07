using System;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    internal static class SourceRefreshRules
    {
        internal sealed class BlockParts { internal string Number, Interface, Body; }

        internal static BlockParts Parts(string xml)
        {
            var document = XDocument.Parse(xml);
            var block = document.Root.Elements().First(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            var attributes = block.Element("AttributeList");
            var face = attributes?.Element("Interface");
            var body = new XElement(block);
            body.Element("AttributeList")?.Element("Interface")?.Remove();
            return new BlockParts { Number = (string)attributes?.Element("Number"),
                Interface = face == null ? "" : TagTableImportRules.Canonical(face), Body = TagTableImportRules.Canonical(body) };
        }
    }
}
