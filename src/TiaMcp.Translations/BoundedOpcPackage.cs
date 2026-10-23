using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Core;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

internal sealed class BoundedOpcPackage
{
    internal static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    internal static readonly XNamespace Relations = "http://schemas.openxmlformats.org/package/2006/relationships";
    internal static readonly XNamespace Content = "http://schemas.openxmlformats.org/package/2006/content-types";
    internal byte[] Original { get; }
    internal Dictionary<string, byte[]> Parts { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, XDocument> Xml { get; } = new(StringComparer.Ordinal);
    internal WorkbookLimits Limits { get; }

    internal BoundedOpcPackage(ReadOnlySpan<byte> input, WorkbookLimits limits)
    {
        Limits = limits;
        Require(limits.MaximumCompressedBytes > 0 && limits.MaximumExpandedBytes > 0 && limits.MaximumEntries > 0 &&
            limits.MaximumCells > 0 && limits.MaximumXmlDepth > 0 && limits.MaximumTextLength > 0 && limits.MaximumXmlBytes > 0 && limits.MaximumXmlNodes > 0,
            "InvalidWorkbookLimits", "Every package limit must be positive.");
        Require(limits.MaximumCompressedBytes <= 128L * 1024 * 1024 && limits.MaximumExpandedBytes <= 512L * 1024 * 1024 &&
            limits.MaximumEntries <= 10000 && limits.MaximumCells <= 1000000 && limits.MaximumXmlDepth <= 128 &&
            limits.MaximumTextLength <= 1048576 && limits.MaximumXmlBytes <= 64 * 1024 * 1024 && limits.MaximumXmlNodes <= 1000000,
            "InvalidWorkbookLimits", "Limits cannot exceed this implementation's fixed safety ceilings.");
        Require(input.Length <= limits.MaximumCompressedBytes, "WorkbookLimit", "Compressed input exceeds limit.");
        Original = input.ToArray();
        var central = InspectDirectory(Original, limits);
        using var stream = new MemoryStream(Original, false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, new UTF8Encoding(false, true));
        Require(archive.Entries.Count == central.Count, "WorkbookIntegrityFailure", "ZIP directory count mismatch.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            SafePart(entry.FullName);
            Require(central.TryGetValue(entry.FullName, out var expected), "WorkbookIntegrityFailure", "ZIP name mismatch.");
            Require(entry.Length == expected!.Size && entry.Length <= limits.MaximumExpandedBytes - expanded,
                "WorkbookLimit", "Expanded package exceeds limit or directory size mismatches.");
            using var source = entry.Open();
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                Require(output.Length + read <= expected.Size && expanded + output.Length + read <= limits.MaximumExpandedBytes,
                    "WorkbookLimit", "Entry expands beyond its declared size or package limit.");
                output.Write(buffer, 0, read);
            }
            byte[] bytes = output.ToArray();
            Require(bytes.Length == expected.Size && Crc32(bytes) == expected.Crc,
                "WorkbookIntegrityFailure", "ZIP CRC/size mismatch.");
            expanded += bytes.Length;
            Require(Parts.TryAdd(entry.FullName, bytes), "DuplicatePackagePart", "Duplicate package part.");
            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                Xml.Add(entry.FullName, ReadXml(bytes, limits));
        }
        ValidateOpc();
    }

