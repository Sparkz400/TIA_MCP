using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    // Deliberately conservative: comments and unresolved textual expressions may block deletion.
    // Absence is evidence only for the exported offline objects, never runtime alias resolution.
    internal static class DependencyRules
    {
        internal static bool Mentions(XDocument document, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An explicit symbol name is required.", nameof(name));
            string pattern = @"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])";
            return document.Descendants().Any(element => element.Attributes().Any(attribute => Regex.IsMatch(attribute.Value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
                element.Nodes().OfType<XText>().Any(text => Regex.IsMatch(text.Value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
        }

        internal static XDocument WithoutSelectedDeclaration(XDocument document, string inspectedOwnerPath, string selectedOwnerPath, string kind, string name)
        {
            var result = new XDocument(document);
            if (string.IsNullOrEmpty(selectedOwnerPath) || !string.Equals(inspectedOwnerPath, selectedOwnerPath, StringComparison.Ordinal)) return result;
            if (kind != "SW.Tags.PlcTag" && kind != "SW.Tags.PlcUserConstant") throw new ArgumentException("Only a selected tag or user constant declaration can be excluded.", nameof(kind));
            var declarations = result.Descendants().Where(element => element.Name.LocalName == kind &&
                element.Elements().FirstOrDefault(child => child.Name.LocalName == "AttributeList")?.Elements().FirstOrDefault(child => child.Name.LocalName == "Name")?.Value == name).ToArray();
            if (declarations.Length != 1) throw new InvalidOperationException("The selected table export must contain exactly one matching declaration before dependency exclusion.");
            declarations[0].Remove();
            return result;
        }

        internal static void RequirePolicy(string policy)
        {
            if (policy != "rejectReferenced") throw new ArgumentException("dependencyPolicy must be rejectReferenced; cascade deletion and implicit reference rewriting are not supported.");
        }

        internal static void RequireEmpty(int objects, int groups)
        {
            if (objects != 0 || groups != 0) throw new InvalidOperationException("Only empty user groups can be deleted; move or delete contained objects explicitly first.");
        }
    }
}
