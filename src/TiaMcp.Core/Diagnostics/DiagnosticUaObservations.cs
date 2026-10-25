using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Offline composition over an exact backend-validated prefix. The reader never acquires data.</summary>
public sealed class DiagnosticUaObservations(string artifactsRoot, Func<string, JsonObject> readSource)
{
    public const string Limits = "Retained UA observations only. Polling cycles and subscription messages are not simultaneous PLC samples. Duplicate notification envelopes remain in the original rows but do not count twice. Gaps, overflow, failures, uncertain values and missing timestamps remain explicit. Caller-selected units and saved declarations do not prove UA datatype equivalence, online addresses or the running program. No interpolation, rate, transition-time, causality or fixed-fault claim is made.";
    internal static readonly string[] FalseClaims = ["atomicAcrossSignals", "scanSynchronized", "programMatchVerified", "addressMatchVerified",
        "datatypeMatchVerified", "sourceAuthenticityVerified", "physicalQualified", "clockSynchronizationVerified", "assertionsAdmitted",
        "causationProven", "bugFixProven", "nativeQualification", "nativeAccessAttempted", "sourceCompletenessVerified"];

    public JsonObject Observe(string snapshotRef)
    {
        var report = Build(snapshotRef);
        var receipt = new DiagnosticStore(artifactsRoot).Save(report);
        // Complete potentially large observations are available through artifact_read.
        receipt["snapshotRef"] = snapshotRef; receipt["bindingId"] = report["bindingId"]!.DeepClone();
        receipt["experimentId"] = report["experimentId"]!.DeepClone(); receipt["acquisitionMode"] = report["acquisitionMode"]!.DeepClone();
        receipt["sourceEvidenceRevalidated"] = true; receipt["limits"] = Limits;
        return receipt;
    }