    private void ValidateOpc()
    {
        Require(Xml.TryGetValue("[Content_Types].xml", out var types) && types.Root?.Name == Content + "Types",
            "WorkbookIntegrityFailure", "OPC content types are required.");
        Require(Xml.ContainsKey("_rels/.rels") && Xml.TryGetValue("xl/workbook.xml", out var workbook) && workbook.Root?.Name == Sheet + "workbook",
            "UnsupportedWorkbookProfile", "Standard transitional workbook parts are required.");
        var contentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types!.Root!.Elements())
        {
            Require(type.Name == Content + "Default" || type.Name == Content + "Override", "WorkbookIntegrityFailure", "Unknown OPC content-type element.");
            string key = type.Name.LocalName + ":" + (string?)(type.Attribute("PartName") ?? type.Attribute("Extension"));
            Require(contentKeys.Add(key), "WorkbookIntegrityFailure", "Duplicate content type declaration.");
            string mime = (string?)type.Attribute("ContentType") ?? "";
            Require(!new[] { "macro", "vba", "oleobject", "activex", "externallink" }.Any(x => mime.Contains(x, StringComparison.OrdinalIgnoreCase)),
                "ActiveWorkbookContent", "Executable, macro or linked content is not accepted.");
            if (type.Name.LocalName == "Override")
            {
                string target = ((string?)type.Attribute("PartName") ?? "").TrimStart('/');
                SafePart(target);
                Require(Parts.ContainsKey(target), "WorkbookIntegrityFailure", "Content type references an absent part.");
            }
        }
        foreach (string part in Parts.Keys.Where(p => p != "[Content_Types].xml"))
        {
            string extension = Path.GetExtension(part).TrimStart('.');
            Require(contentKeys.Contains("Override:/" + part) || contentKeys.Contains("Default:" + extension),
                "WorkbookIntegrityFailure", "Package part lacks a content type.");
        }
        foreach (var pair in Xml.Where(x => x.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            Require(pair.Value.Root?.Name == Relations + "Relationships", "WorkbookIntegrityFailure", "Malformed relationship part.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in pair.Value.Root!.Elements())
            {
                Require(rel.Name == Relations + "Relationship" && ids.Add((string?)rel.Attribute("Id") ?? "") && !string.IsNullOrEmpty((string?)rel.Attribute("Id")),
                    "WorkbookIntegrityFailure", "Invalid/duplicate relationship ID.");
                Require(rel.Attribute("TargetMode") is null || (string?)rel.Attribute("TargetMode") == "Internal",
                    "ExternalWorkbookRelationship", "External relationships are never followed.");
                string target = (string?)rel.Attribute("Target") ?? "";
                Require(!new[] { "vba", "oleObject", "activeX", "externalLink" }.Any(x => ((string?)rel.Attribute("Type") ?? "").Contains(x, StringComparison.OrdinalIgnoreCase)),
                    "ActiveWorkbookContent", "Active relationship types are rejected.");
                string resolved = ResolveRelationship(pair.Key, target);
                Require(Parts.ContainsKey(resolved), "WorkbookIntegrityFailure", "Relationship references an absent part.");
            }
        }
        var rootLinks = Xml["_rels/.rels"].Root!.Elements(Relations + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true).ToArray();
        Require(rootLinks.Length == 1 && ResolveRelationship("_rels/.rels", (string?)rootLinks[0].Attribute("Target") ?? "") == "xl/workbook.xml",
            "UnsupportedWorkbookProfile", "Exactly one standard workbook relationship is required.");
        Require(Xml.TryGetValue("xl/_rels/workbook.xml.rels", out var workbookLinks), "WorkbookIntegrityFailure", "Workbook relationships are missing.");
        var sheetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in Xml["xl/workbook.xml"].Descendants(Sheet + "sheet"))
        {
            string id = (string?)sheet.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")) ?? "";
            Require(sheetIds.Add(id), "WorkbookIntegrityFailure", "Duplicate sheet relationship.");
            var link = workbookLinks!.Root!.Elements(Relations + "Relationship").SingleOrDefault(r => (string?)r.Attribute("Id") == id);
            Require(link is not null && ((string?)link.Attribute("Type"))?.EndsWith("/worksheet", StringComparison.Ordinal) == true,
                "UnsupportedWorkbookProfile", "Only explicitly related worksheet sheets are accepted.");
            string part = ResolveRelationship("xl/_rels/workbook.xml.rels", (string?)link!.Attribute("Target") ?? "");
            Require(Xml.TryGetValue(part, out var sheetDocument) && sheetDocument.Root?.Name == Sheet + "worksheet",
                "WorkbookIntegrityFailure", "Worksheet relationship has wrong content.");
        }
        int cells = 0;
        foreach (var doc in Xml.Values)
        {
            Require(!doc.Descendants(Sheet + "f").Any() && !doc.Descendants(Sheet + "externalLink").Any(),
                "WorkbookFormulaRejected", "Formulas and external links are never evaluated or accepted.");
            cells += doc.Descendants(Sheet + "c").Count();
            Require(cells <= Limits.MaximumCells, "WorkbookLimit", "Cell budget exceeded.");
        }
    }

