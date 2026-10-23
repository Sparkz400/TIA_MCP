using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Xml.Linq;
using TiaMcp.Core.Engineering;

namespace TiaMcp.Core.TestSuite;

public sealed record NativeTestMessage(string State, string Description, string? Path, string? NativeTimestamp,
    string NativeTimestampKind, int ErrorCount, int WarningCount, NativeTestMessage[] Messages);
public sealed record NativeTestResult(string DefinitionId, EngineeringObjectReference DefinitionReference,
    string State, int ErrorCount, int WarningCount, NativeTestMessage[] Messages,
    bool Dispatched, bool CollectionComplete, bool ScopeVerified, string? FailureKind = null);
public sealed record NormalizedTestResult(string DefinitionId, string Outcome, string? RawJson, string? RawSha256, string[] Reasons);
public sealed record TestSuiteResultReport(string PlanSha256, NormalizedTestResult[] Results, string[] RequiredDefinitionIds,
    bool RequiredCoveragePassed, bool NativeExecutedByNormalizer = false, bool ReleaseAccepted = false);
public sealed record TestSuiteFinding(string DefinitionId, string State, string Description, string? NativePath, EngineeringObjectReference? ResolvedObject)
{
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
}

public static class TestSuiteResultNormalizer
{
    private static readonly JsonSerializerOptions ResultJson = new(BridgeProtocol.Json) { MaxDepth = 128 };
    private static JsonNode ParseRaw(string raw) => JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 128 })!;
    public static TestSuiteResultReport Normalize(TestSuiteRunPlan plan, NativeTestResult[] captures)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (captures is null || captures.Length > 1000 || captures.Any(c => c is null || string.IsNullOrWhiteSpace(c.DefinitionId)) || captures.Select(c => c.DefinitionId).Distinct(StringComparer.Ordinal).Count() != captures.Length)
            throw new BridgeException("InvalidTestResults", "Duplicate results or result-count bound exceeded.");
        var definitions = plan.Definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var captureById = captures.ToDictionary(c => c.DefinitionId, StringComparer.Ordinal);
        var excluded = plan.ExcludedDefinitionIds.ToHashSet(StringComparer.Ordinal);
        if (captures.Any(c => !definitions.ContainsKey(c.DefinitionId)))
            throw new BridgeException("UnexpectedTestResult", "Result belongs to an unrequested definition.");
        var results = new List<NormalizedTestResult>();
        long totalBytes = 0; int totalMessages = 0;
        foreach (var id in plan.RequiredDefinitionIds)
        {
            var captured = captureById.GetValueOrDefault(id);
            string? raw = null; string? hash = null;
            if (captured is not null)
            {
                EngineeringIdentityGuard.EnsureFresh(definitions[id].Reference, captured.DefinitionReference);
                raw = Raw(captured);
                totalBytes += Encoding.UTF8.GetByteCount(raw);
                totalMessages += Flatten(captured.Messages).Count();
                if (totalBytes > 64 * 1024 * 1024 || totalMessages > 100000)
                    throw new BridgeException("TestResultLimit", "The complete report exceeds 64 MiB or 100,000 messages.");
                hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            }
            if (excluded.Contains(id))
            { results.Add(new(id, "excluded", raw, hash, ["Required dependency was excluded; coverage gate fails."])); continue; }
            if (captured is null) { results.Add(new(id, "notRun", null, null, ["No captured result for required definition."])); continue; }
            var reasons = new List<string>();
            string? fixtureOutcome = TestSuiteCasingResultProfile.Interpret(definitions[id], captured);
            string outcome;
            if (!captured.Dispatched) { outcome = "blocked"; reasons.Add("Not dispatched."); }
            else if (!captured.CollectionComplete || !captured.ScopeVerified) { outcome = "inconclusive"; reasons.Add("Result collection or effective required scope is incomplete."); }
            else if (fixtureOutcome is not null) { outcome = fixtureOutcome; reasons.Add("Exact owned casing-fixture result tree matched; original informational completion message retained."); }
            else if (captured.State == "Error" || captured.ErrorCount > 0 || Flatten(captured.Messages).Any(m => m.State == "Error" || m.ErrorCount > 0))
            { outcome = captured.FailureKind == "assertion" ? "assertionFailed" : "executionError"; reasons.Add("Native errors retained; assertion classification requires an explicit observed profile."); }
            else if (captured.State != "Success" || captured.WarningCount > 0 || Flatten(captured.Messages).Any(m => m.State != "Success" || m.WarningCount > 0))
            { outcome = "inconclusive"; reasons.Add("Warning/information/unknown states are not automatically passes."); }
            else { outcome = "passed"; }
            results.Add(new(id, outcome, raw, hash, reasons.ToArray()));
        }
        bool passed = plan.Blockers.Length == 0 && results.Count == plan.RequiredDefinitionIds.Length && results.All(r => r.Outcome == "passed");
        return new(plan.PlanSha256, results.ToArray(), plan.RequiredDefinitionIds.ToArray(), passed);
    }

    private static string Raw(NativeTestResult result)
    {
        if (result is null || !TestSuiteRunPlanner.Text(result.DefinitionId, 256))
            throw new BridgeException("InvalidTestResults", "An exact bounded definition identity is required.");
        EngineeringIdentityGuard.Validate(result.DefinitionReference);
        ValidateState(result.State, result.ErrorCount, result.WarningCount);
        if (result.FailureKind is not (null or "assertion" or "native" or "unknown")) throw new BridgeException("InvalidTestResults", "Unknown failure classification.");
        _ = Flatten(result.Messages).Count();
        string raw = CanonicalJson.Serialize(JsonSerializer.SerializeToNode(result, ResultJson)!);
        if (Encoding.UTF8.GetByteCount(raw) > 64 * 1024 * 1024) throw new BridgeException("TestResultLimit", "Result artifact exceeds 64 MiB.");
        return raw;
    }
    private static void ValidateState(string state, int errors, int warnings)
    {
        if (state is not ("Success" or "Information" or "Warning" or "Error" or "Unknown") || errors < 0 || warnings < 0)
            throw new BridgeException("InvalidTestResults", "Invalid native result state/count.");
    }
    public static IEnumerable<NativeTestMessage> Flatten(NativeTestMessage[] messages)
    {
        if (messages is null) throw new BridgeException("InvalidTestResults", "Message array must be explicit.");
        var active = new HashSet<NativeTestMessage>(ReferenceEqualityComparer.Instance); int count = 0; long textBytes = 0;
        IEnumerable<NativeTestMessage> Visit(NativeTestMessage[] rows, int depth)
        {
            if (rows is null || depth > 32) throw new BridgeException("TestResultLimit", "Result tree is null or exceeds 32 nested message levels.");
            foreach (var row in rows)
            {
                if (row is null || !active.Add(row) || ++count > 100000) throw new BridgeException("TestResultLimit", "Result tree is cyclic or too large.");
                ValidateState(row.State, row.ErrorCount, row.WarningCount);
                if (row.Description is null || row.Description.Length > 8192 || row.Path?.Length > 8192 || row.NativeTimestamp?.Length > 100 || row.NativeTimestampKind is not ("Unknown" or "Utc" or "Local" or "Unspecified"))
                    throw new BridgeException("TestResultLimit", "Message text/timestamp bounds or timestamp provenance invalid.");
                // JSON encoders replace unpaired surrogates; refuse instead of silently losing native text.
                ValidateUnicode(row.Description); ValidateUnicode(row.Path); ValidateUnicode(row.NativeTimestamp);
                textBytes += Encoding.UTF8.GetByteCount(row.Description) + Encoding.UTF8.GetByteCount(row.Path ?? "");
                if (textBytes > 32 * 1024 * 1024) throw new BridgeException("TestResultLimit", "Message text budget exceeded.");
                yield return row;
                foreach (var nested in Visit(row.Messages, depth + 1)) yield return nested;
                active.Remove(row);
            }
        }
        return Visit(messages, 0);
    }

    private static void ValidateUnicode(string? value)
    {
        if (value is null) return;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i]))
                    throw new BridgeException("InvalidTestResults", "Native text contains invalid Unicode and cannot be retained losslessly as JSON.");
            }
            else if (char.IsLowSurrogate(value[i]))
                throw new BridgeException("InvalidTestResults", "Native text contains invalid Unicode and cannot be retained losslessly as JSON.");
        }
    }

    public static string ExportJUnit(TestSuiteRunPlan plan, TestSuiteResultReport report, string rawArtifactReference)
    {
        ValidateReport(plan, report);
        if (report.NativeExecutedByNormalizer || report.ReleaseAccepted || string.IsNullOrWhiteSpace(rawArtifactReference) || rawArtifactReference.Length > 8192 || rawArtifactReference.Any(char.IsControl))
            throw new BridgeException("InvalidTestResults", "A raw evidence reference and honest offline provenance are required.");
        try { System.Xml.XmlConvert.VerifyXmlChars(rawArtifactReference); }
        catch (System.Xml.XmlException) { throw new BridgeException("InvalidTestResults", "Raw artifact reference has invalid Unicode/XML characters."); }
        var suite = new XElement("testsuite", new XAttribute("name", "Siemens Test Suite captured execution units"), new XAttribute("tests", report.Results.Length),
            new XAttribute("failures", report.Results.Count(r => r.Outcome == "assertionFailed")),
            new XAttribute("errors", report.Results.Count(r => r.Outcome is "executionError" or "inconclusive" or "outcomeUnknown")),
            new XAttribute("skipped", report.Results.Count(r => r.Outcome is "notRun" or "blocked" or "excluded")),
            new XElement("properties", new XElement("property", new XAttribute("name", "requiredCoveragePassed"), new XAttribute("value", report.RequiredCoveragePassed.ToString().ToLowerInvariant())),
                new XElement("property", new XAttribute("name", "planSha256"), new XAttribute("value", report.PlanSha256)),
                new XElement("property", new XAttribute("name", "rawArtifact"), new XAttribute("value", rawArtifactReference))));
        foreach (var row in report.Results)
        {
            var test = new XElement("testcase", new XAttribute("name", row.DefinitionId));
            string? element = row.Outcome switch { "passed" => null, "assertionFailed" => "failure", "notRun" or "blocked" or "excluded" => "skipped", _ => "error" };
            if (element is not null) test.Add(new XElement(element, new XAttribute("type", row.Outcome), string.Join("\n", row.Reasons)));
            test.Add(new XElement("system-out", "rawSha256=" + row.RawSha256)); suite.Add(test);
        }
        return new XDocument(suite).ToString(SaveOptions.DisableFormatting);
    }

    public static void ValidateReport(TestSuiteRunPlan plan, TestSuiteResultReport report)
    {
        TestSuiteRunPlanner.Validate(plan);
        if (report is null || report.Results is null || report.Results.Length is < 1 or > 1000 || report.Results.Any(r => r is null) || report.RequiredDefinitionIds is null ||
            report.RequiredDefinitionIds.Length != report.Results.Length || report.Results.Select(r => r.DefinitionId).Distinct(StringComparer.Ordinal).Count() != report.Results.Length ||
            !report.RequiredDefinitionIds.SequenceEqual(report.Results.Select(r => r.DefinitionId)) || report.NativeExecutedByNormalizer || report.ReleaseAccepted)
            throw new BridgeException("InvalidTestResults", "Report must retain exact required execution-unit coverage and offline provenance.");
        EngineeringIdentityGuard.Sha256(report.PlanSha256, "planSha256");
        long totalBytes = 0;
        foreach (var row in report.Results)
        {
            if (row.Outcome is not ("passed" or "assertionFailed" or "executionError" or "inconclusive" or "blocked" or "excluded" or "notRun" or "outcomeUnknown") || row.Reasons is null ||
                row.Reasons.Length > 1000 || row.Reasons.Any(r => r is null || r.Length > 4000))
                throw new BridgeException("InvalidTestResults", "Unknown normalized result state.");
            if (row.RawJson is null)
            {
                if (row.RawSha256 is not null || row.Outcome is "passed" or "assertionFailed" or "executionError")
                    throw new BridgeException("InvalidTestResults", "Observed execution outcomes require raw evidence.");
            }
            else
            {
                totalBytes += Encoding.UTF8.GetByteCount(row.RawJson);
                if (totalBytes > 64 * 1024 * 1024) throw new BridgeException("TestResultLimit", "The complete report exceeds 64 MiB.");
                try
                {
                    if (CanonicalJson.Sha256(ParseRaw(row.RawJson)) != row.RawSha256)
                        throw new BridgeException("StaleTestResult", "Raw result identity changed.");
                }
                catch (JsonException e) { throw new BridgeException("InvalidTestResults", "Malformed raw result JSON.", e); }
            }
        }
        if (report.RequiredCoveragePassed && report.Results.Any(r => r.Outcome != "passed"))
            throw new BridgeException("InvalidTestResults", "Incomplete, skipped or failed required coverage cannot pass.");
        // Reconstruct labels and gate from exact plan + raw captures. A valid raw
        // hash alone must not allow Error -> passed or removal of plan blockers.
        NativeTestResult[] captures;
        try
        {
            captures = report.Results.Where(r => r.RawJson is not null).Select(r =>
                JsonSerializer.Deserialize<NativeTestResult>(r.RawJson!, ResultJson) ?? throw new BridgeException("InvalidTestResults", "Missing raw capture.")).ToArray();
        }
        catch (JsonException e) { throw new BridgeException("InvalidTestResults", "Malformed typed raw result.", e); }
        var derived = Normalize(plan, captures);
        if (derived.PlanSha256 != report.PlanSha256 || derived.RequiredCoveragePassed != report.RequiredCoveragePassed ||
            derived.Results.Length != report.Results.Length ||
            !derived.RequiredDefinitionIds.SequenceEqual(report.RequiredDefinitionIds) ||
            derived.Results.Where((r, i) => r.DefinitionId != report.Results[i].DefinitionId || r.Outcome != report.Results[i].Outcome ||
                r.RawJson != report.Results[i].RawJson || r.RawSha256 != report.Results[i].RawSha256 ||
                !r.Reasons.SequenceEqual(report.Results[i].Reasons)).Any())
            throw new BridgeException("InvalidTestResults", "Normalized outcomes or required gate differ from raw evidence and exact plan.");
    }

    public static TestSuiteFinding[] Findings(NativeTestResult capture, EngineeringObjectReference[] observedObjects)
    {
        _ = Raw(capture);
        if (observedObjects is null || observedObjects.Length > 10000) throw new BridgeException("TestResultLimit", "Finding object inventory exceeds 10,000 objects.");
        foreach (var reference in observedObjects) EngineeringIdentityGuard.EnsureSameSnapshot(capture.DefinitionReference, reference);
        var index = observedObjects.Where(o => o.Object.PlcPath == capture.DefinitionReference.Object.PlcPath && o.Object.ScopePath == capture.DefinitionReference.Object.ScopePath)
            .GroupBy(o => o.Object.ObjectPath, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return Flatten(capture.Messages).Where(m => m.State != "Success" || m.ErrorCount > 0 || m.WarningCount > 0).Select(m =>
        {
            var matches = m.Path is not null ? index.GetValueOrDefault(m.Path) ?? [] : [];
            return new TestSuiteFinding(capture.DefinitionId, m.State, m.Description, m.Path, matches.Length == 1 ? matches[0] : null)
            { ErrorCount = m.ErrorCount, WarningCount = m.WarningCount };
        }).ToArray();
    }
}
