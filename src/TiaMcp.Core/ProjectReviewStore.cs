using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Core.EngineeringJobs;

namespace TiaMcp.Core;

/// <summary>Persistent, content-addressed offline reviews; never calls Siemens or executes project text.</summary>
public sealed partial class ProjectReviewStore(string artifactsRoot)
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private string DirectoryPath => Path.Combine(Path.GetFullPath(artifactsRoot), "project-reviews");
    private static string Text(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? "";
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static JsonArray Rows(JsonNode? value) => value as JsonArray ?? throw new BridgeException("InvalidSnapshot", "Expected a snapshot collection.");

    public static byte[] ReadArtifact(string root, JsonObject artifact, int limit = MaxBytes)
    {
        string path = Text(artifact, "path");
        path = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var verified = JsonSerializer.SerializeToElement(new ArtifactStore(root).Read(path, 0, 1), BridgeProtocol.Json);
        if (verified.GetProperty("bytes").GetInt64() > limit)
            throw new BridgeException("ArtifactCapacity", "Review input exceeds its byte limit.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > limit || verified.GetProperty("bytes").GetInt64() != bytes.Length
            || verified.GetProperty("sha256").GetString() != Hash(bytes)
            || (artifact["sha256"] is not null && Text(artifact, "sha256") != Hash(bytes)))
            throw new BridgeException("ArtifactChanged", "Review input changed during capture.");
        return bytes;
    }

    public JsonObject Save(JsonObject model, JsonArray documents, JsonObject capture)
    {
        if (Text(model, "schemaVersion") != "project-model.v1")
            throw new BridgeException("InvalidSnapshot", "A native project-model.v1 export is required.");
        var nodes = NativeNodes(model);
        if (documents.Count > 200) throw new BridgeException("ArtifactCapacity", "At most 200 block/type exports per review.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in documents)
        {
            string path = Text(row, "objectPath");
            if (!nodes.TryGetValue(path, out var node) || Text(node, "kind") is not ("block" or "type") || Text(node, "origin") == "virtual" || !paths.Add(path))
                throw new BridgeException("InvalidSnapshot", "Export requires a unique exact nonvirtual block/type identity in the captured model.");
            if (Text(row, "status") != "exported") continue;
            string xml = Text(row, "xml");
            if (Hash(Encoding.UTF8.GetBytes(xml)) != Text(row, "utf8Sha256"))
                throw new BridgeException("ArtifactChanged", "Export text hash mismatch.");
            row!["signatures"] = XmlSignatures(xml);
        }
        var review = new JsonObject { ["schemaVersion"] = "project-review.v1", ["createdAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["model"] = model.DeepClone(), ["documents"] = documents.DeepClone(), ["capture"] = capture.DeepClone(),
            ["consistency"] = "Sequential native observations; metadata recheck does not certify an atomic project/content revision.",
            ["wholeProjectContentComplete"] = false };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(review, BridgeProtocol.Json);
        if (data.Length > MaxBytes) throw new BridgeException("ArtifactCapacity", "Review exceeds 64 MiB.");
        string output = Publish(data), id = Hash(data);
        return new JsonObject { ["reviewId"] = id, ["artifact"] = new JsonObject { ["path"] = output, ["bytes"] = data.Length, ["sha256"] = id },
            ["nodeCount"] = nodes.Count,
            ["exportedBlocks"] = documents.Count(row => Text(row, "status") == "exported" && Text(nodes[Text(row, "objectPath")], "kind") == "block"),
            ["exportedTypes"] = documents.Count(row => Text(row, "status") == "exported" && Text(nodes[Text(row, "objectPath")], "kind") == "type"),
            ["capture"] = capture.DeepClone(), ["wholeProjectContentComplete"] = false };
    }

    public JsonObject Read(string id)
    {
        if (!Regex.IsMatch(id ?? "", "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant))
            throw new BridgeException("InvalidRequest", "reviewId must be the exact 64-character content hash.");
        var bytes = ReadArtifact(artifactsRoot, new JsonObject { ["path"] = Path.Combine(DirectoryPath, id + ".json"), ["sha256"] = id });
        var review = JsonNode.Parse(bytes)!.AsObject();
        if (Text(review, "schemaVersion") != "project-review.v1") throw new BridgeException("InvalidSnapshot", "Unsupported review schema.");
        return review;
    }

    public JsonObject Compare(string beforeId, string afterId, bool allowDifferentProjectCopy = false, int offset = 0, int maxItems = 100,
        int impactDepth = 3, int maxImpactPaths = 100, int maxImpactEdgeVisits = 5000)
    {
        Page(offset, maxItems);
        if (impactDepth is < 1 or > 5 || maxImpactPaths is < 1 or > 200 || maxImpactEdgeVisits is < 1 or > 20000)
            throw new BridgeException("InvalidRequest", "Impact limits: impactDepth 1..5, maxImpactPaths 1..200, maxImpactEdgeVisits 1..20000.");
        var before = Read(beforeId); var after = Read(afterId);
        var aModel = before["model"]!.AsObject(); var bModel = after["model"]!.AsObject();
        if (!allowDifferentProjectCopy && (Text(aModel["project"], "projectPath") != Text(bModel["project"], "projectPath")
            || Text(aModel["project"], "host") != Text(bModel["project"], "host")))
            throw new BridgeException("ProjectCopyMismatch", "Different project copies require explicit allowDifferentProjectCopy:true.");
        string aRoot = ProjectRoot(aModel), bRoot = ProjectRoot(bModel);
        var a = NativeNodes(aModel).ToDictionary(pair => Relative(pair.Key, aRoot), pair => pair.Value, StringComparer.Ordinal);
        var b = NativeNodes(bModel).ToDictionary(pair => Relative(pair.Key, bRoot), pair => pair.Value, StringComparer.Ordinal);
        var aDocs = Documents(before, aRoot); var bDocs = Documents(after, bRoot);
        var changes = new JsonArray(); int unknownContent = 0, contentCompared = 0, unknownTypes = 0, typesCompared = 0;
        foreach (string path in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(path, out var oldNode); b.TryGetValue(path, out var newNode);
            var change = new JsonObject { ["path"] = path, ["beforeObjectPath"] = oldNode?["objectPath"]?.DeepClone(),
                ["afterObjectPath"] = newNode?["objectPath"]?.DeepClone(), ["kind"] = (newNode ?? oldNode)?["kind"]?.DeepClone() };
            if (oldNode is null || newNode is null)
                change["change"] = oldNode is null ? "addedToCapturedScope" : "removedFromCapturedScope";
            else
            {
                var fields = new JsonArray();
                foreach (string field in new[] { "name", "kind", "access", "metadata" })
                    if (!JsonNode.DeepEquals(oldNode[field], newNode[field])) fields.Add(field);
                aDocs.TryGetValue(path, out var oldDoc); bDocs.TryGetValue(path, out var newDoc);
                bool block = Text(oldNode, "kind") == "block" || Text(newNode, "kind") == "block";
                bool type = Text(oldNode, "kind") == "type" || Text(newNode, "kind") == "type";
                if (block || type)
                {
                    if (Text(oldDoc, "status") == "exported" && Text(newDoc, "status") == "exported")
                    {
                        if (block) contentCompared++;
                        if (type) typesCompared++;
                        foreach (string part in new[] { "interface", "logic", "document" })
                            if (Text(oldDoc?["signatures"], part) != Text(newDoc?["signatures"], part)) fields.Add(part + "Changed");
                        change["contentCompared"] = true;
                    }
                    else { if (block) unknownContent++; if (type) unknownTypes++; change["contentCompared"] = false; }
                }
                if (fields.Count == 0) continue;
                change["change"] = "modified"; change["changedFields"] = fields;
            }
            changes.Add(change);
        }
        var changedPaths = changes.Select(c => Text(c, "afterObjectPath")).Where(p => p.Length > 0).ToHashSet(StringComparer.Ordinal);
        var impacts = new JsonArray();
        foreach (var edge in Rows(bModel["edges"]))
            if (changedPaths.Contains(Text(edge?["target"], "objectPath")) && Text(edge?["source"], "objectPath").Length > 0)
                impacts.Add(new JsonObject { ["kind"] = edge?["kind"]?.DeepClone(), ["source"] = edge?["source"]?.DeepClone(),
                    ["target"] = edge?["target"]?.DeepClone(), ["evidence"] = edge?["evidence"]?.DeepClone() });
        var impactAnalysis = ObservedImpactPaths(beforeId, afterId, aModel, bModel, changes, impactDepth, maxImpactPaths, maxImpactEdgeVisits);
        return new JsonObject { ["beforeReviewId"] = beforeId, ["afterReviewId"] = afterId,
            ["changes"] = new JsonArray(changes.Skip(offset).Take(maxItems).Select(n => n!.DeepClone()).ToArray()),
            ["totalChanges"] = changes.Count, ["nextOffset"] = offset + maxItems < changes.Count ? offset + maxItems : null,
            ["contentComparedBlocks"] = contentCompared, ["contentUnknownBlocks"] = unknownContent,
            ["contentComparedTypes"] = typesCompared, ["contentUnknownTypes"] = unknownTypes,
            ["directObservedImpacts"] = new JsonArray(impacts.Take(200).Select(n => n!.DeepClone()).ToArray()),
            ["impactTruncated"] = impacts.Count > 200, ["impactCoverageComplete"] = false,
            ["observedImpactPaths"] = impactAnalysis["paths"]!.DeepClone(), ["impactTraversal"] = impactAnalysis["coverage"]!.DeepClone(),
            ["beforeCoverage"] = aModel["coverage"]?.DeepClone(), ["afterCoverage"] = bModel["coverage"]?.DeepClone(),
            ["wholeProjectEquivalent"] = false,
            ["comparisonPolicy"] = "Exact relative native paths; no rename inference. XML ignores only top-level DocumentInfo/Created. Whitespace, IDs, namespaces, comments and unknown payload remain significant. Scope absence is not proof of deletion." };
    }

    private static readonly string[] ImpactKinds = ["calls", "dependsOn", "hmiBinding", "instanceOf", "libraryVersion", "reads", "usesType", "writes"];

    private sealed record ImpactWalk(string[] Paths, JsonObject[] Edges);

    private static bool ExactNativeEndpoint(JsonNode? endpoint, Dictionary<string, JsonObject> nodes)
    {
        string path = Text(endpoint, "objectPath"), id = Text(endpoint, "nodeId"), resolution = Text(endpoint, "resolution");
        return path.Length > 0 && id.Length > 0 && nodes.TryGetValue(path, out var node)
            && Text(node, "origin") != "virtual" && id == Text(node, "nodeId") && (resolution.Length == 0 || resolution == "resolved");
    }

    internal static JsonObject ObservedImpactPaths(string beforeId, string afterId, JsonObject before, JsonObject after,
        JsonArray changes, int depth, int maxPaths, int maxEdgeVisits)
    {
        const int maxPathBytes = 1024 * 1024;
        var paths = new JsonArray(); var exclusions = new JsonArray();
        var bounds = new HashSet<string>(StringComparer.Ordinal);
        int visitedEdges = 0, pathBytes = 2;
        bool stopped = false;
        foreach (var (side, reviewId, model) in new[] { ("before", beforeId, before), ("after", afterId, after) })
        {
            var nodes = NativeNodes(model);
            var incoming = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
            var excluded = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Exclude(string reason) => excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
            foreach (var edge in Rows(model["edges"]))
            {
                if (!ImpactKinds.Contains(Text(edge, "kind"), StringComparer.Ordinal)) { Exclude("unsupportedRelationshipKind"); continue; }
                if (edge?["unresolved"] is not null || Text(edge?["evidence"], "resolution") != "resolved"
                    || Text(edge?["evidence"], "origin") is not ("nativeConfirmed" or "parsedSupported"))
                { Exclude("unresolvedOrUnsupportedEvidence"); continue; }
                if (!ExactNativeEndpoint(edge?["source"], nodes) || !ExactNativeEndpoint(edge?["target"], nodes))
                { Exclude("endpointOutsideCapturedNodesOrIdentityMismatch"); continue; }
                string target = Text(edge?["target"], "objectPath");
                if (!incoming.TryGetValue(target, out var adjacent)) incoming[target] = adjacent = [];
                adjacent.Add(edge!.AsObject());
            }
            foreach (var adjacent in incoming.Values)
                adjacent.Sort((left, right) =>
                {
                    int order = StringComparer.Ordinal.Compare(Text(left["source"], "objectPath"), Text(right["source"], "objectPath"));
                    if (order == 0) order = StringComparer.Ordinal.Compare(Text(left, "kind"), Text(right, "kind"));
                    if (order == 0) order = StringComparer.Ordinal.Compare(Text(left, "edgeId"), Text(right, "edgeId"));
                    return order == 0 ? StringComparer.Ordinal.Compare(left.ToJsonString(), right.ToJsonString()) : order;
                });
            exclusions.Add(new JsonObject { ["reviewSide"] = side, ["counts"] = new JsonObject(excluded.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))) });
            if (stopped) continue;
            foreach (var change in changes)
            {
                string root = Text(change, side + "ObjectPath");
                if (root.Length == 0) continue;
                var queue = new Queue<ImpactWalk>(); queue.Enqueue(new([root], []));
                while (queue.TryDequeue(out var walk) && !stopped)
                {
                    if (!incoming.TryGetValue(walk.Paths[0], out var adjacent)) continue;
                    foreach (var edge in adjacent)
                    {
                        if (visitedEdges == maxEdgeVisits) { bounds.Add("maxImpactEdgeVisits"); stopped = true; break; }
                        visitedEdges++;
                        string source = Text(edge["source"], "objectPath");
                        // A cycle cannot supply additional evidence of an object's own impact.
                        if (walk.Paths.Contains(source, StringComparer.Ordinal)) continue;
                        if (walk.Edges.Length == depth) { bounds.Add("impactDepth"); continue; }
                        if (paths.Count == maxPaths) { bounds.Add("maxImpactPaths"); stopped = true; break; }
                        string[] sequence = [source, .. walk.Paths]; JsonObject[] edges = [edge, .. walk.Edges];
                        var path = new JsonObject { ["reviewSide"] = side, ["reviewId"] = reviewId,
                            ["changedPath"] = change!["path"]!.DeepClone(), ["changedObjectPath"] = root,
                            ["change"] = change["change"]!.DeepClone(), ["changedFields"] = change["changedFields"]?.DeepClone(),
                            ["affectedObjectPath"] = source, ["distance"] = edges.Length,
                            ["objectPaths"] = new JsonArray(sequence.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                            ["edges"] = new JsonArray(edges.Select(e => e.DeepClone()).ToArray()) };
                        int bytes = Encoding.UTF8.GetByteCount(path.ToJsonString(BridgeProtocol.Json)) + (paths.Count == 0 ? 0 : 1);
                        if (pathBytes + bytes > maxPathBytes) { bounds.Add("maxImpactPathBytes"); stopped = true; break; }
                        paths.Add(path); pathBytes += bytes;
                        queue.Enqueue(new(sequence, edges));
                    }
                }
                if (stopped) break;
            }
        }
        return new JsonObject { ["paths"] = paths, ["coverage"] = new JsonObject {
            ["complete"] = false, ["freshness"] = "savedSnapshotsOnly", ["truncated"] = bounds.Count > 0,
            ["truncationReasons"] = new JsonArray(bounds.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["limits"] = new JsonObject { ["impactDepth"] = depth, ["maxImpactPaths"] = maxPaths,
                ["maxImpactEdgeVisits"] = maxEdgeVisits, ["maxImpactPathBytes"] = maxPathBytes },
            ["returnedPaths"] = paths.Count, ["visitedEdges"] = visitedEdges, ["pathBytes"] = pathBytes,
            ["excludedEdges"] = exclusions,
            ["supportedRelationshipKinds"] = new JsonArray(ImpactKinds.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            ["policy"] = "Incoming simple dependency paths, breadth first per changed object, before snapshot then after snapshot. Each path runs from the affected object to the changed object; diamonds retain distinct paths and cycles never repeat an object. Only resolved nativeConfirmed/parsedSupported edges with exact captured endpoints are followed. No cross-snapshot edges are joined.",
            ["limitations"] = "Static dependency review candidates, not runtime effects or a compile verdict. Both captures have incomplete relationship coverage; excluded edges and bounds can hide further paths. Absence from captured scope is not deletion; no paths does not establish no impact." } };
    }

    public JsonObject Search(string reviewId, string query, int offset = 0, int maxItems = 50)
    {
        Page(offset, maxItems);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 256) throw new BridgeException("InvalidRequest", "query must contain 1..256 characters.");
        var review = Read(reviewId); var hits = new JsonArray();
        var nodes = NativeNodes(review["model"]!.AsObject());
        foreach (var pair in nodes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || Text(pair.Value, "name").Contains(query, StringComparison.OrdinalIgnoreCase))
                hits.Add(new JsonObject { ["objectPath"] = pair.Key, ["origin"] = "metadata", ["excerpt"] = Text(pair.Value, "name") });
            if (hits.Count >= 10000) break;
        }
        foreach (var doc in Rows(review["documents"]))
        {
            if (hits.Count >= 10000) break;
            if (Text(doc, "status") != "exported") continue;
            var xml = ParseXml(Text(doc, "xml"));
            int index = 0;
            var textValues = xml.DescendantNodes().OfType<XText>().Select(t => t.Value)
                .Concat(xml.Descendants().Attributes().Where(a => a.Name.LocalName is "Text" or "Name" or "Value").Select(a => a.Value));
            foreach (var text in textValues)
            {
                index++;
                int found = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                if (found < 0) continue;
                hits.Add(new JsonObject { ["objectPath"] = doc?["objectPath"]?.DeepClone(), ["origin"] = "nativeXmlText",
                    ["textNode"] = index, ["excerpt"] = text.Substring(Math.Max(0, found - 80), Math.Min(320, text.Length - Math.Max(0, found - 80))) });
                if (hits.Count >= 10000) break;
            }
            if (hits.Count >= 10000) break;
        }
        return new JsonObject { ["reviewId"] = reviewId, ["query"] = query,
            ["hits"] = new JsonArray(hits.Skip(offset).Take(maxItems).Select(n => n!.DeepClone()).ToArray()),
            ["totalObservedHits"] = hits.Count, ["truncated"] = hits.Count >= 10000,
            ["nextOffset"] = offset + maxItems < hits.Count ? offset + maxItems : null, ["freshness"] = "savedSnapshotOnly",
            ["sourceCoverage"] = review["capture"]?.DeepClone() };
    }

    public JsonObject Health(string reviewId, int offset = 0, int maxItems = 100, EngineeringArtifactReference[]? testSuiteResults = null)
    {
        Page(offset, maxItems);
        var review = Read(reviewId); var model = review["model"]!.AsObject(); var findings = new JsonArray();
        void Add(string rule, string severity, string path, string message) => findings.Add(new JsonObject {
            ["rule"] = rule, ["severity"] = severity, ["objectPath"] = path, ["message"] = message });
        var nodes = NativeNodes(model);
        var byId = Rows(model["nodes"]).Where(n => Text(n, "nodeId").Length > 0).ToLookup(n => Text(n, "nodeId"), StringComparer.Ordinal);
        bool Endpoint(JsonNode? endpoint, bool grouping)
        {
            if (ExactNativeEndpoint(endpoint, nodes)) return true;
            string id = Text(endpoint, "nodeId"); var matches = byId[id].ToArray();
            return grouping && id.Length > 0 && matches.Length == 1 && Text(matches[0], "origin") == "virtual"
                && Text(matches[0], "objectPath").Length == 0 && Text(endpoint, "objectPath").Length == 0
                && Text(endpoint, "resolution") is "" or "resolved";
        }
        foreach (var pair in nodes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var n = pair.Value;
            if (n["metadata"]?["isConsistent"]?.GetValue<bool>() == false)
                Add("native-inconsistent", "warning", pair.Key, "Native metadata reports this object inconsistent; inspect compiler diagnostics.");
            if (n["metadata"]?["isKnowHowProtected"]?.GetValue<bool>() == true)
                Add("content-protected", "coverage", pair.Key, "Protected content cannot be included in source review.");
            if (Text(n["access"], "state") is "unavailable" or "protected")
                Add("inspection-unavailable", "coverage", pair.Key, "Native inspection is unavailable for this exact object.");
        }
        foreach (var doc in Rows(review["documents"]))
            if (Text(doc, "status") != "exported") Add("source-unavailable", "coverage", Text(doc, "objectPath"), Text(doc, "reason"));
        foreach (var edge in Rows(model["edges"]))
            if (edge?["unresolved"] is not null || Text(edge?["evidence"], "resolution") != "resolved"
                || Text(edge?["evidence"], "origin") is not ("nativeConfirmed" or "parsedSupported")
                || !Endpoint(edge?["source"], Text(edge, "kind") == "contains") || !Endpoint(edge?["target"], Text(edge, "kind") == "contains"))
                Add("unresolved-reference", "coverage", Text(edge?["source"], "objectPath"), "A captured relationship lacks supported resolved evidence or exact endpoints; this alone does not prove a broken reference.");
        var result = new JsonObject { ["reviewId"] = reviewId, ["findings"] = new JsonArray(findings.Skip(offset).Take(maxItems).Select(n => n!.DeepClone()).ToArray()),
            ["totalFindings"] = findings.Count, ["nextOffset"] = offset + maxItems < findings.Count ? offset + maxItems : null,
            ["freshness"] = "savedSnapshotOnly", ["coverage"] = model["coverage"]?.DeepClone(),
            ["rules"] = new JsonArray("native-inconsistent", "content-protected", "inspection-unavailable", "source-unavailable", "unresolved-reference"),
            ["limitations"] = "No live compile, unused-object verdict, safety validation or physical network inspection. No findings does not establish project correctness." };
        if (testSuiteResults is not null) result["testSuite"] = TestSuiteHealth(reviewId, testSuiteResults);
        return result;
    }

    private static void Page(int offset, int count)
    { if (count is < 1 or > 200 || offset < 0 || offset > int.MaxValue - count) throw new BridgeException("InvalidRequest", "offset must be nonnegative with a representable next page and maxItems 1..200."); }
    private static Dictionary<string, JsonObject> NativeNodes(JsonObject model)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var row in Rows(model["nodes"]))
        {
            string path = Text(row, "objectPath"); if (path.Length == 0) continue;
            if (!result.TryAdd(path, row!.AsObject())) throw new BridgeException("InvalidSnapshot", "Duplicate exact native object path.");
        }
        return result;
    }
    private static string ProjectRoot(JsonObject model) => Text(Rows(model["nodes"]).Single(n => Text(n, "kind") == "project"), "objectPath");
    private static string Relative(string path, string root) => path == root ? "/" : path.StartsWith(root + "/", StringComparison.Ordinal) ? path[root.Length..] : throw new BridgeException("InvalidSnapshot", "Object is outside the captured project root.");
    private static Dictionary<string, JsonObject> Documents(JsonObject review, string root) => Rows(review["documents"]).ToDictionary(n => Relative(Text(n, "objectPath"), root), n => n!.AsObject(), StringComparer.Ordinal);
    private static XDocument ParseXml(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
        using (var depth = XmlReader.Create(new StringReader(xml), settings))
            while (depth.Read()) if (depth.Depth > 128) throw new XmlException("Review XML exceeds 128 nested levels.");
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    public static JsonObject XmlSignatures(string xml)
    {
        var document = ParseXml(xml);
        if (document.Root?.Name == XName.Get("Document"))
            foreach (var created in document.Root.Elements("DocumentInfo").Elements("Created").ToArray()) created.Remove();
        string Digest(IEnumerable<XElement> elements) => Hash(Encoding.UTF8.GetBytes(string.Join("\n", elements.Select(e => e.ToString(SaveOptions.DisableFormatting)))));
        return new JsonObject { ["document"] = Hash(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))),
            ["interface"] = Digest(document.Descendants().Where(e => e.Name.LocalName == "Interface")),
            ["logic"] = Digest(document.Descendants().Where(e => e.Name.LocalName == "NetworkSource")) };
    }
}
