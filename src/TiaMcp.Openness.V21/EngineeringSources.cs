#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Strict import envelope validation; Siemens remains the language compiler.</summary>
    internal static class EngineeringSources
    {
        internal static string[] DeclaredBlocks(string source)
        {
            return Declarations(source, false).Names;
        }

        internal static string[] DeclaredTypes(string source)
        {
            return Declarations(source, true).Names;
        }

        internal static string DeclaredNamespace(string source, bool typesOnly) => Declarations(source, typesOnly).Namespace;

        internal static string SimaticSdDeclarationEnvelope(string source)
        {
            // SIMATIC SD language pragmas may precede a block declaration. Remove
            // only this exact, bounded pragma for declaration scanning; the native
            // import receives the original bytes. All other top-level content
            // still goes through the normal strict declaration lexer.
            return Regex.Replace(source, @"(?m)^[ \t]*\{\s*(?:(?:S7_Optimized\s*:=\s*""(?:TRUE|FALSE)""|S7_PreferredLanguage\s*:=\s*""(?:LAD|FBD)""|S7_Language\s*:=\s*""(?:LAD|FBD)""|S7_Version\s*:=\s*""[0-9]+\.[0-9]+"")\s*;?\s*)+\}[ \t]*(?:\r?\n|$)", "", RegexOptions.CultureInvariant);
        }

        internal static string[] NamedXmlObjects(XDocument document, string elementName)
        {
            if (document.Root == null || document.Root.Name != "Document") throw new BridgeException("InvalidSimaticML", "A native Document root is required.");
            var elements = document.Root.Elements().Where(e => e.Name != "Engineering" && e.Name != "DocumentInfo").ToArray();
            if (elements.Length == 0 || elements.Length > 100 || elements.Any(e => e.Name != elementName)) throw new BridgeException("XmlScopeMismatch", "Only top-level " + elementName + " objects are accepted.");
            var names = elements.Select(e => e.Element("AttributeList")?.Element("Name")?.Value).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new BridgeException("XmlScopeMismatch", "Imported objects require unique explicit names.");
            return names;
        }

        private static SourceDeclarations Declarations(string source, bool typesOnly)
        {
            var tokens = LexSource(source).ToArray();
            var starts = new HashSet<string>(new[] { "FUNCTION_BLOCK", "FUNCTION", "DATA_BLOCK", "ORGANIZATION_BLOCK", "TYPE" }, StringComparer.OrdinalIgnoreCase);
            var forbidden = new HashSet<string>(new[] { "CONFIGURATION", "RESOURCE", "PLC_TAG_TABLE", "VAR_GLOBAL" }, StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            var namespaces = new List<string>();
            string expectedEnd = null;
            string currentNamespace = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (token.Kind == SourceTokenKind.Word && string.Equals(token.Text, "NAMESPACE", StringComparison.OrdinalIgnoreCase))
                {
                    if (expectedEnd != null || currentNamespace != null) throw new BridgeException("SourceScopeMismatch", "Nested namespaces or namespaces inside block/type declarations are not accepted.");
                    if (++i >= tokens.Length || (tokens[i].Kind != SourceTokenKind.Word && tokens[i].Kind != SourceTokenKind.QuotedName))
                        throw new BridgeException("InvalidSource", "NAMESPACE requires an explicit name.");
                    var parts = new List<string> { tokens[i].Text };
                    while (i + 1 < tokens.Length && tokens[i + 1].Kind == SourceTokenKind.Punctuation && tokens[i + 1].Text == ".")
                    {
                        i += 2;
                        if (i >= tokens.Length || (tokens[i].Kind != SourceTokenKind.Word && tokens[i].Kind != SourceTokenKind.QuotedName)) throw new BridgeException("InvalidSource", "A namespace dot must be followed by a name component.");
                        parts.Add(tokens[i].Text);
                    }
                    if (parts.Any(part => !Regex.IsMatch(part, @"^[\p{L}_][\p{L}\p{N}_]*$", RegexOptions.CultureInvariant)))
                        throw new BridgeException("InvalidSource", "This source envelope supports identifier namespace components only.");
                    currentNamespace = string.Join(".", parts);
                    // Even an empty wrapper is an explicitly named source scope. Do not
                    // let it disappear when checking a later declaration's destination.
                    namespaces.Add(currentNamespace);
                    continue;
                }
                if (token.Kind == SourceTokenKind.Word && string.Equals(token.Text, "END_NAMESPACE", StringComparison.OrdinalIgnoreCase))
                {
                    if (expectedEnd != null || currentNamespace == null) throw new BridgeException("SourceScopeMismatch", "Unexpected or premature END_NAMESPACE.");
                    currentNamespace = null;
                    continue;
                }
                if (token.Kind == SourceTokenKind.Word && forbidden.Contains(token.Text)) throw new BridgeException("SourceScopeMismatch", "This source workflow does not accept configuration, resource or global-tag declarations.");
                if (token.Kind == SourceTokenKind.Word && starts.Contains(token.Text))
                {
                    if (expectedEnd != null) throw new BridgeException("SourceScopeMismatch", "Nested or unterminated block/type declaration.");
                    bool isType = string.Equals(token.Text, "TYPE", StringComparison.OrdinalIgnoreCase);
                    if (isType != typesOnly) throw new BridgeException("SourceScopeMismatch", "Block and data-type declarations require separate prepared changes.");
                    if (++i >= tokens.Length || (tokens[i].Kind != SourceTokenKind.Word && tokens[i].Kind != SourceTokenKind.QuotedName))
                        throw new BridgeException("InvalidSource", "Every declaration requires an explicit symbolic name.");
                    string name = tokens[i].Text;
                    if (i + 1 < tokens.Length && tokens[i + 1].Kind == SourceTokenKind.Punctuation && tokens[i + 1].Text == ".")
                        throw new BridgeException("SourceScopeMismatch", "Declaration names must be unqualified; use an explicit NAMESPACE envelope for namespace selection.");
                    if (tokens[i].Kind == SourceTokenKind.Word && Regex.IsMatch(name, @"^(FB|FC|DB|OB|UDT)(\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        throw new BridgeException("SourceScopeMismatch", "Number-addressed declarations must be replaced by explicit quoted symbolic names to prevent unpreviewed number collisions.");
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.IndexOf('$') >= 0 || name.IndexOf('"') >= 0)
                        throw new BridgeException("InvalidSource", "Escaped or invalid declaration names are outside this source-import scope.");
                    names.Add(name);
                    namespaces.Add(currentNamespace ?? "");
                    expectedEnd = "END_" + token.Text.ToUpperInvariant();
                    continue;
                }
                if (token.Kind == SourceTokenKind.Word && starts.Any(start => string.Equals(token.Text, "END_" + start, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.Equals(expectedEnd, token.Text, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("SourceScopeMismatch", "Mismatched declaration end token.");
                    expectedEnd = null;
                    continue;
                }
                if (expectedEnd == null && !(token.Kind == SourceTokenKind.Punctuation && token.Text == ";"))
                    throw new BridgeException("SourceScopeMismatch", "Unexpected content outside a complete block/type declaration.");
            }
            if (expectedEnd != null || currentNamespace != null || names.Count == 0 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                throw new BridgeException("SourceScopeMismatch", "Expected complete, uniquely named declarations.");
            if (namespaces.Distinct(StringComparer.Ordinal).Count() != 1) throw new BridgeException("NamespaceMismatch", "Each source change must target one namespace; split mixed namespace declarations into separate changes.");
            return new SourceDeclarations { Names = names.ToArray(), Namespace = namespaces[0] };
        }

        private static IEnumerable<SourceToken> LexSource(string source)
        {
            for (int i = 0; i < source.Length;)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';
                if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
                if (c == '/' && next == '/') { i += 2; while (i < source.Length && source[i] != '\r' && source[i] != '\n') i++; continue; }
                if (c == '(' && next == '*')
                {
                    int depth = 1; i += 2;
                    while (i < source.Length && depth > 0)
                    {
                        if (i + 1 < source.Length && source[i] == '(' && source[i + 1] == '*') { depth++; i += 2; }
                        else if (i + 1 < source.Length && source[i] == '*' && source[i + 1] == ')') { depth--; i += 2; }
                        else i++;
                    }
                    if (depth != 0) throw new BridgeException("InvalidSource", "Unterminated source comment.");
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    char quote = c; i++;
                    var value = new StringBuilder();
                    bool closed = false;
                    while (i < source.Length)
                    {
                        c = source[i++];
                        if (c == '$' && i < source.Length) { value.Append(c); value.Append(source[i++]); continue; }
                        if (c == quote)
                        {
                            if (i < source.Length && source[i] == quote) { value.Append(quote); i++; continue; }
                            closed = true; break;
                        }
                        if (quote == '"' && (c == '\r' || c == '\n')) throw new BridgeException("InvalidSource", "Quoted names cannot span lines.");
                        value.Append(c);
                    }
                    if (!closed) throw new BridgeException("InvalidSource", "Unterminated source literal or symbolic name.");
                    yield return new SourceToken { Kind = quote == '"' ? SourceTokenKind.QuotedName : SourceTokenKind.Literal, Text = value.ToString() };
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i++;
                    while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    yield return new SourceToken { Kind = SourceTokenKind.Word, Text = source.Substring(start, i - start) };
                    continue;
                }
                yield return new SourceToken { Kind = SourceTokenKind.Punctuation, Text = c.ToString() }; i++;
            }
        }
        private enum SourceTokenKind { Word, QuotedName, Literal, Punctuation }
        private sealed class SourceToken { internal SourceTokenKind Kind; internal string Text; }
        private sealed class SourceDeclarations { internal string[] Names; internal string Namespace; }

        internal static XDocument ReadXml(string xml)
        {
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 }))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }

        internal static BlockDeclaration[] BlockDeclarations(XDocument document)
        {
            if (document.Root == null || document.Root.Name != "Document") throw new BridgeException("InvalidSimaticML", "A native SimaticML Document root is required.");
            var allIds = document.Root.DescendantsAndSelf().Attributes("ID").Select(attribute => attribute.Value).ToArray();
            if (allIds.Distinct(StringComparer.Ordinal).Count() != allIds.Length) throw new BridgeException("InvalidSimaticML", "Duplicate object ID definitions in the XML document.");
            var allowed = new HashSet<string>(new[] { "SW.Blocks.FC", "SW.Blocks.FB", "SW.Blocks.OB", "SW.Blocks.GlobalDB", "SW.Blocks.InstanceDB" }, StringComparer.Ordinal);
            var blocks = new List<BlockDeclaration>();
            foreach (var child in document.Root.Elements())
            {
                if (child.Name == "Engineering" || child.Name == "DocumentInfo") continue;
                if (!allowed.Contains(child.Name.LocalName) || child.Name.NamespaceName.Length != 0)
                    throw new BridgeException("XmlScopeMismatch", "Only explicit top-level FB/FC/OB/DB objects can be imported through this workflow: " + child.Name);
                var attributes = child.Element("AttributeList");
                string name = attributes?.Element("Name")?.Value;
                string language = attributes?.Element("ProgrammingLanguage")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language)) throw new BridgeException("InvalidSimaticML", "Every imported block requires an explicit Name and ProgrammingLanguage.");
                if (child.Descendants().Any(e => e.Name.NamespaceName.Length == 0 && allowed.Contains(e.Name.LocalName)))
                    throw new BridgeException("XmlScopeMismatch", "Nested block declarations are not allowed.");
                // Declared IDs can be referred to repeatedly; definitions themselves must be unique.
                var definitions = child.DescendantsAndSelf().Attributes("ID").Select(a => a.Value).ToArray();
                if (definitions.Distinct(StringComparer.Ordinal).Count() != definitions.Length) throw new BridgeException("InvalidSimaticML", "Duplicate object ID definitions in block " + name);
                blocks.Add(new BlockDeclaration { Name = name, Language = language, IsSafety = language.StartsWith("F_", StringComparison.Ordinal) });
            }
            if (blocks.Count == 0 || blocks.Count > 100 || blocks.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != blocks.Count)
                throw new BridgeException("XmlScopeMismatch", "XML must contain 1–100 uniquely named blocks.");
            return blocks.ToArray();
        }

        internal static string SclFixture(string name) => "FUNCTION \"" + name + "\" : Void\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n Enable : Bool;\r\nEND_VAR\r\nVAR_OUTPUT\r\n Active : Bool;\r\nEND_VAR\r\nBEGIN\r\n #Active := #Enable;\r\nEND_FUNCTION\r\n";
        internal static string StlFixture(string name) => "FUNCTION \"" + name + "\" : Void\r\n{ S7_Optimized_Access := 'TRUE' }\r\nVERSION : 0.1\r\nVAR_INPUT\r\n Enable : Bool;\r\nEND_VAR\r\nVAR_OUTPUT\r\n Active : Bool;\r\nEND_VAR\r\nBEGIN\r\nNETWORK\r\nTITLE = Enable gate\r\n A #Enable;\r\n = #Active;\r\nEND_FUNCTION\r\n";

        internal static string GraphicalFixture(string name, string language)
        {
            XNamespace sections = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
            XNamespace network = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
            var parts = new XElement(network + "Parts",
                new XElement(network + "Access", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", "21"), new XElement(network + "Symbol", new XElement(network + "Component", new XAttribute("Name", "Enable")))),
                new XElement(network + "Access", new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", "22"), new XElement(network + "Symbol", new XElement(network + "Component", new XAttribute("Name", "Active")))));
            var wires = new XElement(network + "Wires");
            if (language == "LAD")
            {
                parts.Add(new XElement(network + "Part", new XAttribute("Name", "Contact"), new XAttribute("UId", "23")));
                parts.Add(new XElement(network + "Part", new XAttribute("Name", "Coil"), new XAttribute("UId", "24")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "25"), new XElement(network + "Powerrail"), Pin(network, "23", "in")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "26"), new XElement(network + "IdentCon", new XAttribute("UId", "21")), Pin(network, "23", "operand")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "27"), Pin(network, "23", "out"), Pin(network, "24", "in")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "28"), Pin(network, "24", "operand"), new XElement(network + "IdentCon", new XAttribute("UId", "22"))));
            }
            else
            {
                parts.Add(new XElement(network + "Part", new XAttribute("Name", "Coil"), new XAttribute("UId", "23")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "24"), new XElement(network + "IdentCon", new XAttribute("UId", "21")), Pin(network, "23", "in")));
                wires.Add(new XElement(network + "Wire", new XAttribute("UId", "25"), Pin(network, "23", "operand"), new XElement(network + "IdentCon", new XAttribute("UId", "22"))));
            }
            var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")),
                new XElement("SW.Blocks.FC", new XAttribute("ID", "0"), new XElement("AttributeList",
                    new XElement("AutoNumber", "true"), new XElement("Interface", new XElement(sections + "Sections",
                        new XElement(sections + "Section", new XAttribute("Name", "Input"), Member(sections, "Enable", "Bool"), Member(sections, "EnableAlternate", "Bool")),
                        new XElement(sections + "Section", new XAttribute("Name", "Output"), Member(sections, "Active", "Bool")),
                        new XElement(sections + "Section", new XAttribute("Name", "InOut")), new XElement(sections + "Section", new XAttribute("Name", "Temp")),
                        new XElement(sections + "Section", new XAttribute("Name", "Constant")), new XElement(sections + "Section", new XAttribute("Name", "Return"), Member(sections, "Ret_Val", "Void")))),
                    new XElement("MemoryLayout", "Optimized"), new XElement("Name", name), new XElement("Namespace"), new XElement("ProgrammingLanguage", language)),
                    new XElement("ObjectList", new XElement("SW.Blocks.CompileUnit", new XAttribute("ID", "1"), new XAttribute("CompositionName", "CompileUnits"),
                        new XElement("AttributeList", new XElement("NetworkSource", new XElement(network + "FlgNet", parts, wires)), new XElement("ProgrammingLanguage", language)))))));
            return doc.ToString();
        }
        // Edits deliberately operate on a native XML tree, never regenerate a block.
        // Siemens import/compile is still required to validate language semantics.
        internal static void SetGraphicalOperand(XDocument document, string networkId, string accessUid, string[] components)
        {
            var network = GraphicalNetwork(CompileUnit(document, networkId));
            var access = ExactlyOne(network.Element(network.Name.Namespace + "Parts").Elements(network.Name.Namespace + "Access")
                .Where(e => e.Attribute("UId")?.Value == accessUid), "AccessNotFound", "accessUid must identify exactly one native Access.");
            SetSymbolComponents(access, components);
        }

        internal static void PatchGraphicalNetwork(XDocument document, string networkId, string[] upsertXml, string[] removeUids)
        {
            var original = GraphicalNetwork(CompileUnit(document, networkId));
            var candidate = new XElement(original);
            XNamespace ns = candidate.Name.Namespace;
            var parts = candidate.Element(ns + "Parts");
            var wires = candidate.Element(ns + "Wires");
            var updates = (upsertXml ?? new string[0]).Select(xml => ReadXml(xml).Root).ToArray();
            var removals = removeUids ?? new string[0];
            if (updates.Length + removals.Length == 0 || updates.Length + removals.Length > 100)
                throw new BridgeException("InvalidNetworkPatch", "Supply between 1 and 100 explicit native node updates/removals.");
            if (updates.Any(e => e == null || e.Name.Namespace != ns || !new[] { "Part", "Access", "Call", "Wire" }.Contains(e.Name.LocalName)
                || string.IsNullOrWhiteSpace(e.Attribute("UId")?.Value) || e.DescendantsAndSelf().Attributes("ID").Any() || HasHiddenEngineeringObjects(e)))
                throw new BridgeException("InvalidNetworkPatch", "Patch fragments must be native Part, Access, Call or Wire nodes with UId and no object ID definitions.");
            var requestedIds = updates.Select(e => e.Attribute("UId").Value).Concat(removals).ToArray();
            if (requestedIds.Any(string.IsNullOrWhiteSpace) || requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Length)
                throw new BridgeException("InvalidNetworkPatch", "Each node UId can occur only once in a patch.");
            foreach (var uid in removals)
            {
                var existing = ExactlyOne(parts.Elements().Concat(wires.Elements()).Where(e => e.Attribute("UId")?.Value == uid),
                    "NetworkNodeNotFound", "A removal must select one existing top-level native node.");
                if (existing.Name.Namespace != ns || !new[] { "Part", "Access", "Call", "Wire" }.Contains(existing.Name.LocalName))
                    throw new BridgeException("UnsupportedNetworkNode", "Unknown native elements cannot be removed by a structured patch.");
                existing.Remove();
            }
            foreach (var update in updates)
            {
                NormalizeGraphicalFragmentPrefixes(update, ns);
                var uid = update.Attribute("UId").Value;
                var existing = parts.Elements().Concat(wires.Elements()).Where(e => e.Attribute("UId")?.Value == uid).ToArray();
                if (existing.Length > 1) throw new BridgeException("InvalidNetworkPatch", "Ambiguous existing UId.");
                if (existing.Length == 1)
                {
                    if (existing[0].Name != update.Name) throw new BridgeException("InvalidNetworkPatch", "A replacement must retain its native node kind.");
                    existing[0].ReplaceWith(new XElement(update));
                }
                else (update.Name.LocalName == "Wire" ? wires : parts).Add(new XElement(update));
            }
            ValidateGraphicalReferences(candidate);
            original.ReplaceWith(candidate);
        }

        internal static string InsertGraphicalNetwork(XDocument document, string afterNetworkId, string compileUnitXml)
        {
            var anchor = CompileUnit(document, afterNetworkId);
            GraphicalNetwork(anchor);
            var incoming = ReadXml(compileUnitXml).Root;
            if (incoming == null || incoming.Name != "SW.Blocks.CompileUnit" || incoming.Attribute("CompositionName")?.Value != "CompileUnits"
                || string.IsNullOrWhiteSpace(incoming.Attribute("ID")?.Value))
                throw new BridgeException("InvalidNetworkPatch", "Insertion requires one native SW.Blocks.CompileUnit with explicit ID and CompileUnits composition.");
            if (HasHiddenEngineeringObjects(incoming))
                throw new BridgeException("XmlScopeMismatch", "A network fragment cannot contain software objects, a Document envelope or nested compile units.");
            if (incoming.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != anchor.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value)
                throw new BridgeException("LanguageConversionNotSupported", "Inserted network language must match the selected network.");
            var network = GraphicalNetwork(incoming);
            ValidateGraphicalReferences(network);
            NormalizeGraphicalFragmentPrefixes(incoming, network.Name.Namespace);
            var ids = incoming.DescendantsAndSelf().Attributes("ID").Select(a => a.Value).ToArray();
            var existingIds = new HashSet<string>(document.Descendants().Attributes("ID").Select(a => a.Value), StringComparer.Ordinal);
            if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(existingIds.Contains))
                throw new BridgeException("InvalidSimaticML", "Inserted network object IDs must be unique across the entire exported document; IDs are never guessed or rewritten.");
            anchor.AddAfterSelf(new XElement(incoming));
            return incoming.Attribute("ID").Value;
        }

        internal static void RequireNativePreservation(XDocument expected, XDocument actual)
        {
            if (expected.Root?.Name != "Document" || actual.Root?.Name != "Document")
                throw new BridgeException("InvalidSimaticML", "Native preservation verification requires Document envelopes.");
            if (!XNode.DeepEquals(NormalizeNativeDocument(expected), NormalizeNativeDocument(actual)))
                throw new BridgeException("NativePreservationMismatch", "The native readback differs from the prepared document beyond export provenance or formatting. Interfaces, comments, IDs, instruction payloads and unsupported elements must be preserved; inspect artifacts and recover the checkpoint.");
        }

        private static XDocument NormalizeNativeDocument(XDocument document)
        {
            var result = new XDocument();
            foreach (var node in document.Nodes())
            {
                if (node is XElement element)
                {
                    var normalized = NormalizeNativeElement(element, false);
                    normalized.Elements("DocumentInfo").Remove();
                    // SimaticML object IDs are serialization numbers that every export reassigns in document order.
                    int id = 0;
                    foreach (var attribute in normalized.DescendantsAndSelf().Attributes("ID")) attribute.Value = (id++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    result.Add(normalized);
                }
                else if (node is XComment comment) result.Add(new XComment(comment.Value));
                else if (node is XProcessingInstruction instruction) result.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
            }
            return result;
        }

        private static XElement NormalizeNativeElement(XElement source, bool inheritedSpacePreservation)
        {
            bool preserveSpace = source.Attribute(XNamespace.Xml + "space")?.Value == "preserve"
                || source.Attribute(XNamespace.Xml + "space") == null && inheritedSpacePreservation;
            var result = new XElement(source.Name, source.Attributes().Where(a => !a.IsNamespaceDeclaration)
                .OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a.Name, a.Value)));
            // Unknown native attributes/text can contain QName values (e.g. xsi:type).
            // Prefix spelling used only for element names is serialization detail, but
            // bindings referenced by payload values cannot be discarded safely.
            var valuePrefixes = source.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Value)
                .Concat(source.Nodes().OfType<XText>().Select(t => t.Value))
                .SelectMany(value => Regex.Matches(value, @"(?<![\p{L}\p{N}_])([\p{L}_][\p{L}\p{N}_.-]*):[\p{L}_][\p{L}\p{N}_.-]*")
                    .Cast<Match>().Select(match => match.Groups[1].Value))
                .Distinct(StringComparer.Ordinal).OrderBy(prefix => prefix, StringComparer.Ordinal);
            foreach (string prefix in valuePrefixes)
            {
                var binding = source.GetNamespaceOfPrefix(prefix);
                if (binding != null && prefix != "xml") result.Add(new XAttribute(XNamespace.Xmlns + prefix, binding.NamespaceName));
            }
            bool elementContent = source.Elements().Any();
            bool mixedContent = source.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
            foreach (var node in source.Nodes())
            {
                if (node is XElement element) result.Add(NormalizeNativeElement(element, preserveSpace));
                else if (node is XText text)
                {
                    if (elementContent && !mixedContent && !preserveSpace && !(node is XCData) && string.IsNullOrWhiteSpace(text.Value)) continue;
                    // CDATA and escaped text have the same native text content.
                    if (result.LastNode is XText previous) previous.Value += text.Value;
                    else result.Add(new XText(text.Value));
                }
                else if (node is XComment comment) result.Add(new XComment(comment.Value));
                else if (node is XProcessingInstruction instruction) result.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
            }
            return result;
        }

        internal static NativeCall[] ReadNativeCalls(XDocument document)
        {
            var result = new List<NativeCall>();
            foreach (var unit in document.Descendants("SW.Blocks.CompileUnit"))
            {
                var source = unit.Element("AttributeList")?.Element("NetworkSource");
                if (source == null) continue;
                foreach (var info in source.Descendants().Where(e => e.Name.LocalName == "CallInfo"))
                {
                    XNamespace ns = info.Name.Namespace;
                    bool graphical = info.Parent?.Name == ns + "Call";
                    var instance = info.Element(ns + "Instance");
                    var instanceComponents = instance?.Elements(ns + "Component").Select(e => e.Attribute("Name")?.Value).ToArray() ?? new string[0];
                    string type = info.Attribute("BlockType")?.Value;
                    string scope = instance?.Attribute("Scope")?.Value;
                    bool simpleInstanceSymbol = (scope == "LocalVariable" || scope == "GlobalVariable")
                        && instanceComponents.Length > 0 && instanceComponents.All(c => !string.IsNullOrWhiteSpace(c))
                        && instance.Elements().All(e => e.Name == ns + "Component" && !e.HasElements);
                    // V21 SCL FC exports place the called function symbol in Instance
                    // rather than CallInfo.Name. It is a target symbol, not an FB DB.
                    var targetComponents = type == "FC" && scope == "GlobalVariable" && simpleInstanceSymbol ? instanceComponents.ToArray() : new string[0];
                    string declaredName = info.Attribute("Name")?.Value;
                    var call = new NativeCall
                    {
                        NetworkId = unit.Attribute("ID")?.Value, CallUid = (graphical ? info.Parent : info).Attribute("UId")?.Value,
                        BlockType = type, Name = !string.IsNullOrWhiteSpace(declaredName) ? declaredName : targetComponents.Length == 1 ? targetComponents[0] : null,
                        CalledTargetComponents = targetComponents,
                        CalledTargetResolution = targetComponents.Length > 0 ? "symbolicOnly" : !string.IsNullOrWhiteSpace(declaredName) ? "declaredNameOnly" : "unresolved",
                        Language = unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value,
                        InstanceScope = scope, InstanceComponents = instanceComponents,
                        InstanceKind = type != "FB" ? "notApplicable" : scope == "LocalVariable" ? "multiInstance" : scope == "GlobalVariable" ? "instance" : "unresolved",
                        InstanceResolution = type != "FB" ? "notApplicable" : simpleInstanceSymbol ? "symbolicOnly" : "unresolved",
                        RawXml = (graphical ? info.Parent : info).ToString(SaveOptions.DisableFormatting)
                    };
                    call.Parameters = info.Elements(ns + "Parameter").Select(parameter => ReadCallParameter(unit, info, parameter, graphical)).ToArray();
                    result.Add(call);
                }
            }
            return result.ToArray();
        }

        internal static void SetCallParameterOperand(XDocument document, string networkId, string callUid, string parameterName, string[] components)
        {
            var unit = CompileUnit(document, networkId);
            var info = SelectCall(unit, callUid);
            var parameter = ExactlyOne(info.Elements(info.Name.Namespace + "Parameter").Where(e => e.Attribute("Name")?.Value == parameterName),
                "ParameterNotFound", "parameterName must identify exactly one declared call argument.");
            bool graphical = info.Parent.Name == info.Name.Namespace + "Call";
            XElement access;
            if (graphical)
            {
                var network = GraphicalNetwork(unit);
                var wire = IsolatedCallWire(network, callUid, parameterName);
                string uid = wire.Element(network.Name.Namespace + "IdentCon").Attribute("UId")?.Value;
                if (network.Element(network.Name.Namespace + "Wires").Descendants(network.Name.Namespace + "IdentCon").Count(e => e.Attribute("UId")?.Value == uid) != 1)
                    throw new BridgeException("SharedCallOperand", "The argument Access is shared. Rewire this isolated parameter to another existing Access instead.");
                access = ExactlyOne(network.Element(network.Name.Namespace + "Parts").Elements(network.Name.Namespace + "Access").Where(e => e.Attribute("UId")?.Value == uid),
                    "AccessNotFound", "Argument Access is unresolved.");
            }
            else
            {
                if (unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != "SCL"
                    || info.Name.NamespaceName != "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4")
                    throw new BridgeException("UnsupportedLanguage", "Native argument operand edits support standard SCL, LAD and FBD.");
                access = SimpleParameterAccess(parameter);
                if (access == null) throw new BridgeException("UnsupportedOperand", "The argument must be a single symbolic Access, without expression tokens or nested calls.");
            }
            SetSymbolComponents(access, components);
        }

        internal static void SetGraphicalCallConnection(XDocument document, string networkId, string callUid, string parameterName, string targetAccessUid)
        {
            var unit = CompileUnit(document, networkId);
            var network = GraphicalNetwork(unit);
            var info = SelectCall(unit, callUid);
            if (info.Parent.Name != info.Name.Namespace + "Call") throw new BridgeException("UnsupportedOperand", "Wire connections require a graphical native Call.");
            ExactlyOne(info.Elements(info.Name.Namespace + "Parameter").Where(e => e.Attribute("Name")?.Value == parameterName),
                "ParameterNotFound", "parameterName must identify one native call argument.");
            var target = ExactlyOne(network.Element(network.Name.Namespace + "Parts").Elements(network.Name.Namespace + "Access").Where(e => e.Attribute("UId")?.Value == targetAccessUid),
                "AccessNotFound", "targetAccessUid must identify one existing Access in this network.");
            SimpleSymbolComponents(target);
            var wire = IsolatedCallWire(network, callUid, parameterName);
            string previous = wire.Element(network.Name.Namespace + "IdentCon").Attribute("UId")?.Value;
            var wired = network.Element(network.Name.Namespace + "Wires").Descendants(network.Name.Namespace + "IdentCon").Select(e => e.Attribute("UId")?.Value).ToArray();
            // TIA V21 import refuses an Access without a wire and one on several wires, so the move must leave both on exactly one.
            if (wired.Count(uid => uid == previous) != 2 || wired.Contains(targetAccessUid))
                throw new BridgeException("AccessWiringUnsupported", "TIA Portal V21 imports a network only when every Access is wired exactly once; moving this wire would leave Access " + previous + " unwired or Access " + targetAccessUid + " on two wires.");
            wire.Element(network.Name.Namespace + "IdentCon").SetAttributeValue("UId", targetAccessUid);
        }

        private static XElement CompileUnit(XDocument document, string networkId)
        {
            var unit = ExactlyOne(document.Descendants("SW.Blocks.CompileUnit").Where(e => e.Attribute("ID")?.Value == networkId),
                "NetworkNotFound", "networkId must identify exactly one compile unit.");
            var language = unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value;
            if (language != "LAD" && language != "FBD" && language != "SCL")
                throw new BridgeException("UnsupportedLanguage", "Structured edits require a standard LAD, FBD or SCL compile unit.");
            // A standard-looking network inside an F-block is still a safety change.
            if (unit.Ancestors().Any(e => e.Name.NamespaceName.Length == 0 && e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)
                && (e.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value ?? "").StartsWith("F_", StringComparison.Ordinal)))
                throw new BridgeException("SafetyBoundary", "Safety blocks require their dedicated import workflow.");
            return unit;
        }

        private static XElement GraphicalNetwork(XElement unit)
        {
            string language = unit.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value;
            if (language != "LAD" && language != "FBD") throw new BridgeException("UnsupportedLanguage", "This edit requires a standard LAD or FBD network.");
            var source = unit.Element("AttributeList")?.Element("NetworkSource");
            var network = ExactlyOne(source == null ? Enumerable.Empty<XElement>() : source.Elements().Where(e => e.Name.LocalName == "FlgNet"),
                "UnsupportedNetwork", "The selected compile unit must contain one native FlgNet.");
            // Only the native format exercised by the existing V21 fixtures is editable.
            if (network.Name.NamespaceName != "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
                || network.Elements(network.Name.Namespace + "Parts").Count() != 1 || network.Elements(network.Name.Namespace + "Wires").Count() != 1)
                throw new BridgeException("UnsupportedNetwork", "Structured edits support native FlgNet/v5 with one Parts and one Wires collection.");
            return network;
        }

        private static void ValidateGraphicalReferences(XElement network)
        {
            XNamespace ns = network.Name.Namespace;
            var parts = network.Element(ns + "Parts");
            var wires = network.Element(ns + "Wires");
            var definitions = network.Descendants().Where(e => e.Name != ns + "NameCon" && e.Name != ns + "IdentCon" && e.Name != ns + "OpenCon")
                .Attributes("UId").Select(a => a.Value).Concat(wires.Descendants(ns + "OpenCon").Attributes("UId").Select(a => a.Value)).ToArray();
            if (definitions.Any(id => !Regex.IsMatch(id, @"^\d+$", RegexOptions.CultureInvariant)) || definitions.Distinct(StringComparer.Ordinal).Count() != definitions.Length)
                throw new BridgeException("InvalidNetworkPatch", "Every native UId definition must be a unique nonnegative integer within its network.");
            foreach (var endpoint in wires.Descendants().Where(e => e.Name == ns + "IdentCon" || e.Name == ns + "NameCon"))
            {
                var targets = parts.Elements().Where(e => e.Attribute("UId")?.Value == endpoint.Attribute("UId")?.Value).ToArray();
                if (targets.Length != 1 || (endpoint.Name == ns + "IdentCon" && targets[0].Name != ns + "Access")
                    || (endpoint.Name == ns + "NameCon" && targets[0].Name != ns + "Part" && targets[0].Name != ns + "Call"))
                    throw new BridgeException("UnresolvedNetworkReference", "A native connector must reference an existing top-level Access, Part or Call of the matching kind.");
                if (endpoint.Name == ns + "NameCon" && string.IsNullOrWhiteSpace(endpoint.Attribute("Name")?.Value))
                    throw new BridgeException("InvalidNetworkPatch", "NameCon requires an explicit pin name.");
            }
        }

        private static XElement SelectCall(XElement unit, string callUid)
        {
            return ExactlyOne(unit.Descendants().Where(e => e.Name.LocalName == "CallInfo" &&
                (e.Parent.Name == e.Name.Namespace + "Call" ? e.Parent.Attribute("UId")?.Value : e.Attribute("UId")?.Value) == callUid),
                "CallNotFound", "callUid must select exactly one native call.");
        }

        private static XElement IsolatedCallWire(XElement network, string callUid, string parameterName)
        {
            XNamespace ns = network.Name.Namespace;
            var wire = ExactlyOne(network.Element(ns + "Wires").Elements(ns + "Wire")
                .Where(e => e.Elements(ns + "NameCon").Any(p => p.Attribute("UId")?.Value == callUid && p.Attribute("Name")?.Value == parameterName)),
                "UnresolvedCallConnection", "The call argument must have one explicit wire connection.");
            if (wire.Elements().Count() != 2 || wire.Elements(ns + "IdentCon").Count() != 1 || wire.Elements(ns + "NameCon").Count() != 1)
                throw new BridgeException("SharedCallConnection", "Only an isolated parameter-to-Access wire can be edited; branched/native expressions remain explicit and unchanged.");
            return wire;
        }

        private static XElement SimpleParameterAccess(XElement parameter)
        {
            XNamespace ns = parameter.Name.Namespace;
            var accesses = parameter.Elements(ns + "Access").ToArray();
            if (accesses.Length != 1 || parameter.Elements(ns + "Token").Count() != 1
                || parameter.Elements().Any(e => e.Name != ns + "Access" && e.Name != ns + "Blank" && e.Name != ns + "NewLine"
                && !(e.Name == ns + "Token" && new[] { ":=", "=>", "<=>" }.Contains(e.Attribute("Text")?.Value)))) return null;
            return accesses[0];
        }

        private static bool HasHiddenEngineeringObjects(XElement fragment) => fragment.Descendants().Any(e =>
            e.Name.NamespaceName.Length == 0 && (e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal) || e.Name.LocalName == "Document"));

        private static void NormalizeGraphicalFragmentPrefixes(XElement fragment, XNamespace networkNamespace)
        {
            // Siemens' FlgNet importer rejects otherwise equivalent ns0:Access
            // fragments emitted by common XML serializers. Emit native nodes under
            // the surrounding/default FlgNet namespace, without touching extensions.
            var aliases = fragment.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration
                && a.Name.Namespace == XNamespace.Xmlns && a.Value == networkNamespace.NamespaceName).ToArray();
            if (aliases.Length == 0) return;
            var prefixes = new HashSet<string>(aliases.Select(a => a.Name.LocalName), StringComparer.Ordinal);
            foreach (var element in fragment.DescendantsAndSelf())
            foreach (string value in element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Value).Concat(element.Nodes().OfType<XText>().Select(t => t.Value)))
            foreach (Match match in Regex.Matches(value, @"(?<![\p{L}\p{N}_])([\p{L}_][\p{L}\p{N}_.-]*):[\p{L}_][\p{L}\p{N}_.-]*"))
            {
                string prefix = match.Groups[1].Value;
                if (prefixes.Contains(prefix) && element.GetNamespaceOfPrefix(prefix) == networkNamespace)
                    throw new BridgeException("UnsupportedNetworkPrefix", "A native namespace alias is referenced by a QName payload value; automatic prefix normalization would change its meaning. Supply native default-namespace XML without ambiguous value bindings.");
            }
            foreach (var alias in aliases) alias.Remove();
        }

        private static XElement[] SimpleSymbolComponents(XElement access)
        {
            XNamespace ns = access.Name.Namespace;
            string scope = access.Attribute("Scope")?.Value;
            var symbols = access.Elements(ns + "Symbol").ToArray();
            if ((scope != "LocalVariable" && scope != "GlobalVariable") || symbols.Length != 1 || access.Elements().Any(e => e.Name != ns + "Symbol"))
                throw new BridgeException("UnsupportedOperand", "Only existing simple local/global symbolic Access nodes can be edited.");
            var components = symbols[0].Elements(ns + "Component").ToArray();
            if (components.Length == 0 || symbols[0].Elements().Any(e => e.Name != ns + "Component") || components.Any(e => e.HasElements || string.IsNullOrWhiteSpace(e.Attribute("Name")?.Value)))
                throw new BridgeException("UnsupportedOperand", "Array indexes, indirect operands and unknown symbol children require explicit native XML editing.");
            return components;
        }

        private static void SetSymbolComponents(XElement access, string[] replacements)
        {
            var components = SimpleSymbolComponents(access);
            if (replacements == null || components.Length != replacements.Length || replacements.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
                throw new BridgeException("UnsupportedOperand", "Supply one nonempty symbolic name per existing component; component count is preserved.");
            for (int i = 0; i < components.Length; i++) components[i].SetAttributeValue("Name", replacements[i]);
        }

        private static NativeParameter ReadCallParameter(XElement unit, XElement info, XElement parameter, bool graphical)
        {
            XNamespace ns = info.Name.Namespace;
            string name = parameter.Attribute("Name")?.Value;
            var result = new NativeParameter { Name = name, Section = parameter.Attribute("Section")?.Value,
                Type = parameter.Attribute("Type")?.Value, Resolution = "unresolved", Components = new string[0], RawXml = parameter.ToString(SaveOptions.DisableFormatting) };
            try
            {
                XElement access;
                if (graphical)
                {
                    var network = GraphicalNetwork(unit);
                    var nativeWires = network.Element(ns + "Wires").Elements(ns + "Wire").Where(e => e.Elements(ns + "NameCon")
                        .Any(pin => pin.Attribute("UId")?.Value == info.Parent.Attribute("UId")?.Value && pin.Attribute("Name")?.Value == name));
                    result.ConnectionXml = new XElement("Connections", nativeWires.Select(e => new XElement(e))).ToString(SaveOptions.DisableFormatting);
                    var wire = IsolatedCallWire(network, info.Parent.Attribute("UId")?.Value, name);
                    result.WireUid = wire.Attribute("UId")?.Value;
                    string uid = wire.Element(ns + "IdentCon").Attribute("UId")?.Value;
                    access = ExactlyOne(network.Element(ns + "Parts").Elements(ns + "Access").Where(e => e.Attribute("UId")?.Value == uid), "AccessNotFound", "Connection Access is unresolved.");
                }
                else access = SimpleParameterAccess(parameter) ?? throw new BridgeException("UnsupportedOperand", "Argument is a native expression or unresolved connection.");
                result.AccessUid = access.Attribute("UId")?.Value;
                result.Scope = access.Attribute("Scope")?.Value;
                result.Components = SimpleSymbolComponents(access).Select(e => e.Attribute("Name").Value).ToArray();
                result.Resolution = "symbolic";
            }
            catch (BridgeException exception) { result.Reason = exception.Code; }
            if (string.IsNullOrEmpty(result.Section))
            {
                string assignment = parameter.Elements(ns + "Token").Select(e => e.Attribute("Text")?.Value).FirstOrDefault(t => t == ":=" || t == "=>" || t == "<=>");
                result.Direction = assignment == "=>" ? "write" : assignment == ":=" ? "readOrInOut" : assignment == "<=>" ? "readWrite" : "unresolved";
            }
            else result.Direction = result.Section == "Output" ? "write" : result.Section == "InOut" ? "readWrite" : result.Section == "Input" ? "read" : "unresolved";
            return result;
        }

        private static XElement ExactlyOne(IEnumerable<XElement> nodes, string code, string message)
        {
            var matches = nodes.Take(2).ToArray();
            if (matches.Length != 1) throw new BridgeException(code, message);
            return matches[0];
        }

        internal sealed class NativeCall
        {
            public string NetworkId { get; set; }
            public string CallUid { get; set; }
            public string BlockType { get; set; }
            public string Name { get; set; }
            public string[] CalledTargetComponents { get; set; }
            public string CalledTargetResolution { get; set; }
            public string Language { get; set; }
            public string InstanceScope { get; set; }
            public string[] InstanceComponents { get; set; }
            public string InstanceKind { get; set; }
            public string InstanceResolution { get; set; }
            public NativeParameter[] Parameters { get; set; }
            public string RawXml { get; set; }
        }

        internal sealed class NativeParameter
        {
            public string Name { get; set; }
            public string Section { get; set; }
            public string Type { get; set; }
            public string Direction { get; set; }
            public string Resolution { get; set; }
            public string Reason { get; set; }
            public string AccessUid { get; set; }
            public string WireUid { get; set; }
            public string Scope { get; set; }
            public string[] Components { get; set; }
            public string RawXml { get; set; }
            public string ConnectionXml { get; set; }
        }

        private static XElement Pin(XNamespace ns, string id, string name) => new XElement(ns + "NameCon", new XAttribute("UId", id), new XAttribute("Name", name));
        private static XElement Member(XNamespace ns, string name, string type) => new XElement(ns + "Member", new XAttribute("Name", name), new XAttribute("Datatype", type));
        internal sealed class BlockDeclaration { internal string Name, Language; internal bool IsSafety; }
    }
}
