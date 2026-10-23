using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;
using TiaMcp.Core.TestSuite;

namespace TiaMcp.Core;

public sealed partial class ProjectReviewStore
{
    private sealed record SuppliedTestSuite(string SchemaVersion, TestSuiteRunPlan Plan,
        NativeTestResult[] Results, TestSuiteScopeObservation[] Scope);
    private sealed record TestSuiteReviewReceipt(string SchemaVersion, string ReviewId,
        EngineeringArtifactReference Input, EngineeringArtifactReference Report, EngineeringArtifactReference Junit);
    private sealed record TestSuiteReviewRow(TestSuiteFinding Finding, string Binding, string? CandidateNodeId);
    private sealed record TestSuiteReviewOutput(byte[] Report, byte[] Junit);

    public JsonObject ImportTestSuiteResults(string reviewId, string uploadRef)
    {
        var upload = new ArtifactUploadStore(artifactsRoot).ResolveCommitted(uploadRef);
        byte[] original = ReadArtifact(artifactsRoot, new() { ["path"] = upload.Path, ["sha256"] = upload.Sha256 });
        string hash = Hash(original);
        var input = new EngineeringArtifactReference("testsuite-jobs/blobs/" + hash + ".json", hash);
        var derived = DeriveTestSuite(reviewId, original, input.RelativePath);
        var jobs = new TestSuiteStyleJobs(artifactsRoot);
        var receipt = new TestSuiteReviewReceipt("testsuite-result-review.v1", reviewId, jobs.RetainBytes(original),
            jobs.RetainBytes(derived.Report), jobs.RetainBytes(derived.Junit));
        var retained = jobs.Retain(receipt);
        return TestSuiteReviewSummary(receipt, retained, JsonNode.Parse(derived.Report)!.AsObject());
    }

