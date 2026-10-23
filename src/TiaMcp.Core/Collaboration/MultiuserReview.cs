using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.Collaboration;

/// <summary>Compares declared Project Server observations; never supplies trusted native readback.</summary>
public static class MultiuserReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Supply 1 byte..4 MiB of observations.");
        string text = Utf8.GetString(bytes);
        using var parsed = JsonDocument.Parse(text, new() { MaxDepth = 24 });
        void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in value.EnumerateObject())
                { _ = Utf8.GetByteCount(field.Name); if (!names.Add(field.Name)) throw Error("Duplicate JSON field."); Unique(field.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
            else if (value.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(value.GetString()!);
        }
        Unique(parsed.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an observation request object.");
    }

    public static JsonObject Build(JsonObject request)
    {
        Fields(request, "schemaVersion before after");
        if (Text(request, "schemaVersion") != "multiuser-observations.v1") throw Error("Unsupported observation schema.");
        var issues = new JsonArray(); var observations = new JsonArray(); var changes = new JsonArray();
        var before = Object(request["before"]); var after = Object(request["after"]);
        var oldRows = Inspect(before, "before"); var newRows = Inspect(after, "after");
        foreach (string key in new[] { "host", "port" })
            if (!JsonNode.DeepEquals(before["server"]![key], after["server"]![key])) throw Error("Server host/port declarations differ; aliases are never repaired.");
        if (Text(before, "captureId") == Text(after, "captureId") && !JsonNode.DeepEquals(before, after)) throw Error("One captureId cannot describe different observations in a pair.");
        foreach (string section in oldRows.Keys)
            foreach (string identity in oldRows[section].Keys.Union(newRows[section].Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                oldRows[section].TryGetValue(identity, out var old); newRows[section].TryGetValue(identity, out var next);
                if (old is not null && next is not null && CanonicalJson.Serialize(old) == CanonicalJson.Serialize(next)) continue;
                changes.Add(new JsonObject { ["section"] = section, ["identity"] = identity,
                    ["status"] = old is null ? "onlyAfter" : next is null ? "onlyBefore" : "differentObservation",
                    ["before"] = old?.DeepClone(), ["after"] = next?.DeepClone() });
            }
        var metadataBefore = before.DeepClone().AsObject(); var metadataAfter = after.DeepClone().AsObject();
        foreach (string section in oldRows.Keys) { metadataBefore[section]!.AsObject().Remove("rows"); metadataAfter[section]!.AsObject().Remove("rows"); }
        if (!JsonNode.DeepEquals(metadataBefore, metadataAfter)) changes.Add(new JsonObject {
            ["section"] = "metadata", ["identity"] = "capture", ["status"] = "differentObservation", ["before"] = metadataBefore, ["after"] = metadataAfter });
        return new JsonObject { ["schemaVersion"] = "multiuser-report.v1", ["observations"] = observations, ["changes"] = changes, ["issues"] = issues,
            ["summary"] = new JsonObject { ["observationCount"] = observations.Count, ["changeCount"] = changes.Count, ["issueCount"] = issues.Count, ["scope"] = "suppliedDeclarationsOnly" },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["conflictFreeVerified"] = false, ["stableProjectIdentityVerified"] = false, ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };

        Dictionary<string, Dictionary<string, JsonObject>> Inspect(JsonObject snapshot, string phase)
        {
            Fields(snapshot, "captureId capturedAtUtc producer principal server groups projects sessions markings");
            string id = Text(snapshot, "captureId", 32);
            if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigitLower(c))) throw Error("captureId requires 32 lowercase hexadecimal characters.");
            if (!DateTimeOffset.TryParseExact(Text(snapshot, "capturedAtUtc"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero)
                throw Error("capturedAtUtc requires an exact UTC round-trip timestamp; it is not freshness proof.");
            Text(snapshot, "producer"); Text(snapshot, "principal");
            var server = Object(snapshot["server"]); Fields(server, "host port alias"); Text(server, "alias");
            string host = Text(server, "host", 2048);
            if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "net.tcp") ||
                string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
                !System.Text.RegularExpressions.Regex.IsMatch(host, @"\A(?:http|https|net\.tcp)://(?:\[[0-9a-fA-F:]+\]|[^/:?#@\s]+)/*\z"))
                throw Error("Server host must be an http/https/net.tcp authority URI without credentials, query, fragment or path.");
            if (server["port"] is not JsonValue port || !port.TryGetValue<int>(out int number) || number is < 1 or > 65535) throw Error("Invalid server port.");
            var indexed = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
            Read("groups", 128, row => { Fields(row, "path"); return Group(row["path"]); });
            Read("projects", 256, row => {
                Fields(row, "groupPath name revision lock"); string project = Project(row);
                if (row["groupPath"]!.AsArray().Count != 0 && !indexed["groups"].ContainsKey(Group(row["groupPath"]))) Issue("projects", project, "groupNotSupplied");
                NullableText(row, "revision"); if (row["revision"] is null) Issue("projects", project, "unknownRevision");
                var state = Object(row["lock"]); Fields(state, "state owner"); string status = Choice(state, "state", "locked unlocked unknown");
                NullableText(state, "owner");
                if ((status == "locked" && state["owner"] is null) || (status != "locked" && state["owner"] is not null)) throw Error("Lock owner must be present exactly when reported locked.");
                if (status != "unlocked") Issue("projects", project, status == "locked" ? "reportedLock" : "unknownLock");
                return project;
            });
            Read("sessions", 512, row => {
                Fields(row, "project sessionId projectPath mode upToDate"); string project = ProjectReference(row["project"]);
                Text(row, "sessionId"); Text(row, "projectPath", 4096); Choice(row, "mode", "multiuser exclusive unknown");
                if (row["upToDate"] is not null && (row["upToDate"] is not JsonValue value || !value.TryGetValue<bool>(out _))) throw Error("upToDate requires a Boolean or unknown null.");
                string identity = Key(project, Text(row, "sessionId"));
                if (!indexed["projects"].ContainsKey(project)) Issue("sessions", identity, "projectNotSupplied");
                if ((bool?)row["upToDate"] != true) Issue("sessions", identity, row["upToDate"] is null ? "unknownSessionFreshness" : "reportedOutdatedSession");
                if (Text(row, "mode") == "unknown") Issue("sessions", identity, "unknownSessionMode");
                return identity;
            });
            Read("markings", 2048, row => {
                Fields(row, "project sessionId objectPath markState"); string project = ProjectReference(row["project"]);
                string session = Key(project, Text(row, "sessionId")); Text(row, "objectPath", 4096); Text(row, "markState");
                string identity = Key(session, Text(row, "objectPath", 4096));
                if (!indexed["sessions"].ContainsKey(session)) Issue("markings", identity, "sessionNotSupplied");
                return identity;
            });
            Issue("markings", "coverage", "deletedObjectsNotRepresented");
            var declared = snapshot.DeepClone().AsObject();
            foreach (string section in indexed.Keys) declared[section]!.AsObject().Remove("rows");
            observations.Add(new JsonObject { ["phase"] = phase, ["section"] = "metadata", ["identity"] = "capture", ["value"] = declared });
            return indexed;

            void Issue(string section, string identity, string code) => issues.Add(new JsonObject { ["phase"] = phase, ["section"] = section, ["identity"] = identity, ["code"] = code });
            void Read(string section, int maximum, Func<JsonObject, string> identity)
            {
                var envelope = Object(snapshot[section]); Fields(envelope, "coverage diagnostic rows");
                string coverage = Choice(envelope, "coverage", "complete partial unknown denied failed unavailable");
                NullableText(envelope, "diagnostic");
                if (envelope["rows"] is not JsonArray rows || rows.Count > maximum) throw Error("Missing or oversized " + section + " rows.");
                if (coverage is not ("complete" or "partial") && rows.Count != 0) throw Error("Unobserved sections cannot contain rows; use partial for retained partial observations.");
                if (coverage != "complete" && envelope["diagnostic"] is null) throw Error("Incomplete coverage requires a public diagnostic.");
                if (coverage != "complete") Issue(section, "coverage", coverage);
                var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var item in rows)
                {
                    var row = Object(item); string key = identity(row);
                    if (!result.TryAdd(key, row)) throw Error("Duplicate exact " + section + " identity.");
                    observations.Add(new JsonObject { ["phase"] = phase, ["section"] = section, ["identity"] = key, ["value"] = row.DeepClone() });
                }
                indexed.Add(section, result);
            }
        }
    }
    private static string Key(params string[] values) => CanonicalJson.Serialize(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
    private static string Project(JsonObject row) => Key(Group(row["groupPath"]), Text(row, "name"));
    private static string ProjectReference(JsonNode? node) { var row = Object(node); Fields(row, "groupPath name"); return Project(row); }
    private static string Group(JsonNode? node)
    {
        if (node is not JsonArray path || path.Count > 16) throw Error("Group path requires 0..16 exact segments.");
        foreach (var item in path) Text(new JsonObject { ["segment"] = item?.DeepClone() }, "segment");
        return CanonicalJson.Serialize(path);
    }
    private static void NullableText(JsonObject node, string key) { if (node[key] is not null) Text(node, key); }
    private static string Choice(JsonObject node, string key, string choices) { string value = Text(node, key); return choices.Split(' ').Contains(value, StringComparer.Ordinal) ? value : throw Error("Invalid " + key + "."); }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object.");
    private static void Fields(JsonObject node, string fields) { var keys = fields.Split(' '); if (node.Count != keys.Length || keys.Any(k => !node.ContainsKey(k))) throw Error("Missing or unknown observation fields."); }
    private static string Text(JsonObject node, string key, int maximum = 512)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) throw Error("Invalid exact text: " + key);
        _ = Utf8.GetByteCount(text); return text;
    }
    private static BridgeException Error(string message) => new("InvalidMultiuserObservation", message);
}
