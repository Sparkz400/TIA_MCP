using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Security;

/// <summary>Compare explicitly supplied grant graphs; never infer native or effective authorization.</summary>
public static class AccessReview
{
    public const int InputLimit = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Input must contain 1 byte..2 MiB.");
        string text = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in element.EnumerateObject())
                { _ = Utf8.GetByteCount(p.Name); if (!names.Add(p.Name)) throw Error("Duplicate JSON field."); Unique(p.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
        }
        Unique(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an object.");
    }

    public static JsonObject Build(string reviewId, JsonObject review, JsonObject request)
    {
        Fields(request, "schemaVersion reviewId owner identity subjectId afterKind requiredRights allowedRights before after");
        if (Text(request, "schemaVersion") != "access-review-request.v1" || Text(request, "reviewId") != reviewId) throw Error("Select this exact saved review and schema.");
        var owner = Object(request["owner"]); Fields(owner, "nodeId objectPath");
        string ownerId = Text(owner, "nodeId"), ownerPath = Text(owner, "objectPath");
        var matches = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == ownerPath).ToArray();
        if (matches.Length != 1 || (string?)matches[0]!["nodeId"] != ownerId || (string?)matches[0]!["origin"] == "virtual") throw Error("Owner must be an exact nonvirtual captured node and path.");
        var identity = Object(request["identity"]); Fields(identity, "domain sourceId version securityMode unknownReason");
        string domain = Text(identity, "domain"); Text(identity, "sourceId");
        if (domain is not ("projectUmac" or "cpuRuntime" or "legacyWeb" or "siwarexWeb" or "opcUa")) throw Error("Unknown security domain.");
        if ((string?)matches[0]!["kind"] != (domain == "projectUmac" ? "project" : "hardware")) throw Error("Project UMAC requires a project owner; device domains require hardware.");
        foreach (string field in new[] { "version", "securityMode", "unknownReason" }) if (identity[field] is not null) Text(identity, field);
        if ((identity["version"] is null || identity["securityMode"] is null) && identity["unknownReason"] is null) throw Error("Unknown version/security mode requires a reason.");
        string subject = Text(request, "subjectId"), afterKind = Text(request, "afterKind");
        if (afterKind is not ("observation" or "proposal")) throw Error("afterKind must be observation or proposal.");
        var required = Rights(request, "requiredRights"); var allowed = Rights(request, "allowedRights");
        if (!required.IsSubsetOf(allowed)) throw Error("Every required right must be explicitly allowed, with the same scope.");
        var observations = new JsonArray(); var assessments = new JsonArray();
        var before = Snapshot(Object(request["before"]), "before"); var after = Snapshot(Object(request["after"]), "after");
        var changes = new JsonArray();
        foreach (string id in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(id, out var old); after.TryGetValue(id, out var next);
            var fields = new JsonArray();
            if (old is not null && next is not null)
                foreach (string field in new[] { "kind", "enabled", "rightsComplete", "assignmentsComplete", "rights", "inherits" })
                    if (!JsonNode.DeepEquals(old[field], next[field])) fields.Add(field);
            changes.Add(new JsonObject { ["id"] = id, ["status"] = old is null ? "onlyAfter" : next is null ? "onlyBefore" : fields.Count == 0 ? "unchanged" : "changed", ["changedFields"] = fields });
        }
        return new JsonObject { ["schemaVersion"] = "access-review.v1", ["reviewId"] = reviewId, ["owner"] = owner.DeepClone(), ["identity"] = identity.DeepClone(),
            ["subjectId"] = subject, ["afterKind"] = afterKind, ["observations"] = observations, ["assessments"] = assessments, ["changes"] = changes,
            ["summary"] = new JsonObject { ["beforeCount"] = before.Count, ["afterCount"] = after.Count, ["changedCount"] = changes.Count(c => (string?)c!["status"] != "unchanged"),
                ["beforeUnexpectedCount"] = assessments[0]!["outsideAllowedRights"]!.AsArray().Count, ["afterUnexpectedCount"] = assessments[1]!["outsideAllowedRights"]!.AsArray().Count,
                ["beforeRequiredNotObservedCount"] = assessments[0]!["requiredNotObserved"]!.AsArray().Count, ["afterRequiredNotObservedCount"] = assessments[1]!["requiredNotObserved"]!.AsArray().Count },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["effectiveRightsVerified"] = false, ["minimumRightsVerified"] = false, ["lockoutSafetyVerified"] = false,
            ["limits"] = "Public supplied grant graph only; exact IDs/scopes never translate between security domains. Potential rights include disabled or unknown-state references and do not implement native deny, inheritance or activation semantics. Missing observations are not proof of revocation. Caller-defined required/allowed rights are not an authenticated policy. No credentials, private keys, native edit, certificate operation or app deployment. Unknown fields are refused; this is not a secret-content detector. Use only public metadata." };

        Dictionary<string, JsonObject> Snapshot(JsonObject snapshot, string phase)
        {
            Fields(snapshot, "identity entitiesComplete entities");
            if (!JsonNode.DeepEquals(identity, snapshot["identity"])) throw Error("Each phase must retain the exact same security domain, source, version and mode declaration.");
            bool complete = Boolean(snapshot, "entitiesComplete"); var entities = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in Array(snapshot, "entities", 512))
            {
                var row = Object(item); Fields(row, "id kind enabled rightsComplete assignmentsComplete rights inherits");
                string id = Text(row, "id"); if (!entities.TryAdd(id, row)) throw Error("Duplicate exact entity ID.");
                if (Text(row, "kind") is not ("user" or "group" or "role")) throw Error("Unknown entity kind.");
                if (row["enabled"] is not null) Boolean(row, "enabled");
                Boolean(row, "rightsComplete"); Boolean(row, "assignmentsComplete"); Rights(row, "rights");
                var links = new HashSet<string>(StringComparer.Ordinal);
                foreach (var link in Array(row, "inherits", 64)) if (!links.Add(Value(link))) throw Error("Duplicate inheritance reference.");
                observations.Add(new JsonObject { ["phase"] = phase, ["entity"] = row.DeepClone() });
            }
            var rights = new HashSet<(string Id, string Scope)>(); var visited = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            var gaps = new JsonArray(); var paths = new JsonArray();
            if (!complete) Gap(subject, "inventoryIncomplete");
            void Gap(string id, string reason) { complete = false; gaps.Add(new JsonObject { ["id"] = id, ["reason"] = reason }); }
            void Visit(string id, int depth)
            {
                if (active.Contains(id)) { Gap(id, "cycle"); return; }
                if (visited.Contains(id)) return;
                if (depth > 64) { Gap(id, "depthLimit"); return; }
                visited.Add(id);
                if (!entities.TryGetValue(id, out var row)) { Gap(id, "missingEntity"); return; }
                active.Add(id); paths.Add(id);
                if (row["enabled"] is null || !Boolean(row, "enabled")) Gap(id, row["enabled"] is null ? "activationUnknown" : "disabledDeclaration");
                if (!Boolean(row, "rightsComplete")) Gap(id, "rightsIncomplete");
                if (!Boolean(row, "assignmentsComplete")) Gap(id, "assignmentsIncomplete");
                rights.UnionWith(Rights(row, "rights"));
                foreach (var link in row["inherits"]!.AsArray()) Visit(Value(link), depth + 1);
                active.Remove(id);
            }
            Visit(subject, 0);
            assessments.Add(new JsonObject { ["phase"] = phase, ["subjectId"] = subject, ["visitedIds"] = paths,
                ["suppliedClosureComplete"] = complete, ["gaps"] = gaps, ["potentialRights"] = RightRows(rights),
                ["outsideAllowedRights"] = RightRows(rights.Except(allowed)), ["requiredNotObserved"] = RightRows(required.Except(rights)),
                ["effectiveRightsVerified"] = false, ["minimumRightsVerified"] = false });
            return entities;
        }
    }
    private static HashSet<(string Id, string Scope)> Rights(JsonObject row, string field)
    {
        var result = new HashSet<(string Id, string Scope)>();
        foreach (var item in Array(row, field, 64))
        { var right = Object(item); Fields(right, "id scope"); if (!result.Add((Text(right, "id"), Text(right, "scope")))) throw Error("Duplicate exact right and scope."); }
        return result;
    }
    private static JsonArray RightRows(IEnumerable<(string Id, string Scope)> rights) => new(rights.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Scope, StringComparer.Ordinal)
        .Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["scope"] = r.Scope }).ToArray());
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw Error("Expected an object.");
    private static void Fields(JsonObject value, string fields)
    { var keys = fields.Split(' '); if (value.Count != keys.Length || keys.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown fields in public access metadata."); }
    private static JsonArray Array(JsonObject value, string key, int max) => value[key] is JsonArray rows && rows.Count <= max ? rows : throw Error("Missing or oversized collection.");
    private static bool Boolean(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out bool b) ? b : throw Error("Expected Boolean.");
    private static string Text(JsonObject value, string key) => Value(value[key]);
    private static string Value(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out string? text) || string.IsNullOrWhiteSpace(text) || text.Length > 512 || text.Any(char.IsControl)) throw Error("Expected bounded public identifier.");
        _ = Utf8.GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidAccessReview", message);
}
