using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Presentation of retained reports, with exact source bytes and no new analysis or native claims.</summary>
public sealed class DiagnosticReportExport(string artifactsRoot)
{
    private static readonly JsonSerializerOptions HandoffJson = new(BridgeProtocol.Json) {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 48 };
    private const string Limits = "Supplied evidence only. Program identity, clock synchronization and native behavior are not verified. This report does not prove causality or that a fault is fixed. Export presents the retained results; it does not rerun analysis or authenticate their source.";

    public JsonObject Export(string reportId, string format)
    {
        var (extension, mediaType) = format switch {
            "json" => (".json", "application/json"), "markdown" => (".md", "text/markdown"),
            "html" => (".html", "text/html"), _ => throw new BridgeException("InvalidRequest", "format must be json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot);
        byte[] source = store.ReadBytes(reportId);
        var report = JsonNode.Parse(source) as JsonObject ?? throw Invalid();
        if (D.Text(report, "schemaVersion") is "diagnostic-report.v2" or "trace-comparison.v2")
            return new DiagnosticQualityReportExport(artifactsRoot).Export(reportId, format);
        bool comparison = Validate(report);
        byte[] bytes = format == "json" ? source : Encoding.UTF8.GetBytes(Render(report, reportId, Encoding.UTF8.GetString(source), comparison, format == "html"));
        var original = store.Publish(source, ".json");
        var receipt = format == "json" ? original.DeepClone().AsObject() : store.Publish(bytes, extension);
        receipt["schemaVersion"] = "diagnostic-report-export.v1";
        receipt["sourceReportId"] = reportId;
        receipt["sourceSchemaVersion"] = report["schemaVersion"]!.DeepClone();
        receipt["sourceArtifact"] = original["artifact"]!.DeepClone();
        receipt["format"] = format; receipt["mediaType"] = mediaType;
        receipt["nativeQualification"] = false; receipt["sourceAuthenticityVerified"] = false;
        receipt["analysisRecomputed"] = false; receipt["limits"] = Limits;
        return receipt;
    }

    private static bool Validate(JsonObject report)
    {
        string schema = D.Text(report, "schemaVersion");
        if (schema is not ("diagnostic-report.v1" or "trace-comparison.v1" or "runtime-capture-analysis.v1")) throw Invalid();
        bool comparison = schema == "trace-comparison.v1";
        bool capture = schema == "runtime-capture-analysis.v1";
        False(report, "nativeQualification"); False(report, comparison ? "bugFixProven" : "causationProven");
        foreach (string field in comparison ? new[] { "beforeMeasurementId", "afterMeasurementId", "beforeTracePlanId", "afterTracePlanId", "beforeReviewId", "afterReviewId" } : capture ? new[] { "caseId", "reviewId" } : new[] { "measurementId", "tracePlanId", "reviewId" })
            if (report[field] is not JsonValue id || !id.TryGetValue<string>(out string? value) || value is null || !Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z")) throw Invalid();
        _ = Rows(report, comparison ? "signals" : "hypotheses").ToArray();
        if (!comparison)
        {
            _ = Rows(report, capture ? "observedChanges" : "transitions").ToArray();
            foreach (var hypothesis in Rows(report, "hypotheses"))
            {
                False(hypothesis, "causationProven");
                if (capture ? D.Text(hypothesis,"verdict")!="unresolved" : D.Text(hypothesis, "verdict") is not ("unresolved" or "supportedPrediction" or "contradictedPrediction")) throw Invalid();
            }
        }
        if(capture)
        {
            foreach(string field in new[]{"atomicAcrossSignals","scanSynchronized","programMatchVerified","addressMatchVerified","sourceAuthenticityVerified","clockSynchronizationVerified"})False(report,field);
            if(!Regex.IsMatch(D.Text(report,"snapshotRef"),"\\Acapture:[a-f0-9]{32}:[0-9]{1,6}:[a-f0-9]{64}\\z") || report["plcChangesBetweenPollsUnknown"]?.GetValue<bool>()!=true || report["capture"] is not JsonObject || report["association"] is not JsonObject association)throw Invalid();
            foreach(string field in new[]{"addressMatchVerified","programMatchVerified","sourceAuthenticityVerified","nativeQualified"})False(association,field);
            if (association.ContainsKey("alarmBindings"))
            {
                try
                {
                    var request = report["capture"]!["request"]!.Deserialize<RuntimeCaptureRequest>(HandoffJson)!;
                    var signals = report["capture"]!["signals"]!.Deserialize<RuntimeCaptureSignal[]>(HandoffJson)!;
                    var retained = association.Deserialize<RuntimeCaptureAssociation>(HandoffJson)!;
                    if (request is null || signals is null || signals.Length == 0 || retained?.AlarmBindings is null ||
                        retained.CaseId != D.Text(report,"caseId") || retained.ReviewId != D.Text(report,"reviewId")) throw Invalid();
                    RuntimeCaptureAssociations.ValidateRequest(request);
                    RuntimeCaptureAssociations.ValidateRetained(request, signals, retained);
                }
                catch (Exception error) when (error is BridgeException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException or IndexOutOfRangeException)
                { throw Invalid(); }
            }
            if (report.ContainsKey("handoff") || report["capture"]?["request"]?["handoffRef"] is not null)
            {
                try
                {
                    var request = report["capture"]!["request"]!.Deserialize<RuntimeCaptureRequest>(HandoffJson)!;
                    var signals = report["capture"]!["signals"]!.Deserialize<RuntimeCaptureSignal[]>(HandoffJson)!;
                    var handoff = report["handoff"]?.Deserialize<RuntimeCaptureHandoff>(HandoffJson);
                    if (request is null || signals is null || signals.Length == 0 || handoff is null ||
                        handoff.ReviewId != D.Text(report,"reviewId") ||
                        !JsonNode.DeepEquals(report["capture"]!["context"], JsonSerializer.SerializeToNode(handoff.Session.Context,BridgeProtocol.Json))) throw Invalid();
                    RuntimeCaptureHandoffs.ValidateRetained(request, signals, handoff);
                }
                catch (Exception error) when (error is BridgeException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException)
                { throw Invalid(); }
            }
            return false;
        }
        string[] contextFields = comparison ? ["beforeAcquisition", "afterAcquisition", "programIdentityChanged", "clockSynchronizationVerified"] : ["acquisitionContext", "clockSynchronizationVerified"];
        if (contextFields.Any(report.ContainsKey))
        {
            if (contextFields.Any(field => !report.ContainsKey(field))) throw Invalid();
            False(report, "clockSynchronizationVerified");
            if (comparison && (report["programIdentityChanged"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))) throw Invalid();
            foreach (string key in comparison ? new[] { "beforeAcquisition", "afterAcquisition" } : new[] { "acquisitionContext" })
            {
                if (report[key] is not JsonObject context) throw Invalid();
                False(context, "identityVerified");
                foreach (string field in new[] { "mode", "clock", "timeUnit", "programIdentity" })
                    if (context[field] is not JsonValue text || !text.TryGetValue<string>(out string? value) || string.IsNullOrWhiteSpace(value)) throw Invalid();
            }
        }
        return comparison;
    }

    private static BridgeException Invalid() => new("InvalidDiagnosticReport", "Expected a retained diagnostic analysis/comparison report with explicit unverified native claims and complete recorded identities.");
    private static void False(JsonObject value, string key)
    { if (value[key] is not JsonValue flag || !flag.TryGetValue<bool>(out bool actual) || actual) throw Invalid(); }
    private static IEnumerable<JsonObject> Rows(JsonObject value, string key)
    {
        if (value[key] is not JsonArray array) throw Invalid();
        foreach (var item in array) yield return item as JsonObject ?? throw Invalid();
    }
    private static string Value(JsonNode? value) => value is null ? "Not recorded" : value is JsonValue scalar && scalar.TryGetValue<string>(out string? text) ? text ?? "Not recorded" : value.ToJsonString();

    internal static string Markdown(string text)
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

    private static string Render(JsonObject report, string reportId, string raw, bool comparison, bool html)
    {
        bool capture=D.Text(report,"schemaVersion")=="runtime-capture-analysis.v1";
        string countKind=capture?"cycles":"samples",countSuffix=capture?"Cycles":"Samples";
        var output = new StringBuilder();
        void Add(string text)
        {
            if ((long)output.Length + text.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Rendered diagnostic report exceeds its 8 MiB bound; use the exact JSON export.");
            output.Append(text);
        }
        string Escape(string text) => html ? WebUtility.HtmlEncode(text) : Markdown(text);
        void Heading(int level, string text) => Add(html ? $"<h{level}>{Escape(text)}</h{level}>\n" : new string('#', level) + " " + Escape(text) + "\n\n");
        void Paragraph(string text) => Add(html ? "<p>" + Escape(text) + "</p>\n" : Escape(text) + "\n\n");
        void Code(string text)
        {
            if (html) Add("<pre tabindex=\"0\"><code>" + WebUtility.HtmlEncode(text) + "</code></pre>\n");
            else
            {
                int width = Math.Max(3, Regex.Matches(text, "`+").Select(m => m.Length + 1).DefaultIfEmpty(3).Max());
                string fence = new('`', width); Add(fence + "json\n" + text + "\n" + fence + "\n\n");
            }
        }
        void Table(string[] headers, IEnumerable<string[]> rows)
        {
            Add(html ? "<div class=\"table-scroll\" tabindex=\"0\"><table><thead><tr>" + string.Concat(headers.Select(h => "<th scope=\"col\">" + Escape(h) + "</th>")) + "</tr></thead><tbody>\n"
                : "| " + string.Join(" | ", headers.Select(Escape)) + " |\n| " + string.Join(" | ", headers.Select(_ => "---")) + " |\n");
            foreach (var row in rows)
                Add(html ? "<tr>" + string.Concat(row.Select(v => "<td>" + Escape(v) + "</td>")) + "</tr>\n" : "| " + string.Join(" | ", row.Select(Escape)) + " |\n");
            Add(html ? "</tbody></table></div>\n" : "\n");
        }
        void Fields(string title, JsonNode? value)
        {
            Heading(3, title);
            if (value is JsonObject fields) Table(["Field", "Recorded value"], fields.Select(p => new[] { p.Key, Value(p.Value) }));
            else Paragraph("Not retained in this report. Re-run analysis or comparison on the retained imports to obtain current context.");
        }
        void Transitions(JsonObject value, string timeUnit)
        {
            if (value["transitions"] is not JsonArray) { Paragraph("Transitions were not retained in this report."); return; }
            Table(["Signal", "After time (" + timeUnit + ")", "By time (" + timeUnit + ")", "Before", "After"], Rows(value, "transitions").Select(row => new[] {
                Value(row["signalId"]), Value(row[timeUnit == "s (aligned)" ? "afterSeconds" : "afterTime"]),
                Value(row[timeUnit == "s (aligned)" ? "bySeconds" : "byTime"]), Value(row["before"]), Value(row["after"]) }));
        }

        string title = comparison ? "Before / after comparison" : capture ? "Polling capture analysis" : "Diagnostic analysis";
        if (html) Add("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>" + title + " · " + reportId[..12] + "</title><style>" + Style + "</style></head><body><main><header><p class=\"eyebrow\">TIA MCP · Retained diagnostic evidence</p>");
        Heading(1, title); Paragraph("Source report SHA-256: " + reportId);
        if (html) Add("</header><aside aria-label=\"Evidence limits\">");
        Paragraph(Limits); if (html) Add("</aside>");
        Heading(2, comparison ? "Comparison reason" : "Reported symptom"); Paragraph(Value(report[comparison ? "reason" : "symptom"]));
        Heading(2, "Result overview");
        if (comparison) Table(["Signal mapping", "Unit", "Samples (before → after)", "Missing (before → after)", "Transitions (before → after)"], Rows(report, "signals").Select(signal => new[] {
            Value(signal["beforeSignalId"]) + " → " + Value(signal["afterSignalId"]), Value(signal["unit"]),
            Value(signal["before"]?["samplesInWindow"]) + " → " + Value(signal["after"]?["samplesInWindow"]),
            Value(signal["before"]?["missingValues"]) + " → " + Value(signal["after"]?["missingValues"]),
            Value(signal["before"]?["transitionCount"]) + " → " + Value(signal["after"]?["transitionCount"]) }));
        else Table(["Hypothesis", "Recorded result", "Matched / contradicted / unknown "+countKind], Rows(report, "hypotheses").Select(h => new[] {
            Value(h["id"]) + ": " + Value(h["statement"]), Value(h["verdict"]), Value(h["matched"+countSuffix]) + " / " + Value(h["contradicted"+countSuffix]) + " / " + Value(h["unknown"+countSuffix]) }));
        Heading(2, "Acquisition and uncertainty");
        if (comparison)
        {
            Fields("Before acquisition", report["beforeAcquisition"]); Fields("After acquisition", report["afterAcquisition"]);
            Fields("Before quality", report["beforeQuality"]); Fields("After quality", report["afterQuality"]);
            Fields("Before assessment", report["beforeAssessment"]); Fields("After assessment", report["afterAssessment"]);
            Fields("Explicit alignment", report["alignment"]);
            Heading(2, "Compared signals");
            foreach (var signal in Rows(report, "signals"))
            {
                Heading(3, Value(signal["beforeSignalId"]) + " → " + Value(signal["afterSignalId"]));
                Paragraph("Unit: " + Value(signal["unit"]) + ". Same recorded storage path: " + Value(signal["sameStorage"]));
                Fields("Before signal identity", signal["beforeIdentity"]); Fields("After signal identity", signal["afterIdentity"]);
                string[] metrics = ["samplesInWindow", "missingValues", "trueSamples", "falseSamples", "minimum", "maximum", "transitionCount", "transitionsTruncated", "coverage"];
                Table(["Observation", "Before", "After"], metrics.Select(key => new[] { key, Value(signal["before"]?[key]), Value(signal["after"]?[key]) }));
                foreach (string phase in new[] { "before", "after" })
                { Heading(3, phase == "before" ? "Before transitions" : "After transitions"); if (signal[phase] is JsonObject observations) Transitions(observations, "s (aligned)"); }
            }
        }
        else
        {
            if(capture)
            {
                Paragraph("Cycle patterns combine sequential reads, not simultaneous samples. Individual read times and sequences are retained in the examples. All behavioral verdicts remain unresolved.");
                Fields("Capture progress",report["capture"]?["recordedProgress"]);Fields("Acquisition context",report["capture"]?["context"]);Fields("Polling request",report["capture"]?["request"]);
                if (report["association"]?["alarmBindings"] is JsonObject alarm)
                {
                    Heading(3, "Saved alarm operand bindings");
                    Paragraph("Original supplied bindings to saved executable source. Selection for capture does not prove a successful observation, active alarm, runtime invocation or running-program match.");
                    var selected = Rows(report["association"]!.AsObject(),"signals").Select(s=>D.Text(s,"signalId")).ToHashSet(StringComparer.Ordinal);
                    Table(["Binding", "Diagnostic signal", "Capture selection", "Saved declaration"], Rows(alarm,"bindings").Select(row=>new[] {
                        Value(row["id"]), Value(row["signalId"]), selected.Contains(D.Text(row,"signalId")) ? "Selected for capture" : "Not selected in this capture", Value(row["declaration"]) }));
                }
                if (report["handoff"] is JsonObject handoff)
                {
                    Fields("Retained security handoff", handoff);
                    Paragraph("Historical declarations tied to the captured session. Identity, effective rights and source authenticity remain unverified; no operation or diagnostic assertion is admitted. Export does not refresh this evidence.");
                }
            }
            else { Fields("Acquisition context", report["acquisitionContext"]); Fields("Acquisition quality", report["acquisitionQuality"]); }
            Fields("Quality assessment", report["qualityAssessment"]);
            Heading(2, "Hypothesis results");
            foreach (var hypothesis in Rows(report, "hypotheses"))
            {
                Heading(3, Value(hypothesis["id"])); Paragraph(Value(hypothesis["statement"]));
                Table(["Result", "Matched "+countKind, "Contradicted "+countKind, "Unknown "+countKind], [new[] { Value(hypothesis["verdict"]), Value(hypothesis["matched"+countSuffix]), Value(hypothesis["contradicted"+countSuffix]), Value(hypothesis["unknown"+countSuffix]) }]);
                Paragraph(Value(hypothesis["reason"]));
            }
            if(capture)
            {
                Heading(2,"Observed value changes");Paragraph("Total: "+Value(report["observedChangeCount"])+". Retained list truncated: "+Value(report["observedChangesTruncated"])+". Consecutive cycles only; gaps and unavailable values are not bridged. Host completion times do not timestamp PLC transitions.");
                Table(["Signal","Before read","After read","Host elapsed ms (before → after)","Before","After"],Rows(report,"observedChanges").Select(row=>new[]{Value(row["signalId"]),Value(row["beforeRead"]?["sequence"]),Value(row["afterRead"]?["sequence"]),Value(row["beforeRead"]?["elapsedMilliseconds"])+" → "+Value(row["afterRead"]?["elapsedMilliseconds"]),Value(row["before"]),Value(row["after"])}));
            }
            else { Heading(2, "Observed transitions"); Paragraph("Total: " + Value(report["transitionCount"]) + ". Retained list truncated: " + Value(report["transitionsTruncated"])); Transitions(report, Value(report["timeUnit"])); }
            Heading(2, "Coverage and next capture"); Code(report["coverageGaps"]?.ToJsonString() ?? "null"); Paragraph(Value(report["nextCapture"]));
        }
        Heading(2, "Source lineage");
        string[] identities = comparison ? ["beforeMeasurementId", "afterMeasurementId", "beforeTracePlanId", "afterTracePlanId", "beforeReviewId", "afterReviewId", "sourceChanged", "programIdentityChanged"] : capture ? ["snapshotRef","caseId","reviewId"] : ["measurementId", "tracePlanId", "reviewId"];
        Table(["Identity", "Recorded value"], identities.Select(key => new[] { key, Value(report[key]) }));
        Heading(2, "Complete source report"); Paragraph("The exact JSON export is identified by the source hash above. This section retains every source field, including fields not projected into the readable tables.");
        if (html) Add("<details><summary>Show complete source JSON</summary>");
        Code(raw); if (html) Add("</details><footer>Offline report export · no native acquisition or verification</footer></main></body></html>");
        return output.ToString();
    }

    internal const string Style = """
        :root{color-scheme:light;font:16px/1.55 'Segoe UI',system-ui,sans-serif;color:#18332d;background:#f2f5f4}*{box-sizing:border-box}body{margin:0}main{max-width:1080px;margin:32px auto;padding:36px;background:white;border:1px solid #d5e0db;border-radius:12px}h1{font-size:2rem;line-height:1.2;margin:.4rem 0 1rem}h2{margin:2.4rem 0 1rem;border-top:1px solid #d5e0db;padding-top:1.2rem;font-size:1.35rem}h3{font-size:1.05rem;margin:1.5rem 0 .7rem}p,td,th,summary{overflow-wrap:anywhere}p{white-space:pre-wrap}.eyebrow{font-size:.85rem;font-weight:650;letter-spacing:.06em;text-transform:uppercase;color:#49655a}header>p:last-child{font:13px/1.6 ui-monospace,monospace;color:#49655a}aside{background:#fff5df;border-left:4px solid #9b6500;padding:12px 18px;margin:24px 0}aside p{margin:0}.table-scroll{overflow-x:auto;max-width:100%;border:1px solid #d5e0db;border-radius:6px;margin:12px 0}table{border-collapse:collapse;width:100%;font-size:.9rem}th,td{text-align:left;vertical-align:top;padding:10px 12px;border-bottom:1px solid #e3eae6;white-space:pre-wrap}th{background:#edf3ef;font-weight:650;min-width:130px}tr:last-child td{border-bottom:0}td:first-child{min-width:130px}pre{padding:18px;background:#f4f7f5;border:1px solid #d5e0db;border-radius:6px;white-space:pre-wrap;overflow-wrap:anywhere;font:13px/1.6 ui-monospace,monospace;max-height:32rem;overflow:auto}summary{cursor:pointer;padding:12px;background:#edf3ef;border-radius:6px}footer{margin-top:32px;color:#49655a;font-size:.85rem}:focus-visible{outline:3px solid #23694e;outline-offset:3px}@media(max-width:640px){main{margin:0;padding:20px 16px;border:0;border-radius:0}h1{font-size:1.6rem}th,td{padding:8px}table{min-width:440px}}@media print{body,main{background:white}main{margin:0;padding:0;max-width:none;border:0}aside,table,pre{break-inside:avoid}.table-scroll,pre{overflow:visible;max-height:none}h2,h3{break-after:avoid}details{display:none}}
        """;
}
