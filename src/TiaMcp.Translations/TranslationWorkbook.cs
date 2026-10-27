using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using static TiaMcp.Translations.TranslationJson;

namespace TiaMcp.Translations;

/// <summary>Captured offline workbook. No method resolves, opens or imports a native project.</summary>
public sealed class TranslationWorkbook
{
    internal BoundedOpcPackage Package { get; }
    internal WorkbookProfile Mapping { get; }
    private readonly Dictionary<string, Dictionary<string, XElement>> cells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> worksheetRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TranslationRow> keyedRows = new(StringComparer.Ordinal);
    internal (TranslationRow Row, TranslationCell Target)[] OrderedTargets { get; }
    private readonly XElement[] sharedStrings;
    public EngineeringObjectReference Project { get; }
    public string WorkbookSha256 { get; }
    public string ProfileSha256 { get; }
    public WorkbookProfile Profile => CopyProfile(Mapping);
    public bool NativeQualified => false;
    public IReadOnlyDictionary<string, string> PartHashes => Package.Parts.ToDictionary(p => p.Key, p => Sha256(p.Value), StringComparer.Ordinal);
    public byte[] OriginalBytes() => Package.Original.ToArray();

    public static TranslationWorkbook Open(ReadOnlySpan<byte> bytes, WorkbookProfile profile,
        EngineeringObjectReference project, string expectedWorkbookSha256, WorkbookLimits? limits = null)
    {
        try { return new(bytes, profile, project, expectedWorkbookSha256, limits ?? new()); }
        catch (InvalidDataException ex) { throw new BridgeException("WorkbookIntegrityFailure", "Invalid ZIP data: " + ex.Message); }
        catch (DecoderFallbackException ex) { throw new BridgeException("WorkbookIntegrityFailure", "Invalid package encoding: " + ex.Message); }
    }

