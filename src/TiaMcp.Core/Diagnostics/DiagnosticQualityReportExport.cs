using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TiaMcp.Core.Diagnostics;

internal sealed class DiagnosticQualityReportExport(string artifactsRoot)
{
    internal JsonObject Export(string reportId, string format)
    {
        string extension = format switch { "json" => ".json", "markdown" => ".md", "html" => ".html",
            _ => throw new BridgeException("InvalidRequest", "Use json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot); byte[] original = store.ReadBytes(reportId);
        var source = JsonNode.Parse(original)!.AsObject(); string schema = TraceQualityMeasurements.Schema(source);
        var reader = new TraceQualityMeasurements(artifactsRoot); var experiments = new DiagnosticQualityExperiments(artifactsRoot);
        var measurements = new JsonArray(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var runs = new JsonArray(); var contexts = new JsonArray(); var seenExperiments = new HashSet<string>(StringComparer.Ordinal);
        void Measurement(string id)
        {
            if (!seen.Add(id)) return;
            var imported = reader.Read(id); var artifact = TraceQualityMeasurements.Object(imported, "inputArtifact");
            byte[] raw = ProjectReviewStore.ReadArtifact(artifactsRoot, artifact, DiagnosticStore.MaxBytes);
            measurements.Add(new JsonObject { ["id"] = id, ["document"] = imported, ["original"] = new JsonObject {
                ["artifact"] = artifact.DeepClone(), ["sha256"] = artifact["sha256"]!.DeepClone(), ["bytes"] = raw.Length,
                ["base64"] = Convert.ToBase64String(raw) } });
        }
        void Pair(DiagnosticExperimentReviewParts p, string id)
        {
            Measurement(D.Required(p.Report, "beforeMeasurementId", 64)); Measurement(D.Required(p.Report, "afterMeasurementId", 64));
            foreach (var (key, document) in new[] { ("beforeExperimentId", p.BeforeExperiment), ("afterExperimentId", p.AfterExperiment) })
            {
                string experimentId = D.Required(p.Report, key, 64);
                if (seenExperiments.Add(experimentId)) contexts.Add(new JsonObject { ["id"] = experimentId, ["document"] = document.DeepClone() });
            }
            runs.Add(new JsonObject { ["id"] = id, ["review"] = p.Report.DeepClone(), ["beforeReport"] = p.BeforeReport.DeepClone(),
                ["afterReport"] = p.AfterReport.DeepClone(), ["comparison"] = p.Comparison.DeepClone() });
        }
        switch (schema)
        {
            case "diagnostic-report.v2":
                TraceQualityMeasurements.Same(source, reader.BuildAnalysis(D.Required(source, "measurementId", 64)), "v2 analysis");
                Measurement(D.Required(source, "measurementId", 64)); break;
            case "trace-comparison.v2":
                TraceQualityMeasurements.Same(source, new TraceQualityComparison(artifactsRoot).Build(
                    D.Required(source, "beforeMeasurementId", 64), D.Required(source, "afterMeasurementId", 64),
                    TraceQualityMeasurements.Object(source, "alignment")), "v2 comparison");
                Measurement(D.Required(source, "beforeMeasurementId", 64)); Measurement(D.Required(source, "afterMeasurementId", 64)); break;
            case "diagnostic-experiment-review.v2": Pair(experiments.ReadReview(reportId), "pair"); break;
            case "diagnostic-experiment-repeat.v2":
                var repeated = experiments.ReadRepeated(reportId);
                for (int index = 0; index < repeated.Pairs.Length; index++)
                    Pair(repeated.Pairs[index], D.Required(source["runs"]![index]!, "id", 64));
                break;
            default: throw new BridgeException("InvalidDiagnosticReport", "Expected a v2 quality analysis, comparison or experiment report.");
        }
        var document = new JsonObject { ["schemaVersion"] = "diagnostic-quality-report.v2", ["sourceReportId"] = reportId,
            ["sourceSchemaVersion"] = schema, ["sourceReport"] = source, ["sourceReportBase64"] = Convert.ToBase64String(original),
            ["measurements"] = measurements, ["experiments"] = contexts, ["runs"] = runs, ["sourceEvidenceRevalidated"] = true,
            ["originalBytesIncluded"] = true };
        TraceQualityMeasurements.Authority(document);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, BridgeProtocol.Json);
        if (json.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Complete quality report exceeds 8 MiB; select fewer run pairs. No observations were omitted.");
        byte[] bytes = format == "json" ? json : Encoding.UTF8.GetBytes(Render(document, Encoding.UTF8.GetString(json), format == "html"));
        if (bytes.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable quality report exceeds 8 MiB; select fewer run pairs. No observations were omitted.");
        var receipt = store.Publish(bytes, extension);
        receipt["schemaVersion"] = "diagnostic-quality-report-export.v2"; receipt["sourceReportId"] = reportId;
        receipt["sourceSchemaVersion"] = schema; receipt["format"] = format;
        receipt["mediaType"] = format switch { "json" => "application/json", "html" => "text/html; charset=utf-8", _ => "text/markdown; charset=utf-8" };
        receipt["sourceEvidenceRevalidated"] = true; receipt["originalBytesIncluded"] = true;
        TraceQualityMeasurements.Authority(receipt); return receipt;
    }
    private static string Render(JsonObject document, string raw, bool html)
    {
        var output = new StringBuilder();
        string Escape(string text) => html ? WebUtility.HtmlEncode(text) : DiagnosticReportExport.Markdown(text);
        string Value(JsonNode? n) => n is null ? "not recorded" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
        void Add(string text)
        {
            if ((long)output.Length + text.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable quality report exceeds 8 MiB.");
            output.AppendLine(text);
        }
        void Heading(int level, string value) => Add(html ? $"<h{level}>{Escape(value)}</h{level}>" : new string('#', level) + " " + Escape(value) + "\n");
        void Paragraph(string value) => Add(html ? "<p>" + Escape(value) + "</p>" : Escape(value) + "\n");
        void Table(string[] headers, IEnumerable<string[]> rows)
        {
            Add(html ? "<div class=\"table-scroll\" tabindex=\"0\"><table><thead><tr>" + string.Concat(headers.Select(h => "<th scope=\"col\">" + Escape(h) + "</th>")) + "</tr></thead><tbody>"
                : "| " + string.Join(" | ", headers.Select(Escape)) + " |\n| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
            foreach (var row in rows) Add(html ? "<tr>" + string.Concat(row.Select(s => "<td>" + Escape(s) + "</td>")) + "</tr>"
                : "| " + string.Join(" | ", row.Select(Escape)) + " |");
            Add(html ? "</tbody></table></div>" : "");
        }
        if (html) Add("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Diagnostic measurement quality</title><style>" + DiagnosticReportExport.Style + "h1,h2,h3{overflow-wrap:anywhere}</style></head><body><main>");
        Heading(1, "Diagnostic measurement quality"); Paragraph(TraceQualityMeasurements.Limits);
        Paragraph("Retained source report: " + Value(document["sourceReportId"]));
        Heading(2, "Findings and requested windows");
        var source = document["sourceReport"]!;
        if (D.Text(source, "schemaVersion") == "diagnostic-experiment-repeat.v2")
        {
            string Counts(JsonNode? h) => Value(h?["runs"]) + " runs; " + Value(h?["conditionObservedRuns"]) +
                " with condition observations; " + Value(h?["unresolvedRuns"]) + " unresolved";
            Table(["Hypothesis", "Before runs", "After runs", "Interpretation"],
                D.Rows(source["hypotheses"]).Select(h => new[] { Value(h!["id"]) + ": " + Value(h["statement"]),
                    Counts(h["before"]), Counts(h["after"]), "Descriptive counts only; independence and behavior remain unverified." }));
        }
        else if (source["hypotheses"] is not null || source["beforeHypotheses"] is not null)
            Table(["Hypothesis", "Matched / contradicted / unknown observations", "Verdict"],
                D.Rows(source["hypotheses"] ?? source["beforeHypotheses"]).Select(h => new[] { Value(h!["id"]) + ": " + Value(h["statement"]),
                    Value(h["matchedSamples"]) + " / " + Value(h["contradictedSamples"]) + " / " + Value(h["unknownSamples"]), Value(h["verdict"]) }));
        foreach (var experiment in D.Rows(document["experiments"]))
        {
            var e = experiment!["document"]!; var diagnostic = e["diagnosticCase"]!;
            Table(["Saved experiment identity", "Original selection"], new[] {
                new[] { "Experiment / case", Value(experiment["id"]) + " / " + Value(e["diagnosticPlanId"]) },
                new[] { "Saved FB", Value(diagnostic["blockPath"]) },
                new[] { "Selected instance", Value(diagnostic["selectedInstance"]?["dbPath"]) + " / " + Value(diagnostic["selectedInstance"]?["memberPath"]) } });
        }
        void Windows(JsonNode report, string label)
        {
            Heading(3, label);
            Paragraph("Explicit lineage: " + Value(report["lineage"]));
            foreach (string side in new[] { "before", "after" })
                if (report[side + "Window"] is JsonObject window)
                {
                    Table(["Phase / clock / epoch", "Requested seconds relative to supplied anchor", "Coverage and limits"], new[] { new[] {
                        side + " / " + Value(window["clockId"]) + " / " + Value(window["epoch"]),
                        Value(window["requestedStartSeconds"]) + " .. " + Value(window["requestedEndSeconds"]) + "; anchor " + Value(window["anchorSeconds"]),
                        Value(window["coverage"]) + "; " + Value(window["verdict"]) + "; " + Value(window["reasons"]) } });
                    Table(["Hypothesis", "Matched", "Contradicted", "Unknown", "Verdict"],
                        D.Rows(window["hypotheses"]).Select(h => new[] { Value(h!["id"]), Value(h["matchedSamples"]),
                            Value(h["contradictedSamples"]), Value(h["unknownSamples"]), Value(h["verdict"]) }));
                }
        }
        if (source["beforeWindow"] is not null) Windows(source, "Selected comparison windows");
        foreach (var run in D.Rows(document["runs"])) Windows(run!["review"]!, "Run pair " + Value(run["id"]));
        foreach (var input in D.Rows(document["measurements"]))
        {
            var imported = input!["document"]!; var measurement = imported["measurement"]!;
            Heading(2, "Measurement " + Value(input["id"]));
            Paragraph("Exact original input SHA-256: " + Value(input["original"]?["sha256"]));
            Heading(3, "Quality and completeness");
            Table(["Evidence", "Recorded value"], new[] { new[] { "Capture state and dropped samples", Value(measurement["capture"]) },
                new[] { "Supplied quality; unverified", Value(measurement["quality"]) } });
            Table(["Structural observation", "Count or state"], imported["qualityAssessment"]!.AsObject()
                .Where(p => p.Key is not ("series" or "capture" or "suppliedQuality")).Select(p => new[] { p.Key, Value(p.Value) }));
            Table(["Signal / clock", "Usable / unknown values", "Good-value range or Boolean counts", "Resets / duplicate times / irregular intervals", "Adjacent recorded changes"],
                D.Rows(imported["qualityAssessment"]!["series"]).Select(s => new[] {
                    Value(s!["signalId"]) + " / " + Value(s["clockId"]), Value(s["usableValueReadings"]) + " / " + Value(s["unknownValueReadings"]),
                    Value(s["minimum"]) + " .. " + Value(s["maximum"]) + "; true " + Value(s["trueReadings"]) + ", false " + Value(s["falseReadings"]),
                    Value(s["clockResets"]) + " / " + Value(s["duplicateTimes"]) + " / " + Value(s["irregularIntervals"]), Value(s["adjacentValueChangeCount"]) }));
            Heading(3, "Clock domains and timing");
            Table(["Clock", "Label", "Kind", "Time unit"], D.Rows(measurement["clocks"]).Select(c => new[] {
                Value(c!["id"]), Value(c["label"]), Value(c["kind"]), Value(c["timeUnit"]) }));
            Paragraph("Expected intervals: " + Value(measurement["expectedIntervals"]));
            Heading(3, "Individual observations");
            Table(["Sequence", "Signal", "Status / raw status", "Value", "Error", "Original timestamps"],
                D.Rows(measurement["samples"]).SelectMany(s => TraceQualityMeasurements.Object(s, "readings").Select(r => new[] {
                    Value(s!["sequence"]), r.Key, Value(r.Value?["status"]) + " / " + Value(r.Value?["statusCode"]),
                    Value(r.Value?["value"]), Value(r.Value?["error"]), Value(r.Value?["timestamps"]) })));
            Paragraph("Absent reading keys and empty sequence groups remain visible in the complete retained JSON; they are never filled with zero or interpolated.");
        }
        Heading(2, "Complete retained JSON");
        if (html) Add("<pre tabindex=\"0\"><code>" + WebUtility.HtmlEncode(raw) + "</code></pre></main></body></html>");
        else
        {
            int width = Math.Max(3, Regex.Matches(raw, "`+").Select(m => m.Length + 1).DefaultIfEmpty(3).Max());
            string fence = new('`', width); Add(fence + "json\n" + raw + "\n" + fence);
        }
        return output.ToString();
    }
}
