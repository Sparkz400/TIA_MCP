using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.HmiComponents;

internal static class NumericTextListGenerationTests
{
    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "text-numeric"); Directory.CreateDirectory(root);
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
        string Snapshot(string dataType = "Int", bool installed = false, string memberName = "Run")
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
                ["schemaVersion"] = "hmi-component-review-request.v7", ["operation"] = update ? "update" : "install", ["reason"] = "Synthetic exact integer mapping.",
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
                    ["afterMember"] = Member(), ["declaredHmiType"] = "Int", ["reason"] = "Explicit integer rule." }),
                ["files"] = files, ["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 100, ["maxImpactEdgeVisits"] = 1000 },
                ["observations"] = new JsonArray(), ["expectations"] = new JsonArray(), ["libraryAssociations"] = new JsonArray(), ["dependencyAssociations"] = new JsonArray() };
        }
        JsonObject Request(JsonObject packet, JsonObject? previous = null)
        {
            var review = store.Review(Input(packet));
            ReleaseUploads();
            return new() { ["schemaVersion"] = "unified-text-list-author-request.v3", ["profile"] = "unified-integer-state-text-list.v1",
                ["componentReviewId"] = review["recordId"]!.DeepClone(), ["outputId"] = "states", ["bindingId"] = "run",
                ["previousRecordId"] = previous?["recordId"]?.DeepClone(), ["retainOutputIds"] = new JsonArray("manual"),
                ["template"] = new JsonObject { ["fileBase"] = "States", ["textLibrary"] = "StateLibrary", ["languages"] = new JsonArray("en-US", "de-DE"), ["defaultLanguage"] = "en-US",
                    ["dataType"] = "Int", ["requireFullTypeCoverage"] = false, ["states"] = new JsonArray(State("Idle", 0, 0), State("Running", 1, 10)) } };
        }
        JsonObject Author(JsonObject request) { try { return store.AuthorTextLists(Input(request)); } finally { ReleaseUploads(); } }
        JsonObject State(string id, long from, long to) => new() { ["id"] = id, ["type"] = from == to ? "SingleValue" : "Range",
            ["fromValue"] = from, ["toValue"] = to, ["isDefault"] = false, ["translations"] = new JsonObject { ["en-US"] = id, ["de-DE"] = id + " Deutsch" } };
        var cases = new JsonArray();
        JsonObject Record(JsonObject request, string label, string expected = "candidatesReady")
        {
            var result = Author(request); Check((string?)result["status"] == expected, label);
            Check(result["files"]!.AsArray().Count == (expected == "candidatesReady" ? 2 : 0), "Atomic candidate pair: " + label);
            Check(JsonNode.DeepEquals(result, new HmiComponentWorkflowStore(root).AuthorTextLists(recordId: (string)result["recordId"]!)), "Released exact reopen: " + label);
            cases.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone(), ["receipt"] = result.DeepClone() }); return result;
        }
        var installRequest = Request(Packet()); var first = Record(installRequest, "initial bounded states");
        Check(first["valueCoverage"]!["unlistedIntervals"]!.AsArray().Count == 2, "Both negative and high unlisted intervals remain visible");
        Check(!(bool)first["dependencyCoverage"]!["allObservedSelectedIndicesMatched"]!, "Unavailable dependency observation never means empty closure");
        var updateRequest = Request(Packet(first), first);
        updateRequest["template"]!["states"] = new JsonArray(State("Idle", 0, 0), State("Active", 1, 20));
        updateRequest["template"]!["states"]![0]!["translations"]!["en-US"] = "Stopped 😀\nNo native execution";
        updateRequest["template"]!["languages"] = new JsonArray("de-DE", "en-US"); updateRequest["template"]!["defaultLanguage"] = "de-DE";
        var second = Record(updateRequest, "regeneration with added removed and translated states");
        Check(second["stateChanges"]!.AsArray().Select(c => (string)c!["change"]!).ToHashSet().SetEquals(new[] { "added", "removed", "changed" }), "Every explicit state delta retained");
        Check(ProjectReviewStore.ReadArtifact(root, second["preservedFiles"]![0]!["artifact"]!.AsObject()).SequenceEqual(manual), "Manual peer stays byte exact");
        var unchanged = Record(Request(Packet(first), first), "unchanged state regeneration");
        Check(unchanged["stateChanges"]!.AsArray().All(c => (string?)c!["change"] == "unchanged"), "Unchanged state identities remain explicit");
        var conversionPacket = Packet(first); conversionPacket["afterComponent"] = Component(Snapshot("DInt"));
        conversionPacket["bindings"]![0]!["declaredHmiType"] = "DInt";
        var conversion = Request(conversionPacket, first); conversion["template"]!["dataType"] = "DInt";
        Record(conversion, "integer conversion requires separate policy", "conflict");
        var renamePacket = Packet(first); renamePacket["afterComponent"] = Component(Snapshot("Int", true, "OperatingState"));
        renamePacket["bindings"]![0]!["afterMember"]!["memberPath"] = new JsonArray("OperatingState");
        Record(Request(renamePacket, first), "explicit renamed integer member");
        foreach (var (type, min, max) in new[] { ("SInt", -128L, 127L), ("USInt", 0L, 255L), ("Int", -32768L, 32767L),
            ("UInt", 0L, 65535L), ("DInt", -2147483648L, 2147483647L), ("UDInt", 0L, 4294967295L), ("LInt", long.MinValue, long.MaxValue) })
        {
            var packet = Packet(); packet["afterComponent"] = Component(Snapshot(type)); packet["bindings"]![0]!["declaredHmiType"] = type;
            var request = Request(packet); request["template"]!["dataType"] = type; request["template"]!["requireFullTypeCoverage"] = true;
            request["template"]!["states"] = new JsonArray(State("EveryValue", min, max));
            var result = Record(request, "full domain " + type);
            Check((bool)result["valueCoverage"]!["allTypeValuesListed"]! && result["valueCoverage"]!["unlistedIntervals"]!.AsArray().Count == 0, "Exact extrema without overflow: " + type);
            foreach (long endpoint in new[] { min, max })
            {
                var narrow = request.DeepClone().AsObject(); narrow["template"]!["requireFullTypeCoverage"] = false;
                narrow["template"]!["states"] = new JsonArray(State("Endpoint", endpoint, endpoint));
                Record(narrow, "single endpoint " + type + " " + endpoint);
            }
        }
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("unknown type", p => p["template"]!["dataType"] = "ULInt"),
            ("fraction", p => p["template"]!["states"]![0]!["fromValue"] = 0.1),
            ("string integer", p => p["template"]!["states"]![0]!["fromValue"] = "0"),
            ("null bound", p => p["template"]!["states"]![0]!["fromValue"] = null),
            ("outside domain", p => p["template"]!["states"]![1]!["toValue"] = 32768),
            ("reversed", p => p["template"]!["states"]![1]!["fromValue"] = 12),
            ("overlap", p => p["template"]!["states"]![1]!["fromValue"] = 0),
            ("duplicate ID", p => p["template"]!["states"]![1]!["id"] = "Idle"),
            ("open range", p => p["template"]!["states"]![1]!["type"] = "From"),
            ("null state id", p => p["template"]!["states"]![0]!["id"] = null),
            ("empty states", p => p["template"]!["states"] = new JsonArray()),
            ("invalid identifier", p => p["template"]!["states"]![0]!["id"] = "not an ID"),
            ("missing language", p => p["template"]!["states"]![1]!["translations"]!.AsObject().Remove("de-DE")),
            ("extra state field", p => p["template"]!["states"]![0]!["nativeQualified"] = true),
            ("too many states", p => { var s = p["template"]!["states"]!.AsArray(); while (s.Count <= 256) s.Add(State("S" + s.Count, s.Count + 100, s.Count + 100)); }) })
        { var value = installRequest.DeepClone().AsObject(); edit(value); Reject(() => Author(value), label); }
        var boundedPacket = Packet(); boundedPacket["afterComponent"] = Component(Snapshot("USInt")); boundedPacket["bindings"]![0]!["declaredHmiType"] = "USInt";
        var bounded = Request(boundedPacket); bounded["template"]!["dataType"] = "USInt"; bounded["template"]!["requireFullTypeCoverage"] = true;
        bounded["template"]!["states"] = new JsonArray(Enumerable.Range(0, 256).Reverse().Select(i => (JsonNode?)State("S" + i, i, i)).ToArray());
        Record(bounded, "all 256 explicit states cover USInt independent of input order");
        var fullRequired = installRequest.DeepClone().AsObject(); fullRequired["template"]!["requireFullTypeCoverage"] = true;
        fullRequired["template"]!["states"]![0]!["isDefault"] = true;
        Record(fullRequired, "default cannot manufacture full type coverage", "conflict");
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("manual edited current", p => { var file = p["files"]![1]!; p["files"]!.AsArray()[1] = File((string)file["id"]!, (string)file["fileName"]!, Encoding.UTF8.GetBytes("edited")); }),
            ("manual owner", p => p["outputs"]![0]!["ownership"] = "manual"),
            ("missing companion", p => p["outputs"]![0]!["current"]!.AsArray().RemoveAt(1)),
            ("removed member", p => p["bindings"]![0]!["afterMember"] = null),
            ("changed type", p => p["afterComponent"] = Component(Snapshot("DInt"))),
            ("foreign predecessor source", p => p["beforeComponent"] = Component(installed)),
            ("manual collision", p => p["files"]![0]!["fileName"] = "STATES.hmi.yml") })
        { var packet = Packet(first); edit(packet); Record(Request(packet, first), label, "conflict"); }
        // Genuine retained v7 observations exercise unavailable/empty/repeated and
        // contradicted lists independently of the authoring implementation.
        const string dependency = "/P/PLC/Dependency", unrelated = "/P/PLC/Unrelated";
        const string typeGuid = "20000000-0000-0000-0000-000000000001", ownerVersion = "10000000-0000-0000-0000-000000000001",
            requiredVersion = "10000000-0000-0000-0000-000000000002", wrongVersion = "10000000-0000-0000-0000-000000000003";
        JsonObject Observation(string version, params string[] required) => new() { ["schemaVersion"] = "plc-library-instance-observation.v1",
            ["source"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["status"] = "linked", ["typeGuid"] = typeGuid, ["versionGuid"] = version,
            ["dependencies"] = new JsonObject { ["schemaVersion"] = "plc-library-dependencies.v1", ["source"] = "LibraryTypeVersion.Dependencies",
                ["status"] = "observed", ["requiredVersionGuids"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()) } };
        foreach (string mode in new[] { "empty", "missing", "unavailable", "repeated", "different", "unrelated", "incoming", "incoming-missing", "matched", "repeated-matched" })
        {
            var saved = reviews.Read(before); var model = saved["model"]!.DeepClone().AsObject();
            var node = model["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["objectPath"] == block);
            node["metadata"]!["libraryInstance"] = mode is "empty" or "unrelated" or "incoming" or "incoming-missing" ? Observation(ownerVersion) :
                mode is "repeated" or "repeated-matched" ? Observation(ownerVersion, requiredVersion, requiredVersion) : Observation(ownerVersion, requiredVersion);
            var target = node.DeepClone().AsObject(); target["objectPath"] = dependency; target["nodeId"] = dependency; target["name"] = "Dependency";
            target["metadata"]!["libraryInstance"] = Observation(mode is "different" or "unrelated" or "incoming" ? wrongVersion : requiredVersion);
            target["metadata"]!["kind"] = "FB";
            model["nodes"]!.AsArray().Add(target);
            if (mode is "unrelated" or "incoming" or "incoming-missing")
            {
                var other = node.DeepClone().AsObject(); other["objectPath"] = unrelated; other["nodeId"] = unrelated; other["name"] = "Unrelated";
                other["metadata"]!["libraryInstance"] = Observation(ownerVersion, requiredVersion); model["nodes"]!.AsArray().Add(other);
                if (mode != "unrelated") model["edges"]!.AsArray().Add(new JsonObject {
                    ["edgeId"] = "selected-consumes-owner", ["kind"] = "usesType",
                    ["source"] = new JsonObject { ["objectPath"] = block, ["nodeId"] = block },
                    ["target"] = new JsonObject { ["objectPath"] = unrelated, ["nodeId"] = unrelated },
                    ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved" }, ["unresolved"] = null });
            }
            string id = (string)reviews.Save(model, saved["documents"]!.DeepClone().AsArray(), new() { ["syntheticFixture"] = true })["reviewId"]!;
            var packet = Packet(); packet["afterComponent"] = Component(id); packet["afterComponent"]!["objectPaths"]!.AsArray().Add(dependency);
            if (mode is "unrelated" or "incoming" or "incoming-missing") packet["afterComponent"]!["objectPaths"]!.AsArray().Add(unrelated);
            if (mode is not ("empty" or "missing" or "incoming-missing")) packet["dependencyAssociations"]!.AsArray().Add(new JsonObject {
                ["scope"] = "afterComponent", ["objectPath"] = mode is "unrelated" or "incoming" ? unrelated : block, ["dependencyIndex"] = 0,
                ["targetObjectPath"] = dependency, ["reason"] = "Exact saved dependency index; no native identity claim." });
            if (mode is "matched" or "repeated-matched")
            {
                // Synthetic structurally supported full FB identity, never native provenance.
                byte[] export = Encoding.UTF8.GetBytes($"<Document><Engineering version=\"V21\"/><SW.Blocks.CodeBlockLibraryTypeVersion><AttributeList><LibraryTypeGuid ReadOnly=\"true\">{typeGuid}</LibraryTypeGuid><Guid ReadOnly=\"true\">{requiredVersion}</Guid><LibraryTypeName ReadOnly=\"true\">Dependency</LibraryTypeName><State ReadOnly=\"true\">Committed</State><VersionNumber>1.0.0</VersionNumber></AttributeList><ObjectList><SW.Blocks.FB CompositionName=\"ContentObject\"><AttributeList><Name>Dependency</Name></AttributeList></SW.Blocks.FB></ObjectList></SW.Blocks.CodeBlockLibraryTypeVersion></Document>");
                packet["files"]!.AsArray().Add(File("export", "dependency.xml", export));
                packet["libraryAssociations"]!.AsArray().Add(new JsonObject { ["scope"] = "afterComponent", ["objectPath"] = dependency,
                    ["fileId"] = "export", ["profile"] = "v21-full-fb-library-version.v1", ["reason"] = "Synthetic structurally supported identity fixture." });
                if (mode == "repeated-matched")
                {
                    var next = packet["dependencyAssociations"]![0]!.DeepClone(); next["dependencyIndex"] = 1;
                    packet["dependencyAssociations"]!.AsArray().Add(next);
                }
            }
            var request = Request(packet); var result = Record(request, "dependency " + mode, mode is "different" or "incoming" ? "conflict" : "candidatesReady");
            Check((bool)result["dependencyCoverage"]!["allObservedSelectedIndicesMatched"]! == (mode is "empty" or "unrelated" or "matched" or "repeated-matched"), "Selected scope coverage " + mode);
            if (mode is "different" or "incoming")
            {
                var report = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, store.Page((string)request["componentReviewId"]!)["reportArtifact"]!.AsObject()))!["report"]!.DeepClone().AsObject();
                report["dependencyImpacts"] = new JsonArray(); report["dependencyImpactCoverage"]![0]!["truncated"] = true;
                Check((int)HmiNumericTextLists.Dependencies(report, report["bindings"]![0]!.AsObject())["contradictionCount"]! == 1, "Impact-page omission cannot hide selected original contradiction");
            }
        }
        var v1 = Author(first["intent"]!.AsObject()); Record(Request(Packet(v1), v1), "explicit v1 predecessor missing lineage");
        foreach (var original in cases.OfType<JsonObject>())
        {
            var result = original["receipt"]!;
            foreach (string field in new[] { "valueCoverage", "dependencyCoverage", "stateChanges" })
            {
                var record = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, result["recordArtifact"]!.AsObject()))!.AsObject();
                record[field] = field == "stateChanges" ? new JsonArray() : new JsonObject();
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json); string id = Hash(data);
                System.IO.File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", id + ".author.json"), data);
                Reject(() => store.AuthorTextLists(recordId: id), "Rehashed invented " + field);
            }
        }
        System.IO.File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, cases }, BridgeProtocol.Json));
        System.IO.File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS numeric component text-list generation: {checks} checks");
    }
}
