using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

internal static class ReportExportChecks
{
    private static int checks;
    private const string Hostile = "Über conveyor | <img src=https://invalid.example/x onerror=alert(1)> **bold** [link](https://invalid.example) www.invalid.example test@invalid.example\n# heading ``` Ω";
    internal static void Run()
    {
        checks = 0;
        using var fixture = new Fixture();
        string trace = Id(fixture.Prepare(fixture.Plan()));
        string imported = Id(fixture.Import(trace, fixture.Capture(trace)));
        var analysis = fixture.Measurements.Analyze(imported)["report"]!.AsObject();
        var options = JsonNode.Parse("""{"reason":"Explicit sample comparison","beforeAnchorSeconds":0,"afterAnchorSeconds":0,"windowStartSeconds":0,"windowEndSeconds":1}""")!.AsObject();
        var comparison = new TraceComparison(fixture.Root).Compare(imported, imported, options)["comparison"]!.AsObject();
        var store = new DiagnosticStore(fixture.Root); var exporter = new DiagnosticReportExport(fixture.Root);
        foreach (var report in new[] { analysis, comparison })
        {
            bool compare = report["schemaVersion"]!.GetValue<string>() == "trace-comparison.v1";
            report[compare ? "reason" : "symptom"] = Hostile;
            report["additionalEvidence"] = JsonNode.Parse("""{"huge":18446744073709551615,"tiny":1e-28,"unknownField":"retained exactly"}""");
            var source = store.Save(report); string id = Id(source); byte[] raw = File.ReadAllBytes(PathOf(source));
            foreach (string format in new[] { "json", "markdown", "html" })
            {
                var receipt = exporter.Export(id, format); byte[] bytes = File.ReadAllBytes(PathOf(receipt));
                Check(receipt["sourceReportId"]!.GetValue<string>() == id && receipt["sourceArtifact"]!["sha256"]!.GetValue<string>() == id, "Exact source identity");
                Check(Id(receipt) == Convert.ToHexStringLower(SHA256.HashData(bytes)), "Output hash");
                Check(receipt["artifact"]!["bytes"]!.GetValue<int>() == bytes.Length, "Output bytes");
                Check(!receipt["nativeQualification"]!.GetValue<bool>() && !receipt["sourceAuthenticityVerified"]!.GetValue<bool>() && !receipt["analysisRecomputed"]!.GetValue<bool>(), "No new evidence claims");
                Check(Id(new DiagnosticReportExport(fixture.Root).Export(id, format)) == Id(receipt), "Deterministic fresh reader export");
                Check(File.ReadAllBytes(PathOf(source)).SequenceEqual(raw), "Source unchanged");
                if (format == "json") { Check(bytes.SequenceEqual(raw) && Id(receipt) == id, "Exact JSON bytes, no normalization"); continue; }
                string text = Encoding.UTF8.GetString(bytes);
                Check(text.Contains("Result overview") && text.Contains("Source lineage") && text.Contains("Complete source report"), "Readable report sections");
                Check(text.Contains("not verified") && text.Contains("does not prove causality"), "Evidence limits retained");
                if (format == "html")
                {
                    Check(!text.Contains("<img") && !text.Contains("<script") && text.Contains("Content-Security-Policy"), "No executable source markup");
                    string embedded = Regex.Matches(text, @"<pre[^>]*><code>(.*?)</code></pre>", RegexOptions.Singleline).Last().Groups[1].Value;
                    Check(WebUtility.HtmlDecode(embedded) == Encoding.UTF8.GetString(raw), "Complete source JSON round trip");
                    Check(WebUtility.HtmlDecode(text).Contains(Hostile), "Readable Unicode and literal hostile source");
                }
                else
                {
                    string readable = text[..text.IndexOf("## Complete source report", StringComparison.Ordinal)];
                    Check(!readable.Contains("<img") && readable.Contains("\\[link\\]") && readable.Contains("\\*\\*bold\\*\\*"), "Markdown cannot create injected image or emphasis");
                    Check(text.Contains(Encoding.UTF8.GetString(raw)), "Markdown complete raw source");
                }
            }
            foreach (string key in compare ? new[] { "beforeAcquisition", "afterAcquisition", "programIdentityChanged", "clockSynchronizationVerified" } : new[] { "acquisitionContext", "clockSynchronizationVerified" }) report.Remove(key);
            string legacy = Id(store.Save(report));
            Check(File.ReadAllText(PathOf(exporter.Export(legacy, "html"))).Contains("Not retained in this report"), "Historical missing context stays explicit");
        }
        string valid = Id(store.Save(analysis));
        foreach (string format in new[] { "HTML", "../html", "", "pdf" }) Error("InvalidRequest", () => exporter.Export(valid, format));
        Error("InvalidRequest", () => exporter.Export("../outside", "html"));
        Error("InvalidDiagnosticReport", () => exporter.Export(Id(store.Save(new JsonObject { ["schemaVersion"] = "diagnostic-case.v1" })), "json"));
        foreach (string defect in new[] { "nativeQualification", "causationProven", "identity", "rows", "context" })
        {
            var bad = analysis.DeepClone().AsObject();
            if (defect is "nativeQualification" or "causationProven") bad[defect] = true;
            else if (defect == "identity") bad["measurementId"] = "not-a-hash";
            else if (defect == "rows") bad["hypotheses"] = new JsonArray(1);
            else bad["clockSynchronizationVerified"] = false;
            Error("InvalidDiagnosticReport", () => exporter.Export(Id(store.Save(bad)), "html"));
        }
        var retained = store.Save(analysis); string path = PathOf(retained); byte[] original = File.ReadAllBytes(path);
        File.AppendAllText(path, " "); Error("ArtifactChanged", () => exporter.Export(Id(retained), "html")); File.WriteAllBytes(path, original);
        var rendered = exporter.Export(Id(retained), "html"); string renderedPath = PathOf(rendered); byte[] renderedBytes = File.ReadAllBytes(renderedPath);
        File.AppendAllText(renderedPath, " "); Error("ArtifactChanged", () => exporter.Export(Id(retained), "html")); File.WriteAllBytes(renderedPath, renderedBytes);
        string missing = new('0', 64); Error("ArtifactUnavailable", () => exporter.Export(missing, "html"));
        var large = analysis.DeepClone().AsObject(); large["symptom"] = new string('x', 5 * 1024 * 1024);
        string largeId = Id(store.Save(large)); int before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Length;
        Error("ArtifactCapacity", () => exporter.Export(largeId, "html")); Error("ArtifactCapacity", () => exporter.Export(largeId, "markdown"));
        Check(Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Length == before, "Oversized rendering publishes no partial output");
        Check(Id(exporter.Export(largeId, "json")) == largeId, "Exact JSON remains usable when rendering is too large");
        Console.WriteLine($"PASS Diagnostic report export: {checks} checks; synthetic reports only.");
    }
    private static string Id(JsonObject receipt) => receipt["id"]!.GetValue<string>();
    private static string PathOf(JsonObject receipt) => receipt["artifact"]!["path"]!.GetValue<string>();
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static void Error(string code, Action action)
    {
        try { action(); } catch (BridgeException error) when (error.Code == code) { checks++; return; }
        throw new Exception("Expected " + code);
    }
}
