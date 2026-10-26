using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Globalization;

namespace TiaMcp.Model
{
    /// <summary>HMI preservation deliberately does not reuse PLC XML normalization.</summary>
    internal static class HmiPreservationRules
    {
        internal const int PolicyVersion = 1;
        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        internal static XDocument ReadXml(string xml)
        {
            if (xml == null || Encoding.UTF8.GetByteCount(xml) > 32 * 1024 * 1024)
                throw new ArgumentException("HMI export exceeds the 32 MiB preservation bound.");
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        // The exact exported owner ID and qualified property name are mandatory.
        // No XPath, same-name lookup, GUID removal, event stripping or translated-text
        // normalization is accepted. An export ID is local to this one artifact.
        internal static XDocument PatchClassicScalar(XDocument original, string nativeOwnerType, string exportedOwnerId,
            string qualifiedPropertyName, string expected, string replacement)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (nativeOwnerType != "Hmi.Screen.Screen" || qualifiedPropertyName != "BackColor")
                throw new ArgumentException("The Classic scalar allowlist is the exported screen BackColor property only.");
            if (string.IsNullOrWhiteSpace(exportedOwnerId)) throw new ArgumentException("An observed export-local object ID is required.");
            ClassicColor(replacement);
            var result = new XDocument(original);
            var owner = result.Descendants().Where(e => e.Name == XName.Get(nativeOwnerType) && e.Attribute("ID")?.Value == exportedOwnerId).ToArray();
            if (owner.Length != 1) throw new ArgumentException("The exact export-local HMI owner must occur once.");
            var property = owner[0].Elements("AttributeList").Elements(XName.Get(qualifiedPropertyName)).ToArray();
            if (property.Length != 1 || !IsPlainScalar(property[0]) || property[0].Value != expected)
                throw new ArgumentException("The exact scalar property is missing, ambiguous, non-scalar, or stale.");
            property[0].Value = replacement;
            return result;
        }

