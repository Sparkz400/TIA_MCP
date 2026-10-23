using System.Globalization;
using System.Net;
using System.Text;

namespace TiaMcp.Graph;

internal static class GraphHtml
{
    private const int DiagramNodeLimit = 500;
    public static string Render(GraphDocument document)
    {
        var report = document.Inspection;
        var html = new StringBuilder("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
            <title>GRAPH sequence review</title><style>
            :root{font:15px/1.5 system-ui,sans-serif;color:#172c3c;background:#edf2f6}*{box-sizing:border-box}
            body{margin:0}header{background:#102b40;color:#fff;padding:32px max(24px,calc((100vw - 1400px)/2))}
            header small{color:#91c9f7;letter-spacing:.14em}h1{font-size:34px;margin:8px 0}h2{margin:0 0 16px}h3{margin:20px 0 8px}
            main{max-width:1448px;margin:auto;padding:24px}section,.card{background:white;border:1px solid #cfdae4;border-radius:10px;margin-bottom:24px;padding:24px}
            .metrics{display:flex;gap:32px;flex-wrap:wrap;margin:20px 0}.metrics b{font-size:26px;display:block}
            a{color:#1462a0}header a{color:#bfe2ff}nav{display:flex;gap:16px;flex-wrap:wrap}.badge{background:#e5eff7;padding:3px 9px;border-radius:5px}
            .notice{border-left:4px solid #e7a436;padding:12px 16px;background:#fff6e3}.error{color:#a0232a}.muted{color:#536777}
            table{border-collapse:collapse;width:100%;margin:12px 0}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #dce4eb;padding:9px}th{background:#f3f7fa}
            .scroll{overflow:auto}pre{background:#f3f6f9;border:1px solid #dce4eb;border-radius:6px;padding:12px;white-space:pre-wrap;overflow-wrap:anywhere;font:12px/1.6 ui-monospace,monospace}
            code{overflow-wrap:anywhere}summary{cursor:pointer;color:#1462a0;padding:6px 0}svg{display:block;margin:auto}svg text{font-family:system-ui,sans-serif;font-size:13px;fill:#17364d}
            svg .edge{fill:none;stroke:#8195a6;stroke-width:1.6}svg .jump{stroke:#a85b18;stroke-dasharray:6 4}svg .step{fill:#edf7ff;stroke:#29628d;stroke-width:2}
            svg .transition{fill:#fff5e3;stroke:#a1762b;stroke-width:2}svg .branch{fill:#f2edff;stroke:#75629d;stroke-width:2}
            :target{scroll-margin-top:16px;outline:2px solid #3c89c1}footer{font-size:13px;color:#536777;padding:16px 0}
            @media(max-width:650px){main{padding:12px}section{padding:16px}header{padding:24px}h1{font-size:28px}}
            @media print{body{background:white}header{color:black;background:white}details{break-inside:avoid}section{break-before:auto}.scroll{overflow:visible}}
            </style></head><body><header><small>OFFLINE DOCUMENT REVIEW</small><h1>GRAPH sequence review</h1>
            <p>Sequence structure, conditions and actions from a retained XML document.</p>
            """);
        var sequences = report.Graphs.SelectMany(g => g.Sequences).ToArray();
        html.Append("<div class=metrics>");
        Metric("Sequences", sequences.Length); Metric("Steps", sequences.Sum(s => s.Steps.Count));
        Metric("Transitions", sequences.Sum(s => s.Transitions.Count)); Metric("Branches", sequences.Sum(s => s.Branches.Count));
        html.Append("</div><nav>");
        foreach (var chart in report.Graphs)
            foreach (var sequence in chart.Sequences)
                html.Append($"<a href=\"#g{chart.Ordinal}s{sequence.Ordinal}\">Graph {chart.Ordinal} · Sequence {sequence.Ordinal}</a>");
        html.Append("<a href=\"#diagnostics\">Diagnostics</a><a href=\"#source\">Retained source</a></nav></header><main>");
        html.Append("<p class=notice>Structural document inspection. Embedded condition and action XML is retained, not evaluated. This report does not certify native import acceptance, TIA roundtrip or runtime behavior for the input document.</p>");
        if (report.HasErrors) html.Append("<p class=\"notice error\">Structural errors were found. Review diagnostics before using the sequence view.</p>");
        foreach (var chart in report.Graphs)
        {
            foreach (var sequence in chart.Sequences)
            {
                string id = $"g{chart.Ordinal}s{sequence.Ordinal}";
                html.Append($"<section id=\"{id}\"><h2>Graph {chart.Ordinal} / Sequence {sequence.Ordinal}</h2>");
                if (sequence.Title is not null) html.Append($"<p>{H(sequence.Title)}</p>");
                html.Append("<p class=muted>Arrows follow exported connections. Dashed lines are jumps. Branch ports and connection order are listed below; this layout is not the native editor layout.</p>");
                Diagram(html, chart, sequence, id);
                html.Append("<details><summary>Connections and branch ports</summary><div class=scroll><table><thead><tr><th>#</th><th>From</th><th>To</th><th>Link</th></tr></thead><tbody>");
                foreach (var c in sequence.Connections)
                    html.Append($"<tr><td>{c.Ordinal}</td><td>{H(Endpoint(c.From))}</td><td>{H(Endpoint(c.To))}</td><td>{H(c.LinkType)}</td></tr>");
                html.Append("</tbody></table></div></details><h3>Steps and actions</h3>");
                for (int i = 0; i < sequence.Steps.Count; i++)
                {
                    var step = sequence.Steps[i];
                    html.Append($"<article id=\"{id}-step-{i}\"><h3>Step {step.Number}: {H(step.Name)} {(step.Init == true ? "<span class=badge>Initial</span>" : "")}</h3>");
                    html.Append($"<p class=muted>Maximum step time: {H(step.MaximumStepTime ?? "unspecified")} · Warning time: {H(step.WarningTime ?? "unspecified")}</p>");
                    foreach (var action in step.Actions)
                        Raw($"Action {action.Ordinal} · Qualifier {action.Qualifier ?? "unspecified"} · Event {action.Event ?? "unspecified"} · Interlock {action.Interlock?.ToString() ?? "unspecified"}", action.Content.Xml);
                    foreach (var condition in step.Interlocks) Raw($"Interlock · {condition.ProgrammingLanguage}", condition.Content.Xml);
                    foreach (var condition in step.Supervisions) Raw($"Supervision · {condition.ProgrammingLanguage}", condition.Content.Xml);
                    Raw("Complete step XML", step.Content.Xml);
                    html.Append("</article>");
                }
                html.Append("<h3>Transitions</h3>");
                for (int i = 0; i < sequence.Transitions.Count; i++)
                {
                    var transition = sequence.Transitions[i];
                    html.Append($"<article id=\"{id}-transition-{i}\"><h3>Transition {transition.Number}: {H(transition.Name)}</h3><p>{H(transition.ProgrammingLanguage)}</p>");
                    Raw("Condition and transition XML", transition.Content.Xml); html.Append("</article>");
                }
                if (sequence.Branches.Count > 0) html.Append("<h3>Branches</h3>");
                for (int i = 0; i < sequence.Branches.Count; i++)
                {
                    var branch = sequence.Branches[i];
                    html.Append($"<article id=\"{id}-branch-{i}\"><p><b>Branch {branch.Number}</b> · {H(branch.Type)} · Cardinality {branch.Cardinality}</p>");
                    Raw("Branch XML", branch.Content.Xml); html.Append("</article>");
                }
                html.Append("</section>");
            }
            html.Append($"<section><h2>Graph {chart.Ordinal} operations and alarm settings</h2>");
            foreach (var f in chart.PreOperations) Raw("Pre-operations", f.Xml);
            foreach (var f in chart.PostOperations) Raw("Post-operations", f.Xml);
            foreach (var f in chart.AlarmSettings) Raw("Alarm settings (opaque)", f.Xml);
            html.Append("</section>");
        }
        html.Append($"<section id=diagnostics><h2>Diagnostics ({report.Diagnostics.Count})</h2>");
        foreach (var d in report.Diagnostics)
            html.Append($"<details><summary class=\"{(d.Severity == "error" ? "error" : "muted")}\">{H(d.Severity)} · {H(d.Code)}</summary><p>{H(d.Message)}</p><code>{H(d.Path)}</code></details>");
        html.Append("<h3>Unsupported and opaque content</h3>");
        foreach (var u in report.Unsupported) Raw($"{u.Code} · {u.Path}", u.Xml);
        html.Append("</section><section id=source><h2>Retained source</h2>");
        html.Append($"<p>{report.SourceBytes} bytes · SHA-256 <code>{H(report.SourceSha256)}</code></p>");
        html.Append($"<p><a download=\"graph-original.xml\" href=\"data:application/xml;base64,{report.OriginalSourceBase64}\">Download byte-identical source XML</a></p>");
        html.Append("<p class=muted>Inspection fragments may use normalized XML serialization. The download preserves the original encoding, declarations, comments, whitespace and unknown content.</p></section>");
        html.Append($"<footer>{H(report.Profile)} · Offline review only · Full schema validation: false · Native qualified: false</footer></main></body></html>");
        var result = html.ToString();
        if (Encoding.UTF8.GetByteCount(result) > GraphDocument.MaxReportBytes) throw new GraphException("ReportSize", "HTML exceeds the output byte limit.");
        return result;
        void Metric(string label, int count) => html.Append($"<div><b>{count}</b>{label}</div>");
        void Raw(string label, string xml) => html.Append($"<details><summary>{H(label)}</summary><pre>{H(xml)}</pre></details>");
    }

    private sealed record Node(string Key, string Label, string Kind, string Anchor, bool Initial = false);
    private static void Diagram(StringBuilder html, GraphChart chart, GraphSequence sequence, string id)
    {
        var nodes = sequence.Steps.Select((s, i) => new Node($"Step:{s.Number}", $"S{s.Number} · {s.Name}", "step", $"{id}-step-{i}", s.Init == true))
            .Concat(sequence.Transitions.Select((t, i) => new Node($"Transition:{t.Number}", $"T{t.Number} · {t.Name}", "transition", $"{id}-transition-{i}")))
            .Concat(sequence.Branches.Select((b, i) => new Node($"Branch:{b.Number}", $"B{b.Number} · {b.Type} ({b.Cardinality})", "branch", $"{id}-branch-{i}"))).ToList();
        // EndConnection is a sentinel, not a numbered identity shared by unrelated connections.
        foreach (var c in sequence.Connections)
        {
            if (c.From?.Kind == "EndConnection") nodes.Add(new($"EndConnection:{c.Ordinal}:from", "End connection", "branch", id));
            if (c.To?.Kind == "EndConnection") nodes.Add(new($"EndConnection:{c.Ordinal}:to", "End connection", "branch", id));
            if (c.LinkType == "Jump" && c.To?.Kind == "Step" && !nodes.Any(n => n.Key == $"Step:{c.To.Number}"))
            {
                var targets = chart.Sequences.SelectMany(s => s.Steps.Select((step, i) => (Sequence: s.Ordinal, Step: step, Index: i)))
                    .Where(t => t.Step.Number == c.To.Number && t.Step.IsMissing != true).ToArray();
                if (targets.Length == 1)
                {
                    var target = targets[0];
                    nodes.Add(new($"Step:{c.To.Number}", $"Sequence {target.Sequence} · S{c.To.Number}", "branch",
                        $"g{chart.Ordinal}s{target.Sequence}-step-{target.Index}"));
                }
            }
        }
        if (nodes.Count > DiagramNodeLimit || nodes.GroupBy(n => n.Key).Any(g => g.Count() > 1)
            || chart.Sequences.SelectMany(s => s.Steps).GroupBy(s => s.Number).Any(g => g.Count() > 1)
            || chart.Sequences.SelectMany(s => s.Transitions).GroupBy(t => t.Number).Any(g => g.Count() > 1)
            || sequence.Steps.Any(s => s.Number is null) || sequence.Transitions.Any(t => t.Number is null) || sequence.Branches.Any(b => b.Number is null))
        {
            html.Append($"<p class=notice>Diagram omitted: ambiguous identities or more than {DiagramNodeLimit} nodes. All nodes and connections remain available below.</p>");
            return;
        }
        if (nodes.Count == 0) return;
        var known = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        string Key(GraphConnection c, bool from)
        {
            var e = from ? c.From : c.To;
            return e is null ? "invalid" : e.Kind == "EndConnection" ? $"EndConnection:{c.Ordinal}:{(from ? "from" : "to")}" : $"{e.Kind}:{e.Number}";
        }
        var edges = sequence.Connections.Where(c => known.Contains(Key(c, true)) && known.Contains(Key(c, false))).ToArray();
        int omitted = sequence.Connections.Count - edges.Length;
        if (omitted > 0) html.Append($"<p class=notice>{omitted} connection(s) cannot be drawn because an endpoint is invalid or unresolved. See connections and diagnostics.</p>");
        var outgoing = edges.ToLookup(c => Key(c, true));
        var incoming = edges.Select(c => Key(c, false)).ToHashSet();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var n in nodes.Where(n => n.Initial || !incoming.Contains(n.Key))) { rank[n.Key] = 0; queue.Enqueue(n.Key); }
        void Walk()
        {
            while (queue.TryDequeue(out var key))
                foreach (var edge in outgoing[key])
                    if (rank.TryAdd(Key(edge, false), rank[key] + 1)) queue.Enqueue(Key(edge, false));
        }
        Walk();
        foreach (var n in nodes)
            if (!rank.ContainsKey(n.Key)) { rank[n.Key] = rank.Count == 0 ? 0 : rank.Values.Max() + 1; queue.Enqueue(n.Key); Walk(); }
        var groups = nodes.GroupBy(n => rank[n.Key]).ToArray();
        int width = Math.Max(800, groups.Max(g => g.Count()) * 240 + 120);
        int height = (rank.Values.Max() + 1) * 150 + 100;
        var positions = new Dictionary<string, (int X, int Y)>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            int i = 0;
            foreach (var n in group) positions[n.Key] = ((++i * width) / (group.Count() + 1), 65 + group.Key * 150);
        }
        html.Append($"<div class=scroll><svg xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"Sequence connection diagram\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\"><defs><marker id=\"{id}-arrow\" markerWidth=\"8\" markerHeight=\"8\" refX=\"7\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L0,6 L7,3 z\" fill=\"#8195a6\"/></marker></defs>");
        foreach (var c in edges)
        {
            var a = positions[Key(c, true)]; var b = positions[Key(c, false)];
            int outsideLane = positions.Values.Max(p => p.X) + 130 + c.Ordinal % 5 * 8;
            string path = b.Y == a.Y + 150 && c.LinkType != "Jump"
                ? $"M {a.X} {a.Y + 27} C {a.X} {a.Y + 80}, {b.X} {b.Y - 80}, {b.X} {b.Y - 30}"
                : $"M {a.X} {a.Y + 27} V {a.Y + 50} H {outsideLane} V {b.Y - 50} H {b.X} V {b.Y - 30}";
            html.Append($"<path class=\"edge {(c.LinkType == "Jump" ? "jump" : "")}\" d=\"{path}\" marker-end=\"url(#{id}-arrow)\"><title>Connection {c.Ordinal}: {H(Endpoint(c.From))} → {H(Endpoint(c.To))}, {H(c.LinkType)}</title></path>");
        }
        foreach (var n in nodes)
        {
            var p = positions[n.Key];
            html.Append($"<a href=\"#{n.Anchor}\"><rect class=\"{n.Kind}\" x=\"{p.X - 95}\" y=\"{p.Y - 27}\" width=\"190\" height=\"54\" rx=\"{(n.Kind == "transition" ? 2 : 7)}\"/>");
            if (n.Initial) html.Append($"<rect x=\"{p.X - 89}\" y=\"{p.Y - 21}\" width=\"178\" height=\"42\" rx=\"4\" fill=\"none\" stroke=\"#29628d\"/>");
            html.Append($"<title>{H(n.Label)}</title><text x=\"{p.X}\" y=\"{p.Y + 5}\" text-anchor=\"middle\">{H(n.Label.Length > 24 ? n.Label[..23] + "…" : n.Label)}</text></a>");
        }
        html.Append("</svg></div>");
    }

    private static string Endpoint(GraphEndpoint? e) => e is null ? "Invalid endpoint" : e.Kind + (e.Number is int n ? " " + n.ToString(CultureInfo.InvariantCulture) : "")
        + (e.In is int i ? $" · In={i}" : "") + (e.Out is int o ? $" · Out={o}" : "");
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
}
