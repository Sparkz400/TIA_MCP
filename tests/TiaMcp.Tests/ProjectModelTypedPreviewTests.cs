using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TiaMcp.Indexing;

namespace TiaMcp.Tests;

internal static class ProjectModelTypedPreviewTests
{
    private const string BlockPath = "/projects/Fixture/devices/D/items/C/plc/P/blocks/Logic";
    internal static Task AdaptationAndBounds()
    {
        var request = Request(); var observation = Observation();
        var result = ProjectModelTypedPreview.Adapt(request, observation);
        Check.Equal(ProjectModelTypedPreview.ProviderId, (string)result["provider"]!);
        Check.Equal("partial", (string)result["status"]!);
        Check.Equal(new string('b', 64), (string)result["contentRevision"]!["value"]!);
        Check.Equal("exportedNativeBlockXml", (string)result["contentRevision"]!["scope"]!);
        Check.Equal(false, JToken.DeepEquals(request.Node["metadata"]!["metadataRevision"], result["contentRevision"]));
        Check.Equal(0, ((JArray)result["attachments"]!).Descendants().OfType<JProperty>().Count(p => p.Name == "nativeXml"));
        Check.Equal(false, (bool)result["attachments"]![0]!["content"]!["nodes"]![0]!["supported"]!);
        Check.Equal("opaque", (string)result["attachments"]![0]!["content"]!["nodes"]![0]!["kind"]!);
        Check.Equal("unresolved", (string)result["attachments"]![0]!["content"]!["connections"]![0]!["resolution"]!);
        Check.Equal(BlockPath, (string)result["attachments"]![0]!["source"]!["objectPath"]!);
        Check.Equal((string)observation["observedAtUtc"]!, (string)result["attachments"]![0]!["source"]!["capturedAtUtc"]!);
        ProjectModelProviders.Validate(result, request, ProjectModelTypedPreview.ProviderId);
        Check.Equal(true, observation["graph"]!["networks"]![0]!["nativeXml"] != null);
        // Wrong-copy/same-name results and source-less previews cannot be accepted.
        var wrong = Observation(); wrong["graph"]!["blockPath"] = BlockPath.Replace("/devices/D/", "/devices/Other/");
        Expect("InvalidProviderResponse", () => ProjectModelTypedPreview.Adapt(request, wrong));
        wrong = Observation(); ((JObject)wrong["nativeXml"]!).Remove("sha256");
        Expect("InvalidProviderResponse", () => ProjectModelTypedPreview.Adapt(request, wrong));
        wrong = Observation(); wrong["nativeXml"]!["sha256"] = new string('z', 64);
        Expect("InvalidProviderResponse", () => ProjectModelTypedPreview.Adapt(request, wrong));
        var multiple = Observation(); var networks = (JArray)multiple["graph"]!["networks"]!;
        networks.Add(networks[0]!.DeepClone()); networks.Add(networks[0]!.DeepClone());
        var itemBound = ProjectModelTypedPreview.Adapt(Request(maxItems: 1), multiple);
        Check.Equal("partial", (string)itemBound["status"]!); Check.Equal(1, itemBound["attachments"]!.Count());
        Check.Equal(3, (int)itemBound["coverage"]![0]!["visited"]!); Check.Equal(true, (bool)itemBound["coverage"]![0]!["continuationAvailable"]!);
        var large = Observation(); large["graph"]!["networks"]![0]!["comment"] = new string('x', 12000);
        var byteBound = ProjectModelTypedPreview.Adapt(Request(maxBytes: 4096), large);
        Check.Equal("partial", (string)byteBound["status"]!); Check.Equal(0, byteBound["attachments"]!.Count());
        Check.Equal(true, Encoding.UTF8.GetByteCount(byteBound.ToString(Formatting.None)) <= 4096);
        Check.Equal(true, (bool)byteBound["coverage"]![0]!["continuationAvailable"]!);
        return Task.CompletedTask;
    }

    internal static JObject AdaptedFixture() => ProjectModelTypedPreview.Adapt(Request(), Observation());

    private static ProjectModelProviderRequest Request(int maxItems = 200, int maxBytes = 65536)
    {
        var reference = new JObject { ["schemaVersion"] = "project-model.v1", ["snapshotId"] = "typed-fixture", ["sessionGeneration"] = "typed-test-session",
            ["projectPath"] = "D:\\fixture\\Fixture.ap21", ["nodeId"] = "logic", ["objectPath"] = BlockPath };
        var node = new JObject { ["nodeId"] = "logic", ["objectPath"] = BlockPath, ["kind"] = "block", ["name"] = "Logic", ["origin"] = "native",
            ["metadata"] = new JObject { ["metadataRevision"] = new JObject { ["value"] = new string('a', 64), ["scope"] = "exposedMetadata", ["includesContent"] = false } } };
        return new ProjectModelProviderRequest(reference, node, "typedLadFbdPreview", maxItems, maxBytes);
    }

    private static JObject Observation() => new JObject {
        ["observedAtUtc"] = "2026-10-08T12:00:00Z", ["truncated"] = false,
        ["nativeXml"] = new JObject { ["path"] = "D:\\artifacts\\logic.xml", ["sha256"] = new string('b', 64), ["bytes"] = 900 },
        ["graph"] = new JObject { ["blockPath"] = BlockPath, ["sourceSha256"] = new string('c', 64), ["networks"] = new JArray(new JObject {
            ["networkId"] = "1", ["language"] = "LAD", ["coverage"] = "partial", ["nativeXml"] = "<Network><Unknown /></Network>",
            ["nodes"] = new JArray(new JObject { ["key"] = "opaque-7", ["kind"] = "opaque", ["supported"] = false, ["nativeXml"] = "<Unknown />",
                ["preservation"] = new JObject { ["nativeXml"] = "<Unknown attribute='retained' />", ["fragmentHash"] = new string('d', 64) } }),
            ["connections"] = new JArray(new JObject { ["source"] = "opaque-7", ["target"] = null, ["resolution"] = "unresolved", ["reason"] = "Unknown native pin", ["evidence"] = new JObject { ["nativeXml"] = "<Wire />" } }) }) } };

    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (ProjectModelStore.ModelException error) { Check.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected model error: " + code);
    }
}
