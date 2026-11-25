#nullable disable // the worker is compiled without nullable annotations; the offline tests link this file into a nullable project
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// Reads TIA's own export of one block, without the 4 MiB bound that guards supplied import documents. Nesting is bounded:
    /// XDocument.Load slows superlinearly with depth (about 2 s at 40 000 levels, no return at 200 000), and native SimaticML
    /// block exports nest about 20 levels, so a document deeper than MaxDepth is refused while it streams, before a tree exists.
    /// </summary>
    internal static class ExportedXml
    {
        internal const int MaxDepth = 512;

        internal static XDocument Load(string path)
        {
            using (var stream = File.OpenRead(path)) return Load(stream);
        }

        internal static XDocument Load(Stream stream)
        {
            using (var inner = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256L * 1024 * 1024 }))
            using (var reader = new DepthBoundReader(inner))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        /// <summary>Forwards every member to the underlying reader and refuses a node deeper than MaxDepth.</summary>
        private sealed class DepthBoundReader : XmlReader
        {
            private readonly XmlReader inner;
            internal DepthBoundReader(XmlReader inner) { this.inner = inner; }

            public override bool Read()
            {
                if (!inner.Read()) return false;
                if (inner.Depth > MaxDepth)
                    throw new BridgeException("ExportedXmlTooDeep", "The exported block XML nests more than " + MaxDepth + " levels; native block exports nest far less. artifact.path holds the unchanged export.");
                return true;
            }

            public override XmlReaderSettings Settings => inner.Settings;
            public override XmlNodeType NodeType => inner.NodeType;
            public override string LocalName => inner.LocalName;
            public override string Name => inner.Name;
            public override string NamespaceURI => inner.NamespaceURI;
            public override string Prefix => inner.Prefix;
            public override bool HasValue => inner.HasValue;
            public override string Value => inner.Value;
            public override int Depth => inner.Depth;
            public override string BaseURI => inner.BaseURI;
            public override bool IsEmptyElement => inner.IsEmptyElement;
            public override bool IsDefault => inner.IsDefault;
            public override char QuoteChar => inner.QuoteChar;
            public override XmlSpace XmlSpace => inner.XmlSpace;
            public override string XmlLang => inner.XmlLang;
            public override int AttributeCount => inner.AttributeCount;
            public override bool EOF => inner.EOF;
            public override ReadState ReadState => inner.ReadState;
            public override XmlNameTable NameTable => inner.NameTable;
            public override bool CanResolveEntity => false;
            public override string GetAttribute(string name) => inner.GetAttribute(name);
            public override string GetAttribute(string name, string namespaceURI) => inner.GetAttribute(name, namespaceURI);
            public override string GetAttribute(int i) => inner.GetAttribute(i);
            public override bool MoveToAttribute(string name) => inner.MoveToAttribute(name);
            public override bool MoveToAttribute(string name, string ns) => inner.MoveToAttribute(name, ns);
            public override void MoveToAttribute(int i) => inner.MoveToAttribute(i);
            public override bool MoveToFirstAttribute() => inner.MoveToFirstAttribute();
            public override bool MoveToNextAttribute() => inner.MoveToNextAttribute();
            public override bool MoveToElement() => inner.MoveToElement();
            public override bool ReadAttributeValue() => inner.ReadAttributeValue();
            public override string LookupNamespace(string prefix) => inner.LookupNamespace(prefix);
            public override void ResolveEntity() => inner.ResolveEntity();
            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
