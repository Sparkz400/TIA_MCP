using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Drives;

/// <summary>Supplied drive parameter evidence; no native writeability or target compatibility certification.</summary>
public static class DriveProfileReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Input must contain 1 byte..4 MiB.");
        string text = Utf8.GetString(bytes);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
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
        if (request["schemaVersion"] is JsonValue version && version.TryGetValue<string>(out string? versionText) && versionText == "drive-profile-review-request.v2")
        {
            var baseline = request.DeepClone().AsObject(); baseline["schemaVersion"] = "drive-profile-review-request.v1";
            foreach (string phase in new[] { "before", "after" })
            {
                var snapshot = Object(baseline[phase]);
                Fields(snapshot, "identity valueDomain parametersComplete parameters telegramsComplete telegrams");
                snapshot.Remove("telegramsComplete"); snapshot.Remove("telegrams");
            }
            var result = Build(reviewId, review, baseline);
            DriveTelegramReview.Add(result, review, request);
            return result;
        }
        Fields(request, "schemaVersion reviewId hardwarePath afterKind before after");
        if (Text(request, "schemaVersion") != "drive-profile-review-request.v1" || Text(request, "reviewId") != reviewId)
            throw Error("Request must name this exact saved review and schema.");
        string hardware = Text(request, "hardwarePath"), afterKind = Text(request, "afterKind");
        if (afterKind is not ("observation" or "proposal")) throw Error("afterKind must be observation or proposal.");
        var captured = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == hardware && (string?)n?["origin"] != "virtual").ToArray();
        if (captured.Length != 1 || (string?)captured[0]!["kind"] != "hardware") throw Error("Select one exact captured hardware object.");
        var b = Object(request["before"]); var a = Object(request["after"]);
        var before = Snapshot(b); var after = Snapshot(a);
        if (!JsonNode.DeepEquals(b["identity"], a["identity"])) throw Error("Drive identity, family, order number, firmware and parameter-set declarations must match exactly. Cross-profile mapping is not inferred.");
        if (afterKind == "proposal" && (Text(b, "valueDomain") != "offline" || Text(a, "valueDomain") != "offline"))
            throw Error("Only offline parameter proposals are supported; online observations do not authorize writes.");
        string driveId = Text(Object(b["identity"]), "driveId");
        var observations = new JsonArray(); int unresolvedReferences = 0, unresolvedParents = 0;
        foreach (var (phase, rows) in new[] { ("before", before), ("after", after) })
            foreach (var pair in rows)
            {
                var row = pair.Value; var value = Object(row["value"]); string reference = "notReference";
                if (Text(value, "kind") == "reference")
                {
                    reference = value["driveId"] is null ? "unresolved" : (string?)value["driveId"] != driveId ? "outsideSuppliedDrive" :
                        value["parameterName"] is not null && rows.ContainsKey(Text(value, "parameterName", 256)) ? "suppliedParameter" : "unresolved";
                    if (reference != "suppliedParameter") unresolvedReferences++;
                }
                string parentStatus = "notDeclared";
                if (row["parentName"] is not null)
                {
                    parentStatus = "unresolved"; var current = row; var visited = new HashSet<string>(StringComparer.Ordinal) { pair.Key };
                    while (visited.Count <= 128 && current["parentName"] is not null && rows.TryGetValue(Text(current, "parentName", 256), out var parent))
                    {
                        if (!visited.Add(Text(parent, "name", 256)) || !JsonNode.DeepEquals(row["number"], parent["number"]) || !JsonNode.DeepEquals(row["arrayIndex"], parent["arrayIndex"])) break;
                        if (parent["parentName"] is null) { parentStatus = "suppliedParentChain"; break; }
                        current = parent;
                    }
                    if (parentStatus == "unresolved") unresolvedParents++;
                }
                observations.Add(new JsonObject { ["phase"] = phase, ["parameter"] = row.DeepClone(), ["referenceResolution"] = reference, ["parentResolution"] = parentStatus });
            }
        var changes = new JsonArray(); int readOnlyConflicts = 0, unknownAccess = 0, identityConflicts = 0;
        foreach (string name in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(name, out var old); after.TryGetValue(name, out var next);
            var fields = new JsonArray(); string status;
            if (old is null || next is null) status = old is null ? "onlyAfter" : "onlyBefore";
            else
            {
                foreach (string field in new[] { "number", "arrayIndex", "parentName", "unit", "access", "value", "metadata" })
                    if (!JsonNode.DeepEquals(old[field], next[field])) fields.Add(field);
                status = fields.Count == 0 ? "unchanged" : "changed";
                if (new[] { "number", "arrayIndex", "parentName" }.Any(k => !JsonNode.DeepEquals(old[k], next[k]))) { status = "selectorConflict"; identityConflicts++; }
            }
            string proposal = afterKind != "proposal" ? "notProposal" : "notAssessed";
            if (afterKind == "proposal" && old is not null && next is not null && !JsonNode.DeepEquals(old["value"], next["value"]))
            {
                if (Text(old, "access") == "readOnly") { proposal = "contradictsSuppliedReadOnly"; readOnlyConflicts++; }
                else if (Text(old, "access") == "unknown") { proposal = "accessUnknown"; unknownAccess++; }
            }
            changes.Add(new JsonObject { ["name"] = name, ["status"] = status, ["changedFields"] = fields, ["proposalAssessment"] = proposal });
        }
        return new JsonObject { ["schemaVersion"] = "drive-profile-review.v1", ["reviewId"] = reviewId, ["hardwarePath"] = hardware,
            ["identity"] = b["identity"]!.DeepClone(), ["afterKind"] = afterKind, ["beforeValueDomain"] = b["valueDomain"]!.DeepClone(), ["afterValueDomain"] = a["valueDomain"]!.DeepClone(),
            ["coverage"] = new JsonObject { ["beforeParametersComplete"] = Boolean(b, "parametersComplete"), ["afterParametersComplete"] = Boolean(a, "parametersComplete") },
            ["observations"] = observations, ["changes"] = changes,
            ["summary"] = new JsonObject { ["beforeCount"] = before.Count, ["afterCount"] = after.Count, ["changedCount"] = changes.Count(c => (string?)c!["status"] != "unchanged"),
                ["selectorConflictCount"] = identityConflicts, ["readOnlyConflictCount"] = readOnlyConflicts, ["unknownAccessChangeCount"] = unknownAccess,
                ["unresolvedReferenceCount"] = unresolvedReferences, ["unresolvedParentCount"] = unresolvedParents },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["driveIdentityVerified"] = false, ["wholeDriveAnalysisComplete"] = false, ["nativeConstraintsVerified"] = false,
            ["limits"] = "Supplied observations bound to a captured hardware selection only. Exact full parameter names never derive from bit-list position. Units, limits/enums and other metadata are retained without conversion or native validation. Read/write declarations cannot authorize writes. Missing rows do not prove addition/deletion; BICO references and parent chains are supplied, not live wiring; parent traversal is bounded to 128 links. No native discovery, engineering edit, transfer, activation, persistence, safety or motion." };

        Dictionary<string, JsonObject> Snapshot(JsonObject snapshot)
        {
            Fields(snapshot, "identity valueDomain parametersComplete parameters");
            if (Text(snapshot, "valueDomain") is not ("offline" or "online")) throw Error("Unknown value domain.");
            Boolean(snapshot, "parametersComplete"); var identity = Object(snapshot["identity"]);
            Fields(identity, "driveId family orderNumber firmware parameterSet unknownReason");
            Text(identity, "driveId", 256);
            foreach (string key in new[] { "family", "orderNumber", "firmware" }) NullableText(identity, key, 256);
            NullableText(identity, "parameterSet", 256);
            string? unknownReason = NullableText(identity, "unknownReason", 1024);
            if (new[] { "family", "orderNumber", "firmware" }.Any(k => identity[k] is null) && unknownReason is null)
                throw Error("Unknown drive family/order/firmware requires an explicit reason.");
            var rows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var item in Array(snapshot, "parameters", 4096))
            {
                var row = Object(item); Fields(row, "name number arrayIndex parentName unit access value metadata");
                if (!rows.TryAdd(Text(row, "name", 256), row)) throw Error("Duplicate exact parameter name.");
                Integer(row, "number", 0, int.MaxValue); Integer(row, "arrayIndex", -1, 32767);
                NullableText(row, "parentName", 256);
                if (row["unit"] is not null) Scalar(row, "unit", 256);
                if (Text(row, "access") is not ("readOnly" or "readWrite" or "unknown")) throw Error("Unknown supplied access state.");
                _ = Object(row["metadata"]); var value = Object(row["value"]);
                switch (Text(value, "kind"))
                {
                    case "scalar": Fields(value, "kind type text"); Text(value, "type", 256); Scalar(value, "text", InputLimit); break;
                    case "reference":
                        Fields(value, "kind driveId parameterName reason");
                        string? owner = NullableText(value, "driveId", 256), parameter = NullableText(value, "parameterName", 256), reason = NullableText(value, "reason", 1024);
                        if ((owner is null || parameter is null) && reason is null) throw Error("Unresolved BICO reference requires a reason.");
                        break;
                    case "unavailable": Fields(value, "kind reason"); Text(value, "reason", 1024); break;
                    default: throw Error("Unknown value kind; preserve scalar, reference or unavailable explicitly.");
                }
            }
            return rows;
        }
    }
    private static void Integer(JsonObject value, string field, int min, int max)
    { if (value[field] is not JsonValue v || !v.TryGetValue<int>(out int number) || number < min || number > max) throw Error("Invalid integer: " + field); }
    private static void Scalar(JsonObject value, string field, int max)
    { if (value[field] is not JsonValue v || !v.TryGetValue<string>(out string? text) || text is null || text.Length > max) throw Error("Expected bounded scalar text: " + field); _ = Utf8.GetByteCount(text); }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object row.");
    private static void Fields(JsonObject value, string fields)
    { var expected = fields.Split(' '); if (value.Count != expected.Length || expected.Any(k => !value.ContainsKey(k))) throw Error("Missing or unknown fields; expected: " + fields); }
    private static JsonArray Array(JsonObject value, string key, int max) => value[key] is JsonArray rows && rows.Count <= max ? rows : throw Error(key + " collection exceeds bounds or is absent.");
    private static bool Boolean(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean " + key);
    private static string? NullableText(JsonObject value, string key, int max = 8192) => value[key] is null ? null : Text(value, key, max);
    private static string Text(JsonNode node, string key, int max = 8192)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out string? text) || text is null || text.Length > max || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) throw Error("Invalid exact text: " + key);
        _ = Utf8.GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidDriveProfile", message);
}
