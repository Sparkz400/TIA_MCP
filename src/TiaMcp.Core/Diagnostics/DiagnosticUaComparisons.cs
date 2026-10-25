using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core.PhysicalRuntime;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Descriptive comparison of two revalidated prefixes; no acquisition or inferred sample joins.</summary>
public sealed class DiagnosticUaComparisons(string artifactsRoot, DiagnosticUaObservations observations)
{
    public const string Limits = "Caller-selected windows use each capture's Host elapsed clock, in seconds relative to its explicit anchor. Alignment does not synchronize clocks. Original source/server timestamps remain unchanged. Only Good scalar Boolean and exactly representable numeric observations enter statistics; other values remain in the original evidence. No interpolation, rates, causal inference, successful-fix verdict or complete alarm inventory. Event groups compare recorded type/source/severity fields, not alarm identities. Different filters, gaps, overflow, duplicates and incomplete prefixes remain explicit.";

    public JsonObject Compare(string beforeSnapshotRef, string afterSnapshotRef, JsonObject request)
    {
        var report = Build(beforeSnapshotRef, afterSnapshotRef, request);
        var receipt = new DiagnosticStore(artifactsRoot).Save(report);
        receipt["beforeSnapshotRef"] = beforeSnapshotRef; receipt["afterSnapshotRef"] = afterSnapshotRef;
        receipt["sourceEvidenceRevalidated"] = true; receipt["limits"] = Limits;
        return receipt;
    }

