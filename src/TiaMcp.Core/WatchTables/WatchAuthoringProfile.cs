using System.Text.Json.Nodes;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.WatchTables;

/// <summary>The WT-05 creation candidate is deliberately smaller than its generic transformation keys.
/// Matching this scope is not a native qualification receipt or permission to dispatch.</summary>
public static class WatchAuthoringProfile
{
    public const string Key = "watch.create-owned-v21-1511-v41-de-active-bool.v1";
    public const string Table = "WT05_BoolCreation";
    public const string Symbol = "\"TiaMcp_EnableGate_DB\".Active";
    public const string Reason = "Captured Output state of the selected FB instance: Active";
    public const string NativeProfile = "f41301a47cadd7b1c72a23bfbb16bf55df4581c5f03d22e3d903785a270a2d8f";
    public const string Hardware = "OrderNumber:6ES7 590-1***0-0AA0|OrderNumber:6ES7 511-1AL03-0AB0/V4.1||||||";
    // Every export embeds DocumentInfo/InstalledProducts, so these pins follow the installation:
    // V21 Update 3, fixtures/watch-tables/native-authoring-v21-update3.
    public const string CandidateSemanticSha256 = "badf398cad8dbf6b7f193e72d299155b5db774b9dca91e0b4114cbd13c7efac0";
    public const string PeerProfileSha256 = "50ac10487b18714b28d10989603b1724cfe787c4d489d413c46680d5775847a1";
    private const string Fb = "326bc4096d5faf7491ea1f920bb273a58dba6b426cad743cd07c95631344674d";
    private const string Db = "fff585da83be4a2e777d7a9b6057352268cd18534106592c090854e234d3c16a";
    private const string ForceTable = "c7b378c7f45d04ecfb47dcaaa2bc047e97fb5cb474b514c86a0156acb81a309a";

