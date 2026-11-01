using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.HmiComponents;

// The Host side of change kind hmi_text_list_import: reopen the retained record, refuse content V21's import changes, hand over the exact pair.
internal static class TextListImportPayloadTests
{
    internal static void Run(string parent, string repository)
    {
        string root = Path.Combine(parent, "text-list-import"); int checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        void Refuse(Func<JsonObject> action, string label, string code)
        { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code + ": " + label); }
        var records = new List<(string Id, JsonObject Receipt)>();
        foreach (string fixture in new[] { "fixtures/hmi-coordinated/retained-v1-v3", "fixtures/hmi-evolution/retained-v1-sets" })
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(repository, fixture, "fixture.json")))!.AsObject();
            foreach (var file in manifest["files"]!.AsArray())
            {
                string copy = Path.Combine(root, (string)file!["path"]!); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Path.Combine(repository, fixture, "store", (string)file["path"]!), copy, true);
            }
            records.AddRange(manifest["records"]!.AsArray().Select(r => ((string)r!["recordId"]!, r["receipt"]!.AsObject())));
        }
        var store = new HmiComponentWorkflowStore(root);
        var target = new JsonObject { ["family"] = "unified", ["kind"] = "hmiDevice", ["hmiPath"] = "/d/hmi/H", ["objectPath"] = "/d/hmi/H" };
        JsonObject Payload(string recordId, string? outputId = null, Action<JsonObject>? edit = null)
        {
            var request = new JsonObject { ["schemaVersion"] = 1, ["target"] = target.DeepClone(), ["expectedContentHash"] = new string('a', 64), ["recordId"] = recordId };
            if (outputId is not null) request["outputId"] = outputId;
            edit?.Invoke(request);
            return store.TextListImportPayload(JsonSerializer.SerializeToElement(request));
        }
        foreach (var (id, receipt) in records)
        {
            var files = receipt["files"]!.AsArray();
            foreach (string? output in files.Select(f => (string?)f!["outputId"]).Distinct())
            {
                var payload = Payload(id, output);
                var expected = files.Where(f => (string?)f!["outputId"] == output).ToArray();
                var rows = payload["candidate"]!["files"]!.AsArray();
                Check(rows.Count == 2 && rows.Select(r => (string)r!["fileName"]!).SequenceEqual(expected.Select(f => (string)f!["fileName"]!)), "Exact pair for " + id[..12] + "/" + output);
                foreach (var row in rows)
                    Check(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes((string)row!["path"]!))) == (string)row["sha256"]! && Path.IsPathRooted((string)row["path"]!), "Absolute hash-exact candidate path");
                Check(JsonNode.DeepEquals(payload["target"], target) && (string?)payload["candidate"]!["recordKind"] == (output is null ? "author" : "set") && payload["candidate"]!["lists"]!.AsArray().Count >= 1, "Target, kind and lists bound");
            }
            Refuse(() => Payload(id, files[0]!["outputId"] is null ? "run" : null), "author/set outputId mismatch " + id[..12], "InvalidRequest");
        }
        string set = records.First(r => r.Receipt["files"]![0]!["outputId"] is not null).Id;
        Refuse(() => Payload(set, "missing"), "unknown set output", "HmiTextListCandidateUnavailable");
        Refuse(() => Payload(new string('0', 64)), "unknown record", "HmiTextListRecordNotFound");
        Refuse(() => Payload(records[0].Id, edit: r => r["candidate"] = new JsonObject()), "client cannot supply the candidate", "InvalidRequest");
        Refuse(() => Payload(records[0].Id, edit: r => r["schemaVersion"] = 2), "schema version", "InvalidRequest");
        Refuse(() => Payload(records[0].Id, edit: r => r.Remove("target")), "target required", "InvalidRequest");
        // A changed retained candidate fails the reopen before anything is handed over.
        string candidatePath = Path.Combine(root, (string)records[0].Receipt["files"]![0]!["artifact"]!["path"]!); byte[] saved = File.ReadAllBytes(candidatePath);
        try { File.AppendAllText(candidatePath, "#"); Refuse(() => Payload(records[0].Id), "changed candidate", "ArtifactChanged"); }
        finally { File.WriteAllBytes(candidatePath, saved); }
        // Records published before the refusals existed still reopen, but their import is refused with the authoring code.
        var request = HmiComponentReview.Parse(File.ReadAllBytes(Path.Combine(repository, "fixtures/hmi/unified-text-list/author-request.json")));
        string reviews = Path.Combine(root, "hmi-component-reviews");
        string Retain(byte[] data, string suffix) { string digest = Convert.ToHexStringLower(SHA256.HashData(data)); File.WriteAllBytes(Path.Combine(reviews, digest + suffix), data); return digest; }
        JsonObject Ref(string digest, string suffix) => new() { ["path"] = "hmi-component-reviews/" + digest + suffix, ["sha256"] = digest };
        foreach (var (code, edit) in new (string, Action<JsonObject>)[] {
            ("HmiTextListValueOutOfRange", r => { r["lists"]![0]!["entries"]![1]!["fromValue"] = 2147483648L; r["lists"]![0]!["entries"]![1]!["toValue"] = 2147483648L; }),
            ("HmiTextListBoolDefaultEntry", r => r["lists"]![0]!["rangeType"] = "Bool"),
            ("HmiTextListDefaultEntryValue", r => { r["lists"]![0]!["entries"]![0]!["isDefault"] = false; r["lists"]![0]!["entries"]![1]!["isDefault"] = true; }),
            ("HmiTextListTextUnsupported", r => r["texts"]![0]!["translations"]!["en-US"] = "line\r\nbreak"),
            ("HmiTextListNameUnsupported", r => r["lists"]![0]!["name"] = "Dot.Name") })
        {
            var legacy = request.DeepClone().AsObject(); edit(legacy);
            var record = new JsonObject { ["schemaVersion"] = "unified-text-list-author-record.v1",
                ["input"] = Ref(Retain(JsonSerializer.SerializeToUtf8Bytes(legacy, BridgeProtocol.Json), ".author-input"), ".author-input"),
                ["files"] = new JsonArray(UnifiedTextListAuthoring.Build(legacy).Select(c => (JsonNode?)new JsonObject { ["fileName"] = c.Name, ["bytes"] = c.Bytes.Length, ["artifact"] = Ref(Retain(c.Bytes, ".candidate"), ".candidate") }).ToArray()),
                ["listCount"] = 1, ["textCount"] = 2, ["languageCount"] = 2, ["nativeExecutable"] = false, ["nativeQualified"] = false, ["preservationVerified"] = false, ["consumerCoverageComplete"] = false,
                ["policy"] = (string)records[0].Receipt["policy"]! };
            string id = Retain(JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json), ".author.json");
            Check((string)store.AuthorTextLists(recordId: id)["recordId"]! == id, "Legacy record still reopens: " + code);
            Refuse(() => Payload(id), "legacy import", code);
        }
        // Actual records of runs q/tl2 and q/tl3, published before the refusals existed.
        var refused = JsonNode.Parse(File.ReadAllBytes(Path.Combine(repository, "fixtures/hmi/unified-text-list/retained-refused/fixture.json")))!.AsObject();
        foreach (var file in refused["files"]!.AsArray())
            File.Copy(Path.Combine(repository, "fixtures/hmi/unified-text-list/retained-refused/store", (string)file!["path"]!), Path.Combine(root, (string)file["path"]!), true);
        foreach (var row in refused["records"]!.AsArray())
        {
            string id = (string)row!["recordId"]!;
            Check((string)store.AuthorTextLists(recordId: id)["recordId"]! == id, "Actual prior record still reopens: " + id[..12]);
            Refuse(() => Payload(id), "actual prior record import", (string)row["expectedCode"]!);
        }
        Console.WriteLine($"PASS Unified text-list import payload: {checks} checks");
    }
}