    internal static void SafePart(string path)
    {
        Require(!string.IsNullOrEmpty(path) && !path.StartsWith('/') && !path.EndsWith('/') &&
            !path.Any(c => c == '\\' || c == ':' || c == '%' || c == '#' || c == '?' || char.IsControl(c)) &&
            path.Split('/').All(s => s.Length != 0 && s != "." && s != ".."),
            "UnsafePackagePath", "Expected a literal relative OPC part path without traversal or URI escapes.");
    }

    private static string ResolveRelationship(string relationshipPart, string target)
    {
        Require(!string.IsNullOrEmpty(target) && !target.Any(c => c == '\\' || c == ':' || c == '%' || c == '#' || c == '?' || char.IsControl(c)),
            "ExternalWorkbookRelationship", "Unsupported relationship target URI.");
        int marker = relationshipPart.LastIndexOf("/_rels/", StringComparison.Ordinal);
        Require(relationshipPart == "_rels/.rels" || marker >= 0, "WorkbookIntegrityFailure", "Relationship part has an unsupported location.");
        string ownerDirectory = relationshipPart == "_rels/.rels" ? "" : relationshipPart[..marker];
        var segments = new List<string>();
        if (!target.StartsWith('/') && ownerDirectory.Length != 0) segments.AddRange(ownerDirectory.Split('/'));
        foreach (string segment in target.TrimStart('/').Split('/'))
        {
            if (segment == "..") { Require(segments.Count > 0, "UnsafePackagePath", "Relationship leaves package root."); segments.RemoveAt(segments.Count - 1); }
            else if (segment != "." && segment.Length != 0) segments.Add(segment);
        }
        string resolved = string.Join('/', segments);
        SafePart(resolved);
        return resolved;
    }

    internal static XDocument ReadXml(byte[] bytes, WorkbookLimits limits)
    {
        try
        {
            Require(bytes.Length <= limits.MaximumXmlBytes, "WorkbookLimit", "XML part byte limit exceeded.");
            // A bounded streaming pass rejects excessive depth/nodes before building the DOM.
            using (var probeStream = new MemoryStream(bytes, false))
            using (var probe = XmlReader.Create(probeStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limits.MaximumXmlBytes }))
            {
                int nodes = 0;
                while (probe.Read()) Require(++nodes <= limits.MaximumXmlNodes && probe.Depth <= limits.MaximumXmlDepth,
                    "WorkbookLimit", "XML node/depth limit exceeded.");
            }
            using var stream = new MemoryStream(bytes, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = limits.MaximumExpandedBytes, IgnoreWhitespace = false
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            Require(!document.Descendants().Any(n => n.Ancestors().Take(limits.MaximumXmlDepth + 1).Count() > limits.MaximumXmlDepth),
                "WorkbookLimit", "XML depth exceeds limit.");
            return document;
        }
        catch (XmlException ex) { throw new BridgeException("WorkbookXmlRejected", "Malformed or prohibited XML: " + ex.Message); }
    }

