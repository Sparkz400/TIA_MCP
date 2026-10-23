using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.WatchTables;

/// <summary>Bounded planner-to-native-document transformation. Never manufactures vendor row grammar.</summary>
public static class WatchTableAuthoring
{
    public const int MaxSteps = 128;
    public const int MaxRows = 256;
    private static string DisplayFormat(string datatype) => datatype.ToUpperInvariant() switch {
        "BOOL" => "Bool", "BYTE" or "WORD" or "DWORD" or "LWORD" => "Hex",
        "SINT" or "INT" or "DINT" or "LINT" => "DEC_signed", "USINT" or "UINT" or "UDINT" or "ULINT" => "DEC_unsigned",
        "REAL" or "LREAL" => "Float", "TIME" or "LTIME" => "Time", "CHAR" => "Character", "WCHAR" => "Unicode_character",
        "DATE" => "Date", "TIME_OF_DAY" => "TIME_OF_DAY",
        _ => throw new BridgeException("UnsupportedProfile", "No explicit native display-format profile for this scalar type.") };
    public static JsonObject Rows(WatchTableDocument document) => new()
    {
        ["name"] = document.Name,
        ["rows"] = new JsonArray(Enumerable.Range(0, document.Count).Select(i => (JsonNode)new JsonObject {
            ["ordinal"] = i, ["kind"] = document.RowKind(i), ["name"] = document.Field(i, "Name"),
            ["address"] = document.Field(i, "Address"), ["displayFormat"] = document.Field(i, "DisplayFormat"),
            ["modifyValue"] = document.Field(i, "ModifyValue"), ["comments"] = JsonSerializer.SerializeToNode(document.Comments(i)) }).ToArray())
    };

