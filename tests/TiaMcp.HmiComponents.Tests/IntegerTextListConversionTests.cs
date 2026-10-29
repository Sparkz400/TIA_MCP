using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class IntegerTextListConversionTests
{
    internal static void Run(string parent)
    {
        string baseFixture = Path.Combine(parent, "text-coordinated", "mcp-fixture.json");
        if (!File.Exists(baseFixture)) CoordinatedTextListTests.Run(parent);
        var fixture = JsonNode.Parse(File.ReadAllBytes(baseFixture))!.AsObject();
        string source = (string)fixture["artifactRoot"]!, root = Path.Combine(parent, "text-conversion");
        Directory.CreateDirectory(root);
        foreach (string folder in new[] { "project-reviews", "hmi-component-reviews" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            foreach (string path in Directory.GetFiles(Path.Combine(source, folder))) File.Copy(path, Path.Combine(root, folder, Path.GetFileName(path)));
        }
        var store = new HmiComponentWorkflowStore(root); var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root);
        var refusals = new JsonArray(); var cases = new JsonArray(); var checks = new JsonArray(); var uploaded = new List<string>();
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
            if (numeric)
            {
                template["dataType"] = old["componentContext"]!["binding"]!["afterDeclaration"]!["dataType"]!.DeepClone();
                template["states"] = new JsonArray(old["intent"]!["lists"]![0]!["entries"]!.AsArray().Select(e => {
                    var state = e!.DeepClone().AsObject(); state.Remove("textId");
                    state["translations"] = old["intent"]!["texts"]!.AsArray().Single(t => (string?)t!["id"] == (string?)e["textId"])!["translations"]!.DeepClone();
                    return (JsonNode?)state;
                }).ToArray());
            }
            return new() { ["outputId"] = nextId ?? id, ["previousOutputId"] = id, ["operation"] = operation, ["bindingId"] = old["bindingId"]!.DeepClone(),
                ["bindingAction"] = "preserve", ["profile"] = old["profile"]!.DeepClone(), ["listName"] = old["intent"]!["lists"]![0]!["name"]!.DeepClone(), ["template"] = template };
        }
        JsonArray Choices(JsonObject prior) => new(prior["outputs"]!.AsArray().Select(o => (JsonNode?)Choice(prior, (string)o!["outputId"]!)).ToArray());
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
                    ["declaredHmiType"] = numeric ? (string?)(row["template"]?["dataType"] ?? old?["componentContext"]?["binding"]?["afterDeclaration"]?["dataType"]) : "Bool", ["reason"] = "Explicit captured transition." });
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
                ["beforeComponent"] = Component(before), ["afterComponent"] = Component(Snapshot(previous, false, (string?)selected.FirstOrDefault(o => (string?)o!["profile"] == "unified-integer-state-text-list.v1")?["template"]?["dataType"] ?? "Int")),
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
            return new() { ["schemaVersion"] = "unified-text-list-set-request.v3", ["componentReviewId"] = review["recordId"]!.DeepClone(),
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
            Check((string?)request["schemaVersion"] == "unified-text-list-set-request.v1" || result["outputs"]!.AsArray().All(o => !o!.AsObject().ContainsKey("authorRecordId")), "No fabricated earlier child author");
            if (!ready) Check(Published().SequenceEqual(before), label + ": conflict publishes no candidates");
            Check(JsonNode.DeepEquals(result, new HmiComponentWorkflowStore(root).GenerateTextListSet(recordId: (string)result["recordId"]!)), label + ": exact released reopen");
            cases.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone(), ["receipt"] = result.DeepClone() });
            return result;
        }
        JsonObject Seed(string type, long low, long high)
        {
            var component = Read(new() { ["path"] = "hmi-component-reviews/" + (string)first["componentReviewId"]! + ".json", ["sha256"] = first["componentReviewId"]!.DeepClone() });
            var packet = Read(component["input"]!.AsObject());
            foreach (var f in packet["files"]!.AsArray()) f!["uploadRef"] = Upload(ProjectReviewStore.ReadArtifact(root,
                new() { ["path"] = "hmi-component-reviews/" + (string)f["sha256"]! + ".source", ["sha256"] = f["sha256"]!.DeepClone() }));
            string sourceReview = Snapshot(first, false, type);
            packet["afterComponent"] = Component(sourceReview); packet["destination"]!["reviewId"] = sourceReview;
            packet["bindings"]![1]!["declaredHmiType"] = type;
            var reviewed = store.Review(Input(packet)); Release();
            var request = baseRequest.DeepClone().AsObject(); request["componentReviewId"] = reviewed["recordId"]!.DeepClone();
            var template = request["outputs"]![1]!["template"]!;
            template["dataType"] = type;
            template["states"] = new JsonArray(new JsonObject { ["id"] = "Boundary", ["type"] = low == high ? "SingleValue" : "Range",
                ["fromValue"] = low, ["toValue"] = high, ["isDefault"] = false,
                ["translations"] = new JsonObject { ["en-US"] = "Exact boundary \uD83D\uDE00", ["de-DE"] = "Exakte Grenze" } });
            return Record(request, $"seed {type} [{low},{high}]");
        }
        JsonObject ConvertRequest(JsonObject previous, string target, Action<JsonObject>? edit = null)
        {
            var selected = Choices(previous); var numeric = selected[1]!;
            string sourceType = (string)numeric["template"]!["dataType"]!;
            numeric["template"]!["dataType"] = target;
            numeric["typeConversion"] = new JsonObject { ["profile"] = "integer-value-preserving.v1", ["fromType"] = sourceType, ["toType"] = target };
            return Request(previous, selected, edit: edit);
        }
        void Refuse(JsonObject request, string label)
        {
            var before = Published(); bool refused = false;
            try { store.GenerateTextListSet(Input(request)); }
            catch (BridgeException e) { refused = e.Code == "InvalidHmiComponentReview"; }
            finally { Release(); }
            Check(refused, label + ": invalid explicit request refuses");
            Check(Published().SequenceEqual(before), label + ": no candidate publication");
            refusals.Add(new JsonObject { ["label"] = label, ["request"] = request.DeepClone() });
        }
        var beforeHashes = Directory.GetFiles(Path.Combine(root, "hmi-component-reviews")).ToDictionary(p => p, p => Hash(File.ReadAllBytes(p)));
        var matrix = new (string From, string To, long Low, long High)[] {
            ("SInt", "Int", -128, 127), ("USInt", "UInt", 0, 255), ("Int", "DInt", -32768, 32767),
            ("UInt", "UDInt", 0, 65535), ("DInt", "Int", -32768, 32767), ("UDInt", "UInt", 0, 65535),
            ("Int", "SInt", -128, 127), ("UInt", "USInt", 0, 255), ("SInt", "USInt", 0, 127),
            ("USInt", "SInt", 0, 127), ("Int", "UInt", 0, 32767), ("UInt", "Int", 0, 32767),
            ("DInt", "UDInt", 0, 2147483647), ("UDInt", "DInt", 0, 2147483647),
            ("DInt", "UDInt", 2147483647, 2147483647), ("UDInt", "DInt", 0, 0) };
        JsonObject? chain = null;
        foreach (var test in matrix)
        {
            var seed = Seed(test.From, test.Low, test.High);
            var converted = Record(ConvertRequest(seed, test.To), $"convert {test.From} to {test.To} [{test.Low},{test.High}]");
            Check((string?)converted["outputs"]![1]!["typeConversion"]!["status"] == "checkedListedValues", "Explicit checked conversion evidence");
            Check(converted["outputs"]![1]!["typeConversion"]!["sourceBindingConflicts"]!.AsArray().Count == 1, "Original type conflict retained");
            Check(JsonNode.DeepEquals(converted["files"], seed["files"]), "Value-preserving conversion retains exact paired output bytes");
            if (chain is null) chain = converted;
        }
        Record(Request(chain!), "v3 unchanged regeneration");
        Record(ConvertRequest(chain!, "DInt"), "mixed v1 v3 chained widening");
        Record(Request(chain!, new(Choice(chain!, "run")), new("state")), "v3 explicit removal after conversion");
        foreach (var test in new (string From, string To, long Low, long High)[] {
            ("Int", "SInt", -129, 127), ("Int", "SInt", -128, 128), ("UInt", "USInt", 0, 256),
            ("DInt", "Int", -32769, 32767), ("DInt", "Int", -32768, 32768), ("UDInt", "UInt", 0, 65536),
            ("SInt", "USInt", -1, 1), ("USInt", "SInt", 0, 128), ("Int", "UInt", -1, 0),
            ("UInt", "Int", 0, 32768), ("DInt", "UDInt", -1, 0), ("UDInt", "DInt", 0, 2147483648),
            ("UDInt", "DInt", 4294967295, 4294967295), ("DInt", "Int", -2147483648, -2147483648) })
        {
            if (test.Low < int.MinValue || test.High > int.MaxValue)
            {
                // V21 imports values outside signed 32-bit as 0, so such a set is refused before its first candidate exists.
                var unpublished = Published(); string? code = null;
                try { Seed(test.From, test.Low, test.High); } catch (BridgeException e) { code = e.Code; }
                Check(code == "HmiTextListValueOutOfRange" && Published().SequenceEqual(unpublished), $"seed beyond signed 32-bit refuses unpublished: {test.From} [{test.Low},{test.High}]");
                continue;
            }
            var seed = Seed(test.From, test.Low, test.High);
            Refuse(ConvertRequest(seed, test.To), $"overflow {test.From} to {test.To} [{test.Low},{test.High}]");
            var hidden = ConvertRequest(seed, test.To);
            hidden["outputs"]![1]!["template"]!["states"]![0]!["fromValue"] = 0;
            hidden["outputs"]![1]!["template"]!["states"]![0]!["toValue"] = 0;
            hidden["outputs"]![1]!["template"]!["states"]![0]!["type"] = "SingleValue";
            Record(hidden, $"cannot hide prior overflow {test.From} to {test.To} [{test.Low},{test.High}]", false);
        }
        var baseline = Seed("Int", 0, 127);
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("wrong source", r => r["outputs"]![1]!["typeConversion"]!["fromType"] = "UInt"),
            ("wrong destination", r => r["outputs"]![1]!["typeConversion"]!["toType"] = "UInt"),
            ("same type", r => r["outputs"]![1]!["typeConversion"]!["toType"] = "Int"),
            ("implicit conversion", r => r["outputs"]![1]!.AsObject().Remove("typeConversion")),
            ("state altered", r => r["outputs"]![1]!["template"]!["states"]![0]!["toValue"] = 126),
            ("translation altered", r => r["outputs"]![1]!["template"]!["states"]![0]!["translations"]!["en-US"] = "Changed"),
            ("unrelated Bool conflict", r => r["outputs"]![0]!["listName"] = "Changed"),
            ("required incomplete full domain", r => r["outputs"]![1]!["template"]!["requireFullTypeCoverage"] = true),
            ("renamed conversion", r => { r["outputs"]![1]!["operation"] = "rename"; r["outputs"]![1]!["listName"] = "Renamed"; }),
            ("rebound conversion", r => r["outputs"]![1]!["bindingAction"] = "rebind") })
        { var request = ConvertRequest(baseline, "USInt"); edit(request); Record(request, label, false); }
        foreach (var (label, edit) in new (string, Action<JsonObject>)[] {
            ("manual current drift", p => { var f = p["files"]![2]!.DeepClone().AsObject(); byte[] raw = Encoding.UTF8.GetBytes("manual current"); f["id"] = "manual-current"; f["uploadRef"] = Upload(raw); f["sha256"] = Hash(raw); f["bytes"] = raw.Length; p["files"]!.AsArray().Add(f); p["outputs"]![1]!["current"]![0] = "manual-current"; }),
            ("incomplete baseline", p => p["outputs"]![1]!["baselineComplete"] = false),
            ("missing companion", p => p["outputs"]![1]!["current"]!.AsArray().RemoveAt(1)),
            ("manual owner", p => p["outputs"]![1]!["ownership"] = "manual"),
            ("wrong captured after type", p => p["afterComponent"] = Component(Snapshot(baseline, false, "DInt"))),
            ("wrong declared HMI type", p => p["bindings"]![1]!["declaredHmiType"] = "DInt"),
            ("unknown before member", p => p["bindings"]![1]!["beforeMember"] = Member("Unknown")),
            ("different after member", p => p["bindings"]![1]!["afterMember"] = Member("StateAlternate")),
            ("wrong supplied proposal", p => { p["outputs"]![1]!["proposed"] = p["outputs"]![0]!["current"]!.DeepClone(); p["outputs"]![1]!["proposedComplete"] = true; }) })
            Record(ConvertRequest(baseline, "USInt", edit), label, false);
        foreach (string type in new[] { "LInt", "ULInt", "Real", "Word", "INT" })
        { var r = ConvertRequest(baseline, "USInt"); r["outputs"]![1]!["typeConversion"]!["fromType"] = type; Refuse(r, "unsupported conversion " + type); }
        var unknown = ConvertRequest(baseline, "USInt"); unknown["outputs"]![1]!["typeConversion"]!["wrap"] = true; Refuse(unknown, "unknown conversion option");
        var empty = ConvertRequest(baseline, "USInt"); empty["outputs"]![1]!["typeConversion"] = new JsonObject(); Refuse(empty, "empty conversion");
        var malformed = ConvertRequest(baseline, "USInt"); malformed["outputs"]![1]!["typeConversion"] = "Int to USInt"; Refuse(malformed, "nonobject conversion");
        var legacy = ConvertRequest(baseline, "USInt"); legacy["schemaVersion"] = "unified-text-list-set-request.v2"; Refuse(legacy, "legacy request cannot acquire conversion semantics");
        Check(beforeHashes.All(p => Hash(File.ReadAllBytes(p.Key)) == p.Value), "Every copied original prior record remains byte-identical");
        Check(JsonNode.DeepEquals(first, store.GenerateTextListSet(recordId: (string)first["recordId"]!)), "Original v1 response remains exact");
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), new JsonObject { ["artifactRoot"] = root, ["cases"] = cases, ["refusals"] = refusals, ["priorV1"] = first }.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "verification.json"), new JsonObject { ["completed"] = true, ["count"] = checks.Count, ["checks"] = checks,
            ["caseCount"] = cases.Count, ["refusalCount"] = refusals.Count, ["nativeExecuted"] = false }.ToJsonString(BridgeProtocol.Json));
        Console.WriteLine($"PASS integer text-list conversion: {checks.Count} checks, {cases.Count} cases, {refusals.Count} refusals.");
    }
}