    internal JsonObject Build(string snapshotRef)
    {
        JsonObject source;
        try { source = readSource(snapshotRef); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new BridgeException("UaDiagnosticSourceUnavailable", "Original capture chain is unavailable; embedded copies cannot replace it."); }
        string mode = D.Required(source, "acquisitionMode", 16);
        var plan = source["plan"]!.AsObject();
        var request = (mode == "polling" ? plan["uaContext"]!["request"] : mode == "subscription" ? plan["request"] : null)?.AsObject()
            ?? throw new BridgeException("InvalidRequest", "Expected a validated UA acquisition source.");
        string bindingId = D.Text(request, "diagnosticBindingId");
        if (bindingId.Length == 0) throw new BridgeException("UaDiagnosticBindingRequired", "This original capture has no pre-acquisition diagnostic binding. It cannot be bound retroactively.");
        var nodes = request["nodes"]!.Deserialize<OpcUaRuntimeNode[]>(BridgeProtocol.Json)!;
        var binding = new DiagnosticUaBindings(artifactsRoot).ValidateForCapture(bindingId, D.Required(request, "attemptId", 32), nodes);
        byte[] bindingBytes = new DiagnosticStore(artifactsRoot).ReadBytes(bindingId);
        if (!JsonNode.DeepEquals(binding, JsonNode.Parse(bindingBytes)))
            throw new BridgeException("UaDiagnosticBindingMismatch", "Binding bytes changed during report composition.");
        var mappings = binding["mappings"]!.AsArray();
        var creation = mode == "subscription" ? source["rows"]!.AsArray().Select(r => r!["event"]).FirstOrDefault(e => D.Text(e, "kind") == "created")?["created"] : null;
        var evidence = new List<JsonObject>[nodes.Length];
        var hostErrors = new int[nodes.Length];
        for (int i = 0; i < evidence.Length; i++) evidence[i] = [];
        int duplicates = 0, keepalives = 0, gapRows = 0, failures = 0, overflow = 0;
        foreach (var row in source["rows"]!.AsArray())
        {
            if (mode == "polling")
            {
                if (D.Text(row, "kind") == "gap") { gapRows++; continue; }
                string selection = D.Required(row!, "selectionId", 32);
                int index = Array.FindIndex(nodes, n => OpcUaCaptureContracts.SelectionId(n) == selection);
                if (index < 0) throw new BridgeException("UaDiagnosticSourceMismatch", "A read is outside the prebound selection.");
                if (row!["errorCode"] is not null) { hostErrors[index]++; continue; }
                var read = JsonNode.Parse(D.Required(row, "readResultJson", 1 << 20))!.AsObject();
                evidence[index].Add(Item(row, read["items"]![0]!, read["startedAt"], read["completedAt"]));
            }
            else
            {
                var evt = row!["event"]!; string kind = D.Text(evt, "kind");
                if (kind == "gap" || kind == "reconnect" && D.Text(evt["reconnect"],"state")=="starting") gapRows++;
                if (kind == "failure") failures++;
                if (evt["duplicate"]!.GetValue<bool>()) { duplicates++; continue; }
                if (evt["keepAlive"]!.GetValue<bool>()) { keepalives++; continue; }
                foreach (var notification in D.Rows(evt["notifications"]))
                {
                    int index = Array.FindIndex(nodes, n => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(n, BridgeProtocol.Json), notification!["value"]!["node"]));
                    if (index < 0) throw new BridgeException("UaDiagnosticSourceMismatch", "A notification is outside the prebound selection.");
                    if (notification!["overflow"]!.GetValue<bool>()) overflow++;
                    var item = Item(row, notification["value"]!, null, null);
                    item["messageSequenceNumber"] = evt["sequenceNumber"]!.DeepClone();
                    item["publishTime"] = evt["publishTime"]!.DeepClone();
                    item["eventKind"] = kind; item["overflow"] = notification["overflow"]!.DeepClone();
                    evidence[index].Add(item);
                }
            }
        }
        var summaries = new JsonArray();
        for (int i = 0; i < nodes.Length; i++)
        {
            var values = evidence[i];
            int Count(Func<JsonNode, bool> test) => values.Count(v => test(v["value"]!));
            summaries.Add(new JsonObject { ["signalId"] = mappings[i]!["signalId"]!.DeepClone(), ["node"] = mappings[i]!["node"]!.DeepClone(),
                ["unit"] = mappings[i]!["unit"]!.DeepClone(), ["savedDatatype"] = mappings[i]!["declaration"]!["datatype"]!.DeepClone(),
                ["monitoredItem"] = creation?["items"]?[i]?.DeepClone(),
                ["observationCount"] = values.Count, ["hostReadErrors"] = hostErrors[i],
                ["goodValues"] = Count(v => D.Text(v, "quality") == "Good" && D.Text(v, "outcome") == "value" && v["encoding"]?.GetValue<string>() != "null"),
                ["uncertain"] = Count(v => D.Text(v, "quality") == "Uncertain"), ["bad"] = Count(v => D.Text(v, "quality") == "Bad"),
                ["unsupported"] = Count(v => D.Text(v, "outcome") == "unsupported"),
                ["metadataErrors"] = Count(v => D.Text(v, "outcome") == "metadataError"),
                ["nullValues"] = Count(v => D.Text(v, "encoding") == "null"),
                ["missingSourceTimestamps"] = Count(v => v["sourceTimestamp"] is null), ["missingServerTimestamps"] = Count(v => v["serverTimestamp"] is null),
                ["observedBuiltInTypes"] = D.Strings(values.Select(v => D.Text(v["value"], "builtInType")).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                ["firstObservation"] = values.FirstOrDefault()?.DeepClone(), ["lastObservation"] = values.LastOrDefault()?.DeepClone(),
                ["coverage"] = values.Count == 0 ? "noSelectedObservations" : "recordedObservationsOnly" });
        }
        var hypotheses = new JsonArray(D.Rows(binding["experiment"]!["diagnosticCase"]!["hypotheses"]).Select(h => (JsonNode)new JsonObject {
            ["id"] = h!["id"]!.DeepClone(), ["statement"] = h["statement"]!.DeepClone(), ["verdict"] = "unresolved",
            ["reason"] = "Independent online signal/program association and simultaneous sample semantics are not established." }).ToArray());
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-ua-observation.v1", ["snapshotRef"] = snapshotRef,
            ["acquisitionMode"] = mode, ["bindingId"] = bindingId, ["experimentId"] = binding["experimentId"]!.DeepClone(),
            ["binding"] = binding, ["bindingArtifact"] = new JsonObject { ["sha256"] = bindingId, ["bytes"] = bindingBytes.Length, ["base64"] = Convert.ToBase64String(bindingBytes) },
            ["captureSource"] = source, ["signals"] = summaries, ["hypotheses"] = hypotheses,
            ["unselectedSignalIds"] = D.Strings(D.Rows(binding["experiment"]!["diagnosticCase"]!["signals"]).Select(s => D.Text(s, "id"))
                .Except(mappings.Select(m => D.Text(m, "signalId")), StringComparer.Ordinal)),
            ["events"] = new JsonObject { ["gapRows"] = gapRows, ["duplicateMessages"] = duplicates, ["keepalives"] = keepalives,
                ["failureEvents"] = failures, ["overflowNotifications"] = overflow },
            ["completeCommittedPrefixRetained"] = true, ["sourceEvidenceRevalidated"] = true, ["limits"] = Limits };
        foreach (string flag in FalseClaims) report[flag] = false;
        if (JsonSerializer.SerializeToUtf8Bytes(report, BridgeProtocol.Json).Length > DiagnosticStore.MaxBytes)
            throw new BridgeException("ArtifactCapacity", "Complete UA observation report exceeds 8 MiB; no source rows were silently omitted.");
        return report;
    }

    private static JsonObject Item(JsonNode row, JsonNode value, JsonNode? started, JsonNode? completed) => new() {
        ["rowSequence"] = row["sequence"]!.DeepClone(), ["elapsedMilliseconds"] = row["elapsedMilliseconds"]!.DeepClone(),
        ["observedUtc"] = row["observedUtc"]!.DeepClone(), ["readStartedAt"] = started?.DeepClone(),
        ["readCompletedAt"] = completed?.DeepClone(), ["value"] = value.DeepClone() };

    public JsonObject Export(string reportId, string format)
    {
        var (extension, media) = format switch { "json" => (".json", "application/json"), "markdown" => (".md", "text/markdown"),
            "html" => (".html", "text/html"), _ => throw new BridgeException("InvalidRequest", "format must be json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot); byte[] source = store.ReadBytes(reportId);
        var saved = JsonNode.Parse(source)!.AsObject();
        if (D.Text(saved, "schemaVersion") != "diagnostic-ua-observation.v1") throw new BridgeException("InvalidRequest", "Expected a retained UA observation report.");
        if (!JsonNode.DeepEquals(saved, Build(D.Required(saved, "snapshotRef", 160))))
            throw new BridgeException("UaDiagnosticReportMismatch", "Report differs from its original capture chain, binding or experiment.");
        byte[] bytes = format == "json" ? source : Encoding.UTF8.GetBytes(Render(saved, reportId, Encoding.UTF8.GetString(source), format == "html"));
        var receipt = store.Publish(bytes, extension);
        receipt["schemaVersion"] = "diagnostic-ua-observation-export.v1"; receipt["reportId"] = reportId;
        receipt["format"] = format; receipt["mediaType"] = media; receipt["sourceEvidenceRevalidated"] = true;
        foreach (string flag in FalseClaims) receipt[flag] = false;
        receipt["limits"] = Limits;
        return receipt;
    }

    private static string Render(JsonObject report, string id, string raw, bool html)
    {
        var output = new StringBuilder();
        string Escape(string text) => html ? WebUtility.HtmlEncode(text) : DiagnosticReportExport.Markdown(text);
        void Line(string text) { output.AppendLine(text); if (output.Length > DiagnosticStore.MaxBytes) throw new BridgeException("ArtifactCapacity", "Readable report exceeds 8 MiB; use exact JSON."); }
        void Heading(int level, string text) => Line(html ? $"<h{level}>{Escape(text)}</h{level}>" : new string('#', level) + " " + Escape(text) + "\n");
        void Paragraph(string text) => Line(html ? "<p>" + Escape(text) + "</p>" : Escape(text) + "\n");
        void Code(string text)
        {
            if (html) Line("<pre tabindex=\"0\"><code>" + WebUtility.HtmlEncode(text) + "</code></pre>");
            else { int longest = 0, current = 0; foreach (char c in text) { current = c == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
                string fence = new('`', Math.Max(3, longest + 1)); Line(fence + "json\n" + text + "\n" + fence + "\n"); }
        }
        if (html) Line("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Retained UA observations</title><style>" + DiagnosticReportExport.Style + "h1,h2,h3,p{overflow-wrap:anywhere}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style></head><body><main>");
        Heading(1, "Retained UA observations"); Paragraph("Report: " + id); Paragraph(Limits);
        Heading(2, "Experiment and capture"); Paragraph("Mode: " + D.Text(report, "acquisitionMode") + "; experiment: " + D.Text(report, "experimentId") + "; snapshot: " + D.Text(report, "snapshotRef"));
        Paragraph(D.Text(report["binding"]!["request"], "reason"));
        Heading(2, "Requested acquisition and server revisions");
        Code(report["captureSource"]!["plan"]!.ToJsonString(BridgeProtocol.Json));
        if (D.Text(report, "acquisitionMode") == "subscription")
            foreach (var row in report["captureSource"]!["rows"]!.AsArray().Where(r => D.Text(r?["event"], "kind") == "created")) Code(row!["event"]!.ToJsonString(BridgeProtocol.Json));
        Heading(2, "Selected signals and observed quality");
        Paragraph("Counts overlap: quality, outcome and missing timestamps describe separate dimensions. First and last observations are recorded endpoints only; no continuous state is inferred.");
        foreach (var signal in report["signals"]!.AsArray()) { Heading(3, D.Text(signal, "signalId")); Code(signal!.ToJsonString(BridgeProtocol.Json)); }
        if (report["captureSource"]!["plan"]?["request"]?["events"] is not null)
            Line(DiagnosticUaTimeline.Render(report["captureSource"]!["rows"]!.AsArray(),html));
        Heading(2, "Timing, gaps and cleanup"); Code(report["captureSource"]!["recordedProgress"]!.ToJsonString(BridgeProtocol.Json)); Code(report["events"]!.ToJsonString(BridgeProtocol.Json));
        Paragraph("Only the selected committed prefix is retained. A running prefix does not establish final termination or remote cleanup. Source/server timestamps and client elapsed times are separate clocks.");
        Heading(2, "Unresolved hypotheses"); Code(report["hypotheses"]!.ToJsonString(BridgeProtocol.Json));
        Heading(2, "Signals without a selected UA observation"); Code(report["unselectedSignalIds"]!.ToJsonString());
        Heading(2, "Original chain hashes");
        foreach (var file in report["captureSource"]!["originalFiles"]!.AsArray()) Paragraph(D.Text(file, "relativePath") + " SHA-256: " + D.Text(file, "sha256"));
        Heading(2, "Complete original report JSON"); Paragraph("Original plan and segment bytes are base64 encoded beside every decoded row. Export reopens and verifies the original files and diagnostic source; embedded copies cannot replace unavailable originals."); Code(raw);
        if (html) Line("</main></body></html>");
        return output.ToString();
    }
}
