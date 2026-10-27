using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Binds an existing sequential capture to an exact experiment without acquiring or upgrading evidence.</summary>
public sealed class DiagnosticRuntimeAcquisitions(string artifactsRoot)
{
    public const string Limits = "Retained sequential polling observations only. Reads in one cycle are not simultaneous or scan synchronized. Saved declarations and caller-supplied program labels do not verify online addresses or the running program. Values between polls, PLC transition times and clock synchronization remain unknown. Pattern counts do not support or contradict behavioral assertions, prove causality, or prove a fix. This acquisition is separate from imported before/after measurement composition.";
    private static readonly string[] FalseClaims = ["atomicAcrossSignals", "scanSynchronized", "programMatchVerified",
        "addressMatchVerified", "sourceAuthenticityVerified", "clockSynchronizationVerified", "assertionsAdmitted",
        "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted"];

    public JsonObject Acquire(RuntimeCaptureStore captures, string experimentId, string snapshotRef, JsonObject request)
    {
        var acquisition = Build(captures, experimentId, snapshotRef, request);
        var store = new DiagnosticStore(artifactsRoot);
        // Request, chain, provenance and per-document bounds precede publication. Global
        // quota/I/O failure may leave a valid immutable analysis child, never a completed acquisition receipt.
        store.Save(acquisition["analysis"]!.AsObject());
        var receipt = store.Save(acquisition); receipt["acquisition"] = acquisition; return receipt;
    }

    internal JsonObject Build(RuntimeCaptureStore captures, string experimentId, string snapshotRef, JsonObject request)
    {
        try { return BuildCore(captures, experimentId, snapshotRef, request); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new BridgeException("RuntimeAcquisitionSourceUnavailable", "Original capture chain or saved source evidence is unavailable; embedded copies cannot replace it."); }
    }

