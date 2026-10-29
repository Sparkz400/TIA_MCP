using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

/// <summary>Author plain Unified text-list candidates; never import or rewrite existing exports.</summary>
internal static class UnifiedTextListAuthoring
{
    internal const int OutputLimit = 8 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static (string Name, byte[] Bytes)[] Build(JsonObject request)
    {
        Fields(request, "schemaVersion fileBase textLibrary languages defaultLanguage texts lists");
        if (Text(request, "schemaVersion") != "unified-text-list-author-request.v1") throw Error("Unsupported text-list authoring schema.");
        string fileBase = Text(request, "fileBase", 120), library = Identifier(request, "textLibrary");
        if (PackageStorage.Relative(fileBase) != fileBase || fileBase.Contains('/') || fileBase.Contains('.')) throw Error("fileBase must be one extension-free filename.");
        var languages = Array(request, "languages", 32).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Error("Expected language string.")).ToArray();
        if (languages.Length == 0 || languages.Distinct(StringComparer.OrdinalIgnoreCase).Count() != languages.Length ||
            languages.Any(s => s.Length > 35 || !Regex.IsMatch(s, "\\A[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*\\z", RegexOptions.CultureInvariant)))
            throw Error("Supply 1..32 distinct explicit language tags; installed project languages are not inferred.");
        string defaultLanguage = Text(request, "defaultLanguage", 35);
        if (!languages.Contains(defaultLanguage, StringComparer.Ordinal)) throw Error("Default language must exactly select a declared language.");
        var texts = Array(request, "texts", 4096); var lists = Array(request, "lists", 128);
        if (texts.Count == 0 || lists.Count == 0) throw Error("At least one text and list are required.");
        var textIds = new HashSet<string>(StringComparer.Ordinal); var usedTexts = new HashSet<string>(StringComparer.Ordinal);
        var translation = new StringBuilder("#Version: 2.0\nTextLibraries:\n  ").Append(Quote(library)).Append(":\n    Type: Text\n    Languages:\n");
        foreach (string language in languages) translation.Append("      - ").Append(Quote(language)).Append('\n');
        translation.Append("    DefaultLanguage: ").Append(Quote(defaultLanguage)).Append("\n    Entries:\n");
        foreach (var item in texts)
        {
            var text = Object(item); Fields(text, "id translations"); string id = Identifier(text, "id");
            if (!textIds.Add(id)) throw Error("Duplicate text identifier.");
            var values = Object(text["translations"]);
            if (values.Count != languages.Length || languages.Any(l => !values.ContainsKey(l))) throw Error("Every text needs exactly one translation for every declared language.");
            translation.Append("      ").Append(Quote(id)).Append(":\n        Text:\n");
            foreach (string language in languages)
            {
                if (values[language] is not JsonValue v || !v.TryGetValue<string>(out var value) || value.Length > 16384) throw Error("Translation must be a string of at most 16384 UTF-16 units; empty text is explicit.");
                translation.Append("          - ").Append(Quote(value)).Append('\n');
            }
            Bound(translation);
        }
        var configuration = new StringBuilder("#Version: 2.0\nTextListContainers:\n  TextList:\n    ResourceListType: TextList\n    ResourceLists:\n");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int entries = 0;
        foreach (var item in lists)
        {
            var list = Object(item); Fields(list, "name rangeType entries"); string name = Text(list, "name", 256), range = Text(list, "rangeType", 16);
            if (name.Any(char.IsControl) || !names.Add(name)) throw Error("List names must be distinct, including case variants, and contain no controls.");
            if (range is not ("Decimal" or "BitNumber" or "Bool")) throw Error("Unsupported range type.");
            var rows = Array(list, "entries", 4096); entries += rows.Count;
            if (rows.Count == 0 || entries > 4096) throw Error("Supply 1..4096 entries per list and at most 4096 across the request.");
            configuration.Append("      ").Append(Quote(name)).Append(":\n        RangeType: ").Append(range).Append("\n        Entries:\n");
            var entryIds = new HashSet<string>(StringComparer.Ordinal); var intervals = new List<(long From, long To)>(); int defaults = 0;
            foreach (var row in rows)
            {
                var entry = Object(row); Fields(entry, "id type fromValue toValue isDefault textId");
                string id = Identifier(entry, "id"), type = Text(entry, "type", 16), text = Identifier(entry, "textId");
                if (!entryIds.Add(id)) throw Error("Entry identifiers must be unique within a list.");
                // V21 imports an unresolved reference as empty texts and still reports success (local.hmi.text-list-import.20261029).
                if (!textIds.Contains(text)) throw new BridgeException("HmiTextListUnresolvedText", $"List {name}, entry {id}: text {text} is not declared.");
                usedTexts.Add(text);
                long? from = Number(entry, "fromValue"), to = Number(entry, "toValue");
                bool valid = type switch { "SingleValue" => from.HasValue && from == to, "Range" => from.HasValue && to.HasValue && from <= to,
                    "From" => from.HasValue && !to.HasValue, "To" => !from.HasValue && to.HasValue, _ => false };
                if (!valid) throw Error("Entry bounds contradict SingleValue/Range/From/To; unused bounds must be null.");
                if (range != "Decimal" && (type != "SingleValue" || from < 0 || (range == "Bool" && from > 1))) throw Error("Bool and BitNumber use nonnegative SingleValue entries; Bool selects 0 or 1.");
                var interval = (From: from ?? long.MinValue, To: to ?? long.MaxValue);
                if (intervals.Any(p => interval.From <= p.To && p.From <= interval.To)) throw Error("Overlapping entry values/ranges require a separate reviewed policy; no precedence is invented.");
                intervals.Add(interval);
                bool isDefault = Boolean(entry, "isDefault"); if (isDefault && ++defaults > 1) throw Error("Only one default entry per list is allowed.");
                configuration.Append("          ").Append(Quote(id)).Append(":\n            Type: ").Append(type).Append("\n            Value: ").Append((from ?? to)!.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (from.HasValue) configuration.Append("            FromValue: ").Append(from.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                if (to.HasValue) configuration.Append("            ToValue: ").Append(to.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                configuration.Append("            IsDefaultEntry: ").Append(isDefault ? "true" : "false").Append("\n            Text: ").Append(Quote(library + "." + text)).Append('\n');
                Bound(configuration);
            }
        }
        if (!usedTexts.SetEquals(textIds)) throw Error("Every authored text must be referenced; unused resources are not silently published.");
        return [(fileBase + ".hmi.yml", Utf8.GetBytes(configuration.ToString())), (fileBase + ".TextLibrary.hmi.yml", Utf8.GetBytes(translation.ToString()))];
    }

    // List-name punctuation V21 imported unchanged (runs q/tl2, q/tl3); letters, digits and inner spaces are also kept.
    private const string NamePunctuation = "!&'()+,-;<=>@\\^_{|}";
    internal const int MaxNativeListName = 128;

    /// <summary>Refuse new candidates whose content V21's Import reports as imported but changes
    /// (local.hmi.text-list-import.20261029). Call after Build and only before publishing new records; retained records keep their semantics.
    /// Names follow an allowlist because untested characters may also be dropped.</summary>
    internal static void RequireNativeImportable(JsonObject intent)
    {
        var texts = intent["texts"]!.AsArray().ToDictionary(t => (string)t!["id"]!, t => t!["translations"]!.AsObject(), StringComparer.Ordinal);
        foreach (var node in intent["lists"]!.AsArray())
        {
            var list = node!.AsObject(); string name = (string)list["name"]!;
            if (name.Length > MaxNativeListName || name.Trim() != name || !name.All(c => char.IsLetterOrDigit(c) || c == ' ' || NamePunctuation.Contains(c)))
                throw new BridgeException("HmiTextListNameUnsupported", $"List {name}: V21 imports only names of at most {MaxNativeListName} letters, digits, inner spaces and {NamePunctuation}; it trims, renames or drops others.");
            foreach (var row in list["entries"]!.AsArray())
            {
                var entry = row!.AsObject(); string where = $"List {name}, entry {(string)entry["id"]!}";
                foreach (string bound in new[] { "fromValue", "toValue" })
                    if (Number(entry, bound) is long value && value is < int.MinValue or > int.MaxValue)
                        throw new BridgeException("HmiTextListValueOutOfRange", $"{where}: {bound} {value} is outside signed 32-bit; V21 imports it as 0.");
                if (Boolean(entry, "isDefault"))
                {
                    if ((string?)list["rangeType"] == "Bool") throw new BridgeException("HmiTextListBoolDefaultEntry", $"{where}: V21 drops a default entry from a Bool list.");
                    if ((string?)entry["type"] != "SingleValue" || Number(entry, "fromValue") != 0)
                        throw new BridgeException("HmiTextListDefaultEntryValue", $"{where}: V21 imports a default entry as SingleValue 0; author it that way.");
                }
                string textId = (string)entry["textId"]!;
                foreach (var (language, translation) in texts[textId])
                    if (((string)translation!).Any(c => char.IsControl(c) && c is not ('\t' or '\n')))
                        throw new BridgeException("HmiTextListTextUnsupported", $"{where}: text {textId} ({language}) contains a control character other than tab or LF, which V21 empties, replaces or turns into LF.");
            }
        }
    }

    private static long? Number(JsonObject row, string name) => row[name] is null ? null : row[name] is JsonValue v && v.TryGetValue<long>(out var value) ? value :
        row[name] is JsonValue integer && integer.TryGetValue<int>(out var narrow) ? narrow : throw Error("Bounds must be exact signed 64-bit integers or null; no numeric coercion.");
    private static string Identifier(JsonObject row, string name)
    {
        string value = Text(row, name, 128);
        if (!Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)) throw Error("Text-library and entry identifiers use ASCII letters, digits and underscore, starting with a letter or underscore.");
        return value;
    }
    private static void Bound(StringBuilder text) { if (text.Length > OutputLimit / 3) throw Error("Authored file exceeds the conservative 8 MiB UTF-8 bound."); }
    private static string Quote(string value)
    {
        _ = Utf8.GetByteCount(value);
        var result = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            int code = rune.Value;
            if (code is 0xfffe or 0xffff) throw Error("Text contains a non-YAML Unicode scalar.");
            if (code is 34 or 92) result.Append('\\').Append((char)code);
            else if (code < 32 || code is >= 127 and <= 159 || code is 0x2028 or 0x2029) result.Append("\\u").Append(code.ToString("x4", CultureInfo.InvariantCulture));
            else result.Append(rune.ToString());
        }
        return result.Append('"').ToString();
    }
}
