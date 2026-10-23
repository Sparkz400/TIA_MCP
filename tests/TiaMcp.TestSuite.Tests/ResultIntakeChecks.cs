using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

internal static class ResultIntakeChecks
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root); if (Directory.Exists(root)) throw new Exception("Fresh result intake evidence required.");
        Directory.CreateDirectory(root); var names = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); names.Add(name); }
        void Reject(Action action, string code) { try { action(); } catch (BridgeException e) when (e.Code == code) { names.Add("refused: " + code); return; } throw new Exception("Expected " + code); }
        var reviews = new ProjectReviewStore(root); var jobs = new TestSuiteStyleJobs(root); var uploads = new ArtifactUploadStore(root);
        const string block = "/projects/Fixture/PLC/Blocks/Motor", other = "/projects/Fixture/Other/Blocks/Motor";
        string Review(string project = "fixture.ap21", string plc = "PLC/A", string scope = "Root", bool exported = false)
        {
            var nodes = new JsonArray(new JsonObject { ["nodeId"] = "project", ["kind"] = "project", ["objectPath"] = "/projects/Fixture" });
            foreach (var (path, id, owner) in new[] { (block, "motor", plc), (other, "other-motor", "PLC/Other") })
                nodes.Add(new JsonObject { ["nodeId"] = id, ["kind"] = "block", ["objectPath"] = path, ["plcPath"] = owner, ["scopePath"] = scope, ["name"] = "Motor" });
            const string xml = "<Document><SW.Blocks.FC><AttributeList><Name>Motor</Name></AttributeList></SW.Blocks.FC></Document>";
            var documents = new JsonArray();
            if (exported) documents.Add(new JsonObject { ["objectPath"] = block, ["status"] = "exported", ["xml"] = xml,
                ["utf8Sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(xml))) });
            return reviews.Save(new JsonObject { ["schemaVersion"] = "project-model.v1", ["project"] = new JsonObject { ["projectPath"] = project, ["host"] = "synthetic" },
                ["nodes"] = nodes, ["edges"] = new JsonArray() }, documents, new JsonObject { ["syntheticFixture"] = true })["reviewId"]!.GetValue<string>();
        }
        JsonObject Packet(string reviewId, string project = "fixture.ap21", string plc = "PLC/A", string scopePath = "Root", string? observation = null, int messages = 1)
        {
            var reference = new EngineeringObjectReference(new(project, plc, scopePath, "/projects/Fixture/Rules/Names", "ruleSet"), new(observation ?? reviewId, observation ?? reviewId));
            var scope = reference with { Object = reference.Object with { ObjectPath = block, Kind = "block" } };
            var definition = new TestSuiteDefinitionCapture("names", "style", "Names", ".xml", reference, Encoding.UTF8.GetBytes("<Rule/>"), "synthetic-review-v1", [scope], [], true, true, []);
            var plan = TestSuiteRunPlanner.Prepare([definition], ["names"], [], "style");
            var results = new[] { new NativeTestResult("names", definition.Manifest.Reference, "Success", 0, 0,
                Enumerable.Range(0, messages).Select(i => new NativeTestMessage("Success", "Counter-only supplied warning " + i, block, null, "Unknown", 0, 1, [])).ToArray(), true, true, true) };
            return JsonSerializer.SerializeToNode(new { schemaVersion = "testsuite-supplied-results.v1", plan, results,
                scope = new[] { new TestSuiteScopeObservation("names", definition.Manifest.Reference, [scope], [], true) } }, BridgeProtocol.Json)!.AsObject();
        }
        (string Id, string Reference, byte[] Bytes) Upload(JsonObject packet)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet, BridgeProtocol.Json);
            var upload = uploads.Begin("results.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            for (int start = 0; start < bytes.Length; start += upload.MaxChunkBytes)
                uploads.Write(upload.UploadId, start, Convert.ToBase64String(bytes, start, Math.Min(upload.MaxChunkBytes, bytes.Length - start)));
            return (upload.UploadId, uploads.Commit(upload.UploadId).ArtifactRef!, bytes);
        }
        EngineeringArtifactReference Ref(JsonNode row) => row.Deserialize<EngineeringArtifactReference>(BridgeProtocol.Json)!;
        JsonObject Page(JsonObject receipt, string section = "summary", int offset = 0, int count = 50)
        { var r = Ref(receipt["receipt"]!); return new ProjectReviewStore(root).TestSuiteResultsPage(r.RelativePath, r.Sha256, section, offset, count); }
        string reviewId = Review(); var packet = Packet(reviewId); var upload = Upload(packet);
        JsonObject Health(params JsonObject[] receipts) => reviews.Health(reviewId, testSuiteResults: receipts.Select(r => Ref(r["receipt"]!)).ToArray())["testSuite"]!.AsObject();
        var imported = reviews.ImportTestSuiteResults(reviewId, upload.Reference);
        var rows = Page(imported, "findings")["rows"]!.AsArray(); var row = rows[0]!["value"]!;
        Check(row["binding"]!.GetValue<string>() == "exactCapturedSource" && row["finding"]!["resolvedObject"]!["object"]!["objectPath"]!.GetValue<string>() == block, "Exact captured-source finding binds to saved object");
        Check(row["finding"]!["state"]!.GetValue<string>() == "Success" && row["finding"]!["warningCount"]!.GetValue<int>() == 1, "Contradictory native state/counter survives end-to-end review");
        Check(imported["summary"]!["nativeQualified"]!.GetValue<bool>() == false && !imported["summary"]!["suppliedRequiredCoveragePassed"]!.GetValue<bool>(), "Unverified warning cannot become native qualification or supplied pass");
        uploads.Release(upload.Id);
        Check(jobs.ReadBytes(Ref(imported["input"]!)).AsSpan().SequenceEqual(upload.Bytes) && Page(imported)["reviewId"]!.GetValue<string>() == reviewId, "Released upload survives byte-exact retention and store restart");
        Check(Page(imported, "findings", 1)["rows"]!.AsArray().Count == 0, "Empty terminal page is explicit");
        Reject(() => Page(imported, "findings", 2), "InvalidRequest"); Reject(() => Page(imported, "missing"), "InvalidRequest"); Reject(() => Page(imported, "findings", 0, 201), "InvalidRequest");

        var stale = reviews.ImportTestSuiteResults(reviewId, Upload(Packet(reviewId, observation: new('a', 64))).Reference);
        row = Page(stale, "findings")["rows"]![0]!["value"]!;
        Check(row["binding"]!.GetValue<string>() == "differentSourceObservation" && row["finding"]!["resolvedObject"] is null && row["candidateNodeId"]!.GetValue<string>() == "motor", "Same selector in another observation stays an unresolved candidate");
        Check(!stale["summary"]!["savedReviewScopeBound"]!.GetValue<bool>(), "Cross-observation scope cannot bind to review");
        var foreignPacket = Packet(reviewId); foreignPacket["results"]![0]!["messages"]![0]!["path"] = other;
        var foreign = reviews.ImportTestSuiteResults(reviewId, Upload(foreignPacket).Reference);
        row = Page(foreign, "findings")["rows"]![0]!["value"]!;
        Check(row["binding"]!.GetValue<string>() == "differentProjectPlcOrScope" && row["candidateNodeId"] is null && row["finding"]!["resolvedObject"] is null, "Same-name object on another PLC cannot become a candidate or resolved finding");
        foreach (string? path in new string?[] { null, "/missing" })
        {
            var missing = Packet(reviewId); missing["results"]![0]!["messages"]![0]!["path"] = path;
            var inspected = reviews.ImportTestSuiteResults(reviewId, Upload(missing).Reference);
            Check(Page(inspected, "findings")["rows"]![0]!["value"]!["binding"]!.GetValue<string>() == "objectAbsentFromReview", "Missing/null native location remains explicit");
        }
        var clean = Packet(reviewId, messages: 0); var cleanReview = reviews.ImportTestSuiteResults(reviewId, Upload(clean).Reference);
        Check(cleanReview["summary"]!["suppliedRequiredCoveragePassed"]!.GetValue<bool>() && cleanReview["summary"]!["savedReviewScopeBound"]!.GetValue<bool>(), "Complete clean supplied evidence reports its narrow coverage");
        Check((int)XDocument.Parse(Encoding.UTF8.GetString(jobs.ReadBytes(Ref(cleanReview["junit"]!)))).Root!.Attribute("errors")! == 0, "Complete clean supplied JUnit passes offline evidence gate only");
        Check(reviews.Health(reviewId)["testSuite"] is null, "Unselected Test Suite results do not appear implicitly in project health");
        var emptyHealth = Health();
        Check(emptyHealth["selectionCount"]!.GetValue<int>() == 0 && !emptyHealth["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>() && !emptyHealth["wholeProjectHealthComplete"]!.GetValue<bool>(), "Empty health selection cannot pass by vacuous coverage");
        var cleanHealth = Health(cleanReview);
        Check(cleanHealth["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>() && !cleanHealth["nativeQualified"]!.GetValue<bool>() && !cleanHealth["wholeProjectHealthComplete"]!.GetValue<bool>(), "Passing supplied coverage never establishes native or whole-project health");
        var combinedHealth = Health(cleanReview, imported);
        Check(combinedHealth["resultCount"]!.GetValue<int>() == 2 && combinedHealth["findingCount"]!.GetValue<int>() == 1 && !combinedHealth["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>(), "Conflicting attempts stay separate instead of replacing a failure with a pass");
        Check(combinedHealth["reports"]![0]!["outcomeCounts"]!["passed"]!.GetValue<int>() == 1 && combinedHealth["reports"]![1]!["outcomeCounts"]!["inconclusive"]!.GetValue<int>() == 1, "Per-packet outcome counts preserve positive-warning classification");
        Check(JsonNode.DeepEquals(combinedHealth["reports"]![1]!["receipt"], imported["receipt"]), "Health retains the exact receipt for paged finding and diagnostic navigation");
        var staleHealth = Health(stale);
        Check(staleHealth["resolvedFindingCount"]!.GetValue<int>() == 0 && staleHealth["unresolvedFindingCount"]!.GetValue<int>() == 1 && !staleHealth["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>(), "Same-review import with cross-observation results remains explicit unresolved health evidence");
        var cleanStale = reviews.ImportTestSuiteResults(reviewId, Upload(Packet(reviewId, observation: new('a', 64), messages: 0)).Reference);
        Check(cleanStale["summary"]!["suppliedRequiredCoveragePassed"]!.GetValue<bool>() && !Health(cleanStale)["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>(), "Clean supplied results cannot bypass exact saved-review scope binding in health");
        Reject(() => Health(imported, imported), "InvalidRequest");
        string anotherReview = Review(); var anotherImport = reviews.ImportTestSuiteResults(anotherReview, Upload(Packet(anotherReview)).Reference);
        Reject(() => Health(imported, anotherImport), "TestSuiteReviewMismatch");
        clean["scope"] = new JsonArray(); var incomplete = reviews.ImportTestSuiteResults(reviewId, Upload(clean).Reference);
        var junit = XDocument.Parse(Encoding.UTF8.GetString(jobs.ReadBytes(Ref(incomplete["junit"]!))));
        Check((int)junit.Root!.Attribute("errors")! == 1 && junit.Descendants("error").Single().Attribute("type")!.Value == "incompleteSuppliedEvidence", "Missing scope produces an actual JUnit failure despite successful raw result");
        Check(junit.Descendants("property").Any(p => (string?)p.Attribute("name") == "nativeQualified" && (string?)p.Attribute("value") == "false"), "JUnit preserves explicit unqualified provenance");
        Check(!Health(incomplete)["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>(), "Health cannot promote raw success with missing independent scope");
        var rootError = Packet(reviewId, messages: 0); rootError["results"]![0]!["errorCount"] = 2;
        var rootErrorReview = reviews.ImportTestSuiteResults(reviewId, Upload(rootError).Reference);
        var rootErrorRow = Page(rootErrorReview, "results")["rows"]![0]!["value"]!;
        Check(rootErrorRow["captureIndex"]!.GetValue<int>() == 0 && rootErrorRow["nativeErrorCount"]!.GetValue<int>() == 2 && rootErrorRow["nativeState"]!.GetValue<string>() == "Success",
            "Root counters without diagnostic messages remain visible with original capture index");
        var rootHealth = Health(rootErrorReview);
        Check(rootHealth["findingCount"]!.GetValue<int>() == 0 && rootHealth["reports"]![0]!["outcomeCounts"]!["executionError"]!.GetValue<int>() == 1 && !rootHealth["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>(), "Zero message findings cannot hide a root native error counter in health");
        var absent = Packet(reviewId); absent["results"] = new JsonArray();
        var absentReview = reviews.ImportTestSuiteResults(reviewId, Upload(absent).Reference);
        var absentRow = Page(absentReview, "results")["rows"]![0]!["value"]!;
        Check(absentRow["outcome"]!.GetValue<string>() == "notRun" && absentRow["captureIndex"] is null && absentRow["nativeState"] is null, "Missing required result cannot acquire invented raw provenance");
        Check(Health(absentReview)["reports"]![0]!["outcomeCounts"]!["notRun"]!.GetValue<int>() == 1, "Health preserves missing execution units rather than inventing results");
        string exportedReview = Review(exported: true);
        var contentMismatch = reviews.ImportTestSuiteResults(exportedReview, Upload(Packet(exportedReview)).Reference);
        Check(!contentMismatch["summary"]!["savedReviewScopeBound"]!.GetValue<bool>() && Page(contentMismatch, "scopeBindings")["rows"]![0]!["value"]!["binding"]!.GetValue<string>() == "differentObjectOrContent",
            "Unknown required content cannot equal a captured exported-content hash");
        foreach (string field in new[] { "plan", "results", "scope" })
        {
            var bad = Packet(reviewId); bad[field] = null;
            Reject(() => reviews.ImportTestSuiteResults(reviewId, Upload(bad).Reference), field == "plan" ? "InvalidRunPlan" : field == "results" ? "InvalidTestResults" : "InvalidScopeCoverage");
        }
        var unknown = Packet(reviewId); unknown["nativeQualified"] = true;
        Reject(() => reviews.ImportTestSuiteResults(reviewId, Upload(unknown).Reference), "InvalidTestSuiteArtifact");
        var forged = Packet(reviewId); forged["plan"]!["nativeExecutionAuthorized"] = true;
        Reject(() => reviews.ImportTestSuiteResults(reviewId, Upload(forged).Reference), "StaleRunPlan");
        string reportPath = Path.Combine(root, Ref(imported["report"]!).RelativePath); byte[] reportBytes = File.ReadAllBytes(reportPath);
        File.WriteAllText(reportPath, "tampered"); Reject(() => Page(imported), "ArtifactChanged"); Reject(() => Health(imported), "ArtifactChanged"); File.WriteAllBytes(reportPath, reportBytes);
        string rawPath = Path.Combine(root, Ref(imported["input"]!).RelativePath); byte[] original = File.ReadAllBytes(rawPath);
        File.WriteAllText(rawPath, "tampered"); Reject(() => Page(imported), "ArtifactChanged"); File.WriteAllBytes(rawPath, original);
        string savedPath = Path.Combine(root, "project-reviews", reviewId + ".json"); byte[] saved = File.ReadAllBytes(savedPath);
        File.WriteAllText(savedPath, "tampered"); Reject(() => Page(imported), "ArtifactChanged"); File.WriteAllBytes(savedPath, saved);
        var receiptObject = JsonNode.Parse(jobs.ReadBytes(Ref(imported["receipt"]!)))!.AsObject();
        receiptObject["report"] = JsonSerializer.SerializeToNode(jobs.RetainBytes(Encoding.UTF8.GetBytes("{}")), BridgeProtocol.Json);
        var wrongDerived = jobs.Retain(receiptObject);
        Reject(() => reviews.TestSuiteResultsPage(wrongDerived.RelativePath, wrongDerived.Sha256), "ArtifactChanged");
        string aliasPath = "testsuite-jobs/aliased-report.json"; File.WriteAllBytes(Path.Combine(root, aliasPath), reportBytes);
        receiptObject = JsonNode.Parse(jobs.ReadBytes(Ref(imported["receipt"]!)))!.AsObject(); receiptObject["report"]!["relativePath"] = aliasPath;
        var aliasedReceipt = new JsonObject { ["receipt"] = JsonSerializer.SerializeToNode(jobs.Retain(receiptObject), BridgeProtocol.Json) };
        Reject(() => Health(imported, aliasedReceipt), "InvalidRequest");
        var selected = new List<JsonObject>();
        for (int i = 0; i < 33; i++)
        {
            var attempt = Packet(reviewId); attempt["results"]![0]!["messages"]![0]!["description"] = "Distinct supplied attempt " + i;
            selected.Add(reviews.ImportTestSuiteResults(reviewId, Upload(attempt).Reference));
        }
        var maximumHealth = Health(selected.Take(32).ToArray());
        Check(maximumHealth["selectionCount"]!.GetValue<int>() == 32 && maximumHealth["findingCount"]!.GetValue<int>() == 32 && JsonSerializer.SerializeToUtf8Bytes(maximumHealth, BridgeProtocol.Json).Length <= 128 * 1024,
            "All 32 selected reports fit the health section without truncation");
        Reject(() => Health(selected.ToArray()), "InvalidRequest");
        var healthModel = reviews.Read(reviewId)["model"]!.DeepClone().AsObject();
        healthModel["nodes"]![1]!["metadata"] = new JsonObject { ["isConsistent"] = false };
        string healthId = reviews.Save(healthModel, new JsonArray(), new JsonObject())["reviewId"]!.GetValue<string>();
        var healthImport = reviews.ImportTestSuiteResults(healthId, Upload(Packet(healthId, messages: 0)).Reference); var healthRef = Ref(healthImport["receipt"]!);
        var firstHealthPage = reviews.Health(healthId, maxItems: 1, testSuiteResults: [healthRef]);
        var nextHealthPage = reviews.Health(healthId, offset: 1, maxItems: 1, testSuiteResults: [healthRef]);
        Check(firstHealthPage["findings"]!.AsArray().Count == 1 && nextHealthPage["findings"]!.AsArray().Count == 0 && firstHealthPage["testSuite"]!["allSelectedSuppliedEvidencePassed"]!.GetValue<bool>() && JsonNode.DeepEquals(firstHealthPage["testSuite"], nextHealthPage["testSuite"]),
            "Metadata health paging stays separate from complete selected Test Suite summaries");

        string longText = new('界', 8000), largeReviewId = Review(longText, longText, longText);
        var large = reviews.ImportTestSuiteResults(largeReviewId, Upload(Packet(largeReviewId, longText, longText, longText)).Reference);
        var largePage = Page(large, "findings");
        Check(largePage["rows"]![0]!["omitted"]!.GetValue<bool>() && JsonSerializer.SerializeToUtf8Bytes(largePage).Length < 128 * 1024, "Oversized finding row has bounded explicit download marker");
        var largeRef = Ref(large["receipt"]!);
        Check(reviews.ReadTestSuiteFinding(largeRef.RelativePath, largeRef.Sha256, 0)["finding"]!["finding"]!["resolvedObject"]!["object"]!["projectPath"]!.GetValue<string>() == longText,
            "Direct retained finding access preserves full evidence beyond the display page budget");
        string nearText = new('界', 6700), nearReviewId = Review(nearText, nearText, nearText);
        var nearPacket = Packet(nearReviewId, nearText, nearText, nearText);
        var near = reviews.ImportTestSuiteResults(nearReviewId, Upload(nearPacket).Reference);
        var nearValue = JsonNode.Parse(jobs.ReadBytes(Ref(near["report"]!)))!["findings"]![0]!;
        int padding = 120 * 1024 - JsonSerializer.SerializeToUtf8Bytes(nearValue, BridgeProtocol.Json).Length;
        string description = nearPacket["results"]![0]!["messages"]![0]!["description"]!.GetValue<string>();
        if (padding < 0 || padding + description.Length > 8192) throw new Exception("Boundary fixture padding out of range: " + padding);
        nearPacket["results"]![0]!["messages"]![0]!["description"] = description + new string('x', padding);
        var boundary = reviews.ImportTestSuiteResults(nearReviewId, Upload(nearPacket).Reference);
        var boundaryPage = Page(boundary, "findings");
        Check(boundaryPage["rows"]![0]!["omitted"]!.GetValue<bool>() && boundaryPage["nextOffset"] is null,
            "Row wrapper crossing the byte limit advances the cursor with an explicit download marker");
        var filesBeforeLimit = Directory.GetFiles(Path.Combine(root, "testsuite-jobs"), "*", SearchOption.AllDirectories).Order().ToArray();
        Reject(() => reviews.ImportTestSuiteResults(largeReviewId, Upload(Packet(largeReviewId, longText, longText, longText, messages: 1000)).Reference), "TestResultLimit");
        Check(Directory.GetFiles(Path.Combine(root, "testsuite-jobs"), "*", SearchOption.AllDirectories).Order().SequenceEqual(filesBeforeLimit), "Report size refusal occurs before result artifact publication");
        var scalePacket = Packet(reviewId, messages: 1000); var scale = reviews.ImportTestSuiteResults(reviewId, Upload(scalePacket).Reference);
        int seen = 0; int? cursor = 0;
        while (cursor is int offset) { var page = Page(scale, "findings", offset, 200); seen += page["rows"]!.AsArray().Count; cursor = page["nextOffset"]?.GetValue<int>(); }
        Check(seen == 1000 && scale["summary"]!["findingCount"]!.GetValue<int>() == 1000, "Full paged findings retained without duplicates or omissions");
        var bulkPacket = Packet(reviewId, messages: 100000);
        foreach (var message in bulkPacket["results"]![0]!["messages"]!.AsArray()) message!["path"] = null;
        var bulk = reviews.ImportTestSuiteResults(reviewId, Upload(bulkPacket).Reference);
        var tail = Page(bulk, "findings", 99999, 1);
        Check(bulk["summary"]!["findingCount"]!.GetValue<int>() == 100000 && tail["rows"]![0]!["value"]!["finding"]!["description"]!.GetValue<string>().EndsWith("99999") && tail["nextOffset"] is null,
            "100000 supplied messages retain all findings and the exact terminal row within artifact bounds");
        File.WriteAllText(Path.Combine(root, "mcp-fixture.json"), JsonSerializer.Serialize(new { artifactRoot = root, reviewId, packet, review = reviews.Read(reviewId) }, BridgeProtocol.Json));
        File.WriteAllText(Path.Combine(root, "checks.json"), JsonSerializer.Serialize(new { checks = names.Count, names, nativeExecuted = false, nativeQualified = false }, BridgeProtocol.Json));
        Console.WriteLine($"PASS supplied Test Suite result intake: {names.Count} checks; no native execution"); return names.Count;
    }
}
