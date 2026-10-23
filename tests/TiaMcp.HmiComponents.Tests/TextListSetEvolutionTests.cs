using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class TextListSetEvolutionTests
{
    internal static void Run(string parent)
    {
        string baseFixture = Path.Combine(parent, "text-coordinated", "mcp-fixture.json");
        if (!File.Exists(baseFixture)) CoordinatedTextListTests.Run(parent);
        var fixture = JsonNode.Parse(File.ReadAllBytes(baseFixture))!.AsObject();
        string source = (string)fixture["artifactRoot"]!, root = Path.Combine(parent, "text-evolution");
        Directory.CreateDirectory(root);
        foreach (string folder in new[] { "project-reviews", "hmi-component-reviews" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (string path in Directory.GetFiles(Path.Combine(source, folder))) File.Copy(path, Path.Combine(root, folder, Path.GetFileName(path)));
        }
        var store = new HmiComponentWorkflowStore(root); var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root);
        var cases = new JsonArray(); var checks = new JsonArray(); var uploaded = new List<string>();
        string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        string Upload(byte[] value)
        {
            var begin = uploads.Begin("evolution.json", value.Length, Hash(value)); uploaded.Add(begin.UploadId);
            for (int offset = 0; offset < value.Length; offset += begin.MaxChunkBytes)
                uploads.Write(begin.UploadId, offset, Convert.ToBase64String(value, offset, Math.Min(begin.MaxChunkBytes, value.Length - offset)));
            return uploads.Commit(begin.UploadId).ArtifactRef!;
        }
        string Input(JsonObject value) => Upload(JsonSerializer.SerializeToUtf8Bytes(value, BridgeProtocol.Json));
        void Release() { foreach (string id in uploaded) uploads.Release(id); uploaded.Clear(); }
        JsonObject Read(JsonObject reference) => JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, reference))!.AsObject();
        JsonObject Report(JsonObject receipt) => Read(new() { ["path"] = "hmi-component-reviews/" + (string)receipt["componentReviewId"]! + ".json", ["sha256"] = receipt["componentReviewId"]!.DeepClone() })["report"]!.AsObject();
        var first = fixture["cases"]![0]!["receipt"]!.DeepClone().AsObject();
        var baseRequest = fixture["cases"]![0]!["request"]!.DeepClone().AsObject();
        const string block = "/P/PLC/Block", hmi = "/P/HMI";
        JsonObject Member(string name) => new() { ["objectPath"] = block, ["memberPath"] = new JsonArray(name) };
        JsonObject Component(string reviewId) => new() { ["reviewId"] = reviewId, ["objectPaths"] = new JsonArray(block) };
        string Snapshot(JsonObject previous, bool destination, string type = "Int", bool occupied = false)
        {
            var priorReport = Report(previous);
            var saved = reviews.Read((string)priorReport["afterComponent"]!["selection"]!["reviewId"]!);
            var model = saved["model"]!.DeepClone().AsObject(); var docs = saved["documents"]!.DeepClone().AsArray();
            var nodes = model["nodes"]!.AsArray();
            // The fixed fixture has only the manual peer and source component;
            // recreate destination observations independently for each transition.
            foreach (var node in nodes.ToArray().Where(n => (string?)n!["kind"] == "hmiResource")) nodes.Remove(node);
            void Resource(string name) => nodes.Add(new JsonObject { ["nodeId"] = hmi + "/" + name, ["objectPath"] = hmi + "/" + name,
                ["kind"] = "hmiResource", ["parentId"] = hmi, ["name"] = name, ["metadata"] = new JsonObject {
                    ["hmiIdentity"] = new JsonObject { ["family"] = "unified", ["hmiPath"] = hmi }, ["properties"] = new JsonObject { ["Value"] = "Explicit" } } });
            if (destination) foreach (var output in previous["outputs"]!.AsArray()) Resource((string)output!["intent"]!["lists"]![0]!["name"]!);
            if (occupied) Resource("Occupied");
            string xml = "<Document><SW.Blocks.FB><AttributeList><Name>Block</Name><Interface><Sections><Section Name=\"Input\">" +
                "<Member Name=\"Run\" Datatype=\"Bool\"/><Member Name=\"State\" Datatype=\"" + type + "\"/>" +
                "<Member Name=\"RunAlternate\" Datatype=\"Bool\"/><Member Name=\"StateAlternate\" Datatype=\"" + type + "\"/>" +
                "<Member Name=\"Unselected\" Datatype=\"Real\"/></Section></Sections></Interface></AttributeList></SW.Blocks.FB></Document>";
            docs[0]!["xml"] = xml; docs[0]!["utf8Sha256"] = Hash(Encoding.UTF8.GetBytes(xml));
            return (string)reviews.Save(model, docs, new() { ["syntheticFixture"] = true })["reviewId"]!;
        }
        JsonObject Choice(JsonObject prior, string id, string operation = "update", string? nextId = null)
        {
            var old = prior["outputs"]!.AsArray().Single(o => (string?)o!["outputId"] == id)!;
            bool numeric = (string?)old["profile"] == "unified-integer-state-text-list.v1";
            var template = baseRequest["outputs"]![numeric ? 1 : 0]!["template"]!.DeepClone().AsObject();
            foreach (string field in new[] { "fileBase", "textLibrary", "languages", "defaultLanguage" }) template[field] = old["intent"]![field]!.DeepClone();
            return new() { ["outputId"] = nextId ?? id, ["previousOutputId"] = id, ["operation"] = operation, ["bindingId"] = old["bindingId"]!.DeepClone(),
                ["bindingAction"] = "preserve", ["profile"] = old["profile"]!.DeepClone(), ["listName"] = old["intent"]!["lists"]![0]!["name"]!.DeepClone(), ["template"] = template };
        }
        JsonArray Choices(JsonObject prior) => new(prior["outputs"]!.AsArray().Select(o => (JsonNode?)Choice(prior, (string)o!["outputId"]!)).ToArray());
        JsonObject Add(string id = "added", bool numeric = false)
        {
            var row = baseRequest["outputs"]![numeric ? 1 : 0]!.DeepClone().AsObject(); row.Remove("previousAuthorRecordId");
            row["outputId"] = id; row["previousOutputId"] = null; row["operation"] = "add"; row["bindingId"] = id + "-binding"; row["bindingAction"] = "preserve";
            row["listName"] = id + "List"; row["template"]!["fileBase"] = id; row["template"]!["textLibrary"] = id + "Texts";
            return row;
        }
        JsonObject Packet(JsonObject previous, JsonArray selected, JsonArray removals, Action<JsonObject>? edit = null)
        {
            var priorReport = Report(previous);
            string before = (string)priorReport["afterComponent"]!["selection"]!["reviewId"]!;
            var files = new JsonArray(); var outputs = new JsonArray(); var bindings = new JsonArray();
            JsonObject File(string id, string name, byte[] raw) => new() { ["id"] = id, ["fileName"] = name, ["bytes"] = raw.Length, ["sha256"] = Hash(raw), ["uploadRef"] = Upload(raw) };
            foreach (var row in selected.Select(o => o!.AsObject()).Concat(removals.Select(n => new JsonObject { ["outputId"] = n!.DeepClone(), ["previousOutputId"] = n.DeepClone(), ["operation"] = "remove" })))
            {
                string id = (string)row["outputId"]!, op = (string)row["operation"]!;
                var old = previous["outputs"]!.AsArray().SingleOrDefault(o => (string?)o!["outputId"] == (string?)row["previousOutputId"]);
                var ids = new JsonArray();
                if (old is not null) foreach (var f in previous["files"]!.AsArray().Where(f => (string?)f!["outputId"] == (string?)old["outputId"]))
                {
                    string fileId = id + "-" + ids.Count; ids.Add(fileId);
                    files.Add(File(fileId, (string)f!["fileName"]!, ProjectReviewStore.ReadArtifact(root, f["artifact"]!.AsObject())));
                }
                string name = old is null ? (string)row["listName"]! : (string)old["intent"]!["lists"]![0]!["name"]!;
                outputs.Add(new JsonObject { ["id"] = id, ["objectPath"] = old is null ? null : hmi + "/" + name, ["parentPath"] = hmi, ["name"] = name,
                    ["kind"] = "hmiResource", ["action"] = op == "add" ? "create" : op == "remove" ? "remove" : "update", ["ownership"] = "generated",
                    ["baseline"] = ids.DeepClone(), ["current"] = ids.DeepClone(), ["proposed"] = new JsonArray(),
                    ["baselineComplete"] = true, ["currentComplete"] = true, ["proposedComplete"] = op == "remove", ["reason"] = "Independent exact evolution source." });
                var oldMember = old?["componentContext"]?["binding"]?["selection"]?["afterMember"];
                bool numeric = (string?)(old?["profile"] ?? row["profile"]) == "unified-integer-state-text-list.v1";
                bindings.Add(new JsonObject { ["id"] = op == "remove" ? (string)old!["bindingId"]! : (string)row["bindingId"]!, ["outputId"] = id, ["property"] = "Value",
                    ["beforeMember"] = oldMember?.DeepClone(), ["afterMember"] = op == "remove" ? null : oldMember?.DeepClone() ?? Member(numeric ? "State" : "Run"),
                    ["declaredHmiType"] = numeric ? "Int" : "Bool", ["reason"] = "Explicit captured transition." });
            }
            var peers = previous["preservedFiles"]!.AsArray().GroupBy(f => (string)f!["outputId"]!);
            foreach (var peer in peers)
            {
                var ids = new JsonArray();
                foreach (var f in peer) { string id = peer.Key + "-" + ids.Count; ids.Add(id); files.Add(File(id, (string)f!["fileName"]!, ProjectReviewStore.ReadArtifact(root, f["artifact"]!.AsObject()))); }
                outputs.Add(new JsonObject { ["id"] = peer.Key, ["objectPath"] = hmi + "/Manual", ["parentPath"] = hmi, ["name"] = "Manual", ["kind"] = "hmiScript",
                    ["action"] = "retain", ["ownership"] = "manual", ["baseline"] = new JsonArray(), ["current"] = ids, ["proposed"] = new JsonArray(),
                    ["baselineComplete"] = false, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Exact unchanged peer." });
            }
            var result = new JsonObject { ["schemaVersion"] = "hmi-component-review-request.v7", ["operation"] = "update", ["reason"] = "Offline evolution fixture.",
                ["beforeComponent"] = Component(before), ["afterComponent"] = Component(Snapshot(previous, false)),
                ["componentMatches"] = new JsonArray(new JsonObject { ["beforeObjectPath"] = block, ["afterObjectPath"] = block, ["reason"] = "Exact selected component." }),
                ["destination"] = new JsonObject { ["reviewId"] = Snapshot(previous, true, occupied: true), ["hmiPath"] = hmi, ["family"] = "unified" },
                ["outputs"] = outputs, ["bindings"] = bindings, ["files"] = files,
                ["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 100, ["maxImpactEdgeVisits"] = 1000 },
                ["observations"] = new JsonArray(), ["expectations"] = new JsonArray(), ["libraryAssociations"] = new JsonArray(), ["dependencyAssociations"] = new JsonArray() };
            edit?.Invoke(result); return result;
        }
        JsonObject Request(JsonObject previous, JsonArray? selected = null, JsonArray? removals = null, Action<JsonObject>? edit = null)
        {
            selected ??= Choices(previous); removals ??= new();
            var packet = Packet(previous, selected, removals, edit);
            var review = store.Review(Input(packet)); Release();
            return new() { ["schemaVersion"] = "unified-text-list-set-request.v2", ["componentReviewId"] = review["recordId"]!.DeepClone(),
                ["previousRecordId"] = previous["recordId"]!.DeepClone(), ["outputs"] = selected.DeepClone(), ["removeOutputIds"] = removals.DeepClone(),
                ["retainOutputIds"] = new JsonArray("manual"), ["requireObservedDependencies"] = false };
        }
        string[] Published() => Directory.GetFiles(Path.Combine(root, "hmi-component-reviews")).Where(p => p.EndsWith(".candidate") || p.EndsWith(".author.json")).Order().ToArray();
        JsonObject Record(JsonObject request, string label, bool ready = true)
        {
            var before = Published(); JsonObject result;
            try { result = store.GenerateTextListSet(Input(request)); } finally { Release(); }
            Check((string?)result["status"] == (ready ? "candidatesReady" : "conflict"), label + ": " + result["conflicts"]!.ToJsonString());
            Check(result["files"]!.AsArray().Count == (ready ? request["outputs"]!.AsArray().Count * 2 : 0), label + ": all candidates or none");
            Check(result["outputs"]!.AsArray().All(o => !o!.AsObject().ContainsKey("authorRecordId")), "No fabricated earlier child author");
            if (!ready) Check(Published().SequenceEqual(before), label + ": conflict publishes no candidates");
            Check(JsonNode.DeepEquals(result, new HmiComponentWorkflowStore(root).GenerateTextListSet(recordId: (string)result["recordId"]!)), label + ": exact released reopen");
            cases.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone(), ["receipt"] = result.DeepClone() });
            return result;
        }
        var unchanged = Record(Request(first), "v1 to v2 unchanged");
        var addPlan = Choices(first); addPlan.Add(Add());
        var added = Record(Request(first, addPlan), "add Bool output");
        var numericAdd = Choices(first); numericAdd.Add(Add("numericAdded", true)); Record(Request(first, numericAdd), "add numeric output");
        var addCollision = Choices(first); addCollision[0]!["operation"] = "rename"; addCollision[0]!["listName"] = "VacatedRun";
        var collisionAdd = Add("replacement"); collisionAdd["listName"] = "RunList"; addCollision.Add(collisionAdd);
        Record(Request(first, addCollision), "addition cannot mask captured sibling through vacated paths", false);
        var renamePlan = Choices(added); var rename = renamePlan[0]!;
        rename["operation"] = "rename"; rename["outputId"] = "renamed"; rename["listName"] = "RenamedList";
        rename["template"]!["fileBase"] = "Renamed"; rename["template"]!["textLibrary"] = "RenamedTexts";
        var renamed = Record(Request(added, renamePlan), "mixed v1 v2 rename lineage");
        Record(Request(renamed), "regenerate after renamed observation");
        var removePlan = Choices(renamed); removePlan.RemoveAt(2);
        var reduced = Record(Request(renamed, removePlan, new("added")), "remove exact added output");
        var empty = Record(Request(reduced, new(), new("renamed", "state")), "remove all outputs preserving peer");
        Record(Request(empty, new(Add("fresh"))), "add after exact empty predecessor");
        Record(Request(empty, new(Add("fresh")), edit: p => p["beforeComponent"] = Component(Snapshot(first, false, "DInt"))),
            "empty predecessor cannot bypass component lineage", false);
        Record(Request(unchanged, new(Choice(unchanged, "run")), new("state"), p => p["outputs"]![1]!["baselineComplete"] = false),
            "removal missing baseline suppresses all", false);
        Record(Request(unchanged, new(Choice(unchanged, "run")), new("state"), p => p["bindings"]![1]!["property"] = "Other"),
            "removal cannot silently change predecessor property", false);
        foreach (bool numeric in new[] { false, true })
        {
            var plan = Choices(unchanged); plan[numeric ? 1 : 0]!["bindingAction"] = "rebind"; plan[numeric ? 1 : 0]!["bindingId"] = numeric ? "state-next" : "run-next";
            var rebound = Record(Request(unchanged, plan, edit: p => p["bindings"]![numeric ? 1 : 0]!["afterMember"] = Member(numeric ? "StateAlternate" : "RunAlternate")),
                numeric ? "numeric explicit member rebind" : "Bool explicit member rebind");
            Record(Request(rebound), "regenerate exact rebound member");
        }
        var swap = Choices(unchanged);
        foreach (var (index, other) in new[] { (0, 1), (1, 0) })
        {
            swap[index]!["operation"] = "rename"; swap[index]!["listName"] = unchanged["outputs"]![other]!["intent"]!["lists"]![0]!["name"]!.DeepClone();
            foreach (string field in new[] { "fileBase", "textLibrary" }) swap[index]!["template"]![field] = unchanged["outputs"]![other]!["intent"]![field]!.DeepClone();
        }
        var swapped = Record(Request(unchanged, swap), "whole set list library filename swap");
        Check(swapped["filenameTransfers"]!.AsArray().Count == 4, "All four swapped filename transfers previewed");
        Record(Request(swapped), "regenerate swapped candidates");
        var caseOnly = Choices(unchanged); caseOnly[0]!["operation"] = "rename"; caseOnly[0]!["template"]!["fileBase"] = "RUN"; caseOnly[0]!["listName"] = "RUNLIST";
        Record(Request(unchanged, caseOnly), "case-only own identity rename");
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("implicit rename", r => r["outputs"]![0]!["listName"] = "Changed"),
            ("filename case collision", r => { r["outputs"]![1]!["operation"] = "rename"; r["outputs"]![1]!["template"]!["fileBase"] = "RUN"; }),
            ("list case collision", r => { r["outputs"]![1]!["operation"] = "rename"; r["outputs"]![1]!["listName"] = "RUNLIST"; }),
            ("library case collision", r => { r["outputs"]![1]!["operation"] = "rename"; r["outputs"]![1]!["template"]!["textLibrary"] = "RUNTEXTS"; }),
            ("asymmetric language order", r => r["outputs"]![0]!["template"]!["languages"] = new JsonArray("de-DE", "en-US")),
            ("required unavailable dependencies", r => r["requireObservedDependencies"] = true),
            ("unaccounted current and predecessor", r => r["outputs"]!.AsArray().RemoveAt(0)),
            ("unaccounted peer", r => r["retainOutputIds"] = new JsonArray()),
            ("captured unselected collision", r => { r["outputs"]![0]!["operation"] = "rename"; r["outputs"]![0]!["listName"] = "Occupied"; }),
            ("retained peer name collision", r => { r["outputs"]![0]!["operation"] = "rename"; r["outputs"]![0]!["listName"] = "Manual"; }),
            ("fake rebind", r => r["outputs"]![0]!["bindingAction"] = "rebind"),
            ("numeric conversion", r => r["outputs"]![1]!["template"]!["dataType"] = "DInt") })
        { var request = Request(unchanged); edit(request); Record(request, label, false); }
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("manual current drift", p => { var f = p["files"]![0]!.DeepClone().AsObject(); byte[] raw = Encoding.UTF8.GetBytes("manual current"); f["id"] = "manual-current"; f["uploadRef"] = Upload(raw); f["sha256"] = Hash(raw); f["bytes"] = raw.Length; p["files"]!.AsArray().Add(f); p["outputs"]![0]!["current"]![0] = "manual-current"; }),
            ("incomplete baseline", p => p["outputs"]![0]!["baselineComplete"] = false),
            ("missing companion", p => p["outputs"]![1]!["current"]!.AsArray().RemoveAt(1)),
            ("manual ownership", p => p["outputs"]![0]!["ownership"] = "manual"),
            ("implicit member rebind", p => p["bindings"]![0]!["afterMember"] = Member("RunAlternate")),
            ("stale before member", p => p["bindings"]![0]!["beforeMember"] = Member("RunAlternate")),
            ("stale component", p => p["beforeComponent"] = Component(Snapshot(first, false, "DInt"))),
            ("retained peer filename conflict", p => p["files"]!.AsArray().Last()!["fileName"] = "RUN.HMI.YML"),
            ("supplied proposal drift", p => { p["outputs"]![0]!["proposed"] = p["outputs"]![1]!["current"]!.DeepClone(); p["outputs"]![0]!["proposedComplete"] = true; }),
            ("orphan binding", p => { var b = p["bindings"]![0]!.DeepClone(); b["id"] = "orphan"; b["property"] = "Other"; p["bindings"]!.AsArray().Add(b); }) })
            Record(Request(unchanged, edit: edit), label, false);
        Record(Request(unchanged, edit: p => { foreach (var o in p["outputs"]!.AsArray().Take(2)) { o!["proposed"] = o["current"]!.DeepClone(); o["proposedComplete"] = true; } }),
            "independent exact proposed pairs");
        foreach (bool manual in new[] { false, true })
        {
            var unpublished = Request(unchanged, edit: p => {
                if (manual) p["outputs"]![0]!["ownership"] = "manual";
                else { p["outputs"]![0]!["proposed"] = p["outputs"]![0]!["current"]!.DeepClone(); p["outputs"]![0]!["proposedComplete"] = true; }
            });
            unpublished["outputs"]![0]!["template"]!["trueText"]!["en-US"] = manual ? "never publish manual conflict" : "never publish proposed mismatch";
            Record(unpublished, manual ? "fresh candidates suppressed by manual conflict" : "fresh candidates suppressed by independent proposed conflict", false);
        }
        // Publication failure occurs after candidates, before the completion marker.
        var failureRequest = Request(unchanged); failureRequest["outputs"]![0]!["template"]!["trueText"]!["en-US"] = "interrupted fresh output";
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(failureRequest, BridgeProtocol.Json);
        string blockedPath = Path.Combine(root, "hmi-component-reviews", Hash(requestBytes) + ".text-set-input");
        var originals = Directory.GetFiles(Path.Combine(root, "hmi-component-reviews")).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
        string[] setBefore = Directory.GetFiles(Path.Combine(root, "hmi-component-reviews"), "*.text-set.json");
        Directory.CreateDirectory(blockedPath);
        bool refused = false;
        try { store.GenerateTextListSet(Upload(requestBytes)); } catch (Exception e) when (e is BridgeException or IOException or UnauthorizedAccessException) { refused = true; }
        finally { Release(); }
        Check(refused, "Injected publication path failure refuses");
        Check(setBefore.Order().SequenceEqual(Directory.GetFiles(Path.Combine(root, "hmi-component-reviews"), "*.text-set.json").Order()), "No complete evolution set after publication failure");
        Check(originals.All(p => Hash(File.ReadAllBytes(p.Key)) == p.Value), "All original bytes survive publication failure");
        File.WriteAllText(Path.Combine(root, "publication-failure.json"), new JsonObject { ["completedSetAdded"] = false, ["originalFilesUnchanged"] = originals.Count,
            ["immutableOrphanCandidates"] = Directory.GetFiles(Path.Combine(root, "hmi-component-reviews"), "*.candidate").Count(p => !originals.ContainsKey(p)),
            ["blockedPath"] = blockedPath, ["nativeExecuted"] = false }.ToJsonString(BridgeProtocol.Json));
        Directory.Delete(blockedPath); // Exact empty injected directory, never evidence.
        Record(failureRequest, "explicit unchanged resubmission after released blocker");
        Check(JsonNode.DeepEquals(first, store.GenerateTextListSet(recordId: (string)first["recordId"]!)), "Original v1 receipt remains exact after all evolution");
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), new JsonObject { ["artifactRoot"] = root, ["cases"] = cases, ["priorV1"] = first }.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "verification.json"), new JsonObject { ["completed"] = true, ["count"] = checks.Count, ["checks"] = checks,
            ["caseCount"] = cases.Count, ["readyCases"] = cases.Count(c => (string?)c!["receipt"]!["status"] == "candidatesReady"), ["nativeExecuted"] = false }.ToJsonString(BridgeProtocol.Json));
        IntegerTextListConversionTests.Run(parent);
        Console.WriteLine($"PASS text-list set evolution: {checks.Count} checks, {cases.Count} cases.");
    }
}
