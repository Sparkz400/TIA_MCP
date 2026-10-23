using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.HmiComponents;

internal static class DependencyImpactTests
{
    internal static JsonObject Run(JsonObject fixture)
    {
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
        JsonObject Association(string scope = "afterComponent", string status = "match") => new() {
            ["selection"] = new JsonObject { ["scope"] = scope, ["objectPath"] = "/Owner", ["targetObjectPath"] = "/Dependency" },
            ["reviewId"] = scope == "afterComponent" ? "after" : "before", ["status"] = status };
        JsonObject Consumer(string scope, string review, string root, string path) => new() {
            ["scope"] = scope, ["reviewId"] = review, ["rootObjectPath"] = root, ["consumerObjectPath"] = path };
        JsonObject Binding(string id, string? before, string? after) => new() { ["selection"] = new JsonObject {
            ["outputId"] = id, ["beforeMember"] = before is null ? null : new JsonObject { ["objectPath"] = before },
            ["afterMember"] = after is null ? null : new JsonObject { ["objectPath"] = after } } };
        JsonObject Report()
        {
            var result = new JsonObject { ["summary"] = new JsonObject(),
                ["dependencyAssociations"] = new JsonArray(Association(), Association("beforeComponent", "different")),
                ["consumerPaths"] = new JsonArray(Consumer("afterComponent", "after", "/Owner", "/Consumer"),
                    Consumer("afterComponent", "after", "/Dependency", "/Consumer"),
                    Consumer("afterComponent", "foreign", "/Owner", "/Foreign"),
                    Consumer("beforeComponent", "before", "/Owner", "/Prior")),
                ["bindings"] = new JsonArray(Binding("screen", null, "/Owner"), Binding("screen", null, "/Consumer"),
                    Binding("foreign", null, "/Foreign"), Binding("prior", "/Prior", "/Elsewhere")),
                ["outputs"] = new JsonArray(), ["effectChecks"] = new JsonArray(), ["derivedEffects"] = new JsonArray(),
                ["expectationCoverage"] = new JsonArray() };
            foreach (string id in new[] { "screen", "foreign", "prior" })
            {
                result["outputs"]!.AsArray().Add(new JsonObject { ["id"] = id, ["selection"] = new JsonObject { ["action"] = "update" } });
                result["effectChecks"]!.AsArray().Add(new JsonObject { ["outputId"] = id, ["status"] = id == "screen" ? "mismatch" : "incomplete" });
                result["expectationCoverage"]!.AsArray().Add(new JsonObject { ["outputId"] = id });
                result["derivedEffects"]!.AsArray().Add(new JsonObject { ["expectation"] = new JsonObject { ["outputId"] = id } });
            }
            return result;
        }
        var report = Report(); var original = report.DeepClone().AsObject();
        HmiComponentDependencyImpacts.Add(report);
        var row = report["dependencyImpacts"]![0]!;
        Check(JsonNode.DeepEquals(row["consumerPathIndices"], new JsonArray(0, 1)), "Both saved paths survive; foreign review and prior scope are excluded");
        Check(JsonNode.DeepEquals(row["bindingIndices"], new JsonArray(0, 1)), "Exact owner and observed consumer select explicit after bindings only");
        Check(row["outputs"]!.AsArray().Count == 1 && (string?)row["outputs"]![0]!["outputId"] == "screen", "Shared output is linked once without discarding either binding");
        Check((string?)row["outputs"]![0]!["effectStatus"] == "mismatch" && (int)row["outputs"]![0]!["effectCheckIndex"]! == 0, "Output mismatch points to the original independent effect check");
        Check(JsonNode.DeepEquals(row["outputs"]![0]!["derivedEffectIndices"], new JsonArray(0)), "Derived expectations use the original expectation selection");
        Check(JsonNode.DeepEquals(row["unmappedObjectPaths"], new JsonArray("/Dependency")), "Unmapped dependency object remains explicit");
        var prior = report["dependencyImpacts"]![1]!;
        Check((string?)prior["associationStatus"] == "different" && JsonNode.DeepEquals(prior["bindingIndices"], new JsonArray(3)), "Contradictory prior association retains only prior evidence and is not promoted");
        Check(!(bool)prior["nativeImpactVerified"]! && !(bool)prior["consumerCoverageComplete"]! && !(bool)prior["outputMappingComplete"]!, "References never establish native effects or complete coverage");
        foreach (string key in new[] { "consumerPaths", "bindings", "outputs", "effectChecks", "derivedEffects", "dependencyAssociations" })
            Check(JsonNode.DeepEquals(report[key], original[key]), "Original evidence section remains exact: " + key);
        var empty = Report(); empty["dependencyAssociations"] = new JsonArray(); HmiComponentDependencyImpacts.Add(empty);
        Check(empty["dependencyImpacts"]!.AsArray().Count == 0 && !(bool)empty["dependencyImpactCoverage"]![0]!["truncated"]!, "No supplied associations does not invent effects");
        var bounded = Report(); bounded["dependencyAssociations"] = new JsonArray();
        for (int i = 0; i < 1000; i++)
        {
            var item = Association(); item["selection"]!["objectPath"] = "/" + new string('o', 4000);
            item["selection"]!["targetObjectPath"] = "/" + new string('d', 4000); bounded["dependencyAssociations"]!.AsArray().Add(item);
        }
        HmiComponentDependencyImpacts.Add(bounded);
        int included = bounded["dependencyImpacts"]!.AsArray().Count;
        Check(included > 0 && included < 1000 && (bool)bounded["dependencyImpactCoverage"]![0]!["truncated"]!, "Large valid paths hit the bounded prefix without losing original associations");
        Check((int)bounded["dependencyImpactCoverage"]![0]!["omittedAssociationIndices"]![0]! == included &&
            (int)bounded["dependencyImpactCoverage"]![0]!["omittedAssociationIndices"]!.AsArray()[^1]! == 999, "Every omitted association index is retained");
        Check((int)bounded["dependencyImpactCoverage"]![0]!["returnedBytes"]! <= HmiComponentDependencyImpacts.ByteLimit, "Impact evidence respects the independent byte budget");

        // Exercise real publication/reopen using the preceding v6 fixture's retained originals.
        string root = (string)fixture["artifactRoot"]!;
        var reviews = new ProjectReviewStore(root); var uploads = new ArtifactUploadStore(root); var store = new HmiComponentWorkflowStore(root);
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        var uploaded = new List<string>();
        string Upload(byte[] data)
        {
            var value = uploads.Begin("impact-input", data.Length, Hash(data)); uploaded.Add(value.UploadId);
            for (int offset = 0; offset < data.Length; offset += value.MaxChunkBytes)
                uploads.Write(value.UploadId, offset, Convert.ToBase64String(data, offset, Math.Min(value.MaxChunkBytes, data.Length - offset)));
            return uploads.Commit(value.UploadId).ArtifactRef!;
        }
        var request = fixture["packets"]![0]!.DeepClone().AsObject(); request["schemaVersion"] = "hmi-component-review-request.v7";
        string reviewId = (string)request["afterComponent"]!["reviewId"]!, owner = (string)request["dependencyAssociations"]![0]!["objectPath"]!;
        var saved = reviews.Read(reviewId); var model = saved["model"]!.DeepClone().AsObject(); string consumer = owner + "Consumer";
        var consumerNode = model["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["objectPath"] == owner).DeepClone().AsObject();
        consumerNode["objectPath"] = consumer; consumerNode["nodeId"] = consumer; consumerNode["name"] = "Consumer"; model["nodes"]!.AsArray().Add(consumerNode);
        model["edges"]!.AsArray().Add(new JsonObject { ["edgeId"] = "impact-consumer", ["kind"] = "usesType",
            ["source"] = new JsonObject { ["objectPath"] = consumer, ["nodeId"] = consumer },
            ["target"] = new JsonObject { ["objectPath"] = owner, ["nodeId"] = owner },
            ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved" }, ["unresolved"] = null });
        string modifiedId = (string)reviews.Save(model, saved["documents"]!.DeepClone().AsArray(), new() { ["syntheticFixture"] = true })["reviewId"]!;
        request["afterComponent"]!["reviewId"] = modifiedId; request["destination"]!["reviewId"] = modifiedId;
        request["afterComponent"]!["objectPaths"]!.AsArray().Add(consumer);
        string hmi = (string)request["destination"]!["hmiPath"]!;
        request["outputs"] = new JsonArray(new JsonObject { ["id"] = "new-tag", ["objectPath"] = null, ["parentPath"] = hmi, ["name"] = "NewTag", ["kind"] = "hmiTag",
            ["action"] = "create", ["ownership"] = "generated", ["baseline"] = new JsonArray(), ["current"] = new JsonArray(), ["proposed"] = new JsonArray(),
            ["baselineComplete"] = true, ["currentComplete"] = true, ["proposedComplete"] = false, ["reason"] = "Explicit fixture mapping with unavailable output content." });
        request["bindings"] = new JsonArray(new JsonObject { ["id"] = "new-binding", ["outputId"] = "new-tag", ["property"] = "Value", ["beforeMember"] = null,
            ["afterMember"] = new JsonObject { ["objectPath"] = owner, ["memberPath"] = new JsonArray("Run") }, ["declaredHmiType"] = "Bool", ["reason"] = "Exact selected owner; declaration remains unavailable." });
        var consumerBinding = request["bindings"]![0]!.DeepClone(); consumerBinding!["id"] = "consumer-binding";
        consumerBinding["property"] = "Visible"; consumerBinding["afterMember"]!["objectPath"] = consumer;
        request["bindings"]!.AsArray().Add(consumerBinding);
        foreach (var file in request["files"]!.AsArray()) file!["uploadRef"] = Upload(Convert.FromBase64String((string)fixture["files"]![(string)file["id"]!]!));
        var receipt = store.Review(Upload(JsonSerializer.SerializeToUtf8Bytes(request, BridgeProtocol.Json)));
        var record = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!.AsObject();
        var actual = record["report"]!["dependencyImpacts"]![0]!;
        Check(actual["consumerPathIndices"]!.AsArray().Count == 1 && actual["outputs"]!.AsArray().Count == 1, "Retained workflow joins actual saved path and explicit output mapping");
        Check(actual["bindingIndices"]!.AsArray().Count == 2, "Actual retained owner and selected consumer bindings join the same output once");
        Check((string?)actual["outputs"]![0]!["effectStatus"] == "incomplete", "Missing proposed/observed files remain incomplete through publication");
        foreach (string section in new[] { "dependencyImpacts", "dependencyImpactCoverage" })
            Check(store.Page((string)receipt["recordId"]!, section)["rows"]!.AsArray().Count > 0, "New section pages: " + section);
        foreach (string id in uploaded) uploads.Release(id);
        Check(JsonNode.DeepEquals(receipt, new HmiComponentWorkflowStore(root).Page((string)receipt["recordId"]!)), "Restart and released uploads preserve the v7 receipt");
        record["report"]!["dependencyImpacts"]![0]!["outputs"]![0]!["effectStatus"] = "match";
        byte[] forged = JsonSerializer.SerializeToUtf8Bytes(record, BridgeProtocol.Json); string forgedId = Hash(forged);
        File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", forgedId + ".json"), forged);
        try { store.Page(forgedId); throw new Exception("Rehashed fabricated impact accepted"); }
        catch (BridgeException error) when (error.Code == "ArtifactChanged") { Check(true, "Rehashed fabricated impact fails original-input recomputation"); }
        var files = fixture["files"]!.DeepClone().AsObject();
        var complete = request.DeepClone().AsObject();
        foreach (var (id, text) in new[] { ("impact-proposed", "expected tag"), ("impact-observed", "expected tag"), ("impact-different", "changed tag") })
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text); files[id] = Convert.ToBase64String(data);
            complete["files"]!.AsArray().Add(new JsonObject { ["id"] = id, ["fileName"] = "tag.xml", ["sha256"] = Hash(data), ["bytes"] = data.Length, ["uploadRef"] = "replaced-by-host-test" });
        }
        complete["outputs"]![0]!["proposed"] = new JsonArray("impact-proposed"); complete["outputs"]![0]!["proposedComplete"] = true;
        complete["observations"] = new JsonArray(new JsonObject { ["outputId"] = "new-tag", ["files"] = new JsonArray("impact-observed"), ["complete"] = true });
        var mismatch = complete.DeepClone().AsObject(); mismatch["observations"]![0]!["files"] = new JsonArray("impact-different");
        var result = new JsonObject { ["artifactRoot"] = root, ["packets"] = new JsonArray(request, complete, mismatch), ["reviewIds"] = new JsonArray(modifiedId), ["files"] = files };
        Console.WriteLine($"PASS dependency impacts: {checks} checks; supplied mappings and saved evidence only.");
        return result;
    }
}
