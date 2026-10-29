using System.Text.Json;

namespace TiaMcp.Core;

/// <summary>Versioned product coverage. Historical fixture evidence is never a target capability decision.</summary>
public sealed class FeatureCatalog
{
    private readonly JsonElement _catalog;
    private readonly JsonElement[] _features;

    public FeatureCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        _catalog = document.RootElement.Clone();
        Validate(_catalog);
        _features = _catalog.GetProperty("features").EnumerateArray().ToArray();
    }

    public static FeatureCatalog LoadBundled()
    {
        using var stream = typeof(FeatureCatalog).Assembly.GetManifestResourceStream("TiaMcp.FeatureCatalog.v1.json")
            ?? throw new InvalidDataException("The bundled feature catalog is missing.");
        using var reader = new StreamReader(stream);
        return new FeatureCatalog(reader.ReadToEnd());
    }

    public string Version => _catalog.GetProperty("catalogVersion").GetString()!;

    public bool HasChangeKind(string kind) => _features.Any(f => f.TryGetProperty("changeKind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == kind);

    public object Query(string? featureId = null, string? domain = null, string? implementationStatus = null,
        int offset = 0, int maxItems = 100)
    {
        if (offset < 0 || maxItems is < 1 or > 200)
            throw new BridgeException("InvalidRequest", "offset must be nonnegative; maxItems must be between 1 and 200.");
        if (implementationStatus is not null && implementationStatus is not ("implemented" or "partial" or "not_implemented"))
            throw new BridgeException("InvalidRequest", "implementationStatus must be implemented, partial or not_implemented.");
        if (featureId is not null && !_features.Any(f => f.GetProperty("featureId").GetString() == featureId))
            throw new BridgeException("FeatureNotFound", "The catalog contains no feature with this exact ID.");
        var matches = _features.Where(f => (featureId is null || f.GetProperty("featureId").GetString() == featureId)
            && (domain is null || f.GetProperty("domain").GetString() == domain)
            && (implementationStatus is null || f.GetProperty("implementation").GetProperty("status").GetString() == implementationStatus)).ToArray();
        var page = matches.Skip(offset).Take(maxItems).ToArray();
        var evidenceIds = page.SelectMany(f => f.GetProperty("qualification").GetProperty("evidenceIds").EnumerateArray())
            .Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal);
        var criteriaIds = page.SelectMany(f => f.GetProperty("acceptanceCriteria").EnumerateArray())
            .Select(v => v.GetString()).ToHashSet(StringComparer.Ordinal);
        return new
        {
            schemaVersion = 1, catalogVersion = _catalog.GetProperty("catalogVersion").GetString(),
            origin = "Bundled TIA MCP feature contracts and recorded fixture evidence",
            qualificationScope = _catalog.GetProperty("qualificationScope").GetString(),
            targetAvailabilityProbed = false,
            features = page,
            evidence = _catalog.GetProperty("evidence").EnumerateArray().Where(e => evidenceIds.Contains(e.GetProperty("id").GetString())).ToArray(),
            criteria = _catalog.GetProperty("criteria").EnumerateArray().Where(c => criteriaIds.Contains(c.GetProperty("id").GetString())).ToArray(),
            pagination = new { offset, returned = page.Length, total = matches.Length,
                nextOffset = offset + page.Length < matches.Length ? (int?)(offset + page.Length) : null,
                complete = offset + page.Length >= matches.Length }
        };
    }

    private static void Validate(JsonElement catalog)
    {
        if (catalog.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported feature catalog schema version.");
        var criteria = Unique(catalog.GetProperty("criteria"), "id");
        var evidence = Unique(catalog.GetProperty("evidence"), "id");
        var evidenceStates = catalog.GetProperty("evidence").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("status").GetString(), StringComparer.Ordinal);
        if (evidenceStates.Values.Any(status => status is not ("passed" or "failed" or "blocked" or "skipped")))
            throw new InvalidDataException("Invalid evidence outcome.");
        Unique(catalog.GetProperty("features"), "featureId");
        foreach (var feature in catalog.GetProperty("features").EnumerateArray())
        {
            var implementation = feature.GetProperty("implementation").GetProperty("status").GetString();
            if (implementation is not ("implemented" or "partial" or "not_implemented"))
                throw new InvalidDataException("Invalid implementation status.");
            var qualification = feature.GetProperty("qualification");
            var state = qualification.GetProperty("status").GetString();
            if (state is not ("verified" or "unverified" or "failed")) throw new InvalidDataException("Invalid qualification status.");
            var references = qualification.GetProperty("evidenceIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
            if ((state is "verified" or "failed") && references.Length == 0)
                throw new InvalidDataException("Observed qualification requires evidence.");
            if (references.Any(id => !evidence.Contains(id))) throw new InvalidDataException("Unknown evidence reference.");
            if (state == "verified" && references.Any(id => evidenceStates[id] != "passed"))
                throw new InvalidDataException("Verified qualification requires passed evidence only.");
            if (state == "failed" && !references.Any(id => evidenceStates[id] == "failed"))
                throw new InvalidDataException("Failed qualification requires a recorded failure.");
            if (feature.GetProperty("acceptanceCriteria").EnumerateArray().Any(v => !criteria.Contains(v.GetString()!)))
                throw new InvalidDataException("Unknown acceptance criterion.");
            if (implementation == "not_implemented" && (feature.GetProperty("mcpTools").GetArrayLength() != 0 || state == "verified"))
                throw new InvalidDataException("An unimplemented feature cannot expose tools or claim verified support.");
        }
    }

    private static HashSet<string> Unique(JsonElement items, string property)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var value = item.GetProperty(property).GetString();
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                throw new InvalidDataException("Catalog identifiers must be nonempty and unique: " + property);
        }
        return seen;
    }
}