    internal JsonObject Build(string beforeSnapshotRef, string afterSnapshotRef, JsonObject request)
    {
        D.Bound(request);
        TraceQualityMeasurements.Fields(request, "reason", "beforeAnchorSeconds", "afterAnchorSeconds", "windowStartSeconds", "windowEndSeconds", "signalMap", "allowChangedSource");
        D.Required(request, "reason", 2000);
        foreach (string key in new[] { "beforeAnchorSeconds", "afterAnchorSeconds", "windowStartSeconds", "windowEndSeconds" })
        {
            decimal time = TraceQualityMeasurements.Time(request[key]);
            if (Math.Abs(time) > 1000000m || time * 1000m != decimal.Truncate(time * 1000m))
                throw new BridgeException("InvalidRequest", "Window values are bounded to +/-1000000 seconds at millisecond resolution, matching Host receipt timing.");
        }
        if (TraceQualityMeasurements.Time(request["windowStartSeconds"]) >= TraceQualityMeasurements.Time(request["windowEndSeconds"]))
            throw new BridgeException("InvalidRequest", "Window start must precede end.");
        if (request.ContainsKey("allowChangedSource") && (request["allowChangedSource"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new BridgeException("InvalidRequest", "allowChangedSource must be Boolean.");
        var maps = TraceQualityMeasurements.Array(request, "signalMap", 64);
        if (maps.Count == 0) throw new BridgeException("InvalidRequest", "Select at least one explicit signal mapping.");
        var before = observations.Build(beforeSnapshotRef); var after = observations.Build(afterSnapshotRef);
        bool changed = D.Text(before["binding"]!["experiment"]!["diagnosticCase"], "reviewId") != D.Text(after["binding"]!["experiment"]!["diagnosticCase"], "reviewId");
        if (changed && request["allowChangedSource"]?.GetValue<bool>() != true)
            throw new BridgeException("ProgramRevisionMismatch", "Different saved reviews require allowChangedSource:true and an explicit reason.");
        var usedBefore = new HashSet<string>(StringComparer.Ordinal); var usedAfter = new HashSet<string>(StringComparer.Ordinal);
        var signals = new JsonArray();
        foreach (var entry in maps)
        {
            var map = entry as JsonObject ?? throw new BridgeException("InvalidRequest", "Signal mapping must be an object.");
            TraceQualityMeasurements.Fields(map, "before", "after");
            string a = D.Required(map, "before", 64), b = D.Required(map, "after", 64);
            var am = Mapping(before, a); var bm = Mapping(after, b);
            if (!usedBefore.Add(a) || !usedAfter.Add(b) || D.Text(am, "unit") != D.Text(bm, "unit") ||
                D.Text(am["declaration"], "datatype") != D.Text(bm["declaration"], "datatype"))
                throw new BridgeException("MeasurementLineageMismatch", "Mappings must be one-to-one with exactly matching saved datatypes and unit labels; no casts or unit conversion.");
            signals.Add(new JsonObject { ["beforeSignalId"] = a, ["afterSignalId"] = b, ["beforeMapping"] = am.DeepClone(), ["afterMapping"] = bm.DeepClone(),
                ["sameNode"] = JsonNode.DeepEquals(am["node"], bm["node"]),
                ["sameStorage"] = D.Text(am["declaration"], "storagePath") == D.Text(bm["declaration"], "storagePath") && JsonNode.DeepEquals(am["declaration"]!["memberPath"], bm["declaration"]!["memberPath"]),
                ["before"] = Signal(before, am, request, "before"), ["after"] = Signal(after, bm, request, "after") });
        }
        var beforeEvents = Events(before, request, "before"); var afterEvents = Events(after, request, "after");
        var report = new JsonObject { ["schemaVersion"] = "diagnostic-ua-comparison.v1", ["beforeSnapshotRef"] = beforeSnapshotRef, ["afterSnapshotRef"] = afterSnapshotRef,
            ["request"] = request.DeepClone(), ["clockBasis"] = "hostElapsedSeconds", ["before"] = before, ["after"] = after,
            ["sourceChanged"] = changed, ["identicalPrefix"] = beforeSnapshotRef == afterSnapshotRef,
            ["sameCapture"] = beforeSnapshotRef.Split(':').Take(2).SequenceEqual(afterSnapshotRef.Split(':').Take(2)),
            ["signals"] = signals, ["beforeWindow"] = Window(before, request, "before"), ["afterWindow"] = Window(after, request, "after"),
            ["beforeEvents"] = beforeEvents, ["afterEvents"] = afterEvents,
            ["sameEventSelection"] = JsonNode.DeepEquals(beforeEvents["selection"], afterEvents["selection"]),
            ["eventGroups"] = EventGroups(beforeEvents, afterEvents), ["verdict"] = "unresolved", ["resampled"] = false,
            ["sourceEvidenceRevalidated"] = true, ["limits"] = Limits };
        foreach (string claim in DiagnosticUaObservations.FalseClaims) report[claim] = false;
        if (Encoding.UTF8.GetByteCount(report.ToJsonString()) > DiagnosticStore.MaxBytes)
            throw new BridgeException("ArtifactCapacity", "Complete comparison exceeds 8 MiB; no source evidence was truncated.");
        return report;
    }

    private static JsonNode Mapping(JsonObject report, string id) => D.Rows(report["binding"]!["mappings"]).SingleOrDefault(m => D.Text(m, "signalId") == id)
        ?? throw new BridgeException("MeasurementLineageMismatch", "Mapping must select an exact prebound signal on each side.");
    private static bool Selected(JsonNode row, JsonObject request, string side)
    {
        decimal ms = TraceQualityMeasurements.Time(row["elapsedMilliseconds"]), seconds = ms / 1000m;
        if (seconds * 1000m != ms) throw new BridgeException("MeasurementFormatUnsupported", "Host time cannot be converted exactly.");
        decimal relative = TraceNumber.SubtractTime(seconds, TraceQualityMeasurements.Time(request[side + "AnchorSeconds"]));
        return relative >= TraceQualityMeasurements.Time(request["windowStartSeconds"]) && relative <= TraceQualityMeasurements.Time(request["windowEndSeconds"]);
    }
    private static JsonArray Rows(JsonObject report) => report["captureSource"]!["rows"]!.AsArray();
    private static JsonObject Window(JsonObject report, JsonObject request, string side)
    {
        var rows = Rows(report); var selected = rows.Where(r => Selected(r!, request, side)).ToArray();
        decimal start = TraceQualityMeasurements.Time(request[side + "AnchorSeconds"]) + TraceQualityMeasurements.Time(request["windowStartSeconds"]);
        decimal end = TraceQualityMeasurements.Time(request[side + "AnchorSeconds"]) + TraceQualityMeasurements.Time(request["windowEndSeconds"]);
        // Addition may round at decimal limits; retain the same exactness requirement as subtraction.
        if (TraceNumber.SubtractTime(start, TraceQualityMeasurements.Time(request[side + "AnchorSeconds"])) != TraceQualityMeasurements.Time(request["windowStartSeconds"]) ||
            TraceNumber.SubtractTime(end, TraceQualityMeasurements.Time(request[side + "AnchorSeconds"])) != TraceQualityMeasurements.Time(request["windowEndSeconds"]))
            throw new BridgeException("MeasurementFormatUnsupported", "Requested absolute window cannot be represented exactly.");
        return new JsonObject { ["selectedRowSequences"] = new JsonArray(selected.Select(r => r!["sequence"]!.DeepClone()).ToArray()),
            ["excludedRows"] = rows.Count - selected.Length, ["firstSelectedElapsedMilliseconds"] = selected.FirstOrDefault()?["elapsedMilliseconds"]?.DeepClone(),
            ["lastSelectedElapsedMilliseconds"] = selected.LastOrDefault()?["elapsedMilliseconds"]?.DeepClone(),
            ["recordedRowsSpanRequestedWindow"] = rows.Count > 0 && TraceQualityMeasurements.Time(rows[0]!["elapsedMilliseconds"]) / 1000m <= start && TraceQualityMeasurements.Time(rows[^1]!["elapsedMilliseconds"]) / 1000m >= end,
            ["fullPrefixEvidence"] = report["events"]!.DeepClone(), ["recordedProgress"] = report["captureSource"]!["recordedProgress"]!.DeepClone(),
            ["uncommittedDataUnknown"] = report["captureSource"]!["uncommittedDataUnknown"]!.DeepClone(), ["completenessVerified"] = false };
    }
    private static JsonObject Signal(JsonObject report, JsonNode mapping, JsonObject request, string side)
    {
        var values = new List<JsonNode>(); var selectedRows = new JsonArray(); int hostErrors = 0;
        var node = mapping["node"]!.Deserialize<OpcUaRuntimeNode>(BridgeProtocol.Json)!;
        foreach (var row in Rows(report).Where(r => Selected(r!, request, side)))
        {
            if (D.Text(report, "acquisitionMode") == "polling")
            {
                if (D.Text(row, "kind") != "read") continue;
                if (D.Text(row, "selectionId") != OpcUaCaptureContracts.SelectionId(node)) continue;
                selectedRows.Add(row!["sequence"]!.DeepClone());
                if (row["errorCode"] is not null) { hostErrors++; continue; }
                values.Add(JsonNode.Parse(D.Text(row, "readResultJson"))!["items"]![0]!);
            }
            else if (row!["event"]!["duplicate"]!.GetValue<bool>() == false)
                foreach (var n in D.Rows(row["event"]!["notifications"]).Where(n => JsonNode.DeepEquals(n!["value"]!["node"], mapping["node"])))
                { selectedRows.Add(row["sequence"]!.DeepClone()); values.Add(n!["value"]!); }
        }
        int Count(Func<JsonNode, bool> f) => values.Count(f);
        bool Good(JsonNode v) => D.Text(v, "quality") == "Good" && D.Text(v, "outcome") == "value" && D.Text(v, "encoding") != "null";
        var ranges = new JsonArray(); int unrepresentable = 0;
        foreach (var group in values.Where(v => Good(v) && v["valueRank"]?.GetValue<int>() == -1 && D.Text(v, "encoding") is "decimalInteger" or "roundTripFloat" or "roundTripDouble")
            .GroupBy(v => new JsonObject { ["builtInType"] = v["builtInType"]?.DeepClone(), ["dataType"] = v["dataType"]?.DeepClone(), ["encoding"] = v["encoding"]?.DeepClone() }.ToJsonString(), StringComparer.Ordinal))
        {
            var numbers = new List<(decimal Number, string Text)>();
            foreach (var value in group)
                try { string text = D.Text(value, "value"); numbers.Add((TraceNumber.Parse(text), text)); }
                catch (BridgeException e) when (e.Code == "MeasurementFormatUnsupported") { unrepresentable++; }
            ranges.Add(new JsonObject { ["type"] = JsonNode.Parse(group.Key), ["count"] = numbers.Count,
                ["minimum"] = numbers.Count == 0 ? null : numbers.OrderBy(n => n.Number).First().Text,
                ["maximum"] = numbers.Count == 0 ? null : numbers.OrderByDescending(n => n.Number).First().Text });
        }
        return new JsonObject { ["selectedRowSequences"] = selectedRows, ["observations"] = values.Count, ["hostReadErrors"] = hostErrors,
            ["goodValues"] = Count(Good), ["uncertain"] = Count(v => D.Text(v, "quality") == "Uncertain"), ["bad"] = Count(v => D.Text(v, "quality") == "Bad"),
            ["nullValues"] = Count(v => D.Text(v, "encoding") == "null"), ["unsupported"] = Count(v => D.Text(v, "outcome") == "unsupported"),
            ["metadataErrors"] = Count(v => D.Text(v, "outcome") == "metadataError"), ["nonScalarValues"] = Count(v => v["valueRank"] is not null && v["valueRank"]!.GetValue<int>() >= 0),
            ["trueValues"] = Count(v => Good(v) && D.Text(v, "encoding") == "boolean" && v["value"]!.GetValue<bool>()),
            ["falseValues"] = Count(v => Good(v) && D.Text(v, "encoding") == "boolean" && !v["value"]!.GetValue<bool>()),
            ["numericRanges"] = ranges, ["numericNotRepresentable"] = unrepresentable,
            ["firstObservation"] = values.FirstOrDefault()?.DeepClone(), ["lastObservation"] = values.LastOrDefault()?.DeepClone() };
    }
    private static JsonObject Events(JsonObject report, JsonObject request, string side)
    {
        var selection = report["captureSource"]!["plan"]?["request"]?["events"];
        var groups = new Dictionary<string, int>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
        int count = 0, duplicate = 0, repeated = 0, overflow = 0;
        foreach (var row in Rows(report).Where(r => Selected(r!, request, side)))
            foreach (var e in D.Rows(row!["event"]?["events"]))
            {
                if (row["event"]!["duplicate"]!.GetValue<bool>()) { duplicate++; continue; }
                var fields = e!["fields"]!.AsArray(); count++;
                if (!ids.Add(D.Text(fields[0], "text"))) repeated++;
                if (e["overflow"]!.GetValue<bool>()) overflow++;
                var key = new JsonArray(new[] { 1, 2, 3, 7 }.Select(i => fields[i]!.DeepClone()).ToArray()).ToJsonString();
                groups[key] = groups.GetValueOrDefault(key) + 1;
            }
        return new JsonObject { ["selection"] = selection?.DeepClone(), ["monitoringRequested"] = selection is not null,
            ["notifications"] = count, ["duplicateNotifications"] = duplicate, ["repeatedEventIds"] = repeated, ["overflowNotifications"] = overflow,
            ["groups"] = new JsonArray(groups.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["fields"] = JsonNode.Parse(p.Key), ["count"] = p.Value }).ToArray()) };
    }
    private static JsonArray EventGroups(JsonObject before, JsonObject after)
    {
        var a = D.Rows(before["groups"]).ToDictionary(g => g!["fields"]!.ToJsonString(), g => g!["count"]!.GetValue<int>(), StringComparer.Ordinal);
        var b = D.Rows(after["groups"]).ToDictionary(g => g!["fields"]!.ToJsonString(), g => g!["count"]!.GetValue<int>(), StringComparer.Ordinal);
        return new JsonArray(a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(key => (JsonNode)new JsonObject {
            ["fields"] = JsonNode.Parse(key), ["beforeCount"] = a.GetValueOrDefault(key), ["afterCount"] = b.GetValueOrDefault(key),
            ["countDifference"] = b.GetValueOrDefault(key) - a.GetValueOrDefault(key) }).ToArray());
    }

    public JsonObject Export(string reportId, string format)
    {
        var (extension, media) = format switch { "json" => (".json", "application/json"), "markdown" => (".md", "text/markdown"), "html" => (".html", "text/html"),
            _ => throw new BridgeException("InvalidRequest", "format must be json, markdown or html.") };
        var store = new DiagnosticStore(artifactsRoot); byte[] raw = store.ReadBytes(reportId); var saved = JsonNode.Parse(raw)!.AsObject();
        if (D.Text(saved, "schemaVersion") != "diagnostic-ua-comparison.v1") throw new BridgeException("InvalidRequest", "Expected a retained UA comparison.");
        if (!JsonNode.DeepEquals(saved, Build(D.Required(saved, "beforeSnapshotRef", 160), D.Required(saved, "afterSnapshotRef", 160), TraceQualityMeasurements.Object(saved, "request"))))
            throw new BridgeException("UaDiagnosticReportMismatch", "Comparison differs from original captures, bindings or experiment evidence.");
        var receipt = store.Publish(format == "json" ? raw : Encoding.UTF8.GetBytes(Render(saved, reportId, Encoding.UTF8.GetString(raw), format == "html")), extension);
        receipt["schemaVersion"] = "diagnostic-ua-comparison-export.v1"; receipt["reportId"] = reportId; receipt["format"] = format; receipt["mediaType"] = media;
        receipt["sourceEvidenceRevalidated"] = true; receipt["limits"] = Limits;
        foreach (string flag in DiagnosticUaObservations.FalseClaims) receipt[flag] = false;
        return receipt;
    }
    private static string Render(JsonObject report, string id, string raw, bool html)
    {
        string E(string text) => html ? WebUtility.HtmlEncode(text) : DiagnosticReportExport.Markdown(text);
        var output = new StringBuilder();
        void Section(string title, string text) { output.AppendLine(html ? "<h2>" + E(title) + "</h2><pre tabindex=\"0\">" + E(text) + "</pre>" : "## " + E(title) + "\n\n" + E(text) + "\n"); }
        if (html) output.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>UA capture comparison</title><style>" + DiagnosticReportExport.Style + "pre{white-space:pre-wrap;overflow-wrap:anywhere}h1,h2,p{overflow-wrap:anywhere}</style></head><body><main><h1>Before / after UA captures</h1>");
        else output.AppendLine("# Before / after UA captures\n");
        Section("Scope and limitations", Limits); Section("Report", id); Section("Window and explicit mappings", report["request"]!.ToJsonString());
        foreach (var signal in report["signals"]!.AsArray()) Section(D.Text(signal, "beforeSignalId") + " → " + D.Text(signal, "afterSignalId"), signal!.ToJsonString());
        Section("Event group counts", report["eventGroups"]!.ToJsonString());
        foreach (string side in new[] { "before", "after" }) { Section(side + " window and full-prefix gaps", report[side + "Window"]!.ToJsonString()); Section(side + " event selection", report[side + "Events"]!.ToJsonString()); }
        if (html) output.AppendLine("<h2>Complete original comparison and both retained captures</h2><pre tabindex=\"0\"><code>" + WebUtility.HtmlEncode(raw) + "</code></pre>");
        else
        {
            int longest = 0, current = 0; foreach (char c in raw) { current = c == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
            string fence = new('`', Math.Max(3, longest + 1));
            output.AppendLine("## Complete original comparison and both retained captures\n\n" + fence + "json\n" + raw + "\n" + fence);
        }
        if (html) output.AppendLine("</main></body></html>");
        return output.ToString();
    }
}
