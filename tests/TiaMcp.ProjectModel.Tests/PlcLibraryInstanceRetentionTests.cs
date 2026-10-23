using Newtonsoft.Json.Linq;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using TiaMcp.Core;
using TiaMcp.Indexing;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class PlcLibraryInstanceRetentionTests
{
    internal static Task Run()
    {
        int checks = 0;
        PlcLibraryInstanceCaptureTests.Run((name, test) => { test(); checks++; Console.WriteLine("PASS " + name); });
        var model = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/project-explorer/representative.project-model.json")));
        var node = model["nodes"]!.OfType<JObject>().First(n => (string?)n["kind"] == "block");
        var f = new PlcLibraryInstanceCaptureTests.Fixture();
        var dependency = new Siemens.Engineering.Library.Types.LibraryTypeVersion();
        f.Version.ReadDependencies = () => new[] { dependency };
        var observation = PlcLibraryInstanceCapture.Read(f.Item);
        node["metadata"] ??= new JObject(); node["metadata"]!["libraryInstance"] = observation.DeepClone();
        var hmi = model["nodes"]!.OfType<JObject>().Single(n => (string?)n["kind"] == "hmi");
        hmi["metadata"]!["family"] = "classic";
        string nodeId = (string)node["nodeId"]!;
        var store = new ProjectModelStore(); var snapshot = store.Capture(model); string id = (string)snapshot["snapshotId"]!;
        var reference = store.Reference(id, nodeId);
        observation["versionGuid"] = Guid.NewGuid().ToString("D");
        observation["dependencies"]!["requiredVersionGuids"]![0] = Guid.NewGuid().ToString("D");
        node["metadata"]!["libraryInstance"]!["status"] = "changed";
        var resolved = store.ResolveReference(reference, (string)snapshot["sessionGeneration"]!);
        Check.Equal("linked", (string?)resolved["metadata"]!["libraryInstance"]!["status"]);
        Check.Equal(f.Version.Identity.ToString("D"), (string?)resolved["metadata"]!["libraryInstance"]!["versionGuid"]);
        Check.Equal(dependency.Identity.ToString("D"), (string?)resolved["metadata"]!["libraryInstance"]!["dependencies"]!["requiredVersionGuids"]![0]);
        string root = Path.Combine(Path.GetTempPath(), "tia-library-instance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var reviews = new ProjectReviewStore(root);
            var saved = reviews.Save(JsonNode.Parse(snapshot.ToString())!.AsObject(), [], new());
            string reviewId = (string)saved["reviewId"]!;
            var reopened = new ProjectReviewStore(root).Read(reviewId);
            var actual = reopened["model"]!["nodes"]!.AsArray().OfType<JsonObject>().Single(n => (string?)n["nodeId"] == nodeId)["metadata"]!["libraryInstance"]!;
            Check.Equal(f.Version.Identity.ToString("D"), (string?)actual["versionGuid"]);
            Check.Equal(false, (bool)actual["nativeQualified"]!);
            Check.Equal("observed", (string?)actual["dependencies"]!["status"]);
            Check.Equal(dependency.Identity.ToString("D"), (string?)actual["dependencies"]!["requiredVersionGuids"]![0]);
            Check.Equal(false, (bool)actual["dependencies"]!["dependencyClosureVerified"]!);
            var request = new JsonObject { ["schemaVersion"] = "hmi-component-review-request.v1", ["operation"] = "install",
                ["reason"] = "Retain a synthetic library observation without promoting its authority.", ["beforeComponent"] = null,
                ["afterComponent"] = new JsonObject { ["reviewId"] = reviewId, ["objectPaths"] = new JsonArray((string)node["objectPath"]!) },
                ["componentMatches"] = new JsonArray(), ["destination"] = new JsonObject { ["reviewId"] = reviewId,
                    ["hmiPath"] = (string)hmi["objectPath"]!, ["family"] = "classic" },
                ["outputs"] = new JsonArray(), ["bindings"] = new JsonArray(), ["files"] = new JsonArray() };
            byte[] input = Encoding.UTF8.GetBytes(request.ToJsonString());
            var uploads = new ArtifactUploadStore(root);
            byte[] reviewBytes = ProjectReviewStore.ReadArtifact(root, saved["artifact"]!.AsObject());
            var reviewUpload = uploads.Begin("review.json", reviewBytes.Length, Convert.ToHexStringLower(SHA256.HashData(reviewBytes)));
            uploads.Write(reviewUpload.UploadId, 0, Convert.ToBase64String(reviewBytes));
            var imported = new ProjectReviewStore(root).Import(uploads.Commit(reviewUpload.UploadId).ArtifactRef!);
            uploads.Release(reviewUpload.UploadId);
            Check.Equal(reviewId, (string?)imported["reviewId"]);
            Check.Equal(false, (bool)imported["sourceAuthenticityVerified"]!);
            var upload = uploads.Begin("library-observation.json", input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
            uploads.Write(upload.UploadId, 0, Convert.ToBase64String(input));
            var receipt = new HmiComponentWorkflowStore(root).Review(uploads.Commit(upload.UploadId).ArtifactRef!);
            uploads.Release(upload.UploadId);
            var reopenedReceipt = new HmiComponentWorkflowStore(root).Page((string)receipt["recordId"]!);
            Check.Equal(true, JsonNode.DeepEquals(receipt, reopenedReceipt));
            var record = JsonNode.Parse(ProjectReviewStore.ReadArtifact(root, receipt["reportArtifact"]!.AsObject()))!;
            var component = record["report"]!["afterComponent"]!;
            Check.Equal(true, JsonNode.DeepEquals(actual, component["objects"]![0]!["capturedObject"]!["metadata"]!["libraryInstance"]));
            foreach (string flag in new[] { "libraryIdentityVerified", "runtimeInstanceVerified", "dependencyClosureVerified" })
                Check.Equal(false, (bool)component[flag]!);
            var page = new HmiComponentWorkflowStore(root).Page((string)receipt["recordId"]!, "components");
            Check.Equal("unmatchedAfter", (string?)page["rows"]![0]!["value"]!["status"]);
            // Old snapshots stay usable and do not acquire an invented association.
            var legacy = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/project-explorer/representative.project-model.json")))!.AsObject();
            var old = reviews.Save(legacy, [], new());
            var oldReopened = new ProjectReviewStore(root).Read((string)old["reviewId"]!);
            Check.Equal(false, oldReopened["model"]!["nodes"]!.AsArray().Any(n => n?["metadata"]?["libraryInstance"] is not null));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PLC library instance: {checks} capture cases and snapshot/saved-review retention passed; synthetic native shapes only.");
        return Task.CompletedTask;
    }
}
