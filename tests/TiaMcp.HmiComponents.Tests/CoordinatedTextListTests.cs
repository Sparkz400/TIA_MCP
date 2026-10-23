using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class CoordinatedTextListTests
{
    internal static void Run(string parent)
    {
        string root = Path.Combine(parent, "text-coordinated"); Directory.CreateDirectory(root);
        var store = new HmiComponentWorkflowStore(root); var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root);
        var uploadIds = new List<string>(); var cases = new JsonArray(); int checks = 0;
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        void Reject(Action action, string label) { try { action(); } catch (Exception e) when (e is BridgeException or ArgumentException or JsonException or InvalidOperationException) { checks++; return; } throw new Exception("Expected refusal: " + label); }
        string Upload(byte[] bytes)
        {
            var begin = uploads.Begin("input.json", bytes.Length, Hash(bytes)); uploadIds.Add(begin.UploadId);
            for (int offset = 0; offset < bytes.Length; offset += begin.MaxChunkBytes)
                uploads.Write(begin.UploadId, offset, Convert.ToBase64String(bytes, offset, Math.Min(begin.MaxChunkBytes, bytes.Length - offset)));
            return uploads.Commit(begin.UploadId).ArtifactRef!;
        }
        string Input(JsonObject value) => Upload(JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json));
        void Release() { foreach (string id in uploadIds) uploads.Release(id); uploadIds.Clear(); }
        const string block = "/P/PLC/Block", hmi = "/P/HMI";
        string Snapshot(bool installed = false, string stateType = "Int")
        {
            var nodes = new JsonArray();
            void Node(string path, string kind, string? parentPath, string name, JsonObject? metadata = null) =>
                nodes.Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = kind, ["parentId"] = parentPath, ["name"] = name, ["metadata"] = metadata ?? new() });
            Node("/P", "project", null, "P"); Node("/P/PLC", "plc", "/P", "PLC"); Node(block, "block", "/P/PLC", "Block");
            Node(hmi, "hmi", "/P", "HMI", new() { ["family"] = "unified" });
            JsonObject Identity() => new() { ["hmiIdentity"] = new JsonObject { ["family"] = "unified", ["hmiPath"] = hmi }, ["properties"] = new JsonObject { ["Value"] = "Run" } };
            if (installed) { Node(hmi + "/RunList", "hmiResource", hmi, "RunList", Identity()); Node(hmi + "/StateList", "hmiResource", hmi, "StateList", Identity()); }
            Node(hmi + "/Manual", "hmiScript", hmi, "Manual", Identity());
            string xml = $"<Document><SW.Blocks.FB><AttributeList><Name>Block</Name><Interface><Sections><Section Name=\"Input\"><Member Name=\"Run\" Datatype=\"Bool\"/><Member Name=\"State\" Datatype=\"{stateType}\"/><Member Name=\"Unselected\" Datatype=\"Real\"/></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>";
            return (string)reviews.Save(new() { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21" },
                ["nodes"] = nodes, ["edges"] = new JsonArray() },
                new(new JsonObject { ["objectPath"] = block, ["status"] = "exported", ["xml"] = xml, ["utf8Sha256"] = Hash(Encoding.UTF8.GetBytes(xml)) }),
                new() { ["syntheticFixture"] = true })["reviewId"]!;
        }
        string before = Snapshot(), installed = Snapshot(true);
        JsonObject Component(string id) => new() { ["reviewId"] = id, ["objectPaths"] = new JsonArray(block) };
        JsonObject Member(string name) => new() { ["objectPath"] = block, ["memberPath"] = new JsonArray(name) };
        byte[] peerBytes = [0, 255, 32, 13, 10];
        JsonObject File(string id, string name, byte[] bytes) => new() { ["id"] = id, ["fileName"] = name, ["bytes"] = bytes.Length, ["sha256"] = Hash(bytes), ["uploadRef"] = Upload(bytes) };
        JsonObject Packet(JsonObject? previous = null)
        {
            var files = new JsonArray(File("peer", "manual.js", peerBytes)); var outputs = new JsonArray(); var bindings = new JsonArray();
            foreach (var (id, name, member, type) in new[] { ("run", "RunList", "Run", "Bool"), ("state", "StateList", "State", "Int") })
            {
                var ids = new JsonArray();
                if (previous is not null) foreach (var f in previous["files"]!.AsArray().Where(f => (string?)f!["outputId"] == id))
                {
                    string fileId = id + "-" + ids.Count; ids.Add(fileId);
                    files.Add(File(fileId, (string)f!["fileName"]!, ProjectReviewStore.ReadArtifact(root, f["artifact"]!.AsObject())));
                }
                outputs.Add(new JsonObject { ["id"] = id, ["objectPath"] = previous is null ? null : hmi + "/" + name, ["parentPath"] = hmi,
                    ["name"] = name, ["kind"] = "hmiResource", ["action"] = previous is null ? "create" : "update", ["ownership"] = "generated",
                    ["baseline"] = ids.DeepClone(), ["current"] = ids.DeepClone(), ["proposed"] = new JsonArray(),
                    ["baselineComplete"] = true, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Explicit coordinated supplied mapping." });
                bindings.Add(new JsonObject { ["id"] = id + "-binding", ["outputId"] = id, ["property"] = "Value",
                    ["beforeMember"] = previous is null ? null : Member(member), ["afterMember"] = Member(member), ["declaredHmiType"] = type, ["reason"] = "Exact supplied declaration." });
            }
            outputs.Add(new JsonObject { ["id"] = "manual", ["objectPath"] = hmi + "/Manual", ["parentPath"] = hmi, ["name"] = "Manual", ["kind"] = "hmiScript",
                ["action"] = "retain", ["ownership"] = "manual", ["baseline"] = new JsonArray(), ["current"] = new JsonArray("peer"), ["proposed"] = new JsonArray(),
                ["baselineComplete"] = false, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Exact independent peer bytes." });
            return new() { ["schemaVersion"] = "hmi-component-review-request.v7", ["operation"] = previous is null ? "install" : "update", ["reason"] = "Synthetic bounded coordinated fixture.",
                ["beforeComponent"] = previous is null ? null : Component(before), ["afterComponent"] = Component(before),
                ["componentMatches"] = previous is null ? new JsonArray() : new JsonArray(new JsonObject { ["beforeObjectPath"] = block, ["afterObjectPath"] = block, ["reason"] = "Explicit same selected source." }),
                ["destination"] = new JsonObject { ["reviewId"] = previous is null ? before : installed, ["hmiPath"] = hmi, ["family"] = "unified" },
                ["outputs"] = outputs, ["bindings"] = bindings, ["files"] = files,
                ["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 100, ["maxImpactEdgeVisits"] = 1000 },
                ["observations"] = new JsonArray(), ["expectations"] = new JsonArray(), ["libraryAssociations"] = new JsonArray(), ["dependencyAssociations"] = new JsonArray() };
        }
        JsonObject Languages() => new() { ["en-US"] = "Run", ["de-DE"] = "Läuft" };
        JsonObject Request(JsonObject packet, JsonObject? previous = null)
        {
            var review = store.Review(Input(packet)); Release();
            var boolTemplate = new JsonObject { ["fileBase"] = "Run", ["textLibrary"] = "RunTexts", ["languages"] = new JsonArray("en-US", "de-DE"), ["defaultLanguage"] = "en-US",
                ["falseText"] = new JsonObject { ["en-US"] = "Stopped", ["de-DE"] = "Gestoppt" }, ["trueText"] = Languages() };
            var numericTemplate = new JsonObject { ["fileBase"] = "State", ["textLibrary"] = "StateTexts", ["languages"] = new JsonArray("en-US", "de-DE"), ["defaultLanguage"] = "en-US",
                ["dataType"] = "Int", ["requireFullTypeCoverage"] = false, ["states"] = new JsonArray(
                    new JsonObject { ["id"] = "Idle", ["type"] = "SingleValue", ["fromValue"] = 0, ["toValue"] = 0, ["isDefault"] = false, ["translations"] = Languages() },
                    new JsonObject { ["id"] = "Active", ["type"] = "Range", ["fromValue"] = 1, ["toValue"] = 10, ["isDefault"] = false, ["translations"] = Languages() }) };
            return new() { ["schemaVersion"] = "unified-text-list-set-request.v1", ["componentReviewId"] = review["recordId"]!.DeepClone(),
                ["previousRecordId"] = previous?["recordId"]?.DeepClone(), ["retainOutputIds"] = new JsonArray("manual"), ["requireObservedDependencies"] = false,
                ["outputs"] = new JsonArray(
                    new JsonObject { ["outputId"] = "run", ["bindingId"] = "run-binding", ["profile"] = "unified-bool-binding-text-list.v1", ["template"] = boolTemplate, ["previousAuthorRecordId"] = null },
                    new JsonObject { ["outputId"] = "state", ["bindingId"] = "state-binding", ["profile"] = "unified-integer-state-text-list.v1", ["template"] = numericTemplate, ["previousAuthorRecordId"] = null }) };
        }
        HashSet<string> Published() => Directory.GetFiles(Path.Combine(root, "hmi-component-reviews")).Where(p => p.EndsWith(".candidate") || p.EndsWith(".author.json")).Select(Path.GetFileName).ToHashSet()!;
        JsonObject Record(JsonObject request, string label, bool ready = true)
        {
            var publishedBefore = Published(); JsonObject result;
            try { result = store.GenerateTextListSet(Input(request)); } finally { Release(); }
            Check((string?)result["status"] == (ready ? "candidatesReady" : "conflict"), label);
            Check(result["files"]!.AsArray().Count == (ready ? request["outputs"]!.AsArray().Count * 2 : 0), "Whole set candidate count: " + label);
            if (!ready) { Check(Published().SetEquals(publishedBefore), "No child publication on any conflict: " + label); Check(result["outputs"]!.AsArray().All(o => o!["authorRecordId"] is null), "No partial child receipt: " + label); }
            Check(JsonNode.DeepEquals(result, new HmiComponentWorkflowStore(root).GenerateTextListSet(recordId: (string)result["recordId"]!)), "Released retained exact reopen: " + label);
            Check(result["preservedFiles"]!.AsArray().All(f => ProjectReviewStore.ReadArtifact(root, f!["artifact"]!.AsObject()).SequenceEqual(peerBytes)), "Every manual peer exact: " + label);
            cases.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone(), ["receipt"] = result.DeepClone() }); return result;
        }
        var firstRequest = Request(Packet()); var first = Record(firstRequest, "mixed initial generation");
        Check(first["coverage"]!["members"]!["observations"]!.AsArray().Any(m => (string?)m!["memberPath"]![0] == "Unselected" && m["bindingIds"]!.AsArray().Count == 0), "Unselected declared member retained");
        Check(first["outputs"]!.AsArray().All(o => !(bool)o!["dependencyCoverage"]!["allObservedSelectedIndicesMatched"]!), "Bool and numeric missing dependency coverage parity");
        var update = Request(Packet(first), first);
        foreach (var output in update["outputs"]!.AsArray()) { output!["template"]!["languages"] = new JsonArray("de-DE", "en-US"); output["template"]!["defaultLanguage"] = "de-DE"; }
        update["outputs"]![0]!["template"]!["trueText"]!["en-US"] = "Running 😀\nExplicit supplied output";
        update["outputs"]![1]!["template"]!["states"]![1]!["toValue"] = 20;
        var second = Record(update, "coordinated regeneration with state and translated Bool changes");
        Record(Request(Packet(first), first), "unchanged coordinated regeneration");
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("asymmetric language order", r => r["outputs"]![0]!["template"]!["languages"] = new JsonArray("de-DE", "en-US")),
            ("asymmetric default", r => r["outputs"]![1]!["template"]!["defaultLanguage"] = "de-DE"),
            ("filename case collision", r => r["outputs"]![1]!["template"]!["fileBase"] = "RUN"),
            ("library case collision", r => r["outputs"]![1]!["template"]!["textLibrary"] = "RUNTEXTS"),
            ("uncovered retained output", r => r["retainOutputIds"] = new JsonArray()),
            ("required selected dependencies", r => r["requireObservedDependencies"] = true),
            ("numeric incomplete domain", r => r["outputs"]![1]!["template"]!["requireFullTypeCoverage"] = true) })
        {
            var request = firstRequest.DeepClone().AsObject(); edit(request);
            if (label == "uncovered retained output") // Keep a second explicit peer so the helper can check supplied preservation.
            {
                var result = store.GenerateTextListSet(Input(request)); Release();
                Check((string?)result["status"] == "conflict" && result["files"]!.AsArray().Count == 0, label);
                cases.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone(), ["receipt"] = result.DeepClone() });
            }
            else Record(request, label, false);
        }
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("manual current edit suppresses both", p => p["files"]!.AsArray()[1] = File("run-0", "Run.hmi.yml", Encoding.UTF8.GetBytes("manual drift"))),
            ("manual generated owner", p => p["outputs"]![0]!["ownership"] = "manual"),
            ("missing numeric companion", p => p["outputs"]![1]!["current"]!.AsArray().RemoveAt(1)),
            ("orphan binding", p => { var b = p["bindings"]![0]!.DeepClone(); b["id"] = "unused"; b["property"] = "Other"; p["bindings"]!.AsArray().Add(b); }),
            ("cross type declaration", p => p["afterComponent"] = Component(Snapshot(stateType: "DInt"))),
            ("peer collides with generated case", p => p["files"]![0]!["fileName"] = "RUN.HMI.YML") })
            Record(Request(EditPacket(first, edit), first), label, false);
        JsonObject EditPacket(JsonObject previous, Action<JsonObject> edit) { var packet = Packet(previous); edit(packet); return packet; }
        // Original dependency contradictions apply to both profiles, independently
        // of whether the bounded impact projection happened to include a row.
        foreach (bool contradictory in new[] { false, true })
        {
            var saved = reviews.Read(before); var model = saved["model"]!.DeepClone().AsObject();
            var owner = model["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["objectPath"] == block);
            const string typeGuid = "20000000-0000-0000-0000-000000000001", required = "10000000-0000-0000-0000-000000000002";
            JsonObject Observation(string version, params string[] dependencies) => new() { ["schemaVersion"] = "plc-library-instance-observation.v1",
                ["source"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["status"] = "linked", ["typeGuid"] = typeGuid, ["versionGuid"] = version,
                ["dependencies"] = new JsonObject { ["schemaVersion"] = "plc-library-dependencies.v1", ["source"] = "LibraryTypeVersion.Dependencies",
                    ["status"] = "observed", ["requiredVersionGuids"] = new JsonArray(dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()) } };
            owner["metadata"]!["libraryInstance"] = Observation("10000000-0000-0000-0000-000000000001", contradictory ? [required] : []);
            var packet = Packet();
            if (contradictory)
            {
                var target = owner.DeepClone().AsObject(); target["nodeId"] = block + "Dependency"; target["objectPath"] = block + "Dependency"; target["name"] = "Dependency";
                target["metadata"]!["libraryInstance"] = Observation("10000000-0000-0000-0000-000000000003");
                model["nodes"]!.AsArray().Add(target);
                packet["dependencyAssociations"]!.AsArray().Add(new JsonObject { ["scope"] = "afterComponent", ["objectPath"] = block,
                    ["dependencyIndex"] = 0, ["targetObjectPath"] = block + "Dependency", ["reason"] = "Original exact index, deliberately contradictory." });
            }
            string savedId = (string)reviews.Save(model, saved["documents"]!.DeepClone().AsArray(), new() { ["syntheticFixture"] = true })["reviewId"]!;
            packet["afterComponent"] = Component(savedId);
            if (contradictory) packet["afterComponent"]!["objectPaths"]!.AsArray().Add(block + "Dependency");
            var request = Request(packet); request["requireObservedDependencies"] = true;
            var result = Record(request, contradictory ? "Bool and numeric original dependency contradiction" : "observed empty dependencies satisfy bounded requirement", !contradictory);
            Check(result["outputs"]!.AsArray().All(o => (int)o!["dependencyCoverage"]!["contradictionCount"]! == (contradictory ? 1 : 0)), "Both profile dependency contradiction parity");
        }
        foreach (string mode in new[] { "members-count", "members-depth", "consumer-truncation" })
        {
            var saved = reviews.Read(before); var model = saved["model"]!.DeepClone().AsObject(); var docs = saved["documents"]!.DeepClone().AsArray();
            var packet = Packet();
            if (mode.StartsWith("members", StringComparison.Ordinal))
            {
                string extra = mode == "members-count" ? string.Join("", Enumerable.Range(0, 4100).Select(i => $"<Member Name=\"M{i}\" Datatype=\"Real\"/>")) :
                    string.Concat(Enumerable.Range(0, 18).Select(i => $"<Member Name=\"Nested{i}\" Datatype=\"Struct\"><Sections><Section Name=\"None\">")) +
                    string.Concat(Enumerable.Range(0, 18).Select(_ => "</Section></Sections></Member>"));
                string xml = ((string)docs[0]!["xml"]!).Replace("</Section></Sections></Interface>", extra + "</Section></Sections></Interface>", StringComparison.Ordinal);
                docs[0]!["xml"] = xml; docs[0]!["utf8Sha256"] = Hash(Encoding.UTF8.GetBytes(xml));
            }
            else
            {
                for (int i = 0; i < 3; i++)
                {
                    string path = block + "Consumer" + i;
                    model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = path, ["objectPath"] = path, ["kind"] = "block", ["parentId"] = "/P/PLC", ["name"] = "Consumer" + i, ["metadata"] = new JsonObject() });
                    model["edges"]!.AsArray().Add(new JsonObject { ["edgeId"] = "uses-" + i, ["kind"] = "usesType",
                        ["source"] = new JsonObject { ["nodeId"] = path, ["objectPath"] = path }, ["target"] = new JsonObject { ["nodeId"] = block, ["objectPath"] = block },
                        ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved" }, ["unresolved"] = null });
                }
                packet["impactLimits"]!["maxImpactPaths"] = 1;
            }
            string id = (string)reviews.Save(model, docs, new() { ["syntheticFixture"] = true })["reviewId"]!; packet["afterComponent"] = Component(id);
            var result = Record(Request(packet), mode);
            if (mode.StartsWith("members", StringComparison.Ordinal)) Check((bool)result["coverage"]!["members"]!["truncated"]!, "Exact member traversal limit explicit: " + mode);
            else Check(result["outputs"]!.AsArray().All(o => o!["dependencyCoverage"]!["unobservedScopes"]!.AsArray().Count > 0), "Unobserved saved consumer dependency scopes explicit");
        }
        var maximumPacket = Packet(); maximumPacket["outputs"] = new JsonArray(); maximumPacket["bindings"] = new JsonArray(); maximumPacket["files"] = new JsonArray();
        var maximumRequest = firstRequest.DeepClone().AsObject(); maximumRequest["outputs"] = new JsonArray(); maximumRequest["retainOutputIds"] = new JsonArray();
        var prototype = Packet();
        for (int i = 0; i < 16; i++)
        {
            var output = prototype["outputs"]![i % 2]!.DeepClone(); output["id"] = "out" + i; output["name"] = "List" + i; maximumPacket["outputs"]!.AsArray().Add(output);
            var binding = prototype["bindings"]![i % 2]!.DeepClone(); binding["id"] = "bind" + i; binding["outputId"] = "out" + i; maximumPacket["bindings"]!.AsArray().Add(binding);
            var selection = firstRequest["outputs"]![i % 2]!.DeepClone(); selection["outputId"] = "out" + i; selection["bindingId"] = "bind" + i;
            selection["template"]!["fileBase"] = "File" + i; selection["template"]!["textLibrary"] = "Library" + i; maximumRequest["outputs"]!.AsArray().Add(selection);
        }
        maximumRequest["componentReviewId"] = store.Review(Input(maximumPacket))["recordId"]!.DeepClone(); Release();
        Record(maximumRequest, "sixteen coordinated outputs with exact thirty-two files");
        var excess = maximumRequest.DeepClone().AsObject(); excess["outputs"]!.AsArray().Add(excess["outputs"]![0]!.DeepClone());
        Reject(() => store.GenerateTextListSet(Input(excess)), "Seventeenth output refused before publication"); Release();
        var adopt = Request(Packet(first));
        for (int i = 0; i < 2; i++) adopt["outputs"]![i]!["previousAuthorRecordId"] = first["outputs"]![i]!["authorRecordId"]!.DeepClone();
        Record(adopt, "adopt exact existing v2 and v3 authors");
        var v1Adopt = Request(Packet(first));
        for (int i = 0; i < 2; i++)
        {
            var author = store.AuthorTextLists(Input(first["outputs"]![i]!["intent"]!.AsObject())); Release();
            v1Adopt["outputs"]![i]!["previousAuthorRecordId"] = author["recordId"]!.DeepClone();
        }
        Record(v1Adopt, "adopt exact existing v1 authors without invented lineage");
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("one output", r => r["outputs"]!.AsArray().RemoveAt(1)),
            ("duplicate output", r => r["outputs"]![1]!["outputId"] = "run"),
            ("duplicate binding", r => r["outputs"]![1]!["bindingId"] = "run-binding"),
            ("unknown selected binding", r => r["outputs"]![1]!["bindingId"] = "missing"),
            ("unsupported profile", r => r["outputs"]![1]!["profile"] = "native"),
            ("lineage override", r => r["outputs"]![0]!["previousAuthorRecordId"] = first["outputs"]![0]!["authorRecordId"]!.DeepClone()),
            ("extra native flag", r => r["nativeQualified"] = true) })
        { var request = update.DeepClone().AsObject(); edit(request); Reject(() => store.GenerateTextListSet(Input(request)), label); Release(); }
        // Deterministically interrupt publication of the second child after the
        // first child can publish. No final set record may exist; all prior bytes
        // must remain intact. The exact unchanged request is then submitted once.
        var interrupted = firstRequest.DeepClone().AsObject();
        interrupted["outputs"]![0]!["template"]!["trueText"]!["en-US"] = "First child before interruption";
        interrupted["outputs"]![1]!["template"]!["states"]![0]!["translations"]!["en-US"] = "Second child publication interrupted";
        var secondSelection = interrupted["outputs"]![1]!;
        var childInput = new JsonObject { ["schemaVersion"] = "unified-text-list-author-request.v3", ["profile"] = secondSelection["profile"]!.DeepClone(),
            ["componentReviewId"] = interrupted["componentReviewId"]!.DeepClone(), ["outputId"] = secondSelection["outputId"]!.DeepClone(),
            ["bindingId"] = secondSelection["bindingId"]!.DeepClone(), ["previousRecordId"] = null, ["template"] = secondSelection["template"]!.DeepClone(), ["retainOutputIds"] = new JsonArray() };
        string storeRoot = Path.Combine(root, "hmi-component-reviews");
        var originalsBefore = Directory.GetFiles(storeRoot).ToDictionary(p => p, p => Hash(System.IO.File.ReadAllBytes(p)));
        string blocked = Path.Combine(storeRoot, Hash(JsonSerializer.SerializeToUtf8Bytes(childInput, BridgeProtocol.Json)) + ".author-input");
        Directory.CreateDirectory(blocked);
        string interruptedUpload = Input(interrupted); bool publicationFailed = false;
        try { store.GenerateTextListSet(interruptedUpload); }
        catch (BridgeException e) when (e.Code == "PackageChanged") { publicationFailed = true; }
        finally { Directory.Delete(blocked); Release(); }
        Check(publicationFailed, "Second-child deterministic publication failure observed");
        Check(originalsBefore.All(p => Hash(System.IO.File.ReadAllBytes(p.Key)) == p.Value), "Every original store byte unchanged across partial publication");
        Check(Directory.GetFiles(storeRoot, "*.text-set.json").All(p => originalsBefore.ContainsKey(p)), "No completed set published on interruption");
        Check(Directory.GetFiles(storeRoot, "*.author.json").Any(p => !originalsBefore.ContainsKey(p)), "First child orphan demonstrates actual partial publication");
        System.IO.File.WriteAllText(Path.Combine(root, "publication-failure.json"), JsonSerializer.Serialize(new {
            nativeExecuted = false, phase = "second child retained-input publication", errorCode = "PackageChanged",
            priorFiles = originalsBefore.Count, priorBytesUnchanged = true, completedSetsAdded = 0,
            unreferencedFiles = Directory.GetFiles(storeRoot).Where(p => !originalsBefore.ContainsKey(p)).Select(Path.GetFileName).Order().ToArray(),
            recovery = "Release the injected empty-directory block, then explicitly submit the exact unchanged input once." }, BridgeProtocol.Json));
        Record(interrupted, "explicit unchanged request after injected publication block release");
        foreach (string field in new[] { "coverage", "outputs", "files", "conflicts", "previousRecordId" })
        {
            var forged = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, second["recordArtifact"]!.AsObject()))!.AsObject();
            forged[field] = field == "coverage" ? new JsonObject() : field == "previousRecordId" ? null : field == "conflicts" ? new JsonArray("invented") : new JsonArray();
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string hash = Hash(raw);
            System.IO.File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", hash + ".text-set.json"), raw);
            Reject(() => store.GenerateTextListSet(recordId: hash), "Rehashed aggregate tamper " + field);
        }
        System.IO.File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, cases }, BridgeProtocol.Json));
        System.IO.File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS coordinated text-list sets: {checks} checks");
    }
}