        internal static int[] ClassicColor(string value)
        {
            var parts = (value ?? "").Split(',');
            if (parts.Length != 3) throw new ArgumentException("Classic BackColor requires an explicit RGB triple, for example 204, 204, 204.");
            var result = new int[3];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out result[i]) || result[i] < 0 || result[i] > 255)
                    throw new ArgumentException("Each Classic BackColor RGB component must be an integer from 0 through 255.");
            if (value != string.Join(", ", result.Select(v => v.ToString(CultureInfo.InvariantCulture))))
                throw new ArgumentException("Classic BackColor requires canonical decimal RGB spelling, for example 204, 204, 204.");
            return result;
        }

        internal static void RequireExactDocument(XDocument expected, XDocument actual)
        {
            // No native normalizations have been qualified for either family.
            // Retain whitespace, namespaces, unknown payloads, bindings, events,
            // translations, resource IDs and library type/version identities.
            // XNode.DeepEquals excludes the XML declaration.
            if (expected.Declaration?.ToString() != actual.Declaration?.ToString() || !XNode.DeepEquals(expected, actual))
                throw new ArgumentException("PreservationMismatch: HMI export differs from the exact prepared document; no normalization is qualified.");
        }

        internal static XDocument NormalizeClassicExport(XDocument document)
        {
            var copy = new XDocument(document);
            if (copy.Root?.Name != XName.Get("Document")) throw new ArgumentException("Unknown Classic export document root.");
            var metadata = copy.Root.Elements("DocumentInfo").ToArray();
            if (metadata.Length > 1) throw new ArgumentException("Ambiguous Classic export metadata envelope.");
            var timestamps = metadata.SelectMany(e => e.Elements("Created")).ToArray();
            if (timestamps.Length > 1) throw new ArgumentException("Ambiguous Classic export creation timestamp.");
            if (timestamps.Length == 1)
            {
                var timestamp = timestamps[0];
                if (!IsPlainScalar(timestamp) || !DateTimeOffset.TryParse(timestamp.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    throw new ArgumentException("Unrecognized native export timestamp shape; do not normalize unknown payload.");
                // Sole normalization: public export-time metadata, proven by retained
                // unchanged V21 Update 2 tag-table exports before/after native reopen.
                // A Created field anywhere else remains engineering content.
                timestamp.Value = "export-time-metadata";
            }
            return copy;
        }

        // Assigning XElement.Value would silently erase comments, processing
        // instructions and CDATA boundaries. Such payloads are not an observed
        // native scalar and cannot be consumed by a patch or normalization.
        private static bool IsPlainScalar(XElement element)
            => !element.HasAttributes && element.Nodes().Count() == 1 && element.FirstNode?.GetType() == typeof(XText);

        internal static string ClassicContentHash(string xml)
            => Hash(NormalizeClassicExport(ReadXml(xml)).ToString(SaveOptions.DisableFormatting));

        internal static void RequireClassicDocumentPreservation(XDocument expected, XDocument actual)
            => RequireExactDocument(NormalizeClassicExport(expected), NormalizeClassicExport(actual));

        internal static string ScopeHash(string family, IDictionary<string, string> nativeArtifactHashes, bool complete)
        {
            HmiRecipeRules.RequireFamily(family);
            if (!complete) throw new ArgumentException("PreservationUnavailable: incomplete HMI exports/readback cannot establish a content fingerprint.");
            if (nativeArtifactHashes == null || nativeArtifactHashes.Count == 0 || nativeArtifactHashes.Count > 50000)
                throw new ArgumentException("A bounded, nonempty complete native-artifact manifest is required.");
            var text = new StringBuilder("hmi-preservation-v1\n" + family + "\n");
            foreach (var pair in nativeArtifactHashes.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(char.IsControl) || pair.Value == null || pair.Value.Length != 64
                    || pair.Value.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid native artifact identity or SHA-256.");
                text.Append(pair.Key.Length).Append(':').Append(pair.Key).Append(':').Append(pair.Value.ToLowerInvariant()).Append('\n');
            }
            return Hash(text.ToString());
        }

        internal static void RequireUnrelatedPreserved(IDictionary<string, string> before, IDictionary<string, string> after,
            IEnumerable<string> intendedChangedPaths, IEnumerable<string> intendedCreatedPaths)
        {
            var changed = new HashSet<string>(intendedChangedPaths ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var created = new HashSet<string>(intendedCreatedPaths ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (changed.Overlaps(created) || changed.Any(p => !before.ContainsKey(p)) || created.Any(before.ContainsKey))
                throw new ArgumentException("The prepared preservation delta is contradictory.");
            if (!new HashSet<string>(before.Keys.Concat(created), StringComparer.Ordinal).SetEquals(after.Keys))
                throw new ArgumentException("PreservationMismatch: an unrelated HMI native artifact was created or removed.");
            foreach (var pair in before)
                if (!changed.Contains(pair.Key) && !string.Equals(pair.Value, after[pair.Key], StringComparison.Ordinal))
                    throw new ArgumentException("PreservationMismatch: unrelated HMI content changed: " + pair.Key);
        }

        // A tag-table export directory has no qualified per-file layout: every file under the
        // created tag's table export may change or appear; any other artifact must stay identical.
        internal static void RequireUnrelatedPreservedOutside(IDictionary<string, string> before, IDictionary<string, string> after, string exportPrefix)
        {
            if (string.IsNullOrEmpty(exportPrefix) || !exportPrefix.EndsWith("/export/", StringComparison.Ordinal))
                throw new ArgumentException("The prepared preservation delta is contradictory.");
            Func<string, bool> inside = path => path.StartsWith(exportPrefix, StringComparison.Ordinal);
            if (!after.Keys.Any(inside)) throw new ArgumentException("PreservationMismatch: the changed tag table no longer exports any native artifact.");
            RequireUnrelatedPreserved(before, after, before.Keys.Where(inside), after.Keys.Where(k => inside(k) && !before.ContainsKey(k)));
        }
    }
}