    public static JsonObject Assess(JsonObject review, byte[] candidate)
    {
        var failures = new JsonArray();
        void Require(bool condition, string name) { if (!condition) failures.Add(name); }
        try
        {
            var before = review["before"]!.AsObject(); var target = before["target"]!.AsObject();
            var observation = before["observation"]!.AsObject(); var selector = review["selector"]!.AsObject();
            var capture = review["generation"]!["capture"]!.AsObject(); var binding = capture["binding"]!.AsObject();
            var peers = before["peers"]!.AsObject(); var edit = review["edit"]!.AsObject();
            string? plc = Text(selector, "plcPath"), project = Text(selector, "projectPath");
            Require(Text(review, "mode") == "create" && selector["allowAbsent"]?.GetValue<bool>() == true &&
                Text(selector, "group") == "root" && Text(selector, "tableName") == Table &&
                Text(target, "group") == "root" && Text(target, "name") == Table &&
                target["exists"]?.GetValue<bool>() == false && target["rows"]?.AsArray().Count == 0,
                "exactAbsentRootDestination");
            Require(project is not null && plc is not null && Text(binding, "projectPath") == project && Text(binding, "plcPath") == plc &&
                Text(observation, "projectFilePath") == project && observation["reference"]?["object"]?["plcPath"]?.GetValue<string>() == plc &&
                JsonNode.DeepEquals(selector["sourceBinding"], binding), "sourceDestinationIdentity");
            Require(Text(observation, "hardwareIdentity") == Hardware && Text(observation, "profileSha256") == NativeProfile &&
                observation["nativeIdentity"] is null && observation["reference"]?["snapshot"]?["tiaVersion"]?.GetValue<int>() == 21 &&
                observation["offline"]?.GetValue<bool>() == true && before["projectModified"]?.GetValue<bool>() == false &&
                target["isConsistent"]?.GetValue<bool>() == true, "cpuFirmwareInstalledApiSavedOffline");
            Require(JsonNode.DeepEquals(peers["languages"], JsonNode.Parse("{\"active\":[\"de-DE\"],\"reference\":\"de-DE\",\"editing\":\"de-DE\"}")), "exactProjectCultures");
            Require(Text(binding, "profile") == "v21-direct-instance-declarations-1" &&
                Text(binding, "blockPath") == plc + "/blocks/TiaMcp_EnableGate" && Text(binding, "instanceDbPath") == plc + "/blocks/TiaMcp_EnableGate_DB" &&
                Text(binding, "blockSha256") == Fb && Text(binding, "instanceSha256") == Db &&
                Text(peers, "block:" + plc + "/blocks/TiaMcp_EnableGate") == Fb &&
                Text(peers, "block:" + plc + "/blocks/TiaMcp_EnableGate_DB") == Db, "exactDirectInstanceDeclarations");
            var tables = before["tables"]!.AsArray();
            Require(tables.Count == 1 && tables[0]?["kind"]?.GetValue<string>() == "force" && tables[0]?["name"]?.GetValue<string>() == "Force table" &&
                tables[0]?["group"]?.GetValue<string>() == "root" && tables[0]?["semanticSha256"]?.GetValue<string>() == ForceTable &&
                peers.Count == 10 && peers.Count(p => p.Key.StartsWith("block:", StringComparison.Ordinal)) == 4 &&
                peers.Count(p => p.Key.StartsWith("tags:", StringComparison.Ordinal)) == 1 &&
                peers.All(p => !p.Key.StartsWith("watch:", StringComparison.Ordinal)), "emptyForceAndSixPeerExports");
            Require(plc is not null && PeerHash(peers, plc) == PeerProfileSha256, "completePeerAndHardwareProfile");
            Require(Text(review["template"]!, "origin") == "packagedGenuineNativeCapture" &&
                Text(review["template"]!, "sourceSha256") == WatchTableNativeTemplate.Sha256, "pinnedPackagedTemplate");
            var steps = edit["steps"]!.AsArray(); var step = steps.Count == 1 ? steps[0]?["step"] as JsonObject : null;
            string? id = step is null ? null : Text(step, "candidateId");
            Require(step is not null && step.Count == 3 && Text(step, "kind") == "add" && step["templateOrdinal"]?.GetValue<int>() == 0 &&
                id is not null && Text(capture["selectors"]!, id) == Symbol, "oneDefaultBoolAddition");
            var rows = review["generation"]!["plan"]!["rows"]!.AsArray().Where(r => r?["candidateId"]?.GetValue<string>() == id).ToArray();
            Require(rows.Length == 1 && rows[0]?["candidate"]?["datatype"]?.GetValue<string>() == "Bool" &&
                rows[0]?["candidate"]?["reason"]?.GetValue<string>() == Reason &&
                JsonNode.DeepEquals(rows[0]?["candidate"]?["memberPath"], new JsonArray("Active")), "exactCapturedBoolOutput");
            Require(JsonNode.DeepEquals(edit["requiredProfiles"], JsonNode.Parse("[\"watch.add-instance-bool.v1\",\"watch.create-instance.v1\",\"watch.reference-free-authoring-ids.v1\"]")) &&
                JsonNode.DeepEquals(edit["documentIdMapping"], JsonNode.Parse("{\"0\":\"0\",\"1\":\"1\",\"2\":\"2\",\"3\":\"3\"}")), "exactTransformationAndIdMapping");
            Require(WatchTableDocument.SemanticHash(candidate) == CandidateSemanticSha256 && Text(review, "expectedSemanticSha256") == CandidateSemanticSha256,
                "completeCandidateXmlIncludingIdsAndMetadata");
            Require(JsonNode.DeepEquals(edit["before"], new JsonObject { ["name"] = Table, ["rows"] = new JsonArray() }) &&
                JsonNode.DeepEquals(edit["after"], WatchTableAuthoring.Rows(new WatchTableDocument(candidate))) && steps.Count == 1 &&
                JsonNode.DeepEquals(steps[0]?["before"], edit["before"]) && JsonNode.DeepEquals(steps[0]?["after"], edit["after"]), "completeReviewSummaries");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.IO.InvalidDataException or NullReferenceException or System.Xml.XmlException)
        { failures.Add("malformedProfileEvidence"); }
        return new JsonObject { ["profile"] = Key, ["scopeMatched"] = failures.Count == 0, ["mismatches"] = failures,
            ["productionApplyEnabled"] = false, ["nativeVerified"] = false,
            ["workerSha256"] = review["before"]?["observation"]?["adapterSha256"]?.DeepClone(),
            ["requiredEvidence"] = "Current frozen Host/Core/worker package; native collision and stale-source refusal; full peer preservation, saved fresh reopen, recovery to absence and fresh recovered reopen; exact owned terminal proofs. Historical revision-03/binary-01 evidence does not admit this build." };
    }

    private static string? Text(JsonNode node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string PeerHash(JsonObject peers, string plc)
    {
        int boundary = plc.IndexOf("/devices/", StringComparison.Ordinal);
        if (boundary <= "/projects/".Length || !plc.StartsWith("/projects/TiaMcpScratch_", StringComparison.Ordinal) ||
            plc[boundary..] != "/devices/ScratchStation/items/ScratchPLC/plc/ScratchPLC") return "";
        string prefix = plc[..boundary];
        string PathValue(string value) => value.StartsWith(prefix + "/", StringComparison.Ordinal) ? "/projects/{owned}" + value[prefix.Length..] : value;
        JsonNode? Normalize(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                var normalized = new JsonObject();
                foreach (var pair in obj)
                {
                    string key = pair.Key;
                    if (key.StartsWith("block:", StringComparison.Ordinal)) key = "block:" + PathValue(key[6..]);
                    if (key.StartsWith("tags:", StringComparison.Ordinal)) key = "tags:" + PathValue(key[5..]);
                    normalized[key] = pair.Key is "path" or "containerPath" or "owner" && pair.Value is JsonValue v && v.TryGetValue<string>(out var path)
                        ? JsonValue.Create(PathValue(path)) : Normalize(pair.Value);
                }
                return normalized;
            }
            if (node is JsonArray array) return new JsonArray(array.Select(Normalize).ToArray());
            return node?.DeepClone();
        }
        return CanonicalJson.Sha256(Normalize(peers)!);
    }
}