    private TranslationWorkbook(ReadOnlySpan<byte> bytes, WorkbookProfile profile,
        EngineeringObjectReference project, string expectedWorkbookSha256, WorkbookLimits limits)
    {
        EngineeringIdentityGuard.Validate(project);
        Require(project.Object.Kind == "project", "InvalidTranslationIdentity", "Export provenance must identify an exact project.");
        EngineeringIdentityGuard.Sha256(expectedWorkbookSha256, "expectedWorkbookSha256");
        WorkbookSha256 = Sha256(bytes);
        Require(WorkbookSha256 == expectedWorkbookSha256, "StaleWorkbook", "Workbook bytes differ from the captured export.");
        Project = project;
        Require(profile is not null && profile.Rows is not null, "UnsupportedWorkbookProfile", "An explicit profile is required.");
        Require(profile.Rows.Length <= limits.MaximumCells, "WorkbookLimit", "Profile row count exceeds cell budget.");
        // The external JSON contract is bounded separately. A valid in-memory 100k-row
        // capture must not roundtrip through that 16 Mi-character input boundary.
        Mapping = CopyProfile(profile!);
        Require(!string.IsNullOrWhiteSpace(Mapping.Id) && Mapping.Id.Length <= 256 &&
            Mapping.Kind is "synthetic" or "unqualified-explicit", "UnsupportedWorkbookProfile", "Only explicitly unqualified offline profiles are accepted.");
        CheckCulture(Mapping.SourceCulture);
        Require(Mapping.Rows is not null && Mapping.Rows.Length <= limits.MaximumCells, "WorkbookLimit", "Bounded profile rows are required.");
        ProfileSha256 = HashJson(Mapping);
        Package = new BoundedOpcPackage(bytes, limits);
        foreach (var pair in Package.Xml.Where(p => p.Value.Root?.Name == BoundedOpcPackage.Sheet + "worksheet"))
        {
            var index = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var rowNumbers = new HashSet<int>();
            foreach (var row in WorksheetRows(pair.Value))
            {
                Require(int.TryParse((string?)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number is > 0 and <= 1048576 && rowNumbers.Add(number),
                    "WorkbookIntegrityFailure", "Invalid or duplicate worksheet row.");
                foreach (var cell in row.Elements(BoundedOpcPackage.Sheet + "c"))
                {
                    string address = (string?)cell.Attribute("r") ?? "";
                    var position = ParseCell(address);
                    Require(position.Row == number && index.TryAdd(address, cell), "WorkbookIntegrityFailure", "Duplicate cell or inconsistent row address.");
                }
            }
            Require(pair.Value.Descendants(BoundedOpcPackage.Sheet + "c").Count() == index.Count,
                "UnsupportedWorkbookProfile", "Cells outside the expected sheetData/row structure are unsupported.");
            cells.Add(pair.Key, index);
            worksheetRows.Add(pair.Key, rowNumbers);
        }
        if (Package.Xml.TryGetValue("xl/sharedStrings.xml", out var stringTable))
            Require(stringTable.Root?.Name == BoundedOpcPackage.Sheet + "sst", "WorkbookIntegrityFailure", "Shared-string table has wrong root.");
        sharedStrings = Package.Xml.TryGetValue("xl/sharedStrings.xml", out var shared)
            ? shared.Root?.Elements(BoundedOpcPackage.Sheet + "si").ToArray() ?? [] : [];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var mappedLocations = new HashSet<CellLocation>();
        foreach (var row in Mapping.Rows!)
        {
            Require(row is not null, "UnsupportedWorkbookProfile", "Null row mapping is invalid.");
            Require(!string.IsNullOrEmpty(row.NativeKey) && row.NativeKey.Length <= limits.MaximumTextLength && keys.Add(row.NativeKey),
                "DuplicateNativeKey", "Native keys must be bounded, present and unique.");
            keyedRows.Add(row.NativeKey, row);
            Require(!string.IsNullOrWhiteSpace(row.Category) && row.Category.Length <= 1024 && row.TokenGrammar is "none" or "brace-literals-v1" or "unknown",
                "UnsupportedWorkbookProfile", "Category and an explicit supported or unknown token grammar are required.");
            Require(row.AlarmTextPolicy is null or "instance-inheritance-v1", "UnsupportedWorkbookProfile", "Unknown alarm text policy.");
            ValidateLocation(row.KeyCell, mappedLocations);
            ValidateLocation(row.SourceCell, mappedLocations);
            var key = ReadText(row.KeyCell);
            Require(key.State == "text" && !key.RichText && key.Text == row.NativeKey, "StaleTextKey", "Native key cell differs from explicit profile.");
            Require(ReadText(row.SourceCell).State != "missing", "UnsupportedWorkbookProfile", "Source cell must exist.");
            Require(row.Targets is not null && row.Targets.Length <= 1000, "UnsupportedWorkbookProfile", "Target language map requires at most 1000 fields.");
            var cultures = new HashSet<string>(StringComparer.Ordinal) { Mapping.SourceCulture };
            foreach (var target in row.Targets!)
            {
                Require(target is not null, "UnsupportedWorkbookProfile", "Null target mapping is invalid.");
                CheckCulture(target.Culture);
                Require(cultures.Add(target.Culture) && (target.MaxUtf16Length is null or > 0), "UnsupportedWorkbookProfile", "Duplicate culture or invalid text limit.");
                ValidateLocation(target.Location, mappedLocations);
                ReadText(target.Location);
            }
        }
        OrderedTargets = Mapping.Rows.OrderBy(r => r.NativeKey, StringComparer.Ordinal)
            .SelectMany(r => r.Targets.OrderBy(t => t.Culture, StringComparer.Ordinal).Select(t => (r, t))).ToArray();
    }

    private static WorkbookProfile CopyProfile(WorkbookProfile profile) => profile with
    {
        Rows = profile.Rows.Select(row => row is null ? null! : row with
        { Targets = row.Targets is null ? null! : row.Targets.ToArray() }).ToArray()
    };

    private void ValidateLocation(CellLocation location, HashSet<CellLocation> known)
    {
        Require(location is not null, "UnsupportedWorkbookProfile", "Cell location required.");
        BoundedOpcPackage.SafePart(location!.Part);
        ParseCell(location.Cell);
        Require(cells.ContainsKey(location.Part) && known.Add(location), "UnsupportedWorkbookProfile", "Mapped locations must be unique worksheet cells.");
        Require(known.Count <= Package.Limits.MaximumCells, "WorkbookLimit", "Mapped locations exceed the cell budget, including absent cells.");
        int rowNumber = ParseCell(location.Cell).Row;
        Require(worksheetRows[location.Part].Contains(rowNumber),
            "UnsupportedWorkbookProfile", "Missing whole rows are not created by this offline profile.");
    }

    public TextCellState ReadText(CellLocation location)
    {
        Require(cells.TryGetValue(location.Part, out var index), "UnsupportedWorkbookProfile", "Unknown worksheet part.");
        if (!index!.TryGetValue(location.Cell, out var cell)) return new("missing", null);
        string? type = (string?)cell.Attribute("t");
        XElement? container;
        if (type == "s")
        {
            Require(cell.Elements(BoundedOpcPackage.Sheet + "v").Count() == 1 && !cell.Elements(BoundedOpcPackage.Sheet + "is").Any() &&
                !cell.Element(BoundedOpcPackage.Sheet + "v")!.HasElements,
                "WorkbookIntegrityFailure", "Shared-string cells require one unambiguous index.");
            Require(int.TryParse(cell.Element(BoundedOpcPackage.Sheet + "v")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) &&
                ordinal >= 0 && ordinal < sharedStrings.Length, "WorkbookIntegrityFailure", "Invalid shared-string reference.");
            container = sharedStrings[ordinal];
        }
        else if (type == "inlineStr")
        {
            Require(cell.Elements(BoundedOpcPackage.Sheet + "is").Count() == 1 && !cell.Elements(BoundedOpcPackage.Sheet + "v").Any(),
                "WorkbookIntegrityFailure", "Inline-string cells require one unambiguous container.");
            container = cell.Element(BoundedOpcPackage.Sheet + "is");
        }
        else
        {
            Require(cell.Elements().All(e => e.Name == BoundedOpcPackage.Sheet + "extLst") && type is null,
                "UnsupportedWorkbookProfile", "Mapped text must be inline/shared text or an explicit empty cell; numeric/formula caches are not coerced.");
            return new("empty", "");
        }
        Require(container is not null, "WorkbookIntegrityFailure", "String container is absent.");
        bool rich = container!.Elements(BoundedOpcPackage.Sheet + "r").Any();
        Require(container.Elements(BoundedOpcPackage.Sheet + "t").Count() <= 1 &&
            !(rich && container.Elements(BoundedOpcPackage.Sheet + "t").Any()) &&
            container.Elements(BoundedOpcPackage.Sheet + "r").All(r => r.Elements(BoundedOpcPackage.Sheet + "t").Count() <= 1),
            "WorkbookIntegrityFailure", "Mixed or repeated string text representations are ambiguous.");
        var textNodes = rich ? container.Elements(BoundedOpcPackage.Sheet + "r").Elements(BoundedOpcPackage.Sheet + "t") : container.Elements(BoundedOpcPackage.Sheet + "t");
        Require(textNodes.All(t => !t.HasElements), "UnsupportedWorkbookProfile", "Mapped text contains nested XML content.");
        string value = DecodeText(string.Concat(textNodes.Select(n => n.Value)));
        Require(value.Length <= Package.Limits.MaximumTextLength, "WorkbookLimit", "Text length budget exceeded.");
        EncodeText(value); // Escaped UTF-16 must also decode to valid scalar text.
        return new(value.Length == 0 ? "empty" : "text", value, rich);
    }

    public (TextCellState Source, TextCellState Target) Read(string nativeKey, string culture)
    {
        var row = Row(nativeKey);
        return (ReadText(row.SourceCell), ReadText(Location(row, culture)));
    }

    internal TranslationRow Row(string key) => keyedRows.TryGetValue(key, out var row) ? row
        : throw new BridgeException("StaleTextKey", "Key is absent from the captured explicit map.");

    internal CellLocation Location(TranslationRow row, string culture) => culture == Mapping.SourceCulture ? row.SourceCell :
        row.Targets.SingleOrDefault(t => t.Culture == culture)?.Location ?? throw new BridgeException("LanguageUnmapped", "Culture is not mapped for this key.");

    internal void CheckWritable(TranslationRow row, string culture, TranslationEditKind kind, string value)
    {
        var location = Location(row, culture);
        var state = ReadText(location);
        Require(!state.RichText, "RichTextEditUnsupported", "Rich text is retained but cannot be flattened for editing.");
        var target = row.Targets.SingleOrDefault(t => t.Culture == culture);
        if (kind == TranslationEditKind.Clear)
            Require(target?.AllowClear == true, "ClearSemanticsUnqualified", "Clearing is not enabled by this explicit offline profile.");
        if (state.State == "missing") Require(target?.AllowCreateMissing == true, "MissingCellEditUnsupported", "Creating this missing cell is not allowed by the profile.");
        if (cells[location.Part].TryGetValue(location.Cell, out var cell))
        {
            Require(cell.Elements().All(e => e.Name == BoundedOpcPackage.Sheet + "v" || e.Name == BoundedOpcPackage.Sheet + "is"),
                "UnsupportedWorkbookProfile", "Edited cell contains opaque child content.");
            Require(cell.Elements(BoundedOpcPackage.Sheet + "v").All(v => !v.HasAttributes && v.Nodes().All(n => n is XText)),
                "UnsupportedWorkbookProfile", "Edited shared-string index contains opaque content.");
            if ((string?)cell.Attribute("t") == "s")
            {
                Require(Mapping.AllowSharedStringToInline, "SharedStringEditUnsupported", "Shared-to-inline conversion requires explicit offline profile selection.");
                int ordinal = int.Parse(cell.Element(BoundedOpcPackage.Sheet + "v")!.Value, CultureInfo.InvariantCulture);
                var shared = sharedStrings[ordinal];
                Require(HasEditableTextOnly(shared),
                    "SharedStringEditUnsupported", "Shared text has opaque associated metadata and cannot be flattened.");
            }
            var inline = cell.Element(BoundedOpcPackage.Sheet + "is");
            if (inline is not null) Require(HasEditableTextOnly(inline),
                "UnsupportedWorkbookProfile", "Opaque inline string content cannot be edited.");
        }
        Require(value.Length <= Package.Limits.MaximumTextLength && (target?.MaxUtf16Length is null || value.Length <= target.MaxUtf16Length),
            "TextLengthExceeded", "Explicit UTF-16 text length limit exceeded.");
        EncodeText(value); // Validate UTF-16 before approval/output.
    }

    internal byte[] Overlay(IReadOnlyList<(CellLocation Location, string Value)> changes)
    {
        var changedParts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var group in changes.GroupBy(c => c.Location.Part, StringComparer.Ordinal))
        {
            var document = new XDocument(Package.Xml[group.Key]);
            var rows = WorksheetRows(document)
                .ToDictionary(r => int.Parse((string)r.Attribute("r")!, CultureInfo.InvariantCulture));
            foreach (var change in group)
            {
                var rowNumber = ParseCell(change.Location.Cell).Row;
                var row = rows[rowNumber];
                var cell = row.Elements(BoundedOpcPackage.Sheet + "c").SingleOrDefault(c => (string?)c.Attribute("r") == change.Location.Cell);
                if (cell is null)
                {
                    cell = new XElement(BoundedOpcPackage.Sheet + "c", new XAttribute("r", change.Location.Cell));
                    var next = row.Elements(BoundedOpcPackage.Sheet + "c").FirstOrDefault(c => ParseCell((string)c.Attribute("r")!).Column > ParseCell(change.Location.Cell).Column);
                    if (next is null) row.Add(cell); else next.AddBeforeSelf(cell);
                }
                cell.SetAttributeValue("t", "inlineStr");
                cell.Elements(BoundedOpcPackage.Sheet + "v").Remove();
                cell.Elements(BoundedOpcPackage.Sheet + "is").Remove();
                cell.Add(new XElement(BoundedOpcPackage.Sheet + "is", new XElement(BoundedOpcPackage.Sheet + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), EncodeText(change.Value))));
            }
            changedParts.Add(group.Key, BoundedOpcPackage.XmlBytes(document));
        }
        return Package.Repack(changedParts);
    }

    private static IEnumerable<XElement> WorksheetRows(XDocument document)
    {
        var containers = document.Root!.Elements(BoundedOpcPackage.Sheet + "sheetData").ToArray();
        Require(containers.Length == 1, "UnsupportedWorkbookProfile", "Exactly one direct worksheet sheetData container is required.");
        return containers[0].Elements(BoundedOpcPackage.Sheet + "row");
    }

    private static bool HasEditableTextOnly(XElement container) => !container.HasAttributes && container.Elements().Count() <= 1 &&
        container.Nodes().All(n => n is XElement text
            ? text.Name == BoundedOpcPackage.Sheet + "t" && text.Nodes().All(child => child is XText) &&
              text.Attributes().All(a => a.Name == XNamespace.Xml + "space")
            : n is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value));

    internal static (int Column, int Row) ParseCell(string address)
    {
        var match = Regex.Match(address ?? "", "^([A-Z]{1,3})([1-9][0-9]{0,6})$", RegexOptions.CultureInvariant);
        Require(match.Success, "UnsupportedWorkbookProfile", "Expected an exact A1 worksheet address.");
        int column = 0;
        foreach (char c in match.Groups[1].Value) column = column * 26 + c - 'A' + 1;
        int row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        Require(column <= 16384 && row <= 1048576, "UnsupportedWorkbookProfile", "Cell address exceeds XLSX limits.");
        return (column, row);
    }

    internal static string ColumnName(int column) => column <= 26 ? ((char)('A' + column - 1)).ToString() : ColumnName((column - 1) / 26) + (char)('A' + (column - 1) % 26);

    private static void CheckCulture(string culture)
    {
        try { Require(!string.IsNullOrWhiteSpace(culture) && CultureInfo.GetCultureInfo(culture).Name == culture,
            "InvalidTranslationCulture", "Culture codes must use exact canonical names."); }
        catch (CultureNotFoundException) { throw new BridgeException("InvalidTranslationCulture", "Unknown culture."); }
    }

    internal static string DecodeText(string text) => Regex.Replace(text, "_x([0-9A-Fa-f]{4})_", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    internal static string EncodeText(string text)
    {
        var result = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                Require(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "InvalidTranslationText", "Isolated surrogate is invalid.");
                result.Append(c).Append(text[++i]); continue;
            }
            Require(!char.IsLowSurrogate(c), "InvalidTranslationText", "Isolated surrogate is invalid.");
            if (c == '_' && i + 7 <= text.Length && text[i + 1] == 'x' && text[i + 6] == '_' && text.AsSpan(i + 2, 4).ToString().All(Uri.IsHexDigit)) result.Append("_x005F_");
            else if (c < 32 && c is not '\t' and not '\n' and not '\r' || c is '\uFFFE' or '\uFFFF') result.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            else result.Append(c);
        }
        return result.ToString();
    }
}
