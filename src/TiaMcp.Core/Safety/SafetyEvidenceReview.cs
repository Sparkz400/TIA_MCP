using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.Safety;

/// <summary>Compares supplied safety_inspect observations without certifying the source or program.</summary>
public static class SafetyEvidenceReview
{
    public const int InputLimit = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > InputLimit) throw Error("Observation must contain 1 byte..4 MiB.");
        string text = Utf8.GetString(bytes);
        using var parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        void Unique(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in element.EnumerateObject())
                { _ = Utf8.GetByteCount(field.Name); if (!names.Add(field.Name)) throw Error("Duplicate JSON field."); Unique(field.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
            else if (element.ValueKind == JsonValueKind.String) _ = Utf8.GetByteCount(element.GetString()!);
        }
        Unique(parsed.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw Error("Expected an observation object.");
    }

    public static JsonObject Build(string reviewId, JsonObject review, string plcPath, byte[] beforeBytes, byte[] afterBytes)
    {
        Selector(plcPath);
        var selected = review["model"]!["nodes"]!.AsArray().Where(n => (string?)n?["objectPath"] == plcPath && (string?)n?["origin"] != "virtual").ToArray();
        if (selected.Length != 1 || (string?)selected[0]!["kind"] != "plc") throw Error("Select one exact captured PLC.");
        string projectPath = Text(Object(review["model"]!["project"]), "projectPath");
        var before = Parse(beforeBytes); var after = Parse(afterBytes);
        var observations = new JsonArray(); var signatures = new JsonArray(); var issues = new JsonArray(); var changes = new JsonArray();
        var b = Inspect(before, "before"); var a = Inspect(after, "after");
        if (!JsonNode.DeepEquals(before["block"]?["path"], after["block"]?["path"])) throw Error("Selected block scopes differ; no cross-block mapping is inferred.");
        foreach (string section in b.Keys)
            foreach (string identity in b[section].Keys.Union(a[section].Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                b[section].TryGetValue(identity, out var old); a[section].TryGetValue(identity, out var next);
                if (old is not null && next is not null && CanonicalJson.Serialize(old) == CanonicalJson.Serialize(next)) continue;
                changes.Add(new JsonObject { ["section"] = section, ["identity"] = identity,
                    ["status"] = old is null ? "onlyAfter" : next is null ? "onlyBefore" : "differentObservation",
                    ["before"] = old?.DeepClone(), ["after"] = next?.DeepClone(),
                    ["beforeSignatureDecimal"] = section.EndsWith("Signatures", StringComparison.Ordinal) && old is not null ? old["value"]!.GetValue<ulong>().ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    ["afterSignatureDecimal"] = section.EndsWith("Signatures", StringComparison.Ordinal) && next is not null ? next["value"]!.GetValue<ulong>().ToString(System.Globalization.CultureInfo.InvariantCulture) : null });
            }
        var metadataBefore = before.DeepClone().AsObject(); var metadataAfter = after.DeepClone().AsObject();
        foreach (string section in b.Keys) { metadataBefore.Remove(section); metadataAfter.Remove(section); }
        bool metadataChanged = CanonicalJson.Serialize(metadataBefore) != CanonicalJson.Serialize(metadataAfter);
        if (metadataChanged) changes.Add(new JsonObject { ["section"] = "otherReportedContent", ["identity"] = "observation",
            ["status"] = "differentObservation", ["before"] = metadataBefore, ["after"] = metadataAfter });
        return new JsonObject { ["schemaVersion"] = "safety-evidence-review.v1", ["reviewId"] = reviewId, ["plcPath"] = plcPath,
            ["projectPath"] = projectPath, ["observations"] = observations, ["signatures"] = signatures, ["changes"] = changes, ["issues"] = issues,
            ["summary"] = new JsonObject { ["changeCount"] = changes.Count, ["issueCount"] = issues.Count,
                ["programSignatureChangeCount"] = changes.Count(c => (string?)c!["section"] == "programSignatures"),
                ["blockSignatureChangeCount"] = changes.Count(c => (string?)c!["section"] == "blockSignatures"),
                ["otherReportedContentChanged"] = metadataChanged, ["originalBytesEqual"] = beforeBytes.AsSpan().SequenceEqual(afterBytes) },
            ["nativeExecutable"] = false, ["nativeExecuted"] = false, ["nativeQualified"] = false,
            ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false, ["safetyApproval"] = false,
            ["requiredSignatureProfileVerified"] = false, ["wholeProgramAnalysisComplete"] = false,
            ["limits"] = "Supplied safety_inspect observations bound to one captured PLC. Values remain exact UInt64; zero values are absent/invalid observations, not valid safety proof. Missing rows do not prove native addition/deletion. Reported completeness is retained but required product-specific signature/profile coverage is not certified. Unknown content remains in originals and comparisons. No native login, compile, mutation, download, activation-test execution or functional-safety approval." };

        Dictionary<string, Dictionary<string, JsonObject>> Inspect(JsonObject snapshot, string phase)
        {
            if (snapshot.ContainsKey("error") || Text(snapshot, "plcPath") != plcPath || Text(Object(snapshot["project"]), "path") != projectPath)
                throw Error("Observation failed or project/PLC ownership differs from the saved selection.");
            var coverage = Object(snapshot["completeness"]);
            bool complete = Boolean(coverage, "scopeComplete"), truncated = Boolean(coverage, "truncated");
            var warnings = Array(snapshot, "warnings", 4096);
            foreach (var warning in warnings) if (warning is not JsonObject && (warning is not JsonValue value || !value.TryGetValue<string>(out _))) throw Error("Warnings require objects or strings.");
            Boolean(snapshot, "isLoggedOn"); Boolean(snapshot, "isPasswordSet");
            if (!complete || truncated || warnings.Count != 0) Issue("incompleteObservation", "Native capture declares gaps, warnings or truncation.");
            var rows = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
            Read("programSignatures", "type", 128, true);
            if (rows["programSignatures"].Count == 0) Issue("noProgramSignatures", "No program signatures were supplied.");
            if (snapshot.ContainsKey("block"))
            {
                var block = Object(snapshot["block"]); string path = Text(block, "path");
                if (Text(block, "plcPath") != plcPath || !path.StartsWith(plcPath + "/", StringComparison.Ordinal)) throw Error("Selected block owner differs.");
                if (!Boolean(snapshot, "blockSignatureServiceAvailable")) Issue("blockSignatureServiceUnavailable", "Selected block signature service is unavailable.");
                Read("blockSignatures", "type", 128, true);
                if (rows["blockSignatures"].Count == 0) Issue("noBlockSignatures", "No selected-block signatures were supplied.");
            }
            else
            {
                if (snapshot.ContainsKey("blockSignatures") || snapshot.ContainsKey("blockSignatureServiceAvailable")) throw Error("Block signature evidence has no exact block selection.");
                rows["blockSignatures"] = new(StringComparer.Ordinal);
            }
            Read("runtimeGroups", "name", 4096, false); Read("settings", "name", 4096, false);
            observations.Add(new JsonObject { ["phase"] = phase, ["reportedCompleteness"] = coverage.DeepClone(),
                ["warnings"] = warnings.DeepClone(), ["reportedBlock"] = snapshot["block"]?.DeepClone(),
                ["programSignatureCount"] = rows["programSignatures"].Count, ["blockSignatureCount"] = rows["blockSignatures"].Count,
                ["runtimeGroupCount"] = rows["runtimeGroups"].Count, ["settingCount"] = rows["settings"].Count });
            return rows;

            void Issue(string code, string message) => issues.Add(new JsonObject { ["phase"] = phase, ["code"] = code, ["message"] = message });
            void Read(string section, string key, int limit, bool signature)
            {
                var indexed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var item in Array(snapshot, section, limit))
                {
                    var row = Object(item); string identity = Text(row, key);
                    if (!indexed.TryAdd(identity, row)) throw Error("Duplicate " + section + " identity.");
                    if (signature)
                    {
                        if (row["value"] is not JsonValue value || !value.TryGetValue<ulong>(out var number)) throw Error("Signatures must be exact unsigned 64-bit integers.");
                        signatures.Add(new JsonObject { ["phase"] = phase, ["scope"] = section, ["type"] = identity,
                            ["valueDecimal"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture), ["zero"] = number == 0 });
                        if (number == 0) Issue("zeroSignature", section + "/" + identity + " is absent or invalid; applicability is not inferred.");
                    }
                }
                rows[section] = indexed;
            }
        }
    }
    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Error("Expected an object.");
    private static JsonArray Array(JsonObject node, string key, int maximum) => node[key] is JsonArray array && array.Count <= maximum ? array : throw Error("Missing or oversized " + key + " array.");
    private static bool Boolean(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<bool>(out bool result) ? result : throw Error("Expected Boolean " + key + ".");
    private static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var result) ? Selector(result) : throw Error("Expected text " + key + ".");
    private static string Selector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) throw Error("Expected a bounded exact identity.");
        _ = Utf8.GetByteCount(value); return value;
    }
    private static BridgeException Error(string message) => new("InvalidSafetyEvidence", message);
}