    private JsonObject BuildCore(RuntimeCaptureStore captures, string experimentId, string snapshotRef, JsonObject request)
    {
        D.Bound(request);
        if (request.Any(p => p.Key is not ("reason" or "units")) || request["units"] is not JsonObject units)
            throw new BridgeException("InvalidRequest", "Supply exactly reason and explicit units; online identity or quality attestations are not accepted.");
        D.Required(request, "reason", 2000);
        var experiment = new DiagnosticExperiments(artifactsRoot).ReadExperiment(experimentId);
        var snapshot = captures.ReadSnapshot(snapshotRef);
        if (snapshot.Progress.Rows > RuntimeCaptureAcquisitionSource.MaximumRows)
            throw new BridgeException("ArtifactCapacity", "Acquisition retains at most 10,000 complete rows; use original runtime capture export for larger captures.");
        if (snapshot.Plan.Association is not { } association ||
            association.CaseId != D.Text(experiment, "diagnosticPlanId") || association.ReviewId != D.Text(experiment, "reviewId"))
            throw new BridgeException("RuntimeAcquisitionLineageMismatch", "Capture must already retain the exact experiment's case, review, selected instance and signal association. Acquisition cannot retroactively associate a capture.");
        var resolved = RuntimeCaptureAssociations.Resolve(artifactsRoot, snapshot.Plan.Request, snapshot.Plan.Signals);
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(resolved, BridgeProtocol.Json),
                JsonSerializer.SerializeToNode(association, BridgeProtocol.Json)))
            throw new BridgeException("CaptureMismatch", "Retained capture mapping differs from its original saved declaration evidence.");
        var analysis = new RuntimeCaptureAnalysis(artifactsRoot).Build(captures, snapshotRef, units);
        var original = RuntimeCaptureAcquisitionSource.Read(artifactsRoot, captures, snapshotRef);
        var progress = snapshot.Progress;
        var issues = new JsonArray("Sequential reads are not an atomic multi-signal sample.",
            "Address and running-program association remain unverified, including caller-supplied labels.",
            "Analysis examples are bounded to 20 per hypothesis and 200 observed changes; complete original rows remain retained. The readable raw-row preview is limited to 64.");
        if (progress.State == "running") issues.Add("Only this immutable committed prefix is included; uncommitted or future data is unknown.");
        if (progress.TerminalReason is not null && progress.TerminalReason != "durationLimit")
            issues.Add("Capture ended with " + progress.TerminalReason + "; requested duration coverage is not established.");
        if (progress.SkippedPolls > 0) issues.Add("Skipped polling cycles are explicit gap rows, not reconstructed samples.");
        if (progress.DroppedReadResults > 0) issues.Add("A read result was dropped by capture retention bounds.");
        var quality = analysis["qualityAssessment"]!;
        foreach (string field in new[] { "missingValues", "hostReadErrors", "providerFailures", "unsupportedValues" })
            if (quality[field]!.GetValue<int>() > 0) issues.Add(field + " remain unknown; no carry-forward or interpolation.");
        var acquisition = new JsonObject { ["schemaVersion"] = "diagnostic-runtime-acquisition.v1",
            ["experimentId"] = experimentId, ["snapshotRef"] = snapshotRef, ["request"] = request.DeepClone(),
            ["experiment"] = experiment, ["captureSource"] = original,
            ["analysisId"] = RuntimeCaptureAcquisitionSource.Id(analysis), ["analysis"] = analysis,
            ["acquisitionMode"] = "sequentialPolling", ["sourceEvidenceRevalidated"] = true,
            ["qualityLimits"] = issues, ["completeCommittedPrefixRetained"] = true,
            ["plcChangesBetweenPollsUnknown"] = true, ["limits"] = Limits };
        foreach (string field in FalseClaims) acquisition[field] = false;
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(acquisition, BridgeProtocol.Json);
        if (encoded.Length > DiagnosticStore.MaxBytes)
            throw new BridgeException("ArtifactCapacity", "Complete acquisition exceeds 8 MiB; no rows or provenance were silently omitted.");
        return acquisition;
    }

    public JsonObject Export(RuntimeCaptureStore captures, string acquisitionId, string format)
    {
        var (extension, mediaType) = format switch { "json" => (".json", "application/json"),
            "markdown" => (".md", "text/markdown"), "html" => (".html", "text/html"),
            _ => throw new BridgeException("InvalidRequest", "format must be json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot); var (retained, source) = Reopen(captures, acquisitionId);
        byte[] bytes = format == "json" ? source : Encoding.UTF8.GetBytes(Render(retained, acquisitionId, Encoding.UTF8.GetString(source), format == "html"));
        if (bytes.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable acquisition exceeds 8 MiB; use exact JSON.");
        var receipt = store.Publish(bytes, extension);
        receipt["schemaVersion"] = "diagnostic-runtime-acquisition-export.v1"; receipt["acquisitionId"] = acquisitionId;
        receipt["sourceArtifact"] = new JsonObject { ["path"] = Path.Combine(Path.GetFullPath(artifactsRoot), "trace-diagnostics", acquisitionId + ".json"),
            ["sha256"] = acquisitionId, ["bytes"] = source.Length };
        receipt["format"] = format; receipt["mediaType"] = mediaType; receipt["sourceEvidenceRevalidated"] = true;
        receipt["analysisRecomputed"] = true; foreach (string field in FalseClaims) receipt[field] = false;
        receipt["limits"] = Limits; return receipt;
    }

    /// <summary>Rederives a retained acquisition from its original capture chain and experiment; altered records refuse.</summary>
    private (JsonObject Retained, byte[] Source) Reopen(RuntimeCaptureStore captures, string acquisitionId)
    {
        var store = new DiagnosticStore(artifactsRoot); byte[] source = store.ReadBytes(acquisitionId);
        var retained = JsonNode.Parse(source)!.AsObject();
        if (D.Text(retained, "schemaVersion") != "diagnostic-runtime-acquisition.v1" || retained["request"] is not JsonObject request)
            throw new BridgeException("InvalidRequest", "Expected a retained runtime acquisition.");
        var derived = Build(captures, D.Required(retained, "experimentId", 64), D.Required(retained, "snapshotRef", 160), request);
        if (!JsonNode.DeepEquals(retained, derived) ||
            !JsonNode.DeepEquals(store.Read(D.Required(retained, "analysisId", 64), "runtime-capture-analysis.v1"), derived["analysis"]))
            throw new BridgeException("CaptureMismatch", "Retained acquisition or analysis differs from its original source chain and experiment.");
        return (retained, source);
    }

    public const string ComparisonLimits = "Descriptive comparison of two retained sequential acquisitions of one experiment against its case's declarative predictions. Cycle counts depend on each capture's duration and coverage; patterns are not rates, statistics or synchronized events. Stimuli between the captures are not linked or verified by this record. All verdicts remain unresolved; no causation or fix proof.";

    /// <summary>Before/after hypothesis pattern counts of two distinct captures of one experiment; both acquisitions are rederived first.</summary>
    public JsonObject Compare(RuntimeCaptureStore captures, string beforeAcquisitionId, string afterAcquisitionId, JsonObject request)
    {
        D.Bound(request);
        if (request.Any(p => p.Key != "reason")) throw new BridgeException("InvalidRequest", "Supply exactly reason.");
        D.Required(request, "reason", 2000);
        var before = Reopen(captures, beforeAcquisitionId).Retained; var after = Reopen(captures, afterAcquisitionId).Retained;
        if (D.Text(before, "experimentId") != D.Text(after, "experimentId") ||
            D.Text(before, "snapshotRef").Split(':')[1] == D.Text(after, "snapshotRef").Split(':')[1])
            throw new BridgeException("MeasurementLineageMismatch", "Compare acquisitions of two distinct captures of the same exact experiment.");
        JsonObject Side(JsonObject acquisition, string id)
        {
            var analysis = acquisition["analysis"]!;
            return new() { ["acquisitionId"] = id, ["snapshotRef"] = acquisition["snapshotRef"]!.DeepClone(), ["analysisId"] = acquisition["analysisId"]!.DeepClone(),
                ["context"] = analysis["capture"]!["context"]?.DeepClone(), ["recordedProgress"] = analysis["capture"]!["recordedProgress"]!.DeepClone(),
                ["signalIds"] = D.Strings(analysis["association"]!["signals"]!.AsArray().Select(s => D.Text(s, "signalId"))),
                ["qualityAssessment"] = analysis["qualityAssessment"]!.DeepClone(), ["qualityLimits"] = acquisition["qualityLimits"]!.DeepClone() };
        }
        JsonObject Counts(JsonNode hypothesis)
        {
            int matched = hypothesis["matchedCycles"]!.GetValue<int>(), contradicted = hypothesis["contradictedCycles"]!.GetValue<int>();
            return new() { ["matchedCycles"] = matched, ["contradictedCycles"] = contradicted, ["unknownCycles"] = hypothesis["unknownCycles"]!.DeepClone(),
                ["pattern"] = (matched > 0, contradicted > 0) switch { (true, false) => "matchedOnly", (false, true) => "contradictedOnly", (true, true) => "mixed", _ => "notApplicable" } };
        }
        // Same experiment, same immutable case: both analyses list its hypotheses in the same order.
        var a = before["analysis"]!["hypotheses"]!.AsArray(); var b = after["analysis"]!["hypotheses"]!.AsArray();
        var hypotheses = new JsonArray(a.Select((h, i) =>
        {
            var x = Counts(h!); var y = Counts(b[i]!);
            return (JsonNode?)new JsonObject { ["id"] = h!["id"]!.DeepClone(), ["statement"] = h["statement"]!.DeepClone(), ["prediction"] = h["prediction"]?.DeepClone(),
                ["before"] = x, ["after"] = y, ["patternChanged"] = D.Text(x, "pattern") != D.Text(y, "pattern"), ["verdict"] = "unresolved", ["causationProven"] = false };
        }).ToArray());
        var beforeSide = Side(before, beforeAcquisitionId); var afterSide = Side(after, afterAcquisitionId);
        var comparison = new JsonObject { ["schemaVersion"] = "diagnostic-runtime-acquisition-comparison.v1", ["experimentId"] = before["experimentId"]!.DeepClone(),
            ["request"] = request.DeepClone(), ["before"] = beforeSide, ["after"] = afterSide, ["hypotheses"] = hypotheses,
            ["sameCaptureContext"] = JsonNode.DeepEquals(beforeSide["context"], afterSide["context"]),
            ["sameSignals"] = JsonNode.DeepEquals(beforeSide["signalIds"], afterSide["signalIds"]),
            ["verdict"] = "unresolved", ["sourceEvidenceRevalidated"] = true, ["limits"] = ComparisonLimits };
        foreach (string field in FalseClaims) comparison[field] = false;
        var receipt = new DiagnosticStore(artifactsRoot).Save(comparison); receipt["comparison"] = comparison; return receipt;
    }

    private static string Render(JsonObject value, string id, string raw, bool html)
    {
        var output = new StringBuilder();
        string Escape(string text) => html ? WebUtility.HtmlEncode(text) : DiagnosticReportExport.Markdown(text);
        string Text(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "not recorded";
        void Line(string text) { output.AppendLine(text); if (output.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable acquisition exceeds its bounded output."); }
        void Heading(int level, string text) => Line(html ? $"<h{level}>{Escape(text)}</h{level}>" : new string('#', level) + " " + Escape(text) + "\n");
        void Paragraph(string text) => Line(html ? "<p>" + Escape(text) + "</p>" : Escape(text) + "\n");
        void Code(string text)
        {
            if (html) Line("<pre tabindex=\"0\"><code>" + WebUtility.HtmlEncode(text) + "</code></pre>");
            else { int longest = 0, current = 0; foreach (char c in text) { current = c == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
                string fence = new('`', Math.Max(3, longest + 1)); Line(fence + "json\n" + text + "\n" + fence + "\n"); }
        }
        if (html) Line("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Retained runtime acquisition</title><style>" + DiagnosticReportExport.Style + "h1,h2,h3,p{overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style></head><body><main>");
        Heading(1, "Retained runtime acquisition"); Paragraph("Acquisition: " + id); Paragraph(Limits);
        Paragraph(D.Text(value["request"], "reason"));
        Heading(2, "Exact experiment and capture identity");
        Paragraph("Experiment: " + D.Text(value, "experimentId") + "\nSnapshot: " + D.Text(value, "snapshotRef"));
        Code(Text(value["experiment"]!["diagnosticCase"]!["sourceRevision"]));
        Code(Text(value["experiment"]!["diagnosticCase"]!["selectedInstance"]));
        Heading(2, "Selected signals and retained associations");
        Code(Text(value["captureSource"]!["plan"]!["association"]));
        Code(Text(value["captureSource"]!["plan"]!["request"]));
        Heading(2, "Timing, quality, gaps and capture limits");
        Code(Text(value["captureSource"]!["recordedProgress"]));
        Code(Text(value["analysis"]!["qualityAssessment"]));
        foreach (var issue in value["qualityLimits"]!.AsArray()) Paragraph(issue!.GetValue<string>());
        Heading(2, "Observed sequential patterns");
        foreach (var finding in value["analysis"]!["hypotheses"]!.AsArray())
        {
            Heading(3, D.Text(finding, "id") + ": unresolved");
            Paragraph(D.Text(finding, "statement"));
            Paragraph("Matched cycles: " + Text(finding!["matchedCycles"]) + "; contradicted cycles: " + Text(finding["contradictedCycles"]) + "; unknown cycles: " + Text(finding["unknownCycles"]));
            Paragraph(D.Text(finding, "reason")); Code(Text(finding["examples"]));
        }
        Heading(2, "Original chain hashes");
        foreach (var file in value["captureSource"]!["originalFiles"]!.AsArray())
            Paragraph(D.Text(file, "relativePath") + "\nSHA-256: " + D.Text(file, "sha256") + "\nBytes: " + Text(file!["bytes"]));
        Heading(2, "Per-read timing and raw results");
        var rows = value["captureSource"]!["rows"]!.AsArray();
        Paragraph($"Preview of {Math.Min(64, rows.Count)} of {rows.Count} complete retained rows. Complete rows and original file bytes are included in the JSON below. ReadResultJson retains the exact recorded string, including request/response times, provider status and values.");
        foreach (var row in rows.Take(64)) Code(Text(row));
        Heading(2, "Complete original acquisition JSON"); Paragraph("Original plan/segment bytes are base64 encoded with exact relative paths, sizes and hashes. Reopen checks the original files; copies embedded here do not hide missing or altered evidence.");
        Code(raw);
        if (html) Line("</main></body></html>");
        return output.ToString();
    }
}
