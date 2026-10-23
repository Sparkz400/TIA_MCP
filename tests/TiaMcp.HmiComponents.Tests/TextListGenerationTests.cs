using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class TextListGenerationTests
{
    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "text-generation"); Directory.CreateDirectory(root);
        var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root); var store = new HmiComponentWorkflowStore(root);
        var uploadIds = new List<string>(); int checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is BridgeException or JsonException or InvalidOperationException or ArgumentException) { checks++; return; } throw new Exception("Expected refusal: " + label); }
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        string Upload(byte[] data)
        {
            var started = uploads.Begin("input.json", data.Length, Hash(data)); uploadIds.Add(started.UploadId);
            for (int offset = 0; offset < data.Length; offset += started.MaxChunkBytes)
                uploads.Write(started.UploadId, offset, Convert.ToBase64String(data, offset, Math.Min(started.MaxChunkBytes, data.Length - offset)));
            return uploads.Commit(started.UploadId).ArtifactRef!;
        }
        string Input(JsonObject request) => Upload(JsonSerializer.SerializeToUtf8Bytes(request, BridgeProtocol.Json));
        void ReleaseUploads() { foreach (string id in uploadIds) uploads.Release(id); uploadIds.Clear(); }
        const string block = "/P/PLC/Block", hmi = "/P/HMI", resource = hmi + "/States", peer = hmi + "/Manual";
        string Snapshot(string dataType = "Bool", bool installed = false, string memberName = "Run")
        {
            var nodes = new JsonArray();
            void Node(string path, string kind, string? parentPath, string name, JsonObject? metadata = null) =>
                nodes.Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["parentId"] = parentPath, ["name"] = name, ["metadata"] = metadata ?? new() });
            Node("/P", "project", null, "P"); Node("/P/PLC", "plc", "/P", "PLC"); Node(block, "block", "/P/PLC", "Block");
            Node(hmi, "hmi", "/P", "HMI", new() { ["family"] = "unified" });
            JsonObject Identity() => new() { ["hmiIdentity"] = new JsonObject { ["family"] = "unified", ["hmiPath"] = hmi }, ["properties"] = new JsonObject { ["Value"] = "Run" } };
            if (installed) Node(resource, "hmiResource", hmi, "States", Identity());
            Node(peer, "hmiScript", hmi, "Manual", Identity());
            string xml = $"<Document><SW.Blocks.FB><AttributeList><Name>Block</Name><Interface><Sections><Section Name=\"Input\"><Member Name=\"{memberName}\" Datatype=\"{dataType}\"/></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>";
            return (string)reviews.Save(new() { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21" },
                    ["nodes"] = nodes, ["edges"] = new JsonArray() },
                new(new JsonObject { ["objectPath"] = block, ["status"] = "exported", ["xml"] = xml, ["utf8Sha256"] = Hash(Encoding.UTF8.GetBytes(xml)) }), new() { ["syntheticFixture"] = true })["reviewId"]!;
        }
        string before = Snapshot(), installed = Snapshot(installed: true), real = Snapshot("Real", true);
        JsonObject Component(string id) => new() { ["reviewId"] = id, ["objectPaths"] = new JsonArray(block) };
        JsonObject Member() => new() { ["objectPath"] = block, ["memberPath"] = new JsonArray("Run") };
        var manual = new byte[] { 0, 255, 12, 34, 56 };
        JsonObject File(string id, string name, byte[] data) => new() { ["id"] = id, ["fileName"] = name, ["uploadRef"] = Upload(data), ["sha256"] = Hash(data), ["bytes"] = data.Length };
        JsonObject Packet(JsonObject? previous = null)
        {
            bool update = previous is not null; var files = new JsonArray(File("peer", "manual.js", manual)); var ids = new JsonArray();
            if (previous is not null) foreach (var f in previous["files"]!.AsArray())
            {
                string id = "current-" + ids.Count; ids.Add(id);
                files.Add(File(id, (string)f!["fileName"]!, ProjectReviewStore.ReadArtifact(root, f["artifact"]!.AsObject())));
            }
            return new() {
                ["schemaVersion"] = "hmi-component-review-request.v7", ["operation"] = update ? "update" : "install", ["reason"] = "Synthetic exact Bool mapping.",
                ["beforeComponent"] = update ? Component(before) : null, ["afterComponent"] = Component(before),
                ["componentMatches"] = update ? new JsonArray(new JsonObject { ["beforeObjectPath"] = block, ["afterObjectPath"] = block, ["reason"] = "Exact selected source." }) : new JsonArray(),
                ["destination"] = new JsonObject { ["reviewId"] = update ? installed : before, ["hmiPath"] = hmi, ["family"] = "unified" },
                ["outputs"] = new JsonArray(
                    new JsonObject { ["id"] = "states", ["objectPath"] = update ? resource : null, ["parentPath"] = hmi, ["name"] = "States", ["kind"] = "hmiResource",
                        ["action"] = update ? "update" : "create", ["ownership"] = "generated", ["baseline"] = ids.DeepClone(), ["current"] = ids.DeepClone(),
                        ["proposed"] = new JsonArray(), ["baselineComplete"] = true, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Explicit authored candidate scope." },
                    new JsonObject { ["id"] = "manual", ["objectPath"] = peer, ["parentPath"] = hmi, ["name"] = "Manual", ["kind"] = "hmiScript",
                        ["action"] = "retain", ["ownership"] = "manual", ["baseline"] = new JsonArray(), ["current"] = new JsonArray("peer"), ["proposed"] = new JsonArray(),
                        ["baselineComplete"] = false, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Preserve these exact independently supplied bytes." }),
                ["bindings"] = new JsonArray(new JsonObject { ["id"] = "run", ["outputId"] = "states", ["property"] = "Value", ["beforeMember"] = update ? Member() : null,
                    ["afterMember"] = Member(), ["declaredHmiType"] = "Bool", ["reason"] = "Explicit Bool rule." }),
                ["files"] = files, ["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 100, ["maxImpactEdgeVisits"] = 1000 },
                ["observations"] = new JsonArray(), ["expectations"] = new JsonArray(), ["libraryAssociations"] = new JsonArray(), ["dependencyAssociations"] = new JsonArray() };
        }
        JsonObject Request(JsonObject packet, JsonObject? previous = null)
        {
            var review = store.Review(Input(packet));
            ReleaseUploads();
            return new() { ["schemaVersion"] = "unified-text-list-author-request.v2", ["profile"] = "unified-bool-binding-text-list.v1",
                ["componentReviewId"] = review["recordId"]!.DeepClone(), ["outputId"] = "states", ["bindingId"] = "run",
                ["previousRecordId"] = previous?["recordId"]?.DeepClone(), ["retainOutputIds"] = new JsonArray("manual"),
                ["template"] = new JsonObject { ["fileBase"] = "States", ["textLibrary"] = "StateLibrary", ["languages"] = new JsonArray("en-US", "de-DE"), ["defaultLanguage"] = "en-US",
                    ["falseText"] = new JsonObject { ["en-US"] = "Off", ["de-DE"] = "Aus" }, ["trueText"] = new JsonObject { ["en-US"] = "On", ["de-DE"] = "Ein" } } };
        }
        JsonObject Author(JsonObject request) { try { return store.AuthorTextLists(Input(request)); } finally { ReleaseUploads(); } }
        var installRequest = Request(Packet()); var first = Author(installRequest);
        Check((string?)first["status"] == "candidatesReady" && first["files"]!.AsArray().Count == 2, "Install creates a complete pair from selected Bool binding");
        Check(JsonNode.DeepEquals(first, Author(installRequest)), "Retry deterministic");
        var updateRequest = Request(Packet(first), first); updateRequest["template"]!["trueText"]!["en-US"] = "Ready 😀\nManual content stays separate";
        updateRequest["template"]!["languages"] = new JsonArray("de-DE", "en-US"); updateRequest["template"]!["defaultLanguage"] = "de-DE";
        var second = Author(updateRequest);
        Check((string?)second["status"] == "candidatesReady", "Changed bilingual regeneration produces candidates");
        Check((string?)first["files"]![0]!["artifact"]!["sha256"] == (string?)second["files"]![0]!["artifact"]!["sha256"], "Translation change preserves configuration bytes");
        Check((string?)first["files"]![1]!["artifact"]!["sha256"] != (string?)second["files"]![1]!["artifact"]!["sha256"], "Translation change affects text-library bytes");
        Check(ProjectReviewStore.ReadArtifact(root, second["preservedFiles"]![0]!["artifact"]!.AsObject()).SequenceEqual(manual), "Opaque manual peer bytes retained exactly");
        var renamedPacket = Packet(first); renamedPacket["afterComponent"] = Component(Snapshot("Bool", true, "Enabled"));
        renamedPacket["bindings"]![0]!["afterMember"]!["memberPath"] = new JsonArray("Enabled");
        var renameRequest = Request(renamedPacket, first); var renamedMember = Author(renameRequest);
        Check((string?)renamedMember["status"] == "candidatesReady" && (string?)renamedMember["componentContext"]!["binding"]!["change"] == "explicitMemberMapping",
            "Explicit renamed Bool member supports changed-interface regeneration");
        var conflicts = new JsonArray();
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("manual owner", p => p["outputs"]![0]!["ownership"] = "manual"),
            ("unknown owner", p => p["outputs"]![0]!["ownership"] = "unknown"),
            ("incomplete current", p => p["outputs"]![0]!["currentComplete"] = false),
            ("missing pair file", p => p["outputs"]![0]!["current"]!.AsArray().RemoveAt(0)),
            ("manual edit", p => { var f = p["files"]![1]!; p["files"]!.AsArray()[1] = File((string)f["id"]!, (string)f["fileName"]!, Encoding.UTF8.GetBytes("manually changed")); }),
            ("changed type", p => p["afterComponent"] = Component(real)),
            ("removed member", p => p["bindings"]![0]!["afterMember"] = null),
            ("incomplete peer", p => p["outputs"]![1]!["currentComplete"] = false),
            ("peer filename collision", p => p["files"]![0]!["fileName"] = "sTATES.hmi.yml"),
            ("foreign before lineage", p => p["beforeComponent"] = Component(installed)) })
        {
            var packet = Packet(first); edit(packet); var request = Request(packet, first); var result = Author(request);
            Check((string?)result["status"] == "conflict" && result["files"]!.AsArray().Count == 0, "Conflicts suppress candidates: " + label);
            conflicts.Add(new JsonObject { ["label"] = label, ["request"] = request, ["receipt"] = result });
        }
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("unknown profile", p => p["profile"] = "invented"), ("unknown output", p => p["outputId"] = "missing"),
            ("unknown binding", p => p["bindingId"] = "missing"), ("duplicate peer", p => p["retainOutputIds"]!.AsArray().Add("manual")),
            ("generated peer", p => p["retainOutputIds"]!.AsArray().Add("states")), ("extra claim", p => p["nativeQualified"] = true),
            ("missing language", p => p["template"]!["trueText"]!.AsObject().Remove("de-DE")) })
        { var request = updateRequest.DeepClone().AsObject(); edit(request); Reject(() => Author(request), label); }
        var renamed = updateRequest.DeepClone().AsObject(); renamed["template"]!["fileBase"] = "Renamed";
        Check((string?)Author(renamed)["status"] == "conflict", "File rename cannot discard old generated pair");
        var noPredecessor = updateRequest.DeepClone().AsObject(); noPredecessor["previousRecordId"] = null;
        Check((string?)Author(noPredecessor)["status"] == "conflict", "Update requires predecessor");
        var v1 = Author(first["intent"]!.AsObject()); var migration = Author(Request(Packet(v1), v1));
        Check((string?)migration["status"] == "candidatesReady" && migration["gaps"]!.AsArray().Any(g => ((string)g!).Contains("v1 predecessor")), "Prior v1 records remain usable with explicit missing-lineage gap");
        var chain = first;
        for (int i = 0; i < 16; i++) { var request = Request(Packet(chain), chain); chain = Author(request); Check((string?)chain["status"] == "candidatesReady", "Bounded lineage step " + i); }
        var beyond = Request(Packet(chain), chain); Reject(() => Author(beyond), "Excess predecessor depth refuses before publication");
        foreach (string id in uploadIds) uploads.Release(id);
        foreach (var result in new[] { first, second, renamedMember, migration, chain }.Concat(conflicts.OfType<JsonObject>().Select(c => c["receipt"]!.AsObject())))
            Check(JsonNode.DeepEquals(result, new HmiComponentWorkflowStore(root).AuthorTextLists(recordId: (string)result["recordId"]!)), "Exact reopen after release, including conflicts and lineage");
        foreach (string flag in new[] { "nativeExecutable", "nativeQualified", "preservationVerified", "consumerCoverageComplete", "ownershipVerified" }) Check(!(bool)second[flag]!, "No qualification promotion " + flag);
        var full = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, second["recordArtifact"]!.AsObject()))!.AsObject();
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("qualification", r => r["nativeQualified"] = true), ("translation", r => r["intent"]!["texts"]![0]!["translations"]!["en-US"] = "wrong"),
            ("lineage", r => r["previousRecordId"] = null), ("preserved content", r => r["preservedFiles"]![0]!["bytes"] = 4) })
        {
            var forged = full.DeepClone().AsObject(); edit(forged); byte[] data = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string id = Hash(data);
            System.IO.File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", id + ".author.json"), data);
            Reject(() => store.AuthorTextLists(recordId: id), "Rehashed forged " + label);
        }
        foreach (var artifact in new[] { first["input"]!.AsObject(), first["files"]![0]!["artifact"]!.AsObject(), second["preservedFiles"]![0]!["artifact"]!.AsObject() })
        {
            string path = Path.Combine(root, (string)artifact["path"]!); byte[] original = System.IO.File.ReadAllBytes(path);
            try { System.IO.File.AppendAllText(path, "changed"); Reject(() => store.AuthorTextLists(recordId: (string)second["recordId"]!), "Tampered predecessor/source refuses"); }
            finally { System.IO.File.WriteAllBytes(path, original); }
        }
        System.IO.File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, installRequest, updateRequest, renameRequest, first, second, renamedMember, migration, conflicts }, BridgeProtocol.Json));
        System.IO.File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS component-bound text-list generation: {checks} checks");
    }
}
