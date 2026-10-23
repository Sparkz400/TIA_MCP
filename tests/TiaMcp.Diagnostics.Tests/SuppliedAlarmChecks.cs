using System.Text;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class SuppliedAlarmChecks
{
    internal const string AlarmPath = Fixture.Plc + "/units/LineA/alarms/42";
    internal static void AddEvidence(Fixture f)
    {
        var nodes = f.Model["nodes"]!.AsArray();
        nodes.Add(new JsonObject { ["nodeId"] = "cpu", ["kind"] = "plc", ["objectPath"] = Fixture.Plc, ["parentId"] = "project" });
        nodes.Add(new JsonObject { ["nodeId"] = "unit", ["kind"] = "softwareUnit", ["objectPath"] = Fixture.Plc + "/units/LineA", ["parentId"] = "cpu" });
        nodes.Add(new JsonObject { ["nodeId"] = "alarm-42", ["kind"] = "alarm", ["objectPath"] = AlarmPath, ["parentId"] = "unit", ["plcPath"] = Fixture.Plc,
            ["metadata"] = new JsonObject { ["alarmId"] = 42, ["text"] = "Überlast conveyor", ["syntheticFixture"] = true } });
        nodes.Add(new JsonObject { ["nodeId"] = "peer-42", ["kind"] = "alarm", ["objectPath"] = Fixture.Plc + "/units/LineB/alarms/42", ["plcPath"] = Fixture.Plc,
            ["metadata"] = new JsonObject { ["alarmId"] = 42, ["text"] = "Same ID, different owner" } });
        nodes.Add(new JsonObject { ["nodeId"] = "supervision", ["kind"] = "supervision", ["objectPath"] = Fixture.Plc + "/supervisions/17", ["parentId"] = "unit" });
        nodes.Add(new JsonObject { ["nodeId"] = "hmi", ["kind"] = "hmiAlarm", ["objectPath"] = "/projects/Fixture/hmi/alarms/42", ["parentId"] = "project" });
        foreach (var (from, to, kind) in new[] { ("alarm-42", "supervision", "dependsOn"), ("hmi", "alarm-42", "hmiBinding"), ("supervision", "DB_Line", "reads") })
            f.Model["edges"]!.AsArray().Add(new JsonObject { ["kind"] = kind,
                ["source"] = Endpoint(from), ["target"] = Endpoint(to),
                ["evidence"] = new JsonObject { ["origin"] = "parsedSupported", ["resolution"] = "resolved", ["location"] = "independently authored fixture only" } });
        f.SaveReview();
        JsonObject Endpoint(string id) => new() { ["nodeId"] = id, ["objectPath"] = nodes.Single(n => n!["nodeId"]!.GetValue<string>() == id)!["objectPath"]!.DeepClone() };
    }
    internal static JsonObject Request(Fixture f)
    {
        var request = f.Request(); request["alarmContext"] = new JsonObject { ["reviewId"] = f.ReviewId, ["nodeId"] = "alarm-42", ["objectPath"] = AlarmPath };
        return request;
    }

    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Alarm context check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        using var f = new Fixture(); AddEvidence(f);
        var request = Request(f); string original = request.ToJsonString(); var receipt = f.Planner.Plan(request); var plan = receipt["plan"]!;
        string id = receipt["id"]!.GetValue<string>(); var alarm = plan["alarmEvidence"]!;
        Check(original == request.ToJsonString());
        Check(alarm["object"]!["objectPath"]!.GetValue<string>() == AlarmPath && alarm["object"]!["metadata"]!["alarmId"]!.GetValue<int>() == 42);
        Check(alarm["ancestryStatus"]!.GetValue<string>() == "capturedToProject" && alarm["ancestry"]!.AsArray().Count == 3);
        Check(alarm["relationships"]!.AsArray().Count == 2 && alarm["totalAdjacentObserved"]!.GetValue<int>() == 2);
        foreach (var r in alarm["relationships"]!.AsArray())
        {
            Check(r!["status"]!.GetValue<string>() == "exactCapturedEndpoints");
            Check(JsonNode.DeepEquals(r["edge"], f.Model["edges"]![r["index"]!.GetValue<int>()]));
        }
        Check(!alarm["sourceAuthenticityVerified"]!.GetValue<bool>() && !alarm["storageSelected"]!.GetValue<bool>() && !alarm["coverageComplete"]!.GetValue<bool>());
        Check(JsonNode.DeepEquals(new DiagnosticStore(f.Root).Read(id)["alarmEvidence"], alarm));
        var watchRequest = new JsonObject { ["tableName"] = "Alarm checks", ["tiaVersion"] = 21,
            ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Compare final output with selected instance." }) };
        var watch = new DiagnosticWatchPlanning(f.Root).Plan(id, watchRequest);
        Check(JsonNode.DeepEquals(new DiagnosticStore(f.Root).Read(watch["id"]!.GetValue<string>())["alarmEvidence"], alarm));
        foreach (string kind in new[] { "supervision", "hmiAlarm" })
        {
            var node = f.Model["nodes"]!.AsArray().Single(n => (string?)n!["kind"] == kind)!;
            var other = Request(f); other["alarmContext"]!["nodeId"] = node["nodeId"]!.DeepClone(); other["alarmContext"]!["objectPath"] = node["objectPath"]!.DeepClone();
            Check(f.Planner.Plan(other)["plan"]!["alarmEvidence"]!["object"]!["kind"]!.GetValue<string>() == kind);
        }
        var noStorage = Request(f); noStorage.Remove("blockPath"); noStorage.Remove("instance"); noStorage.Remove("signals");
        var unresolved = f.Planner.Plan(noStorage); Check(unresolved["plan"]!["selectedInstance"] is null && unresolved["plan"]!["signals"]!.AsArray().Count == 0);
        Reject("InstanceResolutionIncomplete", () => new DiagnosticWatchPlanning(f.Root).Plan(unresolved["id"]!.GetValue<string>(), watchRequest));
        foreach (string mutation in new[] { "stale", "wrongPath", "wrongId", "extra", "missing", "null", "nonAlarm" })
        {
            var bad = Request(f); var selector = bad["alarmContext"]!.AsObject();
            if (mutation == "stale") selector["reviewId"] = new string('a', 64);
            if (mutation == "wrongPath") selector["objectPath"] = Fixture.Plc + "/units/LineB/alarms/42";
            if (mutation == "wrongId") selector["nodeId"] = "peer-42";
            if (mutation == "extra") selector["nativeQualified"] = true;
            if (mutation == "missing") selector.Remove("nodeId");
            if (mutation == "null") bad["alarmContext"] = null;
            if (mutation == "nonAlarm") { selector["nodeId"] = "FB_Conveyor"; selector["objectPath"] = Fixture.Block("FB_Conveyor"); }
            Reject(mutation is "extra" or "missing" or "null" ? "InvalidRequest" : "AlarmContextUnresolved", () => f.Planner.Plan(bad));
        }
        var selected = f.Model["nodes"]!.AsArray().Single(n => n!["nodeId"]!.GetValue<string>() == "alarm-42")!;
        selected["origin"] = "virtual"; f.SaveReview(); Reject("AlarmContextUnresolved", () => f.Planner.Plan(Request(f)));
        selected["origin"] = "native"; selected["access"] = new JsonObject { ["state"] = "protected" };
        selected["parentId"] = "missing"; f.SaveReview();
        var partial = f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!;
        Check(partial["ancestryStatus"]!.GetValue<string>() == "missingParent" && partial["object"]!["access"]!["state"]!.GetValue<string>() == "protected");
        selected["parentId"] = "alarm-42"; f.SaveReview();
        Check(f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!["ancestryStatus"]!.GetValue<string>() == "cycle");
        for (int i = 0; i < 66; i++) f.Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "parent-" + i, ["kind"] = "folder", ["origin"] = "virtual", ["parentId"] = i == 65 ? "project" : "parent-" + (i + 1) });
        selected["parentId"] = "parent-0"; f.SaveReview();
        var ancestryBound = f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!;
        Check(ancestryBound["ancestry"]!.AsArray().Count == 64 && ancestryBound["ancestryStatus"]!.GetValue<string>() == "ancestorLimit" && ancestryBound["truncated"]!.GetValue<bool>());
        f.Model["nodes"]!.AsArray().Single(n => (string?)n!["nodeId"] == "parent-0")!["opaque"] = new string('x', 270000); f.SaveReview();
        Check(f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!["ancestryStatus"]!.GetValue<string>() == "byteLimit");
        selected["parentId"] = "unit";
        var edge = f.Model["edges"]![1]!; edge["target"]!["nodeId"] = "wrong"; f.SaveReview();
        Check(f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!["relationships"]![0]!["status"]!.GetValue<string>() == "endpointMismatch");
        edge["target"]!["nodeId"] = "supervision"; edge["evidence"]!["resolution"] = "unresolved"; f.SaveReview();
        Check(f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!["unusableRelationships"]!.GetValue<int>() == 1);
        edge["evidence"]!["resolution"] = "resolved"; edge["kind"] = "contains"; f.SaveReview();
        Check(f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!["relationships"]![0]!["status"]!.GetValue<string>() == "unsupportedRelationshipKind");
        edge["kind"] = "dependsOn"; edge["evidence"]!["resolution"] = "unresolved";
        for (int i = 0; i < 110; i++) f.Model["edges"]!.AsArray().Add(edge.DeepClone());
        f.SaveReview(); var bounded = f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!;
        Check(bounded["relationships"]!.AsArray().Count == 100 && bounded["omittedRelationships"]!.GetValue<int>() == 12 && bounded["truncated"]!.GetValue<bool>());
        edge["payload"] = new string('x', 270000); f.SaveReview();
        bounded = f.Planner.Plan(Request(f))["plan"]!["alarmEvidence"]!;
        Check(bounded["omittedRelationships"]!.GetValue<int>() > 0 && Encoding.UTF8.GetByteCount(bounded.ToJsonString(BridgeProtocol.Json)) <= 256 * 1024);
        selected["metadata"]!["oversized"] = new string('x', 270000); f.SaveReview(); Reject("ArtifactCapacity", () => f.Planner.Plan(Request(f)));
        selected["metadata"]!.AsObject().Remove("oversized"); f.SaveReview();
        var mappings = new JsonArray(plan["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject { ["id"] = s!["id"]!.DeepClone(), ["storagePath"] = s["storagePath"]!.DeepClone(),
            ["memberPath"] = s["memberPath"]!.DeepClone(), ["reason"] = "Explicit unchanged signal mapping." }).ToArray());
        var replan = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"), ["instance"] = f.Request()["instance"]!.DeepClone(),
            ["reason"] = "Explicit new captured review", ["signalMappings"] = mappings };
        var next = new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!;
        Check(next["alarmEvidence"] is null && next["lineage"]!["previousPlanId"]!.GetValue<string>() == id);
        replan["alarmContext"] = request["alarmContext"]!.DeepClone(); Reject("AlarmContextUnresolved", () => new DiagnosticReplanner(f.Root).Replan(id, replan));
        replan["alarmContext"] = Request(f)["alarmContext"]!.DeepClone();
        Check(new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!["alarmEvidence"]!["reviewId"]!.GetValue<string>() == f.ReviewId);
        var forged = new DiagnosticStore(f.Root).Read(id); forged["alarmEvidence"]!["object"]!["metadata"]!["text"] = "forged";
        var forgedReceipt = new DiagnosticStore(f.Root).Save(forged);
        Reject("CaptureMismatch", () => new DiagnosticWatchPlanning(f.Root).Plan(forgedReceipt["id"]!.GetValue<string>(), watchRequest));
        forged["alarmEvidence"] = "invalid"; forgedReceipt = new DiagnosticStore(f.Root).Save(forged);
        Reject("CaptureMismatch", () => new DiagnosticWatchPlanning(f.Root).Plan(forgedReceipt["id"]!.GetValue<string>(), watchRequest));
        Console.WriteLine($"PASS supplied alarm diagnostic context: {checks} checks; synthetic evidence only");
    }
}
