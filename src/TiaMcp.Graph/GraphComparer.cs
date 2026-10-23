using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TiaMcp.Graph;

public sealed record GraphChange(string Category, string Kind, string Identity, string Field, string? Before, string? After);
public sealed record GraphComparison(string Format, string BeforeSha256, string AfterSha256, bool SourceBytesChanged,
    bool MatchingReliable, bool NativeQualified, bool BehavioralEquivalenceCertified, string Alignment,
    IReadOnlyList<GraphChange> Changes, IReadOnlyList<GraphDiagnostic> BeforeDiagnostics, IReadOnlyList<GraphDiagnostic> AfterDiagnostics)
{
    public string ToJson() => GraphDocument.Serialize(this);
}

public static class GraphComparer
{
    public static GraphComparison Compare(GraphDocument before, GraphDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<GraphChange>();
        bool reliable = !before.Inspection.HasErrors && !after.Inspection.HasErrors;
        var beforeElements = before.Paths.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var afterElements = after.Paths.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

        void CompareXml(string category, string kind, string identity, string field, IEnumerable<XElement> left, IEnumerable<XElement> right)
        {
            var a = new XElement("slice", left.Select(CopyWithSpaceContext));
            var b = new XElement("slice", right.Select(CopyWithSpaceContext));
            if (Fingerprint(a) != Fingerprint(b)) changes.Add(new(category, kind, identity, field, Display(a), Display(b)));
        }
        void CompareValue(string category, string kind, string identity, string field, string? a, string? b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal)) changes.Add(new(category, kind, identity, field, a, b));
        }
        void Nodes(string kind, string container, XElement a, XElement b, string sequenceKey)
        {
            var left = Children(a, container).SelectMany(e => Children(e, kind)).ToArray();
            var right = Children(b, container).SelectMany(e => Children(e, kind)).ToArray();
            static int? Number(XElement e) => int.TryParse((string?)e.Attribute("Number"), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;
            if (left.Concat(right).Any(e => Number(e) is null) || left.GroupBy(Number).Any(g => g.Count() > 1) || right.GroupBy(Number).Any(g => g.Count() > 1))
            {
                reliable = false;
                CompareXml("unresolved", kind, sequenceKey, "ambiguousIdentities", left, right);
                return;
            }
            var l = left.ToDictionary(e => Number(e)!.Value);
            var r = right.ToDictionary(e => Number(e)!.Value);
            CompareValue("structure", kind, sequenceKey, "documentOrder", string.Join(",", left.Select(Number)), string.Join(",", right.Select(Number)));
            foreach (int key in l.Keys.Union(r.Keys).Order())
            {
                string identity = $"{sequenceKey}/{kind}:{key}";
                if (!l.TryGetValue(key, out var x)) { changes.Add(new("structure", kind, identity, "added", null, r[key].ToString(SaveOptions.DisableFormatting))); continue; }
                if (!r.TryGetValue(key, out var y)) { changes.Add(new("structure", kind, identity, "removed", x.ToString(SaveOptions.DisableFormatting), null)); continue; }
                string[] fields = kind switch
                {
                    "Step" => ["Number", "Name", "Init", "IsMissing", "MaximumStepTime", "WarningTime"],
                    "Transition" => ["Number", "Name", "IsMissing", "ProgrammingLanguage"],
                    _ => ["Number", "Type", "Cardinality"]
                };
                foreach (string field in fields)
                    CompareValue(field is "Name" ? "metadata" : field is "MaximumStepTime" or "WarningTime" or "ProgrammingLanguage" ? "conditions" : "structure",
                        kind, identity, field, (string?)x.Attribute(field), (string?)y.Attribute(field));
                string[] content = kind == "Step" ? ["Actions", "Interlocks", "Supervisions"] : kind == "Transition" ? ["FlgNet"] : [];
                foreach (string child in content)
                    CompareXml(child == "Actions" ? "actions" : "conditions", kind, identity, child, Children(x, child), Children(y, child));
                CompareXml("retainedContent", kind, identity, "otherXml", [Remainder(x, content, fields)], [Remainder(y, content, fields)]);
            }
        }

        int graphCount = Math.Max(before.Inspection.Graphs.Count, after.Inspection.Graphs.Count);
        if (before.Inspection.Graphs.Count != after.Inspection.Graphs.Count) reliable = false;
        for (int g = 0; g < graphCount; g++)
        {
            string graphKey = $"Graph:{g + 1}";
            if (g >= before.Inspection.Graphs.Count || g >= after.Inspection.Graphs.Count)
            {
                var l = g < before.Inspection.Graphs.Count ? beforeElements[before.Inspection.Graphs[g].Path] : null;
                var r = g < after.Inspection.Graphs.Count ? afterElements[after.Inspection.Graphs[g].Path] : null;
                changes.Add(new("structure", "Graph", graphKey, l is null ? "added" : "removed", l?.ToString(SaveOptions.DisableFormatting), r?.ToString(SaveOptions.DisableFormatting)));
                continue;
            }
            var ac = before.Inspection.Graphs[g]; var bc = after.Inspection.Graphs[g];
            var a = beforeElements[ac.Path]; var b = afterElements[bc.Path];
            CompareXml("retainedContent", "Graph", graphKey, "operationsAlarmsAndOtherXml", [Remainder(a, ["Sequence"])], [Remainder(b, ["Sequence"])]);
            if (ac.Sequences.Count != bc.Sequences.Count) reliable = false;
            int sequenceCount = Math.Max(ac.Sequences.Count, bc.Sequences.Count);
            for (int s = 0; s < sequenceCount; s++)
            {
                string key = $"{graphKey}/Sequence:{s + 1}";
                if (s >= ac.Sequences.Count || s >= bc.Sequences.Count)
                {
                    var l = s < ac.Sequences.Count ? beforeElements[ac.Sequences[s].Path] : null;
                    var r = s < bc.Sequences.Count ? afterElements[bc.Sequences[s].Path] : null;
                    changes.Add(new("structure", "Sequence", key, l is null ? "added" : "removed", l?.ToString(SaveOptions.DisableFormatting), r?.ToString(SaveOptions.DisableFormatting)));
                    continue;
                }
                var x = beforeElements[ac.Sequences[s].Path]; var y = afterElements[bc.Sequences[s].Path];
                Nodes("Step", "Steps", x, y, key);
                Nodes("Transition", "Transitions", x, y, key);
                Nodes("Branch", "Branches", x, y, key);
                CompareXml("structure", "Sequence", key, "connections", Children(x, "Connections"), Children(y, "Connections"));
                // Container attributes, comments and unknown children also participate in comparison.
                foreach (string container in new[] { "Steps", "Transitions", "Branches" })
                    CompareXml("retainedContent", "Sequence", key, container + "OtherXml",
                        Children(x, container).Select(e => Remainder(e, [container == "Branches" ? "Branch" : container.TrimEnd('s')])),
                        Children(y, container).Select(e => Remainder(e, [container == "Branches" ? "Branch" : container.TrimEnd('s')])));
                CompareXml("retainedContent", "Sequence", key, "otherXml", [Remainder(x, ["Steps", "Transitions", "Branches", "Connections"])],
                    [Remainder(y, ["Steps", "Transitions", "Branches", "Connections"])]);
            }
        }
        XDocument Envelope(GraphDocument doc)
        {
            var clone = new XDocument(doc.Xml);
            // Keep graph positions visible while removing their contents, already compared above.
            foreach (var element in clone.Descendants().Where(e => e.Name.LocalName == "Graph"
                         && e.Name.NamespaceName is "" or GraphDocument.V6Namespace
                         && !e.Ancestors().Any(a => a.Name.LocalName == "Graph")).ToArray())
                element.ReplaceWith(new XElement("RetainedGraphPosition"));
            return clone;
        }
        var envelopeA = Envelope(before); var envelopeB = Envelope(after);
        if (Fingerprint(envelopeA) != Fingerprint(envelopeB))
            changes.Add(new("retainedContent", "Document", "Document", "envelopeXml", envelopeA.ToString(SaveOptions.DisableFormatting), envelopeB.ToString(SaveOptions.DisableFormatting)));
        // An explicit fallback prevents any unclassified XML difference from disappearing.
        if (changes.Count == 0 && Fingerprint(before.Xml) != Fingerprint(after.Xml))
            changes.Add(new("retainedContent", "Document", "Document", "unclassifiedXml", Fingerprint(before.Xml), Fingerprint(after.Xml)));
        return new("tia-graph-comparison.v1", before.Inspection.SourceSha256, after.Inspection.SourceSha256,
            before.Inspection.SourceSha256 != after.Inspection.SourceSha256, reliable, false, false,
            "Graphs and sequences by document ordinal; nodes by kind and numeric Number within each sequence. No sequence move/rename inference. Embedded XML changes do not establish behavior changes.",
            changes.AsReadOnly(), before.Inspection.Diagnostics, after.Inspection.Diagnostics);
    }

    private static IEnumerable<XElement> Children(XElement e, string name) => e.Elements(e.Name.Namespace + name);
    private static string Display(XElement slice) => string.Concat(slice.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
    private static XElement Remainder(XElement original, string[] children, string[]? attributes = null)
    {
        var clone = CopyWithSpaceContext(original);
        // Empty placeholders keep unknown content's position relative to understood children visible.
        // Removing children outright would hide an extension/comment moving across a known node.
        foreach (var name in children)
            foreach (var child in clone.Elements(clone.Name.Namespace + name).ToArray()) child.ReplaceWith(new XElement(child.Name));
        foreach (var name in attributes ?? []) clone.Attribute(name)?.Remove();
        // Indentation is not part of the structural remainder unless xml:space or mixed text makes it significant.
        if (original.HasElements && !original.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
            && original.AncestorsAndSelf().Attributes(XNamespace.Xml + "space").FirstOrDefault()?.Value != "preserve")
            clone.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).Remove();
        return clone;
    }

    private static XElement CopyWithSpaceContext(XElement original)
    {
        var clone = new XElement(original);
        if (original.Attribute(XNamespace.Xml + "space") is null
            && original.Ancestors().Attributes(XNamespace.Xml + "space").FirstOrDefault()?.Value == "preserve")
            clone.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        return clone;
    }

    internal static string Fingerprint(XNode node)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Token(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }
        void Visit(XNode current, bool preserve, bool ignoreWhitespace = false)
        {
            switch (current)
            {
                case XDocument d:
                    Token("document"); foreach (var child in d.Nodes()) Visit(child, false, true); Token("/document"); break;
                case XElement e:
                    Token("element"); Token(e.Name.ToString());
                    foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)) { Token("attribute"); Token(a.Name.ToString()); Token(a.Value); }
                    preserve = (string?)e.Attribute(XNamespace.Xml + "space") switch { "preserve" => true, "default" => false, _ => preserve };
                    bool indentation = !preserve && e.HasElements && !e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
                    foreach (var child in e.Nodes()) Visit(child, preserve, indentation);
                    Token("/element"); break;
                case XText t:
                    if (ignoreWhitespace && string.IsNullOrWhiteSpace(t.Value)) break;
                    Token("text"); Token(t.Value); break;
                case XComment c: Token("comment"); Token(c.Value); break;
                case XProcessingInstruction pi: Token("pi"); Token(pi.Target); Token(pi.Data); break;
            }
        }
        Visit(node, node.Parent?.AncestorsAndSelf().Attributes(XNamespace.Xml + "space").FirstOrDefault()?.Value == "preserve");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
