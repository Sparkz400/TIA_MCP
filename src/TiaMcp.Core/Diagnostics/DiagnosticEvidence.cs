using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Conservative readers for saved, exported interfaces and explicit CallInfo XML.</summary>
internal sealed class DiagnosticEvidence
{
    internal readonly Dictionary<string, JsonObject> Nodes;
    internal readonly Dictionary<string, JsonObject> Documents;
    internal readonly JsonObject Review;
    internal static string QualifiedName(JsonObject node) => D.Text(node["metadata"], "namespace") is string ns && ns.Length > 0 ? ns + "." + D.Text(node, "name") : D.Text(node, "name");
    private readonly Dictionary<string, XDocument> xml = new(StringComparer.Ordinal);
    private int instanceVisits;
    private bool traversalTruncated;
    private readonly JsonArray unresolvedInstances = new();
    internal JsonObject Traversal() => new() { ["visited"] = instanceVisits, ["maxVisited"] = 1000,
        ["maxDepth"] = 8, ["maxInstances"] = 200, ["truncated"] = traversalTruncated, ["complete"] = false,
        ["unresolvedAlternatives"] = unresolvedInstances.DeepClone() };
    private void Unresolved(string db, IEnumerable<string> members, string reason, IEnumerable<JsonObject>? alternatives = null)
    {
        if (unresolvedInstances.Count == 200) { traversalTruncated = true; return; }
        unresolvedInstances.Add(new JsonObject { ["dbPath"] = db, ["memberPath"] = D.Strings(members), ["reason"] = reason,
            ["alternatives"] = new JsonArray((alternatives ?? []).Take(20).Select(a => (JsonNode?)new JsonObject { ["objectPath"] = a["objectPath"]?.DeepClone(),
                ["scopePath"] = a["scopePath"]?.DeepClone(), ["namespace"] = a["metadata"]?["namespace"]?.DeepClone() }).ToArray()) });
    }
    internal DiagnosticEvidence(JsonObject review)
    {
        Review = review;
        Nodes = D.Rows(review["model"]?["nodes"]).OfType<JsonObject>()
            .Where(n => D.Text(n, "objectPath").Length > 0).ToDictionary(n => D.Text(n, "objectPath"), StringComparer.Ordinal);
        Documents = D.Rows(review["documents"]).OfType<JsonObject>().ToDictionary(n => D.Text(n, "objectPath"), StringComparer.Ordinal);
    }
    internal XDocument? Xml(string path)
    {
        if (xml.TryGetValue(path, out var cached)) return cached;
        if (!Documents.TryGetValue(path, out var doc) || D.Text(doc, "status") != "exported"
            || Nodes[path]["metadata"]?["isKnowHowProtected"]?.GetValue<bool>() == true) return null;
        using var reader = XmlReader.Create(new StringReader(D.Text(doc, "xml")), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        return xml[path] = XDocument.Load(reader, LoadOptions.SetLineInfo);
    }
    internal static IEnumerable<XElement> Children(XContainer element, string name) => element.Elements().Where(e => e.Name.LocalName == name);
    internal static XElement? Block(XDocument? doc) => doc?.Root?.Elements().SingleOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
    internal static IEnumerable<XElement> Members(XElement parent) => Children(parent, "Sections").SelectMany(s => Children(s, "Section")).SelectMany(s => Children(s, "Member"));
    internal XElement? Member(string dbPath, string[] path)
    {
        var block = Block(Xml(dbPath));
        if (block?.Name.LocalName is not ("SW.Blocks.InstanceDB" or "SW.Blocks.GlobalDB")) return null;
        return Declaration(dbPath, path);
    }
    internal XElement? Declaration(string objectPath, string[] path)
    {
        var block = Block(Xml(objectPath)); if (block is null) return null;
        XElement? current = Children(Children(block, "AttributeList").Single(), "Interface").SingleOrDefault();
        foreach (string name in path)
        {
            if (current is null) return null;
            var matches = Members(current).Where(m => (string?)m.Attribute("Name") == name).ToArray();
            if (matches.Length != 1) return null;
            current = matches[0];
        }
        return current;
    }
    internal JsonObject Evidence(string path, XElement element) => new() {
        ["objectPath"] = path, ["utf8Sha256"] = Documents[path]["utf8Sha256"]!.DeepClone(),
        ["xmlLine"] = ((IXmlLineInfo)element).LineNumber, ["origin"] = "savedNativeXml", ["liveVerified"] = false };

    internal JsonArray Instances(string blockPath)
    {
        var result = new JsonArray();
        if (!Nodes.TryGetValue(blockPath, out var selected)) return result;
        // A native-confirmed exact InstanceDB -> FB edge is mandatory for each root.
        foreach (var edge in D.Rows(Review["model"]?["edges"]).OfType<JsonObject>())
        {
            if (D.Text(edge, "kind") is not ("instanceOf" or "usesType") || D.Text(edge["evidence"], "origin") != "nativeConfirmed"
                || D.Text(edge["evidence"], "resolution") != "resolved" || edge["unresolved"] is not null) continue;
            string db = D.Text(edge["source"], "objectPath"), type = D.Text(edge["target"], "objectPath");
            if (!Nodes.TryGetValue(db, out var root) || !Nodes.TryGetValue(type, out var fb)
                || D.Text(root, "nodeId") != D.Text(edge["source"], "nodeId") || D.Text(fb, "nodeId") != D.Text(edge["target"], "nodeId")
                || string.IsNullOrEmpty(D.Text(root, "nodeId")) || string.IsNullOrEmpty(D.Text(fb, "nodeId"))
                || D.Text(root, "plcPath") != D.Text(selected, "plcPath") || D.Text(root, "plcPath").Length == 0
                || D.Text(fb, "plcPath") != D.Text(root, "plcPath")
                || D.Text(edge["source"], "resolution") == "unresolved" || D.Text(edge["target"], "resolution") == "unresolved"
                || Block(Xml(db))?.Name.LocalName != "SW.Blocks.InstanceDB" || Block(Xml(type))?.Name.LocalName != "SW.Blocks.FB") continue;
            if (result.OfType<JsonObject>().Any(r => D.Text(r, "dbPath") == db)) continue;
            var rootEvidence = new JsonArray(edge.DeepClone());
            var rootCalls = Calls(type, db, null);
            foreach (var call in rootCalls) rootEvidence.Add(call!.DeepClone());
            Walk(db, type, [], rootEvidence, rootCalls, 0);
        }
        return result;

        void Walk(string db, string type, string[] members, JsonArray evidence, JsonArray rootCalls, int depth)
        {
            if (depth > 8 || result.Count >= 200 || instanceVisits >= 1000) { traversalTruncated = true; return; }
            instanceVisits++;
            var calls = members.Length == 0 ? Calls(type, db, null) : new JsonArray(evidence.OfType<JsonObject>()
                .Where(e => D.Text(e, "calleePath") == type).Select(e => (JsonNode?)e.DeepClone()).ToArray());
            if (type == blockPath)
                result.Add(new JsonObject { ["dbPath"] = db, ["memberPath"] = D.Strings(members), ["blockPath"] = type,
                    ["plcPath"] = Nodes[type]["plcPath"]?.DeepClone(), ["scopePath"] = Nodes[type]["scopePath"]?.DeepClone(),
                    ["namespace"] = Nodes[type]["metadata"]?["namespace"]?.DeepClone(), ["evidence"] = evidence.DeepClone(),
                    ["callSites"] = calls, ["rootCallSites"] = rootCalls.DeepClone(), ["resolution"] = "capturedStorageOnly", ["runtimeCallContextVerified"] = false });
            var block = Block(Xml(type));
            var face = block is null ? null : Children(Children(block, "AttributeList").Single(), "Interface").SingleOrDefault();
            if (face is null) { Unresolved(db, members, "Parent interface unavailable/protected."); return; }
            foreach (var child in Members(face).Where(m => (string?)m.Parent?.Attribute("Name") == "Static"))
            {
                string name = (string?)child.Attribute("Name") ?? "", datatype = (string?)child.Attribute("Datatype") ?? "";
                if (!datatype.StartsWith('"') || !datatype.EndsWith('"') || name.Length == 0) continue;
                // Qualified names are matched exactly as exported. No using-namespace/alias search.
                string targetName = datatype.Trim('"');
                var matches = Nodes.Where(n => D.Text(n.Value, "kind") == "block" && D.Text(n.Value, "scopePath") == D.Text(Nodes[type], "scopePath")
                    && QualifiedName(n.Value) == targetName
                    ).ToArray();
                if (matches.Length != 1 || D.Text(Nodes[type], "scopePath").Length == 0)
                { Unresolved(db, members.Append(name), "Nested type is not uniquely resolved by exact exported qualification in this scope; aliases/other units need explicit native identity evidence.", Nodes.Values.Where(n => D.Text(n, "name") == targetName || QualifiedName(n) == targetName)); continue; }
                string next = matches[0].Key;
                if (Block(Xml(next))?.Name.LocalName != "SW.Blocks.FB") { Unresolved(db, members.Append(name), "Selected nested type is not an available exported FB.", [Nodes[next]]); continue; }
                // A declaration alone is not a call. Require the exact local Instance components.
                if (!CallsIn(type, next, "LocalVariable", [name]).Any()) { Unresolved(db, members.Append(name), "No explicit supported local call binds this member.", [Nodes[next]]); continue; }
                if (Member(db, [.. members, name]) is not XElement stored || (string?)stored.Attribute("Datatype") != datatype)
                { Unresolved(db, members.Append(name), "Embedded instance storage does not match the captured parent declaration.", [Nodes[next]]); continue; }
                var chain = (JsonArray)evidence.DeepClone(); chain.Add(Evidence(type, child)); chain.Add(Evidence(db, stored));
                foreach (var call in CallsIn(type, next, "LocalVariable", [name]))
                { call["callerDbPath"] = db; call["callerMemberPath"] = D.Strings(members); chain.Add(call.DeepClone()); }
                Walk(db, next, [.. members, name], chain, rootCalls, depth + 1);
            }
        }
    }

    private JsonArray Calls(string type, string? db, string? member)
    {
        var calls = new JsonArray();
        if (db is not null && Nodes.Values.Count(n =>
            D.Text(n, "kind") == "block" && D.Text(n, "scopePath") == D.Text(Nodes[db], "scopePath")
            && QualifiedName(n) == QualifiedName(Nodes[db])) != 1) return calls;
        foreach (var node in Nodes.Values.Where(n => D.Text(n, "scopePath") == D.Text(Nodes[type], "scopePath")))
            foreach (var call in CallsIn(D.Text(node, "objectPath"), type, db is null ? "LocalVariable" : "GlobalVariable",
                [db is null ? member! : QualifiedName(Nodes[db])])) calls.Add(call);
        return calls;
    }
    private IEnumerable<JsonObject> CallsIn(string caller, string callee, string scope, string[] components)
    {
        var doc = Xml(caller); if (doc is null) yield break;
        int ordinal = 0;
        foreach (var call in doc.Descendants().Where(e => e.Name.LocalName == "CallInfo" && (string?)e.Attribute("BlockType") == "FB"
            && e.Name.NamespaceName is "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4" or "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"
            && (string?)e.Attribute("Name") == QualifiedName(Nodes[callee])))
        {
            var instance = Children(call, "Instance").SingleOrDefault();
            if ((string?)instance?.Attribute("Scope") != scope || !Children(instance!, "Component").Select(c => (string?)c.Attribute("Name") ?? "").SequenceEqual(components)) continue;
            var row = Evidence(caller, call); row["calleePath"] = callee; row["instanceScope"] = scope; row["instanceComponents"] = D.Strings(components);
            row["callOrdinal"] = ordinal++; row["callUid"] = (string?)call.Attribute("UId") ?? (string?)call.Parent?.Attribute("UId");
            row["parameterBindings"] = DiagnosticCodeReader.Bindings(this, caller, call);
            yield return row;
        }
    }
}
