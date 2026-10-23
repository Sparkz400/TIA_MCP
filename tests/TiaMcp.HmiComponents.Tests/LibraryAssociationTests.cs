using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;

internal static class LibraryAssociationTests
{
    internal static JsonObject Run(string parent, string repository, JsonObject derivedPacket)
    {
        string root = Path.Combine(parent, "library-associations");
        var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root); var store = new HmiComponentWorkflowStore(root);
        var checks = new List<string>(); var uploaded = new List<string>(); var files = new JsonObject(); var reviewIds = new HashSet<string>();
        const string project = "/projects/LibraryFixture", plc = project + "/plc", block = plc + "/blocks/RenamedInstance", type = plc + "/types/Unassociated", hmi = project + "/hmi";
        const string typeGuid = "146cafdb-e1d5-4111-9b23-1078b6557d53", versionGuid = "43b5c338-b589-4ab5-8c82-cae3e08e9d06";
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        string Upload(byte[] data)
        {
            var upload = uploads.Begin("original", data.Length, Hash(data)); uploaded.Add(upload.UploadId);
            for (int offset = 0; offset < data.Length; offset += upload.MaxChunkBytes)
                uploads.Write(upload.UploadId, offset, Convert.ToBase64String(data, offset, Math.Min(upload.MaxChunkBytes, data.Length - offset)));
            return uploads.Commit(upload.UploadId).ArtifactRef!;
        }
        JsonObject File(byte[] data)
        {
            string id = Hash(data); files[id] = Convert.ToBase64String(data);
            return new() { ["id"] = id, ["fileName"] = "library.xml", ["uploadRef"] = Upload(data), ["sha256"] = id, ["bytes"] = data.Length };
        }
        JsonObject Observation() => new() { ["schemaVersion"] = "plc-library-instance-observation.v1", ["source"] = "LibraryTypeInstanceInfo.LibraryTypeVersion",
            ["status"] = "linked", ["typeGuid"] = typeGuid, ["versionGuid"] = versionGuid, ["nativeQualified"] = false };
        string Review(JsonNode? observation, string kind = "FB", int copies = 0)
        {
            JsonObject Node(string path, string nodeKind, string? parentPath, JsonObject? metadata = null) => new() {
                ["nodeId"] = path, ["objectPath"] = path, ["name"] = path.Split('/')[^1], ["kind"] = nodeKind, ["parentId"] = parentPath,
                ["origin"] = "native", ["metadata"] = metadata ?? new JsonObject() };
            var model = new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = "synthetic.ap21", ["host"] = "synthetic" },
                ["nodes"] = new JsonArray(Node(project, "project", null), Node(plc, "plc", project),
                    Node(block, "block", plc, new() { ["kind"] = kind, ["libraryInstance"] = observation?.DeepClone() }),
                    Node(type, "type", plc), Node(hmi, "hmi", project, new() { ["family"] = "classic" })), ["edges"] = new JsonArray() };
            for (int i = 1; i <= copies; i++) model["nodes"]!.AsArray().Add(Node(block + "-" + i, "block", plc, new() { ["kind"] = kind, ["libraryInstance"] = observation?.DeepClone() }));
            string id = (string)reviews.Save(model, [], new() { ["syntheticFixture"] = true })["reviewId"]!; reviewIds.Add(id); return id;
        }
        string reviewId = Review(Observation());
        string originalRoot = Path.Combine(repository, "fixtures/libraries/component-qualification/oracle-xp01");
        var provenance = JsonNode.Parse(System.IO.File.ReadAllBytes(Path.Combine(originalRoot, "provenance.json")))!;
        byte[] source = System.IO.File.ReadAllBytes(Path.Combine(originalRoot, "source-" + versionGuid + "-options3.xml"));
        var inputFile = File(source);
        var request = new JsonObject { ["schemaVersion"] = "hmi-component-review-request.v5", ["operation"] = "install", ["reason"] = "Exact supplied library observation and retained original export; no native claim.",
            ["beforeComponent"] = null, ["afterComponent"] = new JsonObject { ["reviewId"] = reviewId, ["objectPaths"] = new JsonArray(block, type) },
            ["componentMatches"] = new JsonArray(), ["destination"] = new JsonObject { ["reviewId"] = reviewId, ["hmiPath"] = hmi, ["family"] = "classic" },
            ["outputs"] = new JsonArray(), ["bindings"] = new JsonArray(), ["files"] = new JsonArray(inputFile),
            ["impactLimits"] = new JsonObject { ["impactDepth"] = 2, ["maxImpactPaths"] = 20, ["maxImpactEdgeVisits"] = 100 },
            ["observations"] = new JsonArray(), ["expectations"] = new JsonArray(), ["libraryAssociations"] = new JsonArray(new JsonObject {
                ["scope"] = "afterComponent", ["objectPath"] = block, ["fileId"] = inputFile["id"]!.DeepClone(), ["profile"] = "v21-full-fb-library-version.v1", ["reason"] = "Explicit selected full export." }) };
        JsonObject Change(Action<JsonObject> action) { var clone = request.DeepClone().AsObject(); action(clone); return clone; }
        JsonObject WithFile(byte[] bytes) => Change(p => { var f = File(bytes); p["files"] = new JsonArray(f); p["libraryAssociations"]![0]!["fileId"] = f["id"]!.DeepClone(); });
        JsonObject Receipt(JsonObject p) => store.Review(Upload(JsonSerializer.SerializeToUtf8Bytes(p, BridgeProtocol.Json)));
        JsonObject Record(JsonObject receipt) => JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!.AsObject();
        JsonObject Report(JsonObject p) => Record(Receipt(p))["report"]!.AsObject();
        void Reject(JsonObject p, string label, string? message = null)
        {
            try { _ = Receipt(p); }
            catch (BridgeException e) when ((e.Code is "InvalidHmiComponentReview" or "ArtifactChanged") && (message is null || e.Message.Contains(message))) { Check(true, label); return; }
            throw new Exception("Expected refusal: " + label);
        }
        var receipt = Receipt(request); var report = Record(receipt)["report"]!;
        Check((string?)report["libraryAssociations"]![0]!["identifierAgreement"] == "match", "Original full export matches exact observed GUIDs despite renamed instance");
        Check(report["derivedEffects"]!.AsArray().Count == 0 && report["outputs"]!.AsArray().Count == 0, "Association-only request requires no unrelated output edit");
        Check((int)report["summary"]!["unassociatedComponentCount"]! == 1 && report["libraryCoverage"]!.AsArray().Count == 2, "Unselected UDT remains explicitly unassociated");
        foreach (string flag in new[] { "sourceAuthenticityVerified", "libraryIdentityVerified", "contentAssociationVerified", "dependencyClosureVerified", "runtimeInstanceVerified" })
            Check(!(bool)report["libraryAssociations"]![0]![flag]!, "Identifier agreement never promotes " + flag);
        foreach (var item in provenance["files"]!.AsArray())
        {
            byte[] bytes = System.IO.File.ReadAllBytes(Path.Combine(originalRoot, (string)item!["path"]!)); Check(Hash(bytes) == (string)item["sha256"]!, "Original fixture hash: " + item["path"]);
            var candidate = WithFile(bytes);
            if (((string)item["path"]!).EndsWith("options3.xml")) Check((string?)Report(candidate)["libraryAssociations"]![0]!["identifierAgreement"] == "match", "Both full exports match identifiers without content-equality claim");
            else Reject(candidate, "Default export lacks required full identity");
        }
        var packets = new JsonArray(request.DeepClone());
        foreach (string status in new[] { "noService", "notApplicable", "unavailable", "future" })
        {
            var observation = Observation(); observation["status"] = status;
            var candidate = Change(p => p["afterComponent"]!["reviewId"] = Review(observation));
            Check((string?)Report(candidate)["libraryAssociations"]![0]!["identifierAgreement"] == "unavailable", "Non-linked observation stays unavailable: " + status);
        }
        foreach (string defect in new[] { "missing", "shape", "schema", "source", "typeGuid", "versionGuid", "empty" })
        {
            JsonNode? observation = Observation();
            if (defect == "missing") observation = null;
            else if (defect == "shape") observation = new JsonArray();
            else if (defect == "schema") observation["schemaVersion"] = "future";
            else if (defect == "source") observation["source"] = "name";
            else if (defect == "empty") observation["versionGuid"] = Guid.Empty.ToString("D");
            else observation[defect] = 42;
            var candidate = Change(p => p["afterComponent"]!["reviewId"] = Review(observation));
            Check((int)Report(candidate)["summary"]!["libraryIdentifierUnavailableCount"]! == 1, "Unavailable identity retained: " + defect);
            if (defect == "missing") packets.Add(candidate);
        }
        foreach (string key in new[] { "typeGuid", "versionGuid" })
        {
            var observation = Observation(); observation[key] = "10000000-0000-0000-0000-000000000001";
            var candidate = Change(p => p["afterComponent"]!["reviewId"] = Review(observation));
            Check((int)Report(candidate)["summary"]!["libraryIdentifierMismatchCount"]! == 1, "Different " + key + " is an explicit contradiction"); packets.Add(candidate);
        }
        foreach (string defect in new[] { "scope", "path", "profile", "file", "duplicate", "empty", "extra", "before", "kind" })
            Reject(Change(p => {
                var row = p["libraryAssociations"]![0]!;
                switch (defect) {
                    case "scope": row["scope"] = "destination"; break;
                    case "path": row["objectPath"] = type; break;
                    case "profile": row["profile"] = "generic"; break;
                    case "file": row["fileId"] = "absent"; break;
                    case "duplicate": p["libraryAssociations"]!.AsArray().Add(row.DeepClone()); break;
                    case "empty": p["libraryAssociations"] = new JsonArray(); break;
                    case "extra": row["nativeQualified"] = true; break;
                    case "before": row["scope"] = "beforeComponent"; break;
                    case "kind": p["afterComponent"]!["reviewId"] = Review(Observation(), "FC"); break;
                }
            }), "Invalid exact association refuses: " + defect);
        foreach (var (label, mutate) in new (string, Action<XDocument>)[] {
            ("version", d => d.Root!.Element("Engineering")!.SetAttributeValue("version", "V20")),
            ("duplicate owner", d => d.Root!.Add(new XElement(d.Root!.Element("SW.Blocks.CodeBlockLibraryTypeVersion")!))),
            ("nested owner", d => d.Root!.Add(new XElement("Wrapper", new XElement(d.Root!.Element("SW.Blocks.CodeBlockLibraryTypeVersion")!)))),
            ("content role", d => d.Descendants("SW.Blocks.FB").Single().SetAttributeValue("CompositionName", "Other")),
            ("state", d => d.Descendants("State").Single(e => e.Parent?.Parent?.Name == "SW.Blocks.CodeBlockLibraryTypeVersion").Value = "InWork"),
            ("duplicate GUID", d => { var a = d.Root!.Element("SW.Blocks.CodeBlockLibraryTypeVersion")!.Element("AttributeList")!; a.Add(new XElement(a.Element("Guid")!)); }),
            ("empty GUID", d => d.Descendants("Guid").Single().Value = Guid.Empty.ToString("D")),
            ("GUID readonly", d => d.Descendants("Guid").Single().SetAttributeValue("ReadOnly", "false")),
            ("content name", d => d.Descendants("SW.Blocks.FB").Single().Element("AttributeList")!.Element("Name")!.Value = "Wrong"),
            ("duplicate ID", d => d.Descendants("SW.Blocks.FB").Single().SetAttributeValue("ID", "0")),
            ("provenance envelope", d => d.Root!.Add(new XElement("DocumentInfo"))),
            ("encoding declaration", d => d.Declaration!.Encoding = "utf-16") })
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(source).TrimStart('\uFEFF'), LoadOptions.PreserveWhitespace); mutate(document);
            byte[] bytes = Encoding.UTF8.GetBytes(document.Declaration + "\n" + document.ToString(SaveOptions.DisableFormatting)); Reject(WithFile(bytes), "Malformed original profile refuses: " + label);
        }
        Reject(WithFile(Encoding.UTF8.GetBytes("<!DOCTYPE Document [<!ENTITY x 'injected'>]>" + Encoding.UTF8.GetString(source).Split("?>", 2)[1])), "DTD refuses before XML allocation");
        Reject(WithFile(new byte[] { 0xff, 0xfe, 0x80 }), "Invalid UTF-8 refuses");
        foreach (string bound in new[] { "depth", "events" })
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(source).TrimStart('\uFEFF'), LoadOptions.PreserveWhitespace);
            var attributes = document.Descendants("SW.Blocks.FB").Single().Element("AttributeList")!;
            if (bound == "depth") { var cursor = attributes; for (int i = 0; i < 66; i++) { var next = new XElement("Opaque"); cursor.Add(next); cursor = next; } }
            else for (int i = 0; i < 200001; i++) attributes.Add(new XElement("Opaque"));
            Reject(WithFile(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))), "Shared XML " + bound + " bound refuses", "depth/event");
        }
        byte[] Pad(byte[] original, int mib) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("</Document>", "<!--" + new string('x', mib * 1024 * 1024) + "--></Document>", StringComparison.Ordinal));
        var largeFile = File(Pad(source, 9));
        var bounded = Change(p => {
            p["files"] = new JsonArray(largeFile.DeepClone()); p["afterComponent"]!["reviewId"] = Review(Observation(), copies: 7);
            p["afterComponent"]!["objectPaths"] = new JsonArray(Enumerable.Range(0, 8).Select(i => (JsonNode?)JsonValue.Create(i == 0 ? block : block + "-" + i)).ToArray());
            p["libraryAssociations"] = new JsonArray(Enumerable.Range(0, 7).Select(i => { var row = request["libraryAssociations"]![0]!.DeepClone(); row["objectPath"] = i == 0 ? block : block + "-" + i; row["fileId"] = largeFile["id"]!.DeepClone(); return row; }).ToArray());
        });
        Check(Report(bounded)["libraryAssociations"]!.AsArray().Count == 7, "Seven repeated 9 MiB roles stay inside the shared 64 MiB bound");
        var eighth = bounded["libraryAssociations"]![0]!.DeepClone(); eighth["objectPath"] = block + "-7"; bounded["libraryAssociations"]!.AsArray().Add(eighth);
        Reject(bounded, "Eighth repeated role exceeds examined-byte budget", "64 MiB");
        var combined = derivedPacket.DeepClone().AsObject(); combined["schemaVersion"] = "hmi-component-review-request.v5";
        combined["operation"] = "install"; combined["beforeComponent"] = null; combined["componentMatches"] = new JsonArray(); combined["bindings"] = new JsonArray();
        combined["afterComponent"] = bounded["afterComponent"]!.DeepClone();
        combined["libraryAssociations"] = new JsonArray(bounded["libraryAssociations"]!.AsArray().Take(3).Select(r => r!.DeepClone()).ToArray());
        string destinationId = (string)combined["destination"]!["reviewId"]!;
        Check((string?)reviews.Import(Upload(System.IO.File.ReadAllBytes(Path.Combine(parent, "project-reviews", destinationId + ".json"))))["reviewId"] == destinationId, "Mixed budget fixture imports exact original destination");
        foreach (var file in combined["files"]!.AsArray())
        {
            byte[] bytes = System.IO.File.ReadAllBytes(Path.Combine(parent, "hmi-component-reviews", (string)file!["sha256"]! + ".source"));
            if (((string)file["id"]!).StartsWith("screen-", StringComparison.Ordinal)) bytes = Pad(bytes, 10);
            file["uploadRef"] = Upload(bytes); file["sha256"] = Hash(bytes); file["bytes"] = bytes.Length;
        }
        combined["files"]!.AsArray().Add(largeFile.DeepClone());
        Check((string?)Report(combined)["derivedEffects"]![0]!["proposal"]!["status"] == "match", "Independent screen effects and three library roles fit the same XML budget");
        combined["libraryAssociations"]!.AsArray().Add(bounded["libraryAssociations"]![3]!.DeepClone());
        Reject(combined, "Derived effects and library associations cannot each spend a separate 64 MiB budget", "64 MiB");
        var update = Change(p => { p["operation"] = "update"; p["beforeComponent"] = p["afterComponent"]!.DeepClone(); var row = p["libraryAssociations"]![0]!.DeepClone(); row["scope"] = "beforeComponent"; p["libraryAssociations"]!.AsArray().Add(row); });
        Check(Report(update)["libraryAssociations"]!.AsArray().Count == 2, "Before and after associations stay independently scoped"); packets.Add(update);
        foreach (string section in new[] { "libraryAssociations", "libraryCoverage" }) Check(store.Page((string)receipt["recordId"]!, section)["rows"]!.AsArray().Count > 0, "Library section pages: " + section);
        foreach (string id in uploaded) uploads.Release(id);
        Check(JsonNode.DeepEquals(receipt, new HmiComponentWorkflowStore(root).Page((string)receipt["recordId"]!)), "Release and reopen preserve exact original association");
        var forged = Record(receipt); forged["report"]!["libraryAssociations"]![0]!["identifierAgreement"] = "different";
        byte[] forgedBytes = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string forgedId = Hash(forgedBytes);
        System.IO.File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", forgedId + ".json"), forgedBytes);
        try { store.Page(forgedId); throw new Exception("Forged association accepted"); }
        catch (BridgeException e) when (e.Code == "ArtifactChanged") { Check(true, "Rehashed association cannot replace original comparison"); }
        var selectedFiles = new JsonObject();
        foreach (string id in packets.SelectMany(p => p!["files"]!.AsArray()).Select(f => (string)f!["id"]!).Distinct()) selectedFiles[id] = files[id]!.DeepClone();
        var fixture = new JsonObject { ["artifactRoot"] = root, ["packets"] = packets, ["files"] = selectedFiles, ["reviewIds"] = new JsonArray(reviewIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        System.IO.File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), fixture.ToJsonString(BridgeProtocol.Json));
        System.IO.File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, count = checks.Count, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS library-export associations: {checks.Count} checks; original exports and synthetic saved observations, no native execution.");
        return fixture;
    }
}
