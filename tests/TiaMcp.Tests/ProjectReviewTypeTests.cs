using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;

namespace TiaMcp.Tests;

internal static class ProjectReviewTypeTests
{
    private const string Root = "/projects/Alarm";
    private const string TypePath = Root + "/plc/types/Status";
    private const string Xml = "<Document><Type><Interface><Member Name='Ready' Datatype='Bool'/></Interface><Supervisions><Opaque Code='1'>Überlast</Opaque></Supervisions></Type></Document>";

    internal static Task ContentAndImpacts()
    {
        using var f = new Fixture();
        var receipt = f.Store.Save(Model(), Documents(Xml), new JsonObject());
        Check.Equal(0, receipt["exportedBlocks"]!.GetValue<int>());
        Check.Equal(1, receipt["exportedTypes"]!.GetValue<int>());
        string before = f.Import(f.Store.Read(receipt["reviewId"]!.GetValue<string>()));
        string after = f.Save(Xml.Replace("Datatype='Bool'", "Datatype='Int'"));
        var compared = f.Store.Compare(before, after, impactDepth: 4);
        Check.Equal(1, compared["contentComparedTypes"]!.GetValue<int>());
        Check.Equal(0, compared["contentComparedBlocks"]!.GetValue<int>());
        Check.Equal("interfaceChanged,documentChanged", string.Join(",", compared["changes"]![0]!["changedFields"]!.AsArray().Select(n => n!.GetValue<string>())));
        var paths = compared["observedImpactPaths"]!.AsArray();
        Check.Equal(8, paths.Count);
        foreach (var full in paths.Where(p => p!["distance"]!.GetValue<int>() == 4))
        {
            Check.Equal("hmi,alarm,supervision,member,Status", string.Join(",", full!["objectPaths"]!.AsArray().Select(n => n!.GetValue<string>().Split('/').Last())));
            Check.Equal("hmiBinding,dependsOn,reads,usesType", string.Join(",", full["edges"]!.AsArray().Select(n => n!["kind"]!.GetValue<string>())));
            Check.Equal(true, full["edges"]!.AsArray().All(e => Model()["edges"]!.AsArray().Any(original => JsonNode.DeepEquals(e, original))));
            Check.Equal(full["reviewSide"]!.GetValue<string>() == "before" ? before : after, full["reviewId"]!.GetValue<string>());
        }
        Check.Equal(true, JsonNode.DeepEquals(paths, f.Store.Compare(before, after, offset: 1, maxItems: 1, impactDepth: 4)["observedImpactPaths"]));
        Check.Equal(true, f.Store.Compare(before, after)["impactTraversal"]!["truncated"]!.GetValue<bool>());
        Check.Equal(false, compared["impactCoverageComplete"]!.GetValue<bool>());
        foreach (string changed in new[] { Xml.Replace("Code='1'", "Code='2'"), Xml.Replace("Überlast", "Übertemperatur") })
        {
            var delta = f.Store.Compare(before, f.Save(changed));
            Check.Equal("documentChanged", delta["changes"]![0]!["changedFields"]![0]!.GetValue<string>());
            Check.Equal(1, delta["changes"]![0]!["changedFields"]!.AsArray().Count);
        }
        Check.Equal(1, new ProjectReviewStore(f.Root).Search(before, "überlast")["totalObservedHits"]!.GetValue<int>());
        return Task.CompletedTask;
    }

    internal static Task CoverageAndScope()
    {
        using var f = new Fixture(); string before = f.Save(Xml);
        foreach (var documents in new[] { new JsonArray(), new JsonArray(new JsonObject { ["objectPath"] = TypePath, ["status"] = "unavailable", ["reason"] = "not supplied" }) })
        {
            string missing = f.Store.Save(Model(), documents, new JsonObject())["reviewId"]!.GetValue<string>();
            var delta = f.Store.Compare(before, missing);
            Check.Equal(1, delta["contentUnknownTypes"]!.GetValue<int>());
            Check.Equal(0, delta["contentComparedTypes"]!.GetValue<int>());
            Check.Equal(0, delta["contentUnknownBlocks"]!.GetValue<int>());
            Check.Equal(false, delta["wholeProjectEquivalent"]!.GetValue<bool>());
        }
        var partial = Model(); partial["nodes"]!.AsArray().RemoveAt(1); partial["edges"]!.AsArray().RemoveAt(3);
        string partialId = f.Store.Save(partial, new JsonArray(), new JsonObject())["reviewId"]!.GetValue<string>();
        var absence = f.Store.Compare(before, partialId, impactDepth: 4);
        Check.Equal("removedFromCapturedScope", absence["changes"]![0]!["change"]!.GetValue<string>());
        Check.Equal(4, absence["observedImpactPaths"]!.AsArray().Count);
        Check.Equal(true, absence["observedImpactPaths"]!.AsArray().All(p => p!["reviewSide"]!.GetValue<string>() == "before"));
        var transition = Model(); transition["nodes"]![1]!["kind"] = "block";
        string blockId = f.Store.Save(transition, Documents(Xml), new JsonObject())["reviewId"]!.GetValue<string>();
        var kindChange = f.Store.Compare(before, blockId);
        Check.Equal(1, kindChange["contentComparedBlocks"]!.GetValue<int>());
        Check.Equal(1, kindChange["contentComparedTypes"]!.GetValue<int>());
        Check.Equal("kind", kindChange["changes"]![0]!["changedFields"]![0]!.GetValue<string>());
        return Task.CompletedTask;
    }

