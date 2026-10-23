using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.TestSuite;

internal static class SuppliedTestSuiteChecks
{
    internal static JsonObject Packet(Fixture f, string? observation = null, string? path = null)
    {
        string block = Fixture.Block("FB_Conveyor");
        var reference = new EngineeringObjectReference(new(f.Model["project"]!["projectPath"]!.GetValue<string>(), Fixture.Plc, Fixture.Plc,
            "/projects/Fixture/Rules/Names", "ruleSet"), new(observation ?? f.ReviewId, observation ?? f.ReviewId));
        string hash = f.Documents.Single(d => d!["objectPath"]!.GetValue<string>() == block)!["utf8Sha256"]!.GetValue<string>();
        var scope = reference with { Object = reference.Object with { ObjectPath = block, Kind = "block" }, ContentSha256 = hash };
        var definition = new TestSuiteDefinitionCapture("names", "style", "Names", ".xml", reference, Encoding.UTF8.GetBytes("<Rule/>"), "synthetic-diagnostic-v1", [scope], [], true, true, []);
        var plan = TestSuiteRunPlanner.Prepare([definition], ["names"], [], "style");
        return JsonSerializer.SerializeToNode(new { schemaVersion = "testsuite-supplied-results.v1", plan,
            results = new[] { new NativeTestResult("names", definition.Manifest.Reference, "Success", 0, 0,
                [new("Success", "Supplied casing warning for Conveyor; synthetic evidence only.", path ?? block, null, "Unknown", 0, 2, [])], true, true, true) },
            scope = new[] { new TestSuiteScopeObservation("names", definition.Manifest.Reference, [scope], [], true) } }, BridgeProtocol.Json)!.AsObject();
    }

