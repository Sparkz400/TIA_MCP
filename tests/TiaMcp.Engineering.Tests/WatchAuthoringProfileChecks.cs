using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.WatchTables;

internal static class WatchAuthoringProfileChecks
{
    internal static void Run(string repo)
    {
        int passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        string fixture = Path.Combine(repo, "fixtures/watch-tables/native-authoring-v21-update3");
        byte[] bytes = File.ReadAllBytes(Path.Combine(fixture, "creation.reviewed.xml"));
        Check(WatchTableDocument.Hash(bytes) == "55048d4003d6edfd6f6d80130a01ae1c9ede4a3d2fdf022e90377733efe0e733", "Original reviewed XML fixture changed.");
        var original = JsonNode.Parse(File.ReadAllBytes(Path.Combine(fixture, "profile-candidate-review.json")))!.AsObject();
        var matched = WatchAuthoringProfile.Assess(original, bytes);
        Check(matched["scopeMatched"]!.GetValue<bool>() && matched["profile"]!.GetValue<string>() == WatchAuthoringProfile.Key,
            "Retained exact native creation must match the bounded candidate: " + matched.ToJsonString());
        // The Update 2 review differs only in the installation-bound export hashes.
        string historical = Path.Combine(repo, "fixtures/watch-tables/native-authoring-v21");
        var update2 = WatchAuthoringProfile.Assess(JsonNode.Parse(File.ReadAllBytes(Path.Combine(historical, "profile-candidate-review.json")))!.AsObject(),
            File.ReadAllBytes(Path.Combine(historical, "creation.reviewed.xml")));
        Check(JsonNode.DeepEquals(update2["mismatches"], JsonNode.Parse("[\"exactDirectInstanceDeclarations\",\"emptyForceAndSixPeerExports\",\"completePeerAndHardwareProfile\",\"completeCandidateXmlIncludingIdsAndMetadata\"]")),
            "Update 2 evidence must stay historical: " + update2.ToJsonString());
        Check(!matched["nativeVerified"]!.GetValue<bool>() && !matched["productionApplyEnabled"]!.GetValue<bool>(), "Historical native evidence cannot activate this build.");
        var mutations = new Dictionary<string, Action<JsonObject>> {
            ["CPU firmware"] = r => r["before"]!["observation"]!["hardwareIdentity"] = WatchAuthoringProfile.Hardware.Replace("V4.1", "V4.0"),
            ["installed API"] = r => r["before"]!["observation"]!["profileSha256"] = new string('b', 64),
            ["TIA version"] = r => r["before"]!["observation"]!["reference"]!["snapshot"]!["tiaVersion"] = 20,
            ["online"] = r => r["before"]!["observation"]!["offline"] = false,
            ["unsaved"] = r => r["before"]!["projectModified"] = true,
            ["second culture"] = r => r["before"]!["peers"]!["languages"]!["active"]!.AsArray().Add("en-GB"),
            ["editing culture"] = r => r["before"]!["peers"]!["languages"]!["editing"] = "en-GB",
            ["existing table"] = r => r["before"]!["target"]!["exists"] = true,
            ["update"] = r => r["mode"] = "update",
            ["folder"] = r => r["selector"]!["group"] = "root/Child",
            ["other table name"] = r => r["selector"]!["tableName"] = "Other",
            ["populated force"] = r => r["before"]!["tables"]![0]!["semanticSha256"] = new string('c', 64),
            ["extra watch peer"] = r => r["before"]!["peers"]!["watch:root/Peer"] = new string('d', 64),
            ["hardware network"] = r => r["before"]!["peers"]!["hardware"]![5]!["network"]!["nodes"]![0]!["attributes"]!["Address"] = "192.168.0.2",
            ["unselected peer"] = r => { var peers = r["before"]!["peers"]!.AsObject(); peers[peers.First(p => p.Key.EndsWith("/blocks/Main")).Key] = new string('f', 64); },
            ["source hash"] = r => r["generation"]!["capture"]!["binding"]!["blockSha256"] = new string('a', 64),
            ["instance hash"] = r => r["generation"]!["capture"]!["binding"]!["instanceSha256"] = new string('a', 64),
            ["instance scope"] = r => r["generation"]!["capture"]!["binding"]!["instanceDbPath"] = "/Other",
            ["unreviewed source"] = r => r["selector"]!["sourceBinding"]!["blockSha256"] = new string('a', 64),
            ["explicit comment"] = r => r["edit"]!["steps"]![0]!["step"]!["comments"] = new JsonObject { ["de-DE"] = WatchAuthoringProfile.Reason },
            ["explicit position"] = r => r["edit"]!["steps"]![0]!["step"]!["insertAt"] = 0,
            ["other template row"] = r => r["edit"]!["steps"]![0]!["step"]!["templateOrdinal"] = 1,
            ["ID mapping"] = r => r["edit"]!["documentIdMapping"]!["1"] = "A",
            ["template"] = r => r["template"]!["origin"] = "capturedNativeTable",
            ["generic extra profile"] = r => r["edit"]!["requiredProfiles"]!.AsArray().Add("watch.add-instance-dint.v1"),
            ["row summary"] = r => r["edit"]!["after"]!["rows"]![0]!["modifyValue"] = "TRUE",
            ["missing scope"] = r => r.Remove("before")
        };
        foreach (var change in mutations)
        {
            var review = original.DeepClone().AsObject(); change.Value(review);
            var status = WatchAuthoringProfile.Assess(review, bytes);
            Check(!status["scopeMatched"]!.GetValue<bool>() && status["mismatches"]!.AsArray().Count > 0 &&
                !status["productionApplyEnabled"]!.GetValue<bool>(), "Unqualified scope accepted: " + change.Key);
        }
        foreach (var field in new[] { "Name", "DisplayFormat", "ModifyValue", "MonitorTrigger" })
        {
            var xml = XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
            xml.Descendants("SW.WatchAndForceTables.PlcWatchTableEntry").Single().Element("AttributeList")!.Element(field)!.Value = "different";
            using var memory = new MemoryStream(); xml.Save(memory);
            Check(!WatchAuthoringProfile.Assess(original, memory.ToArray())["scopeMatched"]!.GetValue<bool>(), "Whole XML escaped profile gate: " + field);
        }
        var ids = XDocument.Load(new MemoryStream(bytes), LoadOptions.PreserveWhitespace);
        ids.Descendants("MultilingualTextItem").Single().SetAttributeValue("ID", "A");
        using (var memory = new MemoryStream()) { ids.Save(memory); Check(!WatchAuthoringProfile.Assess(original, memory.ToArray())["scopeMatched"]!.GetValue<bool>(), "Native IDs must remain significant."); }
        foreach (string profile in new[] { WatchAuthoringProfile.Key, "watch.create-instance.v1", "watch.add-instance-bool.v1", "watch.reference-free-authoring-ids.v1" })
        {
            try { WatchMutationProfiles.RequireVerified(new JsonArray(profile), false); throw new Exception("Production profile unexpectedly enabled."); }
            catch (BridgeException error) when (error.Code == "UnsupportedProfile") { passed++; }
        }
        Console.WriteLine($"PASS WT exact candidate: {passed} checks; current production admission remains closed.");
    }
}