    internal static async Task IntakeValidation()
    {
        using var f = new Fixture(); var original = f.Store.Read(f.Save(Xml));
        foreach (string mutation in new[] { "virtual", "wrongKind", "duplicate", "foreign", "signature", "hash", "wrongEndpoint" })
        {
            var changed = original.DeepClone().AsObject();
            if (mutation == "virtual") changed["model"]!["nodes"]![1]!["origin"] = "virtual";
            if (mutation == "wrongKind") changed["model"]!["nodes"]![1]!["kind"] = "tag";
            if (mutation == "duplicate") changed["documents"]!.AsArray().Add(changed["documents"]![0]!.DeepClone());
            if (mutation == "foreign") changed["documents"]![0]!["objectPath"] = Root + "/other/Status";
            if (mutation == "signature") changed["documents"]![0]!["signatures"]!["document"] = new string('0', 64);
            if (mutation == "hash") changed["documents"]![0]!["utf8Sha256"] = new string('0', 64);
            if (mutation == "wrongEndpoint") changed["model"]!["edges"]![3]!["target"]!["nodeId"] = "member";
            var error = await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(f.Import(changed)));
            Check.Equal(mutation is "signature" or "hash" ? "ArtifactChanged" : "InvalidSnapshot", error.Code);
            if (mutation is "virtual" or "wrongKind" or "duplicate" or "foreign")
                await Check.ThrowsAsync<BridgeException>(() => Task.FromResult(f.Store.Save(changed["model"]!.AsObject(), changed["documents"]!.AsArray(), new JsonObject())));
        }
        var unresolved = original.DeepClone().AsObject();
        unresolved["model"]!["edges"]![3]!["evidence"]!["resolution"] = "unresolved";
        string withoutLink = f.Import(unresolved);
        var changedSource = unresolved.DeepClone().AsObject(); changedSource["model"]!["nodes"]![1]!["name"] = "Changed";
        Check.Equal(0, f.Store.Compare(withoutLink, f.Import(changedSource), impactDepth: 4)["observedImpactPaths"]!.AsArray().Count);
    }

    private static JsonObject Model()
    {
        var nodes = new JsonArray(new JsonObject { ["nodeId"] = "root", ["kind"] = "project", ["objectPath"] = Root },
            new JsonObject { ["nodeId"] = "type", ["kind"] = "type", ["objectPath"] = TypePath });
        foreach (string name in new[] { "hmi", "alarm", "supervision", "member" })
            nodes.Add(new JsonObject { ["nodeId"] = name, ["kind"] = name, ["objectPath"] = Root + "/" + name });
        var edges = new JsonArray();
        foreach (var (source, target, kind) in new[] { ("hmi", "alarm", "hmiBinding"), ("alarm", "supervision", "dependsOn"), ("supervision", "member", "reads"), ("member", "type", "usesType") })
            edges.Add(new JsonObject { ["kind"] = kind,
                ["source"] = new JsonObject { ["nodeId"] = source, ["objectPath"] = Root + "/" + source },
                ["target"] = new JsonObject { ["nodeId"] = target, ["objectPath"] = target == "type" ? TypePath : Root + "/" + target },
                ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved", ["location"] = "authored fixture only" } });
        return new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["host"] = "synthetic", ["projectPath"] = "alarm.ap21" },
            ["nodes"] = nodes, ["edges"] = edges, ["coverage"] = new JsonArray() };
    }
    private static JsonArray Documents(string xml) => new(new JsonObject { ["objectPath"] = TypePath, ["status"] = "exported", ["xml"] = xml,
        ["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))) });
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tia-types-" + Guid.NewGuid().ToString("N"));
        internal ProjectReviewStore Store => new(Root);
        internal string Save(string xml) => Store.Save(Model(), Documents(xml), new JsonObject { ["syntheticFixture"] = true })["reviewId"]!.GetValue<string>();
        internal string Import(JsonObject review)
        {
            byte[] data = Encoding.UTF8.GetBytes(review.ToJsonString()); var uploads = new ArtifactUploadStore(Root);
            var begun = uploads.Begin("review.json", data.Length, Convert.ToHexStringLower(SHA256.HashData(data)));
            uploads.Write(begun.UploadId, 0, Convert.ToBase64String(data));
            var imported = Store.Import(uploads.Commit(begun.UploadId).ArtifactRef!); uploads.Release(begun.UploadId);
            return imported["reviewId"]!.GetValue<string>();
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
