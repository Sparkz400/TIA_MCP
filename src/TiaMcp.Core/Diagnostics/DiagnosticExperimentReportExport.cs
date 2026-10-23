using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Revalidates original experiment provenance and exports one complete, self-contained report.</summary>
public sealed class DiagnosticExperimentReportExport(string artifactsRoot)
{
    public JsonObject Export(string reportId, string format)
    {
        var (extension, mediaType) = format switch { "json" => (".json", "application/json"),
            "markdown" => (".md", "text/markdown"), "html" => (".html", "text/html"),
            _ => throw new BridgeException("InvalidRequest", "format must be json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot);
        byte[] original = store.ReadBytes(reportId);
        var source = JsonNode.Parse(original)?.AsObject() ?? throw new BridgeException("InvalidRequest", "Expected an experiment report.");
        DiagnosticExperimentReviewParts[] parts;
        string schema = D.Text(source, "schemaVersion");
        if (schema is "diagnostic-experiment-review.v2" or "diagnostic-experiment-repeat.v2")
            return new DiagnosticQualityReportExport(artifactsRoot).Export(reportId, format);
        if (schema == "diagnostic-experiment-review.v1") parts = [new DiagnosticExperiments(artifactsRoot).ReadReview(reportId)];
        else if (schema == "diagnostic-experiment-repeat.v1") parts = new DiagnosticRepeatedExperiments(artifactsRoot).Read(reportId).Pairs;
        else throw new BridgeException("InvalidRequest", "Only retained single-pair or repeated experiment reports are supported.");
        var experiments = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void AddExperiment(string id, JsonObject experiment)
        { if (seen.Add(id)) experiments.Add(new JsonObject { ["id"] = id, ["document"] = experiment.DeepClone() }); }
        var runs = new JsonArray();
        for (int i = 0; i < parts.Length; i++)
        {
            var pair = parts[i];
            AddExperiment(D.Required(pair.Report, "beforeExperimentId", 64), pair.BeforeExperiment);
            AddExperiment(D.Required(pair.Report, "afterExperimentId", 64), pair.AfterExperiment);
            runs.Add(new JsonObject { ["id"] = schema == "diagnostic-experiment-repeat.v1" ? source["runs"]![i]!["id"]!.DeepClone() : JsonValue.Create("pair"),
                ["reviewId"] = schema == "diagnostic-experiment-repeat.v1" ? source["runs"]![i]!["reviewId"]!.DeepClone() : JsonValue.Create(reportId),
                ["review"] = pair.Report.DeepClone(), ["beforeReport"] = pair.BeforeReport.DeepClone(), ["afterReport"] = pair.AfterReport.DeepClone(),
                ["comparison"] = pair.Comparison.DeepClone(),
                ["beforeInputArtifact"] = pair.BeforeImport["inputArtifact"]!.DeepClone(), ["afterInputArtifact"] = pair.AfterImport["inputArtifact"]!.DeepClone(),
                ["beforeWindow"] = DiagnosticExperimentWindows.Assess(pair.BeforeExperiment, pair.BeforeImport, pair.Comparison, "before"),
                ["afterWindow"] = DiagnosticExperimentWindows.Assess(pair.AfterExperiment, pair.AfterImport, pair.Comparison, "after") });
        }
        var combined = new JsonObject { ["schemaVersion"] = "diagnostic-experiment-report.v1", ["sourceReportId"] = reportId,
            ["sourceSchemaVersion"] = schema, ["sourceReport"] = source.DeepClone(), ["experiments"] = experiments, ["runs"] = runs,
            ["sourceEvidenceRevalidated"] = true, ["windowAnalysisRecomputed"] = true, ["sourceAuthenticityVerified"] = false,
            ["independentRunsVerified"] = false, ["clockSynchronizationVerified"] = false, ["causationProven"] = false,
            ["bugFixProven"] = false, ["nativeQualification"] = false, ["nativeAccessAttempted"] = false,
            ["limits"] = DiagnosticRepeatedExperiments.Limits };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(combined, BridgeProtocol.Json);
        if (json.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Combined report exceeds 8 MiB; select fewer repeated pairs.");
        byte[] bytes = format == "json" ? json : Encoding.UTF8.GetBytes(Render(combined, Encoding.UTF8.GetString(json), format == "html"));
        if (bytes.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable report exceeds 8 MiB; select fewer repeated pairs.");
        var sourceReceipt = store.Publish(original, ".json");
        var receipt = store.Publish(bytes, extension);
        receipt["schemaVersion"] = "diagnostic-experiment-report-export.v1";
        receipt["sourceReportId"] = reportId; receipt["sourceSchemaVersion"] = schema;
        receipt["sourceArtifact"] = sourceReceipt["artifact"]!.DeepClone();
        receipt["format"] = format; receipt["mediaType"] = mediaType;
        receipt["sourceEvidenceRevalidated"] = true; receipt["windowAnalysisRecomputed"] = true;
        foreach (string field in new[] { "sourceAuthenticityVerified", "independentRunsVerified", "clockSynchronizationVerified",
            "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted" }) receipt[field] = false;
        receipt["limits"] = DiagnosticRepeatedExperiments.Limits;
        return receipt;
    }

    private static string Render(JsonObject document, string raw, bool html)
    {
        var output = new StringBuilder();
        string Escape(string value) => html ? WebUtility.HtmlEncode(value) : Markdown(value);
        string Value(JsonNode? value) => value is null ? "not supplied" : value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value.ToJsonString();
        void Line(string value) { output.AppendLine(value); if (output.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable report exceeds its bounded output."); }
        void Heading(int level, string title) => Line(html ? $"<h{level}>{Escape(title)}</h{level}>" : new string('#', level) + " " + Escape(title) + "\n");
        void Paragraph(string text) => Line(html ? "<p>" + Escape(text) + "</p>" : Escape(text) + "\n");
        void Table(string[] headers, IEnumerable<string[]> rows)
        {
            if (html)
            {
                Line("<div class=\"table-scroll\" tabindex=\"0\"><table class=\"columns-" + headers.Length + "\"><thead><tr>" + string.Join("", headers.Select(h => "<th scope=\"col\">" + Escape(h) + "</th>")) + "</tr></thead><tbody>");
                foreach (var row in rows) Line("<tr>" + string.Join("", row.Select(v => "<td>" + Escape(v) + "</td>")) + "</tr>");
                Line("</tbody></table></div>");
            }
            else
            {
                Line("| " + string.Join(" | ", headers.Select(Escape)) + " |");
                Line("| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
                foreach (var row in rows) Line("| " + string.Join(" | ", row.Select(Escape)) + " |");
                Line("");
            }
        }
        void Code(string value)
        {
            if (html) Line("<pre>" + WebUtility.HtmlEncode(value) + "</pre>");
            else
            {
                int longest = 0, current = 0;
                foreach (char c in value) { current = c == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
                string fence = new('`', Math.Max(3, longest + 1));
                Line(fence + "json\n" + value + "\n" + fence + "\n");
            }
        }
        if (html) Line("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Diagnostic experiment report</title><style>" + Style + "</style></head><body><main>");
        Heading(1, "Diagnostic experiment report");
        Paragraph("Source report: " + D.Text(document, "sourceReportId"));
        if (html) Line("<aside>");
        Paragraph(DiagnosticRepeatedExperiments.Limits);
        if (html) Line("</aside>");
        Paragraph("Original sources and retained derived reports were revalidated. Requested-window results were recomputed. These checks do not authenticate the source or qualify native behavior.");
        if (document["sourceReport"]?["hypotheses"] is JsonArray aggregates)
        {
            Heading(2, "Repeated-run observations");
            Table(["Hypothesis", "Phase", "Runs", "Supported", "Contradicted", "Unresolved", "Condition observed"],
                aggregates.SelectMany(h => new[] { "before", "after" }.Select(side => new[] {
                    Value(h!["id"]), side, Value(h[side]?["runs"]), Value(h[side]?["supportedPredictionRuns"]),
                    Value(h[side]?["contradictedPredictionRuns"]), Value(h[side]?["unresolvedRuns"]), Value(h[side]?["conditionObservedRuns"]) })));
            Paragraph("Counts describe selected runs. Samples are not pooled, runs are not weighted, and no statistical or successful-fix conclusion is calculated.");
        }
        Heading(2, "Saved experiments and evidence");
        foreach (var item in D.Rows(document["experiments"]))
        {
            var experiment = item!["document"]!; var diagnostic = experiment["diagnosticCase"]!;
            Heading(3, Value(item["id"])); Paragraph(Value(diagnostic["report"]));
            Table(["Identity", "Retained value"], new[] {
                new[] { "Case / review", Value(experiment["diagnosticPlanId"]) + " / " + Value(experiment["reviewId"]) },
                new[] { "Selected FB", Value(diagnostic["blockPath"]) },
                new[] { "Instance DB / member path", Value(diagnostic["selectedInstance"]?["dbPath"]) + " / " + Value(diagnostic["selectedInstance"]?["memberPath"]) },
                new[] { "Watch / trace plan", Value(experiment["watchPlanId"]) + " / " + Value(experiment["tracePlanId"]) },
                new[] { "Composition gaps", Value(experiment["compositionGaps"]) },
                new[] { "Saved source coverage gaps", Value(diagnostic["coverageGaps"]) },
                new[] { "Selected alarm", Value(diagnostic["alarmEvidence"]) },
                new[] { "Executable alarm bindings", Value(diagnostic["alarmBindings"]) },
                new[] { "Selected Test Suite finding", Value(diagnostic["testSuiteEvidence"]) } });
            Table(["Signal", "Role and reason", "Exact storage / member", "Datatype"],
                D.Rows(diagnostic["signals"]).Select(s => new[] { Value(s!["id"]), Value(s["role"]) + ": " + Value(s["reason"]),
                    Value(s["storagePath"]) + " / " + Value(s["memberPath"]), Value(s["datatype"]) }));
        }
        foreach (var run in D.Rows(document["runs"]))
        {
            Heading(2, "Run pair: " + Value(run!["id"]));
            Paragraph("Retained review: " + Value(run["reviewId"]));
            Table(["Provenance", "Retained value"], new[] {
                new[] { "Before / after measurement", Value(run["review"]?["beforeMeasurementId"]) + " / " + Value(run["review"]?["afterMeasurementId"]) },
                new[] { "Explicit lineage", Value(run["review"]?["lineage"]) },
                new[] { "Comparison alignment and mappings", Value(run["comparison"]?["alignment"]) },
                new[] { "Source / program changed", Value(run["review"]?["sourceChanged"]) + " / " + Value(run["review"]?["programIdentityChanged"]) } });
            foreach (string side in new[] { "before", "after" })
            {
                var window = run[side + "Window"]!;
                Heading(3, side + " requested window");
                Table(["Observation", "Value"], new[] {
                    ("Recorded span", "coverage"), ("Samples inside window", "samplesInWindow"),
                    ("Requested start (s)", "requestedStartSeconds"), ("Requested end (s)", "requestedEndSeconds"),
                    ("First recorded sample (s)", "firstRecordedSeconds"), ("Last recorded sample (s)", "lastRecordedSeconds"),
                    ("Quality gate passed", "qualityGatePassed"), ("Quality limits", "qualityGateReasons"),
                    ("Quality gate scope", "qualityGateScope"), ("Acquisition context", "acquisitionContext"),
                    ("Caller-attested quality", "acquisitionQuality"), ("Structural quality assessment", "qualityAssessment")
                }.Select(field => new[] { field.Item1, Value(window[field.Item2]) }));
                Table(["Hypothesis", "Window verdict", "Matched", "Contradicted", "Unknown", "Reason"],
                    D.Rows(window["hypotheses"]).Select(h => new[] { Value(h!["id"]), Value(h["verdict"]),
                        Value(h["matchedSamples"]), Value(h["contradictedSamples"]), Value(h["unknownSamples"]), Value(h["reason"]) }));
                Paragraph("The original whole-capture analysis is retained separately in the complete combined JSON. Only the window verdicts above feed the repeated-run counts.");
            }
            Heading(3, "Raw signal observations in the requested window");
            Table(["Before → after signal", "Unit", "Before samples / missing", "After samples / missing", "Before / after transitions"],
                D.Rows(run["comparison"]?["signals"]).Select(s => new[] {
                    Value(s!["beforeSignalId"]) + " → " + Value(s["afterSignalId"]), Value(s["unit"]),
                    Value(s["before"]?["samplesInWindow"]) + " / " + Value(s["before"]?["missingValues"]),
                    Value(s["after"]?["samplesInWindow"]) + " / " + Value(s["after"]?["missingValues"]),
                    Value(s["before"]?["transitionCount"]) + " / " + Value(s["after"]?["transitionCount"]) }));
        }
        Heading(2, "Complete combined JSON");
        Paragraph("Includes the original retained report document, exact case/evidence snapshots, each full sample analysis and comparison, and original artifact references. It contains no raw-input rewrite.");
        Code(raw);
        if (html) Line("</main></body></html>");
        return output.ToString();
    }

    private static string Markdown(string text)
    {
        var result = new StringBuilder();
        foreach (char c in text)
            result.Append(c switch {
                ':' => "&#58;", '.' => "&#46;", '@' => "&#64;",
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '\n' => "<br>", '\r' => "", '\t' => "    ",
                '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '!' or '|' or '~' => "\\" + c,
                _ => char.IsControl(c) ? "\\u" + ((int)c).ToString("x4") : c.ToString() });
        return result.ToString();
    }

    private const string Style = """
        :root{color-scheme:light;font:16px/1.5 'Segoe UI',system-ui,sans-serif;color:#18332d;background:#f2f5f4}*{box-sizing:border-box}body{margin:0}main{max-width:1120px;margin:32px auto;padding:32px;background:#fff;border:1px solid #d5e0db;border-radius:10px}h1{font-size:2rem}h2{border-top:1px solid #d5e0db;padding-top:20px;margin-top:32px}h3{font-size:1.1rem;margin-top:24px}h1,h2,h3,p,td,th{overflow-wrap:anywhere}p,td{white-space:pre-wrap}aside{background:#fff5df;border-left:4px solid #9b6500;padding:12px 18px}.table-scroll{overflow:auto;max-width:100%;margin:16px 0}table{border-collapse:collapse;width:100%;font-size:.9rem}td,th{text-align:left;vertical-align:top;border:1px solid #d5e0db;padding:9px}th{background:#edf3ef;min-width:110px}.columns-2 th:first-child{width:220px;min-width:180px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f4f7f5;padding:16px;border:1px solid #d5e0db;font:12px/1.5 ui-monospace,monospace}@media(max-width:640px){main{margin:0;padding:16px;border:0}table{min-width:480px}}@media print{main{max-width:none;margin:0;border:0;padding:0}h2,h3{break-after:avoid}tr,aside{break-inside:avoid}.table-scroll{overflow:visible}}
        """;
}
