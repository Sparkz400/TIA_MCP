using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Receipt-ordered presentation of validated original subscription rows; never a source-time join.</summary>
public static class DiagnosticUaTimeline
{
    public const string Limits = "Host receipt order only. Event Time, server ReceiveTime, PublishTime and Host observedUtc are separate clocks. Repeated EventIds and duplicate envelopes remain visible. Gaps and queue overflow do not establish how many events were lost. No interpolation, clock alignment, causality, complete active-alarm inventory, acknowledgement or reset is provided.";

    public static string Render(JsonArray rows, bool html)
    {
        if (rows.Count > 10000) throw new BridgeException("ArtifactCapacity", "Timeline exceeds 10000 original rows; use JSON or CSV.");
        var output = new StringBuilder(); var ids = new HashSet<string>(StringComparer.Ordinal); int bytes = 0;
        string Escape(string text) => html ? WebUtility.HtmlEncode(text) : DiagnosticReportExport.Markdown(text);
        void Line(string value)
        {
            bytes += Encoding.UTF8.GetByteCount(value) + 2;
            if (bytes > DiagnosticStore.MaxBytes - 16384)
                throw new BridgeException("ArtifactCapacity", "Readable timeline exceeds 8 MiB; use JSON or CSV.");
            output.AppendLine(value);
        }
        Line(html ? "<h2>Event and signal timeline</h2><p>" + Escape(Limits) + "</p><div class=\"table-scroll\" tabindex=\"0\"><table><thead><tr><th scope=\"col\">Row / Host time</th><th scope=\"col\">Observation</th><th scope=\"col\">Original details</th></tr></thead><tbody>" : "## Event and signal timeline\n\n" + Escape(Limits) + "\n");
        foreach (var row in rows)
        {
            var e = row!["event"]!;
            var details = new List<string>();
            foreach (var notification in D.Rows(e["events"]))
            {
                var fields = notification!["fields"]!.AsArray();
                string id = D.Text(fields[0], "text"); bool repeated = !ids.Add(id);
                details.Add("EventId: " + id + (repeated ? " (repeated EventId)" : "") + (notification["overflow"]!.GetValue<bool>() ? " (EVENT QUEUE OVERFLOW)" : ""));
                foreach (var field in fields.Skip(1))
                    details.Add(D.Text(field, "name") + ": " + (D.Text(field, "kind") == "value" ?
                        field!["node"]?.ToJsonString() ?? (field["text"] is null ? "(null text)" : D.Text(field, "text")) : D.Text(field, "kind") + " " + D.Text(field, "statusCode")) +
                        (field!["locale"] is null ? "" : " [locale: " + D.Text(field, "locale") + "]"));
            }
            foreach (var n in D.Rows(e["notifications"])) details.Add("Selected signal: " + n!["value"]!.ToJsonString());
            if (details.Count == 0) details.Add(e.ToJsonString());
            if (e["publishTime"] is not null) details.Add("PublishTime: " + D.Text(e, "publishTime") + " | message sequence: " + e["sequenceNumber"]);
            string time = "Row " + row["sequence"] + " | observedUtc: " + D.Text(row, "observedUtc") + " | elapsed ms: " + row["elapsedMilliseconds"];
            string kind = D.Text(e, "kind") + (e["duplicate"]!.GetValue<bool>() ? " (duplicate envelope)" : "");
            string detail = string.Join('\n', details);
            if (html) Line("<tr><td>" + Escape(time) + "</td><td>" + Escape(kind) + "</td><td><pre tabindex=\"0\">" + Escape(detail) + "</pre></td></tr>");
            else Line("### " + Escape(time) + "\n\n" + Escape(kind) + "\n\n" + Escape(detail) + "\n");
        }
        if (html) Line("</tbody></table></div>");
        return output.ToString();
    }

    public static string Export(string snapshotRef, JsonNode progress, JsonArray rows, bool html)
    {
        string body = Render(rows, html);
        if (!html) return "# Retained UA timeline\n\nSnapshot: " + DiagnosticReportExport.Markdown(snapshotRef) + "\n\nRecorded progress: " + DiagnosticReportExport.Markdown(progress.ToJsonString()) + "\n\n" + body;
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>Retained UA timeline</title><style>" + DiagnosticReportExport.Style +
            "td,pre{overflow-wrap:anywhere}pre{white-space:pre-wrap}</style></head><body><main><h1>Retained UA timeline</h1><p>Snapshot: " + WebUtility.HtmlEncode(snapshotRef) + "</p><p>The companion JSON retains the exact plan, server revisions, progress and original rows. Native qualification remains unverified.</p><pre>" + WebUtility.HtmlEncode(progress.ToJsonString()) + "</pre>" + body + "</main></body></html>";
    }
}
