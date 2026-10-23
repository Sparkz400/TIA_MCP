using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;

internal static class DependencyAssociationTests
{
    internal static JsonObject Run(string parent, JsonObject libraryFixture)
    {
        string root = Path.GetFullPath(Path.Combine(parent, "dependency-associations"));
        var uploads = new ArtifactUploadStore(root); var reviews = new ProjectReviewStore(root); var store = new HmiComponentWorkflowStore(root);
        var checks = new List<string>(); var uploaded = new List<string>(); var reviewIds = new HashSet<string>(); var packets = new JsonArray();
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        string Upload(byte[] data)
        {
            var started = uploads.Begin("original", data.Length, Hash(data)); uploaded.Add(started.UploadId);
            for (int offset = 0; offset < data.Length; offset += started.MaxChunkBytes)
                uploads.Write(started.UploadId, offset, Convert.ToBase64String(data, offset, Math.Min(started.MaxChunkBytes, data.Length - offset)));
            return uploads.Commit(started.UploadId).ArtifactRef!;
        }
        var request = libraryFixture["packets"]![0]!.DeepClone().AsObject();
        string originalReview = (string)request["afterComponent"]!["reviewId"]!;
        var model = new ProjectReviewStore((string)libraryFixture["artifactRoot"]!).Read(originalReview)["model"]!.DeepClone().AsObject();
        string targetPath = (string)request["libraryAssociations"]![0]!["objectPath"]!;
        string ownerPath = request["afterComponent"]!["objectPaths"]!.AsArray().Select(p => (string)p!).Single(p => p != targetPath);
        JsonObject Node(JsonObject document, string path) => document["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["objectPath"] == path);
        JsonObject Observe(JsonObject document, string path) => Node(document, path)["metadata"]!["libraryInstance"]!.AsObject();
        string required = (string)Observe(model, targetPath)["versionGuid"]!;
        JsonObject Dependencies(params string[] ids) => new() { ["schemaVersion"] = "plc-library-dependencies.v1", ["source"] = "LibraryTypeVersion.Dependencies",
            ["status"] = "observed", ["requiredVersionGuids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["nativeQualified"] = false, ["dependencyClosureVerified"] = false };
        Observe(model, targetPath)["dependencies"] = Dependencies();
        Node(model, ownerPath)["metadata"]!["libraryInstance"] = new JsonObject {
            ["schemaVersion"] = "plc-library-instance-observation.v1", ["source"] = "LibraryTypeInstanceInfo.LibraryTypeVersion", ["status"] = "linked",
            ["typeGuid"] = "20000000-0000-0000-0000-000000000001", ["versionGuid"] = "10000000-0000-0000-0000-000000000001",
            ["nativeQualified"] = false, ["dependencies"] = Dependencies(required) };
        string Save(JsonObject document)
        {
            string id = (string)reviews.Save(document, [], new() { ["syntheticFixture"] = true })["reviewId"]!;
            reviewIds.Add(id); return id;
        }
        request["schemaVersion"] = "hmi-component-review-request.v6";
        string reviewId = Save(model);
        request["afterComponent"]!["reviewId"] = reviewId; request["destination"]!["reviewId"] = reviewId;
        foreach (var file in request["files"]!.AsArray()) file!["uploadRef"] = Upload(Convert.FromBase64String((string)libraryFixture["files"]![(string)file["id"]!]!));
        request["dependencyAssociations"] = new JsonArray(new JsonObject { ["scope"] = "afterComponent", ["objectPath"] = ownerPath,
            ["dependencyIndex"] = 0, ["targetObjectPath"] = targetPath, ["reason"] = "Associate this exact retained dependency index and selected export/object; no native authority." });
        JsonObject Change(Action<JsonObject> change) { var next = request.DeepClone().AsObject(); change(next); return next; }
        JsonObject WithModel(Action<JsonObject> change)
        {
            var next = model.DeepClone().AsObject(); change(next); string id = Save(next);
            return Change(p => { p["afterComponent"]!["reviewId"] = id; p["destination"]!["reviewId"] = id; });
        }
        JsonObject Receipt(JsonObject input) => store.Review(Upload(JsonSerializer.SerializeToUtf8Bytes(input, BridgeProtocol.Json)));
        JsonObject Record(JsonObject receipt) => JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!.AsObject();
        JsonObject Report(JsonObject input) => Record(Receipt(input))["report"]!.AsObject();
        JsonObject OwnerCoverage(JsonObject report) => report["dependencyCoverage"]!.AsArray().OfType<JsonObject>().Single(r => (string?)r["objectPath"] == ownerPath);
        var receipt = Receipt(request); var report = Record(receipt)["report"]!.AsObject(); packets.Add(request.DeepClone());
        Check((string?)report["dependencyAssociations"]![0]!["status"] == "match", "Exact original export and selected target match the retained dependency GUID");
        Check((bool)OwnerCoverage(report)["allObservedIndicesMatched"]! && !(bool)OwnerCoverage(report)["dependencyClosureVerified"]!, "Complete selected index mapping grants no native dependency closure");
        foreach (string section in new[] { "dependencyAssociations", "dependencyCoverage", "dependencyIdentities" })
            Check(store.Page((string)receipt["recordId"]!, section)["rows"]!.AsArray().Count > 0, "New retained section pages: " + section);
        var unavailable = WithModel(m => Observe(m, ownerPath).Remove("dependencies")); unavailable["dependencyAssociations"] = new JsonArray();
        Check((string?)OwnerCoverage(Report(unavailable))["dependencyStatus"] == "unavailable", "Actual old-style saved observation is unavailable, not an empty dependency set"); packets.Add(unavailable);
        var noExports = Change(p => { p["libraryAssociations"] = new JsonArray(); p["files"] = new JsonArray(); });
        Check((string?)Report(noExports)["dependencyAssociations"]![0]!["status"] == "unavailable", "Explicit mapping without an original export remains unavailable"); packets.Add(noExports);
        var repeated = WithModel(m => Observe(m, ownerPath)["dependencies"] = Dependencies(required, required));
        var repeatedReport = Report(repeated);
        Check((int)repeatedReport["summary"]!["dependencyUnassociatedIndexCount"]! == 1 && OwnerCoverage(repeatedReport)["repeatedVersionGuids"]!.AsArray().Count == 1,
            "Repeated GUID retains both positions and exposes the unmapped position"); packets.Add(repeated.DeepClone());
        var second = repeated["dependencyAssociations"]![0]!.DeepClone(); second["dependencyIndex"] = 1; repeated["dependencyAssociations"]!.AsArray().Add(second);
        Check((bool)OwnerCoverage(Report(repeated))["allObservedIndicesMatched"]!, "Separate explicit mappings can cover repeated original positions"); packets.Add(repeated);
        var wrong = WithModel(m => Observe(m, targetPath)["versionGuid"] = "10000000-0000-0000-0000-000000000001");
        Check((string?)Report(wrong)["dependencyAssociations"]![0]!["status"] == "different", "Retained target contradiction survives the workflow"); packets.Add(wrong);
        var padded = WithModel(m => Observe(m, targetPath)["versionGuid"] = " " + required + " ");
        var paddedReport = Report(padded);
        Check((string?)paddedReport["dependencyAssociations"]![0]!["status"] == "unavailable" && (string?)paddedReport["libraryAssociations"]![0]!["identifierAgreement"] == "unavailable",
            "V6 refuses padded saved identities consistently in both association stages"); packets.Add(padded);
        var conflicting = WithModel(m => Observe(m, ownerPath)["versionGuid"] = required);
        Check((string?)Report(conflicting)["dependencyAssociations"]![0]!["status"] == "conflicting", "Competing type identities for the same version survive the workflow"); packets.Add(conflicting);
        var empty = WithModel(m => Observe(m, ownerPath)["dependencies"] = Dependencies()); empty["dependencyAssociations"] = new JsonArray();
        Check((bool)OwnerCoverage(Report(empty))["allObservedIndicesMatched"]!, "Successful empty original observation is distinct from missing data"); packets.Add(empty);
        var priorModel = model.DeepClone().AsObject(); Observe(priorModel, ownerPath)["dependencies"] = Dependencies();
        Observe(priorModel, targetPath)["typeGuid"] = "20000000-0000-0000-0000-000000000099";
        string priorId = Save(priorModel);
        var update = Change(p => {
            p["operation"] = "update"; p["beforeComponent"] = p["afterComponent"]!.DeepClone(); p["beforeComponent"]!["reviewId"] = priorId;
            var association = p["libraryAssociations"]![0]!.DeepClone(); association["scope"] = "beforeComponent"; p["libraryAssociations"]!.AsArray().Add(association);
        });
        var updateReport = Report(update);
        Check((string?)updateReport["dependencyAssociations"]![0]!["status"] == "match" && (int)updateReport["summary"]!["libraryIdentifierMismatchCount"]! == 1,
            "Contradictory prior snapshot does not contaminate the selected after-snapshot dependency");
        Check(updateReport["dependencyCoverage"]!.AsArray().Count == 4, "Update retains coverage for both exact original review scopes"); packets.Add(update);
        string peerPath = targetPath + "Peer";
        var cycleModel = model.DeepClone().AsObject(); Observe(cycleModel, targetPath)["dependencies"] = Dependencies(required);
        var peer = Node(cycleModel, targetPath).DeepClone().AsObject(); peer["objectPath"] = peerPath; peer["nodeId"] = peerPath; peer["name"] = "Peer";
        cycleModel["nodes"]!.AsArray().Add(peer); string cycleId = Save(cycleModel);
        var cycle = Change(p => {
            p["afterComponent"]!["reviewId"] = cycleId; p["destination"]!["reviewId"] = cycleId;
            p["afterComponent"]!["objectPaths"] = new JsonArray(targetPath, peerPath);
            var association = p["libraryAssociations"]![0]!.DeepClone(); association["objectPath"] = peerPath; p["libraryAssociations"]!.AsArray().Add(association);
            p["dependencyAssociations"]![0]!["objectPath"] = peerPath;
            var reverse = p["dependencyAssociations"]![0]!.DeepClone(); reverse["objectPath"] = targetPath; reverse["targetObjectPath"] = peerPath;
            p["dependencyAssociations"]!.AsArray().Add(reverse);
        });
        var cycleReport = Report(cycle);
        Check((int)cycleReport["summary"]!["dependencyCyclicMappingCount"]! == 2, "A synthetic cyclic instance mapping is retained without authorizing installation");
        Check(cycleReport["dependencyAssociations"]!.AsArray().All(r => !(bool)r!["dependencyClosureVerified"]!), "Cycle analysis never promotes native closure"); packets.Add(cycle);
        foreach (string defect in new[] { "duplicate", "extra", "scope", "target", "index" })
        {
            var invalid = Change(p => {
                var row = p["dependencyAssociations"]![0]!;
                if (defect == "duplicate") p["dependencyAssociations"]!.AsArray().Add(row.DeepClone());
                else if (defect == "extra") row["nativeQualified"] = true;
                else if (defect == "scope") row["scope"] = "beforeComponent";
                else if (defect == "target") row["targetObjectPath"] = "RenamedInstance";
                else row["dependencyIndex"] = 1;
            });
            try { Receipt(invalid); throw new Exception("Invalid dependency mapping accepted"); }
            catch (BridgeException error) when (error.Code == "InvalidHmiComponentReview") { Check(true, "Invalid mapping refuses through retained workflow: " + defect); }
        }
        foreach (string id in uploaded) uploads.Release(id);
        Check(JsonNode.DeepEquals(receipt, new HmiComponentWorkflowStore(root).Page((string)receipt["recordId"]!)), "Upload release and store restart retain the exact receipt");
        var forged = Record(receipt); forged["report"]!["dependencyAssociations"]![0]!["status"] = "different";
        byte[] forgedBytes = JsonSerializer.SerializeToUtf8Bytes(forged, BridgeProtocol.Json); string forgedId = Hash(forgedBytes);
        File.WriteAllBytes(Path.Combine(root, "hmi-component-reviews", forgedId + ".json"), forgedBytes);
        try { store.Page(forgedId); throw new Exception("Forged dependency result accepted"); }
        catch (BridgeException error) when (error.Code == "ArtifactChanged") { Check(true, "Rehashed invented dependency result cannot replace original evidence"); }
        string originalPath = Path.Combine(root, "hmi-component-reviews", (string)request["files"]![0]!["sha256"]! + ".source");
        byte[] original = File.ReadAllBytes(originalPath);
        try
        {
            File.AppendAllText(originalPath, " ");
            try { store.Page((string)receipt["recordId"]!); throw new Exception("Changed export accepted"); }
            catch (BridgeException error) when (error.Code == "ArtifactChanged") { Check(true, "Changed retained original export refuses reopening"); }
        }
        finally { File.WriteAllBytes(originalPath, original); }
        Check(JsonNode.DeepEquals(receipt, store.Page((string)receipt["recordId"]!)), "Restored original bytes restore the exact receipt");
        var fixture = new JsonObject { ["artifactRoot"] = root, ["packets"] = packets, ["files"] = libraryFixture["files"]!.DeepClone(),
            ["reviewIds"] = new JsonArray(reviewIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), fixture.ToJsonString(BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { completed = true, nativeExecuted = false, count = checks.Count, checks }, BridgeProtocol.Json));
        Console.WriteLine($"PASS dependency associations: {checks.Count} checks; retained original exports and synthetic saved observations, no native execution.");
        return fixture;
    }
}
