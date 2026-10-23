using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class ProjectReviewStoreTests
{
    private const string RootPath = "/projects/Example";
    private const string BlockPath = RootPath + "/plc/blocks/Logic";
    private const string Xml = "<Document><DocumentInfo><Created>one</Created></DocumentInfo><Block ID='1'><Interface><Member Name='Enable' Datatype='Bool'/></Interface><NetworkSource><Token Text='MotorReady'/><Comment>Überlast protection</Comment><Opaque><X/> <Y/></Opaque></NetworkSource></Block></Document>";

    internal static async Task PersistenceAndTamper()
    {
        using var files = new Fixture();
        string id = files.Save(Xml);
        var reopened = new ProjectReviewStore(files.Root);
        Check.Equal("project-review.v1", reopened.Read(id)["schemaVersion"]!.GetValue<string>());
        Check.Equal(1, reopened.Search(id, "überlast")["totalObservedHits"]!.GetValue<int>());
        string path = Path.Combine(files.Root, "project-reviews", id + ".json");
        File.AppendAllText(path, " ");
        var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(reopened.Read(id)));
        Check.Equal("ArtifactChanged", error.Code);
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(reopened.Read("../outside")));
    }

    internal static Task ContentAndImpact()
    {
        using var files = new Fixture();
        string before = files.Save(Xml);
        string timestampOnly = files.Save(Xml.Replace(">one<", ">two<"));
        Check.Equal(0, files.Store.Compare(before, timestampOnly)["totalChanges"]!.GetValue<int>());
        string after = files.Save(Xml.Replace("MotorReady", "MotorRunning"));
        var comparison = files.Store.Compare(before, after);
        Check.Equal(1, comparison["contentComparedBlocks"]!.GetValue<int>());
        Check.Equal(1, comparison["totalChanges"]!.GetValue<int>());
        Check.Equal(1, comparison["directObservedImpacts"]!.AsArray().Count);
        var fields = comparison["changes"]![0]!["changedFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Check.Equal(true, fields.Contains("logicChanged") && fields.Contains("documentChanged") && !fields.Contains("interfaceChanged"));
        var signatures = ProjectReviewStore.XmlSignatures(Xml);
        foreach (string changed in new[] { Xml.Replace("<X/> <Y/>", "<X/><Y/>"), Xml.Replace("ID='1'", "ID='2'"),
            Xml.Replace("protection", "PROTECTION"), Xml.Replace("Datatype='Bool'", "Datatype='Int'") })
            Check.Equal(true, signatures["document"]!.GetValue<string>() != ProjectReviewStore.XmlSignatures(changed)["document"]!.GetValue<string>());
        Check.Equal(false, JsonNode.DeepEquals(ProjectReviewStore.XmlSignatures("<Extension><DocumentInfo><Created>one</Created></DocumentInfo></Extension>"),
            ProjectReviewStore.XmlSignatures("<Extension><DocumentInfo><Created>two</Created></DocumentInfo></Extension>")));
        return ProjectReviewImpactTests.Run();
    }

    internal static async Task CoverageAndCopyIdentity()
    {
        using var files = new Fixture();
        string before = files.Save(Xml), noContent = files.Save(null);
        var result = files.Store.Compare(before, noContent);
        Check.Equal(1, result["contentUnknownBlocks"]!.GetValue<int>());
        Check.Equal(0, result["contentComparedBlocks"]!.GetValue<int>());
        Check.Equal(false, result["wholeProjectEquivalent"]!.GetValue<bool>());
        var other = Model(); other["project"]!["projectPath"] = "other.ap21";
        string otherId = files.Store.Save(other, Documents(Xml), new JsonObject())["reviewId"]!.GetValue<string>();
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Compare(before, otherId)));
        Check.Equal(0, files.Store.Compare(before, otherId, true)["totalChanges"]!.GetValue<int>());
        var partial = Model(); partial["nodes"]!.AsArray().RemoveAt(1);
        string partialId = files.Store.Save(partial, new JsonArray(), new JsonObject())["reviewId"]!.GetValue<string>();
        Check.Equal("removedFromCapturedScope", files.Store.Compare(before, partialId)["changes"]![0]!["change"]!.GetValue<string>());
    }

    internal static async Task SearchHealthAndBounds()
    {
        using var files = new Fixture();
        var model = Model(); model["nodes"]![1]!["metadata"]!["isConsistent"] = false;
        string id = files.Store.Save(model, Documents(Xml), new JsonObject())["reviewId"]!.GetValue<string>();
        var search = files.Store.Search(id, "MotorReady");
        Check.Equal(BlockPath, search["hits"]![0]!["objectPath"]!.GetValue<string>());
        Check.Equal("nativeXmlText", search["hits"]![0]!["origin"]!.GetValue<string>());
        Check.Equal(0, files.Store.Search(id, "MotorReady", 1)["hits"]!.AsArray().Count);
        Check.Equal("native-inconsistent", files.Store.Health(id)["findings"]![0]!["rule"]!.GetValue<string>());
        var resolved = Model();
        resolved["nodes"]![0]!["nodeId"] = "project"; resolved["nodes"]![1]!["nodeId"] = "block";
        resolved["edges"] = new JsonArray(new JsonObject { ["kind"] = "calls",
            ["source"] = new JsonObject { ["nodeId"] = "project", ["objectPath"] = RootPath },
            ["target"] = new JsonObject { ["nodeId"] = "block", ["objectPath"] = BlockPath },
            ["evidence"] = new JsonObject { ["origin"] = "nativeConfirmed", ["resolution"] = "resolved" }, ["unresolved"] = null });
        int Gaps(JsonObject value) => files.Store.Health(files.Store.Save(value, Documents(Xml), new JsonObject())["reviewId"]!.GetValue<string>())["totalFindings"]!.GetValue<int>();
        Check.Equal(0, Gaps(resolved));
        foreach (string mutation in new[] { "partial", "inferred", "detail", "wrongId", "foreignPath", "endpointPartial" })
        {
            var changed = resolved.DeepClone().AsObject(); var edge = changed["edges"]![0]!;
            edge["source"]!["resolution"] = "resolved"; edge["target"]!["resolution"] = "resolved";
            if (mutation == "partial") edge["evidence"]!["resolution"] = "partial";
            if (mutation == "inferred") edge["evidence"]!["origin"] = "inferred";
            if (mutation == "detail") edge["unresolved"] = new JsonObject { ["reason"] = "unresolved native target" };
            if (mutation == "wrongId") edge["target"]!["nodeId"] = "different";
            if (mutation == "foreignPath") edge["target"]!["objectPath"] = "/projects/Other/Logic";
            if (mutation == "endpointPartial") edge["target"]!["resolution"] = "partial";
            Check.Equal(1, Gaps(changed));
        }
        var grouped = resolved.DeepClone().AsObject();
        grouped["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "virtual", ["kind"] = "folder", ["origin"] = "virtual" });
        grouped["edges"]![0]!["kind"] = "contains";
        grouped["edges"]![0]!["target"] = new JsonObject { ["nodeId"] = "virtual" };
        Check.Equal(0, Gaps(grouped));
        grouped["edges"]![0]!["kind"] = "calls";
        Check.Equal(1, Gaps(grouped));
        grouped["nodes"]![2]!["objectPath"] = RootPath + "/virtual";
        grouped["edges"]![0]!["target"]!["objectPath"] = RootPath + "/virtual";
        Check.Equal(1, Gaps(grouped));
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Search(id, " ")));
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Health(id, -1)));
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Search(id, "MotorReady", int.MaxValue)));
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Compare(id, id, maxItems: 201)));
    }

    internal static async Task UntrustedInputs()
    {
        using var files = new Fixture();
        await Check.ThrowsAsync<XmlException>(() => Task.FromResult(ProjectReviewStore.XmlSignatures("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>")));
        var docs = Documents(Xml); docs[0]!["objectPath"] = "/projects/Other/Logic";
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Save(Model(), docs, new JsonObject())));
        docs = Documents(Xml); docs[0]!["utf8Sha256"] = new string('0', 64);
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Save(Model(), docs, new JsonObject())));
        var model = Model(); model["nodes"]!.AsArray().Add(model["nodes"]![1]!.DeepClone());
        await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(files.Store.Save(model, new JsonArray(), new JsonObject())));
    }

    internal static async Task ImportedReviewPersistence()
    {
        using var source = new Fixture(); using var target = new Fixture();
        var review = ImportFixture(source);
        review["documents"]![0]!["artifact"] = new JsonObject { ["path"] = "Z:/unavailable/native-export.xml", ["sha256"] = new string('a', 64) };
        byte[] bytes = Encoding.UTF8.GetBytes(review.ToJsonString());
        string reference = Upload(target.Root, bytes), id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var receipt = target.Store.Import(reference);
        Check.Equal(id, receipt["reviewId"]!.GetValue<string>());
        Check.Equal(false, receipt["nativeQualified"]!.GetValue<bool>());
        Check.Equal(false, receipt["sourceAuthenticityVerified"]!.GetValue<bool>());
        new ArtifactUploadStore(target.Root).Release(reference[7..]);
        Check.Equal(true, bytes.SequenceEqual(File.ReadAllBytes(receipt["artifact"]!["path"]!.GetValue<string>())));
        Check.Equal(1, new ProjectReviewStore(target.Root).Search(id, "überlast")["totalObservedHits"]!.GetValue<int>());
        Check.Equal(0, target.Store.Compare(id, id)["totalChanges"]!.GetValue<int>());
        string second = Upload(target.Root, bytes);
        Check.Equal(id, target.Store.Import(second)["reviewId"]!.GetValue<string>());
        string path = receipt["artifact"]!["path"]!.GetValue<string>(); File.AppendAllText(path, " ");
        var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(target.Store.Import(second)));
        Check.Equal("ArtifactChanged", error.Code);
        Check.Equal(bytes.Length + 1L, new FileInfo(path).Length);
    }

    internal static async Task ImportedReviewValidation()
    {
        using var source = new Fixture(); using var target = new Fixture(); var original = ImportFixture(source);
        async Task Reject(Action<JsonObject> edit, string code = "InvalidSnapshot")
        {
            var value = original.DeepClone().AsObject(); edit(value);
            var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(target.Store.Import(Upload(target.Root, Encoding.UTF8.GetBytes(value.ToJsonString())))));
            Check.Equal(code, error.Code);
        }
        await Reject(r => r["documents"]![0]!["signatures"]!["logic"] = new string('a', 64), "ArtifactChanged");
        await Reject(r => r["documents"]![0]!["xml"] = "<Document/>", "ArtifactChanged");
        await Reject(r => r["documents"]!.AsArray().Add(r["documents"]![0]!.DeepClone()));
        await Reject(r => r["documents"]![0]!["objectPath"] = "/projects/Foreign/Logic");
        await Reject(r => r["model"]!["nodes"]![1]!["nodeId"] = "0");
        await Reject(r => r["model"]!["nodes"]![1]!["objectPath"] = "/projects/Foreign/Logic");
        await Reject(r => r["model"]!["nodes"]![1]!["metadata"]!["isConsistent"] = "true");
        await Reject(r => r["model"]!["nodes"]!.AsArray().Add(r["model"]!["nodes"]![0]!.DeepClone()));
        await Reject(r => r["wholeProjectContentComplete"] = true);
        await Reject(r => r["model"]!["nodes"]![1]!["name"] = 5);
        await Reject(r => r["model"]!["nodes"]![1]!["access"] = "readable");
        await Reject(r => r["model"]!["nodes"]![1]!["metadata"]!["namespace"] = new JsonArray());
        await Reject(r => r["model"]!["edges"]!.AsArray().Add(JsonNode.Parse("""{"kind":"calls","source":{"nodeId":"0","objectPath":"/projects/Example"},"target":{"nodeId":"wrong","objectPath":"/projects/Example/plc/blocks/Logic"},"evidence":{"origin":"nativeConfirmed","resolution":"resolved"}}""")));
        await Reject(r => { for (int i = 0; i < 200; i++) r["documents"]!.AsArray().Add(new JsonObject()); }, "ArtifactCapacity");
        await Reject(r => { for (int i = 0; i < 50000; i++) r["model"]!["nodes"]!.AsArray().Add(new JsonObject()); }, "ArtifactCapacity");
        string duplicate = original.ToJsonString().Replace("\"schemaVersion\":", "\"schemaVersion\":\"ignored\",\"schemaVersion\":");
        var duplicateError = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(target.Store.Import(Upload(target.Root, Encoding.UTF8.GetBytes(duplicate)))));
        Check.Equal("InvalidSnapshot", duplicateError.Code);
        await Check.ThrowsAsync<DecoderFallbackException>(() => Task.FromResult(target.Store.Import(Upload(target.Root, [0xff, 0xfe]))));
        var unsafeXml = original.DeepClone().AsObject(); string xml = "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>";
        unsafeXml["documents"]![0]!["xml"] = xml; unsafeXml["documents"]![0]!["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));
        await Check.ThrowsAsync<XmlException>(() => Task.FromResult(target.Store.Import(Upload(target.Root, Encoding.UTF8.GetBytes(unsafeXml.ToJsonString())))));
        await Check.ThrowsAsync<XmlException>(() => Task.FromResult(ProjectReviewStore.XmlSignatures(string.Concat(Enumerable.Repeat("<x>", 130)) + string.Concat(Enumerable.Repeat("</x>", 130)))));
        Check.Equal(false, Directory.Exists(Path.Combine(target.Root, "project-reviews")));
    }

    internal static async Task ImportedReviewPublication()
    {
        using var source = new Fixture(); using var target = new Fixture(); var review = ImportFixture(source);
        review["model"]!["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "virtual", ["kind"] = "folder", ["origin"] = "virtual" });
        review["model"]!["edges"]!.AsArray().Add(JsonNode.Parse("""{"kind":"contains","source":{"nodeId":"0","objectPath":"/projects/Example"},"target":{"nodeId":"virtual"},"evidence":{"origin":"nativeConfirmed","resolution":"resolved"}}"""));
        string reference = Upload(target.Root, Encoding.UTF8.GetBytes(review.ToJsonString()));
        Directory.CreateDirectory(Path.Combine(target.Root, "project-reviews"));
        using (var gate = new FileStream(Path.Combine(target.Root, "project-reviews", ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(target.Store.Import(reference)));
            Check.Equal("IntakeBusy", error.Code);
            Check.Equal(0, Directory.GetFiles(Path.Combine(target.Root, "project-reviews"), "*.json").Length);
        }
        Check.Equal(2, target.Store.Import(reference)["nodeCount"]!.GetValue<int>());
        Check.Equal(0, Directory.GetFiles(Path.Combine(target.Root, "project-reviews"), "*.tmp").Length);
    }

    private static JsonObject ImportFixture(Fixture source)
    {
        var model = Model(); int index = 0; foreach (var row in model["nodes"]!.AsArray()) row!["nodeId"] = (index++).ToString();
        model["edges"] = new JsonArray();
        return source.Store.Read(source.Store.Save(model, Documents(Xml), new JsonObject { ["syntheticFixture"] = true })["reviewId"]!.GetValue<string>());
    }
    private static string Upload(string root, byte[] data)
    {
        var uploads = new ArtifactUploadStore(root); var status = uploads.Begin("review.json", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
        for (int offset = 0; offset < data.Length; offset += status.MaxChunkBytes)
            uploads.Write(status.UploadId, offset, Convert.ToBase64String(data.AsSpan(offset, Math.Min(status.MaxChunkBytes, data.Length - offset))));
        return uploads.Commit(status.UploadId).ArtifactRef!;
    }

    private static JsonObject Model() => new() {
        ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["host"] = "machine", ["projectPath"] = "example.ap21" },
        ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "project", ["objectPath"] = RootPath, ["name"] = "Example" },
            new JsonObject { ["kind"] = "block", ["objectPath"] = BlockPath, ["name"] = "Logic", ["metadata"] = new JsonObject { ["isConsistent"] = true } }),
        ["edges"] = new JsonArray(new JsonObject { ["kind"] = "calls", ["source"] = new JsonObject { ["objectPath"] = RootPath + "/plc/blocks/Main", ["resolution"] = "resolved" },
            ["target"] = new JsonObject { ["objectPath"] = BlockPath, ["resolution"] = "resolved" }, ["evidence"] = new JsonObject { ["origin"] = "nativeConfirmed" } }),
        ["coverage"] = new JsonArray() };
    private static JsonArray Documents(string? xml) => xml is null ? new JsonArray() : new JsonArray(new JsonObject {
        ["objectPath"] = BlockPath, ["status"] = "exported", ["xml"] = xml,
        ["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))) });
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tia-review-" + Guid.NewGuid().ToString("N"));
        internal ProjectReviewStore Store => new(Root);
        internal string Save(string? xml) => Store.Save(Model(), Documents(xml), new JsonObject())["reviewId"]!.GetValue<string>();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
