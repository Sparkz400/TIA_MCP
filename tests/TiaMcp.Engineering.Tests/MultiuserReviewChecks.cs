using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Collaboration;

internal static class MultiuserReviewChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        void Reject(Action action)
        {
            try { action(); }
            catch (Exception e) when (e is BridgeException or JsonException or ArgumentException or InvalidOperationException) { checks++; return; }
            throw new Exception("Expected invalid collaboration observation refusal.");
        }
        var snapshot = JsonNode.Parse("""
        {"captureId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","capturedAtUtc":"2026-10-18T00:00:00.0000000+00:00","producer":"supplied fixture","principal":"DOMAIN/user","server":{"host":"https://server.invalid/","port":443,"alias":"Ü Server"},
        "groups":{"coverage":"complete","diagnostic":null,"rows":[{"path":["Line"]}]},
        "projects":{"coverage":"complete","diagnostic":null,"rows":[{"groupPath":["Line"],"name":"Project","revision":"7","lock":{"state":"locked","owner":"peer"}}]},
        "sessions":{"coverage":"complete","diagnostic":null,"rows":[{"project":{"groupPath":["Line"],"name":"Project"},"sessionId":"session-a","projectPath":"D:/supplied/project.ap21","mode":"multiuser","upToDate":false}]},
        "markings":{"coverage":"complete","diagnostic":null,"rows":[{"project":{"groupPath":["Line"],"name":"Project"},"sessionId":"session-a","objectPath":"PLC/Block","markState":"MarkedByMeAndOthers"}]}}
        """)!.AsObject();
        JsonObject Request() => new() { ["schemaVersion"] = "multiuser-observations.v1", ["before"] = snapshot.DeepClone(), ["after"] = snapshot.DeepClone() };
        var same = MultiuserReview.Build(Request());
        Check((int)same["summary"]!["changeCount"]! == 0, "Identical declarations remain identical, despite reported conflict.");
        Check(same["issues"]!.AsArray().Count(x => (string?)x!["code"] == "deletedObjectsNotRepresented") == 2, "Both phases always retain deleted-object omission.");
        Check(same["issues"]!.AsArray().Any(x => (string?)x!["code"] == "reportedLock"), "Existing lock retained.");
        Check(same["issues"]!.AsArray().Any(x => (string?)x!["code"] == "reportedOutdatedSession"), "Outdated session retained.");
        Check((bool)same["conflictFreeVerified"]! == false && (bool)same["nativeExecutable"]! == false, "No authority or conflict-free claim.");
        Check(same["observations"]!.AsArray().Any(x => (string?)x!["value"]?["markState"] == "MarkedByMeAndOthers"), "Unchanged markings remain inspectable.");
        foreach (string section in new[] { "groups", "projects", "sessions", "markings" })
        {
            var request = Request(); var after = request["after"]!.AsObject(); after["captureId"] = new string('b', 32); after[section]!["rows"] = new JsonArray();
            var result = MultiuserReview.Build(request);
            Check(result["changes"]!.AsArray().Any(x => (string?)x!["section"] == section && (string?)x!["status"] == "onlyBefore"), "Missing " + section + " row is onlyBefore, not deletion.");
        }
        foreach (string state in new[] { "partial", "unknown", "denied", "failed", "unavailable" })
        {
            var request = Request(); var after = request["after"]!.AsObject(); after["captureId"] = new string('b', 32);
            after["projects"] = new JsonObject { ["coverage"] = state, ["diagnostic"] = "Public reason", ["rows"] = new JsonArray() };
            Check(MultiuserReview.Build(request)["issues"]!.AsArray().Any(x => (string?)x!["code"] == state), "Coverage " + state + " survives.");
        }
        var orphan = Request(); orphan["after"]!["captureId"] = new string('b', 32); orphan["after"]!["projects"]!["rows"] = new JsonArray();
        Check(MultiuserReview.Build(orphan)["issues"]!.AsArray().Any(x => (string?)x!["code"] == "projectNotSupplied"), "Missing parent stays visible rather than guessed.");
        void Bad(Action<JsonObject> change) { var value = Request(); value["after"]!["captureId"] = new string('b', 32); change(value); Reject(() => MultiuserReview.Build(value)); }
        Bad(x => x["nativeExecutable"] = true);
        Bad(x => { x["after"]!["captureId"] = new string('a', 32); x["after"]!["producer"] = "changed under same ID"; });
        Bad(x => { x["after"]!["captureId"] = new string('b', 32); x["after"]!["server"]!["host"] = "https://other.invalid/"; });
        foreach (string host in new[] { "https://user:secret@server.invalid/", "https://server.invalid/?token=x", "https://server.invalid/#secret", "file:///tmp/", "https://server.invalid/path", "https://server.invalid:443/" }) Bad(x => x["after"]!["server"]!["host"] = host);
        foreach (string key in new[] { "producer", "principal", "captureId", "capturedAtUtc" }) Bad(x => x["after"]![key] = "");
        foreach (string section in new[] { "groups", "projects", "sessions", "markings" })
        {
            Bad(x => x["after"]![section]!["rows"]!.AsArray().Add(x["after"]![section]!["rows"]![0]!.DeepClone()));
            Bad(x => x["after"]![section]!["coverage"] = "failed");
            Bad(x => x["after"]![section]!["rows"]![0]!["extra"] = true);
        }
        Bad(x => x["after"]!["projects"]!["rows"]![0]!["lock"]!["owner"] = null);
        Bad(x => x["after"]!["projects"]!["rows"]![0]!["lock"]!["state"] = "unlocked");
        Bad(x => x["after"]!["sessions"]!["rows"]![0]!["upToDate"] = "true");
        Bad(x => x["after"]!["groups"]!["rows"]![0]!["path"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode?)JsonValue.Create(i.ToString())).ToArray()));
        foreach (var bound in new[] { ("groups", 128), ("projects", 256), ("sessions", 512), ("markings", 2048) })
            Bad(x => { var rows = x["after"]![bound.Item1]!["rows"]!.AsArray(); var row = rows[0]!.DeepClone(); while (rows.Count <= bound.Item2) rows.Add(row.DeepClone()); });
        var large = Request(); large["after"]!["captureId"] = new string('b', 32);
        var many = large["after"]!["markings"]!["rows"]!.AsArray(); var sample = many[0]!.DeepClone(); many.Clear();
        for (int i = 0; i < 2048; i++) { var row = sample.DeepClone(); row["objectPath"] = "PLC/Block" + i; many.Add(row); }
        Check(MultiuserReview.Build(large)["observations"]!.AsArray().Count(x => (string?)x!["phase"] == "after" && (string?)x!["section"] == "markings") == 2048, "Maximum marking capture remains complete within its supplied scope.");
        Reject(() => MultiuserReview.Parse(Encoding.UTF8.GetBytes("{\"a\":[],\"a\":[]}")));
        Reject(() => MultiuserReview.Parse(new byte[] { 0xff }));
        Reject(() => MultiuserReview.Parse(new byte[MultiuserReview.InputLimit + 1]));
        var bytes = Encoding.UTF8.GetBytes(Request().ToJsonString() + "\n");
        Check(JsonNode.DeepEquals(same, MultiuserReview.Build(MultiuserReview.Parse(bytes))), "UTF-8 parsing preserves the declaration.");
        string root = Path.Combine(Path.GetTempPath(), "tia-multiuser-" + Guid.NewGuid().ToString("N"));
        var uploads = new ArtifactUploadStore(root); var pending = uploads.Begin("observations.json", bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        uploads.Write(pending.UploadId, 0, Convert.ToBase64String(bytes)); var committed = uploads.Commit(pending.UploadId);
        var store = new MultiuserReviewStore(root); var receipt = store.Review(committed.ArtifactRef!); string id = (string)receipt["recordId"]!;
        Check(JsonNode.DeepEquals(receipt, store.Review(committed.ArtifactRef!)), "Intake is byte-idempotent.");
        Check(JsonNode.DeepEquals(receipt, new MultiuserReviewStore(root).Page(id)), "New store reopens the original result.");
        var page = store.Page(id, "observations", 0, 1);
        Check((int)page["nextOffset"]! == 1 && page["rows"]!.AsArray().Count == 1, "Bounded observation page.");
        Reject(() => store.Page(id, "execute")); Reject(() => store.Page(id, "issues", int.MaxValue));
        string path = Path.Combine(root, (string)receipt["input"]!["path"]!); File.AppendAllText(path, " "); Reject(() => store.Page(id)); File.WriteAllBytes(path, bytes);
        Check(JsonNode.DeepEquals(receipt, store.Page(id)), "Exact source restoration restores only the supplied review.");
        Console.WriteLine($"PASS Multiuser supplied observations: {checks} checks; no native server execution.");
    }
}