    internal static void Run()
    {
        using var f = new Fixture(); var reviews = new ProjectReviewStore(f.Root); var uploads = new ArtifactUploadStore(f.Root);
        const string tagPath = Fixture.Plc + "/tags/Enable";
        f.Model["nodes"]!.AsArray().Add(new JsonObject { ["nodeId"] = "EnableTag", ["kind"] = "tag", ["name"] = "Enable",
            ["objectPath"] = tagPath, ["plcPath"] = Fixture.Plc, ["scopePath"] = Fixture.Plc });
        f.SaveReview();
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Supplied Test Suite diagnostic check " + (checks + 1)); checks++; }
        void Reject(string code, Action action) { try { action(); } catch (BridgeException e) when (e.Code == code) { checks++; return; } throw new Exception("Expected " + code); }
        JsonObject Import(JsonObject packet)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, BridgeProtocol.Json);
            var upload = uploads.Begin("results.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            uploads.Write(upload.UploadId, 0, Convert.ToBase64String(bytes));
            var result = reviews.ImportTestSuiteResults(f.ReviewId, uploads.Commit(upload.UploadId).ArtifactRef!);
            uploads.Release(upload.UploadId); return result;
        }
        JsonObject Selector(JsonObject imported)
        { var selector = imported["receipt"]!.DeepClone().AsObject(); selector["index"] = 0; return selector; }
        JsonObject Request(JsonObject imported)
        { var request = f.Request(); request.Remove("blockPath"); request["testSuiteFinding"] = Selector(imported); return request; }
        var imported = Import(Packet(f)); var request = Request(imported); string requestBefore = request.ToJsonString();
        var receipt = f.Planner.Plan(request); var plan = receipt["plan"]!; string id = receipt["id"]!.GetValue<string>();
        Check(request.ToJsonString() == requestBefore);
        Check(plan["blockPath"]!.GetValue<string>() == Fixture.Block("FB_Conveyor") && plan["selectedInstance"]!["memberPath"]![0]!.GetValue<string>() == "Conv2");
        var evidence = plan["testSuiteEvidence"]!;
        Check(evidence["finding"]!["finding"]!["warningCount"]!.GetValue<int>() == 2 && evidence["finding"]!["finding"]!["state"]!.GetValue<string>() == "Success");
        Check(!evidence["summary"]!["sourceAuthenticityVerified"]!.GetValue<bool>() && !evidence["summary"]!["nativeQualified"]!.GetValue<bool>() && !plan["nativeExecutable"]!.GetValue<bool>());
        Check(JsonNode.DeepEquals(evidence["input"], imported["input"]) && JsonNode.DeepEquals(evidence["report"], imported["report"]));
        Check(new DiagnosticStore(f.Root).Read(id)["testSuiteEvidence"]!.ToJsonString() == evidence.ToJsonString());
        Check(new DiagnosticPlanner(f.Root).Plan(Request(imported))["id"]!.GetValue<string>() == id);
        var watch = new DiagnosticWatchPlanning(f.Root).Plan(id, new JsonObject { ["tableName"] = "Finding review", ["tiaVersion"] = 21,
            ["externalStorage"] = new JsonArray(new JsonObject { ["objectPath"] = Fixture.Block("DB_IO"), ["reason"] = "Compare final storage against the explicitly selected instance." }) });
        Check(watch["rowCount"]!.GetValue<int>() == 11);
        var trace = f.Prepare(receipt); var measurement = f.Import(trace["id"]!.GetValue<string>(), f.Capture(trace["id"]!.GetValue<string>()));
        Check(f.Measurements.Analyze(measurement["id"]!.GetValue<string>())["report"] is not null);
        var noInstance = Request(imported); noInstance.Remove("instance"); noInstance.Remove("signals");
        var noInstancePlan = f.Planner.Plan(noInstance)["plan"]!;
        Check(noInstancePlan["selectedInstance"] is null && noInstancePlan["signals"]!.AsArray().Count == 0);
        var conflicting = Request(imported); conflicting["blockPath"] = Fixture.Block("FB_Line");
        Reject("TestSuiteFindingUnresolved", () => f.Planner.Plan(conflicting));
        foreach (JsonNode? index in new JsonNode?[] { JsonValue.Create(-1), JsonValue.Create(1), JsonValue.Create(0.5), null })
        { var bad = Request(imported); bad["testSuiteFinding"]!["index"] = index; Reject("InvalidRequest", () => f.Planner.Plan(bad)); }
        var spoofed = Request(imported); spoofed["testSuiteFinding"]!["nativeQualified"] = true;
        Reject("InvalidRequest", () => f.Planner.Plan(spoofed));
        var stale = Import(Packet(f, observation: new('a', 64)));
        Reject("TestSuiteFindingUnresolved", () => f.Planner.Plan(Request(stale)));
        var absent = Import(Packet(f, path: "/missing"));
        Reject("TestSuiteFindingUnresolved", () => f.Planner.Plan(Request(absent)));
        var nonBlock = Import(Packet(f, path: tagPath));
        Reject("TestSuiteFindingUnresolved", () => f.Planner.Plan(Request(nonBlock)));
        foreach (string field in new[] { "input", "report", "junit", "receipt" })
        {
            string path = Path.Combine(f.Root, imported[field]!["relativePath"]!.GetValue<string>()); byte[] original = File.ReadAllBytes(path);
            try { File.WriteAllText(path, "tampered"); Reject("ArtifactChanged", () => f.Planner.Plan(Request(imported))); }
            finally { File.WriteAllBytes(path, original); }
        }
        f.ReplaceXml("FB_Conveyor", xml => xml.Replace("</Document>", "<!--new observation--></Document>"));
        Reject("TestSuiteFindingUnresolved", () => f.Planner.Plan(Request(imported)));
        var mappings = new JsonArray(plan["signals"]!.AsArray().Select(s => (JsonNode?)new JsonObject { ["id"] = s!["id"]!.DeepClone(),
            ["storagePath"] = s["storagePath"]!.DeepClone(), ["memberPath"] = s["memberPath"]!.DeepClone(), ["reason"] = "Explicit unchanged declaration mapping." }).ToArray());
        var replan = new JsonObject { ["reviewId"] = f.ReviewId, ["blockPath"] = Fixture.Block("FB_Conveyor"), ["instance"] = f.Request()["instance"]!.DeepClone(),
            ["reason"] = "Review changed source without rebasing historical findings.", ["signalMappings"] = mappings };
        var reused = new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!;
        Check(reused["testSuiteEvidence"] is null && reused["lineage"]!["previousPlanId"]!.GetValue<string>() == id);
        Check(new DiagnosticStore(f.Root).Read(id)["testSuiteEvidence"]!["reviewId"]!.GetValue<string>() != f.ReviewId);
        replan["testSuiteFinding"] = Selector(imported);
        Reject("TestSuiteFindingUnresolved", () => new DiagnosticReplanner(f.Root).Replan(id, replan));
        var fresh = Import(Packet(f)); replan["testSuiteFinding"] = Selector(fresh);
        Check(new DiagnosticReplanner(f.Root).Replan(id, replan)["plan"]!["testSuiteEvidence"]!["reviewId"]!.GetValue<string>() == f.ReviewId);
        Console.WriteLine($"PASS supplied Test Suite diagnostic integration: {checks} checks; no native execution");
    }
}
