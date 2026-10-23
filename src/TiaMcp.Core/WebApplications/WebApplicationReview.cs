using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.WebApplications;

/// <summary>Supplied application configuration and exact ZIP file comparison, never native execution.</summary>
public static class WebApplicationReview
{
    public const int InputLimit = 1024 * 1024;
    public const int ArchiveLimit = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Request must contain 1 byte..1 MiB.");
        try
        {
            string text = Utf8.GetString(bytes); using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            void Unique(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { _ = Utf8.GetByteCount(p.Name); if (!seen.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); } }
                else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Unique(item);
                else if (e.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(e.GetString()!);
            }
            Unique(doc.RootElement); return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected a request object.");
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or EncoderFallbackException) { throw Error("Invalid UTF-8 JSON request."); }
    }
    public static string ReviewId(JsonObject request) => Text(request, "reviewId");
    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request, Func<string, JsonObject, JsonObject> inspect)
    {
        Fields(request, "schemaVersion reviewId owner selectedApplicationId afterKind before after");
        if (Text(request, "schemaVersion") != "web-application-review-request.v1" || ReviewId(request) != reviewId) throw Error("Select this exact saved review and schema.");
        string selected = Text(request, "selectedApplicationId"), afterKind = Text(request, "afterKind");
        if (afterKind is not ("observation" or "proposal")) throw Error("afterKind must be observation or proposal.");
        var owner = Object(request["owner"]); Fields(owner, "nodeId objectPath"); string path = Text(owner, "objectPath"), nodeId = Text(owner, "nodeId");
        var matches = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == path).ToArray();
        if (matches.Length != 1 || (string?)matches[0]!["nodeId"] != nodeId || (string?)matches[0]!["origin"] == "virtual" || (string?)matches[0]!["kind"] is not ("hardware" or "project")) throw Error("Select an exact nonvirtual captured project or hardware owner.");
        var before = Phase(Object(request["before"]), "before", selected); var after = Phase(Object(request["after"]), "after", selected);
        foreach (var observation in new[] { before, after })
        {
            var archive = observation["archive"]!.AsObject();
            if (archive["uploadRef"] is not null) observation["content"] = inspect((string)observation["phase"]!, archive);
        }
        var configurationChanges = new JsonArray();
        foreach (string field in new[] { "webEnabled", "systemPagesEnabled", "applicationInventoryComplete", "generationProperties" })
            if (!JsonNode.DeepEquals(before["configuration"]![field], after["configuration"]![field])) configurationChanges.Add(new JsonObject { ["field"] = field, ["before"] = before["configuration"]![field]?.DeepClone(), ["after"] = after["configuration"]![field]?.DeepClone() });
        var applications = Changes(before["configuration"]!["applications"]!.AsArray(), after["configuration"]!["applications"]!.AsArray(), "applicationId", "notObservedBefore", "notObservedAfter");
        bool comparable = before["content"] is JsonObject && after["content"] is JsonObject && JsonNode.DeepEquals(before["archive"]!["purpose"], after["archive"]!["purpose"]);
        var files = comparable ? Changes(before["content"]!["files"]!.AsArray(), after["content"]!["files"]!.AsArray(), "relativePath", "added", "removed") : new JsonArray();
        var archiveChanges = new JsonArray();
        foreach (string field in new[] { "sha256", "rootRelative", "defaultPage", "purpose", "unavailableReason" })
            if (!JsonNode.DeepEquals(before["archive"]![field], after["archive"]![field])) archiveChanges.Add(field);
        return new JsonObject { ["schemaVersion"] = "web-application-review.v1", ["reviewId"] = reviewId, ["owner"] = owner.DeepClone(), ["selectedApplicationId"] = selected, ["afterKind"] = afterKind,
            ["observations"] = new JsonArray(before, after), ["configurationChanges"] = configurationChanges, ["applicationChanges"] = applications, ["fileChanges"] = files, ["archiveChanges"] = archiveChanges,
            ["summary"] = new JsonObject { ["filesComparable"] = comparable, ["fileComparisonState"] = comparable ? "comparedSuppliedRoots" : before["content"] is null || after["content"] is null ? "contentUnavailable" : "purposeChanged", ["changedFiles"] = files.Count(x => (string?)x?["status"] != "unchanged"), ["changedApplications"] = applications.Count(x => (string?)x?["status"] != "unchanged"), ["configurationChanges"] = configurationChanges.Count,
                ["unavailableArchives"] = new[] { before, after }.Count(x => x["content"] is null), ["findings"] = before["findings"]!.AsArray().Count + after["findings"]!.AsArray().Count },
            ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["associationVerified"] = false, ["siemensCompatibilityVerified"] = false, ["generatedBlocksVerified"] = false, ["deploymentVerified"] = false, ["endpointVerified"] = false,
            ["limits"] = "Supplied configuration and public archive bytes only. Application IDs are caller-declared within the exact saved owner; missing rows do not authorize deletion. File add/remove describes the selected archive roots, not deployed files. Generation properties are opaque declarations, not validated target settings. No script execution, browser rendering, network, block generation, import, native configuration or deployment. ZIP/script content is retained untrusted and never certified safe." };
    }
    private static JsonObject Phase(JsonObject phase, string name, string selected)
    {
        Fields(phase, "configuration archive"); var config = Object(phase["configuration"]); Fields(config, "webEnabled systemPagesEnabled applicationInventoryComplete applications generationProperties");
        Boolean(config, "webEnabled", true); Boolean(config, "systemPagesEnabled", true); Boolean(config, "applicationInventoryComplete", false);
        var apps = Array(config["applications"], 512); var seen = new HashSet<string>(StringComparer.Ordinal); var normalized = new JsonArray();
        foreach (var node in apps)
        {
            var app = Object(node); Fields(app, "applicationId name applicationType isDefault properties");
            if (!seen.Add(Text(app, "applicationId"))) throw Error("Duplicate application identity.");
            Text(app, "name"); if (app["applicationType"] is not null) Text(app, "applicationType"); Boolean(app, "isDefault", true);
            var copy = app.DeepClone().AsObject(); copy["properties"] = Properties(app["properties"]); normalized.Add(copy);
        }
        var configuration = config.DeepClone().AsObject(); configuration["applications"] = new JsonArray(normalized.OrderBy(x => (string)x!["applicationId"]!, StringComparer.Ordinal).Select(x => x!.DeepClone()).ToArray()); configuration["generationProperties"] = Properties(config["generationProperties"]);
        var archive = Object(phase["archive"]); Fields(archive, "uploadRef sha256 rootRelative defaultPage purpose unavailableReason");
        if (Text(archive, "purpose") is not ("webApplicationArchive" or "userDefinedPageSource")) throw Error("Select an explicit archive purpose.");
        if (archive["uploadRef"] is null)
        { foreach (string field in new[] { "sha256", "rootRelative", "defaultPage" }) if (archive[field] is not null) throw Error("Unavailable archive selectors must be null."); Text(archive, "unavailableReason"); }
        else
        {
            Text(archive, "uploadRef"); string hash = Text(archive, "sha256"); if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))) throw Error("Archive SHA-256 must be lowercase hex.");
            if (archive["unavailableReason"] is not null || !seen.Contains(selected)) throw Error("Supplied archive requires an observed selected application and no unavailable reason.");
            Relative(archive, "rootRelative", true); Relative(archive, "defaultPage", false);
        }
        var findings = new JsonArray();
        if (!(bool)config["applicationInventoryComplete"]!) findings.Add("applicationInventoryIncomplete");
        if (!seen.Contains(selected)) findings.Add("selectedApplicationNotObserved");
        if (apps.Count(x => (bool?)x?["isDefault"] == true) > 1) findings.Add("multipleDeclaredDefaultApplications");
        if (apps.Any(x => x?["isDefault"] is null)) findings.Add("defaultApplicationStateUnknown");
        if (config["webEnabled"] is null || config["systemPagesEnabled"] is null) findings.Add("webSettingsUnknown");
        if ((bool?)config["webEnabled"] == false && ((bool?)config["systemPagesEnabled"] == true || apps.Any(x => (bool?)x?["isDefault"] == true))) findings.Add("enabledPagesWithWebDisabled");
        return new JsonObject { ["phase"] = name, ["configuration"] = configuration, ["archive"] = archive.DeepClone(), ["content"] = null, ["findings"] = findings };
    }
    private static JsonArray Properties(JsonNode? value)
    {
        var source = Array(value, 128); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in source)
        {
            var p = Object(node); Fields(p, "name value"); if (!seen.Add(Text(p, "name"))) throw Error("Duplicate property name.");
            if (p["value"] is JsonObject or JsonArray) throw Error("Public property values must be scalar or null.");
            if (p["value"] is JsonValue v && v.TryGetValue<string>(out var text) && (text.Length > 8192 || text.Any(char.IsControl))) throw Error("Public property text exceeds its bound.");
        }
        return new JsonArray(source.OrderBy(x => (string)x!["name"]!, StringComparer.Ordinal).Select(x => x!.DeepClone()).ToArray());
    }
    private static JsonArray Changes(JsonArray before, JsonArray after, string key, string added, string removed)
    {
        var left = before.ToDictionary(x => (string)x![key]!, StringComparer.Ordinal); var right = after.ToDictionary(x => (string)x![key]!, StringComparer.Ordinal); var result = new JsonArray();
        foreach (string id in left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        { left.TryGetValue(id, out var b); right.TryGetValue(id, out var a); result.Add(new JsonObject { [key] = id, ["status"] = b is null ? added : a is null ? removed : JsonNode.DeepEquals(b, a) ? "unchanged" : "changed", ["before"] = b?.DeepClone(), ["after"] = a?.DeepClone() }); }
        return result;
    }
    internal static string Relative(JsonObject value, string key, bool allowEmpty)
    {
        if (allowEmpty && value[key] is JsonValue v && v.TryGetValue<string>(out var text) && text == "") return "";
        string path = Text(value, key); if (PackageStorage.Relative(path) != path) throw Error("Use an exact forward-slash relative archive path."); return path;
    }
    private static void Boolean(JsonObject o, string key, bool nullable)
    { if (nullable && o[key] is null) return; if (o[key] is not JsonValue v || !v.TryGetValue<bool>(out _)) throw Error("Expected an explicit boolean or permitted unknown null."); }
    private static JsonArray Array(JsonNode? value, int max) => value is JsonArray a && a.Count <= max ? a : throw Error("Expected a bounded array.");
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Error("Expected an object.");
    private static void Fields(JsonObject o, string fields) { string[] keys = fields.Split(' '); if (o.Count != keys.Length || keys.Any(k => !o.ContainsKey(k))) throw Error("Missing or unknown web-application fields."); }
    private static string Text(JsonObject o, string key) { if (o[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s) || s.Length > 8192 || s.Any(char.IsControl)) throw Error("Expected bounded public text."); _ = Utf8.GetByteCount(s); return s; }
    private static BridgeException Error(string message) => new("InvalidWebApplicationReview", message);
}
