using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.HmiComponents;

internal static class TextListAuthoringTests
{
    internal static void Run(string parent, string repository)
    {
        string root = Path.Combine(parent, "text-authoring"); Directory.CreateDirectory(root);
        var packet = HmiComponentReview.Parse(File.ReadAllBytes(Path.Combine(repository, "fixtures/hmi/unified-text-list/author-request.json")));
        var store = new HmiComponentWorkflowStore(root); var uploads = new ArtifactUploadStore(root); int checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is BridgeException or ArgumentException or JsonException or InvalidOperationException) { checks++; return; } throw new Exception("Expected refusal: " + label); }
        void Invalid(Action<JsonObject> edit, string label) { var value = packet.DeepClone().AsObject(); edit(value); Reject(() => UnifiedTextListAuthoring.Build(value), label); }
        JsonObject Entry(JsonObject value) => value["lists"]![0]!["entries"]![0]!.AsObject();
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(packet, BridgeProtocol.Json);
        var incoming = uploads.Begin("intent.json", raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw)));
        uploads.Write(incoming.UploadId, 0, Convert.ToBase64String(raw)); string reference = uploads.Commit(incoming.UploadId).ArtifactRef!;
        var receipt = store.AuthorTextLists(reference); string id = (string)receipt["recordId"]!;
        Check(JsonNode.DeepEquals(receipt, store.AuthorTextLists(reference)), "Exact authoring retry is idempotent");
        Check(receipt["files"]!.AsArray().Count == 2 && (int)receipt["languageCount"]! == 2, "Paired output and language counts");
        uploads.Release(incoming.UploadId);
        Check(JsonNode.DeepEquals(receipt, new HmiComponentWorkflowStore(root).AuthorTextLists(recordId: id)), "Original intent and candidates survive upload release/store restart");
        foreach (string flag in new[] { "nativeQualified", "nativeExecutable", "preservationVerified", "consumerCoverageComplete" }) Check(!(bool)receipt[flag]!, "No promotion: " + flag);
        foreach (var artifact in receipt["files"]!.AsArray().Select(f => f!["artifact"]!.AsObject()).Concat(new[] { receipt["input"]!.AsObject(), receipt["recordArtifact"]!.AsObject() }))
        {
            string path = Path.Combine(root, (string)artifact["path"]!); byte[] saved = File.ReadAllBytes(path);
            try { File.AppendAllText(path, "changed"); Reject(() => store.AuthorTextLists(recordId: id), "Changed retained file refuses"); }
            finally { File.WriteAllBytes(path, saved); }
        }
        var forged = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, (string)receipt["recordArtifact"]!["path"]!)))!.AsObject(); forged["nativeQualified"] = true;
        byte[] forgedBytes = JsonSerializer.SerializeToUtf8Bytes(forged); string hash = Convert.ToHexStringLower(SHA256.HashData(forgedBytes));
        File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", hash + ".author.json"), forgedBytes);
        Reject(() => store.AuthorTextLists(recordId: hash), "Rehashed forged qualification refuses");
        Reject(() => store.AuthorTextLists(), "Missing selector"); Reject(() => store.AuthorTextLists("upload:fake", id), "Competing selectors");
        Invalid(p => p["schemaVersion"] = "unified-text-list-author-request.v2", "Unknown version");
        Invalid(p => p["nativeExecutable"] = true, "Unknown field");
        foreach (string name in new[] { "../escape", "a/b", "a.b", "CON", "C:escape" }) Invalid(p => p["fileBase"] = name, "Unsafe filename " + name);
        Invalid(p => p["languages"] = new JsonArray("en-US", "EN-us"), "Case-colliding languages");
        Invalid(p => p["languages"]![0] = "en-US\n", "Language tags cannot hide a trailing newline");
        Invalid(p => p["defaultLanguage"] = "fr-FR", "Undeclared default language");
        Invalid(p => p["texts"]![0]!["translations"]!.AsObject().Remove("de-DE"), "Missing translation");
        Invalid(p => p["texts"]![0]!["translations"]!["fr-FR"] = "extra", "Unexpected translation");
        Invalid(p => p["texts"]!.AsArray().Add(p["texts"]![0]!.DeepClone()), "Duplicate text identity");
        Invalid(p => p["lists"]![0]!["entries"]!.AsArray().RemoveAt(1), "Unused text");
        Invalid(p => Entry(p)["textId"] = "Missing", "Unknown text reference");
        Invalid(p => p["textLibrary"] = "Ambiguous.Library", "Ambiguous dotted identity");
        Invalid(p => p["lists"]!.AsArray().Add(p["lists"]![0]!.DeepClone()), "Duplicate list name");
        Invalid(p => p["lists"]![0]!["entries"]![1]!["isDefault"] = true, "Two default entries");
        Invalid(p => Entry(p)["toValue"] = 1, "Contradictory single value");
        Invalid(p => { Entry(p)["type"] = "Range"; Entry(p)["toValue"] = 1; }, "Overlapping ranges");
        Invalid(p => Entry(p)["fromValue"] = "0", "No numeric string coercion");
        Invalid(p => Entry(p)["fromValue"] = 0.5, "No fractional coercion");
        Invalid(p => Entry(p)["fromValue"] = null, "Missing single value");
        Invalid(p => { p["lists"]![0]!["rangeType"] = "Bool"; Entry(p)["fromValue"] = 2; Entry(p)["toValue"] = 2; }, "Bool outside 0/1");
        Invalid(p => { p["lists"]![0]!["rangeType"] = "BitNumber"; Entry(p)["fromValue"] = -1; Entry(p)["toValue"] = -1; }, "Negative bit number");
        Invalid(p => p["texts"]![0]!["translations"]!["en-US"] = new string('x', 16385), "Oversized translation");
        Invalid(p => p["texts"]![0]!["translations"]!["en-US"] = "\ud800", "Invalid UTF-16");
        foreach (string type in new[] { "Range", "From", "To" })
        {
            var value = packet.DeepClone().AsObject(); var first = Entry(value); first["type"] = type;
            first["fromValue"] = type == "To" ? null : -10; first["toValue"] = type == "From" ? null : -5;
            if (type == "From") { value["lists"]![0]!["entries"]!.AsArray().RemoveAt(1); value["texts"]!.AsArray().RemoveAt(1); }
            Check(UnifiedTextListAuthoring.Build(value).Length == 2, "Explicit " + type + " renders");
        }
        foreach (string range in new[] { "Bool", "BitNumber" }) { var value = packet.DeepClone().AsObject(); value["lists"]![0]!["rangeType"] = range; Check(UnifiedTextListAuthoring.Build(value).Length == 2, range + " renders"); }
        var unicode = packet.DeepClone().AsObject(); unicode["texts"]![0]!["translations"]!["en-US"] = "\"\\\r\n\t\0\u0085\u2028\u2029Über 日本語 😀\n---\n!tag &anchor";
        Check(Encoding.UTF8.GetString(UnifiedTextListAuthoring.Build(unicode)[1].Bytes).Contains("😀"), "Astral Unicode stays a scalar, not surrogate escape pairs");
        Invalid(p => { var rows = p["lists"]!.AsArray(); while (rows.Count <= 128) rows.Add(rows[0]!.DeepClone()); }, "List count bound");
        Invalid(p => { var rows = p["texts"]!.AsArray(); while (rows.Count <= 4096) rows.Add(rows[0]!.DeepClone()); }, "Text count bound");
        Invalid(p => { var rows = p["lists"]![0]!["entries"]!.AsArray(); while (rows.Count <= 4096) rows.Add(rows[0]!.DeepClone()); }, "Per-list entry bound");
        Invalid(p => { var rows = p["languages"]!.AsArray(); while (rows.Count <= 32) rows.Add("en-" + rows.Count); }, "Language count bound");
        var large = packet.DeepClone().AsObject(); var many = new JsonArray();
        for (int i = 0; i < 4096; i++) many.Add(new JsonObject { ["id"] = "Entry_" + i, ["type"] = "SingleValue", ["fromValue"] = i, ["toValue"] = i, ["isDefault"] = false, ["textId"] = i % 2 == 0 ? "Text_Off" : "Text_Ready" });
        large["lists"]![0]!["entries"] = many;
        Check(UnifiedTextListAuthoring.Build(large)[0].Bytes.Length < UnifiedTextListAuthoring.OutputLimit, "Full 4096-entry request renders within byte bound");
        var extraList = packet["lists"]![0]!.DeepClone(); extraList["name"] = "Extra"; large["lists"]!.AsArray().Add(extraList);
        Reject(() => UnifiedTextListAuthoring.Build(large), "Total entry budget spans lists");
        var escaped = packet.DeepClone().AsObject(); var languageRows = new JsonArray();
        for (int i = 0; i < 32; i++) languageRows.Add("en-" + i);
        escaped["languages"] = languageRows; escaped["defaultLanguage"] = "en-0";
        foreach (var text in escaped["texts"]!.AsArray())
        {
            var translations = new JsonObject(); foreach (string language in languageRows.Select(n => (string)n!)) translations[language] = new string('\0', 16384);
            text!["translations"] = translations;
        }
        Reject(() => UnifiedTextListAuthoring.Build(escaped), "Escaping amplification is bounded before publication");
        // Content V21 imports as success but changes (local.hmi.text-list-import.20261029) refuses before anything is published.
        string Committed(JsonObject value)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json);
            var upload = uploads.Begin("unfaithful.json", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
            uploads.Write(upload.UploadId, 0, Convert.ToBase64String(data)); return uploads.Commit(upload.UploadId).ArtifactRef!;
        }
        var unfaithful = new (string Code, string Where, Action<JsonObject> Edit)[] {
            ("HmiTextListValueOutOfRange", "entry StateReady", p => { p["lists"]![0]!["entries"]![1]!["fromValue"] = 2147483648L; p["lists"]![0]!["entries"]![1]!["toValue"] = 2147483648L; }),
            ("HmiTextListValueOutOfRange", "entry StateOff", p => { Entry(p)["fromValue"] = -2147483649L; Entry(p)["toValue"] = -2147483649L; }),
            ("HmiTextListBoolDefaultEntry", "entry StateOff", p => p["lists"]![0]!["rangeType"] = "Bool"),
            ("HmiTextListTextUnsupported", "entry StateOff", p => p["texts"]![0]!["translations"]!["en-US"] = "a\0b"),
            ("HmiTextListTextUnsupported", "entry StateOff", p => p["texts"]![0]!["translations"]!["de-DE"] = "line\r\nbreak"),
            ("HmiTextListTextUnsupported", "entry StateReady", p => p["texts"]![1]!["translations"]!["en-US"] = "next\u0085line"),
            ("HmiTextListTextUnsupported", "entry StateOff", p => p["texts"]![0]!["translations"]!["en-US"] = "bell\u0007"),
            ("HmiTextListTextUnsupported", "entry StateReady", p => p["texts"]![1]!["translations"]!["de-DE"] = "del\u007fc1\u009f"),
            ("HmiTextListDefaultEntryValue", "entry StateReady", p => { Entry(p)["isDefault"] = false; p["lists"]![0]!["entries"]![1]!["isDefault"] = true; }),
            ("HmiTextListDefaultEntryValue", "entry StateOff", p => { Entry(p)["type"] = "To"; Entry(p)["fromValue"] = null; Entry(p)["toValue"] = -1; }),
            ("HmiTextListUnresolvedText", "entry StateOff", p => { Entry(p)["textId"] = "Missing"; p["texts"]!.AsArray().RemoveAt(0); }) }
            .Concat(new[] { ".", ":", "#", "\"", "[", "$", "%", "*", "/", "?", "]", "`", "~", "😀", " " }.Select(c => ("HmiTextListNameUnsupported", "List Qualification" + c, (Action<JsonObject>)(p => p["lists"]![0]!["name"] = "Qualification" + c + "States"))))
            .Concat(new[] { " Leading", "Trailing ", new string('N', UnifiedTextListAuthoring.MaxNativeListName + 1) }.Select(n => ("HmiTextListNameUnsupported", "List " + n + ":", (Action<JsonObject>)(p => p["lists"]![0]!["name"] = n))));
        string reviewsDirectory = Path.Combine(root, "hmi-component-reviews");
        foreach (var (code, where, edit) in unfaithful)
        {
            var value = packet.DeepClone().AsObject(); edit(value); int published = Directory.GetFiles(reviewsDirectory).Length;
            try { store.AuthorTextLists(Committed(value)); throw new Exception("Expected " + code + ": " + where); }
            catch (BridgeException e) when (e.Code == code && e.Message.Contains(where)) { }
            Check(Directory.GetFiles(reviewsDirectory).Length == published, "Nothing published for " + code + " at " + where);
        }
        // A record published before the refusal existed keeps its semantics: reopen recomputes the same bytes.
        var legacy = packet.DeepClone().AsObject(); legacy["lists"]![0]!["entries"]![1]!["fromValue"] = 2147483648L; legacy["lists"]![0]!["entries"]![1]!["toValue"] = 2147483648L;
        string Retain(byte[] data, string suffix)
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(data)); File.WriteAllBytes(Path.Combine(reviewsDirectory, digest + suffix), data); return digest;
        }
        JsonObject Ref(string digest, string suffix) => new() { ["path"] = "hmi-component-reviews/" + digest + suffix, ["sha256"] = digest };
        var legacyRecord = receipt.DeepClone().AsObject(); legacyRecord.Remove("recordId"); legacyRecord.Remove("recordArtifact");
        legacyRecord["input"] = Ref(Retain(JsonSerializer.SerializeToUtf8Bytes(legacy, BridgeProtocol.Json), ".author-input"), ".author-input");
        legacyRecord["files"] = new JsonArray(UnifiedTextListAuthoring.Build(legacy).Select(c => (JsonNode?)new JsonObject { ["fileName"] = c.Name, ["bytes"] = c.Bytes.Length, ["artifact"] = Ref(Retain(c.Bytes, ".candidate"), ".candidate") }).ToArray());
        string legacyId = Retain(JsonSerializer.SerializeToUtf8Bytes(legacyRecord, BridgeProtocol.Json), ".author.json");
        var reopened = store.AuthorTextLists(recordId: legacyId);
        Check((string)reopened["recordId"]! == legacyId && JsonNode.DeepEquals(reopened["files"], legacyRecord["files"]), "Retained record with now-refused content still reopens exactly");
        string incomplete = Path.Combine(root, "hmi-component-reviews", "partial.tmp");
        var retry = uploads.Begin("again.json", raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw))); uploads.Write(retry.UploadId, 0, Convert.ToBase64String(raw)); string retryRef = uploads.Commit(retry.UploadId).ArtifactRef!;
        string candidatePath = Path.Combine(root, (string)receipt["files"]![0]!["artifact"]!["path"]!); byte[] originalCandidate = File.ReadAllBytes(candidatePath); File.Delete(candidatePath);
        try
        {
            using (var file = File.Create(incomplete)) file.SetLength(512L * 1024 * 1024);
            Reject(() => store.AuthorTextLists(retryRef), "Incomplete files count against retention capacity");
        }
        finally { File.Delete(incomplete); File.WriteAllBytes(candidatePath, originalCandidate); }
        Check(JsonNode.DeepEquals(receipt, store.AuthorTextLists(retryRef)), "Retry after incomplete publication preserves exact record"); uploads.Release(retry.UploadId);
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS Unified text-list authoring: {checks} checks");
    }
}
