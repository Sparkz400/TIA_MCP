using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.Collaboration;

/// <summary>Compares declared Teamcenter observations; never supplies trusted native readback.</summary>
public static class TeamcenterReview
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

    public const int ManifestLimit = 1024 * 1024;
    public static JsonObject Build(JsonObject request, Func<string, byte[]> resolveManifest)
    {
        Fields(request, "schemaVersion before after");
        if (Text(request, "schemaVersion") != "teamcenter-observations.v1") throw Error("Unsupported observation schema.");
        var observations = new JsonArray(); var changes = new JsonArray(); var issues = new JsonArray(); var manifests = new JsonArray();
        var retained = new Dictionary<string, (string Hash, JsonObject Manifest)>(StringComparer.Ordinal);
        var before = Object(request["before"]); var after = Object(request["after"]);
        var oldRows = Inspect(before, "before"); var newRows = Inspect(after, "after");
        foreach (string field in new[] { "serverUrl", "instance" })
            if (!JsonNode.DeepEquals(before["connection"]![field], after["connection"]![field])) throw Error("Server/instance declarations differ; no authority aliases are inferred.");
        if (Text(before, "captureId") == Text(after, "captureId") && !JsonNode.DeepEquals(before, after)) throw Error("One capture ID cannot describe different observations in a pair.");
        foreach (string section in oldRows.Keys)
            foreach (string identity in oldRows[section].Keys.Union(newRows[section].Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                oldRows[section].TryGetValue(identity, out var old); newRows[section].TryGetValue(identity, out var next);
                if (old is not null && next is not null && CanonicalJson.Serialize(old) == CanonicalJson.Serialize(next)) continue;
                changes.Add(new JsonObject { ["section"] = section, ["identity"] = identity,
                    ["status"] = old is null ? "onlyAfter" : next is null ? "onlyBefore" : "differentObservation", ["before"] = old?.DeepClone(), ["after"] = next?.DeepClone() });
            }
        var b = Metadata(before); var a = Metadata(after);
        if (!JsonNode.DeepEquals(b, a)) changes.Add(new JsonObject { ["section"] = "metadata", ["identity"] = "capture", ["status"] = "differentObservation", ["before"] = b, ["after"] = a });
        return new JsonObject { ["schemaVersion"] = "teamcenter-report.v1", ["observations"] = observations, ["changes"] = changes, ["issues"] = issues, ["manifests"] = manifests,
            ["summary"] = new JsonObject { ["observationCount"] = observations.Count, ["changeCount"] = changes.Count, ["issueCount"] = issues.Count, ["manifestCount"] = manifests.Count, ["scope"] = "suppliedDeclarationsOnly" },
            ["nativeExecutable"] = false, ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["checkoutOwnershipVerified"] = false, ["fileBytesVerified"] = false, ["nativeCacheVerified"] = false, ["sourceEvidencePolicy"] = ProjectReviewStore.SourceEvidencePolicy };

        Dictionary<string, Dictionary<string, JsonObject>> Inspect(JsonObject snapshot, string phase)
        {
            Fields(snapshot, "captureId capturedAtUtc producer connection items revisions datasets localCopies");
            string id = Text(snapshot, "captureId", 32);
            if (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigitLower(c))) throw Error("captureId requires 32 lowercase hexadecimal characters.");
            if (!DateTimeOffset.TryParseExact(Text(snapshot, "capturedAtUtc"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero) throw Error("Expected an exact UTC round-trip timestamp, not freshness proof.");
            Text(snapshot, "producer"); var connection = Object(snapshot["connection"]);
            Fields(connection, "serverUrl instance principal group role connectionId epoch");
            string url = Text(connection, "serverUrl", 2048);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || url.Contains('\\')) throw Error("Expected a public HTTP(S) server URL without credentials, query or fragment.");
            foreach (string key in new[] { "instance", "principal", "connectionId", "epoch" }) Text(connection, key);
            NullableText(connection, "group"); NullableText(connection, "role");
            var indexed = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
            Read("items", 256, row => { Fields(row, "itemType itemId name properties"); string key = Item(row); Text(row, "name"); Properties(row); return key; });
            Read("revisions", 512, row => {
                Fields(row, "itemType itemId revisionId properties"); string key = Revision(row); Properties(row);
                if (!indexed["items"].ContainsKey(Item(row))) Issue("revisions", key, "itemNotSupplied"); return key;
            });
            Read("datasets", 1024, row => {
                Fields(row, "itemType itemId revisionId datasetType name version lock properties"); string key = Dataset(row); NullableText(row, "version"); Properties(row);
                if (row["version"] is null) Issue("datasets", key, "unknownDatasetVersion");
                if (!indexed["revisions"].ContainsKey(Revision(row))) Issue("datasets", key, "revisionNotSupplied");
                var state = Object(row["lock"]); Fields(state, "state owner"); string status = Choice(state, "state", "checkedOut checkedIn unknown"); NullableText(state, "owner");
                if ((status == "checkedOut") != (state["owner"] is not null)) throw Error("Lock owner is required exactly when reported checked out.");
                if (status != "checkedIn") Issue("datasets", key, status == "checkedOut" ? "reportedCheckout" : "unknownLock"); return key;
            });
            Read("localCopies", 128, row => {
                Fields(row, "dataset purpose path cacheVersion serverVersion manifest"); var dataset = Object(row["dataset"]); Fields(dataset, "itemType itemId revisionId datasetType name");
                string datasetKey = Dataset(dataset); string key = Key(datasetKey, Choice(row, "purpose", "source cache"), Text(row, "path", 4096));
                if (!indexed["datasets"].ContainsKey(datasetKey)) Issue("localCopies", key, "datasetNotSupplied");
                NullableText(row, "cacheVersion"); NullableText(row, "serverVersion");
                if (row["cacheVersion"] is null || row["serverVersion"] is null) Issue("localCopies", key, "unknownVersionComparison");
                else if (!JsonNode.DeepEquals(row["cacheVersion"], row["serverVersion"])) Issue("localCopies", key, "reportedVersionMismatch");
                var reference = Object(row["manifest"]); Fields(reference, "uploadRef sha256");
                string uploadRef = Text(reference, "uploadRef"); string hash = Text(reference, "sha256"); EngineeringIdentityGuard.Sha256(hash, "manifestSha256");
                if (uploadRef.Length != 39 || !uploadRef.StartsWith("upload:", StringComparison.Ordinal) || uploadRef[7..].Any(c => !char.IsAsciiHexDigitLower(c))) throw Error("Manifest requires an exact committed upload reference.");
                if (!retained.TryGetValue(uploadRef, out var captured))
                {
                    if (retained.Count >= 16) throw Error("At most 16 distinct manifest uploads per comparison.");
                    byte[] bytes = resolveManifest(uploadRef);
                    if (bytes.Length > ManifestLimit || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != hash) throw Error("Manifest bytes exceed one MiB or differ from the declared hash.");
                    var manifest = Parse(bytes); ValidateManifest(manifest);
                    captured = (hash, manifest); retained.Add(uploadRef, captured);
                    manifests.Add(new JsonObject { ["uploadRef"] = uploadRef, ["sha256"] = hash, ["manifest"] = manifest.DeepClone(), ["manifestBytesVerified"] = true, ["fileBytesVerified"] = false, ["nativeCacheVerified"] = false });
                }
                if (captured.Hash != hash || Text(captured.Manifest, "rootPath", 4096) != Text(row, "path", 4096)) throw Error("Manifest hash/root does not match its exact supplied local copy.");
                if (Text(captured.Manifest, "coverage") != "complete") Issue("localCopies", key, "incompleteManifest");
                return key;
            });
            observations.Add(new JsonObject { ["phase"] = phase, ["section"] = "metadata", ["identity"] = "capture", ["value"] = Metadata(snapshot) });
            return indexed;

            void Issue(string section, string identity, string code) => issues.Add(new JsonObject { ["phase"] = phase, ["section"] = section, ["identity"] = identity, ["code"] = code });
            void Read(string section, int maximum, Func<JsonObject, string> identity)
            {
                var envelope = Object(snapshot[section]); Fields(envelope, "coverage diagnostic rows"); string state = Coverage(envelope);
                if (envelope["rows"] is not JsonArray rows || rows.Count > maximum) throw Error("Missing or oversized " + section + " rows.");
                if (state is not ("complete" or "partial") && rows.Count != 0) throw Error("Unobserved sections cannot contain rows; use partial for retained observations.");
                if (state != "complete") Issue(section, "coverage", state);
                var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var item in rows) { var row = Object(item); string key = identity(row); if (!result.TryAdd(key, row)) throw Error("Duplicate exact " + section + " identity."); observations.Add(new JsonObject { ["phase"] = phase, ["section"] = section, ["identity"] = key, ["value"] = row.DeepClone() }); }
                indexed.Add(section, result);
            }
        }
    }
    private static JsonObject Metadata(JsonObject snapshot) { var result = snapshot.DeepClone().AsObject(); foreach (string key in new[] { "items", "revisions", "datasets", "localCopies" }) result[key]!.AsObject().Remove("rows"); return result; }
    private static void ValidateManifest(JsonObject manifest)
    {
        Fields(manifest, "schemaVersion rootPath coverage diagnostic files");
        if (Text(manifest, "schemaVersion") != "teamcenter-local-manifest.v1") throw Error("Unsupported local manifest schema.");
        Text(manifest, "rootPath", 4096); string state = Coverage(manifest);
        if (manifest["files"] is not JsonArray files || files.Count > 4096 || (state is not ("complete" or "partial") && files.Count != 0)) throw Error("Invalid manifest file coverage/count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in files)
        {
            var file = Object(node); Fields(file, "path bytes sha256"); string path = Text(file, "path", 1024);
            try { if (PackageStorage.Relative(path) != path) throw Error("Manifest paths require exact portable forward-slash spelling."); }
            catch (BridgeException e) when (e.Code == "PackageInvalid") { throw Error("Unsafe manifest relative path."); }
            if (!paths.Add(path)) throw Error("Duplicate or case-aliased manifest path.");
            if (file["bytes"] is not JsonValue size || !size.TryGetValue<long>(out long number) || number is < 0 or > 9007199254740991) throw Error("Manifest byte count must be an exact nonnegative JSON-safe integer.");
            EngineeringIdentityGuard.Sha256(Text(file, "sha256"), "fileSha256");
        }
        foreach (string path in paths)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.Contains(path[..slash])) throw Error("Manifest file paths overlap as ancestors.");
    }
    private static void Properties(JsonObject row)
    {
        if (row["properties"] is not JsonArray values || values.Count > 64) throw Error("At most 64 declared public properties per row.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in values)
        {
            var property = Object(node); Fields(property, "name value"); string name = Text(property, "name");
            if (!names.Add(name) || new[] { "password", "userPassword", "sessionToken", "authorization", "accessToken", "refreshToken", "credential", "secret" }.Contains(name, StringComparer.OrdinalIgnoreCase)) throw Error("Duplicate or credential property is not public metadata.");
            if (property["value"] is null) continue;
            if (property["value"] is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > 4096 || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))) throw Error("Public property requires bounded text or null.");
            _ = Utf8.GetByteCount(text);
        }
    }
    private static string Coverage(JsonObject node) { string state = Choice(node, "coverage", "complete partial unknown denied failed unavailable"); NullableText(node, "diagnostic"); if (state != "complete" && node["diagnostic"] is null) throw Error("Incomplete coverage requires a public diagnostic."); return state; }
    private static string Key(params string[] values) => CanonicalJson.Serialize(new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));
    private static string Item(JsonObject row) => Key(Choice(row, "itemType", "project globalLibrary"), Text(row, "itemId"));
    private static string Revision(JsonObject row) => Key(Item(row), Text(row, "revisionId"));
    private static string Dataset(JsonObject row)
    {
        string revision = Revision(row); string type = Choice(row, "datasetType", "T4TiaProjectDataset T4TiaLibraryDataset");
        if (type != ((string?)row["itemType"] == "project" ? "T4TiaProjectDataset" : "T4TiaLibraryDataset")) throw Error("Dataset type contradicts the declared project/library kind.");
        return Key(revision, type, Text(row, "name"));
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
    private static BridgeException Error(string message) => new("InvalidTeamcenterObservation", message);
}