    public static JsonObject Edit(WatchTableDocument document, WatchTableDocument template, JsonObject plan,
        JsonObject selectors, JsonArray steps, IReadOnlyCollection<string> activeCultures, bool creating)
    {
        if (steps.Count is < 1 or > MaxSteps || document.Count > MaxRows || template.Count > MaxRows)
            throw new BridgeException("WatchTableLimitExceeded", "Authoring accepts 1..128 sequential steps and at most 256 rows.");
        var original = Rows(document); var profiles = new HashSet<string>(StringComparer.Ordinal);
        if (Enumerable.Range(0, document.Count).Any(i => document.RowKind(i) == "PlcTableCommentEntry")) profiles.Add("watch.comment-row-preservation.v1");
        if (creating) profiles.Add("watch.create-instance.v1");
        var selected = plan["rows"]!.AsArray().ToDictionary(r => r!["candidateId"]!.GetValue<string>(), r => r!, StringComparer.Ordinal);
        var changes = new JsonArray(); bool structural = creating; long reviewBytes = 0;
        foreach (var raw in steps)
        {
            if (raw is not JsonObject step) throw new BridgeException("InvalidRequest", "Each step must be an object.");
            string kind = Required(step, "kind");
            var allowed = kind switch {
                "add" => new[] { "kind", "candidateId", "templateOrdinal", "insertAt", "comments" },
                "remove" => ["kind", "ordinal"], "reorder" => ["kind", "order"], "comment" => ["kind", "ordinal", "comments"],
                _ => throw new BridgeException("InvalidRequest", "Step must be add, remove, reorder or comment.") };
            if (step.Any(p => !allowed.Contains(p.Key))) throw new BridgeException("InvalidRequest", "Unknown authoring step field.");
            var before = Rows(document);
            int ordinal = step["ordinal"]?.GetValue<int>() ?? -1;
            switch (kind)
            {
                case "add":
                    string id = Required(step, "candidateId");
                    if (!selected.TryGetValue(id, out var row) || selectors[id] is not JsonValue symbol)
                        throw new BridgeException("UnresolvedSignal", "Only a selected planner row with a captured native selector can be added.");
                    int prototype = step["templateOrdinal"]?.GetValue<int>() ?? throw new BridgeException("InvalidRequest", "Exact captured template ordinal required.");
                    ordinal = step["insertAt"]?.GetValue<int>() ?? document.Count;
                    string datatype = row["candidate"]!["datatype"]!.GetValue<string>();
                    string format = DisplayFormat(datatype);
                    if (template.RowKind(prototype) != "PlcWatchTableEntry" || template.Field(prototype, "DisplayFormat") is null ||
                        !string.IsNullOrEmpty(template.Field(prototype, "Address")))
                        throw new BridgeException("UnsupportedProfile", "Symbolic native scalar row grammar is required; absolute templates are not rebound.");
                    if (document.Count >= MaxRows) throw new BridgeException("WatchTableLimitExceeded", "Authoring row budget exceeded.");
                    document.InsertCapturedRow(template, prototype, ordinal);
                    document.SetField(ordinal, "Name", symbol.GetValue<string>());
                    document.SetField(ordinal, "DisplayFormat", format);
                    document.SetField(ordinal, "ModifyValue", "");
                    var cultures = document.Comments(ordinal).Keys.ToArray();
                    foreach (string culture in cultures) document.SetComment(ordinal, culture, "");
                    if (activeCultures.Count == 0) throw new BridgeException("InactiveCulture", "A captured active project culture is required.");
                    string primary = activeCultures.Contains("en-GB") ? "en-GB" : activeCultures.First();
                    document.UpsertComment(ordinal, primary, step["comments"] is null ? row["candidate"]!["reason"]!.GetValue<string>() : "");
                    document.RetainCommentCultures(ordinal, activeCultures.ToArray());
                    profiles.Add("watch.add-instance-" + datatype.ToLowerInvariant() + ".v1"); structural = true;
                    goto case "comment";
                case "comment":
                    if (kind == "comment" && step["comments"] is not JsonObject) throw new BridgeException("InvalidRequest", "Explicit culture/text map required.");
                    if (step["comments"] is JsonObject comments)
                    {
                        if (comments.Count is < 1 or > 32) throw new BridgeException("WatchTableLimitExceeded", "Comments require 1..32 cultures.");
                        foreach (var comment in comments)
                        {
                            if (!activeCultures.Contains(comment.Key)) throw new BridgeException("InactiveCulture", "Comment culture is not active in the exact project: " + comment.Key);
                            bool addingCulture = !document.Comments(ordinal).ContainsKey(comment.Key);
                            document.UpsertComment(ordinal, comment.Key, comment.Value?.GetValue<string>() ?? throw new BridgeException("InvalidRequest", "Comment text cannot be null."));
                            profiles.Add(addingCulture ? "watch.comment-add-culture.v1" : "watch.comment-existing.v1"); structural |= addingCulture;
                        }
                    }
                    break;
                case "remove": document.Remove(ordinal); profiles.Add("watch.remove-row.v1"); structural = true; break;
                case "reorder":
                    document.Reorder(step["order"]?.AsArray().Select(v => v!.GetValue<int>()).ToArray() ?? throw new BridgeException("InvalidRequest", "Complete permutation required."));
                    profiles.Add("watch.reorder-rows.v1"); structural = true; break;
            }
            var change = new JsonObject { ["step"] = step.DeepClone(), ["before"] = before, ["after"] = Rows(document) };
            reviewBytes += Encoding.UTF8.GetByteCount(change.ToJsonString());
            if (reviewBytes > 16 * 1024 * 1024) throw new BridgeException("WatchTableLimitExceeded", "Complete step review exceeds 16 MiB; split the change into smaller reviewed operations.");
            changes.Add(change);
        }
        JsonNode? mapping = null;
        if (structural)
        {
            mapping = JsonSerializer.SerializeToNode(document.ReindexReferenceFreeDocumentIds(authoring: true));
            profiles.Add("watch.reference-free-authoring-ids.v1");
        }
        // Reparse to enforce the byte/depth/ID budgets after all changes.
        _ = new WatchTableDocument(document.ToBytes());
        return new JsonObject { ["before"] = original, ["after"] = Rows(document), ["steps"] = changes,
            ["documentIdMapping"] = mapping, ["requiredProfiles"] = new JsonArray(profiles.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
    }
    private static string Required(JsonObject value, string key) => value[key]?.GetValue<string>() ?? throw new BridgeException("InvalidRequest", key + " is required.");
}

internal static class WatchTableNativeTemplate
{
    internal const string Profile = "owned-v21-scalar-row-1";
    internal const string Sha256 = "d710c6b85dd5c94cc27d7eb7edc24f5ac188ec5bdbaccdae5998acf5b697c09d";
    internal static byte[] Read()
    {
        using var stream = typeof(WatchTableNativeTemplate).Assembly.GetManifestResourceStream("TiaMcp.WatchTable.OwnedV21.xml")
            ?? throw new BridgeException("UnsupportedProfile", "Packaged genuine native row template is absent.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); var result = bytes.ToArray();
        if (WatchTableDocument.Hash(result) != Sha256) throw new BridgeException("ArtifactChanged", "Packaged native template differs from its retained fixture provenance.");
        return result;
    }
}

/// <summary>Production admission is explicit per mutation profile. Managed tests cannot promote native qualification.</summary>
public static class WatchMutationProfiles
{
    public static JsonObject Status(string profile) => new() { ["profile"] = profile, ["nativeVerified"] = false,
        ["productionApplyEnabled"] = false, ["requiredEvidence"] = "Exact delta, typed/XML readback, unchanged peers, saved fresh reopen, checkpoint recovery and fresh recovered reopen on the exact target profile." };

    public static void RequireVerified(JsonArray profiles, bool qualificationHarness)
    {
        if (profiles.Count == 0) throw new BridgeException("UnsupportedProfile", "A mutation requires explicit native profile gates.");
        if (!qualificationHarness) throw new BridgeException("UnsupportedProfile", "Authoring review is available; these mutation profiles still require native lifecycle verification: " + string.Join(", ", profiles.Select(p => p!.GetValue<string>())));
    }
}
