using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;
using TiaMcp.Core;

namespace TiaMcp.Translations;

public sealed record CellLocation(string Part, string Cell);
public sealed record TranslationCell(string Culture, CellLocation Location, bool AllowClear = false,
    bool AllowCreateMissing = false, int? MaxUtf16Length = null);
public sealed record TranslationRow(string NativeKey, string Category, CellLocation KeyCell,
    CellLocation SourceCell, TranslationCell[] Targets, string TokenGrammar = "none",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AlarmTextPolicy = null);

/// <summary>Explicit offline cell map. Neither kind grants Siemens native layout acceptance.</summary>
public sealed record WorkbookProfile(string Id, string Kind, string SourceCulture,
    TranslationRow[] Rows, bool AllowSharedStringToInline = false);

public sealed record WorkbookLimits(long MaximumCompressedBytes = 100 * 1024 * 1024,
    long MaximumExpandedBytes = 512 * 1024 * 1024, int MaximumEntries = 10000,
    int MaximumCells = 1000000, int MaximumXmlDepth = 128, int MaximumTextLength = 1048576,
    int MaximumXmlBytes = 32 * 1024 * 1024, int MaximumXmlNodes = 1000000);

public enum TranslationEditKind { Keep, Set, Clear }
public sealed record TranslationEdit(string NativeKey, string Culture, TranslationEditKind Kind,
    string? Value, string ExpectedSourceSha256, string ExpectedTargetSha256,
    bool Approved = false, string? Reviewer = null, string? Rationale = null);
public sealed record GlossaryRule(string SourceTerm, string RequiredTargetTerm, string TargetCulture,
    string? Category = null, bool CaseSensitive = true);
public sealed record TranslationReviewOptions(bool UpdateSourceLanguage = false,
    string? GlossaryId = null, GlossaryRule[]? Glossary = null);

public sealed record TextCellState(string State, string? Text, bool RichText = false)
{
    // Hash the state as well as the value: absent, empty and literal whitespace differ.
    [JsonIgnore]
    public string Sha256 => TranslationJson.HashJson(this);
}

public static class TranslationJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!;
    public static string HashJson<T>(T value) => Core.Engineering.CanonicalJson.Sha256(Node(value));
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static T Parse<T>(string json)
    {
        Require(json.Length <= 16 * 1024 * 1024, "TranslationContractLimit", "JSON contract exceeds 16 Mi characters.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        CheckUnique(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new BridgeException("InvalidTranslationContract", "Null contract.");
    }
    private static void CheckUnique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new BridgeException("DuplicateJsonKey", "Duplicate contract property.");
                CheckUnique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckUnique(child);
    }
    internal static T Clone<T>(T value) => Parse<T>(JsonSerializer.Serialize(value, Options));
    internal static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new BridgeException(code, message); }
}
