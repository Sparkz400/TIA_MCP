#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Core.WatchTables
{
    // Shared with the net48 native adapter. This is an observed V21 profile, not a vendor XSD.
    public sealed class WatchTableDocument
    {
        public const string Profile = "v21-watch-withdefaults-preserving-1";
        private const string Prefix = "SW.WatchAndForceTables.";
        private readonly byte[] original;
        private readonly XDocument document;
        private readonly XElement table;
        private bool edited;
        public string Name { get { return table.Element("AttributeList").Element("Name").Value; } }
        public bool IsForce { get { return table.Name.LocalName == Prefix + "PlcForceTable"; } }
        public int Count { get { return Rows().Length; } }
        public string Sha256 { get { return Hash(ToBytes()); } }
        public static byte[] AbsentBytes(string group, string name)
        { return Encoding.UTF8.GetBytes("watch-table-absent.v1\n" + group + "\n" + name); }

        public WatchTableDocument(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Watch document exceeds 16 MiB or is empty.");
            original = (byte[])bytes.Clone();
            // Reject deep/node-heavy XML before allocating its tree, including hostile opaque payloads.
            using (var stream = new MemoryStream(original))
            using (var reader = Reader(stream))
            { int elements = 0; while (reader.Read()) if (reader.Depth > 128 || (reader.NodeType == XmlNodeType.Element && ++elements > 500000)) throw new InvalidDataException("Watch XML depth/node budget exceeded."); }
            using (var stream = new MemoryStream(original))
            using (var reader = Reader(stream)) document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root == null || document.Root.Name != "Document" || document.Root.Elements("Engineering").Count() != 1 ||
                (string)document.Root.Element("Engineering").Attribute("version") != "V21" || document.Root.Elements("DocumentInfo").Count() != 1)
                throw new InvalidDataException("Expected bounded native V21 Document.");
            var info = document.Root.Element("DocumentInfo");
            if (info.Elements("ExportSetting").Count() != 1 || !Plain(info.Element("ExportSetting")) || info.Element("ExportSetting").Value != "WithDefaults" ||
                info.Elements("Created").Count() > 1 || (info.Element("Created") != null && !Plain(info.Element("Created"))))
                throw new InvalidDataException("Expected unambiguous observed WithDefaults export metadata.");
            var tables = document.Root.Elements().Where(e => e.Name.LocalName.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            if (tables.Length != 1 || (tables[0].Name != Prefix + "PlcWatchTable" && tables[0].Name != Prefix + "PlcForceTable"))
                throw new InvalidDataException("Exactly one watch or force document is required.");
            table = tables[0];
            if (document.Root.Elements().Any(e => e != table && e.Name != "Engineering" && e.Name != "DocumentInfo") ||
                table.Elements("AttributeList").Count() != 1 || table.Element("AttributeList").Elements("Name").Count() != 1 ||
                !Plain(table.Element("AttributeList").Element("Name")) || table.Elements("ObjectList").Count() > 1)
                throw new InvalidDataException("Ambiguous native table attributes.");
            var ids = document.Descendants().Attributes("ID").Select(x => x.Value).ToArray();
            var numericIds = new HashSet<uint>();
            foreach (string id in ids)
            { uint number; if (id.Length == 0 || id.Length > 8 || id.Any(c => !Uri.IsHexDigit(c)) || !uint.TryParse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out number) || !numericIds.Add(number)) throw new InvalidDataException("Invalid or duplicate native hexadecimal IDs."); }
            if (document.Descendants().Any(e => (e == table || e.Name.LocalName.StartsWith(Prefix, StringComparison.Ordinal) || e.Name == "MultilingualText" || e.Name == "MultilingualTextItem") && e.Attribute("ID") == null))
                throw new InvalidDataException("Observed native objects require exact IDs.");
            foreach (var element in document.Descendants())
            {
                if (element.Elements("AttributeList").Count() > 1 || element.Elements("ObjectList").Count() > 1 ||
                    (element.Name == "AttributeList" && element.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1)))
                    throw new InvalidDataException("Ambiguous object containers or repeated attributes.");
            }
            if (Count > 10000) throw new InvalidDataException("Watch row budget exceeded.");
            foreach (var row in Rows())
                if ((row.Name != (IsForce ? Prefix + "PlcForceTableEntry" : Prefix + "PlcWatchTableEntry") && row.Name != Prefix + "PlcTableCommentEntry") ||
                    (string)row.Attribute("CompositionName") != "Entries")
                    throw new InvalidDataException("Unknown row profile; retain original bytes without editing.");
        }

        public string Field(int ordinal, string field)
        {
            var attrs = Row(ordinal).Element("AttributeList");
            var value = attrs == null ? null : attrs.Element(field);
            if (value != null && !Plain(value)) throw new InvalidDataException("Captured field must be plain text; opaque markup cannot be flattened.");
            return (string)value;
        }
        public string RowKind(int ordinal) { return Row(ordinal).Name.LocalName.Substring(Prefix.Length); }
        public void Rename(string name)
        {
            Writable(); if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid table name.");
            VerifyText(name); if (Name == name) return; table.Element("AttributeList").Element("Name").Value = name; edited = true;
        }

        public void SetField(int ordinal, string field, string value)
        {
            Writable();
            if (!new[] { "Name", "Address", "DisplayFormat", "ModifyValue" }.Contains(field) || value == null || value.Length > 8192)
                throw new InvalidDataException("Only bounded offline selectors, display format and literal values may be edited.");
            var row = Row(ordinal);
            if (row.Name != Prefix + "PlcWatchTableEntry") throw new InvalidDataException("A watch entry is required.");
            var attrs = row.Element("AttributeList");
            var fields = attrs == null ? new XElement[0] : attrs.Elements(field).ToArray();
            if (fields.Length != 1 || !Plain(fields[0])) throw new InvalidDataException("Field must already exist as plain text in this captured native profile.");
            VerifyText(value);
            if (fields[0].Value == value) return;
            // Genuine V21 exports a cleared scalar as an empty element. Make
            // that explicit in the candidate; do not relax readback hashing.
            if (value.Length == 0) fields[0].RemoveNodes(); else fields[0].Value = value;
            edited = true;
        }

        public void SetComment(int ordinal, string culture, string value)
        {
            Writable();
            if (value == null || value.Length > 65536) throw new InvalidDataException("Comment exceeds limit.");
            var items = Row(ordinal).Elements("ObjectList").Elements("MultilingualText")
                .Where(e => (string)e.Attribute("CompositionName") == "Comment").Elements("ObjectList").Elements("MultilingualTextItem")
                .Elements("AttributeList").ToArray();
            if (items.Any(e => !Plain(e.Element("Culture")))) throw new InvalidDataException("Captured comment cultures must be plain text.");
            var matches = items.Where(e => (string)e.Element("Culture") == culture).Select(e => e.Element("Text")).ToArray();
            if (matches.Length != 1 || matches[0] == null || !Plain(matches[0])) throw new InvalidDataException("Exact existing plain-text language item required.");
            VerifyText(value);
            if (matches[0].Value == value) return;
            matches[0].Value = value; edited = true;
        }

        public Dictionary<string, string> Comments(int ordinal)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in Row(ordinal).Elements("ObjectList").Elements("MultilingualText")
                .Where(e => (string)e.Attribute("CompositionName") == "Comment").Elements("ObjectList").Elements("MultilingualTextItem"))
            {
                var values = item.Element("AttributeList");
                string culture = (string)values?.Element("Culture");
                if (string.IsNullOrEmpty(culture) || !Plain(values.Element("Culture")) || !Plain(values.Element("Text")) || result.ContainsKey(culture))
                    throw new InvalidDataException("Unambiguous captured comment cultures required.");
                result.Add(culture, values.Element("Text").Value);
            }
            return result;
        }

        // A new language reuses genuine item grammar. Project language admission is checked by the dispatcher.
        public void UpsertComment(int ordinal, string culture, string value)
        {
            Writable();
            if (string.IsNullOrEmpty(culture) || culture.Length > 32 || CultureInfo.GetCultureInfo(culture).Name != culture ||
                value == null || value.Length > 65536) throw new InvalidDataException("Bounded exact culture and comment required.");
            VerifyText(value);
            var comments = Comments(ordinal);
            if (comments.ContainsKey(culture)) { SetComment(ordinal, culture, value); return; }
            var containers = Row(ordinal).Elements("ObjectList").Elements("MultilingualText")
                .Where(e => (string)e.Attribute("CompositionName") == "Comment").Elements("ObjectList").ToArray();
            if (containers.Length != 1 || comments.Count == 0 || comments.Count >= 32) throw new InvalidDataException("Captured multilingual item template required.");
            var item = new XElement(containers[0].Elements("MultilingualTextItem").First());
            RemapCloneIds(item);
            item.Element("AttributeList").Element("Culture").Value = culture;
            item.Element("AttributeList").Element("Text").Value = value;
            containers[0].Add(item); edited = true;
        }

        public void InsertCapturedRow(WatchTableDocument source, int ordinal, int insertAt)
        {
            Writable(); source.Writable();
            if (Count >= 10000 || insertAt < 0 || insertAt > Count) throw new InvalidDataException("Invalid insertion bound.");
            var clone = new XElement(source.Row(ordinal)); RemapCloneIds(clone);
            var rows = Rows(); var objects = table.Element("ObjectList");
            if (objects == null)
            {
                var captured = source.table.Element("ObjectList");
                if (captured == null) throw new InvalidDataException("Captured row container required.");
                objects = new XElement(captured); objects.RemoveNodes(); table.Add(objects);
            }
            if (insertAt == rows.Length) objects.Add(clone); else rows[insertAt].AddBeforeSelf(clone);
            edited = true;
        }

        // Used on newly cloned rows only; existing rows retain every unselected language.
        public void RetainCommentCultures(int ordinal, ICollection<string> cultures)
        {
            Writable(); _ = Comments(ordinal);
            foreach (var item in Row(ordinal).Elements("ObjectList").Elements("MultilingualText")
                .Where(e => (string)e.Attribute("CompositionName") == "Comment").Elements("ObjectList").Elements("MultilingualTextItem").ToArray())
                if (!cultures.Contains((string)item.Element("AttributeList").Element("Culture"))) { item.Remove(); edited = true; }
        }

        private void RemapCloneIds(XElement clone)
        {
            if (clone.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (a.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && a.Name != "ID")) ||
                clone.DescendantsAndSelf().Any(e => e.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Referenced native payload needs a separate clone profile.");
            var used = new HashSet<uint>(document.Descendants().Attributes("ID").Select(a => uint.Parse(a.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
            uint next = 0;
            foreach (var id in clone.DescendantsAndSelf().Attributes("ID"))
            { while (!used.Add(next)) next++; id.Value = next.ToString("X", CultureInfo.InvariantCulture); }
        }

        public void Remove(int ordinal) { Writable(); Row(ordinal).Remove(); edited = true; }
        public void Reorder(int[] ordinals)
        {
            Writable(); var rows = Rows();
            if (ordinals == null || ordinals.Length != rows.Length || ordinals.Distinct().Count() != rows.Length || ordinals.Any(i => i < 0 || i >= rows.Length))
                throw new InvalidDataException("Reorder must be an exact row permutation.");
            if (ordinals.SequenceEqual(Enumerable.Range(0, rows.Length))) return;
            // Keep every opaque inter-row comment/PI/text node in its original slot.
            var replacements = ordinals.Select(index => new XElement(rows[index])).ToArray();
            for (int i = 0; i < rows.Length; i++) rows[i].ReplaceWith(replacements[i]);
            edited = true;
        }
        // Explicit candidate transformation for the observed scalar/comment-only
        // native export. IDs stay significant in every readback comparison.
        // Unknown grammar is refused before changing anything, never normalized.
        public Dictionary<string, string> ReindexReferenceFreeDocumentIds(bool authoring = false)
        {
            Writable();
            Action<XElement, string[]> attributes = (element, allowed) =>
            {
                if (element.Attributes().Any(a => a.Name.NamespaceName.Length != 0 || !allowed.Contains(a.Name.LocalName)))
                    throw new InvalidDataException("Document ID reindex requires exact reference-free native attributes.");
            };
            Action<XElement, string[]> children = (element, allowed) =>
            {
                if (element == null || element.Elements().Any(e => e.Name.NamespaceName.Length != 0 || !allowed.Contains(e.Name.LocalName)))
                    throw new InvalidDataException("Document ID reindex refuses unobserved native payload.");
            };
            Action<XElement, string[]> scalarAttributes = (element, allowed) =>
            {
                attributes(element, new string[0]); children(element, allowed);
                if (element.Elements().Any(e => e.HasAttributes || !Plain(e)))
                    throw new InvalidDataException("Document ID reindex requires plain captured scalar fields.");
            };
            if ((string)table.Attribute("ID") != "0" || document.Descendants().Attributes("ID").Any(a => !a.Parent.AncestorsAndSelf().Contains(table)))
                throw new InvalidDataException("Document ID reindex requires the captured table root ID and no external IDs.");
            if (document.Descendants().Attributes().Any(a => a.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (a.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && a.Name != "ID")) ||
                document.Descendants().Any(e => e.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Referenced payload requires a separate explicit reference-remapping profile.");
            attributes(table, new[] { "ID" }); children(table, new[] { "AttributeList", "ObjectList" });
            scalarAttributes(table.Element("AttributeList"), new[] { "Name" });
            if (table.Element("ObjectList") == null) throw new InvalidDataException("Captured row container is required.");
            attributes(table.Element("ObjectList"), new string[0]);
            foreach (var row in Rows())
            {
                bool commentRow = authoring && row.Name == Prefix + "PlcTableCommentEntry";
                if (!commentRow && row.Name != Prefix + "PlcWatchTableEntry") throw new InvalidDataException("Only captured watch signal/comment rows support this ID profile.");
                attributes(row, new[] { "ID", "CompositionName" }); children(row, new[] { "AttributeList", "ObjectList" });
                var fields = row.Element("AttributeList");
                if (commentRow) { if (fields != null) scalarAttributes(fields, new string[0]); }
                else
                {
                if (fields == null) throw new InvalidDataException("Captured row attributes required.");
                scalarAttributes(fields, new[] { "Address", "DisplayFormat", "ModifyTrigger", "ModifyValue", "MonitorTrigger", "Name" });
                if ((!authoring && ((string)fields.Element("DisplayFormat") != "Bool" || !new[] { "FALSE", "TRUE" }.Contains((string)fields.Element("ModifyValue")))) ||
                    (authoring && (fields.Element("DisplayFormat") == null || fields.Element("ModifyValue") == null)) ||
                    (string)fields.Element("ModifyTrigger") != "Permanent" || (string)fields.Element("MonitorTrigger") != "Permanent" || fields.Element("Name") == null)
                    throw new InvalidDataException("Document ID reindex requires the observed offline Bool row shape.");
                }
                var objects = row.Element("ObjectList");
                children(objects, new[] { "MultilingualText" }); attributes(objects, new string[0]);
                var texts = objects.Elements().ToArray();
                if (texts.Length != 1 || (string)texts[0].Attribute("CompositionName") != "Comment") throw new InvalidDataException("Exactly one captured multilingual comment required.");
                var text = texts[0]; attributes(text, new[] { "ID", "CompositionName" }); children(text, new[] { "ObjectList" });
                var items = text.Element("ObjectList"); children(items, new[] { "MultilingualTextItem" }); attributes(items, new string[0]);
                var cultures = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items.Elements())
                {
                    attributes(item, new[] { "ID", "CompositionName" }); children(item, new[] { "AttributeList" });
                    var values = item.Element("AttributeList");
                    if (values == null || (string)item.Attribute("CompositionName") != "Items") throw new InvalidDataException("Captured language item required.");
                    scalarAttributes(values, new[] { "Culture", "Text" });
                    if (values.Element("Text") == null || string.IsNullOrWhiteSpace((string)values.Element("Culture")) || !cultures.Add((string)values.Element("Culture")))
                        throw new InvalidDataException("Distinct captured cultures and exact text fields required.");
                }
                if (cultures.Count == 0) throw new InvalidDataException("At least one captured comment culture required.");
            }
            var ids = table.DescendantsAndSelf().Attributes("ID").ToArray();
            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++) mapping.Add(ids[i].Value, i.ToString("X", CultureInfo.InvariantCulture));
            foreach (var id in ids) { string value = mapping[id.Value]; if (id.Value != value) { id.Value = value; edited = true; } }
            return mapping;
        }
        // Add an opaque clone of an existing genuine row; never synthesize unobserved native grammar.
        public void Duplicate(int ordinal, int insertAt)
        {
            Writable(); var rows = Rows();
            if (Count >= 10000 || insertAt < 0 || insertAt > Count) throw new InvalidDataException("Invalid insertion bound.");
            var clone = new XElement(Row(ordinal));
            var used = new HashSet<uint>(document.Descendants().Attributes("ID").Select(a => uint.Parse(a.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)));
            int next = 0;
            foreach (var id in clone.DescendantsAndSelf().Attributes("ID"))
            { uint replacement; do { replacement = (uint)next++; } while (!used.Add(replacement)); id.Value = replacement.ToString("X", CultureInfo.InvariantCulture); }
            // Unknown ID references cannot safely be rewritten.
            if (clone.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 || (a.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && a.Name != "ID")) ||
                clone.DescendantsAndSelf().Any(e => e.Name.LocalName.IndexOf("Ref", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName.EndsWith("Id", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Referenced native row payload needs a separate clone profile.");
            if (insertAt == rows.Length) table.Element("ObjectList").Add(clone); else rows[insertAt].AddBeforeSelf(clone);
            edited = true;
        }
        public byte[] ToBytes()
        {
            if (!edited) return (byte[])original.Clone();
            using (var stream = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.Entitize })) document.Save(writer);
                return stream.ToArray();
            }
        }
        // Only indentation in known native containers and the observed export time are volatile.
        public static string SemanticHash(byte[] bytes)
        {
            var parsed = new WatchTableDocument(bytes);
            var clone = new XDocument(parsed.document);
            var created = clone.Root.Element("DocumentInfo") == null ? null : clone.Root.Element("DocumentInfo").Element("Created");
            if (created != null) created.Value = "";
            var containers = new HashSet<string>(new[] { "Document", "DocumentInfo", "InstalledProducts", "Product", "OptionPackage", "AttributeList", "ObjectList", "MultilingualText", "MultilingualTextItem",
                Prefix + "PlcWatchTable", Prefix + "PlcForceTable", Prefix + "PlcWatchTableEntry", Prefix + "PlcForceTableEntry", Prefix + "PlcTableCommentEntry" }, StringComparer.Ordinal);
            foreach (var element in clone.Descendants().Where(e => e.Name.NamespaceName.Length == 0 && containers.Contains(e.Name.LocalName) && NativeContainer(e)))
            {
                if (element.AncestorsAndSelf().Select(e => (string)e.Attribute(XNamespace.Xml + "space")).FirstOrDefault(v => v != null) == "preserve" ||
                    element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) continue;
                foreach (var text in element.Nodes().OfType<XText>().Where(t => !(t is XCData) && string.IsNullOrWhiteSpace(t.Value)).ToArray()) text.Remove();
            }
            foreach (var text in clone.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToArray()) text.Remove();
            return Hash(Encoding.UTF8.GetBytes(clone.ToString(SaveOptions.DisableFormatting)));
        }
        // A familiar element name inside vendor/opaque payload is not native indentation.
        private static bool NativeContainer(XElement element)
        {
            if (element.Name.NamespaceName.Length != 0) return false;
            string name = element.Name.LocalName;
            if (element.Parent == null) return name == "Document";
            if (!NativeContainer(element.Parent)) return false;
            string parent = element.Parent.Name.LocalName;
            if (name == "DocumentInfo") return parent == "Document";
            if (name == "InstalledProducts") return parent == "DocumentInfo";
            if (name == "Product" || name == "OptionPackage") return parent == "InstalledProducts";
            if (name == Prefix + "PlcWatchTable" || name == Prefix + "PlcForceTable") return parent == "Document";
            if (name == "AttributeList" || name == "ObjectList")
                return parent == Prefix + "PlcWatchTable" || parent == Prefix + "PlcForceTable" ||
                    parent == Prefix + "PlcWatchTableEntry" || parent == Prefix + "PlcForceTableEntry" || parent == Prefix + "PlcTableCommentEntry" ||
                    parent == "MultilingualText" || parent == "MultilingualTextItem";
            if (parent != "ObjectList") return false;
            string owner = element.Parent.Parent.Name.LocalName;
            if (name == "MultilingualTextItem") return owner == "MultilingualText";
            if (name == "MultilingualText") return owner == Prefix + "PlcWatchTableEntry" || owner == Prefix + "PlcForceTableEntry" || owner == Prefix + "PlcTableCommentEntry";
            return (owner == Prefix + "PlcWatchTable" || owner == Prefix + "PlcForceTable") &&
                (name == Prefix + "PlcWatchTableEntry" || name == Prefix + "PlcForceTableEntry" || name == Prefix + "PlcTableCommentEntry");
        }
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private XElement[] Rows() { return table.Elements("ObjectList").Elements().ToArray(); }
        private XElement Row(int ordinal) { var rows = Rows(); if (ordinal < 0 || ordinal >= rows.Length) throw new InvalidDataException("Row ordinal out of bounds."); return rows[ordinal]; }
        private void Writable() { if (IsForce) throw new InvalidDataException("Force documents are capture-only in this profile."); }
        private static XmlReader Reader(Stream stream) { return XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }); }
        private static bool Plain(XElement element) { return element != null && element.Nodes().All(n => n is XText); }
        private static void VerifyText(string value) { try { XmlConvert.VerifyXmlChars(value); } catch (XmlException error) { throw new InvalidDataException("Invalid XML text.", error); } }
    }
}