    internal static byte[] XmlBytes(XDocument doc)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize, Indent = false }))
            doc.Save(writer);
        return buffer.ToArray();
    }

    internal byte[] Repack(IReadOnlyDictionary<string, byte[]> changed)
    {
        if (changed.Count == 0) return Original.ToArray();
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var part in Parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = output.CreateEntry(part.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = entry.Open();
                target.Write(changed.TryGetValue(part.Key, out var replacement) ? replacement : part.Value);
            }
        }
        return buffer.ToArray();
    }

    private sealed record DirectoryEntry(long Size, uint Crc);
    private static Dictionary<string, DirectoryEntry> InspectDirectory(byte[] bytes, WorkbookLimits limits)
    {
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
        Require(bytes.Length >= 22, "WorkbookIntegrityFailure", "Truncated ZIP.");
        int end = -1;
        for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); --i)
            if (U32(i) == 0x06054b50 && i + 22 + U16(i + 20) == bytes.Length) { end = i; break; }
        Require(end >= 0, "WorkbookIntegrityFailure", "ZIP end directory absent.");
        Require(U16(end + 4) == 0 && U16(end + 6) == 0 && U16(end + 8) == U16(end + 10),
            "UnsupportedWorkbookProfile", "Split ZIP is unsupported.");
        int count = U16(end + 10);
        Require(count != ushort.MaxValue && count <= limits.MaximumEntries, "WorkbookLimit", "ZIP64/count exceeds this profile.");
        long start = U32(end + 16), size = U32(end + 12);
        Require(start + size == end && start <= int.MaxValue, "WorkbookIntegrityFailure", "Invalid central directory extent.");
        int cursor = (int)start;
        long expanded = 0;
        var result = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            Require(cursor + 46 <= end && U32(cursor) == 0x02014b50, "WorkbookIntegrityFailure", "Malformed ZIP directory.");
            int flags = U16(cursor + 8), method = U16(cursor + 10), nameLength = U16(cursor + 28);
            int length = 46 + nameLength + U16(cursor + 30) + U16(cursor + 32);
            Require(cursor + length <= end && U16(cursor + 34) == 0 && (flags & 1) == 0 && (method == 0 || method == 8),
                "UnsupportedWorkbookProfile", "Encrypted, split or unsupported ZIP method.");
            long uncompressed = U32(cursor + 24), compressed = U32(cursor + 20), local = U32(cursor + 42);
            Require(uncompressed != uint.MaxValue && compressed != uint.MaxValue && local + 30 <= start,
                "UnsupportedWorkbookProfile", "ZIP64 or invalid local offset.");
            expanded += uncompressed;
            Require(expanded <= limits.MaximumExpandedBytes, "WorkbookLimit", "Declared expansion exceeds package limit.");
            var nameBytes = bytes.AsSpan(cursor + 46, nameLength);
            Require((flags & 0x800) != 0 || !nameBytes.ContainsAnyInRange((byte)128, (byte)255),
                "UnsupportedWorkbookProfile", "Non-UTF8 ZIP names are unsupported.");
            string name = new UTF8Encoding(false, true).GetString(nameBytes);
            SafePart(name);
            int localAt = (int)local;
            Require(U32(localAt) == 0x04034b50 && U16(localAt + 6) == flags && U16(localAt + 8) == method,
                "WorkbookIntegrityFailure", "Local/central ZIP header mismatch.");
            int localNameLength = U16(localAt + 26), localExtraLength = U16(localAt + 28);
            Require(local + 30 + localNameLength + localExtraLength + compressed <= start && localNameLength == nameLength &&
                bytes.AsSpan(localAt + 30, localNameLength).SequenceEqual(nameBytes), "WorkbookIntegrityFailure", "Local ZIP name/extent mismatch.");
            Require(result.TryAdd(name, new DirectoryEntry(uncompressed, U32(cursor + 16))), "DuplicatePackagePart", "Duplicate ZIP part.");
            cursor += length;
        }
        Require(cursor == end, "WorkbookIntegrityFailure", "Unaccounted directory bytes.");
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data) { crc ^= value; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
        return ~crc;
    }
}