    public JsonObject TestSuiteResultsPage(string relativePath, string sha256, string section = "summary", int offset = 0, int count = 50)
    {
        if (offset < 0 || count is < 1 or > 200 || section is not ("summary" or "results" or "scope" or "findings" or "scopeBindings"))
            throw new BridgeException("InvalidRequest", "Use summary/results/scope/findings/scopeBindings, a nonnegative offset and 1..200 rows.");
        var (result, report) = ReadTestSuiteReview(relativePath, sha256);
        if (section == "summary") return result;
        var source = report[section]!.AsArray();
        if (offset > source.Count) throw new BridgeException("InvalidRequest", "Offset exceeds the selected report section.");
        var rows = new JsonArray(); int next = offset, used = 0;
        while (next < source.Count && rows.Count < count)
        {
            JsonObject row = new() { ["index"] = next, ["value"] = source[next]!.DeepClone() };
            int size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            if (size > 120 * 1024)
            {
                row = new() { ["index"] = next, ["omitted"] = true,
                    ["reason"] = "Row exceeds page budget; retrieve the complete report artifact." };
                size = JsonSerializer.SerializeToUtf8Bytes(row, BridgeProtocol.Json).Length;
            }
            if (used + size > 120 * 1024) break;
            rows.Add(row); used += size; next++;
        }
        result["section"] = section; result["offset"] = offset; result["total"] = source.Count;
        result["rows"] = rows; result["nextOffset"] = next == source.Count ? null : JsonValue.Create(next);
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 124 * 1024)
            throw new BridgeException("TestResultLimit", "Encoded page exceeds 128 KiB; download the report artifact.");
        return result;
    }

    public JsonObject ReadTestSuiteFinding(string relativePath, string sha256, int index)
    {
        if (index < 0) throw new BridgeException("InvalidRequest", "Finding index must be nonnegative.");
        var (result, report) = ReadTestSuiteReview(relativePath, sha256);
        var findings = report["findings"]!.AsArray();
        if (index >= findings.Count) throw new BridgeException("InvalidRequest", "Finding index exceeds the retained report.");
        result["findingIndex"] = index; result["finding"] = findings[index]!.DeepClone();
        return result;
    }

    private JsonObject TestSuiteHealth(string reviewId, EngineeringArtifactReference[] receipts)
    {
        if (receipts.Length > 32 || receipts.Any(r => r is null) || receipts.Select(r => r.Sha256).Distinct(StringComparer.Ordinal).Count() != receipts.Length)
            throw new BridgeException("InvalidRequest", "Supply at most 32 distinct retained Test Suite receipts.");
        var reports = new JsonArray(); var inputs = new HashSet<string>(StringComparer.Ordinal);
        int resultCount = 0, findingCount = 0, resolvedCount = 0; bool passed = receipts.Length > 0;
        foreach (var receipt in receipts)
        {
            var (entry, report) = ReadTestSuiteReview(receipt.RelativePath, receipt.Sha256);
            if (Text(entry, "reviewId") != reviewId)
                throw new BridgeException("TestSuiteReviewMismatch", "Every selected Test Suite receipt must belong to this exact saved review.");
            if (!inputs.Add(Text(report, "inputSha256")))
                throw new BridgeException("InvalidRequest", "The same original packet cannot be counted twice through different receipt references.");
            var counts = new JsonObject();
            foreach (string outcome in new[] { "passed", "assertionFailed", "executionError", "inconclusive", "blocked", "excluded", "notRun", "outcomeUnknown" })
                counts[outcome] = Rows(report["results"]).Count(r => Text(r, "outcome") == outcome);
            entry["outcomeCounts"] = counts; reports.Add(entry);
            var summary = entry["summary"]!;
            resultCount += summary["resultCount"]!.GetValue<int>(); findingCount += summary["findingCount"]!.GetValue<int>();
            resolvedCount += summary["resolvedFindingCount"]!.GetValue<int>();
            passed &= summary["suppliedRequiredCoveragePassed"]!.GetValue<bool>() && summary["savedReviewScopeBound"]!.GetValue<bool>();
        }
        var result = new JsonObject { ["schemaVersion"] = "project-health-testsuite.v1", ["reviewId"] = reviewId,
            ["reports"] = reports, ["selectionCount"] = reports.Count, ["resultCount"] = resultCount, ["findingCount"] = findingCount,
            ["resolvedFindingCount"] = resolvedCount, ["unresolvedFindingCount"] = findingCount - resolvedCount,
            ["allSelectedSuppliedEvidencePassed"] = passed, ["wholeProjectHealthComplete"] = false,
            ["nativeQualified"] = false, ["sourceAuthenticityVerified"] = false, ["liveFreshnessVerified"] = false,
            ["coveragePolicy"] = "Explicitly selected supplied packets only. Execution units and diagnostic messages are counted per packet, not unique defects or Siemens assertions. Attempts are not merged; native counters are not summed. Empty or passing selections never establish whole-project health." };
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 128 * 1024)
            throw new BridgeException("TestResultLimit", "Test Suite health section exceeds 128 KiB; select fewer receipts.");
        return result;
    }

    private (JsonObject Summary, JsonObject Report) ReadTestSuiteReview(string relativePath, string sha256)
    {
        var jobs = new TestSuiteStyleJobs(artifactsRoot);
        var retained = new EngineeringArtifactReference(relativePath, sha256);
        var receipt = TestSuiteStyleJobs.Parse<TestSuiteReviewReceipt>(jobs.ReadBytes(retained));
        if (receipt.SchemaVersion != "testsuite-result-review.v1" || receipt.Input is null || receipt.Report is null || receipt.Junit is null)
            throw new BridgeException("InvalidTestSuiteArtifact", "Expected a complete supplied-result review receipt.");
        var derived = DeriveTestSuite(receipt.ReviewId, jobs.ReadBytes(receipt.Input), receipt.Input.RelativePath);
        if (!jobs.ReadBytes(receipt.Report).AsSpan().SequenceEqual(derived.Report) || !jobs.ReadBytes(receipt.Junit).AsSpan().SequenceEqual(derived.Junit))
            throw new BridgeException("ArtifactChanged", "Test Suite report or JUnit differs from retained input and saved review.");
        var report = JsonNode.Parse(derived.Report)!.AsObject();
        return (TestSuiteReviewSummary(receipt, retained, report), report);
    }

    private TestSuiteReviewOutput DeriveTestSuite(string reviewId, byte[] original, string inputPath)
    {
        var review = Read(reviewId);
        var packet = TestSuiteStyleJobs.Parse<SuppliedTestSuite>(original);
        if (packet.SchemaVersion != "testsuite-supplied-results.v1")
            throw new BridgeException("InvalidTestSuiteArtifact", "Expected testsuite-supplied-results.v1 with plan, results and scope.");
        var results = TestSuiteResultNormalizer.Normalize(packet.Plan, packet.Results);
        var scope = TestSuiteScopeCoverage.Compare(packet.Plan, packet.Scope);
        var assessment = TestSuiteScopeCoverage.Assess(packet.Plan, results, scope);
        var nodes = NativeNodes(review["model"]!.AsObject());
        var documents = Rows(review["documents"]).ToDictionary(d => Text(d, "objectPath"), d => d!, StringComparer.Ordinal);
        string project = Text(review["model"]!["project"], "path");
        if (project.Length == 0) project = Text(review["model"]!["project"], "projectPath");
        var observedReferences = new Dictionary<string, EngineeringObjectReference>(StringComparer.Ordinal);
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        EngineeringObjectReference? Observed(string? path, EngineeringObjectReference context)
        {
            if (path is null || !nodes.TryGetValue(path, out var node) || Text(node, "origin") == "virtual") return null;
            // Share long captured selectors across findings instead of decoding/copying them per message.
            if (observedReferences.TryGetValue(path, out var retained)) return retained.Snapshot.TiaVersion == context.Snapshot.TiaVersion
                ? retained : retained with { Snapshot = retained.Snapshot with { TiaVersion = context.Snapshot.TiaVersion } };
            documents.TryGetValue(path, out var document);
            var reference = new EngineeringObjectReference(new(project, node["plcPath"]?.GetValue<string>(), node["scopePath"]?.GetValue<string>(),
                path, Text(node, "kind")), new(reviewId, reviewId, context.Snapshot.TiaVersion),
                Text(document, "status") == "exported" ? Text(document, "utf8Sha256") : null);
            EngineeringIdentityGuard.Validate(reference);
            string nodeId = Text(node, "nodeId");
            if (nodeId.Length > 8192) throw new BridgeException("TestResultLimit", "Saved-review node ID exceeds the finding bound.");
            observedReferences.Add(path, reference); nodeIds.Add(path, nodeId);
            return reference;
        }
        string Binding(EngineeringObjectReference expected, EngineeringObjectReference? observed, bool exactContent)
        {
            if (observed is null) return "objectAbsentFromReview";
            if (expected.Object.ProjectPath != observed.Object.ProjectPath || expected.Object.PlcPath != observed.Object.PlcPath || expected.Object.ScopePath != observed.Object.ScopePath)
                return "differentProjectPlcOrScope";
            if (expected.Snapshot != observed.Snapshot) return "differentSourceObservation";
            if (exactContent && (expected.Object != observed.Object || expected.ContentSha256 != observed.ContentSha256)) return "differentObjectOrContent";
            return "exactCapturedSource";
        }
        var bindings = packet.Plan.Definitions.SelectMany(d => d.RequiredScope.Select(r => new {
            definitionId = d.Id, reference = r, binding = Binding(r, Observed(r.Object.ObjectPath, r), true) })).ToArray();
        var findings = packet.Results.SelectMany(c => TestSuiteResultNormalizer.Findings(c, []).Select(f =>
        {
            var observed = Observed(f.NativePath, c.DefinitionReference);
            string binding = Binding(c.DefinitionReference, observed, false);
            string? nodeId = observed is not null && binding != "differentProjectPlcOrScope" ? nodeIds[observed.Object.ObjectPath] : null;
            return new TestSuiteReviewRow(f with { ResolvedObject = binding == "exactCapturedSource" ? observed : null }, binding, nodeId);
        })).ToArray();
        bool sourceBound = packet.Plan.Definitions.All(d => d.Reference.Object.ProjectPath == project &&
            d.Reference.Snapshot == new EngineeringSnapshotIdentity(reviewId, reviewId, d.Reference.Snapshot.TiaVersion));
        bool savedReviewScopeBound = sourceBound && bindings.All(b => b.binding == "exactCapturedSource");
        var capturedById = packet.Results.Select((capture, index) => (capture, index)).ToDictionary(x => x.capture.DefinitionId, StringComparer.Ordinal);
        var report = new {
            schemaVersion = "testsuite-result-report.v1", reviewId, inputSha256 = Hash(original), planSha256 = packet.Plan.PlanSha256,
            summary = new { suppliedRequiredCoveragePassed = assessment.RequiredCoveragePassed,
                savedReviewScopeBound,
                resultCount = results.Results.Length, findingCount = findings.Length,
                resolvedFindingCount = findings.Count(f => f.Finding.ResolvedObject is not null),
                sourceAuthenticityVerified = false, liveFreshnessVerified = false, nativeExecuted = false, nativeQualified = false,
                nativeExecutionAuthorized = false, wholeProjectHealthComplete = false, tiaVersionOrigin = "suppliedUnverified",
                sourceEvidencePolicy = SourceEvidencePolicy },
            results = results.Results.Select(r => {
                bool found = capturedById.TryGetValue(r.DefinitionId, out var captured);
                return new { r.DefinitionId, r.Outcome, r.RawSha256, r.Reasons, captureIndex = found ? (int?)captured.index : null,
                    nativeState = found ? captured.capture.State : null, nativeErrorCount = found ? (int?)captured.capture.ErrorCount : null,
                    nativeWarningCount = found ? (int?)captured.capture.WarningCount : null };
            }),
            scope = scope.Entries, scopeBindings = bindings, findings, assessment
        };
        byte[] reportBytes = TestSuiteReviewJson(report);
        var junit = XDocument.Parse(TestSuiteResultNormalizer.ExportJUnit(packet.Plan, results, inputPath));
        bool covered = assessment.RequiredCoveragePassed && savedReviewScopeBound;
        var suite = junit.Root!;
        suite.SetAttributeValue("name", "Supplied Test Suite results and offline evidence checks");
        suite.SetAttributeValue("tests", results.Results.Length + 1);
        suite.SetAttributeValue("errors", (int)suite.Attribute("errors")! + (covered ? 0 : 1));
        var gate = new XElement("testcase", new XAttribute("name", "offline supplied-evidence coverage"), new XAttribute("classname", "TiaMcp.OfflineEvidence"));
        if (!covered) gate.Add(new XElement("error", new XAttribute("type", "incompleteSuppliedEvidence"), "Required supplied results, scope or exact saved-review binding are incomplete."));
        suite.Add(gate);
        var properties = suite.Element("properties")!;
        properties.Add(new XElement("property", new XAttribute("name", "reviewId"), new XAttribute("value", reviewId)),
            new XElement("property", new XAttribute("name", "inputSha256"), new XAttribute("value", Hash(original))));
        properties.Elements("property").Single(p => (string?)p.Attribute("name") == "requiredCoveragePassed").SetAttributeValue("value", covered.ToString().ToLowerInvariant());
        foreach (string name in new[] { "sourceAuthenticityVerified", "nativeQualified", "nativeExecutionAuthorized", "wholeProjectHealthComplete" })
            properties.Add(new XElement("property", new XAttribute("name", name), new XAttribute("value", "false")));
        return new(reportBytes, Encoding.UTF8.GetBytes(junit.ToString(SaveOptions.DisableFormatting)));
    }

    private static JsonObject TestSuiteReviewSummary(TestSuiteReviewReceipt receipt, EngineeringArtifactReference retained, JsonObject report)
    {
        var result = new JsonObject { ["reviewId"] = receipt.ReviewId, ["receipt"] = JsonSerializer.SerializeToNode(retained, BridgeProtocol.Json),
            ["input"] = JsonSerializer.SerializeToNode(receipt.Input, BridgeProtocol.Json), ["report"] = JsonSerializer.SerializeToNode(receipt.Report, BridgeProtocol.Json),
            ["junit"] = JsonSerializer.SerializeToNode(receipt.Junit, BridgeProtocol.Json), ["summary"] = report["summary"]!.DeepClone() };
        if (JsonSerializer.SerializeToUtf8Bytes(result, BridgeProtocol.Json).Length > 124 * 1024)
            throw new BridgeException("TestResultLimit", "Supplied-result summary exceeds its response budget.");
        return result;
    }

    private static byte[] TestSuiteReviewJson<T>(T value)
    {
        using var stream = new TestSuiteReportStream();
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions(BridgeProtocol.Json) { MaxDepth = 128 });
        return stream.ToArray();
    }
    // Bound output during serialization; repeated finding references must not allocate an unbounded report.
    private sealed class TestSuiteReportStream : MemoryStream
    {
        private void Bound(int count)
        { if (Position + count > MaxBytes) throw new BridgeException("TestResultLimit", "Supplied-result report exceeds 64 MiB."); }
        public override void Write(byte[] buffer, int offset, int count) { Bound(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Bound(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Bound(1); base.WriteByte(value); }
    }
}
